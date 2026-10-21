using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.MasterData;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>M1-03 分類／單位維護與物料查詢（T-M1-03-02～07），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class MasterDataAndQueryTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static int s_counter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M1-03-02
    [Fact]
    public async Task 分類_多層循環被拒_停用的上層不可選()
    {
        var a = await CreateCategoryAsync(null, Prefix());
        var b = await CreateCategoryAsync(a, null);
        var c = await CreateCategoryAsync(b, null);

        // 把 A 移到自己的孫分類 C 底下 → A→C→B→A 循環
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => WithMasterDataAsync(async s =>
        {
            var row = (await s.ListCategoriesAsync(Ct)).Single(r => r.Id == a);
            await s.UpdateCategoryAsync(a, row.Version, new CategoryInput(c, row.Code, row.Name, row.ItemCodePrefix, 0), Ct);
        }));
        Assert.Contains("循環", ex.Message);

        await WithMasterDataAsync(async s =>
        {
            var row = (await s.ListCategoriesAsync(Ct)).Single(r => r.Id == b);
            await s.SetCategoryActiveAsync(b, row.Version, false, Ct);
        });
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateCategoryAsync(b, null));
    }

    // T-M1-03-03
    [Fact]
    public async Task 分類_已發號的前綴不可修改_未發號的可以_子分類顯示沿用的前綴()
    {
        var prefix = Prefix();
        var issued = await CreateCategoryAsync(null, prefix);
        var child = await CreateCategoryAsync(issued, null);
        await ApproveOneItemAsync(issued);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => WithMasterDataAsync(async s =>
        {
            var row = (await s.ListCategoriesAsync(Ct)).Single(r => r.Id == issued);
            await s.UpdateCategoryAsync(issued, row.Version, new CategoryInput(null, row.Code, "改名", Prefix(), 0), Ct);
        }));
        Assert.Contains("已發過正式料號", ex.Message);

        var fresh = await CreateCategoryAsync(null, Prefix());
        var newPrefix = Prefix();
        await WithMasterDataAsync(async s =>
        {
            var row = (await s.ListCategoriesAsync(Ct)).Single(r => r.Id == fresh);
            await s.UpdateCategoryAsync(fresh, row.Version, new CategoryInput(null, row.Code, "改名", newPrefix, 0), Ct);
        });
        await WithMasterDataAsync(async s =>
        {
            var rows = await s.ListCategoriesAsync(Ct);
            Assert.Equal(newPrefix, rows.Single(r => r.Id == fresh).ItemCodePrefix);
            Assert.Equal(prefix, rows.Single(r => r.Id == child).EffectivePrefix);
        });
    }

    // T-M1-03-04
    [Fact]
    public async Task 單位_重複代碼_使用中改小數位數_舊版本修改_都被拒()
    {
        var code = $"U{Interlocked.Increment(ref s_counter):D3}";
        var id = await WithMasterDataAsync(s => s.CreateUnitAsync(new UnitInput(code, "測試單位", 0), Ct));

        var dup = await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithMasterDataAsync(s => s.CreateUnitAsync(new UnitInput(code, "重複", 0), Ct)));
        Assert.Equal("單位代碼已存在。", dup.Message);

        var stale = await WithMasterDataAsync(async s => (await s.ListUnitsAsync(Ct)).Single(u => u.Id == id).Version);
        await WithMasterDataAsync(s => s.UpdateUnitAsync(id, stale, new UnitInput(code, "改名", 1), Ct));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            WithMasterDataAsync(s => s.UpdateUnitAsync(id, stale, new UnitInput(code, "再改", 1), Ct)));

        var pcs = await WithMasterDataAsync(async s => (await s.ListUnitsAsync(Ct)).Single(u => u.Id == Pcs));
        await ApproveOneItemAsync(await CreateCategoryAsync(null, Prefix()));
        pcs = await WithMasterDataAsync(async s => (await s.ListUnitsAsync(Ct)).Single(u => u.Id == Pcs));
        Assert.True(pcs.IsInUse);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithMasterDataAsync(s => s.UpdateUnitAsync(Pcs, pcs.Version, new UnitInput("PCS", "個", 2), Ct)));
    }

    // T-M1-03-05
    [Fact]
    public async Task 查詢_關鍵字比對料號申請單號品名型號_LIKE萬用字元被跳脫()
    {
        var category = await CreateCategoryAsync(null, Prefix());
        var tag = Guid.NewGuid().ToString("N")[..8];
        var a = await SubmitAsync(category, $"100%純銅線 {tag}", $"CU-{tag}");
        var b = await SubmitAsync(category, $"100A 斷路器 {tag}", null);
        var code = await WithWorkflowAsync(async s => await s.ApproveAsync(a.ItemId, await VersionAsync(a.ItemId), [], null, Manager, Ct));

        async Task<IReadOnlyList<Guid>> Search(string keyword) =>
            (await WithQueryAsync(q => q.SearchAsync(new ItemSearchQuery(keyword, null, category), Ct))).Items.Select(i => i.Id).ToList();

        Assert.Equal([a.ItemId], await Search(code.ToLowerInvariant()));
        Assert.Equal([b.ItemId], await Search(b.RequestNo));
        Assert.Equal([a.ItemId], await Search("100%"));          // % 不是萬用字元
        Assert.Equal(2, (await Search(tag)).Count);
        Assert.Equal([a.ItemId], await Search($"cu {tag}"));     // 型號正規化比對
    }

    // T-M1-03-06
    [Fact]
    public async Task 查詢_分頁上限100筆_頁碼與筆數會被修正()
    {
        var category = await CreateCategoryAsync(null, Prefix());
        for (var n = 0; n < 3; n++)
        {
            await SubmitAsync(category, $"分頁 {category} {n}", null);
        }

        var huge = await WithQueryAsync(q => q.SearchAsync(new ItemSearchQuery(null, null, null, 0, 100_000), Ct));
        var second = await WithQueryAsync(q => q.SearchAsync(new ItemSearchQuery(null, ItemStatus.PendingApproval, category, 2, 2), Ct));

        Assert.Equal(1, huge.Page);
        Assert.Equal(Paging.MaxPageSize, huge.PageSize);
        Assert.True(huge.Items.Count <= Paging.MaxPageSize);
        Assert.Equal(3, second.TotalCount);
        Assert.Single(second.Items);
        Assert.Equal(2, second.TotalPages);
    }

    // T-M1-03-07
    [Fact]
    public async Task 明細_含版本與審核紀錄_重複指向顯示對方料號()
    {
        var category = await CreateCategoryAsync(null, Prefix());
        var original = await SubmitAsync(category, $"明細 {category}", null);
        var code = await WithWorkflowAsync(async s => await s.ApproveAsync(original.ItemId, await VersionAsync(original.ItemId), [], null, Manager, Ct));
        var dup = await SubmitAsync(category, $"明細 {category}", null);
        await WithWorkflowAsync(async s => await s.RejectAsync(dup.ItemId, await VersionAsync(dup.ItemId), "重複", original.ItemId, Manager, Ct));

        var details = await WithQueryAsync(q => q.GetAsync(dup.ItemId, Ct));

        Assert.NotNull(details);
        Assert.Equal(await VersionAsync(dup.ItemId), details.Version);
        Assert.Equal([ItemReviewAction.Submit, ItemReviewAction.Reject], details.Reviews.Select(r => r.Action));
        Assert.StartsWith(code, details.Reviews[1].DuplicateOfLabel);
        Assert.Null(await WithQueryAsync(q => q.GetAsync(Guid.NewGuid(), Ct)));
    }

    // T-M1-03-18（審查 R3、R4）
    [Fact]
    public async Task 分類_搬移後連同子樹超過8層被拒_已發過號的前綴不能給新分類()
    {
        // X 底下有 3 層（X 本身算第 1 層，子樹高度 4）
        var x = await CreateCategoryAsync(null, Prefix());
        var parent = x;
        for (var n = 0; n < 3; n++)
        {
            parent = await CreateCategoryAsync(parent, null);
        }
        // 另一條 6 層的鏈
        var deep = await CreateCategoryAsync(null, Prefix());
        for (var n = 0; n < 5; n++)
        {
            deep = await CreateCategoryAsync(deep, null);
        }

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => WithMasterDataAsync(async s =>
        {
            var row = (await s.ListCategoriesAsync(Ct)).Single(r => r.Id == x);
            await s.UpdateCategoryAsync(x, row.Version, new CategoryInput(deep, row.Code, row.Name, row.ItemCodePrefix, 0), Ct);
        }));
        Assert.Contains("含底下的子分類", ex.Message);

        var issued = Prefix();
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO item_code_counters (prefix, last_number) VALUES ({issued}, 5)", Ct);
        }
        var reuse = await Assert.ThrowsAsync<DomainRuleException>(() => CreateCategoryAsync(null, issued));
        Assert.Contains("已發過正式料號", reuse.Message);
    }

    private static string Prefix() => $"M{Interlocked.Increment(ref s_counter):D3}";

    private static async Task<Guid> CreateCategoryAsync(Guid? parentId, string? prefix)
    {
        var code = prefix ?? $"SUB-{Interlocked.Increment(ref s_counter):D3}";
        return await WithMasterDataAsync(s => s.CreateCategoryAsync(new CategoryInput(parentId, code, "測試分類 " + code, prefix, 0), Ct));
    }

    private static Task<SubmitItemResult> SubmitAsync(Guid category, string name, string? partNo) =>
        WithWorkflowAsync(s => s.SubmitAsync(Guid.NewGuid(), new ItemDraft(name, category, Pcs, TrackingMode.None, null, partNo, null), Manager, Ct));

    private static async Task ApproveOneItemAsync(Guid category)
    {
        var item = await SubmitAsync(category, $"發號 {Guid.NewGuid():N}", null);
        await WithWorkflowAsync(async s => await s.ApproveAsync(item.ItemId, await VersionAsync(item.ItemId), [], null, Manager, Ct));
    }

    private static async Task<uint> VersionAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
    }

    private static async Task<T> WithMasterDataAsync<T>(Func<IMasterDataService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new MasterDataService(db));
    }

    private static async Task WithMasterDataAsync(Func<IMasterDataService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new MasterDataService(db));
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

    private static async Task<T> WithQueryAsync<T>(Func<IItemQueryService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemQueryService(db));
    }
}
