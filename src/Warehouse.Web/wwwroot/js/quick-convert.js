// 快速進出補齊畫面（ADR-0026）：每行的儲位、目的儲位、批號用 WhPicker 選單（伺服器篩選、限制筆數）。不使用框架，相容 Chrome 109。
// 儲位選單：這個料號有庫存的儲位排前面，數量少的最前面，顯示數量（進、出、移都一樣）；之後是其他儲位。
// 倉庫改了：清單跟著用新的倉庫（已填的儲位保留，送出時伺服器會檢查是否在這個倉庫）。
(function () {
  'use strict';

  var form = document.getElementById('quick-convert-form');
  if (!form || !window.WhPicker || !window.fetch) return;
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

  form.querySelectorAll('input.quick-location').forEach(function (input) {
    var itemId = input.getAttribute('data-item-id');
    window.WhPicker.attach(input, {
      load: function (q) {
        return warehouseId() ? getJson(withQuery(locationsUrl, { warehouseId: warehouseId(), itemId: itemId, q: q })) : noWarehouse();
      },
      render: function (o) {
        return { main: o.code + (o.quantity !== null && o.quantity !== undefined ? '　現有 ' + fmt(o.quantity) : ''), sub: o.name || '' };
      },
      pick: function (o) { input.value = o.code; },
      empty: function () { return warehouseId() ? '沒有符合的儲位' : '請先選倉庫'; }
    });
  });

  form.querySelectorAll('input.quick-target').forEach(function (input) {
    var row = input.closest('tr');
    var targetWarehouse = row.querySelector('select.quick-target-warehouse');
    window.WhPicker.attach(input, {
      load: function (q) {
        // 目的倉庫：選了別倉就列那個倉的儲位（伺服器用倉庫碼找）；否則同倉
        var code = targetWarehouse && targetWarehouse.value;
        if (!code && !warehouseId()) return noWarehouse();
        return getJson(withQuery(locationsUrl, code ? { warehouseCode: code, target: 'true', q: q } : { warehouseId: warehouseId(), target: 'true', q: q }));
      },
      render: function (o) { return { main: o.code, sub: o.name || '' }; },
      pick: function (o) { input.value = o.code; },
      empty: '沒有符合的儲位'
    });
  });

  if (lotsUrl) {
    form.querySelectorAll('input.quick-lot').forEach(function (input) {
      var itemId = input.getAttribute('data-item-id');
      window.WhPicker.attach(input, {
        load: function (q) {
          return warehouseId() ? getJson(withQuery(lotsUrl, { warehouseId: warehouseId(), itemId: itemId, q: q })) : noWarehouse();
        },
        render: function (o) {
          return { main: '批 ' + o.lot, sub: o.location + '　數量 ' + fmt(o.quantity) + (o.expiry ? '　到期 ' + o.expiry : '') };
        },
        pick: function (o) {
          input.value = o.lot || '';
          // 批號所在的儲位一併帶入（來源儲位空白時）
          var location = input.closest('tr').querySelector('input.quick-location');
          if (location && !location.value.trim()) location.value = o.location;
        },
        empty: '這個倉庫沒有可選的批號'
      });
    });
  }
})();
