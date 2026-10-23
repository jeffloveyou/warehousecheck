using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Inventory;

/// <summary>
/// 一筆結存變化：哪個物料、儲位、批號、序號、狀態，增加（正）或扣減（負）多少。
/// <see cref="ReservationId"/>：依預留出庫時指定（只用於扣減），同交易沖銷 min(扣減量, 預留剩餘量)，超出部分當一般出庫。
/// </summary>
public sealed record StockChange(
    Guid LineId, Guid ItemId, Guid LocationId, Guid? LotId, Guid? SerialId, StockStatus Status, decimal Delta,
    Guid? ReservationId = null);

/// <summary>
/// 過帳選項。<see cref="IgnoreReservations"/>：只給盤點調整單（M6-02，使用者 2026-10-23 決定「盤虧不受預留限制」）——
/// 實物就是少了，扣減時不檢查「可用量（扣除預留）」；但結存仍不可為負（trigger）、待驗不可為負，
/// 允許負庫存的物料仍維持全倉結存 ≥ −下限（不扣預留）。預留大於庫存的情況由庫存異常頁列出。
/// </summary>
public sealed record StockApplyOptions(bool IgnoreReservations = false);

/// <summary>建立倉庫層級預留（ADR-0004 決策二）。<see cref="ReferenceType"/> 例如 <c>Manual</c>、<c>StockDocument</c>。</summary>
public sealed record ReservationRequest(
    Guid ItemId, Guid WarehouseId, decimal Quantity, string ReferenceType, Guid? ReferenceId, string? Note, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// 過帳服務：**唯一**可以改變 stock_balances 與 stock_reservations 的地方（AGENTS.md 第六節、ADR-0014、ADR-0004）。
/// 必須在呼叫端已開啟的資料庫交易內呼叫；鎖、結存、異動帳、預留與可用量檢查都在同一交易完成，任何錯誤整筆回滾。
/// 呼叫端若要鎖儲位（例如檢查期初），必須在批號／序號 upsert 之前、依 id 排序 FOR UPDATE（鎖順序見 ADR-0004「併發控制」）。
/// </summary>
public interface IInventoryPostingService
{
    /// <summary>
    /// 套用結存變化。錯誤：<c>stock.insufficient</c>（結存或倉庫可用量不足）、<c>stock.negative_limit</c>（超過負庫存下限）、
    /// <c>stock.busy</c>（等鎖逾時或死結，可重試）；儲位停用、倉庫停用、序號重複為 <c>domain_rule</c>。
    /// </summary>
    Task ApplyAsync(
        Guid documentId, IReadOnlyList<StockChange> changes, Actor actor, DateTimeOffset now, CancellationToken cancellationToken,
        StockApplyOptions? options = null);

    /// <summary>建立預留；倉庫可用量（扣除既有預留）不足時拒絕（<c>stock.insufficient</c>，負庫存物料也不例外）。回傳預留 ID。</summary>
    Task<Guid> ReserveAsync(ReservationRequest request, Actor actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// 一次建立多筆預留（所有鎖一次依序取得）；任何一筆不足就丟例外，呼叫端回滾後全部不建立。
    /// <paramref name="forDocument"/>＝出倉單送審：停用物料可預留；允許負庫存的物料只預留目前可用的部分。回傳實際建立的預留 ID。
    /// </summary>
    Task<IReadOnlyList<Guid>> ReserveManyAsync(
        IReadOnlyList<ReservationRequest> requests, Actor actor, DateTimeOffset now, CancellationToken cancellationToken,
        bool forDocument = false);

    /// <summary>釋放（取消）仍為 Open 的預留；已結束的預留回傳 false（重送不出錯）。</summary>
    Task<bool> ReleaseReservationAsync(Guid reservationId, Actor actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>把已到期的 Open 預留改成 Released 並寫稽核（背景工作呼叫；兩個實例同時執行也只會處理一次）。回傳處理筆數。</summary>
    Task<int> ExpireDueReservationsAsync(Actor actor, DateTimeOffset now, CancellationToken cancellationToken);
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
/// <see cref="MergeIfSame"/>：掃碼「逐件累加」，相同的草稿行累加數量（M5）。
/// </summary>
public sealed record OpeningLineInput(
    string LocationCode, string? ItemCode, string? LotNo, string? SerialNo, DateOnly? ExpiryDate, decimal Quantity,
    bool MergeIfSame = false);

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
    decimal QuantityDelta, decimal BalanceAfter, string ActorName, Guid DocumentId = default, bool IsNegative = false);

/// <summary>
/// 異動歷史查詢條件（M4-05）。料號、儲位碼、單號精確比對（正規化後）；操作者部分相符；
/// 日期區間以當地日期輸入，呼叫端轉成 UTC 的 [From, To)。沒有料號、儲位、單號與起日時只查最近 30 天。
/// </summary>
public sealed record MovementQuery(
    string? ItemCode = null, Guid? WarehouseId = null, string? LocationCode = null, string? DocNo = null, string? ActorName = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1)
{
    /// <summary>沒有料號、儲位、單號與起日時，預設只查最近幾天。</summary>
    public const int DefaultDays = 30;
}

/// <summary>被拒絕的過帳嘗試（M4-05）：送審、確認、核准、預留失敗時另外記錄（交易已回滾，這筆是在另一個連線寫入）。</summary>
public sealed record PostingFailureRow(
    DateTimeOffset OccurredAt, string ActorName, string Command, Guid? DocumentId, string? DocNo, string ErrorCode, string Message, string? TraceId);

/// <summary>單據事件：取消、退回、建立沖銷單（來自稽核紀錄）。</summary>
public sealed record DocumentEventRow(DateTimeOffset OccurredAt, string ActorName, string Action, Guid DocumentId, string DocNo, string? Detail);

/// <summary>久未處理：草稿或待核准超過門檻的單據。</summary>
public sealed record StaleDocumentRow(Guid Id, string DocNo, StockDocumentType DocType, StockDocumentStatus Status, string CreatedByName, DateTimeOffset Since);

/// <summary>預留大於庫存的料號＋倉庫。</summary>
public sealed record OverReservedRow(string ItemCode, string ItemName, string WarehouseCode, decimal Reserved, decimal Balance);

/// <summary>需要注意的預留：建立超過門檻，或即將到期。</summary>
public sealed record StaleReservationRow(
    Guid Id, string ItemCode, string WarehouseCode, decimal RemainingQuantity, string ReferenceType, string? ReferenceDocNo,
    string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// 異常追蹤（M4-05，使用者 2026-10-22 決定四項）。門檻：草稿或待核准 &gt; <see cref="StaleDocumentDays"/> 天、
/// 預留 &gt; <see cref="StaleReservationDays"/> 天或 <see cref="ExpiringSoonDays"/> 天內到期。負庫存見 <see cref="IStockQueryService.GetNegativeBalancesAsync"/>。
/// </summary>
public interface IStockAnomalyService
{
    const int StaleDocumentDays = 7;
    const int StaleReservationDays = 30;
    const int ExpiringSoonDays = 3;

    Task<PagedResult<PostingFailureRow>> GetPostingFailuresAsync(int page, CancellationToken cancellationToken);

    Task<PagedResult<DocumentEventRow>> GetDocumentEventsAsync(int page, CancellationToken cancellationToken);

    Task<PagedResult<StaleDocumentRow>> GetStaleDocumentsAsync(int page, CancellationToken cancellationToken);

    Task<PagedResult<StaleReservationRow>> GetStaleReservationsAsync(int page, CancellationToken cancellationToken);

    /// <summary>
    /// 預留大於庫存（M6-02）：料號＋倉庫的未到期預留合計 &gt; 可用結存（Available 且儲位有效）。
    /// 盤虧調整不受預留限制（使用者 2026-10-23 決定），調整後可能出現，請倉庫主管釋放或調整預留。
    /// </summary>
    Task<PagedResult<OverReservedRow>> GetOverReservedAsync(int page, CancellationToken cancellationToken);
}

/// <summary>記錄被拒絕的過帳嘗試。實作必須在獨立連線寫入，而且自己吞掉錯誤（記錄失敗不能影響原本的錯誤回應）。</summary>
public interface IPostingFailureLog
{
    Task RecordAsync(string command, Guid? documentId, Actor actor, Exception error, CancellationToken cancellationToken);
}

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

    /// <summary>異動歷史（M4-05）：多條件篩選、新到舊、伺服器分頁。</summary>
    Task<PagedResult<MovementRow>> SearchMovementsAsync(MovementQuery query, CancellationToken cancellationToken);

    /// <summary>待補帳：目前結存為負的料號＋儲位（M4-08），倉庫碼、儲位碼、料號排序。</summary>
    Task<PagedResult<NegativeBalanceRow>> GetNegativeBalancesAsync(Guid? warehouseId, int page, CancellationToken cancellationToken);

    /// <summary>還沒做過期初的儲位（停用的不列）。</summary>
    Task<PagedResult<UncountedLocationRow>> GetUncountedLocationsAsync(Guid warehouseId, int page, CancellationToken cancellationToken);
}
