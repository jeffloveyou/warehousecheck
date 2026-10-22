using System.Text.RegularExpressions;

namespace Warehouse.Domain.Inventory;

/// <summary>用途類型適用的方向：進倉單用 In、出倉單用 Out（移庫不選用途）。</summary>
public enum PurposeDirection
{
    In,
    Out,
}

/// <summary>往來對象類型（使用者 2026-10-22 決定建主檔）。</summary>
public enum PartnerType
{
    Vendor,
    Customer,
    Department,
}

/// <summary>主檔代碼共用規則：英數字開頭，可含 - 與 _，最多 20 字，一律轉大寫。</summary>
public static partial class MasterCode
{
    public const int MaxLength = 20;

    public static string Normalize(string? value, string fieldName) =>
        Rules.Matches(value?.Trim().ToUpperInvariant(), Pattern(), fieldName, "英數字開頭、可含 - 與 _，最多 20 字");

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,19}$")]
    private static partial Regex Pattern();
}

/// <summary>
/// 進出倉單的用途類型（可維護清單，使用者 2026-10-22 決定）。不刪除，只停用；
/// 停用後新單據不可再選，已用過的單據保留原值。代碼建立後不可改。
/// </summary>
public sealed class DocumentPurpose
{
    public const int NameMaxLength = 50;

    private DocumentPurpose()
    {
    }

    public static DocumentPurpose Create(Guid id, PurposeDirection direction, string code, string name, int sortOrder) =>
        new()
        {
            Id = id,
            Direction = Enum.IsDefined(direction) ? direction : throw new DomainRuleException("用途方向不正確。"),
            Code = MasterCode.Normalize(code, "用途代碼"),
            Name = Rules.Required(name, NameMaxLength, "用途名稱"),
            SortOrder = sortOrder,
            IsActive = true,
        };

    public Guid Id { get; private set; }

    public PurposeDirection Direction { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    public void Update(string name, int sortOrder)
    {
        Name = Rules.Required(name, NameMaxLength, "用途名稱");
        SortOrder = sortOrder;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    /// <summary>這個用途可以用在哪一種單據。</summary>
    public bool AppliesTo(StockDocumentType type) =>
        (Direction, type) is (PurposeDirection.In, StockDocumentType.Inbound) or (PurposeDirection.Out, StockDocumentType.Outbound);
}

/// <summary>
/// 往來對象主檔：廠商、客戶、部門（M4-10）。代碼在同類型內唯一，建立後不可改；不刪除，只停用。
/// </summary>
public sealed class BusinessPartner
{
    public const int NameMaxLength = 100;

    private BusinessPartner()
    {
    }

    public static BusinessPartner Create(Guid id, PartnerType type, string code, string name, DateTimeOffset now) =>
        new()
        {
            Id = id == Guid.Empty ? throw new DomainRuleException("必須指定 ID。") : id,
            PartnerType = Enum.IsDefined(type) ? type : throw new DomainRuleException("往來對象類型不正確。"),
            Code = MasterCode.Normalize(code, "代碼"),
            Name = Rules.Required(name, NameMaxLength, "名稱"),
            IsActive = true,
            CreatedAt = now.ToUniversalTime(),
        };

    public Guid Id { get; private set; }

    public PartnerType PartnerType { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name) => Name = Rules.Required(name, NameMaxLength, "名稱");

    public void SetActive(bool isActive) => IsActive = isActive;
}
