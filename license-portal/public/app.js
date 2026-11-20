/* TanTienQR — Giao diện cấp license */

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

let currentUser = null;
let lastIssued = null; // { licenseKey, licenseText }
let selectedRequest = null;
let issueBusy = false;
let requestSelectionBusy = false;
let requestRows = new Map();
let requestsLoadSequence = 0;
let licensesLoadSequence = 0;

// ------------------------------------------------------------------ Tiện ích

function show(el) { el.classList.remove('hidden'); }
function hide(el) { el.classList.add('hidden'); }

function setText(sel, text) {
  const el = typeof sel === 'string' ? $(sel) : sel;
  if (el) el.textContent = text;
}

function toast(message, kind = '') {
  const el = $('#toast');
  el.textContent = message;
  el.className = 'toast' + (kind ? ' ' + kind : '');
  el.classList.remove('hidden');
  clearTimeout(el._timer);
  el._timer = setTimeout(() => hide(el), 3200);
}

async function api(path, options = {}) {
  const res = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
    body: options.body ? JSON.stringify(options.body) : undefined,
  });

  let data = null;
  try { data = await res.json(); } catch { data = null; }

  if (!res.ok) {
    const message = (data && data.error) || `Lỗi ${res.status}`;
    throw new Error(message);
  }
  return data;
}

function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatDate(iso) {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '-';
  return d.toLocaleString('vi-VN', {
    day: '2-digit', month: '2-digit', year: 'numeric',
    hour: '2-digit', minute: '2-digit',
  });
}

// ------------------------------------------------------------------ Tabs

function selectTab(name) {
  $$('.tab').forEach((tab) => tab.classList.toggle('active', tab.dataset.tab === name));
  $$('.tab-panel').forEach((panel) => panel.classList.toggle('active', panel.id === 'tab-' + name));
  if (name === 'list') loadLicenses();
  if (name === 'requests') loadRequests();
  if (name === 'audit') loadAudit();
  if (name === 'keys') loadPublicKey();
}

$$('.tab').forEach((tab) => tab.addEventListener('click', () => selectTab(tab.dataset.tab)));

// ------------------------------------------------------------------ Đăng nhập

$('#login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const errorBox = $('#login-error');
  hide(errorBox);

  const btn = e.target.querySelector('button[type="submit"]');
  btn.disabled = true;
  try {
    const data = await api('/api/login', {
      method: 'POST',
      body: {
        username: $('#login-username').value.trim(),
        password: $('#login-password').value,
      },
    });
    currentUser = data.username;
    enterMain();
  } catch (err) {
    errorBox.textContent = err.message;
    show(errorBox);
  } finally {
    btn.disabled = false;
  }
});

$('#logout-btn').addEventListener('click', async () => {
  try { await api('/api/logout', { method: 'POST' }); } catch { /* bỏ qua */ }
  currentUser = null;
  requestsLoadSequence++;
  licensesLoadSequence++;
  requestRows.clear();
  clearRequestSelection();
  hide($('#main-screen'));
  show($('#login-screen'));
  $('#login-password').value = '';
});

function enterMain() {
  hide($('#login-screen'));
  show($('#main-screen'));
  setText('#current-user', currentUser || '');
  loadProducts();
  loadLicenses();
  loadRequests();
}

// ------------------------------------------------------------------ Sản phẩm

async function loadProducts() {
  try {
    const data = await api('/api/products');
    const select = $('#product');
    const filter = $('#filter-product');
    const current = selectedRequest?.product || select.value;
    const filterCurrent = filter.value;

    select.innerHTML = '';
    filter.innerHTML = '<option value="">Tất cả sản phẩm</option>';

    for (const p of data.products) {
      const opt = document.createElement('option');
      opt.value = p.code;
      opt.textContent = p.name && p.name !== p.code ? `${p.code} — ${p.name}` : p.code;
      select.appendChild(opt);

      const opt2 = document.createElement('option');
      opt2.value = p.code;
      opt2.textContent = p.code;
      filter.appendChild(opt2);
    }

    if (current) select.value = current;
    if (filterCurrent) filter.value = filterCurrent;
    return true;
  } catch (err) {
    toast(err.message, 'error');
    return false;
  }
}

// ------------------------------------------------------------------ Cấp license

$('#paste-hwid').addEventListener('click', async () => {
  if (issueBusy || selectedRequest) return;
  const input = $('#hwid');
  try {
    const text = await navigator.clipboard.readText();
    if (issueBusy || selectedRequest) return;
    input.value = text.trim();
    toast('Đã dán mã máy.');
  } catch {
    toast('Không đọc được clipboard. Hãy dán thủ công.', 'error');
    input.focus();
  }
});

