using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Warehouse.IntegrationTests;

/// <summary>M0-04 健康檢查與系統資訊整合測試（T-M0-04-01～04）。</summary>
public class HealthEndpointTests
{
    // T-M0-04-01
    [Fact]
    public async Task Live_資料庫不可用時仍回傳200()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // T-M0-04-02
    [Fact]
    public async Task Ready_資料庫可用時回傳200且狀態為Healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
    }

    // T-M0-04-03
    [Fact]
    public async Task Ready_資料庫不可用時回傳503且五秒內回應()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString);
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync("/health/ready", ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"ready 應在 5 秒內回應，實際 {stopwatch.Elapsed.TotalSeconds:F2} 秒。");
    }

    // T-M0-04-04
    [Fact]
    public async Task SystemInfo_資料庫可用時回傳版本與連線狀態且不洩漏密碼()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = TestDatabase.ConnectionString;
        await using var factory = new WarehouseWebFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/system/info", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        Assert.True(root.GetProperty("databaseConnected").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("databaseVersion").GetString()));

        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        var password = TestDatabase.ExtractPassword(connectionString);
        if (!string.IsNullOrEmpty(password))
        {
            Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        }
    }
}
