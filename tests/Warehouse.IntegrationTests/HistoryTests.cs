using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
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

/// <summary>M4-05 異動歷史與異常追蹤（T-M4-05-01～），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class HistoryTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "歷史作業員" + Guid.NewGuid().ToString("N")[..6], false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M4-05-01：異動歷史依料號、儲位、單號、操作者、日期區間篩選並分頁
    [Fact]
    public async Task 異動歷史_多條件篩選_分頁()
    {
        var (w, code) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync();
        var inbound = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 30m), ("A-02", item, 5m));
        await ConfirmAsync(inbound);
        for (var i = 0; i < 21; i++)
        {
            await ConfirmAsync(await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 1m)));
        }
        var inboundNo = (await Docs(s => s.GetAsync(inbound, Ct)))!.DocNo;

        var byItem = await Stock(s => s.SearchMovementsAsync(new MovementQuery(ItemCode: item.ToLowerInvariant()), Ct));
        Assert.Equal(23, byItem.TotalCount);
        Assert.Equal(20, byItem.Items.Count);
        var page2 = await Stock(s => s.SearchMovementsAsync(new MovementQuery(ItemCode: item, Page: 2), Ct));
        Assert.Equal(3, page2.Items.Count);
        Assert.Equal(1, (await Stock(s => s.SearchMovementsAsync(new MovementQuery(WarehouseId: w, LocationCode: "a-02"), Ct))).TotalCount);
        var byDoc = await Stock(s => s.SearchMovementsAsync(new MovementQuery(DocNo: inboundNo.ToLowerInvariant()), Ct));
        Assert.Equal(2, byDoc.TotalCount);
        Assert.All(byDoc.Items, m => Assert.Equal(inbound, m.DocumentId));
        Assert.Equal(23, (await Stock(s => s.SearchMovementsAsync(new MovementQuery(ItemCode: item, ActorName: Operator.Name[2..]), Ct))).TotalCount);
        Assert.Equal(0, (await Stock(s => s.SearchMovementsAsync(new MovementQuery(ItemCode: item, From: DateTimeOffset.UtcNow.AddMinutes(5)), Ct))).TotalCount);
        Assert.Equal(23, (await Stock(s => s.SearchMovementsAsync(
            new MovementQuery(ItemCode: item, From: DateTimeOffset.UtcNow.AddHours(-1), To: DateTimeOffset.UtcNow.AddMinutes(1)), Ct))).TotalCount);
        _ = code;
    }

    // T-M4-05-02：被拒絕的過帳嘗試在交易回滾後仍留有紀錄（含錯誤碼、單號）；單據取消、退回、沖銷出現在事件清單
    [Fact]
    public async Task 被拒的過帳有紀錄_取消與沖銷出現在事件()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync();
        var outbound = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 5m));
        var docNo = (await Docs(s => s.GetAsync(outbound, Ct)))!.DocNo;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(outbound, withFailureLog: true));
        Assert.Equal("stock.insufficient", ex.ErrorCode);
        var failures = await Anomalies(s => s.GetPostingFailuresAsync(1, Ct));
        var row = Assert.Single(failures.Items, f => f.DocNo == docNo);
        Assert.Equal(("document.confirm", "stock.insufficient", Operator.Name), (row.Command, row.ErrorCode, row.ActorName));

        await Docs(async s => await s.CancelAsync(outbound, await VersionAsync(outbound), Operator, Ct));
        var inbound = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 1m));
        await ConfirmAsync(inbound);
        await Docs(s => s.CreateReversalAsync(inbound, Operator, Ct));
        var events = await Anomalies(s => s.GetDocumentEventsAsync(1, Ct));
        Assert.Contains(events.Items, e => e.DocumentId == outbound && e.Action == "document.cancel");
        Assert.Contains(events.Items, e => e.Action == "document.reversal_create" && e.ActorName == Operator.Name);
    }

    // T-M4-05-03：久未處理：草稿超過 7 天、預留超過 30 天或 3 天內到期
    [Fact]
    public async Task 久未處理_草稿超過7天_預留快到期()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync();
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 10m)));
        var old = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 1m));
        var fresh = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 1m));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE stock_documents SET created_at = now() - interval '8 days' WHERE id = {old}", Ct);
        }
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var reservations = new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System);
            await reservations.CreateAsync(new ReservationInput(item, w, 1m, "快到期", DateTimeOffset.UtcNow.AddDays(1)), "key-" + Guid.NewGuid(), Operator, Ct);
        }

        var stale = await AllAsync(p => Anomalies(s => s.GetStaleDocumentsAsync(p, Ct)));
        Assert.Contains(stale, d => d.Id == old);
        Assert.DoesNotContain(stale, d => d.Id == fresh);
        var expiring = await AllAsync(p => Anomalies(s => s.GetStaleReservationsAsync(p, Ct)));
        Assert.Contains(expiring, r => r.ItemCode == item && r.ExpiresAt is not null);
    }

    // ---- 輔助 ----

    private static async Task<List<T>> AllAsync<T>(Func<int, Task<PagedResult<T>>> page)
    {
        var all = new List<T>();
        for (var p = 1; ; p++)
        {
            var result = await page(p);
            all.AddRange(result.Items);
            if (p * result.PageSize >= result.TotalCount)
            {
                return all;
            }
        }
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "H" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "歷史測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return (id, code);
    }

    private static async Task<string> ItemCodeAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("歷史物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<Guid> DocAsync(StockDocumentType type, Guid warehouse, Guid purpose, params (string Location, string Item, decimal Qty)[] lines)
    {
        var doc = await Docs(s => s.CreateAsync(type, warehouse, new StockDocumentHeader(purpose, null, null, null), Operator, Ct));
        foreach (var (location, item, qty) in lines)
        {
            await Docs(s => s.AddLineAsync(doc, new DocumentLineInput(location, item, qty), Operator, Ct));
        }
        return doc;
    }

    private static async Task ConfirmAsync(Guid doc, bool withFailureLog = false)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var version = await VersionAsync(doc);
        IPostingFailureLog? failures = withFailureLog ? new PostingFailureLog(new TestScopes(), TimeProvider.System, NullLogger<PostingFailureLog>.Instance) : null;
        await new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System, failures)
            .ConfirmAsync(doc, version, "key-" + Guid.NewGuid(), Operator, Ct);
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
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

    private static async Task<T> Anomalies<T>(Func<IStockAnomalyService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockAnomalyService(db, TimeProvider.System));
    }

    /// <summary>給 <see cref="PostingFailureLog"/> 用的最小 scope 工廠：每個 scope 一個新的 DbContext。</summary>
    private sealed class TestScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => MigratedDatabaseFixture.CreateContext());
            return services.BuildServiceProvider().CreateScope();
        }
    }
}