$('#issue-btn').addEventListener('click', async () => {
  if (issueBusy || requestSelectionBusy) return;
  const errorBox = $('#issue-error');
  hide(errorBox);

  const btn = $('#issue-btn');
  const submittedRequest = selectedRequest;
  const submittedBody = {
    customer: $('#customer').value.trim(),
    hwid: $('#hwid').value.trim(),
    daysValid: Number($('#days-valid').value),
    product: $('#product').value,
    features: $('#feature-printer').checked ? ['printer'] : [],
    note: $('#note').value.trim(),
    ...(submittedRequest ? { requestId: submittedRequest.id } : {}),
  };
  issueBusy = true;
  updateIssueControls();
  btn.textContent = 'Đang cấp...';

  try {
    const data = await api('/api/licenses', {
      method: 'POST',
      body: submittedBody,
    });

    lastIssued = { licenseKey: data.licenseKey, licenseText: data.licenseText };

    setText('#result-key', data.licenseKey);
    setText('#result-expiry', 'Hết hạn: ' + formatDate(data.expiresAt));
    $('#result-text').value = data.licenseText;

    const details = $('#result-detail');
    details.innerHTML = `
      <dt>Khách hàng</dt><dd>${escapeHtml(data.payload.customer)}</dd>
      <dt>HWID</dt><dd>${escapeHtml(data.hwidDisplay)}</dd>
      <dt>Sản phẩm</dt><dd>${escapeHtml(data.payload.product)}</dd>
      <dt>Cấp lúc</dt><dd>${escapeHtml(formatDate(data.payload.issued))}</dd>
      <dt>Hết hạn</dt><dd>${escapeHtml(formatDate(data.payload.expires))}</dd>
      <dt>Key ID</dt><dd>${escapeHtml(data.payload.kid)}</dd>
      <dt>Module</dt><dd>${escapeHtml((data.payload.features || []).join(', ') || 'Cơ bản')}</dd>
    `;

    hide($('#issue-empty'));
    show($('#issue-result'));

    clearRequestSelection();

    toast('Cấp license thành công: ' + data.licenseKey, 'success');
    loadLicenses();
    loadRequests();
  } catch (err) {
    errorBox.textContent = err.message;
    show(errorBox);
  } finally {
    issueBusy = false;
    updateIssueControls();
    btn.textContent = 'Cấp license';
  }
});

$('#copy-license').addEventListener('click', async () => {
  const text = $('#result-text').value;
  try {
    await navigator.clipboard.writeText(text);
    toast('Đã copy nội dung license.', 'success');
  } catch {
    const ta = $('#result-text');
    ta.select();
    toast('Nhấn Ctrl+C để copy.', 'error');
  }
});

