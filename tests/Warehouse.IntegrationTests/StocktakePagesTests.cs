using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
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
using Warehouse.Web.Security;

namespace Warehouse.IntegrationTests;

/// <summary>M6-02 盤點畫面（T-M6-02-20～21）：表單開始、計數、送審、核准；盲盤畫面不出現帳面量；倉庫範圍。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class StocktakePagesTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Actor Setup = new(Guid.NewGuid(), "畫面測試準備", true, IsWarehouseManager: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-02-20：主管開始 → 作業員計數（盲盤看不到帳面 37）→ 送審 → 另一主管核准 → 結存與調整單
    [Fact]
    public async Task 網頁流程_開始計數送審核准()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w, item) = await PrepareAsync(37m);
        var manager = await LoginAsync(factory, Roles.WarehouseManager);
        var start = await manager.PostFormAsync("/Stocktakes", "/Stocktakes?handler=Start", new Dictionary<string, string>
        {
            ["Start.WarehouseId"] = w.ToString(), ["Start.LocationCodes"] = "A-01", ["Start.RequestId"] = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var url = start.Headers.Location!.OriginalString;
        Assert.StartsWith("/Stocktakes/Details/", url);

        var counter = await LoginAsync(factory, Roles.Operator);
        var page = await counter.GetStringAsync(url);
        Assert.DoesNotContain(">37<", page); // 盲盤：作業員畫面沒有帳面量
        var count = await counter.PostFormAsync(url, url + "?handler=Count", new Dictionary<string, string>
        {
            ["Count.LocationCode"] = "A-01", ["Count.ItemCode"] = item, ["Count.Quantity"] = "35",
        });
        Assert.Equal(HttpStatusCode.Redirect, count.StatusCode);
        Assert.Contains("Loc=A-01", count.Headers.Location!.OriginalString); // 下一筆保留儲位
        // 表格的「存」送出空白：不能當成 0（審查 1）
        await using (var check = MigratedDatabaseFixture.CreateContext())
        {
            var stId = Guid.Parse(url["/Stocktakes/Details/".Length..]);
            var lineId = await check.Set<StocktakeLine>().Where(l => l.StocktakeId == stId).Select(l => l.Id).SingleAsync(Ct);
            var empty = await counter.PostFormAsync(url, url + "?handler=CountLine&lineId=" + lineId, new Dictionary<string, string> { ["quantity"] = "" });
            Assert.Contains("請輸入實盤量", await empty.Content.ReadAsStringAsync(Ct));
            Assert.Equal(35m, await check.Set<StocktakeLine>().Where(l => l.Id == lineId).Select(l => l.CountedQuantity).SingleAsync(Ct));
        }
        var version = VersionPattern().Match(await counter.GetStringAsync(url)).Groups[1].Value;
        var submit = await counter.PostFormAsync(url, url + "?handler=Submit", new Dictionary<string, string> { ["Version"] = version });
        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);

        var approver = await LoginAsync(factory, Roles.WarehouseManager);
        var review = await approver.GetStringAsync(url);
        Assert.Contains(">-2<", review); // 主管看到差異
        var form = ApproveFormPattern().Match(review);
        var approve = await approver.PostFormAsync(url, url + "?handler=Approve", new Dictionary<string, string>
        {
            ["Version"] = form.Groups[1].Value, ["IdempotencyKey"] = form.Groups[2].Value,
        });
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        Assert.Contains("ADJ-", await approver.GetStringAsync(url));

        // 作業員不能核准（政策擋下）
        var denied = await counter.PostFormAsync(url, url + "?handler=Approve", new Dictionary<string, string> { ["Version"] = "0", ["IdempotencyKey"] = Guid.NewGuid().ToString() });
        Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);

        await using var db = MigratedDatabaseFixture.CreateContext();
        var balance = await (from b in db.StockBalances join i in db.Items on b.ItemId equals i.Id
                             join l in db.Locations on b.LocationId equals l.Id
                             where i.ItemCode == item && l.WarehouseId == w select b.Quantity).SumAsync(Ct);
        Assert.Equal(35m, balance);
    }

    // T-M6-02-21：只能作業其他倉庫的主管不能在這個倉庫開始盤點（warehouse.forbidden）
    [Fact]
    public async Task 倉庫範圍外不能開始盤點()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w, _) = await PrepareAsync(1m);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var id = await WebSession.CreateUserAsync(factory, name, Password, Roles.WarehouseManager);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>().SetWarehousesAsync(id, false, [], UserCommands.CommandLineActor, Ct);
        }
        var web = WebSession.Create(factory);
        await web.LoginAsync(name, Password);
        var response = await web.PostFormAsync("/Stocktakes", "/Stocktakes?handler=Start", new Dictionary<string, string>
        {
            ["Start.WarehouseId"] = w.ToString(), ["Start.RequestId"] = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("作業權限", await response.Content.ReadAsStringAsync(Ct));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.False(await db.Stocktakes.AnyAsync(s => s.WarehouseId == w, Ct));
    }

    private static async Task<WebSession> LoginAsync(WarehouseWebFactory factory, string role)
    {
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, role);
        var web = WebSession.Create(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await web.LoginAsync(name, Password)).StatusCode);
        return web;
    }

    private static async Task<(Guid Warehouse, string Item)> PrepareAsync(decimal quantity)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var warehouses = new WarehouseService(db, TimeProvider.System);
        var w = await warehouses.CreateWarehouseAsync(new WarehouseInput("P" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant(), "畫面盤點倉"), Setup, Ct);
        await warehouses.CreateLocationAsync(w, new LocationInput("A-01", new LocationDetails(null, null, null, null, null, null, null, null, false, false)), Setup, Ct);
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var draft = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft("畫面盤點物料 " + Guid.NewGuid().ToString("N")[..6], El, Pcs, TrackingMode.None, null, null, null), Setup, Ct);
        var version = await db.Items.Where(i => i.Id == draft.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(draft.ItemId, version, [], null, Setup, Ct);
        var item = await db.Items.Where(i => i.Id == draft.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
        var docs = new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);
        var doc = await docs.CreateAsync(StockDocumentType.Inbound, w, new StockDocumentHeader(PurchaseIn, null, null, null), Setup, Ct);
        await docs.AddLineAsync(doc, new DocumentLineInput("A-01", item, quantity), Setup, Ct);
        await docs.ConfirmAsync(doc, (await docs.GetAsync(doc, Ct))!.Version, "pg-" + Guid.NewGuid(), Setup, Ct);
        return (w, item);
    }

    [GeneratedRegex("name=\"Version\" value=\"(\\d+)\"")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("handler=Approve[^>]*>\\s*<input type=\"hidden\" name=\"Version\" value=\"(\\d+)\" />\\s*<input type=\"hidden\" name=\"IdempotencyKey\" value=\"([^\"]+)\"")]
    private static partial Regex ApproveFormPattern();
}
