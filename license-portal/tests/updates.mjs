import assert from 'node:assert/strict';
import { generateKeyPairSync, sign } from 'node:crypto';
import { test } from 'node:test';
import { createPortalFixture, testPassword, testUser } from './fixture.mjs';

test('Phát hành TanTienQR qua Worker, D1 và R2 cục bộ', async (suite) => {
  const fixture = await createPortalFixture();
  const { runtime, db, bucket } = fixture;
  const base = 'https://portal.test';
  const fetch = (path, options) => runtime.dispatchFetch(base + path, options);
  const version = '1.4.2';
  const channelKey = 'channels/stable.json';
  const windowsFolder = `releases/${version}/win-x64`;
  const linuxFolder = `releases/${version}/linux-x64`;
  const { privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const windowsManifest = Buffer.from(JSON.stringify({ version, rid: 'win-x64' }));
  const envelope = JSON.stringify({ data: windowsManifest.toString('base64'),
    sig: sign('RSA-SHA256', windowsManifest, privateKey).toString('base64') });
  const windowsZip = Uint8Array.of(80, 75, 3, 4, 5, 6);
  const linuxZip = Uint8Array.of(80, 75, 3, 4, 7, 8);

  try {
    const login = await fetch('/api/login', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: testUser, password: testPassword }) });
    assert.equal(login.status, 200);
    const cookie = login.headers.get('Set-Cookie').split(';')[0];
    await login.body.cancel();
    const adminPost = (path, data) => fetch(path, {
      method: 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: JSON.stringify(data),
    });
    const hwid = '00112233445566778899AABBCCDDEEFF';
    const issue = async (product, customer = 'Pilot') => {
      const issued = await adminPost('/api/licenses', { customer, hwid, product, daysValid: 30 });
      assert.equal(issued.status, 200);
      return issued.json();
    };
    const { licenseKey, licenseFile } = await issue('TanTienQR');
    const defaultBody = { license: licenseFile, rid: 'win-x64', channel: 'stable' };
    const updatePost = (endpoint, fields = {}) => fetch(`/api/updates/v1/${endpoint}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...defaultBody, ...fields }),
    });
    const expectStatus = async (status, endpoint, fields) => {
      const response = await updatePost(endpoint, fields);
      assert.equal(response.status, status);
      assert.equal(response.headers.get('Cache-Control'), 'no-store');
      await response.body?.cancel();
    };

    await suite.test('Chưa công bố kênh thì không thông báo bản mới', async () => {
      await expectStatus(204, 'check');
      await expectStatus(409, 'download', { version });
    });

    await bucket.put(`${windowsFolder}/manifest.json`, envelope);
    await bucket.put(`${windowsFolder}/package.zip`, windowsZip);
    await bucket.put(`${linuxFolder}/package.zip`, linuxZip);
    await bucket.put(channelKey, JSON.stringify({ version }));

    await suite.test('Trả nguyên manifest đã ký và truyền đúng ZIP theo nền tảng, không dùng cookie', async () => {
      const info = await updatePost('check');
      assert.equal(info.status, 200);
      assert.equal(info.headers.get('Cache-Control'), 'no-store');
      assert.equal(await info.text(), envelope);
      for (const [rid, zip] of [['win-x64', windowsZip], ['linux-x64', linuxZip]]) {
        const response = await updatePost('download', { rid, version });
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('Content-Type'), 'application/zip');
        assert.equal(response.headers.get('Cache-Control'), 'no-store');
        assert.deepEqual(new Uint8Array(await response.arrayBuffer()), zip);
      }
      await expectStatus(204, 'check', { channel: 'pilot' });
    });

    await suite.test('Phiên bản đã rút vẫn còn trong R2 nhưng không thể tải', async () => {
      await bucket.delete(channelKey);
      assert.ok(await bucket.head(`${windowsFolder}/package.zip`));
      await expectStatus(409, 'download', { version });
      await bucket.put(channelKey, '{}');
      await expectStatus(204, 'check');
      await expectStatus(409, 'download', { version });
      await bucket.put(channelKey, JSON.stringify({ version }));
      await expectStatus(409, 'download', { version: '1.4.1' });
    });

    await suite.test('Từ chối đầu vào sai, chứng thư chỉnh sửa, hết hạn và sai sản phẩm', async () => {
      const unsupportedMethod = await fetch('/api/updates/v1/check');
      assert.equal(unsupportedMethod.status, 405);
      await unsupportedMethod.body.cancel();
      const noContentType = await fetch('/api/updates/v1/check', { method: 'POST', body: '{}' });
      assert.equal(noContentType.status, 415);
      await noContentType.body.cancel();
      for (const raw of ['{', 'null', '[]']) {
        const badJson = await fetch('/api/updates/v1/check', { method: 'POST',
          headers: { 'Content-Type': 'application/json' }, body: raw });
        assert.equal(badJson.status, 400);
        await badJson.body.cancel();
      }
      const oversized = await fetch('/api/updates/v1/check', { method: 'POST',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ padding: 'x'.repeat(9000) }) });
      assert.equal(oversized.status, 413);
      await oversized.body.cancel();
      await expectStatus(400, 'check', { license: undefined });
      await expectStatus(400, 'check', { license: { ...licenseFile, v: 2 } });
      await expectStatus(400, 'check', { channel: '../../stable' });
      await expectStatus(400, 'check', { rid: 'win-arm64' });
      await expectStatus(400, 'download', { version: '1.4.2/../' });
      await expectStatus(400, 'check', { license: { ...licenseFile, sig: '!' } });
      await expectStatus(403, 'check', { license: { ...licenseFile, data: Buffer.from('Sửa nội dung').toString('base64') } });
      await expectStatus(403, 'download', { version, license: { ...licenseFile, sig: 'AQ==' } });

      await db.prepare('INSERT INTO products (code, name) VALUES (?, ?)').bind('OtherProduct', 'Sản phẩm phụ').run();
      const wrongProduct = await issue('OtherProduct');
      await expectStatus(403, 'check', { license: wrongProduct.licenseFile });

      const expired = await issue('TanTienQR', 'License hết hạn');
      await db.prepare('UPDATE licenses SET expires_at = ? WHERE license_key = ?')
        .bind('2000-01-01T00:00:00.000Z', expired.licenseKey).run();
      await expectStatus(403, 'download', { version, license: expired.licenseFile });
    });

    await suite.test('License từ công cụ offline hoặc đã xoá khỏi D1 không được quyền tải', async () => {
      const offline = await issue('TanTienQR', 'License chưa ghi nhận');
      await db.prepare('DELETE FROM licenses WHERE license_key = ?').bind(offline.licenseKey).run();
      await expectStatus(403, 'check', { license: offline.licenseFile });
    });

    await suite.test('Kho thiếu gói hoặc cấu hình kênh hỏng báo lỗi an toàn, không trả chi tiết nội bộ', async () => {
      await bucket.put('channels/pilot.json', JSON.stringify({ version: '../private' }));
      await expectStatus(503, 'check', { channel: 'pilot' });
      await bucket.put('channels/pilot.json', JSON.stringify({ version: '9.0.0' }));
      await expectStatus(503, 'check', { channel: 'pilot' });
      await expectStatus(503, 'download', { channel: 'pilot', version: '9.0.0' });
      await bucket.put(`${linuxFolder}/manifest.json`, 'x'.repeat(256 * 1024 + 1));
      await expectStatus(503, 'check', { rid: 'linux-x64' });
      await bucket.delete('channels/pilot.json');
    });

    await suite.test('Thu hồi có hiệu lực với lần tải kế tiếp dù cùng HWID vẫn có license hợp lệ', async () => {
      const replacement = await issue('TanTienQR', 'License thay thế');
      const revoke = await adminPost('/api/licenses/revoke', { licenseKey });
      assert.equal(revoke.status, 200);
      await revoke.body.cancel();
      await expectStatus(403, 'check');
      await expectStatus(403, 'download', { version });
      await expectStatus(200, 'download', { version, license: replacement.licenseFile });
      const deleted = await fetch('/api/licenses', { method: 'DELETE',
        headers: { Cookie: cookie, 'Content-Type': 'application/json' }, body: JSON.stringify({ licenseKey }) });
      assert.equal(deleted.status, 200);
      await deleted.body.cancel();
      await expectStatus(403, 'download', { version });
    });
  } finally {
    await fixture.dispose();
  }
});
