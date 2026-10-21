using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 以指定連線字串啟動 Warehouse.Web 的測試主機。
/// 使用 Testing 環境避免載入開發機 user-secrets；以 UseSetting 注入，
/// 因為 Program 在 Build 前就讀取 ConnectionStrings:Warehouse。
/// </summary>
public sealed class WarehouseWebFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public WarehouseWebFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Warehouse", _connectionString);
    }
}
