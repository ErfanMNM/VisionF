import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { isReceiverLocation, describeResponse, uploadPo } = require('./validation.js');

const responsePayload = overrides => ({
  success: true,
  message: 'Đã nhận PO.',
  orderNo: 'PO-DEMO',
  acceptedCount: 3,
  plannedCount: 3,
  invalidCodes: [],
  duplicateCodes: [],
  ...overrides
});

const rawCode = 'https://id.example/01/04006381333931/21/000012?x=%2F#raw';
const rawInvalid = '  <script>mã gốc & 00001</script>  ';
const file = Object.freeze({
  name: 'CodeContent.csv',
  text() { throw new Error('UI không được đọc File.text().'); },
  arrayBuffer() { throw new Error('UI không được đọc File.arrayBuffer().'); },
  stream() { throw new Error('UI không được đọc File.stream().'); }
});
const metadata = Object.freeze({ orderNo: 'PO-DEMO', orderQty: 3, gtin: '4006381333931', file });

class FakeFormData {
  entries = [];
  append(...args) { this.entries.push(args); }
}

test('chỉ bật gửi tại route embedded chính xác trên origin loopback HTTP(S)', () => {
  for (const hostname of ['127.0.0.1', 'localhost', '[::1]']) {
    for (const protocol of ['http:', 'https:']) {
      assert.equal(isReceiverLocation({ hostname, protocol, pathname: '/po-demo/' }), true);
    }
  }
  for (const location of [
    { protocol: 'file:', hostname: '', pathname: '/po-demo/' },
    { protocol: 'http:', hostname: '192.168.1.1', pathname: '/po-demo/' },
    { protocol: 'https:', hostname: 'example.com', pathname: '/po-demo/' },
    { protocol: 'http:', hostname: 'localhost.evil.test', pathname: '/po-demo/' },
    { protocol: 'http:', hostname: '127.0.0.1', pathname: '/' },
    { protocol: 'http:', hostname: '127.0.0.1', pathname: '/po-demo' },
    { protocol: 'http:', hostname: '127.0.0.1', pathname: '/po-demo/index.html' },
    { protocol: 'http:', hostname: '127.0.0.1', pathname: '/PO-DEMO/' }
  ]) assert.equal(isReceiverLocation(location), false, JSON.stringify(location));
});

test('200 đầy đủ giữ acceptedCount và plannedCount chính xác', () => {
  const result = describeResponse(200, responsePayload());
  assert.equal(result.kind, 'success');
  assert.equal(result.ok, true);
  assert.equal(result.uncertain, false);
  assert.equal(result.showCounts, true);
  assert.equal(result.accepted, 3);
  assert.equal(result.planned, 3);
  assert.equal(result.differs, false);
});

test('200 một phần tách lý do, giữ nguyên mã và cả reason chưa biết', () => {
  const payload = responsePayload({
    acceptedCount: 1,
    plannedCount: 6,
    invalidCodes: [
      { code: rawInvalid, reason: 'invalidStructure' },
      { code: '/01/00000000000000/21/ABC', reason: 'gtinMismatch' },
      { code: 'mã chưa biết 0001', reason: 'futureReason' }
    ],
    duplicateCodes: [rawCode, rawCode]
  });
  const before = JSON.stringify(payload);
  const result = describeResponse(200, payload);
  assert.equal(result.kind, 'partial');
  assert.equal(result.ok, true);
  assert.equal(result.accepted, 1);
  assert.equal(result.planned, 6);
  assert.equal(result.invalidCount, 1);
  assert.equal(result.mismatchCount, 1);
  assert.equal(result.otherInvalidCount, 1);
  assert.equal(result.differs, true);
  assert.deepEqual(result.invalidCodes, payload.invalidCodes);
  assert.deepEqual(result.duplicateCodes, [rawCode, rawCode]);
  assert.equal(JSON.stringify(payload), before);
});

