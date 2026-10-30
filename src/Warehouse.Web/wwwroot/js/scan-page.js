// 掃碼查料頁（M5-03，docs/04 §6）：用共用掃碼模組接條碼槍、PDA、相機，每筆掃碼以背景請求取得查詢結果並換掉結果區，
// 頁面不重新載入（連續掃時字元不會遺失）。網址同步成 ?code=，重新整理或分享連結看到的是同一筆。
(function () {
  'use strict';

  var form = document.getElementById('scan-lookup-form');
  if (!form || !window.WhScan || !window.fetch) return;
  function $(id) { return document.getElementById(id); }
  var input = $('scan-input');
  input.removeAttribute('name'); // 改由 JS 處理，Enter 不送出表單

  // 「登錄為原廠條碼」表單填寫中：連續查料相機讀到的碼不換掉結果區（不然正在填的料號會消失）
  function registering() {
    var code = $('RegisterItemCode');
    return !!code && (code.value.trim() !== '' || document.activeElement === code || ($('RegisterPackQuantity') && $('RegisterPackQuantity').value !== ''));
  }

  async function lookup(value, source) {
    if (source === 'camera' && registering()) {
      if ($('scan-live')) $('scan-live').textContent = '登錄表單填寫中，相機讀到的「' + value + '」沒有查詢；要查詢請先登錄或清空料號欄。';
      var state = $('scan-camera-state');
      if (state) state.textContent = '相機：登錄表單填寫中，略過「' + value + '」';
      window.WhScan.feedback('warn');
      return;
    }
    var url = '/Scan?code=' + encodeURIComponent(value);
    try {
      var res = await fetch(url, { credentials: 'same-origin' });
      if (!res.ok || res.redirected) {
        throw new Error(res.redirected ? '登入已逾時，請重新整理頁面並登入。' : '查詢失敗（HTTP ' + res.status + '）');
      }
      var template = document.createElement('template');
      template.innerHTML = await res.text();
      var fresh = template.content.querySelector('[data-scan-result]');
      var old = document.querySelector('[data-scan-result]');
      if (!fresh || !old) throw new Error('查詢結果格式不正確');
      old.parentNode.replaceChild(fresh, old);
      if (window.WhCameraField) window.WhCameraField.enhance(fresh); // 新結果區的登錄欄也要有相機按鈕
      history.replaceState(null, '', url);
      var summary = fresh.querySelector('#scan-summary');
      if (summary && $('scan-live')) $('scan-live').textContent = summary.textContent;
      var kind = fresh.getAttribute('data-kind');
      window.WhScan.feedback(kind === 'Item' || kind === 'Location' ? 'ok' : kind === 'Text' ? 'warn' : 'error');
    } catch (e) {
      var old2 = document.querySelector('[data-scan-result]');
      old2.textContent = '';
      var div = document.createElement('div');
      div.className = 'message error';
      div.textContent = '「' + value + '」' + (e && e.message ? e.message : '查詢失敗');
      old2.appendChild(div);
      window.WhScan.feedback('error');
    }
  }

  window.WhScan.attach({
    input: input,
    onScan: lookup,
    onReject: function (message) {
      var old = document.querySelector('[data-scan-result]');
      old.textContent = '';
      var div = document.createElement('div');
      div.className = 'message error';
      div.textContent = message;
      old.appendChild(div);
      window.WhScan.feedback('error');
    },
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
  form.addEventListener('submit', function (ev) { ev.preventDefault(); });
  input.focus();
})();
