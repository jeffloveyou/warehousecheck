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
}
