using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Items;

/// <summary>
/// <see cref="IItemIndexService"/> 的 EF Core 實作。新增時先查詢衝突給清楚的訊息；
/// 同時新增的競態由資料庫部分唯一索引最後把關（捕捉 23505 再轉成同樣的錯誤）。
/// </summary>
public sealed class ItemIndexService(WarehouseDbContext db, TimeProvider clock) : IItemIndexService
{
    /// <summary>每個物料最多幾筆有效的別名、關鍵字（避免無限制累積，AGENTS.md 第三節）。</summary>
    public const int MaxActiveAliases = 20;

    public const int MaxActiveKeywords = 100;

    /// <summary>明細頁最多顯示幾筆已停用的歷史（較舊的請用 SQL 查，docs/04 §4.3）。</summary>
    public const int MaxInactiveShown = 50;

    public async Task<IReadOnlyList<ItemIndexRow>> ListAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            throw new NotFoundException("item.not_found", "找不到物料。");
        }

        var aliases = await db.ItemAliases.AsNoTracking()
            .Where(a => a.ItemId == itemId)
            .OrderByDescending(a => a.IsActive).ThenByDescending(a => a.CreatedAt)
            .Take(MaxActiveAliases + MaxInactiveShown)
            .Select(a => new ItemIndexRow(
                a.Id, ItemIndexKind.Alias, a.Text, a.IsActive, a.CreatedByName, a.CreatedById, a.CreatedAt,
                a.DeactivatedByName, a.DeactivatedAt, 1))
            .ToListAsync(cancellationToken);
        // 使用數：同一個正規化關鍵字目前掛在幾個「有效物料」上（畫面顯示「此關鍵字已被 N 個料號使用」）
        var keywords = await db.ItemKeywords.AsNoTracking()
            .Where(k => k.ItemId == itemId)
            .OrderByDescending(k => k.IsActive).ThenByDescending(k => k.CreatedAt)
            .Take(MaxActiveKeywords + MaxInactiveShown)
            .Select(k => new ItemIndexRow(
                k.Id, ItemIndexKind.Keyword, k.Text, k.IsActive, k.CreatedByName, k.CreatedById, k.CreatedAt,
                k.DeactivatedByName, k.DeactivatedAt,
                db.ItemKeywords.Count(o => o.IsActive && o.NormalizedText == k.NormalizedText
                    && db.Items.Any(i => i.Id == o.ItemId && i.Status == ItemStatus.Active))))
            .ToListAsync(cancellationToken);

        var active = aliases.Concat(keywords).Where(r => r.IsActive).OrderBy(r => r.Kind).ThenBy(r => r.CreatedAt);
        var inactive = aliases.Concat(keywords).Where(r => !r.IsActive)
            .OrderByDescending(r => r.DeactivatedAt).Take(MaxInactiveShown);
        return active.Concat(inactive).ToList();
    }

    public async Task<Guid> AddAsync(
        Guid itemId, ItemIndexKind kind, string text, Actor actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        ItemIndexEntry entry = kind == ItemIndexKind.Alias
            ? ItemAlias.Create(itemId, text, actor.Id, actor.Name, now)
            : ItemKeyword.Create(itemId, text, actor.Id, actor.Name, now);

        // 鎖住物料那一列到交易結束：同一物料的索引新增會排隊，物料也不會在檢查之後被停用；
        // 「本物料別名與關鍵字不重複」「數量上限」因此不會被同時送出的請求繞過（M2 審查 S2、S3）。
        // 不同物料搶同一個別名則由資料庫唯一索引把關。
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var status = await LockItemStatusAsync(itemId, cancellationToken)
            ?? throw new NotFoundException("item.not_found", "找不到物料。");
        if (status != nameof(ItemStatus.Active))
        {
            throw new ConflictException("index.item_not_active", "只有已核准且有效的物料可以新增搜尋索引。");
        }
        await EnsureNoConflictAsync(itemId, kind, entry.NormalizedText, cancellationToken);
        await EnsureWithinLimitAsync(itemId, kind, cancellationToken);

        db.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // 其他物料搶先新增了同一個別名：重新查一次，回報被誰占用
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await EnsureNoConflictAsync(itemId, kind, entry.NormalizedText, cancellationToken);
            throw new ConflictException("index.duplicate", "這個索引剛被別人新增，請重新整理。");
        }
        await transaction.CommitAsync(cancellationToken);
        return entry.Id;
    }

    /// <summary>在目前交易內鎖住物料列並回傳狀態文字（items.status）；物料不存在時回傳 null。</summary>
    private async Task<string?> LockItemStatusAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT status FROM items WHERE id = @id FOR UPDATE";
        command.Parameters.Add(new NpgsqlParameter("id", itemId));
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task EnsureWithinLimitAsync(Guid itemId, ItemIndexKind kind, CancellationToken cancellationToken)
    {
        var (count, max) = kind == ItemIndexKind.Alias
            ? (await db.ItemAliases.CountAsync(a => a.ItemId == itemId && a.IsActive, cancellationToken), MaxActiveAliases)
            : (await db.ItemKeywords.CountAsync(k => k.ItemId == itemId && k.IsActive, cancellationToken), MaxActiveKeywords);
        if (count >= max)
        {
            throw new ConflictException("index.limit_exceeded", $"每個物料最多 {max} 筆有效的{(kind == ItemIndexKind.Alias ? "專屬別名" : "共用關鍵字")}，請先停用不需要的。");
        }
    }

    public async Task DeactivateAsync(
        Guid itemId, ItemIndexKind kind, Guid entryId, Actor actor, CancellationToken cancellationToken)
    {
        ItemIndexEntry? entry = kind == ItemIndexKind.Alias
            ? await db.ItemAliases.SingleOrDefaultAsync(a => a.Id == entryId && a.ItemId == itemId, cancellationToken)
            : await db.ItemKeywords.SingleOrDefaultAsync(k => k.Id == entryId && k.ItemId == itemId, cancellationToken);
        if (entry is null)
        {
            throw new NotFoundException("index.not_found", "找不到這筆搜尋索引。");
        }
        if (!actor.IsItemManager && entry.CreatedById != actor.Id)
        {
            throw new PermissionDeniedException("index.permission_denied", "只有建立者或物料管理員可以停用這筆索引。");
        }

        entry.Deactivate(actor.Id, actor.Name, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("index.concurrency_conflict", "這筆索引已被他人修改，請重新整理。");
        }
    }

    /// <summary>
    /// 衝突規則（ADR-0013）：
    /// - 別名：其他物料已有相同的有效別名 → <see cref="AliasTakenException"/>。
    /// - 本物料已有相同文字的有效別名或關鍵字 → 重複（不建立第二筆，docs/03 情境 C）。
    /// - 其他物料有相同的「關鍵字」不算衝突：共用關鍵字本來就可以多個物料共用。
    /// </summary>
    private async Task EnsureNoConflictAsync(
        Guid itemId, ItemIndexKind kind, string normalized, CancellationToken cancellationToken)
    {
        if (kind == ItemIndexKind.Alias)
        {
            var holder = await db.ItemAliases.AsNoTracking()
                .Where(a => a.IsActive && a.NormalizedText == normalized && a.ItemId != itemId)
                .Join(db.Items, a => a.ItemId, i => i.Id, (a, i) => new { i.Id, Label = (i.ItemCode ?? i.RequestNo) + " " + i.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (holder is not null)
            {
                throw new AliasTakenException(holder.Id, holder.Label);
            }
        }

        var ownAlias = await db.ItemAliases.AnyAsync(
            a => a.IsActive && a.ItemId == itemId && a.NormalizedText == normalized, cancellationToken);
        var ownKeyword = await db.ItemKeywords.AnyAsync(
            k => k.IsActive && k.ItemId == itemId && k.NormalizedText == normalized, cancellationToken);
        if (ownAlias || ownKeyword)
        {
            throw new ConflictException(
                "index.duplicate", $"此物料已經有這個{(ownAlias ? "專屬別名" : "共用關鍵字")}，不需要重複新增。");
        }
    }
}
