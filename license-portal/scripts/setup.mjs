/**
 * Script setup mot lan: sinh cap khoa RSA, hash mat khau admin, tao D1 database,
 * dat secrets len Worker, xuat public key de dan vao app.
 *
 * Chay:  node scripts/setup.mjs
 *
 * KHONG commit file .dev.vars hay file private key duoc sinh ra.
 */

import { spawnSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { writeFileSync, existsSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const WRANGLER_CONFIG = join(ROOT, 'wrangler.jsonc');

const ADMIN_USERNAME = process.env.ADMIN_USERNAME || 'admin';
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || 'tantien512';
const PRODUCT = process.env.DEFAULT_PRODUCT || 'TanTienQR';
const DB_NAME = 'tantien-license';

function log(msg) { console.log(msg); }
function fail(msg) { console.error('ERROR: ' + msg); process.exit(1); }

function run(args, opts = {}) {
  // Tren Windows, .cmd can shell: true de spawn duoc tu Node.
  const localBin = join(ROOT, 'node_modules', '.bin', process.platform === 'win32' ? 'wrangler.cmd' : 'wrangler');
  const useLocal = existsSync(localBin);
  const cmd = useLocal ? localBin : 'npx';
  const cmdArgs = useLocal ? args : ['wrangler', ...args];

  const res = spawnSync(cmd, cmdArgs, {
    cwd: ROOT,
    encoding: 'utf8',
    shell: process.platform === 'win32',
    input: opts.input,
    env: { ...process.env, FORCE_COLOR: '0', NO_COLOR: '1' },
  });

  if (res.error) throw res.error;
  if (res.status !== 0) {
    const err = new Error(
      `Command failed (${res.status}): ${cmd} ${cmdArgs.join(' ')}\n${res.stderr || ''}`,
    );
    err.stdout = res.stdout;
    err.stderr = res.stderr;
    err.status = res.status;
    throw err;
  }
  return res.stdout || '';
}

// ---------------------------------------------------------------- 1. D1 database

log('[1/5] Kiem tra D1 database...');
let dbId = null;
try {
  const out = run(['d1', 'list']);
  // Output dang bang: "| <uuid> | <name> | ..." — tach theo dau '|'.
  for (const line of out.split(/\r?\n/)) {
    if (!line.includes(DB_NAME)) continue;
    // Bang cua wrangler dung ky tu box-drawing U+2502 (│), khong phai ASCII '|'.
    const cells = line.split(/[│|]/)
      .map((c) => c.replace(/[\u2500-\u257F]/g, '').trim())
      .filter(Boolean);
    const uuid = cells.find((c) => /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(c));
    if (uuid) { dbId = uuid; break; }
  }
} catch (e) {
  log('  (khong doc duoc danh sach: ' + (e.stderr || e.message) + ')');
}

if (!dbId) {
  log('  -> Tao database moi "' + DB_NAME + '"...');
  const out = run(['d1', 'create', DB_NAME]);
  const match = out.match(/database_id["\s:]+([a-f0-9-]{32,})/i);
  if (!match) fail('Khong tim thay database_id trong output:\n' + out);
  dbId = match[1];
  log('  -> Da tao: ' + dbId);
} else {
  log('  -> Da ton tai: ' + dbId);
}

// Ghi database_id vao wrangler.jsonc
let cfg = readFileSync(WRANGLER_CONFIG, 'utf8');
if (cfg.includes('PLACEHOLDER_DB_ID')) {
  cfg = cfg.replace('PLACEHOLDER_DB_ID', dbId);
  writeFileSync(WRANGLER_CONFIG, cfg);
  log('  -> Da cap nhat wrangler.jsonc');
}

// ---------------------------------------------------------------- 2. RSA key

log('[2/5] Sinh cap khoa RSA 2048...');
// Sinh khoa bang WebCrypto (Node 18+ co crypto.webcrypto)
const { subtle } = globalThis.crypto;
const kp = await subtle.generateKey(
  {
    name: 'RSASSA-PKCS1-v1_5',
    modulusLength: 2048,
    publicExponent: new Uint8Array([1, 0, 1]),
    hash: 'SHA-256',
  },
  true,
  ['sign', 'verify'],
);
const spki = new Uint8Array(await subtle.exportKey('spki', kp.publicKey));
const pkcs8 = new Uint8Array(await subtle.exportKey('pkcs8', kp.privateKey));
const spkiB64 = Buffer.from(spki).toString('base64');
const pkcs8B64 = Buffer.from(pkcs8).toString('base64');
const keyId = createHash('sha256').update(spki).digest('hex').slice(0, 8).toUpperCase();
log('  -> Key ID: ' + keyId);

// ---------------------------------------------------------------- 3. Password hash

log('[3/5] Tinh hash mat khau admin...');
// Cloudflare Workers chi ho tro PBKDF2 toi da 100.000 iterations.
const PBKDF2_ITERATIONS = 100_000;
const salt = randomBytes(16);
const hash = await pbkdf2(ADMIN_PASSWORD, salt, PBKDF2_ITERATIONS);
const passwordHash = `pbkdf2$${PBKDF2_ITERATIONS}$${salt.toString('base64')}$${hash}`;
log('  -> User: ' + ADMIN_USERNAME);

// ---------------------------------------------------------------- 4. Secrets

log('[4/5] Dat Worker secrets...');
const secrets = {
  LICENSE_PRIVATE_KEY_PKCS8: pkcs8B64,
  LICENSE_KEY_ID: keyId,
  ADMIN_USERNAME,
  ADMIN_PASSWORD_HASH: passwordHash,
  SESSION_SECRET: randomBytes(32).toString('base64'),
};

for (const [name, value] of Object.entries(secrets)) {
  try {
    run(['secret', 'put', name], { input: value });
    log('  -> ' + name);
  } catch (e) {
    fail('Dat secret "' + name + '" that bai: ' + (e.stderr || e.message));
  }
}

// Ghi .dev.vars de dev local co secret (KHONG commit — da them vao .gitignore)
const devVars = Object.entries(secrets).map(([k, v]) => `${k}="${v}"`).join('\n');
writeFileSync(join(ROOT, '.dev.vars'), devVars + '\n');
log('  -> Da ghi .dev.vars (local dev)');

// ---------------------------------------------------------------- 5. Migration

log('[5/5] Ap dung migration D1...');
try {
  const out = run(['d1', 'migrations', 'apply', DB_NAME, '--remote']);
  log(out.trim().split('\n').slice(-4).join('\n'));
} catch (e) {
  log('  Canh bao: migration that bai: ' + (e.stderr || e.message));
}

// ---------------------------------------------------------------- Output

log('');
log('='.repeat(64));
log('HOAN TAT SETUP');
log('='.repeat(64));
log('');
log('Public key (SPKI base64) — dan vao app:');
log('');
log('  ' + spkiB64);
log('');
log('Key ID: ' + keyId);
log('');
log('Ten dang nhap: ' + ADMIN_USERNAME);
log('Mat khau:      ' + ADMIN_PASSWORD);
log('');
log('Buoc tiep theo:');
log('  1. Mo ClassLibraryMT/Licensing/LicenseKeys.cs');
log('  2. Them public key + key id vao danh sach "All"');
log('  3. Build app:  dotnet build TanTienQR.csproj -c Release');
log('  4. Deploy web: npx wrangler deploy');
log('='.repeat(64));

// ---------------------------------------------------------------- helpers

async function pbkdf2(password, salt, iterations) {
  const enc = new TextEncoder();
  const key = await subtle.importKey('raw', enc.encode(password), { name: 'PBKDF2' }, false, ['deriveBits']);
  const saltBytes = new Uint8Array(salt);
  const bits = await subtle.deriveBits(
    { name: 'PBKDF2', salt: saltBytes, iterations, hash: 'SHA-256' }, key, 256);
  return Buffer.from(bits).toString('base64');
}
