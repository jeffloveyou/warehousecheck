// 單據、期初的連續掃碼面板（M5-03，docs/04 §6）。不使用框架，相容 Chrome 109。
// 流程（FR-05）：
//  - 進倉、期初：物料 →（批號／序號）→ 儲位 → 數量
//  - 出倉：來源儲位 → 物料 →（批號／序號）→ 數量
//  - 移庫：來源儲位 → 目的儲位 → 物料 →（批號／序號）→ 數量
// 掃到什麼由伺服器判定（只做精確比對）：儲位填儲位欄、物料填物料欄，其他文字只在等待批號／序號時填入，否則拒絕。
// 加入明細：
//  - 一般：欄位齊了游標跳到數量（預填包裝數量），確認後按 Enter。
//  - 序號物料：每個序號一行、數量 1，欄位齊了自動加入。
//  - 逐件累加（預設關）：每掃一次物料就加入（相同的草稿行累加包裝數量）。
// 加入明細只是草稿，不影響庫存；過帳仍要按頁面上的「確認過帳」。
// 網路錯誤不自動重送（避免累加兩次），改為重新讀取明細表讓人核對。
(function () {
  'use strict';

  var panel = document.getElementById('scan-panel');
  if (!panel || !window.WhScan || !window.fetch) return;
  panel.hidden = false;

  var mode = panel.getAttribute('data-mode');
  var warehouseId = panel.getAttribute('data-warehouse-id');
  var resolveUrl = panel.getAttribute('data-resolve-url');
  var addUrl = panel.getAttribute('data-add-url');
  function $(id) { return document.getElementById(id); }
  var el = {
    input: $('scan-input'), status: $('scan-status'), next: $('scan-next'), hints: $('scan-hints'), log: $('scan-log'),
    location: $('scan-location'), target: $('scan-target'), item: $('scan-item'), itemInfo: $('scan-item-info'),
    lot: $('scan-lot'), serial: $('scan-serial'), expiry: $('scan-expiry'), reservation: $('scan-reservation'),
    quantity: $('scan-quantity'), add: $('scan-add'), clear: $('scan-clear'), accumulate: $('scan-accumulate')
  };
  var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
  var item = null;          // 伺服器解析出的物料（追蹤模式、單位、包裝數量）
  var pending = 0;          // 逐件累加：已掃但還沒加入的數量
  var lastLot = null;       // 同一物料連續掃時沿用批號 { itemId, lot }
  var busy = false;
  var confirmLarge = false; // 數量偏大時要再按一次 Enter

  var LABELS = {
    location: mode === 'inbound' ? '掃描入庫儲位' : mode === 'opening' ? '掃描儲位' : '掃描來源儲位',
    target: '掃描目的儲位',
    item: '掃描物料（料號 QR 或已登錄的原廠條碼）',
    lot: '掃描或輸入批號',
    serial: '掃描或輸入序號',
    quantity: '確認數量後按 Enter，或按「加入明細」'
  };
  var TRACKING = { None: '不追蹤批號序號', Lot: '批號管理', Serial: '序號管理' };

  function fmt(n) { return String(Number(n)); }
  function tracking() { return item ? item.trackingMode : 'None'; }

  // 目前還缺哪一步
  function step() {
    var order = mode === 'outbound' ? ['location', 'item', 'track']
      : mode === 'transfer' ? ['location', 'target', 'item', 'track']
      : ['item', 'track', 'location'];
    for (var i = 0; i < order.length; i++) {
      var s = order[i];
      if (s === 'track') {
        if (tracking() === 'Lot' && !el.lot.value.trim()) return 'lot';
        if (tracking() === 'Serial' && !el.serial.value.trim()) return 'serial';
      } else if (!el[s].value.trim()) {
        return s;
      }
    }
    return 'quantity';
  }

  function showNext() {
    var s = step();
    panel.setAttribute('data-step', s);
    el.next.textContent = '下一步：' + LABELS[s] + (el.accumulate.checked ? '（逐件累加中）' : '');
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

  // 結存、建議儲位做成可點的 chip（用 textContent 建立，不插入 HTML）
  function chip(text, onClick) {
    var a = document.createElement('a');
    a.href = '#';
    a.className = 'chip';
    a.textContent = text;
    a.addEventListener('click', function (ev) { ev.preventDefault(); onClick(); afterChange(false); });
    return a;
  }

  function showHints(extra) {
    el.hints.textContent = '';
    if (!extra) return;
    if (extra.suggestions && extra.suggestions.length) {
      el.hints.appendChild(document.createTextNode('建議儲位：'));
      extra.suggestions.forEach(function (s) {
        var reason = s.reason === 'Default' ? '預設' : s.reason === 'Frequent' ? '常用' : '有庫存';
        el.hints.appendChild(chip(s.location + '（' + reason + (s.quantity > 0 ? '，現有 ' + fmt(s.quantity) : '') + '）', function () {
          el.location.value = s.location;
        }));
      });
    }
    if (extra.balances) {
      if (!extra.balances.length) {
        el.hints.appendChild(document.createTextNode('本倉沒有這個料號的結存。'));
      } else {
        el.hints.appendChild(document.createTextNode('本倉結存：'));
        extra.balances.forEach(function (b) {
          var text = b.location + (b.lot ? ' 批 ' + b.lot : '') + (b.serial ? ' 序 ' + b.serial : '') + '：' + fmt(b.quantity)
            + (b.available ? '' : '（待驗儲位）');
          el.hints.appendChild(chip(text, function () {
            el.location.value = b.location;
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

  function setItem(body) {
    var it = body.item;
    var same = item && item.id === it.id;
    item = it;
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
    } else if (el.accumulate.checked) {
      pending += it.packQuantity;
      el.quantity.value = fmt(pending);
    } else {
      el.quantity.value = fmt(it.packQuantity);
    }
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
        el.location.value = loc.code;
        el.target.value = '';
        ok('來源儲位 ' + loc.code + (cleared ? '（已清除目的儲位，請再掃目的）' : ''));
      } else if (label === el.location.value.trim()) {
        fail('目的儲位不可與來源相同：' + loc.code);
        return false;
      } else {
        el.target.value = label;
        ok('目的儲位 ' + (loc.warehouseId === warehouseId ? loc.code : loc.warehouseCode + ' 倉 ' + loc.code));
      }
    } else {
      el.location.value = loc.code;
      ok('儲位 ' + loc.code + (loc.status === 'Inspection' ? '（待驗）' : ''));
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

  function applyText(body, s) {
    if (s === 'lot') {
      el.lot.value = body.value;
      ok('批號 ' + body.value);
      return true;
    }
    if (s === 'serial') {
      el.serial.value = body.value;
      ok('序號 ' + body.value);
      return true;
    }
    fail('「' + body.value + '」不是已登錄的物料或儲位。若是包裝上的新條碼，請到物料頁登錄為原廠條碼；或用下方搜尋。');
    return false;
  }

  // 一筆掃碼：伺服器解析 → 依類型套用 → 欄位齊了就跳數量或自動加入
  async function onScan(value) {
    var s = step();
    var url = resolveUrl + (resolveUrl.indexOf('?') < 0 ? '?' : '&') + 'code=' + encodeURIComponent(value)
      + (mode === 'transfer' && el.location.value.trim() && !el.target.value.trim() ? '&target=true' : '');
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
    if (body.kind === 'Location') {
      applied = applyLocation(body);
    } else if (body.kind === 'Item') {
      applied = applyItem(body);
      wasItem = applied;
    } else if (body.kind === 'Text') {
      applied = applyText(body, s);
    } else {
      fail(body.message || '無法辨識：' + value);
    }
    log(value, el.status.textContent, applied);
    if (applied) await afterChange(wasItem);
  }

  async function afterChange(wasItem) {
    showNext();
    if (step() !== 'quantity') {
      focusScan();
      return;
    }
    if (tracking() === 'Serial') {
      el.quantity.value = '1';
      await submit(false);
    } else if (el.accumulate.checked) {
      if (pending > 0) await submit(true);
      else focusScan();
    } else if (wasItem || document.activeElement === el.input) {
      focusQuantity();
    }
  }

  function payload(merge) {
    return {
      locationCode: el.location.value.trim(),
      targetLocationCode: el.target ? el.target.value.trim() : null,
      itemCode: el.item.value.trim(),
      lotNo: el.lot.value.trim() || null,
      serialNo: el.serial.value.trim() || null,
      expiryDate: el.expiry && el.expiry.value ? el.expiry.value : null,
      reservationId: el.reservation && el.reservation.value ? el.reservation.value : null,
      quantity: Number(el.quantity.value),
      merge: merge
    };
  }

  // 用重繪的明細表換掉舊的，並更新頁面上所有表單的單據版本（確認過帳的併發檢查要用最新版本）
  function replaceLines(html) {
    var old = document.querySelector('[data-scan-lines]');
    var template = document.createElement('template');
    template.innerHTML = html.trim();
    var fresh = template.content.querySelector('[data-scan-lines]');
    if (!old || !fresh) return false;
    old.parentNode.replaceChild(fresh, old);
    var version = fresh.getAttribute('data-version');
    if (version) {
      document.querySelectorAll('input[name="Version"]').forEach(function (input) { input.value = version; });
    }
    return true;
  }

  async function reloadLines() {
    try {
      var res = await fetch(window.location.pathname + window.location.search, { credentials: 'same-origin' });
      if (res.ok) replaceLines(await res.text());
    } catch (e) { /* 仍然離線：讓人自行重新整理 */ }
  }

  async function submit(merge) {
    if (busy) return;
    var s = step();
    if (s !== 'quantity') {
      fail('還沒完成：' + LABELS[s]);
      focusScan();
      return;
    }
    var data = payload(merge);
    if (merge) data.quantity = pending;
    if (!(data.quantity > 0)) {
      fail('數量必須大於 0。');
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
      } else {
        var message = type.indexOf('json') >= 0 ? (await res.json()).message : null;
        fail(message || (res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '加入失敗（HTTP ' + res.status + '）'));
        log(data.itemCode, '加入失敗', false);
      }
    } catch (e) {
      fail('網路中斷：不確定這一行是否已加入，已重新讀取明細表，請核對後再繼續（不會自動重送）。');
      log(data.itemCode, '不確定是否已加入', false);
      await reloadLines();
    } finally {
      busy = false;
      el.add.disabled = false;
      pending = 0;
      showNext();
      focusScan();
    }
  }

  // 加入後：保留儲位；物料清空等下一個（逐件累加保留物料與批號，連續掃同一物料）
  function afterSubmit() {
    if (item) lastLot = { itemId: item.id, lot: el.lot.value.trim() };
    el.serial.value = '';
    if (!el.accumulate.checked || tracking() === 'Serial') {
      if (tracking() !== 'Serial') {
        item = null;
        el.item.value = '';
        el.itemInfo.textContent = '';
        el.lot.value = '';
        el.hints.textContent = '';
        el.quantity.value = '';
        if (el.expiry) el.expiry.value = '';
        if (el.reservation) el.reservation.length = 1;
      }
    }
  }

  function clearLine() {
    item = null;
    pending = 0;
    ['item', 'lot', 'serial', 'quantity'].forEach(function (k) { el[k].value = ''; });
    if (el.expiry) el.expiry.value = '';
    if (el.reservation) el.reservation.length = 1;
    el.itemInfo.textContent = '';
    el.hints.textContent = '';
    status('已清除（儲位保留）。', 'warn');
    showNext();
    focusScan();
  }

  var scanner = window.WhScan.attach({
    input: el.input,
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

  // 數量欄：Enter 加入。若在數量欄收到條碼槍輸入（很長、或字元間隔極短），改交給掃碼佇列，不當成數量
  var keyTimes = [];
  el.quantity.addEventListener('focus', function () { keyTimes = []; });
  el.quantity.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Enter' && ev.key !== 'Tab') {
      if (ev.key.length === 1) {
        keyTimes.push(performance.now());
        confirmLarge = false; // 改了數量就要重新確認
      }
      return;
    }
    var text = el.quantity.value;
    var fast = keyTimes.length >= 4 && (keyTimes[keyTimes.length - 1] - keyTimes[0]) / (keyTimes.length - 1) < 30;
    var looksLikeScan = fast || text.replace(/\D/g, '').length > 8;
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
  el.accumulate.addEventListener('change', function () { pending = 0; showNext(); focusScan(); });

  // 手動改欄位：物料欄改成另一個料號時重新解析；其他欄位按 Enter 回到掃碼框
  el.item.addEventListener('change', function () {
    item = null;
    el.itemInfo.textContent = '';
    if (el.item.value.trim()) scanner.enqueue(el.item.value.trim(), 'manual');
  });
  ['location', 'target', 'lot', 'serial', 'expiry'].forEach(function (k) {
    if (!el[k]) return;
    el[k].addEventListener('keydown', function (ev) {
      if (ev.key === 'Enter') { ev.preventDefault(); afterChange(false); }
    });
    el[k].addEventListener('change', showNext);
  });

  // 頁面要離開時，面板上還有未加入的物料就提醒（PWA 更新提示也會呼叫）
  window.WhScanDirty = function () { return scanner.pending() > 0 || busy || !!el.item.value.trim(); };
  // 自動化實測（tools/ScanCheck）用：佇列是否還在處理
  window.WhScanBusy = function () { return scanner.pending() > 0 || busy; };

  showNext();
  focusScan();
})();
