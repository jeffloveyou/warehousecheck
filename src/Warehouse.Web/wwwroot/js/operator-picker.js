// 公用帳號的操作人選單（ADR-0030）。只在公用帳號的頁面載入；原生 JS，相容 Chrome 109。
// 1. 同一台裝置記住上次選的人（localStorage，失敗就不記，不影響送出）。
// 2. 選單在表單外時，送出任何 POST 表單前補一個 hidden operatorNameId；標了 data-operator-required 的表單沒選人就擋下。
// 3. 必選表單的送出按鈕附上姓名，例如「確認過帳（王小明）」，換人沒改時一眼看得出來。
(function () {
  'use strict';
  var select = document.querySelector('[data-operator-select]');
  if (!select) return;
  var key = 'warehouse.operatorNameId';
  try {
    var saved = localStorage.getItem(key);
    if (saved && Array.prototype.some.call(select.options, function (o) { return o.value === saved; })) select.value = saved;
  } catch (e) { }

  function name() {
    var option = select.options[select.selectedIndex];
    return select.value && option ? option.textContent : '';
  }

  function label() {
    var n = name();
    document.querySelectorAll('form[data-operator-required] button[type=submit], form[data-operator-required] button:not([type])').forEach(function (b) {
      if (!b.hasAttribute('data-operator-base')) b.setAttribute('data-operator-base', b.textContent.trim());
      b.textContent = b.getAttribute('data-operator-base') + (n ? '（' + n + '）' : '');
    });
    document.dispatchEvent(new CustomEvent('operator-change'));
  }

  select.addEventListener('change', function () {
    try { localStorage.setItem(key, select.value); } catch (e) { }
    label();
  });

  document.addEventListener('submit', function (ev) {
    var form = ev.target;
    if (!(form instanceof HTMLFormElement) || (form.getAttribute('method') || '').toLowerCase() !== 'post') return;
    if (form.hasAttribute('data-operator-required') && !select.value) {
      ev.preventDefault(); ev.stopImmediatePropagation();
      select.scrollIntoView({ block: 'center' }); select.focus();
      return;
    }
    if (form.contains(select)) return;
    var hidden = form.querySelector('input[type=hidden][name=operatorNameId]');
    if (!hidden) {
      hidden = document.createElement('input'); hidden.type = 'hidden'; hidden.name = 'operatorNameId'; form.appendChild(hidden);
    }
    hidden.value = select.value;
  }, true);

  window.WhOperator = { id: function () { return select.value; }, name: name, element: select };
  label();
})();
