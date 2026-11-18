import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createPortalFixture, testPassword, testUser } from './fixture.mjs';

// Preview dùng khóa, tài khoản và DB mới trong bộ nhớ; chỉ phục vụ localhost.
const fixture = await createPortalFixture({ port: Number(process.argv[2] ?? 0) });
const baseUrl = String(await fixture.runtime.ready);
const login = await fixture.runtime.dispatchFetch(new URL('/api/login', baseUrl), {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ username: testUser, password: testPassword }),
});
if (!login.ok) throw new Error('Không đăng nhập được preview cục bộ.');
const metadata = { baseUrl, username: testUser, password: testPassword,
  cookie: login.headers.get('set-cookie').split(';')[0], pid: process.pid };
const metadataPath = join(tmpdir(), 'visionf-license-preview.json');
await writeFile(metadataPath, JSON.stringify(metadata, null, 2), 'utf8');
console.log(JSON.stringify({ baseUrl, metadataPath }));

let stopping = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  await fixture.dispose();
  process.exit(0);
}
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