$('#download-license').addEventListener('click', () => {
  if (!lastIssued) return;
  const blob = new Blob([lastIssued.licenseText], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'license-' + lastIssued.licenseKey + '.lic';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
});

// ------------------------------------------------------------------ Danh sách

async function loadLicenses() {
  const sequence = ++licensesLoadSequence;
  const body = $('#licenses-body');
  body.innerHTML = '<tr><td colspan="8" class="empty-cell">Đang tải...</td></tr>';

  const params = new URLSearchParams({
    q: $('#search').value.trim(),
    product: $('#filter-product').value,
    status: $('#filter-status').value,
    limit: '200',
  });

  try {
    const data = await api('/api/licenses?' + params.toString());
    if (sequence !== licensesLoadSequence || !currentUser) return;
    renderLicenses(data.licenses);
    setText('#licenses-count', 'Tổng số: ' + data.total + ' license');
  } catch (err) {
    if (sequence !== licensesLoadSequence || !currentUser) return;
    body.innerHTML = `<tr><td colspan="8" class="empty-cell">${escapeHtml(err.message)}</td></tr>`;
  }
}

function renderLicenses(licenses) {
  const body = $('#licenses-body');

  if (!licenses || licenses.length === 0) {
    body.innerHTML = '<tr><td colspan="8" class="empty-cell">Không có license nào.</td></tr>';
    return;
  }

  const now = Date.now();
  body.innerHTML = licenses.map((l) => {
    let status = 'active';
    let badgeClass = 'badge-active';
    let statusText = 'Còn hiệu lực';

    if (l.revoked_at) {
      status = 'revoked';
      badgeClass = 'badge-revoked';
      statusText = 'Đã thu hồi';
    } else if (new Date(l.expires_at).getTime() <= now) {
      status = 'expired';
      badgeClass = 'badge-expired';
      statusText = 'Hết hạn';
    }

    const title = l.revoke_reason
      ? ` title="${escapeHtml(l.revoke_reason)}"`
      : '';

    return `
      <tr>
        <td class="mono">${escapeHtml(l.license_key)}</td>
        <td>${escapeHtml(l.customer)}</td>
        <td class="mono">${escapeHtml(l.hwid_display)}</td>
        <td>${escapeHtml(l.product)}</td>
        <td>${escapeHtml(JSON.parse(l.features_json || '[]').join(', ') || 'Cơ bản')}</td>
        <td>${escapeHtml(formatDate(l.expires_at))}</td>
        <td><span class="badge ${badgeClass}"${title}>${statusText}</span></td>
        <td>
          <div class="row-actions">
            <button class="icon-btn" data-act="download" data-key="${escapeHtml(l.license_key)}">Tải</button>
            <button class="icon-btn" data-act="renew" data-key="${escapeHtml(l.license_key)}">Gia hạn</button>
            ${status !== 'revoked'
              ? `<button class="icon-btn danger" data-act="revoke" data-key="${escapeHtml(l.license_key)}">Thu hồi</button>`
              : `<button class="icon-btn danger" data-act="delete" data-key="${escapeHtml(l.license_key)}">Xoá</button>`}
          </div>
        </td>
      </tr>
    `;
  }).join('');
}

$('#licenses-body').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-act]');
  if (!btn) return;
  const act = btn.dataset.act;
  const key = btn.dataset.key;

  if (act === 'download') {
    window.location.href = '/api/licenses/download?key=' + encodeURIComponent(key);
  } else if (act === 'renew') {
    openModal({
      title: 'Gia hạn license',
      text: 'Cấp license mới cho máy ' + key + '. License cũ sẽ bị thu hồi.',
      mode: 'renew',
      key,
    });
  } else if (act === 'revoke') {
    openModal({
      title: 'Thu hồi license',
      text: 'Thao tác này không thể hoàn tác. License ' + key + ' sẽ không còn hiệu lực.',
      mode: 'revoke',
      key,
    });
  } else if (act === 'delete') {
    openModal({
      title: 'Xoá license đã thu hồi',
      text: 'Xoá vĩnh viễn bản ghi license ' + key + ' khỏi danh sách. Nhật ký thu hồi và thao tác xoá vẫn được giữ lại.',
      mode: 'delete',
      key,
    });
  }
});

let searchTimer = null;
$('#search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(loadLicenses, 280);
});
$('#filter-product').addEventListener('change', loadLicenses);
$('#filter-status').addEventListener('change', loadLicenses);
$('#reload-btn').addEventListener('click', () => { loadProducts(); loadLicenses(); });

// ------------------------------------------------------------------ Yêu cầu cấp license

function updateIssueControls() {
  const busy = issueBusy || requestSelectionBusy;
  for (const selector of ['#customer', '#days-valid', '#note', '#feature-printer', '#clear-request', '#issue-btn']) {
    $(selector).disabled = busy;
  }
  $('#hwid').disabled = busy;
  $('#hwid').readOnly = selectedRequest !== null;
  $('#product').disabled = busy || selectedRequest !== null;
  $('#paste-hwid').disabled = busy || selectedRequest !== null;
  $('#logout-btn').disabled = busy || modalBusy;
  $$('#requests-body button').forEach((button) => { button.disabled = busy; });
}

function clearRequestSelection() {
  selectedRequest = null;
  hide($('#request-selection'));
  setText('#request-selection-detail', '');
  $('#customer').value = '';
  $('#hwid').value = '';
  $('#note').value = '';
  $('#feature-printer').checked = false;
  updateIssueControls();
}

$('#clear-request').addEventListener('click', () => {
  if (issueBusy || requestSelectionBusy) return;
  clearRequestSelection();
  $('#customer').focus();
});

async function loadRequests() {
  const sequence = ++requestsLoadSequence;
  const body = $('#requests-body');
  body.innerHTML = '<tr><td colspan="6" class="empty-cell">Đang tải...</td></tr>';
  const params = new URLSearchParams({ status: $('#requests-status').value, limit: '200' });
  try {
    const data = await api('/api/license-requests?' + params.toString());
    if (sequence !== requestsLoadSequence || !currentUser) return;
    requestRows = new Map(data.requests.map((request) => [request.id, request]));
    renderRequests(data.requests);
    setText('#requests-count', `Hiển thị ${data.requests.length} / ${data.total} yêu cầu`);
  } catch (err) {
    if (sequence !== requestsLoadSequence || !currentUser) return;
    requestRows.clear();
    body.innerHTML = `<tr><td colspan="6" class="empty-cell">${escapeHtml(err.message)}</td></tr>`;
    setText('#requests-count', '');
  }
}

