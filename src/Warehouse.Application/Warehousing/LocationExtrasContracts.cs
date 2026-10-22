using Warehouse.Application.Abstractions;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Application.Warehousing;

public sealed record ItemLocationRow(
    Guid Id, Guid WarehouseId, string WarehouseCode, Guid LocationId, string LocationCode, LocationStatus LocationStatus, bool IsDefault);

/// <summary>建議儲位的來源。</summary>
public enum SuggestionReason
{
    Default,
    Frequent,
    HasStock,
}

/// <summary>建議儲位：來源、目前在這個儲位的數量（沒有庫存為 0）。只是建議，由人選擇。</summary>
public sealed record LocationSuggestion(
    Guid LocationId, string LocationCode, LocationStatus Status, SuggestionReason Reason, decimal QuantityHere);

/// <summary>物料的預設／常用倉位與建議儲位（M3-07）。維護限倉庫主管；寫入留稽核。</summary>
public interface IItemLocationService
{
    Task<IReadOnlyList<ItemLocationRow>> ListAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>新增常用倉位；<paramref name="isDefault"/> 為 true 時取代這個倉庫原本的預設倉位。</summary>
    Task AddAsync(Guid itemId, Guid warehouseId, string locationCode, bool isDefault, Actor actor, CancellationToken cancellationToken);

    Task SetDefaultAsync(Guid itemId, Guid itemLocationId, Actor actor, CancellationToken cancellationToken);

    Task RemoveAsync(Guid itemId, Guid itemLocationId, Actor actor, CancellationToken cancellationToken);

    /// <summary>
    /// 某物料在某倉庫的建議儲位：預設 → 常用 → 目前有庫存的儲位（依數量多到少），去重、最多 10 個；不列停用儲位。
    /// </summary>
    Task<IReadOnlyList<LocationSuggestion>> SuggestAsync(Guid itemId, Guid warehouseId, CancellationToken cancellationToken);
}

public sealed record LocationImageRow(
    Guid Id, int Width, int Height, string UploadedByName, DateTimeOffset UploadedAt, bool IsActive);

/// <summary>讀檔結果。</summary>
public sealed record StoredFile(Stream Content, string ContentType, long Length);

/// <summary>
/// 檔案存放（附件目錄）。鍵是系統產生的相對路徑（例 <c>location-images/2026/10/&lt;guid&gt;-large.jpg</c>），
/// 實作必須拒絕任何跑出附件目錄的路徑。
/// </summary>
public interface IFileStore
{
    Task SaveAsync(string key, byte[] content, CancellationToken cancellationToken);

    /// <summary>檔案不存在時回傳 null。</summary>
    Stream? OpenRead(string key);
}

/// <summary>
/// 倉位圖片（M3-08）。上傳限制：原檔 ≤ <see cref="MaxUploadBytes"/>、只收 JPEG／PNG／WebP（依檔案內容判斷，不看副檔名）、
/// 每個儲位最多 <see cref="LocationImage.MaxActivePerLocation"/> 張有效圖片。上傳與停用限倉庫主管。
/// </summary>
public interface ILocationImageService
{
    const long MaxUploadBytes = 10 * 1024 * 1024;

    Task<IReadOnlyList<LocationImageRow>> ListAsync(Guid locationId, CancellationToken cancellationToken);

    Task<Guid> UploadAsync(Guid locationId, Stream content, long length, Actor actor, CancellationToken cancellationToken);

    Task DeactivateAsync(Guid locationId, Guid imageId, Actor actor, CancellationToken cancellationToken);

    /// <summary>讀取圖片；<paramref name="thumbnail"/> 為 true 取縮圖。圖片不存在或已停用回傳 null。</summary>
    Task<StoredFile?> OpenAsync(Guid imageId, bool thumbnail, CancellationToken cancellationToken);
}
