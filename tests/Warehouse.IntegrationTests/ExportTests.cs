using System.Net;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Errors;
using Warehouse.Application.Exports;
using Warehouse.Application.Items;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Web;

namespace Warehouse.IntegrationTests;

/// <summary>M6-03 Excel 匯出（T-M6-03-01～05）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ExportTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "匯出測試物料管理員", IsItemManager: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-03-01：物料清單匯出：Excel 可開、標題列、條件與清單相同、中文與控制字元正確處理
    [Fact]
    public async Task 物料匯出_內容與篩選()
    {
        var token = "匯出" + Guid.NewGuid().ToString("N")[..6];
        var code = await ItemAsync(token + " 伺服\u0001馬達");
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var web = await LoginAsync(factory, Roles.Operator);
        var response = await web.Client.GetAsync("/Items?handler=Export&q=" + Uri.EscapeDataString(token), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExportFile.ContentType, response.Content.Headers.ContentType!.MediaType);
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync(Ct));
        var sheet = workbook.Worksheet(1);
        Assert.Equal("料號", sheet.Cell(1, 1).GetString());
        Assert.Equal(code, sheet.Cell(2, 1).GetString());
        Assert.Equal(token + " 伺服馬達", sheet.Cell(2, 3).GetString());
        Assert.Equal("有效", sheet.Cell(2, 10).GetString());
        Assert.True(sheet.Cell(3, 1).IsEmpty());
    }

    // T-M6-03-02：超過 50,000 筆 → export.too_large，不截斷
    [Fact]
    public async Task 超過上限_拒絕匯出()
    {
        var action = "test.bulk_" + Guid.NewGuid().ToString("N")[..8];
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO audit_logs (id, occurred_at, actor_id, actor_name, action, entity_type, entity_id)
                SELECT gen_random_uuid(), now(), '00000000-0000-0000-0000-000000000000', '大量', {action}, 'test', g::text
                FROM generate_series(1, {IExportService.MaxRows + 1}) g
                """, Ct);
        }
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var export = scope.ServiceProvider.GetRequiredService<IExportService>();
        var now = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            export.AuditAsync(new AuditQuery(now.AddHours(-1), now.AddMinutes(1), ActionPrefix: action), Ct));
        Assert.Equal("export.too_large", error.ErrorCode);
        var ok = await export.AuditAsync(new AuditQuery(now.AddHours(-1), now.AddMinutes(1), ActionPrefix: action, EntityId: "7"), Ct);
        Assert.Equal(1, ok.Rows);

        // 網頁：回到原頁並顯示訊息
        var admin = await LoginAsync(factory, Roles.SystemAdmin);
        var page = await admin.Client.GetAsync("/Admin/Audit?handler=Export&ActionPrefix=" + action, Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("超過", await admin.GetStringAsync(page.Headers.Location!.OriginalString));
    }

    // T-M6-03-03：同時匯出數已滿 → 429，釋放後可再匯出
    [Fact]
    public async Task 同時匯出數限制()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Export:MaxConcurrent"] = "1" }).Build();
        using var gate = new ExportGate(config);
        var release = new TaskCompletionSource();
        var page = new TestPage();
        var first = gate.RunAsync(page, async () =>
        {
            await release.Task;
            return new ExportFile(new MemoryStream([1]), "a.xlsx", 0);
        }, "/");
        var second = await gate.RunAsync(page, () => Task.FromResult(new ExportFile(new MemoryStream([1]), "b.xlsx", 0)), "/");
        Assert.Equal(429, ((ContentResult)second).StatusCode);
        release.SetResult();
        Assert.IsType<FileStreamResult>(await first);
        Assert.IsType<FileStreamResult>(await gate.RunAsync(page, () => Task.FromResult(new ExportFile(new MemoryStream([1]), "c.xlsx", 0)), "/"));
    }

    // T-M6-03-04：權限：只讀使用者可匯出物料、不能匯出操作日誌；結存匯出沒有條件時要求縮小
    [Fact]
    public async Task 匯出權限與必要條件()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var viewer = await LoginAsync(factory, Roles.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync("/Items?handler=Export&q=zzzz-none", Ct)).StatusCode);
        var audit = await viewer.Client.GetAsync("/Admin/Audit?handler=Export", Ct);
        Assert.StartsWith("/Account/AccessDenied", audit.Headers.Location!.OriginalString);
        var stock = await viewer.Client.GetAsync("/Stock?handler=Export", Ct);
        Assert.Equal(HttpStatusCode.Redirect, stock.StatusCode);
        Assert.Contains("請先指定", await viewer.GetStringAsync(stock.Headers.Location!.OriginalString));
    }

    // T-M6-03-05：公式樣式的文字仍是文字；超長字串截斷並標記；委派例外時同時數會釋放、權限錯誤不轉成訊息
    [Fact]
    public async Task 文字安全與Gate例外路徑()
    {
        var token = "公式" + Guid.NewGuid().ToString("N")[..6];
        await ItemAsync("=HYPERLINK(\"http://x\",\"" + token + "\")");
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var web = await LoginAsync(factory, Roles.Operator);
        var response = await web.Client.GetAsync("/Items?handler=Export&q=" + Uri.EscapeDataString(token), Ct);
        using (var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync(Ct)))
        {
            var cell = workbook.Worksheet(1).Cell(2, 3);
            Assert.False(cell.HasFormula);
            Assert.StartsWith("=HYPERLINK", cell.GetString());
        }
        var longText = Warehouse.Infrastructure.Exports.XlsxWriter.Clean(new string('長', 40_000) + "￿");
        Assert.Equal(32_767, longText.Length);
        Assert.EndsWith("…(截斷)", longText);
        Assert.Equal("ab", Warehouse.Infrastructure.Exports.XlsxWriter.Clean("a\u0001￿b"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Export:MaxConcurrent"] = "1" }).Build();
        using var gate = new ExportGate(config);
        var page = new TestPage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(page, () => throw new InvalidOperationException(), "/"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => gate.RunAsync(page, () => throw new PermissionDeniedException("x", "y"), "/"));
        Assert.IsType<FileStreamResult>(await gate.RunAsync(page, () => Task.FromResult(new ExportFile(new MemoryStream([1]), "d.xlsx", 0)), "/"));
    }

    // T-M6-03-06：盲盤時作業員匯出盤點不含帳面量，主管匯出有；異動頁極端日期不會 500
    [Fact]
    public async Task 盤點匯出盲盤_與異動日期檢查()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (stocktakeId, _) = await StocktakeAsync(factory);
        var counter = await LoginAsync(factory, Roles.Operator);
        var manager = await LoginAsync(factory, Roles.WarehouseManager);
        static async Task<(string Snapshot, string Counted)> ReadAsync(WebSession session, Guid id)
        {
            var file = await session.Client.GetAsync($"/Stocktakes/Details/{id}?handler=Export", Ct);
            using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync(Ct));
            var sheet = workbook.Worksheet(1);
            return (sheet.Cell(2, 9).GetString(), sheet.Cell(2, 11).GetString());
        }
        Assert.Equal(("", "3"), await ReadAsync(counter, stocktakeId));
        Assert.Equal(("4", "3"), await ReadAsync(manager, stocktakeId));

        var extreme = await counter.Client.GetAsync("/Stock/Movements?From=0001-01-01&To=9999-12-31", Ct);
        Assert.Equal(HttpStatusCode.OK, extreme.StatusCode);
        Assert.Contains("超出可查詢範圍", await extreme.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<(Guid Stocktake, string Item)> StocktakeAsync(WarehouseWebFactory factory)
    {
        var manager = new Actor(Guid.NewGuid(), "匯出盤點主管", false, IsWarehouseManager: true);
        var item = await ItemAsync("匯出盤點物料 " + Guid.NewGuid().ToString("N")[..6]);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var warehouses = services.GetRequiredService<Warehouse.Application.Warehousing.IWarehouseService>();
        var w = await warehouses.CreateWarehouseAsync(new Warehouse.Application.Warehousing.WarehouseInput("E" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant(), "匯出倉"), manager, Ct);
        await warehouses.CreateLocationAsync(w, new Warehouse.Application.Warehousing.LocationInput("A-01",
            new Warehouse.Domain.Warehousing.LocationDetails(null, null, null, null, null, null, null, null, false, false)), manager, Ct);
        var docs = services.GetRequiredService<Warehouse.Application.Inventory.IStockDocumentService>();
        var purpose = SeedData.Purposes.Single(p => p.Direction == Warehouse.Domain.Inventory.PurposeDirection.In && p.Code == "PURCHASE").Id;
        var doc = await docs.CreateAsync(Warehouse.Domain.Inventory.StockDocumentType.Inbound, w,
            new Warehouse.Domain.Inventory.StockDocumentHeader(purpose, null, null, null), manager, Ct);
        await docs.AddLineAsync(doc, new Warehouse.Application.Inventory.DocumentLineInput("A-01", item, 4m), manager, Ct);
        await docs.ConfirmAsync(doc, (await docs.GetAsync(doc, Ct))!.Version, "ex-" + Guid.NewGuid(), manager, Ct);
        var stocktakes = services.GetRequiredService<Warehouse.Application.Inventory.IStocktakeService>();
        var st = await stocktakes.StartAsync(w, [], null, manager, Ct);
        await stocktakes.CountAsync(st, new Warehouse.Application.Inventory.StocktakeCountInput("A-01", item, 3m), manager, Ct);
        return (st, item);
    }

    private sealed class TestPage : PageModel;

    private static async Task<WebSession> LoginAsync(WarehouseWebFactory factory, string role)
    {
        var name = "x" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, role);
        var web = WebSession.Create(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await web.LoginAsync(name, Password)).StatusCode);
        return web;
    }

    private static async Task<string> ItemAsync(string name)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft(name, El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }
}
