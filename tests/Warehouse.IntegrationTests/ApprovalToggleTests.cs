using System.Net;
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
/// M12 審核開關與使用模式（ADR-0027，T-M12-01～）：物料、期初、盤點可關閉審核，真實 PostgreSQL。
/// 審核開關是全域設定：每個測試在 finally 套回「標準」（＝系統預設）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ApprovalToggleTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "審核開關作業員", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "審核開關主管", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "審核開關物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M12-01：模式套用只改審核相關 8 項；標準＝系統預設；逐項改過就不符合任何模式
    [Fact]
    public void 使用模式_套用與比對()
    {
        var defaults = new SystemSettings(false, true, true, true) { PrintCompanyName = "甲公司", RememberMeDays = 30 };
        Assert.Equal(UsageMode.Standard, UsageModes.Match(defaults));

        var simple = UsageModes.Apply(defaults, UsageMode.Simple);
        Assert.Equal(UsageMode.Simple, UsageModes.Match(simple));
        Assert.False(simple.ItemApprovalEnabled || simple.OpeningApprovalEnabled || simple.StocktakeApprovalEnabled || simple.DocumentApprovalEnabled);
        Assert.True(simple.AllowSelfApproveOpening && simple.AllowSelfApproveStocktake && simple.AllowSelfApproveDocuments);
        Assert.False(simple.StocktakeBlindCount);
        Assert.Equal(("甲公司", 30), (simple.PrintCompanyName, simple.RememberMeDays)); // 其他設定不動

        var strict = UsageModes.Apply(simple, UsageMode.Strict);
        Assert.True(strict.DocumentApprovalEnabled && strict.ItemApprovalEnabled && strict.StocktakeBlindCount);
        Assert.False(strict.AllowSelfApproveDocuments);
        Assert.Null(UsageModes.Match(strict with { StocktakeBlindCount = false }));
    }

    // T-M12-02：物料審核關閉 → 作業員提出即啟用；疑似重複維持待核准；物料管理員確認過候選就啟用
    [Fact]
    public async Task 物料審核關閉_提出即啟用_疑似重複維持待核准()
    {
        var name = "審核關閉物料 " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await SetAsync(s => s with { ItemApprovalEnabled = false });

            var first = await Items(s => s.SubmitAsync(Guid.NewGuid(), Draft(name), ItemSubmitOptions.None, Operator, Ct));
            Assert.NotNull(first.ItemCode);
            Assert.Equal(ItemStatus.Active, await StatusAsync(first.ItemId));

            var duplicate = await Items(s => s.SubmitAsync(Guid.NewGuid(), Draft(name), ItemSubmitOptions.None, Operator, Ct));
            Assert.Null(duplicate.ItemCode);
            Assert.Equal(ItemStatus.PendingApproval, await StatusAsync(duplicate.ItemId));

            // 作業員勾選「不是重複」不算數；物料管理員確認過的候選才放行
            var byOperator = await Items(s => s.SubmitAsync(Guid.NewGuid(), Draft(name), new ItemSubmitOptions([], false, [first.ItemId, duplicate.ItemId]), Operator, Ct));
            Assert.Null(byOperator.ItemCode);
            var acknowledged = await Items(s => s.SubmitAsync(Guid.NewGuid(), Draft(name),
                new ItemSubmitOptions([], false, [first.ItemId, duplicate.ItemId, byOperator.ItemId]), ItemManager, Ct));
            Assert.NotNull(acknowledged.ItemCode);
        }
        finally
        {
            await SetAsync(s => UsageModes.Apply(s, UsageMode.Standard));
        }
    }

    // T-M12-07：物料審核關閉時兩人同時提出同名物料 → 排隊後查重複，只有一筆直接啟用
    [Fact]
    public async Task 物料審核關閉_同時提出同名_只有一筆啟用()
    {
        var name = "同時提出物料 " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await SetAsync(s => s with { ItemApprovalEnabled = false });
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                Items(s => s.SubmitAsync(Guid.NewGuid(), Draft(name), ItemSubmitOptions.None, Operator, Ct))));
            Assert.Single(results, r => r.ItemCode is not null);
        }
        finally
        {
            await SetAsync(s => UsageModes.Apply(s, UsageMode.Standard));
        }
    }

    // T-M12-03：物料審核開啟（預設）時，作業員提出仍是待核准
    [Fact]
    public async Task 物料審核開啟_作業員提出維持待核准()
    {
        var result = await Items(s => s.SubmitAsync(Guid.NewGuid(), Draft("審核開啟物料 " + Guid.NewGuid().ToString("N")[..8]),
            ItemSubmitOptions.None, Operator, Ct));
        Assert.Null(result.ItemCode);
        Assert.Equal(ItemStatus.PendingApproval, await StatusAsync(result.ItemId));
    }

    // T-M12-04：期初審核關閉 → 建立者確認即過帳、送審被拒、同鍵重送只過帳一次；別人的草稿不能確認；
    // 開啟後確認被拒；關閉前已送審的單仍由主管核准
    [Fact]
    public async Task 期初審核關閉_確認過帳_冪等_開啟時拒絕確認()
    {
        var warehouse = await WarehouseAsync("C-01", "C-02", "C-03");
        var item = await ItemCodeAsync();
        var submitted = await OpeningDraftAsync(warehouse, "C-03", item, 2m);
        await Opening(async s => await s.SubmitAsync(submitted, await DocVersionAsync(submitted), Operator, Ct));
        var doc = await OpeningDraftAsync(warehouse, "C-01", item, 5m);
        try
        {
            await SetAsync(s => s with { OpeningApprovalEnabled = false });

            Assert.Equal("opening.approval_disabled", await TryAsync(async () =>
                await Opening(async s => await s.SubmitAsync(doc, await DocVersionAsync(doc), Operator, Ct))));
            var other = new Actor(Guid.NewGuid(), "別的作業員", false);
            Assert.Equal("opening.permission_denied", await TryAsync(async () =>
                await Opening(async s => await s.ConfirmAsync(doc, await DocVersionAsync(doc), "k-" + Guid.NewGuid(), other, Ct))));

            var version = await DocVersionAsync(doc);
            var key = "k-" + Guid.NewGuid();
            await Opening(s => s.ConfirmAsync(doc, version, key, Operator, Ct));
            await Opening(s => s.ConfirmAsync(doc, version, key, Operator, Ct)); // 重送：不重複過帳
            var details = (await Opening(s => s.GetAsync(doc, Ct)))!;
            Assert.Equal((StockDocumentStatus.Posted, Operator.Name), (details.Status, details.ApprovedByName));
            Assert.Equal(5m, await BalanceAsync(warehouse, "C-01", item));

            // 已送審的單：審核關閉後仍由主管核准
            await Opening(async s => await s.ApproveAsync(submitted, await DocVersionAsync(submitted), "k-" + Guid.NewGuid(), Manager, Ct));
            Assert.Equal(2m, await BalanceAsync(warehouse, "C-03", item));

            await SetAsync(s => s with { OpeningApprovalEnabled = true });
            var later = await OpeningDraftAsync(warehouse, "C-02", item, 1m);
            Assert.Equal("opening.approval_required", await TryAsync(async () =>
                await Opening(async s => await s.ConfirmAsync(later, await DocVersionAsync(later), "k-" + Guid.NewGuid(), Operator, Ct))));
            Assert.Equal(0m, await BalanceAsync(warehouse, "C-02", item));
        }
        finally
        {
            await SetAsync(s => UsageModes.Apply(s, UsageMode.Standard));
        }
    }

    // T-M12-05：盤點審核關閉 → 不套用盲盤、送審被拒、確認即產生調整單過帳且同鍵重送不重複；開啟後確認被拒
    [Fact]
    public async Task 盤點審核關閉_確認即調整_不盲盤_開啟時拒絕確認()
    {
        var warehouse = await WarehouseAsync("D-01");
        var item = await ItemCodeAsync();
        var opening = await OpeningDraftAsync(warehouse, "D-01", item, 10m);
        await Opening(async s => await s.SubmitAsync(opening, await DocVersionAsync(opening), Operator, Ct));
        await Opening(async s => await s.ApproveAsync(opening, await DocVersionAsync(opening), "k-" + Guid.NewGuid(), Manager, Ct));
        try
        {
            await SetAsync(s => s with { StocktakeApprovalEnabled = false, StocktakeBlindCount = true });
            var st = await Stocktakes(s => s.StartAsync(warehouse, [], null, Manager, Ct));
            await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("D-01", item, 7m), Operator, Ct));

            var line = (await Stocktakes(s => s.GetAsync(st, new StocktakeLineFilter(), Operator, Ct)))!.Lines.Items.Single();
            Assert.Equal((10m, -3m), (line.BookQuantityAtCount, line.Difference)); // 盲盤開著也看得到帳面量
            Assert.Equal("stocktake.approval_disabled", await TryAsync(async () =>
                await Stocktakes(async s => await s.SubmitAsync(st, await StocktakeVersionAsync(st), Operator, Ct))));

            // 開頁後別人改了實盤量（表頭版本不變）→ 確認被拒，不會過帳確認者沒看過的差異
            var stale = (await Stocktakes(s => s.GetAsync(st, new StocktakeLineFilter(), Operator, Ct)))!.CountStamp;
            var other = new Actor(Guid.NewGuid(), "另一位盤點員", false);
            await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("D-01", item, 1m), other, Ct));
            Assert.Equal("stocktake.counts_changed", await TryAsync(async () =>
                await Stocktakes(async s => await s.ConfirmAsync(st, await StocktakeVersionAsync(st), stale, "k-" + Guid.NewGuid(), Operator, Ct))));
            Assert.Equal(10m, await BalanceAsync(warehouse, "D-01", item));
            await Stocktakes(s => s.CountAsync(st, new StocktakeCountInput("D-01", item, 7m), Operator, Ct));

            var version = await StocktakeVersionAsync(st);
            var stamp = await StampAsync(st);
            var key = "k-" + Guid.NewGuid();
            var adjustment = await Stocktakes(s => s.ConfirmAsync(st, version, stamp, key, Operator, Ct));
            Assert.NotNull(adjustment);
            Assert.Equal(adjustment, await Stocktakes(s => s.ConfirmAsync(st, version, stamp, key, Operator, Ct))); // 重送回傳同一張調整單
            Assert.Equal(7m, await BalanceAsync(warehouse, "D-01", item));
            var details = (await Stocktakes(s => s.GetAsync(st, new StocktakeLineFilter(), Manager, Ct)))!;
            Assert.Equal(StocktakeStatus.Approved, details.Status);

            await SetAsync(s => s with { StocktakeApprovalEnabled = true });
            var next = await Stocktakes(s => s.StartAsync(warehouse, [], null, Manager, Ct));
            await Stocktakes(s => s.CountAsync(next, new StocktakeCountInput("D-01", item, 6m), Operator, Ct));
            Assert.Null((await Stocktakes(s => s.GetAsync(next, new StocktakeLineFilter(), Operator, Ct)))!.Lines.Items.Single().BookQuantityAtCount);
            Assert.Equal("stocktake.approval_required", await TryAsync(async () =>
                await Stocktakes(async s => await s.ConfirmAsync(next, await StocktakeVersionAsync(next), await StampAsync(next), "k-" + Guid.NewGuid(), Operator, Ct))));
            Assert.Equal(7m, await BalanceAsync(warehouse, "D-01", item));
            await Stocktakes(async s => await s.CancelAsync(next, await StocktakeVersionAsync(next), Manager, Ct));
        }
        finally
        {
            await SetAsync(s => UsageModes.Apply(s, UsageMode.Standard));
        }
    }

    // T-M12-06：設定頁「套用簡易」只填表單、不儲存；按儲存後才生效
    [Fact]
    public async Task 設定頁套用模式_只填表單不儲存()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, Roles.SystemAdmin);
        var admin = WebSession.Create(factory);
        await admin.LoginAsync(name, Password);
        try
        {
            var page = await admin.GetStringAsync("/Settings");
            Assert.Contains("目前：<strong>標準</strong>", page);

            var response = await admin.PostFormAsync("/Settings", "/Settings?handler=Mode&mode=Simple", StandardForm());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains("還沒儲存", html);
            Assert.Contains("目前：<strong>簡易（小公司）</strong>", html);
            Assert.Contains("name=\"Form.ItemApprovalEnabled\" value=\"true\" id=\"setting-item-approval\"", html); // 未勾選
            Assert.Equal(UsageMode.Standard, UsageModes.Match(await GetSettingsAsync()));
        }
        finally
        {
            await SetAsync(s => UsageModes.Apply(s, UsageMode.Standard));
        }
    }

    // ---- 輔助 ----

    /// <summary>預設值的設定表單（Form 與 Original 相同）。</summary>
    private static Dictionary<string, string> StandardForm()
    {
        var fields = new Dictionary<string, string>();
        foreach (var prefix in new[] { "Form", "Original" })
        {
            fields[$"{prefix}.ItemApprovalEnabled"] = "true";
            fields[$"{prefix}.OpeningApprovalEnabled"] = "true";
            fields[$"{prefix}.StocktakeApprovalEnabled"] = "true";
            fields[$"{prefix}.StocktakeBlindCount"] = "true";
            fields[$"{prefix}.RememberMeDays"] = "14";
        }
        return fields;
    }

    private static ItemDraft Draft(string name) => new(name, El, Pcs, TrackingMode.None, null, null, null);

    private static async Task<ItemStatus> StatusAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => i.Status).SingleAsync(Ct);
    }

    private static async Task<string> ItemCodeAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(), Draft("審核開關物料 " + Guid.NewGuid().ToString("N")[..8]), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<Guid> WarehouseAsync(params string[] locations)
    {
        var code = "M" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "審核開關測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return id;
    }

    private static async Task<Guid> OpeningDraftAsync(Guid warehouse, string location, string itemCode, decimal qty)
    {
        var doc = await Opening(s => s.CreateAsync(warehouse, null, Operator, Ct));
        await Opening(s => s.AddLineAsync(doc, new OpeningLineInput(location, itemCode, null, null, null, qty), Operator, Ct));
        return doc;
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

    private static async Task<uint> DocVersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<uint> StocktakeVersionAsync(Guid st)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Stocktakes.Where(s => s.Id == st).Select(s => EF.Property<uint>(s, "Version")).SingleAsync(Ct);
    }

    private static async Task<string> StampAsync(Guid st) =>
        (await Stocktakes(s => s.GetAsync(st, new StocktakeLineFilter(), Operator, Ct)))!.CountStamp;

    private static async Task<SystemSettings> GetSettingsAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await new SettingsService(db, TimeProvider.System).GetAsync(Ct);
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
    }

    private static async Task<T> Items<T>(Func<IItemWorkflowService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    private static async Task<T> Opening<T>(Func<IOpeningService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Opening(Func<IOpeningService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
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
}
