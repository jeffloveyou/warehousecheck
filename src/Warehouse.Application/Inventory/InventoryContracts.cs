using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Inventory;

/// <summary>一筆結存變化：哪個物料、儲位、批號、序號、狀態，增加多少（M3 只有增加；M4 加扣減與負庫存規則）。</summary>
public sealed record StockChange(
    Guid LineId, Guid ItemId, Guid LocationId, Guid? LotId, Guid? SerialId, StockStatus Status, decimal Delta);

/// <summary>
/// 過帳服務：**唯一**可以改變 stock_balances 的地方（AGENTS.md 第六節、ADR-0014）。
/// 必須在呼叫端已開啟的資料庫交易內呼叫；結存更新與異動帳在同一交易寫入。
/// </summary>
public interface IInventoryPostingService
{
    Task ApplyAsync(Guid documentId, IReadOnlyList<StockChange> changes, Actor actor, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record OpeningRow(
    Guid Id, string DocNo, string WarehouseCode, StockDocumentStatus Status, int LineCount,
    string CreatedByName, DateTimeOffset CreatedAt, string? ApprovedByName, DateTimeOffset? PostedAt);

public sealed record OpeningLineRow(
    Guid Id, int LineNo, Guid LocationId, string LocationCode, Guid? ItemId, string? ItemCode, string? ItemName,
    string? UnitCode, TrackingMode? TrackingMode, string? LotNo, string? SerialNo, DateOnly? ExpiryDate, decimal Quantity);

public sealed record OpeningDetails(
    Guid Id, string DocNo, Guid WarehouseId, string WarehouseCode, StockDocumentStatus Status, string? Note,
    Guid CreatedById, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
    string? ApprovedByName, DateTimeOffset? PostedAt, string? ReturnComment, uint Version,
    IReadOnlyList<OpeningLineRow> Lines);

/// <summary>
/// 新增明細：以儲位碼、料號輸入（掃碼或手輸都可以；大小寫、全形會正規化）。
/// <see cref="ItemCode"/> 空白 = 整個儲位確認為空。
/// </summary>
public sealed record OpeningLineInput(
    string LocationCode, string? ItemCode, string? LotNo, string? SerialNo, DateOnly? ExpiryDate, decimal Quantity);

/// <summary>
/// 期初建帳（M3-03，FR-04）。流程：草稿 → 送出 → 核准（＝過帳）；可退回、取消。
/// - 草稿的修改限建立者或倉庫主管；核准、退回限倉庫主管，並依 D-07 設定檢查是否可自核。
/// - 每個儲位只能做一次期初；過帳後儲位標記為已清點（opening_document_id）。
/// - 核准需要冪等鍵：同一鍵重送回傳同一結果，不會重複過帳。
/// </summary>
public interface IOpeningService
{
    Task<PagedResult<OpeningRow>> ListAsync(Guid? warehouseId, StockDocumentStatus? status, int page, CancellationToken cancellationToken);

    Task<OpeningDetails?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Guid> CreateAsync(Guid warehouseId, string? note, Actor actor, CancellationToken cancellationToken);

    Task AddLineAsync(Guid documentId, OpeningLineInput input, Actor actor, CancellationToken cancellationToken);

    Task RemoveLineAsync(Guid documentId, Guid lineId, Actor actor, CancellationToken cancellationToken);

    Task SubmitAsync(Guid documentId, uint expectedVersion, Actor actor, CancellationToken cancellationToken);

    Task ReturnAsync(Guid documentId, uint expectedVersion, string comment, Actor actor, CancellationToken cancellationToken);

    Task CancelAsync(Guid documentId, uint expectedVersion, Actor actor, CancellationToken cancellationToken);

    /// <summary>核准並過帳。<paramref name="idempotencyKey"/> 每次「按下核准」產生一個（頁面隱藏欄位或 API 標頭）。</summary>
    Task ApproveAsync(Guid documentId, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken);
}

public sealed record BalanceRow(
    Guid ItemId, string? ItemCode, string ItemName, string UnitCode, Guid LocationId, string WarehouseCode, string LocationCode,
    string? LotNo, DateOnly? ExpiryDate, string? SerialNo, StockStatus Status, bool LocationAvailable, decimal Quantity);

public sealed record MovementRow(
    DateTimeOffset OccurredAt, string DocNo, StockDocumentType DocType, string? ItemCode, string ItemName,
    string WarehouseCode, string LocationCode, string? LotNo, string? SerialNo, StockStatus Status,
    decimal QuantityDelta, decimal BalanceAfter, string ActorName);

public sealed record UncountedLocationRow(Guid LocationId, string WarehouseCode, string LocationCode, string? Name);

/// <summary>
/// 庫存查詢（唯讀、伺服器分頁）。可用量只計入「結存狀態 Available 且儲位目前也是有效」的部分
/// （<see cref="BalanceRow.LocationAvailable"/>）：儲位改成待驗或停用後，原本的結存立即不算可用（M3-01 審查 #3）。
/// </summary>
public interface IStockQueryService
{
    Task<PagedResult<BalanceRow>> GetBalancesAsync(
        Guid? warehouseId, Guid? locationId, Guid? itemId, int page, CancellationToken cancellationToken);

    Task<PagedResult<MovementRow>> GetMovementsAsync(
        Guid? itemId, Guid? locationId, Guid? documentId, int page, CancellationToken cancellationToken);

    /// <summary>還沒做過期初的儲位（停用的不列）。</summary>
    Task<PagedResult<UncountedLocationRow>> GetUncountedLocationsAsync(Guid warehouseId, int page, CancellationToken cancellationToken);
}
