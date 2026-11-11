import { createSession, clearSessionCookieHeader, getCookie, sessionCookieHeader, verifyPassword, verifySession, SESSION_COOKIE } from './auth';
import { audit, issueLicense, renewLicense, revokeLicense, verifyStoredLicense } from './license';
import type { Env } from './types';

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname;

    try {
      if (path.startsWith('/api/')) {
        return await handleApi(request, env, url);
      }
      // Phan con lai: phuc vu static assets (UI).
      return env.ASSETS.fetch(request);
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      console.error('Unhandled error:', message);
      return json({ error: message }, 500);
    }
  },
};

async function handleApi(request: Request, env: Env, url: URL): Promise<Response> {
  const path = url.pathname;
  const method = request.method.toUpperCase();

  // ---- API cong khai ----
  if (path === '/api/health') {
    return json({ ok: true, app: env.APP_NAME, time: new Date().toISOString() });
  }

  if (path === '/api/session' && method === 'GET') {
    const user = await currentUser(request, env);
    return json({ authenticated: user !== null, username: user });
  }

  if (path === '/api/login' && method === 'POST') {
    return await handleLogin(request, env);
  }

  if (path === '/api/logout' && method === 'POST') {
    return json({ ok: true }, 200, { 'Set-Cookie': clearSessionCookieHeader() });
  }

  // ---- API can dang nhap ----
  const user = await currentUser(request, env);
  if (!user) {
    return json({ error: 'Chua dang nhap.' }, 401);
  }

  if (path === '/api/licenses' && method === 'GET') {
    return await handleListLicenses(env, url);
  }

  if (path === '/api/licenses' && method === 'POST') {
    return await handleIssue(request, env, user);
  }

  if (path === '/api/licenses/download' && method === 'GET') {
    return await handleDownload(env, url);
  }

  if (path === '/api/licenses/revoke' && method === 'POST') {
    return await handleRevoke(request, env, user);
  }

  if (path === '/api/licenses/renew' && method === 'POST') {
    return await handleRenew(request, env, user);
  }

  if (path === '/api/licenses/verify' && method === 'POST') {
    return await handleVerify(request, env);
  }

  if (path === '/api/products' && method === 'GET') {
    const { results } = await env.DB.prepare(
      'SELECT code, name FROM products WHERE active = 1 ORDER BY code',
    ).all<{ code: string; name: string }>();
    return json({ products: results });
  }

  if (path === '/api/audit' && method === 'GET') {
    const { results } = await env.DB.prepare(
      'SELECT actor, action, detail, created_at FROM audit_log ORDER BY id DESC LIMIT 100',
    ).all();
    return json({ entries: results });
  }

  if (path === '/api/public-key' && method === 'GET') {
    const row = await env.DB.prepare(
      'SELECT key_id, public_key, algorithm FROM signing_keys WHERE active = 1 ORDER BY created_at DESC LIMIT 1',
    ).first<{ key_id: string; public_key: string; algorithm: string }>();
    if (!row) return json({ error: 'Chua cau hinh khoa ky.' }, 404);
    return json(row);
  }

  return json({ error: 'Endpoint khong ton tai.' }, 404);
}

// ---------------------------------------------------------------- handlers

async function handleLogin(request: Request, env: Env): Promise<Response> {
  let body: { username?: string; password?: string };
  try {
    body = await request.json();
  } catch {
    return json({ error: 'Du lieu gui len khong hop le.' }, 400);
  }

  const username = (body.username ?? '').trim();
  const password = body.password ?? '';
  if (!username || !password) {
    return json({ error: 'Vui long nhap ten dang nhap va mat khau.' }, 400);
  }

  // Uu tien dung tai khoan trong D1; neu rong dung secret env.
  const row = await env.DB.prepare(
    'SELECT username, password_hash FROM admins WHERE username = ?',
  ).bind(username).first<{ username: string; password_hash: string }>();

  const expectedHash = row?.password_hash ?? env.ADMIN_PASSWORD_HASH;
  const expectedUser = row?.username ?? env.ADMIN_USERNAME;

  if (!expectedHash || username !== expectedUser) {
    // Van phai tinh hash de tránh leak thoi gian.
    await verifyPassword(password, 'pbkdf2$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=');
    return json({ error: 'Ten dang nhap hoac mat khau sai.' }, 401);
  }

  const ok = await verifyPassword(password, expectedHash);
  if (!ok) return json({ error: 'Ten dang nhap hoac mat khau sai.' }, 401);

  const token = await createSession(username, env.SESSION_SECRET);
  await env.DB.prepare(
    'UPDATE admins SET last_login_at = ? WHERE username = ?',
  ).bind(new Date().toISOString(), username).run();

  await audit(env, username, 'login', 'Dang nhap thanh cong');
  return json({ ok: true, username }, 200, { 'Set-Cookie': sessionCookieHeader(token) });
}

