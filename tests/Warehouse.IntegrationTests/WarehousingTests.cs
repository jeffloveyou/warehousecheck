using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M3-01／M3-06 倉庫、儲位、系統設定與稽核（T-M3-01-05～11），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class WarehousingTests
{
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", IsItemManager: false);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M3-01-05
    [Fact]
    public async Task 倉庫代碼唯一_同代碼儲位可在不同倉庫_同倉重複被拒()
    {
        var code = Code();
        var w1 = await WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(code, "主倉"), Manager, Ct));
        var w2 = await WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(Code(), "副倉"), Manager, Ct));

        var dup = await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(code, "重複"), Manager, Ct)));
        Assert.Equal("倉庫代碼已存在。", dup.Message);

        await WithAsync(s => s.CreateLocationAsync(w1, new LocationInput("A-01", Empty), Manager, Ct));
        await WithAsync(s => s.CreateLocationAsync(w2, new LocationInput("A-01", Empty), Manager, Ct));
        var dupLocation = await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithAsync(s => s.CreateLocationAsync(w1, new LocationInput("A-01", Empty), Manager, Ct)));
        Assert.Contains("相同的儲位碼", dupLocation.Message);
    }

    // T-M3-01-06
    [Fact]
    public async Task 停用的倉庫不能新增儲位_重新啟用後可以()
    {
        var w = await WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(Code(), "停用測試"), Manager, Ct));
        var version = (await WithAsync(s => s.ListWarehousesAsync(Ct))).Single(x => x.Id == w).Version;
        await WithAsync(s => s.SetWarehouseActiveAsync(w, version, false, Manager, Ct));

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithAsync(s => s.CreateLocationAsync(w, new LocationInput("A-01", Empty), Manager, Ct)));

        version = (await WithAsync(s => s.ListWarehousesAsync(Ct))).Single(x => x.Id == w).Version;
        await WithAsync(s => s.SetWarehouseActiveAsync(w, version, true, Manager, Ct));
        await WithAsync(s => s.CreateLocationAsync(w, new LocationInput("A-01", Empty), Manager, Ct));
    }

    // T-M3-01-07
    [Fact]
    public async Task 修改與狀態變更寫入稽核_舊版本被拒()
    {
        var w = await WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(Code(), "稽核測試"), Manager, Ct));
        var id = await WithAsync(s => s.CreateLocationAsync(w, new LocationInput("B-01", Empty), Manager, Ct));
        var stale = (await WithAsync(s => s.GetLocationAsync(id, Ct)))!.Version;

        await WithAsync(s => s.UpdateLocationAsync(id, stale, Empty with { Zone = "B", Hazardous = true }, Manager, Ct));
        var current = (await WithAsync(s => s.GetLocationAsync(id, Ct)))!;
        await WithAsync(s => s.ChangeLocationStatusAsync(id, current.Version, LocationStatus.Inspection, Manager, Ct));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            WithAsync(s => s.UpdateLocationAsync(id, stale, Empty, Manager, Ct)));

        await using var db = MigratedDatabaseFixture.CreateContext();
        var audits = await db.AuditLogs.Where(a => a.EntityId == id.ToString()).OrderBy(a => a.OccurredAt).ToListAsync(Ct);
        Assert.Equal(["location.create", "location.update", "location.status"], audits.Select(a => a.Action));
        // jsonb 讀回時會重新排版（冒號後加空白），用 JSON 解析比對
        Assert.True(System.Text.Json.JsonDocument.Parse(audits[1].After!).RootElement.GetProperty("Hazardous").GetBoolean());
        Assert.Contains("Inspection", audits[2].After);
        Assert.All(audits, a => Assert.Equal(Manager.Name, a.ActorName));
    }

    // T-M3-01-08
    [Fact]
    public async Task 儲位查詢_小寫或全形儲位碼可找到_依倉庫與狀態篩選_分頁()
    {
        var w = await WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(Code(), "查詢測試"), Manager, Ct));
        for (var n = 1; n <= 3; n++)
        {
            await WithAsync(s => s.CreateLocationAsync(w, new LocationInput($"Q-0{n}", Empty with { Name = $"第{n}格" }), Manager, Ct));
        }

        var lower = await WithAsync(s => s.SearchLocationsAsync(new LocationSearch(w, "q-02", null), Ct));
        var fullWidth = await WithAsync(s => s.SearchLocationsAsync(new LocationSearch(w, "Ｑ－０３", null), Ct));
        var byName = await WithAsync(s => s.SearchLocationsAsync(new LocationSearch(w, "第1格", null), Ct));
        var page = await WithAsync(s => s.SearchLocationsAsync(new LocationSearch(w, null, LocationStatus.Available, 2, 2), Ct));

        Assert.Equal("Q-02", Assert.Single(lower.Items).Code);
        Assert.Equal("Q-03", Assert.Single(fullWidth.Items).Code);
        Assert.Equal("Q-01", Assert.Single(byName.Items).Code);
        Assert.Equal((3, 1), (page.TotalCount, page.Items.Count));
        Assert.StartsWith("WH:LOC:", lower.Items[0].QrContent);
    }

    // T-M3-01-09（D-07 設定變更留稽核）
    [Fact]
    public async Task 設定變更只對有變的項目寫稽核()
    {
        var admin = new Actor(Guid.NewGuid(), "系統管理員", false);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var settings = new SettingsService(db, TimeProvider.System);
            var before = await settings.GetAsync(Ct);
            await settings.UpdateAsync(before with { AllowSelfApproveOpening = !before.AllowSelfApproveOpening }, admin, Ct);
            Assert.Equal(!before.AllowSelfApproveOpening, (await settings.GetAsync(Ct)).AllowSelfApproveOpening);
            await settings.UpdateAsync(before, admin, Ct); // 改回原值
        }

        await using var check = MigratedDatabaseFixture.CreateContext();
        var audits = await check.AuditLogs
            .Where(a => a.Action == "setting.update" && a.ActorId == admin.Id)
            .ToListAsync(Ct);
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.Equal(SettingKeys.AllowSelfApproveOpening, a.EntityId));
    }

    // T-M3-01-10
    [Theory]
    [InlineData("UPDATE audit_logs SET actor_name = '竄改'")]
    [InlineData("DELETE FROM audit_logs")]
    [InlineData("TRUNCATE audit_logs")]
    public async Task 稽核紀錄不可修改刪除或清空(string sql)
    {
        await WithAsync(s => s.CreateWarehouseAsync(new WarehouseInput(Code(), "稽核保護"), Manager, Ct));
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }

    private static string Code() => "T" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();

    private static async Task<T> WithAsync<T>(Func<IWarehouseService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task WithAsync(Func<IWarehouseService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new WarehouseService(db, TimeProvider.System));
    }
}