test('200 toàn mã trùng vẫn thành công nhưng không có mã mới', () => {
  const result = describeResponse(200, responsePayload({
    acceptedCount: 0, duplicateCodes: [rawCode, rawCode, rawCode]
  }));
  assert.equal(result.kind, 'duplicate');
  assert.equal(result.ok, true);
  assert.equal(result.uncertain, false);
  assert.equal(result.accepted, 0);
  assert.equal(result.planned, 3);
  assert.equal(result.differs, true);
  assert.equal(result.duplicateCodes.length, 3);
});

test('401 và 409 không cần orderNo hay plannedCount có giá trị', () => {
  for (const [status, kind] of [[401, 'unauthorized'], [409, 'conflict']]) {
    const payload = responsePayload({ success: false, orderNo: '', acceptedCount: 0, plannedCount: 0 });
    const result = describeResponse(status, payload);
    assert.equal(result.kind, kind);
    assert.equal(result.ok, false);
    assert.equal(result.showCounts, false);
    assert.equal(result.message, payload.message);
  }
});

test('422 GTIN metadata sai có thể không chứa mã bị loại', () => {
  const payload = responsePayload({ success: false, acceptedCount: 0, message: 'GTIN chưa hợp lệ.' });
  const result = describeResponse(422, payload);
  assert.equal(result.kind, 'invalid');
  assert.equal(result.ok, false);
  assert.equal(result.uncertain, false);
  assert.deepEqual(result.invalidCodes, []);
  assert.equal(result.message, payload.message);
});

test('422 toàn mã sai vẫn giữ danh sách mã sai và trùng để xử lý', () => {
  const invalidCodes = [{ code: rawInvalid, reason: 'invalidStructure' }];
  const result = describeResponse(422, responsePayload({
    success: false, acceptedCount: 0, plannedCount: 2, invalidCodes, duplicateCodes: [rawCode]
  }));
  assert.equal(result.kind, 'invalid');
  assert.equal(result.showCounts, true);
  assert.deepEqual(result.invalidCodes, invalidCodes);
  assert.deepEqual(result.duplicateCodes, [rawCode]);
});

test('400, 413 và 415 hiển thị thông điệp lỗi thay vì thành công', () => {
  for (const status of [400, 413, 415]) {
    const payload = responsePayload({ success: false, acceptedCount: 0, message: `Lỗi HTTP ${status}` });
    const result = describeResponse(status, payload);
    assert.equal(result.kind, 'error');
    assert.equal(result.ok, false);
    assert.equal(result.message, payload.message);
  }
});

test('408 nhắc kiểm tra PO trước gửi lại vì kết quả lưu chưa xác định', () => {
  const result = describeResponse(408, responsePayload({ success: false, acceptedCount: 0 }));
  assert.equal(result.ok, false);
  assert.equal(result.uncertain, true);
});

test('5xx luôn đánh dấu kết quả chưa xác định', () => {
  for (const status of [500, 503]) {
    const result = describeResponse(status, responsePayload({ success: false, acceptedCount: 0 }));
    assert.equal(result.ok, false);
    assert.equal(result.uncertain, true);
  }
});

test('200 success false hoặc payload malformed không được báo thành công', () => {
  const malformed = [
    null, {}, 'HTML lỗi', [],
    responsePayload({ acceptedCount: -1 }),
    responsePayload({ acceptedCount: 1.5 }),
    responsePayload({ plannedCount: '3' }),
    responsePayload({ invalidCodes: [{ code: rawCode }] }),
    responsePayload({ duplicateCodes: [{ code: rawCode }] }),
    responsePayload({ success: 'true' })
  ];
  for (const payload of [responsePayload({ success: false }), ...malformed]) {
    const result = describeResponse(200, payload);
    assert.equal(result.kind, 'unknown');
    assert.equal(result.ok, false);
    assert.equal(result.uncertain, true);
    assert.equal(typeof result.showCounts, 'boolean');
    assert.equal(typeof result.differs, 'boolean');
  }
});

