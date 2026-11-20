import { ApiError, readRequestJson } from './requests';
import type { Env } from './types';

const checkPath = '/api/updates/v1/check';
const downloadPath = '/api/updates/v1/download';
const responseHeaders = {
  'Cache-Control': 'no-store',
  'X-Content-Type-Options': 'nosniff',
};
const releaseUnavailable = 'Dịch vụ cập nhật tạm thời không khả dụng.';

export async function handleUpdateRequest(request: Request, env: Env, path: string): Promise<Response> {
  if (path !== checkPath && path !== downloadPath) return error('Endpoint không tồn tại.', 404);
  if (request.method !== 'POST') return error('Chỉ hỗ trợ phương thức POST.', 405, { Allow: 'POST' });
  if (request.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json') {
    return error('Yêu cầu cần có Content-Type: application/json.', 415);
  }

  try {
    const { license, rid, channel, version } = await readRequestJson(request);
    if (rid !== 'win-x64' && rid !== 'linux-x64') throw new ApiError('Nền tảng cập nhật không hợp lệ.');
    if (channel !== 'stable' && channel !== 'pilot') throw new ApiError('Kênh cập nhật không hợp lệ.');
    if ((path === downloadPath || version !== undefined) && !isVersion(version)) {
      throw new ApiError('Phiên bản cập nhật không hợp lệ.');
    }
    const credential = readCredential(license);
    const stored = await env.DB.withSession('first-primary').prepare(`
      SELECT expires_at FROM licenses
      WHERE sig_b64 = ? AND data_b64 = ? AND product = 'TanTienQR' AND revoked_at IS NULL
      LIMIT 1
    `).bind(credential.sig, credential.data).first<{ expires_at: string }>();
    // Chỉ file trùng bản D1 đã cấp mới được tải. Mã máy được kiểm tra tại ứng dụng.
    if (!stored || !(Date.parse(stored.expires_at) > Date.now())) {
      return error('License không có quyền tải bản cập nhật. Hãy liên hệ quản trị viên để kiểm tra hoặc cấp lại.', 403);
    }

    const bucket = env.UPDATE_BUCKET;
    if (!bucket) return error(releaseUnavailable, 503);
    const current = await readChannel(bucket, channel);
    if (path === downloadPath && current !== version) {
      return error('Phiên bản trên kênh đã thay đổi. Hãy kiểm tra lại.', 409);
    }
    if (current === null) return new Response(null, { status: 204, headers: responseHeaders });

    const folder = `releases/${current}/${rid}`;
    if (path === checkPath) {
      const manifest = await bucket.get(`${folder}/manifest.json`);
      if (!manifest || manifest.size > 256 * 1024) {
        await manifest?.body.cancel();
        throw new Error('Manifest chưa sẵn sàng.');
      }
      return new Response(manifest.body, {
        headers: { ...responseHeaders, 'Content-Type': 'application/json; charset=utf-8' },
      });
    }

    const zip = await bucket.get(`${folder}/package.zip`);
    if (!zip) throw new Error('Gói cập nhật chưa sẵn sàng.');
    return new Response(zip.body, {
      headers: {
        ...responseHeaders,
        'Content-Type': 'application/zip',
        'Content-Disposition': `attachment; filename="TanTienQR-${current}-${rid}.zip"`,
      },
    });
  } catch (cause) {
    if (cause instanceof ApiError) return error(cause.message, cause.status);
    // Sự cố hạ tầng được ghi bằng mã cố định, tránh đưa thông tin từ POST vào log.
    console.error('update_request_failed', path === checkPath ? 'check' : 'download');
    return error(releaseUnavailable, 503);
  }
}

function readCredential(value: unknown): { data: string; sig: string } {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ApiError('Thiếu file license.');
  const license = value as Record<string, unknown>;
  if (license.v !== 1) throw new ApiError('Định dạng file license không hợp lệ.');
  return { data: normalizeBase64(license.data), sig: normalizeBase64(license.sig) };
}

function normalizeBase64(value: unknown): string {
  if (typeof value === 'string' && value.length > 0) {
    try {
      const bytes = atob(value);
      if (bytes.length > 0) return btoa(bytes);
    } catch { /* Báo lỗi định dạng chung, không phản hồi lại dữ liệu license. */ }
  }
  throw new ApiError('Định dạng file license không hợp lệ.');
}

function isVersion(value: unknown): value is string {
  return typeof value === 'string'
    && /^(?:0|[1-9][0-9]{0,9})(?:\.(?:0|[1-9][0-9]{0,9})){2}$/.test(value)
    && value.split('.').every(part => Number(part) <= 2_147_483_647);
}

async function readChannel(bucket: R2Bucket, channel: string): Promise<string | null> {
  const pointer = await bucket.get(`channels/${channel}.json`);
  if (!pointer) return null;
  if (pointer.size > 1024) {
    await pointer.body.cancel();
    throw new Error('Thông tin kênh quá lớn.');
  }
  const data = await pointer.json<{ version?: unknown } | null>();
  // Công cụ phát hành dùng {} làm tombstone để rút bằng conditional PUT.
  if (data && typeof data === 'object' && !Array.isArray(data) && Object.keys(data).length === 0) return null;
  if (!isVersion(data?.version)) throw new Error('Thông tin kênh không hợp lệ.');
  return data.version;
}

function error(message: string, status: number, headers?: Record<string, string>): Response {
  return Response.json({ error: message }, { status, headers: { ...responseHeaders, ...headers } });
}
