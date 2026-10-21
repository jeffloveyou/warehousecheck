// 系統狀態 PoC：只在載入與按下「重新整理」時查詢，不輪詢
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function load() {
    $('error').textContent = '';
    fetch('/api/system/info', { headers: { Accept: 'application/json' } })
      .then(function (res) {
        if (!res.ok) { throw new Error('伺服器回應 HTTP ' + res.status); }
        return res.json();
      })
      .then(function (data) {
        $('version').textContent = data.version;
        var conn = $('dbConnected');
        conn.textContent = data.databaseConnected ? '已連線' : '無法連線';
        conn.className = data.databaseConnected ? 'ok' : 'bad';
        $('dbVersion').textContent = data.databaseVersion || '-';
        $('serverTime').textContent = data.serverTimeUtc;
      })
      .catch(function (err) {
        $('error').textContent = '無法取得系統狀態：' + err.message;
      });
  }

  $('refresh').addEventListener('click', load);
  load();
})();
