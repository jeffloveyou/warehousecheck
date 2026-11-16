using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Infrastructure.Inventory;

public sealed partial class ScanCartService
{
    public Task<bool> OwnsAsync(Guid id, Actor actor, CancellationToken cancellationToken) =>
        db.ScanCarts.AsNoTracking().AnyAsync(c => c.Id == id && c.OwnerId == actor.Id, cancellationToken);
    public async Task<PagedResult<ScanCartSummary>> ListPageAsync(Actor actor, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var query = db.ScanCarts.AsNoTracking().Where(c => c.OwnerId == actor.Id);
        var count = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id).Skip((page - 1) * 20).Take(20)
            .Select(c => new ScanCartSummary(c.Id, c.Note, db.Warehouses.Where(w => w.Id == c.WarehouseId).Select(w => w.Code).FirstOrDefault(),
                c.Lines.Count, c.CreatedAt, c.UpdatedAt)).ToListAsync(cancellationToken);
        return new(rows, page, 20, count);
    }

    public async Task<IReadOnlyList<RecipientOption>> RecipientsAsync(string? query, CancellationToken cancellationToken)
    {
        var text = (query ?? "").Trim();
        if (text.Length > 100) text = text[..100];
        return await db.Users.AsNoTracking().Where(u => u.IsActive && (text == "" || u.DisplayName.Contains(text) || u.UserName!.Contains(text)))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(20)
            .Select(u => new RecipientOption(u.Id, u.UserName!, u.DisplayName)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScanCartStockRow>> StockAsync(Guid id, Guid warehouseId, Actor actor, CancellationToken cancellationToken)
    {
        var cart = await GetAsync(id, actor, cancellationToken);
        if (cart is null) return [];
        await WarehouseAccess.EnsureAsync(db, actor, warehouseId, cancellationToken);
        var ids = cart.Lines.Select(l => l.ItemId).Distinct().ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var available = (await Warehouse.Infrastructure.Persistence.DbCommands.QueryAsync(db, InventoryPostingService.AvailableSql,
            r => (Id: r.GetGuid(0), Quantity: r.GetDecimal(2)), cancellationToken,
            Warehouse.Infrastructure.Persistence.DbCommands.Param("items", ids),
            Warehouse.Infrastructure.Persistence.DbCommands.Param("warehouses", ids.Select(_ => warehouseId).ToArray())))
            .ToDictionary(r => r.Id, r => r.Quantity);
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.ItemCode, i.AllowNegativeStock, i.NegativeStockLimit }).ToListAsync(cancellationToken);
        // 一次查各料前 20 個有效來源；查詢數不隨行數增加，500 行最多回 10,000 個儲位。
        const string sql = """
            SELECT item_id, code, quantity FROM (
                SELECT b.item_id, l.code, SUM(b.quantity) AS quantity,
                    ROW_NUMBER() OVER (PARTITION BY b.item_id ORDER BY l.code) AS rn
                FROM stock_balances b JOIN locations l ON l.id = b.location_id
                WHERE b.item_id = ANY(@items) AND l.warehouse_id = @warehouse
                    AND l.status = 'Available' AND b.stock_status = 'Available'
                GROUP BY b.item_id, l.code
            ) s WHERE rn <= 20 ORDER BY item_id, code
            """;
        var locations = await Warehouse.Infrastructure.Persistence.DbCommands.QueryAsync(db, sql,
            r => (Id: r.GetGuid(0), Code: r.GetString(1), Quantity: r.GetDecimal(2)), cancellationToken,
            Warehouse.Infrastructure.Persistence.DbCommands.Param("items", ids),
            Warehouse.Infrastructure.Persistence.DbCommands.Param("warehouse", warehouseId));
        var result = new List<ScanCartStockRow>();
        foreach (var i in items)
        {
            var rows = locations.Where(l => l.Id == i.Id).ToList();
            if (rows.Count == 0) result.Add(new(i.Id, i.ItemCode!, "", 0, available.GetValueOrDefault(i.Id), i.AllowNegativeStock, i.NegativeStockLimit));
            else result.AddRange(rows.Select(l => new ScanCartStockRow(i.Id, i.ItemCode!, l.Code, l.Quantity,
                available.GetValueOrDefault(i.Id), i.AllowNegativeStock, i.NegativeStockLimit)));
        }
        return result;
    }
}
