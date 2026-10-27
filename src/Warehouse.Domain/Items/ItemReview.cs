namespace Warehouse.Domain.Items;

/// <summary>物料審核紀錄的動作種類（以文字儲存；新增值的相容順序見 ADR-0011「後果」）。</summary>
public enum ItemReviewAction
{
    Submit,
    Update,
    Resubmit,
    Approve,
    Return,
    Reject,
    Withdraw,
    Deactivate,
    Reactivate,

    /// <summary>核准後變更正式料號（D-32，ADR-0025）；<see cref="ItemReview.ItemCode"/> 為新料號。</summary>
    CodeChange,
}

/// <summary>
/// 物料審核紀錄（item_reviews，只能新增）：每次提出、修改、核准、退回、拒絕、撤回、停用、啟用各一筆。
/// 操作者以 ID＋名稱快照保存，不關聯使用者表，帳號日後停用或改名都不影響紀錄。
/// 資料庫以 trigger trg_item_reviews_append_only 禁止 UPDATE／DELETE（Migration M1_02）。
/// </summary>
public sealed class ItemReview
{
    public const int CommentMaxLength = 500;
    public const int ActorNameMaxLength = 100;

    private ItemReview()
    {
    }

    public static ItemReview Record(
        Guid itemId,
        ItemReviewAction action,
        ItemStatus fromStatus,
        ItemStatus toStatus,
        Guid actorId,
        string actorName,
        DateTimeOffset now,
        string? comment = null,
        string? itemCode = null,
        Guid? duplicateOfItemId = null)
    {
        if (actorId == Guid.Empty)
        {
            throw new DomainRuleException("審核紀錄必須有操作者。");
        }
        var trimmedComment = Rules.Optional(comment, CommentMaxLength, "意見");
        if (action is ItemReviewAction.Return or ItemReviewAction.Reject && trimmedComment is null)
        {
            throw new DomainRuleException("退回或拒絕時必須填寫意見，讓提出人知道原因。");
        }
        if (duplicateOfItemId is not null && action != ItemReviewAction.Reject)
        {
            throw new DomainRuleException("只有拒絕時可以註明重複於哪個物料。");
        }
        if (duplicateOfItemId == itemId)
        {
            throw new DomainRuleException("物料不能判定為與自己重複。");
        }

        return new ItemReview
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ItemCode = itemCode,
            DuplicateOfItemId = duplicateOfItemId,
            Comment = trimmedComment,
            ActorId = actorId,
            ActorName = Rules.Required(actorName, ActorNameMaxLength, "操作者名稱"),
            OccurredAt = now.ToUniversalTime(),
        };
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public ItemReviewAction Action { get; private set; }

    public ItemStatus FromStatus { get; private set; }

    public ItemStatus ToStatus { get; private set; }

    /// <summary>核准時發出的正式料號、變更料號時的新料號；其他動作為 null。</summary>
    public string? ItemCode { get; private set; }

    /// <summary>判定重複時，與哪個既有物料重複。</summary>
    public Guid? DuplicateOfItemId { get; private set; }

    public string? Comment { get; private set; }

    public Guid ActorId { get; private set; }

    public string ActorName { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }
}
