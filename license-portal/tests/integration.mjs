import assert from 'node:assert/strict';
import { verify } from 'node:crypto';
import { test } from 'node:test';
import { createPortalFixture, testPassword, testUser } from './fixture.mjs';

test('Portal license với Workers và D1 cục bộ', async (suite) => {
  const fixture = await createPortalFixture();
  const { runtime, db } = fixture;
  let cookie = '';
  const machine = '00112233445566778899AABBCCDDEEFF';
  const grouped = machine.match(/.{4}/g).join('-');
  let requestId, issuedKey;

  async function call(path, { method = 'GET', body, authenticated = true, raw } = {}) {
    const response = await runtime.dispatchFetch('https://portal.test' + path, {
      method, headers: { 'Content-Type': 'application/json', ...(authenticated && cookie ? { Cookie: cookie } : {}) },
      body: raw ?? (body === undefined ? undefined : JSON.stringify(body)),
    });
    const data = await response.json();
    return { response, status: response.status, data };
  }
  async function count(table) {
    return (await db.prepare(`SELECT COUNT(*) AS n FROM ${table}`).first()).n;
  }
  const issueBody = { customer: 'Khách hàng kiểm thử', hwid: machine, product: 'TanTienQR', daysValid: 30 };

  try {
    await suite.test('API quản trị từ chối khách chưa đăng nhập', async () => {
      for (const options of [
        ['/api/license-requests'], ['/api/licenses'],
        ['/api/licenses', { method: 'DELETE', body: { licenseKey: 'NO-KEY' } }],
        ['/api/licenses', { method: 'POST', body: issueBody }],
      ]) {
        const result = await call(options[0], { ...options[1], authenticated: false });
        assert.equal(result.status, 401);
      }
    });

    await suite.test('Gửi HWID công khai, chuẩn hoá mã và metadata', async () => {
      const result = await call('/api/license-requests', { method: 'POST', authenticated: false,
        body: { hwid: grouped.toLowerCase(), product: 'TanTienQR', customer: '  Công ty kiểm thử  ', contact: 'test@example.invalid', note: 'Đề nghị cấp license' } });
      assert.equal(result.status, 200);
      assert.equal(result.data.ok, true);
      assert.equal(result.data.status, 'pending');
      assert.match(result.data.requestId, /^[0-9a-f-]{36}$/i);
      assert.equal(result.data.licenseText, undefined);
      assert.equal(result.data.licenseKey, undefined);
      requestId = result.data.requestId;
      const row = await db.prepare('SELECT * FROM license_requests WHERE id = ?').bind(requestId).first();
      assert.equal(row.hwid, machine);
      assert.equal(row.hwid_display, grouped);
      assert.equal(row.customer, 'Công ty kiểm thử');
      assert.equal(await count('licenses'), 0);
    });

    await suite.test('Gửi lặp và đồng thời chỉ giữ một yêu cầu pending', async () => {
      const results = await Promise.all(Array.from({ length: 6 }, () => call('/api/license-requests',
        { method: 'POST', authenticated: false, body: { hwid: machine, product: 'TanTienQR' } })));
      assert.ok(results.every(result => result.status === 200 && result.data.requestId === requestId));
      assert.equal(await count('license_requests'), 1);
    });

    await suite.test('Từ chối HWID, kiểu dữ liệu, sản phẩm và kích thước body sai', async () => {
      for (const body of [null, [], { hwid: 'ABC' }, { hwid: machine + 'Z' }, { hwid: 123 },
        { hwid: machine, customer: {} }, { hwid: machine, contact: 'x'.repeat(201) },
        { hwid: machine, note: 'x'.repeat(1001) }, { hwid: machine, product: 'NOT-A-PRODUCT' }]) {
        assert.equal((await call('/api/license-requests', { method: 'POST', authenticated: false, body })).status, 400);
      }
      assert.equal((await call('/api/license-requests', { method: 'POST', authenticated: false, raw: '{' })).status, 400);
      assert.equal((await call('/api/license-requests', { method: 'POST', authenticated: false,
        body: { hwid: machine, note: 'x'.repeat(9000) } })).status, 413);
      assert.equal(await count('license_requests'), 1);
    });

    await suite.test('Đăng nhập admin bằng tài khoản chỉ tồn tại trong test', async () => {
      const bad = await call('/api/login', { method: 'POST', body: { username: testUser, password: 'wrong' } });
      assert.equal(bad.status, 401);
      const result = await call('/api/login', { method: 'POST', body: { username: testUser, password: testPassword } });
      assert.equal(result.status, 200);
      cookie = result.response.headers.get('set-cookie').split(';')[0];
      assert.match(cookie, /^tt_session=/);
      assert.equal((await call('/api/session')).data.authenticated, true);
    });

    await suite.test('Admin xem và lọc yêu cầu; người ngoài không xem metadata', async () => {
      const result = await call('/api/license-requests?status=pending&limit=1&offset=0');
      assert.equal(result.status, 200);
      assert.equal(result.data.total, 1);
      assert.equal(result.data.requests[0].id, requestId);
      assert.equal(result.data.requests[0].contact, 'test@example.invalid');
      assert.equal((await call('/api/license-requests?status=invalid')).status, 400);
      assert.equal((await call('/api/license-requests', { authenticated: false })).status, 401);
    });

    await suite.test('Cấp license phải khớp HWID và sản phẩm của yêu cầu', async () => {
      const result = await call('/api/licenses', { method: 'POST', body: { ...issueBody, requestId, hwid: 'A'.repeat(32) } });
      assert.equal(result.status, 409);
      assert.equal(await count('licenses'), 0);
      assert.equal((await db.prepare('SELECT status FROM license_requests WHERE id = ?').bind(requestId).first()).status, 'pending');
    });

    await suite.test('Duyệt đồng thời chỉ cấp một license và ký đúng payload', async () => {
      const results = await Promise.all([0, 1].map(() => call('/api/licenses',
        { method: 'POST', body: { ...issueBody, requestId } })));
      assert.deepEqual(results.map(result => result.status).sort(), [200, 409]);
      const result = results.find(item => item.status === 200).data;
      issuedKey = result.licenseKey;
      assert.equal(await count('licenses'), 1);
      const row = await db.prepare('SELECT * FROM license_requests WHERE id = ?').bind(requestId).first();
      assert.equal(row.status, 'issued');
      assert.equal(row.license_key, issuedKey);
      const payload = Buffer.from(result.licenseFile.data, 'base64');
      assert.equal(JSON.parse(payload.toString()).hwid, machine);
      assert.equal(verify('RSA-SHA256', payload, fixture.publicKey, Buffer.from(result.licenseFile.sig, 'base64')), true);
      assert.equal((await call('/api/license-requests?status=pending')).data.total, 0);
      assert.equal((await call('/api/license-requests?status=issued')).data.total, 1);
      assert.equal((await call('/api/licenses', { method: 'POST', body: { ...issueBody, requestId } })).status, 409);
      assert.equal(await count('licenses'), 1);
    });

    await suite.test('Không thể xoá license chưa thu hồi, kể cả đã hết hạn', async () => {
      assert.equal((await call('/api/licenses', { method: 'DELETE', body: { licenseKey: issuedKey } })).status, 409);
      await db.prepare('UPDATE licenses SET expires_at = ? WHERE license_key = ?').bind('2000-01-01T00:00:00.000Z', issuedKey).run();
      assert.equal((await call('/api/licenses', { method: 'DELETE', body: { licenseKey: issuedKey } })).status, 409);
      assert.equal(await count('licenses'), 1);
    });

    await suite.test('Xoá license đã thu hồi, giữ nhật ký và lịch sử yêu cầu', async () => {
      assert.equal((await call('/api/licenses/revoke', { method: 'POST', body: { licenseKey: issuedKey, reason: 'Kết thúc kiểm thử' } })).status, 200);
      assert.equal((await call('/api/licenses', { method: 'DELETE', body: { licenseKey: issuedKey }, authenticated: false })).status, 401);
      const result = await call('/api/licenses', { method: 'DELETE', body: { licenseKey: issuedKey } });
      assert.equal(result.status, 200);
      assert.equal(await count('licenses'), 0);
      const auditRows = await db.prepare('SELECT action, detail FROM audit_log WHERE detail LIKE ?').bind(`%${issuedKey}%`).all();
      assert.ok(auditRows.results.some(row => row.action.includes('delete')));
      const row = await db.prepare('SELECT status, license_key FROM license_requests WHERE id = ?').bind(requestId).first();
      assert.equal(row.status, 'issued');
      assert.equal(row.license_key, null);
      assert.equal((await call('/api/licenses/download?key=' + issuedKey)).status, 404);
      assert.equal((await call('/api/licenses', { method: 'DELETE', body: { licenseKey: issuedKey } })).status, 404);
    });

    await suite.test('Luồng cấp thủ công cũ vẫn hoạt động, máy có thể gửi yêu cầu mới sau duyệt', async () => {
      const result = await call('/api/licenses', { method: 'POST', body: issueBody });
      assert.equal(result.status, 200);
      assert.equal((await call('/api/licenses/verify', { method: 'POST', body: { licenseKey: result.data.licenseKey } })).status, 200);
      const request = await call('/api/license-requests', { method: 'POST', authenticated: false, body: { hwid: machine, product: 'TanTienQR' } });
      assert.equal(request.status, 200);
      assert.notEqual(request.data.requestId, requestId);
    });
  } finally {
    await fixture.dispose();
  }
});
