using Warehouse.Api;

namespace Warehouse.Web;

/// <summary>
/// /sw.js（M5-02，ADR-0016，docs/11）：Service Worker 由伺服器輸出，內容帶版本號，所以每次發版瀏覽器都會發現新版。
/// 快取範圍刻意很小：
/// - 只快取 /css、/js、/lib、/icons 與離線頁（快取名稱含版本，新版接手時刪掉舊快取）。
/// - 頁面（含登入後的內容）、/api、/files、所有非 GET 一律不經 Service Worker：不會顯示過時的庫存，也不會留下登入後的資料。
/// - 導覽（開頁）失敗時才顯示離線頁；離線頁明確說明不能送出。
/// - 新版不自動接手（不呼叫 skipWaiting），等畫面上的 pwa.js 收到使用者同意才接手（D-23：作業完成後再更新）。
/// 回應帶 Cache-Control: no-cache，瀏覽器每次都會向伺服器確認 sw.js 是否改變。
/// </summary>
public static class ServiceWorkerEndpoint
{
    public static IEndpointRouteBuilder MapServiceWorker(this IEndpointRouteBuilder endpoints)
    {
        var script = Script.Replace("__VERSION__", AppVersion.Current.Replace("'", string.Empty), StringComparison.Ordinal);
        endpoints.MapGet("/sw.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Text(script, "text/javascript; charset=utf-8");
        }).AllowAnonymous();
        return endpoints;
    }

    private const string Script = """
        // 倉庫管理系統 Service Worker（由 ServiceWorkerEndpoint.cs 產生，版本 __VERSION__）
        'use strict';
        const CACHE = 'wh-static-__VERSION__';
        const OFFLINE = '/offline.html';
        const PRECACHE = [OFFLINE, '/icons/icon-192.png'];
        const STATIC = /^\/(css|js|lib|icons)\//;

        self.addEventListener('install', (event) => {
          event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)));
        });

        self.addEventListener('activate', (event) => {
          event.waitUntil(caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k.startsWith('wh-static-') && k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim()));
        });

        // 使用者在畫面上按「更新」才接手
        self.addEventListener('message', (event) => {
          if (event.data === 'skipWaiting') self.skipWaiting();
        });

        self.addEventListener('fetch', (event) => {
          const request = event.request;
          if (request.method !== 'GET') return;
          const url = new URL(request.url);
          if (url.origin !== self.location.origin) return;
          if (request.mode === 'navigate') {
            event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
            return;
          }
          if (STATIC.test(url.pathname)) {
            event.respondWith(caches.open(CACHE).then((cache) => cache.match(request).then((hit) => hit || fetch(request).then((response) => {
              if (response.ok && response.type === 'basic') cache.put(request, response.clone());
              return response;
            }))));
          }
          // 其他（/api、/files、頁面的 fetch）不處理，照瀏覽器預設走網路
        });
        """;
}
