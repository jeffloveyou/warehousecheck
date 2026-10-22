using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Text.Json;
using Warehouse.Api;

namespace Warehouse.IntegrationTests;

/// <summary>M5-02 PWA：manifest、Service Worker、離線頁、圖示（T-M5-02-01～03）。不需要登入。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class PwaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M5-02-01：Service Worker 帶版本、不快取自己、只快取靜態檔
    [Fact]
    public async Task ServiceWorker_不需登入_帶版本_不被快取_只快取靜態檔()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/sw.js", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl!.ToString());
        var script = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("wh-static-" + AppVersion.Current.Replace("'", string.Empty), script);
        Assert.Contains("/offline.html", script);
        Assert.Contains("request.method !== 'GET'", script);   // 非 GET 一律不經快取
        Assert.Contains("url.searchParams.has('v')", script);  // 只快取帶版本參數的靜態檔（PR 4 審查必修 1）
        // 安裝時不自動接手（D-23：等使用者按「更新」）
        var install = script[script.IndexOf("'install'", StringComparison.Ordinal)..script.IndexOf("'activate'", StringComparison.Ordinal)];
        Assert.DoesNotContain("skipWaiting", install);
    }

    // T-M5-02-03（PR 4 審查必修 2）：Pwa:Disabled 時輸出自我註銷版：立即接手、刪快取、取消註冊，不攔截請求
    [Fact]
    public async Task 設定停用PWA時_ServiceWorker改為自我註銷()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("Pwa:Disabled", "true")).CreateClient();

        var script = await client.GetStringAsync("/sw.js", Ct);
        Assert.Contains("self.registration.unregister()", script);
        Assert.Contains("skipWaiting", script);
        Assert.DoesNotContain("'fetch'", script);
    }

    // T-M5-02-02：manifest、離線頁、圖示不需登入；版面有 manifest 連結與 pwa.js
    [Fact]
    public async Task Manifest_離線頁與圖示_不需登入_版面有連結()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var manifest = await client.GetAsync("/manifest.webmanifest", Ct);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync(Ct));
        Assert.Equal("standalone", json.RootElement.GetProperty("display").GetString());
        var icons = json.RootElement.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("src").GetString()!).ToList();
        Assert.Contains("/icons/icon-512.png", icons);
        foreach (var path in icons.Append("/offline.html").Append("/icons/apple-touch-icon.png").Append("/icons/favicon-32.png"))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Ct)).StatusCode);
        }

        var login = await client.GetStringAsync("/Account/Login", Ct);
        Assert.Contains("rel=\"manifest\"", login);
        Assert.Contains("/js/pwa.js", login);
        Assert.Contains("id=\"pwa-update\"", login);
        Assert.Contains("id=\"offline-banner\"", login);
    }
}
