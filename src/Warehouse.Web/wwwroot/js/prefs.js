// 常用設定（M8-06，ADR-0024）：登入後跟著帳號走，換電腦、手機也一樣。不使用框架，相容 Chrome 109。
// 用到的頁面（明細輸入、掃碼、預留）由版面輸出 <script id="wh-prefs" type="application/json" data-token=…>，這裡不另外打 API 讀取。
// - get(key, fallback)：用伺服器的值。伺服器還沒有、這台裝置有 M8-05 以前留下的 localStorage 值時，上傳一次，
//   成功後刪掉本機的舊值——共用電腦上不會把前一位使用者的設定帶給下一位（審查 R2）。
// - set(key, value)：送到伺服器（keepalive：馬上離開頁面也會送出）；登入後不再寫 localStorage。
// 頁面沒有 #wh-prefs（未登入等）時只用 localStorage。
(function () {
  'use strict';

  var node = document.getElementById('wh-prefs');
  var server = {};
  try { server = node ? JSON.parse(node.textContent || '{}') || {} : {}; } catch (e) { server = {}; }
  var token = node ? node.getAttribute('data-token') : null;
  // 伺服器鍵 → M8-05 以前這台裝置用的 localStorage 鍵
  var LOCAL = { 'scan.addMode': 'wh.scan.addMode', 'scan.hiddenFields': 'wh.scan.hiddenFields', 'scan.mute': 'wh-scan-mute' };

  function localKey(key) { return LOCAL[key] || key; }
  function readLocal(key) {
    try { return window.localStorage.getItem(localKey(key)); } catch (e) { return null; }
  }
  function writeLocal(key, value) {
    try { window.localStorage.setItem(localKey(key), value); } catch (e) { /* 私密模式等：只在這次頁面有效 */ }
  }
  function removeLocal(key) {
    try { window.localStorage.removeItem(localKey(key)); } catch (e) { /* 略過 */ }
  }

  // 回傳 Promise<number>：HTTP 狀態碼（網路錯誤為 0）
  function send(key, value) {
    try {
      return fetch('/Account/Preferences', {
        method: 'POST',
        credentials: 'same-origin',
        keepalive: true,
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
        body: JSON.stringify({ key: key, value: value })
      }).then(function (res) { return res.status; }, function () { return 0; });
    } catch (e) {
      return Promise.resolve(0);
    }
  }

  function get(key, fallback) {
    if (!node) {
      var v = readLocal(key);
      return v === null ? fallback : v;
    }
    if (Object.prototype.hasOwnProperty.call(server, key)) return server[key];
    var old = readLocal(key);
    if (old === null) return fallback;
    server[key] = old; // 同一頁只上傳一次
    // 已儲存（2xx）或舊值格式不合（400，再送也一樣）都刪掉本機舊值，不會每頁重送
    send(key, old).then(function (status) { if ((status >= 200 && status < 300) || status === 400) removeLocal(key); });
    return old;
  }

  function set(key, value) {
    value = String(value);
    if (!node) { writeLocal(key, value); return; }
    if (server[key] === value) return;
    server[key] = value;
    send(key, value);
  }

  window.WhPrefs = { get: get, set: set };
})();
