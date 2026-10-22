namespace Warehouse.Domain.Warehousing;

/// <summary>
/// 物料的預設／常用倉位（M3-07）。每個物料在每個倉庫最多一個預設倉位（資料庫部分唯一索引）。
/// 只是「建議」：入庫、期初時列出供人選擇，系統不會自動決定儲位。
/// </summary>
public sealed class ItemLocation
{
    private ItemLocation()
    {
    }

    public static ItemLocation Create(Guid id, Guid itemId, Guid warehouseId, Guid locationId, bool isDefault, DateTimeOffset now)
    {
        if (id == Guid.Empty || itemId == Guid.Empty || warehouseId == Guid.Empty || locationId == Guid.Empty)
        {
            throw new DomainRuleException("常用倉位必須指定物料與儲位。");
        }
        return new ItemLocation
        {
            Id = id, ItemId = itemId, WarehouseId = warehouseId, LocationId = locationId, IsDefault = isDefault,
            CreatedAt = now.ToUniversalTime(),
        };
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    /// <summary>儲位所屬倉庫（冗餘存放，讓「每倉一個預設」可以用唯一索引保證）。</summary>
    public Guid WarehouseId { get; private set; }

    public Guid LocationId { get; private set; }

    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;
}

/// <summary>
/// 倉位圖片（M3-08）。上傳的原圖不保存：伺服器重新編碼成大圖（長邊 1600）與縮圖（長邊 320）的 JPEG，
/// 同時去掉 EXIF（含 GPS）。檔案放在與發布目錄分開的附件目錄，檔名是系統產生的 GUID。只停用、不刪除。
/// </summary>
public sealed class LocationImage
{
    public const int MaxActivePerLocation = 10;

    private LocationImage()
    {
    }

    public static LocationImage Create(
        Guid id, Guid locationId, string largeKey, string thumbKey, int width, int height, long largeBytes, long thumbBytes,
        Guid actorId, string actorName, DateTimeOffset now) => new()
    {
        Id = id,
        LocationId = locationId,
        LargeKey = largeKey,
        ThumbKey = thumbKey,
        Width = width,
        Height = height,
        LargeBytes = largeBytes,
        ThumbBytes = thumbBytes,
        UploadedById = actorId,
        UploadedByName = actorName,
        UploadedAt = now.ToUniversalTime(),
    };

    public Guid Id { get; private set; }

    public Guid LocationId { get; private set; }

    /// <summary>大圖在附件目錄中的相對路徑（系統產生）。</summary>
    public string LargeKey { get; private set; } = string.Empty;

    public string ThumbKey { get; private set; } = string.Empty;

    /// <summary>大圖的寬高（像素）。</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    public long LargeBytes { get; private set; }

    public long ThumbBytes { get; private set; }

    public Guid UploadedById { get; private set; }

    public string UploadedByName { get; private set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; private set; }

    public bool IsActive { get; private set; } = true;

    public string? DeactivatedByName { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public void Deactivate(string actorName, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainRuleException("這張圖片已經停用。");
        }
        IsActive = false;
        DeactivatedByName = actorName;
        DeactivatedAt = now.ToUniversalTime();
    }
}
