// 可輸入縮小範圍的選擇清單（M8-03）：輸入框下方的下拉清單，打字 300 ms 後向伺服器篩選（每次最多 20～50 筆，不下載完整清單）。
// 不使用框架，相容 Chrome 109。用法：
//   WhPicker.attach(input, {
//     load: function (text) { return Promise<array> },     // 依輸入文字取得選項
//     render: function (item) { return { main, sub } },   // 每一列顯示的文字（textContent，不插入 HTML）
//     pick: function (item) { },                          // 選定（點選或方向鍵＋Enter）
//     empty: '沒有符合的項目'
//   })
// 鍵盤：↓ 打開或往下、↑ 往上、Enter 選定目前反白的一列（沒有反白時不攔，Enter 照原本的行為）、Esc 關閉。
// 清單只在輸入框有焦點時出現；條碼槍掃描時焦點在掃碼框，不會跳出清單。
(function () {
  'use strict';

  var DELAY = 300;

  function attach(input, options) {
    var wrap = document.createElement('div');
    wrap.className = 'picker';
    input.parentNode.insertBefore(wrap, input);
    wrap.appendChild(input);
    // 欄位被隱藏時外框一起隱藏（applyFields 依 data-field 切換），否則空的外框會讓表格錯位
    if (input.hasAttribute('data-field')) wrap.setAttribute('data-field', input.getAttribute('data-field'));
    var list = document.createElement('ul');
    list.className = 'picker-list';
    list.setAttribute('role', 'listbox');
    list.hidden = true;
    wrap.appendChild(list);
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-expanded', 'false');

    var items = [];
    var active = -1;
    var timer = null;
    var seq = 0;          // 只採用最後一次查詢的結果（較早的回應晚到時丟棄）
    var open = false;

    function close() {
      open = false;
      list.hidden = true;
      active = -1;
      input.setAttribute('aria-expanded', 'false');
    }

    function highlight(index) {
      var rows = list.children;
      for (var i = 0; i < rows.length; i++) rows[i].className = i === index ? 'active' : '';
      active = index;
      if (index >= 0 && rows[index]) rows[index].scrollIntoView({ block: 'nearest' });
    }

    function show(result) {
      items = result || [];
      list.textContent = '';
      if (!items.length) {
        var none = document.createElement('li');
        none.className = 'picker-empty';
        none.textContent = (typeof options.empty === 'function' ? options.empty() : options.empty) || '沒有符合的項目';
        list.appendChild(none);
      }
      items.forEach(function (item, index) {
        var text = options.render(item);
        var li = document.createElement('li');
        li.setAttribute('role', 'option');
        var main = document.createElement('span');
        main.textContent = text.main;
        li.appendChild(main);
        if (text.sub) {
          var sub = document.createElement('span');
          sub.className = 'muted';
          sub.textContent = '　' + text.sub;
          li.appendChild(sub);
        }
        // mousedown 先於 blur：點選時清單還沒關
        li.addEventListener('mousedown', function (ev) { ev.preventDefault(); choose(index); });
        list.appendChild(li);
      });
      open = true;
      list.hidden = false;
      input.setAttribute('aria-expanded', 'true');
      active = -1;
    }

    function choose(index) {
      var item = items[index];
      close();
      if (item) options.pick(item);
    }

    function load() {
      var mine = ++seq;
      Promise.resolve(options.load(input.value.trim())).then(function (result) {
        if (mine === seq && document.activeElement === input) show(result);
      }, function () {
        if (mine === seq) close();
      });
    }

    function schedule() {
      clearTimeout(timer);
      timer = setTimeout(load, DELAY);
    }

    input.addEventListener('focus', schedule);
    input.addEventListener('input', schedule);
    input.addEventListener('blur', function () { clearTimeout(timer); seq++; setTimeout(close, 0); });
    input.addEventListener('keydown', function (ev) {
      if (ev.isComposing) return;
      if (ev.key === 'ArrowDown') {
        ev.preventDefault();
        if (!open) { load(); return; }
        highlight(Math.min(active + 1, items.length - 1));
      } else if (ev.key === 'ArrowUp') {
        if (!open) return;
        ev.preventDefault();
        highlight(Math.max(active - 1, 0));
      } else if (ev.key === 'Enter' && open && active >= 0) {
        ev.preventDefault();
        ev.stopImmediatePropagation(); // 不再觸發輸入框原本的 Enter（例如送出或下一步）
        choose(active);
      } else if (ev.key === 'Escape' && open) {
        ev.preventDefault();
        close();
      }
    });

    return { close: close };
  }

  window.WhPicker = { attach: attach };
})();
