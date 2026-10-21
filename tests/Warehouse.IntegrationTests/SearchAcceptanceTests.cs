using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.Search;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Search;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M2-05 驗收情境：docs/03 §7 的 A～G 與 docs/05 T-SEARCH-01～05，使用規格裡的真實詞彙。
/// 情境 H（不能從搜尋頁建立物料）在 <c>WebAuthTests.搜尋頁顯示命中原因_*</c>。
/// 其他測試的物料名稱也可能含「伺服」，所以這裡只檢查「指定物料有沒有出現、原因對不對」，不檢查總筆數。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class SearchAcceptanceTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly Actor Operator = new(Guid.NewGuid(), "現場作業員", IsItemManager: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 情境 A、T-SEARCH-01：同一物料有多種說法，任一種都查得到
    [Fact]
    public async Task 情境A_伺服放大器_ServoAmp_伺服的驅動器_都能查到同一個料號()
    {
        var item = await ActiveItemAsync("AC 伺服驅動單元 200W", "MR-J4-20A-ACC", "三菱", "200W，單相 AC200V");
        await AddAsync(item, ItemIndexKind.Alias, "伺服放大器");
        await AddAsync(item, ItemIndexKind.Keyword, "Servo Amp");
        await AddAsync(item, ItemIndexKind.Keyword, "伺服的驅動器");

        await AssertHitAsync("伺服放大器", item, HitReason.Alias);
        await AssertHitAsync("servo amp", item, HitReason.Keyword);
        await AssertHitAsync("ＳＥＲＶＯ　ＡＭＰ", item, HitReason.Keyword); // 全形也一樣
        await AssertHitAsync("伺服的驅動器", item, HitReason.Keyword);
    }

    // 情境 B、T-SEARCH-02：共用關鍵字掛兩個料號 → 顯示兩筆及各自規格，不自動選第一筆
    [Fact]
    public async Task 情境B_伺服AMP掛在兩個料號_兩筆都列出並各自顯示規格()
    {
        var small = await ActiveItemAsync("伺服放大器 100W 驗收B", "MR-J4-10A-ACC", "三菱", "100W");
        var large = await ActiveItemAsync("伺服放大器 400W 驗收B", "MR-J4-40A-ACC", "三菱", "400W");
        await AddAsync(small, ItemIndexKind.Keyword, "伺服AMP");
        await AddAsync(large, ItemIndexKind.Keyword, "伺服AMP");

        var result = await SearchAsync("伺服amp");

        var hits = result.Items.Where(h => h.Id == small || h.Id == large).ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(HitReason.Keyword, h.Reason));
        Assert.Equal(["100W", "400W"], hits.Select(h => h.SpecificationSummary).Order());
        Assert.NotEqual(hits[0].ItemCode, hits[1].ItemCode);
    }

    // 情境 C、D（T-SEARCH-03）
    [Fact]
    public async Task 情境C_同料號重複索引被提示_情境D_別名被占用須改用共用關鍵字()
    {
        var owner = await ActiveItemAsync("伺服馬達 驗收CD", null, null, null);
        var other = await ActiveItemAsync("伺服馬達 備品 驗收CD", null, null, null);
        await AddAsync(owner, ItemIndexKind.Keyword, "主軸馬達");
        await AddAsync(owner, ItemIndexKind.Alias, "J4-40B 維修備品");

        var duplicate = await Assert.ThrowsAsync<ConflictException>(() => AddAsync(owner, ItemIndexKind.Keyword, "主軸馬達"));
        Assert.Equal("index.duplicate", duplicate.ErrorCode);
        var taken = await Assert.ThrowsAsync<AliasTakenException>(() => AddAsync(other, ItemIndexKind.Alias, "J4-40B 維修備品"));
        Assert.Equal(owner, taken.HolderItemId);
        await AddAsync(other, ItemIndexKind.Keyword, "J4-40B 維修備品");

        await AssertHitAsync("J4-40B 維修備品", owner, HitReason.Alias);
        await AssertHitAsync("J4-40B 維修備品", other, HitReason.Keyword);
    }

    // 情境 E：型號不同寫法 → 相似候選（型號），不是精確料號
    [Fact]
    public async Task 情境E_MR_J4_40B的不同寫法_以型號命中而非料號()
    {
        var item = await ActiveItemAsync("伺服放大器 400W 驗收E", "MR-J4-40B", "三菱", null);

        var hit = await AssertHitAsync("MR J4 40B", item, HitReason.PartNo);

        Assert.Equal(3, hit.Tier);
        Assert.Equal("MR-J4-40B", hit.HitText);
    }

    // 情境 F、T-SEARCH-04：搜不到 → 加到既有物料 → 下次查得到，並記錄人與時間
    [Fact]
    public async Task 情境F_搜不到維修用放大器_加為關鍵字後查得到並留紀錄()
    {
        var item = await ActiveItemAsync("伺服放大器 750W 驗收F", "MR-J4-70A-ACC", "三菱", null);
        Assert.DoesNotContain((await SearchAsync("維修用放大器")).Items, h => h.Id == item);

        await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, "維修用放大器", Operator, Ct));

        await AssertHitAsync("維修用放大器", item, HitReason.Keyword);
        var row = (await WithIndexAsync(s => s.ListAsync(item, Ct))).Single(r => r.Text == "維修用放大器");
        Assert.Equal(Operator.Name, row.CreatedByName);
    }

    // 情境 G、T-SEARCH-05：停用的錯誤索引不再命中，歷史與建立者仍可稽核
    [Fact]
    public async Task 情境G_停用錯誤索引後不再命中_歷史仍保留()
    {
        var item = await ActiveItemAsync("電磁閥 驗收G", "SY5120-ACC", "SMC", null);
        var wrong = await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, "機台維修用伺服", Operator, Ct));
        await AssertHitAsync("機台維修用伺服", item, HitReason.Keyword);

        await WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, wrong, Manager, Ct));

        Assert.DoesNotContain((await SearchAsync("機台維修用伺服")).Items, h => h.Id == item);
        var row = (await WithIndexAsync(s => s.ListAsync(item, Ct))).Single(r => r.Id == wrong);
        Assert.False(row.IsActive);
        Assert.Equal((Operator.Name, Manager.Name), (row.CreatedByName, row.DeactivatedByName));
    }

    private static async Task<ItemSearchHit> AssertHitAsync(string text, Guid itemId, HitReason reason)
    {
        var result = await SearchAsync(text);
        var hit = Assert.Single(result.Items, h => h.Id == itemId);
        Assert.Equal(reason, hit.Reason);
        return hit;
    }

    private static async Task<PagedResult<ItemSearchHit>> SearchAsync(string text)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await new ItemSearchService(db).SearchAsync(new ItemSearchRequest(text, PageSize: ItemSearch.MaxPageSize), Ct);
    }

    private static Task<Guid> AddAsync(Guid itemId, ItemIndexKind kind, string text) =>
        WithIndexAsync(s => s.AddAsync(itemId, kind, text, Manager, Ct));

    private static async Task<Guid> ActiveItemAsync(string name, string? partNo, string? brand, string? spec)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft(name, El, Pcs, TrackingMode.None, brand, partNo, spec), Operator, Ct);
        var candidates = await workflow.FindDuplicateCandidatesAsync(name, partNo, item.ItemId, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, candidates.Select(c => c.ItemId).ToList(), null, Manager, Ct);
        return item.ItemId;
    }

    private static async Task<T> WithIndexAsync<T>(Func<IItemIndexService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemIndexService(db, TimeProvider.System));
    }

    private static async Task WithIndexAsync(Func<IItemIndexService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemIndexService(db, TimeProvider.System));
    }
}
