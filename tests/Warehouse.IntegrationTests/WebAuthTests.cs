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

    // T-UI-PAGING-01：Razor Pages 把 "page" 當成頁面名稱，分頁參數改用 "p"（Win7 試用回報「下一頁沒作用」）
    [Fact]
    public async Task 分頁_下一頁連結帶頁碼_第二頁內容不同()
    {
        var token = "分頁" + Guid.NewGuid().ToString("N")[..6];
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var workflow = new Warehouse.Infrastructure.Items.ItemWorkflowService(db, TimeProvider.System);
            var manager = new Actor(Guid.NewGuid(), "分頁測試", IsItemManager: true);
            for (var n = 0; n < 25; n++)
            {
                var item = await workflow.SubmitAsync(Guid.NewGuid(),
                    new Warehouse.Application.Items.ItemDraft($"{token} 物料 {n:00}", El, Pcs, Warehouse.Domain.Items.TrackingMode.None, null, null, null), manager, Ct);
                var version = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                    db.Items.Where(i => i.Id == item.ItemId).Select(i => Microsoft.EntityFrameworkCore.EF.Property<uint>(i, "Version")), Ct);
                await workflow.ApproveAsync(item.ItemId, version, [], null, manager, Ct);
            }
        }
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var userName = NewUserName();
        await WebSession.CreateUserAsync(factory, userName, Password, Roles.Operator);
        var web = WebSession.Create(factory);
        await web.LoginAsync(userName, Password);

        foreach (var first in new[] { "/Search?Q=" + Uri.EscapeDataString(token), "/Items?q=" + Uri.EscapeDataString(token) })
        {
            var page1 = await web.GetStringAsync(first);
            var next = WebUtility.HtmlDecode(NextPagePattern().Match(page1).Groups[1].Value);
            Assert.Contains("p=2", next);
            var page2 = await web.GetStringAsync(next);
            Assert.NotEqual(FirstItemPattern().Match(page1).Groups[1].Value, FirstItemPattern().Match(page2).Groups[1].Value);
        }
    }

    [GeneratedRegex("<a href=\"([^\"]+)\"[^>]*>下一頁</a>")]
    private static partial Regex NextPagePattern();

    [GeneratedRegex("href=\"/Items/Details/([0-9a-f-]{36})\"")]
    private static partial Regex FirstItemPattern();

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

    // T-M2-02-06
    [Fact]
    public async Task 索引API與明細頁_新增別名_占用時409附占用者_明細頁顯示索引()
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

        async Task<Guid> ActiveItemAsync()
        {
            var created = await op.Client.PostAsJsonAsync("/api/items", new
            {
                requestId = Guid.NewGuid(), name = "索引頁 " + Guid.NewGuid().ToString("N")[..8], categoryId = El, baseUnitId = Pcs, trackingMode = "None",
            }, Ct);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemId").GetGuid();
            var version = (await manager.Client.GetFromJsonAsync<JsonElement>($"/api/items/{id}", Ct)).GetProperty("version").GetUInt32();
            (await manager.Client.PostAsJsonAsync($"/api/items/{id}/approve", new { version }, Ct)).EnsureSuccessStatusCode();
            return id;
        }

        var a = await ActiveItemAsync();
        var b = await ActiveItemAsync();
        var alias = "頁面別名 " + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.Created, (await op.Client.PostAsJsonAsync($"/api/items/{a}/aliases", new { text = alias }, Ct)).StatusCode);
        var taken = await op.Client.PostAsJsonAsync($"/api/items/{b}/aliases", new { text = alias }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        var problem = await taken.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("index.alias_taken", problem.GetProperty("errorCode").GetString());
        Assert.Equal(a, problem.GetProperty("holderItemId").GetGuid());

        var keyword = await op.Client.PostAsJsonAsync($"/api/items/{b}/keywords", new { text = "頁面關鍵字" }, Ct);
        var keywordId = (await keyword.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("entryId").GetGuid();
        var other = WebSession.Create(factory);
        var otherName = NewUserName();
        await WebSession.CreateUserAsync(factory, otherName, Password, Roles.Operator);
        await other.LoginAsync(otherName, Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.Client.PostAsJsonAsync($"/api/items/{b}/keywords/{keywordId}/deactivate", new { }, Ct)).StatusCode);

        var html = await op.GetStringAsync($"/Items/Details/{a}?indexText=" + Uri.EscapeDataString("預填文字"));
        Assert.Contains(alias, html);
        Assert.Contains("value=\"預填文字\"", html);

        // 審查 R2：路由大小寫不同仍要建立「別名」，不能變成關鍵字
        var upper = "大寫路由 " + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.Created, (await op.Client.PostAsJsonAsync($"/api/items/{b}/ALIASES", new { text = upper }, Ct)).StatusCode);
        var list = await op.Client.GetFromJsonAsync<JsonElement>($"/api/items/{b}/index", Ct);
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("text").GetString() == upper && e.GetProperty("kind").GetString() == "Alias");
        Assert.Equal(HttpStatusCode.NotFound, (await op.Client.GetAsync($"/api/items/{Guid.NewGuid()}/index", Ct)).StatusCode);

        // 審查 S9：索引文字含 HTML 時頁面要編碼輸出
        await op.Client.PostAsJsonAsync($"/api/items/{b}/keywords", new { text = "<script>x</script>" }, Ct);
        var detailsB = await op.GetStringAsync($"/Items/Details/{b}");
        Assert.DoesNotContain("<script>x</script>", detailsB);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", detailsB);

        // 審查 R1：沒有 Items.Index 權限的角色（SystemAdmin）直接送出網頁表單也會被擋
        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var admin = WebSession.Create(factory);
        await admin.LoginAsync(adminName, Password);
        var denied = await admin.PostFormAsync($"/Items/Details/{a}", $"/Items/Details/{a}?handler=AddIndex", new Dictionary<string, string>
        {
            ["IndexText"] = "管理者偷加",
            ["IndexKind"] = "Alias",
        });
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
    }

    // T-M2-04-01（docs/03 情境 F、H）
    [Fact]
    public async Task 搜尋頁顯示命中原因_搜不到時可加索引但不能建立物料_SystemAdmin不能進加索引頁()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.ItemManager);
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        var admin = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        await admin.LoginAsync(adminName, Password);
        var tag = "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var created = await op.Client.PostAsJsonAsync("/api/items", new
        {
            requestId = Guid.NewGuid(), name = $"伺服放大器 {tag}", categoryId = El, baseUnitId = Pcs, trackingMode = "None",
        }, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemId").GetGuid();
        var version = (await manager.Client.GetFromJsonAsync<JsonElement>($"/api/items/{id}", Ct)).GetProperty("version").GetUInt32();
        (await manager.Client.PostAsJsonAsync($"/api/items/{id}/approve", new { version }, Ct)).EnsureSuccessStatusCode();

        // 搜不到「維修用放大器」→ 頁面提供加索引的連結
        var miss = await op.GetStringAsync("/Search?Q=" + Uri.EscapeDataString($"維修用放大器{tag}"));
        Assert.Contains("id=\"no-result\"", miss);
        Assert.Contains("/Search/AddIndex?text=", miss);

        // 加索引頁：找目標物料 → 連到明細頁並預填文字（不提供建立物料）
        var add = await op.GetStringAsync("/Search/AddIndex?text=" + Uri.EscapeDataString($"維修用放大器{tag}") + "&Target=" + tag);
        Assert.Contains($"/Items/Details/{id}?indexText=", add);
        Assert.DoesNotContain("/Items/Create", add);
        var denied = await admin.Client.GetAsync("/Search/AddIndex?text=x", Ct);
        Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);

        await op.Client.PostAsJsonAsync($"/api/items/{id}/keywords", new { text = $"維修用放大器{tag}" }, Ct);
        var hit = await op.GetStringAsync("/Search?Q=" + Uri.EscapeDataString($"維修用放大器{tag}"));
        Assert.Contains("共用關鍵字", hit);
        Assert.Contains($"/Items/Details/{id}", hit);

        var api = await op.Client.GetFromJsonAsync<JsonElement>($"/api/search?q={tag}", Ct);
        Assert.Equal("Partial", api.GetProperty("items")[0].GetProperty("reason").GetString());
    }

    // T-M2-03-08（docs/03 §8 rate limit）
    [Fact]
    public async Task 搜尋超過每分鐘上限_回429()
    {
        await using var factory = new WarehouseWebFactory(
            TestDatabase.ConnectionString, settings: new Dictionary<string, string> { ["Search:RateLimitPerMinute"] = "3" });
        var user = NewUserName();
        await WebSession.CreateUserAsync(factory, user, Password, Roles.Operator);
        var web = WebSession.Create(factory);
        await web.LoginAsync(user, Password);

        for (var n = 0; n < 3; n++)
        {
            Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/api/search?q=x", Ct)).StatusCode);
        }
        var limited = await web.Client.GetAsync("/api/search?q=x", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("search.rate_limited", (await limited.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString());
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single()), 1, 60);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await web.Client.GetAsync("/Search?Q=x", Ct)).StatusCode); // 網頁共用額度
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/Items", Ct)).StatusCode);                   // 其他頁面不受影響
    }

    // T-M3-01-11
    [Fact]
    public async Task 倉庫頁_作業員可看不可改_倉庫主管可新增_設定頁只限系統管理員_掃碼API()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.WarehouseManager);
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        var admin = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        await admin.LoginAsync(adminName, Password);
        var code = "P" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        Assert.DoesNotContain("warehouse-form", await op.GetStringAsync("/Warehouses"));
        var denied = await op.PostFormAsync("/Warehouses", "/Warehouses?handler=Save", new Dictionary<string, string>
        {
            ["Code"] = code, ["Name"] = "作業員偷建",
        });
        Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);

        var created = await manager.PostFormAsync("/Warehouses", "/Warehouses?handler=Save", new Dictionary<string, string>
        {
            ["Code"] = code, ["Name"] = "主管建立",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var warehouses = await op.Client.GetFromJsonAsync<JsonElement>("/api/warehouses", Ct);
        var warehouseId = warehouses.EnumerateArray().Single(w => w.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        var locationPage = $"/Warehouses/Locations?warehouseId={warehouseId}";
        var location = await manager.PostFormAsync(locationPage, $"/Warehouses/Locations?handler=Create&WarehouseId={warehouseId}",
            new Dictionary<string, string> { ["Form.Code"] = $"{code}-A-01", ["Form.Zone"] = "A", ["Form.Hazardous"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, location.StatusCode);

        var scan = await op.Client.GetFromJsonAsync<JsonElement>($"/api/scan?code=" + Uri.EscapeDataString($"wh:loc:{code.ToLowerInvariant()}:{code.ToLowerInvariant()}-a-01"), Ct);
        Assert.Equal("Location", scan.GetProperty("kind").GetString());
        Assert.Equal("A", scan.GetProperty("location").GetProperty("zone").GetString());

        Assert.StartsWith("/Account/AccessDenied", (await op.Client.GetAsync("/Settings", Ct)).Headers.Location!.OriginalString);
        Assert.Contains("settings-form", await admin.GetStringAsync("/Settings"));
    }

    // T-M3-03-11
    [Fact]
    public async Task 期初頁面流程_作業員建立送出_主管以API核准需冪等鍵_庫存頁看得到()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        var itemManagerName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.WarehouseManager);
        await WebSession.CreateUserAsync(factory, itemManagerName, Password, Roles.ItemManager);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        var itemManager = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        await itemManager.LoginAsync(itemManagerName, Password);
        var code = "S" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        // 主管建倉庫與儲位；物料管理員建一個有效物料
        await manager.PostFormAsync("/Warehouses", "/Warehouses?handler=Save", new Dictionary<string, string> { ["Code"] = code, ["Name"] = "期初頁面" });
        var warehouseId = (await op.Client.GetFromJsonAsync<JsonElement>("/api/warehouses", Ct)).EnumerateArray()
            .Single(w => w.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        await manager.PostFormAsync($"/Warehouses/Locations?warehouseId={warehouseId}", $"/Warehouses/Locations?handler=Create&WarehouseId={warehouseId}",
            new Dictionary<string, string> { ["Form.Code"] = "A-01" });
        var created = await itemManager.Client.PostAsJsonAsync("/api/items", new
        {
            requestId = Guid.NewGuid(), name = "期初頁面物料 " + code, categoryId = El, baseUnitId = Pcs, trackingMode = "None",
        }, Ct);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemId").GetGuid();
        var itemVersion = (await itemManager.Client.GetFromJsonAsync<JsonElement>($"/api/items/{itemId}", Ct)).GetProperty("version").GetUInt32();
        var itemCode = (await (await itemManager.Client.PostAsJsonAsync($"/api/items/{itemId}/approve", new { version = itemVersion }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("itemCode").GetString()!;

        // 作業員：建立期初單 → 加明細 → 送出
        var create = await op.PostFormAsync("/Opening", "/Opening?handler=Create", new Dictionary<string, string> { ["WarehouseId"] = warehouseId.ToString() });
        var docUrl = create.Headers.Location!.OriginalString;
        var docId = Guid.Parse(docUrl.Split('/').Last());
        Assert.Equal(HttpStatusCode.Redirect, (await op.PostFormAsync(docUrl, docUrl + "?handler=AddLine", new Dictionary<string, string>
        {
            ["Line.LocationCode"] = "a-01", ["Line.ItemCode"] = itemCode.ToLowerInvariant(), ["Line.Quantity"] = "9",
        })).StatusCode);
        var version = VersionPattern().Match(await op.GetStringAsync(docUrl)).Groups[1].Value;
        Assert.Equal(HttpStatusCode.Redirect, (await op.PostFormAsync(docUrl, docUrl + "?handler=Submit", new Dictionary<string, string> { ["Version"] = version })).StatusCode);

        // 核准：作業員 403；缺冪等鍵 400；主管成功；同鍵重送 204
        var docVersion = (await manager.Client.GetFromJsonAsync<JsonElement>($"/api/stock/opening/{docId}", Ct)).GetProperty("version").GetUInt32();
        var approveUrl = $"/api/stock/opening/{docId}/approve";
        HttpRequestMessage Approve(WebSession who, string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, approveUrl) { Content = JsonContent.Create(new { version = docVersion }) };
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }
            return request;
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Client.SendAsync(Approve(op, "key-" + Guid.NewGuid()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.Client.SendAsync(Approve(manager, null), Ct)).StatusCode);
        var key = "key-" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await manager.Client.SendAsync(Approve(manager, key), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.Client.SendAsync(Approve(manager, key), Ct)).StatusCode);

        var balances = await op.Client.GetFromJsonAsync<JsonElement>($"/api/stock/balances?warehouseId={warehouseId}", Ct);
        Assert.Equal(9m, balances.GetProperty("items")[0].GetProperty("quantity").GetDecimal());
        var stockPage = await op.GetStringAsync($"/Stock?WarehouseId={warehouseId}&LocationCode=A-01");
        Assert.Contains(itemCode, stockPage);
        Assert.Contains("期初", stockPage);
    }

    // T-M4-02-10：進出倉單 API：作業員建單確認（需冪等鍵、重送 204）、核准限主管、主檔維護限主管與物料管理員
    [Fact]
    public async Task 進出倉單API_確認需冪等鍵_重送204_權限正確()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var operatorName = NewUserName();
        var managerName = NewUserName();
        await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.WarehouseManager);
        var op = WebSession.Create(factory);
        var manager = WebSession.Create(factory);
        await op.LoginAsync(operatorName, Password);
        await manager.LoginAsync(managerName, Password);
        var code = "T" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await manager.PostFormAsync("/Warehouses", "/Warehouses?handler=Save", new Dictionary<string, string> { ["Code"] = code, ["Name"] = "單據 API" });
        var warehouseId = (await op.Client.GetFromJsonAsync<JsonElement>("/api/warehouses", Ct)).EnumerateArray()
            .Single(w => w.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        await manager.PostFormAsync($"/Warehouses/Locations?warehouseId={warehouseId}", $"/Warehouses/Locations?handler=Create&WarehouseId={warehouseId}",
            new Dictionary<string, string> { ["Form.Code"] = "A-01" });
        var itemCode = await ApprovedItemCodeAsync(factory, "單據 API 物料 " + code);
        var purposes = await op.Client.GetFromJsonAsync<JsonElement>("/api/master/purposes?direction=In", Ct);
        var purchase = purposes.EnumerateArray().Single(p => p.GetProperty("code").GetString() == "PURCHASE").GetProperty("id").GetGuid();

        var created = await op.Client.PostAsJsonAsync("/api/stock/documents", new { type = "Inbound", warehouseId, purposeId = purchase }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var docId = await created.Content.ReadFromJsonAsync<Guid>(Ct);
        Assert.Equal(HttpStatusCode.Created, (await op.Client.PostAsJsonAsync($"/api/stock/documents/{docId}/lines",
            new { locationCode = "a-01", itemCode, quantity = 6 }, Ct)).StatusCode);
        var version = (await op.Client.GetFromJsonAsync<JsonElement>($"/api/stock/documents/{docId}", Ct)).GetProperty("version").GetUInt32();

        HttpRequestMessage Post(string action, string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/stock/documents/{docId}/{action}") { Content = JsonContent.Create(new { version }) };
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }
            return request;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await op.Client.SendAsync(Post("confirm", null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Client.SendAsync(Post("approve", "key-" + Guid.NewGuid()), Ct)).StatusCode);
        var key = "key-" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await op.Client.SendAsync(Post("confirm", key), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.Client.SendAsync(Post("confirm", key), Ct)).StatusCode);
        var balances = await op.Client.GetFromJsonAsync<JsonElement>($"/api/stock/balances?warehouseId={warehouseId}", Ct);
        Assert.Equal(6m, balances.GetProperty("items")[0].GetProperty("quantity").GetDecimal());

        var partner = new { type = "Vendor", code = "v-" + code, name = "API 廠商" };
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Client.PostAsJsonAsync("/api/master/partners", partner, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await manager.Client.PostAsJsonAsync("/api/master/partners", partner, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.Client.PostAsJsonAsync("/api/master/partners", partner, Ct)).StatusCode);
    }

    /// <summary>以服務直接建立並核准一個有效物料（不經畫面），回傳料號。</summary>
    private static async Task<string> ApprovedItemCodeAsync(WarehouseWebFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var workflow = new Warehouse.Infrastructure.Items.ItemWorkflowService(db, TimeProvider.System);
        var actor = new Actor(Guid.NewGuid(), "物料管理員", IsItemManager: true);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new Warehouse.Application.Items.ItemDraft(name, El, Pcs, Warehouse.Domain.Items.TrackingMode.None, null, null, null), actor, Ct);
        var itemVersion = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.Items.Where(i => i.Id == item.ItemId).Select(i => Microsoft.EntityFrameworkCore.EF.Property<uint>(i, "Version")), Ct);
        await workflow.ApproveAsync(item.ItemId, itemVersion, [], null, actor, Ct);
        return db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).Single();
    }

    // T-M3-08-05
    [Fact]
    public async Task 倉位圖片_主管在頁面上傳_登入者可看縮圖_未登入與作業員上傳被擋()
    {
        var attachments = Path.Combine(Path.GetTempPath(), "warehouse-web-attachments-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString,
                settings: new Dictionary<string, string> { ["Storage:AttachmentsDirectory"] = attachments });
            var managerName = NewUserName();
            var operatorName = NewUserName();
            await WebSession.CreateUserAsync(factory, managerName, Password, Roles.WarehouseManager);
            await WebSession.CreateUserAsync(factory, operatorName, Password, Roles.Operator);
            var manager = WebSession.Create(factory);
            var op = WebSession.Create(factory);
            await manager.LoginAsync(managerName, Password);
            await op.LoginAsync(operatorName, Password);
            var code = "G" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
            await manager.PostFormAsync("/Warehouses", "/Warehouses?handler=Save", new Dictionary<string, string> { ["Code"] = code, ["Name"] = "圖片" });
            var warehouseId = (await manager.Client.GetFromJsonAsync<JsonElement>("/api/warehouses", Ct)).EnumerateArray()
                .Single(w => w.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
            await manager.PostFormAsync($"/Warehouses/Locations?warehouseId={warehouseId}", $"/Warehouses/Locations?handler=Create&WarehouseId={warehouseId}",
                new Dictionary<string, string> { ["Form.Code"] = "P-01" });
            var locationId = (await manager.Client.GetFromJsonAsync<JsonElement>($"/api/scan?code=WH:LOC:{code}:P-01", Ct))
                .GetProperty("location").GetProperty("id").GetGuid();
            var page = $"/Warehouses/Location/{locationId}";

            async Task<HttpResponseMessage> UploadAsync(WebSession who)
            {
                var html = await who.GetStringAsync(page);
                var token = TokenPattern().Match(html).Groups[1].Value;
                using var bitmap = new SkiaSharp.SKBitmap(400, 300);
                using var data = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                var form = new MultipartFormDataContent
                {
                    { new StringContent(System.Net.WebUtility.HtmlDecode(token)), "__RequestVerificationToken" },
                    { new ByteArrayContent(data.ToArray()), "Photo", "偽裝.jpg" },
                };
                return await who.Client.PostAsync(page + "?handler=Upload", form, Ct);
            }

            var uploaded = await UploadAsync(manager);
            Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
            var imageId = ImagePattern().Match(await op.GetStringAsync(page)).Groups[1].Value;
            Assert.NotEmpty(imageId);
            var thumb = await op.Client.GetAsync($"/files/location-images/{imageId}/thumb", Ct);
            Assert.Equal(("image/jpeg", true), (thumb.Content.Headers.ContentType!.MediaType, thumb.Headers.CacheControl!.Private));
            Assert.Contains("loading=\"lazy\"", await op.GetStringAsync(page));

            var anonymous = await WebSession.Create(factory).Client.GetAsync($"/files/location-images/{imageId}/large", Ct);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("/Account/Login", anonymous.Headers.Location!.OriginalString);
            Assert.StartsWith("/Account/AccessDenied", (await UploadAsync(op)).Headers.Location!.OriginalString);
        }
        finally
        {
            if (Directory.Exists(attachments))
            {
                Directory.Delete(attachments, recursive: true);
            }
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("/files/location-images/([0-9a-f-]{36})/thumb")]
    private static partial Regex ImagePattern();

    private static string NewUserName() => "u" + Guid.NewGuid().ToString("N")[..10];

    [GeneratedRegex("name=\"RequestId\" value=\"([0-9a-f-]+)\"")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("name=\"Version\" value=\"([0-9]+)\"")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("id=\"item-code\">([A-Z0-9]+-[0-9]+)<")]
    private static partial Regex ItemCodePattern();
}
