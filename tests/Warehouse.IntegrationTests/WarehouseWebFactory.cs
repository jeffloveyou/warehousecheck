using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 以指定連線字串啟動 Warehouse.Web 的測試主機。
/// 預設使用 Testing 環境避免載入開發機 user-secrets；以 UseSetting 注入，
/// 因為 Program 在 Build 前就讀取 ConnectionStrings:Warehouse。
/// </summary>
public sealed class WarehouseWebFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;
    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string> _settings;

    public WarehouseWebFactory(
        string? connectionString,
        string environment = "Testing",
        IReadOnlyDictionary<string, string>? settings = null)
    {
        _connectionString = connectionString;
        _environment = environment;
        _settings = settings ?? new Dictionary<string, string>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        if (_connectionString is not null)
        {
            builder.UseSetting("ConnectionStrings:Warehouse", _connectionString);
        }
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
