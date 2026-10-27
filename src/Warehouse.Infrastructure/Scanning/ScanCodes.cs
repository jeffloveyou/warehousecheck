using Microsoft.EntityFrameworkCore;
using Warehouse.Domain;
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
    /// 料號欄：正式料號（可帶 <c>WH:ITEM:</c> 前綴）或有效原廠條碼。回傳的查詢最多一筆；<c>Display</c> 用於錯誤訊息。
    /// 包裝數量不是 1 的條碼（外箱條碼）在這裡拒絕：表單的數量欄是基本單位，直接收外箱條碼會把「1 箱」記成「1 個」
    /// （M5-01 審查 M1）。外箱條碼請用連續掃碼面板，面板會依包裝數量換算後送出正式料號。
    /// 條碼先單獨查出物料 ID，再用主鍵查物料，避免「料號 = x OR id IN (...)」讓 PostgreSQL 掃整張物料表（審查 I3）。
    /// </summary>
    public static async Task<(IQueryable<Item> Query, string Display)> ItemsAsync(
        WarehouseDbContext db, string? input, CancellationToken cancellationToken)
    {
        var scan = ScanCodeParser.Parse(input);
        if (scan.Kind == ScanCodeKind.Item)
        {
            var code = scan.ItemCode!;
            return (await ByCodeAsync(db, code, cancellationToken), code);
        }
        var text = scan.Normalized;
        var barcode = await db.ItemBarcodes.AsNoTracking()
            .Where(b => b.IsActive && b.NormalizedText == text)
            .Select(b => new { b.ItemId, b.PackQuantity })
            .SingleOrDefaultAsync(cancellationToken);
        if (barcode is null)
        {
            return (await ByCodeAsync(db, text, cancellationToken), text);
        }
        if (barcode.PackQuantity != 1m)
        {
            throw new DomainRuleException(
                $"「{text}」是一次代表 {barcode.PackQuantity:0.####} 個基本單位的包裝條碼，這個欄位的數量是基本單位；請改用連續掃碼面板，或直接輸入料號。");
        }
        return (db.Items.Where(i => i.Id == barcode.ItemId), text);
    }

    /// <summary>
    /// 目前的料號；沒有物料使用、而且是保留中的歷史料號時（D-32，舊標籤、舊 Excel）對應到改號後的同一個物料。
    /// 格式不像料號的文字不查歷史表。
    /// </summary>
    private static async Task<IQueryable<Item>> ByCodeAsync(WarehouseDbContext db, string code, CancellationToken cancellationToken)
    {
        var current = db.Items.Where(i => i.ItemCode == code);
        if (!Item.IsValidItemCode(code) || await current.AnyAsync(cancellationToken))
        {
            return current;
        }
        return await new Items.ItemCodeRules(db).ReservedOwnerAsync(code, cancellationToken) is { } ownerId
            ? db.Items.Where(i => i.Id == ownerId)
            : current;
    }
}
