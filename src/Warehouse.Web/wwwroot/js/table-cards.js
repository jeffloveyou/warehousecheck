// 已分頁的高頻查詢表：手機將每行變成帶欄名的卡片，保留桌面與無JS表格。
(function () {
  'use strict';
  document.querySelectorAll('main table').forEach(function (table) {
    var headings = Array.from(table.querySelectorAll('thead th')).map(function (h) { return h.textContent.trim(); });
    if (!headings.length) return;
    table.classList.add('mobile-cards');
    table.querySelectorAll('tbody tr').forEach(function (row) {
      Array.from(row.cells).forEach(function (cell, index) { if (cell.colSpan === 1 && headings[index]) cell.dataset.label = headings[index]; });
    });
  });
})();
