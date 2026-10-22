using Warehouse.Domain.Items;
using Warehouse.Domain.Scanning;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Scanning;

/// <summary>
/// 表單欄位收到掃碼內容時的共同轉換（M5-01）：單據、期初單、預留的「儲位碼」「料號」欄可以直接收掃到的值，
/// 不必先由畫面解析。只做精確比對。
/// </summary>
internal static class ScanCodes
{
    /// <summary>
    /// 儲位欄：<c>WH:LOC:倉庫碼:儲位碼</c> 取出儲位碼與 QR 上的倉庫碼（呼叫端要確認與單據倉庫相同）；
    /// 其他輸入正規化後當儲位碼，倉庫碼為 null。
    /// </summary>
    public static (string Code, string? WarehouseCode) Location(string? input)
    {
        var scan = ScanCodeParser.Parse(input);
        return scan.Kind == ScanCodeKind.Location
            ? (scan.LocationCode!, scan.WarehouseCode)
            : (scan.Normalized, null);
    }

    /// <summary>
    /// 料號欄：正式料號（可帶 <c>WH:ITEM:</c> 前綴）或有效原廠條碼。回傳的查詢最多一筆
    /// （料號唯一、有效條碼唯一，且條碼不可是料號格式）；<c>Display</c> 用於錯誤訊息。
    /// </summary>
    public static (IQueryable<Item> Query, string Display) Items(WarehouseDbContext db, string? input)
    {
        var scan = ScanCodeParser.Parse(input);
        if (scan.Kind == ScanCodeKind.Item)
        {
            var code = scan.ItemCode!;
            return (db.Items.Where(i => i.ItemCode == code), code);
        }
        var text = scan.Normalized;
        var barcodeItemIds = db.ItemBarcodes.Where(b => b.IsActive && b.NormalizedText == text).Select(b => b.ItemId);
        return (db.Items.Where(i => i.ItemCode == text || barcodeItemIds.Contains(i.Id)), text);
    }
}
