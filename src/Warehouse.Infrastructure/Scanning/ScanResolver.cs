using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Scanning;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Scanning;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Scanning;

/// <summary>
/// <see cref="IScanResolver"/> 的實作。每一步都是索引上的精確比對（料號、條碼與別名的部分唯一索引、儲位碼），
/// 一筆掃碼最多約 5 句小查詢；不呼叫模糊搜尋。
/// </summary>
public sealed class ScanResolver(WarehouseDbContext db, IWarehouseService warehouses) : IScanResolver
{
    public async Task<ScanResolution> ResolveAsync(string? raw, Guid? warehouseId, CancellationToken cancellationToken)
    {
        var original = raw ?? string.Empty;
        var (value, error) = ScanInput.Check(original);
        if (error is not null)
        {
            return Rejected(original, value, error);
        }
        var scan = ScanCodeParser.Parse(value);

        if (scan.Kind == ScanCodeKind.Location)
        {
            var location = await warehouses.GetLocationByCodeAsync(scan.WarehouseCode!, scan.LocationCode!, cancellationToken);
            if (location is null)
            {
                return Rejected(original, value, $"找不到儲位 {scan.WarehouseCode} {scan.LocationCode}。");
            }
            if (warehouseId is { } expected && location.WarehouseId != expected)
            {
                return Rejected(original, value, $"這是倉庫 {location.WarehouseCode} 的儲位，與目前作業的倉庫不同。");
            }
            return new ScanResolution(ScanResultKind.Location, original, value, null, location, null);
        }

        if (scan.Kind == ScanCodeKind.Item)
        {
            var code = scan.ItemCode!;
            var byCode = await FindItemAsync(db.Items.Where(i => i.ItemCode == code), ScanMatch.ItemCode, 1m, cancellationToken);
            if (byCode.Item is null && !byCode.Unusable
                && await new Items.ItemCodeRules(db).ReservedOwnerAsync(code, cancellationToken) is { } ownerId)
            {
                // 舊標籤上的歷史料號（D-32）：對應到同一個物料，畫面顯示目前的新料號
                byCode = await FindItemAsync(db.Items.Where(i => i.Id == ownerId), ScanMatch.OldItemCode, 1m, cancellationToken);
            }
            return byCode.Item is not null
                ? new ScanResolution(ScanResultKind.Item, original, value, null, null, byCode.Item)
                : Rejected(original, value, byCode.Unusable ? $"料號 {code} 尚未核准或已拒絕，不能使用。" : $"找不到料號 {code}。");
        }

        var normalized = scan.Normalized;
        var barcode = await db.ItemBarcodes.AsNoTracking()
            .Where(b => b.IsActive && b.NormalizedText == normalized)
            .Select(b => new { b.ItemId, b.PackQuantity })
            .SingleOrDefaultAsync(cancellationToken);
        if (barcode is not null)
        {
            var byBarcode = await FindItemAsync(
                db.Items.Where(i => i.Id == barcode.ItemId), ScanMatch.Barcode, barcode.PackQuantity, cancellationToken);
            return byBarcode.Item is not null
                ? new ScanResolution(ScanResultKind.Item, original, value, null, null, byBarcode.Item)
                : Rejected(original, value, "這個條碼登錄的物料尚未核准或已拒絕，不能使用。");
        }

        var aliasItemIds = db.ItemAliases.Where(a => a.IsActive && a.NormalizedText == normalized).Select(a => a.ItemId);
        var byAlias = await FindItemAsync(db.Items.Where(i => aliasItemIds.Contains(i.Id)), ScanMatch.Alias, 1m, cancellationToken);
        if (byAlias.Item is not null)
        {
            return new ScanResolution(ScanResultKind.Item, original, value, null, null, byAlias.Item);
        }
        if (byAlias.Unusable)
        {
            // 別名指向不能用的物料：明確拒絕，不落到「其他文字」被當成批號填入（M5-01 審查 S2）
            return Rejected(original, value, "這個專屬別名對應的物料尚未核准或已拒絕，不能使用。");
        }

        // 沒有前綴的儲位碼（例 A-01-01）：只在指定倉庫時精確查詢
        if (warehouseId is { } id)
        {
            var warehouseCode = await db.Warehouses.AsNoTracking()
                .Where(w => w.Id == id).Select(w => w.Code).SingleOrDefaultAsync(cancellationToken);
            if (warehouseCode is not null
                && await warehouses.GetLocationByCodeAsync(warehouseCode, normalized, cancellationToken) is { } location)
            {
                return new ScanResolution(ScanResultKind.Location, original, value, null, location, null);
            }
        }

        return new ScanResolution(ScanResultKind.Text, original, value, null, null, null);
    }

    /// <summary>
    /// 查物料的掃碼摘要。有效與已停用的物料都回傳（<see cref="ScanItem.IsActive"/>；停用物料仍可出庫清空，由呼叫端判斷）；
    /// 待核准、已退回、已拒絕的物料不能用在任何作業，<c>Unusable</c> 為 true。
    /// </summary>
    private async Task<(ScanItem? Item, bool Unusable)> FindItemAsync(
        IQueryable<Item> items, ScanMatch match, decimal packQuantity, CancellationToken cancellationToken)
    {
        var row = await items.AsNoTracking()
            .Select(i => new
            {
                i.Id, i.ItemCode, i.Name, i.Specification, i.TrackingMode, i.Status,
                Unit = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => new { u.Code, u.DecimalPlaces }).First(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return (null, false);
        }
        if (row.Status is not (ItemStatus.Active or ItemStatus.Inactive) || row.ItemCode is null)
        {
            return (null, true);
        }
        return (new ScanItem(
            row.Id, row.ItemCode, row.Name, row.Specification, row.Unit.Code, row.Unit.DecimalPlaces, row.TrackingMode,
            match, packQuantity, row.Status == ItemStatus.Active), false);
    }

    private static ScanResolution Rejected(string raw, string value, string message) =>
        new(ScanResultKind.Rejected, raw, value, message, null, null);
}
