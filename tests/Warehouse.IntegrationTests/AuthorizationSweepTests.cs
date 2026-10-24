using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M6-04 權限繞過掃描（T-M6-04-01～04）：
/// 1. 所有端點：除白名單外都要登入；/api 的寫入端點都要有「寫入」政策（不只是查詢權限）。
/// 2. 所有頁面 POST handler 都必須登記在 <see cref="Handlers"/>（新增 handler 忘了登記就失敗，逼開發者想清楚權限）。
/// 3. 只讀使用者直接 POST 每個需要權限的 handler（帶有效的 antiforgery token），一律被拒（轉到拒絕頁或 403）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class AuthorizationSweepTests
{
    private const string Password = "Test-pass-123";

    /// <summary>不需要登入的端點（其餘一律要登入）。</summary>
    private static readonly HashSet<string> Anonymous =
    [
        "/health/live", "/health/ready", "/api/system/info", "/sw.js", "Index", "Account/Login", "Account/AccessDenied",
        "dev/ca", "dev/camera", "dev/scanner", "",
    ];

    /// <summary>只讀的政策；寫入端點不能只靠這兩個。</summary>
    private static readonly HashSet<string> ReadPolicies = [Policies.StockRead, Policies.ItemsRead];

    /// <summary>
    /// 每個頁面 POST handler 的保護方式：政策名稱＝handler 或資料夾要求這個政策；
    /// <see cref="Self"/>＝只影響登入者自己（登出、改密碼）；<see cref="Open"/>＝匿名頁（登入）；<see cref="DevOnly"/>＝只在開發環境存在。
    /// </summary>
    private static readonly Dictionary<string, string> Handlers = new()
    {
        ["Account.ChangePasswordModel.OnPostAsync"] = Self,
        ["Account.LoginModel.OnPostAsync"] = Open,
        ["Account.LogoutModel.OnPostAsync"] = Self,
        ["Admin.Users.EditModel.OnPostActiveAsync"] = Policies.UsersManage,
        ["Admin.Users.EditModel.OnPostResetPasswordAsync"] = Policies.UsersManage,
        ["Admin.Users.EditModel.OnPostSaveAsync"] = Policies.UsersManage,
        ["Admin.Users.EditModel.OnPostUnlockAsync"] = Policies.UsersManage,
        ["Admin.Users.EditModel.OnPostWarehousesAsync"] = Policies.UsersManage,
        ["Admin.Users.IndexModel.OnPostCreateAsync"] = Policies.UsersManage,
        ["Dev.ScannerModel.OnPostReport"] = DevOnly,
        ["Documents.DetailsModel.OnPostAddLineAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostApproveAsync"] = Policies.DocumentApprove,
        ["Documents.DetailsModel.OnPostCancelAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostConfirmAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostRemoveAttachmentAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostRemoveLineAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostReturnAsync"] = Policies.DocumentApprove,
        ["Documents.DetailsModel.OnPostReverseAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostScanAddLineAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostSubmitAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostUpdateHeaderAsync"] = Policies.DocumentEdit,
        ["Documents.DetailsModel.OnPostUploadAsync"] = Policies.DocumentEdit,
        ["Documents.IndexModel.OnPostCreateAsync"] = Policies.DocumentEdit,
        ["Import.DetailsModel.OnPostCommitAsync"] = "依批次種類",
        ["Import.IndexModel.OnPostAsync"] = "依匯入種類",
        ["Items.CreateModel.OnPostAsync"] = Policies.ItemsSubmit,
        ["Items.DetailsModel.OnPostAddIndexAsync"] = Policies.ItemsIndex,
        ["Items.DetailsModel.OnPostAddItemLocationAsync"] = Policies.WarehousesManage,
        ["Items.DetailsModel.OnPostApproveAsync"] = Policies.ItemsReview,
        ["Items.DetailsModel.OnPostDeactivateAsync"] = Policies.ItemsReview,
        ["Items.DetailsModel.OnPostDeactivateIndexAsync"] = Policies.ItemsIndex,
        ["Items.DetailsModel.OnPostDefaultItemLocationAsync"] = Policies.WarehousesManage,
        ["Items.DetailsModel.OnPostNegativePolicyAsync"] = Policies.NegativePolicy,
        ["Items.DetailsModel.OnPostReactivateAsync"] = Policies.ItemsReview,
        ["Items.DetailsModel.OnPostRejectAsync"] = Policies.ItemsReview,
        ["Items.DetailsModel.OnPostRemoveItemLocationAsync"] = Policies.WarehousesManage,
        ["Items.DetailsModel.OnPostReturnAsync"] = Policies.ItemsReview,
        ["Items.DetailsModel.OnPostWithdrawAsync"] = Policies.ItemsSubmit,
        ["Items.EditModel.OnPostAsync"] = Policies.ItemsSubmit,
        ["Items.EditModel.OnPostResubmitAsync"] = Policies.ItemsSubmit,
        ["MasterData.CategoriesModel.OnPostSaveAsync"] = Policies.MasterDataManage,
        ["MasterData.CategoriesModel.OnPostToggleAsync"] = Policies.MasterDataManage,
        ["MasterData.PartnersModel.OnPostSaveAsync"] = Policies.PartnersManage,
        ["MasterData.PartnersModel.OnPostToggleAsync"] = Policies.PartnersManage,
        ["MasterData.PurposesModel.OnPostSaveAsync"] = Policies.PartnersManage,
        ["MasterData.PurposesModel.OnPostToggleAsync"] = Policies.PartnersManage,
        ["MasterData.UnitsModel.OnPostSaveAsync"] = Policies.MasterDataManage,
        ["MasterData.UnitsModel.OnPostToggleAsync"] = Policies.MasterDataManage,
        ["Opening.DetailsModel.OnPostAddLineAsync"] = Policies.OpeningEdit,
        ["Opening.DetailsModel.OnPostApproveAsync"] = Policies.OpeningApprove,
        ["Opening.DetailsModel.OnPostCancelAsync"] = Policies.OpeningEdit,
        ["Opening.DetailsModel.OnPostRemoveLineAsync"] = Policies.OpeningEdit,
        ["Opening.DetailsModel.OnPostReturnAsync"] = Policies.OpeningApprove,
        ["Opening.DetailsModel.OnPostScanAddLineAsync"] = Policies.OpeningEdit,
        ["Opening.DetailsModel.OnPostSubmitAsync"] = Policies.OpeningEdit,
        ["Opening.IndexModel.OnPostCreateAsync"] = Policies.OpeningEdit,
        ["Reservations.IndexModel.OnPostCancelAsync"] = Policies.ReservationManage,
        ["Reservations.IndexModel.OnPostCreateAsync"] = Policies.ReservationManage,
        ["Scan.IndexModel.OnPostRegisterAsync"] = Policies.ItemsIndex,
        ["Settings.IndexModel.OnPostAsync"] = Policies.SettingsManage,
        ["Stocktakes.DetailsModel.OnPostApproveAsync"] = Policies.StocktakeManage,
        ["Stocktakes.DetailsModel.OnPostCancelAsync"] = Policies.StocktakeManage,
        ["Stocktakes.DetailsModel.OnPostCountAsync"] = Policies.StocktakeCount,
        ["Stocktakes.DetailsModel.OnPostCountLineAsync"] = Policies.StocktakeCount,
        ["Stocktakes.DetailsModel.OnPostRecountAsync"] = Policies.StocktakeManage,
        ["Stocktakes.DetailsModel.OnPostSubmitAsync"] = Policies.StocktakeCount,
        ["Stocktakes.IndexModel.OnPostStartAsync"] = Policies.StocktakeManage,
        ["Warehouses.IndexModel.OnPostSaveAsync"] = Policies.WarehousesManage,
        ["Warehouses.IndexModel.OnPostToggleAsync"] = Policies.WarehousesManage,
        ["Warehouses.IndexModel.OnPostDeleteAsync"] = Policies.WarehousesManage,
        ["Warehouses.IndexModel.OnPostRestoreAsync"] = Policies.WarehousesManage,
        ["Warehouses.LocationModel.OnPostDeactivateImageAsync"] = Policies.WarehousesManage,
        ["Warehouses.LocationModel.OnPostStatusAsync"] = Policies.WarehousesManage,
        ["Warehouses.LocationModel.OnPostUpdateAsync"] = Policies.WarehousesManage,
        ["Warehouses.LocationModel.OnPostUploadAsync"] = Policies.WarehousesManage,
        ["Warehouses.LocationsModel.OnPostCreateAsync"] = Policies.WarehousesManage,
    };

    /// <summary>每個 /api 寫入端點應有的政策（新增端點沒登記、或政策被改掉都會失敗，審查 R4）。</summary>
    private static readonly Dictionary<string, string> ApiWrites = new()
    {
        ["DELETE /api/stock/documents/{id:guid}/lines/{lineId:guid}"] = Policies.DocumentEdit,
        ["POST /api/items/"] = Policies.ItemsSubmit,
        ["POST /api/items/{id:guid}/aliases"] = Policies.ItemsIndex,
        ["POST /api/items/{id:guid}/aliases/{entryId:guid}/deactivate"] = Policies.ItemsIndex,
        ["POST /api/items/{id:guid}/approve"] = Policies.ItemsReview,
        ["POST /api/items/{id:guid}/barcodes"] = Policies.ItemsIndex,
        ["POST /api/items/{id:guid}/barcodes/{entryId:guid}/deactivate"] = Policies.ItemsIndex,
        ["POST /api/items/{id:guid}/keywords"] = Policies.ItemsIndex,
        ["POST /api/items/{id:guid}/keywords/{entryId:guid}/deactivate"] = Policies.ItemsIndex,
        ["POST /api/items/{id:guid}/reject"] = Policies.ItemsReview,
        ["POST /api/items/{id:guid}/return"] = Policies.ItemsReview,
        ["POST /api/master/partners"] = Policies.PartnersManage,
        ["POST /api/master/purposes"] = Policies.PartnersManage,
        ["POST /api/stock/documents/"] = Policies.DocumentEdit,
        ["POST /api/stock/documents/{id:guid}/approve"] = Policies.DocumentApprove,
        ["POST /api/stock/documents/{id:guid}/cancel"] = Policies.DocumentEdit,
        ["POST /api/stock/documents/{id:guid}/confirm"] = Policies.DocumentEdit,
        ["POST /api/stock/documents/{id:guid}/lines"] = Policies.DocumentEdit,
        ["POST /api/stock/documents/{id:guid}/return"] = Policies.DocumentApprove,
        ["POST /api/stock/documents/{id:guid}/reverse"] = Policies.DocumentEdit,
        ["POST /api/stock/documents/{id:guid}/submit"] = Policies.DocumentEdit,
        ["POST /api/stock/opening/{id:guid}/approve"] = Policies.OpeningApprove,
        ["POST /api/stock/reservations"] = Policies.ReservationManage,
        ["POST /api/stock/reservations/{id:guid}/cancel"] = Policies.ReservationManage,
        ["PUT /api/items/{id:guid}/negative-policy"] = Policies.NegativePolicy,
        ["PUT /api/master/partners/{id:guid}"] = Policies.PartnersManage,
        ["PUT /api/master/partners/{id:guid}/active"] = Policies.PartnersManage,
        ["PUT /api/master/purposes/{id:guid}"] = Policies.PartnersManage,
        ["PUT /api/master/purposes/{id:guid}/active"] = Policies.PartnersManage,
        ["PUT /api/stock/documents/{id:guid}/header"] = Policies.DocumentEdit,
    };

    private const string Self = "（登入者本人）";
    private const string Open = "（匿名）";
    private const string DevOnly = "（只在開發環境）";

    /// <summary>需要 {id} 路由參數的頁面。</summary>
    private static readonly HashSet<string> IdRoutes =
        ["Documents/Details", "Items/Details", "Items/Edit", "Opening/Details", "Stocktakes/Details", "Warehouses/Location", "Import/Details"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-04-01：端點白名單與 API 寫入政策
    [Fact]
    public void 所有端點_要登入_API寫入要有寫入政策()
    {
        using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString);
        _ = factory.Server;
        var problems = new List<string>();
        var seen = new HashSet<string>();
        foreach (var endpoint in factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).OfType<string>().ToHashSet();
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null && !Anonymous.Contains(route))
            {
                problems.Add($"匿名端點不在白名單：{route}");
            }
            foreach (var method in methods.Where(m => m is "POST" or "PUT" or "DELETE" or "PATCH"))
            {
                if (!route.StartsWith("/api", StringComparison.Ordinal))
                {
                    continue;
                }
                var key = $"{method} {route}";
                if (!ApiWrites.TryGetValue(key, out var expected))
                {
                    problems.Add($"API 寫入端點未登記：{key}（政策 {string.Join("|", policies)}）");
                }
                else if (!policies.Contains(expected) || policies.Except(ReadPolicies).Count() != 1)
                {
                    problems.Add($"API 寫入端點政策不符：{key} 預期 {expected}，實際 {string.Join("|", policies)}");
                }
                seen.Add(key);
            }
        }
        problems.AddRange(ApiWrites.Keys.Except(seen).Select(k => "已不存在的 API：" + k));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // T-M6-04-02：每個頁面 POST handler 都登記了保護方式（新增 handler 時必須更新上表）
    [Fact]
    public void 所有頁面POST_都有登記保護方式()
    {
        var found = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(PageModel).IsAssignableFrom(t) && t.Namespace!.StartsWith("Warehouse.Web.Pages", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                // 所有非 GET／HEAD 的 handler（含 OnPut／OnDelete／OnPatch），也包含共用基底類別定義的（審查 R5）
                .Where(m => m.Name.StartsWith("On", StringComparison.Ordinal) && !m.Name.StartsWith("OnGet", StringComparison.Ordinal)
                    && !m.Name.StartsWith("OnHead", StringComparison.Ordinal) && !m.Name.StartsWith("OnPage", StringComparison.Ordinal)
                    && m.DeclaringType!.Namespace!.StartsWith("Warehouse.Web", StringComparison.Ordinal))
                .Select(m => $"{t.FullName!["Warehouse.Web.Pages.".Length..]}.{m.Name}"))
            .ToHashSet();
        Assert.Empty(found.Except(Handlers.Keys).Select(h => "未登記：" + h));
        Assert.Empty(Handlers.Keys.Except(found).Select(h => "已不存在：" + h));
    }

    // T-M6-04-03：每個需要權限的 handler，用「不符合登記政策」的每個角色（含有其他寫入權的角色）帶有效 token 直接 POST
    // → 一律轉拒絕頁或 403。登記的政策本身也因此受測：handler 若改用較寬的政策，會有角色意外通過（審查 R1）。
    [Fact]
    public async Task 不符合政策的角色_直接POST全部被拒()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        string[] roles = [Roles.Viewer, Roles.Operator, Roles.ItemManager, Roles.WarehouseManager, Roles.SystemAdmin];
        var sessions = new Dictionary<string, (WebSession Web, string Token)>();
        foreach (var role in roles)
        {
            var name = "v" + Guid.NewGuid().ToString("N")[..10];
            await WebSession.CreateUserAsync(factory, name, Password, role);
            var web = WebSession.Create(factory);
            await web.LoginAsync(name, Password);
            sessions[role] = (web, WebUtility.HtmlDecode(TokenPattern().Match(await web.GetStringAsync("/Stock")).Groups[1].Value));
        }
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        var problems = new List<string>();
        foreach (var (key, protection) in Handlers.Where(h => h.Value is not (Self or Open or DevOnly)))
        {
            var parts = key.Split('.');
            var page = string.Join("/", parts[..^1]).Replace("Model", "", StringComparison.Ordinal);
            var handler = parts[^1]["OnPost".Length..].Replace("Async", "", StringComparison.Ordinal);
            var url = "/" + page.Replace("/Index", "", StringComparison.Ordinal)
                + (IdRoutes.Contains(page) ? "/" + Guid.NewGuid() : "")
                + (handler.Length > 0 ? "?handler=" + handler : "")
                + (page == "Admin/Users/Edit" ? "&id=" + Guid.NewGuid() : "")
                + (page.StartsWith("Import/Index", StringComparison.Ordinal) ? "?Kind=Items" : "");
            // 依種類檢查的匯入只測只讀使用者；其他依登記的政策找出不該通過的角色
            var denied = new List<string>();
            foreach (var role in roles)
            {
                var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)], "test"));
                var allowed = protection.StartsWith("依", StringComparison.Ordinal)
                    ? role != Roles.Viewer
                    : (await authorization.AuthorizeAsync(principal, protection)).Succeeded;
                if (!allowed)
                {
                    denied.Add(role);
                }
            }
            foreach (var role in denied)
            {
                var (web, token) = sessions[role];
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("RequestVerificationToken", token);
                request.Content = handler.StartsWith("Scan", StringComparison.Ordinal)
                    ? new StringContent("{}", Encoding.UTF8, "application/json")
                    : new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
                using var response = await web.Client.SendAsync(request, Ct);
                var rejected = response.StatusCode == HttpStatusCode.Forbidden
                    || (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.StartsWith("/Account/AccessDenied", StringComparison.Ordinal));
                // 匯入確認先讀批次（隨機 ID 不存在 → 404），權限另由 ImportTests 的「失去權限後確認」測試
                var notFound = response.StatusCode == HttpStatusCode.NotFound && protection == "依批次種類";
                if (!rejected && !notFound)
                {
                    problems.Add($"{role} → {key}（{protection}）→ {(int)response.StatusCode} {response.Headers.Location}");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // T-M6-04-04：角色 × API 寫入矩陣：不允許的角色一律 403；允許的角色通過授權（內容不完整時回 400／404，不是 403）
    [Fact]
    public async Task 角色與API寫入矩陣()
    {
        string[] all = [Roles.Viewer, Roles.Operator, Roles.ItemManager, Roles.WarehouseManager, Roles.SystemAdmin];
        var g = Guid.NewGuid();
        (HttpMethod Method, string Url, string[] Allowed)[] matrix =
        [
            (HttpMethod.Post, "/api/items", [Roles.Operator, Roles.ItemManager]),
            (HttpMethod.Post, $"/api/items/{g}/approve", [Roles.ItemManager]),
            (HttpMethod.Post, $"/api/items/{g}/aliases", [Roles.Operator, Roles.ItemManager]),
            (HttpMethod.Put, $"/api/items/{g}/negative-policy", [Roles.ItemManager]),
            (HttpMethod.Post, "/api/stock/documents", [Roles.Operator, Roles.WarehouseManager]),
            (HttpMethod.Post, $"/api/stock/documents/{g}/approve", [Roles.WarehouseManager]),
            (HttpMethod.Post, $"/api/stock/opening/{g}/approve", [Roles.WarehouseManager]),
            (HttpMethod.Post, "/api/stock/reservations", [Roles.Operator, Roles.WarehouseManager]),
            (HttpMethod.Post, "/api/master/purposes", [Roles.WarehouseManager, Roles.ItemManager]),
        ];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var problems = new List<string>();
        foreach (var role in all)
        {
            var name = "m" + Guid.NewGuid().ToString("N")[..10];
            await WebSession.CreateUserAsync(factory, name, Password, role);
            var web = WebSession.Create(factory);
            await web.LoginAsync(name, Password);
            foreach (var (method, url, allowed) in matrix)
            {
                using var request = new HttpRequestMessage(method, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                using var response = await web.Client.SendAsync(request, Ct);
                var forbidden = response.StatusCode == HttpStatusCode.Forbidden;
                if (allowed.Contains(role) == forbidden)
                {
                    problems.Add($"{role} {method} {url} → {(int)response.StatusCode}（預期{(allowed.Contains(role) ? "允許" : "403")}）");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // T-STOCK-08：不能手動開調整單（盤差只能經盤點核准產生）
    [Fact]
    public async Task 不能手動建立調整單()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, Roles.WarehouseManager);
        var web = WebSession.Create(factory);
        await web.LoginAsync(name, Password);
        Guid warehouse;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            warehouse = await new Warehouse.Infrastructure.Warehousing.WarehouseService(db, TimeProvider.System).CreateWarehouseAsync(
                new Warehouse.Application.Warehousing.WarehouseInput("Z" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant(), "調整測試倉"),
                new Actor(Guid.NewGuid(), "測試", false, IsWarehouseManager: true), Ct);
        }
        using var response = await web.Client.PostAsync("/api/stock/documents",
            new StringContent($$"""{"type":"Adjustment","warehouseId":"{{warehouse}}"}""", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("domain_rule", problem.GetProperty("errorCode").GetString());
        Assert.Contains("盤點核准", problem.GetProperty("detail").GetString());
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
