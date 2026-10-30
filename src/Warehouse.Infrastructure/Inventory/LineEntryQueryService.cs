using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Text;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="ILineEntryQueryService"/> 實作（M8-02）。每次查詢最多 2 次資料庫往返的篩選＋排序＋筆數上限；
/// 「有庫存的儲位」以結存表依儲位加總（有 ix_stock_balances 的料號索引）。
/// </summary>
public sealed class LineEntryQueryService(WarehouseDbContext db) : ILineEntryQueryService
{
    public async Task<IReadOnlyList<LocationOption>> LocationsAsync(
        Guid warehouseId, string? keyword, Guid? itemId, LocationListMode mode, CancellationToken cancellationToken)
    {
        var locations = Filter(warehouseId, keyword);
        var warehouseCode = await db.Warehouses.AsNoTracking().Where(w => w.Id == warehouseId).Select(w => w.Code)
            .SingleOrDefaultAsync(cancellationToken);
        if (warehouseCode is null)
        {
            return [];
        }

        // 這個料號在本倉各儲位的結存合計（只取篩選後的儲位）
        var stock = itemId is { } item && mode != LocationListMode.Plain
            ? await db.StockBalances.AsNoTracking()
                .Where(b => b.ItemId == item && b.Quantity > 0 && locations.Any(l => l.Id == b.LocationId))
                .GroupBy(b => b.LocationId)
                .Select(g => new { LocationId = g.Key, Quantity = g.Sum(b => b.Quantity) })
                .ToDictionaryAsync(x => x.LocationId, x => x.Quantity, cancellationToken)
            : [];

        var priority = new List<(Guid Id, string? Reason)>();
        if (itemId is not null && mode == LocationListMode.StockAscending)
        {
            // 超過上限時保留數量少的儲位（使用者要先清掉零散的）
            priority.AddRange(stock.OrderBy(kv => kv.Value).Select(kv => (kv.Key, (string?)"Stock")));
        }
        else if (itemId is not null && mode == LocationListMode.Source)
        {
            // 超過上限時保留數量多的儲位
            priority.AddRange(stock.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, (string?)"Stock")));
        }
        else if (itemId is { } inboundItem && mode == LocationListMode.Inbound)
        {
            var favorites = await db.ItemLocations.AsNoTracking()
                .Where(x => x.ItemId == inboundItem && x.WarehouseId == warehouseId && locations.Any(l => l.Id == x.LocationId))
                .OrderByDescending(x => x.IsDefault)
                .Select(x => new { x.LocationId, x.IsDefault })
                .ToListAsync(cancellationToken);
            priority.AddRange(favorites.Select(f => (f.LocationId, (string?)(f.IsDefault ? "Default" : "Frequent"))));
            // 進倉時也把已有庫存的儲位排在常用之後（同一料號放在一起）
            priority.AddRange(stock.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Where(k => favorites.All(f => f.LocationId != k)).Select(k => (k, (string?)"Stock")));
        }

        var priorityIds = priority.Select(p => p.Id).Take(ILineEntryQueryService.LocationLimit).ToList();
        var first = await locations.Where(l => priorityIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Code, l.Name, l.Status }).ToListAsync(cancellationToken);
        var rest = await locations.Where(l => !priorityIds.Contains(l.Id))
            .OrderBy(l => l.Code)
            .Take(ILineEntryQueryService.LocationLimit)
            .Select(l => new { l.Id, l.Code, l.Name, l.Status })
            .ToListAsync(cancellationToken);

        var reasons = priority.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Reason);
        // 依原因排序（預設 → 常用 → 有庫存），同一原因依儲位碼（字典的順序不固定，不能用加入順序）
        static int Rank(string? reason) => reason switch { "Default" => 0, "Frequent" => 1, _ => 2 };
        var ordered = (mode == LocationListMode.StockAscending
                ? first.OrderBy(l => stock.GetValueOrDefault(l.Id)).ThenBy(l => l.Code, StringComparer.Ordinal)
                : first.OrderBy(l => Rank(reasons.GetValueOrDefault(l.Id))).ThenBy(l => l.Code, StringComparer.Ordinal))
            .Concat(rest);
        return ordered
            .Select(l => new LocationOption(
                l.Id, warehouseCode, l.Code, l.Name, l.Status,
                stock.TryGetValue(l.Id, out var quantity) ? quantity : null,
                reasons.GetValueOrDefault(l.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<LotOption>> LotsAsync(
        Guid warehouseId, Guid itemId, Guid? locationId, string? keyword, CancellationToken cancellationToken)
    {
        var balances = db.StockBalances.AsNoTracking()
            .Where(b => b.ItemId == itemId && b.Quantity > 0
                && db.Locations.Any(l => l.Id == b.LocationId && l.WarehouseId == warehouseId));
        if (locationId is { } location)
        {
            balances = balances.Where(b => b.LocationId == location);
        }

        var rows =
            from b in balances
            join loc in db.Locations on b.LocationId equals loc.Id
            from lot in db.InventoryLots.Where(x => x.Id == b.LotId).DefaultIfEmpty()
            from serial in db.ItemSerials.Where(x => x.Id == b.SerialId).DefaultIfEmpty()
            where b.LotId != null || b.SerialId != null
            select new { b, loc.Code, LotNo = (string?)lot.LotNo, Expiry = (DateOnly?)lot.ExpiryDate, SerialNo = (string?)serial.SerialNo };

        var text = Trim(keyword);
        if (text is not null)
        {
            var pattern = LikePattern.Contains(text);
            rows = rows.Where(r => (r.LotNo != null && EF.Functions.ILike(r.LotNo, pattern, LikePattern.Escape))
                || (r.SerialNo != null && EF.Functions.ILike(r.SerialNo, pattern, LikePattern.Escape)));
        }

        return await rows
            .OrderBy(r => r.Expiry == null)
            .ThenBy(r => r.Expiry)
            .ThenBy(r => r.LotNo)
            .ThenBy(r => r.SerialNo)
            .ThenBy(r => r.Code)
            .Take(ILineEntryQueryService.LotLimit)
            .Select(r => new LotOption(r.b.LocationId, r.Code, r.LotNo, r.SerialNo, r.Expiry, r.b.StockStatus, r.b.Quantity))
            .ToListAsync(cancellationToken);
    }

    /// <summary>可作業的儲位：本倉、非停用、倉庫沒有被刪除；關鍵字比對儲位碼（正規化）與名稱、區、架（與儲位頁相同規則）。</summary>
    private IQueryable<Location> Filter(Guid warehouseId, string? keyword)
    {
        var locations = db.Locations.AsNoTracking()
            .Where(l => l.WarehouseId == warehouseId && l.Status != LocationStatus.Inactive
                && db.Warehouses.Any(w => w.Id == warehouseId && w.DeletedAt == null));
        var text = Trim(keyword);
        if (text is null)
        {
            return locations;
        }
        var pattern = LikePattern.Contains(TextNormalizer.Normalize(text));
        var raw = LikePattern.Contains(text);
        return locations.Where(l =>
            EF.Functions.Like(l.Code, pattern, LikePattern.Escape)
            || (l.Name != null && EF.Functions.ILike(l.Name, raw, LikePattern.Escape))
            || (l.Zone != null && EF.Functions.ILike(l.Zone, raw, LikePattern.Escape))
            || (l.Rack != null && EF.Functions.ILike(l.Rack, raw, LikePattern.Escape)));
    }

    private static string? Trim(string? keyword)
    {
        var text = keyword?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        return text.Length > Paging.MaxKeywordLength ? text[..Paging.MaxKeywordLength] : text;
    }
}
