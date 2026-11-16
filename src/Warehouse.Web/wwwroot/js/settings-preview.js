// 只說明目前表單的效果，不改設定、不發送請求。
(function () {
  'use strict';
  var form = document.getElementById('settings-form');
  if (!form) return;
  function checked(name) { var el = form.querySelector('[name="Form.' + name + '"]'); return !!el && el.checked; }
  function update() {
    var checkout = document.getElementById('settings-checkout-effect');
    checkout.textContent = !checked('QuickCheckoutEnabled') ? '快速進出使用轉草稿流程，再到單據完成作業。' :
      checked('DocumentApprovalEnabled') ? '快速進出會送審；主管核准後才入帳。' : '快速進出確認結帳即入帳。';
    var fields = [['Purpose', '用途（移庫除外）'], ['Partner', '往來對象'], ['ExternalNo', '外部單號'], ['Note', '備註']];
    var required = fields.filter(function (f) { return checked('Document' + f[0] + 'Enabled') && checked('Document' + f[0] + 'Required'); }).map(function (f) { return f[1]; });
    document.getElementById('settings-required-effect').textContent = required.length ? '結帳／確認／送審時必填：' + required.join('、') + '。' : '表頭目前沒有設定額外必填欄位。';
  }
  form.addEventListener('change', update);
  update();
})();
