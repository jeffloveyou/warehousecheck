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

/// <summary>M2-01／M2-02 專屬別名與共用關鍵字（T-M2-01-03～T-M2-02-05），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemIndexTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly Actor OperatorA = new(Guid.NewGuid(), "作業員甲", IsItemManager: false);
    private static readonly Actor OperatorB = new(Guid.NewGuid(), "作業員乙", IsItemManager: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M2-01-03（ADR-0003 驗證：專屬別名併發新增只有一筆成功）
    [Fact]
    public async Task 二十個物料同時新增同一個專屬別名_只有一筆成功_其餘告知被誰占用()
    {
        var items = new List<Guid>();
        for (var n = 0; n < 20; n++)
        {
            items.Add(await ActiveItemAsync());
        }
        var text = "維修備品 " + Tag();

        var results = await Task.WhenAll(items.Select(async id =>
        {
            try
            {
                await WithIndexAsync(s => s.AddAsync(id, ItemIndexKind.Alias, text, OperatorA, Ct));
                return (Id: id, Error: (AppException?)null);
            }
            catch (AliasTakenException ex)
            {
                return (Id: id, Error: ex);
            }
        }));

        var winner = Assert.Single(results, r => r.Error is null);
        Assert.All(results.Where(r => r.Error is not null), r => Assert.Equal(winner.Id, ((AliasTakenException)r.Error!).HolderItemId));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ItemAliases.CountAsync(a => a.Text == text, Ct));
    }

    // T-M2-01-04（docs/03 情境 D）
    [Fact]
    public async Task 別名已被其他物料占用_被拒並說明占用者_改加為共用關鍵字可以()
    {
        var holder = await ActiveItemAsync();
        var other = await ActiveItemAsync();
        var text = "J4-40B 維修備品 " + Tag();
        await WithIndexAsync(s => s.AddAsync(holder, ItemIndexKind.Alias, text, OperatorA, Ct));

        var ex = await Assert.ThrowsAsync<AliasTakenException>(() =>
            WithIndexAsync(s => s.AddAsync(other, ItemIndexKind.Alias, text.ToLowerInvariant(), OperatorA, Ct)));
        Assert.Equal("index.alias_taken", ex.ErrorCode);
        Assert.StartsWith("EL-", ex.HolderLabel);

        await WithIndexAsync(s => s.AddAsync(other, ItemIndexKind.Keyword, text, OperatorA, Ct));
    }

    // T-M2-02-01（docs/03 情境 B、C）
    [Fact]
    public async Task 共用關鍵字可掛多個物料_同物料重複或與自己的別名相同_提示重複()
    {
        var a = await ActiveItemAsync();
        var b = await ActiveItemAsync();
        var keyword = "伺服AMP " + Tag();
        var alias = "伺服專屬 " + Tag();
        await WithIndexAsync(s => s.AddAsync(a, ItemIndexKind.Keyword, keyword, OperatorA, Ct));
        await WithIndexAsync(s => s.AddAsync(b, ItemIndexKind.Keyword, keyword, OperatorB, Ct));
        await WithIndexAsync(s => s.AddAsync(a, ItemIndexKind.Alias, alias, OperatorA, Ct));

        var dup = await Assert.ThrowsAsync<ConflictException>(() =>
            WithIndexAsync(s => s.AddAsync(a, ItemIndexKind.Keyword, "  " + keyword.ToLowerInvariant(), OperatorB, Ct)));
        Assert.Equal("index.duplicate", dup.ErrorCode);
        await Assert.ThrowsAsync<ConflictException>(() =>
            WithIndexAsync(s => s.AddAsync(a, ItemIndexKind.Keyword, alias, OperatorB, Ct)));

        var rows = await WithIndexAsync(s => s.ListAsync(a, Ct));
        Assert.Equal(2, rows.Single(r => r.Kind == ItemIndexKind.Keyword).UsageCount);
        Assert.Single(rows, r => r.Kind == ItemIndexKind.Alias);
    }

    // T-M2-02-02
    [Fact]
    public async Task 同一物料同時新增同一個關鍵字_只有一筆成功()
    {
        var item = await ActiveItemAsync();
        var text = "並行關鍵字 " + Tag();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, text, OperatorA, Ct));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }));

        Assert.Single(outcomes, ok => ok);
    }

    // T-M2-02-03（docs/03 情境 G、T-SEARCH-05 的資料面）
    [Fact]
    public async Task 停用後保留紀錄_可以重新新增_停用者與時間可查()
    {
        var item = await ActiveItemAsync();
        var text = "錯誤的俗稱 " + Tag();
        var first = await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, text, OperatorA, Ct));

        await WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, first, Manager, Ct));
        await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, text, OperatorA, Ct));

        var rows = await WithIndexAsync(s => s.ListAsync(item, Ct));
        Assert.Equal(2, rows.Count);
        var old = rows.Single(r => r.Id == first);
        Assert.False(old.IsActive);
        Assert.Equal(Manager.Name, old.DeactivatedByName);
        Assert.NotNull(old.DeactivatedAt);
        Assert.True(rows.First().IsActive); // 有效的排前面
    }

    // T-M2-02-04（T-SEC-02）
    [Fact]
    public async Task 作業員不能停用別人建立的索引_建立者與管理員可以()
    {
        var item = await ActiveItemAsync();
        var mine = await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, "甲的關鍵字 " + Tag(), OperatorA, Ct));
        var another = await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, "甲的另一個 " + Tag(), OperatorA, Ct));

        var ex = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, mine, OperatorB, Ct)));
        Assert.Equal("index.permission_denied", ex.ErrorCode);
        await WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, mine, OperatorA, Ct));
        await WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, another, Manager, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            WithIndexAsync(s => s.DeactivateAsync(Guid.NewGuid(), ItemIndexKind.Keyword, another, Manager, Ct)));
    }

    // T-M2-02-05（docs/03 情境 H 的一部分：索引只能加在已核准物料）
    [Fact]
    public async Task 待核准物料不能加索引_料號格式的文字被拒()
    {
        var pending = await WithWorkflowAsync(s => s.SubmitAsync(
            Guid.NewGuid(), new ItemDraft("待核准 " + Tag(), El, Pcs, TrackingMode.None, null, null, null), OperatorA, Ct));
        var active = await ActiveItemAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            WithIndexAsync(s => s.AddAsync(pending.ItemId, ItemIndexKind.Keyword, "任何字", OperatorA, Ct)));
        Assert.Equal("index.item_not_active", ex.ErrorCode);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithIndexAsync(s => s.AddAsync(active, ItemIndexKind.Alias, "el-000999", OperatorA, Ct)));
    }

    // T-M2-01-05
    [Theory]
    [InlineData("DELETE FROM item_keywords WHERE id = @id", "不可刪除")]
    [InlineData("UPDATE item_keywords SET text = '竄改' WHERE id = @id", "只能停用")]
    [InlineData("UPDATE item_keywords SET is_active = true, deactivated_at = NULL, deactivated_by_id = NULL, deactivated_by_name = NULL WHERE id = @id", "只能停用")]
    [InlineData("UPDATE item_keywords SET deactivated_by_name = '別人' WHERE id = @id", "停用紀錄")]
    [InlineData("TRUNCATE item_keywords", "整批清除")]
    public async Task 資料庫禁止刪除_改寫或重新啟用索引(string sql, string message)
    {
        var item = await ActiveItemAsync();
        var id = await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Keyword, "保護 " + Tag(), OperatorA, Ct));
        await WithIndexAsync(s => s.DeactivateAsync(item, ItemIndexKind.Keyword, id, Manager, Ct));

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@id", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("id", id);
        }
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        Assert.Contains(message, ex.MessageText);
    }

    // T-M2-02-07（審查 R3、S7）
    [Fact]
    public async Task 每個物料有效別名上限20筆_零寬空白不能做出外觀相同的別名()
    {
        var item = await ActiveItemAsync();
        var tag = Tag();
        for (var n = 0; n < ItemIndexService.MaxActiveAliases; n++)
        {
            await WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Alias, $"上限 {tag} {n}", OperatorA, Ct));
        }
        var limit = await Assert.ThrowsAsync<ConflictException>(() =>
            WithIndexAsync(s => s.AddAsync(item, ItemIndexKind.Alias, $"上限 {tag} 超過", OperatorA, Ct)));
        Assert.Equal("index.limit_exceeded", limit.ErrorCode);

        var other = await ActiveItemAsync();
        await Assert.ThrowsAsync<AliasTakenException>(() =>
            WithIndexAsync(s => s.AddAsync(other, ItemIndexKind.Alias, $"上限 {tag}​ 0", OperatorA, Ct)));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithIndexAsync(s => s.AddAsync(other, ItemIndexKind.Keyword, "​​", OperatorA, Ct)));
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>建立一筆已核准（有效）的物料。</summary>
    private static async Task<Guid> ActiveItemAsync()
    {
        var item = await WithWorkflowAsync(s => s.SubmitAsync(
            Guid.NewGuid(), new ItemDraft("索引測試 " + Tag(), El, Pcs, TrackingMode.None, null, null, null), Manager, Ct));
        await using var db = MigratedDatabaseFixture.CreateContext();
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await WithWorkflowAsync(s => s.ApproveAsync(item.ItemId, version, [], null, Manager, Ct));
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

    private static async Task<T> WithWorkflowAsync<T>(Func<IItemWorkflowService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemWorkflowService(db, TimeProvider.System));
    }
}
