using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Inventory;

public sealed record DocumentRow(
    Guid Id, string DocNo, StockDocumentType DocType, StockDocumentStatus Status, string WarehouseCode,
    string? PurposeName, string? PartnerName, string? ExternalNo, int LineCount,
    string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? PostedAt);

public sealed record DocumentLineRow(
    Guid Id, int LineNo, Guid LocationId, string LocationCode,
    Guid? TargetLocationId, string? TargetWarehouseCode, string? TargetLocationCode,
    Guid ItemId, string? ItemCode, string ItemName, string UnitCode, TrackingMode TrackingMode,
    string? LotNo, string? SerialNo, DateOnly? ExpiryDate, decimal Quantity, Guid? ReservationId, bool IsDecrease = false)
{
    /// <summary>帶正負號的數量：盤點調整單的盤虧為負，其他單據等於 <see cref="Quantity"/>。</summary>
    public decimal SignedQuantity => IsDecrease ? -Quantity : Quantity;
}

public sealed record DocumentDetails(
    Guid Id, string DocNo, StockDocumentType DocType, StockDocumentStatus Status, Guid WarehouseId, string WarehouseCode,
    Guid? PurposeId, string? PurposeName, Guid? PartnerId, string? PartnerName, string? ExternalNo, string? Note,
    Guid? ReversalOfDocumentId, string? ReversalOfDocNo,
    Guid CreatedById, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
    string? ApprovedByName, DateTimeOffset? PostedAt, string? ReturnComment, uint Version,
    IReadOnlyList<DocumentLineRow> Lines, bool FromScanCart = false);

/// <summary>
/// 新增明細：儲位碼、料號以文字輸入（掃碼或手輸，大小寫、全形會正規化）。
/// 移庫：<see cref="TargetLocationCode"/> 必填；<see cref="TargetWarehouseCode"/> 空白＝與來源同倉，但目的儲位是 <c>WH:LOC:</c> QR 時用 QR 上的倉庫（M5-01）。
/// <see cref="ReservationId"/>：出倉單依手動預留出庫時指定。
/// <see cref="MergeIfSame"/>：掃碼「逐件累加」，相同的草稿行累加數量（M5）。
/// </summary>
public sealed record DocumentLineInput(
    string LocationCode, string ItemCode, decimal Quantity,
    string? LotNo = null, string? SerialNo = null, DateOnly? ExpiryDate = null,
    string? TargetWarehouseCode = null, string? TargetLocationCode = null, Guid? ReservationId = null,
    bool MergeIfSame = false);

/// <summary>
/// 進倉、出倉、移庫單（M4-09～11，ADR-0015）。
/// - 單據審核關閉（預設）：草稿 →「確認」直接過帳（<see cref="ConfirmAsync"/>）。
/// - 審核開啟：草稿 → 送審（出倉單同時建立預留，D-26）→ 倉庫主管核准＝過帳；可退回、取消（釋放預留）。
/// - 草稿的修改限建立者或倉庫主管；草稿與待核准都不影響結存。
/// - 送審、確認、核准都要冪等鍵：同一鍵重送回傳同一結果，不會重複過帳或重複預留。
/// - 已過帳不可修改，更正用 <see cref="CreateReversalAsync"/> 產生沖銷單。
/// </summary>
public interface IStockDocumentService
{
    /// <summary>進出倉、移庫單清單（不含期初），新到舊、伺服器分頁。</summary>
    Task<PagedResult<DocumentRow>> ListAsync(
        StockDocumentType? type, StockDocumentStatus? status, Guid? warehouseId, int page, CancellationToken cancellationToken);

    Task<DocumentDetails?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// 建立草稿。<paramref name="requestId"/>：畫面開表單時產生、直接當單據 ID；連點或重送時回傳同一張單，不會多一張（M4-03 審查 #13）。
    /// </summary>
    Task<Guid> CreateAsync(
        StockDocumentType type, Guid warehouseId, StockDocumentHeader header, Actor actor, CancellationToken cancellationToken,
        Guid? requestId = null);

    Task UpdateHeaderAsync(Guid id, uint expectedVersion, StockDocumentHeader header, Actor actor, CancellationToken cancellationToken);

    Task<Guid> AddLineAsync(Guid id, DocumentLineInput input, Actor actor, CancellationToken cancellationToken);

    Task RemoveLineAsync(Guid id, Guid lineId, Actor actor, CancellationToken cancellationToken);

    /// <summary>一次刪除多行草稿明細（M8-03，明細表勾選）：同一交易，任一行不存在就整批不刪。</summary>
    Task RemoveLinesAsync(Guid id, IReadOnlyCollection<Guid> lineIds, Actor actor, CancellationToken cancellationToken);

    /// <summary>送審（只在審核開啟時）。出倉單依料號合計建立預留，可用量不足就無法送審。</summary>
    Task SubmitAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken);

    /// <summary>確認並過帳（只在審核關閉時）。</summary>
    Task ConfirmAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken);

    /// <summary>核准並過帳（倉庫主管；自核依設定）。</summary>
    Task ApproveAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken);

    /// <summary>退回成草稿（倉庫主管，原因必填），釋放本單預留。</summary>
    Task ReturnAsync(Guid id, uint expectedVersion, string comment, Actor actor, CancellationToken cancellationToken);

    /// <summary>取消草稿或待核准單，釋放本單預留。</summary>
    Task CancelAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken);

    /// <summary>為已過帳單建立沖銷單（方向相反的草稿）；一張單只能有一張未取消的沖銷單。</summary>
    Task<Guid> CreateReversalAsync(Guid id, Actor actor, CancellationToken cancellationToken);

    /// <summary>
    /// 快速進出：把自己的購物車整批轉成一張草稿單（ADR-0026）。同一個交易：建單、逐行套用與 <see cref="AddLineAsync"/> 相同的規則、刪除購物車；
    /// 任一行不合規則就整批不轉（購物車保留）。<see cref="ScanCartConvertInput.RequestId"/> 是新單據 ID，重送回傳同一張。
    /// </summary>
    Task<Guid> CreateFromScanCartAsync(Guid cartId, ScanCartConvertInput input, Actor actor, CancellationToken cancellationToken);

    /// <summary>刪除購物車轉入的草稿（不留單據；稽核紀錄保留）。只限草稿、建立者或倉庫主管、沒有附件與預留紀錄。</summary>
    Task DeleteScanCartDraftAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken);
}
