using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Scanning;

/// <summary>一筆掃碼判定成什麼。</summary>
public enum ScanResultKind
{
    /// <summary>儲位（<c>WH:LOC:</c> QR，或指定倉庫時的儲位碼文字）。</summary>
    Location,

    /// <summary>物料：正式料號、有效原廠條碼或有效專屬別名「完全相同」（物料是有效或已停用；待核准、已拒絕的物料回 Rejected）。</summary>
    Item,

    /// <summary>不是任何已登錄的識別碼：可能是批號、序號，或尚未登錄的條碼；畫面依目前步驟決定用途，不猜物料。</summary>
    Text,

    /// <summary>拒絕：漏字、找不到的料號／儲位、別的倉庫的儲位、停用的物料等。<see cref="ScanResolution.Message"/> 說明原因。</summary>
    Rejected,
}

/// <summary>物料是靠什麼精確比中的。</summary>
public enum ScanMatch
{
    ItemCode,
    Barcode,
    Alias,

    /// <summary>掃到保留中的歷史料號（D-32，ADR-0025）；<see cref="ScanItem.ItemCode"/> 是目前的新料號。</summary>
    OldItemCode,
}

/// <summary>
/// 掃到的物料。<see cref="PackQuantity"/>：比中原廠條碼時是該條碼的包裝數量，其他情況為 1。
/// <see cref="IsActive"/> 為 false 表示已停用：仍可出庫、移庫清空庫存，但不可進倉、期初、預留（由呼叫端依作業判斷，M5-01 審查 S3）。
/// </summary>
public sealed record ScanItem(
    Guid Id,
    string ItemCode,
    string Name,
    string? Specification,
    string UnitCode,
    int DecimalPlaces,
    TrackingMode TrackingMode,
    ScanMatch MatchedBy,
    decimal PackQuantity,
    bool IsActive);

/// <summary>
/// 掃碼解析結果。<see cref="Raw"/> 是原始輸入；<see cref="Value"/> 是去掉控制字元後的文字
/// （<see cref="ScanResultKind.Text"/> 時就是要填入批號、序號欄的值）。
/// </summary>
public sealed record ScanResolution(
    ScanResultKind Kind,
    string Raw,
    string Value,
    string? Message,
    LocationRow? Location,
    ScanItem? Item);

/// <summary>
/// 掃碼解析（M5-01，ADR-0016）：相機、USB 條碼槍、PDA 共用。只做精確比對，不走模糊搜尋或共用關鍵字。
/// 順序：<c>WH:LOC:</c> 儲位 → 料號（含 <c>WH:ITEM:</c>）→ 原廠條碼 → 專屬別名 → 指定倉庫的儲位碼文字 → 其他文字。
/// </summary>
public interface IScanResolver
{
    /// <param name="raw">掃到的原始字串。</param>
    /// <param name="warehouseId">目前作業的倉庫（單據、期初單）。有指定時，別的倉庫的儲位 QR 會被拒絕，且可用沒有前綴的儲位碼。</param>
    Task<ScanResolution> ResolveAsync(string? raw, Guid? warehouseId, CancellationToken cancellationToken);
}
