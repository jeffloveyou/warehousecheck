using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
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
/// M4-01 過帳服務：扣減、可用量、預留、負庫存例外、併發（T-M4-01-01～），真實 PostgreSQL。
/// 期初單提供初始結存，也提供異動帳需要的單據與明細 ID（單據流程在 M4-09 才有）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class PostingTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M4-01-01：AFTER trigger 不誤拒（ON CONFLICT 的預定插入列為負數）
    [Fact]
    public async Task 結存10出3_成功_異動後結存7()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 10));

        await PostAsync(s, (s.Loc["A-01"], item, -3m, null));

        Assert.Equal(7m, await BalanceAsync(item, s.Loc["A-01"]));
        var last = await LastMovementAsync(item);
        Assert.Equal((-3m, 7m, false), (last.QuantityDelta, last.BalanceAfter, last.IsNegative));
    }

    // T-M4-01-02：任何一行不足，整筆回滾（前面成功的行也不留下）
    [Fact]
    public async Task 一行不足_整筆回滾()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 5), ("A-02", 2));
        var before = await MovementCountAsync(item);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            PostAsync(s, (s.Loc["A-01"], item, -1m, null), (s.Loc["A-02"], item, -3m, null)));

        Assert.Equal("stock.insufficient", ex.ErrorCode);
        Assert.Equal((5m, 2m), (await BalanceAsync(item, s.Loc["A-01"]), await BalanceAsync(item, s.Loc["A-02"])));
        Assert.Equal(before, await MovementCountAsync(item));
    }

    // T-M4-01-03：沒有結存的儲位出庫（INSERT 負數，trigger 拒絕）
    [Fact]
    public async Task 沒有結存的儲位出庫_拒絕()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 5), ("A-02", 0));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s, (s.Loc["A-02"], item, -1m, null)));

        Assert.Equal("stock.insufficient", ex.ErrorCode);
        Assert.Contains("A-02", ex.Message);
    }

    // T-M4-01-04（ADR-0004 必要測試）：下限 5、結存 2、出 7 → −5；再出 1 → 拒絕
    [Fact]
    public async Task 負庫存下限5_結存2出7成功_再出1拒絕()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 2));
        await SetNegativeAsync(item, 5);

        await PostAsync(s, (s.Loc["A-01"], item, -7m, null));
        Assert.Equal(-5m, await BalanceAsync(item, s.Loc["A-01"]));
        var last = await LastMovementAsync(item);
        Assert.True(last.IsNegative);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "stock.negative" && a.EntityId == s.Doc.ToString(), Ct));
        }

        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s, (s.Loc["A-01"], item, -1m, null)));
        Assert.Equal("stock.negative_limit", ex.ErrorCode);
        Assert.Equal(-5m, await BalanceAsync(item, s.Loc["A-01"]));
    }

    // T-M4-01-05：負庫存下限是料號全倉合計（使用者 2026-10-22）
    [Fact]
    public async Task 負庫存下限_以全倉合計計算()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s1 = await StockAsync(item, ("A-01", 3));
        var s2 = await StockAsync(item, ("B-01", 0));
        await SetNegativeAsync(item, 5);

        await PostAsync(s2, (s2.Loc["B-01"], item, -8m, null));   // 全倉 3 − 8 = −5，單一儲位 −8 也可以
        Assert.Equal(-8m, await BalanceAsync(item, s2.Loc["B-01"]));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s1, (s1.Loc["A-01"], item, -1m, null)));
        Assert.Equal("stock.negative_limit", ex.ErrorCode);
    }

    // T-M4-01-06：負數只能在有效儲位；待驗儲位不可變負
    [Fact]
    public async Task 負庫存物料_待驗儲位不可變負()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("Q-01", 1));
        await SetNegativeAsync(item, 10);
        await SetLocationStatusAsync(s.Loc["Q-01"], "Inspection");

        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s, (s.Loc["Q-01"], item, -2m, null)));

        Assert.Equal("stock.insufficient", ex.ErrorCode);
        Assert.Equal(1m, await BalanceAsync(item, s.Loc["Q-01"]));
    }

    // T-M4-01-07（ADR-0004 必要測試）：結存 10、預留 8；非預留出 3 拒絕；依預留出 8 成功並沖銷
    [Fact]
    public async Task 預留8_非預留出3拒絕_依預留出8成功並沖銷()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 10));
        var reservation = await ReserveAsync(item, s.Warehouse, 8);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s, (s.Loc["A-01"], item, -3m, null)));
        Assert.Equal("stock.insufficient", ex.ErrorCode);

        await PostAsync(s, (s.Loc["A-01"], item, -8m, reservation));

        var r = await ReservationAsync(reservation);
        Assert.Equal((ReservationStatus.Consumed, 0m), (r.Status, r.RemainingQuantity));
        Assert.Equal(2m, await AvailableAsync(item, s.Warehouse));
        await PostAsync(s, (s.Loc["A-01"], item, -2m, null));
        Assert.Equal(0m, await BalanceAsync(item, s.Loc["A-01"]));
    }

    // T-M4-01-08：出庫量大於預留剩餘量 → 沖銷剩餘量，超出部分當一般出庫（使用者 2026-10-22）
    [Fact]
    public async Task 依預留出庫超過剩餘量_部分沖銷_超出部分檢查可用量()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 10));
        var mine = await ReserveAsync(item, s.Warehouse, 3);
        var others = await ReserveAsync(item, s.Warehouse, 6);

        // 10 − 5 − 6（別人的預留）＝ −1 → 拒絕，自己的預留不變
        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s, (s.Loc["A-01"], item, -5m, mine)));
        Assert.Equal("stock.insufficient", ex.ErrorCode);
        Assert.Equal(3m, (await ReservationAsync(mine)).RemainingQuantity);

        await PostAsync(s, (s.Loc["A-01"], item, -4m, mine));      // 沖銷 3、一般出庫 1
        Assert.Equal(ReservationStatus.Consumed, (await ReservationAsync(mine)).Status);
        Assert.Equal(6m, (await ReservationAsync(others)).RemainingQuantity);
        Assert.Equal(0m, await AvailableAsync(item, s.Warehouse));
    }

    // T-M4-01-09：預留不能預留不存在的庫存（負庫存物料也一樣）
    [Fact]
    public async Task 預留超過可用量_拒絕_負庫存物料也拒絕()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 5));
        await ReserveAsync(item, s.Warehouse, 2);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => ReserveAsync(item, s.Warehouse, 4));
        Assert.Equal("stock.insufficient", ex.ErrorCode);

        await SetNegativeAsync(item, 100);
        ex = await Assert.ThrowsAsync<ConflictException>(() => ReserveAsync(item, s.Warehouse, 4));
        Assert.Equal("stock.insufficient", ex.ErrorCode);
        Assert.Equal(3m, await AvailableAsync(item, s.Warehouse));
    }

    // T-M4-01-10：釋放後可用量恢復；已釋放的預留不能沖銷；重複釋放回傳 false
    [Fact]
    public async Task 釋放預留_可用量恢復_已釋放不可沖銷()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 5));
        var reservation = await ReserveAsync(item, s.Warehouse, 5);
        Assert.Equal(0m, await AvailableAsync(item, s.Warehouse));

        Assert.True(await ReleaseAsync(reservation));
        Assert.False(await ReleaseAsync(reservation));
        Assert.Equal(5m, await AvailableAsync(item, s.Warehouse));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => PostAsync(s, (s.Loc["A-01"], item, -1m, reservation)));
        Assert.Equal("reservation.closed", ex.ErrorCode);
        await Assert.ThrowsAsync<NotFoundException>(() => ReleaseAsync(Guid.NewGuid()));
    }

    // T-M4-01-11：同倉移到待驗儲位會減少可用量 → 預留占滿時拒絕；有效儲位之間移動不影響可用量 → 成功
    [Fact]
    public async Task 同倉移庫_移到待驗儲位檢查可用量_有效儲位間不檢查()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 10), ("A-02", 0), ("Q-01", 0));
        await SetLocationStatusAsync(s.Loc["Q-01"], "Inspection");
        await ReserveAsync(item, s.Warehouse, 10);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            PostAsync(s, (s.Loc["A-01"], item, -1m, null), (s.Loc["Q-01"], item, 1m, null)));
        Assert.Equal("stock.insufficient", ex.ErrorCode);

        await PostAsync(s, (s.Loc["A-01"], item, -4m, null), (s.Loc["A-02"], item, 4m, null));
        Assert.Equal((6m, 4m), (await BalanceAsync(item, s.Loc["A-01"]), await BalanceAsync(item, s.Loc["A-02"])));
    }

    // T-M4-01-12：跨倉移庫，兩端數量守恆；來源倉可用量檢查
    [Fact]
    public async Task 跨倉移庫_數量守恆_來源倉不足拒絕()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s1 = await StockAsync(item, ("A-01", 5));
        var s2 = await StockAsync(item, ("B-01", 0));

        await PostAsync(s1, (s1.Loc["A-01"], item, -5m, null), (s2.Loc["B-01"], item, 5m, null));

        Assert.Equal((0m, 5m), (await BalanceAsync(item, s1.Loc["A-01"]), await BalanceAsync(item, s2.Loc["B-01"])));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var total = await db.StockMovements.Where(m => m.ItemId == item && m.DocumentId == s1.Doc).SumAsync(m => m.QuantityDelta, Ct);
            Assert.Equal(5m, total); // 期初 +5；移庫 −5、+5 合計為 0
            Assert.Equal(5m, await db.StockBalances.Where(b => b.ItemId == item).SumAsync(b => b.Quantity, Ct));
        }
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            PostAsync(s1, (s1.Loc["A-01"], item, -1m, null), (s2.Loc["B-01"], item, 1m, null)));
        Assert.Equal("stock.insufficient", ex.ErrorCode);
    }

    // T-M4-01-13：同一維度在同一張單內先加後減，依淨值判斷；異動後結存逐行正確
    [Fact]
    public async Task 同一維度加減合計_依淨值_異動後結存逐行回推()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 4));

        await PostAsync(s, (s.Loc["A-01"], item, -5m, null), (s.Loc["A-01"], item, 2m, null));

        Assert.Equal(1m, await BalanceAsync(item, s.Loc["A-01"]));
        await using var db = MigratedDatabaseFixture.CreateContext();
        var afters = await db.StockMovements.Where(m => m.ItemId == item && m.QuantityDelta != 4m)
            .Select(m => new { m.QuantityDelta, m.BalanceAfter }).ToListAsync(Ct);
        Assert.Equal(6m, afters.Single(m => m.QuantityDelta == 2m).BalanceAfter);  // 增加在前
        Assert.Equal(1m, afters.Single(m => m.QuantityDelta == -5m).BalanceAfter);
        Assert.All(afters, m => Assert.True(m.BalanceAfter >= 0));
    }

    // T-M4-01-14：序號出庫後可在別的儲位再入庫；批號結存不足拒絕
    [Fact]
    public async Task 序號出庫後可再入庫_批號不足拒絕()
    {
        var serial = await ItemAsync(TrackingMode.Serial);
        var s = await StockAsync(serial, ("S-01", 1, null, "SN-1"), ("S-02", 0, null, null));
        var serialId = await SerialIdAsync(serial, "SN-1");

        await PostAsync(s, [new StockChange(s.Line, serial, s.Loc["S-01"], null, serialId, StockStatus.Available, -1m)]);
        await PostAsync(s, [new StockChange(s.Line, serial, s.Loc["S-02"], null, serialId, StockStatus.Available, 1m)]);
        Assert.Equal(1m, await BalanceAsync(serial, s.Loc["S-02"]));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            PostAsync(s, [new StockChange(s.Line, serial, s.Loc["S-01"], null, serialId, StockStatus.Available, 1m)]));

        var lot = await ItemAsync(TrackingMode.Lot);
        var l = await StockAsync(lot, ("L-01", 3, "LOT-A", null));
        var lotId = await LotIdAsync(lot, "LOT-A");
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            PostAsync(l, [new StockChange(l.Line, lot, l.Loc["L-01"], lotId, null, StockStatus.Available, -4m)]));
        Assert.Equal("stock.insufficient", ex.ErrorCode);
    }

    // T-M4-01-20（審查 #1）：序號在同一筆過帳內移庫、改狀態（扣減先寫，與儲位 id 大小無關）；序號每筆數量必須是 1
    [Fact]
    public async Task 序號移庫與改狀態_同一筆過帳_來回都成功()
    {
        var serial = await ItemAsync(TrackingMode.Serial);
        var s = await StockAsync(serial, ("S-01", 1, null, "SN-9"), ("S-02", 0, null, null));
        var id = await SerialIdAsync(serial, "SN-9");
        StockChange Change(string loc, StockStatus status, decimal delta) => new(s.Line, serial, s.Loc[loc], null, id, status, delta);

        await PostAsync(s, [Change("S-02", StockStatus.Available, 1), Change("S-01", StockStatus.Available, -1)]);
        await PostAsync(s, [Change("S-01", StockStatus.Available, 1), Change("S-02", StockStatus.Available, -1)]);
        await PostAsync(s, [Change("S-01", StockStatus.Quarantine, 1), Change("S-01", StockStatus.Available, -1)]);
        await PostAsync(s, [Change("S-01", StockStatus.Available, 1), Change("S-01", StockStatus.Quarantine, -1)]);

        Assert.Equal((1m, 0m), (await BalanceAsync(serial, s.Loc["S-01"]), await BalanceAsync(serial, s.Loc["S-02"])));
        await Assert.ThrowsAsync<DomainRuleException>(() => PostAsync(s, [Change("S-02", StockStatus.Available, 2)]));
    }

    // T-M4-01-21（審查 #3）：負庫存物料的預留也要維持全倉合計 ≥ −下限
    [Fact]
    public async Task 負庫存物料預留_不可讓全倉合計低於下限()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s1 = await StockAsync(item, ("A-01", 0));
        var s2 = await StockAsync(item, ("B-01", 3));
        await SetNegativeAsync(item, 5);
        await PostAsync(s1, (s1.Loc["A-01"], item, -7m, null));   // 全倉 −4

        var ex = await Assert.ThrowsAsync<ConflictException>(() => ReserveAsync(item, s2.Warehouse, 3)); // 乙倉夠，但全倉會變 −7
        Assert.Equal("stock.negative_limit", ex.ErrorCode);
        await ReserveAsync(item, s2.Warehouse, 1);                                                          // 全倉 −5，可以
    }

    // T-M4-01-22（審查 #7）：倉庫還有 Open 預留時不能停用
    [Fact]
    public async Task 倉庫有未結束預留_不能停用()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 1));
        var reservation = await ReserveAsync(item, s.Warehouse, 1);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            // 測試捷徑：直接把結存歸零（模擬「庫存已清空、預留還在」），只為了測停用檢查
            await db.Database.ExecuteSqlAsync($"UPDATE stock_balances SET quantity = 0 WHERE item_id = {item}", Ct);
        }

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => SetWarehouseActiveAsync(s.Warehouse, false));
        Assert.Contains("預留", ex.Message);
        await ReleaseAsync(reservation);
        await SetWarehouseActiveAsync(s.Warehouse, false);
    }

    private static async Task SetWarehouseActiveAsync(Guid warehouse, bool active)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var version = await db.Warehouses.Where(w => w.Id == warehouse).Select(w => EF.Property<uint>(w, "Version")).SingleAsync(Ct);
        await new WarehouseService(db, TimeProvider.System).SetWarehouseActiveAsync(warehouse, version, active, Manager, Ct);
    }

    // T-M4-01-15：停用儲位不能過帳；數量小數位依單位
    [Fact]
    public async Task 停用儲位與小數位_拒絕()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 5), ("X-01", 0));
        await SetLocationStatusAsync(s.Loc["X-01"], "Inactive");

        await Assert.ThrowsAsync<DomainRuleException>(() => PostAsync(s, (s.Loc["X-01"], item, 1m, null)));
        await Assert.ThrowsAsync<DomainRuleException>(() => PostAsync(s, (s.Loc["A-01"], item, -0.5m, null))); // PCS 整數
    }

    // T-M4-01-16（ADR-0004 必要測試）：20 個並行出庫、結存 10 → 恰好 10 筆成功，結存 0，不為負
    [Fact]
    public async Task 並行出庫20筆_結存10_恰好10筆成功()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 10));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => TryAsync(() => PostAsync(s, (s.Loc["A-01"], item, -1m, null)))));

        Assert.Equal(10, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.Equal("stock.insufficient", r));
        Assert.Equal(0m, await BalanceAsync(item, s.Loc["A-01"]));
    }

    // T-M4-01-17（ADR-0004 必要測試）：並行建立預留與出庫 → 可用量不會小於 0
    [Fact]
    public async Task 並行預留與出庫_可用量不小於0()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s = await StockAsync(item, ("A-01", 10));

        var tasks = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? TryAsync(() => ReserveAsync(item, s.Warehouse, 1))
            : TryAsync(() => PostAsync(s, (s.Loc["A-01"], item, -1m, null))));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Count(r => r is null));
        Assert.Equal(0m, await AvailableAsync(item, s.Warehouse));
        Assert.True(await BalanceAsync(item, s.Loc["A-01"]) >= 0);
    }

    // T-M4-01-18：負庫存物料並行出庫，合計不超過下限
    [Fact]
    public async Task 負庫存並行出庫_不超過下限()
    {
        var item = await ItemAsync(TrackingMode.None);
        var s1 = await StockAsync(item, ("A-01", 0));
        var s2 = await StockAsync(item, ("B-01", 0));
        await SetNegativeAsync(item, 5);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? TryAsync(() => PostAsync(s1, (s1.Loc["A-01"], item, -1m, null)))
            : TryAsync(() => PostAsync(s2, (s2.Loc["B-01"], item, -1m, null)))));

        Assert.Equal(5, results.Count(r => r is null));
        Assert.Equal(-5m, await BalanceAsync(item, s1.Loc["A-01"]) + await BalanceAsync(item, s2.Loc["B-01"]));
    }

    // T-M4-01-19（M3-03 審查 #7）：500 行一次過帳（批次 upsert），記錄耗時
    [Fact]
    public async Task 五百行過帳_批次寫入_記錄耗時()
    {
        var items = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            items.Add(await ItemAsync(TrackingMode.None));
        }
        var codes = Enumerable.Range(1, 50).Select(i => $"P-{i:D3}").ToArray();
        var s = await StockAsync(items[0], codes.Select(c => (c, 0m)).ToArray());
        var add = codes.SelectMany(c => items.Select(i => new StockChange(s.Line, i, s.Loc[c], null, null, StockStatus.Available, 2m))).ToList();
        await PostAsync(s, add);

        var watch = Stopwatch.StartNew();
        await PostAsync(s, add.Select(c => c with { Delta = -1m }).ToList());
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"500 行扣減過帳：{watch.ElapsedMilliseconds} ms");
        Assert.Equal(1m, await BalanceAsync(items[9], s.Loc["P-050"]));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"500 行過帳 {watch.ElapsedMilliseconds} ms");
    }

    // ---- 輔助 ----

    private sealed record Stocked(Guid Warehouse, Dictionary<string, Guid> Loc, Guid Doc, Guid Line);

    private static Task<Stocked> StockAsync(Guid item, params (string Location, decimal Qty)[] lines) =>
        StockAsync(item, lines.Select(l => (l.Location, l.Qty, (string?)null, (string?)null)).ToArray());

    /// <summary>建立倉庫與儲位，以期初單建立初始結存（數量 0 的儲位登記為空儲位）。</summary>
    private static async Task<Stocked> StockAsync(Guid item, params (string Location, decimal Qty, string? Lot, string? Serial)[] lines)
    {
        var code = "P" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        Guid warehouse;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var service = new WarehouseService(db, TimeProvider.System);
            warehouse = await service.CreateWarehouseAsync(new WarehouseInput(code, "過帳測試倉"), Manager, Ct);
            foreach (var l in lines)
            {
                await service.CreateLocationAsync(warehouse, new LocationInput(l.Location, Empty), Manager, Ct);
            }
        }
        var itemCode = await ItemCodeAsync(item);
        Guid doc;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var opening = Opening(db);
            doc = await opening.CreateAsync(warehouse, null, Operator, Ct);
            foreach (var l in lines)
            {
                await opening.AddLineAsync(doc, new OpeningLineInput(l.Location, l.Qty > 0 ? itemCode : null, l.Lot, l.Serial, null, l.Qty), Operator, Ct);
            }
        }
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await Opening(db).SubmitAsync(doc, await VersionAsync(doc), Operator, Ct);
        }
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await Opening(db).ApproveAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), Manager, Ct);
        }
        await using var read = MigratedDatabaseFixture.CreateContext();
        var locations = await read.Locations.Where(l => l.WarehouseId == warehouse).ToDictionaryAsync(l => l.Code, l => l.Id, Ct);
        var line = await read.Set<StockDocumentLine>().Where(l => l.DocumentId == doc).OrderBy(l => l.LineNo).Select(l => l.Id).FirstAsync(Ct);
        return new Stocked(warehouse, locations, doc, line);
    }

    private static Task PostAsync(Stocked s, params (Guid Location, Guid Item, decimal Delta, Guid? Reservation)[] changes) =>
        PostAsync(s, changes.Select(c => new StockChange(s.Line, c.Item, c.Location, null, null, StockStatus.Available, c.Delta, c.Reservation)).ToList());

    private static async Task PostAsync(Stocked s, IReadOnlyList<StockChange> changes)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        await new InventoryPostingService(db).ApplyAsync(s.Doc, changes, Operator, DateTimeOffset.UtcNow, Ct);
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);
    }

    private static async Task<Guid> ReserveAsync(Guid item, Guid warehouse, decimal quantity)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var id = await new InventoryPostingService(db).ReserveAsync(
            new ReservationRequest(item, warehouse, quantity, "Manual", null, "測試"), Operator, DateTimeOffset.UtcNow, Ct);
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private static async Task<bool> ReleaseAsync(Guid reservation)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var released = await new InventoryPostingService(db).ReleaseReservationAsync(reservation, Operator, DateTimeOffset.UtcNow, Ct);
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);
        return released;
    }

    /// <summary>執行並回傳錯誤碼（成功為 null）；預期的業務錯誤以外的例外照常丟出。</summary>
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
    }

    private static async Task<decimal> BalanceAsync(Guid item, Guid location)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockBalances.Where(b => b.ItemId == item && b.LocationId == location).SumAsync(b => b.Quantity, Ct);
    }

    private static async Task<decimal> AvailableAsync(Guid item, Guid warehouse)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var stock = await (from b in db.StockBalances
                           join l in db.Locations on b.LocationId equals l.Id
                           where b.ItemId == item && l.WarehouseId == warehouse && b.StockStatus == StockStatus.Available
                                 && l.Status == LocationStatus.Available
                           select b.Quantity).SumAsync(Ct);
        var reserved = await db.StockReservations
            .Where(r => r.ItemId == item && r.WarehouseId == warehouse && r.Status == ReservationStatus.Open)
            .SumAsync(r => r.RemainingQuantity, Ct);
        return stock - reserved;
    }

    private static async Task<StockReservation> ReservationAsync(Guid id)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockReservations.AsNoTracking().SingleAsync(r => r.Id == id, Ct);
    }

    private static async Task<StockMovement> LastMovementAsync(Guid item)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockMovements.AsNoTracking().Where(m => m.ItemId == item).OrderByDescending(m => m.OccurredAt).FirstAsync(Ct);
    }

    private static async Task<int> MovementCountAsync(Guid item)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockMovements.CountAsync(m => m.ItemId == item, Ct);
    }

    private static async Task<Guid> SerialIdAsync(Guid item, string serial)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.ItemSerials.Where(x => x.ItemId == item && x.SerialNo == serial).Select(x => x.Id).SingleAsync(Ct);
    }

    private static async Task<Guid> LotIdAsync(Guid item, string lot)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.InventoryLots.Where(x => x.ItemId == item && x.LotNo == lot).Select(x => x.Id).SingleAsync(Ct);
    }

    private static async Task SetNegativeAsync(Guid item, decimal limit)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE items SET allow_negative_stock = true, negative_stock_limit = {limit} WHERE id = {item}", Ct);
    }

    private static async Task SetLocationStatusAsync(Guid location, string status)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await db.Database.ExecuteSqlAsync($"UPDATE locations SET status = {status} WHERE id = {location}", Ct);
    }

    private static async Task<Guid> ItemAsync(TrackingMode tracking)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("過帳物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, tracking, null, null, null), ItemManager, Ct);
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

    private static OpeningService Opening(WarehouseDbContext db) =>
        new(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);
}
