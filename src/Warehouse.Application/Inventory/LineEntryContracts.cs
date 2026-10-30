using Warehouse.Domain.Inventory;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Application.Inventory;

/// <summary>明細輸入的儲位清單排序方式（M8-02）。</summary>
public enum LocationListMode
{
    /// <summary>出倉、移庫來源：這個料號有庫存的儲位排在前面（附數量）。</summary>
    Source,

    /// <summary>進倉、期初：這個料號的預設、常用儲位排在前面。</summary>
    Inbound,

    /// <summary>移庫目的、沒有料號：只依關鍵字列出。</summary>
    Plain,

    /// <summary>快速進出補儲位（ADR-0026）：這個料號有庫存的儲位排前面，數量少的最前面（附數量）；之後是其他儲位。進、出、移都一樣。</summary>
    StockAscending,
}

/// <summary>儲位選項。<see cref="Quantity"/>：料號在這個儲位的結存合計（沒有料號或沒有庫存時為 null）；<see cref="Reason"/>：Stock／Default／Frequent／null。</summary>
public sealed record LocationOption(
    Guid Id, string WarehouseCode, string Code, string? Name, LocationStatus Status, decimal? Quantity, string? Reason);

/// <summary>批號／序號選項（出倉、移庫）：某料號在某儲位（或整個倉庫）可選的結存維度。</summary>
public sealed record LotOption(
    Guid LocationId, string LocationCode, string? LotNo, string? SerialNo, DateOnly? ExpiryDate, StockStatus Status, decimal Quantity);

/// <summary>
/// 明細輸入用的唯讀查詢（M8-02）：可輸入文字縮小範圍的選擇清單（儲位、批號／序號）。
/// 一律在伺服器篩選、限制筆數，畫面不下載完整清單（低負載原則）；倉庫範圍由呼叫端（頁面）檢查。
/// </summary>
public interface ILineEntryQueryService
{
    /// <summary>每次最多回傳的儲位數。</summary>
    const int LocationLimit = 20;

    /// <summary>每次最多回傳的批號／序號數。</summary>
    const int LotLimit = 50;

    /// <summary>
    /// 倉庫的儲位選項（不含停用的儲位與已刪除的倉庫）；關鍵字比對儲位碼、名稱、區、架。
    /// 有 <paramref name="itemId"/> 時依 <paramref name="mode"/> 把有庫存或常用的儲位排在前面，其餘依儲位碼排序。
    /// </summary>
    Task<IReadOnlyList<LocationOption>> LocationsAsync(
        Guid warehouseId, string? keyword, Guid? itemId, LocationListMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// 料號在倉庫（指定 <paramref name="locationId"/> 時只看該儲位）有結存的批號、序號，數量大於 0；
    /// 依有效期限（先到期先出，沒有期限的排後面）、批號、序號排序；關鍵字比對批號或序號。
    /// </summary>
    Task<IReadOnlyList<LotOption>> LotsAsync(
        Guid warehouseId, Guid itemId, Guid? locationId, string? keyword, CancellationToken cancellationToken);
}
