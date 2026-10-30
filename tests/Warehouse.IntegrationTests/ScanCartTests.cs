using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>快速進出購物車（ADR-0026，T-QC-01～）：只看得到自己的、合併、倉庫鎖定、轉單（同一交易、冪等、整批不轉）、刪除購物車草稿。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ScanCartTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-QC-01：只看得到、只能改自己的；同料同儲位合併；儲位 QR 鎖定倉庫，別倉儲位被拒；只有儲位碼時要先有倉庫
    [Fact]
    public async Task 購物車_自己的_合併_倉庫鎖定()
    {
        var me = await UserAsync();
        var other = await UserAsync();
        var (w1, c1) = await WarehouseAsync("A-01", "A-02");
        var (_, c2) = await WarehouseAsync("B-01");
        var item = await ItemCodeAsync(TrackingMode.None);

        var cart = await Carts(s => s.CreateAsync("領料", me, Ct));
        await Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, null, 2m), me, Ct));
        await Carts(s => s.AddAsync(cart, new ScanCartAddInput("wh:item:" + item.ToLowerInvariant(), null, 3m), me, Ct)); // 同料、都沒儲位 → 合併
        await Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, $"WH:LOC:{c1}:A-01", 1m), me, Ct));
        await Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, "a-02", 4m), me, Ct)); // 已鎖定倉庫：儲位碼可以
        var details = (await Carts(s => s.GetAsync(cart, me, Ct)))!;
        Assert.Equal(w1, details.WarehouseId);
        Assert.Equal([(null, 5m), ("A-01", 1m), ("A-02", 4m)], details.Lines.Select(l => (l.LocationCode, l.Quantity)).ToArray());

        Assert.Contains("另一個倉庫", await MessageAsync(() => Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, $"WH:LOC:{c2}:B-01", 1m), me, Ct))));
        Assert.Contains("數量必須大於 0", await MessageAsync(() => Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, null, 0m), me, Ct))));
        Assert.Contains("找不到料號", await MessageAsync(() => Carts(s => s.AddAsync(cart, new ScanCartAddInput("NOPE-" + Suffix(), null, 1m), me, Ct))));

        // 別人：看不到、改不了、刪不了
        Assert.Null(await Carts(s => s.GetAsync(cart, other, Ct)));
        Assert.Empty(await Carts(s => s.ListAsync(other, Ct)));
        Assert.Equal("scan_cart.not_found", await CodeAsync(() => Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, null, 1m), other, Ct))));
        Assert.Equal("scan_cart.not_found", await CodeAsync(() => Carts(s => s.DeleteAsync(cart, other, Ct))));

        // 新購物車沒有倉庫：只有儲位碼不知道是哪個倉庫
        var second = await Carts(s => s.CreateAsync(null, me, Ct));
        Assert.Contains("不知道是哪個倉庫", await MessageAsync(() => Carts(s => s.AddAsync(second, new ScanCartAddInput(item, "A-01", 1m), me, Ct))));
        Assert.Equal(2, (await Carts(s => s.ListAsync(me, Ct))).Count);

        // 刪掉有儲位的行後解除倉庫鎖定，可以改用別倉
        foreach (var line in details.Lines.Where(l => l.LocationId is not null))
        {
            await Carts(s => s.RemoveLineAsync(cart, line.Id, me, Ct));
        }
        await Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, $"WH:LOC:{c2}:B-01", 1m), me, Ct));

        // 放棄：實體刪除
        await Carts(s => s.DeleteAsync(second, me, Ct));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.False(await db.ScanCarts.AnyAsync(c => c.Id == second, Ct));
    }

    // T-QC-02：轉出倉草稿（補儲位、用途）→ 購物車刪除、草稿明細正確 → 確認過帳結存正確；同一 RequestId 重送回傳同一張
    [Fact]
    public async Task 轉單_出倉_冪等_過帳()
    {
        var me = await UserAsync();
        var (w, c) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync(TrackingMode.None);
        await InboundAsync(w, ("A-01", item, 10m), me);

        var cart = await Carts(s => s.CreateAsync(null, me, Ct));
        var line = await Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, null, 4m), me, Ct));
        var request = Guid.NewGuid();
        var input = new ScanCartConvertInput(StockDocumentType.Outbound, w, ProductionOut, [new ScanCartConvertLine(line, "a-01")], request);
        var doc = await Docs(s => s.CreateFromScanCartAsync(cart, input, me, Ct));
        Assert.Equal(request, doc);
        Assert.Equal(doc, await Docs(s => s.CreateFromScanCartAsync(cart, input, me, Ct))); // 重送：同一張，不報錯

        var details = (await Docs(s => s.GetAsync(doc, Ct)))!;
        Assert.True(details.FromScanCart);
        Assert.Equal((StockDocumentStatus.Draft, ProductionOut), (details.Status, details.PurposeId));
        Assert.Equal(("A-01", 4m), (details.Lines.Single().LocationCode, details.Lines.Single().Quantity));
        Assert.Null(await Carts(s => s.GetAsync(cart, me, Ct)));

        await Docs(async s => await s.ConfirmAsync(doc, details.Version, "key-" + Guid.NewGuid(), me, Ct));
        Assert.Equal(6m, await BalanceAsync(w, "A-01"));
        _ = c;
    }

    // T-QC-03：任一行不合規則整批不轉（購物車保留、沒有草稿）；倉庫與購物車鎖定的不同被拒；序號物料每個序號一行；移庫不用用途
    [Fact]
    public async Task 轉單_整批不轉_序號展開_移庫()
    {
        var me = await UserAsync();
        var (w, c) = await WarehouseAsync("A-01", "A-02");
        var (w2, _) = await WarehouseAsync("B-01");
        var plain = await ItemCodeAsync(TrackingMode.None);
        var serial = await ItemCodeAsync(TrackingMode.Serial);

        var cart = await Carts(s => s.CreateAsync(null, me, Ct));
        var l1 = await Carts(s => s.AddAsync(cart, new ScanCartAddInput(plain, $"WH:LOC:{c}:A-01", 2m), me, Ct));
        var l2 = await Carts(s => s.AddAsync(cart, new ScanCartAddInput(serial, null, 2m), me, Ct));

        // 第 2 行序號個數不對 → 整批不轉
        var bad = new ScanCartConvertInput(StockDocumentType.Inbound, w, PurchaseIn,
            [new ScanCartConvertLine(l1, "A-01"), new ScanCartConvertLine(l2, "A-02", SerialNos: ["SN-" + Suffix()])], Guid.NewGuid());
        Assert.Contains("第 2 行", await MessageAsync(() => Docs(s => s.CreateFromScanCartAsync(cart, bad, me, Ct))));
        Assert.Equal(2, (await Carts(s => s.GetAsync(cart, me, Ct)))!.Lines.Count);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.False(await db.StockDocuments.AnyAsync(d => d.Id == bad.RequestId, Ct));
        }
        // 購物車鎖定在 w，選別的倉庫被拒
        Assert.Contains("另一個倉庫", await MessageAsync(() => Docs(s => s.CreateFromScanCartAsync(cart,
            bad with { WarehouseId = w2, RequestId = Guid.NewGuid() }, me, Ct))));
        // 少一行（購物車在別的裝置被加了一行）→ 整批不轉
        Assert.Contains("還沒補齊", await MessageAsync(() => Docs(s => s.CreateFromScanCartAsync(cart,
            bad with { Lines = [new ScanCartConvertLine(l1, "A-01")], RequestId = Guid.NewGuid() }, me, Ct))));

        var sn1 = "SN-" + Suffix().ToUpperInvariant();
        var sn2 = "SN-" + Suffix().ToUpperInvariant();
        var good = bad with { Lines = [new ScanCartConvertLine(l1, "A-01"), new ScanCartConvertLine(l2, "A-02", SerialNos: [sn1, sn2])], RequestId = Guid.NewGuid() };
        var inbound = await Docs(s => s.CreateFromScanCartAsync(cart, good, me, Ct));
        var lines = (await Docs(s => s.GetAsync(inbound, Ct)))!.Lines;
        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { sn1, sn2 }.Order().ToArray(), lines.Where(x => x.SerialNo is not null).Select(x => x.SerialNo!).Order().ToArray());
        await Docs(async x => await x.ConfirmAsync(inbound, (await x.GetAsync(inbound, Ct))!.Version, "key-" + Guid.NewGuid(), me, Ct));

        // 移庫：用途忽略、每行目的儲位
        var cart2 = await Carts(x => x.CreateAsync(null, me, Ct));
        var t1 = await Carts(x => x.AddAsync(cart2, new ScanCartAddInput(plain, null, 2m), me, Ct));
        var transfer = await Docs(x => x.CreateFromScanCartAsync(cart2, new ScanCartConvertInput(StockDocumentType.Transfer, w, PurchaseIn,
            [new ScanCartConvertLine(t1, "A-01", TargetLocationCode: "A-02")], Guid.NewGuid()), me, Ct));
        var t = (await Docs(x => x.GetAsync(transfer, Ct)))!;
        Assert.Null(t.PurposeId);
        Assert.Equal(("A-01", "A-02"), (t.Lines.Single().LocationCode, t.Lines.Single().TargetLocationCode));
    }

    // T-QC-04：刪除購物車轉入的草稿（不留單據、稽核保留）；一般草稿與已過帳不能刪；別人不能刪
    [Fact]
    public async Task 刪除購物車草稿_只限購物車轉入的草稿()
    {
        var me = await UserAsync();
        var other = await UserAsync();
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        var cart = await Carts(s => s.CreateAsync(null, me, Ct));
        var line = await Carts(s => s.AddAsync(cart, new ScanCartAddInput(item, null, 1m), me, Ct));
        var doc = await Docs(s => s.CreateFromScanCartAsync(cart,
            new ScanCartConvertInput(StockDocumentType.Inbound, w, PurchaseIn, [new ScanCartConvertLine(line, "A-01")], Guid.NewGuid()), me, Ct));

        Assert.Equal("document.permission_denied", await CodeAsync(async () => await Docs(async s => await s.DeleteScanCartDraftAsync(doc, await VersionAsync(doc), other, Ct))));
        await Docs(async s => await s.DeleteScanCartDraftAsync(doc, await VersionAsync(doc), me, Ct));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.False(await db.StockDocuments.AnyAsync(d => d.Id == doc, Ct));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "document.delete_scan_cart_draft" && a.EntityId == doc.ToString(), Ct));
        }

        // 一般草稿：服務拒絕；資料庫 trigger 也擋（直接刪）
        var normal = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w, new StockDocumentHeader(PurchaseIn, null, null, null), me, Ct));
        Assert.Contains("改用取消", await MessageAsync(async () => await Docs(async s => await s.DeleteScanCartDraftAsync(normal, await VersionAsync(normal), me, Ct))));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM stock_documents WHERE id = {normal}", Ct));
            Assert.Contains("不可刪除", ex.Message);
        }

        // 購物車轉入但已過帳：不能刪（服務與 trigger）
        var cart2 = await Carts(s => s.CreateAsync(null, me, Ct));
        var line2 = await Carts(s => s.AddAsync(cart2, new ScanCartAddInput(item, null, 1m), me, Ct));
        var posted = await Docs(s => s.CreateFromScanCartAsync(cart2,
            new ScanCartConvertInput(StockDocumentType.Inbound, w, PurchaseIn, [new ScanCartConvertLine(line2, "A-01")], Guid.NewGuid()), me, Ct));
        await Docs(async s => await s.ConfirmAsync(posted, await VersionAsync(posted), "key-" + Guid.NewGuid(), me, Ct));
        Assert.Contains("改用取消", await MessageAsync(async () => await Docs(async s => await s.DeleteScanCartDraftAsync(posted, await VersionAsync(posted), me, Ct))));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM stock_documents WHERE id = {posted}", Ct));
            Assert.Contains("不可刪除", ex.Message);
        }
    }

    // T-QC-05：補儲位選單：有這個料的儲位排前面、數量少的最前面，之後是其他儲位
    [Fact]
    public async Task 儲位選單_有料的在前_數量少的最前()
    {
        var me = await UserAsync();
        var (w, _) = await WarehouseAsync("A-01", "A-02", "A-03", "A-04");
        var item = await ItemCodeAsync(TrackingMode.None);
        await InboundAsync(w, ("A-03", item, 9m), me);
        await InboundAsync(w, ("A-02", item, 2m), me);
        await using var db = MigratedDatabaseFixture.CreateContext();
        var itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
        var options = await new LineEntryQueryService(db).LocationsAsync(w, null, itemId, LocationListMode.StockAscending, Ct);
        Assert.Equal(["A-02", "A-03", "A-01", "A-04"], options.Select(o => o.Code).ToArray());
        Assert.Equal([2m, 9m, null, null], options.Select(o => o.Quantity).ToArray());
    }

    private static async Task InboundAsync(Guid warehouse, (string Location, string Item, decimal Qty) line, Actor actor)
    {
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), actor, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput(line.Location, line.Item, line.Qty), actor, Ct));
        await Docs(async s => await s.ConfirmAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), actor, Ct));
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>購物車擁有者必須是存在的帳號（外鍵）。</summary>
    private static async Task<Actor> UserAsync()
    {
        var name = "qc" + Suffix();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var user = new AppUser { Id = Guid.NewGuid(), UserName = name, NormalizedUserName = name.ToUpperInvariant(), DisplayName = name, SecurityStamp = Guid.NewGuid().ToString() };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return new Actor(user.Id, name, false);
    }

    private static async Task<string?> MessageAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex) when (ex is AppException or DomainRuleException)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> CodeAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException ex)
        {
            return ex.ErrorCode;
        }
    }

    private static async Task<decimal> BalanceAsync(Guid warehouse, string location)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await (from b in db.StockBalances
                      join l in db.Locations on b.LocationId equals l.Id
                      where l.WarehouseId == warehouse && l.Code == location
                      select b.Quantity).SumAsync(Ct);
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "Q" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "快速進出測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return (id, code);
    }

    private static async Task<string> ItemCodeAsync(TrackingMode tracking)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("購物車物料 " + Suffix(), El, Pcs, tracking, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    private static async Task<T> Carts<T>(Func<IScanCartService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ScanCartService(db, TimeProvider.System));
    }

    private static async Task Carts(Func<IScanCartService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ScanCartService(db, TimeProvider.System));
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
}
