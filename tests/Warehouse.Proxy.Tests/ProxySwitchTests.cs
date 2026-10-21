using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Warehouse.Proxy.Tests;

/// <summary>
/// M0-12 反向代理切換（T-M0-12-03～04）：兩個以真實 Kestrel 執行的假後端 A、B，
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
        await WriteUpstreamAsync(_backendA, ct);
        await using var proxy = CreateProxy();
        using var client = proxy.CreateClient();

        Assert.Equal("A", await client.GetStringAsync("/slot", ct));

        await WriteUpstreamAsync(_backendB, ct);

        var stopwatch = Stopwatch.StartNew();
        string slot;
        do
        {
            await Task.Delay(100, ct);
            slot = await client.GetStringAsync("/slot", ct);
        } while (slot != "B" && stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("B", slot);
    }

    // T-M0-12-04
    [Fact]
    public async Task 轉送時帶X_Forwarded_Proto()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteUpstreamAsync(_backendA, ct);
        await using var proxy = CreateProxy();
        using var client = proxy.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        Assert.Equal("https", await client.GetStringAsync("/proto", ct));
    }

    private WebApplicationFactory<Program> CreateProxy() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("WAREHOUSE_CONFIG_DIR", _configDir.FullName);
        });

    private async Task WriteUpstreamAsync(WebApplication backend, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            ReverseProxy = new { Clusters = new { app = new { Destinations = new { active = new { Address = AddressOf(backend) } } } } },
        });
        await File.WriteAllTextAsync(Path.Combine(_configDir.FullName, UpstreamFile.FileName), json, ct);
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
