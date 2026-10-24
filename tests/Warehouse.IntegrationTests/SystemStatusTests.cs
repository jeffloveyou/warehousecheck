using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;

namespace Warehouse.IntegrationTests;

/// <summary>M7-02 系統狀態頁與檔案日誌（T-M7-02-01～03）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public sealed class SystemStatusTests : IDisposable
{
    private const string Password = "Test-pass-123";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("warehouse-status-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Delete(recursive: true);

    // T-M7-02-01：只有系統管理員看得到；顯示資料庫與備份狀態，沒有外部複本、WAL 未開時列出警告
    [Fact]
    public async Task 系統管理員看狀態頁_警告沒有外部複本()
    {
        var finishedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "backup-status.json"), JsonSerializer.Serialize(new
        {
            finishedAt,
            succeeded = true,
            durationSeconds = 12.5,
            files = new[] { new { kind = "dump", name = "warehouse_prod-20261023.dump", sizeMb = 3.2 } },
            backupRoot = @"C:\WarehouseBackup",
            freeGb = 200.0,
            externalCopyPath = "",
        }), Ct);
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString, "Production", new Dictionary<string, string>
        {
            ["Backup:StatusDirectory"] = _dir.FullName,
            ["Storage:AttachmentsDirectory"] = _dir.FullName,
        });

        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var admin = WebSession.Create(factory);
        await admin.LoginAsync(adminName, Password);
        var html = await admin.GetStringAsync("/Admin/System");
        Assert.Equal("", System.Text.RegularExpressions.Regex.Match(html, "無法取得資料庫資訊[^<]*").Value);
        Assert.Contains("id=\"system-db\"", html);
        Assert.Contains("warehouse_prod-20261023.dump", html);
        Assert.Contains("備份只存在這台電腦", html);
        Assert.DoesNotContain("超過 26 小時沒有完成備份", html);

        foreach (var role in new[] { Roles.WarehouseManager, Roles.Operator, Roles.ItemManager, Roles.Viewer })
        {
            var name = NewUserName();
            await WebSession.CreateUserAsync(factory, name, Password, role);
            var web = WebSession.Create(factory);
            await web.LoginAsync(name, Password);
            var denied = await web.Client.GetAsync("/Admin/System", Ct);
            Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
        }
    }

    // T-M7-02-02：備份紀錄過舊或沒有紀錄時警告
    [Fact]
    public async Task 備份過舊或沒有紀錄_列出警告()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString, "Production", new Dictionary<string, string>
        {
            ["Backup:StatusDirectory"] = _dir.FullName,
            ["Storage:AttachmentsDirectory"] = _dir.FullName,
        });
        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var admin = WebSession.Create(factory);
        await admin.LoginAsync(adminName, Password);

        Assert.Contains("找不到備份紀錄", await admin.GetStringAsync("/Admin/System"));

        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "backup-status.json"), JsonSerializer.Serialize(new
        {
            finishedAt = DateTimeOffset.UtcNow.AddHours(-30),
            succeeded = false,
            message = "pg_dump 失敗",
            externalCopyPath = @"E:\Backup",
            externalCopySucceeded = true,
        }), Ct);
        var html = await admin.GetStringAsync("/Admin/System");
        Assert.Contains("超過 26 小時沒有完成備份", html);
        Assert.Contains("pg_dump 失敗", html);
        Assert.DoesNotContain("備份只存在這台電腦", html);
    }

    // T-M7-02-03：檔案日誌一行一筆 JSON，含等級、類別、版本；Warning 以下的框架日誌不寫
    [Fact]
    public async Task 檔案日誌寫JSON行()
    {
        var factory = new WarehouseWebFactory(TestDatabase.ConnectionString, settings: new Dictionary<string, string>
        {
            ["Logging:File:Directory"] = _dir.FullName,
            ["Logging:File:Name"] = "WarehouseT",
        });
        await using (factory)
        {
            using var client = factory.CreateClient();
            var loggers = factory.Services.GetRequiredService<ILoggerFactory>();
            loggers.CreateLogger("Warehouse.Test").LogInformation("測試訊息 {Value}", 42);
            loggers.CreateLogger("Microsoft.Test").LogInformation("框架訊息不寫");
            loggers.CreateLogger("Microsoft.Test").LogWarning("框架警告要寫");
        }

        var file = Assert.Single(_dir.GetFiles("WarehouseT-*.log"));
        var lines = (await File.ReadAllLinesAsync(file.FullName, Ct)).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var info = Assert.Single(lines, l => l.GetProperty("category").GetString() == "Warehouse.Test");
        Assert.Equal("Information", info.GetProperty("level").GetString());
        Assert.Equal("測試訊息 42", info.GetProperty("message").GetString());
        Assert.False(string.IsNullOrEmpty(info.GetProperty("version").GetString()));
        Assert.DoesNotContain(lines, l => l.GetProperty("message").GetString() == "框架訊息不寫");
        Assert.Contains(lines, l => l.GetProperty("message").GetString() == "框架警告要寫");
    }

    private static string NewUserName() => "s" + Guid.NewGuid().ToString("N")[..10];
}
