using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Errors;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// 期初單與進出倉單共用的過帳輔助：冪等鍵檢查、批號／序號解析。
/// 都必須在呼叫端的交易內執行（批號、序號 upsert 依（物料, 號碼）排序後呼叫，見 ADR-0004 鎖順序）。
/// </summary>
internal static class DocumentSupport
{
    /// <summary>冪等鍵：去空白、長度 8～100。</summary>
    public static string NormalizeKey(string? key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        if (trimmed.Length is < 8 or > 100)
        {
            throw new DomainRuleException("缺少或不正確的請求識別碼（Idempotency-Key，8～100 字）。");
        }
        return trimmed;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// 同一鍵已處理過：內容相同回傳 true（呼叫端直接結束，不再過帳）；內容不同丟 <c>idempotency.key_reused</c>。
    /// </summary>
    public static async Task<bool> AlreadyProcessedAsync(
        WarehouseDbContext db, string key, string commandType, string requestHash, CancellationToken cancellationToken)
    {
        var existing = await db.ProcessedCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Key == key, cancellationToken);
        if (existing is null)
        {
            return false;
        }
        if (existing.CommandType != commandType || existing.RequestHash != requestHash)
        {
            throw new ConflictException("idempotency.key_reused", "這個請求識別碼已用於其他操作，請重新整理頁面後再試。");
        }
        return true;
    }

