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
using Warehouse.Application.Errors;
using Warehouse.Infrastructure.Identity;
using Warehouse.Web.Security;

namespace Warehouse.IntegrationTests;

/// <summary>M6-01 帳號管理、只讀角色、登入稽核與限流（T-M6-01-01～10）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class UserAdminTests
{
    private const string Password = "Test-pass-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-01-01：系統管理員在畫面新增帳號 → 初始密碼只顯示一次 → 用它登入必須先改密碼；稽核有 user.create
    [Fact]
    public async Task 管理員新增帳號_初始密碼可登入且必須改密碼()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var admin = await LoginAsNewAsync(factory, Roles.SystemAdmin);
        var newName = NewUserName();

        var response = await admin.PostFormAsync("/Admin/Users", "/Admin/Users?handler=Create", new Dictionary<string, string>
        {
            ["Input.UserName"] = newName,
            ["Input.DisplayName"] = "新同事",
            ["Input.Roles"] = Roles.Operator,
        });
        var html = await response.Content.ReadAsStringAsync(Ct);
        var password = WebUtility.HtmlDecode(PasswordPattern().Match(html).Groups[1].Value);
        Assert.Equal(12, password.Length);

        var user = WebSession.Create(factory);
        var login = await user.LoginAsync(newName, password);
        Assert.Equal("/Account/ChangePassword", login.Headers.Location!.OriginalString);

        await using var db = MigratedDatabaseFixture.CreateContext();
        var id = await db.Users.Where(u => u.UserName == newName).Select(u => u.Id).SingleAsync(Ct);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "user.create" && a.EntityId == id.ToString(), Ct));
        // 稽核不含密碼
        Assert.False(await AuditContainsAsync(password));
    }

    // T-M6-01-02：非系統管理員不能進帳號管理（頁面轉拒絕頁、直接 POST 也被擋）
    [Fact]
    public async Task 非系統管理員_不能使用帳號管理()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        foreach (var role in new[] { Roles.WarehouseManager, Roles.ItemManager, Roles.Operator, Roles.Viewer })
        {
            var web = await LoginAsNewAsync(factory, role);
            var page = await web.Client.GetAsync("/Admin/Users", Ct);
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.StartsWith("/Account/AccessDenied", page.Headers.Location!.OriginalString);
            // 帶有效的 antiforgery token（取自其他頁面的登出表單），確認擋下它的是授權而不是 token
            var post = await web.PostFormAsync("/Stock", "/Admin/Users?handler=Create",
                new Dictionary<string, string> { ["Input.UserName"] = NewUserName(), ["Input.DisplayName"] = "x", ["Input.Roles"] = Roles.Operator });
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.StartsWith("/Account/AccessDenied", post.Headers.Location!.OriginalString);
        }
    }

    // T-M6-01-03：只讀使用者可以查詢，任何寫入都 403
    [Fact]
    public async Task 只讀使用者_可查詢不可寫入()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var web = await LoginAsNewAsync(factory, Roles.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/api/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Client.GetAsync("/Stock", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Client.PostAsJsonAsync("/api/items", new { }, Ct)).StatusCode);
        var create = await web.Client.GetAsync("/Items/Create", Ct);
        Assert.StartsWith("/Account/AccessDenied", create.Headers.Location!.OriginalString);
    }

    // T-M6-01-04：改角色、停用、重設密碼都會換 security stamp（舊 Cookie 在下次檢查時失效）並寫稽核
    [Fact]
    public async Task 帳號變更_更新SecurityStamp並寫稽核()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var adminActor = await NewActorAsync(factory, Roles.SystemAdmin);
        var targetId = await WebSession.CreateUserAsync(factory, NewUserName(), Password, Roles.Operator);

        async Task<string> StampAsync()
        {
            await using var db = MigratedDatabaseFixture.CreateContext();
            return await db.Users.Where(u => u.Id == targetId).Select(u => u.SecurityStamp!).SingleAsync(Ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var stamp0 = await StampAsync();
        var summary = await service.GetAsync(targetId, Ct);
        await service.UpdateAsync(new UpdateUserRequest(targetId, "改名", [Roles.Viewer], summary.ConcurrencyStamp), adminActor, Ct);
        var stamp1 = await StampAsync();
        Assert.NotEqual(stamp0, stamp1);
        Assert.Equal([Roles.Viewer], (await service.GetAsync(targetId, Ct)).Roles);

        await service.SetActiveAsync(targetId, false, adminActor, Ct);
        var stamp2 = await StampAsync();
        Assert.NotEqual(stamp1, stamp2);
        var reset = await service.ResetPasswordAsync(targetId, adminActor, Ct);
        Assert.NotEqual(stamp2, await StampAsync());
        Assert.True((await service.GetAsync(targetId, Ct)).MustChangePassword);

        await using var check = MigratedDatabaseFixture.CreateContext();
        var actions = await check.AuditLogs.Where(a => a.EntityId == targetId.ToString()).Select(a => a.Action).ToListAsync(Ct);
        Assert.Contains("user.update", actions);
        Assert.Contains("user.disable", actions);
        Assert.Contains("user.reset_password", actions);
        Assert.False(await AuditContainsAsync(reset.Password));
    }

    // T-M6-01-05：舊版本號修改 → 衝突，不覆蓋
    [Fact]
    public async Task 用舊版本修改帳號_回衝突()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var adminActor = await NewActorAsync(factory, Roles.SystemAdmin);
        var targetId = await WebSession.CreateUserAsync(factory, NewUserName(), Password, Roles.Operator);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var stale = (await service.GetAsync(targetId, Ct)).ConcurrencyStamp;
        await service.UpdateAsync(new UpdateUserRequest(targetId, "第一次", [Roles.Operator], stale), adminActor, Ct);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            service.UpdateAsync(new UpdateUserRequest(targetId, "第二次", [Roles.Operator], stale), adminActor, Ct));
        Assert.Equal("user.concurrency_conflict", error.ErrorCode);
        Assert.Equal("第一次", (await service.GetAsync(targetId, Ct)).DisplayName);
    }

    // T-M6-01-06：不能停用自己、不能拿掉自己的系統管理員；最後一個啟用中的系統管理員不能被停用或拿掉角色
    [Fact]
    public async Task 自我保護與最後一個系統管理員()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var admin = await NewActorAsync(factory, Roles.SystemAdmin);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        Assert.Equal("user.self", (await Assert.ThrowsAsync<ConflictException>(() =>
            service.SetActiveAsync(admin.Id, false, admin, Ct))).ErrorCode);
        var stamp = (await service.GetAsync(admin.Id, Ct)).ConcurrencyStamp;
        Assert.Equal("user.self", (await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateAsync(new UpdateUserRequest(admin.Id, "x", [Roles.Operator], stamp), admin, Ct))).ErrorCode);

        // 暫時把其他系統管理員停用，讓 admin 成為唯一一個（測試資料庫由各測試共用，結束時還原）
        await using var db = MigratedDatabaseFixture.CreateContext();
        var others = await db.Users.Where(u => u.IsActive && u.Id != admin.Id
            && db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == RoleSeed.SystemAdminId)).Select(u => u.Id).ToListAsync(Ct);
        await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Ct);
        try
        {
            Assert.Equal("user.last_admin", (await Assert.ThrowsAsync<ConflictException>(() =>
                service.SetActiveAsync(admin.Id, false, UserCommands.CommandLineActor, Ct))).ErrorCode);
            Assert.Equal("user.last_admin", (await Assert.ThrowsAsync<ConflictException>(() =>
                service.UpdateAsync(new UpdateUserRequest(admin.Id, "x", [Roles.Operator], stamp), UserCommands.CommandLineActor, Ct))).ErrorCode);
            var after = await service.GetAsync(admin.Id, Ct);
            Assert.True(after.IsActive);
            Assert.Contains(Roles.SystemAdmin, after.Roles);
        }
        finally
        {
            await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true), Ct);
        }
    }

    // T-M6-01-07：輸入檢查（沒有角色、不認得的角色、帳號重複）
    [Fact]
    public async Task 新增帳號_輸入錯誤被拒()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var admin = await NewActorAsync(factory, Roles.SystemAdmin);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await Assert.ThrowsAsync<Warehouse.Domain.DomainRuleException>(() =>
            service.CreateAsync(new CreateUserRequest(NewUserName(), "甲", []), admin, Ct));
        await Assert.ThrowsAsync<Warehouse.Domain.DomainRuleException>(() =>
            service.CreateAsync(new CreateUserRequest(NewUserName(), "甲", ["Boss"]), admin, Ct));
        await Assert.ThrowsAsync<Warehouse.Domain.DomainRuleException>(() =>
            service.CreateAsync(new CreateUserRequest("有 空白", "甲", [Roles.Operator]), admin, Ct));
        var name = NewUserName();
        await service.CreateAsync(new CreateUserRequest(name, "甲", [Roles.Operator, Roles.WarehouseManager]), admin, Ct);
        Assert.Equal("user.name_taken", (await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(new CreateUserRequest(name, "乙", [Roles.Operator]), admin, Ct))).ErrorCode);
    }

    // T-M6-01-08：登入成功、失敗（含不存在的帳號）、鎖定都寫稽核，不記密碼
    [Fact]
    public async Task 登入結果寫稽核()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = NewUserName();
        var id = await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        var web = WebSession.Create(factory);
        var ghost = NewUserName();
        await web.LoginAsync(ghost, "Wrong-pass-999");
        for (var n = 0; n < 5; n++)
        {
            await web.LoginAsync(name, "Wrong-pass-999");
        }
        await using (var reset = factory.Services.CreateAsyncScope())
        {
            var users = reset.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(id.ToString()))!;
            await users.SetLockoutEndDateAsync(user, null);
        }
        await web.LoginAsync(name, Password);

        await using var db = MigratedDatabaseFixture.CreateContext();
        // 不存在的帳號只記遮罩值；操作者名稱固定為「（登入）」
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "auth.login_failed" && a.ActorName == LoginAuditLog.ActorName && a.EntityId == "", Ct));
        Assert.True(await AuditContainsAsync(LoginAuditLog.Mask(ghost)));
        Assert.False(await AuditContainsAsync(ghost));
        var mine = await db.AuditLogs.Where(a => a.EntityId == id.ToString()).Select(a => a.Action).ToListAsync(Ct);
        Assert.Contains("auth.login_failed", mine);
        Assert.Contains("auth.lockout", mine);
        Assert.Contains("auth.login_success", mine);
        Assert.False(await AuditContainsAsync("Wrong-pass-999"));
    }

    // T-M6-01-09：帳號不存在或停用時也會做一次密碼雜湊比對（回應時間與密碼錯誤相近，M1-03 審查 S3）
    [Fact]
    public async Task 帳號不存在_仍執行密碼雜湊()
    {
        await using var baseFactory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var counter = new CountingHasher();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<AppUser>>(counter)));
        var web = new WebSession(factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true,
        }));
        await web.LoginAsync(NewUserName(), "Wrong-pass-999");
        Assert.Equal(1, counter.Verifications);
    }

    // T-M6-01-11：被停用（或拿掉系統管理員）的管理員，在舊 Cookie 有效期內也不能再做帳號管理，包括把自己重新啟用
    [Fact]
    public async Task 被停用的管理員_不能再操作帳號()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var a = await NewActorAsync(factory, Roles.SystemAdmin);
        var b = await NewActorAsync(factory, Roles.SystemAdmin);
        var target = await WebSession.CreateUserAsync(factory, NewUserName(), Password, Roles.Operator);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await service.SetActiveAsync(a.Id, false, b, Ct);

        Assert.Equal("user.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            service.SetActiveAsync(a.Id, true, a, Ct))).ErrorCode);
        Assert.Equal("user.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            service.ResetPasswordAsync(target, a, Ct))).ErrorCode);
        Assert.False((await service.GetAsync(a.Id, Ct)).IsActive);

        // 只拿掉角色（帳號仍啟用）也一樣
        var stamp = (await service.GetAsync(b.Id, Ct)).ConcurrencyStamp;
        await service.SetActiveAsync(a.Id, true, b, Ct);
        var aStamp = (await service.GetAsync(a.Id, Ct)).ConcurrencyStamp;
        await service.UpdateAsync(new UpdateUserRequest(a.Id, "A", [Roles.Viewer], aStamp), b, Ct);
        Assert.Equal("user.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            service.UpdateAsync(new UpdateUserRequest(b.Id, "B", [Roles.Viewer], stamp), a, Ct))).ErrorCode);
    }

    // T-M6-01-12：兩個請求同時停用剩下的兩位系統管理員 → 只有一個成功，另一個 user.last_admin
    [Fact]
    public async Task 同時停用最後兩位管理員_只成功一個()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var x = await NewActorAsync(factory, Roles.SystemAdmin);
        var y = await NewActorAsync(factory, Roles.SystemAdmin);
        await using var db = MigratedDatabaseFixture.CreateContext();
        var others = await db.Users.Where(u => u.IsActive && u.Id != x.Id && u.Id != y.Id
            && db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == RoleSeed.SystemAdminId)).Select(u => u.Id).ToListAsync(Ct);
        await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Ct);
        try
        {
            async Task<string?> DisableAsync(Guid id)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
                        .SetActiveAsync(id, false, UserCommands.CommandLineActor, Ct);
                    return null;
                }
                catch (ConflictException ex)
                {
                    return ex.ErrorCode;
                }
            }
            var results = await Task.WhenAll(DisableAsync(x.Id), DisableAsync(y.Id));
            Assert.Single(results, r => r is null);
            Assert.Single(results, r => r == "user.last_admin");
            Assert.Equal(1, await db.Users.CountAsync(u => (u.Id == x.Id || u.Id == y.Id) && u.IsActive, Ct));
        }
        finally
        {
            await db.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true), Ct);
        }
    }

    // T-M6-01-13：停用的帳號登入也做一次雜湊比對，稽核記 reason=inactive
    [Fact]
    public async Task 停用帳號登入_仍執行密碼雜湊()
    {
        await using var baseFactory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = NewUserName();
        var id = await WebSession.CreateUserAsync(baseFactory, name, Password, Roles.Operator);
        await using (var scope = baseFactory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>().SetActiveAsync(id, false, UserCommands.CommandLineActor, Ct);
        }
        var counter = new CountingHasher();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<AppUser>>(counter)));
        var web = new WebSession(factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true,
        }));
        var response = await web.LoginAsync(name, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // 停用：留在登入頁
        Assert.Equal(1, counter.Verifications);
        Assert.True(await AuditContainsAsync("inactive"));
    }

    // T-M6-01-10：同一來源每分鐘登入嘗試超過上限 → 顯示「太頻繁」，不再查帳號
    [Fact]
    public async Task 登入限流()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString,
            settings: new Dictionary<string, string> { ["Login:RateLimitPerMinute"] = "3" });
        var web = WebSession.Create(factory);
        for (var n = 0; n < 3; n++)
        {
            var ok = await web.LoginAsync(NewUserName(), "Wrong-pass-999");
            Assert.DoesNotContain("太頻繁", await ok.Content.ReadAsStringAsync(Ct));
        }
        var limited = await web.LoginAsync(NewUserName(), "Wrong-pass-999");
        Assert.Contains("太頻繁", await limited.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<WebSession> LoginAsNewAsync(WarehouseWebFactory factory, string role)
    {
        var name = NewUserName();
        await WebSession.CreateUserAsync(factory, name, Password, role);
        var web = WebSession.Create(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await web.LoginAsync(name, Password)).StatusCode);
        return web;
    }

    private static async Task<Actor> NewActorAsync(WarehouseWebFactory factory, string role)
    {
        var name = NewUserName();
        var id = await WebSession.CreateUserAsync(factory, name, Password, role);
        return new Actor(id, name, false);
    }

    private static string NewUserName() => "a" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>任何稽核的前後值（jsonb 轉文字）是否含有這段文字（檢查密碼沒有被記錄）。</summary>
    private static async Task<bool> AuditContainsAsync(string text)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var pattern = "%" + text + "%";
        return await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM audit_logs WHERE before::text LIKE {pattern} OR after::text LIKE {pattern}")
            .SingleAsync(Ct) > 0;
    }

    [GeneratedRegex("id=\"initial-password\">([^<]+)<")]
    private static partial Regex PasswordPattern();

    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> inner = new();
        private int verifications;

        public int Verifications => verifications;

        public string HashPassword(AppUser user, string password) => inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref verifications);
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
