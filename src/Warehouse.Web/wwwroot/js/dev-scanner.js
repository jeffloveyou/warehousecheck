// /dev/scanner：條碼槍／PDA 連續掃碼測試（M0-05）。只在這一頁載入，不使用框架，相容 Chrome 109。
// 做法與 M5 正式掃碼相同：
//  1. 條碼槍、PDA（Keyboard Wedge）把條碼當成鍵盤輸入，最後送出 Enter 或 Tab。
//  2. 收到 Enter／Tab 時立刻取值並清空輸入框，焦點不離開，下一筆的字元可以馬上進來。
//  3. 取到的值放進佇列，由單一 worker 依序「查詢」（這裡是 ?handler=Echo，可加延遲模擬網路），結果順序與掃碼順序一致。
// 另外記錄每筆從第一個字到最後一個字的時間：太慢代表設備字元間隔設太大，太快且舊電腦掉字代表要調慢。
(function () {
  'use strict';

  var input = document.getElementById('scan-input');
  var tbody = document.querySelector('#scan-list tbody');
  var expectedBox = document.getElementById('expected');
  var scans = [];
  var queue = [];
  var working = false;
  var firstAt = null;
  var lastAt = null;

  function text(id, value) { document.getElementById(id).textContent = value; }

  function expectedList() {
    return expectedBox.value.split('\n').map(function (s) { return s.trim(); }).filter(function (s) { return s.length > 0; });
  }

  // 多重集合比對：預期但沒掃到＝遺失；掃到但不在預期＝變形或多掃；位置不同＝順序錯
  function summary() {
    var expected = expectedList();
    var remaining = {};
    expected.forEach(function (e) { remaining[e] = (remaining[e] || 0) + 1; });
    var unexpected = [];
    var seen = {};
    var duplicates = 0;
    scans.forEach(function (s) {
      if (seen[s.v]) { duplicates++; }
      seen[s.v] = true;
      if (remaining[s.v] > 0) { remaining[s.v]--; } else { unexpected.push(s.v); }
    });
    var missing = [];
    Object.keys(remaining).forEach(function (k) { for (var i = 0; i < remaining[k]; i++) { missing.push(k); } });
    var orderErrors = 0;
    for (var i = 0; i < Math.min(expected.length, scans.length); i++) {
      if (scans[i].v !== expected[i]) { orderErrors++; }
    }
    var times = scans.map(function (s) { return s.ms; });
    return {
      count: scans.length,
      expectedCount: expected.length,
      missing: missing,
      unexpected: unexpected,
      orderErrors: orderErrors,
      duplicates: duplicates,
      avgMs: times.length ? Math.round(times.reduce(function (a, b) { return a + b; }, 0) / times.length) : 0,
      maxMs: times.length ? Math.max.apply(null, times) : 0,
      enter: scans.filter(function (s) { return s.term === 'Enter'; }).length,
      tab: scans.filter(function (s) { return s.term === 'Tab'; }).length,
      failedResponses: scans.filter(function (s) { return s.ok === false; }).length
    };
  }

  function refresh() {
    var s = summary();
    text('scan-count', String(s.count));
    text('queue-count', String(queue.length));
    text('timing', s.count ? '平均 ' + s.avgMs + ' ms，最長 ' + s.maxMs + ' ms' : '-');
    text('terminators', 'Enter ' + s.enter + '、Tab ' + s.tab);
    text('compare', s.expectedCount === 0 ? '（未貼預期清單）'
      : '遺失 ' + s.missing.length + '、不在清單（變形）' + s.unexpected.length + '、順序不同 ' + s.orderErrors
        + '、重複 ' + s.duplicates + '、回應錯誤 ' + s.failedResponses);
  }

  function addRow(scan) {
    var tr = document.createElement('tr');
    [scan.n, scan.v, scan.len, scan.ms + ' ms', scan.term, '…'].forEach(function (value) {
      var td = document.createElement('td');
      td.textContent = String(value);
      tr.appendChild(td);
    });
    tbody.insertBefore(tr, tbody.firstChild);
    return tr.lastChild;
  }

  async function lookup(scan, cell) {
    try {
      var delay = document.getElementById('delay').value || '0';
      var res = await fetch('/dev/scanner?handler=Echo&delay=' + encodeURIComponent(delay) + '&code=' + encodeURIComponent(scan.v),
        { headers: { 'Accept': 'application/json' } });
      var body = res.ok ? await res.json() : null;
      scan.ok = body !== null && body.code === scan.v; // 中文等字元來回後必須完全相同
      cell.textContent = scan.ok ? 'OK' : '不符（HTTP ' + res.status + '）';
    } catch (e) {
      scan.ok = false;
      cell.textContent = '失敗：' + e.message;
    }
    cell.className = scan.ok ? 'ok' : 'bad';
  }

  async function drain() {
    if (working) { return; }
    working = true;
    while (queue.length > 0) {
      var job = queue.shift();
      refresh();
      await lookup(job.scan, job.cell);
    }
    working = false;
    refresh();
  }

  input.addEventListener('input', function () {
    var now = performance.now();
    if (firstAt === null && input.value.length > 0) { firstAt = now; }
    lastAt = now;
  });

  input.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Enter' && ev.key !== 'Tab') { return; }
    ev.preventDefault(); // Tab 不讓焦點跳走，Enter 不送出表單
    var value = input.value.trim();
    input.value = '';
    var ms = firstAt === null ? 0 : Math.round(lastAt - firstAt);
    firstAt = null;
    if (value.length === 0) { return; }
    var scan = { n: scans.length + 1, v: value, len: value.length, ms: ms, term: ev.key, ok: null };
    scans.push(scan);
    queue.push({ scan: scan, cell: addRow(scan) });
    refresh();
    drain();
  });

  document.getElementById('load-default').addEventListener('click', async function () {
    var res = await fetch('/dev/scanner?handler=Codes');
    expectedBox.value = await res.text();
    refresh();
    input.focus();
  });

  expectedBox.addEventListener('change', refresh);

  document.getElementById('reset').addEventListener('click', function () {
    scans = [];
    queue = [];
    tbody.textContent = '';
    text('report-state', '');
    refresh();
    input.focus();
  });

  document.getElementById('report').addEventListener('click', async function () {
    var report = {
      device: document.getElementById('device').value,
      userAgent: navigator.userAgent,
      screen: screen.width + 'x' + screen.height,
      hardwareConcurrency: navigator.hardwareConcurrency || null,
      deviceMemory: navigator.deviceMemory || null,
      delayMs: Number(document.getElementById('delay').value || 0),
      summary: summary(),
      scans: scans.slice(0, 500).map(function (s) { return { v: s.v, ms: s.ms, term: s.term, ok: s.ok }; })
    };
    var token = document.querySelector('input[name="__RequestVerificationToken"]').value;
    try {
      var res = await fetch('/dev/scanner?handler=Report', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
        body: JSON.stringify(report)
      });
      text('report-state', res.ok ? '已送出，謝謝。' : '送出失敗（HTTP ' + res.status + '）');
    } catch (e) {
      text('report-state', '送出失敗：' + e.message);
    }
  });

  refresh();
  input.focus();
})();
