using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Domain;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Settings;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M8-06（ADR-0024）：登入「記住我」與跟著帳號走的常用設定，真實 PostgreSQL＋完整網站。
/// 會改「記住登入天數」的測試一律在 finally 還原為預設 14 天（同一個 Collection 依序執行）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class RememberMePreferenceTests
{
    private const string Password = "Remember-me-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewUserName() => "rm" + Guid.NewGuid().ToString("N")[..10];

    // T-M8-06-01：勾選記住我 → 登入 Cookie 帶 Expires（約 14 天後）；不勾 → 沒有 Expires（關閉瀏覽器即失效）
    [Fact]
    public async Task 勾選記住我_Cookie有14天期限_不勾則是工作階段Cookie()
    {
        await SetRememberDaysAsync(14);
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = NewUserName();
        await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);

        var remembered = await LoginAsync(factory, name, rememberMe: true);
        var expires = AuthCookieExpires(remembered);
        Assert.NotNull(expires);
        Assert.InRange(expires!.Value, DateTimeOffset.UtcNow.AddDays(13.9), DateTimeOffset.UtcNow.AddDays(14.1));

        var session = await LoginAsync(factory, name, rememberMe: false);
        Assert.Contains(session.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Warehouse.Auth=", StringComparison.Ordinal));
        Assert.Null(AuthCookieExpires(session));
    }

    // T-M8-06-02：天數設為 0 → 登入頁不顯示勾選框，硬送 RememberMe=true 也不會變成持久登入
    [Fact]
    public async Task 天數為0_不顯示記住我_硬送也不持久()
    {
        await SetRememberDaysAsync(0);
        try
        {
            await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
            var name = NewUserName();
            await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
            var web = WebSession.Create(factory);
            Assert.DoesNotContain("remember-me", await web.GetStringAsync("/Account/Login"));

            var response = await LoginAsync(factory, name, rememberMe: true);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Null(AuthCookieExpires(response));
        }
        finally
        {
            await SetRememberDaysAsync(14);
        }
    }

    // T-M8-06-03：持久 Cookie 在帳號停用後失效（security stamp 驗證；測試把驗證間隔設為 0，正式環境最慢 5 分鐘）
    [Fact]
    public async Task 記住我登入後帳號被停用_Cookie失效()
    {
        await SetRememberDaysAsync(14);
        await using var baseFactory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero)));
        var name = NewUserName();
        var id = await WebSession.CreateUserAsync(baseFactory, name, Password, Roles.Operator);
        var web = new WebSession(factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true,
        }));
        await web.PostFormAsync("/Account/Login", "/Account/Login", LoginFields(name, rememberMe: true));
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/Items", Ct)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(id.ToString()))!;
            user.IsActive = false;
            await users.UpdateAsync(user);
            await users.UpdateSecurityStampAsync(user);
        }

        var after = await web.Client.GetAsync("/Items", Ct);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/Login", after.Headers.Location!.ToString());
    }

    // T-M8-06-04：常用設定只收白名單鍵與合法值；null 刪除；各帳號分開
    [Fact]
    public async Task 常用設定_白名單_格式檢查_各帳號分開()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var a = await WebSession.CreateUserAsync(factory, NewUserName(), Password, Roles.Operator);
        var b = await WebSession.CreateUserAsync(factory, NewUserName(), Password, Roles.Operator);
        await using var scope = factory.Services.CreateAsyncScope();
        var prefs = scope.ServiceProvider.GetRequiredService<IUserPreferenceService>();

        await prefs.SetAsync(a, UserPreferenceKeys.ScanAddMode, "each", Ct);
        await prefs.SetAsync(a, UserPreferenceKeys.ScanAddMode, "auto", Ct); // 同一鍵覆寫
        await prefs.SetAsync(a, UserPreferenceKeys.ScanHiddenFields, "lot,expiry", Ct);
        await prefs.SetAsync(b, UserPreferenceKeys.ScanMute, "1", Ct);

        var mine = await prefs.GetAllAsync(a, Ct);
        Assert.Equal("auto", mine[UserPreferenceKeys.ScanAddMode]);
        Assert.Equal("lot,expiry", mine[UserPreferenceKeys.ScanHiddenFields]);
        Assert.False(mine.ContainsKey(UserPreferenceKeys.ScanMute));
        Assert.Equal("1", (await prefs.GetAllAsync(b, Ct))[UserPreferenceKeys.ScanMute]);

        await Assert.ThrowsAsync<DomainRuleException>(() => prefs.SetAsync(a, "other.key", "1", Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => prefs.SetAsync(a, UserPreferenceKeys.ScanAddMode, "fast", Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => prefs.SetAsync(a, UserPreferenceKeys.ScanHiddenFields, "<script>", Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => prefs.SetAsync(a, UserPreferenceKeys.DocumentsLastWarehouse, "abc", Ct));

        await prefs.SetAsync(a, UserPreferenceKeys.ScanAddMode, null, Ct);
        Assert.False((await prefs.GetAllAsync(a, Ct)).ContainsKey(UserPreferenceKeys.ScanAddMode));
    }

    // T-M8-06-05：頁面上輸出自己的設定與 token；背景 POST 儲存（204），之後換一個裝置（新的 Cookie）登入也讀得到；不合法 400
    [Fact]
    public async Task 網頁儲存常用設定_換裝置登入後一樣()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = NewUserName();
        await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        var pc = WebSession.Create(factory);
        await pc.LoginAsync(name, Password);
        // 只有用到常用設定的頁面（掃碼、明細輸入、預留）才輸出，一般清單頁不輸出（避免 no-store，審查 R3）
        Assert.DoesNotContain("id=\"wh-prefs\"", await pc.GetStringAsync("/Items"));
        var html = await pc.GetStringAsync("/Scan");
        var token = WebUtility.HtmlDecode(PrefsTokenPattern().Match(html).Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(token), "掃碼頁應輸出 #wh-prefs 與 token");

        Assert.Equal(HttpStatusCode.NoContent, (await PostPreferenceAsync(pc, token, UserPreferenceKeys.ScanAddMode, "each")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostPreferenceAsync(pc, token, "other.key", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostPreferenceAsync(pc, "", UserPreferenceKeys.ScanMute, "1")).StatusCode); // 沒有 token

        var phone = WebSession.Create(factory);
        await phone.LoginAsync(name, Password);
        Assert.Contains("\"scan.addMode\":\"each\"", await phone.GetStringAsync("/Scan"));
    }

    // T-M8-06-06：記住我登入後改密碼，新的 Cookie 仍是持久的（RefreshSignInAsync 保留 IsPersistent）
    [Fact]
    public async Task 記住我登入後改密碼_仍保持記住()
    {
        await SetRememberDaysAsync(14);
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = NewUserName();
        await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        var web = WebSession.Create(factory);
        await web.PostFormAsync("/Account/Login", "/Account/Login", LoginFields(name, rememberMe: true));

        var changed = await web.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword", new Dictionary<string, string>
        {
            ["Input.CurrentPassword"] = Password,
            ["Input.NewPassword"] = "Remember-me-456",
            ["Input.ConfirmPassword"] = "Remember-me-456",
        });
        var expires = AuthCookieExpires(changed);
        Assert.NotNull(expires);
        Assert.True(expires > DateTimeOffset.UtcNow.AddDays(13));
    }

    // T-M8-06-07：讀不到常用設定（例如新版比 Migration 先上線）時頁面照常顯示，只是沒有偏好（審查 R1）
    [Fact]
    public async Task 常用設定表讀取失敗_頁面仍可開啟()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = NewUserName();
        await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        var web = WebSession.Create(factory);
        await web.LoginAsync(name, Password);
        await using var db = MigratedDatabaseFixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE user_preferences RENAME TO user_preferences_hidden", Ct);
        try
        {
            var html = await web.GetStringAsync("/Scan");
            Assert.Contains("id=\"wh-prefs\"", html);
            Assert.Contains(">{}</script>", html);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE user_preferences_hidden RENAME TO user_preferences", Ct);
        }
    }

    private static Task<HttpResponseMessage> PostPreferenceAsync(WebSession web, string token, string key, string? value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Preferences") { Content = JsonContent.Create(new { key, value }) };
        request.Headers.Add("RequestVerificationToken", token);
        return web.Client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> LoginAsync(WarehouseWebFactory factory, string name, bool rememberMe) =>
        await WebSession.Create(factory).PostFormAsync("/Account/Login", "/Account/Login", LoginFields(name, rememberMe));

    private static Dictionary<string, string> LoginFields(string name, bool rememberMe) => new()
    {
        ["Input.UserName"] = name,
        ["Input.Password"] = Password,
        ["Input.RememberMe"] = rememberMe ? "true" : "false",
    };

    /// <summary>登入 Cookie 的 expires 屬性；工作階段 Cookie（沒有 expires）回傳 null。</summary>
    private static DateTimeOffset? AuthCookieExpires(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Warehouse.Auth=", StringComparison.Ordinal));
        var match = ExpiresPattern().Match(cookie);
        return match.Success ? DateTimeOffset.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static async Task SetRememberDaysAsync(int days)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var settings = new SettingsService(db, TimeProvider.System);
        var current = await settings.GetAsync(Ct);
        if (current.RememberMeDays != days)
        {
            await settings.UpdateAsync(current, current with { RememberMeDays = days },
                new Actor(Guid.NewGuid(), "測試管理員", false), Ct);
        }
    }

    [GeneratedRegex("expires=([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiresPattern();

    [GeneratedRegex("id=\"wh-prefs\"[^>]*data-token=\"([^\"]+)\"")]
    private static partial Regex PrefsTokenPattern();
}
