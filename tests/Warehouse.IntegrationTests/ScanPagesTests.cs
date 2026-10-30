using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M5-03 掃碼查料頁、期初掃碼面板、預留與導覽（T-M5-03-13～18），完整 Web 主程式。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class ScanPagesTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M5-03-13～15
    [Fact]
    public async Task 掃碼查料_物料儲位與未登錄條碼_作業員可登錄_SystemAdmin不行()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var op = await LoginAsync(factory, Roles.Operator);
        var admin = await LoginAsync(factory, Roles.SystemAdmin);
        var (warehouseCode, _, itemCode) = await SeedAsync(factory);

        var home = await op.GetStringAsync("/Scan");
        Assert.Contains("id=\"scan-input\"", home);
        Assert.Contains("id=\"nav-scan\"", home);
        Assert.DoesNotContain("camera-scan.js", home);
        Assert.DoesNotContain("camera-decoder.js", home);
        Assert.Matches("<meta name=\"wh-camera-scan-v\" content=\"[^\"]+\"", home); // 只放版本，按下相機才 import()

        var item = await op.GetStringAsync("/Scan?code=" + Uri.EscapeDataString("WH:ITEM:" + itemCode.ToLowerInvariant() + "\r\n"));
        Assert.Contains("data-kind=\"Item\"", item);
        Assert.Contains("id=\"scan-balances\"", item);
        var location = await op.GetStringAsync($"/Scan?code=WH:LOC:{warehouseCode}:S-01");
        Assert.Contains("data-kind=\"Location\"", location);
        Assert.Contains("id=\"scan-location\"", location);
        Assert.Contains("data-kind=\"Rejected\"", await op.GetStringAsync("/Scan?code=AB"));

        // 未登錄的條碼：作業員可輸入正式料號登錄（只用精確料號）；之後再掃就是物料
        var barcode = "48" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);
        var unknown = await op.GetStringAsync("/Scan?code=" + barcode);
        Assert.Contains("data-kind=\"Text\"", unknown);
        Assert.Contains("id=\"scan-register-form\"", unknown);
        Assert.DoesNotContain("id=\"scan-register-form\"", await admin.GetStringAsync("/Scan?code=" + barcode));
        var wrong = await op.PostFormAsync("/Scan?code=" + barcode, "/Scan?handler=Register&code=" + barcode,
            new Dictionary<string, string> { ["RegisterItemCode"] = "伺服", ["RegisterPackQuantity"] = "" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("找不到料號", await wrong.Content.ReadAsStringAsync(Ct));
        var forbidden = await admin.PostFormAsync("/Scan?code=" + barcode, "/Scan?handler=Register&code=" + barcode,
            new Dictionary<string, string> { ["RegisterItemCode"] = itemCode });
        Assert.StartsWith("/Account/AccessDenied", forbidden.Headers.Location!.OriginalString);
        var registered = await op.PostFormAsync("/Scan?code=" + barcode, "/Scan?handler=Register&code=" + barcode,
            new Dictionary<string, string> { ["RegisterItemCode"] = "wh:item:" + itemCode.ToLowerInvariant(), ["RegisterPackQuantity"] = "6" });
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        var after = await op.GetStringAsync(registered.Headers.Location!.OriginalString);
        Assert.Contains("data-kind=\"Item\"", after);
        Assert.Contains("外箱條碼", after);
    }

    // T-M5-03-16、17：期初掃碼面板（解析附建議儲位、背景加入、累加、權限）；預留料號欄可掃
    [Fact]
    public async Task 期初掃碼面板_背景加入與累加_預留頁有單欄掃碼()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var op = await LoginAsync(factory, Roles.Operator);
        var admin = await LoginAsync(factory, Roles.SystemAdmin);
        var (_, warehouseId, itemCode) = await SeedAsync(factory);
        var create = await op.PostFormAsync("/Opening", "/Opening?handler=Create", new Dictionary<string, string> { ["WarehouseId"] = warehouseId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var url = create.Headers.Location!.OriginalString;
        var html = await op.GetStringAsync(url);
        Assert.Contains("id=\"scan-panel\"", html);
        Assert.Contains("data-mode=\"opening\"", html);
        var token = WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);

        var resolved = await op.Client.GetFromJsonAsync<JsonElement>($"{url}?handler=ScanResolve&code=" + itemCode, Ct);
        Assert.Equal("Item", resolved.GetProperty("kind").GetString());
        Assert.True(resolved.GetProperty("extra").TryGetProperty("suggestions", out _));

        async Task<HttpResponseMessage> AddAsync(WebSession who, object body, string antiforgery)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "?handler=ScanAddLine") { Content = JsonContent.Create(body) };
            request.Headers.Add("RequestVerificationToken", antiforgery);
            return await who.Client.SendAsync(request, Ct);
        }
        var line = new { locationCode = "s-01", itemCode, quantity = 4m, merge = true };
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(op, line, token)).StatusCode);
        var merged = await (await AddAsync(op, line, token)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("data-scan-lines", merged);
        Assert.Contains("明細（1 行）", merged);
        Assert.Contains("8 PCS", merged);
        var noItem = await AddAsync(op, new { locationCode = "S-01", quantity = 1m }, token);
        Assert.Equal(HttpStatusCode.BadRequest, noItem.StatusCode);
        var adminToken = WebUtility.HtmlDecode(TokenPattern().Match(await admin.GetStringAsync(url)).Groups[1].Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(admin, line, adminToken)).StatusCode);

        var manager = await LoginAsync(factory, Roles.WarehouseManager);
        var reservations = await manager.GetStringAsync("/Reservations");
        Assert.Contains("data-scan-field", reservations);
        Assert.Contains("/js/scan-field.js", reservations);
    }

    // T-QC-06：快速進出畫面：作業員有選單、建立購物車、掃碼加入（JSON＋antiforgery）、選出倉補儲位產生草稿；別人看不到；系統管理員不能用
    [Fact]
    public async Task 快速進出_建立加入轉單_權限()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var op = await LoginAsync(factory, Roles.Operator);
        var op2 = await LoginAsync(factory, Roles.Operator);
        var admin = await LoginAsync(factory, Roles.SystemAdmin);
        var (warehouseCode, warehouseId, itemCode) = await SeedAsync(factory);

        Assert.Contains("id=\"nav-quick\"", await op.GetStringAsync("/Quick"));
        Assert.DoesNotContain("id=\"nav-quick\"", await admin.GetStringAsync("/"));
        var denied = await admin.Client.GetAsync("/Quick", Ct);
        Assert.Contains("/Account/AccessDenied", denied.Headers.Location?.OriginalString ?? "");

        var created = await op.PostFormAsync("/Quick", "/Quick?handler=Create", new Dictionary<string, string> { ["Note"] = "頁面測試" });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var cartUrl = created.Headers.Location!.OriginalString;
        Assert.StartsWith("/Quick/Cart/", cartUrl);
        var page = await op.GetStringAsync(cartUrl);
        Assert.Contains("id=\"quick-cart\"", page);
        Assert.Contains("/js/quick-cart.js", page);
        Assert.DoesNotContain("camera-scan.js", page);
        var token = WebUtility.HtmlDecode(TokenPattern().Match(page).Groups[1].Value);

        async Task<HttpResponseMessage> AddAsync(WebSession who, object body, string antiforgery)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, cartUrl + "?handler=Add") { Content = JsonContent.Create(body) };
            request.Headers.Add("RequestVerificationToken", antiforgery);
            return await who.Client.SendAsync(request, Ct);
        }
        var added = await AddAsync(op, new { itemCode, locationCode = $"WH:LOC:{warehouseCode}:S-01", quantity = 3m }, token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var lines = await added.Content.ReadAsStringAsync(Ct);
        Assert.Contains("data-quick-lines", lines);
        Assert.Contains(itemCode, lines);
        var rejected = await AddAsync(op, new { itemCode = "NOPE-" + Guid.NewGuid().ToString("N")[..6], quantity = 1m }, token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("找不到料號", await rejected.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(op, null!, token)).StatusCode); // 空的 JSON：400，不是 500

        // 別的作業員：看不到（回快速進出）、加不進去
        var other = await op2.Client.GetAsync(cartUrl, Ct);
        Assert.Equal("/Quick", other.Headers.Location?.OriginalString);
        var otherToken = WebUtility.HtmlDecode(TokenPattern().Match(await op2.GetStringAsync("/Quick")).Groups[1].Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(op2, new { itemCode, quantity = 1m }, otherToken)).StatusCode);

        // 選出倉：補齊畫面帶入購物車的儲位；儲位選單有數量排序參數
        var id = Guid.Parse(cartUrl["/Quick/Cart/".Length..]);
        var convertUrl = $"/Quick/Convert/{id}?type=Inbound";
        var convert = await op.GetStringAsync(convertUrl);
        Assert.Contains("id=\"quick-convert-form\"", convert);
        Assert.Contains("value=\"S-01\"", convert);
        Guid lineId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            lineId = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().Set<Warehouse.Domain.Inventory.ScanCartLine>()
                .Where(l => l.CartId == id).Select(l => l.Id).SingleAsync(Ct);
        }
        var purchase = SeedData.Purposes.Single(p => p.Direction == Warehouse.Domain.Inventory.PurposeDirection.In && p.Code == "PURCHASE").Id;
        var posted = await op.PostFormAsync(convertUrl, convertUrl, new Dictionary<string, string>
        {
            ["Type"] = "Inbound", ["WarehouseId"] = warehouseId.ToString(), ["PurposeId"] = purchase.ToString(), ["RequestId"] = Guid.NewGuid().ToString(),
            ["Lines[0].LineId"] = lineId.ToString(), ["Lines[0].LocationCode"] = "S-01",
        });
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        var docUrl = posted.Headers.Location!.OriginalString;
        Assert.StartsWith("/Documents/Details/", docUrl);
        var doc = await op.GetStringAsync(docUrl);
        Assert.Contains("id=\"scan-cart-draft-delete\"", doc);
        Assert.Contains(itemCode, doc);
        // 購物車已轉成單據：回快速進出
        Assert.Equal("/Quick", (await op.Client.GetAsync(cartUrl, Ct)).Headers.Location?.OriginalString);

        // 刪除草稿（不留單據）→ 回快速進出
        var deleted = await op.PostFormAsync(docUrl, docUrl + "?handler=DeleteScanCartDraft", new Dictionary<string, string>
        {
            ["Version"] = Regex.Match(doc, "name=\"Version\" value=\"(\\d+)\"").Groups[1].Value,
        });
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Equal("/Quick", deleted.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await op.Client.GetAsync(docUrl, Ct)).StatusCode);
    }

    // T-CAM-01：各作業頁的表單相機欄位（data-camera-field、數量方式）與本機解碼器（同源、固定版本、application/wasm）
    [Fact]
    public async Task 相機入口_各頁欄位有相機_解碼器為本機靜態檔()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var op = await LoginAsync(factory, Roles.Operator);
        var manager = await LoginAsync(factory, Roles.WarehouseManager);
        var (_, _, itemCode) = await SeedAsync(factory);
        Guid itemId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            itemId = await scope.ServiceProvider.GetRequiredService<WarehouseDbContext>().Items.Where(i => i.ItemCode == itemCode).Select(i => i.Id).SingleAsync(Ct);
        }

        var pages = new (WebSession Who, string Url, string[] Expected)[]
        {
            (manager, "/Reservations", ["data-camera-field=\"item\" data-camera-qty=\"Create_Quantity\"", "data-camera-qty-mode=\"reservation.qtyMode\"", "data-camera-bar"]),
            (op, "/Search", ["data-camera-field=\"item-search\""]),
            (op, "/Stock", ["data-camera-field=\"location\" data-camera-warehouse=\"stock-warehouse\"", "data-camera-field=\"item\" data-camera-allow-pack data-camera-allow-inactive"]),
            (op, "/Items/Create", ["data-camera-field=\"raw\"", "data-camera-bar"]),
            (op, "/Items/Details/" + itemId, ["data-camera-field=\"raw\" data-camera-kind-select=\"index-kind\"", "id=\"index-kind\""]),
            (op, "/Scan?code=48" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999), ["id=\"RegisterItemCode\"", "data-camera-field=\"item\""]),
        };
        foreach (var (who, url, expected) in pages)
        {
            var html = await who.GetStringAsync(url);
            foreach (var e in expected)
            {
                Assert.True(html.Contains(e, StringComparison.Ordinal), $"{url} 應含 {e}");
            }
            Assert.True(html.Contains("/js/camera-field.js", StringComparison.Ordinal), url + " 應載入 camera-field.js");
            Assert.DoesNotContain("camera-scan.js", html);
        }

        // 解碼器與 WASM 從本機提供；.wasm 的 MIME 要對，瀏覽器才能串流編譯
        var wasm = await op.Client.GetAsync("/lib/zxing-wasm/zxing_reader.wasm?v=3.1.6", Ct);
        Assert.Equal(HttpStatusCode.OK, wasm.StatusCode);
        Assert.Equal("application/wasm", wasm.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await op.Client.GetAsync("/lib/zxing-wasm/zxing-reader.js?v=3.1.6", Ct)).StatusCode);
        var decoder = await op.GetStringAsync("/js/camera-decoder.js");
        Assert.Contains("ZXING_VERSION = '3.1.6'", decoder);
        Assert.DoesNotContain("jsdelivr", decoder);
    }

    // T-M5-03-18（PR 3 審查 M1）：沒有前綴的儲位碼在查料頁顯示為儲位，不能登錄成原廠條碼（竄改網址也不行）
    [Fact]
    public async Task 查料頁的儲位碼文字_顯示為儲位且不能登錄成條碼()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var op = await LoginAsync(factory, Roles.Operator);
        var (_, _, itemCode) = await SeedAsync(factory);

        var html = await op.GetStringAsync("/Scan?code=s-01");
        Assert.Contains("id=\"scan-location-code\"", html);
        Assert.DoesNotContain("id=\"scan-register-form\"", html);
        var forged = await op.PostFormAsync("/Scan", "/Scan?handler=Register&code=S-01",
            new Dictionary<string, string> { ["RegisterItemCode"] = itemCode });
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        Assert.Contains("不能登錄為原廠條碼", await forged.Content.ReadAsStringAsync(Ct));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        Assert.False(await db.ItemBarcodes.AnyAsync(b => b.NormalizedText == "S-01", Ct));
    }

    // T-M5-03-19（PR 3 審查 S8）：期初掃碼加入，別人的草稿與已送出的單都被拒（寫入前，回 400 訊息）
    [Fact]
    public async Task 期初掃碼加入_別人的草稿與已送出的單被拒()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var owner = await LoginAsync(factory, Roles.Operator);
        var other = await LoginAsync(factory, Roles.Operator);
        var (_, warehouseId, itemCode) = await SeedAsync(factory);
        var url = (await owner.PostFormAsync("/Opening", "/Opening?handler=Create",
            new Dictionary<string, string> { ["WarehouseId"] = warehouseId.ToString() })).Headers.Location!.OriginalString;

        async Task<HttpResponseMessage> AddAsync(WebSession who)
        {
            var token = WebUtility.HtmlDecode(TokenPattern().Match(await who.GetStringAsync(url)).Groups[1].Value);
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "?handler=ScanAddLine")
            {
                Content = JsonContent.Create(new { locationCode = "S-01", itemCode, quantity = 1m }),
            };
            request.Headers.Add("RequestVerificationToken", token);
            return await who.Client.SendAsync(request, Ct);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(other)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(owner)).StatusCode);
        var page = await owner.GetStringAsync(url);
        var version = Regex.Match(page, "name=\"Version\" value=\"([0-9]+)\"").Groups[1].Value;
        Assert.Equal(HttpStatusCode.Redirect,
            (await owner.PostFormAsync(url, url + "?handler=Submit", new Dictionary<string, string> { ["Version"] = version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(owner)).StatusCode);
    }

    private static async Task<WebSession> LoginAsync(WarehouseWebFactory factory, string role)
    {
        var name = "sp" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, role);
        var session = WebSession.Create(factory);
        await session.LoginAsync(name, Password);
        return session;
    }

    private static async Task<(string WarehouseCode, Guid WarehouseId, string ItemCode)> SeedAsync(WarehouseWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var manager = new Actor(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
        var warehouses = new WarehouseService(db, TimeProvider.System);
        var code = "Q" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var warehouse = await warehouses.CreateWarehouseAsync(new WarehouseInput(code, "掃碼頁測試倉"), manager, Ct);
        await warehouses.CreateLocationAsync(warehouse,
            new LocationInput("S-01", new LocationDetails(null, null, null, null, null, null, null, null, false, false)), manager, Ct);
        var itemManager = new Actor(Guid.NewGuid(), "物料管理員", IsItemManager: true);
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("掃碼頁物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, TrackingMode.None, null, null, null), itemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, itemManager, Ct);
        return (code, warehouse, await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
