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
// 網路錯誤不自動重送（避免累加兩次），改為重新讀取明細表讓人核對。
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
  var confirmLarge = false; // 數量偏大時要再按一次 Enter

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
    panel.setAttribute('data-step', s);
    el.next.textContent = '下一步：' + LABELS[s] + (addMode() === 'each' ? '（逐件累加中）' : addMode() === 'auto' ? '（齊了自動加入）' : '');
  }

  function status(text, kind) {
    el.status.textContent = text;
    el.status.className = 'scan-status scan-' + kind;
    window.WhScan.feedback(kind);
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
    el.input.focus();
    setTimeout(function () {
      var active = document.activeElement;
      if (!active || active === document.body || active.tagName === 'BUTTON' || active.tagName === 'A') el.input.focus();
    }, 0);
  }

  function focusQuantity() {
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
    var same = item && item.id === it.id;
    item = it;
    hideResults();
    if (!same) {
      el.item.value = it.itemCode;
      el.serial.value = '';
      el.lot.value = lastLot && lastLot.itemId === it.id ? lastLot.lot : '';
      if (el.expiry) el.expiry.value = '';
      pending = 0;
      showHints(body.extra);
    }
    el.itemInfo.textContent = it.name + (it.specification ? '　' + it.specification : '') + '（單位 ' + it.unitCode + '，'
      + (TRACKING[it.trackingMode] || it.trackingMode)
      + (it.matchedBy === 'Barcode' && it.packQuantity !== 1 ? '，此條碼每掃一次 ' + fmt(it.packQuantity) + ' ' + it.unitCode : '')
      + (it.isActive ? '' : '，已停用') + '）';
    if (it.trackingMode === 'Serial') {
      el.quantity.value = '1';
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
    if (tracking() === 'Serial') {
      el.quantity.value = '1';
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

  function payload(merge) {
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
      merge: merge
    };
  }

  // 用重繪的明細表換掉舊的。加明細不會改變單據版本（只寫明細表），所以不動頁面上的 Version；
  // 版本不同代表別人改過表頭或狀態，只提示重新整理，不自動套用新版本（否則表頭表單會無聲覆蓋別人的修改，PR 2 審查 M1）
  function replaceLines(html) {
    var old = document.querySelector('[data-scan-lines]');
    var template = document.createElement('template');
    template.innerHTML = html.trim();
    var fresh = template.content.querySelector('[data-scan-lines]');
    if (!old || !fresh) return false;
    old.parentNode.replaceChild(fresh, old);
    var version = fresh.getAttribute('data-version');
    var pageVersion = document.querySelector('input[name="Version"]');
    if (version && pageVersion && pageVersion.value !== version && stale) {
      stale.hidden = false;
    }
    if (window.WhLines) window.WhLines.bind(fresh);
    return true;
  }

  async function reloadLines() {
    try {
      var res = await fetch(window.location.pathname + window.location.search, { credentials: 'same-origin' });
      if (res.ok) replaceLines(await res.text());
    } catch (e) { /* 仍然離線：讓人自行重新整理 */ }
  }

  // 一次只送一筆：手動按「加入明細」與掃碼觸發的送出若同時發生，後到的等前一筆結束再送（PR 2 審查 S1）
  // epoch：遇到「規則錯誤」或「不確定是否已寫入」時遞增；之前排隊的送出一律放棄，
  // 否則排在後面、內容相同的那一筆會再送一次（等於自動重送，M8-03 審查 1）
  var inflight = Promise.resolve();
  var epoch = 0;
  function submit(merge) {
    var mine = epoch;
    var run = inflight.then(function () { return mine === epoch ? submitNow(merge) : undefined; });
    inflight = run.catch(function () { });
    return run;
  }

  async function submitNow(merge) {
    var s = step();
    if (s !== 'quantity') {
      fail('還沒完成：' + LABELS[s]);
      focusScan();
      return;
    }
    var data = payload(merge);
    if (merge) data.quantity = pending;
    if (!(data.quantity > 0)) {
      if (!merge) {
        fail('數量必須大於 0。');
        focusQuantity();
      }
      return;
    }
    if (!merge && !/^\d+(\.\d+)?$/.test(el.quantity.value.trim())) {
      fail('數量請輸入數字（例 3 或 2.5）。');
      focusQuantity();
      return;
    }
    if (!merge && !confirmLarge && data.quantity > 1000 * Math.max(1, item ? item.packQuantity : 1)) {
      confirmLarge = true;
      status('數量 ' + fmt(data.quantity) + ' 偏大，請確認後再按一次 Enter（掃錯欄位時請修改）。', 'warn');
      focusQuantity();
      return;
    }
    if (!navigator.onLine) {
      fail('目前離線，送出不會成功；恢復連線後再按 Enter。');
      return;
    }
    confirmLarge = false;
    busy = true;
    el.add.disabled = true;
    if (merge) pending -= data.quantity; // 送出中又掃到的件數留在 pending，下一次再送
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
      if (res.ok && type.indexOf('text/html') >= 0 && replaceLines(await res.text())) {
        ok('已加入：' + data.itemCode + ' × ' + fmt(data.quantity) + (merge ? '（累加）' : '') + '，儲位 ' + data.locationCode);
        log(data.itemCode, '加入 ' + fmt(data.quantity), true);
        afterSubmit();
      } else if (res.status === 400 && type.indexOf('json') >= 0) {
        // 400＝規則錯誤（伺服器在寫入前拒絕），這一筆沒有加入
        epoch++;
        fail((await res.json()).message || '加入失敗');
        log(data.itemCode, '加入失敗', false);
        if (merge) pending = 0;
      } else {
        // 其他錯誤（逾時登入、500 等）不能確定是否已寫入：重新讀取明細表讓人核對，不自動重送（PR 2 審查 S2）
        epoch++;
        fail(res.redirected ? '登入已逾時，請重新整理頁面並登入。'
          : '不確定這一行是否已加入（HTTP ' + res.status + '），已重新讀取明細表，請核對後再繼續。');
        log(data.itemCode, '不確定是否已加入', false);
        if (merge) pending = 0;
        await reloadLines();
      }
    } catch (e) {
      epoch++;
      fail('網路中斷：不確定這一行是否已加入，已重新讀取明細表，請核對後再繼續（不會自動重送）。');
      log(data.itemCode, '不確定是否已加入', false);
      if (merge) pending = 0;
      await reloadLines();
    } finally {
      busy = false;
      el.add.disabled = false;
      showNext();
      focusScan();
    }
  }

  // 加入後：保留儲位；物料清空等下一個（逐件累加保留物料與批號，連續掃同一物料）
  function afterSubmit() {
    lastItemQuery = null;
    if (item) lastLot = { itemId: item.id, lot: el.lot.value.trim() };
    el.serial.value = '';
    if (addMode() !== 'each' || tracking() === 'Serial') {
      if (tracking() !== 'Serial') {
        item = null;
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
    item = null;
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

  // 數量欄：Enter 加入。若在數量欄收到條碼槍輸入（很長、或字元間隔極短），改交給掃碼佇列，不當成數量
  var keyTimes = [];
  el.quantity.addEventListener('focus', function () { keyTimes = []; });
  el.quantity.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Enter' && ev.key !== 'Tab') {
      if (ev.key && ev.key.length === 1) {
        keyTimes.push(performance.now());
        confirmLarge = false; // 改了數量就要重新確認
      }
      return;
    }
    // 全形數字（中文輸入法全形模式）先轉半形，才不會被當成條碼
    var text = el.quantity.value.replace(/[０-９．]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); });
    el.quantity.value = text;
    var fast = keyTimes.length >= 4 && (keyTimes[keyTimes.length - 1] - keyTimes[0]) / (keyTimes.length - 1) < 30;
    // 數量欄是文字欄（inputmode=decimal），英數條碼不會被瀏覽器吃掉；不是純數字就一定是掃到條碼（PR 2 審查 M3）
    var looksLikeScan = fast || !/^[0-9.]*$/.test(text.trim()) || text.replace(/\D/g, '').length > 8;
    if (ev.key === 'Tab' && !looksLikeScan) return; // 人按 Tab：照常移到下一欄
    ev.preventDefault();
    keyTimes = [];
    if (looksLikeScan) {
      el.quantity.value = item ? fmt(item.packQuantity) : '';
      status('數量欄收到條碼，已改當成掃碼處理。', 'warn');
      focusScan();
      scanner.enqueue(text, 'keyboard');
      return;
    }
    submit(false);
  });
  el.add.addEventListener('click', function () { submit(false); });
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

  // 頁面要離開時，面板上還有未加入的物料就提醒（PWA 更新提示也會呼叫）
  window.WhScanDirty = function () { return scanner.pending() > 0 || busy || !!el.item.value.trim(); };
  // 自動化實測（tools/ScanCheck）用：佇列是否還在處理
  window.WhScanBusy = function () { return scanner.pending() > 0 || busy; };

  // 初始化都成功後才收起沒有 JS 時的傳統表單（中途出錯時頁面仍有可用的輸入方式；同一頁只保留一套輸入）
  var fallback = document.querySelector('[data-entry-fallback]');
  if (fallback) fallback.hidden = true;
  applyFields();
  showNext();
  focusScan();
})();
