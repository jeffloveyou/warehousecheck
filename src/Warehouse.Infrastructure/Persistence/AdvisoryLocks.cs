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
    /// 「提出時自動核准」的鎖（D-31 審查 🟠-1）：提出與核准在同一個交易，提交前別人看不到這筆新物料，
    /// 兩個自動核准同時進行時，重複檢查會互相看不到。所有自動核准（表單、匯入）都先取這把鎖排隊，
    /// 後到的在先到的提交後才查重複。一般提出、手動核准不需要（待核准物料已提交，核准檢查看得到）。
    /// 必須在交易內呼叫。鍵名不可變更（A/B 兩版需鎖到同一把）。
    /// </summary>
    public static Task LockItemAutoApproveAsync(WarehouseDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"item-auto-approve"}))", cancellationToken);

    /// <summary>
    /// 料號前綴的鎖（D-32，ADR-0025）：自動發號、核准時指定料號、變更料號都先取這把鎖，再檢查號碼是否已被物料
    /// 或保留中的歷史料號占用。同一前綴的發號與指定因此排隊，不會同時通過檢查。
    /// 多個前綴時依字母排序取鎖（避免死結）。必須在交易內呼叫。鍵名前綴不可變更（A/B 兩版需鎖到同一把）。
    /// </summary>
    public static async Task LockItemCodePrefixesAsync(WarehouseDbContext db, IEnumerable<string> prefixes, CancellationToken cancellationToken)
    {
        foreach (var prefix in prefixes.Distinct().Order(StringComparer.Ordinal))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"item-code-prefix:" + prefix}))", cancellationToken);
        }
    }

    /// <summary>
    /// 精確識別碼的鎖（M5-01，ADR-0016）：新增專屬別名與原廠條碼前，先以正規化文字取這把鎖，再檢查另一張表。
    /// 兩張表各自的部分唯一索引只能擋「同表重複」；跨表（別名＝條碼）靠這把鎖讓兩個新增排隊，不會同時通過檢查。
    /// 必須在交易內呼叫。鍵名前綴不可變更（A/B 兩版需鎖到同一把）。
    /// </summary>
    public static Task LockIdentifierAsync(WarehouseDbContext db, string normalized, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"ident:" + normalized}))", cancellationToken);

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
