using Warehouse.Domain.Text;

namespace Warehouse.Domain.Items;

/// <summary>
/// 物料搜尋索引的共同部分（docs/03、ADR-0013）：專屬別名 <see cref="ItemAlias"/> 與共用關鍵字 <see cref="ItemKeyword"/>。
/// - 原文 <see cref="Text"/> 用於顯示；<see cref="NormalizedText"/> 用於比對（<see cref="TextNormalizer.Normalize"/>）。
/// - 只停用、不刪除：停用者與時間保留，可稽核（資料庫另有 trigger 禁止刪除與改寫）。
/// 這個基底類別不是 EF 實體，兩種索引各自一張表。
/// </summary>
public abstract class ItemIndexEntry
{
    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public string NormalizedText { get; private set; } = string.Empty;

    public Guid CreatedById { get; private set; }

    public string CreatedByName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive { get; private set; } = true;

    public Guid? DeactivatedById { get; private set; }

    public string? DeactivatedByName { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    /// <summary>停用後不再被搜尋命中；要恢復請重新新增（產生新的一筆，舊紀錄保留）。</summary>
    public void Deactivate(Guid actorId, string actorName, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainRuleException("此索引已經停用。");
        }
        IsActive = false;
        DeactivatedById = actorId;
        DeactivatedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "操作者名稱");
        DeactivatedAt = now.ToUniversalTime();
    }

    protected void Initialize(
        Guid itemId, string text, int maxLength, string label, Guid actorId, string actorName, DateTimeOffset now)
    {
        if (itemId == Guid.Empty || actorId == Guid.Empty)
        {
            throw new DomainRuleException("索引必須指定物料與建立者。");
        }
        var trimmed = Rules.Required(text, maxLength, label);
        var normalized = TextNormalizer.Normalize(trimmed);
        if (normalized.Length == 0)
        {
            throw new DomainRuleException($"{label}只有看不見的字元，請輸入實際文字。");
        }
        if (normalized.Length > maxLength * 4)
        {
            throw new DomainRuleException($"{label}含有展開後過長的特殊字元，請改用一般文字。");
        }
        if (TextNormalizer.LooksLikeItemIdentifier(normalized))
        {
            throw new DomainRuleException($"{label}不可使用料號或申請單號的格式（例如 EL-000125），請直接用料號搜尋。");
        }

        Id = Guid.NewGuid();
        ItemId = itemId;
        Text = trimmed;
        NormalizedText = normalized;
        CreatedById = actorId;
        CreatedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "操作者名稱");
        CreatedAt = now.ToUniversalTime();
    }
}

/// <summary>專屬別名：有效的別名全系統唯一，精確命中只會對應一個物料（例「J4-40B 維修備品」）。</summary>
public sealed class ItemAlias : ItemIndexEntry
{
    public const int TextMaxLength = 100;

    private ItemAlias()
    {
    }

    public static ItemAlias Create(Guid itemId, string text, Guid actorId, string actorName, DateTimeOffset now)
    {
        var alias = new ItemAlias();
        alias.Initialize(itemId, text, TextMaxLength, "專屬別名", actorId, actorName, now);
        return alias;
    }
}

/// <summary>共用關鍵字：俗稱、用途、特徵，可同時掛在多個物料（例「伺服AMP」）；同一物料不重複。</summary>
public sealed class ItemKeyword : ItemIndexEntry
{
    public const int TextMaxLength = 50;

    private ItemKeyword()
    {
    }

    public static ItemKeyword Create(Guid itemId, string text, Guid actorId, string actorName, DateTimeOffset now)
    {
        var keyword = new ItemKeyword();
        keyword.Initialize(itemId, text, TextMaxLength, "共用關鍵字", actorId, actorName, now);
        return keyword;
    }
}
