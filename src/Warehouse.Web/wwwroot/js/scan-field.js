// 單欄掃碼（M5-03，docs/04 §6）：只有一個料號欄的表單（建立預留、期初「查建議儲位」）。不使用框架，相容 Chrome 109。
// 欄位加 data-scan-field，選填 data-scan-next（下一個欄位的 id）。
// Enter／Tab 時不送出表單，改呼叫 /api/scan 精確解析（料號、WH:ITEM: QR、原廠條碼、專屬別名）：
//  - 物料（單件）：欄位換成正式料號、顯示品名，焦點移到下一個欄位。外箱條碼不換（數量是基本單位），提示改掃單件或輸入料號。
//  - 其他：顯示原因，欄位保留原文讓人修改；不猜物料、不送出表單（同一個值再按 Tab 就放行，Shift+Tab 一律放行）。
// 已解析過的值再按 Enter 才照常送出表單。需要先載入 scan-input.js（提示音）；不載入相機程式。
// 解析邏輯另外以 window.WhScanField.resolveItem 提供給單欄相機（camera-field.js），鍵盤與相機走同一套規則；
// 結果一律顯示在輸入欄正下方的 .scan-field-info（infoFor）。
(function () {
  'use strict';

  var feedback = window.WhScan ? window.WhScan.feedback : function () { };
  var clean = window.WhScan ? window.WhScan.clean : function (v) { return String(v || '').trim(); };

  // 輸入欄正下方的訊息列（沒有就建立）
  function infoFor(input) {
    var next = input.nextElementSibling;
    if (next && next.classList.contains('scan-field-info')) return next;
    var info = document.createElement('div');
    info.className = 'muted scan-field-info';
    info.setAttribute('role', 'status');
    input.insertAdjacentElement('afterend', info);
    return info;
  }

  function show(input, kind, text) {
    var info = infoFor(input);
    info.className = kind + ' scan-field-info';
    info.textContent = text;
  }

  // 解析成正式料號並填入。opts.allowPack：外箱條碼也換成料號（查詢用，不涉及數量）；opts.allowInactive：停用物料也可（查詢用）。
  // 回傳是否成功；失敗時欄位保留原文、訊息說明原因
  async function resolveItem(input, value, opts) {
    opts = opts || {};
    var before = input.value;
    try {
      var res = await fetch('/api/scan?code=' + encodeURIComponent(value), { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
      var body = res.ok && (res.headers.get('Content-Type') || '').indexOf('json') >= 0 ? await res.json() : null;
      // 等回應期間使用者改了欄位（例如相機還在查、先手打了別的料號）：晚到的結果不蓋掉
      if (input.value !== before) return false;
      var item = body && body.kind === 'Item' ? body.item : null;
      if (item && (item.isActive || opts.allowInactive) && (item.packQuantity === 1 || opts.allowPack)) {
        input.value = item.itemCode;
        input.dataset.resolved = item.itemCode;
        delete input.dataset.failed;
        show(input, 'ok', item.itemCode + ' ' + item.name + '（單位 ' + item.unitCode + '）'
          + (item.packQuantity !== 1 ? '　外箱條碼，一次代表 ' + Number(item.packQuantity) + ' ' + item.unitCode : '')
          + (item.isActive ? '' : '　已停用'));
        feedback('ok');
        var next = opts.focusNext !== false && input.getAttribute('data-scan-next') && document.getElementById(input.getAttribute('data-scan-next'));
        if (next) next.focus();
        return true;
      }
      input.dataset.failed = value;
      // 外箱條碼不換成料號（與伺服器表單規則一致）：這裡的數量是基本單位，換掉就看不出是「一箱」（M5-03 審查 S2）
      show(input, 'bad', item && !item.isActive ? '物料 ' + item.itemCode + ' 已停用。'
        : item ? '這是外箱條碼（一次代表 ' + Number(item.packQuantity) + ' ' + item.unitCode + ' ' + item.itemCode + '），請掃單件條碼或直接輸入料號，數量填基本單位。'
        : body && body.message ? body.message
        : body ? '「' + value + '」不是已登錄的料號或原廠條碼，請用搜尋確認。'
        : res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '查詢失敗（HTTP ' + res.status + '）。');
      feedback('error');
      return false;
    } catch (e) {
      show(input, 'bad', '網路錯誤，請確認連線後再試。');
      feedback('error');
      return false;
    }
  }

  window.WhScanField = { infoFor: infoFor, show: show, resolveItem: resolveItem };

  document.querySelectorAll('input[data-scan-field]').forEach(function (input) {
    infoFor(input);
    var busy = false;

    input.addEventListener('input', function () { delete input.dataset.resolved; delete input.dataset.failed; });
    input.addEventListener('keydown', async function (ev) {
      if ((ev.key !== 'Enter' && ev.key !== 'Tab') || ev.isComposing) return;
      if (ev.key === 'Tab' && ev.shiftKey) return; // Shift+Tab 一律照常往回
      var value = clean(input.value);
      if (!value || input.dataset.resolved === value) return; // 空白或已解析：照常（Tab 換欄、Enter 送出）
      if (ev.key === 'Tab' && input.dataset.failed === value) return; // 同一個值已提示過查不到：Tab 放行，不困住鍵盤使用者
      ev.preventDefault();
      if (busy) return;
      busy = true;
      try {
        await resolveItem(input, value);
      } finally {
        busy = false;
      }
    });
  });
})();
