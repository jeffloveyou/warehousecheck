using System.Text.Json;

namespace Warehouse.IntegrationTests;

/// <summary>M0-10 設定與發布目錄分離：WAREHOUSE_CONFIG_DIR（T-M0-10-01～02）。</summary>
public class ExternalConfigurationTests
{
    // T-M0-10-01
    [Fact]
    public async Task 外部設定目錄_可提供連線字串()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("warehouse-config-");
        try
        {
            var json = JsonSerializer.Serialize(new { ConnectionStrings = new { Warehouse = TestDatabase.ConnectionString } });
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "appsettings.Testing.json"), json, ct);

            await using var factory = new WarehouseWebFactory(
                connectionString: null,
                settings: new Dictionary<string, string> { ["WAREHOUSE_CONFIG_DIR"] = dir.FullName });
            using var client = factory.CreateClient();

            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/system/info", ct));
            Assert.True(document.RootElement.GetProperty("databaseConnected").GetBoolean());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // T-M0-10-02
    [Fact]
    public void 外部設定目錄不存在_啟動失敗並說明原因()
    {
        using var factory = new WarehouseWebFactory(
            connectionString: TestDatabase.UnreachableConnectionString,
            settings: new Dictionary<string, string> { ["WAREHOUSE_CONFIG_DIR"] = @"Z:\not-exist-warehouse-config" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("WAREHOUSE_CONFIG_DIR", error.ToString());
    }
}
