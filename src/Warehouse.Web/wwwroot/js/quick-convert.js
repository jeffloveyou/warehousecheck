// 快速進出補齊畫面（ADR-0026）：每行的儲位、目的儲位、批號用 WhPicker 選單（伺服器篩選、限制筆數）。不使用框架，相容 Chrome 109。
// 儲位選單：這個料號有庫存的儲位排前面，數量少的最前面，顯示數量（進、出、移都一樣）；之後是其他儲位。
// 未鎖定倉庫時，換倉須重選與該倉相關的儲位及追蹤資料。
(function () {
  'use strict';

  var form = document.getElementById('quick-convert-form');
  if (!form) return;
  var dirty = form.dataset.unsaved === 'true';
  var submitting = false;
  form.addEventListener('input', function () { dirty = true; });
  form.addEventListener('change', function () { dirty = true; });
  form.addEventListener('submit', function () { submitting = true; });
  window.addEventListener('pageshow', function () { submitting = false; });
  window.WhPageUpdateBlocked = function () { return dirty || submitting; };
  window.addEventListener('beforeunload', function (event) {
    if (!dirty || submitting) return;
    event.preventDefault();
    event.returnValue = '';
  });
  if (!window.WhPicker || !window.fetch) return;
  var locationsUrl = form.getAttribute('data-locations-url');
  var lotsUrl = form.getAttribute('data-lots-url');
  var warehouse = document.getElementById('quick-warehouse');

  function fmt(n) { return String(Number(n)); }
  function withQuery(url, params) {
    var query = Object.keys(params).filter(function (k) { return params[k] !== null && params[k] !== undefined && params[k] !== ''; })
      .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]); }).join('&');
    return url + (url.indexOf('?') >= 0 ? '&' : '?') + query;
  }
  function getJson(url) {
    return fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
      .then(function (res) { return res.ok ? res.json() : { items: [] }; })
      .then(function (body) { return body.items || []; });
  }
  function warehouseId() { return warehouse ? warehouse.value : ''; }
  function noWarehouse() { return Promise.resolve([]); }

  warehouse.addEventListener('change', function () {
    form.querySelectorAll('input.quick-location,input.quick-lot,textarea.quick-serials').forEach(function (input) { input.value = ''; });
    form.querySelectorAll('input.quick-target').forEach(function (input) {
      var selected = input.closest('tr').querySelector('select.quick-target-warehouse');
      if (!selected || !selected.value) input.value = '';
    });
  });

  form.querySelectorAll('input.quick-location').forEach(function (input) {
    var itemId = input.getAttribute('data-item-id');
    window.WhPicker.attach(input, {
      load: function (q) {
        var source = warehouseId();
        return source ? getJson(withQuery(locationsUrl, { warehouseId: source, itemId: itemId, q: q }))
          .then(function (rows) { return warehouseId() === source ? rows : []; }) : noWarehouse();
      },
      render: function (o) {
        return { main: o.code + (o.quantity !== null && o.quantity !== undefined ? '　現有 ' + fmt(o.quantity) : ''), sub: o.name || '' };
      },
      pick: function (o) { input.value = o.code; dirty = true; },
      empty: function () { return warehouseId() ? '沒有符合的儲位' : '請先選倉庫'; }
    });
  });

  form.querySelectorAll('input.quick-target').forEach(function (input) {
    var row = input.closest('tr');
    var targetWarehouse = row.querySelector('select.quick-target-warehouse');
    if (targetWarehouse) targetWarehouse.addEventListener('change', function () { input.value = ''; });
    window.WhPicker.attach(input, {
      load: function (q) {
        // 目的倉庫：選了別倉就列那個倉的儲位（伺服器用倉庫碼找）；否則同倉
        var code = targetWarehouse && targetWarehouse.value;
        var source = warehouseId();
        if (!code && !source) return noWarehouse();
        return getJson(withQuery(locationsUrl, code ? { warehouseCode: code, target: 'true', q: q } : { warehouseId: source, target: 'true', q: q }))
          .then(function (rows) { return code === (targetWarehouse && targetWarehouse.value) && (code || warehouseId() === source) ? rows : []; });
      },
      render: function (o) { return { main: o.code, sub: o.name || '' }; },
      pick: function (o) { input.value = o.code; dirty = true; },
      empty: '沒有符合的儲位'
    });
  });

  if (lotsUrl) {
    form.querySelectorAll('input.quick-lot').forEach(function (input) {
      var itemId = input.getAttribute('data-item-id');
      window.WhPicker.attach(input, {
        load: function (q) {
          var source = warehouseId();
          return source ? getJson(withQuery(lotsUrl, { warehouseId: source, itemId: itemId, q: q }))
            .then(function (rows) { return warehouseId() === source ? rows : []; }) : noWarehouse();
        },
        render: function (o) {
          return { main: '批 ' + o.lot, sub: o.location + '　數量 ' + fmt(o.quantity) + (o.expiry ? '　到期 ' + o.expiry : '') };
        },
        pick: function (o) {
          input.value = o.lot || '';
          dirty = true;
          // 批號所在的儲位一併帶入（來源儲位空白時）
          var location = input.closest('tr').querySelector('input.quick-location');
          if (location && !location.value.trim()) location.value = o.location;
        },
        empty: '這個倉庫沒有可選的批號'
      });
    });
  }
})();
