using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>M4-04 併發、冪等、回滾補測（T-M4-06-01～），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class PostingHardeningTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor Admin = new(Guid.NewGuid(), "系統管理員", false);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M4-06-01：審核開啟，結存 10，兩張各 8 的出倉單同時送審 → 只有一張成功，可用量不為負
    [Fact]
    public async Task 兩張出倉單同時送審_只有一張成功()
    {
        var (w, item) = await StockedAsync(10m, "A-01");
        var a = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, "A-01", 8m);
        var b = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, "A-01", 8m);
        await SetApprovalAsync(true);
        try
        {
            var (va, vb) = (await VersionAsync(a), await VersionAsync(b));
            var results = await Task.WhenAll(
                TryAsync(() => Docs(s => s.SubmitAsync(a, va, "key-" + Guid.NewGuid(), Operator, Ct))),
                TryAsync(() => Docs(s => s.SubmitAsync(b, vb, "key-" + Guid.NewGuid(), Operator, Ct))));
            Assert.Equal(1, results.Count(r => r is null));
            Assert.Contains("stock.insufficient", results);
            Assert.Equal(2m, await AvailableAsync(item, w));
        }
        finally
        {
            await SetApprovalAsync(false);
        }
    }

    // T-M4-06-02：明細指定的手動預留在過帳前被取消 → 確認被拒、單據仍是草稿、結存不變
    [Fact]
    public async Task 指定的預留已取消_確認被拒_不留痕()
    {
        var (w, item) = await StockedAsync(10m, "A-01");
        Guid reservation;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            reservation = await new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System)
                .CreateAsync(new ReservationInput(item, w, 3m, null, null), "key-" + Guid.NewGuid(), Operator, Ct);
        }
        var doc = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, item, "A-01", 3m, reservation);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System).CancelAsync(reservation, Operator, Ct);
        }

        var ex = await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(doc));
        Assert.Equal("reservation.closed", ex.ErrorCode);
        Assert.Equal(StockDocumentStatus.Draft, (await Docs(s => s.GetAsync(doc, Ct)))!.Status);
        Assert.Equal(10m, await BalanceAsync(item, w, "A-01"));
    }

    // T-M4-06-03：回滾：移庫目的儲位在過帳前被停用 → 整張失敗；沒有異動、沒有冪等紀錄、沒有新批號
    [Fact]
    public async Task 中途失敗_整筆回滾不留痕()
    {
        var (w, item) = await StockedAsync(5m, "A-01", "B-01");
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Transfer, w, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item, 2m, TargetLocationCode: "B-01"), Operator, Ct));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE locations SET status = 'Inactive' WHERE warehouse_id = {w} AND code = 'B-01'", Ct);
        }
        var key = "key-" + Guid.NewGuid();
        var before = await MovementCountAsync(item);

        await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await Docs(async s => await s.ConfirmAsync(doc, await VersionAsync(doc), key, Operator, Ct)));

        Assert.Equal(before, await MovementCountAsync(item));
        Assert.Equal(5m, await BalanceAsync(item, w, "A-01"));
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.False(await check.ProcessedCommands.AnyAsync(c => c.Key == key, Ct));
        Assert.Equal(StockDocumentStatus.Draft, (await Docs(s => s.GetAsync(doc, Ct)))!.Status);
    }

    // T-M4-06-04（效能）：500 行跨儲位移庫一次確認，記錄耗時；數量守恆
    [Fact]
    public async Task 五百行移庫_確認耗時_數量守恆()
    {
        var codes = Enumerable.Range(1, 10).Select(i => $"M-{i:D2}").ToArray();
        var (w, _) = await StockedAsync(0m, codes);
        var items = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            items.Add(await ItemCodeAsync());
        }
        var inbound = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
        var transfer = await Docs(s => s.CreateAsync(StockDocumentType.Transfer, w, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var itemIds = await db.Items.Where(i => items.Contains(i.ItemCode!)).Select(i => i.Id).ToListAsync(Ct);
            var locations = await db.Locations.Where(l => l.WarehouseId == w).ToDictionaryAsync(l => l.Code, l => l.Id, Ct);
            var inDoc = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == inbound, Ct);
            var trDoc = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == transfer, Ct);
            for (var l = 0; l < 10; l++)
            {
                foreach (var itemId in itemIds)
                {
                    inDoc.AddMovementLine(Guid.NewGuid(), itemId, TrackingMode.None, 0, locations[codes[l]], null, null, null, null, 3m, null);
                    trDoc.AddMovementLine(Guid.NewGuid(), itemId, TrackingMode.None, 0, locations[codes[l]], locations[codes[(l + 1) % 10]],
                        null, null, null, 2m, null);
                }
            }
            await db.SaveChangesAsync(Ct);
        }
        await ConfirmAsync(inbound);

        var watch = Stopwatch.StartNew();
        await ConfirmAsync(transfer);
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"500 行移庫確認：{watch.ElapsedMilliseconds} ms");
        await using var check = MigratedDatabaseFixture.CreateContext();
        var movements = await check.StockMovements.Where(m => m.DocumentId == transfer).ToListAsync(Ct);
        Assert.Equal(1000, movements.Count);
        Assert.Equal(0m, movements.Sum(m => m.QuantityDelta));
        Assert.Equal(1500m, await check.StockBalances.Where(b => check.Locations.Any(l => l.Id == b.LocationId && l.WarehouseId == w)).SumAsync(b => b.Quantity, Ct));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
    }

    // T-M4-06-05：API 同一個 Idempotency-Key 並行送 5 次確認 → 全部 204，只過帳一次
    [Fact]
    public async Task API同鍵並行5次_全部成功_只過帳一次()
    {
        var (w, item) = await StockedAsync(0m, "A-01");
        var doc = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, item, "A-01", 4m);
        var version = await VersionAsync(doc);
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var userName = "op" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, userName, "Test-pass-123", Roles.WarehouseManager);
        var session = WebSession.Create(factory);
        await session.LoginAsync(userName, "Test-pass-123");
        var key = "key-" + Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/stock/documents/{doc}/confirm") { Content = JsonContent.Create(new { version }) };
            request.Headers.Add("Idempotency-Key", key);
            return session.Client.SendAsync(request, Ct);
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(4m, await BalanceAsync(item, w, "A-01"));
        Assert.Equal(1, await MovementCountAsync(item, doc));
    }

    // ---- 輔助 ----

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

    private static async Task<(Guid Warehouse, string Item)> StockedAsync(decimal quantity, params string[] locations)
    {
        var code = "K" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        Guid warehouse;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var service = new WarehouseService(db, TimeProvider.System);
            warehouse = await service.CreateWarehouseAsync(new WarehouseInput(code, "強化測試倉"), Manager, Ct);
            foreach (var location in locations)
            {
                await service.CreateLocationAsync(warehouse, new LocationInput(location, Empty), Manager, Ct);
            }
        }
        var item = await ItemCodeAsync();
        if (quantity > 0)
        {
            await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, warehouse, PurchaseIn, item, locations[0], quantity));
        }
        return (warehouse, item);
    }

    private static async Task<Guid> DocAsync(
        StockDocumentType type, Guid warehouse, Guid purpose, string item, string location, decimal quantity, Guid? reservation = null)
    {
        var doc = await Docs(s => s.CreateAsync(type, warehouse, new StockDocumentHeader(purpose, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput(location, item, quantity, ReservationId: reservation), Operator, Ct));
        return doc;
    }

    private static async Task ConfirmAsync(Guid doc) =>
        await Docs(async s => await s.ConfirmAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), Operator, Ct));

    private static async Task SetApprovalAsync(bool enabled)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new SettingsService(db, TimeProvider.System);
        var current = await service.GetAsync(Ct);
        if (current.DocumentApprovalEnabled != enabled)
        {
            await service.UpdateAsync(current, current with { DocumentApprovalEnabled = enabled }, Admin, Ct);
        }
    }

    private static async Task<decimal> BalanceAsync(string itemCode, Guid warehouse, string location)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await (from b in db.StockBalances
                      join l in db.Locations on b.LocationId equals l.Id
                      join i in db.Items on b.ItemId equals i.Id
                      where i.ItemCode == itemCode && l.WarehouseId == warehouse && l.Code == location
                      select b.Quantity).SumAsync(Ct);
    }

    private static async Task<decimal> AvailableAsync(string itemCode, Guid warehouse)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var itemId = await db.Items.Where(i => i.ItemCode == itemCode).Select(i => i.Id).SingleAsync(Ct);
        var stock = await (from b in db.StockBalances
                           join l in db.Locations on b.LocationId equals l.Id
                           where b.ItemId == itemId && l.WarehouseId == warehouse && b.StockStatus == StockStatus.Available
                           select b.Quantity).SumAsync(Ct);
        var reserved = await db.StockReservations.Where(r => r.ItemId == itemId && r.WarehouseId == warehouse && r.Status == ReservationStatus.Open)
            .SumAsync(r => r.RemainingQuantity, Ct);
        return stock - reserved;
    }

    private static async Task<int> MovementCountAsync(string itemCode, Guid? document = null)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockMovements.CountAsync(m => db.Items.Any(i => i.Id == m.ItemId && i.ItemCode == itemCode)
            && (document == null || m.DocumentId == document), Ct);
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<string> ItemCodeAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("強化物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
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
}
