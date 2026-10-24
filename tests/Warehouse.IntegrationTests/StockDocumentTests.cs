using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.MasterData;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M4-02 進倉、出倉、移庫單（T-M4-02-01～），真實 PostgreSQL。
/// 審核設定是全域的：會改設定的測試在 finally 改回預設（同一 collection 內的測試依序執行）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class StockDocumentTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員甲", false);
    private static readonly Actor Operator2 = new(Guid.NewGuid(), "作業員乙", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管甲", false, IsWarehouseManager: true);
    private static readonly Actor Manager2 = new(Guid.NewGuid(), "倉庫主管乙", false, IsWarehouseManager: true);
    private static readonly Actor Admin = new(Guid.NewGuid(), "系統管理員", false);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M4-02-01：審核關閉（預設）：進 10 → 出 3 → 跨倉移 4；結存、異動、單號格式正確
    [Fact]
    public async Task 進倉出倉跨倉移庫_確認即過帳_結存正確()
    {
        var (w1, c1) = await WarehouseAsync("A-01", "A-02");
        var (w2, c2) = await WarehouseAsync("B-01");
        var item = await ItemCodeAsync(TrackingMode.None);

        var inbound = await DocAsync(StockDocumentType.Inbound, w1, PurchaseIn, ("A-01", item, 10m));
        await ConfirmAsync(inbound, Operator);
        var outbound = await DocAsync(StockDocumentType.Outbound, w1, ProductionOut, ("A-01", item, 3m));
        await ConfirmAsync(outbound, Operator);
        var transfer = await CreateAsync(StockDocumentType.Transfer, w1, null);
        await Docs(s => s.AddLineAsync(transfer, new DocumentLineInput("a-01", item.ToLowerInvariant(), 4m,
            TargetWarehouseCode: c2.ToLowerInvariant(), TargetLocationCode: "b-01"), Operator, Ct));
        await ConfirmAsync(transfer, Operator);

        Assert.Equal(3m, await BalanceAsync(w1, "A-01"));
        Assert.Equal(4m, await BalanceAsync(w2, "B-01"));
        var details = (await Docs(s => s.GetAsync(transfer, Ct)))!;
        Assert.Equal(StockDocumentStatus.Posted, details.Status);
        Assert.Matches("^TR-[0-9]{6}$", details.DocNo);
        Assert.Equal(("B-01", c2), (details.Lines.Single().TargetLocationCode, details.Lines.Single().TargetWarehouseCode));
        Assert.Matches("^IN-[0-9]{6}$", (await Docs(s => s.GetAsync(inbound, Ct)))!.DocNo);
        Assert.Matches("^OUT-[0-9]{6}$", (await Docs(s => s.GetAsync(outbound, Ct)))!.DocNo);
        _ = c1;
    }

    // M8-01：從未使用的倉庫連同儲位真刪除；刪除後列表看不到、帳號範圍與稽核正確
    [Fact]
    public async Task 刪除倉庫_從未使用_連同儲位刪除()
    {
        var (w, code) = await WarehouseAsync("A-01", "A-02");
        var version = (await Warehouses(s => s.ListWarehousesAsync(Ct))).Single(x => x.Id == w).Version;

        Assert.Equal(WarehouseDeleteResult.Removed, await Warehouses(s => s.DeleteWarehouseAsync(w, version, Manager, Ct)));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.False(await db.Warehouses.AnyAsync(x => x.Id == w, Ct));
        Assert.False(await db.Locations.AnyAsync(l => l.WarehouseId == w, Ct));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "warehouse.delete" && a.EntityId == w.ToString(), Ct));
        // 倉庫碼釋出，可以建立同代碼的新倉庫
        await Warehouses(s => s.CreateWarehouseAsync(new WarehouseInput(code, "重建"), Manager, Ct));
    }

    // M8-01：有庫存不能刪；出清後「刪除」＝移除：列表與選單看不到、歷史單據仍顯示倉庫碼、不能直接啟用、可還原
    [Fact]
    public async Task 刪除倉庫_有庫存拒絕_清空後移除_歷史保留_可還原()
    {
        var (w, code) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        var inbound = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 5m));
        await ConfirmAsync(inbound, Operator);
        async Task<uint> WarehouseVersionAsync() => (await Warehouses(s => s.ListWarehousesAsync(true, Ct))).Single(x => x.Id == w).Version;

        var stocked = await MessageAsync(async () => await Warehouses(async s => await s.DeleteWarehouseAsync(w, await WarehouseVersionAsync(), Manager, Ct)));
        Assert.Contains("還有庫存", stocked);

        var outbound = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 5m));
        await ConfirmAsync(outbound, Operator);
        Assert.Equal(WarehouseDeleteResult.Hidden,
            await Warehouses(async s => await s.DeleteWarehouseAsync(w, await WarehouseVersionAsync(), Manager, Ct)));

        Assert.DoesNotContain(await Warehouses(s => s.ListWarehousesAsync(Ct)), x => x.Id == w);
        var hidden = (await Warehouses(s => s.ListWarehousesAsync(true, Ct))).Single(x => x.Id == w);
        Assert.Equal((true, false), (hidden.IsDeleted, hidden.IsActive));
        Assert.Equal(code, (await Docs(s => s.GetAsync(inbound, Ct)))!.WarehouseCode);
        Assert.Contains("請先還原", await MessageAsync(async () =>
            await Warehouses(async s => await s.SetWarehouseActiveAsync(w, await WarehouseVersionAsync(), true, Manager, Ct))));
        Assert.Contains("倉庫代碼已存在", await MessageAsync(() => Warehouses(s => s.CreateWarehouseAsync(new WarehouseInput(code, "重用"), Manager, Ct))));

        await Warehouses(async s => await s.RestoreWarehouseAsync(w, await WarehouseVersionAsync(), Manager, Ct));
        var restored = (await Warehouses(s => s.ListWarehousesAsync(Ct))).Single(x => x.Id == w);
        Assert.Equal((false, false), (restored.IsDeleted, restored.IsActive));
    }

    // M8-01：草稿單、移入這個倉庫的草稿移庫單、未結束的預留都會擋下刪除
    [Fact]
    public async Task 刪除倉庫_有草稿單或移入的移庫單或預留_拒絕()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        async Task<string?> TryDeleteAsync() => await MessageAsync(async () =>
            await Warehouses(async s => await s.DeleteWarehouseAsync(
                w, (await s.ListWarehousesAsync(true, Ct)).Single(x => x.Id == w).Version, Manager, Ct)));

        var draft = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 1m));
        Assert.Contains("草稿或送審中", await TryDeleteAsync());
        await Docs(async s => await s.CancelAsync(draft, await VersionAsync(draft), Operator, Ct));

        var (source, _) = await WarehouseAsync("S-01");
        var inbound = await DocAsync(StockDocumentType.Inbound, source, PurchaseIn, ("S-01", item, 3m));
        await ConfirmAsync(inbound, Operator);
        var targetCode = (await Warehouses(s => s.ListWarehousesAsync(Ct))).Single(x => x.Id == w).Code;
        var transfer = await CreateAsync(StockDocumentType.Transfer, source, null);
        await Docs(s => s.AddLineAsync(transfer, new DocumentLineInput("S-01", item, 1m, TargetWarehouseCode: targetCode, TargetLocationCode: "A-01"), Operator, Ct));
        Assert.Contains("草稿或送審中", await TryDeleteAsync());
        await Docs(async s => await s.CancelAsync(transfer, await VersionAsync(transfer), Operator, Ct));

        // 用過（有已取消的單據）而且清空：可以刪除（移除）
        Assert.Null(await TryDeleteAsync());
    }

    // M8-01：被預留的庫存擋下刪除；已刪除的倉庫不能再開單、新增儲位、設定常用倉位，資料庫也不允許把它改回啟用
    [Fact]
    public async Task 刪除倉庫_預留的庫存擋下_刪除後不能再寫入_資料庫擋啟用()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 2m)), Operator);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
            await new ItemLocationService(db, TimeProvider.System).AddAsync(itemId, w, "A-01", true, Manager, Ct);
        }
        async Task<string?> TryDeleteAsync() => await MessageAsync(async () =>
            await Warehouses(async s => await s.DeleteWarehouseAsync(
                w, (await s.ListWarehousesAsync(true, Ct)).Single(x => x.Id == w).Version, Manager, Ct)));

        var reservation = await ManualReservationAsync(item, w, 1m);
        await ConfirmAsync(await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 1m)), Operator);
        Assert.Contains("還有庫存", await TryDeleteAsync()); // 被預留的量仍是庫存（預留建立時必須有可用量，所以有預留就一定有結存）
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            await new InventoryPostingService(db).ReleaseReservationAsync(reservation, Operator, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        }
        await ConfirmAsync(await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 1m)), Operator);
        Assert.Null(await TryDeleteAsync());

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.False(await db.ItemLocations.AnyAsync(x => x.WarehouseId == w, Ct)); // 常用倉位一起清掉
        }
        Assert.Contains("已刪除", await MessageAsync(() => CreateAsync(StockDocumentType.Inbound, w, PurchaseIn)));
        Assert.Contains("已刪除", await MessageAsync(() =>
            Warehouses(s => s.CreateLocationAsync(w, new LocationInput("A-02", Empty), Manager, Ct))));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
            Assert.Contains("已刪除", await MessageAsync(() =>
                new ItemLocationService(db, TimeProvider.System).AddAsync(itemId, w, "A-01", false, Manager, Ct)));
        }
        // A/B 相容窗口內舊版按「啟用」：資料庫 CHECK 擋下
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"UPDATE warehouses SET is_active = true WHERE id = {w}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    // M8-02：明細輸入的儲位清單：出倉把有庫存的儲位排前（附數量）、進倉把常用儲位排前；不含停用儲位；關鍵字比對碼與名稱
    [Fact]
    public async Task 明細輸入_儲位清單_有庫存或常用的排前面_可輸入縮小範圍()
    {
        var code = "D" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        Guid w;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var service = new WarehouseService(db, TimeProvider.System);
            w = await service.CreateWarehouseAsync(new WarehouseInput(code, "清單測試倉"), Manager, Ct);
            await service.CreateLocationAsync(w, new LocationInput("A-01", Empty with { Name = "一號料架" }), Manager, Ct);
            await service.CreateLocationAsync(w, new LocationInput("A-02", Empty), Manager, Ct);
            await service.CreateLocationAsync(w, new LocationInput("B-01", Empty), Manager, Ct);
            var c01 = await service.CreateLocationAsync(w, new LocationInput("C-01", Empty), Manager, Ct);
            var row = (await service.GetLocationAsync(c01, Ct))!;
            await service.ChangeLocationStatusAsync(c01, row.Version, LocationStatus.Inactive, Manager, Ct);
        }
        var item = await ItemCodeAsync(TrackingMode.None);
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("B-01", item, 3m), ("A-02", item, 5m)), Operator);
        Guid itemId;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
            await new ItemLocationService(db, TimeProvider.System).AddAsync(itemId, w, "A-01", true, Manager, Ct);
        }

        await using var query = MigratedDatabaseFixture.CreateContext();
        var queries = new LineEntryQueryService(query);
        var source = await queries.LocationsAsync(w, null, itemId, LocationListMode.Source, Ct);
        Assert.Equal(["A-02", "B-01", "A-01"], source.Select(o => o.Code)); // 有庫存的依碼排序在前，C-01 停用不列
        Assert.Equal((5m, "Stock"), (source[0].Quantity!.Value, source[0].Reason));
        Assert.Equal(("一號料架", null), (source[2].Name, source[2].Quantity));

        var inbound = await queries.LocationsAsync(w, null, itemId, LocationListMode.Inbound, Ct);
        Assert.Equal(("A-01", "Default"), (inbound[0].Code, inbound[0].Reason));
        Assert.Equal(["A-02", "B-01"], inbound.Skip(1).Select(o => o.Code));

        Assert.Equal("A-01", Assert.Single(await queries.LocationsAsync(w, "一號", itemId, LocationListMode.Source, Ct)).Code);
        Assert.Equal("B-01", Assert.Single(await queries.LocationsAsync(w, "ｂ-0", null, LocationListMode.Plain, Ct)).Code); // 全形小寫也找得到
    }

    // M8-02：批號清單依有效期限先到期先出（沒有期限排後），可指定儲位、可輸入縮小範圍
    [Fact]
    public async Task 明細輸入_批號清單_先到期先出_可指定儲位()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync(TrackingMode.Lot);
        var inbound = await CreateAsync(StockDocumentType.Inbound, w, PurchaseIn);
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", item, 2m, "L1", null, new DateOnly(2027, 1, 1)), Operator, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-02", item, 1m, "L2", null, new DateOnly(2026, 12, 1)), Operator, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-02", item, 4m, "L3"), Operator, Ct));
        await ConfirmAsync(inbound, Operator);

        await using var db = MigratedDatabaseFixture.CreateContext();
        var itemId = await db.Items.Where(i => i.ItemCode == item).Select(i => i.Id).SingleAsync(Ct);
        var a01 = await db.Locations.Where(l => l.WarehouseId == w && l.Code == "A-01").Select(l => l.Id).SingleAsync(Ct);
        var queries = new LineEntryQueryService(db);

        var all = await queries.LotsAsync(w, itemId, null, null, Ct);
        Assert.Equal(["L2", "L1", "L3"], all.Select(o => o.LotNo));
        Assert.Equal((new DateOnly(2026, 12, 1), 1m, "A-02"), (all[0].ExpiryDate!.Value, all[0].Quantity, all[0].LocationCode));
        Assert.Equal("L1", Assert.Single(await queries.LotsAsync(w, itemId, a01, null, Ct)).LotNo);
        Assert.Equal("L3", Assert.Single(await queries.LotsAsync(w, itemId, null, "l3", Ct)).LotNo);
    }

    // M8-03：勾選多行一次刪除；其中一行已不存在時整批不刪
    [Fact]
    public async Task 勾選刪除明細_一次刪多行_有一行不存在就整批不刪()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02", "A-03");
        var item = await ItemCodeAsync(TrackingMode.None);
        var doc = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, [("A-01", item, 1m), ("A-02", item, 2m), ("A-03", item, 3m)], null);
        var lines = (await Docs(s => s.GetAsync(doc, Ct)))!.Lines.Select(l => l.Id).ToList();

        Assert.Contains("找不到", await MessageAsync(() => Docs(s => s.RemoveLinesAsync(doc, [lines[0], Guid.NewGuid()], Operator, Ct))));
        Assert.Equal(3, (await Docs(s => s.GetAsync(doc, Ct)))!.Lines.Count);

        await Docs(s => s.RemoveLinesAsync(doc, [lines[0], lines[2]], Operator, Ct));
        var left = Assert.Single((await Docs(s => s.GetAsync(doc, Ct)))!.Lines);
        Assert.Equal("A-02", left.LocationCode);
        Assert.Contains("勾選", await MessageAsync(() => Docs(s => s.RemoveLinesAsync(doc, [], Operator, Ct))));
    }

    // M8-03：全公司明細欄位開關在服務裡檢查（畫面、API 一致）：關閉後有效期限、預留、跨倉移庫明確拒絕；別人的草稿不能勾選刪除
    [Fact]
    public async Task 明細欄位開關_關閉後明確拒絕_別人不能勾選刪除()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var (_, c2) = await WarehouseAsync("B-01");
        var lotItem = await ItemCodeAsync(TrackingMode.Lot);
        var item = await ItemCodeAsync(TrackingMode.None);
        await SetLineFieldsAsync(false);
        try
        {
            var inbound = await CreateAsync(StockDocumentType.Inbound, w, PurchaseIn);
            Assert.Contains("有效期限", await MessageAsync(() =>
                Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", lotItem, 1m, "L1", null, new DateOnly(2027, 1, 1)), Operator, Ct))));
            await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", item, 5m), Operator, Ct)); // 沒填關閉的欄位照常
            await ConfirmAsync(inbound, Operator);

            var transfer = await CreateAsync(StockDocumentType.Transfer, w, null);
            Assert.Contains("跨倉", await MessageAsync(() =>
                Docs(s => s.AddLineAsync(transfer, new DocumentLineInput("A-01", item, 1m, TargetWarehouseCode: c2, TargetLocationCode: "B-01"), Operator, Ct))));
            await Docs(s => s.AddLineAsync(transfer, new DocumentLineInput("A-01", item, 1m, TargetLocationCode: "A-02"), Operator, Ct)); // 同倉照常

            var outbound = await CreateAsync(StockDocumentType.Outbound, w, ProductionOut);
            Assert.Contains("預留", await MessageAsync(() =>
                Docs(s => s.AddLineAsync(outbound, new DocumentLineInput("A-01", item, 1m, ReservationId: Guid.NewGuid()), Operator, Ct))));

            var line = (await Docs(s => s.GetAsync(transfer, Ct)))!.Lines.Single().Id;
            Assert.NotNull(await MessageAsync(() => Docs(s => s.RemoveLinesAsync(transfer, [line], Operator2, Ct))));
            Assert.Single((await Docs(s => s.GetAsync(transfer, Ct)))!.Lines);
        }
        finally
        {
            await SetLineFieldsAsync(true);
        }
    }

    // T-M4-02-02：草稿不影響結存；審核關閉時不能送審；出庫不足整張回滾、單據仍是草稿
    [Fact]
    public async Task 草稿不影響結存_不能送審_出庫不足整張回滾()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync(TrackingMode.None);
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 5m), ("A-02", item, 1m)), Operator);
        var draft = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 2m), ("A-02", item, 3m));

        Assert.Equal(5m, await BalanceAsync(w, "A-01"));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => SubmitAsync(draft, Operator));
        Assert.Equal("document.approval_disabled", ex.ErrorCode);
        ex = await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(draft, Operator));
        Assert.Equal("stock.insufficient", ex.ErrorCode);

        Assert.Equal((5m, 1m), (await BalanceAsync(w, "A-01"), await BalanceAsync(w, "A-02")));
        Assert.Equal(StockDocumentStatus.Draft, (await Docs(s => s.GetAsync(draft, Ct)))!.Status);
    }

    // T-M4-02-03：用途必填（預設）、方向不符被拒、停用的主檔不可新用
    [Fact]
    public async Task 用途必填_方向不符與停用主檔被拒()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        var noPurpose = await DocAsync(StockDocumentType.Inbound, w, null, ("A-01", item, 1m));

        var rule = await Assert.ThrowsAsync<DomainRuleException>(() => ConfirmAsync(noPurpose, Operator));
        Assert.Contains("用途", rule.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateAsync(StockDocumentType.Inbound, w, ProductionOut));

        var partner = await Master(s => s.CreatePartnerAsync(new PartnerInput(PartnerType.Vendor, "v-" + Suffix(), "測試廠商"), Manager, Ct));
        var version = await PartnerVersionAsync(partner);
        await Master(s => s.SetPartnerActiveAsync(partner, version, false, Manager, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Docs(s => s.CreateAsync(StockDocumentType.Inbound, w, new StockDocumentHeader(PurchaseIn, partner, null, null), Operator, Ct)));
    }

    // T-M4-02-04：審核開啟：確認被拒；出倉單送審即預留；不足無法送審；核准沖銷；退回、取消釋放；自核依設定
    [Fact]
    public async Task 審核開啟_送審預留_核准沖銷_退回取消釋放_自核依設定()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 10m)), Operator);
        await SetApprovalAsync(true);
        try
        {
            var first = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 6m));
            var ex = await Assert.ThrowsAsync<ConflictException>(() => ConfirmAsync(first, Operator));
            Assert.Equal("document.approval_required", ex.ErrorCode);

            await SubmitAsync(first, Operator);
            Assert.Equal(4m, await AvailableAsync(item, w));
            Assert.Equal(10m, await BalanceAsync(w, "A-01"));                       // 待核准不影響結存

            var second = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 5m));
            ex = await Assert.ThrowsAsync<ConflictException>(() => SubmitAsync(second, Operator));
            Assert.Equal("stock.insufficient", ex.ErrorCode);
            Assert.Equal(StockDocumentStatus.Draft, (await Docs(s => s.GetAsync(second, Ct)))!.Status);

            await Assert.ThrowsAsync<PermissionDeniedException>(() => ApproveAsync(first, Operator));
            await ApproveAsync(first, Manager);
            Assert.Equal((4m, 4m), (await BalanceAsync(w, "A-01"), await AvailableAsync(item, w)));
            Assert.Equal(0, await OpenReservationCountAsync(first));

            // 退回、取消釋放預留
            var third = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 4m));
            await SubmitAsync(third, Operator);
            Assert.Equal(0m, await AvailableAsync(item, w));
            await Docs(async s => await s.ReturnAsync(third, await VersionAsync(third), "數量再確認", Manager, Ct));
            Assert.Equal(4m, await AvailableAsync(item, w));
            await SubmitAsync(third, Operator);
            await Docs(async s => await s.CancelAsync(third, await VersionAsync(third), Operator, Ct));
            Assert.Equal(4m, await AvailableAsync(item, w));

            // 自核：預設不允許
            var own = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", item, 1m), creator: Manager);
            await SubmitAsync(own, Manager);
            await Assert.ThrowsAsync<DomainRuleException>(() => ApproveAsync(own, Manager));
            await ApproveAsync(own, Manager2);
            Assert.Equal(3m, await BalanceAsync(w, "A-01"));
        }
        finally
        {
            await SetApprovalAsync(false);
        }
    }

    // T-M4-02-05：冪等：同鍵重送只過帳一次；同鍵用在另一張單被拒；同時確認同一張單只過帳一次
    [Fact]
    public async Task 冪等_同鍵重送只過帳一次_並行確認只過帳一次()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        var doc = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 2m));
        var version = await VersionAsync(doc);
        var key = "key-" + Guid.NewGuid();

        await Docs(s => s.ConfirmAsync(doc, version, key, Operator, Ct));
        await Docs(s => s.ConfirmAsync(doc, version, key, Operator, Ct));
        Assert.Equal(2m, await BalanceAsync(w, "A-01"));
        var other = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 2m));
        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await Docs(async s => await s.ConfirmAsync(other, await VersionAsync(other), key, Operator, Ct)));
        Assert.Equal("idempotency.key_reused", ex.ErrorCode);

        var concurrent = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 5m));
        var v = await VersionAsync(concurrent);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TryAsync(() =>
            Docs(s => s.ConfirmAsync(concurrent, v, "key-" + Guid.NewGuid(), Operator, Ct)))));
        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(7m, await BalanceAsync(w, "A-01"));
    }

    // T-M4-02-06：批號、序號：進倉建立、出倉必須已存在、序號移庫
    [Fact]
    public async Task 批號序號_進倉建立_出倉找不到被拒_序號移庫()
    {
        var (w, _) = await WarehouseAsync("A-01", "A-02");
        var lot = await ItemCodeAsync(TrackingMode.Lot);
        var serial = await ItemCodeAsync(TrackingMode.Serial);
        var inbound = await CreateAsync(StockDocumentType.Inbound, w, PurchaseIn);
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", lot, 5m, LotNo: "l-1", ExpiryDate: new DateOnly(2027, 1, 31)), Operator, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("A-01", serial, 1m, SerialNo: "sn-1"), Operator, Ct));
        await ConfirmAsync(inbound, Operator);

        var wrongLot = await CreateAsync(StockDocumentType.Outbound, w, ProductionOut);
        await Docs(s => s.AddLineAsync(wrongLot, new DocumentLineInput("A-01", lot, 1m, LotNo: "L-404"), Operator, Ct));
        var rule = await Assert.ThrowsAsync<DomainRuleException>(() => ConfirmAsync(wrongLot, Operator));
        Assert.Contains("L-404", rule.Message);

        var move = await CreateAsync(StockDocumentType.Transfer, w, null);
        await Docs(s => s.AddLineAsync(move, new DocumentLineInput("A-01", serial, 1m, SerialNo: "SN-1", TargetLocationCode: "A-02"), Operator, Ct));
        await Docs(s => s.AddLineAsync(move, new DocumentLineInput("A-01", lot, 2m, LotNo: "L-1", TargetLocationCode: "A-02"), Operator, Ct));
        await ConfirmAsync(move, Operator);

        Assert.Equal(1m, await BalanceAsync(w, "A-02", serial));
        Assert.Equal(0m, await BalanceAsync(w, "A-01", serial));
        Assert.Equal((3m, 2m), (await BalanceAsync(w, "A-01", lot), await BalanceAsync(w, "A-02", lot)));
    }

    // T-M4-02-07：沖銷單：方向相反、結存回復；一張單只能有一張有效沖銷單；沖銷單不能再沖銷
    [Fact]
    public async Task 沖銷單_結存回復_只能一張()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        var inbound = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 8m));
        await ConfirmAsync(inbound, Operator);

        var reversal = await Docs(s => s.CreateReversalAsync(inbound, Operator, Ct));
        var details = (await Docs(s => s.GetAsync(reversal, Ct)))!;
        Assert.Equal((StockDocumentType.Outbound, inbound), (details.DocType, details.ReversalOfDocumentId));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Docs(s => s.CreateReversalAsync(inbound, Operator, Ct)));
        Assert.Equal("document.reversal_exists", ex.ErrorCode);

        await ConfirmAsync(reversal, Operator);  // 沖銷單不需要用途
        Assert.Equal(0m, await BalanceAsync(w, "A-01"));
        await Assert.ThrowsAsync<DomainRuleException>(() => Docs(s => s.CreateReversalAsync(reversal, Operator, Ct)));
    }

    // T-M4-02-08：草稿只能由建立者或主管修改；已過帳不可再改；期初單不出現在進出倉清單
    [Fact]
    public async Task 權限與狀態_別人不能改_已過帳不能改_期初不在清單()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var item = await ItemCodeAsync(TrackingMode.None);
        var doc = await DocAsync(StockDocumentType.Inbound, w, PurchaseIn, ("A-01", item, 1m));

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item, 1m, LotNo: null), Operator2, Ct)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ConfirmAsync(doc, Operator2));
        await ConfirmAsync(doc, Manager);   // 主管可以確認別人的單
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item, 1m), Operator, Ct)));

        var opening = await Opening(s => s.CreateAsync(w, null, Operator, Ct));
        Assert.Null(await Docs(s => s.GetAsync(opening, Ct)));
        var list = await Docs(s => s.ListAsync(null, null, w, 1, Ct));
        Assert.DoesNotContain(list.Items, r => r.Id == opening);
        Assert.Contains(list.Items, r => r.Id == doc && r.Status == StockDocumentStatus.Posted);
    }

    // T-M4-02-09：主檔：同類型代碼唯一（轉大寫）、不同類型可同代碼；用途停用後清單不列
    [Fact]
    public async Task 主檔_代碼唯一_停用不列()
    {
        var code = "c-" + Suffix();
        await Master(s => s.CreatePartnerAsync(new PartnerInput(PartnerType.Customer, code, "客戶甲"), Manager, Ct));
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Master(s => s.CreatePartnerAsync(new PartnerInput(PartnerType.Customer, code.ToUpperInvariant(), "客戶乙"), Manager, Ct)));
        Assert.Equal("master.code_exists", ex.ErrorCode);
        await Master(s => s.CreatePartnerAsync(new PartnerInput(PartnerType.Department, code, "部門"), Manager, Ct));
        var found = await Master(s => s.ListPartnersAsync(null, code, false, 1, Ct));
        Assert.Equal(2, found.Items.Count);

        var purpose = await Master(s => s.CreatePurposeAsync(new PurposeInput(PurposeDirection.Out, "p-" + Suffix(), "測試用途", 99), ItemManager, Ct));
        var version = (await Master(s => s.ListPurposesAsync(PurposeDirection.Out, true, Ct))).Single(p => p.Id == purpose).Version;
        await Master(s => s.SetPurposeActiveAsync(purpose, version, false, ItemManager, Ct));
        Assert.DoesNotContain(await Master(s => s.ListPurposesAsync(PurposeDirection.Out, false, Ct)), p => p.Id == purpose);
        var (w, _) = await WarehouseAsync("A-01");
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateAsync(StockDocumentType.Outbound, w, purpose));
    }

    // T-M4-02-11（審查 #1、#2、#4、#6）：審核開啟時——停用物料可送審出倉；不能指定別張單的送審預留；
    // 負庫存物料只預留有的部分；指定手動預留時超出部分另補預留，核准時各自沖銷
    [Fact]
    public async Task 審核開啟_停用物料_他單預留_負庫存_手動預留超出()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var inactive = await ItemCodeAsync(TrackingMode.None);
        var negative = await ItemCodeAsync(TrackingMode.None);
        var plain = await ItemCodeAsync(TrackingMode.None);
        await ConfirmAsync(await DocAsync(StockDocumentType.Inbound, w, PurchaseIn,
            [("A-01", inactive, 3m), ("A-01", negative, 2m), ("A-01", plain, 10m)], null), Operator);
        await SqlAsync($"UPDATE items SET status = 'Inactive' WHERE item_code = {inactive}");
        await SqlAsync($"UPDATE items SET allow_negative_stock = true, negative_stock_limit = 10 WHERE item_code = {negative}");
        var manual = await ManualReservationAsync(plain, w, 3m);
        await SetApprovalAsync(true);
        try
        {
            // 停用物料：送審、核准都可以（清空庫存）
            var clear = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", inactive, 3m));
            await SubmitAsync(clear, Operator);
            await ApproveAsync(clear, Manager);
            Assert.Equal(0m, await BalanceAsync(w, "A-01", inactive));

            // 負庫存物料：結存 2、出 5 → 只預留 2；核准後 −3
            var neg = await DocAsync(StockDocumentType.Outbound, w, ProductionOut, ("A-01", negative, 5m));
            await SubmitAsync(neg, Operator);
            Assert.Equal(0m, await AvailableAsync(negative, w));
            await ApproveAsync(neg, Manager);
            Assert.Equal(-3m, await BalanceAsync(w, "A-01", negative));

            // 手動預留 3、明細 8 → 送審補預留 5；可用 10 − 3 − 5 = 2
            var withManual = await CreateAsync(StockDocumentType.Outbound, w, ProductionOut);
            await Docs(s => s.AddLineAsync(withManual, new DocumentLineInput("A-01", plain, 8m, ReservationId: manual), Operator, Ct));
            await SubmitAsync(withManual, Operator);
            Assert.Equal(2m, await AvailableAsync(plain, w));

            // 別張單不能指定這張單送審時建立的預留
            var stolen = (await OpenReservationIdsAsync(withManual)).Single();
            var thief = await CreateAsync(StockDocumentType.Outbound, w, ProductionOut);
            var ex = await Assert.ThrowsAsync<ConflictException>(() =>
                Docs(s => s.AddLineAsync(thief, new DocumentLineInput("A-01", plain, 1m, ReservationId: stolen), Operator, Ct)));
            Assert.Equal("reservation.not_selectable", ex.ErrorCode);

            await ApproveAsync(withManual, Manager);
            Assert.Equal((2m, 2m), (await BalanceAsync(w, "A-01", plain), await AvailableAsync(plain, w)));
            Assert.Equal(0, await OpenReservationCountAsync(withManual));
        }
        finally
        {
            await SetApprovalAsync(false);
        }
    }

    // T-M4-02-12（審查 #5）：500 行序號出倉單一次確認，批號／序號與回填都批次處理，記錄耗時
    [Fact]
    public async Task 五百行序號出倉_確認耗時()
    {
        var codes = Enumerable.Range(1, 5).Select(i => $"S-{i:D2}").ToArray();
        var (w, _) = await WarehouseAsync(codes);
        var serial = await ItemCodeAsync(TrackingMode.Serial);
        var serials = Enumerable.Range(1, 500).Select(i => $"SN{i:D5}").ToList();
        var inbound = await CreateAsync(StockDocumentType.Inbound, w, PurchaseIn);
        var outbound = await CreateAsync(StockDocumentType.Outbound, w, ProductionOut);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            // 直接在 Domain 加 500 行（逐行呼叫 API 會讓這個測試變很慢，不是要量的部分）
            var itemId = await db.Items.Where(i => i.ItemCode == serial).Select(i => i.Id).SingleAsync(Ct);
            var locations = await db.Locations.Where(l => l.WarehouseId == w).ToDictionaryAsync(l => l.Code, l => l.Id, Ct);
            foreach (var id in new[] { inbound, outbound })
            {
                var doc = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == id, Ct);
                for (var i = 0; i < serials.Count; i++)
                {
                    doc.AddMovementLine(Guid.NewGuid(), itemId, TrackingMode.Serial, 0, locations[codes[i % codes.Length]], null,
                        null, serials[i], null, 1m, null);
                }
            }
            await db.SaveChangesAsync(Ct);
        }
        await ConfirmAsync(inbound, Operator);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await ConfirmAsync(outbound, Operator);
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"500 行序號出倉確認：{watch.ElapsedMilliseconds} ms");
        Assert.Equal(0m, await BalanceAsync(w, "S-01", serial));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
    }

    // T-M4-02-13（M4-03 審查 #1 #13）：同一個請求 ID 建單兩次只有一張、別人用同一個 ID 被拒；往來對象代碼精確查詢不受相似代碼影響
    [Fact]
    public async Task 建單請求ID冪等_往來對象代碼精確查詢()
    {
        var (w, _) = await WarehouseAsync("A-01");
        var requestId = Guid.NewGuid();
        var header = new StockDocumentHeader(PurchaseIn, null, null, null);
        var first = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w, header, Operator, Ct, requestId));
        Assert.Equal(first, await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w, header, Operator, Ct, requestId)));
        Assert.Equal(requestId, first);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Docs(s => s.CreateAsync(StockDocumentType.Inbound, w, header, Operator2, Ct, requestId)));
        Assert.Equal("document.request_id_conflict", ex.ErrorCode);

        var prefix = "Z" + Guid.NewGuid().ToString("N")[..5];
        for (var i = 0; i < 21; i++)
        {
            await Master(s => s.CreatePartnerAsync(new PartnerInput(PartnerType.Vendor, $"{prefix}{i:D2}", "相似廠商"), Manager, Ct));
        }
        var target = await Master(s => s.CreatePartnerAsync(new PartnerInput(PartnerType.Vendor, prefix, "目標廠商"), Manager, Ct));
        var found = await Master(s => s.FindActivePartnersByCodeAsync(prefix.ToLowerInvariant(), Ct));
        Assert.Equal(target, Assert.Single(found).Id);
    }

    // ---- 輔助 ----

    private static async Task SqlAsync(FormattableString sql)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await db.Database.ExecuteSqlAsync(sql, Ct);
    }

    private static async Task<Guid> ManualReservationAsync(string itemCode, Guid warehouse, decimal quantity)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var itemId = await db.Items.Where(i => i.ItemCode == itemCode).Select(i => i.Id).SingleAsync(Ct);
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var id = await new InventoryPostingService(db).ReserveAsync(
            new ReservationRequest(itemId, warehouse, quantity, "Manual", null, null), Operator, DateTimeOffset.UtcNow, Ct);
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private static async Task<List<Guid>> OpenReservationIdsAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockReservations.Where(r => r.ReferenceId == doc && r.Status == ReservationStatus.Open).Select(r => r.Id).ToListAsync(Ct);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<Guid> CreateAsync(StockDocumentType type, Guid warehouse, Guid? purpose, Actor? creator = null) =>
        await Docs(s => s.CreateAsync(type, warehouse, new StockDocumentHeader(purpose, null, null, null), creator ?? Operator, Ct));

    private static async Task<Guid> DocAsync(
        StockDocumentType type, Guid warehouse, Guid? purpose, (string Location, string Item, decimal Qty) line, Actor? creator = null) =>
        await DocAsync(type, warehouse, purpose, [line], creator);

    private static async Task<Guid> DocAsync(
        StockDocumentType type, Guid warehouse, Guid? purpose, (string Location, string Item, decimal Qty) line1,
        (string Location, string Item, decimal Qty) line2) =>
        await DocAsync(type, warehouse, purpose, [line1, line2], null);

    private static async Task<Guid> DocAsync(
        StockDocumentType type, Guid warehouse, Guid? purpose, (string Location, string Item, decimal Qty)[] lines, Actor? creator)
    {
        var doc = await CreateAsync(type, warehouse, purpose, creator);
        foreach (var (location, item, qty) in lines)
        {
            await Docs(s => s.AddLineAsync(doc, new DocumentLineInput(location, item, qty), creator ?? Operator, Ct));
        }
        return doc;
    }

    private static async Task ConfirmAsync(Guid doc, Actor actor) =>
        await Docs(async s => await s.ConfirmAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), actor, Ct));

    private static async Task SubmitAsync(Guid doc, Actor actor) =>
        await Docs(async s => await s.SubmitAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), actor, Ct));

    private static async Task ApproveAsync(Guid doc, Actor actor) =>
        await Docs(async s => await s.ApproveAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), actor, Ct));

    /// <summary>執行並回傳錯誤訊息（成功回傳 null），用來確認是哪一條規則擋下。</summary>
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

    private static async Task<string?> TryAsync(Func<Task> action)
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
        catch (DomainRuleException)
        {
            return "domain_rule";
        }
    }

    private static async Task SetLineFieldsAsync(bool enabled)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new SettingsService(db, TimeProvider.System);
        var current = await service.GetAsync(Ct);
        var updated = current with { LineExpiryEnabled = enabled, LineReservationEnabled = enabled, LineCrossTransferEnabled = enabled };
        if (updated != current)
        {
            await service.UpdateAsync(current, updated, Admin, Ct);
        }
    }

    private static async Task SetApprovalAsync(bool enabled)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new SettingsService(db, TimeProvider.System);
        var current = await service.GetAsync(Ct);
        if (current.DocumentApprovalEnabled != enabled)
        {
            await service.UpdateAsync(current, current with { DocumentApprovalEnabled = enabled }, Admin, Ct);
        }
    }

    private static async Task<decimal> BalanceAsync(Guid warehouse, string location, string? itemCode = null)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await (from b in db.StockBalances
                      join l in db.Locations on b.LocationId equals l.Id
                      join i in db.Items on b.ItemId equals i.Id
                      where l.WarehouseId == warehouse && l.Code == location && (itemCode == null || i.ItemCode == itemCode)
                      select b.Quantity).SumAsync(Ct);
    }

    private static async Task<decimal> AvailableAsync(string itemCode, Guid warehouse)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var itemId = await db.Items.Where(i => i.ItemCode == itemCode).Select(i => i.Id).SingleAsync(Ct);
        var stock = await (from b in db.StockBalances
                           join l in db.Locations on b.LocationId equals l.Id
                           where b.ItemId == itemId && l.WarehouseId == warehouse && b.StockStatus == StockStatus.Available
                           select b.Quantity).SumAsync(Ct);
        var reserved = await db.StockReservations
            .Where(r => r.ItemId == itemId && r.WarehouseId == warehouse && r.Status == ReservationStatus.Open)
            .SumAsync(r => r.RemainingQuantity, Ct);
        return stock - reserved;
    }

    private static async Task<int> OpenReservationCountAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockReservations.CountAsync(r => r.ReferenceId == doc && r.Status == ReservationStatus.Open, Ct);
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<uint> PartnerVersionAsync(Guid partner)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.BusinessPartners.Where(p => p.Id == partner).Select(p => EF.Property<uint>(p, "Version")).SingleAsync(Ct);
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "D" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "單據測試倉"), Manager, Ct);
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
            new ItemDraft("單據物料 " + Suffix(), El, Pcs, tracking, null, null, null), ItemManager, Ct);
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

    private static async Task<T> Warehouses<T>(Func<IWarehouseService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task Warehouses(Func<IWarehouseService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task<T> Master<T>(Func<IDocumentMasterDataService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new DocumentMasterDataService(db, TimeProvider.System));
    }

    private static async Task Master(Func<IDocumentMasterDataService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new DocumentMasterDataService(db, TimeProvider.System));
    }

    private static async Task<T> Opening<T>(Func<IOpeningService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }
}
