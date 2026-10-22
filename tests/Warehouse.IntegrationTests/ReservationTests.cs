using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Warehouse.Web.Jobs;
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

/// <summary>M4-07／M4-08 手動預留、到期釋放、負庫存設定、待補帳（T-M4-04-01～），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ReservationTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員甲", false);
    private static readonly Actor Operator2 = new(Guid.NewGuid(), "作業員乙", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M4-04-01（ADR-0004 必要測試）：同一鍵重送預留只建立一筆；同鍵不同內容被拒；並行 5 次也只一筆
    [Fact]
    public async Task 同鍵重送預留_只建立一筆()
    {
        var (w, item) = await StockedAsync(10m);
        var input = new ReservationInput(item, w, 3m, "工單 A", null);
        var key = "key-" + Guid.NewGuid();

        var first = await Reservations(s => s.CreateAsync(input, key, Operator, Ct));
        Assert.Equal(first, await Reservations(s => s.CreateAsync(input, key, Operator, Ct)));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Reservations(s => s.CreateAsync(input with { Quantity = 4m }, key, Operator, Ct)));
        Assert.Equal("idempotency.key_reused", ex.ErrorCode);

        var key2 = "key-" + Guid.NewGuid();
        var ids = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Reservations(s => s.CreateAsync(input, key2, Operator, Ct))));
        Assert.Single(ids.Distinct());
        Assert.Equal(4m, await AvailableAsync(item, w));
    }

    // T-M4-04-02：取消：本人或主管；別人不行；出倉單送審預留不能在這裡取消
    [Fact]
    public async Task 取消_限本人或主管_單據預留不可取消()
    {
        var (w, item) = await StockedAsync(10m);
        var mine = await CreateAsync(item, w, 2m, Operator);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Reservations(s => s.CancelAsync(mine, Operator2, Ct)));
        await Reservations(s => s.CancelAsync(mine, Operator, Ct));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Reservations(s => s.CancelAsync(mine, Operator, Ct)));
        Assert.Equal("reservation.closed", ex.ErrorCode);
        var others = await CreateAsync(item, w, 2m, Operator2);
        await Reservations(s => s.CancelAsync(others, Manager, Ct));

        var docReservation = Guid.NewGuid();
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            docReservation = await new InventoryPostingService(db).ReserveAsync(
                new ReservationRequest(await ItemIdAsync(item), w, 1m, StockDocumentService.ReservationReference, Guid.NewGuid(), null), Operator, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        }
        ex = await Assert.ThrowsAsync<ConflictException>(() => Reservations(s => s.CancelAsync(docReservation, Manager, Ct)));
        Assert.Equal("reservation.not_cancellable", ex.ErrorCode);
    }

    // T-M4-04-03：到期：過去的到期時間被拒；到期後不計入可用量、不能沖銷；背景工作改為已釋放並寫稽核
    [Fact]
    public async Task 到期預留_不計可用量_背景工作釋放()
    {
        var (w, item) = await StockedAsync(10m);
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateAsync(item, w, 1m, Operator, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var soon = await CreateAsync(item, w, 4m, Operator, DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Equal(6m, await AvailableAsync(item, w));

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Assert.Equal(10m, await AvailableAsync(item, w));       // 背景工作還沒跑，可用量已不計
        var outbound = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, 1m, soon);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(outbound));
        Assert.Equal("reservation.closed", ex.ErrorCode);

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            var count = await new InventoryPostingService(db).ExpireDueReservationsAsync(ReservationExpiryJob.SystemActor, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
            Assert.True(count >= 1);
        }
        await using var check = MigratedDatabaseFixture.CreateContext();
        var row = await check.StockReservations.AsNoTracking().SingleAsync(r => r.Id == soon, Ct);
        Assert.Equal(ReservationStatus.Released, row.Status);
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == "reservation.expire" && a.EntityId == soon.ToString(), Ct));
    }

    // T-M4-04-04（ADR-0004 必要測試）：已有負結存時關閉負庫存被拒；下限不可小於目前全倉負數；批號物料不能開；非物料管理員被拒
    [Fact]
    public async Task 負庫存設定_有負結存不可關閉_下限檢查_權限()
    {
        var (w, item) = await StockedAsync(2m);
        var itemId = await ItemIdAsync(item);
        await SetPolicyAsync(itemId, true, 5m, ItemManager);
        await ConfirmAsync(await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, 6m));   // −4

        var rule = await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicyAsync(itemId, false, 0m, ItemManager));
        Assert.Contains("負結存", rule.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicyAsync(itemId, true, 3m, ItemManager));
        await SetPolicyAsync(itemId, true, 4m, ItemManager);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => SetPolicyAsync(itemId, true, 9m, Manager));

        var lot = await ItemCodeAsync(TrackingMode.Lot);
        var lotId = await ItemIdAsync(lot);
        await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicyAsync(lotId, true, 1m, ItemManager));

        var negative = await Stock(s => s.GetNegativeBalancesAsync(w, 1, Ct));
        var row = Assert.Single(negative.Items);
        Assert.Equal((-4m, item), (row.Quantity, row.ItemCode));
        Assert.StartsWith("OUT-", row.LastDocNo);
    }

    // T-M4-04-05（ADR-0004 必要測試）：並行「關閉負庫存」與「造成負數的出庫」：最後不會出現不允許負庫存卻有負結存
    [Fact]
    public async Task 並行關閉負庫存與出庫_不會留下不合法負結存()
    {
        for (var round = 0; round < 5; round++)
        {
            var (w, item) = await StockedAsync(1m);
            var itemId = await ItemIdAsync(item);
            await SetPolicyAsync(itemId, true, 10m, ItemManager);
            var outbound = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, 3m);
            var version = await VersionAsync(outbound);
            var itemVersion = await ItemVersionAsync(itemId);

            await Task.WhenAll(
                Swallow(() => Docs(s => s.ConfirmAsync(outbound, version, "key-" + Guid.NewGuid(), Operator, Ct))),
                Swallow(() => Policies(s => s.SetAsync(itemId, itemVersion, false, 0m, ItemManager, Ct))));

            await using var db = MigratedDatabaseFixture.CreateContext();
            var allow = await db.Items.Where(i => i.Id == itemId).Select(i => i.AllowNegativeStock).SingleAsync(Ct);
            var hasNegative = await db.StockBalances.AnyAsync(b => b.ItemId == itemId && b.Quantity < 0, Ct);
            Assert.False(!allow && hasNegative, $"第 {round} 輪：不允許負庫存卻有負結存");
        }
    }

    // T-M4-04-06（審查 #4）：出倉明細可選的預留只列手動、未過期的；20 筆以上單據預留也不會擠掉手動預留
    [Fact]
    public async Task 可選預留_只列手動且未過期()
    {
        var (w, item) = await StockedAsync(50m);
        var itemId = await ItemIdAsync(item);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            await new InventoryPostingService(db).ReserveManyAsync(Enumerable.Range(0, 21)
                .Select(_ => new ReservationRequest(itemId, w, 1m, StockDocumentService.ReservationReference, Guid.NewGuid(), null)).ToList(),
                Operator, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        }
        var manual = await CreateAsync(item, w, 2m, Operator);
        var expiring = await CreateAsync(item, w, 1m, Operator, DateTimeOffset.UtcNow.AddSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        var selectable = await Reservations(s => s.ListAsync(item, w, ReservationStatus.Open, 1, Ct, selectableOnly: true));
        Assert.Equal([manual], selectable.Items.Select(r => r.Id));
        Assert.DoesNotContain(selectable.Items, r => r.Id == expiring);
    }

    // T-M4-04-07（審查 #5 #6）：可用量（扣除預留）已是負數時不可關閉負庫存；下限超過上限或小數位不符被拒
    [Fact]
    public async Task 負可用量不可關閉_下限上限與小數位()
    {
        var (w, item) = await StockedAsync(3m);
        var itemId = await ItemIdAsync(item);
        await SetPolicyAsync(itemId, true, 5m, ItemManager);
        await ConfirmAsync(await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, 3m));   // 結存 0
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            // 測試捷徑：直接做出「結存 0、預留 2」→ 可用量 −2（例如允許負庫存時建立的單據預留）
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO stock_reservations (id, item_id, warehouse_id, quantity, remaining_quantity, reference_type, status, created_by_id, created_by_name, created_at)
                VALUES ({Guid.NewGuid()}, {itemId}, {w}, 2, 2, 'Manual', 'Open', {Guid.NewGuid()}, '測試', now())
                """, Ct);
        }
        var rule = await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicyAsync(itemId, false, 0m, ItemManager));
        Assert.Contains("預留", rule.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicyAsync(itemId, true, 1_000_000_000m, ItemManager));
        await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicyAsync(itemId, true, 2.5m, ItemManager));   // PCS 整數
    }

    // ---- 輔助 ----

    private static async Task Swallow(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AppException)
        {
        }
        catch (DomainRuleException)
        {
        }
    }

    private static async Task<(Guid Warehouse, string Item)> StockedAsync(decimal quantity)
    {
        var code = "R" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        Guid warehouse;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var service = new WarehouseService(db, TimeProvider.System);
            warehouse = await service.CreateWarehouseAsync(new WarehouseInput(code, "預留測試倉"), Manager, Ct);
            await service.CreateLocationAsync(warehouse, new LocationInput("A-01", Empty), Manager, Ct);
        }
        var item = await ItemCodeAsync(TrackingMode.None);
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, warehouse, PurchaseIn, item, quantity));
        return (warehouse, item);
    }

    private static async Task<Guid> CreateAsync(string item, Guid warehouse, decimal quantity, Actor actor, DateTimeOffset? expires = null) =>
        await Reservations(s => s.CreateAsync(new ReservationInput(item, warehouse, quantity, null, expires), "key-" + Guid.NewGuid(), actor, Ct));

    private static async Task<Guid> DocAsync(
        StockDocumentType type, Guid warehouse, Guid purpose, string item, decimal quantity, Guid? reservation = null)
    {
        var doc = await Docs(s => s.CreateAsync(type, warehouse, new StockDocumentHeader(purpose, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item, quantity, ReservationId: reservation), Operator, Ct));
        return doc;
    }

    private static async Task ConfirmAsync(Guid doc) =>
        await Docs(async s => await s.ConfirmAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), Operator, Ct));

    private static async Task SetPolicyAsync(Guid itemId, bool allow, decimal limit, Actor actor) =>
        await Policies(async s => await s.SetAsync(itemId, await ItemVersionAsync(itemId), allow, limit, actor, Ct));

    private static async Task<decimal> AvailableAsync(string itemCode, Guid warehouse)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var itemId = await db.Items.Where(i => i.ItemCode == itemCode).Select(i => i.Id).SingleAsync(Ct);
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var available = await db.Database.SqlQueryRaw<decimal>("""
            SELECT (COALESCE((SELECT SUM(b.quantity) FROM stock_balances b JOIN locations l ON l.id = b.location_id
                              WHERE b.item_id = {0} AND l.warehouse_id = {1} AND b.stock_status = 'Available' AND l.status = 'Available'), 0)
                  - COALESCE((SELECT SUM(r.remaining_quantity) FROM stock_reservations r
                              WHERE r.item_id = {0} AND r.warehouse_id = {1} AND r.status = 'Open'
                                AND (r.expires_at IS NULL OR r.expires_at > now())), 0)) AS "Value"
            """, itemId, warehouse).SingleAsync(Ct);
        return available;
    }

    private static async Task<Guid> ItemIdAsync(string itemCode)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.ItemCode == itemCode).Select(i => i.Id).SingleAsync(Ct);
    }

    private static async Task<uint> ItemVersionAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<string> ItemCodeAsync(TrackingMode tracking)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("預留物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, tracking, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<T> Reservations<T>(Func<IStockReservationService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }

    private static async Task Reservations(Func<IStockReservationService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }

    private static async Task Policies(Func<IItemNegativePolicyService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
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

    private static async Task<T> Stock<T>(Func<IStockQueryService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockQueryService(db));
    }
}
