// 逐行背景儲存；其他列未存的數字保留在本裝置，最後確認前重新核對伺服器差異。
(function () {
  'use strict';
  var status = document.getElementById('stocktake-save-status'); if (!status || !window.fetch) return;
  var key = 'stocktake.work.' + status.dataset.owner + '.' + status.dataset.document;
  var draft = {}, queue = Promise.resolve(), active = 0, review = false, saved = 0;
  var queuedValues = new Map();
  var reviewButton = document.getElementById('stocktake-review-current');
  var entryKey = key + '.entry';
  var entryForm = null, entryDraft = null;
  var entryFields = ['LocationCode', 'ItemCode', 'LotNo', 'SerialNo', 'Quantity'];
  try { draft = JSON.parse(localStorage.getItem(key) || '{}'); } catch (e) {}
  try { entryDraft = JSON.parse(localStorage.getItem(entryKey) || 'null'); } catch (e) {}
  reviewButton.hidden = false;
  function persist() { try { localStorage.setItem(key, JSON.stringify(draft)); } catch (e) { status.textContent = '裝置無法保存；請逐行儲存後再離頁。'; } }
  function inputs() { return document.querySelectorAll('#stocktake-lines input[name="quantity"]'); }
  function pending() { return Object.keys(draft).length > 0 || active > 0; }
  function label(input, text) { var node = input.parentElement.querySelector('.count-save-state'); if (!node) { node = document.createElement('span'); node.className = 'count-save-state'; node.setAttribute('role', 'status'); input.parentElement.appendChild(node); } node.textContent = text; }
  function save(input) {
    var form = document.getElementById(input.getAttribute('form')), id = form.id, value = input.value;
    if (value === '' || !input.checkValidity()) { label(input, '未儲存：請填有效數字，沒有庫存填 0'); return; }
    if (queuedValues.get(id)?.value === value) return;
    var request = { value: value };
    queuedValues.set(id, request);
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
        if (queuedValues.get(id) === request && draft[id] === value) {
          delete draft[id]; persist(); label(input, '已儲存 ' + value);
        } else label(input, '新數字尚未儲存');
        saved++;
        review = true;
        status.textContent = '實盤量已更新；最後確認或送審前請讀取最新盤點結果並核對差異。';
      } catch (e) { label(input, e.name === 'AbortError' ? '結果待確認：等待逾時，可核對後重試原實盤量' : '未儲存：' + e.message); }
      finally {
        clearTimeout(timeout);
        if (queuedValues.get(id) === request) queuedValues.delete(id);
        active--;
      }
    });
  }
  function bindInputs() {
    inputs().forEach(function (input) {
      var id = input.getAttribute('form');
      if (Object.prototype.hasOwnProperty.call(draft, id)) { input.value = draft[id]; label(input, '已恢復未儲存數字，請核對後按存'); }
      input.addEventListener('input', function () { draft[id] = input.value; persist(); label(input, '未儲存'); });
      input.addEventListener('change', function () {
        if (Object.prototype.hasOwnProperty.call(draft, id)) save(input);
      });
      input.addEventListener('keydown', function (ev) { if (ev.key === 'Enter') { ev.preventDefault(); save(input); } });
    });
  }

  function syncEntryForm() {
    var current = document.getElementById('stocktake-count-form');
    if (current === entryForm) return;
    if (entryForm) {
      var values = entryFields.map(function (name) { return entryForm.elements.namedItem('Count.' + name).value; });
      // 單獨留下掃碼後的儲位是下一筆預設值，不視為一筆未完成的實盤。
      if (values.slice(1).some(Boolean)) {
        entryDraft = values;
        try { localStorage.setItem(entryKey, JSON.stringify(values)); }
        catch (e) { /* 儲存空間不可用時保留在本頁記憶體。 */ }
      }
    }
    entryForm = current;
    if (!entryForm || !entryDraft) return;
    entryFields.forEach(function (name, index) { entryForm.elements.namedItem('Count.' + name).value = entryDraft[index]; });
    try {
      if (localStorage.getItem(entryKey) === JSON.stringify(entryDraft)) localStorage.removeItem(entryKey);
    } catch (e) {}
    entryDraft = null;
    status.textContent = '已恢復未記錄的實盤輸入，請核對後按記錄。';
  }

  function showReviewStatus() {
    if (!entryForm) {
      status.textContent = entryDraft || Object.keys(draft).length
        ? '目前不可盤點。未存輸入已保留，恢復盤點後可核對。' : '已讀取目前盤點狀態。';
    } else if (Object.keys(draft).length) {
      status.textContent = '仍有未儲存的實盤量，請核對後逐行儲存。';
    } else if (review) {
      status.textContent = '實盤量已更新；最後確認或送審前請讀取最新盤點結果並核對差異。';
    } else if (!status.textContent.includes('已恢復未記錄')) {
      status.textContent = '已讀取最新盤點結果，請核對差異後再確認或送審。';
    }
  }

  bindInputs();
  syncEntryForm();
  if (!entryForm && (entryDraft || Object.keys(draft).length)) showReviewStatus();
  reviewButton.addEventListener('click', async function () {
    if (active) {
      status.textContent = '正在儲存實盤量，請等候完成再讀取最新結果。';
      return;
    }
    var before = saved;
    if (await window.WhPageQuery(location.href) && before === saved && !active) {
      review = pending();
      showReviewStatus();
    }
  });
  document.addEventListener('wh:page-updated', function (ev) {
    if (ev.detail.id === 'query-results') {
      bindInputs();
      showReviewStatus();
    }
    if (ev.detail.id === 'stocktake-count-editor') syncEntryForm();
  });
  document.addEventListener('wh:before-page-update', function (ev) {
    if (active) {
      ev.preventDefault();
      status.textContent = '正在儲存實盤量，請等候完成再換頁。';
    }
  });
  document.addEventListener('submit', function (ev) {
    if (ev.target.id.indexOf('count-line-') === 0) { ev.preventDefault(); var input = Array.from(inputs()).find(function (i) { return i.getAttribute('form') === ev.target.id; }); if (input) save(input); return; }
    var url = new URL(ev.target.action || location.href); var action = url.searchParams.get('handler');
    if (entryForm && ['Submit', 'Confirm', 'Cancel'].indexOf(action) >= 0 && (pending() || review)) {
      ev.preventDefault(); status.textContent = pending() ? '還有未儲存實盤量，請逐行儲存後再繼續。' : '請讀取最新盤點結果並核對差異，再確認或送審。'; status.scrollIntoView({ block: 'center' });
    }
  }, true);
  window.addEventListener('beforeunload', function (ev) { if (pending()) { ev.preventDefault(); ev.returnValue = ''; } });
})();
