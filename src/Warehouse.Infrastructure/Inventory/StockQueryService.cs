using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>結存、異動、尚未清點儲位的查詢（唯讀，AsNoTracking＋投影，伺服器分頁）。</summary>
public sealed class StockQueryService(WarehouseDbContext db) : IStockQueryService
{
    public async Task<PagedResult<BalanceRow>> GetBalancesAsync(
        Guid? warehouseId, Guid? locationId, Guid? itemId, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var balances = db.StockBalances.AsNoTracking().Where(b => b.Quantity != 0);
        if (locationId is { } l)
        {
            balances = balances.Where(b => b.LocationId == l);
        }
        if (itemId is { } i)
        {
            balances = balances.Where(b => b.ItemId == i);
        }
        if (warehouseId is { } w)
        {
            balances = balances.Where(b => db.Locations.Any(x => x.Id == b.LocationId && x.WarehouseId == w));
        }

        var query =
            from b in balances
            join item in db.Items on b.ItemId equals item.Id
            join unit in db.Units on item.BaseUnitId equals unit.Id
            join loc in db.Locations on b.LocationId equals loc.Id
            join wh in db.Warehouses on loc.WarehouseId equals wh.Id
            from lot in db.InventoryLots.Where(x => x.Id == b.LotId).DefaultIfEmpty()
            from serial in db.ItemSerials.Where(x => x.Id == b.SerialId).DefaultIfEmpty()
            orderby wh.Code, loc.Code, item.ItemCode, lot.LotNo, serial.SerialNo
            select new BalanceRow(
                item.Id, item.ItemCode, item.Name, unit.Code, loc.Id, wh.Code, loc.Code, lot.LotNo, lot.ExpiryDate,
                serial.SerialNo, b.StockStatus, loc.Status == LocationStatus.Available, b.Quantity);

        var total = await balances.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize).ToListAsync(cancellationToken);
        return new PagedResult<BalanceRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<PagedResult<MovementRow>> GetMovementsAsync(
        Guid? itemId, Guid? locationId, Guid? documentId, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var movements = db.StockMovements.AsNoTracking();
        if (itemId is { } i)
        {
            movements = movements.Where(m => m.ItemId == i);
        }
        if (locationId is { } l)
        {
            movements = movements.Where(m => m.LocationId == l);
        }
        if (documentId is { } d)
        {
            movements = movements.Where(m => m.DocumentId == d);
        }

        var query =
            from m in movements
            join doc in db.StockDocuments on m.DocumentId equals doc.Id
            join item in db.Items on m.ItemId equals item.Id
            join loc in db.Locations on m.LocationId equals loc.Id
            join wh in db.Warehouses on loc.WarehouseId equals wh.Id
            from lot in db.InventoryLots.Where(x => x.Id == m.LotId).DefaultIfEmpty()
            from serial in db.ItemSerials.Where(x => x.Id == m.SerialId).DefaultIfEmpty()
            orderby m.OccurredAt descending, m.Id
            select new MovementRow(
                m.OccurredAt, doc.DocNo, doc.DocType, item.ItemCode, item.Name, wh.Code, loc.Code, lot.LotNo,
                serial.SerialNo, m.StockStatus, m.QuantityDelta, m.BalanceAfter, m.ActorName);

        var total = await movements.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize).ToListAsync(cancellationToken);
        return new PagedResult<MovementRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<PagedResult<UncountedLocationRow>> GetUncountedLocationsAsync(
        Guid warehouseId, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var locations = db.Locations.AsNoTracking()
            .Where(l => l.WarehouseId == warehouseId && l.OpeningDocumentId == null && l.Status != LocationStatus.Inactive);
        var total = await locations.CountAsync(cancellationToken);
        var rows = await locations
            .OrderBy(l => l.Code)
            .Skip((page - 1) * Paging.DefaultPageSize)
            .Take(Paging.DefaultPageSize)
            .Select(l => new UncountedLocationRow(
                l.Id, db.Warehouses.Where(w => w.Id == l.WarehouseId).Select(w => w.Code).First(), l.Code, l.Name))
            .ToListAsync(cancellationToken);
        return new PagedResult<UncountedLocationRow>(rows, page, Paging.DefaultPageSize, total);
    }
}
