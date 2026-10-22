// PWA（M5-02，ADR-0016，docs/11）：註冊 Service Worker、新版提示、離線提示。不使用框架，相容 Chrome 109。
// - Service Worker 只快取靜態檔與離線頁；頁面與 API 一律走網路（見 /sw.js）。
// - 有新版時不自動重新載入（D-23）：顯示「有新版本」，由使用者完成目前作業後按下才更新；
//   掃碼面板還有未加入的資料時，第一次按只提醒，再按一次才更新。
// - 離線時顯示紅色提示；掃碼面板送出前也會檢查（doc-scan.js）。
(function () {
  'use strict';

  var offline = document.getElementById('offline-banner');
  function updateOnline() { if (offline) offline.hidden = navigator.onLine; }
  window.addEventListener('online', updateOnline);
  window.addEventListener('offline', updateOnline);
  updateOnline();

  if (!('serviceWorker' in navigator) || !window.isSecureContext) return;
  // 伺服器設定 Pwa:Disabled 時不註冊（sw.js 已是自我註銷版），也不顯示更新提示（docs/11 §7.1）
  if (document.body.getAttribute('data-pwa-disabled') === 'true') return;

  var banner = document.getElementById('pwa-update');
  var button = document.getElementById('pwa-update-button');
  var note = document.getElementById('pwa-update-note');
  var accepted = false;
  var warned = false;

  // 每次有新的等待中版本就重設按鈕；按下時送給「目前」等待中的版本（原本那個可能已被更新的版本取代，PR 4 審查 4）
  function prompt(reg) {
    if (!banner || !button) return;
    banner.hidden = false;
    button.disabled = false;
    warned = false;
    if (note) note.textContent = '';
    button.onclick = function () {
      // 送出中：不能放棄（請求可能已被伺服器接受，重新載入後會以為沒加入而重掃），請等它完成（PR 4 審查 5）
      if (window.WhScanBusy && window.WhScanBusy()) {
        if (note) note.textContent = '正在送出明細，請等完成後再按更新。';
        return;
      }
      if (window.WhScanDirty && window.WhScanDirty() && !warned) {
        warned = true;
        if (note) note.textContent = '掃碼面板上還有未加入的資料。請先按「加入明細」或「清除」；確定要放棄，再按一次更新。';
        return;
      }
      if (!reg.waiting) {
        if (note) note.textContent = '新版本已不在等待中，請稍候或重新整理頁面。';
        return;
      }
      accepted = true;
      button.disabled = true;
      reg.waiting.postMessage('skipWaiting');
    };
  }

  // 新版 Service Worker 接手後才重新載入，而且只在使用者按了更新時（第一次安裝也會觸發 controllerchange，不重新載入）
  navigator.serviceWorker.addEventListener('controllerchange', function () {
    if (accepted) window.location.reload();
  });

  window.addEventListener('load', function () {
    navigator.serviceWorker.register('/sw.js').then(function (reg) {
      if (reg.waiting && navigator.serviceWorker.controller) prompt(reg);
      reg.addEventListener('updatefound', function () {
        var worker = reg.installing;
        if (!worker) return;
        worker.addEventListener('statechange', function () {
          if (worker.state === 'installed' && navigator.serviceWorker.controller) prompt(reg);
        });
      });
      // 畫面長時間開著（例如 PDA）：回到前景時檢查是否有新版，最多每 30 分鐘一次
      var lastCheck = Date.now();
      document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible' && Date.now() - lastCheck > 30 * 60 * 1000) {
          lastCheck = Date.now();
          reg.update().catch(function () { });
        }
      });
    }).catch(function () { /* 註冊失敗（例如憑證不受信任）：系統照常可用，只是不能安裝 */ });
  });
})();
