using Warehouse.Application.Scanning;

namespace Warehouse.Web;

/// <summary>
/// 掃碼解析結果給畫面 JS 的 JSON 形狀（單據、期初、掃碼查料共用，wwwroot/js/doc-scan.js 讀取）。
/// 列舉一律轉成文字；<c>extra</c> 是各頁另外附的資料（結存、建議儲位、預留）。
/// </summary>
public static class ScanJson
{
    public static object From(ScanResolution result, object? extra = null) => new
    {
        kind = result.Kind.ToString(),
        value = result.Value,
        message = result.Message,
        location = result.Location is { } l
            ? new { id = l.Id, warehouseId = l.WarehouseId, warehouseCode = l.WarehouseCode, code = l.Code, name = l.Name, status = l.Status.ToString() }
            : null,
        item = result.Item is { } i
            ? new
            {
                id = i.Id, itemCode = i.ItemCode, name = i.Name, specification = i.Specification, unitCode = i.UnitCode,
                decimalPlaces = i.DecimalPlaces, trackingMode = i.TrackingMode.ToString(), matchedBy = i.MatchedBy.ToString(),
                packQuantity = i.PackQuantity, isActive = i.IsActive,
            }
            : null,
        extra,
    };
}
