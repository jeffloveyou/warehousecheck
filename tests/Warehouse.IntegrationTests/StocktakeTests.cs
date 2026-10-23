using Microsoft.EntityFrameworkCore;
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
/// M6-02 盤點（T-M6-02-01～）：不凍結儲位、差異＝實盤 − 計數當下帳面、核准產生調整單，真實 PostgreSQL。
/// 盲盤與自核是全域設定：會改設定的測試在 finally 改回預設。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class StocktakeTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "盤點作業員", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "盤點主管甲", false, IsWarehouseManager: true);
    private static readonly Actor Manager2 = new(Guid.NewGuid(), "盤點主管乙", false, IsWarehouseManager: true);
    private static readonly Actor Manager3 = new(Guid.NewGuid(), "盤點主管丙", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "盤點物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-02-01：快照後出庫 3 再計數 → 差異不含那 3；核准後結存＝實盤
    [Fact]
    public async Task 快照後有出庫再計數_差異只算計數當下()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 10m);
        var st = await StartAsync(w);
        await MoveAsync(StockDocumentType.Outbound, w, "A-01", item, 3m);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 6m), Operator, Ct));

        var line = (await DetailsAsync(st, Manager)).Lines.Items.Single();
        Assert.Equal((10m, 7m, -1m, -3m), (line.SnapshotQuantity, line.BookQuantityAtCount, line.Difference, line.MovementSinceSnapshot));
        await SubmitAndApproveAsync(st);
        Assert.Equal(6m, await BalanceAsync(w, "A-01", item));
        var details = await DetailsAsync(st, Manager);
        Assert.Equal(StocktakeStatus.Approved, details.Status);
        Assert.StartsWith("ADJ-", details.AdjustmentDocNo);
    }

    // T-M6-02-02：計數後、核准前又出庫 → 核准用相對差異，結存＝實盤 − 期間出庫（核准不會覆蓋之後的異動）
    [Fact]
    public async Task 計數後核准前出庫_核准以差異調整()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 10m);
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 8m), Operator, Ct));
        await MoveAsync(StockDocumentType.Outbound, w, "A-01", item, 3m);
        await SubmitAndApproveAsync(st);
        Assert.Equal(5m, await BalanceAsync(w, "A-01", item));
    }

    // T-M6-02-03：同一冪等鍵重送核准只調整一次；另一位主管同時用不同鍵核准只成功一個
    [Fact]
    public async Task 核准冪等與併發()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 10m);
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 7m), Operator, Ct));
        await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct));
        var version = await VersionAsync(st);
        var key = "st-" + Guid.NewGuid();
        var first = await Stocktakes(s => s.ApproveAsync(st, version, key, Manager2, Ct));
        var again = await Stocktakes(s => s.ApproveAsync(st, version, key, Manager2, Ct));
        Assert.Equal(first, again);
        Assert.Equal(7m, await BalanceAsync(w, "A-01", item));
        Assert.NotNull(await TryAsync(() => Stocktakes(s => s.ApproveAsync(st, version, "st-" + Guid.NewGuid(), Manager3, Ct))));

        // 兩位主管同時核准另一張
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 3m);
        var st2 = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st2, new StocktakeCountInput("A-01", item, 4m), Operator, Ct));
        await Stocktakes(async s => await s.SubmitAsync(st2, await VersionAsync(st2), Operator, Ct));
        var v2 = await VersionAsync(st2);
        var results = await Task.WhenAll(
            TryAsync(() => Stocktakes(s => s.ApproveAsync(st2, v2, "a-" + Guid.NewGuid(), Manager3, Ct))),
            TryAsync(() => Stocktakes(s => s.ApproveAsync(st2, v2, "b-" + Guid.NewGuid(), Manager2, Ct))));
        Assert.Single(results, r => r is null);
        Assert.Equal(4m, await BalanceAsync(w, "A-01", item));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.StockDocuments.CountAsync(d => d.DocType == StockDocumentType.Adjustment && d.Note == "盤點 " + db.Stocktakes.Where(s => s.Id == st2).Select(s => s.StocktakeNo).First(), Ct));
    }

    // T-M6-02-04：盤虧低於預留 → 核准成功（不受預留限制），出現在「預留大於庫存」
    [Fact]
    public async Task 盤虧碰預留_允許調整並列入異常()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 10m);
        await Reservations(s => s.CreateAsync(new ReservationInput(item, w, 9m, null, null), "r-" + Guid.NewGuid(), Operator, Ct));
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 5m), Operator, Ct));
        await SubmitAndApproveAsync(st);
        Assert.Equal(5m, await BalanceAsync(w, "A-01", item));

        await using var db = MigratedDatabaseFixture.CreateContext();
        var anomalies = new StockAnomalyService(db, TimeProvider.System);
        var found = false;
        for (var page = 1; !found; page++)
        {
            var result = await anomalies.GetOverReservedAsync(page, Ct);
            found = result.Items.Any(r => r.ItemCode == item && r.Reserved == 9m && r.Balance == 5m);
            if (page >= result.TotalPages)
            {
                break;
            }
        }
        Assert.True(found);
    }

    // T-M6-02-05：計數後被出庫，調整會讓非負庫存物料變負 → 核准失敗、結存不變、盤點仍待核准
    [Fact]
    public async Task 調整不可讓一般物料變負()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 10m);
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 2m), Operator, Ct));
        await MoveAsync(StockDocumentType.Outbound, w, "A-01", item, 5m);
        await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct));
        Assert.Equal("stock.insufficient", await TryAsync(async () =>
            await Stocktakes(async s => await s.ApproveAsync(st, await VersionAsync(st), "k-" + Guid.NewGuid(), Manager2, Ct))));
        Assert.Equal(5m, await BalanceAsync(w, "A-01", item));
        Assert.Equal(StocktakeStatus.Submitted, (await DetailsAsync(st, Manager)).Status);
    }

    // T-M6-02-06：盲盤（預設）：盤點中作業員看不到帳面量，主管看得到；送審後公開
    [Fact]
    public async Task 盲盤_作業員看不到帳面量()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 4m);
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 4m), Operator, Ct));
        var hidden = await DetailsAsync(st, Operator);
        Assert.True(hidden.QuantitiesHidden);
        var line = hidden.Lines.Items.Single();
        Assert.Null(line.SnapshotQuantity);
        Assert.Null(line.BookQuantityAtCount);
        Assert.Null(line.Difference);
        Assert.Equal(4m, line.CountedQuantity);
        Assert.Equal(4m, (await DetailsAsync(st, Manager)).Lines.Items.Single().SnapshotQuantity);

        await SetAsync(s => s with { StocktakeBlindCount = false });
        try
        {
            Assert.False((await DetailsAsync(st, Operator)).QuantitiesHidden);
        }
        finally
        {
            await SetAsync(s => s with { StocktakeBlindCount = true });
        }
    }

    // T-M6-02-07：現場找到新批號 → 核准建立批號與結存；序號在別的儲位在庫 → 核准失敗
    [Fact]
    public async Task 現場找到新批號與他處在庫序號()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var lotItem = await ItemCodeAsync(TrackingMode.Lot);
        var serialItem = await ItemCodeAsync(TrackingMode.Serial);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", serialItem, 1m, serial: "SN-1");
        var st = await StartAsync(w, "A-02");
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-02", lotItem, 4m, LotNo: "l-new"), Operator, Ct));
        await SubmitAndApproveAsync(st);
        Assert.Equal(4m, await BalanceAsync(w, "A-02", lotItem));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.True(await db.InventoryLots.AnyAsync(l => l.LotNo == "L-NEW" && db.Items.Any(i => i.Id == l.ItemId && i.ItemCode == lotItem), Ct));
        }

        var st2 = await StartAsync(w, "A-02");
        await Stocktakes(s => s.CountAsync(st2, new StocktakeCountInput("A-02", lotItem, 4m, LotNo: "L-NEW"), Operator, Ct));
        await Stocktakes(s => s.CountAsync(st2, new StocktakeCountInput("A-02", serialItem, 1m, SerialNo: "SN-1"), Operator, Ct));
        await Stocktakes(async s => await s.SubmitAsync(st2, await VersionAsync(st2), Operator, Ct));
        Assert.Equal("domain_rule", await TryAsync(async () =>
            await Stocktakes(async s => await s.ApproveAsync(st2, await VersionAsync(st2), "k-" + Guid.NewGuid(), Manager2, Ct))));
        Assert.Equal(1m, await BalanceAsync(w, "A-01", serialItem));
        Assert.Equal(0m, await BalanceAsync(w, "A-02", serialItem));
    }

    // T-M6-02-08：自核預設禁止；同一儲位不能同時在兩張盤點；未全部計數不能送審；重盤後要重新輸入；取消釋放儲位
    [Fact]
    public async Task 流程規則()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 3m);
        await MoveAsync(StockDocumentType.Inbound, w, "A-02", item, 3m);
        var st = await StartAsync(w);
        Assert.Equal("stocktake.location_busy", await TryAsync(() => StartAsync(w, "A-01")));
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 3m), Operator, Ct));
        Assert.NotNull(await TryAsync(async () => await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct))));
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-02", item, 2m), Manager, Ct));
        await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Manager, Ct));
        // Manager 建立也送審：預設不能自核
        Assert.Equal("domain_rule", await TryAsync(async () =>
            await Stocktakes(async s => await s.ApproveAsync(st, await VersionAsync(st), "k-" + Guid.NewGuid(), Manager, Ct))));

        var lineA02 = (await DetailsAsync(st, Manager2)).Lines.Items.Single(l => l.LocationCode == "A-02").Id;
        await Stocktakes(async s => await s.RequestRecountAsync(st, await VersionAsync(st), [lineA02], "請重點", Manager2, Ct));
        Assert.Equal(1, (await DetailsAsync(st, Manager2)).UncountedCount);
        Assert.NotNull(await TryAsync(async () => await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct))));
        await Stocktakes(s => s.CountLineAsync(st, lineA02, 3m, Operator, Ct));
        await Stocktakes(async s => await s.CancelAsync(st, await VersionAsync(st), Manager2, Ct));
        Assert.Equal((3m, 3m), (await BalanceAsync(w, "A-01", item), await BalanceAsync(w, "A-02", item)));
        // 取消後儲位釋放，可以再開
        Assert.NotEqual(Guid.Empty, await StartAsync(w, "A-01"));
    }

    // T-M6-02-09：作業員不能開始或核准；計數不能在範圍外的儲位；調整單不能沖銷
    [Fact]
    public async Task 權限與錯誤操作()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 5m);
        Assert.Equal("stocktake.permission_denied", await TryAsync(() => Stocktakes(s => s.StartAsync(w, [], null, Operator, Ct))));
        var st = await StartAsync(w, "A-01");
        Assert.NotNull(await TryAsync(() => Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-02", item, 1m), Operator, Ct))));
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 4m), Operator, Ct));
        await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct));
        Assert.Equal("stocktake.permission_denied", await TryAsync(async () =>
            await Stocktakes(async s => await s.ApproveAsync(st, await VersionAsync(st), "k-" + Guid.NewGuid(), Operator, Ct))));
        await Stocktakes(async s => await s.ApproveAsync(st, await VersionAsync(st), "k-" + Guid.NewGuid(), Manager2, Ct));
        var adjustment = (await DetailsAsync(st, Manager)).AdjustmentDocumentId!.Value;
        await using var db = MigratedDatabaseFixture.CreateContext();
        var docs = new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);
        await Assert.ThrowsAsync<DomainRuleException>(() => docs.CreateReversalAsync(adjustment, Manager, Ct));
        var doc = (await docs.GetAsync(adjustment, Ct))!;
        Assert.Equal(-1m, doc.Lines.Single().SignedQuantity);
        Assert.Equal(StockDocumentType.Adjustment, doc.DocType);
    }

    // T-M6-02-10：現場找到新批號 → 之後進倉建立了同一批號 → 重盤時帳面量算到那筆結存（審查 2）
    [Fact]
    public async Task 現場新批號_之後進倉_重盤用新帳面()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var lotItem = await ItemCodeAsync(TrackingMode.Lot);
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", lotItem, 5m, LotNo: "L9"), Operator, Ct));
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", lotItem, 5m, lot: "L9");
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", lotItem, 5m, LotNo: "L9"), Operator, Ct));
        var line = (await DetailsAsync(st, Manager)).Lines.Items.Single();
        Assert.Equal((5m, 0m), (line.BookQuantityAtCount!.Value, line.Difference!.Value));
        await SubmitAndApproveAsync(st);
        Assert.Equal(5m, await BalanceAsync(w, "A-01", lotItem));
    }

    // T-M6-02-11：有批號的序號物料只掃序號 → 對到原本那一行，不另開；批號填錯被拒（審查 3）
    [Fact]
    public async Task 序號物料只掃序號_對到原行()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var serialItem = await ItemCodeAsync(TrackingMode.Serial);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", serialItem, 1m, serial: "SN-9", lot: "LX");
        var st = await StartAsync(w);
        Assert.Equal("domain_rule", await TryAsync(() =>
            Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", serialItem, 1m, LotNo: "LY", SerialNo: "SN-9"), Operator, Ct))));
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", serialItem, 1m, SerialNo: "SN-9"), Operator, Ct));
        var details = await DetailsAsync(st, Manager);
        Assert.Equal(1, details.LineCount);
        Assert.Equal(0m, details.Lines.Items.Single().Difference);
    }

    // T-M6-02-12：同一張盤點內序號從 A-01 移到 A-02（A −1、B +1）可以核准
    [Fact]
    public async Task 盤點內序號移位_可核准()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var serialItem = await ItemCodeAsync(TrackingMode.Serial);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", serialItem, 1m, serial: "SN-5");
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", serialItem, 0m, SerialNo: "SN-5"), Operator, Ct));
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-02", serialItem, 1m, SerialNo: "SN-5"), Operator, Ct));
        await SubmitAndApproveAsync(st);
        Assert.Equal((0m, 1m), (await BalanceAsync(w, "A-01", serialItem), await BalanceAsync(w, "A-02", serialItem)));
    }

    // T-M6-02-13：允許負庫存的物料：盤虧可到負（下限內，不扣預留）
    [Fact]
    public async Task 負庫存物料_盤虧可到下限內()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
            var version = await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
            await new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System)
                .SetAsync(itemId, version, true, 5m, ItemManager, Ct);
        }
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 2m);
        await Reservations(s => s.CreateAsync(new ReservationInput(item, w, 2m, null, null), "r-" + Guid.NewGuid(), Operator, Ct));
        var st = await StartAsync(w);
        await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 0m), Operator, Ct));
        await MoveAsync(StockDocumentType.Outbound, w, "A-01", item, 3m);
        await SubmitAndApproveAsync(st);
        Assert.Equal(-3m, await BalanceAsync(w, "A-01", item));
    }

    // T-M6-02-14：計數與過帳互斥：另一個交易持有儲位 FOR UPDATE 並改結存時，計數會等它 commit，讀到的是新帳面
    [Fact]
    public async Task 計數等待進行中的過帳()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 10m);
        var st = await StartAsync(w);
        await using var conn = new Npgsql.NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var cmd = new Npgsql.NpgsqlCommand("""
            SELECT 1 FROM locations WHERE warehouse_id = @w FOR UPDATE;
            UPDATE stock_balances b SET quantity = 20 FROM locations l, items i
            WHERE l.id = b.location_id AND l.warehouse_id = @w AND i.id = b.item_id AND i.item_code = @item;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("w", w);
            cmd.Parameters.AddWithValue("item", item);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var counting = Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("A-01", item, 20m), Operator, Ct));
        await Task.Delay(700, Ct);
        Assert.False(counting.IsCompleted);
        await tx.CommitAsync(Ct);
        await counting;
        var line = (await DetailsAsync(st, Manager)).Lines.Items.Single();
        Assert.Equal((20m, 0m), (line.BookQuantityAtCount!.Value, line.Difference!.Value));
    }

    // T-M6-04-05：錯誤操作：同一冪等鍵換成另一張盤點 → 拒絕；舊版本號核准 → 衝突；已取消的盤點不能核准
    [Fact]
    public async Task 盤點錯誤操作()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync(TrackingMode.None);
        await MoveAsync(StockDocumentType.Inbound, w, "A-01", item, 2m);
        await MoveAsync(StockDocumentType.Inbound, w, "A-02", item, 2m);
        var first = await StartAsync(w, "A-01");
        var second = await StartAsync(w, "A-02");
        foreach (var st in new[] { first, second })
        {
            await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput(st == first ? "A-01" : "A-02", item, 1m), Operator, Ct));
            await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct));
        }
        var key = "same-" + Guid.NewGuid();
        await Stocktakes(async s => await s.ApproveAsync(first, await VersionAsync(first), key, Manager2, Ct));
        Assert.Equal("idempotency.key_reused", await TryAsync(async () =>
            await Stocktakes(async s => await s.ApproveAsync(second, await VersionAsync(second), key, Manager2, Ct))));
        var stale = await VersionAsync(second);
        await Stocktakes(async s => await s.CancelAsync(second, await VersionAsync(second), Manager2, Ct));
        Assert.Equal("stocktake.concurrency_conflict", await TryAsync(() =>
            Stocktakes(s => s.ApproveAsync(second, stale, "k-" + Guid.NewGuid(), Manager2, Ct))));
        Assert.Equal("domain_rule", await TryAsync(async () =>
            await Stocktakes(async s => await s.ApproveAsync(second, await VersionAsync(second), "k-" + Guid.NewGuid(), Manager2, Ct))));
        Assert.Equal((1m, 2m), (await BalanceAsync(w, "A-01", item), await BalanceAsync(w, "A-02", item)));
    }

    private static async Task SubmitAndApproveAsync(Guid st)
    {
        await Stocktakes(async s => await s.SubmitAsync(st, await VersionAsync(st), Operator, Ct));
        await Stocktakes(async s => await s.ApproveAsync(st, await VersionAsync(st), "k-" + Guid.NewGuid(), Manager2, Ct));
    }

    private static Task<Guid> StartAsync(Guid warehouse, params string[] locations) =>
        Stocktakes(s => s.StartAsync(warehouse, locations, null, Manager, Ct));

    private static async Task<StocktakeDetails> DetailsAsync(Guid st, Actor viewer) =>
        (await Stocktakes(s => s.GetAsync(st, new StocktakeLineFilter(), viewer, Ct)))!;

    private static async Task<uint> VersionAsync(Guid st)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Stocktakes.Where(s => s.Id == st).Select(s => EF.Property<uint>(s, "Version")).SingleAsync(Ct);
    }

    private static async Task MoveAsync(
        StockDocumentType type, Guid warehouse, string location, string item, decimal qty, string? serial = null, string? lot = null)
    {
        var doc = await Docs(s => s.CreateAsync(type, warehouse,
            new StockDocumentHeader(type == StockDocumentType.Inbound ? PurchaseIn : ProductionOut, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput(location, item, qty, LotNo: lot, SerialNo: serial), Operator, Ct));
        await Docs(async s => await s.ConfirmAsync(doc, (await s.GetAsync(doc, Ct))!.Version, "m-" + Guid.NewGuid(), Operator, Ct));
    }

    private static async Task SetAsync(Func<SystemSettings, SystemSettings> change)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new SettingsService(db, TimeProvider.System);
        var current = await service.GetAsync(Ct);
        await service.UpdateAsync(current, change(current), Manager, Ct);
    }

    private static async Task<string?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException ex)
        {
            return ex.ErrorCode;
        }
        catch (DomainRuleException)
        {
            return "domain_rule";
        }
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "T" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "盤點測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return (id, code);
    }

    private static async Task<string> ItemCodeAsync(TrackingMode tracking)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("盤點物料 " + Guid.NewGuid().ToString("N")[..6], El, Pcs, tracking, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<decimal> BalanceAsync(Guid warehouse, string location, string itemCode)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await (from b in db.StockBalances
                      join l in db.Locations on b.LocationId equals l.Id
                      join i in db.Items on b.ItemId equals i.Id
                      where l.WarehouseId == warehouse && l.Code == location && i.ItemCode == itemCode
                      select b.Quantity).SumAsync(Ct);
    }

    private static async Task<T> Stocktakes<T>(Func<IStocktakeService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StocktakeService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Stocktakes(Func<IStocktakeService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StocktakeService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task<T> Docs<T>(Func<IStockDocumentService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Docs(Func<IStockDocumentService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task<T> Reservations<T>(Func<IStockReservationService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }
}
