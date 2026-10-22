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

  var banner = document.getElementById('pwa-update');
  var button = document.getElementById('pwa-update-button');
  var note = document.getElementById('pwa-update-note');
  var accepted = false;
  var warned = false;

  function prompt(worker) {
    if (!banner || !button) return;
    banner.hidden = false;
    button.onclick = function () {
      if (window.WhScanDirty && window.WhScanDirty() && !warned) {
        warned = true;
        if (note) note.textContent = '掃碼面板上還有未加入的資料。請先按「加入明細」或「清除」；確定要放棄，再按一次更新。';
        return;
      }
      accepted = true;
      button.disabled = true;
      worker.postMessage('skipWaiting');
    };
  }

  // 新版 Service Worker 接手後才重新載入，而且只在使用者按了更新時（第一次安裝也會觸發 controllerchange，不重新載入）
  navigator.serviceWorker.addEventListener('controllerchange', function () {
    if (accepted) window.location.reload();
  });

  window.addEventListener('load', function () {
    navigator.serviceWorker.register('/sw.js').then(function (reg) {
      if (reg.waiting && navigator.serviceWorker.controller) prompt(reg.waiting);
      reg.addEventListener('updatefound', function () {
        var worker = reg.installing;
        if (!worker) return;
        worker.addEventListener('statechange', function () {
          if (worker.state === 'installed' && navigator.serviceWorker.controller) prompt(worker);
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
