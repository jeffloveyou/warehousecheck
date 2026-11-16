using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>R1/R3 查詢範圍、倉庫可出量及單據追溯；每個測試自行建立物料、倉庫與單據。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public sealed class UsabilityQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Actor Manager = new(Guid.NewGuid(), "易用性查詢主管", true, true);

    [Fact]
    public async Task 結存第二頁不會切換只有一頁的倉庫摘要()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var (warehouse, _, _, itemCode) = await PrepareAsync(db);
        var settings = new SettingsService(db, TimeProvider.System);
        var original = await settings.GetAsync(Ct);
        try
        {
            if (original.DocumentApprovalEnabled)
                await settings.UpdateAsync(original, original with { DocumentApprovalEnabled = false }, Manager, Ct);
            var warehouses = new WarehouseService(db, TimeProvider.System);
            var docs = Documents(db);
            var purchase = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
            var id = await docs.CreateAsync(StockDocumentType.Inbound, warehouse, new(purchase, null, null, null), Manager, Ct);
            for (var n = 1; n <= 25; n++)
            {
                var code = $"A-{n:00}";
                if (n > 1)
                    await warehouses.CreateLocationAsync(warehouse,
                        new(code, new LocationDetails(null, null, null, null, null, null, null, null, false, false)), Manager, Ct);
                await docs.AddLineAsync(id, new(code, itemCode, 1m), Manager, Ct);
            }
            await docs.ConfirmAsync(id, (await docs.GetAsync(id, Ct))!.Version, Guid.NewGuid().ToString(), Manager, Ct);
            var page = new Warehouse.Web.Pages.Stock.IndexModel(new StockQueryService(db), warehouses, new ItemQueryService(db))
            {
                WarehouseId = warehouse, ItemCode = itemCode, PageNumber = 2, SummaryPageNumber = 1
            };
            await page.OnGetAsync(Ct);
            Assert.NotNull(page.Balances);
            Assert.Equal(2, page.Balances.Page);
            Assert.Equal(5, page.Balances.Items.Count);
            Assert.NotNull(page.Summary);
            Assert.Equal(1, page.Summary.Page);
            Assert.Equal(1, page.Summary.TotalPages);
            Assert.Equal(25m, Assert.Single(page.Summary.Items).Balance);
        }
        finally
        {
            var current = await settings.GetAsync(Ct);
            if (current != original) await settings.UpdateAsync(current, original, Manager, Ct);
        }
    }

    [Fact]
    public async Task 倉庫異動不混倉_摘要扣有效預留_儲位結存不扣預留()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var (warehouse, otherWarehouse, itemId, itemCode) = await PrepareAsync(db);
        var settings = new SettingsService(db, TimeProvider.System);
        var original = await settings.GetAsync(Ct);
        try
        {
            if (original.DocumentApprovalEnabled)
                await settings.UpdateAsync(original, original with { DocumentApprovalEnabled = false }, Manager, Ct);
            await InboundAsync(db, warehouse, itemCode, 10m);
            await InboundAsync(db, otherWarehouse, itemCode, 3m);
            var reservations = new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System);
            await reservations.CreateAsync(new(itemCode, warehouse, 4m, "查詢預留", null), Guid.NewGuid().ToString(), Manager, Ct);
            var stock = new StockQueryService(db);
            var movements = await stock.GetMovementsAsync(itemId, null, null, 1, Ct, warehouse);
            Assert.Single(movements.Items);
            Assert.Equal(10m, movements.Items.Single().QuantityDelta);
            var location = await db.Locations.Where(l => l.WarehouseId == warehouse).Select(l => l.Id).SingleAsync(Ct);
            var summary = (await stock.GetWarehouseSummaryAsync(warehouse, location, itemId, 1, Ct)).Items.Single();
            Assert.Equal((10m, 10m, 4m, 6m), (summary.Balance, summary.EligibleBalance, summary.Reserved, summary.Available));
            Assert.Equal(10m, (await stock.GetBalancesAsync(warehouse, location, itemId, 1, Ct)).Items.Single().Quantity);
            Assert.Empty((await stock.GetMovementsAsync(itemId, location, null, 1, Ct, otherWarehouse)).Items);
        }
        finally
        {
            var current = await settings.GetAsync(Ct);
            if (current != original) await settings.UpdateAsync(current, original, Manager, Ct);
        }
    }

    [Fact]
    public async Task 單據可依料號單號操作人日期及倉庫範圍查詢_空範圍不回傳()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var (warehouse, otherWarehouse, _, itemCode) = await PrepareAsync(db);
        var docs = Documents(db);
        var purchase = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
        var id = await docs.CreateAsync(StockDocumentType.Inbound, warehouse, new(purchase, null, null, null), Manager, Ct);
        await docs.AddLineAsync(id, new("A-01", itemCode, 1m), Manager, Ct);
        var detail = (await docs.GetAsync(id, Ct))!;
        var filter = new DocumentListFilter(detail.DocNo, itemCode, Manager.Name, Manager.Id,
            detail.CreatedAt.AddSeconds(-1), detail.CreatedAt.AddSeconds(1), [warehouse]);
        var result = await docs.ListAsync(null, null, null, 1, Ct, filter: filter);
        Assert.Equal(id, Assert.Single(result.Items).Id);
        Assert.Empty((await docs.ListAsync(null, null, null, 1, Ct, filter: filter with { AllowedWarehouseIds = [] })).Items);
        Assert.Empty((await docs.ListAsync(null, null, null, 1, Ct, filter: filter with { AllowedWarehouseIds = [otherWarehouse] })).Items);
        Assert.Empty((await docs.ListAsync(null, null, null, 1, Ct, filter: filter with { CreatedById = Guid.NewGuid() })).Items);
        Assert.Empty((await docs.ListAsync(null, null, null, 1, Ct, filter: filter with { From = detail.CreatedAt.AddDays(1) })).Items);
        Assert.Empty((await docs.ListAsync(null, null, null, 1, Ct, filter: filter with { ReturnedOnly = true })).Items);
        // 只建立退回草稿 fixture，不觸及結存；驗證 ReturnComment 的查詢條件。
        var returned = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == id, Ct);
        returned.Submit(DateTimeOffset.UtcNow);
        returned.Return("測試退回原因");
        await db.SaveChangesAsync(Ct);
        Assert.Equal(id, Assert.Single((await docs.ListAsync(null, null, null, 1, Ct,
            filter: filter with { ReturnedOnly = true })).Items).Id);
    }

    private static StockDocumentService Documents(WarehouseDbContext db) =>
        new(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);

    private static async Task InboundAsync(WarehouseDbContext db, Guid warehouse, string itemCode, decimal quantity)
    {
        var docs = Documents(db);
        var purchase = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
        var id = await docs.CreateAsync(StockDocumentType.Inbound, warehouse, new(purchase, null, null, null), Manager, Ct);
        await docs.AddLineAsync(id, new("A-01", itemCode, quantity), Manager, Ct);
        var version = (await docs.GetAsync(id, Ct))!.Version;
        await docs.ConfirmAsync(id, version, Guid.NewGuid().ToString(), Manager, Ct);
    }

    private static async Task<(Guid Warehouse, Guid OtherWarehouse, Guid ItemId, string ItemCode)> PrepareAsync(WarehouseDbContext db)
    {
        var warehouses = new WarehouseService(db, TimeProvider.System);
        async Task<Guid> CreateWarehouse()
        {
            var id = await warehouses.CreateWarehouseAsync(new("U" + Guid.NewGuid().ToString("N")[..7], "查詢測試倉"), Manager, Ct);
            await warehouses.CreateLocationAsync(id, new("A-01", new LocationDetails(null, null, null, null, null, null, null, null, false, false)), Manager, Ct);
            return id;
        }
        var warehouse = await CreateWarehouse();
        var otherWarehouse = await CreateWarehouse();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft("查詢物料 " + Guid.NewGuid().ToString("N"),
            SeedData.Categories.Single(c => c.Code == "EL").Id, SeedData.Units.Single(u => u.Code == "PCS").Id,
            TrackingMode.None, null, null, null), Manager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, Manager, Ct);
        var code = await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
        return (warehouse, otherWarehouse, item.ItemId, code);
    }
}
