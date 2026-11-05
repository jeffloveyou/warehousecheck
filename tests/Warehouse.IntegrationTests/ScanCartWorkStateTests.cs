using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Inventory;

namespace Warehouse.IntegrationTests;

public partial class ScanCartTests
{
    [Fact]
    public async Task 核對草稿_帳號隔離_版本前進_過期不能覆蓋()
    {
        var me = await UserAsync(); var other = await UserAsync();
        var id = await Carts(s => s.CreateAsync(null, me, Ct));
        var before = (await Carts(s => s.GetAsync(id, me, Ct)))!;
        var input = new ScanCartDraftInput(before.Version, "{\"fields\":{\"quick-project\":\"P-100\"}}");
        await Carts(s => s.SaveDraftAsync(id, input, me, Ct));
        var saved = (await Carts(s => s.GetAsync(id, me, Ct)))!;
        Assert.Equal(input.Json, saved.DraftJson); Assert.NotEqual(before.Version, saved.Version);
        Assert.Null(await Carts(s => s.GetAsync(id, other, Ct)));
        await Assert.ThrowsAsync<NotFoundException>(() => Carts(s => s.SaveDraftAsync(id, input with { ExpectedVersion = saved.Version }, other, Ct)));
        Assert.Equal("scan_cart.concurrency_conflict", await CodeAsync(() => Carts(s => s.SaveDraftAsync(id, input, me, Ct))));
        await Assert.ThrowsAsync<DomainRuleException>(() => Carts(s => s.SaveDraftAsync(id, input with { Json = "[]" }, me, Ct)));
    }

    [Fact]
    public async Task 明確選料_永久識別不符拒絕_舊加入空欄位雜湊相容()
    {
        var me = await UserAsync(); var code = await ItemCodeAsync(TrackingMode.None);
        var id = await Carts(s => s.CreateAsync(null, me, Ct));
        var input = new ScanCartAddInput(code, null, 1m, Guid.NewGuid());
        await Assert.ThrowsAsync<DomainRuleException>(() => Carts(s => s.AddAsync(id, input with { ItemId = Guid.NewGuid() }, me, Ct)));
        Assert.Empty((await Carts(s => s.GetAsync(id, me, Ct)))!.Lines);
        await Carts(s => s.AddAsync(id, input, me, Ct));
        var legacy = System.Text.Json.JsonSerializer.Serialize(new { CartId = id, ActorId = me.Id,
            Input = new { input.ItemCode, input.LocationCode, input.Quantity, input.OperationId } });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy)));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(hash, await db.ProcessedCommands.Where(c => c.Key == "cart.add:" + input.OperationId!.Value.ToString("N")).Select(c => c.RequestHash).SingleAsync(Ct));
    }

    [Fact]
    public async Task 撤銷掃碼_只減原增量_重送不再減_修改後拒絕()
    {
        var me = await UserAsync(); var code = await ItemCodeAsync(TrackingMode.None);
        var id = await Carts(s => s.CreateAsync(null, me, Ct));
        await Carts(s => s.AddAsync(id, new ScanCartAddInput(code, null, 2m, Guid.NewGuid()), me, Ct));
        var original = new ScanCartAddInput(code, null, 3m, Guid.NewGuid());
        await Carts(s => s.AddAsync(id, original, me, Ct));
        var before = (await Carts(s => s.GetAsync(id, me, Ct)))!;
        var undo = new ScanCartUndoInput(Guid.NewGuid(), before.Version, original);
        await Carts(s => s.UndoScanAsync(id, undo, me, Ct));
        await Carts(s => s.UndoScanAsync(id, undo, me, Ct));
        Assert.Equal(2m, (await Carts(s => s.GetAsync(id, me, Ct)))!.Lines.Single().Quantity);
        var next = original with { OperationId = Guid.NewGuid() };
        await Carts(s => s.AddAsync(id, next, me, Ct));
        Assert.Equal("scan_cart.concurrency_conflict", await CodeAsync(() => Carts(s => s.UndoScanAsync(id, undo with { OperationId = Guid.NewGuid(), Original = next }, me, Ct))));
    }

    [Fact]
    public async Task 結帳來源車可查回原單據_其他帳號查不到_預留在交易內消耗()
    {
        await SetApprovalAsync(false);
        var me = await UserAsync(); var other = await UserAsync();
        var (warehouse, _) = await WarehouseAsync("A-01"); var code = await ItemCodeAsync(TrackingMode.None);
        await InboundAsync(warehouse, ("A-01", code, 5m), me);
        Guid reservationId;
        await using (var db = MigratedDatabaseFixture.CreateContext())
            reservationId = await new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System)
                .CreateAsync(new ReservationInput(code, warehouse, 5m, "保留給此車", null), Guid.NewGuid().ToString(), me, Ct);
        var cartId = await Carts(s => s.CreateAsync(null, me, Ct));
        var line = await Carts(s => s.AddAsync(cartId, new ScanCartAddInput(code, null, 5m, Guid.NewGuid()), me, Ct));
        var cart = (await Carts(s => s.GetAsync(cartId, me, Ct)))!;
        var input = new ScanCartCheckoutInput(Guid.NewGuid(), cart.Version, StockDocumentType.Outbound, warehouse,
            new StockDocumentHeader(ProductionOut, null, null, null), [new(line, "A-01", 5m, reservationId)], me.Id, null);
        var result = await Docs(s => s.CheckoutScanCartAsync(cartId, input, me, Ct));
        Assert.Equal(result, await Carts(s => s.CheckoutResultAsync(cartId, me, Ct)));
        Assert.Null(await Carts(s => s.CheckoutResultAsync(cartId, other, Ct)));
        Assert.Null(await Carts(s => s.GetAsync(cartId, me, Ct)));
        Assert.Equal(0m, await BalanceAsync(warehouse, "A-01"));
        await using var read = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(0m, await read.StockReservations.Where(r => r.Id == reservationId).Select(r => r.RemainingQuantity).SingleAsync(Ct));
    }
}
