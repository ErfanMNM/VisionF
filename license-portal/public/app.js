/* TanTienQR — Giao diện cấp license */

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

let currentUser = null;
let lastIssued = null; // { licenseKey, licenseText }

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

$$('.tab').forEach((tab) => {
  tab.addEventListener('click', () => {
    $$('.tab').forEach((t) => t.classList.remove('active'));
    $$('.tab-panel').forEach((p) => p.classList.remove('active'));
    tab.classList.add('active');
    const panel = $('#tab-' + tab.dataset.tab);
    if (panel) panel.classList.add('active');

    if (tab.dataset.tab === 'list') loadLicenses();
    if (tab.dataset.tab === 'audit') loadAudit();
    if (tab.dataset.tab === 'keys') loadPublicKey();
  });
});

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
}

// ------------------------------------------------------------------ Sản phẩm

async function loadProducts() {
  try {
    const data = await api('/api/products');
    const select = $('#product');
    const filter = $('#filter-product');
    const current = select.value;
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
  } catch (err) {
    toast(err.message, 'error');
  }
}

// ------------------------------------------------------------------ Cấp license

$('#paste-hwid').addEventListener('click', async () => {
  const input = $('#hwid');
  try {
    const text = await navigator.clipboard.readText();
    input.value = text.trim();
    toast('Đã dán mã máy.');
  } catch {
    toast('Không đọc được clipboard. Hãy dán thủ công.', 'error');
    input.focus();
  }
});

$('#issue-btn').addEventListener('click', async () => {
  const errorBox = $('#issue-error');
  hide(errorBox);

  const btn = $('#issue-btn');
  btn.disabled = true;
  btn.textContent = 'Đang cấp...';

  try {
    const data = await api('/api/licenses', {
      method: 'POST',
      body: {
        customer: $('#customer').value.trim(),
        hwid: $('#hwid').value.trim(),
        daysValid: Number($('#days-valid').value),
        product: $('#product').value,
        note: $('#note').value.trim(),
      },
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
    `;

    hide($('#issue-empty'));
    show($('#issue-result'));

    $('#customer').value = '';
    $('#hwid').value = '';
    $('#note').value = '';

    toast('Cấp license thành công: ' + data.licenseKey, 'success');
    loadLicenses();
  } catch (err) {
    errorBox.textContent = err.message;
    show(errorBox);
  } finally {
    btn.disabled = false;
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
  const body = $('#licenses-body');
  body.innerHTML = '<tr><td colspan="7" class="empty-cell">Đang tải...</td></tr>';

  const params = new URLSearchParams({
    q: $('#search').value.trim(),
    product: $('#filter-product').value,
    status: $('#filter-status').value,
    limit: '200',
  });

  try {
    const data = await api('/api/licenses?' + params.toString());
    renderLicenses(data.licenses);
    setText('#licenses-count', 'Tổng số: ' + data.total + ' license');
  } catch (err) {
    body.innerHTML = `<tr><td colspan="7" class="empty-cell">${escapeHtml(err.message)}</td></tr>`;
  }
}

function renderLicenses(licenses) {
  const body = $('#licenses-body');

  if (!licenses || licenses.length === 0) {
    body.innerHTML = '<tr><td colspan="7" class="empty-cell">Không có license nào.</td></tr>';
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
        <td>${escapeHtml(formatDate(l.expires_at))}</td>
        <td><span class="badge ${badgeClass}"${title}>${statusText}</span></td>
        <td>
          <div class="row-actions">
            <button class="icon-btn" data-act="download" data-key="${escapeHtml(l.license_key)}">Tải</button>
            <button class="icon-btn" data-act="renew" data-key="${escapeHtml(l.license_key)}">Gia hạn</button>
            ${status !== 'revoked'
              ? `<button class="icon-btn danger" data-act="revoke" data-key="${escapeHtml(l.license_key)}">Thu hồi</button>`
              : ''}
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

// ------------------------------------------------------------------ Hộp thoại

let modalState = null;

function openModal({ title, text, mode, key }) {
  modalState = { mode, key };
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
  } else {
    hide(reasonLabel); hide(reasonInput);
    show(daysLabel); show(daysInput);
    daysInput.value = '30';
    setText('#modal-confirm', 'Gia hạn');
    $('#modal-confirm').className = 'btn btn-primary';
  }

  show($('#modal'));
  setTimeout(() => (mode === 'revoke' ? reasonInput : daysInput).focus(), 30);
}

function closeModal() {
  hide($('#modal'));
  modalState = null;
}

$('#modal-cancel').addEventListener('click', closeModal);
$('.modal-backdrop').addEventListener('click', closeModal);

$('#modal-confirm').addEventListener('click', async () => {
  if (!modalState) return;
  const btn = $('#modal-confirm');
  btn.disabled = true;

  try {
    if (modalState.mode === 'revoke') {
      await api('/api/licenses/revoke', {
        method: 'POST',
        body: { licenseKey: modalState.key, reason: $('#modal-reason').value.trim() },
      });
      toast('Đã thu hồi license.', 'success');
    } else {
      const data = await api('/api/licenses/renew', {
        method: 'POST',
        body: { licenseKey: modalState.key, daysValid: Number($('#modal-days').value) },
      });
      lastIssued = { licenseKey: data.licenseKey, licenseText: data.licenseText };
      setText('#result-key', data.licenseKey);
      setText('#result-expiry', 'Hết hạn: ' + formatDate(data.expiresAt));
      $('#result-text').value = data.licenseText;
      hide($('#issue-empty'));
      show($('#issue-result'));
      toast('Đã gia hạn: ' + data.licenseKey, 'success');
    }
    closeModal();
    loadLicenses();
  } catch (err) {
    toast(err.message, 'error');
  } finally {
    btn.disabled = false;
  }
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && modalState) closeModal();
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
