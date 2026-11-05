// 快速進出：掃碼佇列、操作冪等、局部更新及一次結帳。Chrome 109。
(async function () {
  'use strict';
  var panel = document.getElementById('quick-cart');
  if (!panel || !window.WhScan || !window.fetch) return;
  function $(id) { return document.getElementById(id); }
  var base = panel.getAttribute('data-base-url');
  var initial = $('quick-initial');
  var cart = initial ? JSON.parse(initial.textContent) : { version: 0, lines: [] };
  var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
  var token = tokenInput ? tokenInput.value : '';
  var el = { input: $('scan-input'), status: $('scan-status'), qty: $('quick-qty'), qtyRow: $('quick-qty-row'), qtyItem: $('quick-qty-item'), mode: $('quick-add-mode') };
  var checkout = $('quick-checkout-form');
  // 取得分頁鎖及恢復偏好前不可輸入，避免極快的操作被尚未完成的初始化覆蓋。
  var initialControls = Array.from(document.querySelectorAll('main input,main select,main textarea,main button')).map(function (control) {
    var state = { control: control, disabled: control.disabled }; control.disabled = true; return state;
  });
  // 同一瀏覽器同一車只允許一個編輯分頁；Web Locks 不輪詢，關頁自動釋放。
  var lockName = 'quick.edit.' + panel.dataset.actorId + '.' + panel.dataset.cartId, releaseTab = null;
  var fallbackLock = false, lockOwner = null, ownsTab = false;
  function readOnly(message) {
    var notice = document.createElement('div'); notice.className = 'message warn'; notice.id = 'quick-tab-lock'; notice.textContent = message;
    var retry = document.createElement('button'); retry.type = 'button'; retry.textContent = '關閉另一個分頁後重新檢查'; retry.addEventListener('click', function () { window.location.reload(); }); notice.appendChild(retry);
    panel.before(notice);
    document.querySelectorAll('main input,main select,main textarea,main button').forEach(function (c) { if (c !== retry) c.disabled = true; });
  }
  if (navigator.locks && navigator.locks.request) {
    ownsTab = await new Promise(function (resolve) {
      navigator.locks.request(lockName, { ifAvailable: true }, function (lock) {
        if (!lock) { resolve(false); return; }
        resolve(true); return new Promise(function (release) { releaseTab = release; });
      }).catch(function () { resolve(false); });
    });
  } else {
    // 非安全來源的降級：儲存鎖＋storage事件，異常關閉時由使用者明確接手。
    fallbackLock = true;
    try {
      lockOwner = uuid();
      var holder = localStorage.getItem(lockName);
      if (!holder) { localStorage.setItem(lockName, lockOwner); ownsTab = localStorage.getItem(lockName) === lockOwner; }
    } catch (e) { ownsTab = false; }
  }
  if (!ownsTab) {
    readOnly('另一個分頁正在編輯這台購物車，本頁暫時唯讀。');
    if (fallbackLock && lockOwner) {
      var take = document.createElement('button'); take.type = 'button'; take.textContent = '原分頁已關閉，接手作業';
      take.addEventListener('click', function () { localStorage.removeItem(lockName); window.location.reload(); }); $('quick-tab-lock').appendChild(take);
    }
    return;
  }
  window.addEventListener('pagehide', function () {
    ownsTab = false;
    if (releaseTab) releaseTab();
    if (fallbackLock) { try { if (localStorage.getItem(lockName) === lockOwner) localStorage.removeItem(lockName); } catch (e) {} }
  });
  window.addEventListener('pageshow', function (ev) { if (ev.persisted) window.location.reload(); });
  if (fallbackLock) window.addEventListener('storage', function (ev) {
    if (ev.key === lockName && ev.newValue !== lockOwner && ownsTab) { ownsTab = false; readOnly('作業已由另一個分頁接手，本頁停止修改。'); }
  });
  var location = null, pending = null, failures = [], requestInFlight = false, completed = false, checkoutRequest = null, checkoutUnknown = false;
  var stockSequence = 0, journal = [], lastAdded = null, undoRequest = null, draftStale = false, dirty = false, storageFailed = false, restoring = true;
  var stateKey = 'quick.work.' + panel.dataset.actorId + '.' + panel.dataset.cartId;
  var recovered = null;
  try { recovered = JSON.parse(localStorage.getItem(stateKey) || 'null'); } catch (e) { storageFailed = true; }
  var prefsNode = $('quick-preferences'), prefs = prefsNode ? JSON.parse(prefsNode.textContent) : {};
  function fields() {
    var values = {};
    if (checkout) checkout.querySelectorAll('input[id],select[id],textarea[id]').forEach(function (i) { if (!i.closest('#quick-allocations') && i.id !== 'quick-select-all' && i.id !== 'quick-bulk-location') values[i.id] = i.value; });
    return values;
  }
  function allocationState() {
    var rows = {};
    if (checkout) $('quick-allocations').querySelectorAll('.quick-allocation-card').forEach(function (c) {
      rows[c.dataset.lineId] = Array.from(c.querySelectorAll('.quick-allocation')).map(function (r) { return { location: r.querySelector('.allocation-location').value, quantity: r.querySelector('.allocation-quantity').value, reservationId: r.querySelector('.allocation-reservation') ? r.querySelector('.allocation-reservation').value : '' }; });
    });
    return rows;
  }
  function lineEdits() { var values = {}; document.querySelectorAll('.line-total[data-dirty]').forEach(function (i) { values[i.closest('[data-line-id]').dataset.lineId] = i.value; }); return values; }
  function workState() { return { lineEdits: lineEdits(), version: cart.version, signature: cart.lines.map(function (l) { return l.id + ':' + l.quantity; }).sort().join('|'), fields: fields(), allocations: allocationState() }; }
  function persist() {
    if (restoring || completed || !stillOwnsTab()) return;
    try { localStorage.setItem(stateKey, JSON.stringify({ draft: workState(), scanLocation: location, failures: failures.map(function (f) { return { raw: f.raw, request: f.request, message: f.message }; }), checkoutRequest: checkoutRequest, journal: journal, pendingQuantity: pending ? el.qty.value : null, lastAdded: lastAdded, undoRequest: undoRequest, stale: draftStale })); storageFailed = false; }
    catch (e) { storageFailed = true; if ($('quick-draft-status')) $('quick-draft-status').textContent = '此裝置無法保存，離頁前請儲存帳號草稿。'; }
  }
  function changed() { dirty = true; persist(); if ($('quick-draft-status') && !storageFailed) $('quick-draft-status').textContent = '已保存此裝置；尚未儲存至帳號'; }
  function stillOwnsTab() {
    if (!ownsTab) return false;
    if (fallbackLock) { try { if (localStorage.getItem(lockName) !== lockOwner) { ownsTab = false; return false; } } catch (e) { ownsTab = false; return false; } }
    return true;
  }
  function clearWork() { if (!stillOwnsTab()) return; try { localStorage.removeItem(stateKey); } catch (e) {} }
  function markCard(card) { card.dataset.edited = '1'; allocationSummary(card); changed(); }
  function allocationSummary(card) {
    var line = cart.lines.find(function (l) { return l.id === card.dataset.lineId; }); if (!line) return;
    var total = 0; card.querySelectorAll('.quick-allocation').forEach(function (row) {
      var value = Number(row.querySelector('.allocation-quantity').value);
      if (row.querySelector('.allocation-location').value.trim() && Number.isFinite(value) && value > 0) total += value;
    });
    var summary = card.querySelector('.allocation-summary'); if (summary) { var left = Math.round((Number(line.quantity) - total) * 1000000) / 1000000; summary.textContent = '已分配 ' + total + '，' + (left >= 0 ? '待分配 ' + left : '超出 ' + -left); summary.className = 'allocation-summary ' + (left ? 'bad' : 'good'); }
  }
  async function edit(handler, input) {
    if (locked()) return;
    requestInFlight = true; persist(); sync();
    try { var state = await post(handler, input); cart = state; lastAdded = null; undoRequest = null; if (input.lineId) { var edited = document.querySelector('#quick-allocations [data-line-id="' + input.lineId + '"] .line-total'); if (edited) delete edited.dataset.dirty; } renderCart(); changed(); status('已儲存清單修改（尚未入帳）。', 'ok'); return true; }
    catch (e) { status(e.message + '；輸入保留，請核對後再送出。', 'bad'); if (e.code === 'scan_cart.concurrency_conflict') { draftStale = true; $('quick-review-draft').hidden = false; } }
    finally { requestInFlight = false; sync(); }
  }
  var MODE_KEY = 'quick.addMode';
  try { var saved = localStorage.getItem(MODE_KEY); if (saved === 'manual' || saved === 'auto') el.mode.value = saved; } catch (e) { }
  function uuid() {
    if (window.crypto.randomUUID) return window.crypto.randomUUID();
    var b = new Uint8Array(16); window.crypto.getRandomValues(b); b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
    var h = Array.from(b).map(function (n) { return n.toString(16).padStart(2, '0'); }).join('');
    return h.slice(0, 8) + '-' + h.slice(8, 12) + '-' + h.slice(12, 16) + '-' + h.slice(16, 20) + '-' + h.slice(20);
  }
  function status(text, kind) { el.status.textContent = text; el.status.className = 'scan-status scan-' + kind; }
  function busy() { return !!pending || requestInFlight || (scanner && scanner.pending() > 0); }
  function locked() { return !ownsTab || busy() || failures.length > 0 || completed || checkoutUnknown || draftStale; }
  function sync() {
    if ($('quick-checkout')) $('quick-checkout').disabled = !ownsTab || busy() || failures.length > 0 || completed || draftStale;
    if ($('quick-undo')) $('quick-undo').disabled = locked() || (!lastAdded && !undoRequest);
    var action = $('quick-action-form');
    if (action) action.querySelectorAll('button').forEach(function (b) { b.disabled = locked(); });
    el.mode.disabled = !!pending || requestInFlight || checkoutUnknown || completed;
    var frozen = !ownsTab || requestInFlight || checkoutUnknown || completed;
    el.input.disabled = frozen;
    $('scan-camera').disabled = frozen || !!pending;
    $('quick-location-clear').disabled = frozen || busy();
    if (checkout) checkout.querySelectorAll('input,select,textarea,button').forEach(function (c) {
      if (c.id !== 'quick-checkout' && c.id !== 'quick-next') c.disabled = frozen || (c.id === 'quick-checkout-warehouse' && !!cart.warehouseId) || (c.id === 'quick-recipient' && $('quick-checkout-type').value !== 'Outbound');
    });
    if (checkout) {
      var outbound = $('quick-checkout-type').value === 'Outbound'; $('quick-precheck').hidden = !outbound;
      checkout.querySelectorAll('.quick-reservation').forEach(function (details) {
        var enabled = checkout.dataset.reservations === 'true', selected = !!details.parentElement.querySelector('.allocation-reservation').value;
        details.hidden = !outbound || (!enabled && !selected);
        details.querySelector('.reservation-disabled-note').hidden = enabled || !selected;
        details.querySelector('.allocation-reservation-pick').disabled = frozen || !enabled;
        if (!enabled && selected && outbound) details.open = true;
      });
    }
    if (completed && checkout) { checkout.querySelector('.quick-checkout-bar').hidden = true; document.body.classList.remove('quick-has-checkout'); }
    if (checkoutUnknown && $('quick-checkout')) $('quick-checkout').textContent = '重試確認原結帳結果';
    if ($('quick-summary')) $('quick-summary').textContent = cart.lines.length + ' 行，' + (pending ? '等待數量' : busy() ? '處理中' : failures.length ? '有待處理紀錄' : '請核對各料數量與儲位');
  }
  async function json(url, options) {
    if (options && options.method && options.method !== 'GET' && !stillOwnsTab()) {
      var lost = new Error('這台購物車已由另一分頁接手，本頁未送出新操作。'); lost.definite = true; throw lost;
    }
    var abort = new AbortController(), timeout = setTimeout(function () { abort.abort(); }, 15000);
    try {
      options = options || {}; options.credentials = 'same-origin'; options.signal = abort.signal;
      var res = await fetch(url, options);
      if (res.redirected || res.status === 401 || res.status === 403) throw new Error('登入已逾時或沒有權限，請重新登入後核對清單。');
      var body;
      try { body = await res.json(); } catch (e) { throw new Error('回應不完整，請核對結果或重試原操作。'); }
      if (!res.ok) { var err = new Error(body.message || '處理失敗'); err.definite = res.status === 400; err.code = body.code; throw err; }
      return body;
    } finally { clearTimeout(timeout); }
  }
  function post(handler, body) { return json(base + '?handler=' + handler, { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token }, body: JSON.stringify(body) }); }
  function button(text, fn) { var b = document.createElement('button'); b.type = 'button'; b.className = 'secondary'; b.textContent = text; b.addEventListener('click', fn); return b; }
  function removeFailure(f) { failures = failures.filter(function (x) { return x !== f; }); renderFailures(); }
  function fail(message, raw, request, existing) {
    var f = existing || { raw: raw, request: request }; f.message = message; f.running = false;
    if (!existing) failures.push(f);
    renderFailures(); window.WhScan.feedback('error'); status(message, 'bad');
  }
  function renderFailures() {
    var root = $('quick-failures'); if (!root) return;
    root.textContent = '';
    failures.forEach(function (f) {
      var li = document.createElement('li'), text = document.createElement('span'); text.textContent = f.message + ' '; li.appendChild(text);
      var retry = button(f.running ? '處理中' : '重試原操作', function () { f.running = true; renderFailures(); scanner.enqueue(f.raw, 'retry', { retry: f, operationId: f.request ? f.request.operationId : uuid(), request: f.request }); }); retry.disabled = f.running || requestInFlight || completed; li.appendChild(retry);
      var skip = button('核對後略過此紀錄', function () { removeFailure(f); }); skip.disabled = f.running || requestInFlight || completed; li.appendChild(skip);
      if (!f.request) li.appendChild(button('搜尋物料／登錄條碼', function () { resolveUnknown(li, f); }));
      root.appendChild(li);
    }); sync(); persist();
  }
  async function resolveUnknown(li, failure) {
    var area = document.createElement('div'), q = document.createElement('input'), list = document.createElement('div'); q.placeholder = '輸入品名或規格搜尋'; q.maxLength = 100;
    area.appendChild(q); area.appendChild(button('搜尋', async function () {
      list.textContent = '查詢中…';
      try {
        var result = await json('/api/search?q=' + encodeURIComponent(q.value) + '&pageSize=20'); list.textContent = '';
        (result.items || []).filter(function (item) { return !!item.itemCode; }).forEach(function (item) {
          var row = document.createElement('p'); row.textContent = item.itemCode + ' ' + item.name + ' ' + (item.specificationSummary || '') + ' ';
          row.appendChild(button('選擇並核對', function () {
            list.textContent = ''; var info = document.createElement('p'); info.textContent = '條碼 ' + failure.raw + ' → ' + item.itemCode + ' ' + item.name + ' ' + (item.specificationSummary || ''); list.appendChild(info);
            var pack = document.createElement('input'); pack.type = 'number'; pack.min = '0'; pack.step = 'any'; pack.value = '1'; pack.setAttribute('aria-label', '包裝量'); list.appendChild(pack);
            list.appendChild(button('核對物料與包裝量後登錄', async function () {
              try { var reply = await post('Register', { code: failure.raw, itemCode: item.itemCode, packQuantity: Number(pack.value) }); info.textContent = reply.message; }
              catch (e) { info.textContent = e.message; }
            }));
          })); list.appendChild(row);
        });
        if (!list.children.length) list.textContent = '沒有符合物料；請調整關鍵字。';
      } catch (e) { list.textContent = e.message; }
    })); area.appendChild(list); li.appendChild(area);
  }
  function setLocation(loc) { location = loc; $('quick-location').textContent = loc ? loc.label : '（未指定）'; $('quick-location-clear').hidden = !loc; persist(); }
  $('quick-location-clear').addEventListener('click', function () { if (busy() || checkoutUnknown || completed) return; setLocation(null); status('已清除目前儲位。', 'warn'); scanner.focus(); });
  function rowFor(line) {
    var row = document.createElement('tr'); row.dataset.lineId = line.id;
    for (var i = 0; i < 6; i++) row.appendChild(document.createElement('td'));
    row.cells[0].textContent = line.lineNo; row.cells[1].textContent = line.itemCode;
    row.cells[2].textContent = line.itemName + ' ' + (line.specification || '') + (line.itemActive ? '' : '（已停用）');
    row.cells[3].textContent = line.locationCode || '（未指定）'; row.cells[4].textContent = line.quantity + ' ' + line.unitCode;
    var form = document.createElement('form'); form.method = 'post'; form.action = base + '?handler=UpdateLine'; form.className = 'inline quick-line-form';
    function input(name, value, type) { var x = document.createElement('input'); x.name = name; x.type = type || 'text'; x.value = value; form.appendChild(x); return x; }
    input('__RequestVerificationToken', token, 'hidden'); input('lineId', line.id, 'hidden'); input('version', cart.version, 'hidden');
    var qty = input('quantity', line.quantity); qty.inputMode = 'decimal'; qty.style.width = '6em'; qty.setAttribute('aria-label', '第 ' + line.lineNo + ' 行數量');
    var loc = input('locationCode', line.locationCode && cart.warehouseCode ? 'WH:LOC:' + cart.warehouseCode + ':' + line.locationCode : ''); loc.placeholder = '儲位 QR'; loc.style.width = '10em'; loc.setAttribute('aria-label', '第 ' + line.lineNo + ' 行儲位');
    var update = document.createElement('button'); update.type = 'submit'; update.className = 'secondary'; update.textContent = '更新'; form.appendChild(update);
    var del = document.createElement('button'); del.type = 'submit'; del.formAction = base + '?handler=RemoveLine'; del.className = 'secondary'; del.textContent = '刪除'; form.appendChild(del);
    row.cells[5].appendChild(form); return row;
  }
  function renderCart(line) {
    var root = document.querySelector('[data-quick-lines]'); if (!root) return;
    var table = root.querySelector('table');
    if (!table) { root.textContent = ''; var h = document.createElement('h2'); root.appendChild(h); table = document.createElement('table'); table.id = 'quick-line-table'; table.innerHTML = '<thead><tr><th>#</th><th>料號</th><th>品名</th><th>儲位</th><th>數量</th><th>修改</th></tr></thead><tbody></tbody>'; var wrap = document.createElement('div'); wrap.className = 'table-wrap'; wrap.appendChild(table); root.appendChild(wrap); }
    var body = table.querySelector('tbody');
    if (!line) { body.textContent = ''; cart.lines.forEach(function (l) { body.appendChild(rowFor(l)); }); }
    else { var old = body.querySelector('[data-line-id="' + line.id + '"]'); if (old) old.replaceWith(rowFor(line)); else body.appendChild(rowFor(line)); }
    root.querySelector('h2').textContent = '購物車內容（' + cart.lines.length + ' 行' + (cart.warehouseCode ? '，倉庫 ' + cart.warehouseCode : '') + '）';
    root.dataset.version = cart.version;
    root.querySelectorAll('input[name="version"]').forEach(function (v) { v.value = cart.version; });
    stockSequence++;
    if (checkout && !completed) $('quick-stock').textContent = '清單已變更，請更新出庫檢查；正式結帳仍重新驗證。';
    renderAllocations(line); sync();
  }
  async function refresh() { cart = await json(base + '?handler=Snapshot'); renderCart(); persist(); }
  async function add(request, label, prior) {
    var active = prior || { raw: request.itemCode, request: request, message: '加入結果待確認，可重試原操作。' };
    if (!prior) failures.push(active);
    journal.forEach(function (j) { if (j.operationId === request.operationId) j.request = request; }); persist();
    try {
      var result = await json(base + '?handler=AddDelta', { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token }, body: JSON.stringify(request) });
      if (result.baseVersion !== cart.version || !result.line) await refresh();
      else {
        cart.version = result.version; cart.warehouseCode = result.warehouseCode; cart.warehouseId = result.warehouseId;
        var found = cart.lines.findIndex(function (l) { return l.id === result.line.id; }); if (found >= 0) cart.lines[found] = result.line; else cart.lines.push(result.line);
        renderCart(result.line);
      }
      removeFailure(active); lastAdded = { original: request, expectedVersion: cart.version }; undoRequest = null; persist();
      status('已加入清單：' + label + ' × ' + request.quantity + '（尚未入帳）', 'ok'); window.WhScan.feedback('ok');
    } catch (e) { fail((e.definite ? '沒有加入：' : '結果待確認，可重試同一操作：') + label + '。' + e.message, request.itemCode, request, active); }
  }
  async function onScan(value, source, extra) {
    var prior = extra && extra.retry;
    if (!ownsTab || completed || checkoutUnknown || requestInFlight) { fail('這車已完成、由另一分頁接手或結帳結果尚待確認，請先處理。', value, null, prior); return; }
    if (extra && extra.request) { await add(extra.request, extra.request.itemCode, prior); return; }
    if (prior && prior.request) { await add(prior.request, prior.request.itemCode, prior); return; }
    try {
      var body = await json('/api/scan?code=' + encodeURIComponent(value));
      if (body.kind === 'Location') {
        var loc = body.location; if (loc.status === 'Inactive') throw new Error('儲位已停用。');
        setLocation({ code: 'WH:LOC:' + loc.warehouseCode + ':' + loc.code, label: loc.warehouseCode + ' 倉 ' + loc.code });
        if (prior) removeFailure(prior); status('目前儲位：' + location.label + '，接著掃物料。', 'warn'); return;
      }
      if (body.kind !== 'Item') throw new Error(body.message || '不是已登錄的物料或儲位 QR，請搜尋核對後登錄。');
      var item = body.item, label = item.itemCode + ' ' + item.name;
      var request = { itemId: item.id, itemCode: item.itemCode, locationCode: location ? location.code : null, quantity: Number(item.packQuantity), operationId: extra && extra.operationId ? extra.operationId : uuid() };
      if (el.mode.value === 'auto') { await add(request, label, prior); return; }
      await new Promise(function (resolve) {
        qtyKeys = { text: '', last: 0, fast: true };
        pending = { request: request, label: label, resolve: resolve, prior: prior }; el.qty.value = recovered && recovered.pendingQuantity !== null && recovered.pendingQuantity !== undefined ? recovered.pendingQuantity : request.quantity; if (recovered) recovered.pendingQuantity = null; el.qtyItem.textContent = label + '（單位 ' + item.unitCode + '）'; el.qtyRow.hidden = false;
        status('等待數量確認：' + label, 'warn'); window.WhScan.feedback('warn'); sync(); persist(); el.qty.focus(); el.qty.select();
      });
    } catch (e) { fail(e.message + '（' + value + '）', value, null, prior); }
  }
  var confirmingQuantity = false;
  async function addPending() {
    if (!pending || confirmingQuantity) return;
    var p = pending, text = el.qty.value.replace(/[０-９．]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); }).trim();
    confirmingQuantity = true;
    try {
      var n = Number(text); if (!text || !Number.isFinite(n) || n <= 0) { status('數量要大於 0。', 'bad'); return; }
      p.request.quantity = n;
      await add(p.request, p.label, p.prior);
      pending = null; el.qtyRow.hidden = true; p.resolve(); scanner.focus();
    } catch (e) { status(e.message + '，目前物料仍等待數量。', 'bad'); }
    finally { confirmingQuantity = false; sync(); persist(); }
  }
  var qtyKeys = { text: '', last: 0, fast: true };
  el.qty.addEventListener('keydown', function (ev) {
    if (ev.isComposing) return;
    if (ev.key.length === 1 && !ev.ctrlKey && !ev.altKey && !ev.metaKey) {
      if (ev.timeStamp - qtyKeys.last > 80) qtyKeys = { text: '', last: 0, fast: true };
      if (qtyKeys.last && ev.timeStamp - qtyKeys.last > 45) qtyKeys.fast = false;
      qtyKeys.text += ev.key; qtyKeys.last = ev.timeStamp;
    }
    if (ev.key === 'Enter' || ev.key === 'Tab') {
      if (qtyKeys.fast && qtyKeys.text.length >= 3 && ev.timeStamp - qtyKeys.last < 80) {
        ev.preventDefault(); el.qty.value = qtyKeys.text; status('這段快速輸入可能是條碼。請選擇「把此內容當下一筆掃碼」，或按確認將它當作數量。', 'warn'); persist();
      } else if (ev.key === 'Enter') { ev.preventDefault(); addPending(); }
      qtyKeys = { text: '', last: 0, fast: true };
    }
    if (ev.key === 'Escape') cancelPending();
  });
  $('quick-qty-add').addEventListener('click', addPending);
  $('quick-qty-scan').addEventListener('click', function () { if (!pending || !el.qty.value.trim()) return; scanner.enqueue(el.qty.value.trim(), 'keyboard'); el.qty.value = pending.request.quantity; status('已保留下一筆掃碼，請先確認目前數量。', 'warn'); persist(); });
  el.qty.addEventListener('input', persist);
  function cancelPending() { if (!pending || confirmingQuantity) return; var p = pending; pending = null; el.qtyRow.hidden = true; if (p.prior) removeFailure(p.prior); p.resolve(); status('已取消這筆，未加入清單。', 'warn'); scanner.focus(); sync(); }
  $('quick-qty-cancel').addEventListener('click', cancelPending);
  el.mode.addEventListener('change', function () { try { localStorage.setItem(MODE_KEY, el.mode.value); } catch (e) {} scanner.focus(); });
  var scanner = window.WhScan.attach({ input: el.input, container: panel, onScan: onScan,
    onQueued: function (job) { if (!job.extra) job.extra = {}; if (!job.extra.operationId) job.extra.operationId = uuid(); journal.push({ value: job.value, source: job.source, operationId: job.extra.operationId, request: job.extra.request }); persist(); },
    onFinished: function (job) { journal = journal.filter(function (j) { return j.operationId !== job.extra.operationId; }); persist(); },
    onReject: function (message) { fail(message, '', null); }, onBusyChange: sync,
    shouldPauseCamera: function () { return !!pending || requestInFlight || checkoutUnknown || completed; },
    focusWarning: $('scan-focus-warning'), refocusButton: $('scan-refocus'), capsIndicator: $('scan-caps'), muteCheckbox: $('scan-mute'), cameraButton: $('scan-camera'), cameraBox: $('scan-camera-box'), cameraVideo: $('scan-video'), cameraState: $('scan-camera-state'), cameraStop: $('scan-camera-stop') });
  window.WhScanBusy = function () { return busy() || failures.length > 0 || checkoutUnknown; };
  document.addEventListener('submit', function (ev) { if (ev.target !== checkout && (locked() || (checkoutRequest && !completed))) { ev.preventDefault(); status('仍有待確認、未完成的掃碼或結帳結果，請先處理。', 'bad'); } }, true);
  window.addEventListener('beforeunload', function (ev) { if ((locked() || (dirty && storageFailed)) && !completed) { ev.preventDefault(); ev.returnValue = ''; } });
  function allocRow(card, line, loc, quantity, reservationId) {
    var row = document.createElement('div'); row.className = 'quick-allocation';
    var locationInput = document.createElement('input'); locationInput.type = 'text'; locationInput.className = 'allocation-location'; locationInput.value = loc || ''; locationInput.placeholder = '實際儲位'; locationInput.required = true; locationInput.maxLength = 200; locationInput.setAttribute('aria-label', line.itemCode + ' 儲位');
    var qty = document.createElement('input'); qty.type = 'text'; qty.className = 'allocation-quantity'; qty.inputMode = 'decimal'; qty.value = quantity; qty.required = true; qty.setAttribute('aria-label', line.itemCode + ' 分配數量');
    row.appendChild(locationInput); row.appendChild(qty);
    var reservation = document.createElement('input'); reservation.type = 'hidden'; reservation.className = 'allocation-reservation'; reservation.value = reservationId || ''; row.appendChild(reservation);
    var advanced = document.createElement('details'); advanced.className = 'quick-reservation'; advanced.open = !!reservationId;
    var advancedTitle = document.createElement('summary'); advancedTitle.textContent = '使用預留（選填）'; advanced.appendChild(advancedTitle); row.appendChild(advanced);
    var disabledNote = document.createElement('p'); disabledNote.className = 'reservation-disabled-note bad'; disabledNote.hidden = true; disabledNote.textContent = '指定預留已關閉，請清除這筆原選擇後重新核對。清除不會釋放實際預留。'; advanced.appendChild(disabledNote);
    var reserved = document.createElement('span'); reserved.className = 'allocation-reservation-label'; reserved.textContent = reservation.value ? '已指定預留 ' + reservation.value : '不使用預留'; advanced.appendChild(reserved);
    var choices = document.createElement('div');
    async function loadReservations(page) {
      if (locked()) return;
      if (checkout.dataset.reservations !== 'true') { status('指定預留已關閉，請先清除原選擇。', 'warn'); return; }
      if ($('quick-checkout-type').value !== 'Outbound' || !$('quick-checkout-warehouse').value) { status('請先選領料與倉庫。', 'warn'); return; }
      choices.textContent = '查詢預留…';
      try { var result = await json(base + '?handler=Reservations&warehouseId=' + encodeURIComponent($('quick-checkout-warehouse').value) + '&itemCode=' + encodeURIComponent(line.itemCode) + '&p=' + page); choices.textContent = '';
        (result.items || []).forEach(function (r) { choices.appendChild(button('使用預留：剩餘 ' + r.remainingQuantity + ' ' + (r.note || '') + '（' + r.createdByName + '）', function () { reservation.value = r.id; reserved.textContent = '已指定預留，剩餘 ' + r.remainingQuantity + ' ' + (r.note || ''); choices.textContent = ''; markCard(card); })); });
        if (!(result.items || []).length) choices.textContent = '沒有符合的手動預留。';
        if (page > 1) choices.appendChild(button('上一頁預留', function () { loadReservations(page - 1); }));
        if (page < result.totalPages) choices.appendChild(button('下一頁預留', function () { loadReservations(page + 1); }));
      } catch (e) { choices.textContent = e.message; }
    }
    var pickReservation = button('選擇既有預留', function () { loadReservations(1); }); pickReservation.classList.add('allocation-reservation-pick'); advanced.appendChild(pickReservation);
    advanced.appendChild(button('清除預留', function () { reservation.value = ''; reserved.textContent = '不使用預留'; markCard(card); sync(); })); advanced.appendChild(choices); row.appendChild(button('移除此分配', function () { row.remove(); markCard(card); })); card.appendChild(row);
    if (window.WhPicker) window.WhPicker.attach(locationInput, { load: async function (q) { var w = $('quick-checkout-warehouse').value; if (!w) return []; var r = await json(base + '?handler=Locations&warehouseId=' + encodeURIComponent(w) + '&itemId=' + line.itemId + '&q=' + encodeURIComponent(q)); return r.items || []; }, render: function (o) { return { main: o.code, sub: o.name || '' }; }, pick: function (o) { locationInput.value = o.code; markCard(card); }, empty: '請先選倉庫，或輸入儲位碼' });
    row.querySelectorAll('input').forEach(function (i) { i.addEventListener('input', function () { markCard(card); }); });
  }
  function renderAllocations(changedLine) {
    if (!checkout) return;
    var tracked = cart.lines.some(function (l) { return l.trackingMode !== 'None' && l.trackingMode !== 0; }); checkout.hidden = tracked; document.body.classList.toggle('quick-has-checkout', !tracked);
    if (tracked) { $('quick-lines').hidden = false; $('quick-note-form').hidden = false; status('這車含批號／序號物料，請使用轉草稿補齊。', 'warn'); return; }
    var root = $('quick-allocations');
    $('quick-lines').hidden = !tracked;
    $('quick-note-form').hidden = !tracked;
    if (!changedLine) Array.from(root.children).forEach(function (c) { if (!cart.lines.some(function (l) { return l.id === c.dataset.lineId; })) c.remove(); });
    (changedLine ? [changedLine] : cart.lines).forEach(function (line) {
      var card = root.querySelector('[data-line-id="' + line.id + '"]');
      if (!card) { card = document.createElement('section'); card.className = 'quick-allocation-card'; card.dataset.lineId = line.id; var h = document.createElement('h4'); card.appendChild(h);
        var check = document.createElement('input'); check.type = 'checkbox'; check.className = 'allocation-select'; check.setAttribute('aria-label', '勾選 ' + line.itemCode); card.appendChild(check);
        var sum = document.createElement('p'); sum.className = 'allocation-summary'; card.appendChild(sum);
        var total = document.createElement('input'); total.type = 'number'; total.step = 'any'; total.min = '0'; total.className = 'line-total'; total.value = line.quantity; total.setAttribute('aria-label', line.itemCode + ' 清單總量'); total.addEventListener('input', function () { total.dataset.dirty = '1'; changed(); }); card.appendChild(total);
        card.appendChild(button('儲存總量', function () { var current = cart.lines.find(function (x) { return x.id === line.id; }); edit('EditLine', { lineId: line.id, expectedVersion: cart.version, quantity: Number(total.value), locationCode: current.locationCode && cart.warehouseCode ? 'WH:LOC:' + cart.warehouseCode + ':' + current.locationCode : null }); }));
        card.appendChild(button('刪除此料', function () { edit('DeleteLine', { lineId: line.id, expectedVersion: cart.version }); })); card.appendChild(button('＋分到另一儲位', function () { card.dataset.edited = '1'; allocRow(card, line, '', ''); markCard(card); })); root.appendChild(card); allocRow(card, line, line.locationCode, line.quantity); }
      else if (!card.dataset.edited && card.querySelectorAll('.quick-allocation').length === 1) { card.querySelector('.allocation-quantity').value = line.quantity; card.querySelector('.allocation-location').value = line.locationCode || ''; }
      if (card.querySelector('.line-total') && !card.querySelector('.line-total').dataset.dirty) card.querySelector('.line-total').value = line.quantity;
      allocationSummary(card);
      card.querySelector('h4').textContent = line.itemCode + ' ' + line.itemName + '｜合計 ' + line.quantity + ' ' + line.unitCode;
    });
    var w = $('quick-checkout-warehouse'); if (cart.warehouseId) { w.value = cart.warehouseId; w.disabled = true; }
  }
  async function precheck() {
    if (!checkout || $('quick-checkout-type').value !== 'Outbound' || !$('quick-checkout-warehouse').value) return;
    var area = $('quick-stock'), sequence = ++stockSequence, version = cart.version, warehouseId = $('quick-checkout-warehouse').value; area.textContent = '查詢庫存…';
    try {
      var rows = await json(base + '?handler=Stock&warehouseId=' + encodeURIComponent(warehouseId));
      if (sequence !== stockSequence || version !== cart.version || warehouseId !== $('quick-checkout-warehouse').value || $('quick-checkout-type').value !== 'Outbound') return;
      area.textContent = '';
      cart.lines.forEach(function (l, index, all) {
        if (all.findIndex(function (x) { return x.itemId === l.itemId; }) !== index) return;
        var stock = rows.filter(function (s) { return s.itemId === l.itemId; }), available = stock.length ? stock[0].warehouseAvailable : 0;
        var need = all.filter(function (x) { return x.itemId === l.itemId; }).reduce(function (n, x) { return n + Number(x.quantity); }, 0);
        var p = document.createElement('p'); p.textContent = l.itemCode + ' 本車 ' + need + '，倉庫可用量（已扣預留）' + available + '。來源結存：' + stock.filter(function (r) { return r.locationCode; }).map(function (r) { return r.locationCode + ' ' + r.quantity; }).join('、');
        if (available < need) { p.className = 'bad'; p.textContent += '；未指定預留的數量可能不足。已選預留部分依該預留餘額檢查，正式結帳仍重新驗證。'; } area.appendChild(p);
      });
      var note = document.createElement('p'); note.className = 'muted'; note.textContent = '來源結存最多列每料前 20 個有效儲位；預留屬倉庫層級。以上為當下提示，正式結帳重新檢查。'; area.appendChild(note);
    } catch (e) { if (sequence === stockSequence && version === cart.version) area.textContent = e.message; }
  }
  if (checkout) {
    $('quick-recipient').addEventListener('input', function () { $('quick-recipient-id').value = ''; });
    if (window.WhPicker) window.WhPicker.attach($('quick-recipient'), { load: function (q) { return json(base + '?handler=Recipients&q=' + encodeURIComponent(q)); }, render: function (o) { return { main: o.displayName, sub: o.userName }; }, pick: function (o) { $('quick-recipient').value = o.displayName; $('quick-recipient-id').value = o.id; changed(); }, empty: '沒有符合的啟用帳號' });
    $('quick-precheck').addEventListener('click', precheck);
    $('quick-checkout-type').addEventListener('change', function () {
      var type = this.value; checkout.querySelectorAll('optgroup[data-types]').forEach(function (g) { g.disabled = g.getAttribute('data-types').split(',').indexOf(type) < 0; g.querySelectorAll('option').forEach(function (o) { o.disabled = g.disabled; if (g.disabled && o.selected) o.parentNode.parentNode.value = ''; }); });
      var purpose = $('Header_PurposeId'); if (purpose && !purpose.value) { var preferred = prefs[type === 'Inbound' ? 'quick.lastPurpose.in' : 'quick.lastPurpose.out']; if (preferred && Array.from(purpose.options).some(function (o) { return o.value === preferred && !o.disabled; })) purpose.value = preferred; }
      $('quick-recipient').disabled = type !== 'Outbound'; if (type === 'Outbound') precheck(); else $('quick-stock').textContent = '';
      $('quick-checkout').textContent = checkout.getAttribute('data-approval') === 'true' ? '核對後送審（尚未入帳）' : type === 'Inbound' ? '確認進貨入庫' : type === 'Outbound' ? '確認領料出庫' : '核對後確認結帳';
      sync();
    });
    $('quick-checkout-warehouse').addEventListener('change', function () { $('quick-stock').textContent = ''; precheck(); });
    checkout.addEventListener('submit', async function (ev) {
      ev.preventDefault(); if (!stillOwnsTab() || busy() || failures.length || completed || draftStale) { status('請先完成掃碼、待處理紀錄或確認作業分頁。', 'bad'); return; }
      var output = $('quick-checkout-result'), checkoutSent = false;
      requestInFlight = true; sync();
      try {
        if (!checkoutRequest) {
          if (!cart.lines.length) throw new Error('購物車是空的。');
          var type = $('quick-checkout-type').value, warehouse = $('quick-checkout-warehouse').value;
          if (type === 'Outbound') await precheck();
          if (!type || !warehouse) throw new Error('請選擇進貨／領料及倉庫。');
          if (type === 'Outbound' && !$('quick-recipient-id').value) throw new Error('請搜尋並點選實際領用帳號。');
          var allocations = [];
          var issues = [];
          $('quick-allocations').querySelectorAll('.allocation-error').forEach(function (c) { c.classList.remove('allocation-error'); });
          cart.lines.forEach(function (line) {
            var sum = 0, invalid = false, card = $('quick-allocations').querySelector('[data-line-id="' + line.id + '"]');
            card.querySelectorAll('.quick-allocation').forEach(function (r) { var q = Number(r.querySelector('.allocation-quantity').value), loc = r.querySelector('.allocation-location').value.trim();
              if (!Number.isFinite(q) || q <= 0 || !loc) invalid = true;
              sum += q; allocations.push({ lineId: line.id, locationCode: loc, quantity: q, reservationId: type === 'Outbound' ? r.querySelector('.allocation-reservation').value || null : null });
            });
            if (invalid || Math.abs(sum - Number(line.quantity)) > 0.0000001) { card.classList.add('allocation-error'); issues.push({ card: card, text: line.itemCode + (invalid ? ' 儲位或數量未填完整' : ' 分配合計不符') }); }
            var unsaved = card.querySelector('.line-total[data-dirty]'); if (unsaved && Number(unsaved.value) !== Number(line.quantity)) issues.push({ card: card, text: line.itemCode + ' 清單總量尚未儲存' });
          });
          if (issues.length) { output.textContent = issues.length + ' 項需要處理：'; issues.forEach(function (issue) { output.appendChild(button(issue.text, function () { issue.card.scrollIntoView({ block: 'center' }); var i = issue.card.querySelector('.allocation-location'); if (i) i.focus(); })); }); issues[0].card.scrollIntoView({ block: 'center' }); var issueError = new Error('請修正標示的分配或未儲存總量。'); issueError.keepOutput = true; throw issueError; }
          (checkout.dataset.requiredHeaders || '').split(',').forEach(function (id) { var input = $(id); if (input && !input.value.trim() && !(id === 'Header_PartnerId' && $('Header_PartnerCode') && $('Header_PartnerCode').value.trim())) { input.closest('details') && (input.closest('details').open = true); throw new Error('請填齊標示為必填的表頭資料。'); } });
          function value(id) { return $(id) ? $(id).value || null : null; }
          var partnerId = value('Header_PartnerId'), partnerCode = value('Header_PartnerCode');
          if (partnerCode) { var matches = await json(base + '?handler=Partner&code=' + encodeURIComponent(partnerCode)); if (matches.length !== 1) throw new Error('往來對象代碼找不到或不唯一，請改用選單。'); partnerId = matches[0].id; }
          checkoutRequest = { requestId: uuid(), expectedVersion: cart.version, type: type === 'Inbound' ? 1 : 2, warehouseId: warehouse, header: { purposeId: value('Header_PurposeId'), partnerId: partnerId, externalNo: value('Header_ExternalNo'), note: value('Header_Note') }, allocations: allocations, recipientUserId: type === 'Outbound' ? value('quick-recipient-id') : null, projectReference: value('quick-project') };
        }
        requestInFlight = true; sync(); output.textContent = '正在確認，請勿重掃或離頁…';
        checkoutSent = true; checkoutUnknown = true; persist();
        var result = await post('Checkout', checkoutRequest);
        showCompleted(result);
      } catch (e) {
        if (!e.keepOutput) output.textContent = e.message;
        if (checkoutSent && !e.definite) { checkoutUnknown = true; output.textContent += ' 結果待確認，按相同結帳按鈕安全重試；不要另開一車重送。'; }
        else { checkoutRequest = null; checkoutUnknown = false; if (e.code === 'scan_cart.concurrency_conflict') output.textContent += ' 請重新整理後核對整車。'; }
      } finally { requestInFlight = false; sync(); persist(); }
    });
  }
  function showCompleted(result) {
    completed = true; checkoutUnknown = false; dirty = false; clearWork(); el.input.disabled = true;
    var message = result.message || (result.status === 'Posted' ? '已完成過帳' : '單據目前狀態：' + result.status);
    status(message + '，單號 ' + result.docNo, 'ok');
    if ($('scan-camera-stop')) $('scan-camera-stop').click();
    $('quick-actions').hidden = true; $('quick-stock').textContent = ''; $('scan-focus-warning').hidden = true;
    $('quick-note-form').parentElement.hidden = true;
    var output = $('quick-checkout-result'); output.textContent = message + '，單號 ' + result.docNo + '。';
    var link = document.createElement('a'); link.href = result.url || '/Documents/Details/' + result.documentId; link.textContent = '查看／列印單據'; output.appendChild(link);
    var next = button('開始下一車', function () { var form = document.createElement('form'); form.method = 'post'; form.action = base + '?handler=Next'; var t = document.createElement('input'); t.type = 'hidden'; t.name = '__RequestVerificationToken'; t.value = token; form.appendChild(t); document.body.appendChild(form); form.submit(); }); next.id = 'quick-next'; output.appendChild(next);
    document.querySelectorAll('.quick-line-form input,.quick-line-form button').forEach(function (c) { c.disabled = true; });
    output.scrollIntoView({ block: 'center' }); sync();
  }
  function applyDraft(draft, fromServer) {
    if (!draft || !checkout) return;
    Object.keys(draft.fields || {}).forEach(function (id) { if ($(id) && (!cart.warehouseId || id !== 'quick-checkout-warehouse')) $(id).value = draft.fields[id]; });
    Object.keys(draft.allocations || {}).forEach(function (id) { var card = $('quick-allocations').querySelector('[data-line-id="' + id + '"]'), line = cart.lines.find(function (l) { return l.id === id; }); if (!card || !line) return;
      card.querySelectorAll('.quick-allocation').forEach(function (r) { r.remove(); }); (draft.allocations[id] || []).forEach(function (r) { allocRow(card, line, r.location, r.quantity, r.reservationId); }); card.dataset.edited = '1'; allocationSummary(card);
    });
    Object.keys(draft.lineEdits || {}).forEach(function (id) { var input = $('quick-allocations').querySelector('[data-line-id="' + id + '"] .line-total'); if (input) { input.value = draft.lineEdits[id]; input.dataset.dirty = '1'; } });
    if ((!fromServer && draft.version !== cart.version) || (fromServer && draft.signature !== workState().signature) || (recovered && recovered.stale)) { draftStale = true; $('quick-review-draft').hidden = false; $('quick-draft-status').textContent = '已恢復草稿，但清單有其他修改，請核對數量及儲位。'; }
  }
  document.addEventListener('submit', async function (ev) {
    var form = ev.target;
    if (!form.matches('.quick-line-form,#quick-note-form') || ev.defaultPrevented) return;
    ev.preventDefault(); var data = new FormData(form), remove = ev.submitter && ev.submitter.formAction.indexOf('RemoveLine') >= 0;
    await edit(form.id === 'quick-note-form' ? 'EditNote' : remove ? 'DeleteLine' : 'EditLine', { lineId: data.get('lineId') || '00000000-0000-0000-0000-000000000000', expectedVersion: cart.version, quantity: Number(data.get('quantity')), locationCode: data.get('locationCode'), note: data.get('note') });
  });
  $('quick-search-button').addEventListener('click', async function () {
    if (locked()) return; var area = $('quick-search-results'); area.textContent = '搜尋中…';
    try { var results = await json('/api/search?q=' + encodeURIComponent($('quick-search-query').value.trim()) + '&pageSize=20'); area.textContent = '';
      (results.items || []).filter(function (i) { return i.itemCode; }).forEach(function (i) { var row = document.createElement('p'); row.textContent = i.itemCode + ' ' + i.name + ' ' + (i.specificationSummary || '') + ' ';
        row.appendChild(button('選擇並核對加入', async function () { if (locked()) return; var selected; try { selected = await json('/api/items/' + i.id); if (!selected.itemCode) throw new Error('物料尚未核准。'); } catch (e) { area.textContent = e.message; return; } area.textContent = ''; var label = document.createElement('p'); label.textContent = selected.itemCode + ' ' + selected.name + ' ' + (selected.specification || '') + '（單位 ' + selected.unitCode + '）'; area.appendChild(label);
          var q = document.createElement('input'); q.type = 'number'; q.step = 'any'; q.value = '1'; q.min = '0'; q.setAttribute('aria-label', '搜尋加入數量'); area.appendChild(q);
          area.appendChild(button('確認物料及數量，加入清單', async function () { if (locked()) return; var n = Number(q.value); if (!Number.isFinite(n) || n <= 0) { label.textContent += '；請填正數數量'; return; } var request = { itemId: selected.id, itemCode: selected.itemCode, locationCode: location ? location.code : null, quantity: n, operationId: uuid() }; requestInFlight = true; sync(); await add(request, i.itemCode, null); requestInFlight = false; sync(); }));
        })); area.appendChild(row); }); if (!area.children.length) area.textContent = '沒有符合的已核准物料。';
    } catch (e) { area.textContent = e.message; }
  });
  $('quick-search-query').addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); $('quick-search-button').click(); } });
  $('quick-undo').addEventListener('click', async function () { if (locked() || (!lastAdded && !undoRequest)) return; undoRequest = undoRequest || { operationId: uuid(), expectedVersion: lastAdded.expectedVersion, original: lastAdded.original }; persist(); await edit('Undo', undoRequest); });
  if (checkout) {
    checkout.noValidate = true;
    var optional = document.createElement('details'); optional.id = 'quick-optional'; var optionalTitle = document.createElement('summary'); optionalTitle.textContent = '其他選填資料'; optional.appendChild(optionalTitle); var optionalGrid = document.createElement('div'); optionalGrid.className = 'form-grid'; optional.appendChild(optionalGrid);
    ['Header_PartnerId', 'Header_ExternalNo', 'Header_Note', 'quick-project'].forEach(function (id) { if ((checkout.dataset.requiredHeaders || '').split(',').indexOf(id) >= 0) return; var input = $(id), label = checkout.querySelector('label[for="' + id + '"]'); if (!input || !label) return; var control = label.nextElementSibling; optionalGrid.appendChild(label); optionalGrid.appendChild(control); });
    if (optionalGrid.children.length) checkout.querySelector('.form-grid').after(optional);
    checkout.addEventListener('input', changed); checkout.addEventListener('change', changed);
    $('quick-select-all').addEventListener('change', function () { var yes = this.checked; $('quick-allocations').querySelectorAll('.allocation-select').forEach(function (i) { i.checked = yes; }); });
    $('quick-bulk-apply').addEventListener('click', function () { if (locked()) return; var value = $('quick-bulk-location').value.trim(); if (!value) return;
      $('quick-allocations').querySelectorAll('.quick-allocation-card').forEach(function (c) { if (!c.querySelector('.allocation-select').checked) return; c.querySelectorAll('.allocation-location').forEach(function (i) { if (!i.value.trim()) i.value = value; }); markCard(c); });
    });
    $('quick-save-draft').addEventListener('click', async function () { if (locked()) return; var draft = workState(); var saved = await edit('Draft', { expectedVersion: cart.version, json: JSON.stringify(draft) }); if (saved && !draftStale) { dirty = false; $('quick-draft-status').textContent = '已儲存帳號草稿'; } });
    $('quick-review-draft').addEventListener('click', async function () { if (busy() || checkoutUnknown) return; await refresh(); draftStale = false; this.hidden = true; changed(); sync(); });
    var warehouse = $('quick-checkout-warehouse'), preferredWarehouse = prefs['documents.lastWarehouse'];
    if (!warehouse.value && preferredWarehouse && Array.from(warehouse.options).some(function (o) { return o.value === preferredWarehouse; })) warehouse.value = preferredWarehouse;
  }
  renderAllocations();
  var serverDraft = null; try { serverDraft = cart.draftJson ? JSON.parse(cart.draftJson) : null; } catch (e) {}

  applyDraft(recovered && recovered.draft ? recovered.draft : serverDraft, !(recovered && recovered.draft));
  if ($('quick-optional') && Array.from($('quick-optional').querySelectorAll('input,select')).some(function (i) { return i.value; })) $('quick-optional').open = true;
  if (recovered) { if (recovered.scanLocation) setLocation(recovered.scanLocation); failures = recovered.failures || []; lastAdded = recovered.lastAdded || null; undoRequest = recovered.undoRequest || null; checkoutRequest = recovered.checkoutRequest || null; checkoutUnknown = !!checkoutRequest; renderFailures(); }
  restoring = false;
  if (checkout) { $('quick-checkout-type').dispatchEvent(new Event('change')); checkout.hidden = cart.lines.some(function (l) { return l.trackingMode !== 0 && l.trackingMode !== 'None'; }); }
  initialControls.forEach(function (state) { state.control.disabled = state.disabled; }); panel.dataset.ready = 'true';
  sync();
  if (checkoutUnknown) {
    $('quick-checkout-result').textContent = '已恢復尚待確認的原結帳操作，先查詢結果；尚未完成時可安全重試。';
    json(base + '?handler=CheckoutResult').then(function (r) { if (r) showCompleted(r); }).catch(function (e) { $('quick-checkout-result').textContent = e.message + '，原操作仍保留。'; });
  } else if (recovered && recovered.journal) recovered.journal.forEach(function (j) { if (!failures.some(function (f) { return f.request && f.request.operationId === j.operationId; })) scanner.enqueue(j.value, j.source, { operationId: j.operationId, request: j.request }); });
  persist(); el.input.focus();
})();
