using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;

namespace Warehouse.Application.Inventory;

public sealed record ReservationRow(
    Guid Id, Guid ItemId, string ItemCode, string ItemName, Guid WarehouseId, string WarehouseCode,
    decimal Quantity, decimal RemainingQuantity, string ReferenceType, Guid? ReferenceId, string? ReferenceDocNo, string? Note,
    ReservationStatus Status, Guid CreatedById, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? ClosedAt);

/// <summary>手動預留：料號（掃碼或手輸）、倉庫、數量、說明、到期時間（可空白＝不到期）。</summary>
public sealed record ReservationInput(string ItemCode, Guid WarehouseId, decimal Quantity, string? Note, DateTimeOffset? ExpiresAt);

/// <summary>
/// 手動預留（M4-07，ADR-0004 決策二；使用者 2026-10-22）：
/// - 作業員與倉庫主管可建立；取消限建立者本人或倉庫主管。
/// - 出倉單送審自動建立的預留（<c>StockDocument</c>）不能在這裡取消，要取消或退回該單據。
/// - 建立需要冪等鍵：同一鍵重送回傳同一筆預留，不會建兩筆。
/// - 可設到期時間：過期後不計入可用量，背景工作每 10 分鐘改為已釋放並寫稽核。
/// </summary>
public interface IStockReservationService
{
    /// <summary>預留清單（新到舊、伺服器分頁）。<paramref name="itemCode"/> 精確比對（正規化後）。</summary>
    /// <param name="selectableOnly">true＝只列出倉單可以指定的預留：手動、仍 Open、未過期（出倉明細的下拉用）。</param>
    Task<PagedResult<ReservationRow>> ListAsync(
        string? itemCode, Guid? warehouseId, ReservationStatus? status, int page, CancellationToken cancellationToken,
        bool selectableOnly = false);

    Task<Guid> CreateAsync(ReservationInput input, string idempotencyKey, Actor actor, CancellationToken cancellationToken);

    Task CancelAsync(Guid id, Actor actor, CancellationToken cancellationToken);
}

/// <summary>
/// 物料負庫存設定（M4-08，ADR-0004 決策一）：只有物料管理員可以改；只限不追蹤批號／序號的物料。
/// 關閉時，該料號不可有任何負結存；開啟或修改下限時，新下限不可讓目前的全倉可用量低於 −下限。
/// 與造成負數的過帳以「料號鎖」排隊（ADR-0004 併發控制規則 7）。
/// </summary>
public interface IItemNegativePolicyService
{
    Task SetAsync(Guid itemId, uint expectedVersion, bool allow, decimal limit, Actor actor, CancellationToken cancellationToken);
}

/// <summary>待補帳：目前結存為負的料號＋儲位，含最後一筆造成負數的單據。</summary>
public sealed record NegativeBalanceRow(
    Guid ItemId, string ItemCode, string ItemName, string UnitCode, string WarehouseCode, string LocationCode, decimal Quantity,
    decimal NegativeStockLimit, string? LastDocNo, Guid? LastDocumentId, DateTimeOffset? LastOccurredAt);
