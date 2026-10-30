// 表單相機掃碼（docs/04 §6.2）：每張表單一顆「相機掃碼」，連續掃，按「停止相機」才釋放鏡頭。不使用框架，相容 Chrome 109。
// 需要先載入 scan-input.js（提示音、相機模組網址）與 scan-field.js（欄位下方訊息列）。
// 本檔不含相機與解碼程式：按下按鈕才 import camera-scan.js（再由它載入解碼器），條碼槍、PDA 不會載入。
//
// 群組：同一個 <form> 裡有 data-camera-field 的欄位。按鈕列 .camera-field-bar 放在第一個相機欄位所在區塊前面
//       （表單內有 [data-camera-bar] 就放進去）；相機畫面在按鈕列裡，結果訊息在「當前欄」正下方。
// 當前欄：組內最後點選的相機欄位，沒有就用第一個空白欄；相機開著時點別的欄位只換目標，鏡頭不重開。
// 掃到：依伺服器判斷的類型。放錯欄（料號欄掃到儲位等）不填、不跳，在當前欄下方提示；
//       填好就跳到下一個空白的相機欄（盤點：依物料追蹤方式決定要不要跳批號／序號；條碼列：沒有空白列就新增一列），
//       沒有空白欄就把焦點移到下一個要人填的欄位（data-scan-next 或數量欄），相機目標不變。
// 重複：沿用連續掃碼規則（camera-scan.js：讀到後暫停 1.5 秒；同一碼離開畫面 1.5 秒才再算）。
// 送出：一律由人按。相機開著時送出表單，重新載入後自動重開相機（sessionStorage，只限同一頁、30 秒內），從第一個空白欄開始。
//
// data-camera-field 的值：
//   item         解析成正式料號。選填 data-camera-allow-pack、data-camera-allow-inactive（查詢用）。
//                data-camera-qty="<數量欄 id>"：配合表單內的數量方式選單 [data-camera-qty-mode]（manual／pack／each）。
//   item-search  解析成物料就填料號並送出搜尋；不是物料碼就只填原文並提示，不自動搜尋。
//   location     解析儲位：data-camera-warehouse 是固定倉庫 Id（別的倉庫的儲位拒絕），或倉庫下拉選單的 id（儲位 QR 會改選單）。
//   raw          填原文（批號、序號、原廠條碼）；已登錄的物料／儲位碼（含 WH: QR）視為放錯欄。
//                選填 data-camera-kind-select（索引類型選單 id，不是原廠條碼時自動改）、data-camera-tracking="Lot|Serial"（盤點跳欄用）。
(function () {
  'use strict';

  if (!window.WhScan || !window.WhScanField) return;
  var clean = window.WhScan.clean;
  var feedback = window.WhScan.feedback;
  var show = window.WhScanField.show;
  var infoFor = window.WhScanField.infoFor;

  var REOPEN_KEY = 'wh.camera.reopen';
  var REOPEN_MS = 30000;
  var TRACKING = { None: '不追蹤', Lot: '批號管理', Serial: '序號管理' };
  var FORMAT_NAMES = {
    qr_code: 'QR Code', ean_13: 'EAN-13', ean_8: 'EAN-8', upc_a: 'UPC-A', upc_e: 'UPC-E', code_128: 'Code 128',
    code_39: 'Code 39', code_93: 'Code 93', itf: 'ITF', codabar: 'Codabar', data_matrix: 'Data Matrix'
  };

  var camera = null;
  var session = 0;
  var box = null;
  var video = null;
  var state = null;
  var activeGroup = null; // 相機正在掃的表單
  var target = null;      // 當前欄
  var working = Promise.resolve(); // 掃到的結果依序處理（解析要等伺服器）
  var lastFocused = new WeakMap();  // 表單 → 最後點選的相機欄位
  var groupItem = new WeakMap();    // 表單 → 最後解析到的物料（決定批號／序號要不要跳）
  var filling = false;              // 相機自己填值時觸發的 input 事件不算「人改了」

  function groupOf(input) { return input.form || input.closest('form') || document.body; }
  function visible(el) { return el.isConnected && el.getClientRects().length > 0 && !el.disabled; }
  function fields(group) {
    return Array.prototype.filter.call(group.querySelectorAll('input[data-camera-field]'), visible);
  }

  function labelOf(input) {
    var label = input.id && document.querySelector('label[for="' + input.id + '"]');
    var text = label ? label.textContent : input.getAttribute('aria-label') || input.getAttribute('placeholder') || '這一欄';
    return text.replace(/[*＊\s]+$/g, '').trim();
  }

  // 輸入欄與訊息列包在同一個 .camera-field 裡：表單格線（label｜欄位）與 .filters 一列的排列不會被打亂
  function wrap(input) {
    if (input.parentElement.classList.contains('camera-field')) return;
    var next = input.nextElementSibling;
    var info = next && next.classList.contains('scan-field-info') ? next : null; // scan-field.js 先建好的訊息列一起搬
    var focused = document.activeElement === input; // 搬動元素會失焦（autofocus 的欄位要放回焦點）
    var holder = document.createElement('div');
    holder.className = 'camera-field';
    input.insertAdjacentElement('beforebegin', holder);
    holder.appendChild(input);
    if (info) holder.appendChild(info);
    if (focused) input.focus();
  }

  function addBar(group) {
    if (group.querySelector('.camera-field-bar')) return;
    var bar = document.createElement('div');
    bar.className = 'camera-field-bar';
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'secondary camera-field-open';
    button.textContent = '相機掃碼';
    var hint = document.createElement('span');
    hint.className = 'muted camera-field-hint';
    hint.setAttribute('role', 'status');
    bar.appendChild(button);
    bar.appendChild(hint);
    var slot = group.querySelector('[data-camera-bar]');
    if (slot) {
      slot.appendChild(bar);
      return;
    }
    // 第一個相機欄位往上找到表單的直接子元素，按鈕列放在它前面
    var node = group.querySelector('input[data-camera-field]').parentElement;
    while (node.parentElement && node.parentElement !== group) node = node.parentElement;
    node.insertAdjacentElement('beforebegin', bar);
  }

  function enhance(root) {
    (root || document).querySelectorAll('input[data-camera-field]').forEach(function (input) {
      wrap(input);
      infoFor(input);
      addBar(groupOf(input));
    });
    (root || document).querySelectorAll('select[data-camera-qty-mode]').forEach(initQtyMode);
  }

  // ---- 數量方式（盤點、建立預留）：記在帳號的常用設定（prefs.js），沒有時用這台裝置 ----
  function initQtyMode(select) {
    if (select.dataset.ready) return;
    select.dataset.ready = '1';
    var key = select.getAttribute('data-camera-qty-mode');
    var saved = null;
    if (window.WhPrefs) saved = window.WhPrefs.get(key, null);
    else { try { saved = window.localStorage.getItem(key); } catch (e) { saved = null; } }
    if (saved && select.querySelector('option[value="' + saved + '"]')) select.value = saved;
    select.addEventListener('change', function () {
      if (window.WhPrefs) window.WhPrefs.set(key, select.value);
      else { try { window.localStorage.setItem(key, select.value); } catch (e) { /* 略過 */ } }
    });
  }
  function qtyMode(group) {
    var select = group.querySelector('select[data-camera-qty-mode]');
    return select ? select.value : 'manual';
  }

  // ---- 相機畫面（整頁共用一個，開在哪個表單就搬到那個表單的按鈕列） ----
  function ensureBox() {
    if (box) return;
    box = document.createElement('div');
    box.className = 'scan-camera-box camera-field-box';
    box.hidden = true;
    video = document.createElement('video');
    video.setAttribute('playsinline', '');
    video.setAttribute('muted', '');
    video.muted = true;
    var bar = document.createElement('div');
    state = document.createElement('span');
    state.className = 'muted';
    var stopButton = document.createElement('button');
    stopButton.type = 'button';
    stopButton.className = 'secondary camera-field-stop';
    stopButton.textContent = '停止相機';
    stopButton.addEventListener('click', function () {
      var input = target;
      session++; // 停止後還在等伺服器的結果不再填值（審查 4）
      forgetReopen();
      if (camera) camera.stop();
      hideBox();
      if (input && input.isConnected) input.focus();
    });
    bar.appendChild(state);
    bar.appendChild(document.createTextNode(' '));
    bar.appendChild(stopButton);
    box.appendChild(video);
    box.appendChild(bar);
  }

  function hideBox() {
    if (box) box.hidden = true;
    activeGroup = null;
  }

  var lastState = '';
  function showState(text) {
    if (text) lastState = text;
    if (state) state.textContent = '相機：' + lastState + (lastState.indexOf('掃描中') === 0 && target ? '　掃入：' + labelOf(target) : '');
  }

  function setTarget(input) {
    target = input;
    if (activeGroup) showState();
  }

  function hint(group, text) {
    var h = group.querySelector('.camera-field-hint');
    if (h) h.textContent = text;
  }

  function firstBlank(group) {
    var list = fields(group);
    for (var i = 0; i < list.length; i++) if (!list[i].value.trim()) return list[i];
    return list[0] || null;
  }

  async function open(group, auto) {
    var reason = window.WhScan.cameraUnsupportedReason();
    if (reason) {
      hint(group, reason + ' 請直接輸入或用條碼槍。');
      return;
    }
    var mine = ++session;
    try {
      camera = camera || await import(window.WhScan.cameraModuleUrl());
    } catch (e) {
      hint(group, '相機模組載入失敗，請直接輸入或用條碼槍。');
      return;
    }
    if (mine !== session || !group.isConnected) return;
    ensureBox();
    group.querySelector('.camera-field-bar').appendChild(box);
    var last = lastFocused.get(group);
    var first = auto ? firstBlank(group) : (last && visible(last) ? last : firstBlank(group));
    hint(group, '');
    // 先 start 再顯示：start 會先停掉這頁其他的相機（連續掃碼面板、別的表單）
    camera.start(video, {
      onStop: function () { if (mine === session) hideBox(); },
      onState: function (text) { if (mine === session) showState(text); },
      onError: function (message) {
        if (mine !== session) return;
        hideBox();
        hint(group, auto ? '相機沒有自動開啟（' + message + '），請按「相機掃碼」繼續。' : message + ' 也可以直接輸入。');
        if (!auto) feedback('error');
      },
      onResult: function (text, ms, format) {
        if (mine !== session) return;
        // 輪到它處理時才讀當前欄：前一筆解析完跳欄後，下一筆填進跳過去的欄位（審查 5）
        working = working.then(function () {
          var input = target;
          if (mine !== session || !input || !input.isConnected) return undefined;
          return apply(group, input, clean(text), format);
        }).catch(function () { /* 單筆失敗已在欄位下方提示 */ });
      }
    }, { continuous: true });
    activeGroup = group;
    setTarget(first);
    if (first) first.focus();
    box.hidden = false;
    if (box.scrollIntoView) box.scrollIntoView({ block: 'nearest' });
  }

  // ---- 送出後自動重開 ----
  function groupKey(group) {
    if (group.id) return group.id;
    return String(Array.prototype.indexOf.call(document.querySelectorAll('form'), group));
  }
  function rememberReopen(group) {
    try { window.sessionStorage.setItem(REOPEN_KEY, JSON.stringify({ path: location.pathname, form: groupKey(group), at: Date.now() })); } catch (e) { /* 略過 */ }
  }
  function forgetReopen() {
    try { window.sessionStorage.removeItem(REOPEN_KEY); } catch (e) { /* 略過 */ }
  }
  function reopenIfAsked() {
    var saved = null;
    try { saved = JSON.parse(window.sessionStorage.getItem(REOPEN_KEY) || 'null'); } catch (e) { saved = null; }
    forgetReopen();
    if (!saved || saved.path !== location.pathname || Date.now() - saved.at > REOPEN_MS) return;
    var group = Array.prototype.filter.call(document.querySelectorAll('form'), function (f) { return groupKey(f) === saved.form; })[0];
    if (group && group.querySelector('.camera-field-bar')) open(group, true);
  }

  // ---- 跳欄 ----
  // 盤點的批號／序號欄（data-camera-tracking）：只有剛掃到的物料用該追蹤方式才跳過去
  function wanted(group, input) {
    var need = input.getAttribute('data-camera-tracking');
    if (!need) return true;
    var item = groupItem.get(group);
    return !!item && item.trackingMode === need;
  }

  function advance(group, input) {
    var list = fields(group);
    var at = list.indexOf(input);
    var row = input.closest('.barcode-row');
    if (row) {
      // 原廠條碼列：下一個空白列，沒有就新增一列（達上限就停在這一列）
      for (var i = at + 1; i < list.length; i++) {
        if (list[i].closest('.barcode-row') && !list[i].value.trim()) { setTarget(list[i]); list[i].focus(); return; }
      }
      var added = window.WhItemBarcodes ? window.WhItemBarcodes.addRow() : null;
      var text = added && added.querySelector('input[data-camera-field]');
      if (text) { setTarget(text); text.focus(); }
      else hint(group, '條碼列已達上限。');
      return;
    }
    var order = list.slice(at + 1).concat(list.slice(0, at));
    for (var j = 0; j < order.length; j++) {
      if (!order[j].value.trim() && wanted(group, order[j])) { setTarget(order[j]); order[j].focus(); return; }
    }
    // 沒有空白的相機欄：逐件累加時目標回到料號欄繼續累加；否則焦點移到要人填的欄位（相機目標不變）
    var qtyField = group.querySelector('input[data-camera-qty]');
    if (qtyField && qtyMode(group) === 'each' && visible(qtyField)) {
      setTarget(qtyField);
    }
    var nextId = input.getAttribute('data-scan-next') || (qtyField && qtyField.getAttribute('data-camera-qty'));
    var next = nextId && document.getElementById(nextId);
    if (next && visible(next) && qtyMode(group) !== 'each') next.focus();
  }

  // ---- 掃到一筆 ----
  function formatText(format) {
    return format && FORMAT_NAMES[format] ? '（' + FORMAT_NAMES[format] + '）' : '';
  }

  async function scanApi(text, warehouseId) {
    var url = '/api/scan?code=' + encodeURIComponent(text) + (warehouseId ? '&warehouseId=' + encodeURIComponent(warehouseId) : '');
    var res = await fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
    if (res.ok && (res.headers.get('Content-Type') || '').indexOf('json') >= 0) return await res.json();
    throw new Error(res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '查詢失敗（HTTP ' + res.status + '）。');
  }

  function wrongField(input, text) {
    show(input, 'bad', text + '這一欄是「' + labelOf(input) + '」，沒有填入；要填別的欄位請先點那一欄。');
    feedback('error');
    return false;
  }

  async function apply(group, input, text, format) {
    if (!text) return;
    var mode = input.getAttribute('data-camera-field');
    var ok;
    try {
      if (mode === 'raw') ok = await applyRaw(group, input, text, format);
      else if (mode === 'location') ok = await applyLocation(input, text);
      else ok = await applyItem(group, input, text, format, mode === 'item-search');
    } catch (e) {
      show(input, 'bad', e && e.message ? e.message : '網路錯誤，請確認連線後再試。');
      feedback('error');
      return;
    }
    if (ok === 'stay' || !ok || activeGroup !== group) return;
    advance(group, input);
  }

  async function applyItem(group, input, text, format, search) {
    var body = await scanApi(text);
    var item = body.kind === 'Item' ? body.item : null;
    if (!item) {
      if (body.kind === 'Location') return wrongField(input, '「' + text + '」是儲位 ' + body.location.warehouseCode + ' 倉 ' + body.location.code + '，');
      if (search) {
        input.value = text;
        show(input, 'warn', '「' + text + '」' + formatText(format) + '不是已登錄的物料碼，已填入原文；要用這段文字搜尋請按「搜尋」。');
        feedback('warn');
        return 'stay';
      }
      show(input, 'bad', body.message || '「' + text + '」' + formatText(format) + '不是已登錄的料號或原廠條碼，沒有填入。');
      feedback('error');
      return false;
    }
    var qtyId = input.getAttribute('data-camera-qty');
    var qtyInput = qtyId && document.getElementById(qtyId);
    var qmode = qtyInput ? qtyMode(group) : 'manual';
    var allowPack = search || input.hasAttribute('data-camera-allow-pack') || qmode !== 'manual';
    var allowInactive = search || input.hasAttribute('data-camera-allow-inactive');
    if (!item.isActive && !allowInactive) {
      show(input, 'bad', '物料 ' + item.itemCode + ' 已停用，沒有填入。');
      feedback('error');
      return false;
    }
    var pack = Number(item.packQuantity);
    if (pack !== 1 && !allowPack) {
      show(input, 'bad', '這是外箱條碼（一次代表 ' + pack + ' ' + item.unitCode + ' ' + item.itemCode + '），請掃單件條碼或直接輸入料號，數量填基本單位；或把「數量方式」改成帶入包裝量／逐件累加。');
      feedback('error');
      return false;
    }
    // 數量欄只能是一筆序號（盤點）：序號物料的外箱條碼不收
    var serialOne = qtyInput && qtyInput.hasAttribute('data-camera-serial-one') && item.trackingMode === 'Serial';
    if (serialOne && pack !== 1) {
      show(input, 'bad', '序號管理的物料一行只能是一個序號，請掃單件條碼，不要掃外箱條碼。');
      feedback('error');
      return false;
    }
    var counted = qtyInput ? Number(qtyInput.value || 0) : 0;
    // 「目前累加的物料」只認相機填的標記，且料號欄現值也要相同：手改或條碼槍換了料號、手改了數量，都不算同一物料（審查 1）
    var countedItem = qtyInput ? qtyInput.dataset.cameraItem || '' : '';
    var sameItem = countedItem === item.itemCode && input.value.trim() === item.itemCode;
    if (qmode === 'each' && counted > 0 && !sameItem) {
      show(input, 'bad', '數量已有 ' + counted + (countedItem ? '（' + countedItem + '）' : '（手動輸入）') + '，掃到的是 ' + item.itemCode + '，沒有填入；請先按記錄／建立，或清空數量再掃。');
      feedback('error');
      return false;
    }
    filling = true;
    try {
      input.value = item.itemCode;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dataset.resolved = item.itemCode; // input 事件會清掉 scan-field.js 的標記，再設一次（鍵盤 Enter 不必重查）
      groupItem.set(group, item);
      var qtyNote = '';
      if (qtyInput) {
        var places = Number(item.decimalPlaces) || 0;
        var round = function (n) { return String(Number(n.toFixed(places))); }; // 小數包裝量累加不留浮點誤差（審查 7）
        if (serialOne) qtyInput.value = '1';
        else if (qmode === 'pack') qtyInput.value = round(pack);
        else if (qmode === 'each') qtyInput.value = round((sameItem ? counted : 0) + pack);
        else if (countedItem && countedItem !== item.itemCode) qtyInput.value = ''; // 換物料：清掉相機帶的舊數量（審查 3）
        if (qmode !== 'manual' || serialOne) {
          qtyInput.dataset.cameraItem = item.itemCode;
          qtyNote = '　數量 ' + qtyInput.value;
        } else {
          delete qtyInput.dataset.cameraItem;
        }
      }
    } finally {
      filling = false;
    }
    show(input, 'ok', item.itemCode + ' ' + item.name + '（單位 ' + item.unitCode + '，' + (TRACKING[item.trackingMode] || item.trackingMode)
      + (pack !== 1 ? '，外箱條碼一次 ' + pack + ' ' + item.unitCode : '') + (item.isActive ? '' : '，已停用') + '）' + qtyNote);
    feedback('ok');
    if (search) {
      if (input.form) {
        rememberReopen(group);
        input.form.submit();
      }
      return 'stay';
    }
    return qmode === 'each' && sameItem ? 'stay' : true;
  }

  async function applyRaw(group, input, text, format) {
    var body = await scanApi(text);
    if (/^WH:/i.test(text) || body.kind === 'Item' || body.kind === 'Location') {
      var what = body.kind === 'Item' ? '物料 ' + body.item.itemCode + ' ' + body.item.name
        : body.kind === 'Location' ? '儲位 ' + body.location.warehouseCode + ' 倉 ' + body.location.code : '本系統的 QR';
      return wrongField(input, '「' + text + '」是' + what + '，不是' + labelOf(input) + '；');
    }
    if (input.closest('.barcode-row')) {
      var others = Array.prototype.filter.call(group.querySelectorAll('.barcode-row input[data-camera-field]'), function (x) {
        return x !== input && x.value.trim() === text;
      });
      if (others.length) {
        show(input, 'bad', '「' + text + '」已經在其他條碼列，沒有重複填入。');
        feedback('warn');
        return false;
      }
    }
    var note = '';
    var kindSelect = input.getAttribute('data-camera-kind-select') && document.getElementById(input.getAttribute('data-camera-kind-select'));
    if (kindSelect && kindSelect.value !== 'Barcode') {
      kindSelect.value = 'Barcode';
      kindSelect.dispatchEvent(new Event('change', { bubbles: true }));
      note = '，索引類型已改為「原廠條碼」';
    }
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    show(input, 'ok', '已填入「' + text + '」' + formatText(format) + note + '，請確認後再送出。');
    feedback('ok');
    return true;
  }

  async function applyLocation(input, text) {
    var warehouse = input.getAttribute('data-camera-warehouse') || '';
    var select = warehouse && document.getElementById(warehouse);
    // 倉庫下拉：先不指定倉庫（儲位 QR 自帶倉庫）；只是儲位碼文字時再用目前選的倉庫找
    var body = await scanApi(text, select ? null : warehouse);
    if (select && body.kind === 'Text' && select.value) body = await scanApi(text, select.value);
    if (body.kind === 'Item') return wrongField(input, '「' + text + '」是物料 ' + body.item.itemCode + ' ' + body.item.name + '，');
    var loc = body.kind === 'Location' ? body.location : null;
    if (!loc) {
      show(input, 'bad', body.message || '「' + text + '」不是已登錄的儲位' + (select && !select.value ? '（儲位碼文字要先選倉庫）' : '') + '，沒有填入。');
      feedback('error');
      return false;
    }
    if (loc.status === 'Inactive') {
      show(input, 'bad', '儲位 ' + loc.warehouseCode + ' 倉 ' + loc.code + ' 已停用，沒有填入。');
      feedback('error');
      return false;
    }
    var note = '';
    if (select && select.value !== loc.warehouseId) {
      var option = select.querySelector('option[value="' + loc.warehouseId + '"]');
      if (!option) {
        show(input, 'bad', '儲位 ' + loc.code + ' 屬於 ' + loc.warehouseCode + ' 倉，你沒有這個倉庫的權限或倉庫已停用。');
        feedback('error');
        return false;
      }
      select.value = loc.warehouseId;
      note = '（倉庫已改為 ' + loc.warehouseCode + '）';
    }
    input.value = loc.code;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    show(input, 'ok', '儲位 ' + loc.warehouseCode + ' 倉 ' + loc.code + (loc.name ? ' ' + loc.name : '') + note);
    feedback('ok');
    return true;
  }

  // ---- 事件 ----
  document.addEventListener('click', function (ev) {
    var button = ev.target.closest ? ev.target.closest('.camera-field-open') : null;
    if (!button) return;
    ev.preventDefault();
    var group = button.form || button.closest('form') || document.body;
    // 同一張表單的相機已開著：不重開
    if (activeGroup === group && camera && camera.isRunning()) return;
    open(group, false);
  });

  // 點選相機欄位：記住當前欄；相機開著就只換目標
  document.addEventListener('focusin', function (ev) {
    var input = ev.target;
    if (!input || input.tagName !== 'INPUT' || !input.hasAttribute('data-camera-field')) return;
    var group = groupOf(input);
    lastFocused.set(group, input);
    if (activeGroup === group) setTarget(input);
  });

  // 相機開著時送出表單（人按記錄／建立）：重新載入後自動重開
  document.addEventListener('submit', function (ev) {
    if (ev.defaultPrevented) return; // 被其他程式擋下的送出不算（審查 6）
    if (activeGroup && ev.target === activeGroup && camera && camera.isRunning()) rememberReopen(activeGroup);
  });

  // 人改了料號欄或數量欄（手打、條碼槍）：數量不再算是相機累加的
  document.addEventListener('input', function (ev) {
    if (filling) return;
    var el = ev.target;
    if (!el || el.tagName !== 'INPUT') return;
    var qtyId = el.getAttribute('data-camera-qty');
    var qty = qtyId ? document.getElementById(qtyId) : (el.id && document.querySelector('input[data-camera-qty="' + el.id + '"]') ? el : null);
    if (qty) delete qty.dataset.cameraItem;
  });

  // 某個區塊要被整塊換掉（/Scan 查料換結果區）前呼叫：相機正掃這個區塊裡的表單就關掉，相機畫面搬出來，
  // 不然畫面被移除後鏡頭還開著、看不到「停止相機」
  function release(root) {
    if (!box || !root.contains(box)) return;
    if (activeGroup && camera) camera.stop();
    session++;
    hideBox();
    document.body.appendChild(box);
  }

  window.WhCameraField = { enhance: enhance, release: release };
  enhance(document);
  reopenIfAsked();
})();
