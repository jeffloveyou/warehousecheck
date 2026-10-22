using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M4-03 單據、主檔、列印頁面（完整 Web 主程式：Cookie、antiforgery、授權政策）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class DocumentPagesTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M4-03-05：作業員在頁面建進倉單 → 加明細（小寫）→ 確認過帳；看不到核准；列印頁有公司名稱、簽名欄；主檔頁權限
    [Fact]
    public async Task 單據頁面流程_建單加明細確認_列印_主檔權限()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = "op" + Guid.NewGuid().ToString("N")[..10];
        var managerName = "wm" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.WarehouseManager);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        var (warehouseId, itemCode) = await SeedAsync(factory);
        var company = "頁面測試公司" + Guid.NewGuid().ToString("N")[..4];
        var previous = await SetCompanyAsync(factory, company);
        try
        {
            var create = await op.PostFormAsync("/Documents", "/Documents?handler=Create", new Dictionary<string, string>
            {
                ["Create.Type"] = "Inbound", ["Create.WarehouseId"] = warehouseId.ToString(), ["Header.PurposeId"] = PurchaseIn.ToString(),
            });
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            var docUrl = create.Headers.Location!.OriginalString;
            Assert.StartsWith("/Documents/Details/", docUrl);

            Assert.Equal(HttpStatusCode.Redirect, (await op.PostFormAsync(docUrl, docUrl + "?handler=AddLine", new Dictionary<string, string>
            {
                ["Line.LocationCode"] = "a-01", ["Line.ItemCode"] = itemCode.ToLowerInvariant(), ["Line.Quantity"] = "7",
            })).StatusCode);
            var html = await op.GetStringAsync(docUrl);
            Assert.Contains(itemCode, html);
            Assert.DoesNotContain("handler=Approve", html);
            var version = VersionPattern().Match(html).Groups[1].Value;
            var key = KeyPattern().Match(html).Groups[1].Value;
            Assert.Equal(HttpStatusCode.Redirect, (await op.PostFormAsync(docUrl, docUrl + "?handler=Confirm",
                new Dictionary<string, string> { ["Version"] = version, ["IdempotencyKey"] = key })).StatusCode);

            var posted = await op.GetStringAsync(docUrl);
            Assert.Contains("已過帳", posted);
            var print = await op.GetStringAsync(docUrl.Replace("/Details/", "/Print/"));
            Assert.Contains(company, print);
            Assert.Contains("經手人", print);
            Assert.Contains("收料人", print);
            Assert.DoesNotContain("未過帳", print);

            // 主檔：作業員可以看，不能新增；主管可以新增
            Assert.Contains("用途", await op.GetStringAsync("/MasterData/Purposes"));
            var partnerCode = "p" + Guid.NewGuid().ToString("N")[..8];
            var denied = await op.PostFormAsync("/MasterData/Partners", "/MasterData/Partners?handler=Save", new Dictionary<string, string>
            {
                ["Input.Type"] = "Vendor", ["Input.Code"] = partnerCode, ["Input.Name"] = "頁面廠商",
            });
            // 頁面內 Forbid()：Cookie 驗證會轉到 AccessDenied（或直接 403）
            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden
                || (denied.Headers.Location?.OriginalString.Contains("AccessDenied") ?? false), $"作業員新增往來對象應被拒，實際 {denied.StatusCode}");
            Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync("/MasterData/Partners", "/MasterData/Partners?handler=Save",
                new Dictionary<string, string> { ["Input.Type"] = "Vendor", ["Input.Code"] = partnerCode, ["Input.Name"] = "頁面廠商" })).StatusCode);
            Assert.Contains(partnerCode.ToUpperInvariant(), await manager.GetStringAsync("/MasterData/Partners?q=" + partnerCode));
        }
        finally
        {
            await SetCompanyAsync(factory, previous);
        }
    }

    // T-M5-03-01～05：連續掃碼面板（解析 JSON、背景加入明細、逐件累加、antiforgery、權限、草稿以外拒絕）
    [Fact]
    public async Task 掃碼面板_解析與背景加入明細_逐件累加_需要antiforgery與編輯權限()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = "op" + Guid.NewGuid().ToString("N")[..10];
        var viewerName = "ro" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, viewerName, Password, Roles.SystemAdmin);
        var op = WebSession.Create(factory);
        var viewer = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await viewer.LoginAsync(viewerName, Password);
        var (warehouseId, itemCode) = await SeedAsync(factory);
        var create = await op.PostFormAsync("/Documents", "/Documents?handler=Create", new Dictionary<string, string>
        {
            ["Create.Type"] = "Inbound", ["Create.WarehouseId"] = warehouseId.ToString(), ["Header.PurposeId"] = PurchaseIn.ToString(),
        });
        var docUrl = create.Headers.Location!.OriginalString;
        var html = await op.GetStringAsync(docUrl);
        Assert.Contains("id=\"scan-panel\"", html);
        Assert.Contains("/js/doc-scan.js", html);
        Assert.DoesNotContain("camera-scan.js", html);   // T-PERF-05：相機模組按下才載入
        var token = WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);

        // 解析：物料 QR（小寫、帶尾碼）→ Item；空儲位碼文字在本倉 → Location；太短 → Rejected
        var item = await op.Client.GetFromJsonAsync<JsonElement>($"{docUrl}?handler=ScanResolve&code=" + Uri.EscapeDataString("wh:item:" + itemCode.ToLowerInvariant() + "\r"), Ct);
        Assert.Equal(("Item", itemCode, true), (item.GetProperty("kind").GetString(), item.GetProperty("item").GetProperty("itemCode").GetString(),
            item.GetProperty("item").GetProperty("isActive").GetBoolean()));
        Assert.True(item.GetProperty("extra").TryGetProperty("suggestions", out _));
        var location = await op.Client.GetFromJsonAsync<JsonElement>($"{docUrl}?handler=ScanResolve&code=a-01", Ct);
        Assert.Equal(("Location", "A-01"), (location.GetProperty("kind").GetString(), location.GetProperty("location").GetProperty("code").GetString()));
        Assert.Equal("Rejected", (await op.Client.GetFromJsonAsync<JsonElement>($"{docUrl}?handler=ScanResolve&code=ab", Ct)).GetProperty("kind").GetString());

        async Task<HttpResponseMessage> AddAsync(WebSession who, object body, string? antiforgery)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, docUrl + "?handler=ScanAddLine") { Content = JsonContent.Create(body) };
            if (antiforgery is not null)
            {
                request.Headers.Add("RequestVerificationToken", antiforgery);
            }
            return await who.Client.SendAsync(request, Ct);
        }

        var line = new { locationCode = "A-01", itemCode, quantity = 2m, merge = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(op, line, null)).StatusCode);   // 沒有 antiforgery
        var first = await AddAsync(op, line, token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var fragment = await first.Content.ReadAsStringAsync(Ct);
        Assert.Contains("data-scan-lines", fragment);
        Assert.Contains(itemCode, fragment);
        Assert.True(first.Headers.Contains("X-Line-Id"));
        // 逐件累加：同一行累加，不新增第二行；關閉累加時重複加入被拒
        var second = await AddAsync(op, line with { quantity = 3m }, token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var merged = await second.Content.ReadAsStringAsync(Ct);
        Assert.Contains("明細（1 行）", merged);
        Assert.Contains(">5<", merged);
        var duplicate = await AddAsync(op, line with { merge = false }, token);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("相同儲位", (await duplicate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("message").GetString());

        // 沒有單據編輯權限（SystemAdmin）：看不到面板，送出被拒
        var viewerHtml = await viewer.GetStringAsync(docUrl);
        Assert.DoesNotContain("id=\"scan-panel\"", viewerHtml);
        var viewerToken = WebUtility.HtmlDecode(TokenPattern().Match(viewerHtml).Groups[1].Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(viewer, line, viewerToken)).StatusCode);

        // 過帳後不能再加
        var page = await op.GetStringAsync(docUrl);
        Assert.Equal(HttpStatusCode.Redirect, (await op.PostFormAsync(docUrl, docUrl + "?handler=Confirm", new Dictionary<string, string>
        {
            ["Version"] = VersionPattern().Match(page).Groups[1].Value, ["IdempotencyKey"] = KeyPattern().Match(page).Groups[1].Value,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(op, line, token)).StatusCode);
        Assert.Contains(">5<", await op.GetStringAsync(docUrl));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private static async Task<(Guid Warehouse, string ItemCode)> SeedAsync(WarehouseWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var manager = new Actor(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
        var warehouses = new WarehouseService(db, TimeProvider.System);
        var warehouse = await warehouses.CreateWarehouseAsync(
            new WarehouseInput("G" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant(), "頁面測試倉"), manager, Ct);
        await warehouses.CreateLocationAsync(warehouse,
            new LocationInput("A-01", new LocationDetails(null, null, null, null, null, null, null, null, false, false)), manager, Ct);
        var itemManager = new Actor(Guid.NewGuid(), "物料管理員", IsItemManager: true);
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("頁面物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, TrackingMode.None, null, null, null), itemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, itemManager, Ct);
        return (warehouse, await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct));
    }

    private static async Task<string> SetCompanyAsync(WarehouseWebFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = new SettingsService(scope.ServiceProvider.GetRequiredService<WarehouseDbContext>(), TimeProvider.System);
        var current = await service.GetAsync(Ct);
        if (current.PrintCompanyName != name)
        {
            await service.UpdateAsync(current, current with { PrintCompanyName = name }, new Actor(Guid.NewGuid(), "系統管理員", false), Ct);
        }
        return current.PrintCompanyName;
    }

    [GeneratedRegex("name=\"Version\" value=\"([0-9]+)\"")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("name=\"IdempotencyKey\" value=\"([0-9a-f-]+)\"")]
    private static partial Regex KeyPattern();
}
