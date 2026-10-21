using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M1-01 資料表與約束（T-M1-01-05～14）：在真實 PostgreSQL 上驗證 Migration、種子資料，
/// 以及「即使繞過 Domain 直接寫 SQL」資料庫仍會拒絕的規則。每項同時比對錯誤代碼與約束名稱，
/// 確認是預期的那條規則擋下。寫入都在交易內執行並回滾。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemSchemaTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;

    // T-M1-01-05
    [Fact]
    public async Task 空資料庫套用Migration_內建資料存在_模型與快照一致()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();

        var units = await db.Units.OrderBy(u => u.Code).Select(u => u.Code).ToListAsync(ct);
        var prefixes = await db.ItemCategories.Select(c => c.ItemCodePrefix).ToListAsync(ct);

        Assert.Equal(["BOX", "G", "KG", "L", "M", "PCS", "ROLL", "SET"], units);
        Assert.Equivalent(new[] { "ME", "EL", "RM", "SF", "FG", "CS" }, prefixes);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        // 改了 Configurations 卻忘了新增 Migration 時，這裡會失敗
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // T-M1-01-06
    [Fact]
    public async Task 經EF寫入待核准物料_可讀回_多筆沒有料號可並存_當地時間轉UTC()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var taipeiNow = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(8));

        var servo = Item.Submit(Guid.NewGuid(), "MR-900001", "伺服放大器", El, Pcs, TrackingMode.None, "三菱", "MR-J4-10A", null, taipeiNow);
        servo.SetNegativeStockPolicy(true, 12.5m, taipeiNow);
        db.Items.Add(servo);
        db.Items.Add(Item.Submit(Guid.NewGuid(), "MR-900002", "伺服馬達", El, Pcs, TrackingMode.Serial, null, null, null, taipeiNow));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var items = await db.Items.Where(i => i.RequestNo.StartsWith("MR-9")).OrderBy(i => i.RequestNo).ToListAsync(ct);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.ItemCode));
        Assert.Equal(ItemStatus.PendingApproval, items[0].Status);
        Assert.Equal(TrackingMode.Serial, items[1].TrackingMode);
        Assert.Equal("MR-J4-10A", items[0].ManufacturerPartNo);
        Assert.True(items[0].AllowNegativeStock);
        Assert.Equal(12.5m, items[0].NegativeStockLimit);
        Assert.Equal(taipeiNow, items[0].CreatedAt); // 同一時間點（存成 UTC）
    }

    // T-M1-01-07：唯一約束（23505）
    [Theory]
    [InlineData("INSERT INTO units (id, code, name, decimal_places) VALUES (gen_random_uuid(), 'PCS', '重複', 0);", "ix_units_code")]
    [InlineData("INSERT INTO item_categories (id, code, name, item_code_prefix) VALUES (gen_random_uuid(), 'EL', '重複', 'EX');", "ix_item_categories_code")]
    [InlineData("INSERT INTO item_categories (id, code, name, item_code_prefix) VALUES (gen_random_uuid(), 'EX', '前綴重複', 'EL');", "ix_item_categories_item_code_prefix")]
    public async Task 重複的單位代碼_分類代碼_料號前綴_被資料庫拒絕(string sql, string constraint)
    {
        await AssertRejectedAsync(sql, PostgresErrorCodes.UniqueViolation, constraint);
    }

    [Fact]
    public async Task 重複的申請單號與正式料號_被資料庫拒絕()
    {
        await AssertRejectedAsync(
            InsertItem("MR-900010", null, "PendingApproval") + InsertItem("MR-900010", null, "PendingApproval"),
            PostgresErrorCodes.UniqueViolation, "ix_items_request_no");
        await AssertRejectedAsync(
            InsertItem("MR-900011", "EL-900001", "Active") + InsertItem("MR-900012", "EL-900001", "Active"),
            PostgresErrorCodes.UniqueViolation, "ix_items_item_code");
    }

    // T-M1-01-08：物料 CHECK（23514）
    [Theory]
    [InlineData("MR-900020", null, "Active", "None", false, 0, "ck_items_code_matches_status")]           // 有效物料沒有料號
    [InlineData("MR-900021", null, "Inactive", "None", false, 0, "ck_items_code_matches_status")]         // 停用物料沒有料號
    [InlineData("MR-900022", "EL-900020", "PendingApproval", "None", false, 0, "ck_items_code_matches_status")]
    [InlineData("MR-900023", "EL-900021", "Returned", "None", false, 0, "ck_items_code_matches_status")]
    [InlineData("MR-900024", "EL-900022", "Rejected", "None", false, 0, "ck_items_code_matches_status")]  // 拒絕不占料號
    [InlineData("MR-900025", null, "Approved", "None", false, 0, "ck_items_status")]                      // 未定義狀態
    [InlineData("MR-900026", null, "PendingApproval", "Box", false, 0, "ck_items_tracking_mode")]
    [InlineData("MR-900027", null, "PendingApproval", "Lot", true, 5, "ck_items_negative_stock_tracking")] // 批號開負庫存
    [InlineData("MR-900028", null, "PendingApproval", "None", true, -1, "ck_items_negative_stock_limit")]
    [InlineData("MR-900029", null, "PendingApproval", "None", false, 5, "ck_items_negative_stock_limit_when_disabled")]
    [InlineData("MR-12", null, "PendingApproval", "None", false, 0, "ck_items_request_no_format")]
    [InlineData("MR-900030", "EL-12", "Active", "None", false, 0, "ck_items_item_code_format")]
    public async Task 違反物料規則_被資料庫拒絕(
        string requestNo, string? itemCode, string status, string trackingMode, bool allowNegativeStock, int limit, string constraint)
    {
        await AssertRejectedAsync(
            InsertItem(requestNo, itemCode, status, trackingMode, allowNegativeStock, limit),
            PostgresErrorCodes.CheckViolation, constraint);
    }

    [Fact]
    public async Task 品名空白_被資料庫拒絕()
    {
        await AssertRejectedAsync(InsertItem("MR-900031", null, "PendingApproval", name: "   "),
            PostgresErrorCodes.CheckViolation, "ck_items_name_not_blank");
    }

    // T-M1-01-09：單位與分類 CHECK（23514）
    [Theory]
    [InlineData("INSERT INTO units (id, code, name, decimal_places) VALUES (gen_random_uuid(), 'XX', '錯誤', 5);", "ck_units_decimal_places")]
    [InlineData("INSERT INTO units (id, code, name, decimal_places) VALUES (gen_random_uuid(), 'pcs', '小寫', 0);", "ck_units_code_format")]
    [InlineData("INSERT INTO item_categories (id, code, name) VALUES (gen_random_uuid(), 'NOPREFIX', '頂層缺前綴');", "ck_item_categories_prefix_required")]
    [InlineData("INSERT INTO item_categories (id, parent_id, code, name, item_code_prefix) VALUES (gen_random_uuid(), '0b1f6a10-0002-4c3a-9a00-000000000002', 'EL-SUB', '子分類', 'x');", "ck_item_categories_prefix_format")]
    [InlineData("INSERT INTO item_categories (id, parent_id, code, name) VALUES ('11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'SELF', '指向自己');", "ck_item_categories_not_self_parent")]
    public async Task 不合格的單位或分類_被資料庫拒絕(string sql, string constraint)
    {
        await AssertRejectedAsync(sql, PostgresErrorCodes.CheckViolation, constraint);
    }

    // T-M1-01-10：外鍵 RESTRICT（23503）
    [Fact]
    public async Task 仍有物料的分類與單位_不可刪除()
    {
        await AssertRejectedAsync(
            InsertItem("MR-900040", null, "PendingApproval") + $"DELETE FROM item_categories WHERE id = '{El}';",
            PostgresErrorCodes.ForeignKeyViolation, "fk_items_item_categories_category_id");
        await AssertRejectedAsync(
            InsertItem("MR-900041", null, "PendingApproval") + $"DELETE FROM units WHERE id = '{Pcs}';",
            PostgresErrorCodes.ForeignKeyViolation, "fk_items_units_base_unit_id");
    }

    // T-M1-01-11：料號發放後不得變更、清除、刪除（trigger，23001）
    [Theory]
    [InlineData("UPDATE items SET item_code = 'EL-900051' WHERE request_no = 'MR-900050';")]
    [InlineData("UPDATE items SET item_code = NULL, status = 'Rejected' WHERE request_no = 'MR-900050';")]
    [InlineData("DELETE FROM items WHERE request_no = 'MR-900050';")]
    public async Task 已發放的料號_不可變更清除或刪除(string sql)
    {
        var error = await AssertRejectedAsync(
            InsertItem("MR-900050", "EL-900050", "Active") + sql, PostgresErrorCodes.RestrictViolation, constraint: null);
        Assert.Contains("EL-900050", error.MessageText);
    }

    [Fact]
    public async Task 尚無料號的物料_可修改也可刪除()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlRawAsync(InsertItem("MR-900060", null, "PendingApproval"), ct);
        var updated = await db.Database.ExecuteSqlRawAsync("UPDATE items SET name = '改名' WHERE request_no = 'MR-900060';", ct);
        var deleted = await db.Database.ExecuteSqlRawAsync("DELETE FROM items WHERE request_no = 'MR-900060';", ct);

        Assert.Equal(1, updated);
        Assert.Equal(1, deleted);
    }

    // T-M1-01-12：樂觀併發（xmin）
    [Fact]
    public async Task 兩人同時修改同一物料_後存者收到併發衝突()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var setup = MigratedDatabaseFixture.CreateContext())
        {
            setup.Items.Add(Item.Submit(id, "MR-900070", "耗材", El, Pcs, TrackingMode.None, null, null, null, now));
            await setup.SaveChangesAsync(ct);
        }

        try
        {
            await using var first = MigratedDatabaseFixture.CreateContext();
            await using var second = MigratedDatabaseFixture.CreateContext();
            var a = await first.Items.SingleAsync(i => i.Id == id, ct);
            var b = await second.Items.SingleAsync(i => i.Id == id, ct);

            a.SetNegativeStockPolicy(true, 10, now);
            await first.SaveChangesAsync(ct);
            b.SetNegativeStockPolicy(true, 20, now);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));
        }
        finally
        {
            // 這個測試需要兩個連線各自提交，無法用交易回滾，結束時自行清除（沒有料號，trigger 允許刪除）
            await using var cleanup = MigratedDatabaseFixture.CreateContext();
            await cleanup.Items.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
        }
    }

    // T-M1-01-13
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
        Assert.Equal(1, await db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname = 'trg_items_protect_item_code'")
            .SingleAsync(ct));
    }

    /// <summary>直接以 SQL 插入一筆物料（繞過 Domain，專門測資料庫約束）。值都來自測試常數，不是使用者輸入。</summary>
    private static string InsertItem(
        string requestNo, string? itemCode, string status, string trackingMode = "None",
        bool allowNegativeStock = false, int negativeStockLimit = 0, string name = "測試") =>
        "INSERT INTO items (id, request_no, item_code, status, name, category_id, base_unit_id, tracking_mode, " +
        "allow_negative_stock, negative_stock_limit, created_at, updated_at) VALUES (gen_random_uuid(), " +
        $"'{requestNo}', {(itemCode is null ? "NULL" : $"'{itemCode}'")}, '{status}', '{name}', '{El}', '{Pcs}', " +
        $"'{trackingMode}', {(allowNegativeStock ? "true" : "false")}, {negativeStockLimit}, now(), now());";

    /// <summary>
    /// 在交易內執行 SQL（離開時回滾），斷言被資料庫以指定錯誤代碼與約束名稱拒絕。
    /// constraint 為 null 時表示由 trigger 拒絕（沒有約束名稱）。
    /// </summary>
    private static async Task<PostgresException> AssertRejectedAsync(string sql, string sqlState, string? constraint)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, ct));
        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        return error;
    }

    private static async Task<int> CountTablesAsync(WarehouseDbContext db, CancellationToken ct) =>
        await db.Database
            .SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name IN ('units', 'item_categories', 'items')")
            .SingleAsync(ct);
}
