/**
 * Logic cap license. Payload phai KHOP 100% voi
 * ClassLibraryMT/Licensing/LicenseModels.cs + LicenseSigner.cs:
 *
 *   { hwid, customer, issued, expires, product, kid }
 *
 * va file license.lic:
 *   { "v": 1, "data": "<base64 json>", "sig": "<base64 RSA-SHA256 PKCS1v1.5>" }
 *
 * LUU Y: .NET Serialize mac dinh ghi DateTime theo ISO 8601 round-trip
 * (vi du "2026-11-02T09:40:54.8457572Z") — ta dung ISO 8601 cung format.
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
 * Tao va ky 1 license moi.
 */
export async function issueLicense(
  env: Env,
  input: { customer: string; hwid: string; daysValid: number; product: string; note?: string; createdBy: string },
): Promise<IssueResult> {
  const customer = input.customer.trim();
  if (!customer) throw new Error('Ten khach hang khong duoc de trong.');
  if (customer.length > 200) throw new Error('Ten khach hang qua dai (toi da 200 ky tu).');

  const hwid = normalizeHwid(input.hwid);
  if (hwid.length !== 32) {
    throw new Error('HWID phai la 32 ky tu hex. Vao app -> Quan tri -> License de lay ma may.');
  }

  const daysValid = Math.floor(Number(input.daysValid));
  if (!Number.isFinite(daysValid) || daysValid <= 0) {
    throw new Error('So ngay hieu luc phai > 0.');
  }
  if (daysValid > 3650) throw new Error('So ngay hieu luc toi da 3650 ngay (10 nam).');

  const product = (input.product || env.DEFAULT_PRODUCT).trim();
  if (!product) throw new Error('San pham khong duoc de trong.');
  if (product.length > 100) throw new Error('Ten san pham qua dai.');

  // Kiem tra san pham co ton tai trong bang products khong.
  const productRow = await env.DB.prepare(
    'SELECT code FROM products WHERE code = ? AND active = 1',
  ).bind(product).first<{ code: string }>();
  if (!productRow) throw new Error(`San pham "${product}" khong ton tai hoac da bi vo hieu hoa.`);

  // Load private key tu Worker secret.
  const pkcs8 = (env.LICENSE_PRIVATE_KEY_PKCS8 ?? '').trim();
  if (!pkcs8) throw new Error('Chua cau hinh LICENSE_PRIVATE_KEY_PKCS8 tren Worker.');

  let keyId = (env.LICENSE_KEY_ID ?? '').trim();
  if (!/^[0-9A-Fa-f]{8}$/.test(keyId)) {
    throw new Error('LICENSE_KEY_ID phai la 8 ky tu hex.');
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

  // Thu tu field phai giong .NET: hwid, customer, issued, expires, product, kid.
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
 * Kiem tra chu ky cua 1 license da luu (dung cho chuc nang "verify").
 * Worker verify bang WebCrypto, app verify bang .NET — ca hai cung PKCS1v1.5.
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
  if (!row) return { ok: false, message: 'Khong tim thay license.' };
  if (row.revoked_at) return { ok: false, message: `License da bi thu hoi (${row.revoked_at}).` };
  if (new Date(row.expires_at).getTime() < Date.now()) {
    return { ok: false, message: `License het han (${row.expires_at.slice(0, 10)}).` };
  }

  const pubRow = await env.DB.prepare(
    'SELECT public_key FROM signing_keys WHERE key_id = ?',
  ).bind(row.key_id).first<{ public_key: string }>();
  if (!pubRow) return { ok: false, message: `Khong tim thay public key cho kid=${row.key_id}.` };

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
    if (!ok) return { ok: false, message: 'Chu ky license khong hop le.' };
    return { ok: true, message: 'Chu ky hop le.' };
  } catch (e) {
    return { ok: false, message: `Loi xac thuc: ${String(e)}` };
  }
}

/** Thu hoi (revoke) license. */
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

/** Gia han: cap lai license moi cho cung HWID, thay the ban cu. */
export async function renewLicense(
  env: Env,
  licenseKey: string,
  daysValid: number,
  actor: string,
): Promise<IssueResult> {
  const row = await env.DB.prepare(
    'SELECT customer, product, hwid FROM licenses WHERE license_key = ?',
  ).bind(licenseKey).first<{ customer: string; product: string; hwid: string }>();
  if (!row) throw new Error('Khong tim thay license de gia han.');

  const result = await issueLicense(env, {
    customer: row.customer,
    hwid: row.hwid,
    daysValid,
    product: row.product,
    note: `Gia han tu ${licenseKey}`,
    createdBy: actor,
  });
  await revokeLicense(env, licenseKey, 'Thay the bang license gia han', actor);
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
