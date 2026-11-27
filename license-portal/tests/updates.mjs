import assert from 'node:assert/strict';
import { generateKeyPairSync, sign } from 'node:crypto';
import { test } from 'node:test';
import { createPortalFixture, testPassword, testUser } from './fixture.mjs';

const githubAssetUrl = (tag, name) => `https://github.com/ErfanMNM/VisionF/releases/download/${tag}/${name}`;

test('Phát hành TanTienQR qua Worker, D1 và GitHub Releases', async (suite) => {
  const fixture = await createPortalFixture();
  const { runtime, db, github } = fixture;
  const base = 'https://portal.test';
  const fetch = (path, options) => runtime.dispatchFetch(base + path, options);
  const { privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const packageBytes = {
    'win-x64': Uint8Array.of(80, 75, 3, 4, 5, 6),
    'linux-x64': Uint8Array.of(80, 75, 3, 4, 7, 8),
  };

  const publish = (version, { prerelease = false, draft = false } = {}) => {
    const tag = `v${version}`;
    const assets = [];
    const envelopes = {};
    for (const rid of ['win-x64', 'linux-x64']) {
      const payload = Buffer.from(JSON.stringify({ version, rid }));
      const envelope = Buffer.from(JSON.stringify({ data: payload.toString('base64'),
        sig: sign('RSA-SHA256', payload, privateKey).toString('base64') }));
      const manifestName = `manifest-${rid}.json`;
      const packageName = `package-${rid}.zip`;
      const manifestUrl = githubAssetUrl(tag, manifestName);
      const packageUrl = githubAssetUrl(tag, packageName);
      github.setAsset(manifestUrl, envelope);
      github.setAsset(packageUrl, packageBytes[rid]);
      assets.push(
        { name: manifestName, size: envelope.length, browser_download_url: manifestUrl },
        { name: packageName, size: packageBytes[rid].length, browser_download_url: packageUrl },
      );
      envelopes[rid] = envelope;
    }
    return { release: { tag_name: tag, prerelease, draft, published_at: '2026-11-28T00:00:00Z', assets }, envelopes };
  };

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

    await suite.test('Chưa có release phù hợp thì không thông báo bản mới', async () => {
      github.setReleases([]);
      await expectStatus(204, 'check');
      await expectStatus(409, 'download', { version: '1.4.2' });
    });

    const oldStable = publish('1.4.1');
    const currentStable = publish('2.0.0');
    const currentPilot = publish('2.1.0', { prerelease: true });
    const draft = publish('99.0.0', { draft: true });
    github.setReleases([
      { ...draft.release, tag_name: 'v99.0.0' },
      currentPilot.release,
      { tag_name: 'release-not-semver', prerelease: false, draft: false,
        published_at: '2026-11-28T00:00:00Z', assets: [] },
      oldStable.release,
      currentStable.release,
    ]);

    await suite.test('Stable và pilot chọn đúng release; manifest giữ nguyên bytes và ZIP được stream', async () => {
      const info = await updatePost('check');
      assert.equal(info.status, 200);
      assert.equal(info.headers.get('Cache-Control'), 'no-store');
      assert.deepEqual(Buffer.from(await info.arrayBuffer()), currentStable.envelopes['win-x64']);

      for (const [rid, bytes] of Object.entries(packageBytes)) {
        const response = await updatePost('download', { rid, version: '2.0.0' });
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('Content-Type'), 'application/zip');
        assert.equal(response.headers.get('Cache-Control'), 'no-store');
        assert.deepEqual(new Uint8Array(await response.arrayBuffer()), bytes);
      }

      const pilot = await updatePost('check', { channel: 'pilot' });
      assert.equal(pilot.status, 200);
      assert.deepEqual(Buffer.from(await pilot.arrayBuffer()), currentPilot.envelopes['win-x64']);

      const outbound = github.requests;
      assert.ok(outbound.every((request) => request.method === 'GET'));
      assert.ok(outbound.every((request) => !request.headers.authorization));
      assert.ok(outbound.every((request) => !request.url.includes(licenseFile.data)));
      assert.ok(outbound.every((request) => !request.url.includes(licenseFile.sig)));
    });

    await suite.test('Sắp xếp phiên bản theo từng thành phần SemVer dạng số', async () => {
      const lower = publish('1.9.0');
      const higher = publish('1.10.0');
      github.setReleases([lower.release, higher.release]);
      const response = await updatePost('check');
      assert.equal(response.status, 200);
      assert.deepEqual(Buffer.from(await response.arrayBuffer()), higher.envelopes['win-x64']);
      github.setReleases([draft.release, currentPilot.release,
        { tag_name: 'release-not-semver', prerelease: false, draft: false,
          published_at: '2026-11-28T00:00:00Z', assets: [] },
        oldStable.release, currentStable.release]);
    });

    await suite.test('Stale version conflicts before downloading an asset', async () => {
      github.clearRequests();
      await expectStatus(409, 'download', { version: '1.4.1' });
      assert.equal(github.requests.filter((request) => request.url.startsWith('https://github.com/')).length, 0);
    });

    await suite.test('Từ chối input sai, license không hợp lệ/hết hạn/sai sản phẩm trước khi gọi GitHub', async () => {
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
      await expectStatus(400, 'download', { version: '2.0.0/../' });
      await expectStatus(400, 'check', { license: { ...licenseFile, sig: '!' } });
      await expectStatus(403, 'check', { license: { ...licenseFile, data: Buffer.from('Sửa nội dung').toString('base64') } });
      await expectStatus(403, 'download', { version: '2.0.0', license: { ...licenseFile, sig: 'AQ==' } });

      const requestsBeforeDenied = github.requests.length;
      const expired = await issue('TanTienQR', 'License hết hạn');
      await db.prepare('UPDATE licenses SET expires_at = ? WHERE license_key = ?')
        .bind('2000-01-01T00:00:00.000Z', expired.licenseKey).run();
      await expectStatus(403, 'download', { version: '2.0.0', license: expired.licenseFile });
      assert.equal(github.requests.length, requestsBeforeDenied);

      await db.prepare('INSERT INTO products (code, name) VALUES (?, ?)').bind('OtherProduct', 'Sản phẩm phụ').run();
      const wrongProduct = await issue('OtherProduct');
      await expectStatus(403, 'check', { license: wrongProduct.licenseFile });
      const offline = await issue('TanTienQR', 'License chưa ghi nhận');
      await db.prepare('DELETE FROM licenses WHERE license_key = ?').bind(offline.licenseKey).run();
      await expectStatus(403, 'check', { license: offline.licenseFile });

      const replacement = await issue('TanTienQR', 'License thay thế');
      const revoke = await adminPost('/api/licenses/revoke', { licenseKey });
      assert.equal(revoke.status, 200);
      await revoke.body.cancel();
      await expectStatus(403, 'check');
      await expectStatus(200, 'download', { version: '2.0.0', license: replacement.licenseFile });
      defaultBody.license = replacement.licenseFile;
    });

    await suite.test('Thiếu asset, asset quá lớn hoặc URL không thuộc repo trả 503 fail closed', async () => {
      const newest = publish('3.0.0');
      github.setReleases([newest.release, currentStable.release]);
      const packageAsset = newest.release.assets.find((asset) => asset.name === 'package-win-x64.zip');

      const duplicate = publish('3.1.0');
      duplicate.release.assets.push(duplicate.release.assets[0]);
      github.setReleases([duplicate.release, newest.release]);
      await expectStatus(503, 'check');

      github.setReleases([newest.release, currentStable.release]);
      github.deleteAsset(packageAsset.browser_download_url);
      await expectStatus(503, 'download', { version: '3.0.0' });

      github.setAsset(packageAsset.browser_download_url, packageBytes['win-x64']);
      packageAsset.size = 256 * 1024 * 1024 + 1;
      await expectStatus(503, 'download', { version: '3.0.0' });
      packageAsset.size = packageBytes['win-x64'].length;

      github.setAssetResponse(packageAsset.browser_download_url, () =>
        new Response(Uint8Array.of(1, 2, 3, 4, 5, 6, 7)));
      const oversizedStream = await updatePost('download', { version: '3.0.0' });
      assert.equal(oversizedStream.status, 200);
      await assert.rejects(() => oversizedStream.arrayBuffer());
      github.setAsset(packageAsset.browser_download_url, packageBytes['win-x64']);

      const manifestAsset = newest.release.assets.find((asset) => asset.name === 'manifest-win-x64.json');
      manifestAsset.browser_download_url = 'https://evil.example/malware.zip';
      await expectStatus(503, 'check');
    });

    await suite.test('API lỗi, JSON quá lớn và asset status lỗi không lộ chi tiết upstream', async () => {
      const newest = publish('4.0.0');
      github.setReleases([newest.release]);
      github.setApiResponse(503, 'secret upstream details');
      await expectStatus(503, 'check');
      github.setApiResponse(200, 'x'.repeat(2 * 1024 * 1024 + 1));
      await expectStatus(503, 'check');
      github.resetApiResponse();

      const url = newest.release.assets.find((asset) => asset.name === 'package-win-x64.zip').browser_download_url;
      github.setAsset(url, 'private github error', { status: 500 });
      const response = await updatePost('download', { version: '4.0.0' });
      assert.equal(response.status, 503);
      assert.doesNotMatch(await response.text(), /private github error|github\.com/i);
    });

    await suite.test('Từ chối tải redirect tới origin ngoài GitHub', async () => {
      const newest = publish('5.0.0');
      github.setReleases([newest.release]);
      const url = newest.release.assets.find((asset) => asset.name === 'package-win-x64.zip').browser_download_url;
      const cdnUrl = 'https://release-assets.githubusercontent.com/visionf/signed-package.zip';
      github.setAssetResponse(url, () => Response.redirect(cdnUrl, 302));
      github.setAsset(cdnUrl, packageBytes['win-x64']);
      const response = await updatePost('download', { version: '5.0.0' });
      assert.equal(response.status, 200);
      assert.deepEqual(new Uint8Array(await response.arrayBuffer()), packageBytes['win-x64']);

      github.clearRequests();
      github.setAssetResponse(url, () => Response.redirect('https://evil.example/asset', 302));
      await expectStatus(503, 'download', { version: '5.0.0' });
      assert.equal(github.requests.some((request) => request.url.startsWith('https://evil.example/')), false);
    });
  } finally {
    await fixture.dispose();
  }
});
