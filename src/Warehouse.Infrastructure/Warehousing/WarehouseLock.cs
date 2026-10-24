using Warehouse.Application.Errors;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Warehousing;

/// <summary>
/// 寫入前鎖住倉庫列（FOR SHARE）並在鎖內確認倉庫可用（M8-01 審查 #1）：停用、刪除倉庫時會先取 FOR UPDATE，
/// 所以「先檢查、後新增」（開單、盤點、期初、新增儲位、常用儲位、儲位圖片、帳號範圍）不會在刪除的同時插入資料。
/// 鎖的順序一律「倉庫 → 儲位」，避免和刪除互相等待（死結）。必須在交易內呼叫。
/// </summary>
internal static class WarehouseLock
{
    /// <summary>鎖住並回傳倉庫碼；找不到丟 NotFound，已刪除或停用丟 DomainRuleException（<paramref name="inactiveMessage"/>）。</summary>
    public static async Task<string> LockUsableAsync(
        WarehouseDbContext db, Guid warehouseId, string inactiveMessage, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(db, "SELECT code, is_active, deleted_at IS NOT NULL FROM warehouses WHERE id = @id FOR SHARE",
            r => (Code: r.GetString(0), IsActive: r.GetBoolean(1), IsDeleted: r.GetBoolean(2)), cancellationToken, Param("id", warehouseId));
        if (rows.Count == 0)
        {
            throw new NotFoundException("warehouse.not_found", "找不到倉庫（可能已被刪除）。");
        }
        var row = rows[0];
        if (row.IsDeleted)
        {
            throw new DomainRuleException($"倉庫 {row.Code} 已刪除。");
        }
        if (!row.IsActive)
        {
            throw new DomainRuleException(inactiveMessage);
        }
        return row.Code;
    }

    /// <summary>鎖住多個倉庫（依 id 排序，避免交叉等待）；任一個不存在或已刪除就拒絕。用於帳號範圍（停用的倉庫可以指定）。</summary>
    public static async Task LockNotDeletedAsync(WarehouseDbContext db, IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken)
    {
        if (warehouseIds.Count == 0)
        {
            return;
        }
        var rows = await QueryAsync(db, "SELECT id FROM warehouses WHERE id = ANY(@ids) AND deleted_at IS NULL ORDER BY id FOR SHARE",
            r => r.GetGuid(0), cancellationToken, Param("ids", warehouseIds.ToArray()));
        if (rows.Count != warehouseIds.Count)
        {
            throw new DomainRuleException("有不存在或已刪除的倉庫，請重新整理後再選。");
        }
    }
}
