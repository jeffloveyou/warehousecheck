using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M1-03 登入與授權（T-SEC-01、T-M1-03-08～14）：經由完整的 Web 主程式（Cookie、antiforgery、授權政策、API 錯誤格式）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class WebAuthTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-SEC-01、T-M1-03-08
    [Fact]
    public async Task 未登入_API回401_頁面轉登入_健康檢查與狀態頁仍可匿名()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var web = WebSession.Create(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Client.GetAsync("/api/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Client.PostAsJsonAsync("/api/items", new { }, Ct)).StatusCode);
        var page = await web.Client.GetAsync("/Items", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/Account/Login", page.Headers.Location!.OriginalString);
        Assert.False(page.Headers.Location.IsAbsoluteUri); // 經代理時才不會指向內部槽位主機（審查 R1）
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/health/ready", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/api/system/info", Ct)).StatusCode);
    }

    // T-M1-03-09
    [Fact]
    public async Task 登入_密碼錯誤顯示訊息_正確轉到物料清單_Cookie安全屬性正確()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var user = NewUserName();
        await WebSession.CreateUserAsync(factory, user, Password, Roles.Operator);
        var web = WebSession.Create(factory);

        var wrong = await web.LoginAsync(user, "wrong-password");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("帳號或密碼錯誤", await wrong.Content.ReadAsStringAsync(Ct));

        var ok = await web.LoginAsync(user, Password);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/Items", ok.Headers.Location!.OriginalString);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Warehouse.Auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(user + " 顯示名", await web.GetStringAsync("/Items"));
    }

    // T-M1-03-10
    [Fact]
    public async Task 連續5次密碼錯誤_帳號鎖定_正確密碼也不能登入()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var user = NewUserName();
        await WebSession.CreateUserAsync(factory, user, Password, Roles.Operator);
        var web = WebSession.Create(factory);

        for (var n = 0; n < 5; n++)
        {
            await web.LoginAsync(user, "wrong-password");
        }
        var locked = await web.LoginAsync(user, Password);

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains("暫時鎖定", await locked.Content.ReadAsStringAsync(Ct));
    }

    // T-M1-03-11
    [Fact]
    public async Task 初始密碼登入後_只能先改密碼_改完才能使用()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var user = NewUserName();
        await WebSession.CreateUserAsync(factory, user, Password, Roles.Operator, mustChangePassword: true);
        var web = WebSession.Create(factory);

        var login = await web.LoginAsync(user, Password);
        Assert.Equal("/Account/ChangePassword", login.Headers.Location!.OriginalString);
        var items = await web.Client.GetAsync("/Items", Ct);
        Assert.Equal("/Account/ChangePassword", items.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client.GetAsync("/api/items", Ct)).StatusCode);

        var changed = await web.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword", new Dictionary<string, string>
        {
            ["Input.CurrentPassword"] = Password,
            ["Input.NewPassword"] = "New-pass-456",
            ["Input.ConfirmPassword"] = "New-pass-456",
        });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/Items", Ct)).StatusCode);
    }

    // T-SEC-01、T-M1-03-12
    [Fact]
    public async Task API_作業員可提出不能核准_管理員核准_錯誤以ProblemDetails回傳()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.ItemManager);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        var name = "API 測試 " + Guid.NewGuid().ToString("N")[..8];

        var submitted = await op.Client.PostAsJsonAsync("/api/items", new
        {
            requestId = Guid.NewGuid(), name, categoryId = El, baseUnitId = Pcs, trackingMode = "None",
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var itemId = (await submitted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemId").GetGuid();

        var item = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/items/{itemId}", Ct);
        Assert.Equal("PendingApproval", item.GetProperty("status").GetString());
        var version = item.GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await op.Client.PostAsJsonAsync($"/api/items/{itemId}/approve", new { version }, Ct)).StatusCode);

        var conflict = await manager.Client.PostAsJsonAsync($"/api/items/{itemId}/approve", new { version = version + 1 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("item.concurrency_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString());

        var approved = await manager.Client.PostAsJsonAsync($"/api/items/{itemId}/approve", new { version }, Ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Matches("^EL-[0-9]{6}$", (await approved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemCode").GetString());

        // 同名再提出一筆 → 核准時 409，附候選清單
        var second = await op.Client.PostAsJsonAsync("/api/items", new
        {
            requestId = Guid.NewGuid(), name, categoryId = El, baseUnitId = Pcs, trackingMode = "None",
        }, Ct);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemId").GetGuid();
        var secondVersion = (await manager.Client.GetFromJsonAsync<JsonElement>($"/api/items/{secondId}", Ct)).GetProperty("version").GetUInt32();
        var dup = await manager.Client.PostAsJsonAsync($"/api/items/{secondId}/approve", new { version = secondVersion }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var problem = await dup.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("item.duplicate_unconfirmed", problem.GetProperty("errorCode").GetString());
        Assert.Equal(itemId, problem.GetProperty("candidates")[0].GetProperty("itemId").GetGuid());

        var invalid = await op.Client.PostAsJsonAsync("/api/items", new
        {
            requestId = Guid.NewGuid(), name = " ", categoryId = El, baseUnitId = Pcs, trackingMode = "None",
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("domain_rule", (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString());
    }

    // T-M1-03-13
    [Fact]
    public async Task 頁面權限_作業員不能進分類單位維護_管理員可以()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.ItemManager);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);

        foreach (var url in new[] { "/MasterData/Categories", "/MasterData/Units" })
        {
            var denied = await op.Client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
            Assert.Contains(url.EndsWith("Units") ? "PCS" : "EL", await manager.GetStringAsync(url));
        }
        Assert.Equal(HttpStatusCode.OK, (await op.Client.GetAsync("/Items/Create", Ct)).StatusCode);
    }

    // T-M1-03-14
    [Fact]
    public async Task 頁面流程_作業員提出_管理員在明細頁核准_清單查得到料號()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.ItemManager);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        var name = "頁面測試 " + Guid.NewGuid().ToString("N")[..8];

        var createHtml = await op.GetStringAsync("/Items/Create");
        var requestId = RequestIdPattern().Match(createHtml).Groups[1].Value;
        var created = await op.PostFormAsync("/Items/Create", "/Items/Create", new Dictionary<string, string>
        {
            ["RequestId"] = requestId,
            ["Input.Name"] = name,
            ["Input.CategoryId"] = El.ToString(),
            ["Input.BaseUnitId"] = Pcs.ToString(),
            ["Input.TrackingMode"] = "None",
            ["Input.ManufacturerPartNo"] = "PG-" + name[^8..],
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var detailsUrl = created.Headers.Location!.OriginalString;
        Assert.Contains("已提出，申請單號 MR-", await op.GetStringAsync(detailsUrl));

        var detailsHtml = await manager.GetStringAsync(detailsUrl);
        var version = VersionPattern().Match(detailsHtml).Groups[1].Value;
        var approved = await manager.PostFormAsync(detailsUrl, detailsUrl + "?handler=Approve", new Dictionary<string, string>
        {
            ["Version"] = version,
        });
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        var after = await manager.GetStringAsync(detailsUrl);
        var code = ItemCodePattern().Match(after).Groups[1].Value;
        Assert.Matches("^EL-[0-9]{6}$", code);

        var list = await op.GetStringAsync("/Items?q=" + Uri.EscapeDataString(code));
        Assert.Contains(name, list);
    }

    // T-M1-03-17（審查 R2、S15、S13）
    [Fact]
    public async Task 登出有效_外部ReturnUrl不會被導出_停用帳號不能登入_API非JSON寫入回415()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var user = NewUserName();
        var disabled = NewUserName();
        await WebSession.CreateUserAsync(factory, user, Password, Roles.ItemManager);
        await WebSession.CreateUserAsync(factory, disabled, Password, Roles.Operator);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var entity = (await users.FindByNameAsync(disabled))!;
            entity.IsActive = false;
            await users.UpdateAsync(entity);
        }
        var web = WebSession.Create(factory);

        var login = await web.PostFormAsync("/Account/Login", "/Account/Login", new Dictionary<string, string>
        {
            ["Input.UserName"] = user,
            ["Input.Password"] = Password,
            ["ReturnUrl"] = "https://evil.example/phish",
        });
        Assert.Equal("/Items", login.Headers.Location!.OriginalString);

        var form = new StringContent("version=1", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await web.Client.PostAsync($"/api/items/{Guid.NewGuid()}/approve", form, Ct)).StatusCode);

        // 頁首的登出表單要帶 antiforgery token，否則會 400
        var logout = await web.PostFormAsync("/Items", "/Account/Logout", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await web.Client.GetAsync("/Items", Ct)).StatusCode);

        var blocked = await WebSession.Create(factory).LoginAsync(disabled, Password);
        Assert.Contains("帳號或密碼錯誤", await blocked.Content.ReadAsStringAsync(Ct));
    }

    private static string NewUserName() => "u" + Guid.NewGuid().ToString("N")[..10];

    [GeneratedRegex("name=\"RequestId\" value=\"([0-9a-f-]+)\"")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("name=\"Version\" value=\"([0-9]+)\"")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("id=\"item-code\">([A-Z0-9]+-[0-9]+)<")]
    private static partial Regex ItemCodePattern();
}
