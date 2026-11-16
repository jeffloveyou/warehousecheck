using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 公用帳號＋操作人（ADR-0030，T-SA-01～）：公用帳號送出進出倉動作必須從人員名單選操作人，
/// 紀錄寫成「操作人（帳號顯示名稱）」；一般帳號不受影響；API 寫入拒絕公用帳號。完整 Web 主程式。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class SharedAccountOperatorTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 快速結帳_公用帳號未選操作人拒絕_選了寫入組合姓名_領用人同名_停用人員不能選()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await SetApprovalAsync(factory, false);
        var (shared, sharedId, display) = await LoginAsync(factory, isShared: true);
        var (warehouseCode, warehouseId, itemCode) = await SeedAsync(factory);
        var person = await AddOperatorAsync(factory, "王" + Suffix());
        var retired = await AddOperatorAsync(factory, "李" + Suffix());
        await DeactivateOperatorAsync(factory, retired.Id);

        // 進倉：沒選操作人 → 400 user.operator_required，不建單、不異動
        var (url, token, lineId, version) = await NewCartAsync(shared, warehouseCode, itemCode);
        var inbound = new
        {
            requestId = Guid.NewGuid(), expectedVersion = version, type = 1, warehouseId,
            header = new { purposeId = PurchaseIn }, allocations = new[] { new { lineId, locationCode = "S-01", quantity = 1m } },
            expectedApprovalEnabled = false,
        };
        using (var missing = await PostJsonAsync(shared, url + "?handler=CheckoutReviewed", inbound, token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("user.operator_required", (await JsonAsync(missing)).GetProperty("code").GetString());
        }
        using (var stopped = await PostJsonAsync(shared, url + $"?handler=CheckoutReviewed&operatorNameId={retired.Id}", inbound, token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stopped.StatusCode);
            Assert.Equal("user.operator_inactive", (await JsonAsync(stopped)).GetProperty("code").GetString());
        }
        await using (var db = Db(factory, out var scope))
        {
            Assert.False(await db.StockDocuments.AnyAsync(d => d.Id == inbound.requestId, Ct));
            await scope.DisposeAsync();
        }

        // 選了操作人 → 過帳；建立者與異動操作者都是「王xx（公用顯示名）」
        using (var ok = await PostJsonAsync(shared, url + $"?handler=CheckoutReviewed&operatorNameId={person.Id}", inbound, token))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        // 同一 RequestId 換操作人重送：回原單、不重複過帳、姓名維持第一次（冪等鍵不含姓名）
        var other = await AddOperatorAsync(factory, "周" + Suffix());
        using (var again = await PostJsonAsync(shared, url + $"?handler=CheckoutReviewed&operatorNameId={other.Id}", inbound, token))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(inbound.requestId, (await JsonAsync(again)).GetProperty("documentId").GetGuid());
        }
        var expected = $"{person.Name}（{display}）";
        await using (var db = Db(factory, out var scope))
        {
            var doc = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == inbound.requestId, Ct);
            Assert.Equal(StockDocumentStatus.Posted, doc.Status);
            Assert.Equal(expected, doc.CreatedByName);
            Assert.Equal(sharedId, doc.CreatedById);
            var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == doc.Id, Ct);
            Assert.Equal(expected, movement.ActorName);
            await scope.DisposeAsync();
        }

        // 出倉領給自己：領用人名稱也是組合姓名
        var (url2, token2, line2, version2) = await NewCartAsync(shared, warehouseCode, itemCode);
        var outbound = new
        {
            requestId = Guid.NewGuid(), expectedVersion = version2, type = 2, warehouseId,
            header = new { purposeId = ProductionOut }, allocations = new[] { new { lineId = line2, locationCode = "S-01", quantity = 1m } },
            recipientUserId = sharedId, expectedApprovalEnabled = false,
        };
        using (var ok = await PostJsonAsync(shared, url2 + $"?handler=CheckoutReviewed&operatorNameId={person.Id}", outbound, token2))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        await using (var db = Db(factory, out var scope))
        {
            var doc = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == outbound.requestId, Ct);
            Assert.Equal(expected, doc.RecipientName);
            Assert.Equal(expected, doc.CreatedByName);
            await scope.DisposeAsync();
        }

        // 結帳畫面：公用帳號有操作人選單（不含停用人員）
        var page = await shared.GetStringAsync((await NewCartAsync(shared, warehouseCode, itemCode)).Url);
        Assert.Contains("id=\"operator-name\"", page);
        Assert.Contains(person.Name, page);
        Assert.DoesNotContain(retired.Name, page);
    }

    [Fact]
    public async Task 單據頁_公用帳號建單與確認過帳各記操作人_一般帳號不顯示選單_API寫入拒絕()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await SetApprovalAsync(factory, false);
        var (shared, _, display) = await LoginAsync(factory, isShared: true);
        var (normal, normalId, normalDisplay) = await LoginAsync(factory, isShared: false);
        var (_, warehouseId, itemCode) = await SeedAsync(factory);
        var creator = await AddOperatorAsync(factory, "陳" + Suffix());
        var poster = await AddOperatorAsync(factory, "林" + Suffix());
        var createFields = new Dictionary<string, string>
        {
            ["Create.Type"] = "Inbound", ["Create.WarehouseId"] = warehouseId.ToString(), ["Header.PurposeId"] = PurchaseIn.ToString(),
        };

        // 沒選操作人：留在原頁顯示錯誤
        var refused = await shared.PostFormAsync("/Documents", "/Documents?handler=Create", createFields);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("公用帳號請先選擇操作人", await refused.Content.ReadAsStringAsync(Ct));

        var create = await shared.PostFormAsync("/Documents", "/Documents?handler=Create",
            new Dictionary<string, string>(createFields) { ["operatorNameId"] = creator.Id.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var docUrl = create.Headers.Location!.OriginalString;
        Assert.Equal(HttpStatusCode.Redirect, (await shared.PostFormAsync(docUrl, docUrl + "?handler=AddLine", new Dictionary<string, string>
        {
            ["Line.LocationCode"] = "S-01", ["Line.ItemCode"] = itemCode, ["Line.Quantity"] = "2",
        })).StatusCode);
        var html = await shared.GetStringAsync(docUrl);
        Assert.Contains("id=\"operator-name\"", html);
        Assert.Contains("data-operator-required", html);
        var confirm = new Dictionary<string, string>
        {
            ["Version"] = VersionPattern().Match(html).Groups[1].Value, ["IdempotencyKey"] = KeyPattern().Match(html).Groups[1].Value,
        };
        var noOperator = await shared.PostFormAsync(docUrl, docUrl + "?handler=Confirm", confirm);
        Assert.Equal(HttpStatusCode.OK, noOperator.StatusCode);
        Assert.Contains("公用帳號請先選擇操作人", await noOperator.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Redirect, (await shared.PostFormAsync(docUrl, docUrl + "?handler=Confirm",
            new Dictionary<string, string>(confirm) { ["operatorNameId"] = poster.Id.ToString() })).StatusCode);

        var docId = Guid.Parse(docUrl["/Documents/Details/".Length..]);
        await using (var db = Db(factory, out var scope))
        {
            var doc = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == docId, Ct);
            Assert.Equal(StockDocumentStatus.Posted, doc.Status);
            Assert.Equal($"{creator.Name}（{display}）", doc.CreatedByName);
            Assert.Equal($"{poster.Name}（{display}）", (await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == docId, Ct)).ActorName);
            await scope.DisposeAsync();
        }

        // 沖銷（RunAsync requireOperator 的另一個入口）：沒選人被拒，選了以後沖銷草稿的建立者是該操作人
        var noReverse = await shared.PostFormAsync(docUrl, docUrl + "?handler=Reverse", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.OK, noReverse.StatusCode);
        Assert.Contains("公用帳號請先選擇操作人", await noReverse.Content.ReadAsStringAsync(Ct));
        var reversed = await shared.PostFormAsync(docUrl, docUrl + "?handler=Reverse",
            new Dictionary<string, string> { ["operatorNameId"] = creator.Id.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, reversed.StatusCode);
        await using (var db = Db(factory, out var scope))
        {
            var reversal = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.ReversalOfDocumentId == docId, Ct);
            Assert.Equal($"{creator.Name}（{display}）", reversal.CreatedByName);
            await scope.DisposeAsync();
        }

        // 一般帳號：沒有選單，照舊記本人顯示名稱（帶了 operatorNameId 也忽略）
        var normalPage = await normal.GetStringAsync("/Documents");
        Assert.DoesNotContain("id=\"operator-name\"", normalPage);
        var normalCart = await normal.PostFormAsync("/Quick", "/Quick?handler=Create", new Dictionary<string, string>());
        Assert.DoesNotContain("operator-picker.js", await normal.GetStringAsync(normalCart.Headers.Location!.OriginalString));
        var normalCreate = await normal.PostFormAsync("/Documents", "/Documents?handler=Create",
            new Dictionary<string, string>(createFields) { ["operatorNameId"] = creator.Id.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, normalCreate.StatusCode);
        var normalDoc = Guid.Parse(normalCreate.Headers.Location!.OriginalString["/Documents/Details/".Length..]);
        await using (var db = Db(factory, out var scope))
        {
            var doc = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == normalDoc, Ct);
            Assert.Equal(normalDisplay, doc.CreatedByName);
            Assert.Equal(normalId, doc.CreatedById);
            await scope.DisposeAsync();
        }

        // API：公用帳號寫入 403、讀取照常；一般帳號可寫
        var body = new { type = "Inbound", warehouseId, purposeId = PurchaseIn };
        using (var apiShared = await shared.Client.PostAsJsonAsync("/api/stock/documents", body, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, apiShared.StatusCode);
            Assert.Contains("user.shared_account_api", await apiShared.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(HttpStatusCode.OK, (await shared.Client.GetAsync("/api/stock/documents", Ct)).StatusCode);
        using (var apiNormal = await normal.Client.PostAsJsonAsync("/api/stock/documents", body, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, apiNormal.StatusCode);
        }
    }

    [Fact]
    public async Task 人員名單_重複姓名擋下_停用後查不到_帳號公用旗標可設定並寫稽核()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        // 每次呼叫用新的 scope（同正式環境一個請求一個 DbContext；失敗的新增不會殘留在追蹤裡）
        async Task<T> Master<T>(Func<IDocumentMasterDataService, Task<T>> action)
        {
            await using var s = factory.Services.CreateAsyncScope();
            return await action(s.ServiceProvider.GetRequiredService<IDocumentMasterDataService>());
        }
        async Task<T> Admin<T>(Func<IUserAdminService, Task<T>> action)
        {
            await using var s = factory.Services.CreateAsyncScope();
            return await action(s.ServiceProvider.GetRequiredService<IUserAdminService>());
        }
        var name = "張" + Suffix();
        var id = await Master(m => m.CreateOperatorNameAsync(name, 1, Manager, Ct));
        var duplicate = await Assert.ThrowsAsync<ConflictException>(() => Master(m => m.CreateOperatorNameAsync(" " + name + " ", 2, Manager, Ct)));
        Assert.Contains("人員名單", duplicate.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() => Master(m => m.CreateOperatorNameAsync(new string('名', OperatorName.NameMaxLength + 1), 0, Manager, Ct)));
        Assert.Equal(name, await Master(m => m.FindActiveOperatorNameAsync(id, Ct)));
        var row = (await Master(m => m.ListOperatorNamesAsync(false, Ct))).Single(o => o.Id == id);
        await Master(async m => { await m.SetOperatorNameActiveAsync(id, row.Version, false, Manager, Ct); return true; });
        Assert.Null(await Master(m => m.FindActiveOperatorNameAsync(id, Ct)));
        Assert.DoesNotContain(await Master(m => m.ListOperatorNamesAsync(false, Ct)), o => o.Id == id);
        Assert.Contains(await Master(m => m.ListOperatorNamesAsync(true, Ct)), o => o.Id == id && !o.IsActive);

        var adminName = "sa" + Guid.NewGuid().ToString("N")[..10];
        var actor = new Actor(await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin), adminName, false);
        var created = await Admin(a => a.CreateAsync(new CreateUserRequest("pub" + Suffix(), "倉庫公用", [Roles.Operator], IsShared: true), actor, Ct));
        var summary = await Admin(a => a.GetAsync(created.UserId, Ct));
        Assert.True(summary.IsShared);
        // IsShared 不帶（null）＝不變更
        await Admin(async a => { await a.UpdateAsync(new UpdateUserRequest(created.UserId, "倉庫公用二", [Roles.Operator], summary.ConcurrencyStamp), actor, Ct); return true; });
        summary = await Admin(a => a.GetAsync(created.UserId, Ct));
        Assert.True(summary.IsShared);
        await Admin(async a => { await a.UpdateAsync(new UpdateUserRequest(created.UserId, "倉庫公用二", [Roles.Operator], summary.ConcurrencyStamp, IsShared: false), actor, Ct); return true; });
        Assert.False((await Admin(a => a.GetAsync(created.UserId, Ct))).IsShared);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "operator_name.deactivate" && a.EntityId == id.ToString(), Ct));
    }

    private static async Task<(WebSession Session, Guid Id, string Display)> LoginAsync(WarehouseWebFactory factory, bool isShared)
    {
        var name = "sa" + Guid.NewGuid().ToString("N")[..10];
        var id = await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        if (isShared)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == id, Ct);
            user.IsShared = true;
            await db.SaveChangesAsync(Ct);
        }
        var session = WebSession.Create(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await session.LoginAsync(name, Password)).StatusCode);
        return (session, id, name + " 顯示名");
    }

    private static async Task<(string Url, string Token, Guid LineId, uint Version)> NewCartAsync(WebSession session, string warehouseCode, string itemCode)
    {
        var created = await session.PostFormAsync("/Quick", "/Quick?handler=Create", new Dictionary<string, string>());
        var url = created.Headers.Location!.OriginalString;
        var token = WebUtility.HtmlDecode(TokenPattern().Match(await session.GetStringAsync(url)).Groups[1].Value);
        using (var added = await PostJsonAsync(session, url + "?handler=AddDelta",
            new { itemCode, quantity = 1m, locationCode = $"WH:LOC:{warehouseCode}:S-01", operationId = Guid.NewGuid() }, token))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }
        using var snapshot = JsonDocument.Parse(await session.GetStringAsync(url + "?handler=Snapshot"));
        var line = snapshot.RootElement.GetProperty("lines")[0];
        var version = snapshot.RootElement.GetProperty("version").GetUInt32();
        return (url, token, line.GetProperty("id").GetGuid(), version);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(WebSession session, string url, object body, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await session.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static WarehouseDbContext Db(WarehouseWebFactory factory, out AsyncServiceScope scope)
    {
        scope = factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
    }

    private static async Task<(Guid Id, string Name)> AddOperatorAsync(WarehouseWebFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var master = scope.ServiceProvider.GetRequiredService<IDocumentMasterDataService>();
        return (await master.CreateOperatorNameAsync(name, 0, Manager, Ct), name);
    }

    private static async Task DeactivateOperatorAsync(WarehouseWebFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var master = scope.ServiceProvider.GetRequiredService<IDocumentMasterDataService>();
        var row = (await master.ListOperatorNamesAsync(true, Ct)).Single(o => o.Id == id);
        await master.SetOperatorNameActiveAsync(id, row.Version, false, Manager, Ct);
    }

    private static async Task SetApprovalAsync(WarehouseWebFactory factory, bool enabled)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = new SettingsService(scope.ServiceProvider.GetRequiredService<WarehouseDbContext>(), TimeProvider.System);
        var current = await service.GetAsync(Ct);
        if (current.DocumentApprovalEnabled != enabled)
        {
            await service.UpdateAsync(current, current with { DocumentApprovalEnabled = enabled }, new Actor(Guid.NewGuid(), "系統管理員", false), Ct);
        }
    }

    private static async Task<(string WarehouseCode, Guid WarehouseId, string ItemCode)> SeedAsync(WarehouseWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var warehouses = new WarehouseService(db, TimeProvider.System);
        var code = "S" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var warehouse = await warehouses.CreateWarehouseAsync(new WarehouseInput(code, "公用帳號測試倉"), Manager, Ct);
        await warehouses.CreateLocationAsync(warehouse,
            new LocationInput("S-01", new LocationDetails(null, null, null, null, null, null, null, null, false, false)), Manager, Ct);
        var itemManager = new Actor(Guid.NewGuid(), "物料管理員", IsItemManager: true);
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("公用帳號物料 " + Suffix(), El, Pcs, TrackingMode.None, null, null, null), itemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, itemManager, Ct);
        return (code, warehouse, await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct));
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("name=\"Version\" value=\"([0-9]+)\"")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("name=\"IdempotencyKey\" value=\"([0-9a-f-]+)\"")]
    private static partial Regex KeyPattern();
}
