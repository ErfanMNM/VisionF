/**
 * Logic cấp license. Payload phải KHỚP 100% với
 * ClassLibraryMT/Licensing/LicenseModels.cs + LicenseSigner.cs:
 *
 *   { hwid, customer, issued, expires, product, kid }
 *
 * và file license.lic:
 *   { "v": 1, "data": "<base64 json>", "sig": "<base64 RSA-SHA256 PKCS1v1.5>" }
 *
 * LƯU Ý: .NET Serialize mặc định ghi DateTime theo ISO 8601 round-trip
 * (ví dụ "2026-11-02T09:40:54.8457572Z") — ta dùng ISO 8601 cùng format.
 */

import { computeKeyId, enc, groupHwid, importSigningKey, normalizeHwid, signData, toBase64 } from './crypto';
import type { Env } from './types';

export interface LicensePayload {
  hwid: string;
  customer: string;
  issued: string;
  expires: string;
  product: string;
  kid: string;
}

export interface LicenseFile {
  v: number;
  data: string;
  sig: string;
}

export interface IssueResult {
  id: number;
  licenseKey: string;
  file: LicenseFile;
  fileText: string;
  payload: LicensePayload;
  expiresAt: string;
  hwidDisplay: string;
}

/**
 * Tạo và ký 1 license mới.
 */
