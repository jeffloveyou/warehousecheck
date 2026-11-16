using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Imports;
using Warehouse.Application.Items;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Imports;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>R4 維護入口：完整問題下載、標籤內容與工作首頁資料隔離。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public sealed class UsabilityMaintenanceTests
{
    private const string Password = "Test-pass-123";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 完整匯入報告超過500筆_保留列號防公式_限本人與管理員()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (owner, actor) = await LoginAsync(factory, Roles.Operator);
        var (other, stranger) = await LoginAsync(factory, Roles.Operator);
        var (admin, _) = await LoginAsync(factory, Roles.SystemAdmin);
        var id = Guid.NewGuid();
        var issues = Enumerable.Range(2, 601).Select(n => new ImportIssue(n,
            n == 602 ? "=HYPERLINK(\"bad\")" : "測試問題 " + n, true)).ToList();
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            db.ImportBatches.Add(new ImportBatch
            {
                Id = id, Kind = ImportKind.Items, Status = ImportStatus.Validated, FileName = "issues.csv",
                FileHash = new string('A', 64), RowCount = 601, ErrorCount = 601,
                IssuesJson = JsonSerializer.Serialize(issues), CreatedById = actor.Id, CreatedByName = actor.Name,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(Ct);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
            Assert.Equal(500, (await imports.GetAsync(id, actor, false, 500, Ct))!.Issues.Count);
            var all = new List<ImportIssue>();
            await foreach (var issue in imports.ReadIssuesAsync(id, actor, false, Ct)) all.Add(issue);
            Assert.Equal(issues, all);
            var leaked = new List<ImportIssue>();
            await foreach (var issue in imports.ReadIssuesAsync(id, stranger, false, Ct)) leaked.Add(issue);
            Assert.Empty(leaked);
        }
        var url = $"/Import/Details/{id}?handler=Issues";
        var response = await owner.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(602, csv.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("602,錯誤,\"'=HYPERLINK(\"\"bad\"\")\"", csv);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task 物料標籤沿用正式QR_只列啟用且選定物料()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (web, actor) = await LoginAsync(factory, Roles.ItemManager);
        await using var scope = factory.Services.CreateAsyncScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IItemWorkflowService>();
        var items = scope.ServiceProvider.GetRequiredService<IItemQueryService>();
        var category = SeedData.Categories.Single(c => c.Code == "EL").Id;
        var unit = SeedData.Units.Single(u => u.Code == "PCS").Id;
        var created = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft("補印" + Guid.NewGuid().ToString("N"),
            category, unit, TrackingMode.None, null, null, "測試規格"), actor, Ct);
        var detail = (await items.GetAsync(created.ItemId, Ct))!;
        if (detail.Status != ItemStatus.Active)
            await workflow.ApproveAsync(detail.Id, detail.Version, [], null, actor, Ct);
        detail = (await items.GetAsync(created.ItemId, Ct))!;
        var labels = await items.LabelsAsync([detail.Id], Ct);
        Assert.Equal(detail.ItemCode, Assert.Single(labels).ItemCode);
        var response = await web.PostFormAsync("/Items/Labels", "/Items/Labels?handler=Print",
            new Dictionary<string, string> { ["SelectedIds"] = detail.Id.ToString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);
        var qr = scope.ServiceProvider.GetRequiredService<IQrCodeRenderer>().RenderSvg("WH:ITEM:" + detail.ItemCode);
        Assert.Contains(qr, html);
        Assert.Contains("測試規格", html);
        await workflow.DeactivateAsync(detail.Id, detail.Version, "標籤停用排除測試", actor, Ct);
        Assert.Empty(await items.LabelsAsync([detail.Id], Ct));
        Assert.Empty(await items.LabelsAsync(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), Ct));
    }

    [Fact]
    public async Task 作業首頁只列自己的購物車_唯讀角色不顯示作業待辦()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (owner, actor) = await LoginAsync(factory, Roles.Operator);
        var (_, other) = await LoginAsync(factory, Roles.Operator);
        var (viewer, _) = await LoginAsync(factory, Roles.Viewer);
        var ownNote = "本人購物車" + Guid.NewGuid().ToString("N");
        var otherNote = "他人購物車" + Guid.NewGuid().ToString("N");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var carts = scope.ServiceProvider.GetRequiredService<IScanCartService>();
            await carts.CreateAsync(ownNote, actor, Ct);
            await carts.CreateAsync(otherNote, other, Ct);
        }
        var home = await owner.GetStringAsync("/");
        Assert.Contains(ownNote, home);
        Assert.DoesNotContain(otherNote, home);
        Assert.Contains("id=\"home-returned\"", home);
        var readOnly = await viewer.GetStringAsync("/");
        Assert.DoesNotContain("id=\"home-carts\"", readOnly);
        Assert.DoesNotContain("id=\"home-approvals\"", readOnly);
    }

    [Fact]
    public async Task 儲位標籤可只補印勾選項目_外來識別碼拒絕產生()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (web, actor) = await LoginAsync(factory, Roles.WarehouseManager);
        await using var scope = factory.Services.CreateAsyncScope();
        var warehouses = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
        var code = "L" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var warehouse = await warehouses.CreateWarehouseAsync(new(code, "選取標籤倉"), actor, Ct);
        var details = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
        var first = await warehouses.CreateLocationAsync(warehouse, new("A-01", details), actor, Ct);
        await warehouses.CreateLocationAsync(warehouse, new("A-02", details), actor, Ct);
        var url = $"/Warehouses/Labels?WarehouseId={warehouse}&PrintSelected=true&SelectedIds={first}";
        var html = await web.GetStringAsync(url);
        Assert.DoesNotContain("此次沒有產生標籤", html);
        Assert.DoesNotContain("選取格式不正確", html);
        Assert.DoesNotContain("沒有符合條件的儲位", html);
        var qr = scope.ServiceProvider.GetRequiredService<IQrCodeRenderer>();
        Assert.Contains(qr.RenderSvg($"WH:LOC:{code}:A-01"), html);
        Assert.DoesNotContain(qr.RenderSvg($"WH:LOC:{code}:A-02"), html);
        var invalid = await web.GetStringAsync($"/Warehouses/Labels?WarehouseId={warehouse}&PrintSelected=true&SelectedIds={Guid.NewGuid()}");
        Assert.Contains("此次沒有產生標籤", invalid);
        Assert.DoesNotContain("<svg", invalid);
    }

    private static async Task<(WebSession Web, Actor Actor)> LoginAsync(WarehouseWebFactory factory, string role)
    {
        var name = "u" + Guid.NewGuid().ToString("N")[..12];
        var id = await WebSession.CreateUserAsync(factory, name, Password, role);
        var web = WebSession.Create(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await web.LoginAsync(name, Password)).StatusCode);
        return (web, new Actor(id, name, role == Roles.ItemManager, role == Roles.WarehouseManager));
    }
}
