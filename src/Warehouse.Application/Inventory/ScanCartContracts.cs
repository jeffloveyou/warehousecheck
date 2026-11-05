using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using System.Text.Json.Serialization;

namespace Warehouse.Application.Inventory;

/// <summary>購物車清單的一列（只看得到自己的）。</summary>
public sealed record ScanCartSummary(
    Guid Id, string? Note, string? WarehouseCode, int LineCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ScanCartLineRow(
    Guid Id, int LineNo, Guid ItemId, string ItemCode, string ItemName, string? Specification, string UnitCode, int DecimalPlaces,
    TrackingMode TrackingMode, bool ItemActive, Guid? LocationId, string? LocationCode, decimal Quantity);

public sealed record ScanCartDetails(
    Guid Id, string? Note, Guid? WarehouseId, string? WarehouseCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ScanCartLineRow> Lines, uint Version = 0, string? DraftJson = null);

/// <summary>
/// 掃進購物車：<paramref name="ItemCode"/> 可以是料號、WH:ITEM: QR、原廠條碼、專屬別名（數量由畫面依包裝量算好，基本單位）。
/// <paramref name="LocationCode"/> 可以是儲位 QR（WH:LOC:倉:儲位）或儲位碼（購物車已鎖定倉庫時）；空白＝之後再補。
/// </summary>
public sealed record ScanCartAddInput(string ItemCode, string? LocationCode, decimal Quantity, Guid? OperationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ItemId = null);

public sealed record ScanCartAllocation(Guid LineId, string LocationCode, decimal Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReservationId = null);
public sealed record ScanCartCheckoutInput(Guid RequestId, uint ExpectedVersion, StockDocumentType Type,
    Guid WarehouseId, StockDocumentHeader Header, IReadOnlyList<ScanCartAllocation> Allocations,
    Guid? RecipientUserId, string? ProjectReference);
public sealed record ScanCartCheckoutResult(Guid DocumentId, string DocNo, StockDocumentStatus Status);
public sealed record RecipientOption(Guid Id, string UserName, string DisplayName);
public sealed record ScanCartAddResult(Guid LineId, uint BaseVersion, ScanCartDetails? Cart, int LineCount);
public sealed record ScanCartDraftInput(uint ExpectedVersion, string Json);
public sealed record ScanCartUndoInput(Guid OperationId, uint ExpectedVersion, ScanCartAddInput Original);
public sealed record ScanCartStockRow(Guid ItemId, string ItemCode, string LocationCode, decimal Quantity,
    decimal WarehouseAvailable, bool AllowNegativeStock, decimal NegativeStockLimit);

/// <summary>
/// 轉成草稿的一行：來源（進倉為目的）儲位必填；移庫另填目的倉庫／儲位。
/// 批號物料填 <paramref name="LotNo"/>；序號物料填 <paramref name="SerialNos"/>（個數＝數量，每個序號一行）。
/// </summary>
public sealed record ScanCartConvertLine(
    Guid LineId, string LocationCode, string? TargetWarehouseCode = null, string? TargetLocationCode = null,
    string? LotNo = null, IReadOnlyList<string>? SerialNos = null, DateOnly? ExpiryDate = null);

/// <summary>整批轉成一張草稿單（進倉、出倉或移庫）。<paramref name="RequestId"/> 是新單據的 ID，重送不會建第二張。</summary>
public sealed record ScanCartConvertInput(
    StockDocumentType Type, Guid WarehouseId, Guid? PurposeId, IReadOnlyList<ScanCartConvertLine> Lines, Guid RequestId);

/// <summary>
/// 快速進出購物車（ADR-0026）：只是記在帳號的清單，不動結存、不預留。只有擁有者能看、能改。
/// 轉成草稿單見 <see cref="IStockDocumentService.CreateFromScanCartAsync"/>。
/// </summary>
public interface IScanCartService
{
    Task<IReadOnlyList<ScanCartSummary>> ListAsync(Actor actor, CancellationToken cancellationToken);
    Task<PagedResult<ScanCartSummary>> ListPageAsync(Actor actor, int page, CancellationToken cancellationToken);
    Task<bool> OwnsAsync(Guid id, Actor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecipientOption>> RecipientsAsync(string? query, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScanCartStockRow>> StockAsync(Guid id, Guid warehouseId, Actor actor, CancellationToken cancellationToken);

    /// <summary>找不到或不是自己的：null。</summary>
    Task<ScanCartDetails?> GetAsync(Guid id, Actor actor, CancellationToken cancellationToken);

    Task<Guid> CreateAsync(string? note, Actor actor, CancellationToken cancellationToken);

    Task SetNoteAsync(Guid id, string? note, Actor actor, CancellationToken cancellationToken, uint? expectedVersion = null);
    Task SaveDraftAsync(Guid id, ScanCartDraftInput input, Actor actor, CancellationToken cancellationToken);
    Task UndoScanAsync(Guid id, ScanCartUndoInput input, Actor actor, CancellationToken cancellationToken);
    Task<ScanCartCheckoutResult?> CheckoutResultAsync(Guid id, Actor actor, CancellationToken cancellationToken);

    /// <summary>加入一筆；同物料＋同儲位合併數量。回傳那一行的 ID。</summary>
    Task<Guid> AddAsync(Guid id, ScanCartAddInput input, Actor actor, CancellationToken cancellationToken);
    Task<ScanCartAddResult> AddWithResultAsync(Guid id, ScanCartAddInput input, Actor actor, CancellationToken cancellationToken);

    /// <summary>改數量與儲位（儲位空白＝之後再補）。</summary>
    Task UpdateLineAsync(Guid id, Guid lineId, decimal quantity, string? locationCode, Actor actor, CancellationToken cancellationToken, uint? expectedVersion = null);

    Task RemoveLineAsync(Guid id, Guid lineId, Actor actor, CancellationToken cancellationToken, uint? expectedVersion = null);

    /// <summary>放棄購物車：實體刪除，不留紀錄（使用者決定）。</summary>
    Task DeleteAsync(Guid id, Actor actor, CancellationToken cancellationToken);
}