export async function issueLicense(
  env: Env,
  input: { customer: string; hwid: string; daysValid: number; product: string; note?: string; createdBy: string },
): Promise<IssueResult> {
  const customer = input.customer.trim();
  if (!customer) throw new Error('Tên khách hàng không được để trống.');
  if (customer.length > 200) throw new Error('Tên khách hàng quá dài (tối đa 200 ký tự).');

  const hwid = normalizeHwid(input.hwid);
  if (hwid.length !== 32) {
    throw new Error('HWID phải là 32 ký tự hex. Vào app → Quản trị → License để lấy mã máy.');
  }

  const daysValid = Math.floor(Number(input.daysValid));
  if (!Number.isFinite(daysValid) || daysValid <= 0) {
    throw new Error('Số ngày hiệu lực phải > 0.');
  }
  if (daysValid > 3650) throw new Error('Số ngày hiệu lực tối đa 3650 ngày (10 năm).');

  const product = (input.product || env.DEFAULT_PRODUCT).trim();
  if (!product) throw new Error('Sản phẩm không được để trống.');
  if (product.length > 100) throw new Error('Tên sản phẩm quá dài.');

  // Kiểm tra sản phẩm có tồn tại trong bảng products không.
  const productRow = await env.DB.prepare(
    'SELECT code FROM products WHERE code = ? AND active = 1',
  ).bind(product).first<{ code: string }>();
  if (!productRow) throw new Error(`Sản phẩm "${product}" không tồn tại hoặc đã bị vô hiệu hoá.`);

  // Load private key từ Worker secret.
  const pkcs8 = (env.LICENSE_PRIVATE_KEY_PKCS8 ?? '').trim();
  if (!pkcs8) throw new Error('Chưa cấu hình LICENSE_PRIVATE_KEY_PKCS8 trên Worker.');

  let keyId = (env.LICENSE_KEY_ID ?? '').trim();
  if (!/^[0-9A-Fa-f]{8}$/.test(keyId)) {
    throw new Error('LICENSE_KEY_ID phải là 8 ký tự hex.');
  }
  keyId = keyId.toUpperCase();

  const issued = new Date();
  const expires = new Date(issued.getTime() + daysValid * 86_400_000);

  const payload: LicensePayload = {
    hwid,
    customer,
    issued: issued.toISOString(),
    expires: expires.toISOString(),
    product,
    kid: keyId,
  };

  // Thứ tự field phải giống .NET: hwid, customer, issued, expires, product, kid.
  const json = JSON.stringify({
    hwid: payload.hwid,
    customer: payload.customer,
    issued: payload.issued,
    expires: payload.expires,
    product: payload.product,
    kid: payload.kid,
  });

  const dataBytes = enc.encode(json);
  const privateKey = await importSigningKey(pkcs8);
  const sigB64 = await signData(privateKey, dataBytes);

  const file: LicenseFile = {
    v: 1,
    data: toBase64(dataBytes),
    sig: sigB64,
  };

  const licenseKey = generateLicenseKey();
  const hwidDisplay = groupHwid(hwid);

  // Luu vao D1.
  await env.DB.prepare(
    `INSERT INTO licenses
       (license_key, customer, product, hwid, hwid_display, days_valid,
        issued_at, expires_at, key_id, data_b64, sig_b64, note, created_by)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  )
    .bind(
      licenseKey,
      customer,
      product,
      hwid,
      hwidDisplay,
      daysValid,
      payload.issued,
      payload.expires,
      keyId,
      file.data,
      file.sig,
      (input.note ?? '').trim() || null,
      input.createdBy,
    )
    .run();

  await audit(env, input.createdBy, 'issue', `key=${licenseKey} customer=${customer} product=${product}`);

  const idRow = await env.DB.prepare(
    'SELECT id FROM licenses WHERE license_key = ?',
  ).bind(licenseKey).first<{ id: number }>();

  return {
    id: idRow?.id ?? 0,
    licenseKey,
    file,
    fileText: JSON.stringify(file),
    payload,
    expiresAt: payload.expires,
    hwidDisplay,
  };
}

/**
 * Kiểm tra chữ ký của 1 license đã lưu (dùng cho chức năng "verify").
 * Worker verify bằng WebCrypto, app verify bằng .NET — cả hai cùng PKCS1v1.5.
 */
export async function verifyStoredLicense(
  env: Env,
  licenseKey: string,
): Promise<{ ok: boolean; message: string }> {
  const row = await env.DB.prepare(
    'SELECT data_b64, sig_b64, key_id, expires_at, revoked_at FROM licenses WHERE license_key = ?',
  ).bind(licenseKey).first<{
    data_b64: string; sig_b64: string; key_id: string; expires_at: string; revoked_at: string | null;
  }>();
  if (!row) return { ok: false, message: 'Không tìm thấy license.' };
  if (row.revoked_at) return { ok: false, message: `License đã bị thu hồi (${row.revoked_at}).` };
  if (new Date(row.expires_at).getTime() < Date.now()) {
    return { ok: false, message: `License hết hạn (${row.expires_at.slice(0, 10)}).` };
  }

  const pubRow = await env.DB.prepare(
    'SELECT public_key FROM signing_keys WHERE key_id = ?',
  ).bind(row.key_id).first<{ public_key: string }>();
  if (!pubRow) return { ok: false, message: `Không tìm thấy public key cho kid=${row.key_id}.` };

  try {
    const spki = Uint8Array.from(atob(pubRow.public_key), (c) => c.charCodeAt(0));
    const pubKey = await crypto.subtle.importKey(
      'spki',
      spki as BufferSource,
      { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
      false,
      ['verify'],
    );
    const data = Uint8Array.from(atob(row.data_b64), (c) => c.charCodeAt(0));
    const sig = Uint8Array.from(atob(row.sig_b64), (c) => c.charCodeAt(0));
    const ok = await crypto.subtle.verify(
      { name: 'RSASSA-PKCS1-v1_5' },
      pubKey,
      sig as BufferSource,
      data as BufferSource,
    );
    if (!ok) return { ok: false, message: 'Chữ ký license không hợp lệ.' };
    return { ok: true, message: 'Chữ ký hợp lệ.' };
  } catch (e) {
    return { ok: false, message: `Lỗi xác thực: ${String(e)}` };
  }
}

/** Thu hồi (revoke) license. */
export async function revokeLicense(
  env: Env,
  licenseKey: string,
  reason: string,
  actor: string,
): Promise<boolean> {
  const existing = await env.DB.prepare(
    'SELECT id, revoked_at FROM licenses WHERE license_key = ?',
  ).bind(licenseKey).first<{ id: number; revoked_at: string | null }>();
  if (!existing) return false;
  if (existing.revoked_at) return true; // da thu hoi roi

  await env.DB.prepare(
    'UPDATE licenses SET revoked_at = ?, revoke_reason = ? WHERE license_key = ?',
  )
    .bind(new Date().toISOString(), (reason || '').trim() || null, licenseKey)
    .run();

  await audit(env, actor, 'revoke', `key=${licenseKey} reason=${reason || '-'}`);
  return true;
}

/** Gia hạn: cấp lại license mới cho cùng HWID, thay thế bản cũ. */
export async function renewLicense(
  env: Env,
  licenseKey: string,
  daysValid: number,
  actor: string,
): Promise<IssueResult> {
  const row = await env.DB.prepare(
    'SELECT customer, product, hwid FROM licenses WHERE license_key = ?',
  ).bind(licenseKey).first<{ customer: string; product: string; hwid: string }>();
  if (!row) throw new Error('Không tìm thấy license để gia hạn.');

  const result = await issueLicense(env, {
    customer: row.customer,
    hwid: row.hwid,
    daysValid,
    product: row.product,
    note: `Gia hạn từ ${licenseKey}`,
    createdBy: actor,
  });
  await revokeLicense(env, licenseKey, 'Thay thế bằng license gia hạn', actor);
  return result;
}

/** Khoa nghiep vu ngan: LK-YYYYMMDD-XXXXXX */
function generateLicenseKey(): string {
  const now = new Date();
  const y = now.getUTCFullYear();
  const m = String(now.getUTCMonth() + 1).padStart(2, '0');
  const d = String(now.getUTCDate()).padStart(2, '0');
  const rand = [...crypto.getRandomValues(new Uint8Array(4))]
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase();
  return `LK-${y}${m}${d}-${rand}`;
}

export async function audit(env: Env, actor: string, action: string, detail: string): Promise<void> {
  try {
    await env.DB.prepare(
      'INSERT INTO audit_log (actor, action, detail) VALUES (?, ?, ?)',
    ).bind(actor, action, detail).run();
  } catch {
    // Audit khong duoc lam fail request chinh.
  }
}

export { computeKeyId, groupHwid, normalizeHwid };
