using Microsoft.EntityFrameworkCore;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL 交易層級 advisory lock（交易結束自動釋放）。鍵名集中在這裡，避免不同程式用錯名稱而鎖不到同一把。
/// </summary>
internal static class AdvisoryLocks
{
    private const string CategoryTree = "item_categories_tree";

    /// <summary>
    /// 分類樹（上層、前綴）的鎖：
    /// - 修改分類取「獨占」，兩人同時改分類樹會排隊（避免互設上層形成循環）。
    /// - 核准發號取「共享」，多筆核准可同時進行，但會等正在修改前綴的交易完成，
    ///   確保「已發號的前綴不可修改」不會因為同時發生而被繞過（M1-03 審查 R4）。
    /// 必須在交易內呼叫。
    /// </summary>
    public static Task LockCategoryTreeAsync(WarehouseDbContext db, bool exclusive, CancellationToken cancellationToken) =>
        exclusive
            ? db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({CategoryTree}))", cancellationToken)
            : db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtext({CategoryTree}))", cancellationToken);

    /// <summary>
    /// 「料號＋倉庫」庫存鎖鍵（ADR-0004 併發控制）：所有會降低倉庫可用量的寫入（扣減、建立預留）都先取這把鎖，
    /// 再於新語句檢查可用量。SHA-256("stock-iw:"‖item‖warehouse) 前 8 bytes。
    /// **算法不可變更**：A/B 兩個版本同時運作時必須鎖到同一把；變更需排維護窗口。
    /// </summary>
    public static long StockKey(Guid itemId, Guid warehouseId) => Key("stock-iw:", itemId, warehouseId);

    /// <summary>
    /// 「料號」負庫存鎖鍵：允許負庫存的物料扣減時取得，全倉合計下限的檢查與修改負庫存設定靠它排隊（ADR-0004 決策一）。
    /// 同樣不可變更算法。
    /// </summary>
    public static long StockItemKey(Guid itemId) => Key("stock-i:", itemId, Guid.Empty);

    /// <summary>
    /// 依鍵值排序後逐一取得交易層級鎖。排序在程式內做好，SQL 依陣列順序取鎖
    /// （不在 SQL 裡 ORDER BY：PostgreSQL 文件警告取鎖順序不保證）；依鍵值排序，雜湊碰撞也不會死結，只會多等。
    /// </summary>
    public static async Task LockStockAsync(WarehouseDbContext db, IEnumerable<long> keys, CancellationToken cancellationToken)
    {
        var sorted = keys.Distinct().Order().ToArray();
        if (sorted.Length > 0)
        {
            await DbCommands.ExecuteAsync(db, "SELECT pg_advisory_xact_lock(k) FROM unnest(@keys) AS k", cancellationToken,
                DbCommands.Param("keys", sorted));
        }
    }

    private static long Key(string prefix, Guid a, Guid b)
    {
        Span<byte> buffer = stackalloc byte[64];
        var length = System.Text.Encoding.ASCII.GetBytes(prefix, buffer);
        a.TryWriteBytes(buffer[length..]);
        b.TryWriteBytes(buffer[(length + 16)..]);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(buffer[..(length + 32)], hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash);
    }
}
