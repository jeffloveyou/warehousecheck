using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>
/// M1-02 申請單號／正式料號產生、審核流程、重複候選（T-M1-02-05～16），在真實 PostgreSQL 上執行。
/// 這些測試會 commit 資料；每個測試建立自己的分類（獨立前綴），所以料號從 000001 起算、互不干擾。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemWorkflowTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員甲", IsItemManager: false);
    private static int s_prefixCounter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M1-02-05
    [Fact]
    public async Task 並行提出20筆_申請單號全部不同且格式正確()
    {
        var category = await CreateCategoryAsync();

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(n =>
            WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"並行提出 {category} {n}"), Operator, Ct))));

        Assert.Equal(20, results.Select(r => r.RequestNo).Distinct().Count());
        Assert.All(results, r => Assert.Matches("^MR-[0-9]{6,}$", r.RequestNo));
        await using var db = MigratedDatabaseFixture.CreateContext();
        var ids = results.Select(r => r.ItemId).ToList();
        Assert.Equal(20, await db.ItemReviews.CountAsync(r => ids.Contains(r.ItemId) && r.Action == ItemReviewAction.Submit, Ct));
        Assert.All(await db.Items.Where(i => ids.Contains(i.Id)).ToListAsync(Ct), i =>
        {
            Assert.Equal(ItemStatus.PendingApproval, i.Status);
            Assert.Null(i.ItemCode);
            Assert.Equal(Operator.Id, i.CreatedById);
        });
    }

    // T-M1-02-06
    [Fact]
    public async Task 同分類並行核准20筆_料號全部不同且連號()
    {
        var category = await CreateCategoryAsync();
        var prefix = await PrefixOfAsync(category);
        var items = new List<SubmitItemResult>();
        for (var n = 1; n <= 20; n++)
        {
            items.Add(await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"並行核准 {category} {n}"), Operator, Ct)));
        }

        var codes = await Task.WhenAll(items.Select(async item =>
        {
            var version = await VersionAsync(item.ItemId);
            return await WithServiceAsync(s => s.ApproveAsync(item.ItemId, version, [], null, Manager, Ct));
        }));

        var expected = Enumerable.Range(1, 20).Select(n => $"{prefix}-{n:D6}").ToList();
        Assert.Equal(expected, codes.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(20L, await CounterAsync(prefix));
    }

    // T-M1-02-07
    [Fact]
    public async Task 同一筆並行核准_只有一筆成功_其餘版本衝突_料號只消耗一個()
    {
        var category = await CreateCategoryAsync();
        var prefix = await PrefixOfAsync(category);
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"重複按核准 {category}"), Operator, Ct));
        var version = await VersionAsync(item.ItemId);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                return await WithServiceAsync(s => s.ApproveAsync(item.ItemId, version, [], null, Manager, Ct));
            }
            catch (ConcurrencyConflictException)
            {
                return null;
            }
        }));

        Assert.Single(outcomes, o => o is not null);
        Assert.Equal($"{prefix}-000001", outcomes.Single(o => o is not null));
        Assert.Equal(1L, await CounterAsync(prefix));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ItemReviews.CountAsync(r => r.ItemId == item.ItemId && r.Action == ItemReviewAction.Approve, Ct));
    }

    // T-M1-02-08
    [Fact]
    public async Task 退回_拒絕_撤回都不消耗料號_紀錄完整保留()
    {
        var category = await CreateCategoryAsync();
        var prefix = await PrefixOfAsync(category);
        var returned = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"退回 {category}"), Operator, Ct));
        var rejected = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"拒絕 {category}"), Operator, Ct));
        var withdrawn = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"撤回 {category}"), Operator, Ct));
        var approved = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"核准 {category}"), Operator, Ct));

        await WithServiceAsync(async s => await s.ReturnAsync(returned.ItemId, await VersionAsync(returned.ItemId), "請補規格", Manager, Ct));
        await WithServiceAsync(async s => await s.RejectAsync(rejected.ItemId, await VersionAsync(rejected.ItemId), "不需要建檔", null, Manager, Ct));
        await WithServiceAsync(async s => await s.WithdrawAsync(withdrawn.ItemId, await VersionAsync(withdrawn.ItemId), null, Operator, Ct));
        var code = await WithServiceAsync(async s => await s.ApproveAsync(approved.ItemId, await VersionAsync(approved.ItemId), [], null, Manager, Ct));

        Assert.Equal($"{prefix}-000001", code);
        await using var db = MigratedDatabaseFixture.CreateContext();
        var history = await db.ItemReviews.Where(r => r.ItemId == returned.ItemId).OrderBy(r => r.OccurredAt).ToListAsync(Ct);
        Assert.Equal([ItemReviewAction.Submit, ItemReviewAction.Return], history.Select(r => r.Action));
        Assert.Equal("請補規格", history[1].Comment);
        Assert.Equal(Manager.Name, history[1].ActorName);
        var rejectedItem = await db.Items.SingleAsync(i => i.Id == rejected.ItemId, Ct);
        Assert.Equal(ItemStatus.Rejected, rejectedItem.Status);
        Assert.Equal(rejected.RequestNo, rejectedItem.RequestNo); // 申請單號保留
    }

    // T-M1-02-09
    [Fact]
    public async Task 退回後修改重送_再核准_申請單號不變()
    {
        var category = await CreateCategoryAsync();
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"重送 {category}"), Operator, Ct));
        await WithServiceAsync(async s => await s.ReturnAsync(item.ItemId, await VersionAsync(item.ItemId), "品名請寫完整", Manager, Ct));

        await WithServiceAsync(async s => await s.ResubmitAsync(
            item.ItemId, await VersionAsync(item.ItemId), Draft(category, $"重送 {category} 完整品名"), Operator, Ct));
        var code = await WithServiceAsync(async s => await s.ApproveAsync(item.ItemId, await VersionAsync(item.ItemId), [], "OK", Manager, Ct));

        await using var db = MigratedDatabaseFixture.CreateContext();
        var saved = await db.Items.SingleAsync(i => i.Id == item.ItemId, Ct);
        Assert.Equal(item.RequestNo, saved.RequestNo);
        Assert.Equal(code, saved.ItemCode);
        Assert.Equal($"重送 {category} 完整品名", saved.Name);
        var approve = await db.ItemReviews.SingleAsync(r => r.ItemId == item.ItemId && r.Action == ItemReviewAction.Approve, Ct);
        Assert.Equal(code, approve.ItemCode);
    }

    // T-M1-02-10
    [Fact]
    public async Task 有未確認的重複候選_不能核准_確認後可以核准()
    {
        var category = await CreateCategoryAsync();
        var partNo = $"PN-{category}-10A";
        var existing = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"既有 {category}", partNo), Operator, Ct));
        await WithServiceAsync(async s => await s.ApproveAsync(existing.ItemId, await VersionAsync(existing.ItemId), [], null, Manager, Ct));

        // 提出時：型號寫法不同，正規化後相同 → 列為候選，但仍可提出
        var candidates = await WithServiceAsync(s => s.FindDuplicateCandidatesAsync("完全不同的品名", partNo.ToLowerInvariant().Replace('-', ' '), null, Ct));
        var candidate = Assert.Single(candidates);
        Assert.Equal(existing.ItemId, candidate.ItemId);
        Assert.Equal(DuplicateMatchReason.SamePartNo, candidate.Reason);
        var newItem = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "完全不同的品名", partNo.ToLowerInvariant()), Operator, Ct));

        // 核准時：未確認 → 擋下並回傳最新候選
        var ex = await Assert.ThrowsAsync<DuplicateCandidatesUnconfirmedException>(async () =>
            await WithServiceAsync(async s => await s.ApproveAsync(newItem.ItemId, await VersionAsync(newItem.ItemId), [], null, Manager, Ct)));
        Assert.Equal(existing.ItemId, Assert.Single(ex.Candidates).ItemId);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.Equal(ItemStatus.PendingApproval, (await db.Items.SingleAsync(i => i.Id == newItem.ItemId, Ct)).Status);
        }

        // 確認看過候選後可以核准
        var code = await WithServiceAsync(async s => await s.ApproveAsync(
            newItem.ItemId, await VersionAsync(newItem.ItemId), [existing.ItemId], "確認為不同物料", Manager, Ct));
        Assert.EndsWith("-000002", code);
    }

    // T-M1-02-11
    [Fact]
    public async Task 兩筆疑似重複並行核准且都沒確認對方_兩筆都被擋下()
    {
        var category = await CreateCategoryAsync();
        var first = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"並行重複 {category}"), Operator, Ct));
        var second = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"並行重複  {category}"), Operator, Ct)); // 多一個空白

        var results = await Task.WhenAll(new[] { first, second }.Select(async item =>
        {
            try
            {
                var version = await VersionAsync(item.ItemId);
                await WithServiceAsync(s => s.ApproveAsync(item.ItemId, version, [], null, Manager, Ct));
                return "approved";
            }
            catch (DuplicateCandidatesUnconfirmedException)
            {
                return "blocked";
            }
        }));

        Assert.Equal(["blocked", "blocked"], results);
    }

    // T-M1-02-12
    [Fact]
    public async Task 已拒絕的物料_不列為重複候選_判定重複時記錄指向()
    {
        var category = await CreateCategoryAsync();
        var original = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"原物料 {category}"), Operator, Ct));
        var dup = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"原物料 {category}"), Operator, Ct));

        await WithServiceAsync(async s => await s.RejectAsync(
            dup.ItemId, await VersionAsync(dup.ItemId), "與既有申請重複", original.ItemId, Manager, Ct));

        var candidates = await WithServiceAsync(s => s.FindDuplicateCandidatesAsync($"原物料 {category}", null, null, Ct));
        Assert.Equal(original.ItemId, Assert.Single(candidates).ItemId);
        await using var db = MigratedDatabaseFixture.CreateContext();
        var reject = await db.ItemReviews.SingleAsync(r => r.ItemId == dup.ItemId && r.Action == ItemReviewAction.Reject, Ct);
        Assert.Equal(original.ItemId, reject.DuplicateOfItemId);
    }

    // T-M1-02-13
    [Fact]
    public async Task 子分類沒有前綴時_沿用上層前綴()
    {
        var parent = await CreateCategoryAsync();
        var prefix = await PrefixOfAsync(parent);
        var child = await CreateCategoryAsync(parent);
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(child, $"子分類物料 {child}"), Operator, Ct));

        var code = await WithServiceAsync(async s => await s.ApproveAsync(item.ItemId, await VersionAsync(item.ItemId), [], null, Manager, Ct));

        Assert.Equal($"{prefix}-000001", code);
    }

    // T-M1-02-14
    [Fact]
    public async Task 權限_作業員不能核准_也不能撤回別人的物料_提出人可以撤回()
    {
        var category = await CreateCategoryAsync();
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"權限 {category}"), Operator, Ct));
        var otherOperator = Operator with { Id = Guid.NewGuid(), Name = "作業員乙" };

        await Assert.ThrowsAsync<PermissionDeniedException>(async () =>
            await WithServiceAsync(async s => await s.ApproveAsync(item.ItemId, await VersionAsync(item.ItemId), [], null, Operator, Ct)));
        await Assert.ThrowsAsync<PermissionDeniedException>(async () =>
            await WithServiceAsync(async s => await s.WithdrawAsync(item.ItemId, await VersionAsync(item.ItemId), null, otherOperator, Ct)));
        await WithServiceAsync(async s => await s.WithdrawAsync(item.ItemId, await VersionAsync(item.ItemId), null, Operator, Ct));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(ItemStatus.Rejected, (await db.Items.SingleAsync(i => i.Id == item.ItemId, Ct)).Status);
    }

    // T-M1-02-15
    [Fact]
    public async Task 用舊版本修改_版本衝突_找不到物料_停用分類不可提出()
    {
        var category = await CreateCategoryAsync();
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"版本 {category}"), Operator, Ct));
        var stale = await VersionAsync(item.ItemId);
        await WithServiceAsync(s => s.UpdateAsync(item.ItemId, stale, Draft(category, $"版本 {category} 改1"), [], Operator, Ct));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            WithServiceAsync(s => s.UpdateAsync(item.ItemId, stale, Draft(category, $"版本 {category} 改2"), [], Operator, Ct)));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            WithServiceAsync(s => s.ReturnAsync(Guid.NewGuid(), 0, "x", Manager, Ct)));

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE item_categories SET is_active = false WHERE id = {category}", Ct);
        }
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "停用分類"), Operator, Ct)));
        Assert.Contains("分類", ex.Message);
    }

    // T-M1-02-16
    [Theory]
    [InlineData("UPDATE item_reviews SET comment = '竄改' WHERE item_id = @id")]
    [InlineData("DELETE FROM item_reviews WHERE item_id = @id")]
    public async Task 審核紀錄_不可修改或刪除(string sql)
    {
        var category = await CreateCategoryAsync();
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"紀錄 {category}"), Operator, Ct));

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", item.ItemId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        Assert.Contains("審核紀錄只能新增", ex.MessageText);
    }

    // T-M1-02-18（審查 #1）
    [Fact]
    public async Task 同名候選超過顯示上限_型號相同的候選仍會擋下核准並排在最前面()
    {
        var category = await CreateCategoryAsync();
        var name = $"六角螺絲 {category}";
        var partNo = $"HX-{category}";
        var target = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name, partNo), Operator, Ct));
        var sameNames = new List<Guid>();
        for (var n = 0; n < ItemWorkflowService.MaxDuplicateCandidates + 1; n++)
        {
            sameNames.Add((await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name), Operator, Ct))).ItemId);
        }
        var samePart = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, "另一個品名 " + category, partNo), Operator, Ct));

        // 核准者只確認了 51 筆同名的，沒確認型號相同（申請單號最大）的那筆
        var ex = await Assert.ThrowsAsync<DuplicateCandidatesUnconfirmedException>(async () =>
            await WithServiceAsync(async s => await s.ApproveAsync(target.ItemId, await VersionAsync(target.ItemId), sameNames, null, Manager, Ct)));

        Assert.Equal(ItemWorkflowService.MaxDuplicateCandidates, ex.Candidates.Count);
        Assert.Equal(samePart.ItemId, ex.Candidates[0].ItemId);
        Assert.Equal(DuplicateMatchReason.SamePartNo, ex.Candidates[0].Reason);
    }

    // T-M1-02-19（審查 #2）
    [Fact]
    public async Task 有效物料改名成另一筆有效物料的品名_未確認時被擋下()
    {
        var category = await CreateCategoryAsync();
        var a = await SubmitAndApproveAsync(category, $"物料A {category}");
        var b = await SubmitAndApproveAsync(category, $"物料B {category}");
        var renamed = Draft(category, $"物料A {category}");

        await Assert.ThrowsAsync<DuplicateCandidatesUnconfirmedException>(async () =>
            await WithServiceAsync(async s => await s.UpdateAsync(b, await VersionAsync(b), renamed, [], Manager, Ct)));
        await WithServiceAsync(async s => await s.UpdateAsync(b, await VersionAsync(b), renamed, [a], Manager, Ct));

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal($"物料A {category}", (await db.Items.SingleAsync(i => i.Id == b, Ct)).Name);
    }

    // T-M1-02-20（審查 #3）
    [Fact]
    public async Task 同一個請求ID重送_只產生一筆申請_回傳相同申請單號()
    {
        var category = await CreateCategoryAsync();
        var requestId = Guid.NewGuid();
        var draft = Draft(category, $"冪等 {category}");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            WithServiceAsync(s => s.SubmitAsync(requestId, draft, Operator, Ct))));
        var again = await WithServiceAsync(s => s.SubmitAsync(requestId, draft, Operator, Ct));

        Assert.Single(results.Select(r => r.RequestNo).Append(again.RequestNo).Distinct());
        Assert.All(results, r => Assert.Equal(requestId, r.ItemId));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ItemReviews.CountAsync(r => r.ItemId == requestId, Ct));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            WithServiceAsync(s => s.SubmitAsync(requestId, draft, Operator with { Id = Guid.NewGuid() }, Ct)));
    }

    // T-M1-02-21（審查 #4～#6）
    [Fact]
    public async Task 管理員不能撤回_重複不能指向已拒絕物料_核准時分類已停用被拒()
    {
        var category = await CreateCategoryAsync();
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"審查 {category}"), Operator, Ct));
        var rejected = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, $"已拒絕 {category}"), Operator, Ct));
        await WithServiceAsync(async s => await s.RejectAsync(rejected.ItemId, await VersionAsync(rejected.ItemId), "不需要", null, Manager, Ct));

        await Assert.ThrowsAsync<PermissionDeniedException>(async () =>
            await WithServiceAsync(async s => await s.WithdrawAsync(item.ItemId, await VersionAsync(item.ItemId), null, Manager, Ct)));
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await WithServiceAsync(async s => await s.RejectAsync(item.ItemId, await VersionAsync(item.ItemId), "重複", rejected.ItemId, Manager, Ct)));

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE item_categories SET is_active = false WHERE id = {category}", Ct);
        }
        await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await WithServiceAsync(async s => await s.ApproveAsync(item.ItemId, await VersionAsync(item.ItemId), [], null, Manager, Ct)));
    }

    private static async Task<Guid> SubmitAndApproveAsync(Guid category, string name)
    {
        var item = await WithServiceAsync(s => s.SubmitAsync(Guid.NewGuid(), Draft(category, name), Operator, Ct));
        await WithServiceAsync(async s => await s.ApproveAsync(item.ItemId, await VersionAsync(item.ItemId), [], null, Manager, Ct));
        return item.ItemId;
    }

    private static ItemDraft Draft(Guid categoryId, string name, string? partNo = null) =>
        new(name, categoryId, Pcs, TrackingMode.None, null, partNo, null);

    /// <summary>每個測試用自己的 DbContext，模擬不同使用者／請求同時操作。</summary>
    private static async Task<T> WithServiceAsync<T>(Func<IItemWorkflowService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    private static async Task WithServiceAsync(Func<IItemWorkflowService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    /// <summary>建立測試專用分類：頂層用新前綴（Q001、Q002…），子分類不設前綴。</summary>
    private static async Task<Guid> CreateCategoryAsync(Guid? parentId = null)
    {
        var n = Interlocked.Increment(ref s_prefixCounter);
        var code = $"Q{n:D3}";
        var category = new ItemCategory(Guid.NewGuid(), parentId, parentId is null ? code : code + "-SUB", "測試分類",
            parentId is null ? code : null);
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

    private static async Task<uint> VersionAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
    }

    private static async Task<long> CounterAsync(string prefix)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.ItemCodeCounters.Where(c => c.Prefix == prefix).Select(c => c.LastNumber).SingleAsync(Ct);
    }
}
