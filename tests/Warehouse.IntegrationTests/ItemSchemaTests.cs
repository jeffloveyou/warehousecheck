using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M1-01 資料表與約束（T-M1-01-05～10）：在真實 PostgreSQL 上驗證 Migration、種子資料，
/// 以及「即使繞過 Domain 直接寫 SQL」資料庫仍會拒絕的規則。寫入都在交易內執行並回滾。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemSchemaTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;

    // T-M1-01-05
    [Fact]
    public async Task 空資料庫套用Migration_內建單位與分類存在()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();

        var units = await db.Units.OrderBy(u => u.Code).Select(u => u.Code).ToListAsync(ct);
        var prefixes = await db.ItemCategories.Select(c => c.ItemCodePrefix).ToListAsync(ct);

        Assert.Equal(["BOX", "G", "KG", "L", "M", "PCS", "ROLL", "SET"], units);
        Assert.Equivalent(new[] { "ME", "EL", "RM", "SF", "FG", "CS" }, prefixes);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
    }

    // T-M1-01-06
    [Fact]
    public async Task 經EF寫入待核准物料_可讀回_多筆沒有料號可並存()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        db.Items.Add(Item.Submit(Guid.NewGuid(), "MR-T00001", "伺服放大器", El, Pcs, TrackingMode.None, "三菱", "MR-J4-10A", null, now));
        db.Items.Add(Item.Submit(Guid.NewGuid(), "MR-T00002", "伺服馬達", El, Pcs, TrackingMode.Serial, null, null, null, now));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var items = await db.Items.Where(i => i.RequestNo.StartsWith("MR-T")).OrderBy(i => i.RequestNo).ToListAsync(ct);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.ItemCode));
        Assert.Equal(ItemStatus.PendingApproval, items[0].Status);
        Assert.Equal(TrackingMode.Serial, items[1].TrackingMode);
        Assert.Equal("MR-J4-10A", items[0].ManufacturerPartNo);
    }

    // T-M1-01-07：唯一約束（23505）
    [Theory]
    [InlineData("INSERT INTO units (id, code, name, decimal_places) VALUES (gen_random_uuid(), 'PCS', '重複', 0)")]
    [InlineData("INSERT INTO item_categories (id, code, name, item_code_prefix) VALUES (gen_random_uuid(), 'EL', '重複', 'EX')")]
    public async Task 重複的單位或分類代碼_被資料庫拒絕(string sql)
    {
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ExecuteExpectingErrorAsync(sql));
    }

    [Fact]
    public async Task 重複的申請單號_被資料庫拒絕()
    {
        var sql = InsertItem("MR-DUP", null, "PendingApproval") + InsertItem("MR-DUP", null, "PendingApproval");
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ExecuteExpectingErrorAsync(sql));
    }

    [Fact]
    public async Task 重複的正式料號_被資料庫拒絕()
    {
        var sql = InsertItem("MR-A1", "EL-000001", "Active") + InsertItem("MR-A2", "EL-000001", "Active");
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ExecuteExpectingErrorAsync(sql));
    }

    // T-M1-01-08：CHECK（23514）
    [Theory]
    [InlineData("MR-C1", null, "Active", "None", false)]                 // 有效物料沒有料號
    [InlineData("MR-C2", "EL-000009", "PendingApproval", "None", false)] // 待核准卻有料號
    [InlineData("MR-C3", "EL-000010", "Rejected", "None", false)]        // 拒絕的物料不得占用料號
    [InlineData("MR-C4", null, "PendingApproval", "Lot", true)]          // 批號物料開負庫存
    [InlineData("MR-C5", null, "Approved", "None", false)]               // 不存在的狀態
    [InlineData("MR-C6", null, "PendingApproval", "Box", false)]         // 不存在的追蹤模式
    public async Task 違反狀態料號與負庫存規則的物料_被資料庫拒絕(
        string requestNo, string? itemCode, string status, string trackingMode, bool allowNegativeStock)
    {
        var sql = InsertItem(requestNo, itemCode, status, trackingMode, allowNegativeStock);
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ExecuteExpectingErrorAsync(sql));
    }

    [Theory]
    [InlineData("INSERT INTO units (id, code, name, decimal_places) VALUES (gen_random_uuid(), 'XX', '錯誤', 5)")]
    [InlineData("INSERT INTO units (id, code, name, decimal_places) VALUES (gen_random_uuid(), 'pcs', '小寫', 0)")]
    [InlineData("INSERT INTO item_categories (id, code, name) VALUES (gen_random_uuid(), 'NOPREFIX', '頂層缺前綴')")]
    public async Task 不合格的單位或分類_被資料庫拒絕(string sql)
    {
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ExecuteExpectingErrorAsync(sql));
    }

    // T-M1-01-09：外鍵 RESTRICT（23503）
    [Fact]
    public async Task 仍有物料的分類_不可刪除()
    {
        var sql = InsertItem("MR-F1", null, "PendingApproval") + $"DELETE FROM item_categories WHERE id = '{El}';";
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ExecuteExpectingErrorAsync(sql));
    }

    // T-M1-01-10
    [Fact]
    public async Task Migration可完整降回再套用()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(Migration.InitialDatabase, cancellationToken: ct);
        var tablesAfterDown = await CountTablesAsync(db, ct);
        await migrator.MigrateAsync(cancellationToken: ct);

        Assert.Equal(0, tablesAfterDown);
        Assert.Equal(3, await CountTablesAsync(db, ct));
        Assert.Equal(8, await db.Units.CountAsync(ct));
    }

    /// <summary>直接以 SQL 插入一筆物料（繞過 Domain，專門測資料庫約束）。值都來自測試常數，不是使用者輸入。</summary>
    private static string InsertItem(
        string requestNo, string? itemCode, string status, string trackingMode = "None", bool allowNegativeStock = false) =>
        "INSERT INTO items (id, request_no, item_code, status, name, category_id, base_unit_id, tracking_mode, " +
        "allow_negative_stock, created_at, updated_at) VALUES (gen_random_uuid(), " +
        $"'{requestNo}', {(itemCode is null ? "NULL" : $"'{itemCode}'")}, '{status}', '測試', '{El}', '{Pcs}', " +
        $"'{trackingMode}', {(allowNegativeStock ? "true" : "false")}, now(), now());";

    /// <summary>在交易內執行 SQL（離開時回滾），回傳 PostgreSQL 錯誤代碼；沒有錯誤則測試失敗。</summary>
    private static async Task<string> ExecuteExpectingErrorAsync(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, ct));
        return error.SqlState;
    }

    private static async Task<int> CountTablesAsync(WarehouseDbContext db, CancellationToken ct) =>
        await db.Database
            .SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name IN ('units', 'item_categories', 'items')")
            .SingleAsync(ct);
}
