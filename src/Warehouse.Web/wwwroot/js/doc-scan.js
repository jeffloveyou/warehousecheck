// 單據、期初的明細輸入區（M5-03 連續掃碼，M8-03 合併搜尋與選擇清單，docs/04 §6）。不使用框架，相容 Chrome 109。
// 流程（FR-05）：
//  - 進倉、期初：物料 →（批號／序號）→ 儲位 → 數量
//  - 出倉：物料 → 來源儲位（清單先列有庫存的儲位）→（批號／序號，清單先到期先出）→ 數量
//  - 移庫：物料 → 來源儲位 → 目的儲位 →（批號／序號）→ 數量
// 掃碼、手打、選清單都填同一組欄位，順序不拘；「下一步」只是提示。
// 掃到什麼由伺服器判定（只做精確比對）：儲位填儲位欄、物料填物料欄；其他文字在等待批號／序號時填入，
// 否則當成物料搜尋，列出候選讓人點選（模糊結果不會自動成為明細）。
// 加入方式（每台裝置記住）：
//  - 手動（預設）：欄位齊了游標跳到數量（預填包裝數量），確認後按 Enter。
//  - 自動：欄位齊了就用包裝數量加入。
//  - 逐件累加：每掃一次物料就加入（相同的草稿行累加包裝數量）。
//  - 序號物料一律每個序號一行、數量 1，欄位齊了自動加入。
// 加入明細只是草稿，不影響庫存；過帳仍要按頁面上的「確認過帳」。
// 新版單據與期初掃碼以原識別碼及原內容重試。
(function () {
  'use strict';

  var panel = document.getElementById('scan-panel');
  if (!panel || !window.WhScan || !window.fetch) return;
  panel.hidden = false;

  var mode = panel.getAttribute('data-mode');
  var warehouseId = panel.getAttribute('data-warehouse-id');
  var warehouseCode = panel.getAttribute('data-warehouse-code');
  var resolveUrl = panel.getAttribute('data-resolve-url');
  var addUrl = panel.getAttribute('data-add-url');
  var quantityUrl = panel.getAttribute('data-quantity-url');
  var numericToQuantity = panel.getAttribute('data-scan-numeric-to-quantity') === 'true';
  var itemSearchUrl = panel.getAttribute('data-item-search-url');
  var locationsUrl = panel.getAttribute('data-locations-url');
  var lotsUrl = panel.getAttribute('data-lots-url');
  var lotSerialEnabled = panel.getAttribute('data-lot-serial-enabled') !== 'false';
  function $(id) { return document.getElementById(id); }
  var el = {
    input: $('scan-input'), status: $('scan-status'), next: $('scan-next'), hints: $('scan-hints'), log: $('scan-log'),
    location: $('scan-location'), locationInfo: $('scan-location-info'), target: $('scan-target'), targetInfo: $('scan-target-info'),
    targetWarehouse: $('scan-target-warehouse'), item: $('scan-item'), itemInfo: $('scan-item-info'), itemResults: $('scan-item-results'),
    lot: $('scan-lot'), serial: $('scan-serial'), expiry: $('scan-expiry'), reservation: $('scan-reservation'),
    quantity: $('scan-quantity'), add: $('scan-add'), clear: $('scan-clear'), addMode: $('scan-add-mode')
  };
  var stale = $('scan-stale');
  var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
  var item = null;          // 伺服器解析出的物料（追蹤模式、單位、包裝數量）
  var locationId = null;    // 目前來源／入庫儲位的 id（選清單或掃碼時才知道；手打時為 null，批號清單改列整個倉庫）
  var pending = 0;          // 逐件累加：已掃但還沒加入的數量
  var lastLot = null;       // 同一物料連續掃時沿用批號 { itemId, lot }
  var busy = false;
  var manualQuantity = null; // 操作員輸入的數量，第一次辨識物料後綁定該料號
  var requireManualAdd = false;
  var lastEntrySaved = false;
  var prefilledLot = false;
  var prefilledExpiry = false;
  var fieldsItemId = null;  // 已套用欄位的物料，解析物料欄時 item 可能暫時為 null
  var confirmLarge = false; // 數量偏大時要再按一次 Enter
  var uncertainKey = null;  // 舊掃碼入口及期初單的人工核對狀態
  var keyedAdd = /[?&]handler=ScanAddLineOnce(?:&|$)/i.test(addUrl);
  var pendingKey = 'wh.document.scan.pending:' + addUrl;
  var pendingAdd = null;
  if (keyedAdd) {
    try { pendingAdd = JSON.parse(sessionStorage.getItem(pendingKey)); } catch (e) { }
  }
  var retryAdd = document.createElement('button');
  retryAdd.type = 'button';
  retryAdd.className = 'secondary';
  retryAdd.textContent = '重試原加入明細';
  retryAdd.hidden = !pendingAdd;
  el.add.after(retryAdd);

  function rememberAdd(data) {
    pendingAdd = data;
    retryAdd.hidden = !data;
    try {
      if (data) sessionStorage.setItem(pendingKey, JSON.stringify(data));
      else sessionStorage.removeItem(pendingKey);
    } catch (e) { }
  }

  function operationId() {
    var bytes = window.crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    var hex = Array.from(bytes).map(function (value) { return value.toString(16).padStart(2, '0'); }).join('');
    return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
  }

  // ---- 偏好跟著帳號（prefs.js，M8-06）；沒有 prefs.js 時退回這台裝置的 localStorage ----
  var LOCAL_KEYS = { 'scan.addMode': 'wh.scan.addMode', 'scan.hiddenFields': 'wh.scan.hiddenFields' };
  function load(key, fallbackValue) {
    if (window.WhPrefs) return window.WhPrefs.get(key, fallbackValue);
    try { var v = window.localStorage.getItem(LOCAL_KEYS[key]); return v === null ? fallbackValue : v; } catch (e) { return fallbackValue; }
  }
  function save(key, value) {
    if (window.WhPrefs) { window.WhPrefs.set(key, value); return; }
    try { window.localStorage.setItem(LOCAL_KEYS[key], value); } catch (e) { /* 私密模式等：只在這次頁面有效 */ }
  }
  var ADD_MODE_KEY = 'scan.addMode';
  var HIDDEN_KEY = 'scan.hiddenFields';
  el.addMode.value = load(ADD_MODE_KEY, 'manual');
  if (!el.addMode.value) el.addMode.value = 'manual';
  function addMode() { return el.addMode.value; }
  var hiddenFields = (load(HIDDEN_KEY, '') || '').split(',').filter(function (x) { return x; });

  var LABELS = {
    location: mode === 'inbound' ? '選擇或掃描入庫儲位' : mode === 'opening' ? '選擇或掃描儲位' : '選擇或掃描來源儲位',
    target: '選擇或掃描目的儲位',
    item: '掃描物料，或輸入料號、品名後按 Enter 搜尋',
    lot: mode === 'inbound' || mode === 'opening' ? '掃描或輸入批號' : '選擇或掃描批號',
    serial: mode === 'inbound' || mode === 'opening' ? '掃描或輸入序號' : '選擇或掃描序號',
    quantity: '確認數量後按 Enter，或按「加入明細」'
  };
  var TRACKING = { None: '不追蹤批號序號', Lot: '批號管理', Serial: '序號管理' };
  var REASONS = { Stock: '有庫存', Default: '預設', Frequent: '常用' };

  function fmt(n) { return String(Number(n)); }
  function tracking() { return item ? item.trackingMode : 'None'; }
  function withQuery(url, params) {
    var query = Object.keys(params).filter(function (k) { return params[k] !== null && params[k] !== undefined && params[k] !== ''; })
      .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]); }).join('&');
    return url + (query ? (url.indexOf('?') < 0 ? '?' : '&') + query : '');
  }
  async function getJson(url) {
    var res = await fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
    if (!res.ok || (res.headers.get('Content-Type') || '').indexOf('json') < 0) throw new Error('HTTP ' + res.status);
    return res.json();
  }

  // ---- 欄位顯示：全公司關閉的欄位伺服器就沒有輸出；每個人可以再隱藏；料號需要的批號／序號一律顯示 ----
  function required(field) {
    return (field === 'lot' && tracking() === 'Lot') || (field === 'serial' && tracking() === 'Serial');
  }
  function applyFields() {
    var nodes = panel.querySelectorAll('[data-field]');
    for (var i = 0; i < nodes.length; i++) {
      var f = nodes[i].getAttribute('data-field');
      var companyOff = !lotSerialEnabled && (f === 'lot' || f === 'serial');
      nodes[i].hidden = !required(f) && (companyOff || hiddenFields.indexOf(f) >= 0);
    }
  }
  var toggles = panel.querySelectorAll('[data-field-toggle]');
  for (var t = 0; t < toggles.length; t++) {
    (function (box) {
      var f = box.getAttribute('data-field-toggle');
      box.checked = hiddenFields.indexOf(f) < 0;
      box.addEventListener('change', function () {
        hiddenFields = hiddenFields.filter(function (x) { return x !== f; });
        if (!box.checked) hiddenFields.push(f);
        save(HIDDEN_KEY, hiddenFields.join(','));
        applyFields();
      });
    })(toggles[t]);
  }

  // 目前還缺哪一步
  function step() {
    var order = mode === 'outbound' ? ['item', 'location', 'track']
      : mode === 'transfer' ? ['item', 'location', 'target', 'track']
      : ['item', 'track', 'location'];
    for (var i = 0; i < order.length; i++) {
      var s = order[i];
      if (s === 'track') {
        if (tracking() === 'Lot' && !el.lot.value.trim()) return 'lot';
        if (tracking() === 'Serial' && !el.serial.value.trim()) return 'serial';
      } else if (s === 'item') {
        // 以伺服器解析出的物料為準（物料欄改了、還在解析時不算完成，M8-03 審查 4）
        if (!item || item.itemCode !== el.item.value.trim()) return 'item';
      } else if (!el[s].value.trim()) {
        return s;
      }
    }
    return 'quantity';
  }

  function showNext() {
    var s = step();
    el.next.textContent = '下一步：' + LABELS[s] + (addMode() === 'each' ? '（逐件累加中）' : addMode() === 'auto' ? '（齊了自動加入）' : '');
  }

  function status(text, kind, silent) {
    el.status.textContent = text;
    el.status.className = 'scan-status scan-' + kind;
    if (!silent) window.WhScan.feedback(kind);
  }
  function ok(text) { status(text, 'ok'); }
  function fail(text) { status(text, 'bad'); }

  function log(value, text, good) {
    var li = document.createElement('li');
    li.className = good ? 'ok' : 'bad';
    li.textContent = new Date().toLocaleTimeString() + '　' + value + ' → ' + text;
    el.log.insertBefore(li, el.log.firstChild);
    while (el.log.children.length > 20) el.log.removeChild(el.log.lastChild);
  }

  // 回到掃碼框。按鈕的 click 事件裡呼叫時，瀏覽器可能在事件之後才把焦點留在按鈕上，所以下一個 tick 再確認一次
  function focusScan() {
    if (document.activeElement && document.activeElement.hasAttribute('data-line-quantity')) return;
    el.input.focus();
    setTimeout(function () {
      var active = document.activeElement;
      if (!active || active === document.body || active.tagName === 'BUTTON' || active.tagName === 'A') el.input.focus();
    }, 0);
  }

  function focusQuantity() {
    if (document.activeElement && document.activeElement.hasAttribute('data-line-quantity')) return;
    el.quantity.focus();
    el.quantity.select();
  }

  // ---- 儲位 ----
  function locationText(o) {
    return (o.name ? o.name : '') + (o.quantity !== null && o.quantity !== undefined ? '　庫存 ' + fmt(o.quantity) : '')
      + (o.status === 'Inspection' ? '　待驗' : '');
  }
  function setLocation(o) {
    el.location.value = o.code;
    locationId = o.id;
    el.locationInfo.textContent = locationText(o);
  }
  // 目的倉選單跟著實際的目的儲位走：掃到本倉 QR 時選單切回本倉，不會用選單上的別倉去找同名儲位（M8-03 審查 2）
  function setTarget(o, code) {
    if (el.targetWarehouse && o.warehouseCode) {
      for (var i = 0; i < el.targetWarehouse.options.length; i++) {
        if (el.targetWarehouse.options[i].value === o.warehouseCode) el.targetWarehouse.value = o.warehouseCode;
      }
    }
    el.target.value = code;
    if (el.targetInfo) el.targetInfo.textContent = (o.warehouseCode && o.warehouseCode !== warehouseCode ? o.warehouseCode + ' 倉　' : '') + (o.name || '');
  }

  // 結存、建議儲位做成可點的 chip（用 textContent 建立，不插入 HTML）
  function chip(text, onClick) {
    var a = document.createElement('a');
    a.href = '#';
    a.className = 'chip';
    a.textContent = text;
    a.addEventListener('click', function (ev) { ev.preventDefault(); onClick(); afterChange(false, true); });
    return a;
  }

  function showHints(extra) {
    el.hints.textContent = '';
    if (!extra) return;
    if (extra.suggestions && extra.suggestions.length) {
      el.hints.appendChild(document.createTextNode('建議儲位：'));
      extra.suggestions.forEach(function (s) {
        var reason = REASONS[s.reason] || '有庫存';
        el.hints.appendChild(chip(s.location + '（' + reason + (s.quantity > 0 ? '，現有 ' + fmt(s.quantity) : '') + '）', function () {
          el.location.value = s.location;
          locationId = null;
          el.locationInfo.textContent = '';
        }));
      });
    }
    if (extra.balances) {
      if (!extra.balances.length) {
        el.hints.appendChild(document.createTextNode('本倉沒有這個料號的結存。'));
      } else {
        el.hints.appendChild(document.createTextNode('本倉結存（點選帶入；更多請在儲位、批號欄選擇）：'));
        extra.balances.forEach(function (b) {
          var text = b.location + (b.lot ? ' 批 ' + b.lot : '') + (b.serial ? ' 序 ' + b.serial : '') + '：' + fmt(b.quantity)
            + (b.available ? '' : '（待驗儲位）');
          el.hints.appendChild(chip(text, function () {
            el.location.value = b.location;
            locationId = null;
            el.locationInfo.textContent = '';
            el.lot.value = b.lot || '';
            el.serial.value = b.serial || '';
            if (b.serial) el.quantity.value = '1';
          }));
        });
        if (extra.balanceTotal > extra.balances.length) {
          el.hints.appendChild(document.createTextNode('（共 ' + extra.balanceTotal + ' 筆，只列前 ' + extra.balances.length + ' 筆）'));
        }
      }
    }
    if (el.reservation) {
      el.reservation.length = 1;
      (extra.reservations || []).forEach(function (r) {
        var option = document.createElement('option');
        option.value = r.id;
        option.textContent = '剩餘 ' + fmt(r.remaining) + '，' + r.by + '，' + (r.expiresAt ? '到期 ' + r.expiresAt : '不到期');
        el.reservation.appendChild(option);
      });
    }
  }

  function hideResults() {
    el.itemResults.hidden = true;
    el.itemResults.textContent = '';
  }
    // 點相機按鈕會讓輸入欄失焦，所以另外保存最後聚焦的欄位。
    var lastCameraInputId = el.input.id;

    panel.addEventListener('focusin', function (ev) {
        var target = ev.target;

        if (!target || target.tagName !== 'INPUT') return;

        // 只記錄可輸入資料的欄位，不記錄靜音等勾選框。
        if (['text', 'number', 'date', 'search', 'tel', 'email', 'url']
            .indexOf(target.type) < 0) return;

        if (target.id) lastCameraInputId = target.id;
    });

    // 搜尋結果只有一份，依照指定欄位搬動。
    // 可搬動候選清單或掃碼提示；未指定元素時，搬動候選清單。
    function placeResults(targetId, resultElement) {
        var result = resultElement || el.itemResults;
        var input = document.getElementById(targetId);

        // 欄位不存在或已隱藏，就退回上方掃碼框。
        if (!input || !panel.contains(input) ||
            input.getClientRects().length === 0) {
            input = el.input;
        }

        var anchor = input === el.input
            ? panel.querySelector('.scan-bar')
            : input;

        if (!anchor) return;

        // 避免批號、序號、數量等 grid 欄位的排列被打亂。
        result.style.gridColumn =
            anchor.parentElement.classList.contains('scan-fields')
                ? '1 / -1'
                : '';

        if (anchor.nextElementSibling !== result) {
            anchor.after(result);
        }
    }
  function setItem(body) {
    var it = body.item;
    lastItemQuery = null;
    var same = fieldsItemId === it.id;
    var keepPrefill = !!manualQuantity && !manualQuantity.itemId;
    lastEntrySaved = false;
    if (manualQuantity && manualQuantity.itemId && manualQuantity.itemId !== it.id) manualQuantity = null;
    fieldsItemId = it.id;
    item = it;
    el.item.value = it.itemCode;
    hideResults();
    if (!same) {
      el.serial.value = '';
      if (!(keepPrefill && prefilledLot && it.trackingMode === 'Lot')) {
        el.lot.value = lastLot && lastLot.itemId === it.id ? lastLot.lot : '';
      }
      if (el.expiry && !(keepPrefill && prefilledExpiry && it.trackingMode === 'Lot')) el.expiry.value = '';
      pending = 0;
      showHints(body.extra);
    }
    prefilledLot = false;
    prefilledExpiry = false;
    el.itemInfo.textContent = it.name + (it.specification ? '　' + it.specification : '') + '（單位 ' + it.unitCode + '，'
      + (TRACKING[it.trackingMode] || it.trackingMode)
      + (it.matchedBy === 'Barcode' && it.packQuantity !== 1 ? '，此條碼每掃一次 ' + fmt(it.packQuantity) + ' ' + it.unitCode : '')
      + (it.isActive ? '' : '，已停用') + '）';
    if (it.trackingMode === 'Serial') {
      manualQuantity = null;
      el.quantity.value = '1';
    } else if (manualQuantity) {
      manualQuantity.itemId = it.id;
      manualQuantity.auto = true;
      pending = 0;
    } else if (addMode() === 'each') {
      pending += it.packQuantity;
      el.quantity.value = fmt(pending);
    } else {
      el.quantity.value = fmt(it.packQuantity);
    }
    applyFields();
  }

  function applyLocation(body) {
    var loc = body.location;
    if (loc.status === 'Inactive') {
      fail('儲位 ' + loc.code + ' 已停用，不能使用。');
      return false;
    }
    var label = loc.warehouseId === warehouseId ? loc.code : 'WH:LOC:' + loc.warehouseCode + ':' + loc.code;
    if (mode === 'transfer') {
      if (!el.location.value.trim() || el.target.value.trim()) {
        var cleared = el.target.value.trim() !== '';
        setLocation(loc);
        el.target.value = '';
        if (el.targetInfo) el.targetInfo.textContent = '';
        ok('來源儲位 ' + loc.code + (cleared ? '（已清除目的儲位，請再掃目的）' : ''));
      } else if (label === el.location.value.trim()) {
        fail('目的儲位不可與來源相同：' + loc.code);
        return false;
      } else {
        setTarget(loc, label);
        ok('目的儲位 ' + (loc.warehouseId === warehouseId ? loc.code : loc.warehouseCode + ' 倉 ' + loc.code));
      }
    } else {
      setLocation(loc);
      ok('儲位 ' + loc.code + (loc.name ? ' ' + loc.name : '') + (loc.status === 'Inspection' ? '（待驗）' : ''));
    }
    return true;
  }

  function applyItem(body) {
    var it = body.item;
    if (!it.isActive && (mode === 'inbound' || mode === 'opening')) {
      fail('物料 ' + it.itemCode + ' 已停用，不能' + (mode === 'opening' ? '建帳' : '進倉') + '。');
      return false;
    }
    setItem(body);
    ok('物料 ' + it.itemCode + ' ' + it.name);
    return true;
  }

  // ---- 物料搜尋（按 Enter 才查；候選由人點選，再以精確解析帶入） ----
    async function searchItems(text, resultTargetId) {
    if (!itemSearchUrl) return false;
    var body;
    try {
      body = await getJson(withQuery(itemSearchUrl, { q: text }));
    } catch (e) {
      fail('搜尋失敗，請確認連線後再試。');
      return false;
    }
        placeResults(resultTargetId);
        el.itemResults.textContent = '';
        el.itemResults.hidden = false;
    if (!body.items.length) {
      el.itemResults.appendChild(document.createTextNode('找不到符合「' + text + '」的有效物料。'));
      fail('「' + text + '」不是已登錄的物料或儲位，也搜尋不到。若是包裝上的新條碼，請到物料頁登錄為原廠條碼。');
      return false;
    }
    var title = document.createElement('div');
    title.className = 'muted';
    title.textContent = '搜尋「' + text + '」：' + (body.total > body.items.length ? '超過 ' + body.items.length + ' 筆，只列前 ' + body.items.length + ' 筆，' : '')
      + '請確認料號與規格後點選。';
    el.itemResults.appendChild(title);
    var list = document.createElement('ul');
    list.className = 'scan-result-list';
    body.items.forEach(function (h) {
      var li = document.createElement('li');
      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'link';
      button.textContent = h.itemCode + '　' + h.name;
      button.addEventListener('click', function () {
        hideResults();
        scanner.enqueue(h.itemCode, 'manual');
      });
      li.appendChild(button);
      var sub = [h.brand, h.partNo, h.specification, '單位 ' + h.unitCode].filter(function (x) { return x; }).join('　');
      if (sub) {
        var span = document.createElement('span');
        span.className = 'muted';
        span.textContent = '　' + sub;
        li.appendChild(span);
      }
      list.appendChild(li);
    });
    el.itemResults.appendChild(list);
    status('找到 ' + body.items.length + ' 筆候選，請點選物料。', 'warn');
    return true;
  }

  function applyText(body, s, source) {
    if (s === 'lot' && source !== 'item-field') {
      el.lot.value = body.value;
      ok('批號 ' + body.value);
      return true;
    }
    if (s === 'serial' && source !== 'item-field') {
      el.serial.value = body.value;
      ok('序號 ' + body.value);
      return true;
    }
    return false;
  }

  // 一筆掃碼或輸入：伺服器解析 → 依類型套用 → 欄位齊了就跳數量或自動加入
    async function onScan(value, source, extra) {
        if (!quantityChoice.hidden) { fail('請先確認數量欄收到的內容，再掃下一筆。'); return; }
        if (pendingAdd && !busy) { fail('請先重試原加入明細，再掃下一筆。這次掃碼尚未加入。'); return; }
        if (source === 'camera') {
            var statusTargetId =
                (extra && extra.resultTargetId) || el.input.id;

            placeResults(statusTargetId, el.status);
        }

        var s = step();
    var url = withQuery(resolveUrl, {
      code: value,
      target: mode === 'transfer' && el.location.value.trim() && !el.target.value.trim() ? 'true' : null
    });
    var res;
    var body = null;
    try {
      res = await fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
      if (res.ok && (res.headers.get('Content-Type') || '').indexOf('json') >= 0) body = await res.json();
    } catch (e) {
      fail('網路錯誤，沒有處理「' + value + '」，請確認連線後重掃。');
      log(value, '網路錯誤', false);
      return;
    }
    if (!body) {
      fail(res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '查詢失敗（HTTP ' + res.status + '）：' + value);
      log(value, '查詢失敗', false);
      return;
    }
    var applied = false;
    var wasItem = false;
    if (body.kind === 'Location' && source !== 'item-field') {
      applied = applyLocation(body);
    } else if (body.kind === 'Item') {
      applied = applyItem(body);
      wasItem = applied;
    } else if (body.kind === 'Text' || body.kind === 'Location') {
      applied = applyText(body, s, source);
      if (!applied) {
        // 不是已登錄的料號、條碼或儲位：當成物料搜尋，列出候選讓人點選
          var resultTargetId = source === 'camera'
              ? ((extra && extra.resultTargetId) || el.input.id)
              : (source === 'item-field' ? el.item.id : el.input.id);

          var found = await searchItems(value, resultTargetId);
        log(value, found ? '列出搜尋結果' : el.status.textContent, found);
        return;
      }
    } else {
      fail(body.message || '無法辨識：' + value);
    }
    log(value, el.status.textContent, applied);
    if (applied) await afterChange(wasItem);
  }

  // 使用者正在輸入區的其他欄位（例如物料欄按 Tab 到儲位欄打字）：解析晚到時不搶焦點（M8-03 審查 7）
  function editingOtherField() {
    var a = document.activeElement;
    return !!a && a !== el.input && panel.contains(a) && (a.tagName === 'INPUT' || a.tagName === 'SELECT');
  }

  // userAction：使用者剛在某個欄位操作（選清單、按 Enter、點 chip），可以移動焦點；解析晚到時（false）不搶焦點
  async function afterChange(wasItem, userAction) {
    var mayMove = userAction || !editingOtherField();
    showNext();
    if (step() !== 'quantity') {
      if (mayMove) focusScan();
      return;
    }
    if (requireManualAdd) {
      if (mayMove) focusQuantity();
      return;
    }
    if (tracking() === 'Serial') {
      el.quantity.value = '1';
      await submit(false);
    } else if (manualQuantity && manualQuantity.auto) {
      await submit(false);
    } else if (addMode() === 'each') {
      if (pending > 0) await submit(true);
      else focusScan();
    } else if (addMode() === 'auto') {
      if (!(Number(el.quantity.value) > 0) && item) el.quantity.value = fmt(item.packQuantity);
      await submit(false);
    } else if (mayMove || document.activeElement === el.item) {
      focusQuantity();
    }
  }

  function payload() {
    var targetWarehouse = el.targetWarehouse && !el.targetWarehouse.hidden && el.targetWarehouse.value !== warehouseCode
      ? el.targetWarehouse.value : null;
    return {
      locationCode: el.location.value.trim(),
      targetLocationCode: el.target ? el.target.value.trim() : null,
      targetWarehouseCode: el.target && el.target.value.trim().indexOf('WH:LOC:') === 0 ? null : targetWarehouse,
      itemCode: item ? item.itemCode : el.item.value.trim(),
      lotNo: el.lot.value.trim() || null,
      serialNo: el.serial.value.trim() || null,
      expiryDate: el.expiry && !el.expiry.hidden && el.expiry.value ? el.expiry.value : null,
      reservationId: el.reservation && !el.reservation.hidden && el.reservation.value ? el.reservation.value : null,
      quantity: Number(el.quantity.value),
      merge: true // 相同的行一律累加（使用者 2026-11-02：像買菜一樣，掃到或輸入相同的就加數量）
    };
  }

  // 明細的累加鍵（同伺服器 SameKey：儲位、目的、物料、批號、序號、預留；大小寫不分）
  function lineKey(data) {
    return [data.locationCode, data.targetLocationCode, data.itemCode, data.lotNo, data.serialNo, data.reservationId]
      .map(function (v) { return (v || '').toUpperCase(); }).join('|');
  }

  // 用重繪的明細表換掉舊的。加明細不會改變單據版本（只寫明細表），所以不動頁面上的 Version；
  // 版本不同代表別人改過表頭或狀態，只提示重新整理，不自動套用新版本（否則表頭表單會無聲覆蓋別人的修改，PR 2 審查 M1）
  function replaceLines(html) {
    var old = document.querySelector('[data-scan-lines]');
    var template = document.createElement('template');
    template.innerHTML = html.trim();
    var fresh = template.content.querySelector('[data-scan-lines]');
    if (!old || !fresh) return false;
    var active = document.activeElement;
    var inside = old.contains(active);
    var focusedLine = inside && active.getAttribute('data-line-quantity');
    var selection = focusedLine ? [active.selectionStart, active.selectionEnd] : null;
    var row = inside && active.closest('[data-line-id]');
    var rowIndex = row ? Array.from(old.querySelectorAll('[data-line-id]')).indexOf(row) : -1;
    var checked = new Set(Array.from(old.querySelectorAll('.line-select:checked'), function (box) { return box.value; }));
    fresh.querySelectorAll('.line-select').forEach(function (box) { box.checked = checked.has(box.value); });
    var oldTable = old.querySelector('.table-wrap');
    var left = oldTable ? oldTable.scrollLeft : 0;
    old.parentNode.replaceChild(fresh, old);
    bindQuantityEditors(fresh);
    if (window.WhTableCards) window.WhTableCards(fresh);
    if (window.WhLines) window.WhLines.bind(fresh);
    var table = fresh.querySelector('.table-wrap');
    if (table) table.scrollLeft = left;
    if (inside) {
      var replacement = active.id ? $(active.id) : null;
      if (row) {
        var rows = fresh.querySelectorAll('[data-line-id]');
        var sameRow = fresh.querySelector('[data-line-id="' + row.dataset.lineId + '"]');
        var nearest = sameRow || rows[Math.min(rowIndex, rows.length - 1)];
        if (focusedLine) replacement = quantityEditor(focusedLine);
        if (!replacement && nearest) replacement = nearest.querySelector(active.matches('.line-select') ? '.line-select' : '[data-remove-lines] button');
      }
      if (replacement && replacement.disabled) replacement = null;
      (replacement || fresh.querySelector('h2')).focus({ preventScroll: true });
      if (focusedLine && replacement && replacement.hasAttribute('data-line-quantity')) {
        replacement.setSelectionRange(selection[0], selection[1]);
      }
    }
    var version = fresh.getAttribute('data-version');
    var pageVersion = document.querySelector('input[name="Version"]');
    if (version && pageVersion && pageVersion.value !== version && stale) {
      stale.hidden = false;
    }
    return true;
  }

  async function reloadLines() {
    try {
      var res = await fetch(window.location.pathname + window.location.search, { credentials: 'same-origin', cache: 'no-store' });
      return res.ok && !res.redirected && replaceLines(await res.text());
    } catch (e) { return false; }
  }

  // 一次只送一筆：手動按「加入明細」與掃碼觸發的送出若同時發生，後到的等前一筆結束再送（PR 2 審查 S1）
  // epoch：遇到「規則錯誤」或「不確定是否已寫入」時遞增；之前排隊的送出一律放棄，
  // 否則排在後面、內容相同的那一筆會再送一次（等於自動重送，M8-03 審查 1）
  var inflight = Promise.resolve();
  var epoch = 0;
  var adding = 0;
  function submit(each, manual) {
    if (quantityNeedsReview()) { fail('請先確認數量欄收到的內容。'); return; }
    if (requireManualAdd && !manual) return;
    var mine = epoch;
    adding++;
    var run = inflight.then(function () { return mine === epoch ? submitNow(each, manual) : undefined; })
      .finally(function () { adding--; });
    inflight = run.catch(function () { });
    return run;
  }

  async function submitNow(each, manual) {
    if (quantityNeedsReview()) { fail('請先確認數量欄收到的內容。'); return; }
    if (requireManualAdd && !manual) return;
    if (pendingAdd) { fail('請先重試原加入明細，再加入下一筆。'); return; }
    var s = step();
    if (s !== 'quantity') {
      fail('還沒完成：' + LABELS[s]);
      focusScan();
      return;
    }
    var data = payload();
    if (each) data.quantity = pending;
    if (!(data.quantity > 0)) {
      if (!each) {
        fail('數量必須大於 0。');
        focusQuantity();
      }
      return;
    }
    if (!each && !/^\d+(\.\d+)?$/.test(el.quantity.value.trim())) {
      fail('數量請輸入數字（例 3 或 2.5）。');
      focusQuantity();
      return;
    }
    if (!each && !confirmLarge && data.quantity > 1000 * Math.max(1, item ? item.packQuantity : 1)) {
      confirmLarge = true;
      status('數量 ' + fmt(data.quantity) + ' 偏大，請確認後再按一次 Enter（掃錯欄位時請修改）。', 'warn');
      focusQuantity();
      return;
    }
    var key = lineKey(data);
    if (!each && uncertainKey !== null && uncertainKey === key) {
      uncertainKey = null;
      status('上一筆同樣的明細不確定是否已加入；相同的明細會累加，可能變成加兩次。請先核對明細表，確定要再加 ' + fmt(data.quantity) + ' 就再按一次 Enter。', 'warn');
      focusQuantity();
      return;
    }
    if (!navigator.onLine) {
      fail('目前離線，送出不會成功；恢復連線後再按 Enter。');
      return;
    }
    confirmLarge = false;
    if (keyedAdd) {
      data.operationId = operationId();
      rememberAdd(data);
    }
    if (each) pending -= data.quantity; // 送出中又掃到的件數留在 pending，下一次再送
    await sendLine(data, each, key, false);
  }

  async function sendLine(data, each, key, replaying) {
    var origin = document.activeElement;
    busy = true;
    el.add.disabled = true;
    retryAdd.disabled = true;
    try {
      var res = await fetch(addUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/html, application/json',
          'RequestVerificationToken': tokenInput ? tokenInput.value : ''
        },
        body: JSON.stringify(data)
      });
      var type = res.headers.get('Content-Type') || '';
      if (res.ok && (!keyedAdd || res.headers.has('X-Line-Id')) && type.indexOf('text/html') >= 0 && replaceLines(await res.text())) {
        if (keyedAdd) rememberAdd(null);
        // 相同的行會累加（標頭 X-Line-*，ScanLineHeaders.cs）：講清楚加到第幾行、現在共幾個；
        // 有效期限沿用該行時用警告色提醒這次填的值沒採用（預留不同會是另一行）
        var lineNo = res.headers.get('X-Line-No');
        var lineQty = res.headers.get('X-Line-Quantity');
        var ignored = (res.headers.get('X-Line-Ignored') || '').split(',').filter(Boolean)
          .map(function (k) { return k === 'expiry' ? '有效期限' : '預留'; });
        if (replaying) {
          ok('已確認原加入操作，明細表已更新。');
          log(data.itemCode, '已確認原加入操作', true);
        } else if (res.headers.get('X-Line-Merged') === '1' && lineNo && lineQty) {
          var text = '已累加：' + data.itemCode + ' + ' + fmt(data.quantity) + '，第 ' + lineNo + ' 行現在共 ' + fmt(lineQty);
          if (ignored.length) {
            status(text + '。這次填的' + ignored.join('、') + '與該行不同，已沿用該行的值。', 'warn');
          } else {
            ok(text);
          }
          log(data.itemCode, '累加 ' + fmt(data.quantity) + '，共 ' + fmt(lineQty), true);
        } else {
          ok('已加入：' + data.itemCode + ' × ' + fmt(data.quantity) + '，儲位 ' + data.locationCode);
          log(data.itemCode, '加入 ' + fmt(data.quantity), true);
        }
        afterSubmit();
      } else if (res.status === 400 && type.indexOf('json') >= 0) {
        // 400＝規則錯誤（伺服器在寫入前拒絕），這一筆沒有加入
        if (keyedAdd) rememberAdd(null);
        epoch++;
        fail((await res.json()).message || '加入失敗');
        log(data.itemCode, '加入失敗', false);
        if (each) pending = 0;
      } else {
        // 其他錯誤（逾時登入、500 等）不能確定是否已寫入：重新讀取明細表讓人核對，不自動重送（PR 2 審查 S2）
        epoch++;
        fail(keyedAdd ? '加入結果待確認。請重試原加入明細，系統會使用同一筆請求。'
          : res.redirected ? '登入已逾時，請重新整理頁面並登入。'
          : '不確定這一行是否已加入（HTTP ' + res.status + '），已重新讀取明細表，請核對後再繼續。');
        log(data.itemCode, '不確定是否已加入', false);
        if (!keyedAdd) uncertainKey = key;
        if (each) pending = 0;
        await reloadLines();
      }
    } catch (e) {
      epoch++;
      fail(keyedAdd ? '網路中斷，請重試原加入明細，系統會使用同一筆請求。'
        : '網路中斷：不確定這一行是否已加入，已重新讀取明細表，請核對後再繼續（不會自動重送）。');
      log(data.itemCode, '不確定是否已加入', false);
      if (!keyedAdd) uncertainKey = key;
      if (each) pending = 0;
      await reloadLines();
    } finally {
      busy = false;
      el.add.disabled = false;
      retryAdd.disabled = false;
      showNext();
      var active = document.activeElement;
      if (active === origin || active === document.body && (origin === el.add || origin === retryAdd)) {
        if (!active.hasAttribute('data-line-quantity')) el.input.focus();
      }
    }
  }

  // 加入後：保留儲位；物料清空等下一個（逐件累加保留物料與批號，連續掃同一物料）
  function afterSubmit() {
    lastItemQuery = null;
    quantityKeys = null;
    confirmedQuantity = null;
    requireManualAdd = false;
    if (item) lastLot = { itemId: item.id, lot: el.lot.value.trim() };
    el.serial.value = '';
    var usedManualQuantity = !!manualQuantity;
    lastEntrySaved = true;
    prefilledLot = false;
    prefilledExpiry = false;
    manualQuantity = null;
    if (usedManualQuantity) el.quantity.value = '';
    if (addMode() !== 'each' || tracking() === 'Serial') {
      if (tracking() !== 'Serial') {
        item = null;
        fieldsItemId = null;
        el.item.value = '';
        el.itemInfo.textContent = '';
        el.lot.value = '';
        el.hints.textContent = '';
        el.quantity.value = '';
        if (el.expiry) el.expiry.value = '';
        if (el.reservation) el.reservation.length = 1;
        hideResults();
        applyFields();
      }
    }
  }

  function clearLine() {
    uncertainKey = null;
    item = null;
    fieldsItemId = null;
    lastEntrySaved = false;
    prefilledLot = false;
    prefilledExpiry = false;
    manualQuantity = null;
    quantityKeys = null;
    confirmedQuantity = null;
    requireManualAdd = false;
    clearQuantityChoice();
    lastItemQuery = null;
    pending = 0;
    ['item', 'lot', 'serial', 'quantity'].forEach(function (k) { el[k].value = ''; });
    if (el.expiry) el.expiry.value = '';
    if (el.reservation) el.reservation.length = 1;
    el.itemInfo.textContent = '';
    el.hints.textContent = '';
    hideResults();
    applyFields();
    status('已清除（儲位保留）。', 'warn');
    showNext();
    focusScan();
  }

    var scanner = window.WhScan.attach({
        input: el.input,
        container: panel,
        getCameraResultTarget: function () {
            return lastCameraInputId;
        },
        onScan: onScan,
    onReject: function (message) { fail(message); log('', message, false); focusScan(); },
    focusWarning: $('scan-focus-warning'),
    refocusButton: $('scan-refocus'),
    capsIndicator: $('scan-caps'),
    muteCheckbox: $('scan-mute'),
    cameraButton: $('scan-camera'),
    cameraBox: $('scan-camera-box'),
    cameraVideo: $('scan-video'),
    cameraState: $('scan-camera-state'),
    cameraStop: $('scan-camera-stop')
  });

  // ---- 選擇清單（先掛上，Enter 選定時清單會攔下，不再觸發下面的欄位 Enter） ----
  if (window.WhPicker && locationsUrl) {
    window.WhPicker.attach(el.location, {
      load: function (q) {
        return getJson(withQuery(locationsUrl, { q: q, itemId: item ? item.id : null })).then(function (b) { return b.items; });
      },
      render: function (o) { return { main: o.code, sub: locationText(o) + (o.reason && o.reason !== 'Stock' ? '　' + REASONS[o.reason] : '') }; },
      pick: function (o) { setLocation(o); afterChange(false, true); },
      empty: '沒有符合的儲位'
    });
    if (el.target) {
      window.WhPicker.attach(el.target, {
        load: function (q) {
          var wh = el.targetWarehouse && !el.targetWarehouse.hidden ? el.targetWarehouse.value : null;
          return getJson(withQuery(locationsUrl, { q: q, target: 'true', warehouseCode: wh })).then(function (b) { return b.items; });
        },
        render: function (o) { return { main: (o.warehouseCode !== warehouseCode ? o.warehouseCode + ' 倉 ' : '') + o.code, sub: o.name || '' }; },
        pick: function (o) {
          var code = o.warehouseCode === warehouseCode ? o.code : 'WH:LOC:' + o.warehouseCode + ':' + o.code;
          if (code === el.location.value.trim()) { fail('目的儲位不可與來源相同：' + o.code); return; }
          setTarget(o, code);
          afterChange(false, true);
        },
        empty: '沒有符合的儲位'
      });
    }
  }
  if (window.WhPicker && lotsUrl) {
    var lotPicker = {
      load: function (q) {
        if (!item || item.trackingMode === 'None') return Promise.resolve([]);
        return getJson(withQuery(lotsUrl, { itemId: item.id, locationId: locationId, q: q })).then(function (b) { return b.items; });
      },
      render: function (o) {
        return {
          main: (o.lot ? '批 ' + o.lot : '') + (o.serial ? (o.lot ? '　' : '') + '序 ' + o.serial : ''),
          sub: o.location + '　數量 ' + fmt(o.quantity) + (o.expiry ? '　到期 ' + o.expiry : '') + (o.status !== 'Available' ? '　' + o.status : '')
        };
      },
      pick: function (o) {
        el.lot.value = o.lot || '';
        el.serial.value = o.serial || '';
        if (el.location.value.trim() !== o.location) {
          var previous = el.location.value.trim();
          el.location.value = o.location;
          locationId = o.locationId;
          el.locationInfo.textContent = '';
          // 批號在別的儲位：明確提示來源儲位已更換；移庫時目的與新來源相同就清掉目的（M8-03 審查建議 3）
          if (previous) status('來源儲位已改為 ' + o.location + '（原本是 ' + previous + '）', 'warn');
          if (el.target && el.target.value.trim() === o.location) { el.target.value = ''; if (el.targetInfo) el.targetInfo.textContent = ''; }
        }
        if (o.serial) el.quantity.value = '1';
        afterChange(false, true);
      },
      empty: function () {
        return !item ? '請先選物料' : item.trackingMode === 'None' ? '這個物料不追蹤批號序號' : '這個儲位沒有可選的批號或序號';
      }
    };
    window.WhPicker.attach(el.lot, lotPicker);
    window.WhPicker.attach(el.serial, lotPicker);
  }

  // HID/PDA 會把字元鍵入目前欄位。用尾碼和字元間隔分出一段掃碼，保留該段之前的數量。
  var quantityKeys = null;
  var confirmedQuantity = null;
  var quantityChoice = document.createElement('span');
  quantityChoice.hidden = true;
  el.quantity.after(quantityChoice);

  function normalizeQuantity(text) {
    return text.replace(/[０-９．]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); }).trim();
  }

  function clearQuantityChoice() {
    quantityChoice.hidden = true;
    quantityChoice.textContent = '';
    el.quantity.readOnly = false;
  }

  function noteManualQuantity() {
    var prior = manualQuantity;
    manualQuantity = el.quantity.value.trim() ? {
      itemId: lastEntrySaved ? null : fieldsItemId,
      auto: !item || !!(prior && prior.auto)
    } : null;
    confirmLarge = false;
  }

  function routeQuantityScan(keys, toQuantity) {
    quantityKeys = null;
    requireManualAdd = false;
    clearQuantityChoice();
    if (toQuantity) {
      el.quantity.value = keys.text;
      confirmedQuantity = keys.text;
      noteManualQuantity();
      status('已填入數量 ' + keys.text + '，請掃物料或確認後加入。', 'warn');
      return;
    }
    el.quantity.value = keys.before;
    confirmedQuantity = keys.before;
    manualQuantity = keys.manual;
    if (manualQuantity) manualQuantity.auto = true;
    focusScan();
    scanner.enqueue(keys.text, 'item-field');
  }

  function showQuantityChoice(keys) {
    var text = normalizeQuantity(el.quantity.value);
    quantityKeys = null;
    quantityChoice.hidden = false;
    el.quantity.readOnly = true;
    var options = [
      { label: '「' + keys.text + '」是條碼', action: function () {
        routeQuantityScan(keys, /^\d+$/.test(keys.text) && numericToQuantity);
      } },
      { label: '「' + text + '」是手填數量', action: function () {
        clearQuantityChoice();
        el.quantity.value = text;
        confirmedQuantity = text;
        noteManualQuantity();
        requireManualAdd = true;
        el.quantity.focus();
      } }
    ];
    options.forEach(function (option) {
      var choice = document.createElement('button');
      choice.type = 'button';
      choice.textContent = option.label;
      choice.addEventListener('click', option.action);
      quantityChoice.appendChild(choice);
    });
    status('數量欄的輸入可能是掃碼，請先確認用途。', 'warn');
  }

  function quantityNeedsReview() {
    if (!quantityChoice.hidden) return true;
    var text = normalizeQuantity(el.quantity.value);
    var longNumber = /^\d{8,}$/.test(text);
    var suspectKeys = quantityKeys && (quantityKeys.uncertain ||
      (quantityKeys.fast && !/^\d+\.\d+$/.test(text) && (quantityKeys.text.length >= 4 || quantityKeys.before.trim())));
    if (text !== confirmedQuantity && (suspectKeys || longNumber)) {
      showQuantityChoice(longNumber ? longQuantityChoice(text, quantityKeys) : quantityKeys);
      return true;
    }
    return false;
  }

  function longQuantityChoice(text, keys) {
    if (keys && keys.fast && keys.text.length >= 4) return keys;
    var start = keys && keys.start;
    return { text: text, before: start ? start.before : '', manual: start ? start.manual : null };
  }

  el.quantity.addEventListener('paste', function (ev) {
    var value = ev.clipboardData && normalizeQuantity(ev.clipboardData.getData('text'));
    if (!value || !/^\d{2,}$/.test(value) && !/[^\d.]/.test(value)) return;
    ev.preventDefault();
    if (!quantityChoice.hidden) return;
    var keys = { text: value, before: el.quantity.value, manual: manualQuantity };
    el.quantity.setRangeText(value, el.quantity.selectionStart, el.quantity.selectionEnd, 'end');
    showQuantityChoice(keys);
  });
  el.quantity.addEventListener('input', function () {
    confirmedQuantity = null;
    noteManualQuantity();
  });
  el.quantity.addEventListener('keydown', function (ev) {
    if (ev.isComposing) return;
    if (!quantityChoice.hidden) {
      if (ev.key === 'Enter') ev.preventDefault();
      return;
    }
    if (ev.key !== 'Enter' && ev.key !== 'Tab') {
      if (!ev.ctrlKey && !ev.altKey && !ev.metaKey && ev.key && ev.key.length === 1) {
        var gap = quantityKeys ? ev.timeStamp - quantityKeys.last : Infinity;
        if (!quantityKeys || gap >= 30) {
          if (quantityKeys && quantityKeys.fast) {
            quantityKeys.uncertain = true;
          } else {
            var start = quantityKeys ? quantityKeys.start : { before: el.quantity.value, manual: manualQuantity };
            quantityKeys = { text: '', before: el.quantity.value, manual: manualQuantity, fast: false, start: start };
          }
        } else if (quantityKeys.text.length >= 1) {
          quantityKeys.fast = true;
        }
        quantityKeys.text += ev.key;
        quantityKeys.last = ev.timeStamp;
      } else if (ev.key !== 'Shift') {
        quantityKeys = null;
      }
      return;
    }
    var keys = quantityKeys;
    quantityKeys = null;
    var text = normalizeQuantity(el.quantity.value);
    el.quantity.value = text;
    var recent = keys && ev.timeStamp - keys.last < 80;
    var numeric = keys && /^\d+$/.test(keys.text);
    var barcode = keys && keys.text.length >= 4 && !/^\d+\.\d+$/.test(keys.text);
    var longNumber = /^\d{8,}$/.test(text);
    if (recent && !keys.uncertain && (barcode || /[^\d.]/.test(keys.text))) {
      ev.preventDefault();
      routeQuantityScan(keys, numeric && numericToQuantity);
      return;
    }
    if (text !== confirmedQuantity && (keys && (keys.uncertain || (!recent && barcode) ||
        (keys.fast && numeric && (keys.before.trim() || ev.key === 'Tab'))) || longNumber)) {
      ev.preventDefault();
      showQuantityChoice(longNumber ? longQuantityChoice(text, keys) : keys);
      return;
    }
    if (ev.key === 'Tab') return;
    ev.preventDefault();
    if (!item) { focusScan(); return; }
    submit(false, true);
  });
  el.add.addEventListener('click', function () { submit(false, true); });
  document.addEventListener('submit', function (event) {
    if (!event.target.querySelector('#document-submit,#document-confirm,#opening-submit,#opening-confirm')) return;
    var reviewingQuantity = quantityNeedsReview();
    if (!pendingAdd && !lineEdits.size && !reviewingQuantity && !requireManualAdd && !scanWorkActive() && retryRemove.hidden) return;
    event.preventDefault();
    fail(pendingAdd ? '請先確認上一筆加入結果，再送審或過帳。' :
      reviewingQuantity ? '請先確認數量欄的掃碼內容。' :
      scanWorkActive() ? '正在處理明細，請等畫面更新後再送審或過帳。' :
      requireManualAdd ? '請先加入已確認的手填數量，或清除本筆輸入。' :
      !retryRemove.hidden ? '請先讀取最新明細，核對刪除結果。' : '請先儲存或取消明細數量的修改，再送審或過帳。');
    if (pendingAdd && !busy) retryAdd.focus();
    else if (!retryRemove.hidden && !removing) retryRemove.focus();
  });
  retryAdd.addEventListener('click', function () {
    if (!pendingAdd || busy || retryAdd.disabled) return;
    if (!navigator.onLine) { fail('目前離線，恢復連線後再重試原加入明細。'); return; }
    retryAdd.disabled = true;
    var run = inflight.then(function () {
      if (pendingAdd) return sendLine(pendingAdd, false, null, true);
      retryAdd.disabled = false;
    });
    inflight = run.catch(function () { });
  });
  el.clear.addEventListener('click', clearLine);
  el.addMode.addEventListener('change', function () { save(ADD_MODE_KEY, el.addMode.value); pending = 0; showNext(); focusScan(); });
  if (el.targetWarehouse) {
    el.targetWarehouse.addEventListener('change', function () {
      el.target.value = '';
      if (el.targetInfo) el.targetInfo.textContent = '';
      showNext();
    });
  }

  // 物料欄：按 Enter 立刻解析（料號、條碼精確命中就帶入，否則列出搜尋結果）；改了內容離開欄位時也解析
  var lastItemQuery = null; // Enter 之後失焦也會觸發 change：同一個輸入不查兩次
  el.item.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Enter' || ev.isComposing) return;
    ev.preventDefault();
    var value = el.item.value.trim();
    lastItemQuery = value;
    if (value && !(item && item.itemCode === value)) {
      // 先清掉舊物料：解析或搜尋期間不會沿用舊物料的追蹤方式、包裝量（M8-03 審查 4）
      item = null;
      el.itemInfo.textContent = '';
      applyFields();
      scanner.enqueue(value, 'item-field');
    }
  });
  el.item.addEventListener('change', function () {
    var value = el.item.value.trim();
    if ((item && item.itemCode === value) || value === lastItemQuery) return;
    lastItemQuery = value;
    item = null;
    el.itemInfo.textContent = '';
    applyFields();
    if (value) scanner.enqueue(value, 'item-field');
  });
  el.lot.addEventListener('input', function () { prefilledLot = !fieldsItemId || lastEntrySaved; });
  if (el.expiry) el.expiry.addEventListener('input', function () { prefilledExpiry = !fieldsItemId || lastEntrySaved; });
  // 手打儲位：不知道 id，批號清單改列整個倉庫
  el.location.addEventListener('input', function () { locationId = null; el.locationInfo.textContent = ''; });
  if (el.target) el.target.addEventListener('input', function () { if (el.targetInfo) el.targetInfo.textContent = ''; });
  ['location', 'target', 'lot', 'serial', 'expiry'].forEach(function (k) {
    if (!el[k]) return;
    el[k].addEventListener('keydown', function (ev) {
      if (ev.key === 'Enter' && !ev.isComposing) { ev.preventDefault(); afterChange(false, true); }
    });
    el[k].addEventListener('change', showNext);
  });

  // 明細表的「刪除」「刪除勾選的明細」：改用 fetch 送出、換掉明細表，不重新整理頁面，輸入區的內容不受影響（使用者 2026-11-02）。
  // 用事件委派：明細表重繪後不必重綁。勾選刪除的確認視窗在 documents.js（按取消時 defaultPrevented，這裡就不送）。
  // 與加入明細排同一個佇列，一次只送一筆；不看 epoch（加入失敗不應讓排隊中的刪除消失）
  var removeUrl = panel.getAttribute('data-remove-url');
  var removing = 0; // 刪除送出中：離開頁面、PWA 換版重載前要知道
  var retryRemove = document.createElement('button');
  retryRemove.type = 'button';
  retryRemove.className = 'secondary';
  retryRemove.textContent = '讀取最新明細';
  retryRemove.hidden = true;
  retryAdd.after(retryRemove);
  retryRemove.addEventListener('click', function () {
    if (scanWorkActive()) return;
    removing++;
    var run = inflight.then(async function () {
      if (await reloadAfterRemove()) ok('已讀取最新明細，請核對刪除結果。');
      else fail('目前無法讀取最新明細，請稍後重試。');
    }).finally(function () { removing--; });
    inflight = run.catch(function () { });
  });

  async function reloadAfterRemove() {
    retryRemove.hidden = await reloadLines();
    return retryRemove.hidden;
  }

  document.addEventListener('submit', function (ev) {
    var form = ev.target;
    if (ev.defaultPrevented || !removeUrl || !form.matches('form[data-remove-lines]')) return;
    ev.preventDefault();
    if (removing) return;
    var data = new FormData(form); // 包含用 form 屬性掛到勾選表單的勾選框
    var ids = data.getAll('lineIds').concat(data.getAll('lineId'));
    if (!ids.length) return;
    form.setAttribute('aria-busy', 'true');
    removing++;
    var run = inflight.then(function () { return removeCore(ids); }).finally(function () {
      removing--;
      form.removeAttribute('aria-busy');
    });
    inflight = run.catch(function () { });
  });

  async function removeCore(ids) {
    if (!navigator.onLine) {
      fail('目前離線，無法刪除；恢復連線後再按一次。');
      return;
    }
    try {
      var res = await fetch(removeUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/html, application/json',
          'RequestVerificationToken': tokenInput ? tokenInput.value : ''
        },
        body: JSON.stringify({ lineIds: ids })
      });
      var type = res.headers.get('Content-Type') || '';
      if (res.ok && !res.redirected && type.indexOf('text/html') >= 0 && replaceLines(await res.text())) {
        ok('已刪除 ' + ids.length + ' 行明細。');
        log('明細', '刪除 ' + ids.length + ' 行', true);
      } else if (res.status === 400 && type.indexOf('json') >= 0) {
        fail((await res.json()).message || '刪除失敗');
        log('明細', '刪除失敗', false);
        await reloadLines(); // 例如別人已刪掉那一行：換成最新的明細表
      } else {
        fail('不確定是否已刪除（HTTP ' + res.status + '）。' +
          (await reloadAfterRemove() ? '已重新讀取明細表，請核對。' : '目前無法讀取最新明細，請按「讀取最新明細」重試。'));
      }
    } catch (e) {
      fail('網路中斷：不確定是否已刪除。' +
        (await reloadAfterRemove() ? '已重新讀取明細表，請核對。' : '目前無法讀取最新明細，請按「讀取最新明細」重試。'));
    }
    showNext();
  }

  // 失焦儲存與掃碼、刪除共用 inflight。保留其他列正在輸入的值，局部換表時不會清掉。
  var lineEdits = new Map();
  var lineKeyBursts = new WeakMap();
  var savingLines = 0;
  var quantityRecovery = document.createElement('div');
  retryAdd.after(quantityRecovery);

  function renderQuantityRecovery() {
    quantityRecovery.textContent = '';
    lineEdits.forEach(function (edit, id) {
      if (!edit.request || quantityEditor(id)) return;
      var retry = document.createElement('button');
      retry.type = 'button';
      retry.className = 'secondary';
      retry.textContent = '重試明細數量（' + edit.request.originalQuantity + ' → ' + edit.request.quantity + '）';
      retry.disabled = edit.saving;
      retry.addEventListener('click', function () { enqueueQuantity(id, edit, true); });
      quantityRecovery.appendChild(retry);
    });
  }

  function quantityEditor(id) {
    return document.querySelector('[data-line-quantity="' + id + '"]');
  }

  function renderQuantityEditor(input, edit) {
    input.readOnly = !!(edit && edit.request);
    input.value = edit ? edit.value : input.dataset.savedQuantity;
    var cell = input.parentElement;
    cell.querySelector('[data-line-quantity-status]').textContent = edit ? edit.message : '';
    var retry = cell.querySelector('[data-line-quantity-retry]');
    retry.hidden = !(edit && ((edit.request && !edit.saving) || edit.confirm));
    retry.textContent = edit && edit.confirm ? '確認儲存' : '重試';
    cell.querySelector('[data-line-quantity-cancel]').hidden = !(edit && !edit.request);
    if (edit && edit.error) input.setAttribute('aria-invalid', 'true');
    else input.removeAttribute('aria-invalid');
  }

  function bindQuantityEditors(root) {
    if (!quantityUrl || !root) return;
    root.querySelectorAll('[data-line-quantity]').forEach(function (input) {
      renderQuantityEditor(input, lineEdits.get(input.dataset.lineQuantity));
    });
    lineEdits.forEach(function (edit, id) {
      if (!quantityEditor(id) && !edit.request) {
        lineEdits.delete(id);
        fail('明細已不存在或目前無法編輯，請核對明細表。');
      }
    });
    renderQuantityRecovery();
  }

  function cancelQuantity(input) {
    var edit = lineEdits.get(input.dataset.lineQuantity);
    if (edit && edit.request) return;
    lineEdits.delete(input.dataset.lineQuantity);
    lineKeyBursts.delete(input);
    renderQuantityEditor(input, null);
  }

  function queueQuantity(input, explicit, confirmed) {
    var id = input.dataset.lineQuantity;
    var edit = lineEdits.get(id);
    if (!edit || edit.saving || (!explicit && (edit.request || edit.error))) return;
    var retrying = !!edit.request;
    if (!edit.request) {
      var burst = lineKeyBursts.get(input);
      lineKeyBursts.delete(input);
      var value = normalizeQuantity(edit.value);
      if (canonicalQuantity(value) === input.dataset.savedQuantity) { cancelQuantity(input); return; }
      if (!/^\d+(\.\d+)?$/.test(value)) {
        edit.message = '請輸入數字。';
        edit.error = true;
        renderQuantityEditor(input, edit);
        return;
      }
      if ((edit.confirm || /^\d{8,}$/.test(value) || (burst && burst.count >= 4)) && !confirmed) {
        edit.message = '數量可能來自掃碼，請核對後按「確認儲存」。';
        edit.confirm = true;
        edit.error = true;
        renderQuantityEditor(input, edit);
        return;
      }
      edit.confirm = false;
      edit.request = { lineId: id, originalQuantity: edit.original, quantity: value, operationId: operationId() };
    }
    enqueueQuantity(id, edit, retrying);
  }

  function canonicalQuantity(value) {
    return value.replace(/^0+(?=\d)/, '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');
  }

  function enqueueQuantity(id, edit, retrying) {
    edit.saving = true;
    edit.error = false;
    edit.message = '儲存中…';
    var input = quantityEditor(id);
    if (input) renderQuantityEditor(input, edit);
    renderQuantityRecovery();
    savingLines++;
    var run = inflight.then(function () { return saveLineQuantity(id, edit, retrying); });
    inflight = run.catch(function () { });
  }

  async function saveLineQuantity(id, edit, retrying) {
    try {
      var res = await fetch(quantityUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json',
          'RequestVerificationToken': tokenInput ? tokenInput.value : ''
        },
        body: JSON.stringify(edit.request)
      });
      var input = quantityEditor(id);
      if (res.status === 204 && !res.redirected) {
        if (retrying) {
          if (!await reloadLines()) {
            edit.message = '原修改已確認，尚未讀到目前數量，請重試確認。';
            return;
          }
          lineEdits.delete(id);
          input = quantityEditor(id);
          if (input) renderQuantityEditor(input, null);
          ok('已確認原修改，畫面顯示目前明細數量。');
          return;
        }
        var saved = canonicalQuantity(edit.request.quantity);
        lineEdits.delete(id);
        if (input) {
          input.dataset.savedQuantity = saved;
          renderQuantityEditor(input, null);
          input.parentElement.querySelector('[data-line-quantity-status]').textContent = '已儲存';
        }
        return;
      }
      if ([400, 403, 409].indexOf(res.status) >= 0 && !res.redirected) {
        var error = (res.headers.get('Content-Type') || '').indexOf('json') >= 0 ? await res.json() : null;
        edit.message = error && error.message ? error.message : '無法儲存數量，請核對權限或重新登入。';
        edit.request = null;
        if (res.status === 409 || res.status === 403) {
          await reloadLines();
          input = quantityEditor(id);
          if (input) edit.original = input.dataset.savedQuantity;
        }
      } else {
        edit.message = '儲存結果待確認，請重試原操作。';
      }
    } catch (error) {
      edit.message = '連線中斷，請重試原操作。';
    } finally {
      savingLines--;
      edit.saving = false;
      if (lineEdits.has(id)) {
        edit.error = true;
        var current = quantityEditor(id);
        if (current) renderQuantityEditor(current, edit);
        else if (!edit.request) { lineEdits.delete(id); fail(edit.message); }
        else fail(edit.message);
      }
      renderQuantityRecovery();
    }
  }

  document.addEventListener('input', function (event) {
    var input = event.target;
    var id = input.getAttribute('data-line-quantity');
    if (!id || !quantityUrl) return;
    var edit = lineEdits.get(id) || { original: input.dataset.savedQuantity };
    if (edit.request) return;
    edit.value = input.value;
    edit.error = false;
    edit.confirm = false;
    edit.message = '';
    lineEdits.set(id, edit);
    var cell = input.parentElement;
    cell.querySelector('[data-line-quantity-status]').textContent = '';
    cell.querySelector('[data-line-quantity-cancel]').hidden = false;
    cell.querySelector('[data-line-quantity-retry]').hidden = true;
    input.removeAttribute('aria-invalid');
  });
  document.addEventListener('focusout', function (event) {
    if (!event.target.hasAttribute('data-line-quantity')) return;
    queueQuantity(event.target, false);
  });
  document.addEventListener('keydown', function (event) {
    var input = event.target;
    if (event.isComposing || !input.hasAttribute('data-line-quantity')) return;
    if (!event.ctrlKey && !event.altKey && !event.metaKey && /^\d$/.test(event.key)) {
      var burst = lineKeyBursts.get(input);
      lineKeyBursts.set(input, {
        count: burst && event.timeStamp - burst.last < 35 ? burst.count + 1 : 1,
        last: event.timeStamp
      });
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      queueQuantity(input, true);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      cancelQuantity(input);
    }
  });
  document.addEventListener('mousedown', function (event) {
    if (event.target.hasAttribute('data-line-quantity-cancel')) event.preventDefault();
  });
  document.addEventListener('click', function (event) {
    var target = event.target;
    if (target.hasAttribute('data-line-quantity-retry')) {
      queueQuantity(target.parentElement.querySelector('[data-line-quantity]'), true, true);
    } else if (target.hasAttribute('data-line-quantity-cancel')) {
      cancelQuantity(target.parentElement.querySelector('[data-line-quantity]'));
    }
  });
  bindQuantityEditors(document.querySelector('[data-scan-lines]'));

  function scanWorkActive() {
    return scanner.pending() > 0 || adding > 0 || busy || removing > 0 || savingLines > 0;
  }

  // 頁面要離開時，面板上還有未加入的物料就提醒（PWA 更新提示也會呼叫）
  var priorUpdateBlock = window.WhPageUpdateBlocked;
  window.WhPageUpdateBlocked = function () {
    return !!(priorUpdateBlock && priorUpdateBlock()) || scanWorkActive() || !!pendingAdd || lineEdits.size > 0 || !quantityChoice.hidden || !retryRemove.hidden;
  };
  window.WhScanDirty = function () { return scanWorkActive() || lineEdits.size > 0 || !!manualQuantity || !!el.item.value.trim(); };
  // 自動化實測（tools/ScanCheck）用：佇列是否還在處理
  window.WhScanBusy = function () { return scanWorkActive() || Array.from(lineEdits.values()).some(function (edit) { return !!edit.request; }); };

  // 初始化都成功後才收起沒有 JS 時的傳統表單（中途出錯時頁面仍有可用的輸入方式；同一頁只保留一套輸入）
  var fallback = document.querySelector('[data-entry-fallback]');
  if (fallback) fallback.hidden = true;
  if (pendingAdd) status('上一筆加入結果待確認。請先重試原加入明細。', 'bad', true);
  applyFields();
  showNext();
  focusScan();
})();
