/**
 * Web Crypto helpers cho Cloudflare Workers.
 *
 * QUAN TRONG: Worker runtime chi ho tro RSASSA-PKCS1-v1_5 (PKCS#1 v1.5), KHONG ho tro
 * RSASSA-PSS. App .NET verify license bang `RSASignaturePadding.Pkcs1` — vay vay
 * su dung PKCS1-v1_5 o server de tuong thich 100%, khong can sua app.
 */

const enc = new TextEncoder();

export function toBase64(bytes: ArrayBuffer | Uint8Array): string {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < view.length; i += chunk) {
    binary += String.fromCharCode(...view.subarray(i, i + chunk));
  }
  return btoa(binary);
}

export function fromBase64(b64: string): Uint8Array {
  const clean = b64.trim().replace(/\s+/g, '');
  const binary = atob(clean);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}

export async function sha256Hex(data: ArrayBuffer | Uint8Array): Promise<string> {
  const view = data instanceof Uint8Array ? data : new Uint8Array(data);
  const digest = crypto.subtle.digest('SHA-256', view as BufferSource);
  return [...new Uint8Array(await digest)]
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase();
}

/**
 * Chuuan hoa HWID giong ClassLibraryMT/Licensing/HardwareId.Normalize:
 * bo gach/cach, doi hoa, chi giu 0-9 A-F.
 */
export function normalizeHwid(input: string | null | undefined): string {
  if (!input) return '';
  let out = '';
  for (const c of input.toUpperCase()) {
    if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')) out += c;
  }
  return out;
}

/** Hien thi dang XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX. */
export function groupHwid(hwid: string): string {
  if (hwid.length !== 32) return hwid;
  const parts: string[] = [];
  for (let i = 0; i < 32; i += 4) parts.push(hwid.slice(i, i + 4));
  return parts.join('-');
}

export interface RsaKeyPair {
  privateKeyPkcs8: string; // base64 PKCS#8 — luu vao Worker secret
  publicKeySpki: string;   // base64 SPKI — dua vao app
  keyId: string;           // 8 hex = SHA256(SPKI)[0..8]
}

/** Sinh cap khoa RSA 2048 moi. Chi dung luc setup, khong goi trong request handler. */
export async function generateRsaKeyPair(): Promise<RsaKeyPair> {
  const kp = await crypto.subtle.generateKey(
    {
      name: 'RSASSA-PKCS1-v1_5',
      modulusLength: 2048,
      publicExponent: new Uint8Array([1, 0, 1]),
      hash: 'SHA-256',
    },
    true,
    ['sign', 'verify'],
  ) as CryptoKeyPair;

  const spki = new Uint8Array(await crypto.subtle.exportKey('spki', kp.publicKey) as ArrayBuffer);
  const pkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', kp.privateKey) as ArrayBuffer);
  const spkiB64 = toBase64(spki);
  return {
    privateKeyPkcs8: toBase64(pkcs8),
    publicKeySpki: spkiB64,
    keyId: (await sha256Hex(spki)).slice(0, 8),
  };
}

/** Import private key PKCS#8 (base64) de ky. */
export async function importSigningKey(pkcs8Base64: string): Promise<CryptoKey> {
  const raw = fromBase64(pkcs8Base64);
  return crypto.subtle.importKey(
    'pkcs8',
    raw as BufferSource,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['sign'],
  );
}

/** Tinh kid = 8 ky tu hex dau cu SHA256(public key SPKI), giong LicenseKeys.ComputeKeyId. */
export async function computeKeyId(spkiBase64: string): Promise<string> {
  return (await sha256Hex(fromBase64(spkiBase64))).slice(0, 8);
}

/** Ky SHA-256 bang RSASSA-PKCS1-v1_5. */
export async function signData(privateKey: CryptoKey, data: Uint8Array): Promise<string> {
  const sig = await crypto.subtle.sign(
    { name: 'RSASSA-PKCS1-v1_5' },
    privateKey,
    data as BufferSource,
  );
  return toBase64(sig);
}

export { enc };
