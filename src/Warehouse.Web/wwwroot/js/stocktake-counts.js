// 逐行背景儲存；其他列未存的數字保留在本裝置，最後確認前重新核對伺服器差異。
(function () {
  'use strict';
  var status = document.getElementById('stocktake-save-status'); if (!status || !window.fetch) return;
  var key = 'stocktake.work.' + status.dataset.owner + '.' + status.dataset.document;
  var draft = {}, queue = Promise.resolve(), active = 0, review = false;
  try { draft = JSON.parse(localStorage.getItem(key) || '{}'); } catch (e) {}
  function persist() { try { localStorage.setItem(key, JSON.stringify(draft)); } catch (e) { status.textContent = '裝置無法保存；請逐行儲存後再離頁。'; } }
  var inputs = document.querySelectorAll('#stocktake-lines input[name="quantity"]');
  function pending() { return Object.keys(draft).length > 0 || active > 0; }
  function label(input, text) { var node = input.parentElement.querySelector('.count-save-state'); if (!node) { node = document.createElement('span'); node.className = 'count-save-state'; node.setAttribute('role', 'status'); input.parentElement.appendChild(node); } node.textContent = text; }
  function save(input) {
    var form = document.getElementById(input.getAttribute('form')), id = form.id, value = input.value;
    if (value === '' || !input.checkValidity()) { label(input, '未儲存：請填有效數字，沒有庫存填 0'); return; }
    draft[id] = value; persist(); active++;
    queue = queue.then(async function () {
      label(input, '儲存中…');
      var controller = new AbortController(), timeout = setTimeout(function () { controller.abort(); }, 15000);
      try {
        var data = new FormData(form); data.set('quantity', value);
        var url = new URL(form.action); url.searchParams.set('handler', 'SaveLine');
        var response = await fetch(url.toString(), { method: 'POST', credentials: 'same-origin', body: data, signal: controller.signal });
        if (response.redirected || !response.ok) { var error = response.headers.get('content-type') && response.headers.get('content-type').indexOf('json') >= 0 ? await response.json() : null; throw new Error(error && error.message || '登入逾時或儲存失敗，請登入後重試。'); }
        var result = await response.json(); if (!result || !result.message) throw new Error('回應不完整，請核對後重試儲存。');
        if (draft[id] === value) { delete draft[id]; persist(); label(input, '已儲存 ' + value); } else label(input, '新數字尚未儲存');
        review = true; status.textContent = '實盤量已更新；最後確認／送審前請重新整理核對最新差異。';
      } catch (e) { label(input, e.name === 'AbortError' ? '結果待確認：等待逾時，可核對後重試原實盤量' : '未儲存：' + e.message); }
      finally { clearTimeout(timeout); active--; }
    });
  }
  inputs.forEach(function (input) {
    var id = input.getAttribute('form');
    if (Object.prototype.hasOwnProperty.call(draft, id)) { input.value = draft[id]; label(input, '已恢復未儲存數字，請核對後按存'); }
    input.addEventListener('input', function () { draft[id] = input.value; persist(); label(input, '未儲存'); });
    input.addEventListener('change', function () { save(input); });
    input.addEventListener('keydown', function (ev) { if (ev.key === 'Enter') { ev.preventDefault(); save(input); } });
  });
  document.addEventListener('submit', function (ev) {
    if (ev.target.id.indexOf('count-line-') === 0) { ev.preventDefault(); var input = Array.from(inputs).find(function (i) { return i.getAttribute('form') === ev.target.id; }); if (input) save(input); return; }
    var url = new URL(ev.target.action || location.href); var action = url.searchParams.get('handler');
    if (['Submit', 'Confirm', 'Approve', 'Cancel', 'Recount'].indexOf(action) >= 0 && (pending() || review)) {
      ev.preventDefault(); status.textContent = pending() ? '還有未儲存實盤量，請逐行儲存後再繼續。' : '請重新整理核對最新差異，再確認或送審。'; status.scrollIntoView({ block: 'center' });
    }
  }, true);
  window.addEventListener('beforeunload', function (ev) { if (pending()) { ev.preventDefault(); ev.returnValue = ''; } });
})();
