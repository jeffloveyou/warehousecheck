using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;
using Warehouse.Web.Security;

namespace Warehouse.IntegrationTests;

/// <summary>M6-01b 依倉庫限制作業（T-M6-01-20～）：只限制作業，查詢不受限制；權限變更立即生效。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class WarehouseScopeTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "範圍測試主管", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "範圍測試物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-01-20：只授權 W1 的作業員：W1 可建單過帳；W2 建單、期初、預留、跨倉移庫到 W2 都 403；查詢 W2 正常
    [Fact]
    public async Task 限定倉庫的作業員_只能在授權倉庫作業()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync("A-01");
        var (w2, c2) = await WarehouseAsync("B-01");
        var item = await ItemCodeAsync();
        var actor = await RestrictedUserAsync(factory, Roles.Operator, [w1]);

        var inbound = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w1, new StockDocumentHeader(PurchaseIn, null, null, null), actor, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", item, 5m), actor, Ct));
        await Docs(async s => await s.ConfirmAsync(inbound, (await s.GetAsync(inbound, Ct))!.Version, Key(), actor, Ct));

        await ForbiddenAsync(() =>
            Docs(s => s.CreateAsync(StockDocumentType.Inbound, w2, new StockDocumentHeader(PurchaseIn, null, null, null), actor, Ct)));
        await ForbiddenAsync(() => Opening(s => s.CreateAsync(w2, null, actor, Ct)));
        await ForbiddenAsync(() =>
            Reservations(s => s.CreateAsync(new ReservationInput(item, w2, 1m, null, null), Key(), actor, Ct)));

        var transfer = await Docs(s => s.CreateAsync(StockDocumentType.Transfer, w1, new StockDocumentHeader(null, null, null, null), actor, Ct));
        await ForbiddenAsync(() => Docs(s => s.AddLineAsync(transfer,
            new DocumentLineInput("A-01", item, 1m, TargetWarehouseCode: c2, TargetLocationCode: "B-01"), actor, Ct)));

        // 查詢不受限制
        await using var db = MigratedDatabaseFixture.CreateContext();
        var stock = new StockQueryService(db);
        Assert.NotNull(await stock.GetBalancesAsync(w2, null, null, 1, Ct));
    }

    // T-M6-01-21：主管已建好的 W2 單據，被限定在 W1 的主管不能核准、退回、取消、沖銷；權限改回全部倉庫後立即可以
    [Fact]
    public async Task 限定倉庫的主管_不能核准他倉單據_改權限立即生效()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync("A-01");
        var (w2, _) = await WarehouseAsync("B-01");
        var item = await ItemCodeAsync();
        var restricted = await RestrictedUserAsync(factory, Roles.WarehouseManager, [w1], warehouseManager: true);

        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w2, new StockDocumentHeader(PurchaseIn, null, null, null), Manager, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("B-01", item, 2m), Manager, Ct));
        var version = (await Docs(s => s.GetAsync(doc, Ct)))!.Version;
        await ForbiddenAsync(() => Docs(s => s.CancelAsync(doc, version, restricted, Ct)));
        await ForbiddenAsync(() => Docs(s => s.ConfirmAsync(doc, version, Key(), restricted, Ct)));
        await Docs(s => s.ConfirmAsync(doc, version, Key(), Manager, Ct));
        await ForbiddenAsync(() => Docs(s => s.CreateReversalAsync(doc, restricted, Ct)));
        await ForbiddenAsync(() => Warehouses(s =>
            s.CreateLocationAsync(w2, new LocationInput("B-09", Empty), restricted, Ct)));
        await ForbiddenAsync(() => Warehouses(s =>
            s.CreateWarehouseAsync(new WarehouseInput("X" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant(), "不可建"), restricted, Ct)));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
                .SetWarehousesAsync(restricted.Id, true, [], UserCommands.CommandLineActor, Ct);
        }
        var reversal = await Docs(s => s.CreateReversalAsync(doc, restricted, Ct));
        Assert.NotEqual(Guid.Empty, reversal);
    }

    // T-M6-01-22：直接打 API 帶別倉 ID → 403 warehouse.forbidden，資料不變
    [Fact]
    public async Task API直接帶別倉ID_回403()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync("A-01");
        var (w2, _) = await WarehouseAsync("B-01");
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var id = await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
                .SetWarehousesAsync(id, false, [w1], UserCommands.CommandLineActor, Ct);
        }
        var web = WebSession.Create(factory);
        await web.LoginAsync(name, Password);

        var denied = await web.Client.PostAsJsonAsync("/api/stock/documents",
            new { Type = "Inbound", WarehouseId = w2, PurposeId = PurchaseIn }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("warehouse.forbidden", await denied.Content.ReadAsStringAsync(Ct));
        var allowed = await web.Client.PostAsJsonAsync("/api/stock/documents",
            new { Type = "Inbound", WarehouseId = w1, PurposeId = PurchaseIn }, Ct);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.False(await db.StockDocuments.AnyAsync(d => d.WarehouseId == w2 && d.CreatedById == id, Ct));
        // 建單頁的倉庫下拉只列可作業的倉庫
        var page = await web.GetStringAsync("/Documents");
        Assert.Contains(w1.ToString(), page);
        var createForm = page[page.IndexOf("Create_WarehouseId", StringComparison.Ordinal)..];
        Assert.DoesNotContain($"value=\"{w2}\"", createForm);
    }

    // T-M6-01-23：設定範圍寫稽核；清單有不存在的倉庫被拒；空清單＝只能查詢
    [Fact]
    public async Task 設定倉庫範圍_稽核與輸入檢查()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync("A-01");
        var actor = await RestrictedUserAsync(factory, Roles.Operator, []);
        await ForbiddenAsync(() => Opening(s => s.CreateAsync(w1, null, actor, Ct)));
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await Assert.ThrowsAsync<Warehouse.Domain.DomainRuleException>(() =>
            admin.SetWarehousesAsync(actor.Id, false, [Guid.NewGuid()], UserCommands.CommandLineActor, Ct));
        var summary = await admin.GetAsync(actor.Id, Ct);
        Assert.False(summary.AllWarehouses);
        Assert.Empty(summary.WarehouseIds);
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "user.warehouses" && a.EntityId == actor.Id.ToString(), Ct));
    }

    private static async Task<Actor> RestrictedUserAsync(
        WarehouseWebFactory factory, string role, Guid[] warehouses, bool warehouseManager = false)
    {
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var id = await WebSession.CreateUserAsync(factory, name, Password, role);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
            .SetWarehousesAsync(id, false, warehouses, UserCommands.CommandLineActor, Ct);
        return new Actor(id, name, false, warehouseManager);
    }

    // T-M6-01-24：跨倉移庫 W1→W2：只能作業 W1 的主管不能核准；手動預留、期初核准、儲位修改、常用倉位在他倉都被拒；結存不變
    [Fact]
    public async Task 跨倉移庫核准與其他服務_他倉一律被拒()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync("A-01");
        var (w2, c2) = await WarehouseAsync("B-01");
        var item = await ItemCodeAsync();
        var restricted = await RestrictedUserAsync(factory, Roles.WarehouseManager, [w1], warehouseManager: true);
        var inbound = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w1, new StockDocumentHeader(PurchaseIn, null, null, null), Manager, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", item, 5m), Manager, Ct));
        await Docs(async s => await s.ConfirmAsync(inbound, (await s.GetAsync(inbound, Ct))!.Version, Key(), Manager, Ct));

        var transfer = await Docs(s => s.CreateAsync(StockDocumentType.Transfer, w1, new StockDocumentHeader(null, null, null, null), Manager, Ct));
        await Docs(s => s.AddLineAsync(transfer, new DocumentLineInput("A-01", item, 2m, TargetWarehouseCode: c2, TargetLocationCode: "B-01"), Manager, Ct));
        var version = (await Docs(s => s.GetAsync(transfer, Ct)))!.Version;
        await ForbiddenAsync(() => Docs(s => s.ConfirmAsync(transfer, version, Key(), restricted, Ct)));
        await ForbiddenAsync(() => Docs(s => s.CancelAsync(transfer, version, restricted, Ct)));

        // 他倉的手動預留不能取消
        var reservation = await Reservations(s => s.CreateAsync(new ReservationInput(item, w1, 1m, null, null), Key(), Manager, Ct));
        var outsider = await RestrictedUserAsync(factory, Roles.WarehouseManager, [w2], warehouseManager: true);
        await ForbiddenAsync(() => Reservations(async s => { await s.CancelAsync(reservation, outsider, Ct); return 0; }));
        // 期初單：W2 主管在 W1 的期初單不能核准
        var opening = await Opening(s => s.CreateAsync(w1, null, Manager, Ct));
        await ForbiddenAsync(() => Opening(async s => { await s.CancelAsync(opening, 0, outsider, Ct); return 0; }));
        // 儲位修改、常用倉位
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var location = await db.Locations.Where(l => l.WarehouseId == w1).Select(l => new { l.Id, Version = EF.Property<uint>(l, "Version") }).SingleAsync(Ct);
            await ForbiddenAsync(() => Warehouses(async s => { await s.UpdateLocationAsync(location.Id, location.Version, Empty, outsider, Ct); return 0; }));
            var itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
            await ForbiddenAsync(async () =>
            {
                await using var db2 = MigratedDatabaseFixture.CreateContext();
                await new ItemLocationService(db2, TimeProvider.System).AddAsync(itemId, w1, "A-01", true, outsider, Ct);
            });
        }

        await using var check = MigratedDatabaseFixture.CreateContext();
        var stock = new StockQueryService(check);
        Assert.Equal(5m, (await stock.GetBalancesAsync(w1, null, null, 1, Ct)).Items.Sum(b => b.Quantity));
        Assert.Equal(0m, (await stock.GetBalancesAsync(w2, null, null, 1, Ct)).Items.Sum(b => b.Quantity));
    }

    // T-M6-01-25：被限制倉庫的系統管理員不能設定任何人的倉庫範圍（含放寬自己）
    [Fact]
    public async Task 被限制的管理員_不能改倉庫範圍()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync("A-01");
        var admin = await RestrictedUserAsync(factory, Roles.SystemAdmin, [w1]);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await ForbiddenAsync(() => service.SetWarehousesAsync(admin.Id, true, [], admin, Ct));
        Assert.False((await service.GetAsync(admin.Id, Ct)).AllWarehouses);
        // 也不能新增帳號（新帳號預設全部倉庫）或重設別人密碼來繞過自己的範圍（M6-04 審查 R2）
        await ForbiddenAsync(() => service.CreateAsync(new CreateUserRequest("r" + Guid.NewGuid().ToString("N")[..9], "繞過", [Roles.WarehouseManager]), admin, Ct));
        var other = await WebSession.CreateUserAsync(factory, "r" + Guid.NewGuid().ToString("N")[..9], Password, Roles.WarehouseManager);
        await ForbiddenAsync(() => service.ResetPasswordAsync(other, admin, Ct));
    }

    private static async Task ForbiddenAsync(Func<Task> action) =>
        Assert.Equal("warehouse.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(action)).ErrorCode);

    private static string Key() => Guid.NewGuid().ToString("N");

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "S" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "範圍測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return (id, code);
    }

    private static async Task<string> ItemCodeAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("範圍物料 " + Guid.NewGuid().ToString("N")[..6], El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<T> Docs<T>(Func<IStockDocumentService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Docs(Func<IStockDocumentService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task<T> Opening<T>(Func<IOpeningService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task<T> Reservations<T>(Func<IStockReservationService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }

    private static async Task<T> Warehouses<T>(Func<IWarehouseService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new WarehouseService(db, TimeProvider.System));
    }
}
