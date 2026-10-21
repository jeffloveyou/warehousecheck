using Warehouse.Application.Abstractions;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Application.Warehousing;

public sealed record WarehouseRow(Guid Id, string Code, string Name, bool IsActive, int LocationCount, uint Version);

public sealed record WarehouseInput(string Code, string Name);

/// <summary>儲位清單與明細共用的資料。<see cref="QrContent"/> 是 QR Code 的文字內容（M3-04）。</summary>
public sealed record LocationRow(
    Guid Id,
    Guid WarehouseId,
    string WarehouseCode,
    string Code,
    string? Name,
    LocationStatus Status,
    string? Note,
    string? Zone,
    string? Rack,
    string? Level,
    string? Bin,
    LocationType? LocationType,
    decimal? MaxLoadKg,
    bool TemperatureControlled,
    bool Hazardous,
    DateTimeOffset UpdatedAt,
    uint Version)
{
    public string QrContent => Location.QrContent(WarehouseCode, Code);
}

/// <summary>新增或修改儲位。<see cref="Code"/> 只在新增時使用（儲位碼不可改）。</summary>
public sealed record LocationInput(string Code, LocationDetails Details);

/// <summary>儲位查詢：關鍵字比對儲位碼、名稱、區架層格（部分符合）；伺服器分頁。</summary>
public sealed record LocationSearch(
    Guid? WarehouseId, string? Keyword, LocationStatus? Status, int Page = 1, int PageSize = Paging.DefaultPageSize);

/// <summary>
/// 倉庫與儲位維護（M3-01、M3-06）。新增、修改、狀態變更都寫入稽核；
/// 修改要帶讀取時的版本（xmin），不符丟 ConcurrencyConflictException。
/// 權限（Warehouses.Manage）由頁面與 API 檢查；服務另外以 <see cref="Actor"/> 記錄操作者。
/// </summary>
public interface IWarehouseService
{
    Task<IReadOnlyList<WarehouseRow>> ListWarehousesAsync(CancellationToken cancellationToken);

    Task<Guid> CreateWarehouseAsync(WarehouseInput input, Actor actor, CancellationToken cancellationToken);

    /// <summary>只能改名稱（代碼不可改）。</summary>
    Task RenameWarehouseAsync(Guid id, uint expectedVersion, string name, Actor actor, CancellationToken cancellationToken);

    Task SetWarehouseActiveAsync(Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken);

    Task<PagedResult<LocationRow>> SearchLocationsAsync(LocationSearch search, CancellationToken cancellationToken);

    Task<LocationRow?> GetLocationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>新增儲位；倉庫必須存在且啟用。</summary>
    Task<Guid> CreateLocationAsync(Guid warehouseId, LocationInput input, Actor actor, CancellationToken cancellationToken);

    Task UpdateLocationAsync(Guid id, uint expectedVersion, LocationDetails details, Actor actor, CancellationToken cancellationToken);

    Task ChangeLocationStatusAsync(Guid id, uint expectedVersion, LocationStatus status, Actor actor, CancellationToken cancellationToken);
}
