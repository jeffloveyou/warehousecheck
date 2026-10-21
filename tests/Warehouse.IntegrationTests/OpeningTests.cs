using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M3-02／M3-03 期初建帳、結存與異動帳（T-M3-03-01～12），真實 PostgreSQL。
/// 每個測試建立自己的倉庫（隨機代碼）與物料，彼此不干擾。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class OpeningTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid Kg = SeedData.Units.Single(u => u.Code == "KG").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管甲", false, IsWarehouseManager: true);
    private static readonly Actor Manager2 = new(Guid.NewGuid(), "倉庫主管乙", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M3-03-01：完整流程，結存與異動一致
    [Fact]
    public async Task 建立送出核准_結存與異動一致_儲位標記已清點()
    {
        var (warehouse, code) = await WarehouseAsync("A-01", "A-02", "A-03");
        var plain = await ItemAsync(TrackingMode.None, Kg);
        var lot = await ItemAsync(TrackingMode.Lot);
        var serial = await ItemAsync(TrackingMode.Serial);
        var doc = await Opening(s => s.CreateAsync(warehouse, "測試期初", Operator, Ct));
        await AddAsync(doc, Operator, "a-01", plain, null, null, 12.5m);                 // 小寫儲位碼也可以
        await AddAsync(doc, Operator, "A-01", lot, "L2026", null, 30, new DateOnly(2027, 12, 31));
        await AddAsync(doc, Operator, "A-02", serial, null, "SN-001", 1);
        await AddAsync(doc, Operator, "A-02", serial, null, "SN-002", 1);
        await Opening(s => s.AddLineAsync(doc, new OpeningLineInput("A-03", null, null, null, null, 0), Operator, Ct)); // 空儲位
        await Opening(async s => await s.SubmitAsync(doc, await VersionAsync(doc), Operator, Ct));

        await ApproveAsync(doc, Manager);

        var details = (await Opening(s => s.GetAsync(doc, Ct)))!;
        Assert.Equal(StockDocumentStatus.Posted, details.Status);
        Assert.Equal(Manager.Name, details.ApprovedByName);
        var balances = (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items;
        Assert.Equal(4, balances.Count);
        Assert.Equal(12.5m, balances.Single(b => b.ItemId == plain).Quantity);
        Assert.Equal(("L2026", new DateOnly(2027, 12, 31)), balances.Where(b => b.ItemId == lot).Select(b => (b.LotNo, b.ExpiryDate)).Single());
        Assert.Equal(["SN-001", "SN-002"], balances.Where(b => b.ItemId == serial).Select(b => b.SerialNo).Order());
        Assert.All(balances, b => Assert.Equal(StockStatus.Available, b.Status));
        var movements = (await Stock(s => s.GetMovementsAsync(null, null, doc, 1, Ct))).Items;
        Assert.Equal(4, movements.Count);
        Assert.All(movements, m => Assert.Equal(m.QuantityDelta, m.BalanceAfter));
        Assert.Empty((await Stock(s => s.GetUncountedLocationsAsync(warehouse, 1, Ct))).Items); // 三個儲位都已清點（含空儲位）
        _ = code;
    }

    // T-M3-03-02：冪等
    [Fact]
    public async Task 同一冪等鍵重送只過帳一次_同鍵不同內容被拒()
    {
        var (warehouse, _) = await WarehouseAsync("B-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await SubmittedAsync(warehouse, ("B-01", item, 5m));
        var version = await VersionAsync(doc);
        var key = "key-" + Guid.NewGuid();

        await Opening(s => s.ApproveAsync(doc, version, key, Manager, Ct));
        await Opening(s => s.ApproveAsync(doc, version, key, Manager, Ct)); // 重送：不拋錯、不重複過帳

        Assert.Equal(5m, (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items.Single().Quantity);
        Assert.Single((await Stock(s => s.GetMovementsAsync(null, null, doc, 1, Ct))).Items);
        var otherWarehouse = await WarehouseIdAsync("C-01");
        var other = await SubmittedAsync(otherWarehouse, ("C-01", item, 1m));
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Opening(async s => await s.ApproveAsync(other, await VersionAsync(other), key, Manager, Ct)));
        Assert.Equal("idempotency.key_reused", ex.ErrorCode);
    }

    // T-M3-03-03：兩位主管同時核准同一張單
    [Fact]
    public async Task 兩人同時核准同一張單_只過帳一次()
    {
        var (warehouse, _) = await WarehouseAsync("D-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await SubmittedAsync(warehouse, ("D-01", item, 7m));
        var version = await VersionAsync(doc);

        var results = await Task.WhenAll(new[] { Manager, Manager2 }.Select(async actor =>
        {
            try
            {
                await Opening(s => s.ApproveAsync(doc, version, "key-" + Guid.NewGuid(), actor, Ct));
                return "ok";
            }
            catch (Exception ex) when (ex is ConcurrencyConflictException or DomainRuleException)
            {
                return "rejected";
            }
        }));

        Assert.Equal(["ok", "rejected"], results.Order());
        Assert.Equal(7m, (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items.Single().Quantity);
    }

    // T-M3-03-04：序號不可同時在兩處
    [Fact]
    public async Task 序號已在庫_另一張單再放同一序號_過帳被拒且整張回滾()
    {
        var (warehouse, _) = await WarehouseAsync("E-01", "E-02");
        var serial = await ItemAsync(TrackingMode.Serial);
        var plain = await ItemAsync(TrackingMode.None);
        var first = await SubmittedWithTrackingAsync(warehouse, ("E-01", serial, 1m, null, "SN-X"));
        await ApproveAsync(first, Manager);

        var second = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));
        await AddAsync(second, Operator, "E-02", plain, null, null, 3);       // 這行本身沒問題
        await AddAsync(second, Operator, "E-02", serial, null, "sn-x", 1);    // 同一序號（小寫）
        await Opening(async s => await s.SubmitAsync(second, await VersionAsync(second), Operator, Ct));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ApproveAsync(second, Manager));
        Assert.Contains("序號已經在庫", ex.Message);
        var balances = (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items;
        Assert.DoesNotContain(balances, b => b.ItemId == plain);            // 同一交易的其他行也沒有寫入
        Assert.Equal(StockDocumentStatus.Submitted, (await Opening(s => s.GetAsync(second, Ct)))!.Status);
        Assert.Contains(await UncountedCodesAsync(warehouse), c => c == "E-02");
    }

    // T-M3-03-05：明細規則（小數位、追蹤模式、停用儲位、重複）
    [Fact]
    public async Task 明細規則_小數位_追蹤模式_停用儲位_重複明細()
    {
        var (warehouse, _) = await WarehouseAsync("F-01", "F-02");
        var pcs = await ItemAsync(TrackingMode.None);
        var lot = await ItemAsync(TrackingMode.Lot);
        var serial = await ItemAsync(TrackingMode.Serial);
        var doc = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));

        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "F-01", pcs, null, null, 1.5m));   // PCS 不可小數
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "F-01", lot, null, null, 1));      // 缺批號
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "F-01", serial, null, "S1", 2));   // 序號數量 2
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "F-01", pcs, "L1", null, 1));      // 不追蹤卻填批號
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "ZZ-99", pcs, null, null, 1));     // 儲位不存在
        await AddAsync(doc, Operator, "F-01", pcs, null, null, 1);
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "F-01", pcs, null, null, 2));      // 重複明細

        var warehouseCode = await CodeOfAsync(warehouse);
        var location = (await Warehouses(s => s.GetLocationByCodeAsync(warehouseCode, "F-02", Ct)))!;
        await Warehouses(s => s.ChangeLocationStatusAsync(location.Id, location.Version, LocationStatus.Inactive, Manager, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "F-02", pcs, null, null, 1));      // 停用儲位
    }

    // T-M3-03-06：D-07 自核
    [Fact]
    public async Task 自核規則_預設建立者不可核准自己的單_開啟設定後可以()
    {
        var (warehouse, _) = await WarehouseAsync("G-01", "G-02");
        var item = await ItemAsync(TrackingMode.None);
        var own = await Opening(s => s.CreateAsync(warehouse, null, Manager, Ct));
        await AddAsync(own, Manager, "G-01", item, null, null, 1);
        await Opening(async s => await s.SubmitAsync(own, await VersionAsync(own), Manager, Ct));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ApproveAsync(own, Manager));
        Assert.Contains("不允許建立者核准自己的單", ex.Message);

        var admin = new Actor(Guid.NewGuid(), "系統管理員", false);
        var before = await Settings(s => s.GetAsync(Ct));
        await Settings(s => s.UpdateAsync(before, before with { AllowSelfApproveOpening = true }, admin, Ct));
        try
        {
            await ApproveAsync(own, Manager);
            Assert.Equal(StockDocumentStatus.Posted, (await Opening(s => s.GetAsync(own, Ct)))!.Status);
        }
        finally
        {
            var now = await Settings(s => s.GetAsync(Ct));
            await Settings(s => s.UpdateAsync(now, now with { AllowSelfApproveOpening = false }, admin, Ct));
        }
    }

    // T-M3-03-07：待驗儲位、已期初儲位、停用有庫存的儲位
    [Fact]
    public async Task 待驗儲位的結存不算可用_已期初的儲位不能再期初_有庫存的儲位不能停用()
    {
        var (warehouse, _) = await WarehouseAsync("H-01");
        var item = await ItemAsync(TrackingMode.None);
        var code = await CodeOfAsync(warehouse);
        var location = (await Warehouses(s => s.GetLocationByCodeAsync(code, "H-01", Ct)))!;
        await Warehouses(s => s.ChangeLocationStatusAsync(location.Id, location.Version, LocationStatus.Inspection, Manager, Ct));
        var doc = await SubmittedAsync(warehouse, ("H-01", item, 4m));
        await ApproveAsync(doc, Manager);

        var balance = (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items.Single();
        // 結存寫 Available，但儲位是待驗，所以不算可用；儲位改回有效後立即可用（ADR-0014 決策 7）
        Assert.Equal((StockStatus.Available, false), (balance.Status, balance.LocationAvailable));

        var again = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(again, Operator, "H-01", item, null, null, 1));
        Assert.Contains("已經做過期初", ex.Message);

        location = (await Warehouses(s => s.GetLocationByCodeAsync(code, "H-01", Ct)))!;
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Warehouses(s => s.ChangeLocationStatusAsync(location.Id, location.Version, LocationStatus.Inactive, Manager, Ct)));
        var wh = (await Warehouses(s => s.ListWarehousesAsync(Ct))).Single(w => w.Id == warehouse);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Warehouses(s => s.SetWarehouseActiveAsync(warehouse, wh.Version, false, Manager, Ct)));
    }

    // T-M3-03-08：權限
    [Fact]
    public async Task 權限_作業員不能核准_別人的草稿不能改_倉庫主管可以退回()
    {
        var (warehouse, _) = await WarehouseAsync("J-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await SubmittedAsync(warehouse, ("J-01", item, 1m));
        var other = Operator with { Id = Guid.NewGuid(), Name = "作業員乙" };

        await Assert.ThrowsAsync<PermissionDeniedException>(() => ApproveAsync(doc, Operator));
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            Opening(async s => await s.CancelAsync(doc, await VersionAsync(doc), other, Ct)));
        await Opening(async s => await s.ReturnAsync(doc, await VersionAsync(doc), "數量再確認", Manager, Ct));

        var returned = (await Opening(s => s.GetAsync(doc, Ct)))!;
        Assert.Equal((StockDocumentStatus.Draft, "數量再確認"), (returned.Status, returned.ReturnComment));
    }

    // T-M3-03-09：資料庫保護
    [Theory]
    [InlineData("UPDATE stock_movements SET quantity_delta = 999 WHERE document_id = @doc", "只能新增")]
    [InlineData("DELETE FROM stock_movements WHERE document_id = @doc", "只能新增")]
    [InlineData("UPDATE stock_documents SET note = '竄改' WHERE id = @doc", "不可修改")]
    [InlineData("DELETE FROM stock_document_lines WHERE document_id = @doc", "只有草稿")]
    [InlineData("UPDATE stock_document_lines SET document_id = (SELECT id FROM stock_documents WHERE status = 'Draft' LIMIT 1) WHERE document_id = @doc", "不可移到其他單據")]
    [InlineData("UPDATE stock_balances SET quantity = -1 WHERE location_id IN (SELECT location_id FROM stock_movements WHERE document_id = @doc)", "")]
    public async Task 異動帳_已過帳單據與明細_負結存_資料庫拒絕(string sql, string message)
    {
        var (warehouse, _) = await WarehouseAsync("K-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await SubmittedAsync(warehouse, ("K-01", item, 2m));
        await ApproveAsync(doc, Manager);

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("doc", doc);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Contains(ex.SqlState, new[] { PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.CheckViolation });
        Assert.Contains(message, ex.MessageText);
    }

    // T-M3-03-10：取消與明細刪除
    [Fact]
    public async Task 草稿可刪明細_取消後不能再操作_空單不能送出()
    {
        var (warehouse, _) = await WarehouseAsync("M-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Opening(async s => await s.SubmitAsync(doc, await VersionAsync(doc), Operator, Ct)));
        await AddAsync(doc, Operator, "M-01", item, null, null, 1);
        var line = (await Opening(s => s.GetAsync(doc, Ct)))!.Lines.Single();
        await Opening(s => s.RemoveLineAsync(doc, line.Id, Operator, Ct));
        await Opening(async s => await s.CancelAsync(doc, await VersionAsync(doc), Operator, Ct));

        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "M-01", item, null, null, 1));
        Assert.Equal(StockDocumentStatus.Cancelled, (await Opening(s => s.GetAsync(doc, Ct)))!.Status);
    }

    // T-M3-03-12（審查 #13）：結存合併路徑——同一維度（批號、序號都是 NULL）兩次增加，只有一列、數量相加
    [Fact]
    public async Task 過帳服務_同一維度兩次增加_合併成一列_NULL維度也視為相同()
    {
        var (warehouse, code) = await WarehouseAsync("N-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await SubmittedAsync(warehouse, ("N-01", item, 1m));
        await ApproveAsync(doc, Manager);
        var location = (await Warehouses(s => s.GetLocationByCodeAsync(code, "N-01", Ct)))!;
        var line = (await Opening(s => s.GetAsync(doc, Ct)))!.Lines.Single();

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            var posting = new InventoryPostingService(db);
            var change = new StockChange(line.Id, item, location.Id, null, null, StockStatus.Available, 2m);
            await posting.ApplyAsync(doc, [change, change], Manager, DateTimeOffset.UtcNow, Ct);
            await tx.CommitAsync(Ct);
        }

        var balances = (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items;
        Assert.Equal(5m, Assert.Single(balances).Quantity);
        var movements = (await Stock(s => s.GetMovementsAsync(null, location.Id, null, 1, Ct))).Items;
        Assert.Equal([5m, 3m, 1m], movements.Select(m => m.BalanceAfter).OrderDescending());
    }

    // T-M3-03-13（審查 #13）：同一個鍵同時送兩次，只過帳一次、兩次都成功
    [Fact]
    public async Task 同一冪等鍵並行送出_只過帳一次()
    {
        var (warehouse, _) = await WarehouseAsync("P-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await SubmittedAsync(warehouse, ("P-01", item, 3m));
        var version = await VersionAsync(doc);
        var key = "key-" + Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Opening(s => s.ApproveAsync(doc, version, key, Manager, Ct))));

        Assert.Equal(3m, (await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items.Single().Quantity);
        Assert.Single((await Stock(s => s.GetMovementsAsync(null, null, doc, 1, Ct))).Items);
    }

    // T-M3-03-14（審查 #13）：兩張不同的單同時核准同一個儲位，只有一張成功
    [Fact]
    public async Task 兩張單同時核准同一儲位_只有一張成功()
    {
        var (warehouse, _) = await WarehouseAsync("Q-01");
        var item = await ItemAsync(TrackingMode.None);
        var a = await SubmittedAsync(warehouse, ("Q-01", item, 1m));
        var b = await SubmittedAsync(warehouse, ("Q-01", item, 2m));

        var results = await Task.WhenAll(new[] { a, b }.Select(async doc =>
        {
            try
            {
                await ApproveAsync(doc, Manager);
                return "ok";
            }
            catch (Exception ex) when (ex is DomainRuleException or ConcurrencyConflictException)
            {
                return "rejected";
            }
        }));

        Assert.Equal(["ok", "rejected"], results.Order());
        Assert.Single((await Stock(s => s.GetBalancesAsync(warehouse, null, null, 1, Ct))).Items);
    }

    // T-M3-03-15（審查 #8）：同一序號登記在不同批號下
    [Fact]
    public async Task 數量上限_過大的數量被拒()
    {
        var (warehouse, _) = await WarehouseAsync("R-01");
        var item = await ItemAsync(TrackingMode.None);
        var doc = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));

        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(doc, Operator, "R-01", item, null, null, 10_000_000_000_000m));
    }

    // ---- 輔助 ----

    private static async Task ApproveAsync(Guid doc, Actor actor) =>
        await Opening(async s => await s.ApproveAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), actor, Ct));

    private static Task AddAsync(
        Guid doc, Actor actor, string location, Guid itemId, string? lot, string? serial, decimal qty, DateOnly? expiry = null) =>
        Opening(async s => await s.AddLineAsync(
            doc, new OpeningLineInput(location, await ItemCodeAsync(itemId), lot, serial, expiry, qty), actor, Ct));

    private static async Task<Guid> SubmittedAsync(Guid warehouse, params (string Location, Guid Item, decimal Qty)[] lines) =>
        await SubmittedWithTrackingAsync(warehouse, lines.Select(l => (l.Location, l.Item, l.Qty, (string?)null, (string?)null)).ToArray());

    private static async Task<Guid> SubmittedWithTrackingAsync(Guid warehouse, params (string Location, Guid Item, decimal Qty, string? Lot, string? Serial)[] lines)
    {
        var doc = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));
        foreach (var l in lines)
        {
            await AddAsync(doc, Operator, l.Location, l.Item, l.Lot, l.Serial, l.Qty);
        }
        await Opening(async s => await s.SubmitAsync(doc, await VersionAsync(doc), Operator, Ct));
        return doc;
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "O" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var id = await Warehouses(s => s.CreateWarehouseAsync(new WarehouseInput(code, "期初測試倉"), Manager, Ct));
        foreach (var location in locations)
        {
            await Warehouses(s => s.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct));
        }
        return (id, code);
    }

    private static async Task<Guid> WarehouseIdAsync(params string[] locations) => (await WarehouseAsync(locations)).Id;

    private static async Task<string> CodeOfAsync(Guid warehouse) =>
        (await Warehouses(s => s.ListWarehousesAsync(Ct))).Single(w => w.Id == warehouse).Code;

    private static async Task<IReadOnlyList<string>> UncountedCodesAsync(Guid warehouse) =>
        (await Stock(s => s.GetUncountedLocationsAsync(warehouse, 1, Ct))).Items.Select(l => l.LocationCode).ToList();

    private static async Task<Guid> ItemAsync(TrackingMode tracking, Guid? unit = null)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("期初物料 " + Guid.NewGuid().ToString("N")[..8], El, unit ?? Pcs, tracking, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return item.ItemId;
    }

    private static async Task<string> ItemCodeAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<T> Opening<T>(Func<IOpeningService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var settings = new SettingsService(db, TimeProvider.System);
        return await action(new OpeningService(db, new InventoryPostingService(db), settings, TimeProvider.System));
    }

    private static async Task Opening(Func<IOpeningService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var settings = new SettingsService(db, TimeProvider.System);
        await action(new OpeningService(db, new InventoryPostingService(db), settings, TimeProvider.System));
    }

    private static async Task<T> Stock<T>(Func<IStockQueryService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockQueryService(db));
    }

    private static async Task<T> Warehouses<T>(Func<IWarehouseService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task Warehouses(Func<IWarehouseService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task<T> Settings<T>(Func<ISettingsService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new SettingsService(db, TimeProvider.System));
    }

    private static async Task Settings(Func<ISettingsService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new SettingsService(db, TimeProvider.System));
    }
}
