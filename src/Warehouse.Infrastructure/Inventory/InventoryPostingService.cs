using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IInventoryPostingService"/> 實作：**全系統唯一**寫入 stock_balances 的程式（ADR-0014）。
/// 每筆變化：
/// 1. upsert 結存：INSERT … ON CONFLICT（ux_stock_balances_dimension，NULLS NOT DISTINCT）DO UPDATE 加上數量，RETURNING 新結存。
///    同一列被兩個交易同時更新時，PostgreSQL 讓後者等前者 commit 再加，不會遺失更新。
/// 2. 寫一筆 stock_movements（異動量、異動後結存、操作者、時間）。
/// 兩者在呼叫端的同一交易內；任何一步失敗，整個交易回滾。
/// M3 只處理「增加」；扣減、可用量與負庫存下限檢查在 M4 加入（ADR-0004 條件式更新）。
/// </summary>
public sealed class InventoryPostingService(WarehouseDbContext db) : IInventoryPostingService
{
    private const string UpsertBalanceSql = """
        INSERT INTO stock_balances (id, item_id, location_id, lot_id, serial_id, stock_status, quantity, updated_at)
        VALUES (@id, @item, @location, @lot, @serial, @status, @delta, @now)
        ON CONFLICT (item_id, location_id, lot_id, serial_id, stock_status)
        DO UPDATE SET quantity = stock_balances.quantity + EXCLUDED.quantity, updated_at = EXCLUDED.updated_at
        RETURNING quantity
        """;

    private const string InsertMovementSql = """
        INSERT INTO stock_movements (id, document_id, line_id, item_id, location_id, lot_id, serial_id, stock_status,
            quantity_delta, balance_after, occurred_at, actor_id, actor_name)
        VALUES (@id, @document, @line, @item, @location, @lot, @serial, @status, @delta, @after, @now, @actor, @actor_name)
        """;

    public async Task ApplyAsync(
        Guid documentId, IReadOnlyList<StockChange> changes, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utc = now.ToUniversalTime();
        foreach (var change in changes)
        {
            if (change.Delta <= 0)
            {
                throw new NotSupportedException("M3 的過帳只支援增加；扣減在 M4 實作（需可用量與負庫存檢查）。");
            }

            decimal balanceAfter;
            try
            {
                balanceAfter = (await QueryAsync(db, UpsertBalanceSql, r => r.GetDecimal(0), cancellationToken,
                    Param("id", Guid.NewGuid()), Param("item", change.ItemId), Param("location", change.LocationId),
                    Uuid("lot", change.LotId), Uuid("serial", change.SerialId), Param("status", change.Status.ToString()),
                    Param("delta", change.Delta), Param("now", utc))).Single();
            }
            catch (PostgresException ex) when (ex.ConstraintName is "ux_stock_balances_serial_in_stock" or "ck_stock_balances_serial_quantity")
            {
                throw new DomainRuleException("序號已經在庫（同一序號不能同時在兩個儲位，也不能入庫兩次）。");
            }

            await ExecuteAsync(db, InsertMovementSql, cancellationToken,
                Param("id", Guid.NewGuid()), Param("document", documentId), Param("line", change.LineId),
                Param("item", change.ItemId), Param("location", change.LocationId), Uuid("lot", change.LotId),
                Uuid("serial", change.SerialId), Param("status", change.Status.ToString()), Param("delta", change.Delta),
                Param("after", balanceAfter), Param("now", utc), Param("actor", actor.Id), Param("actor_name", actor.Name));
        }
    }
}
