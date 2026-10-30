// 快速進出購物車的掃碼階段（ADR-0026，docs/04 §6.2）。不使用框架，相容 Chrome 109。需要先載入 scan-input.js。
// - 條碼槍、PDA、相機都走 WhScan 的佇列：一次處理一筆、照順序，不會掉字或重複。
// - 每筆先用 /api/scan 精確解析：
//     儲位 QR → 記為「目前儲位」，之後掃的物料都記在這個儲位（可清除）。
//     物料 → 手動：帶出數量欄（預設包裝量），按 Enter 加入；自動：直接加入一個包裝量（外箱條碼＝一箱的數量）。
//     其他 → 提示（只有儲位碼不知道是哪個倉庫，請掃儲位 QR）。
// - 加入：POST ?handler=Add（JSON＋RequestVerificationToken），成功換掉明細表（不重新載入頁面）；伺服器同料同儲位合併。
// - 結果訊息一律顯示在掃碼框正下方（#scan-status）。只有拿到重繪的明細表才算「已加入」；
//   登入逾時（被轉址）＝沒有加入；網路錯誤或伺服器錯誤＝不確定，重新讀取購物車給人核對，不自動重送（避免加兩次）。
(function () {
  'use strict';

  var panel = document.getElementById('quick-cart');
  if (!panel || !window.WhScan || !window.fetch) return;
  function $(id) { return document.getElementById(id); }

  var addUrl = panel.getAttribute('data-add-url');
  var linesUrl = panel.getAttribute('data-lines-url');
  var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
  var el = {
    input: $('scan-input'), status: $('scan-status'), qtyRow: $('quick-qty-row'), qty: $('quick-qty'), qtyItem: $('quick-qty-item'),
    qtyAdd: $('quick-qty-add'), mode: $('quick-add-mode'), location: $('quick-location'), locationClear: $('quick-location-clear')
  };
  var location = null;   // { code: 'WH:LOC:倉:儲位', label }
  var pending = null;    // 手動模式等數量的物料 { itemCode, label, unitCode }
  var MODE_KEY = 'quick.addMode';

  // 加入方式只記在這台裝置（不佔帳號設定的白名單）
  try { var saved = window.localStorage.getItem(MODE_KEY); if (saved === 'manual' || saved === 'auto') el.mode.value = saved; } catch (e) { /* 略過 */ }
  el.mode.addEventListener('change', function () {
    try { window.localStorage.setItem(MODE_KEY, el.mode.value); } catch (e) { /* 略過 */ }
    hideQty();
    scanner.focus();
  });

  function status(text, kind) {
    el.status.className = 'scan-status scan-' + kind;
    el.status.textContent = text;
  }
  function fmt(n) { return String(Number(n)); }

  function setLocation(loc) {
    location = loc;
    el.location.textContent = loc ? loc.label : '（未指定）';
    el.locationClear.hidden = !loc;
  }
  el.locationClear.addEventListener('click', function () {
    setLocation(null);
    status('已清除目前儲位：之後掃的物料不指定儲位（選動作時再補）。', 'warn');
    scanner.focus();
  });

  function hideQty() {
    pending = null;
    el.qtyRow.hidden = true;
  }

  async function add(itemCode, quantity, label) {
    var res;
    try {
      res = await fetch(addUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/html, application/json',
          'RequestVerificationToken': tokenInput ? tokenInput.value : ''
        },
        body: JSON.stringify({ itemCode: itemCode, locationCode: location ? location.code : null, quantity: quantity })
      });
    } catch (e) {
      // 伺服器可能已經加入、只是回應斷掉：不能說「沒有加入」，否則重掃會變兩倍（審查 2）
      await uncertain(label);
      return;
    }
    // 登入逾時或沒有權限時會被轉址到登入頁（也是 200 HTML）：一定要拿到重繪的明細表才算成功（審查 1）
    if (res.redirected) {
      status('登入已逾時，沒有加入「' + label + '」。請重新整理頁面並登入後再掃。', 'bad');
      window.WhScan.feedback('error');
      return;
    }
    var fresh = res.ok && (res.headers.get('Content-Type') || '').indexOf('html') >= 0 ? linesFrom(await res.text()) : null;
    if (fresh) {
      replaceLines(fresh);
      status('已加入 ' + label + ' × ' + fmt(quantity) + (location ? '（儲位 ' + location.label + '）' : '（未指定儲位）'), 'ok');
      window.WhScan.feedback('ok');
      return;
    }
    if (res.status === 400) {
      var body = null;
      try { body = await res.json(); } catch (e) { body = null; }
      status(body && body.message ? body.message : '沒有加入「' + label + '」。', 'bad');
      window.WhScan.feedback('error');
      return;
    }
    await uncertain(label);
  }

  function linesFrom(html) {
    var template = document.createElement('template');
    template.innerHTML = html;
    return template.content.querySelector('[data-quick-lines]');
  }
  function replaceLines(fresh) {
    var old = document.querySelector('[data-quick-lines]');
    if (old) old.parentNode.replaceChild(fresh, old);
  }

  // 結果不確定（網路斷、伺服器錯誤）：重新讀取購物車給人核對，不自動重送
  async function uncertain(label) {
    window.WhScan.feedback('error');
    status('不確定「' + label + '」是否已加入，正在重新讀取購物車…', 'bad');
    try {
      var res = await fetch(linesUrl, { headers: { 'Accept': 'text/html' }, credentials: 'same-origin' });
      var fresh = res.ok && !res.redirected ? linesFrom(await res.text()) : null;
      if (fresh) {
        replaceLines(fresh);
        status('不確定「' + label + '」是否已加入：已重新讀取購物車，請核對下方數量後再決定要不要重掃。', 'bad');
        return;
      }
    } catch (e) { /* 仍然連不上 */ }
    status('不確定「' + label + '」是否已加入，而且讀不到購物車。請確認連線後重新整理頁面核對，再決定要不要重掃。', 'bad');
  }

  async function onScan(value) {
    var res;
    var body = null;
    try {
      res = await fetch('/api/scan?code=' + encodeURIComponent(value), { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
      if (res.ok && (res.headers.get('Content-Type') || '').indexOf('json') >= 0) body = await res.json();
    } catch (e) {
      status('網路錯誤，沒有處理「' + value + '」，請確認連線後重掃。', 'bad');
      window.WhScan.feedback('error');
      return;
    }
    if (!body) {
      status(res && (res.redirected || res.status === 401) ? '登入已逾時，沒有處理「' + value + '」。請重新整理頁面並登入後再掃。' : '查詢失敗：' + value, 'bad');
      window.WhScan.feedback('error');
      return;
    }
    if (body.kind === 'Location') {
      var loc = body.location;
      if (loc.status === 'Inactive') {
        status('儲位 ' + loc.warehouseCode + ' 倉 ' + loc.code + ' 已停用。', 'bad');
        window.WhScan.feedback('error');
        return;
      }
      hideQty();
      setLocation({ code: 'WH:LOC:' + loc.warehouseCode + ':' + loc.code, label: loc.warehouseCode + ' 倉 ' + loc.code });
      status('目前儲位：' + loc.warehouseCode + ' 倉 ' + loc.code + '，接著掃物料。', 'ok');
      window.WhScan.feedback('ok');
      return;
    }
    if (body.kind === 'Item') {
      var item = body.item;
      var pack = Number(item.packQuantity);
      var label = item.itemCode + ' ' + item.name;
      if (el.mode.value === 'auto') {
        hideQty();
        await add(item.itemCode, pack, label);
        return;
      }
      pending = { itemCode: item.itemCode, label: label };
      el.qtyItem.textContent = label + '（單位 ' + item.unitCode + (pack !== 1 ? '，外箱條碼一箱 ' + fmt(pack) : '') + '）';
      el.qty.value = fmt(pack);
      el.qtyRow.hidden = false;
      status('輸入 ' + label + ' 的數量後按 Enter 加入。', 'warn');
      window.WhScan.feedback('ok');
      el.qty.focus();
      el.qty.select();
      return;
    }
    status(body.message || ('「' + value + '」不是已登錄的物料或儲位 QR（只有儲位碼時請掃儲位 QR，才知道是哪個倉庫）。'), 'bad');
    window.WhScan.feedback('error');
  }

  async function addPending() {
    if (!pending) return;
    var text = el.qty.value.replace(/[０-９．]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); }).trim();
    var quantity = Number(text);
    if (!text || !(quantity > 0)) {
      status('數量要大於 0。', 'bad');
      el.qty.focus();
      return;
    }
    var p = pending;
    hideQty();
    scanner.focus();
    await add(p.itemCode, quantity, p.label);
  }
  el.qty.addEventListener('keydown', function (ev) {
    if (ev.key === 'Enter' && !ev.isComposing) { ev.preventDefault(); addPending(); }
    if (ev.key === 'Escape') { hideQty(); status('已取消。', 'warn'); scanner.focus(); }
  });
  el.qtyAdd.addEventListener('click', addPending);

  var scanner = window.WhScan.attach({
    input: el.input,
    container: panel,
    onScan: onScan,
    onReject: function (message) { status(message, 'bad'); window.WhScan.feedback('error'); },
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
  // 自動化實測（tools/ScanCheck）用：佇列是否還在處理
  window.WhScanBusy = function () { return scanner.pending() > 0; };
  el.input.focus();
})();
