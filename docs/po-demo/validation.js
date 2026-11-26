(function attachValidation(root) {
  function hasValidGtin(value) {
    if (typeof value !== 'string') return false;
    const gtin = value.trim();
    if (!/^(?:\d{8}|\d{12}|\d{13}|\d{14})$/.test(gtin)) return false;

    let sum = 0;
    let weight = 3;
    for (let index = gtin.length - 2; index >= 0; index -= 1) {
      sum += Number(gtin[index]) * weight;
      weight = weight === 3 ? 1 : 3;
    }
    return Number(gtin.at(-1)) === (10 - (sum % 10)) % 10;
  }

  function isReceiverLocation(location) {
    return ['http:', 'https:'].includes(location.protocol)
      && ['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname)
      && location.pathname === '/po-demo/';
  }

  function describeResponse(status, payload) {
    const validCount = value => Number.isSafeInteger(value) && value >= 0;
    const valid = Boolean(payload && typeof payload.success === 'boolean'
      && typeof payload.message === 'string'
      && validCount(payload.acceptedCount) && validCount(payload.plannedCount)
      && Array.isArray(payload.invalidCodes)
      && payload.invalidCodes.every(item => item && typeof item.code === 'string' && typeof item.reason === 'string')
      && Array.isArray(payload.duplicateCodes) && payload.duplicateCodes.every(code => typeof code === 'string'));
    const ok = status >= 200 && status < 300 && valid && payload.success;
    const accepted = valid ? payload.acceptedCount : null;
    const planned = valid ? payload.plannedCount : null;
    const invalidCodes = valid ? payload.invalidCodes : [];
    const duplicateCodes = valid ? payload.duplicateCodes : [];
    let kind = 'error';
    let title = 'Máy chủ từ chối yêu cầu';
    if (status === 401) { kind = 'unauthorized'; title = 'API key chưa đúng hoặc còn thiếu'; }
    else if (status === 409) { kind = 'conflict'; title = 'PO đang có xung đột'; }
    else if (status === 422) { kind = 'invalid'; title = 'PO hoặc mã chưa hợp lệ'; }
    else if (ok) {
      kind = accepted === 0 && duplicateCodes.length > 0 ? 'duplicate'
        : accepted !== planned || invalidCodes.length > 0 || duplicateCodes.length > 0 ? 'partial' : 'success';
      title = { success: 'Đã nhận PO', partial: 'Đã nhận một phần mã', duplicate: 'Không có mã mới được nhận' }[kind];
    } else if (status >= 200 && status < 300) {
      kind = 'unknown'; title = 'Chưa xác định kết quả nhận PO';
    }
    const uncertain = kind === 'unknown' || status === 408 || status >= 500;
    return {
      kind, title, ok, uncertain, accepted, planned, invalidCodes, duplicateCodes,
      showCounts: valid && planned > 0,
      message: valid ? payload.message : 'Phản hồi máy chủ không có dữ liệu kết quả hợp lệ.',
      invalidCount: invalidCodes.filter(item => item.reason === 'invalidStructure').length,
      mismatchCount: invalidCodes.filter(item => item.reason === 'gtinMismatch').length,
      otherInvalidCount: invalidCodes.filter(item => !['invalidStructure', 'gtinMismatch'].includes(item.reason)).length,
      differs: valid && accepted !== planned
    };
  }

  async function uploadPo(metadata, apiKey, dependencies = {}) {
    const fetchRequest = dependencies.fetch || root.fetch.bind(root);
    const Form = dependencies.FormData || root.FormData;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 60000);
    const headers = {};
    try {
      const body = new Form();
      body.append('orderNo', metadata.orderNo);
      body.append('orderQty', String(metadata.orderQty));
      body.append('gtin', metadata.gtin);
      body.append('file', metadata.file, metadata.file.name);
      if (apiKey) headers['x-api-key'] = apiKey;
      const response = await fetchRequest('/api/po', {
        method: 'POST', body, headers, signal: controller.signal,
        mode: 'same-origin', credentials: 'omit', redirect: 'error', cache: 'no-store'
      });
      let payload = null;
      try {
        payload = await response.json();
        // Không đưa key bị server phản chiếu vào bất kỳ phần hiển thị nào.
        if (apiKey) payload = JSON.parse(JSON.stringify(payload, (_key, value) =>
          typeof value === 'string' ? value.split(apiKey).join('[đã ẩn]') : value));
      } catch { payload = null; }
      return describeResponse(response.status, payload);
    } catch {
      return { kind: 'transport', title: 'Chưa xác định kết quả nhận PO', ok: false, uncertain: true,
        showCounts: false, invalidCodes: [], duplicateCodes: [],
        message: 'Không nhận được phản hồi hoàn chỉnh. Kết nối có thể bị gián đoạn hoặc hết thời gian chờ.' };
    } finally {
      clearTimeout(timeout);
      delete headers['x-api-key'];
      apiKey = '';
    }
  }

  const api = Object.freeze({ hasValidGtin, isReceiverLocation, describeResponse, uploadPo });
  root.PoDemoValidation = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(globalThis);
