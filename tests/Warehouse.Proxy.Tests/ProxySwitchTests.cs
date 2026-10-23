using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Warehouse.Proxy.Tests;

/// <summary>
/// M0-12 反向代理切換（T-M0-12-03～04）與 M7-10 強化（T-M7-10-01～04）：兩個以真實 Kestrel 執行的假後端 A、B，
/// 代理只轉到 upstream.json 指定的實例；改寫檔案後新請求改到另一個實例。
/// </summary>
public sealed class ProxySwitchTests : IAsyncLifetime
{
    private readonly DirectoryInfo _configDir = Directory.CreateTempSubdirectory("warehouse-proxy-");
    private WebApplication _backendA = null!;
    private WebApplication _backendB = null!;

    public async ValueTask InitializeAsync()
    {
        _backendA = await StartBackendAsync("A");
        _backendB = await StartBackendAsync("B");
    }

    public async ValueTask DisposeAsync()
    {
        await _backendA.DisposeAsync();
        await _backendB.DisposeAsync();
        _configDir.Delete(recursive: true);
    }

    // T-M0-12-03
    [Fact]
    public async Task 改寫upstream檔案_新請求改送到另一個實例()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteUpstreamAsync("A", _backendA, ct);
        await using var proxy = CreateProxy();
        using var client = proxy.CreateClient();

        Assert.Equal("A", await client.GetStringAsync("/slot", ct));

        await WriteUpstreamAsync("B", _backendB, ct);

        Assert.Equal("B", await WaitForSlotAsync(client, "B", ct));
    }

    // T-M0-12-04
    [Fact]
    public async Task 轉送時帶X_Forwarded_Proto()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteUpstreamAsync("A", _backendA, ct);
        await using var proxy = CreateProxy();
        using var client = proxy.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        Assert.Equal("https", await client.GetStringAsync("/proto", ct));
    }

    // T-M7-10-01：switch-release 以槽位標頭確認切換（兩槽位同版本也能確認）
    [Fact]
    public async Task 回應帶目前槽位標頭()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteUpstreamAsync("A", _backendA, ct);
        await using var proxy = CreateProxy();
        using var client = proxy.CreateClient();

        using var first = await client.GetAsync("/slot", ct);
        Assert.Equal("A", first.Headers.GetValues(UpstreamFile.SlotHeader).Single());

        await WriteUpstreamAsync("B", _backendB, ct);
        await WaitForSlotAsync(client, "B", ct);
        using var second = await client.GetAsync("/slot", ct);
        Assert.Equal("B", second.Headers.GetValues(UpstreamFile.SlotHeader).Single());
    }

    // T-M7-10-02：upstream.json 被寫壞時保留上一份有效設定（原本目的地消失、全部 503）
    [Fact]
    public async Task upstream無效時保留上一份目的地_之後有效內容照常生效()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteUpstreamAsync("A", _backendA, ct);
        await using var proxy = CreateProxy();
        using var client = proxy.CreateClient();
        Assert.Equal("A", await client.GetStringAsync("/slot", ct));

        await WriteRawAsync("{ 這不是 JSON", ct);
        await Task.Delay(1000, ct);
        Assert.Equal("A", await client.GetStringAsync("/slot", ct));

        await WriteRawAsync("""{ "ActiveSlot": "C", "ReverseProxy": {} }""", ct);
        await Task.Delay(1000, ct);
        Assert.Equal("A", await client.GetStringAsync("/slot", ct));

        await WriteUpstreamAsync("B", _backendB, ct);
        Assert.Equal("B", await WaitForSlotAsync(client, "B", ct));
    }

    // T-M7-10-03：啟動時檔案無效 → 啟動失敗（不在沒有目的地的情況下運作）
    [Fact]
    public async Task 啟動時upstream無效_啟動失敗()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteRawAsync("""{ "ActiveSlot": "A" }""", ct);
        await using var proxy = CreateProxy();

        var ex = Assert.ThrowsAny<Exception>(() => proxy.CreateClient());
        Assert.Contains("Address", ex.ToString());
    }

    // T-M7-10-04
    [Theory]
    [InlineData("""{ "ActiveSlot": "A", "ReverseProxy": { "Clusters": { "app": { "Destinations": { "active": { "Address": "http://localhost:5080/" } } } } } }""", true)]
    [InlineData("""{ "ActiveSlot": "B", "ReverseProxy": { "Clusters": { "app": { "Destinations": { "active": { "Address": "ftp://x/" } } } } } }""", false)]
    [InlineData("""{ "ReverseProxy": { "Clusters": { "app": { "Destinations": { "active": { "Address": "http://localhost:5080/" } } } } } }""", false)]
    [InlineData("""[]""", false)]
    public void 解析upstream內容(string json, bool valid)
    {
        if (valid)
        {
            var upstream = UpstreamFile.Parse(json);
            Assert.Equal("A", upstream.Slot);
            Assert.Equal(new Uri("http://localhost:5080/"), upstream.Address);
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => UpstreamFile.Parse(json));
        }
    }

    private WebApplicationFactory<Program> CreateProxy() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("WAREHOUSE_CONFIG_DIR", _configDir.FullName);
        });

    private static async Task<string> WaitForSlotAsync(HttpClient client, string expected, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string slot;
        do
        {
            await Task.Delay(100, ct);
            slot = await client.GetStringAsync("/slot", ct);
        } while (slot != expected && stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        return slot;
    }

    private Task WriteUpstreamAsync(string slot, WebApplication backend, CancellationToken ct) =>
        WriteRawAsync(JsonSerializer.Serialize(new
        {
            ActiveSlot = slot,
            ReverseProxy = new { Clusters = new { app = new { Destinations = new { active = new { Address = AddressOf(backend) } } } } },
        }), ct);

    // 與 switch-release 相同：暫存檔＋置換，讀取端不會讀到寫一半的內容
    private async Task WriteRawAsync(string content, CancellationToken ct)
    {
        var path = Path.Combine(_configDir.FullName, UpstreamFile.FileName);
        await File.WriteAllTextAsync(path + ".tmp", content, ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // 啟動後 Urls 會是實際綁定的位址（埠 0 → 系統配置的埠）
    private static string AddressOf(WebApplication app) => app.Urls.First();

    private static async Task<WebApplication> StartBackendAsync(string slot)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/slot", () => slot);
        app.MapGet("/proto", (HttpRequest request) => request.Headers["X-Forwarded-Proto"].ToString());
        await app.StartAsync();
        return app;
    }
}