function renderRequests(requests) {
  const body = $('#requests-body');
  if (!requests.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-cell">Chưa có yêu cầu trong trạng thái này.</td></tr>';
    return;
  }
  body.innerHTML = requests.map((request) => `
    <tr>
      <td>
        ${escapeHtml(formatDate(request.created_at))}
        <div class="request-cell-detail mono">${escapeHtml(request.id)}</div>
      </td>
      <td>
        ${escapeHtml(request.customer || 'Chưa ghi tên')}
        <div class="request-cell-detail">${escapeHtml(request.contact || 'Chưa có thông tin liên hệ')}</div>
      </td>
      <td class="mono">
        ${escapeHtml(request.hwid_display || request.hwid)}
        <div class="request-cell-detail">${escapeHtml(request.product)}</div>
      </td>
      <td class="request-note">${escapeHtml(request.note || '—')}</td>
      <td>
        <span class="badge ${request.status === 'pending' ? 'badge-pending' : 'badge-active'}">${request.status === 'pending' ? 'Đang chờ cấp' : 'Đã cấp'}</span>
        ${request.license_key ? `<div class="request-cell-detail mono">${escapeHtml(request.license_key)}</div>` : ''}
      </td>
      <td>${request.status === 'pending'
        ? `<button class="icon-btn" data-request="${escapeHtml(request.id)}">Cấp license</button>`
        : '<span class="muted">Đã xử lý</span>'}</td>
    </tr>
  `).join('');
  updateIssueControls();
}

$('#requests-body').addEventListener('click', async (e) => {
  const button = e.target.closest('button[data-request]');
  if (!button || issueBusy || requestSelectionBusy) return;
  const request = requestRows.get(button.dataset.request);
  if (!request || request.status !== 'pending') return;

  requestSelectionBusy = true;
  updateIssueControls();
  try {
    if (!await loadProducts()) return;
    if (![...$('#product').options].some((option) => option.value === request.product)) {
      throw new Error('Sản phẩm của yêu cầu không còn được phép cấp license.');
    }
    selectedRequest = request;
    $('#customer').value = request.customer || '';
    $('#hwid').value = request.hwid_display || request.hwid;
    $('#product').value = request.product;
    $('#note').value = request.note || '';
    setText('#request-selection-detail', `Mã yêu cầu: ${request.id}${request.contact ? ' · Liên hệ: ' + request.contact : ''}`);
    show($('#request-selection'));
    hide($('#issue-error'));
    selectTab('issue');
  } catch (err) {
    toast(err.message, 'error');
  } finally {
    requestSelectionBusy = false;
    updateIssueControls();
    if (selectedRequest?.id === request.id) $('#customer').focus();
  }
});

$('#requests-status').addEventListener('change', loadRequests);
$('#requests-reload').addEventListener('click', loadRequests);

// ------------------------------------------------------------------ Hộp thoại

let modalState = null;
let modalBusy = false;
let modalReturnFocus = null;

function openModal({ title, text, mode, key }) {
  if (modalBusy) return;
  modalReturnFocus = document.activeElement;
  modalState = { mode, key };
  const state = modalState;
  setText('#modal-title', title);
  setText('#modal-text', text);

  const reasonLabel = $('#modal-reason-label');
  const reasonInput = $('#modal-reason');
  const daysLabel = $('#modal-days-label');
  const daysInput = $('#modal-days');

  if (mode === 'revoke') {
    show(reasonLabel); show(reasonInput);
    hide(daysLabel); hide(daysInput);
    reasonInput.value = '';
    setText('#modal-confirm', 'Thu hồi');
    $('#modal-confirm').className = 'btn btn-danger';
  } else if (mode === 'delete') {
    hide(reasonLabel); hide(reasonInput);
    hide(daysLabel); hide(daysInput);
    setText('#modal-confirm', 'Xoá vĩnh viễn');
    $('#modal-confirm').className = 'btn btn-danger';
  } else {
    hide(reasonLabel); hide(reasonInput);
    show(daysLabel); show(daysInput);
    daysInput.value = '30';
    setText('#modal-confirm', 'Gia hạn');
    $('#modal-confirm').className = 'btn btn-primary';
  }

  show($('#modal'));
  setTimeout(() => {
    if (modalState !== state || modalBusy) return;
    (mode === 'revoke' ? reasonInput : mode === 'delete' ? $('#modal-cancel') : daysInput).focus();
  }, 30);
}

