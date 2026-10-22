// 單欄掃碼（M5-03，docs/04 §6）：只有一個料號欄的表單（建立預留、期初「查建議儲位」）。不使用框架，相容 Chrome 109。
// 欄位加 data-scan-field，選填 data-scan-next（下一個欄位的 id）。
// Enter／Tab 時不送出表單，改呼叫 /api/scan 精確解析（料號、WH:ITEM: QR、原廠條碼、專屬別名）：
//  - 物料：欄位換成正式料號、顯示品名（外箱條碼另外提示包裝數量），焦點移到下一個欄位。
//  - 其他：顯示原因，欄位保留原文讓人修改；不猜物料、不送出表單。
// 已解析過的值再按 Enter 才照常送出表單。需要先載入 scan-input.js（提示音）；不載入相機程式。
(function () {
  'use strict';

  var feedback = window.WhScan ? window.WhScan.feedback : function () { };
  var clean = window.WhScan ? window.WhScan.clean : function (v) { return String(v || '').trim(); };

  document.querySelectorAll('input[data-scan-field]').forEach(function (input) {
    var info = document.createElement('div');
    info.className = 'muted scan-field-info';
    info.setAttribute('role', 'status');
    input.insertAdjacentElement('afterend', info);
    var busy = false;

    input.addEventListener('input', function () { delete input.dataset.resolved; });
    input.addEventListener('keydown', async function (ev) {
      if ((ev.key !== 'Enter' && ev.key !== 'Tab') || ev.isComposing) return;
      var value = clean(input.value);
      if (!value || input.dataset.resolved === value) return; // 空白或已解析：照常（Tab 換欄、Enter 送出）
      ev.preventDefault();
      if (busy) return;
      busy = true;
      try {
        var res = await fetch('/api/scan?code=' + encodeURIComponent(value), { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' });
        var body = res.ok && (res.headers.get('Content-Type') || '').indexOf('json') >= 0 ? await res.json() : null;
        if (body && body.kind === 'Item' && body.item.isActive) {
          var item = body.item;
          input.value = item.itemCode;
          input.dataset.resolved = item.itemCode;
          info.className = 'ok scan-field-info';
          info.textContent = item.itemCode + ' ' + item.name + '（單位 ' + item.unitCode + '）'
            + (item.packQuantity !== 1 ? '　這是外箱條碼：一箱 ' + Number(item.packQuantity) + ' ' + item.unitCode + '，數量請填基本單位' : '');
          feedback(item.packQuantity !== 1 ? 'warn' : 'ok');
          var next = input.getAttribute('data-scan-next') && document.getElementById(input.getAttribute('data-scan-next'));
          if (next) next.focus();
        } else {
          info.className = 'bad scan-field-info';
          info.textContent = body && body.kind === 'Item' ? '物料 ' + body.item.itemCode + ' 已停用。'
            : body && body.message ? body.message
            : body ? '「' + value + '」不是已登錄的料號或原廠條碼，請用搜尋確認。'
            : res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '查詢失敗（HTTP ' + res.status + '）。';
          feedback('error');
        }
      } catch (e) {
        info.className = 'bad scan-field-info';
        info.textContent = '網路錯誤，請確認連線後再試。';
        feedback('error');
      } finally {
        busy = false;
      }
    });
  });
})();
