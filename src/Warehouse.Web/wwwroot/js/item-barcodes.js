// 物料提出／修改表單的原廠條碼列（M8-05，docs/04 §4）。不使用框架，相容 Chrome 109；不載入相機程式。
// - 條碼槍送 Enter 時不送出表單，改把焦點移到同一列的包裝數量；包裝數量按 Enter 移到下一列（沒有就新增一列）。
// - 「再加一個條碼」複製最後一列並重編 Input.Barcodes[i] 的索引，最多 data-max-rows 列（伺服器也會檢查）。
(function () {
  'use strict';

  var box = document.getElementById('item-barcodes');
  if (!box) return;
  var rows = document.getElementById('barcode-rows');
  var addButton = document.getElementById('add-barcode-row');
  var maxRows = parseInt(box.getAttribute('data-max-rows'), 10) || 10;

  function renumber() {
    rows.querySelectorAll('.barcode-row').forEach(function (row, i) {
      row.querySelector('.barcode-text').name = 'Input.Barcodes[' + i + '].Text';
      row.querySelector('.barcode-pack').name = 'Input.Barcodes[' + i + '].PackQuantity';
    });
    addButton.disabled = rows.querySelectorAll('.barcode-row').length >= maxRows;
  }

  function addRow() {
    var all = rows.querySelectorAll('.barcode-row');
    if (all.length >= maxRows) return null;
    var row = all[all.length - 1].cloneNode(true);
    row.querySelector('.barcode-text').value = '';
    row.querySelector('.barcode-pack').value = '';
    rows.appendChild(row);
    renumber();
    return row;
  }

  addButton.addEventListener('click', function () {
    var row = addRow();
    if (row) row.querySelector('.barcode-text').focus();
  });

  rows.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Enter' || ev.isComposing) return;
    var target = ev.target;
    var row = target.closest('.barcode-row');
    if (!row) return;
    ev.preventDefault(); // 條碼槍的 Enter 尾碼不送出整張表單
    if (target.classList.contains('barcode-text')) {
      target.value = target.value.replace(/[\u0000-\u001f\u007f]/g, '').trim();
      row.querySelector('.barcode-pack').focus();
      return;
    }
    var next = row.nextElementSibling || (row.querySelector('.barcode-text').value ? addRow() : null);
    if (next) next.querySelector('.barcode-text').focus();
  });

  renumber();
})();
