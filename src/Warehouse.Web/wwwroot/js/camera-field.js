// 單欄相機掃碼（docs/04 §6.2）：在 <input data-camera-field="…"> 後面加「相機」按鈕，掃一次就停，結果填進「按下按鈕的那一欄」。
// 不使用框架，相容 Chrome 109。需要先載入 scan-input.js（提示音、相機模組網址）與 scan-field.js（物料解析、欄位下方訊息列）。
// 本檔不含相機與解碼程式：按下按鈕才 import camera-scan.js（再由它載入解碼器），條碼槍、PDA 不會載入。
//
// 版面：輸入欄 → 相機按鈕 → 結果訊息（.scan-field-info，就在輸入欄下方）→ 相機畫面（共用一個，按下時搬到該欄訊息下面）。
// 目標：按下按鈕時就綁定那一欄（不看最後焦點）；每按一次換一個代號，舊的相機結果、晚到的回應都不會寫到別的欄位。
// data-camera-field 的值：
//   item         解析成正式料號（與鍵盤 data-scan-field 同一套規則：停用、外箱條碼拒絕）。
//                選填 data-camera-allow-pack、data-camera-allow-inactive（查詢用：外箱條碼、停用物料也換成料號）。
//   item-search  解析成物料就填料號並送出搜尋；不是物料碼就只填原文並提示，不自動搜尋。
//   location     解析儲位：data-camera-warehouse 是固定倉庫 Id（別的倉庫的儲位拒絕），
//                或倉庫下拉選單的 id（儲位 QR 會把選單改成該儲位的倉庫）。停用儲位拒絕。
//   raw          填清理後的原文（批號、序號、原廠條碼），不查成料號、不送出表單。
//                選填 data-camera-kind-select：索引類型下拉選單的 id，不是「原廠條碼」時自動改成原廠條碼並說明。
// 動態內容：點擊用事件委派（複製出來的條碼列也能用）；整塊換掉的區域呼叫 window.WhCameraField.enhance(root) 補上按鈕。
(function () {
  'use strict';

  if (!window.WhScan || !window.WhScanField) return;
  var clean = window.WhScan.clean;
  var feedback = window.WhScan.feedback;
  var show = window.WhScanField.show;
  var infoFor = window.WhScanField.infoFor;

  var FORMAT_NAMES = {
    qr_code: 'QR Code', ean_13: 'EAN-13', ean_8: 'EAN-8', upc_a: 'UPC-A', upc_e: 'UPC-E', code_128: 'Code 128',
    code_39: 'Code 39', code_93: 'Code 93', itf: 'ITF', codabar: 'Codabar', data_matrix: 'Data Matrix'
  };

  var camera = null;
  var session = 0;
  var box = null;
  var video = null;
  var state = null;
  var activeInput = null;

  function labelOf(input) {
    var label = input.id && document.querySelector('label[for="' + input.id + '"]');
    var text = label ? label.textContent : input.getAttribute('aria-label') || input.getAttribute('placeholder') || '這一欄';
    return text.replace(/[*＊\s]+$/g, '').trim();
  }

  // 輸入欄、按鈕、訊息、相機畫面包在同一個 .camera-field 裡：表單格線（label｜欄位）與 .filters 一列的排列不會被打亂
  function wrap(input) {
    if (input.parentElement.classList.contains('camera-field')) return;
    var next = input.nextElementSibling;
    var info = next && next.classList.contains('scan-field-info') ? next : null; // scan-field.js 先建好的訊息列一起搬
    var focused = document.activeElement === input; // 搬動元素會失焦（autofocus 的欄位要放回焦點）
    var box = document.createElement('div');
    box.className = 'camera-field';
    input.insertAdjacentElement('beforebegin', box);
    box.appendChild(input);
    if (info) box.appendChild(info);
    if (focused) input.focus();
  }

  function enhance(root) {
    (root || document).querySelectorAll('input[data-camera-field]').forEach(function (input) {
      var next = input.nextElementSibling;
      if (next && next.classList.contains('camera-field-button')) return;
      wrap(input);
      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'secondary camera-field-button';
      button.textContent = '相機';
      button.setAttribute('aria-label', '用相機掃描：' + labelOf(input));
      input.insertAdjacentElement('afterend', button);
      infoFor(input); // 訊息列放在按鈕後面（輸入欄下方）
    });
  }

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
    stopButton.className = 'secondary';
    stopButton.textContent = '停止相機';
    stopButton.addEventListener('click', function () {
      var input = activeInput;
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
    activeInput = null;
  }

  async function open(input) {
    var reason = window.WhScan.cameraUnsupportedReason();
    if (reason) {
      show(input, 'bad', reason + ' 請直接輸入或用條碼槍。');
      return;
    }
    var mine = ++session;
    try {
      camera = camera || await import(window.WhScan.cameraModuleUrl());
    } catch (e) {
      show(input, 'bad', '相機模組載入失敗，請直接輸入或用條碼槍。');
      return;
    }
    if (mine !== session || !input.isConnected) return;
    ensureBox();
    infoFor(input).insertAdjacentElement('afterend', box);
    var label = labelOf(input);
    // 先 start 再顯示：start 會先停掉這頁其他的相機（連續掃碼面板、上一個欄位）
    camera.start(video, {
      onStop: function () { if (mine === session) hideBox(); },
      onState: function (text) {
        if (mine === session) state.textContent = '相機：' + text + (text.indexOf('掃描中') === 0 ? '　掃入：' + label : '');
      },
      onError: function (message) {
        if (mine !== session) return;
        hideBox();
        show(input, 'bad', message + ' 也可以直接輸入。');
        feedback('error');
      },
      onResult: function (text, ms, format) {
        if (mine !== session) return;
        hideBox();
        if (input.isConnected) apply(input, clean(text), format);
      }
    }, { continuous: false });
    activeInput = input;
    box.hidden = false;
    if (box.scrollIntoView) box.scrollIntoView({ block: 'nearest' });
  }

  function formatText(format) {
    return format && FORMAT_NAMES[format] ? '（' + FORMAT_NAMES[format] + '）' : '';
  }

  async function apply(input, text, format) {
    if (!text) return;
    var mode = input.getAttribute('data-camera-field');
    input.focus();
    if (mode === 'raw') {
      applyRaw(input, text, format);
    } else if (mode === 'location') {
      await applyLocation(input, text);
    } else if (mode === 'item-search') {
      var found = await window.WhScanField.resolveItem(input, text, { allowPack: true, allowInactive: true, focusNext: false });
      if (found) {
        if (input.form) input.form.submit();
      } else {
        input.value = text;
        show(input, 'warn', '「' + text + '」' + formatText(format) + '不是已登錄的物料碼，已填入原文；要用這段文字搜尋請按「搜尋」。');
      }
    } else {
      await window.WhScanField.resolveItem(input, text, {
        allowPack: input.hasAttribute('data-camera-allow-pack'),
        allowInactive: input.hasAttribute('data-camera-allow-inactive')
      });
    }
  }

  function applyRaw(input, text, format) {
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
  }

  async function scanApi(text, warehouseId) {
    var url = '/api/scan?code=' + encodeURIComponent(text) + (warehouseId ? '&warehouseId=' + encodeURIComponent(warehouseId) : '');
    var res = await fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
    if (res.ok && (res.headers.get('Content-Type') || '').indexOf('json') >= 0) return await res.json();
    throw new Error(res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '查詢失敗（HTTP ' + res.status + '）。');
  }

  async function applyLocation(input, text) {
    var target = input.getAttribute('data-camera-warehouse') || '';
    var select = target && document.getElementById(target);
    try {
      // 倉庫下拉：先不指定倉庫（儲位 QR 自帶倉庫）；只是儲位碼文字時再用目前選的倉庫找
      var body = await scanApi(text, select ? null : target);
      if (select && body.kind === 'Text' && select.value) body = await scanApi(text, select.value);
      var loc = body.kind === 'Location' ? body.location : null;
      if (!loc) {
        show(input, 'bad', body.message || '「' + text + '」不是已登錄的儲位' + (select && !select.value ? '（儲位碼文字要先選倉庫）' : '') + '。');
        feedback('error');
        return;
      }
      if (loc.status === 'Inactive') {
        show(input, 'bad', '儲位 ' + loc.warehouseCode + ' 倉 ' + loc.code + ' 已停用。');
        feedback('error');
        return;
      }
      var note = '';
      if (select && select.value !== loc.warehouseId) {
        var option = select.querySelector('option[value="' + loc.warehouseId + '"]');
        if (!option) {
          show(input, 'bad', '儲位 ' + loc.code + ' 屬於 ' + loc.warehouseCode + ' 倉，你沒有這個倉庫的權限或倉庫已停用。');
          feedback('error');
          return;
        }
        select.value = loc.warehouseId;
        note = '（倉庫已改為 ' + loc.warehouseCode + '）';
      }
      input.value = loc.code;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      show(input, 'ok', '儲位 ' + loc.warehouseCode + ' 倉 ' + loc.code + (loc.name ? ' ' + loc.name : '') + note);
      feedback('ok');
    } catch (e) {
      show(input, 'bad', e && e.message ? e.message : '網路錯誤，請確認連線後再試。');
      feedback('error');
    }
  }

  document.addEventListener('click', function (ev) {
    var button = ev.target.closest ? ev.target.closest('.camera-field-button') : null;
    if (!button) return;
    var input = button.previousElementSibling;
    if (!input || !input.hasAttribute('data-camera-field')) return;
    ev.preventDefault();
    open(input);
  });

  window.WhCameraField = { enhance: enhance };
  enhance(document);
})();
