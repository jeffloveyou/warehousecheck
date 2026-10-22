// 共用掃碼輸入模組（M5-03，ADR-0016，docs/04 §6）。不使用框架，相容 Chrome 109。
// 三種設備共用同一個佇列：
//  - USB 條碼槍、Android PDA（Keyboard Wedge）：等同快速鍵盤輸入，最後送 Enter 或 Tab。
//    收到尾碼時立刻取值並清空輸入框（焦點不離開），下一筆的字元可以馬上進來（ADR-0007：表單送出會遺失 44／50 筆）。
//  - 手機相機：按下「相機掃碼」才 import('/js/camera-scan.js')（T-PERF-05），讀到的內容送進同一個佇列。
// 佇列由單一 worker 依序交給頁面的 onScan（例如 doc-scan.js 呼叫伺服器解析），結果順序與掃碼順序一致。
// 不在每次按鍵呼叫 API；Enter 只觸發「解析」，不直接過帳。
// 焦點守衛：焦點不在任何輸入框時，條碼槍打進來的字直接接到掃碼框，並顯示提示。
(function () {
  'use strict';

  var MIN_LENGTH = 3;
  var muted = false;
  try { muted = window.localStorage.getItem('wh-scan-mute') === '1'; } catch (e) { /* 私密模式等：維持預設 */ }
  var audio = null;

  // 回饋：成功短嗶一聲、警告中音一聲、錯誤低音兩聲（瀏覽器內建合成音，不載入音檔）；Android 另外震動
  function feedback(kind) {
    if (!muted) {
      try {
        var Ctx = window.AudioContext || window.webkitAudioContext;
        audio = audio || (Ctx ? new Ctx() : null);
        if (audio) {
          var tones = kind === 'ok' ? [[1200, 0.07]] : kind === 'warn' ? [[700, 0.12]] : [[280, 0.16], [280, 0.16]];
          var t = audio.currentTime;
          tones.forEach(function (tone) {
            var osc = audio.createOscillator();
            var gain = audio.createGain();
            osc.frequency.value = tone[0];
            gain.gain.value = 0.12;
            osc.connect(gain);
            gain.connect(audio.destination);
            osc.start(t);
            osc.stop(t + tone[1]);
            t += tone[1] + 0.07;
          });
        }
      } catch (e) { /* 沒有音效裝置時略過 */ }
    }
    if (navigator.vibrate) {
      try { navigator.vibrate(kind === 'ok' ? 50 : [120, 80, 120]); } catch (e) { /* 略過 */ }
    }
  }

  function setMuted(value) {
    muted = !!value;
    try { window.localStorage.setItem('wh-scan-mute', muted ? '1' : '0'); } catch (e) { /* 略過 */ }
  }

  // 去掉控制字元（尾碼 CR／LF／Tab、GS）與頭尾空白；伺服器端 ScanInput 也會再做一次
  function clean(raw) {
    return String(raw || '').replace(/[\u0000-\u001f\u007f]/g, '').trim();
  }

  function isEditable(el) {
    if (!el) return false;
    if (el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable) return true;
    return el.tagName === 'INPUT' && ['checkbox', 'radio', 'button', 'submit', 'file'].indexOf(el.type) < 0;
  }

  // options：input、onScan(value, source, extra) 回傳 Promise、onReject(message)、
  //          container（焦點在它以外的輸入框也提示）、focusWarning、refocusButton、capsIndicator、muteCheckbox、
  //          cameraButton、cameraBox、cameraVideo、cameraState、cameraStop
  function attach(o) {
    var input = o.input;
    var queue = [];
    var working = false;
    var camera = null;

    function enqueue(value, source, extra) {
      queue.push({ value: value, source: source, extra: extra || null });
      drain();
    }

    async function drain() {
      if (working) return;
      working = true;
      while (queue.length > 0) {
        var job = queue.shift();
        try {
          await o.onScan(job.value, job.source, job.extra);
        } catch (e) {
          o.onReject('處理「' + job.value + '」時發生錯誤：' + (e && e.message ? e.message : e));
        }
      }
      working = false;
    }

    input.addEventListener('keydown', function (ev) {
      if (o.capsIndicator && ev.getModifierState) {
        o.capsIndicator.hidden = !ev.getModifierState('CapsLock');
      }
      if (ev.key !== 'Enter' && ev.key !== 'Tab') return;
      if (ev.isComposing) return; // 中文輸入法選字中的 Enter 不是掃碼尾碼
      var value = clean(input.value);
      if (value.length === 0 && ev.key === 'Tab') return; // 空白時 Tab 照常換欄（不形成鍵盤陷阱）
      ev.preventDefault(); // Tab 不讓焦點跳走，Enter 不送出任何表單
      input.value = '';
      if (value.length === 0) return;
      if (value.length < MIN_LENGTH) {
        o.onReject('只收到「' + value + '」，太短，可能漏字，請重掃。');
        return;
      }
      enqueue(value, 'keyboard');
    });

    // 焦點守衛：焦點離開輸入框 0.8 秒後仍沒回來才顯示提示。
    // 不立刻顯示：按按鈕時 mousedown 就會失焦，提示若馬上出現會把按鈕往下推，滑鼠放開時已不在按鈕上，按鈕就沒按到。
    var warnTimer = null;
    // 焦點在掃碼區（o.container，例如掃碼面板）以外的輸入框也提示：條碼槍的字與 Enter 會打進那個欄位、送出那個表單
    function focusOk() {
      var active = document.activeElement;
      return document.hasFocus() && isEditable(active) && (!o.container || o.container.contains(active));
    }
    function checkFocus() {
      if (!o.focusWarning) return;
      if (warnTimer) { clearTimeout(warnTimer); warnTimer = null; }
      if (focusOk()) {
        o.focusWarning.hidden = true;
        return;
      }
      warnTimer = setTimeout(function () {
        warnTimer = null;
        o.focusWarning.hidden = focusOk();
      }, 800);
    }
    document.addEventListener('focusin', checkFocus);
    document.addEventListener('focusout', function () { setTimeout(checkFocus, 0); });
    window.addEventListener('focus', checkFocus);
    window.addEventListener('blur', checkFocus);
    if (o.refocusButton) {
      o.refocusButton.addEventListener('click', function () { input.focus(); });
    }
    // 焦點不在任何輸入框（點了頁面空白處、表格、按鈕、連結）時，條碼槍的字元直接接到掃碼框，不會遺失。
    // 例外：在按鈕、連結上按空白鍵是「按下」，照常處理。第一個字轉進掃碼框後焦點就在掃碼框，尾碼 Enter 不會按到按鈕。
    document.addEventListener('keydown', function (ev) {
      var active = document.activeElement;
      if (isEditable(active)) return;
      if (ev.ctrlKey || ev.altKey || ev.metaKey || !ev.key || ev.key.length !== 1) return;
      if (ev.key === ' ' && active && active !== document.body && active !== document.documentElement) return;
      ev.preventDefault();
      input.focus();
      input.value += ev.key;
    }, true);

    if (o.muteCheckbox) {
      o.muteCheckbox.checked = muted;
      o.muteCheckbox.addEventListener('change', function () { setMuted(o.muteCheckbox.checked); input.focus(); });
    }

    // 相機：只在安全連線（HTTPS）且瀏覽器支援時顯示按鈕；按下才載入模組
    var canCamera = window.isSecureContext && navigator.mediaDevices && navigator.mediaDevices.getUserMedia;
    if (o.cameraButton && canCamera) {
      o.cameraButton.hidden = false;
      o.cameraButton.addEventListener('click', async function () {
        try {
          camera = camera || await import('/js/camera-scan.js');
        } catch (e) {
          o.onReject('相機模組載入失敗，請改用手動輸入。');
          return;
        }
        if (o.cameraBox) o.cameraBox.hidden = false;
        camera.start(o.cameraVideo, {
          onDecoder: function () { },
          onState: function (text) { if (o.cameraState) o.cameraState.textContent = '相機：' + text; },
          onResult: function (text, ms, format) { enqueue(clean(text), 'camera', { symbology: format || null }); },
          onError: function (message) {
            if (o.cameraBox) o.cameraBox.hidden = true;
            o.onReject(message + ' 也可以直接在掃碼框輸入。');
          }
        }, { continuous: true });
      });
      if (o.cameraStop) {
        o.cameraStop.addEventListener('click', function () {
          if (camera) camera.stop();
          if (o.cameraBox) o.cameraBox.hidden = true;
          input.focus();
        });
      }
    }

    checkFocus();
    return {
      focus: function () { input.focus(); },
      enqueue: enqueue,
      pending: function () { return queue.length + (working ? 1 : 0); }
    };
  }

  window.WhScan = { attach: attach, feedback: feedback, clean: clean, minLength: MIN_LENGTH };
})();