async function handleListLicenses(env: Env, url: URL): Promise<Response> {
  const q = (url.searchParams.get('q') ?? '').trim();
  const product = (url.searchParams.get('product') ?? '').trim();
  const status = (url.searchParams.get('status') ?? 'all').trim();
  const limit = Math.min(Math.max(parseInt(url.searchParams.get('limit') ?? '100', 10) || 100, 1), 500);
  const offset = Math.max(parseInt(url.searchParams.get('offset') ?? '0', 10) || 0, 0);

  const where: string[] = [];
  const binds: string[] = [];

  if (q) {
    where.push('(customer LIKE ? OR hwid LIKE ? OR license_key LIKE ? OR hwid_display LIKE ?)');
    const like = `%${q}%`;
    binds.push(like, like, like, like);
  }
  if (product) {
    where.push('product = ?');
    binds.push(product);
  }
  if (status === 'active') {
    where.push('revoked_at IS NULL AND expires_at > ?');
    binds.push(new Date().toISOString());
  } else if (status === 'expired') {
    where.push('revoked_at IS NULL AND expires_at <= ?');
    binds.push(new Date().toISOString());
  } else if (status === 'revoked') {
    where.push('revoked_at IS NOT NULL');
  }

  const whereSql = where.length ? `WHERE ${where.join(' AND ')}` : '';

  const countRow = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM licenses ${whereSql}`,
  ).bind(...binds).first<{ n: number }>();

  const { results } = await env.DB.prepare(
    `SELECT license_key, customer, product, hwid_display, days_valid,
            issued_at, expires_at, key_id, revoked_at, revoke_reason, note, created_by, created_at
       FROM licenses ${whereSql}
      ORDER BY id DESC LIMIT ? OFFSET ?`,
  ).bind(...binds, limit, offset).all();

  return json({
    licenses: results,
    total: countRow?.n ?? 0,
    limit,
    offset,
  });
}

async function handleIssue(request: Request, env: Env, user: string): Promise<Response> {
  let body: { customer?: string; hwid?: string; daysValid?: number; product?: string; note?: string };
  try {
    body = await request.json();
  } catch {
    return json({ error: 'Du lieu gui len khong hop le.' }, 400);
  }

  try {
    const result = await issueLicense(env, {
      customer: body.customer ?? '',
      hwid: body.hwid ?? '',
      daysValid: Number(body.daysValid ?? 30),
      product: (body.product ?? '').trim() || env.DEFAULT_PRODUCT,
      note: body.note ?? '',
      createdBy: user,
    });
    return json({
      ok: true,
      licenseKey: result.licenseKey,
      licenseFile: result.file,
      licenseText: result.fileText,
      payload: result.payload,
      expiresAt: result.expiresAt,
      hwidDisplay: result.hwidDisplay,
    });
  } catch (e) {
    const message = e instanceof Error ? e.message : String(e);
    return json({ error: message }, 400);
  }
}

async function handleDownload(env: Env, url: URL): Promise<Response> {
  const key = (url.searchParams.get('key') ?? '').trim();
  if (!key) return json({ error: 'Thieu key.' }, 400);

  const row = await env.DB.prepare(
    'SELECT data_b64, sig_b64 FROM licenses WHERE license_key = ?',
  ).bind(key).first<{ data_b64: string; sig_b64: string }>();
  if (!row) return json({ error: 'Khong tim thay license.' }, 404);

  const file = { v: 1, data: row.data_b64, sig: row.sig_b64 };
  return new Response(JSON.stringify(file), {
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      'Content-Disposition': `attachment; filename="license-${key}.lic"`,
      'Cache-Control': 'no-store',
    },
  });
}

async function handleRevoke(request: Request, env: Env, user: string): Promise<Response> {
  let body: { licenseKey?: string; reason?: string };
  try {
    body = await request.json();
  } catch {
    return json({ error: 'Du lieu gui len khong hop le.' }, 400);
  }

  const key = (body.licenseKey ?? '').trim();
  if (!key) return json({ error: 'Thieu license key.' }, 400);

  const ok = await revokeLicense(env, key, body.reason ?? '', user);
  if (!ok) return json({ error: 'Khong tim thay license.' }, 404);
  return json({ ok: true });
}

async function handleRenew(request: Request, env: Env, user: string): Promise<Response> {
  let body: { licenseKey?: string; daysValid?: number };
  try {
    body = await request.json();
  } catch {
    return json({ error: 'Du lieu gui len khong hop le.' }, 400);
  }

  const key = (body.licenseKey ?? '').trim();
  if (!key) return json({ error: 'Thieu license key.' }, 400);

  try {
    const result = await renewLicense(env, key, Number(body.daysValid ?? 30), user);
    return json({
      ok: true,
      licenseKey: result.licenseKey,
      licenseFile: result.file,
      licenseText: result.fileText,
      payload: result.payload,
      expiresAt: result.expiresAt,
      hwidDisplay: result.hwidDisplay,
    });
  } catch (e) {
    const message = e instanceof Error ? e.message : String(e);
    return json({ error: message }, 400);
  }
}

async function handleVerify(request: Request, env: Env): Promise<Response> {
  let body: { licenseKey?: string };
  try {
    body = await request.json();
  } catch {
    return json({ error: 'Du lieu gui len khong hop le.' }, 400);
  }
  const key = (body.licenseKey ?? '').trim();
  if (!key) return json({ error: 'Thieu license key.' }, 400);

  const result = await verifyStoredLicense(env, key);
  return json(result, result.ok ? 200 : 400);
}

// ---------------------------------------------------------------- helpers

async function currentUser(request: Request, env: Env): Promise<string | null> {
  const token = getCookie(request, SESSION_COOKIE);
  if (!token) return null;
  return verifySession(token, env.SESSION_SECRET);
}

function json(data: unknown, status = 200, extraHeaders: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      ...extraHeaders,
    },
  });
}
