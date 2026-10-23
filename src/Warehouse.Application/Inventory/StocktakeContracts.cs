using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;

namespace Warehouse.Application.Inventory;

/// <summary>盤點單列表的一列。</summary>
public sealed record StocktakeRow(
    Guid Id, string StocktakeNo, Guid WarehouseId, string WarehouseCode, StocktakeStatus Status, bool AllLocations,
    int LineCount, int CountedCount, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);

/// <summary>
/// 盤點明細（畫面用）。盲盤時（設定開啟且檢視者不是倉庫主管）<see cref="SnapshotQuantity"/>、<see cref="BookQuantityAtCount"/>、
/// <see cref="Difference"/> 一律為 null，伺服器端就不回傳帳面量。
/// <see cref="MovementSinceSnapshot"/>：快照後到計數前的異動淨量（計數當下帳面 − 快照），讓審核者看出「不凍結」期間的進出。
/// </summary>
public sealed record StocktakeLineRow(
    Guid Id, int LineNo, Guid LocationId, string LocationCode, Guid ItemId, string ItemCode, string ItemName, string UnitCode,
    string? LotNo, string? SerialNo, StockStatus StockStatus, bool IsAdded, decimal? SnapshotQuantity,
    decimal? CountedQuantity, decimal? BookQuantityAtCount, decimal? Difference, decimal? MovementSinceSnapshot,
    string? CountedByName, DateTimeOffset? CountedAt, bool RecountRequested);

public sealed record StocktakeDetails(
    Guid Id, string StocktakeNo, Guid WarehouseId, string WarehouseCode, StocktakeStatus Status, bool AllLocations,
    string? Note, DateTimeOffset SnapshotAt, Guid CreatedById, string CreatedByName, DateTimeOffset CreatedAt,
    string? SubmittedByName, DateTimeOffset? SubmittedAt, string? ApprovedByName, DateTimeOffset? ApprovedAt,
    Guid? AdjustmentDocumentId, string? AdjustmentDocNo, string? RecountComment, IReadOnlyList<string> LocationCodes,
    bool QuantitiesHidden, uint Version, PagedResult<StocktakeLineRow> Lines, int LineCount, int UncountedCount,
    int? DifferenceCount);

/// <summary>明細篩選（伺服器分頁，每頁 <see cref="PageSize"/> 行，預設 <see cref="DefaultPageSize"/>；列印一次取全部，上限為盤點單行數上限）。</summary>
public sealed record StocktakeLineFilter(
    string? LocationCode = null, bool OnlyUncounted = false, bool OnlyDifferences = false, int Page = 1, int PageSize = StocktakeLineFilter.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
}

/// <summary>
/// 輸入實盤量：依「儲位＋料號（可掃物料 QR 或原廠條碼）＋批號／序號」找到盤點行並記錄；找不到就新增一行「現場找到」。
/// 掃碼與手輸走同一個入口（條碼槍、PDA 掃完 Enter 就送出）。
/// </summary>
public sealed record StocktakeCountInput(string LocationCode, string ItemCode, decimal CountedQuantity, string? LotNo = null, string? SerialNo = null);

/// <summary>
/// 盤點（M6-02，ADR-0018）。不凍結儲位：差異＝實盤 − 計數當下帳面；核准時產生並過帳調整單（經過帳服務，盤虧不受預留限制）。
/// 權限：開始、取消、要求重盤、核准限倉庫主管；輸入實盤量、送審限作業員或倉庫主管；都要在倉庫作業範圍內（warehouse.forbidden）。
/// 錯誤碼：<c>stocktake.not_found</c>、<c>stocktake.concurrency_conflict</c>、<c>stocktake.location_busy</c>（儲位已在其他盤點中）、
/// <c>stocktake.too_large</c>、<c>stocktake.permission_denied</c>、<c>stocktake.busy</c>，以及過帳的 <c>stock.*</c>。
/// </summary>
public interface IStocktakeService
{
    Task<PagedResult<StocktakeRow>> ListAsync(Guid? warehouseId, StocktakeStatus? status, int page, CancellationToken cancellationToken);

    /// <summary>檢視盤點單；<paramref name="viewer"/> 決定盲盤時是否隱藏帳面量。</summary>
    Task<StocktakeDetails?> GetAsync(Guid id, StocktakeLineFilter filter, Actor viewer, CancellationToken cancellationToken);

    /// <summary>
    /// 開始盤點：<paramref name="locationCodes"/> 為空＝倉庫內全部未停用的儲位。同一交易內鎖儲位（等進行中的過帳完成）並建立快照行。
    /// <paramref name="requestId"/>：重送同一請求回傳同一張盤點單。
    /// </summary>
    Task<Guid> StartAsync(
        Guid warehouseId, IReadOnlyList<string> locationCodes, string? note, Actor actor, CancellationToken cancellationToken, Guid? requestId = null);

    /// <summary>輸入（或重新輸入）實盤量，回傳盤點行 ID。</summary>
    Task<Guid> CountAsync(Guid id, StocktakeCountInput input, Actor actor, CancellationToken cancellationToken);

    /// <summary>直接修改某一行的實盤量（畫面表格上的輸入框）。</summary>
    Task CountLineAsync(Guid id, Guid lineId, decimal countedQuantity, Actor actor, CancellationToken cancellationToken);

    Task SubmitAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken);

    Task RequestRecountAsync(Guid id, uint expectedVersion, IReadOnlyList<Guid> lineIds, string comment, Actor actor, CancellationToken cancellationToken);

    Task CancelAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken);

    /// <summary>核准：有差異時產生並過帳調整單（回傳調整單 ID；沒有差異回傳 null）。同一冪等鍵重送不會重複調整。</summary>
    Task<Guid?> ApproveAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken);
}
