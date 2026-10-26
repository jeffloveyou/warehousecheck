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

    public const int MaxActiveBarcodes = 50;

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
        var barcodes = await db.ItemBarcodes.AsNoTracking()
            .Where(b => b.ItemId == itemId)
            .OrderByDescending(b => b.IsActive).ThenByDescending(b => b.CreatedAt)
            .Take(MaxActiveBarcodes + MaxInactiveShown)
            .Select(b => new ItemIndexRow(
                b.Id, ItemIndexKind.Barcode, b.Text, b.IsActive, b.CreatedByName, b.CreatedById, b.CreatedAt,
                b.DeactivatedByName, b.DeactivatedAt, 1, b.PackQuantity, b.Symbology))
            .ToListAsync(cancellationToken);

        var all = aliases.Concat(keywords).Concat(barcodes).ToList();
        var active = all.Where(r => r.IsActive).OrderBy(r => r.Kind).ThenBy(r => r.CreatedAt);
        var inactive = all.Where(r => !r.IsActive)
            .OrderByDescending(r => r.DeactivatedAt).Take(MaxInactiveShown);
        return active.Concat(inactive).ToList();
    }

    public async Task<Guid> AddAsync(
        Guid itemId, ItemIndexKind kind, string text, Actor actor, CancellationToken cancellationToken,
        decimal packQuantity = 1m, string? symbology = null)
    {
        var now = clock.GetUtcNow();

        // 鎖住物料那一列到交易結束：同一物料的索引新增會排隊，物料也不會在檢查之後被停用；
        // 「本物料別名與關鍵字不重複」「數量上限」因此不會被同時送出的請求繞過（M2 審查 S2、S3）。
        // 不同物料搶同一個別名（或條碼）由資料庫唯一索引把關；別名與條碼互搶由識別碼鎖把關（M5-01）。
        await using var transaction = await db.Database.BeginOrJoinTransactionAsync(cancellationToken);
        var (status, createdById) = await LockItemStatusAsync(itemId, cancellationToken)
            ?? throw new NotFoundException("item.not_found", "找不到物料。");
        // 原廠條碼可在提出時就登錄（M8-05）：先占住條碼，核准前掃碼解析會拒絕（尚未核准），拒絕或撤回時釋放
        var barcodeOnPending = kind == ItemIndexKind.Barcode
            && status is nameof(ItemStatus.PendingApproval) or nameof(ItemStatus.Returned);
        if (status != nameof(ItemStatus.Active) && !barcodeOnPending)
        {
            throw new ConflictException("index.item_not_active", kind == ItemIndexKind.Barcode
                ? "只有有效、待核准或退回的物料可以登錄原廠條碼。"
                : "只有已核准且有效的物料可以新增搜尋索引。");
        }
        // 未核准的物料只有提出人或物料管理員能登錄條碼：其他人不能替別人的申請占住真實商品的條碼（審查 🟠-3）
        if (barcodeOnPending && !actor.IsItemManager && createdById != actor.Id)
        {
            throw new PermissionDeniedException("index.permission_denied", "尚未核准的物料，只有提出人或物料管理員可以登錄條碼。");
        }
        ItemIndexEntry entry = kind switch
        {
            ItemIndexKind.Alias => ItemAlias.Create(itemId, text, actor.Id, actor.Name, now),
            ItemIndexKind.Keyword => ItemKeyword.Create(itemId, text, actor.Id, actor.Name, now),
            _ => await CreateBarcodeAsync(itemId, text, packQuantity, symbology, actor, now, cancellationToken),
        };
        if (kind != ItemIndexKind.Keyword)
        {
            await AdvisoryLocks.LockIdentifierAsync(db, entry.NormalizedText, cancellationToken);
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
            if (!transaction.IsOwned)
            {
                throw new ConflictException("index.duplicate", "這個索引剛被別人新增，請重新整理後再試。");
            }
            // 其他物料搶先新增了同一個別名：重新查一次，回報被誰占用
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await EnsureNoConflictAsync(itemId, kind, entry.NormalizedText, cancellationToken);
            throw new ConflictException("index.duplicate", "這個索引剛被別人新增，請重新整理。");
        }
        await transaction.CommitAsync(cancellationToken);
        return entry.Id;
    }

    /// <summary>在目前交易內鎖住物料列，回傳狀態文字（items.status）與提出人；物料不存在時回傳 null。</summary>
    private async Task<(string Status, Guid? CreatedById)?> LockItemStatusAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        // NO KEY UPDATE：仍與同一物料的其他修改（UPDATE items、FOR UPDATE）互斥，但不擋過帳插入異動時外鍵檢查的 KEY SHARE
        // （匯入大量別名時這個鎖會持有到整批結束，M6-03b 審查 🟠-4）
        command.CommandText = "SELECT status, created_by_id FROM items WHERE id = @id FOR NO KEY UPDATE";
        command.Parameters.Add(new NpgsqlParameter("id", itemId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    /// <summary>原廠條碼的包裝數量要符合物料的追蹤模式與單位小數位數（物料列已在交易內鎖住）。</summary>
    private async Task<ItemBarcode> CreateBarcodeAsync(
        Guid itemId, string text, decimal packQuantity, string? symbology, Actor actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new
            {
                i.TrackingMode,
                DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.DecimalPlaces).First(),
            })
            .SingleAsync(cancellationToken);
        return ItemBarcode.Create(
            itemId, text, packQuantity, symbology, item.TrackingMode, item.DecimalPlaces, actor.Id, actor.Name, now);
    }

    private async Task EnsureWithinLimitAsync(Guid itemId, ItemIndexKind kind, CancellationToken cancellationToken)
    {
        var (count, max) = kind switch
        {
            ItemIndexKind.Alias => (await db.ItemAliases.CountAsync(a => a.ItemId == itemId && a.IsActive, cancellationToken), MaxActiveAliases),
            ItemIndexKind.Keyword => (await db.ItemKeywords.CountAsync(k => k.ItemId == itemId && k.IsActive, cancellationToken), MaxActiveKeywords),
            _ => (await db.ItemBarcodes.CountAsync(b => b.ItemId == itemId && b.IsActive, cancellationToken), MaxActiveBarcodes),
        };
        if (count >= max)
        {
            throw new ConflictException("index.limit_exceeded", $"每個物料最多 {max} 筆有效的{KindName(kind)}，請先停用不需要的。");
        }
    }

    private static string KindName(ItemIndexKind kind) => kind switch
    {
        ItemIndexKind.Alias => "專屬別名",
        ItemIndexKind.Keyword => "共用關鍵字",
        _ => "原廠條碼",
    };

    public async Task DeactivateAsync(
        Guid itemId, ItemIndexKind kind, Guid entryId, Actor actor, CancellationToken cancellationToken)
    {
        ItemIndexEntry? entry = kind switch
        {
            ItemIndexKind.Alias => await db.ItemAliases.SingleOrDefaultAsync(a => a.Id == entryId && a.ItemId == itemId, cancellationToken),
            ItemIndexKind.Keyword => await db.ItemKeywords.SingleOrDefaultAsync(k => k.Id == entryId && k.ItemId == itemId, cancellationToken),
            _ => await db.ItemBarcodes.SingleOrDefaultAsync(b => b.Id == entryId && b.ItemId == itemId, cancellationToken),
        };
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
    /// - 原廠條碼（M5-01）：其他物料已登錄同一條碼 → index.barcode_taken；
    ///   別名與條碼互斥（任何物料、含本物料）→ index.identifier_conflict。掃碼時才不會一個碼對到兩種意思。
    /// </summary>
    private async Task EnsureNoConflictAsync(
        Guid itemId, ItemIndexKind kind, string normalized, CancellationToken cancellationToken)
    {
        if (kind == ItemIndexKind.Barcode)
        {
            // 與任何倉庫的儲位碼相同也不行：單據面板掃到沒有前綴的儲位標籤時，會先比中條碼而變成物料（M5-03 審查 M1）
            var locationHolder = await db.Locations.AsNoTracking()
                .Where(l => l.Code == normalized)
                .Join(db.Warehouses, l => l.WarehouseId, w => w.Id, (l, w) => w.Code)
                .FirstOrDefaultAsync(cancellationToken);
            if (locationHolder is not null)
            {
                throw new ConflictException("index.identifier_conflict", $"這段文字是倉庫 {locationHolder} 的儲位碼，不能登錄為原廠條碼。");
            }
            var barcodeHolder = await db.ItemBarcodes.AsNoTracking()
                .Where(b => b.IsActive && b.NormalizedText == normalized)
                .Join(db.Items, b => b.ItemId, i => i.Id, (b, i) => new { i.Id, Label = (i.ItemCode ?? i.RequestNo) + " " + i.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (barcodeHolder is not null)
            {
                throw barcodeHolder.Id == itemId
                    ? new ConflictException("index.duplicate", "此物料已經登錄這個原廠條碼，不需要重複新增。")
                    : new ConflictException("index.barcode_taken", $"這個條碼已登錄在 {barcodeHolder.Label}，一個條碼只能對應一個物料。");
            }
            var aliasHolder = await HolderLabelAsync(db.ItemAliases, normalized, cancellationToken);
            if (aliasHolder is not null)
            {
                throw new ConflictException("index.identifier_conflict", $"這個條碼與 {aliasHolder} 的專屬別名相同，請先停用該別名。");
            }
            return;
        }
        if (kind == ItemIndexKind.Alias)
        {
            var barcodeHolder = await HolderLabelAsync(db.ItemBarcodes, normalized, cancellationToken);
            if (barcodeHolder is not null)
            {
                throw new ConflictException("index.identifier_conflict", $"這段文字已是 {barcodeHolder} 的原廠條碼，不能再當專屬別名。");
            }
        }
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

    /// <summary>某張索引表中與文字相同的有效紀錄屬於哪個物料（料號＋品名）；沒有則回傳 null。</summary>
    private Task<string?> HolderLabelAsync<T>(IQueryable<T> entries, string normalized, CancellationToken cancellationToken)
        where T : ItemIndexEntry =>
        entries.AsNoTracking()
            .Where(e => e.IsActive && e.NormalizedText == normalized)
            .Join(db.Items, e => e.ItemId, i => i.Id, (e, i) => (string?)((i.ItemCode ?? i.RequestNo) + " " + i.Name))
            .FirstOrDefaultAsync(cancellationToken);
}
