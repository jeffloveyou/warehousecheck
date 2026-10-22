using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>結存、異動、尚未清點儲位的查詢（唯讀，AsNoTracking＋投影，伺服器分頁）。</summary>
public sealed class StockQueryService(WarehouseDbContext db, TimeProvider? clock = null) : IStockQueryService
{
    public async Task<PagedResult<NegativeBalanceRow>> GetNegativeBalancesAsync(Guid? warehouseId, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var query =
            from b in db.StockBalances.AsNoTracking()
            where b.Quantity < 0
            join item in db.Items on b.ItemId equals item.Id
            join unit in db.Units on item.BaseUnitId equals unit.Id
            join loc in db.Locations on b.LocationId equals loc.Id
            join wh in db.Warehouses on loc.WarehouseId equals wh.Id
            where warehouseId == null || wh.Id == warehouseId
            let last = db.StockMovements
                .Where(m => m.ItemId == b.ItemId && m.LocationId == b.LocationId && m.IsNegative)
                .OrderByDescending(m => m.OccurredAt)
                .FirstOrDefault()
            orderby wh.Code, loc.Code, item.ItemCode
            select new NegativeBalanceRow(
                item.Id, item.ItemCode ?? item.RequestNo, item.Name, unit.Code, wh.Code, loc.Code, b.Quantity, item.NegativeStockLimit,
                db.StockDocuments.Where(d => d.Id == last!.DocumentId).Select(d => d.DocNo).FirstOrDefault(),
                last == null ? null : last.DocumentId, last == null ? null : last.OccurredAt);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize).ToListAsync(cancellationToken);
        return new PagedResult<NegativeBalanceRow>(rows, page, Paging.DefaultPageSize, total);
    }

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

    public async Task<PagedResult<MovementRow>> SearchMovementsAsync(MovementQuery q, CancellationToken cancellationToken)
    {
        var page = Paging.ClampPage(q.Page);
        // 沒有料號、儲位、單號，也沒有起日時，只查最近 <see cref="MovementQuery.DefaultDays"/> 天（避免全表掃描與計數，M4-03 審查 #12）
        if (string.IsNullOrWhiteSpace(q.ItemCode) && string.IsNullOrWhiteSpace(q.LocationCode) && string.IsNullOrWhiteSpace(q.DocNo) && q.From is null)
        {
            q = q with { From = (clock ?? TimeProvider.System).GetUtcNow().AddDays(-MovementQuery.DefaultDays) };
        }
        var movements = db.StockMovements.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.ItemCode))
        {
            var code = Domain.Text.TextNormalizer.Normalize(q.ItemCode);
            movements = movements.Where(m => db.Items.Any(i => i.Id == m.ItemId && i.ItemCode == code));
        }
        if (q.WarehouseId is { } w)
        {
            movements = movements.Where(m => db.Locations.Any(l => l.Id == m.LocationId && l.WarehouseId == w));
        }
        if (!string.IsNullOrWhiteSpace(q.LocationCode))
        {
            var code = Domain.Text.TextNormalizer.Normalize(q.LocationCode);
            movements = movements.Where(m => db.Locations.Any(l => l.Id == m.LocationId && l.Code == code
                && (q.WarehouseId == null || l.WarehouseId == q.WarehouseId)));
        }
        if (!string.IsNullOrWhiteSpace(q.DocNo))
        {
            var docNo = q.DocNo.Trim().ToUpperInvariant();
            movements = movements.Where(m => db.StockDocuments.Any(d => d.Id == m.DocumentId && d.DocNo == docNo));
        }
        if (!string.IsNullOrWhiteSpace(q.ActorName))
        {
            var actor = q.ActorName.Trim();
            actor = actor.Length > Paging.MaxKeywordLength ? actor[..Paging.MaxKeywordLength] : actor;
            var pattern = LikePattern.Contains(actor);
            movements = movements.Where(m => EF.Functions.ILike(m.ActorName, pattern, LikePattern.Escape));
        }
        if (q.From is { } from)
        {
            var utc = from.ToUniversalTime();
            movements = movements.Where(m => m.OccurredAt >= utc);
        }
        if (q.To is { } to)
        {
            var utc = to.ToUniversalTime();
            movements = movements.Where(m => m.OccurredAt < utc);
        }
        return await PageMovementsAsync(movements, page, cancellationToken);
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

        return await PageMovementsAsync(movements, page, cancellationToken);
    }

    /// <summary>異動列的共用投影與分頁（單據、物料、儲位、倉庫、批號、序號）。</summary>
    private async Task<PagedResult<MovementRow>> PageMovementsAsync(
        IQueryable<Domain.Inventory.StockMovement> movements, int page, CancellationToken cancellationToken)
    {
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
                serial.SerialNo, m.StockStatus, m.QuantityDelta, m.BalanceAfter, m.ActorName, m.DocumentId, m.IsNegative);

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
