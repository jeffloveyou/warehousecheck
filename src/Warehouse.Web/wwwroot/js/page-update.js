// 同頁查詢及表單只更新頁面指定的區塊，沿用 Razor 產生的網址、驗證和權限。
(function () {
  'use strict';
  var regions = '[data-page-region]';
  var queryRegions = '[data-page-region=""], [data-page-region~="query"]';
  if (!window.fetch || !document.querySelector(regions)) return;
  var currentUrl = location.href;
  var unknownWrites = new WeakSet();
  var active;
  var status = document.createElement('p');
  status.className = 'muted';
  status.setAttribute('role', 'status');
  status.hidden = true;
  document.querySelector(regions).before(status);

  function pagePath(url) { return url.pathname.replace(/\/$|\/index\/?$/i, '').toLowerCase(); }
  function samePage(url) { return url.origin === location.origin && pagePath(url) === pagePath(location); }

  function controls(form) {
    return Array.from(form.elements).filter(function (field) {
      return field.name && field.matches('input:not([type=file]), select, textarea');
    });
  }
  function syncFilters(content) {
    document.querySelectorAll('form[data-page-query][id], form[data-page-search][id]').forEach(function (form) {
      var fresh = content.querySelector('#' + CSS.escape(form.id));
      if (!fresh) return;
      var fields = controls(fresh);
      controls(form).forEach(function (field) {
        var match = fields.find(function (candidate) { return candidate.name === field.name; });
        if (!match) return;
        if (field.type === 'checkbox' || field.type === 'radio') field.checked = match.checked;
        else field.value = match.value;
      });
    });
  }
  function replace(old, fresh) {
    if (old.dataset.pageKey !== undefined && old.dataset.pageKey === fresh.dataset.pageKey) return;
    var focused = document.activeElement;
    var selection = typeof focused.selectionStart === 'number'
      ? [focused.selectionStart, focused.selectionEnd, focused.selectionDirection] : null;
    var replacement;
    if (old.contains(focused)) {
      var selector = focused.id ? '#' + CSS.escape(focused.id) : null;
      if (focused.matches('.pager a, [data-page-link], [data-page-edit-link]')) {
        var pagers = Array.from(old.querySelectorAll('.pager, .label-pages'));
        var pagerIndex = pagers.indexOf(focused.closest('.pager, .label-pages'));
        var freshPager = fresh.querySelectorAll('.pager, .label-pages')[pagerIndex];
        var links = Array.from(freshPager ? freshPager.querySelectorAll('a') : fresh.querySelectorAll('a[data-page-link], a[data-page-edit-link]'));
        replacement = links.find(function (link) { return link.textContent.trim() === focused.textContent.trim(); })
          || (freshPager && links[0]);
      }
      if (selector) {
        var candidates = Array.from(fresh.querySelectorAll(selector));
        replacement = candidates[0] || replacement;
      }
      var row = focused.closest('[data-page-row]');
      if (!replacement && row) {
        var rows = Array.from(old.querySelectorAll('[data-page-row]'));
        var index = rows.indexOf(row);
        var updatedRows = Array.from(fresh.querySelectorAll('[data-page-row]'));
        var choices = updatedRows.slice(index).concat(updatedRows.slice(0, index).reverse());
        replacement = choices.map(function (candidate) {
          return candidate.querySelector('button[type="submit"]:not([disabled]), a');
        }).find(Boolean);
      }
      var fallback = old.dataset.pageFocusFallback;
      replacement = replacement || fresh.querySelector('h2[tabindex="-1"]') ||
        (fallback && document.querySelector(fallback)) || old;
    }
    if (window.WhCameraField) window.WhCameraField.release(old);
    old.replaceChildren(...fresh.childNodes);
    if (fresh.hasAttribute('data-page-key')) old.dataset.pageKey = fresh.dataset.pageKey;
    if (fresh.hasAttribute('data-kind')) old.dataset.kind = fresh.dataset.kind;
    if (window.WhCameraField) window.WhCameraField.enhance(old);
    if (window.WhTableCards) window.WhTableCards(old);
    document.dispatchEvent(new CustomEvent('wh:page-updated', { detail: old }));
    if (replacement) {
      if (replacement === old) old.tabIndex = -1;
      replacement.focus({ preventScroll: true });
      if (selection && focused.id && replacement.id === focused.id && typeof replacement.selectionStart === 'number') {
        replacement.setSelectionRange(selection[0], selection[1], selection[2]);
      }
    }
  }

  async function update(url, options) {
    if (active && active.write) return;
    if (!document.dispatchEvent(new Event('wh:before-page-update', { cancelable: true }))) {
      if (options.restore) history.replaceState(null, '', currentUrl);
      return;
    }
    if (active) {
      active.controller.abort();
      active.targets.forEach(function (target) { target.removeAttribute('aria-busy'); });
    }
    var write = !!options.body;
    var submitted = options.form;
    var submittedValues = submitted && submitted.id && controls(submitted).filter(function (field) {
      return field.type !== 'hidden' && !field.disabled;
    }).map(function (field) {
      var property = field.type === 'checkbox' || field.type === 'radio' ? 'checked' : 'value';
      return { field: field, property: property, value: field[property] };
    });
    var selector = options.group
      ? options.group.split(/\s+/).map(function (group) {
        var token = CSS.escape(group);
        return '[data-page-region~="' + token + '"], [data-page-group~="' + token + '"]';
      }).join(', ')
      : queryRegions;
    var targets = Array.from(document.querySelectorAll(selector));
    var request = { controller: new AbortController(), write: write, targets: targets };
    var timer = setTimeout(function () { request.controller.abort(); }, 15000);
    active = request;
    status.hidden = false;
    status.className = 'visually-hidden';
    status.textContent = write ? '處理中…' : '查詢中…';
    targets.forEach(function (target) { target.setAttribute('aria-busy', 'true'); });
    try {
      var response = await fetch(url, {
        method: write ? 'POST' : 'GET', body: options.body, credentials: 'same-origin',
        cache: 'no-store', signal: request.controller.signal,
        headers: submitted && submitted.hasAttribute('data-page-write') ? { 'X-Warehouse-Page-Update': '1' } : undefined
      });
      if (active !== request) return;
      var finalUrl = new URL(response.url || url);
      if (response.redirected && /\/Account\/(Login|AccessDenied|ChangePassword)\/?$/i.test(finalUrl.pathname)) {
        throw new Error('登入或權限已變更，請重新登入後核對。');
      }
      if (!response.ok) throw new Error('伺服器回應 HTTP ' + response.status + '。');
      if (!samePage(finalUrl)) throw new Error('未收到同一頁的回應。');
      var template = document.createElement('template');
      template.innerHTML = await response.text();
      if (active !== request) return;
      if (!document.dispatchEvent(new Event('wh:before-page-update', { cancelable: true }))) {
        throw new Error('請等候目前操作完成後再試。');
      }
      var incoming = Array.from(template.content.querySelectorAll(selector));
      if (!targets.length || targets.length !== incoming.length ||
          targets.some(function (target, index) { return target.id !== incoming[index].id; })) {
        throw new Error('回應缺少畫面區塊。');
      }
      if (options.restore) syncFilters(template.content);
      if (submittedValues) {
        var updatedForm = template.content.querySelector('#' + CSS.escape(submitted.id));
        submittedValues.forEach(function (entry) {
          var field = entry.field;
          var freshField = updatedForm && updatedForm.elements.namedItem(field.name);
          if (freshField && field[entry.property] !== entry.value && !freshField.readOnly) {
            freshField[entry.property] = field[entry.property];
          }
        });
      }
      var hasError = incoming.some(function (target) { return target.querySelector('#page-error, .validation-summary-errors'); });
      targets.forEach(function (target, index) {
        if (!hasError || !target.hasAttribute('data-page-keep-on-error')) {
          replace(target, incoming[index]);
        } else {
          var error = incoming[index].querySelector('.validation-summary-errors, .validation-summary-valid');
          var previous = target.querySelector('.validation-summary-valid, .validation-summary-errors');
          if (error && previous) previous.replaceWith(error);
        }
      });
      var filterForm = document.querySelector('form[data-page-context]');
      var incomingForm = filterForm && template.content.querySelector('#' + CSS.escape(filterForm.id));
      if (incomingForm) {
        filterForm.dataset.pageContext.split(' ').forEach(function (name) {
          var field = filterForm.elements.namedItem(name), current = incomingForm.elements.namedItem(name);
          if (field && current) field.value = current.value;
        });
        var cancel = filterForm.querySelector('[data-page-context-cancel]');
        if (cancel) {
          var cancelUrl = new URL(finalUrl);
          cancelUrl.searchParams.delete('edit');
          cancel.href = cancelUrl.href;
        }
      }
      if (!options.group && filterForm && filterForm.dataset.pageEdit) {
        finalUrl.searchParams.set('edit', filterForm.dataset.pageEdit);
      }
      if (!options.keepUrl && !options.restore && (!write || response.redirected) && finalUrl.href !== location.href) {
        history[options.replaceHistory ? 'replaceState' : 'pushState'](null, '', finalUrl);
      }
      currentUrl = location.href;
      if (submitted) unknownWrites.delete(submitted);
      status.textContent = write ? '' : '結果已更新。';
      status.hidden = write;
      return true;
    } catch (error) {
      if (active !== request) return;
      if (options.restore) history.replaceState(null, '', currentUrl);
      if (submitted && submitted.hasAttribute('data-page-write')) unknownWrites.add(submitted);
      status.className = 'message error';
      var reason = error.name === 'AbortError' ? '等待伺服器回應逾時。' : (error.message || '');
      status.textContent = (write ? '操作結果待核對，請確認後再送出。' : '查詢失敗，已保留目前畫面。') + reason;
    } finally {
      clearTimeout(timer);
      if (active === request) {
        active = null;
        targets.forEach(function (target) { target.removeAttribute('aria-busy'); });
        document.dispatchEvent(new CustomEvent('wh:page-settled', { detail: targets }));
      }
    }
  }

  document.addEventListener('submit', function (event) {
    var form = event.target;
    if (event.defaultPrevented || !form.matches('form[data-page-query], form[data-page-search], form[data-page-post], form[data-page-write]')) return;
    var button = event.submitter;
    var url = new URL(button && button.hasAttribute('formaction') ? button.formAction : form.action, location.href);
    if (!samePage(url) || form.target && form.target !== '_self') return;
    event.preventDefault();
    if (active && active.write) return;
    if (unknownWrites.has(form) && !window.confirm('上次操作結果待核對。確認尚未生效後，才重新送出。現在要重新送出嗎？')) return;
    var data = new FormData(form);
    if (button && button.name) data.append(button.name, button.value);
    if (form.hasAttribute('data-page-post') || form.hasAttribute('data-page-write')) {
      update(url, {
        body: data, form: form, group: form.dataset.pageWrite || form.dataset.pagePost,
        keepUrl: form.hasAttribute('data-page-keep-url'), replaceHistory: form.hasAttribute('data-page-replace-url')
      });
    } else {
      url.search = new URLSearchParams(data).toString();
      update(url, {});
    }
  });
  document.addEventListener('click', function (event) {
    if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    var link = event.target.closest('a[data-page-link], a[data-page-edit-link], [data-page-region=""] .pager a, [data-page-region~="query"] .pager a');
    if (!link || link.hasAttribute('download') || link.target && link.target !== '_self') return;
    var url = new URL(link.href);
    if (!samePage(url) || url.hash) return;
    event.preventDefault();
    update(url, { group: link.dataset.pageEditLink || link.dataset.pageLink, keepUrl: link.hasAttribute('data-page-keep-url') });
  });
  window.addEventListener('popstate', function () {
    if (location.href === currentUrl || !document.querySelector(queryRegions)) return;
    if (active && active.write) { history.replaceState(null, '', currentUrl); return; }
    var url = new URL(location.href);
    var context = document.querySelector('form[data-page-context]');
    var group = context && (context.dataset.pageEdit || '') !== (url.searchParams.get('edit') || '') ? context.dataset.pageWrite : null;
    update(url, { restore: true, group: group });
  });
  window.WhPageQuery = function (url) { return update(new URL(url, location.href), { replaceHistory: true }); };
  var priorUpdateBlock = window.WhPageUpdateBlocked;
  window.WhPageUpdateBlocked = function () {
    return !!(active && active.write || priorUpdateBlock && priorUpdateBlock());
  };
})();
