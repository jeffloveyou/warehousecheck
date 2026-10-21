using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.Search;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Search;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M2-03 搜尋排序、命中原因、多詞、跳脫、上限（T-M2-03-01～08），真實 PostgreSQL。
/// 每個測試在文字裡放一個隨機標記（tag），只會找到自己建立的物料。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemSearchTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M2-03-01（docs/03 §5 排序）
    [Fact]
    public async Task 五層排序_料號_別名_型號品名_關鍵字_部分符合()
    {
        var tag = Tag();
        var word = $"SRV{tag}";
        var byAlias = await ActiveItemAsync($"別名物料 {tag}");
        await AddIndexAsync(byAlias, ItemIndexKind.Alias, word);
        var byName = await ActiveItemAsync(word);
        var byPartNo = await ActiveItemAsync($"型號物料 {tag}", partNo: $"srv-{tag}");
        var byKeyword = await ActiveItemAsync($"關鍵字物料 {tag}");
        await AddIndexAsync(byKeyword, ItemIndexKind.Keyword, word);
        var partial = await ActiveItemAsync($"大型 {word}改良版");

        var result = await SearchAsync(word.ToLowerInvariant());

        Assert.Equal(5, result.TotalCount);
        Assert.Equal([2, 3, 3, 4, 5], result.Items.Select(h => h.Tier));
        Assert.Equal(byAlias, result.Items[0].Id);
        Assert.Equal((HitReason.Alias, word), (result.Items[0].Reason, result.Items[0].HitText));
        Assert.Equivalent(new[] { byName, byPartNo }, result.Items.Skip(1).Take(2).Select(h => h.Id));
        Assert.Contains(result.Items, h => h.Id == byPartNo && h.Reason == HitReason.PartNo && h.HitText == $"srv-{tag}");
        Assert.Equal((byKeyword, HitReason.Keyword), (result.Items[3].Id, result.Items[3].Reason));
        Assert.Equal((partial, HitReason.Partial), (result.Items[4].Id, result.Items[4].Reason));
        Assert.Equal("品名", result.Items[4].HitText);

        // 第 1 層：用料號查
        var code = (await SearchAsync(word)).Items[0].ItemCode!;
        var byCode = await SearchAsync(code.ToLowerInvariant());
        Assert.Equal((1, HitReason.ItemCode, byAlias), (byCode.Items[0].Tier, byCode.Items[0].Reason, byCode.Items[0].Id));
    }

    // T-M2-03-02（多詞：每個詞都要符合，可分散在不同欄位）
    [Fact]
    public async Task 多詞查詢_每個詞都要出現在某個欄位或索引()
    {
        var tag = Tag();
        var both = await ActiveItemAsync($"伺服 {tag}", brand: "三菱");
        await AddIndexAsync(both, ItemIndexKind.Keyword, $"驅動器{tag}");
        await ActiveItemAsync($"伺服 {tag} 只有一個詞");

        var result = await SearchAsync($"{tag}  驅動器{tag} 三菱");

        var hit = Assert.Single(result.Items);
        Assert.Equal(both, hit.Id);
        Assert.Equal(HitReason.Partial, hit.Reason);
        Assert.Contains("品名", hit.HitText);
        Assert.Contains("品牌", hit.HitText);
        Assert.Contains($"關鍵字：驅動器{tag}", hit.HitText);
    }

    // T-M2-03-03（LIKE 萬用字元跳脫）
    [Fact]
    public async Task 百分比與底線是一般字元_不是萬用字元()
    {
        var tag = Tag();
        var percent = await ActiveItemAsync($"100%純銅 {tag}");
        await ActiveItemAsync($"100A純銅 {tag}");
        var underscore = await ActiveItemAsync($"A_B 端子 {tag}");
        await ActiveItemAsync($"AXB 端子 {tag}");

        Assert.Equal([percent], (await SearchAsync($"100% {tag}")).Items.Select(h => h.Id));
        Assert.Equal([underscore], (await SearchAsync($"A_B {tag}")).Items.Select(h => h.Id));
    }

    // T-M2-03-04（情境 E：型號不同寫法）
    [Fact]
    public async Task 型號寫法不同_去符號後相同就命中()
    {
        var tag = Tag();
        var item = await ActiveItemAsync($"伺服放大器 {tag}", partNo: $"MR-J4-40B-{tag}");

        var result = await SearchAsync($"mr j4 40b {tag}");

        Assert.Equal(item, Assert.Single(result.Items).Id);
        Assert.Equal(HitReason.PartNo, result.Items[0].Reason);
    }

    // T-M2-03-05（T-SEARCH-05、情境 G）
    [Fact]
    public async Task 停用的索引不再命中_停用的物料預設不出現_勾選後出現()
    {
        var tag = Tag();
        var item = await ActiveItemAsync($"停用測試 {tag}");
        var keyword = await AddIndexAsync(item, ItemIndexKind.Keyword, $"舊稱{tag}");
        Assert.Single((await SearchAsync($"舊稱{tag}")).Items);

        await WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, keyword, Manager, Ct));
        Assert.Empty((await SearchAsync($"舊稱{tag}")).Items);

        await WithWorkflowAsync(async s => await s.DeactivateAsync(item, await VersionAsync(item), null, Manager, Ct));
        Assert.Empty((await SearchAsync($"停用測試 {tag}")).Items);
        var withInactive = await WithSearchAsync(s => s.SearchAsync(new ItemSearchRequest($"停用測試 {tag}", IncludeInactive: true), Ct));
        Assert.Equal(ItemStatus.Inactive, Assert.Single(withInactive.Items).Status);
    }

    // T-M2-03-06（docs/03 §8、§9 上限）
    [Fact]
    public async Task 空字串不查詢_每頁上限50_超長字串截斷()
    {
        Assert.Equal(0, (await SearchAsync("   ")).TotalCount);
        var tag = Tag();
        for (var n = 0; n < 3; n++)
        {
            await ActiveItemAsync($"分頁 {tag} {n}");
        }

        var huge = await WithSearchAsync(s => s.SearchAsync(new ItemSearchRequest(tag, PageSize: 1000), Ct));
        var second = await WithSearchAsync(s => s.SearchAsync(new ItemSearchRequest(tag, Page: 2, PageSize: 2), Ct));
        var longText = await SearchAsync(tag + new string(' ', 10) + new string('x', 500));

        Assert.Equal(ItemSearch.MaxPageSize, huge.PageSize);
        Assert.Equal(3, second.TotalCount);
        Assert.Single(second.Items);
        Assert.Equal(0, longText.TotalCount); // 截到 100 字後仍包含一個不存在的長詞
    }

    // T-M2-03-07（ADR-0013 決策 8）
    [Fact]
    public async Task 新物料品名等於既有物料的專屬別名_列為重複候選()
    {
        var tag = Tag();
        var holder = await ActiveItemAsync($"原名 {tag}");
        await AddIndexAsync(holder, ItemIndexKind.Alias, $"現場俗稱 {tag}");

        var candidates = await WithWorkflowAsync(s => s.FindDuplicateCandidatesAsync($"現場俗稱 {tag}", null, null, Ct));

        var candidate = Assert.Single(candidates);
        Assert.Equal((holder, DuplicateMatchReason.SameAlias), (candidate.ItemId, candidate.Reason));
    }

    // T-M2-03-09（審查 R1、R2）
    [Fact]
    public async Task 全形規格與品牌可用半形查到_單一英文字母不做部分比對()
    {
        var tag = Tag();
        var item = await ActiveItemAsync($"電源供應器 {tag}", brand: "ＳＭＣ", spec: "輸出 ＤＣ２４Ｖ ５Ａ");

        var bySpec = await SearchAsync($"24v {tag}");
        var byBrand = await SearchAsync($"smc {tag}");
        var single = await SearchAsync($"V {tag}");

        Assert.Equal(item, Assert.Single(bySpec.Items).Id);
        Assert.Contains("規格", bySpec.Items[0].HitText);
        Assert.Contains("品牌", Assert.Single(byBrand.Items).HitText);
        // 「V」被略過，只用 tag 比對：仍找得到，但「V」本身不會讓所有含 V 的物料都出現
        Assert.Equal(item, Assert.Single(single.Items).Id);
        Assert.Equal(0, (await SearchAsync("V")).TotalCount);
    }

    private static string Tag() => "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static Task<PagedResult<ItemSearchHit>> SearchAsync(string text) =>
        WithSearchAsync(s => s.SearchAsync(new ItemSearchRequest(text), Ct));

    private static async Task<Guid> ActiveItemAsync(string name, string? partNo = null, string? brand = null, string? spec = null)
    {
        var item = await WithWorkflowAsync(s => s.SubmitAsync(
            Guid.NewGuid(), new ItemDraft(name, El, Pcs, TrackingMode.None, brand, partNo, spec), Manager, Ct));
        // 測試資料可能彼此相似（同 tag），核准時直接確認所有候選
        var candidates = await WithWorkflowAsync(s => s.FindDuplicateCandidatesAsync(name, partNo, item.ItemId, Ct));
        await WithWorkflowAsync(async s => await s.ApproveAsync(
            item.ItemId, await VersionAsync(item.ItemId), candidates.Select(c => c.ItemId).ToList(), null, Manager, Ct));
        return item.ItemId;
    }

    private static Task<Guid> AddIndexAsync(Guid itemId, ItemIndexKind kind, string text) =>
        WithIndexAsync(s => s.AddAsync(itemId, kind, text, Manager, Ct));

    private static async Task<uint> VersionAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
    }

    private static async Task<T> WithSearchAsync<T>(Func<IItemSearchService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemSearchService(db));
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

    private static async Task<T> WithWorkflowAsync<T>(Func<IItemWorkflowService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    private static async Task WithWorkflowAsync(Func<IItemWorkflowService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemWorkflowService(db, TimeProvider.System));
    }
}