test('multipart gửi metadata rõ ràng, File giữ nguyên identity và key chỉ ở header', async () => {
  const apiKey = 'secret-for-test-only';
  let calls = 0;
  let retainedHeaders;
  const result = await uploadPo(metadata, apiKey, {
    FormData: FakeFormData,
    fetch: async (url, options) => {
      calls += 1;
      assert.equal(url, '/api/po');
      assert.equal(options.method, 'POST');
      assert.deepEqual(options.body.entries, [
        ['orderNo', metadata.orderNo], ['orderQty', '3'], ['gtin', metadata.gtin],
        ['file', file, file.name]
      ]);
      assert.equal(options.body.entries[3][1], file);
      assert.deepEqual(options.headers, { 'x-api-key': apiKey });
      retainedHeaders = options.headers;
      assert.equal(options.mode, 'same-origin');
      assert.equal(options.credentials, 'omit');
      assert.equal(options.redirect, 'error');
      assert.equal(options.cache, 'no-store');
      assert.ok(options.signal instanceof AbortSignal);
      assert.equal(options.signal.aborted, false);
      return { status: 200, json: async () => responsePayload() };
    }
  });
  assert.equal(calls, 1);
  assert.equal(result.kind, 'success');
  assert.deepEqual(retainedHeaders, {});
  assert.equal(JSON.stringify(result).includes(apiKey), false);
});

test('key trống không tạo header x-api-key', async () => {
  await uploadPo(metadata, '', {
    FormData: FakeFormData,
    fetch: async (_url, options) => {
      assert.deepEqual(options.headers, {});
      return { status: 200, json: async () => responsePayload() };
    }
  });
});

test('key bị server phản chiếu được ẩn trong message, code và reason', async () => {
  for (const apiKey of ['unique-key-for-test', 'key-"slash\\-for-test']) {
    const payload = responsePayload({
      message: `Lỗi ${apiKey} và ${apiKey}`,
      invalidCodes: [{ code: `${rawInvalid}${apiKey}`, reason: `future-${apiKey}` }],
      duplicateCodes: [`${rawCode}${apiKey}`]
    });
    const result = await uploadPo(metadata, apiKey, {
      FormData: FakeFormData,
      fetch: async () => ({ status: 200, json: async () => payload })
    });
    assert.equal(result.message.includes(apiKey), false);
    assert.equal(result.invalidCodes[0].code.includes(apiKey), false);
    assert.equal(result.invalidCodes[0].reason.includes(apiKey), false);
    assert.equal(result.duplicateCodes[0].includes(apiKey), false);
    assert.match(result.message, /\[đã ẩn\]/);
    assert.match(result.invalidCodes[0].code, /\[đã ẩn\]/);
    assert.match(result.invalidCodes[0].reason, /\[đã ẩn\]/);
    assert.match(result.duplicateCodes[0], /\[đã ẩn\]/);
  }
});

test('response không đọc được không tự retry và xóa header key tạm', async () => {
  let calls = 0;
  let retainedHeaders;
  const result = await uploadPo(metadata, 'temporary-key', {
    FormData: FakeFormData,
    fetch: async (_url, options) => {
      calls += 1;
      retainedHeaders = options.headers;
      return { status: 200, json: async () => { throw new SyntaxError('body chưa hoàn chỉnh'); } };
    }
  });
  assert.equal(calls, 1);
  assert.equal(result.kind, 'unknown');
  assert.equal(result.uncertain, true);
  assert.deepEqual(retainedHeaders, {});
});

test('transport và AbortError giữ kết quả chưa xác định, không phản chiếu lỗi/key hay retry', async () => {
  for (const name of ['TypeError', 'AbortError']) {
    let calls = 0;
    let retainedHeaders;
    const secret = `temporary-${name}-key`;
    const result = await uploadPo(metadata, secret, {
      FormData: FakeFormData,
      fetch: async (_url, options) => {
        calls += 1;
        retainedHeaders = options.headers;
        const error = new Error(`Không đưa vào UI: ${secret}`);
        error.name = name;
        throw error;
      }
    });
    assert.equal(calls, 1);
    assert.equal(result.kind, 'transport');
    assert.equal(result.ok, false);
    assert.equal(result.uncertain, true);
    assert.equal(result.showCounts, false);
    assert.deepEqual(retainedHeaders, {});
    assert.equal(JSON.stringify(result).includes(secret), false);
  }
});
