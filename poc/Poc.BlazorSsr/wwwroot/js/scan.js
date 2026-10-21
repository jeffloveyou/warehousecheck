// 掃碼頁共用 JS（與 Poc.RazorPages 相同檔案，供 Blazor SSR 的 /scan-js 比較用）。
// 做法：
//  1. Razor Pages 每次互動都是整頁請求，連續快速掃碼會遺失輸入，所以掃碼改在瀏覽器端處理。
//  2. 攔截 Enter：立即取出輸入框的值放入佇列，並馬上清空輸入框（輸入框不 disabled、焦點不離開），
//     下一筆掃碼的字元可以立刻繼續輸入，不會等待網路。
//  3. 佇列以單一 worker 依序呼叫 GET /api/items/{code}（回傳 { itemCode, name } 或 404），
//     因此結果順序與掃碼順序一致；每筆結果插到清單最上方並更新 #scan-count。
//  4. 全程不整頁重新載入。相容 Chrome 109（fetch、async/await）。
(function () {
  'use strict';

  var input = document.getElementById('scan-input');
  var list = document.getElementById('scan-list');
  var countEl = document.getElementById('scan-count');
  var queue = [];
  var working = false;

  function addRow(code, item, failed) {
    var li = document.createElement('li');
    li.className = 'scan-row';
    if (item) {
      var codeSpan = document.createElement('span');
      codeSpan.className = 'item-code';
      codeSpan.textContent = item.itemCode;
      var nameSpan = document.createElement('span');
      nameSpan.className = 'item-name';
      nameSpan.textContent = item.name;
      li.appendChild(codeSpan);
      li.appendChild(document.createTextNode(' '));
      li.appendChild(nameSpan);
    } else {
      var err = document.createElement('span');
      err.className = 'scan-error';
      err.textContent = (failed ? '查詢失敗：' : '找不到料號：') + code;
      li.appendChild(err);
    }
    list.insertBefore(li, list.firstChild);
    countEl.textContent = String(list.children.length);
  }

  async function lookup(code) {
    try {
      var res = await fetch('/api/items/' + encodeURIComponent(code), { headers: { 'Accept': 'application/json' } });
      if (res.ok) {
        addRow(code, await res.json(), false);
      } else {
        addRow(code, null, res.status !== 404);
      }
    } catch (e) {
      addRow(code, null, true);
    }
  }

  async function drain() {
    if (working) { return; }
    working = true;
    while (queue.length > 0) {
      await lookup(queue.shift());
    }
    working = false;
  }

  input.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Enter') { return; }
    ev.preventDefault();
    var value = input.value.trim();
    input.value = '';
    if (value.length === 0) { return; }
    queue.push(value);
    drain();
  });

  input.focus();
})();
