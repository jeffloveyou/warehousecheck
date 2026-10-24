using Microsoft.AspNetCore.Mvc;
using Warehouse.Application.Inventory;
using Warehouse.Application.Search;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Text;

namespace Warehouse.Web.Pages.Shared;

/// <summary>
/// 明細輸入區（M8-02，wwwroot/js/picker.js、doc-scan.js）的 JSON 查詢，進出倉單與期初單共用。
/// 物料搜尋按 Enter 才查（不逐字查，ADR-0015 補充 3）；儲位、批號／序號清單在伺服器篩選、限制筆數。
/// </summary>
public static class LineEntryJson
{
    /// <summary>物料搜尋最多列幾筆候選。</summary>
    public const int ItemLimit = 20;

    /// <summary>
    /// 物料搜尋（輸入框按 Enter）：回傳候選清單。結果只是候選，由人點選後畫面再以精確解析（ScanResolve）帶入，
    /// 模糊結果不會自動成為明細對象。
    /// </summary>
    public static async Task<JsonResult> ItemsAsync(IItemSearchService search, string? q, CancellationToken cancellationToken)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return new JsonResult(new { items = Array.Empty<object>(), total = 0 });
        }
        var parsed = ScanCodeParser.Parse(query);
        var text = parsed.Kind == ScanCodeKind.Item && parsed.ItemCode is not null ? parsed.ItemCode : TextNormalizer.Normalize(query);
        var result = await search.SearchAsync(new ItemSearchRequest(text, false, 1, ItemLimit), cancellationToken);
        return new JsonResult(new
        {
            items = result.Items.Where(h => h.ItemCode is not null).Select(h => new
            {
                id = h.Id, itemCode = h.ItemCode, name = h.Name, brand = h.Brand, partNo = h.ManufacturerPartNo,
                specification = h.SpecificationSummary, unitCode = h.UnitCode, reason = h.Reason.ToString(), hitText = h.HitText,
            }),
            total = result.TotalCount,
        });
    }

    public static async Task<JsonResult> LocationsAsync(
        ILineEntryQueryService queries, Guid warehouseId, string? q, Guid? itemId, LocationListMode mode, CancellationToken cancellationToken)
    {
        var options = await queries.LocationsAsync(warehouseId, q, itemId, mode, cancellationToken);
        return new JsonResult(new
        {
            items = options.Select(o => new
            {
                id = o.Id, warehouseCode = o.WarehouseCode, code = o.Code, name = o.Name, status = o.Status.ToString(),
                quantity = o.Quantity, reason = o.Reason,
            }),
        });
    }

    public static async Task<JsonResult> LotsAsync(
        ILineEntryQueryService queries, Guid warehouseId, Guid itemId, Guid? locationId, string? q, CancellationToken cancellationToken)
    {
        var options = await queries.LotsAsync(warehouseId, itemId, locationId, q, cancellationToken);
        return new JsonResult(new
        {
            items = options.Select(o => new
            {
                locationId = o.LocationId, location = o.LocationCode, lot = o.LotNo, serial = o.SerialNo,
                expiry = o.ExpiryDate?.ToString("yyyy-MM-dd"), status = o.Status.ToString(), quantity = o.Quantity,
            }),
        });
    }
}
