using Microsoft.EntityFrameworkCore;
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
