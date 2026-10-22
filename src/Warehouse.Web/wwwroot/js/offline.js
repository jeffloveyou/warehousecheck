// 離線頁的「重新整理」按鈕（M5-02）：放在外部檔案，日後設定 CSP（script-src 'self'）也能用。由 Service Worker 預先快取。
document.getElementById('offline-reload').addEventListener('click', function () { location.reload(); });
