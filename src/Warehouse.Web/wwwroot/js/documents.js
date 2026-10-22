// 進出倉單頁面的少量互動（ES2020 以內，Chrome 109 可用）。沒有 JS 時頁面仍可用，只是少了便利功能。
(function () {
    'use strict';

    // 建立單據：依單據類型停用不適用的用途／往來對象群組（optgroup 的 data-types 列出適用的類型）
    var typeSelect = document.getElementById('Create_Type');
    if (typeSelect) {
        var filters = document.querySelectorAll('select[data-filter-by-type]');
        var applyType = function () {
            var type = typeSelect.value;
            filters.forEach(function (select) {
                select.querySelectorAll('optgroup').forEach(function (group) {
                    var types = (group.getAttribute('data-types') || '').split(',');
                    group.disabled = !type || types.indexOf(type) < 0;
                });
                var selected = select.options[select.selectedIndex];
                if (selected && selected.disabled) {
                    select.value = '';
                }
            });
        };
        typeSelect.addEventListener('change', applyType);
        applyType();
    }

    // 明細表單：「帶入」連結把儲位、批號、序號填進表單
    var setValue = function (id, value) {
        var element = document.getElementById(id);
        if (element && value !== null && value !== undefined) {
            element.value = value;
        }
    };
    document.querySelectorAll('a.fill-line').forEach(function (link) {
        link.addEventListener('click', function (event) {
            event.preventDefault();
            setValue('Line_LocationCode', link.getAttribute('data-location'));
            // 結存列有 data-lot／data-serial（可為空字串，代表該維度沒有批號或序號）；建議儲位沒有，不動這兩欄
            if (link.hasAttribute('data-lot')) {
                setValue('Line_LotNo', link.getAttribute('data-lot'));
            }
            if (link.hasAttribute('data-serial')) {
                var serial = link.getAttribute('data-serial');
                setValue('Line_SerialNo', serial);
                if (serial) {
                    setValue('Line_Quantity', '1');
                }
            }
            var target = document.getElementById(link.getAttribute('data-focus') || 'Line_Quantity');
            if (target) {
                target.focus();
            }
        });
    });
})();
