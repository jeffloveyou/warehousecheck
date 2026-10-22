using Warehouse.Api;

namespace Warehouse.Web;

/// <summary>
/// /sw.js（M5-02，ADR-0016，docs/11 §7）：Service Worker 由伺服器輸出，內容帶版本號，所以每次發版瀏覽器都會發現新版。
/// 快取範圍刻意很小：
/// - 只快取「網址帶版本參數 ?v=」的 /css、/js、/lib 檔（asp-append-version 產生，內容變了網址就變）、/icons 與離線頁。
///   沒有版本參數的檔案（例如按需載入的 /js/camera-scan.js、/lib/jsqr/jsQR.js）不經快取：
///   否則新版 SW 等待使用者同意期間，舊快取會把舊版程式配給新版頁面（PR 4 審查必修 1）。
/// - 頁面（含登入後的內容）、/api、/files、所有非 GET 一律不經 Service Worker：不會顯示過時的庫存，也不會留下登入後的資料。
/// - 導覽（開頁）失敗時才顯示離線頁；有導覽預載時用預載回應，舊手機不必等 SW 啟動。
/// - 新版不自動接手（不呼叫 skipWaiting），等畫面上的 pwa.js 收到使用者同意才接手（D-23：作業完成後再更新）。
/// 緊急停用：設定 <c>Pwa:Disabled=true</c> 時改輸出「自我註銷」版：立即接手、刪除快取、取消註冊，不攔截任何請求、
/// 不重新載入任何畫面（正在作業的頁面照常運作，下次開頁就不再有 SW）。回滾到 M5-02 之前的版本前要先這樣做（docs/11 §7）。
/// 回應帶 Cache-Control: no-cache，瀏覽器每次都會向伺服器確認 sw.js 是否改變。
/// </summary>
public static class ServiceWorkerEndpoint
{
    public static IEndpointRouteBuilder MapServiceWorker(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var version = AppVersion.Current.Replace("'", string.Empty).Replace("\\", string.Empty);
        var script = configuration.GetValue<bool>("Pwa:Disabled")
            ? Unregister.Replace("__VERSION__", version, StringComparison.Ordinal)
            : Script.Replace("__VERSION__", version, StringComparison.Ordinal);
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
        const PRECACHE = [OFFLINE, '/js/offline.js', '/icons/icon-192.png'];
        const VERSIONED = /^\/(css|js|lib)\//;

        self.addEventListener('install', (event) => {
          event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)));
        });

        self.addEventListener('activate', (event) => {
          event.waitUntil((async () => {
            const keys = await caches.keys();
            await Promise.all(keys.filter((k) => k.startsWith('wh-static-') && k !== CACHE).map((k) => caches.delete(k)));
            if (self.registration.navigationPreload) await self.registration.navigationPreload.enable();
            await self.clients.claim();
          })());
        });

        // 使用者在畫面上按「更新」才接手
        self.addEventListener('message', (event) => {
          if (event.data === 'skipWaiting') self.skipWaiting();
        });

        function cacheFirst(request) {
          return caches.open(CACHE).then((cache) => cache.match(request).then((hit) => hit || fetch(request).then((response) => {
            if (response.ok && response.type === 'basic') cache.put(request, response.clone());
            return response;
          })));
        }

        self.addEventListener('fetch', (event) => {
          const request = event.request;
          if (request.method !== 'GET') return;
          const url = new URL(request.url);
          if (url.origin !== self.location.origin) return;
          if (request.mode === 'navigate') {
            event.respondWith((async () => {
              try {
                return (await event.preloadResponse) || await fetch(request);
              } catch (e) {
                return (await caches.match(OFFLINE)) || Response.error();
              }
            })());
            return;
          }
          if ((VERSIONED.test(url.pathname) && url.searchParams.has('v')) || url.pathname.startsWith('/icons/')
              || url.pathname === OFFLINE || url.pathname === '/js/offline.js') {
            event.respondWith(cacheFirst(request));
          }
          // 其他（沒有版本參數的檔案、/api、/files、頁面的 fetch）不處理，照瀏覽器預設走網路
        });
        """;

    private const string Unregister = """
        // 倉庫管理系統 Service Worker：已停用（Pwa:Disabled，版本 __VERSION__）。立即接手、刪除快取、取消註冊；不攔截請求、不重新載入畫面。
        'use strict';
        self.addEventListener('install', () => self.skipWaiting());
        self.addEventListener('activate', (event) => {
          event.waitUntil((async () => {
            const keys = await caches.keys();
            await Promise.all(keys.filter((k) => k.startsWith('wh-static-')).map((k) => caches.delete(k)));
            await self.registration.unregister();
          })());
        });
        """;
}
