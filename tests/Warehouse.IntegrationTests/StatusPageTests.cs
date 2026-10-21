using System.Net;

namespace Warehouse.IntegrationTests;

/// <summary>M0-09 Razor Pages 系統狀態頁（T-M0-09-01～02）。</summary>
public class StatusPageTests
{
    // T-M0-09-01
    [Fact]
    public async Task 首頁_資料庫可用時顯示版本與已連線()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("系統狀態", html);
        Assert.Contains("已連線", html);
        Assert.Contains("id=\"version\"", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    // T-M0-09-02
    [Fact]
    public async Task 首頁_資料庫不可用時仍可開啟並顯示無法連線()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("無法連線", html);
    }
}
