namespace Warehouse.Domain.Inventory;

/// <summary>批號（inventory_lots）：同一物料內批號唯一；有效期限選填。過帳時沒有就建立。</summary>
public sealed class InventoryLot
{
    private InventoryLot()
    {
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public string LotNo { get; private set; } = string.Empty;

    public DateOnly? ExpiryDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>序號（item_serials）：同一物料內序號唯一。是否在庫看結存（stock_balances 數量 1）。</summary>
public sealed class ItemSerial
{
    private ItemSerial()
    {
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public string SerialNo { get; private set; } = string.Empty;

    public Guid? LotId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// 即時結存（stock_balances）。維度：物料＋儲位＋批號＋序號＋狀態（NULLS NOT DISTINCT 唯一）。
/// **只能由過帳服務以 SQL 條件式更新寫入**；程式其他地方只能讀（AGENTS.md 第六節）。
/// </summary>
public sealed class StockBalance
{
    private StockBalance()
    {
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid? LotId { get; private set; }

    public Guid? SerialId { get; private set; }

    public StockStatus StockStatus { get; private set; }

    public decimal Quantity { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>
/// 庫存異動帳（stock_movements）：每次結存變化一筆，只能新增（trigger 禁止修改、刪除、清空）。
/// 更正錯帳要用新的調整單，不刪改舊紀錄。
/// </summary>
public sealed class StockMovement
{
    private StockMovement()
    {
    }

    public Guid Id { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid LineId { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid? LotId { get; private set; }

    public Guid? SerialId { get; private set; }

    public StockStatus StockStatus { get; private set; }

    public decimal QuantityDelta { get; private set; }

    public decimal BalanceAfter { get; private set; }

    /// <summary>這筆異動後結存為負（負庫存例外，ADR-0004）；待補帳清單與稽核的來源。</summary>
    public bool IsNegative { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid ActorId { get; private set; }

    public string ActorName { get; private set; } = string.Empty;
}

/// <summary>預留狀態：Open＝仍占用可用量；Consumed＝已全部沖銷（依預留出庫）；Released＝取消或退回時釋放。</summary>
public enum ReservationStatus
{
    Open,
    Consumed,
    Released,
}

/// <summary>
/// 倉庫層級預留（stock_reservations，ADR-0004 決策二）：料號＋倉庫＋數量，不指定儲位、批號、序號。
/// 可用量 = Σ 可用結存 − Σ Open 預留的剩餘量。**只能由過帳服務以 SQL 寫入**（建立、沖銷、釋放都要取同一把鎖）。
/// </summary>
public sealed class StockReservation
{
    public const int ReferenceTypeMaxLength = 30;
    public const int NoteMaxLength = 200;

    private StockReservation()
    {
    }

    /// <summary>建立預留前的輸入檢查（數量 &gt; 0、來源必填、長度上限）；回傳去空白後的來源與說明。</summary>
    public static (string ReferenceType, string? Note) ValidateRequest(decimal quantity, string? referenceType, string? note)
    {
        if (quantity <= 0)
        {
            throw new DomainRuleException("預留數量必須大於 0。");
        }
        return (Rules.Required(referenceType, ReferenceTypeMaxLength, "預留來源"), Rules.Optional(note, NoteMaxLength, "預留說明"));
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid WarehouseId { get; private set; }

    /// <summary>建立時的預留量。</summary>
    public decimal Quantity { get; private set; }

    /// <summary>尚未沖銷的數量；0 時狀態為 Consumed。</summary>
    public decimal RemainingQuantity { get; private set; }

    /// <summary>來源類型，例如 <c>Manual</c>、<c>StockDocument</c>（待核准出倉單，ADR-0004 決策三）。</summary>
    public string ReferenceType { get; private set; } = string.Empty;

    public Guid? ReferenceId { get; private set; }

    public string? Note { get; private set; }

    public ReservationStatus Status { get; private set; }

    public Guid CreatedById { get; private set; }

    public string CreatedByName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
}
