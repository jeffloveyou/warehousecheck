using System.Text.RegularExpressions;

namespace Warehouse.Domain.Items;

/// <summary>
/// 物料分類（階層）。頂層分類必須設定料號前綴（ADR-0006：ME、EL、RM、SF、FG、CS）；
/// 子分類的前綴可省略，省略時沿用上層（料號產生器於 M1-02 實作）。
/// </summary>
public sealed partial class ItemCategory
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 50;
    public const int PrefixMaxLength = 4;

    private ItemCategory()
    {
    }

    public ItemCategory(Guid id, Guid? parentId, string code, string name, string? itemCodePrefix, int sortOrder = 0)
    {
        if (parentId == id)
        {
            throw new DomainRuleException("分類不可以把自己設為上層分類。");
        }
        Id = id;
        ParentId = parentId;
        Code = Rules.Matches(code, CodePattern(), "分類代碼", "2～20 碼大寫英數或連字號");
        Name = Rules.Required(name, NameMaxLength, "分類名稱");
        if (itemCodePrefix is null)
        {
            if (parentId is null)
            {
                throw new DomainRuleException("頂層分類必須設定料號前綴。");
            }
        }
        else
        {
            ItemCodePrefix = Rules.Matches(itemCodePrefix, PrefixPattern(), "料號前綴", "2～4 碼大寫英數");
        }
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }

    /// <summary>上層分類；null 表示頂層。</summary>
    public Guid? ParentId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>料號前綴（如 EL → EL-000125）；子分類為 null 時沿用上層。</summary>
    public string? ItemCodePrefix { get; private set; }

    public bool IsActive { get; private set; } = true;

    public int SortOrder { get; private set; }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,19}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z0-9]{2,4}$")]
    private static partial Regex PrefixPattern();
}