    /// <summary>批號沒有就建立；已存在且有效期限不同時拒絕（避免同一批號兩個期限）。</summary>
    public static async Task<Guid> ResolveLotAsync(
        WarehouseDbContext db, StockDocumentLine line, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = (await QueryAsync(db, """
            INSERT INTO inventory_lots (id, item_id, lot_no, expiry_date, created_at) VALUES (@id, @item, @lot, @expiry, @now)
            ON CONFLICT (item_id, lot_no) DO UPDATE SET expiry_date = COALESCE(inventory_lots.expiry_date, EXCLUDED.expiry_date)
            RETURNING id, expiry_date
            """,
            r => (Id: r.GetGuid(0), Expiry: r.IsDBNull(1) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(1))),
            cancellationToken, Param("id", Guid.NewGuid()), Param("item", line.ItemId!.Value), Param("lot", line.LotNo),
            new NpgsqlParameter("expiry", NpgsqlTypes.NpgsqlDbType.Date) { Value = line.ExpiryDate is { } e ? e : DBNull.Value },
            Param("now", now))).Single();
        if (line.ExpiryDate is { } expiry && row.Expiry != expiry)
        {
            throw new DomainRuleException($"批號 {line.LotNo} 已存在，有效期限是 {row.Expiry:yyyy-MM-dd}，與明細不同。");
        }
        return row.Id;
    }

    /// <summary>序號沒有就建立；已存在但屬於不同批號時拒絕（序號與結存的批號必須一致，M3-03 審查 #8）。</summary>
    public static async Task<Guid> ResolveSerialAsync(
        WarehouseDbContext db, StockDocumentLine line, Guid? lotId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = (await QueryAsync(db, """
            INSERT INTO item_serials (id, item_id, serial_no, lot_id, created_at) VALUES (@id, @item, @serial, @lot, @now)
            ON CONFLICT (item_id, serial_no) DO UPDATE SET serial_no = EXCLUDED.serial_no
            RETURNING id, lot_id
            """, r => (Id: r.GetGuid(0), LotId: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1)), cancellationToken,
            Param("id", Guid.NewGuid()), Param("item", line.ItemId!.Value), Param("serial", line.SerialNo),
            Uuid("lot", lotId), Param("now", now))).Single();
        if (row.LotId != lotId)
        {
            throw new DomainRuleException($"序號 {line.SerialNo} 已登記在其他批號下，請確認。");
        }
        return row.Id;
    }

    /// <summary>
    /// 批次解析批號（M4-02 審查 #5：500 行不逐行往返）。回傳「明細 id → 批號 id」。
    /// <paramref name="create"/>＝進倉：依（物料, 批號）排序一次 upsert，有效期限不同就拒絕；否則必須已存在。
    /// </summary>
    public static async Task<Dictionary<Guid, Guid>> ResolveLotsAsync(
        WarehouseDbContext db, IEnumerable<StockDocumentLine> lines, bool create, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var withLot = lines.Where(l => l.LotNo is not null).ToList();
        var keys = withLot.GroupBy(l => (Item: l.ItemId!.Value, Lot: l.LotNo!))
            .OrderBy(g => g.Key.Item).ThenBy(g => g.Key.Lot, StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return [];
        }
        Dictionary<(Guid, string), Guid> ids;
        if (create)
        {
            var expiries = keys.Select(g =>
            {
                var distinct = g.Select(l => l.ExpiryDate).Where(e => e is not null).Distinct().ToList();
                return distinct.Count > 1
                    ? throw new DomainRuleException($"批號 {g.Key.Lot} 在這張單有兩個不同的有效期限。")
                    : distinct.SingleOrDefault();
            }).ToArray();
            var rows = await QueryAsync(db, """
                INSERT INTO inventory_lots (id, item_id, lot_no, expiry_date, created_at)
                SELECT u.id, u.item_id, u.lot_no, u.expiry, @now
                FROM unnest(@ids, @items, @lots, @expiries) WITH ORDINALITY AS u(id, item_id, lot_no, expiry, ord)
                ORDER BY u.ord
                ON CONFLICT (item_id, lot_no) DO UPDATE SET expiry_date = COALESCE(inventory_lots.expiry_date, EXCLUDED.expiry_date)
                RETURNING id, item_id, lot_no, expiry_date
                """,
                r => (Id: r.GetGuid(0), Item: r.GetGuid(1), Lot: r.GetString(2), Expiry: r.IsDBNull(3) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(3))),
                cancellationToken, Param("now", now),
                Param("ids", keys.Select(_ => Guid.NewGuid()).ToArray()),
                Param("items", keys.Select(k => k.Key.Item).ToArray()),
                Param("lots", keys.Select(k => k.Key.Lot).ToArray()),
                new NpgsqlParameter("expiries", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Date) { Value = expiries });
            var byKey = rows.ToDictionary(r => (r.Item, r.Lot));
            for (var i = 0; i < keys.Count; i++)
            {
                if (expiries[i] is { } expiry && byKey[keys[i].Key].Expiry != expiry)
                {
                    throw new DomainRuleException($"批號 {keys[i].Key.Lot} 已存在，有效期限是 {byKey[keys[i].Key].Expiry:yyyy-MM-dd}，與明細不同。");
                }
            }
            ids = byKey.ToDictionary(x => x.Key, x => x.Value.Id);
        }
        else
        {
            ids = (await QueryAsync(db, """
                SELECT id, item_id, lot_no FROM inventory_lots
                WHERE (item_id, lot_no) IN (SELECT * FROM unnest(@items, @lots))
                """, r => (Id: r.GetGuid(0), Item: r.GetGuid(1), Lot: r.GetString(2)), cancellationToken,
                Param("items", keys.Select(k => k.Key.Item).ToArray()), Param("lots", keys.Select(k => k.Key.Lot).ToArray())))
                .ToDictionary(r => (r.Item, r.Lot), r => r.Id);
            var missing = withLot.OrderBy(l => l.LineNo).FirstOrDefault(l => !ids.ContainsKey((l.ItemId!.Value, l.LotNo!)));
            if (missing is not null)
            {
                throw new DomainRuleException($"找不到批號 {missing.LotNo}（第 {missing.LineNo} 行）。");
            }
        }
        return withLot.ToDictionary(l => l.Id, l => ids[(l.ItemId!.Value, l.LotNo!)]);
    }

    /// <summary>
    /// 批次解析序號。<paramref name="create"/>＝進倉：一次 upsert，已登記在其他批號就拒絕；否則必須已存在。
    /// <paramref name="lotIds"/> 是 <see cref="ResolveLotsAsync"/> 的結果（序號物料可同時有批號）。
    /// </summary>
    public static async Task<Dictionary<Guid, Guid>> ResolveSerialsAsync(
        WarehouseDbContext db, IEnumerable<StockDocumentLine> lines, IReadOnlyDictionary<Guid, Guid> lotIds, bool create,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var withSerial = lines.Where(l => l.SerialNo is not null).ToList();
        var keys = withSerial.GroupBy(l => (Item: l.ItemId!.Value, Serial: l.SerialNo!))
            .OrderBy(g => g.Key.Item).ThenBy(g => g.Key.Serial, StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return [];
        }
        Dictionary<(Guid, string), Guid> ids;
        if (create)
        {
            var lots = keys.Select(g =>
            {
                var distinct = g.Select(l => lotIds.TryGetValue(l.Id, out var lot) ? lot : (Guid?)null).Distinct().ToList();
                return distinct.Count > 1
                    ? throw new DomainRuleException($"序號 {g.Key.Serial} 在這張單對應到不同批號。")
                    : distinct[0];
            }).ToArray();
            var rows = await QueryAsync(db, """
                INSERT INTO item_serials (id, item_id, serial_no, lot_id, created_at)
                SELECT u.id, u.item_id, u.serial_no, u.lot_id, @now
                FROM unnest(@ids, @items, @serials, @lots) WITH ORDINALITY AS u(id, item_id, serial_no, lot_id, ord)
                ORDER BY u.ord
                ON CONFLICT (item_id, serial_no) DO UPDATE SET serial_no = EXCLUDED.serial_no
                RETURNING id, item_id, serial_no, lot_id
                """,
                r => (Id: r.GetGuid(0), Item: r.GetGuid(1), Serial: r.GetString(2), Lot: r.IsDBNull(3) ? (Guid?)null : r.GetGuid(3)),
                cancellationToken, Param("now", now),
                Param("ids", keys.Select(_ => Guid.NewGuid()).ToArray()),
                Param("items", keys.Select(k => k.Key.Item).ToArray()),
                Param("serials", keys.Select(k => k.Key.Serial).ToArray()),
                new NpgsqlParameter("lots", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = lots });
            var byKey = rows.ToDictionary(r => (r.Item, r.Serial));
            for (var i = 0; i < keys.Count; i++)
            {
                if (byKey[keys[i].Key].Lot != lots[i])
                {
                    throw new DomainRuleException($"序號 {keys[i].Key.Serial} 已登記在其他批號下，請確認。");
                }
            }
            ids = byKey.ToDictionary(x => x.Key, x => x.Value.Id);
        }
        else
        {
            ids = (await QueryAsync(db, """
                SELECT id, item_id, serial_no FROM item_serials
                WHERE (item_id, serial_no) IN (SELECT * FROM unnest(@items, @serials))
                """, r => (Id: r.GetGuid(0), Item: r.GetGuid(1), Serial: r.GetString(2)), cancellationToken,
                Param("items", keys.Select(k => k.Key.Item).ToArray()), Param("serials", keys.Select(k => k.Key.Serial).ToArray())))
                .ToDictionary(r => (r.Item, r.Serial), r => r.Id);
            var missing = withSerial.OrderBy(l => l.LineNo).FirstOrDefault(l => !ids.ContainsKey((l.ItemId!.Value, l.SerialNo!)));
            if (missing is not null)
            {
                throw new DomainRuleException($"找不到序號 {missing.SerialNo}（第 {missing.LineNo} 行）。");
            }
        }
        return withSerial.ToDictionary(l => l.Id, l => ids[(l.ItemId!.Value, l.SerialNo!)]);
    }

    /// <summary>一次回填明細的 lot_id／serial_id（只更新有批號或序號的行）。</summary>
    public static async Task FillLineDimensionsAsync(
        WarehouseDbContext db, IReadOnlyDictionary<Guid, Guid> lotIds, IReadOnlyDictionary<Guid, Guid> serialIds, CancellationToken cancellationToken)
    {
        var lineIds = lotIds.Keys.Union(serialIds.Keys).ToArray();
        if (lineIds.Length == 0)
        {
            return;
        }
        await ExecuteAsync(db, """
            UPDATE stock_document_lines l SET lot_id = u.lot_id, serial_id = u.serial_id
            FROM unnest(@ids, @lots, @serials) AS u(id, lot_id, serial_id)
            WHERE l.id = u.id
            """, cancellationToken,
            Param("ids", lineIds),
            new NpgsqlParameter("lots", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = lineIds.Select(id => lotIds.TryGetValue(id, out var v) ? v : (Guid?)null).ToArray() },
            new NpgsqlParameter("serials", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = lineIds.Select(id => serialIds.TryGetValue(id, out var v) ? v : (Guid?)null).ToArray() });
    }
}