function closeModal(force = false) {
  if (modalBusy && force !== true) return;
  hide($('#modal'));
  modalState = null;
  modalReturnFocus?.focus();
  modalReturnFocus = null;
}

$('#modal-cancel').addEventListener('click', () => closeModal());
$('.modal-backdrop').addEventListener('click', () => closeModal());

$('#modal-confirm').addEventListener('click', async () => {
  if (!modalState || modalBusy) return;
  const state = modalState;
  modalBusy = true;
  const btn = $('#modal-confirm');
  btn.disabled = true;
  $('#modal-cancel').disabled = true;
  $('#modal-reason').disabled = true;
  $('#modal-days').disabled = true;
  $('#logout-btn').disabled = true;

  try {
    if (state.mode === 'revoke') {
      await api('/api/licenses/revoke', {
        method: 'POST',
        body: { licenseKey: state.key, reason: $('#modal-reason').value.trim() },
      });
      toast('Đã thu hồi license.', 'success');
    } else if (state.mode === 'delete') {
      await api('/api/licenses', { method: 'DELETE', body: { licenseKey: state.key } });
      if (lastIssued?.licenseKey === state.key) {
        lastIssued = null;
        $('#result-text').value = '';
        hide($('#issue-result'));
        show($('#issue-empty'));
      }
      toast('Đã xoá license đã thu hồi. Nhật ký được giữ lại.', 'success');
    } else {
      const data = await api('/api/licenses/renew', {
        method: 'POST',
        body: { licenseKey: state.key, daysValid: Number($('#modal-days').value) },
      });
      lastIssued = { licenseKey: data.licenseKey, licenseText: data.licenseText };
      setText('#result-key', data.licenseKey);
      setText('#result-expiry', 'Hết hạn: ' + formatDate(data.expiresAt));
      $('#result-text').value = data.licenseText;
      hide($('#issue-empty'));
      show($('#issue-result'));
      toast('Đã gia hạn: ' + data.licenseKey, 'success');
    }
    closeModal(true);
    loadLicenses();
  } catch (err) {
    toast(err.message, 'error');
  } finally {
    modalBusy = false;
    btn.disabled = false;
    $('#modal-cancel').disabled = false;
    $('#modal-reason').disabled = false;
    $('#modal-days').disabled = false;
    updateIssueControls();
  }
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && modalState) closeModal();
  if (e.key === 'Tab' && modalState) {
    const focusable = [...$('.modal-card').querySelectorAll('button:not(:disabled), input:not(:disabled):not(.hidden)')];
    if (!focusable.length) { e.preventDefault(); return; }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
});

// ------------------------------------------------------------------ Khóa ký / Nhật ký

async function loadPublicKey() {
  const box = $('#key-info');
  box.textContent = 'Đang tải...';
  try {
    const data = await api('/api/public-key');
    box.innerHTML = `
      <div><strong>Key ID:</strong> ${escapeHtml(data.key_id)}</div>
      <div style="margin-top:10px"><strong>Thuật toán:</strong> ${escapeHtml(data.algorithm)}</div>
      <div style="margin-top:10px"><strong>Public key (base64 SPKI):</strong></div>
      <div style="margin-top:4px">${escapeHtml(data.public_key)}</div>
    `;
  } catch (err) {
    box.textContent = err.message;
  }
}

async function loadAudit() {
  const body = $('#audit-body');
  body.innerHTML = '<tr><td colspan="4" class="empty-cell">Đang tải...</td></tr>';
  try {
    const data = await api('/api/audit');
    if (!data.entries || data.entries.length === 0) {
      body.innerHTML = '<tr><td colspan="4" class="empty-cell">Chưa có hoạt động nào.</td></tr>';
      return;
    }
    body.innerHTML = data.entries.map((a) => `
      <tr>
        <td>${escapeHtml(formatDate(a.created_at))}</td>
        <td>${escapeHtml(a.actor)}</td>
        <td>${escapeHtml(a.action)}</td>
        <td>${escapeHtml(a.detail || '')}</td>
      </tr>
    `).join('');
  } catch (err) {
    body.innerHTML = `<tr><td colspan="4" class="empty-cell">${escapeHtml(err.message)}</td></tr>`;
  }
}

// ------------------------------------------------------------------ Khởi động

(async function boot() {
  try {
    const session = await api('/api/session');
    if (session.authenticated) {
      currentUser = session.username;
      enterMain();
    } else {
      show($('#login-screen'));
    }
  } catch {
    show($('#login-screen'));
  }
})();
