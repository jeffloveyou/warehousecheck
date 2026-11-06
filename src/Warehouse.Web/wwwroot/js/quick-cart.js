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
  var operation = $('quick-checkout-type'), allocationRoot = $('quick-allocations');
  var mobile = window.matchMedia('(max-width: 600px)'), draftConflicts = [];
  var warehouseInput = $('quick-checkout-warehouse'), locationInput = $('quick-location-input');
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
  var locationResolving = false, locationSequence = 0, previousWarehouse = warehouseInput.value;
  var preparing = false, checkoutNoteEdited = false;
  var stateKey = 'quick.work.' + panel.dataset.actorId + '.' + panel.dataset.cartId;
  var reviewedCheckoutKey = stateKey + '.checkout-reviewed';
  var recovered = null, recoveredReviewedCheckout = null;
  try { recovered = JSON.parse(localStorage.getItem(stateKey) || 'null'); } catch (e) { storageFailed = true; }
  try { recoveredReviewedCheckout = JSON.parse(localStorage.getItem(reviewedCheckoutKey) || 'null'); } catch (e) { storageFailed = true; }
  // 舊暫存只有數量，對應當時佇列首筆。不要把它帶到其他物料。
  var recoveredQuantityId = recovered && (recovered.pendingOperationId || (recovered.journal && recovered.journal[0] && recovered.journal[0].operationId));
  var prefsNode = $('quick-preferences'), prefs = prefsNode ? JSON.parse(prefsNode.textContent) : {};
  function fields() {
    var values = {};
    if (checkout) checkout.querySelectorAll('input[id],select[id],textarea[id]').forEach(function (i) {
      if (!i.closest('#quick-allocations') && i.id !== 'quick-select-all' && i.id !== 'quick-bulk-location'
        && (i.id !== 'Header_Note' || checkoutNoteEdited)) values[i.id] = i.value;
    });
    values['quick-checkout-warehouse'] = warehouseInput.value;
    values['quick-checkout-type'] = operation.value;
    return values;
  }
  function allocationState() {
    var rows = {};
    allocationRoot.querySelectorAll('.quick-allocation-card').forEach(function (c) {
      rows[c.dataset.lineId] = Array.from(c.querySelectorAll('.quick-allocation')).map(function (r) { return { location: r.querySelector('.allocation-location').value, quantity: r.querySelector('.allocation-quantity').value, reservationId: r.querySelector('.allocation-reservation') ? r.querySelector('.allocation-reservation').value : '' }; });
    });
    return rows;
  }
  function lineEdits() { var values = {}; document.querySelectorAll('.line-total[data-dirty]').forEach(function (i) { values[i.closest('[data-line-id]').dataset.lineId] = i.value; }); return values; }
  function workState() {
    var sources = Object.fromEntries(cart.lines.map(function (l) { return [l.id, { quantity: l.quantity, locationId: l.locationId, locationCode: l.locationCode }]; }));
    return { lineEdits: lineEdits(), version: cart.version, signature: cart.lines.map(function (l) { return l.id + ':' + l.quantity; }).sort().join('|'), sources: sources, conflicts: draftConflicts, noteEdited: checkoutNoteEdited, fields: fields(), allocations: allocationState(), scanLocation: location, scanLocationText: locationInput.value };
  }
  function persist() {
    if (restoring || completed || !stillOwnsTab()) return;
    try {
      // 舊版重新開頁也不能把新版待確認的內容送到不檢查審核模式的入口。
      var reviewedCheckout = checkoutRequest && typeof checkoutRequest.expectedApprovalEnabled === 'boolean';
      if (reviewedCheckout) localStorage.setItem(reviewedCheckoutKey, JSON.stringify(checkoutRequest));
      else localStorage.removeItem(reviewedCheckoutKey);
      localStorage.setItem(stateKey, JSON.stringify({ draft: workState(), scanLocation: location, failures: failures.map(function (f) { return { raw: f.raw, request: f.request, message: f.message, savedQuantity: f.savedQuantity }; }), checkoutRequest: reviewedCheckout ? null : checkoutRequest, journal: journal, pendingQuantity: pending ? el.qty.value : recoveredQuantityId ? recovered.pendingQuantity : null, pendingOperationId: pending ? pending.request.operationId : recoveredQuantityId, lastAdded: lastAdded, undoRequest: undoRequest, stale: draftStale })); storageFailed = false; }
    catch (e) { storageFailed = true; if ($('quick-draft-status')) $('quick-draft-status').textContent = '此裝置無法保存，離頁前請儲存帳號草稿。'; }
  }
  function changed() { dirty = true; persist(); if ($('quick-draft-status') && !storageFailed && !draftStale) $('quick-draft-status').textContent = '已保存此裝置；尚未儲存至帳號'; if (!restoring) updateAction(); }
  function stillOwnsTab() {
    if (!ownsTab) return false;
    if (fallbackLock) { try { if (localStorage.getItem(lockName) !== lockOwner) { ownsTab = false; return false; } } catch (e) { ownsTab = false; return false; } }
    return true;
  }
  function clearWork() {
    if (!stillOwnsTab()) return;
    try { localStorage.removeItem(stateKey); localStorage.removeItem(reviewedCheckoutKey); } catch (e) { }
  }
  function markCard(card) { card.dataset.edited = '1'; allocationSummary(card); changed(); }
  function allocationSummary(card) {
    var line = cart.lines.find(function (l) { return l.id === card.dataset.lineId; }); if (!line) return;
    var total = 0, locations = [], incomplete = false;
    card.querySelectorAll('.quick-allocation').forEach(function (row) {
      var value = Number(row.querySelector('.allocation-quantity').value);
      var code = row.querySelector('.allocation-location').value.trim();
      if (code && Number.isFinite(value) && value > 0) { total += value; locations.push(code + ' × ' + value); }
      else incomplete = true;
    });
    var summary = card.querySelector('.allocation-summary'); if (summary) { var left = Math.round((Number(line.quantity) - total) * 1000000) / 1000000; summary.textContent = '已分配 ' + total + '，' + (left >= 0 ? '待分配 ' + left : '超出 ' + -left); summary.className = 'allocation-summary ' + (left ? 'bad' : 'good'); }
    card.querySelector('.quick-line-summary').textContent = (locations.join('、') || '未指定儲位') + (incomplete && locations.length ? '、另有未填分配' : '');
    if (incomplete || left || card.querySelector('.line-total[data-dirty]') || card.dataset.conflict) setLineOpen(card, true);
  }
  async function edit(handler, input) {
    if (locked()) return;
    requestInFlight = true; persist(); sync();
    try {
      cart = await post(handler, input);
      if (handler === 'EditNote') checkoutNoteEdited = false;
      lastAdded = null; undoRequest = null;
      if (input.lineId) {
        var card = document.querySelector('#quick-allocations [data-line-id="' + input.lineId + '"]');
        if (card) {
          delete card.querySelector('.line-total').dataset.dirty;
          var line = cart.lines.find(function (l) { return l.id === input.lineId; });
          if (handler === 'EditLine' && line && card.querySelectorAll('.quick-allocation').length === 1) card.querySelector('.allocation-quantity').value = line.quantity;
        }
      }
      renderCart(); changed(); status('已儲存清單修改（尚未入帳）。', 'ok'); return true;
    }
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
  function busy() { return locationResolving || !!pending || requestInFlight || (scanner && scanner.pending() > 0); }
  function locked() { return !ownsTab || busy() || failures.length > 0 || completed || checkoutUnknown || draftStale; }
  function sync() {
    if (restoring) { document.querySelectorAll('main input,main select,main textarea,main button').forEach(function (c) { c.disabled = true; }); return; }
    if ($('quick-undo')) $('quick-undo').disabled = locked() || (!lastAdded && !undoRequest);
    var action = $('quick-action-form');
    if (action) action.querySelectorAll('button').forEach(function (b) { b.disabled = locked(); });
    el.mode.disabled = !!pending || requestInFlight || checkoutUnknown || completed;
    var frozen = !ownsTab || requestInFlight || checkoutUnknown || completed;
    operation.disabled = frozen || busy();
    $('quick-find-item').disabled = frozen || busy();
    el.input.disabled = frozen || locationResolving;
    $('scan-camera').disabled = frozen || !!pending || locationResolving;
    warehouseInput.disabled = frozen || busy() || draftStale || !!cart.warehouseId;
    locationInput.disabled = frozen || busy() || draftStale;
    $('quick-location-apply').disabled = locationInput.disabled;
    $('quick-location-clear').disabled = frozen || busy();
    $('quick-save-draft').disabled = locked(); $('quick-review-draft').disabled = frozen || busy();
    document.querySelectorAll('.quick-line-form input,.quick-line-form button').forEach(function (c) { c.disabled = frozen || busy() || draftStale; });
    var selectedWarehouse = warehouseInput.options[warehouseInput.selectedIndex];
    if ($('quick-checkout-warehouse-label')) $('quick-checkout-warehouse-label').textContent = selectedWarehouse && selectedWarehouse.value ? selectedWarehouse.textContent : '（未指定）';
    $('quick-convert-warehouse').value = warehouseInput.value;
    if (checkout) checkout.querySelectorAll('input,select,textarea,button').forEach(function (c) {
      if (c.id !== 'quick-checkout' && c.id !== 'quick-next') c.disabled = frozen || (c.id === 'quick-recipient' && $('quick-checkout-type').value !== 'Outbound');
    });
    allocationRoot.querySelectorAll('input,button').forEach(function (c) { c.disabled = frozen || busy() || draftStale; });
    $('quick-bulk-apply').disabled = frozen || busy() || draftStale;
    var outbound = operation.value === 'Outbound'; if (checkout) $('quick-precheck').hidden = !outbound;
    allocationRoot.querySelectorAll('.quick-reservation').forEach(function (details) {
        var enabled = checkout && checkout.dataset.reservations === 'true', selected = !!details.parentElement.querySelector('.allocation-reservation').value;
        details.hidden = !selected && (!outbound || !enabled);
        details.querySelector('.reservation-disabled-note').hidden = (enabled && outbound) || !selected;
        details.querySelector('.allocation-reservation-pick').disabled = frozen || !enabled || !outbound;
        if (selected && (!enabled || !outbound)) details.open = true;
    });
    updateAction();
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
  function fail(message, raw, request, existing, savedQuantity) {
    var f = existing || { raw: raw, request: request }; f.message = message; f.running = false;
    if (savedQuantity) f.savedQuantity = savedQuantity;
    if (!existing) failures.push(f);
    renderFailures(); window.WhScan.feedback('error'); status(message, 'bad');
  }
  function renderFailures(skipPersist) {
    var root = $('quick-failures'); if (!root) return;
    root.textContent = '';
    failures.forEach(function (f) {
      var li = document.createElement('li'), text = document.createElement('span'); text.textContent = f.message + ' '; li.appendChild(text);
      var retry = button(f.running ? '處理中' : '重試原操作', function () { f.running = true; renderFailures(); scanner.enqueue(f.raw, 'retry', { retry: f, operationId: f.request ? f.request.operationId : f.savedQuantity ? f.savedQuantity.operationId : uuid(), request: f.request }); }); retry.disabled = f.running || requestInFlight || completed; li.appendChild(retry);
      var skip = button('核對後略過此紀錄', function () { removeFailure(f); }); skip.disabled = f.running || requestInFlight || completed; li.appendChild(skip);
      if (!f.request) li.appendChild(button('搜尋物料／登錄條碼', function () { resolveUnknown(li, f); }));
      root.appendChild(li);
    }); sync(); if (!skipPersist) persist();
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
  function warehouseCode() { var option = warehouseInput.options[warehouseInput.selectedIndex]; return option ? option.dataset.code || '' : ''; }
  function fullLocation(code) { return /^WH:LOC:/i.test(code) ? code : 'WH:LOC:' + warehouseCode() + ':' + code; }
  function setLocation(loc, keepText) {
    location = loc;
    if (!keepText) locationInput.value = loc ? loc.inputCode || loc.code.split(':').pop() : '';
    $('quick-location').textContent = loc ? loc.label : locationInput.value.trim() ? '（輸入尚未確認，請點選或套用）' : '（未指定）';
    $('quick-location-clear').hidden = !loc && !locationInput.value; persist();
  }
  function requireLocationConfirmed() {
    if (locationInput.value.trim() && !location) throw new Error('儲位輸入尚未確認，請先點選或套用儲位；也可清除後先記料。');
  }
  function changeWarehouse(id) {
    warehouseInput.value = id; previousWarehouse = id; locationSequence++; setLocation(null);
    document.querySelectorAll('.allocation-location,.quick-line-location').forEach(function (i) { i.value = ''; });
    document.querySelectorAll('.allocation-reservation').forEach(function (i) { i.value = ''; });
    document.querySelectorAll('.allocation-reservation-label').forEach(function (i) { i.textContent = '不使用預留'; });
    document.querySelectorAll('.allocation-reservation-choices').forEach(function (i) { i.textContent = ''; });
    document.querySelectorAll('.quick-allocation-card').forEach(function (card) { card.dataset.edited = '1'; allocationSummary(card); });
    stockSequence++; if ($('quick-stock')) $('quick-stock').textContent = '';
    changed(); sync();
  }
  function adoptLocation(loc) {
    if (loc.status === 'Inactive') throw new Error('儲位已停用。');
    var option = Array.from(warehouseInput.options).find(function (o) { return o.dataset.code === loc.warehouseCode; });
    if (!option) throw new Error('儲位不在可操作的倉庫，或倉庫已停用。');
    if (cart.warehouseId && cart.warehouseId !== option.value) throw new Error('購物車已鎖定倉庫，不能使用其他倉庫的儲位。');
    if (warehouseInput.value !== option.value) {
      if (restoring) { warehouseInput.value = option.value; previousWarehouse = option.value; }
      else changeWarehouse(option.value);
    }
    setLocation({ code: 'WH:LOC:' + loc.warehouseCode + ':' + loc.code, inputCode: loc.code, warehouseId: option.value, label: loc.warehouseCode + ' 倉 ' + loc.code });
    previousWarehouse = warehouseInput.value; changed(); sync();
  }
  async function applyLocation() {
    if (!stillOwnsTab() || busy() || checkoutUnknown || completed || draftStale) return;
    var text = locationInput.value.trim(), selected = warehouseInput.value, sequence = ++locationSequence, focusAfter = false;
    if (!text) { setLocation(null); changed(); scanner.focus(); return; }
    if (!selected && !/^WH:LOC:/i.test(text)) { status('請先選擇倉庫，再輸入儲位碼；也可以直接掃含倉庫的儲位 QR。', 'bad'); return; }
    locationResolving = true; sync();
    try {
      var result = await json('/api/scan?code=' + encodeURIComponent(fullLocation(text)));
      if (sequence !== locationSequence || selected !== warehouseInput.value || text !== locationInput.value.trim() || !stillOwnsTab()) return;
      if (result.kind !== 'Location') throw new Error(result.message || '找不到這個儲位，請從搜尋結果點選。');
      adoptLocation(result.location); status('接下來加入使用：' + location.label + '；先前紀錄不變。', 'ok'); focusAfter = true;
    } catch (e) { if (sequence === locationSequence) status(e.message, 'bad'); }
    finally { locationResolving = false; sync(); if (focusAfter) scanner.focus(); }
  }
  locationInput.addEventListener('input', function () { locationSequence++; setLocation(null, true); changed(); });
  $('quick-location-apply').addEventListener('click', applyLocation);
  warehouseInput.addEventListener('change', function () {
    if (!stillOwnsTab() || busy() || checkoutUnknown || completed || draftStale || cart.warehouseId) { warehouseInput.value = cart.warehouseId || previousWarehouse; return; }
    changeWarehouse(warehouseInput.value);
    status('已切換倉庫，請重新選擇儲位；先前物料與數量保留，人工分配位置已清空。', 'warn');
    if (checkout) precheck();
  });
  if (window.WhPicker) window.WhPicker.attach(locationInput, {
    load: async function (q) {
      var w = warehouseInput.value; if (!w) return [];
      var result = await json(base + '?handler=Locations&warehouseId=' + encodeURIComponent(w) + '&q=' + encodeURIComponent(q));
      return w === warehouseInput.value ? result.items || [] : [];
    }, render: function (o) { return { main: o.code, sub: o.name || '' }; },
    pick: function (o) {
      if (!stillOwnsTab() || busy() || checkoutUnknown || completed || draftStale || o.warehouseCode !== warehouseCode()) return;
      try { locationSequence++; adoptLocation(o); status('接下來加入使用：' + location.label + '；先前紀錄不變。', 'ok'); scanner.focus(); } catch (e) { status(e.message, 'bad'); }
    }, empty: '請先選倉庫，再輸入儲位碼或名稱搜尋'
  });
  // Picker 先處理方向鍵＋Enter；未反白候選時才將完整儲位碼送解析。
  locationInput.addEventListener('keydown', function (e) { if (!e.isComposing && e.key === 'Enter') { e.preventDefault(); applyLocation(); } });
  function bindLinePickers() {
    if (!window.WhPicker) return;
    document.querySelectorAll('.quick-line-location:not([data-picker])').forEach(function (input) {
      input.dataset.picker = 'true'; window.WhPicker.attach(input, {
        load: async function (q) { var w = warehouseInput.value; if (!w) return []; var r = await json(base + '?handler=Locations&warehouseId=' + encodeURIComponent(w) + '&itemId=' + input.dataset.itemId + '&q=' + encodeURIComponent(q)); return w === warehouseInput.value ? r.items || [] : []; },
        render: function (o) { return { main: o.code, sub: o.name || '' }; },
        pick: function (o) { if (!locked() && o.warehouseCode === warehouseCode()) input.value = o.code; }, empty: '請先選倉庫，再搜尋儲位'
      });
    });
  }
  $('quick-location-clear').addEventListener('click', function () { if (!stillOwnsTab() || busy() || checkoutUnknown || completed || draftStale) return; locationSequence++; setLocation(null); changed(); status('已清除目前儲位，先前紀錄不變。', 'warn'); scanner.focus(); });
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
    var loc = input('locationCode', line.locationCode || ''); loc.placeholder = '輸入或搜尋儲位'; loc.className = 'quick-line-location'; loc.dataset.itemId = line.itemId; loc.style.width = '10em'; loc.setAttribute('aria-label', '第 ' + line.lineNo + ' 行儲位');
    var update = document.createElement('button'); update.type = 'submit'; update.className = 'secondary'; update.textContent = '更新'; form.appendChild(update);
    var del = document.createElement('button'); del.type = 'submit'; del.formAction = base + '?handler=RemoveLine'; del.className = 'secondary'; del.textContent = '刪除'; form.appendChild(del);
    row.cells[5].appendChild(form); return row;
  }
  function renderCart(line) {
    var root = document.querySelector('[data-quick-lines]'); if (!root) return;
    if (!checkoutNoteEdited && $('Header_Note')) $('Header_Note').value = cart.note || '';
    if (cart.warehouseId && warehouseInput.value !== cart.warehouseId) {
      var previousLocationCode = location ? location.code : locationInput.value.trim();
      changeWarehouse(cart.warehouseId);
      if (previousLocationCode) { locationInput.value = previousLocationCode; setLocation(null, true); }
      if ($('quick-allocations') && $('quick-allocations').children.length) requireDraftReview('倉庫已變更，請重新選擇分配儲位。');
    }
    var table = root.querySelector('table');
    if (!table) { root.textContent = ''; var h = document.createElement('h2'); root.appendChild(h); table = document.createElement('table'); table.id = 'quick-line-table'; table.innerHTML = '<thead><tr><th>#</th><th>料號</th><th>品名</th><th>儲位</th><th>數量</th><th>修改</th></tr></thead><tbody></tbody>'; var wrap = document.createElement('div'); wrap.className = 'table-wrap'; wrap.appendChild(table); root.appendChild(wrap); }
    var body = table.querySelector('tbody');
    if (!line) { body.textContent = ''; cart.lines.forEach(function (l) { body.appendChild(rowFor(l)); }); }
    else { var old = body.querySelector('[data-line-id="' + line.id + '"]'); if (old) old.replaceWith(rowFor(line)); else body.appendChild(rowFor(line)); }
    root.querySelector('h2').textContent = '購物車內容（' + cart.lines.length + ' 行' + (cart.warehouseCode ? '，倉庫 ' + cart.warehouseCode : '') + '）';
    root.dataset.version = cart.version;
    root.querySelectorAll('input[name="version"]').forEach(function (v) { v.value = cart.version; });
    bindLinePickers();
    stockSequence++;
    if (checkout && !completed) $('quick-stock').textContent = '清單已變更，請更新出庫檢查；正式結帳仍重新驗證。';
    renderAllocations(line); sync();
  }
  async function refresh() {
    var before = workState();
    cart = await json(base + '?handler=Snapshot'); renderCart();
    if (before.version !== cart.version) applyDraft(before, true);
    sync(); persist();
  }
  async function add(request, label, prior) {
    var active = prior || { raw: request.itemCode, request: request, message: '加入結果待確認，可重試原操作。' };
    active.request = request;
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
      var parts = /^WH:LOC:([^:]+):(.+)$/i.exec(request.locationCode || '');
      var locationLabel = parts ? parts[1] + ' 倉 ' + parts[2] : request.locationCode || '未指定儲位';
      status('已加入清單：' + label + ' × ' + request.quantity + ' ＠ ' + locationLabel + '（尚未入帳）', 'ok'); window.WhScan.feedback('ok');
    } catch (e) { fail((e.definite ? '沒有加入：' : '結果待確認，可重試同一操作：') + label + '。' + e.message, request.itemCode, request, active); }
  }
  async function onScan(value, source, extra) {
    var prior = extra && extra.retry;
    var savedQuantity = prior && prior.savedQuantity;
    if (recovered && extra && recoveredQuantityId === extra.operationId && recovered.pendingQuantity != null) {
      savedQuantity = { operationId: recoveredQuantityId, value: recovered.pendingQuantity };
    }
    if (!ownsTab || completed || checkoutUnknown || requestInFlight || locationResolving) { fail('這車已完成、由另一分頁接手或正在確認操作結果，請先處理。', value, null, prior); return; }
    if (extra && extra.request) { await add(extra.request, extra.request.itemCode, prior); return; }
    if (prior && prior.request) { await add(prior.request, prior.request.itemCode, prior); return; }
    try {
      var body = await json('/api/scan?code=' + encodeURIComponent(value));
      if (body.kind === 'Location') {
        adoptLocation(body.location);
        if (prior) removeFailure(prior); status('目前儲位：' + location.label + '，接著掃物料。', 'warn'); return;
      }
      if (body.kind !== 'Item') throw new Error(body.message || '不是已登錄的物料或儲位 QR，請搜尋核對後登錄。');
      requireLocationConfirmed();
      var item = body.item, label = item.itemCode + ' ' + item.name;
      var request = { itemId: item.id, itemCode: item.itemCode, locationCode: location ? location.code : null, quantity: Number(item.packQuantity), operationId: extra && extra.operationId ? extra.operationId : uuid() };
      if (el.mode.value === 'auto' && !savedQuantity) { await add(request, label, prior); return; }
      await new Promise(function (resolve) {
        qtyKeys = { text: '', last: 0, fast: true };
        pending = { request: request, label: label, resolve: resolve, prior: prior };
        el.qty.value = savedQuantity ? savedQuantity.value : request.quantity;
        if (recoveredQuantityId === request.operationId) recoveredQuantityId = null;
        el.qtyItem.textContent = label + '（單位 ' + item.unitCode + '）'; el.qtyRow.hidden = false;
        status('等待數量確認：' + label, 'warn'); window.WhScan.feedback('warn'); sync(); persist(); el.qty.focus(); el.qty.select();
      });
    } catch (e) { fail(e.message + '（' + value + '）', value, null, prior, savedQuantity); }
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
    onFinished: function (job) { if (recoveredQuantityId === job.extra.operationId) recoveredQuantityId = null; journal = journal.filter(function (j) { return j.operationId !== job.extra.operationId; }); persist(); },
    onReject: function (message) { fail(message, '', null); }, onBusyChange: sync,
    shouldPauseCamera: function () { return !!pending || requestInFlight || locationResolving || checkoutUnknown || completed; },
    focusWarning: $('scan-focus-warning'), refocusButton: $('scan-refocus'), capsIndicator: $('scan-caps'), muteCheckbox: $('scan-mute'), cameraButton: $('scan-camera'), cameraBox: $('scan-camera-box'), cameraVideo: $('scan-video'), cameraState: $('scan-camera-state'), cameraStop: $('scan-camera-stop') });
  window.WhScanBusy = function () { return busy() || failures.length > 0 || checkoutUnknown; };
  document.addEventListener('submit', function (ev) { if (ev.target !== checkout && (locked() || (checkoutRequest && !completed))) { ev.preventDefault(); status('仍有待確認、未完成的掃碼或結帳結果，請先處理。', 'bad'); } }, true);
  window.addEventListener('beforeunload', function (ev) { if (((!preparing && locked()) || (dirty && storageFailed)) && !completed) { ev.preventDefault(); ev.returnValue = ''; } });
  function allocationLayout(card) {
    var rows = Array.from(card.querySelectorAll('.quick-allocation')), details = card.querySelector('.quick-split-details');
    if (!details) return;
    var multi = rows.length > 1;
    var line = cart.lines.find(function (l) { return l.id === card.dataset.lineId; });
    rows.forEach(function (r) {
      if (multi) details.querySelector('.quick-split-rows').appendChild(r); else card.querySelector('.quick-line-editor').insertBefore(r, details);
      r.querySelector('.allocation-quantity-field').hidden = !multi && line && Number(r.querySelector('.allocation-quantity').value) === Number(line.quantity);
      r.querySelector('.allocation-remove').hidden = !multi;
    });
    if (multi) details.open = true;
    details.querySelector('summary').textContent = multi ? '拆分儲位（' + rows.length + ' 筆，合計須等於總量）' : '拆分到多個儲位';
  }
  function allocRow(card, line, loc, quantity, reservationId) {
    var row = document.createElement('div'); row.className = 'quick-allocation';
    var locationInput = document.createElement('input'); locationInput.type = 'text'; locationInput.className = 'allocation-location'; locationInput.value = loc || ''; locationInput.placeholder = '實際儲位'; locationInput.required = true; locationInput.maxLength = 200; locationInput.setAttribute('aria-label', line.itemCode + ' 儲位');
    var qty = document.createElement('input'); qty.type = 'text'; qty.className = 'allocation-quantity'; qty.inputMode = 'decimal'; qty.value = quantity; qty.required = true; qty.setAttribute('aria-label', line.itemCode + ' 分配數量');
    var locationLabel = document.createElement('label'); locationLabel.textContent = '實際儲位'; locationLabel.appendChild(locationInput); row.appendChild(locationLabel);
    var qtyLabel = document.createElement('label'); qtyLabel.className = 'allocation-quantity-field'; qtyLabel.textContent = '分配數量'; qtyLabel.appendChild(qty); row.appendChild(qtyLabel);
    var reservation = document.createElement('input'); reservation.type = 'hidden'; reservation.className = 'allocation-reservation'; reservation.value = reservationId || ''; row.appendChild(reservation);
    var advanced = document.createElement('details'); advanced.className = 'quick-reservation'; advanced.open = !!reservationId;
    var advancedTitle = document.createElement('summary'); advancedTitle.textContent = '使用預留（選填）'; advanced.appendChild(advancedTitle); row.appendChild(advanced);
    var disabledNote = document.createElement('p'); disabledNote.className = 'reservation-disabled-note bad'; disabledNote.hidden = true; disabledNote.textContent = '指定預留已關閉，請清除這筆原選擇後重新核對。清除不會釋放實際預留。'; advanced.appendChild(disabledNote);
    var reserved = document.createElement('span'); reserved.className = 'allocation-reservation-label'; reserved.textContent = reservation.value ? '已指定預留 ' + reservation.value : '不使用預留'; advanced.appendChild(reserved);
    var choices = document.createElement('div'); choices.className = 'allocation-reservation-choices';
    async function loadReservations(page) {
      if (locked()) return;
      if (!checkout || checkout.dataset.reservations !== 'true') { status('指定預留已關閉，請先清除原選擇。', 'warn'); return; }
      if ($('quick-checkout-type').value !== 'Outbound' || !$('quick-checkout-warehouse').value) { status('請先選領料與倉庫。', 'warn'); return; }
      var selectedWarehouse = warehouseInput.value;
      choices.textContent = '查詢預留…';
      try { var result = await json(base + '?handler=Reservations&warehouseId=' + encodeURIComponent(selectedWarehouse) + '&itemCode=' + encodeURIComponent(line.itemCode) + '&p=' + page);
        if (selectedWarehouse !== warehouseInput.value) return;
        choices.textContent = '';
        (result.items || []).forEach(function (r) { choices.appendChild(button('使用預留：剩餘 ' + r.remainingQuantity + ' ' + (r.note || '') + '（' + r.createdByName + '）', function () { if (locked() || selectedWarehouse !== warehouseInput.value) return; reservation.value = r.id; reserved.textContent = '已指定預留，剩餘 ' + r.remainingQuantity + ' ' + (r.note || ''); choices.textContent = ''; markCard(card); })); });
        if (!(result.items || []).length) choices.textContent = '沒有符合的手動預留。';
        if (page > 1) choices.appendChild(button('上一頁預留', function () { loadReservations(page - 1); }));
        if (page < result.totalPages) choices.appendChild(button('下一頁預留', function () { loadReservations(page + 1); }));
      } catch (e) { if (selectedWarehouse === warehouseInput.value) choices.textContent = e.message; }
    }
    var pickReservation = button('選擇既有預留', function () { loadReservations(1); }); pickReservation.classList.add('allocation-reservation-pick'); advanced.appendChild(pickReservation);
    var clearReservation = button('清除預留', function () { reservation.value = ''; reserved.textContent = '不使用預留'; markCard(card); sync(); });
    clearReservation.classList.add('allocation-clear-reservation'); advanced.appendChild(clearReservation); advanced.appendChild(choices);
    var remove = button('移除此分配', function () { if (locked()) return; row.remove(); allocationLayout(card); markCard(card); }); remove.classList.add('allocation-remove'); row.appendChild(remove); card.querySelector('.quick-line-editor').appendChild(row);
    if (window.WhPicker) window.WhPicker.attach(locationInput, { load: async function (q) { var w = warehouseInput.value; if (!w) return []; var r = await json(base + '?handler=Locations&warehouseId=' + encodeURIComponent(w) + '&itemId=' + line.itemId + '&q=' + encodeURIComponent(q)); return w === warehouseInput.value ? r.items || [] : []; }, render: function (o) { return { main: o.code, sub: (o.name || '') + (o.quantity === null ? '' : ' 結存 ' + o.quantity) }; }, pick: function (o) { if (!locked() && o.warehouseCode === warehouseCode()) { locationInput.value = o.code; markCard(card); } }, empty: '請先選倉庫，或輸入儲位碼' });
    row.querySelectorAll('input').forEach(function (i) { i.addEventListener('input', function () { markCard(card); }); });
    allocationLayout(card);
  }
  function isTracked(line) { return line.trackingMode !== 'None' && line.trackingMode !== 0; }
  function setLineOpen(card, open) {
    var expanded = open || !mobile.matches;
    card.querySelector('.quick-line-editor').hidden = !expanded;
    var toggle = card.querySelector('.quick-line-edit');
    toggle.setAttribute('aria-expanded', String(expanded));
    toggle.textContent = expanded ? '收合' : '編輯';
  }
  function renderAllocations(changedLine) {
    var tracked = cart.lines.filter(isTracked), trackedIds = new Set(tracked.map(function (line) { return line.id; }));
    var root = allocationRoot;
    $('quick-lines').hidden = !tracked.length;
    $('quick-note-form').hidden = !checkout || tracked.length ? false : true;
    $('quick-lines').querySelectorAll('tr[data-line-id]').forEach(function (row) { row.hidden = !trackedIds.has(row.dataset.lineId); });
    $('quick-allocation-panel').hidden = cart.lines.length === tracked.length;
    if (!changedLine) Array.from(root.children).forEach(function (c) { if (!cart.lines.some(function (l) { return l.id === c.dataset.lineId; })) c.remove(); });
    (changedLine ? [changedLine] : cart.lines).forEach(function (line) {
      if (isTracked(line)) return;
      var card = root.querySelector('[data-line-id="' + line.id + '"]');
      if (!card) { card = document.createElement('section'); card.className = 'quick-allocation-card'; card.dataset.lineId = line.id; var h = document.createElement('h4'); card.appendChild(h);
        var check = document.createElement('input'); check.type = 'checkbox'; check.className = 'allocation-select'; check.setAttribute('aria-label', '勾選 ' + line.itemCode); card.appendChild(check);
        var brief = document.createElement('p'); brief.className = 'quick-line-summary'; card.appendChild(brief);
        var editor = document.createElement('div'); editor.className = 'quick-line-editor'; editor.id = 'quick-line-editor-' + line.id;
        var toggle = button('編輯', function () { setLineOpen(card, editor.hidden); }); toggle.classList.add('quick-line-edit'); toggle.setAttribute('aria-controls', editor.id);
        card.appendChild(toggle); card.appendChild(editor); setLineOpen(card, false);
        var sum = document.createElement('p'); sum.className = 'allocation-summary'; editor.appendChild(sum);
        var spec = document.createElement('p'); spec.className = 'muted'; spec.textContent = line.specification || ''; editor.appendChild(spec);
        var total = document.createElement('input'); total.type = 'number'; total.step = 'any'; total.min = '0'; total.className = 'line-total'; total.value = line.quantity; total.setAttribute('aria-label', line.itemCode + ' 清單總量'); total.addEventListener('input', function () { total.dataset.dirty = '1'; changed(); }); var totalLabel = document.createElement('label'); totalLabel.className = 'quick-total-label'; totalLabel.textContent = '數量（' + line.unitCode + '）'; totalLabel.appendChild(total); editor.appendChild(totalLabel);
        editor.appendChild(button('儲存總量', function () { var current = cart.lines.find(function (x) { return x.id === line.id; }); edit('EditLine', { lineId: line.id, expectedVersion: cart.version, quantity: Number(total.value), locationCode: current.locationCode && cart.warehouseCode ? 'WH:LOC:' + cart.warehouseCode + ':' + current.locationCode : null }); }));
        editor.appendChild(button('刪除此料', function () { edit('DeleteLine', { lineId: line.id, expectedVersion: cart.version }); }));
        var split = document.createElement('details'); split.className = 'quick-split-details'; split.appendChild(document.createElement('summary'));
        var splitRows = document.createElement('div'); splitRows.className = 'quick-split-rows'; split.appendChild(splitRows);
        split.appendChild(button('＋分到另一儲位', function () { if (locked()) return; card.dataset.edited = '1'; allocRow(card, line, '', ''); markCard(card); })); editor.appendChild(split);
        root.appendChild(card); allocRow(card, line, line.locationCode, line.quantity); }
      else if (!card.dataset.edited && card.querySelectorAll('.quick-allocation').length === 1) { card.querySelector('.allocation-quantity').value = line.quantity; card.querySelector('.allocation-location').value = line.locationCode || ''; }
      if (card.querySelector('.line-total') && !card.querySelector('.line-total').dataset.dirty) card.querySelector('.line-total').value = line.quantity;
      allocationLayout(card); allocationSummary(card);
      card.querySelector('h4').textContent = line.itemCode + ' ' + line.itemName + '｜合計 ' + line.quantity + ' ' + line.unitCode;
    });
    var w = $('quick-checkout-warehouse'); if (cart.warehouseId) { w.value = cart.warehouseId; w.disabled = true; }
    Array.from(operation.options).forEach(function (option) {
      if (option.value === 'Inbound' || option.value === 'Outbound') option.disabled = !checkout || !!tracked.length;
    });
    if (operation.selectedOptions[0] && operation.selectedOptions[0].disabled && !checkoutUnknown) operation.value = '';
    if (checkout) checkout.hidden = !!tracked.length || operation.value.startsWith('Draft');
  }
  mobile.addEventListener('change', function () {
    allocationRoot.querySelectorAll('.quick-allocation-card').forEach(function (card) { setLineOpen(card, false); allocationSummary(card); });
  });
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
  function value(id) { return $(id) ? $(id).value || null : null; }
  function inspectOperation() {
    var type = operation.value, draft = type.startsWith('Draft'), issues = [], allocations = [];
    if (!type) return { issues: issues, allocations: allocations };
    if (!cart.lines.length) issues.push({ target: el.input, text: '請先加入物料' });
    if (!warehouseInput.value && !draft) issues.push({ target: warehouseInput, text: '請選擇倉庫' });
    if (type === 'Outbound' && !value('quick-recipient-id')) issues.push({ target: $('quick-recipient'), text: '請點選領用人' });
    if (!draft && checkout) (checkout.dataset.requiredHeaders || '').split(',').forEach(function (id) {
      var input = $(id);
      if (input && !input.value.trim() && !(id === 'Header_PartnerId' && value('Header_PartnerCode'))) {
        var label = checkout.querySelector('label[for="' + id + '"]');
        issues.push({ target: input, text: '請填寫' + (label ? label.textContent.replace('*', '').trim() : '必填資料') });
      }
    });
    var cards = new Map(Array.from(allocationRoot.children).map(function (card) { return [card.dataset.lineId, card]; }));
    var trackedRows = new Map(Array.from($('quick-lines').querySelectorAll('tr[data-line-id]:not([hidden])')).map(function (row) { return [row.dataset.lineId, row]; }));
    cart.lines.forEach(function (line) {
      var card = cards.get(line.id);
      if (!card) {
        var tracked = trackedRows.get(line.id);
        if (tracked) {
          var qty = tracked.querySelector('[name=quantity]'), loc = tracked.querySelector('[name=locationCode]');
          var original = line.locationCode ? fullLocation(line.locationCode) : '';
          if (Number(qty.value) !== Number(line.quantity) || (loc.value.trim() ? fullLocation(loc.value.trim()) : '') !== original) {
            issues.push({ target: tracked.querySelector('button'), text: line.itemCode + ' 明細修改尚未儲存' });
          }
        }
        allocations.push({ lineId: line.id, locationCode: line.locationCode || '', quantity: line.quantity, reservationId: null });
        return;
      }
      var sum = 0, problem = null;
      var unsaved = card.querySelector('.line-total[data-dirty]');
      if (unsaved && Number(unsaved.value) !== Number(line.quantity)) problem = { target: unsaved, text: '清單總量尚未儲存' };
      var rows = card.querySelectorAll('.quick-allocation');
      rows.forEach(function (row) {
        var amount = row.querySelector('.allocation-quantity'), place = row.querySelector('.allocation-location');
        var q = Number(amount.value), loc = place.value.trim(), reservation = row.querySelector('.allocation-reservation').value || null;
        sum += q;
        if (!problem && (!Number.isFinite(q) || q <= 0 || !/^\d+(?:\.\d+)?$/.test(amount.value.trim())))
          problem = { target: amount, text: '請填寫正數分配量' };
        var decimals = (amount.value.trim().split('.')[1] || '').replace(/0+$/, '').length;
        if (!problem && decimals > line.decimalPlaces)
          problem = { target: amount, text: '分配量最多 ' + line.decimalPlaces + ' 位小數' };
        if (!problem && ((!draft && !loc) || (draft && loc && !warehouseInput.value))) problem = { target: draft ? warehouseInput : place, text: draft ? '已有儲位，請先選擇倉庫' : '缺少實際儲位' };
        if (!problem && draft && reservation) problem = { target: row.querySelector('.allocation-clear-reservation'), text: '轉草稿前請清除指定預留' };
        allocations.push({ lineId: line.id, locationCode: loc, quantity: q, reservationId: draft || type === 'Outbound' ? reservation : null });
      });
      if (!problem && (!rows.length || Math.abs(sum - Number(line.quantity)) > 0.0000001)) {
        problem = { target: card.querySelector('.allocation-quantity') || card.querySelector('.quick-line-edit'), text: '分配合計不符' };
      }
      if (problem) issues.push({ card: card, target: problem.target, text: line.itemCode + ' ' + problem.text });
    });
    if (allocations.length > 500) issues.push({ target: allocationRoot, text: '分配超過 500 筆，請拆成多車' });
    return { issues: issues, allocations: allocations };
  }
  function showIssue(issue) {
    var output = $('quick-checkout-result'); output.textContent = issue.text;
    if (issue.card) { issue.card.classList.add('allocation-error'); setLineOpen(issue.card, true); }
    var target = issue.target || output;
    for (var detail = target.closest('details'); detail; detail = detail.parentElement.closest('details')) detail.open = true;
    target.scrollIntoView({ block: 'center' }); target.focus({ preventScroll: true });
  }
  function updateAction() {
    var main = $('quick-checkout'), type = operation.value;
    var bar = main.closest('.quick-checkout-bar'); bar.hidden = completed || restoring;
    document.body.classList.toggle('quick-has-checkout', !bar.hidden);
    main.disabled = !ownsTab || busy() || completed || (!checkoutUnknown && (draftStale || failures.length > 0));
    var problems = type && !checkoutUnknown && !main.disabled ? inspectOperation().issues.length : 0;
    $('quick-summary').textContent = cart.lines.length + ' 行' + (pending ? ' · 等待數量' : busy() ? ' · 處理中' : checkoutUnknown ? ' · 結帳結果待確認' : failures.length ? ' · 有待處理紀錄' : draftStale ? ' · 請先核對草稿' : problems ? ' · ' + problems + ' 項待補齊' : '');
    main.textContent = checkoutUnknown ? '重試確認原結帳結果' : !type ? '選作業' : problems ? '補齊 ' + problems + ' 項' :
      type === 'DraftTransfer' ? '轉移庫草稿' : type === 'DraftInbound' ? '轉進倉草稿' : type === 'DraftOutbound' ? '轉出倉草稿' :
      checkout && checkout.dataset.approval === 'true' ? '送審（尚未入帳）' : type === 'Inbound' ? '確認入庫' : '確認出庫';
  }
  function prepareConvert(type, allocations) {
    var form = document.createElement('form');
    form.method = 'post'; form.action = $('quick-action-form').action + '?handler=Prepare';
    var fields = { Type: type.slice(5), WarehouseId: warehouseInput.value, ExpectedVersion: cart.version, AllocationJson: JSON.stringify(allocations), __RequestVerificationToken: token };
    var checkoutNote = $('Header_Note');
    if (checkoutNote && checkoutNoteEdited) { fields.HasDocumentNote = true; fields.Note = checkoutNote.value; }
    Object.keys(fields).forEach(function (name) { var input = document.createElement('input'); input.type = 'hidden'; input.name = name; input.value = fields[name]; form.appendChild(input); });
    persist(); preparing = true; requestInFlight = true; sync(); document.body.appendChild(form); form.submit();
  }
  $('quick-checkout').addEventListener('click', function () {
    if (checkoutUnknown) { submitCheckout(); return; }
    if (locked() || !stillOwnsTab()) return;
    var type = operation.value;
    if (!type) { showIssue({ target: operation, text: '請選擇這批作業' }); return; }
    var result = inspectOperation();
    if (result.issues.length) { showIssue(result.issues[0]); return; }
    if (type.startsWith('Draft')) prepareConvert(type, result.allocations);
    else if (checkout && !checkout.hidden) checkout.requestSubmit();
  });
  if (checkout) {
    $('quick-recipient').addEventListener('input', function () { $('quick-recipient-id').value = ''; });
    if (window.WhPicker) window.WhPicker.attach($('quick-recipient'), { load: function (q) { return json(base + '?handler=Recipients&q=' + encodeURIComponent(q)); }, render: function (o) { return { main: o.displayName, sub: o.userName }; }, pick: function (o) { $('quick-recipient').value = o.displayName; $('quick-recipient-id').value = o.id; changed(); }, empty: '沒有符合的啟用帳號' });
    $('quick-precheck').addEventListener('click', precheck);
    checkout.addEventListener('submit', submitCheckout);
  }
  operation.addEventListener('change', function () {
    if (checkout) {
      var type = this.value;
      checkout.hidden = type.startsWith('Draft') || cart.lines.some(isTracked);
      checkout.querySelectorAll('optgroup[data-types]').forEach(function (g) { g.disabled = g.getAttribute('data-types').split(',').indexOf(type) < 0; g.querySelectorAll('option').forEach(function (o) { o.disabled = g.disabled; if (g.disabled && o.selected) o.parentNode.parentNode.value = ''; }); });
      var purpose = $('Header_PurposeId'); if (purpose && !purpose.value) { var preferred = prefs[type === 'Inbound' ? 'quick.lastPurpose.in' : 'quick.lastPurpose.out']; if (preferred && Array.from(purpose.options).some(function (o) { return o.value === preferred && !o.disabled; })) purpose.value = preferred; }
      $('quick-recipient').disabled = type !== 'Outbound'; if (type === 'Outbound') precheck(); else $('quick-stock').textContent = '';
    }
    if (!restoring) changed(); sync();
  });
  async function submitCheckout(ev) {
      if (ev) ev.preventDefault();
      if (!stillOwnsTab() || busy() || completed || (!checkoutUnknown && (failures.length || draftStale))) return;
      var type = operation.value;
      if (!checkoutRequest && (!checkout || checkout.hidden || (type !== 'Inbound' && type !== 'Outbound'))) return;
      var inspection = checkoutRequest ? null : inspectOperation();
      if (inspection && inspection.issues.length) { showIssue(inspection.issues[0]); return; }
      var output = $('quick-checkout-result'), checkoutSent = false;
      requestInFlight = true; sync();
      try {
        if (!checkoutRequest) {
          var warehouse = warehouseInput.value;
          if (type === 'Outbound') await precheck();
          var partnerId = value('Header_PartnerId'), partnerCode = value('Header_PartnerCode');
          if (partnerCode) { var matches = await json(base + '?handler=Partner&code=' + encodeURIComponent(partnerCode)); if (matches.length !== 1) throw new Error('往來對象代碼找不到或不唯一，請改用選單。'); partnerId = matches[0].id; }
          checkoutRequest = { requestId: uuid(), expectedVersion: cart.version, type: type === 'Inbound' ? 1 : 2, warehouseId: warehouse, header: { purposeId: value('Header_PurposeId'), partnerId: partnerId, externalNo: value('Header_ExternalNo'), note: value('Header_Note') }, allocations: inspection.allocations, recipientUserId: type === 'Outbound' ? value('quick-recipient-id') : null, projectReference: value('quick-project'), expectedApprovalEnabled: checkout.dataset.approval === 'true' };
        }
        requestInFlight = true; sync(); output.textContent = '正在確認，請勿重掃或離頁…';
        checkoutSent = true; checkoutUnknown = true; persist();
        var result = await post(typeof checkoutRequest.expectedApprovalEnabled === 'boolean' ? 'CheckoutReviewed' : 'Checkout', checkoutRequest);
        showCompleted(result);
      } catch (e) {
        output.textContent = e.message;
        if (checkoutSent && !e.definite) { checkoutUnknown = true; output.textContent += ' 結果待確認，按相同結帳按鈕安全重試；不要另開一車重送。'; }
        else { checkoutRequest = null; checkoutUnknown = false; if (e.code === 'scan_cart.concurrency_conflict') output.textContent += ' 請重新整理後核對整車。'; }
        output.scrollIntoView({ block: 'center' }); output.focus({ preventScroll: true });
      } finally { requestInFlight = false; sync(); persist(); }
  }
  function showCompleted(result) {
    completed = true; checkoutUnknown = false; dirty = false; clearWork(); el.input.disabled = true;
    var message = result.message || (result.status === 'Posted' ? '已完成過帳' : '單據目前狀態：' + result.status);
    status(message + '，單號 ' + result.docNo, 'ok');
    if ($('scan-camera-stop')) $('scan-camera-stop').click();
    $('quick-actions').hidden = true; if ($('quick-stock')) $('quick-stock').textContent = ''; $('scan-focus-warning').hidden = true;
    $('quick-note-form').parentElement.hidden = true;
    var output = $('quick-checkout-result'); output.textContent = message + '，單號 ' + result.docNo + '。';
    var link = document.createElement('a'); link.href = result.url || '/Documents/Details/' + result.documentId; link.textContent = '查看／列印單據'; output.appendChild(link);
    var next = button('開始下一車', function () { var form = document.createElement('form'); form.method = 'post'; form.action = base + '?handler=Next'; var t = document.createElement('input'); t.type = 'hidden'; t.name = '__RequestVerificationToken'; t.value = token; form.appendChild(t); document.body.appendChild(form); form.submit(); }); next.id = 'quick-next'; output.appendChild(next);
    document.querySelectorAll('.quick-line-form input,.quick-line-form button').forEach(function (c) { c.disabled = true; });
    output.scrollIntoView({ block: 'center' }); sync();
  }
  function requireDraftReview(message) {
    draftStale = true;
    $('quick-review-draft').hidden = false;
    $('quick-draft-status').textContent = message;
  }
  function renderDraftConflicts() {
    var root = $('quick-draft-conflicts'); root.textContent = ''; root.hidden = !draftConflicts.length;
    draftConflicts.forEach(function (conflict) {
      var p = document.createElement('p'); p.textContent = conflict.text; root.appendChild(p);
      var card = Array.from(allocationRoot.children).find(function (row) { return row.dataset.lineId === conflict.id; });
      if (card) { card.dataset.conflict = '1'; card.classList.add('allocation-error'); setLineOpen(card, true); }
    });
  }
  function applyDraft(draft, fromServer) {
    if (!draft) return;
    var savedFields = draft.fields || {};
    var noteInput = $('Header_Note'), savedNote = savedFields.Header_Note;
    var legacyNote = noteInput && draft.noteEdited === undefined && typeof savedNote === 'string' && savedNote !== (cart.note || '');
    checkoutNoteEdited = !!(noteInput && (draft.noteEdited === true || legacyNote));
    if (noteInput) noteInput.value = checkoutNoteEdited ? savedNote || '' : cart.note || '';
    Object.keys(savedFields).forEach(function (id) { if (id !== 'Header_Note' && $(id) && (!cart.warehouseId || id !== 'quick-checkout-warehouse')) {
      if (id !== 'quick-checkout-warehouse' || Array.from(warehouseInput.options).some(function (o) { return o.value === savedFields[id]; })) $(id).value = savedFields[id];
    } });
    previousWarehouse = warehouseInput.value;
    var draftWarehouse = (draft.fields || {})['quick-checkout-warehouse'];
    var hasSavedLocations = Object.values(draft.allocations || {}).some(function (rows) { return Array.isArray(rows) && rows.some(function (row) { return row && (row.location || row.reservationId); }); });
    var warehouseMismatch = hasSavedLocations && draftWarehouse !== warehouseInput.value && !!(draftWarehouse || cart.warehouseId);
    draftConflicts = Array.isArray(draft.conflicts) ? draft.conflicts.slice() : [];
    if (legacyNote && (fromServer || draft.version !== cart.version)) {
      draftConflicts.push({ text: '已帶回舊草稿備註，與目前購物車備註不同。請核對結帳資料或在補齊頁修改。' });
    }
    cart.lines.forEach(function (line) {
      var id = line.id, card = allocationRoot.querySelector('[data-line-id="' + id + '"]');
      var allocations = (draft.allocations || {})[id], previous = (draft.sources || {})[id];
      var different = previous && (Number(previous.quantity) !== Number(line.quantity) || previous.locationId !== line.locationId);
      if (!card || !Array.isArray(allocations) || !allocations.length) {
        if (different) draftConflicts.push({ id: id, text: line.itemCode + '｜目前 ' + (line.locationCode || '未指定') + ' × ' + line.quantity + '｜舊草稿 ' + (previous.locationCode || '未指定') + ' × ' + previous.quantity });
        return;
      }
      var manual = allocations.length !== 1 || (allocations[0].location || '') !== (line.locationCode || '') || !!allocations[0].reservationId || Number(allocations[0].quantity) !== Number(line.quantity);
      var oldUnverified = !draft.sources && (fromServer || draft.version !== cart.version) &&
        manual;
      if (warehouseMismatch || different || oldUnverified || (draft.sources && !previous)) {
        var saved = allocations.map(function (row) { return (row.location || '未指定') + ' × ' + row.quantity; }).join('、');
        draftConflicts.push({ id: id, text: line.itemCode + '｜目前 ' + (line.locationCode || '未指定') + ' × ' + line.quantity + '｜舊草稿 ' + saved });
        allocations = [{ location: line.locationCode, quantity: line.quantity }];
        delete card.dataset.edited;
        var total = card.querySelector('.line-total'); total.value = line.quantity; delete total.dataset.dirty;
      } else {
        if (manual) card.dataset.edited = '1'; else delete card.dataset.edited;
        if (Object.prototype.hasOwnProperty.call(draft.lineEdits || {}, id)) {
          var input = card.querySelector('.line-total'); input.value = draft.lineEdits[id]; input.dataset.dirty = '1';
        }
      }
      card.querySelectorAll('.quick-allocation').forEach(function (row) { row.remove(); });
      allocations.forEach(function (row) { allocRow(card, line, row.location, row.quantity, row.reservationId); });
      allocationSummary(card);
    });
    renderDraftConflicts();
    if (warehouseMismatch) requireDraftReview('草稿的分配儲位屬於舊倉庫，請核對目前清單並重新選擇。');
    else if (draftConflicts.length || (!fromServer && draft.version !== cart.version) || (fromServer && draft.signature !== workState().signature) || (restoring && recovered && recovered.stale)) requireDraftReview('已恢復草稿，但清單有其他修改，請核對數量及儲位。');
  }
  document.addEventListener('submit', async function (ev) {
    var form = ev.target;
    if (!form.matches('.quick-line-form,#quick-note-form') || ev.defaultPrevented) return;
    ev.preventDefault(); var data = new FormData(form), remove = ev.submitter && ev.submitter.formAction.indexOf('RemoveLine') >= 0;
    var code = (data.get('locationCode') || '').trim();
    if (code && !/^WH:LOC:/i.test(code) && !warehouseCode()) { status('請先選倉庫，再修改儲位。', 'bad'); return; }
    await edit(form.id === 'quick-note-form' ? 'EditNote' : remove ? 'DeleteLine' : 'EditLine', { lineId: data.get('lineId') || '00000000-0000-0000-0000-000000000000', expectedVersion: cart.version, quantity: Number(data.get('quantity')), locationCode: code ? fullLocation(code) : null, note: data.get('note') });
  });
  $('quick-search-button').addEventListener('click', async function () {
    if (locked()) return; var area = $('quick-search-results'); area.textContent = '搜尋中…';
    try { var results = await json('/api/search?q=' + encodeURIComponent($('quick-search-query').value.trim()) + '&pageSize=20'); area.textContent = '';
      (results.items || []).filter(function (i) { return i.itemCode; }).forEach(function (i) { var row = document.createElement('p'); row.textContent = i.itemCode + ' ' + i.name + ' ' + (i.specificationSummary || '') + ' ';
        row.appendChild(button('選擇並核對加入', async function () { if (locked()) return; var selected; try { selected = await json('/api/items/' + i.id); if (!selected.itemCode) throw new Error('物料尚未核准。'); } catch (e) { area.textContent = e.message; return; } area.textContent = ''; var label = document.createElement('p'); label.textContent = selected.itemCode + ' ' + selected.name + ' ' + (selected.specification || '') + '（單位 ' + selected.unitCode + '）'; area.appendChild(label);
          var q = document.createElement('input'); q.type = 'number'; q.step = 'any'; q.value = '1'; q.min = '0'; q.setAttribute('aria-label', '搜尋加入數量'); area.appendChild(q);
          area.appendChild(button('確認物料及數量，加入清單', async function () { if (locked()) return; try { requireLocationConfirmed(); } catch (e) { status(e.message, 'bad'); return; } var n = Number(q.value); if (!Number.isFinite(n) || n <= 0) { label.textContent += '；請填正數數量'; return; } var request = { itemId: selected.id, itemCode: selected.itemCode, locationCode: location ? location.code : null, quantity: n, operationId: uuid() }; requestInFlight = true; sync(); await add(request, i.itemCode, null); requestInFlight = false; sync(); }));
        })); area.appendChild(row); }); if (!area.children.length) area.textContent = '沒有符合的已核准物料。';
    } catch (e) { area.textContent = e.message; }
  });
  $('quick-search-query').addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); $('quick-search-button').click(); } });
  $('quick-find-item').addEventListener('click', function () { $('quick-search').open = true; $('quick-search-query').focus(); });
  $('quick-search').addEventListener('toggle', function () { $('quick-find-item').setAttribute('aria-expanded', String(this.open)); });
  $('quick-undo').addEventListener('click', async function () { if (locked() || (!lastAdded && !undoRequest)) return; undoRequest = undoRequest || { operationId: uuid(), expectedVersion: lastAdded.expectedVersion, original: lastAdded.original }; persist(); await edit('Undo', undoRequest); });
  if (checkout) {
    checkout.noValidate = true;
    var optional = document.createElement('details'); optional.id = 'quick-optional'; var optionalTitle = document.createElement('summary'); optionalTitle.textContent = '其他選填資料'; optional.appendChild(optionalTitle); var optionalGrid = document.createElement('div'); optionalGrid.className = 'form-grid'; optional.appendChild(optionalGrid);
    ['Header_PartnerId', 'Header_ExternalNo', 'Header_Note', 'quick-project'].forEach(function (id) { if ((checkout.dataset.requiredHeaders || '').split(',').indexOf(id) >= 0) return; var input = $(id), label = checkout.querySelector('label[for="' + id + '"]'); if (!input || !label) return; var control = label.nextElementSibling; optionalGrid.appendChild(label); optionalGrid.appendChild(control); });
    if (optionalGrid.children.length) checkout.querySelector('.form-grid').after(optional);
    if ($('Header_Note')) $('Header_Note').addEventListener('input', function () { checkoutNoteEdited = true; });
    checkout.addEventListener('input', changed); checkout.addEventListener('change', changed);
  }
  $('quick-lines').addEventListener('input', updateAction);
  document.querySelector('.quick-bulk-details').addEventListener('toggle', function () { $('quick-allocation-panel').classList.toggle('quick-bulk-mode', this.open); });
  $('quick-select-all').addEventListener('change', function () { var yes = this.checked; allocationRoot.querySelectorAll('.allocation-select').forEach(function (i) { i.checked = yes; }); });
  $('quick-bulk-apply').addEventListener('click', function () {
    if (locked()) return;
    var code = $('quick-bulk-location').value.trim(); if (!code) return;
    allocationRoot.querySelectorAll('.quick-allocation-card').forEach(function (card) {
      if (!card.querySelector('.allocation-select').checked) return;
      card.querySelectorAll('.allocation-location').forEach(function (input) { if (!input.value.trim()) input.value = code; });
      card.dataset.edited = '1'; allocationSummary(card);
    });
    changed();
  });
  $('quick-save-draft').addEventListener('click', async function () { if (locked()) return; var draft = workState(); var saved = await edit('Draft', { expectedVersion: cart.version, json: JSON.stringify(draft) }); if (saved && !draftStale) { dirty = false; $('quick-draft-status').textContent = '已儲存帳號草稿'; } });
  $('quick-review-draft').addEventListener('click', async function () {
    if (busy() || checkoutUnknown || !stillOwnsTab()) return;
    var version = cart.version;
    requestInFlight = true; sync();
    try {
      await refresh();
      if (version !== cart.version) { requireDraftReview('核對期間清單已變更，請再核對最新明細。'); return; }
      draftStale = false; draftConflicts = []; this.hidden = true; renderDraftConflicts();
      allocationRoot.querySelectorAll('[data-conflict]').forEach(function (card) { delete card.dataset.conflict; card.classList.remove('allocation-error'); });
      changed();
    } catch (e) { status(e.message, 'bad'); }
    finally { requestInFlight = false; sync(); }
  });
  var preferredWarehouse = prefs['documents.lastWarehouse'];
  if (cart.warehouseId) warehouseInput.value = cart.warehouseId;
  else if (!warehouseInput.value && preferredWarehouse && Array.from(warehouseInput.options).some(function (o) { return o.value === preferredWarehouse; })) warehouseInput.value = preferredWarehouse;
  previousWarehouse = warehouseInput.value;
  bindLinePickers();
  renderAllocations();
  var serverDraft = null; try { serverDraft = cart.draftJson ? JSON.parse(cart.draftJson) : null; } catch (e) {}

  var restoredDraft = recovered && recovered.draft ? recovered.draft : serverDraft;
  applyDraft(restoredDraft, !(recovered && recovered.draft));
  var savedLocation = restoredDraft && Object.prototype.hasOwnProperty.call(restoredDraft, 'scanLocation') ? restoredDraft.scanLocation : recovered && recovered.scanLocation;
  if (savedLocation) {
    try {
      var restoredLocation = await json('/api/scan?code=' + encodeURIComponent(savedLocation.code));
      if (restoredLocation.kind !== 'Location') throw new Error('儲位已不存在或不可用。');
      if (warehouseCode() && warehouseCode() !== restoredLocation.location.warehouseCode) throw new Error('草稿儲位與目前倉庫不同。');
      adoptLocation(restoredLocation.location);
    } catch (e) {
      // 原儲位無法驗證時，不讓待重播的原始掃碼被當成「未指定儲位」。
      locationInput.value = savedLocation.code || (restoredDraft && restoredDraft.scanLocationText) || '';
      setLocation(null, true); status('未恢復目前儲位：' + e.message + '，請重新選擇。', 'warn');
    }
  } else if (restoredDraft && restoredDraft.scanLocationText) { locationInput.value = restoredDraft.scanLocationText; setLocation(null, true); }
  if ($('quick-optional') && Array.from($('quick-optional').querySelectorAll('input,select')).some(function (i) { return i.value; })) $('quick-optional').open = true;
  if (recovered) { failures = recovered.failures || []; lastAdded = recovered.lastAdded || null; undoRequest = recovered.undoRequest || null; }
  checkoutRequest = recoveredReviewedCheckout || (recovered && recovered.checkoutRequest) || null;
  checkoutUnknown = !!checkoutRequest;
  if (operation.selectedOptions[0] && operation.selectedOptions[0].disabled) operation.value = '';
  operation.dispatchEvent(new Event('change'));
  restoring = false;
  $('quick-operation').hidden = false; $('quick-actions').hidden = true; $('quick-find-item').hidden = false;
  initialControls.forEach(function (state) { state.control.disabled = state.disabled; }); panel.dataset.ready = 'true';
  renderFailures(true);
  if (checkoutUnknown) {
    $('quick-checkout-result').textContent = '已恢復尚待確認的原結帳操作，先查詢結果；尚未完成時可安全重試。';
    json(base + '?handler=CheckoutResult').then(function (r) { if (r) showCompleted(r); }).catch(function (e) { $('quick-checkout-result').textContent = e.message + '，原操作仍保留。'; });
  } else if (recovered && recovered.journal) recovered.journal.forEach(function (j) { if (!failures.some(function (f) { return (f.request && f.request.operationId === j.operationId) || (f.savedQuantity && f.savedQuantity.operationId === j.operationId); })) scanner.enqueue(j.value, j.source, { operationId: j.operationId, request: j.request }); });
  persist(); el.input.focus();
})();
