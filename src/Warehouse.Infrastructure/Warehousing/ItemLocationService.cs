using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Warehousing;

/// <summary>
/// <see cref="IItemLocationService"/> 實作（M3-07）。設定預設倉位時，在同一交易先取消同倉庫原本的預設再設定新的
/// （順序很重要：資料庫部分唯一索引 ux_item_locations_default 不允許同時有兩個預設）。
/// </summary>
public sealed class ItemLocationService(WarehouseDbContext db, TimeProvider clock) : IItemLocationService
{
    private const int MaxSuggestions = 10;

    public async Task<IReadOnlyList<ItemLocationRow>> ListAsync(Guid itemId, CancellationToken cancellationToken) =>
        await (
            from x in db.Set<ItemLocation>().AsNoTracking()
            where x.ItemId == itemId
            join l in db.Locations on x.LocationId equals l.Id
            join w in db.Warehouses on x.WarehouseId equals w.Id
            orderby w.Code, x.IsDefault descending, l.Code
            select new ItemLocationRow(x.Id, w.Id, w.Code, l.Id, l.Code, l.Status, x.IsDefault))
            .ToListAsync(cancellationToken);

    public async Task AddAsync(
        Guid itemId, Guid warehouseId, string locationCode, bool isDefault, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Items.AnyAsync(i => i.Id == itemId && i.Status == ItemStatus.Active, cancellationToken))
        {
            throw new DomainRuleException("只有有效的物料可以設定常用倉位。");
        }
        var code = TextNormalizer.Normalize(locationCode);
        var location = await db.Locations.AsNoTracking()
            .SingleOrDefaultAsync(l => l.WarehouseId == warehouseId && l.Code == code, cancellationToken)
            ?? throw new DomainRuleException($"這個倉庫沒有儲位 {code}。");
        if (location.Status == LocationStatus.Inactive)
        {
            throw new DomainRuleException($"儲位 {code} 已停用。");
        }
        var now = clock.GetUtcNow();
        if (isDefault)
        {
            await ClearDefaultAsync(itemId, warehouseId, cancellationToken);
        }
        db.Add(ItemLocation.Create(Guid.NewGuid(), itemId, warehouseId, location.Id, isDefault, now));
        AuditLog.Add(db, actor, now, "item_location.add", "item", itemId.ToString(), null, new { Location = code, IsDefault = isDefault });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetDefaultAsync(Guid itemId, Guid itemLocationId, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.Set<ItemLocation>().SingleOrDefaultAsync(x => x.Id == itemLocationId && x.ItemId == itemId, cancellationToken)
            ?? throw new NotFoundException("item_location.not_found", "找不到這個常用倉位。");
        await ClearDefaultAsync(itemId, row.WarehouseId, cancellationToken);
        row.SetDefault(true);
        AuditLog.Add(db, actor, clock.GetUtcNow(), "item_location.default", "item", itemId.ToString(), null, new { row.LocationId });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid itemId, Guid itemLocationId, Actor actor, CancellationToken cancellationToken)
    {
        var row = await db.Set<ItemLocation>().SingleOrDefaultAsync(x => x.Id == itemLocationId && x.ItemId == itemId, cancellationToken)
            ?? throw new NotFoundException("item_location.not_found", "找不到這個常用倉位。");
        db.Remove(row);
        AuditLog.Add(db, actor, clock.GetUtcNow(), "item_location.remove", "item", itemId.ToString(), new { row.LocationId, row.IsDefault }, null);
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocationSuggestion>> SuggestAsync(Guid itemId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var configured = await (
            from x in db.Set<ItemLocation>().AsNoTracking()
            where x.ItemId == itemId && x.WarehouseId == warehouseId
            join l in db.Locations on x.LocationId equals l.Id
            where l.Status != LocationStatus.Inactive
            select new { l.Id, l.Code, l.Status, x.IsDefault })
            .ToListAsync(cancellationToken);
        var stocked = await (
            from b in db.StockBalances.AsNoTracking()
            where b.ItemId == itemId && b.Quantity > 0
            join l in db.Locations on b.LocationId equals l.Id
            where l.WarehouseId == warehouseId && l.Status != LocationStatus.Inactive
            group b by new { l.Id, l.Code, l.Status } into g
            orderby g.Sum(b => b.Quantity) descending
            select new { g.Key.Id, g.Key.Code, g.Key.Status, Quantity = g.Sum(b => b.Quantity) })
            .Take(MaxSuggestions)
            .ToListAsync(cancellationToken);
        // 常用倉位的現有數量另外查：它可能不在「庫存最多的前 10 個」裡（審查 Y5）
        var configuredIds = configured.Select(c => c.Id).ToList();
        var quantityOf = await db.StockBalances.AsNoTracking()
            .Where(b => b.ItemId == itemId && configuredIds.Contains(b.LocationId) && b.Quantity > 0)
            .GroupBy(b => b.LocationId)
            .Select(g => new { LocationId = g.Key, Quantity = g.Sum(b => b.Quantity) })
            .ToDictionaryAsync(x => x.LocationId, x => x.Quantity, cancellationToken);

        var suggestions = configured
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new LocationSuggestion(
                c.Id, c.Code, c.Status, c.IsDefault ? SuggestionReason.Default : SuggestionReason.Frequent, quantityOf.GetValueOrDefault(c.Id)))
            .Concat(stocked.Where(s => configured.All(c => c.Id != s.Id))
                .Select(s => new LocationSuggestion(s.Id, s.Code, s.Status, SuggestionReason.HasStock, s.Quantity)))
            .Take(MaxSuggestions)
            .ToList();
        return suggestions;
    }

    private Task ClearDefaultAsync(Guid itemId, Guid warehouseId, CancellationToken cancellationToken) =>
        DbCommands.ExecuteAsync(db,
            "UPDATE item_locations SET is_default = false WHERE item_id = @item AND warehouse_id = @warehouse AND is_default",
            cancellationToken, DbCommands.Param("item", itemId), DbCommands.Param("warehouse", warehouseId));

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new DomainRuleException(pg.ConstraintName == "ux_item_locations_default"
                ? "同時有人設定了這個倉庫的預設倉位，請重新整理。"
                : "這個儲位已經是此物料的常用倉位。");
        }
    }
}
