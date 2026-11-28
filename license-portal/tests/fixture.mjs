import { createHash, generateKeyPairSync, pbkdf2Sync, randomBytes } from 'node:crypto';
import { readFile, readdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { Miniflare, Log, LogLevel, convertV4MiniflareOptions } from 'miniflare';

export const portalRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const testUser = 'local-test-admin';
export const testPassword = 'local-test-password';

// Chỉ dùng secret/khóa được tạo lúc chạy, không nạp .dev.vars hoặc dữ liệu D1 thật.
export async function createPortalFixture({ port = 0 } = {}) {
  const temporaryRoot = await mkdtemp(join(tmpdir(), 'visionf-license-tests-'));
  const githubState = {
    releases: [],
    apiStatus: 200,
    apiBody: null,
    assets: new Map(),
    requests: [],
  };
  const githubApiUrl = 'https://api.github.com/repos/ErfanMNM/VisionF/releases?per_page=100&page=1';
  const outboundService = async (request) => {
    githubState.requests.push({ url: request.url, method: request.method,
      headers: Object.fromEntries(request.headers.entries()) });
    if (request.url === githubApiUrl) {
      return new Response(githubState.apiBody ?? JSON.stringify(githubState.releases), {
        status: githubState.apiStatus, headers: { 'Content-Type': 'application/json' },
      });
    }
    const assetResponse = githubState.assets.get(request.url);
    if (!assetResponse) return new Response('Not found', { status: 404 });
    return assetResponse();
  };
  let runtime;
  try {
    const workerPath = join(temporaryRoot, 'worker.mjs');
    await build({ entryPoints: [join(portalRoot, 'src/index.ts')], outfile: workerPath,
      bundle: true, format: 'esm', target: 'es2022', platform: 'browser' });
    const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
    const spki = publicKey.export({ type: 'spki', format: 'der' });
    const keyId = createHash('sha256').update(spki).digest('hex').slice(0, 8).toUpperCase();
    const salt = randomBytes(16);
    const passwordHash = `pbkdf2$100000$${salt.toString('base64')}$${pbkdf2Sync(testPassword, salt, 100000, 32, 'sha256').toString('base64')}`;
    runtime = new Miniflare(convertV4MiniflareOptions({
      host: '127.0.0.1', port, modules: true, script: await readFile(workerPath, 'utf8'),
      compatibilityDate: '2026-11-11', compatibilityFlags: ['nodejs_compat_v2'],
      log: new Log(LogLevel.ERROR),
      d1Databases: { DB: 'local-test-license-db' },
      outboundService,
      bindings: {
        APP_NAME: 'TanTienQR License Portal · Kiểm thử', DEFAULT_PRODUCT: 'TanTienQR',
        UPDATE_METADATA_CACHE_TTL_SECONDS: '0',
        LICENSE_PRIVATE_KEY_PKCS8: privateKey.export({ type: 'pkcs8', format: 'der' }).toString('base64'),
        LICENSE_KEY_ID: keyId, ADMIN_USERNAME: testUser, ADMIN_PASSWORD_HASH: passwordHash,
        SESSION_SECRET: randomBytes(32).toString('hex'),
      },
      serviceBindings: {
        ASSETS: async (request) => {
          const publicRoot = join(portalRoot, 'public');
          const pathname = decodeURIComponent(new URL(request.url).pathname);
          const path = resolve(publicRoot, pathname === '/' ? 'index.html' : pathname.slice(1));
          if (!path.startsWith(publicRoot + sep)) return new Response('Not found', { status: 404 });
          try {
            const contentType = path.endsWith('.js') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html';
            return new Response(await readFile(path), { headers: { 'Content-Type': `${contentType}; charset=utf-8` } });
          } catch { return new Response('Not found', { status: 404 }); }
        },
      },
    }));
    await runtime.ready;
    const db = await runtime.getD1Database('DB');
    const migrations = (await readdir(join(portalRoot, 'migrations'))).filter(name => name.endsWith('.sql')).sort();
    for (const name of migrations) {
      const sql = await readFile(join(portalRoot, 'migrations', name), 'utf8');
      const statements = sql.replace(/--[^\n]*/g, '').split(';').map(query => query.trim()).filter(Boolean);
      await db.batch(statements.map(query => db.prepare(query)));
    }
    await db.prepare('DELETE FROM signing_keys').run();
    await db.prepare('INSERT INTO signing_keys (key_id, public_key) VALUES (?, ?)').bind(keyId, spki.toString('base64')).run();
    const github = {
      setReleases(releases) { githubState.releases = releases; },
      setApiResponse(status, body = '') { githubState.apiStatus = status; githubState.apiBody = body; },
      resetApiResponse() { githubState.apiStatus = 200; githubState.apiBody = null; },
      setAsset(url, body, { status = 200, headers = {} } = {}) {
        githubState.assets.set(url, () => new Response(body, { status, headers }));
      },
      setAssetResponse(url, factory) { githubState.assets.set(url, factory); },
      deleteAsset(url) { githubState.assets.delete(url); },
      clearRequests() { githubState.requests.length = 0; },
      get requests() { return [...githubState.requests]; },
    };
    return { runtime, db, github, publicKey,
      async dispose() {
        await runtime.dispose();
        await rm(temporaryRoot, { recursive: true, force: true });
      },
    };
  } catch (error) {
    await runtime?.dispose();
    await rm(temporaryRoot, { recursive: true, force: true });
    throw error;
  }
}
