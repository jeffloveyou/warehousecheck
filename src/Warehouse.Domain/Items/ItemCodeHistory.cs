namespace Warehouse.Domain.Items;

/// <summary>
/// 歷史料號（item_code_history，只能新增；D-32，ADR-0025）：物料變更料號時記一筆舊料號。
/// - 保留（<see cref="Released"/> = false）：舊料號仍可搜尋、掃碼找到這個物料，也不會給其他物料使用（料號不重新分配）。
/// - 釋出（<see cref="Released"/> = true）：只留紀錄，其他物料之後可以使用這個號碼。
/// 同一個號碼可能有多筆（釋出後被其他物料使用、再被變更），以最新一筆（<see cref="Seq"/> 最大）判斷是否保留。
/// </summary>
public sealed class ItemCodeHistory
{
    public const int ReasonMaxLength = 200;

    private ItemCodeHistory()
    {
    }

    public static ItemCodeHistory Record(
        Guid itemId, string code, string newCode, bool released, string reason, Guid actorId, string actorName, DateTimeOffset now)
    {
        if (actorId == Guid.Empty)
        {
            throw new DomainRuleException("歷史料號必須有操作者。");
        }
        if (!Item.IsValidItemCode(code) || !Item.IsValidItemCode(newCode))
        {
            throw new DomainRuleException("料號格式不正確。");
        }
        return new ItemCodeHistory
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Code = code,
            NewCode = newCode,
            Released = released,
            Reason = Rules.Required(reason, ReasonMaxLength, "變更原因"),
            ActorId = actorId,
            ActorName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "操作者名稱"),
            OccurredAt = now.ToUniversalTime(),
        };
    }

    public Guid Id { get; private set; }

    /// <summary>資料庫產生的遞增序號：同一號碼「最新一筆」以它判斷（時間可能相同，Guid 沒有順序）。</summary>
    public long Seq { get; private set; }

    public Guid ItemId { get; private set; }

    /// <summary>舊料號。</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>變更後的料號（當時）。</summary>
    public string NewCode { get; private set; } = string.Empty;

    public bool Released { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid ActorId { get; private set; }

    public string ActorName { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }
}
