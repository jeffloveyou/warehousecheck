namespace Warehouse.Application.MasterData;

/// <summary>分類清單的一列。<see cref="Path"/> 為「電控 / 伺服」形式，供下拉選單顯示。</summary>
public sealed record CategoryRow(
    Guid Id,
    Guid? ParentId,
    string Code,
    string Name,
    string Path,
    string? ItemCodePrefix,
    string? EffectivePrefix,
    int SortOrder,
    bool IsActive,
    uint Version);

/// <summary>單位清單的一列。</summary>
public sealed record UnitRow(Guid Id, string Code, string Name, int DecimalPlaces, bool IsActive, bool IsInUse, uint Version);

public sealed record CategoryInput(Guid? ParentId, string Code, string Name, string? ItemCodePrefix, int SortOrder);

public sealed record UnitInput(string Code, string Name, int DecimalPlaces);

/// <summary>
/// 分類與計量單位維護（物料管理員）。不提供刪除，一律停用（已被物料使用的資料不能刪）。
/// 修改都要帶 <c>expectedVersion</c>（xmin），版本不符丟 <c>ConcurrencyConflictException</c>。
/// </summary>
public interface IMasterDataService
{
    /// <summary>全部分類（含停用），依路徑排序。分類表很小，不分頁。</summary>
    Task<IReadOnlyList<CategoryRow>> ListCategoriesAsync(CancellationToken cancellationToken);

    Task<Guid> CreateCategoryAsync(CategoryInput input, CancellationToken cancellationToken);

    /// <summary>
    /// 修改分類（代碼不可改）。會檢查：上層不可形成循環（A→B→A）、
    /// 已發過料號的前綴不可改或移除。
    /// </summary>
    Task UpdateCategoryAsync(Guid id, uint expectedVersion, CategoryInput input, CancellationToken cancellationToken);

    Task SetCategoryActiveAsync(Guid id, uint expectedVersion, bool isActive, CancellationToken cancellationToken);

    /// <summary>全部單位（含停用），依代碼排序。</summary>
    Task<IReadOnlyList<UnitRow>> ListUnitsAsync(CancellationToken cancellationToken);

    Task<Guid> CreateUnitAsync(UnitInput input, CancellationToken cancellationToken);

    /// <summary>修改名稱與小數位數；代碼不可改；已有物料使用時不可改小數位數。</summary>
    Task UpdateUnitAsync(Guid id, uint expectedVersion, UnitInput input, CancellationToken cancellationToken);

    Task SetUnitActiveAsync(Guid id, uint expectedVersion, bool isActive, CancellationToken cancellationToken);
}
