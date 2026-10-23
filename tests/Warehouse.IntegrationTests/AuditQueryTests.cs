using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Audit;
using Warehouse.Domain;
using Warehouse.Web.Security;

namespace Warehouse.IntegrationTests;

/// <summary>M6-01c 操作日誌查詢（T-M6-01-30～33）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class AuditQueryTests
{
    private const string Password = "Test-pass-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-01-30：系統管理員可在操作日誌頁看到帳號建立與登入紀錄；其他角色被拒
    [Fact]
    public async Task 系統管理員可查操作日誌_其他角色被拒()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var created = await CreateViaServiceAsync(factory);
        var admin = WebSession.Create(factory);
        await admin.LoginAsync(adminName, Password);

        var html = await admin.GetStringAsync("/Admin/Audit?EntityType=user&EntityId=" + created);
        Assert.Contains("user.create", html);
        var login = await admin.GetStringAsync("/Admin/Audit?ActionPrefix=auth.");
        Assert.Contains("auth.login_success", login);

        foreach (var role in new[] { Roles.WarehouseManager, Roles.Operator, Roles.Viewer })
        {
            var name = NewUserName();
            await WebSession.CreateUserAsync(factory, name, Password, role);
            var web = WebSession.Create(factory);
            await web.LoginAsync(name, Password);
            var denied = await web.Client.GetAsync("/Admin/Audit", Ct);
            Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
        }
    }

    // T-M6-01-31：期間超過 366 天、起日晚於迄日被拒；畫面顯示訊息不出錯
    [Fact]
    public async Task 期間檢查()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditQueryService>();
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DomainRuleException>(() => audit.SearchAsync(new AuditQuery(now.AddDays(-400), now), Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => audit.SearchAsync(new AuditQuery(now, now.AddDays(-1)), Ct));

        var adminName = NewUserName();
        await WebSession.CreateUserAsync(factory, adminName, Password, Roles.SystemAdmin);
        var web = WebSession.Create(factory);
        await web.LoginAsync(adminName, Password);
        var html = await web.GetStringAsync("/Admin/Audit?From=2020-01-01&To=2026-01-01");
        Assert.Contains("最長 366 天", html);
        // 手改網址的極端日期：顯示訊息，不是 500
        Assert.Contains("超出可查詢範圍", await web.GetStringAsync("/Admin/Audit?From=0001-01-01&To=9999-12-31"));
    }

    // T-M6-01-32：動作開頭的 _ 與 % 當一般字元（不是萬用字元）；分頁新到舊
    [Fact]
    public async Task 動作開頭篩選_萬用字元被跳脫()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var created = await CreateViaServiceAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditQueryService>();
        var now = DateTimeOffset.UtcNow;

        var exact = await audit.SearchAsync(new AuditQuery(now.AddHours(-1), now.AddMinutes(1), ActionPrefix: "user.create", EntityId: created.ToString()), Ct);
        Assert.Single(exact.Items);
        var wildcard = await audit.SearchAsync(new AuditQuery(now.AddHours(-1), now.AddMinutes(1), ActionPrefix: "user_create", EntityId: created.ToString()), Ct);
        Assert.Empty(wildcard.Items);
        var byActor = await audit.SearchAsync(new AuditQuery(now.AddHours(-1), now.AddMinutes(1), ActorName: "命令", EntityId: created.ToString()), Ct);
        Assert.Equal("命令列", byActor.Items.Single().ActorName);
    }

    private static async Task<Guid> CreateViaServiceAsync(WarehouseWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
            .CreateAsync(new CreateUserRequest(NewUserName(), "日誌測試", [Roles.Viewer]), UserCommands.CommandLineActor, Ct);
        return result.UserId;
    }

    private static string NewUserName() => "l" + Guid.NewGuid().ToString("N")[..10];
}
