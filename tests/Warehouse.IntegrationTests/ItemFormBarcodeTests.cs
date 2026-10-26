using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Scanning;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Scanning;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M8-05 物料表單一起登錄原廠條碼、提出時自動核准、核准後改基本單位／追蹤模式（ADR-0023），真實 PostgreSQL。
/// 每個測試建立自己的分類（獨立料號前綴）、物料、倉庫，互不干擾。
/// 會切換系統設定「有異動的物料可改單位／追蹤模式」的測試一律在 finally 還原為關閉
/// （同一個 Collection 內測試依序執行，不會影響其他測試）。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemFormBarcodeTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid SetUnit = SeedData.Units.Single(u => u.Code == "SET").Id;
    private static readonly Guid Kg = SeedData.Units.Single(u => u.Code == "KG").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Guid ProductionOut = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.Out && p.Code == "PRODUCTION").Id;
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員甲", IsItemManager: false);
    private static readonly Actor WarehouseManager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly LocationDetails EmptyLocation = new(null, null, null, null, null, null, null, null, false, false);
    private static int s_prefixCounter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Approved(Guid Id, string Code, Guid Category, string Name);

    // ---- 提出時登錄條碼 ----

    // 項目 4：待核准時條碼已登錄但掃碼不可解析；核准後可解析且外箱包裝數量 10
    [Fact]
    public async Task 作業員提出帶兩個條碼_待核准時掃碼被拒_核准後解析到物料且外箱數量為10()
    {
        var category = await CreateCategoryAsync();
        var single = Barcode();
        var box = Barcode();

        var result = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "條碼物料 " + category),
            new ItemSubmitOptions([new ItemBarcodeDraft(single), new ItemBarcodeDraft(box, 10m)], false, []), Operator, Ct));

        Assert.Null(result.ItemCode);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.Equal(ItemStatus.PendingApproval, (await db.Items.SingleAsync(i => i.Id == result.ItemId, Ct)).Status);
            var barcodes = await db.ItemBarcodes.Where(b => b.ItemId == result.ItemId).ToListAsync(Ct);
            Assert.Equal(2, barcodes.Count);
            Assert.All(barcodes, b => Assert.True(b.IsActive));
            Assert.Equal(10m, barcodes.Single(b => b.Text == box).PackQuantity);
            Assert.Equal(1m, barcodes.Single(b => b.Text == single).PackQuantity);
        }
        Assert.Equal(ScanResultKind.Rejected, (await ResolveAsync(single)).Kind);
        Assert.Equal(ScanResultKind.Rejected, (await ResolveAsync(box)).Kind);

        await Workflow(async s => await s.ApproveAsync(result.ItemId, await VersionAsync(result.ItemId), [], null, ItemManager, Ct));

        var byBox = await ResolveAsync(box);
        Assert.Equal(ScanResultKind.Item, byBox.Kind);
        Assert.Equal((result.ItemId, ScanMatch.Barcode, 10m), (byBox.Item!.Id, byBox.Item.MatchedBy, byBox.Item.PackQuantity));
        Assert.Equal(1m, (await ResolveAsync(single)).Item!.PackQuantity);
    }

    // 項目 5：任一條碼被別的有效物料占用 → 整筆不建立（交易回滾）
    [Fact]
    public async Task 其中一個條碼已被別的有效物料使用_整筆不建立_另一個條碼也沒登錄()
    {
        var category = await CreateCategoryAsync();
        var other = await ApprovedItemAsync();
        var taken = Barcode();
        await Index(s => s.AddAsync(other.Id, ItemIndexKind.Barcode, taken, Operator, Ct));
        var fresh = Barcode();
        var requestId = Guid.NewGuid();

        // fresh 排在前面：它先登錄成功，之後 taken 衝突 → 驗證 fresh 也被回滾
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Workflow(s => s.SubmitAsync(requestId,
            Draft(category, "衝突條碼 " + category),
            new ItemSubmitOptions([new ItemBarcodeDraft(fresh), new ItemBarcodeDraft(taken)], false, []), Operator, Ct)));

        Assert.Equal("index.barcode_taken", ex.ErrorCode);
        Assert.Contains(taken, ex.Message);
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.False(await db.Items.AnyAsync(i => i.Id == requestId, Ct));
        Assert.False(await db.ItemReviews.AnyAsync(r => r.ItemId == requestId, Ct));
        Assert.False(await db.ItemBarcodes.AnyAsync(b => b.Text == fresh, Ct));
        Assert.Equal(1, await db.ItemBarcodes.CountAsync(b => b.Text == taken, Ct));
    }

    // 項目 6：冪等
    [Fact]
    public async Task 同一個請求ID重送_回傳同一個物料_不重複登錄條碼()
    {
        var category = await CreateCategoryAsync();
        var requestId = Guid.NewGuid();
        var code = Barcode();
        var options = new ItemSubmitOptions([new ItemBarcodeDraft(code, 6m)], false, []);
        var draft = Draft(category, "冪等條碼 " + category);

        var first = await Workflow(s => s.SubmitAsync(requestId, draft, options, Operator, Ct));
        var again = await Workflow(s => s.SubmitAsync(requestId, draft, options, Operator, Ct));

        Assert.Equal(first.ItemId, again.ItemId);
        Assert.Equal(first.RequestNo, again.RequestNo);
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ItemBarcodes.CountAsync(b => b.Text == code, Ct));
        Assert.Equal(1, await db.ItemReviews.CountAsync(r => r.ItemId == requestId, Ct));
    }

    // 項目 6（並行）：同時送達的相同請求，只產生一個物料、一筆條碼
    [Fact]
    public async Task 同一個請求ID同時送達_只產生一個物料與一筆條碼()
    {
        var category = await CreateCategoryAsync();
        var requestId = Guid.NewGuid();
        var code = Barcode();
        var options = new ItemSubmitOptions([new ItemBarcodeDraft(code)], false, []);
        var draft = Draft(category, "並行冪等 " + category);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Workflow(s => s.SubmitAsync(requestId, draft, options, Operator, Ct))));

        Assert.All(results, r => Assert.Equal(requestId, r.ItemId));
        Assert.Single(results.Select(r => r.RequestNo).Distinct());
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ItemBarcodes.CountAsync(b => b.Text == code, Ct));
        Assert.Equal(1, await db.ItemReviews.CountAsync(r => r.ItemId == requestId && r.Action == ItemReviewAction.Submit, Ct));
    }

    // ---- 提出時自動核准 ----

    // 項目 7
    [Fact]
    public async Task 物料管理員自動核准_發出料號_審核紀錄兩筆_重送不產生第二個料號()
    {
        var category = await CreateCategoryAsync();
        var prefix = await PrefixOfAsync(category);
        var requestId = Guid.NewGuid();
        var code = Barcode();
        var options = new ItemSubmitOptions([new ItemBarcodeDraft(code, 10m)], true, []);
        var draft = Draft(category, "自動核准 " + category);

        var first = await Workflow(s => s.SubmitAsync(requestId, draft, options, ItemManager, Ct));

        Assert.NotNull(first.ItemCode);
        Assert.Equal($"{prefix}-000001", first.ItemCode);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var item = await db.Items.SingleAsync(i => i.Id == requestId, Ct);
            Assert.Equal(ItemStatus.Active, item.Status);
            Assert.Equal(first.ItemCode, item.ItemCode);
            var reviews = await db.ItemReviews.Where(r => r.ItemId == requestId).ToListAsync(Ct);
            Assert.Equal(2, reviews.Count);
            Assert.Single(reviews, r => r.Action == ItemReviewAction.Submit);
            var approve = Assert.Single(reviews, r => r.Action == ItemReviewAction.Approve);
            Assert.Equal("提出時自動核准", approve.Comment);
            Assert.Equal(first.ItemCode, approve.ItemCode);
        }
        // 核准後條碼立刻可掃
        Assert.Equal(requestId, (await ResolveAsync(code)).Item!.Id);

        var again = await Workflow(s => s.SubmitAsync(requestId, draft, options, ItemManager, Ct));

        Assert.Equal(requestId, again.ItemId);
        Assert.Equal(1L, await CounterAsync(prefix));
        // 契約註解：重送時已核准的物料要回傳正式料號
        Assert.Equal(first.ItemCode, again.ItemCode);
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(2, await check.ItemReviews.CountAsync(r => r.ItemId == requestId, Ct));
    }

    // 項目 8
    [Fact]
    public async Task 作業員要求自動核准_被拒_物料沒有建立()
    {
        var category = await CreateCategoryAsync();
        var requestId = Guid.NewGuid();
        var code = Barcode();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => Workflow(s => s.SubmitAsync(requestId,
            Draft(category, "越權核准 " + category), new ItemSubmitOptions([new ItemBarcodeDraft(code)], true, []), Operator, Ct)));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.False(await db.Items.AnyAsync(i => i.Id == requestId, Ct));
        Assert.False(await db.ItemBarcodes.AnyAsync(b => b.Text == code, Ct));
    }

    // 項目 9
    [Fact]
    public async Task 自動核准有未確認的重複候選_整筆不建立_確認後成功()
    {
        var category = await CreateCategoryAsync();
        var name = "重複候選 " + category;
        var existing = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name), Operator, Ct));
        var requestId = Guid.NewGuid();
        var code = Barcode();
        var draft = Draft(category, name);

        var ex = await Assert.ThrowsAsync<DuplicateCandidatesUnconfirmedException>(() => Workflow(s => s.SubmitAsync(requestId,
            draft, new ItemSubmitOptions([new ItemBarcodeDraft(code)], true, []), ItemManager, Ct)));

        Assert.Contains(ex.Candidates, c => c.ItemId == existing.ItemId);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.False(await db.Items.AnyAsync(i => i.Id == requestId, Ct));
            Assert.False(await db.ItemBarcodes.AnyAsync(b => b.Text == code, Ct));
        }

        var ok = await Workflow(s => s.SubmitAsync(requestId, draft,
            new ItemSubmitOptions([new ItemBarcodeDraft(code)], true, [existing.ItemId]), ItemManager, Ct));

        Assert.NotNull(ok.ItemCode);
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(ItemStatus.Active, (await check.Items.SingleAsync(i => i.Id == requestId, Ct)).Status);
        Assert.True(await check.ItemBarcodes.AnyAsync(b => b.Text == code && b.IsActive, Ct));
    }

    // ---- 拒絕／撤回釋放條碼 ----

    // 項目 10
    [Theory]
    [InlineData("reject")]
    [InlineData("withdraw")]
    public async Task 待核准物料被拒絕或撤回_條碼停用_可再登錄給別的物料(string how)
    {
        var category = await CreateCategoryAsync();
        var code = Barcode();
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"釋放條碼 {how} {category}"),
            new ItemSubmitOptions([new ItemBarcodeDraft(code, 4m)], false, []), Operator, Ct));
        var other = await ApprovedItemAsync();
        // 釋放前仍被占用
        var taken = await Assert.ThrowsAsync<ConflictException>(() => Index(s => s.AddAsync(other.Id, ItemIndexKind.Barcode, code, Operator, Ct)));
        Assert.Equal("index.barcode_taken", taken.ErrorCode);

        if (how == "reject")
        {
            await Workflow(async s => await s.RejectAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), "不需要", null, ItemManager, Ct));
        }
        else
        {
            await Workflow(async s => await s.WithdrawAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), null, Operator, Ct));
        }

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var barcode = await db.ItemBarcodes.SingleAsync(b => b.ItemId == submitted.ItemId, Ct);
            Assert.False(barcode.IsActive);
        }
        await Index(s => s.AddAsync(other.Id, ItemIndexKind.Barcode, code, Operator, Ct, 2m));
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await check.ItemBarcodes.CountAsync(b => b.Text == code && b.IsActive, Ct));
        Assert.Equal(other.Id, (await ResolveAsync(code)).Item!.Id);
    }

    // ---- 修改時新增／停用條碼 ----

    // 項目 11
    [Fact]
    public async Task 修改時同一次呼叫新增並停用條碼()
    {
        var category = await CreateCategoryAsync();
        var oldCode = Barcode();
        var newCode = Barcode();
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "改條碼 " + category),
            new ItemSubmitOptions([new ItemBarcodeDraft(oldCode)], false, []), Operator, Ct));
        Guid oldId;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            oldId = await db.ItemBarcodes.Where(b => b.Text == oldCode).Select(b => b.Id).SingleAsync(Ct);
        }

        await Workflow(async s => await s.UpdateAsync(submitted.ItemId, await VersionAsync(submitted.ItemId),
            Draft(category, "改條碼 " + category + " v2"), [],
            new ItemUpdateOptions([new ItemBarcodeDraft(newCode, 5m)], [oldId], false, null), Operator, Ct));

        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.False((await check.ItemBarcodes.SingleAsync(b => b.Id == oldId, Ct)).IsActive);
        var added = await check.ItemBarcodes.SingleAsync(b => b.Text == newCode, Ct);
        Assert.True(added.IsActive);
        Assert.Equal((submitted.ItemId, 5m), (added.ItemId, added.PackQuantity));
        Assert.Equal("改條碼 " + category + " v2", (await check.Items.SingleAsync(i => i.Id == submitted.ItemId, Ct)).Name);
    }

    // ---- 核准後改基本單位／追蹤模式 ----

    // 項目 12
    [Fact]
    public async Task 核准後沒有任何異動_管理員可直接改基本單位()
    {
        var item = await ApprovedItemAsync();

        var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, Kg, TrackingMode.None, Ct));
        Assert.False(check.HasActivity);
        Assert.True(check.CanChange);

        await Workflow(async s => await s.UpdateAsync(item.Id, await VersionAsync(item.Id), DraftOf(item, unit: Kg), [], ItemManager, Ct));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(Kg, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId);
    }

    // 項目 13
    [Fact]
    public async Task 有異動且設定關閉_改單位被擋_檢查回傳阻擋原因()
    {
        var (item, _) = await StockedItemAsync(5m);
        await SetUnitChangeSettingAsync(false);

        var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, SetUnit, TrackingMode.None, Ct));
        Assert.True(check.HasActivity);
        Assert.False(check.CanChange);
        Assert.NotEmpty(check.Blockers);

        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow(async s => await s.UpdateAsync(
            item.Id, await VersionAsync(item.Id), DraftOf(item, unit: SetUnit), [],
            new ItemUpdateOptions([], [], true, "測試原因"), ItemManager, Ct)));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(Pcs, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId);
    }

    // 項目 14
    [Fact]
    public async Task 有異動且設定開啟_沒勾選或沒原因被擋_勾選並填原因成功且寫入審核紀錄()
    {
        var (item, _) = await StockedItemAsync(5m);
        await WithSettingEnabledAsync(async () =>
        {
            var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, SetUnit, TrackingMode.None, Ct));
            Assert.True(check.HasActivity);
            Assert.True(check.CanChange);

            await Assert.ThrowsAsync<DomainRuleException>(() => UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], false, "有原因但沒勾選")));
            await Assert.ThrowsAsync<DomainRuleException>(() => UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, null)));
            await Assert.ThrowsAsync<DomainRuleException>(() => UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, "   ")));
            await using (var db = MigratedDatabaseFixture.CreateContext())
            {
                Assert.Equal(Pcs, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId);
            }

            await UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, "盤點後改用組為單位"));

            await using var after = MigratedDatabaseFixture.CreateContext();
            Assert.Equal(SetUnit, (await after.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId);
            var update = await after.ItemReviews.Where(r => r.ItemId == item.Id && r.Action == ItemReviewAction.Update)
                .OrderByDescending(r => r.OccurredAt).FirstAsync(Ct);
            Assert.Contains("盤點後改用組為單位", update.Comment);
        });
    }

    // 項目 15-a
    [Fact]
    public async Task 設定開啟但有草稿單據明細含此物料_被擋()
    {
        var (item, warehouse) = await StockedItemAsync(5m);
        var draftDoc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(draftDoc, new DocumentLineInput("A-01", item.Code, 1), Operator, Ct));

        await WithSettingEnabledAsync(async () =>
        {
            var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, SetUnit, TrackingMode.None, Ct));
            Assert.False(check.CanChange);
            Assert.Contains(check.Blockers, b => b.Contains("單據"));
            await Assert.ThrowsAsync<DomainRuleException>(() => UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, "原因")));

            // 移除草稿明細後即可
            var lineId = (await Docs(s => s.GetAsync(draftDoc, Ct)))!.Lines.Single().Id;
            await Docs(s => s.RemoveLineAsync(draftDoc, lineId, Operator, Ct));
            await UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, "原因"));
        });
    }

    // 項目 15-b
    [Fact]
    public async Task 設定開啟但有Open預留_被擋_釋放後可改()
    {
        var (item, warehouse) = await StockedItemAsync(5m);
        var reservation = await Reservations(s => s.CreateAsync(new ReservationInput(item.Code, warehouse, 2m, null, null),
            "key-" + Guid.NewGuid(), WarehouseManager, Ct));

        await WithSettingEnabledAsync(async () =>
        {
            var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, SetUnit, TrackingMode.None, Ct));
            Assert.False(check.CanChange);
            Assert.Contains(check.Blockers, b => b.Contains("預留"));
            await Assert.ThrowsAsync<DomainRuleException>(() => UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, "原因")));

            await Reservations(s => s.CancelAsync(reservation, WarehouseManager, Ct));
            await UpdateUnitAsync(item, SetUnit, new ItemUpdateOptions([], [], true, "原因"));
        });
    }

    // 項目 15-c
    [Fact]
    public async Task 設定開啟_追蹤模式None改Lot且結存不為0被擋_結存歸零後成功()
    {
        var (item, warehouse) = await StockedItemAsync(5m);
        await WithSettingEnabledAsync(async () =>
        {
            var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, Pcs, TrackingMode.Lot, Ct));
            Assert.False(check.CanChange);
            Assert.Contains(check.Blockers, b => b.Contains("結存"));
            await Assert.ThrowsAsync<DomainRuleException>(() => Workflow(async s => await s.UpdateAsync(
                item.Id, await VersionAsync(item.Id), DraftOf(item, tracking: TrackingMode.Lot), [],
                new ItemUpdateOptions([], [], true, "原因"), ItemManager, Ct)));

            // 全部出庫，結存歸零（過帳後的單據不算未完成）
            var outbound = await Docs(s => s.CreateAsync(StockDocumentType.Outbound, warehouse, new StockDocumentHeader(ProductionOut, null, null, null), Operator, Ct));
            await Docs(s => s.AddLineAsync(outbound, new DocumentLineInput("A-01", item.Code, 5m), Operator, Ct));
            await ConfirmAsync(outbound);

            await Workflow(async s => await s.UpdateAsync(
                item.Id, await VersionAsync(item.Id), DraftOf(item, tracking: TrackingMode.Lot), [],
                new ItemUpdateOptions([], [], true, "改批號管理"), ItemManager, Ct));

            await using var db = MigratedDatabaseFixture.CreateContext();
            Assert.Equal(TrackingMode.Lot, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).TrackingMode);
        });
    }

    // 項目 16
    [Fact]
    public async Task 有外箱條碼_包裝數量10_改成序號管理被擋()
    {
        var item = await ApprovedItemAsync();
        await Index(s => s.AddAsync(item.Id, ItemIndexKind.Barcode, Barcode(), Operator, Ct, 10m));

        var check = await Workflow(s => s.CheckUnitTrackingChangeAsync(item.Id, Pcs, TrackingMode.Serial, Ct));
        Assert.False(check.CanChange);
        Assert.Contains(check.Blockers, b => b.Contains("條碼"));

        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow(async s => await s.UpdateAsync(
            item.Id, await VersionAsync(item.Id), DraftOf(item, tracking: TrackingMode.Serial), [], ItemManager, Ct)));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(TrackingMode.None, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).TrackingMode);
    }

    // 項目 17
    [Fact]
    public async Task 核准後改分類_被擋()
    {
        var item = await ApprovedItemAsync();
        var otherCategory = await CreateCategoryAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow(async s => await s.UpdateAsync(
            item.Id, await VersionAsync(item.Id), DraftOf(item, category: otherCategory), [], ItemManager, Ct)));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(item.Category, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).CategoryId);
    }

    // 項目 18
    [Fact]
    public async Task 作業員改已核准物料的單位_權限不足()
    {
        var item = await ApprovedItemAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => Workflow(async s => await s.UpdateAsync(
            item.Id, await VersionAsync(item.Id), DraftOf(item, unit: Kg), [], Operator, Ct)));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(Pcs, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId);
    }

    // ---- 併發（項目 19）----

    // 19-a：別的交易已對此物料寫入異動（外鍵取得物料列的 KEY SHARE）但尚未提交 → 修改單位的 FOR UPDATE 必須等待，
    // 提交後才判斷，因此看得到那筆異動而被擋；不會出現「判斷沒有異動、卻在提交前冒出異動」。
    // 這裡用「預留」代表別的交易寫入的異動（其外鍵與單據明細一樣指向 items.id）。
    [Fact]
    public async Task 別的交易已寫入但未提交的異動_修改單位會等待_提交後看到異動而被擋()
    {
        var item = await ApprovedItemAsync();
        var (warehouse, _) = await WarehouseAsync();
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO stock_reservations (id, item_id, warehouse_id, quantity, remaining_quantity, reference_type, status, created_by_id, created_by_name, created_at)
            VALUES (gen_random_uuid(), @item, @warehouse, 1, 1, 'Manual', 'Open', gen_random_uuid(), '併發測試', now())
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("item", item.Id);
            insert.Parameters.AddWithValue("warehouse", warehouse);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var version = await VersionAsync(item.Id);

        var update = Task.Run(() => Workflow(s => s.UpdateAsync(item.Id, version, DraftOf(item, unit: Kg), [], ItemManager, Ct)), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.False(update.IsCompleted, "修改單位應等待尚未提交的異動交易（物料列鎖）");

        await tx.CommitAsync(Ct);

        await Assert.ThrowsAsync<DomainRuleException>(() => update.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(Pcs, (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId);
    }

    // 19-b：交易 A 在交易內鎖住物料列（等同修改單位判斷中）→ 交易 B 新增引用此物料的單據明細必須等待，A 結束後才完成。
    // 這裡用手動交易模擬 A 的 SELECT … FOR UPDATE（與服務使用的語法相同）。
    [Fact]
    public async Task 修改單位鎖住物料列期間_新增單據明細會等待_鎖釋放後才寫入()
    {
        var (item, warehouse) = await StockedItemAsync(5m);
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT 1 FROM items WHERE id = @id FOR UPDATE", connection, tx))
        {
            lockCommand.Parameters.AddWithValue("id", item.Id);
            await lockCommand.ExecuteScalarAsync(Ct);
        }

        var addLine = Task.Run(() => Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item.Code, 1), Operator, Ct)));
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.False(addLine.IsCompleted, "新增引用此物料的明細應等待物料列鎖");
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.False(await db.Set<StockDocumentLine>().AnyAsync(l => l.DocumentId == doc, Ct));
        }

        await tx.CommitAsync(Ct);

        await addLine.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await check.Set<StockDocumentLine>().CountAsync(l => l.DocumentId == doc, Ct));
    }

    // 19-c：真實服務同時執行（5 輪）。順序不固定，但結果必須一致：
    // A 被擋（DomainRuleException）→ 單位沒變且 B 的明細存在；A 成功 → 單位已變。兩者都不可死結或出現其他例外。
    // 排隊的因果關係由 19-a、19-b 確定性驗證；這裡是整合性的煙霧測試。
    [Fact]
    public async Task 同時修改單位與新增單據明細_結果一致且不死結()
    {
        for (var round = 0; round < 5; round++)
        {
            var item = await ApprovedItemAsync();
            var (warehouse, _) = await WarehouseAsync();
            var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
            var version = await VersionAsync(item.Id);
            using var gate = new SemaphoreSlim(0);

            var a = Task.Run(async () =>
            {
                await gate.WaitAsync(Ct);
                try
                {
                    await Workflow(s => s.UpdateAsync(item.Id, version, DraftOf(item, unit: Kg), [], ItemManager, Ct));
                    return true;
                }
                catch (DomainRuleException)
                {
                    return false;
                }
            }, Ct);
            var b = Task.Run(async () =>
            {
                await gate.WaitAsync(Ct);
                await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item.Code, 1), Operator, Ct));
            }, Ct);
            gate.Release(2);

            var updated = await a.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await b.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            await using var db = MigratedDatabaseFixture.CreateContext();
            var unit = (await db.Items.SingleAsync(i => i.Id == item.Id, Ct)).BaseUnitId;
            Assert.Equal(updated ? Kg : Pcs, unit);
            Assert.Equal(1, await db.Set<StockDocumentLine>().CountAsync(l => l.DocumentId == doc, Ct));
        }
    }

    // ---- 審查修正（M8-05 審查 🔴-1、🟠-2、🟠-3、🟠-4、🟡-5） ----

    // 🔴-1：退回物料已登錄的條碼，在盤點計數時不能用來新增盤盈行
    [Fact]
    public async Task 退回物料的條碼在盤點計數時被拒()
    {
        var category = await CreateCategoryAsync();
        var code = Barcode();
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "退回條碼物料 " + category),
            new ItemSubmitOptions([new ItemBarcodeDraft(code)], false, []), Operator, Ct));
        await Workflow(async s => await s.ReturnAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), "資料不全", ItemManager, Ct));
        var (warehouse, _) = await WarehouseAsync();
        var stocktake = await Stocktakes(s => s.StartAsync(warehouse, [], null, WarehouseManager, Ct));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Stocktakes(s => s.CountAsync(stocktake, new StocktakeCountInput("A-01", code, 5m), Operator, Ct)));
        Assert.Contains("尚未核准", ex.Message);
    }

    // 🟠-2：明細寫入後物料的追蹤模式被改掉（模擬「先讀後寫」的時間窗）→ 過帳時重讀設定並拒絕，不會留下沒有批號的批號物料結存
    [Fact]
    public async Task 明細寫入後物料改成批號管理_過帳被拒()
    {
        var item = await ApprovedItemAsync();
        var (warehouse, _) = await WarehouseAsync();
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item.Code, 3m), Operator, Ct));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE items SET tracking_mode = 'Lot' WHERE id = {item.Id}", Ct);
        }

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ConfirmAsync(doc));
        Assert.Contains("追蹤模式", ex.Message);
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.False(await check.StockBalances.AnyAsync(b => b.ItemId == item.Id, Ct));
    }

    // 🟠-3：別人的待核准物料不能被登錄條碼（避免占住真實商品條碼）；提出人與物料管理員可以
    [Fact]
    public async Task 別人的待核准物料不能登錄條碼_提出人與管理員可以()
    {
        var category = await CreateCategoryAsync();
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "他人條碼物料 " + category), Operator, Ct));
        var other = new Actor(Guid.NewGuid(), "作業員乙", IsItemManager: false);

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            Index(s => s.AddAsync(submitted.ItemId, ItemIndexKind.Barcode, Barcode(), other, Ct)));
        await Index(s => s.AddAsync(submitted.ItemId, ItemIndexKind.Barcode, Barcode(), Operator, Ct));
        await Index(s => s.AddAsync(submitted.ItemId, ItemIndexKind.Barcode, Barcode(), ItemManager, Ct));
    }

    // 🟠-4：待核准物料已有外箱條碼（12）時改成序號管理被擋；同一次停用該條碼就可以改
    [Fact]
    public async Task 待核准物料有外箱條碼_改序號被擋_同時停用條碼則可改()
    {
        var category = await CreateCategoryAsync();
        var name = "外箱改序號 " + category;
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name),
            new ItemSubmitOptions([new ItemBarcodeDraft(Barcode(), 12m)], false, []), Operator, Ct));
        var serialDraft = new ItemDraft(name, category, Pcs, TrackingMode.Serial, null, null, null);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Workflow(async s =>
            await s.UpdateAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), serialDraft, [], ItemUpdateOptions.None, Operator, Ct)));
        Assert.Contains("外箱條碼", ex.Message);

        Guid boxId;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            boxId = await db.ItemBarcodes.Where(b => b.ItemId == submitted.ItemId).Select(b => b.Id).SingleAsync(Ct);
        }
        await Workflow(async s => await s.UpdateAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), serialDraft, [],
            new ItemUpdateOptions([], [boxId], false, null), Operator, Ct));
        await using var check = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(TrackingMode.Serial, (await check.Items.SingleAsync(i => i.Id == submitted.ItemId, Ct)).TrackingMode);
    }

    // 🟠-4：核准時再檢查一次（例如有人直接改了資料）：序號物料帶外箱條碼不能核准
    [Fact]
    public async Task 核准時已登錄的外箱條碼不符序號管理_核准被擋()
    {
        var category = await CreateCategoryAsync();
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "核准檢查條碼 " + category),
            new ItemSubmitOptions([new ItemBarcodeDraft(Barcode(), 12m)], false, []), Operator, Ct));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE items SET tracking_mode = 'Serial' WHERE id = {submitted.ItemId}", Ct);
        }

        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow(async s =>
            await s.ApproveAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), [], null, ItemManager, Ct)));
    }

    // 🟡-5：兩張表單以相反順序填同樣兩個條碼並同時送出：不可死結（PostgresException），一張成功、另一張是條碼衝突
    [Fact]
    public async Task 相反順序的相同條碼同時提出_不死結()
    {
        for (var round = 0; round < 5; round++)
        {
            var category = await CreateCategoryAsync();
            var (x, y) = (Barcode(), Barcode());
            using var gate = new SemaphoreSlim(0);
            async Task<Exception?> Submit(string name, string first, string second)
            {
                await gate.WaitAsync(Ct);
                try
                {
                    await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name),
                        new ItemSubmitOptions([new ItemBarcodeDraft(first), new ItemBarcodeDraft(second)], false, []), ItemManager, Ct));
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }
            var a = Task.Run(() => Submit("順序甲 " + round, x, y), Ct);
            var b = Task.Run(() => Submit("順序乙 " + round, y, x), Ct);
            gate.Release(2);
            var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Single(results, r => r is null);
            Assert.IsType<ConflictException>(Assert.Single(results, r => r is not null), exactMatch: false);
        }
    }

    // ---- 輔助 ----

    private static async Task<T> Stocktakes<T>(Func<IStocktakeService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StocktakeService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static string Barcode() => "47" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);

    private static ItemDraft Draft(Guid categoryId, string name) =>
        new(name, categoryId, Pcs, TrackingMode.None, null, null, null);

    private static ItemDraft DraftOf(Approved item, Guid? unit = null, TrackingMode tracking = TrackingMode.None, Guid? category = null) =>
        new(item.Name, category ?? item.Category, unit ?? Pcs, tracking, null, null, null);

    private static async Task<Approved> ApprovedItemAsync()
    {
        var category = await CreateCategoryAsync();
        var name = "表單條碼物料 " + category;
        var submitted = await Workflow(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name), ItemManager, Ct));
        var code = await Workflow(async s => await s.ApproveAsync(submitted.ItemId, await VersionAsync(submitted.ItemId), [], null, ItemManager, Ct));
        return new Approved(submitted.ItemId, code, category, name);
    }

    /// <summary>已核准的物料，並在新建的倉庫（儲位 A-01）過帳進倉 <paramref name="quantity"/>，使物料有異動紀錄。</summary>
    private static async Task<(Approved Item, Guid Warehouse)> StockedItemAsync(decimal quantity)
    {
        var item = await ApprovedItemAsync();
        var (warehouse, _) = await WarehouseAsync();
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", item.Code, quantity), Operator, Ct));
        await ConfirmAsync(doc);
        return (item, warehouse);
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync()
    {
        var code = "F" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "表單條碼測試倉"), WarehouseManager, Ct);
        await service.CreateLocationAsync(id, new LocationInput("A-01", EmptyLocation), WarehouseManager, Ct);
        return (id, code);
    }

    private static async Task ConfirmAsync(Guid doc) =>
        await Docs(async s => await s.ConfirmAsync(doc, await DocVersionAsync(doc), "key-" + Guid.NewGuid(), Operator, Ct));

    private static Task UpdateUnitAsync(Approved item, Guid unit, ItemUpdateOptions options) =>
        Workflow(async s => await s.UpdateAsync(item.Id, await VersionAsync(item.Id), DraftOf(item, unit: unit), [], options, ItemManager, Ct));

    /// <summary>開啟系統設定執行 <paramref name="body"/>，結束（含失敗）一律還原為關閉。</summary>
    private static async Task WithSettingEnabledAsync(Func<Task> body)
    {
        await SetUnitChangeSettingAsync(true);
        try
        {
            await body();
        }
        finally
        {
            await SetUnitChangeSettingAsync(false);
        }
    }

    private static async Task SetUnitChangeSettingAsync(bool allow)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var settings = new SettingsService(db, TimeProvider.System);
        var current = await settings.GetAsync(Ct);
        await settings.UpdateAsync(current, current with { AllowItemUnitTrackingChangeWithActivity = allow }, ItemManager, Ct);
    }

    /// <summary>每個測試用自己的分類與前綴（F001、F002…），料號從 000001 起算。</summary>
    private static async Task<Guid> CreateCategoryAsync()
    {
        var code = $"F{Interlocked.Increment(ref s_prefixCounter):D3}";
        var category = new ItemCategory(Guid.NewGuid(), null, code, "表單條碼測試分類", code);
        await using var db = MigratedDatabaseFixture.CreateContext();
        db.ItemCategories.Add(category);
        await db.SaveChangesAsync(Ct);
        return category.Id;
    }

    private static async Task<string> PrefixOfAsync(Guid categoryId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return (await db.ItemCategories.SingleAsync(c => c.Id == categoryId, Ct)).ItemCodePrefix!;
    }

    private static async Task<long> CounterAsync(string prefix)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.ItemCodeCounters.Where(c => c.Prefix == prefix).Select(c => c.LastNumber).SingleAsync(Ct);
    }

    private static async Task<uint> VersionAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
    }

    private static async Task<uint> DocVersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<ScanResolution> ResolveAsync(string raw)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await new ScanResolver(db, new WarehouseService(db, TimeProvider.System)).ResolveAsync(raw, null, Ct);
    }

    private static async Task<T> Workflow<T>(Func<IItemWorkflowService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    private static async Task Workflow(Func<IItemWorkflowService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    private static async Task<T> Index<T>(Func<IItemIndexService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemIndexService(db, TimeProvider.System));
    }

    private static async Task Index(Func<IItemIndexService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemIndexService(db, TimeProvider.System));
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

    private static async Task<T> Reservations<T>(Func<IStockReservationService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }

    private static async Task Reservations(Func<IStockReservationService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }
}
