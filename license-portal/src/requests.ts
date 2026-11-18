import { groupHwid, normalizeHwid } from './crypto';
import type { Env } from './types';

/** Lỗi đầu vào hoặc trạng thái nghiệp vụ được phản hồi trực tiếp cho client. */
export class ApiError extends Error {
  constructor(message: string, readonly status = 400) {
    super(message);
  }
}

export interface LicenseRequestRow {
  id: string;
  hwid: string;
  hwid_display: string;
  product: string;
  customer: string;
  contact: string;
  note: string;
  status: 'pending' | 'issued';
  license_key: string | null;
  created_at: string;
  updated_at: string;
}

/** Đọc body có giới hạn ngay cả khi request không có Content-Length. */
export async function readRequestJson(request: Request): Promise<Record<string, unknown>> {
  const maxBytes = 8 * 1024;
  const declaredSize = Number(request.headers.get('Content-Length'));
  if (Number.isFinite(declaredSize) && declaredSize > maxBytes) {
    throw new ApiError('Yêu cầu quá lớn (tối đa 8 KB).', 413);
  }
  if (!request.body) throw new ApiError('Dữ liệu gửi lên không hợp lệ.');

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > maxBytes) {
        await reader.cancel();
        throw new ApiError('Yêu cầu quá lớn (tối đa 8 KB).', 413);
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  try {
    const body: unknown = JSON.parse(new TextDecoder().decode(bytes));
    if (!body || typeof body !== 'object' || Array.isArray(body)) {
      throw new Error('Cần JSON object.');
    }
    return body as Record<string, unknown>;
  } catch {
    throw new ApiError('Dữ liệu gửi lên không hợp lệ.');
  }
}

/** Chỉ cho phép gạch nối và khoảng trắng ngoài các ký tự hex của HWID. */
export function requireHwid(value: unknown): string {
  if (typeof value !== 'string' || value.length > 100 || !/^[0-9a-fA-F\s-]+$/.test(value)) {
    throw new ApiError('HWID phải gồm 32 ký tự hex, có thể phân nhóm bằng dấu gạch nối.');
  }
  const hwid = normalizeHwid(value);
  if (hwid.length !== 32) throw new ApiError('HWID phải gồm đúng 32 ký tự hex.');
  return hwid;
}

export function requestText(value: unknown, label: string, maxLength: number): string {
  if (value === undefined || value === null) return '';
  if (typeof value !== 'string') throw new ApiError(`${label} phải là chuỗi.`);
  const text = value.trim();
  if (text.length > maxLength) throw new ApiError(`${label} quá dài (tối đa ${maxLength} ký tự).`);
  return text;
}

/** Lưu yêu cầu công khai, không ký và không trả bất kỳ license nào. */
export async function submitLicenseRequest(env: Env, body: Record<string, unknown>): Promise<LicenseRequestRow> {
  const hwid = requireHwid(body.hwid);
  const product = requestText(body.product, 'Sản phẩm', 100) || env.DEFAULT_PRODUCT;
  const customer = requestText(body.customer, 'Tên khách hàng', 200);
  const contact = requestText(body.contact, 'Thông tin liên hệ', 200);
  const note = requestText(body.note, 'Ghi chú', 1000);
  const productRow = await env.DB.prepare(
    'SELECT code FROM products WHERE code = ? AND active = 1',
  ).bind(product).first<{ code: string }>();
  if (!productRow) throw new ApiError('Sản phẩm không tồn tại hoặc đã bị vô hiệu hoá.');

  const id = crypto.randomUUID();
  const now = new Date().toISOString();
  // D1 batch là transaction: insert và đọc bản đang chờ không thể bị chen
  // giữa bởi thao tác cấp license từ một quản trị viên khác.
  const result = await env.DB.batch<LicenseRequestRow>([
    env.DB.prepare(
      `INSERT INTO license_requests
         (id, hwid, hwid_display, product, customer, contact, note, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT DO NOTHING`,
    ).bind(id, hwid, groupHwid(hwid), product, customer, contact, note, now, now),
    env.DB.prepare(
      `SELECT id, hwid, hwid_display, product, customer, contact, note,
              status, license_key, created_at, updated_at
         FROM license_requests WHERE product = ? AND hwid = ? AND status = 'pending'`,
    ).bind(product, hwid),
  ]);
  const pending = result[1].results[0];
  if (!pending) throw new Error('Không lưu được yêu cầu cấp license.');
  return pending;
}

export async function listLicenseRequests(env: Env, url: URL): Promise<{
  requests: LicenseRequestRow[]; total: number; limit: number; offset: number;
}> {
  const status = url.searchParams.get('status') ?? 'pending';
  if (!['pending', 'issued', 'all'].includes(status)) throw new ApiError('Bộ lọc trạng thái không hợp lệ.');
  const limit = Math.min(Math.max(parseInt(url.searchParams.get('limit') ?? '100', 10) || 100, 1), 500);
  const offset = Math.max(parseInt(url.searchParams.get('offset') ?? '0', 10) || 0, 0);
  const where = status === 'all' ? '' : 'WHERE status = ?';
  const binds = status === 'all' ? [] : [status];
  const count = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM license_requests ${where}`,
  ).bind(...binds).first<{ n: number }>();
  const { results } = await env.DB.prepare(
    `SELECT id, hwid, hwid_display, product, customer, contact, note,
            status, license_key, created_at, updated_at
       FROM license_requests ${where} ORDER BY created_at DESC, id DESC LIMIT ? OFFSET ?`,
  ).bind(...binds, limit, offset).all<LicenseRequestRow>();
  return { requests: results, total: count?.n ?? 0, limit, offset };
}
