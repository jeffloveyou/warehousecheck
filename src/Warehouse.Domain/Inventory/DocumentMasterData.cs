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

/// <summary>
/// 進出倉單附件（M4-10，使用者 2026-10-22）：圖片（轉成 JPEG 大圖＋縮圖）或 PDF（原檔）。
/// 草稿可移除（停用）；送出、過帳後只能新增，不能移除（trigger 保證）。檔案在附件目錄，不在資料庫。
/// </summary>
public sealed class DocumentAttachment
{
    public const int MaxActivePerDocument = 10;
    public const int FileNameMaxLength = 200;

    private DocumentAttachment()
    {
    }

    public static DocumentAttachment Create(
        Guid id, Guid documentId, bool isPdf, string fileName, string fileKey, string? thumbKey, long sizeBytes,
        Guid actorId, string actorName, DateTimeOffset now) =>
        new()
        {
            Id = id,
            DocumentId = documentId,
            IsPdf = isPdf,
            FileName = Rules.Required(Path.GetFileName(fileName ?? string.Empty), FileNameMaxLength, "檔名"),
            FileKey = fileKey,
            ThumbKey = thumbKey,
            SizeBytes = sizeBytes,
            UploadedById = actorId,
            UploadedByName = actorName,
            UploadedAt = now.ToUniversalTime(),
            IsActive = true,
        };

    public Guid Id { get; private set; }

    public Guid DocumentId { get; private set; }

    public bool IsPdf { get; private set; }

    /// <summary>使用者上傳時的原檔名（只顯示用，不用來組路徑）。</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>附件目錄內的鍵：圖片是 1600 大圖、PDF 是原檔。</summary>
    public string FileKey { get; private set; } = string.Empty;

    /// <summary>圖片縮圖的鍵；PDF 為 null。</summary>
    public string? ThumbKey { get; private set; }

    public long SizeBytes { get; private set; }

    public Guid UploadedById { get; private set; }

    public string UploadedByName { get; private set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; private set; }

    public bool IsActive { get; private set; }
}
