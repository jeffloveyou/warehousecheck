using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.MasterData;

/// <summary>
/// 分類與單位維護。分類的「上層／前綴」修改會影響料號產生，所以在交易內取 advisory lock，
/// 讓同時修改分類樹的請求排隊，避免兩人同時把 A 設為 B 的上層、B 設為 A 的上層。
/// </summary>
public sealed class MasterDataService(WarehouseDbContext db) : IMasterDataService
{
    /// <summary>分類最多幾層（與料號前綴往上找的上限一致）。</summary>
    public const int MaxCategoryDepth = 8;

    public async Task<IReadOnlyList<CategoryRow>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await db.ItemCategories.AsNoTracking()
            .Select(c => new
            {
                c.Id, c.ParentId, c.Code, c.Name, c.ItemCodePrefix, c.SortOrder, c.IsActive,
                Version = EF.Property<uint>(c, "Version"),
            })
            .ToListAsync(cancellationToken);
        var byId = categories.ToDictionary(c => c.Id);

        // 往上組出路徑與實際使用的前綴；遇到循環或過深就停（資料錯誤時也不會無窮迴圈）
        (string Path, string? Prefix, string SortKey) Describe(Guid id)
        {
            var names = new List<string>();
            var sortKeys = new List<string>();
            string? prefix = null;
            Guid? current = id;
            for (var depth = 0; depth < MaxCategoryDepth && current is { } cid && byId.TryGetValue(cid, out var c); depth++)
            {
                names.Insert(0, c.Name);
                sortKeys.Insert(0, $"{c.SortOrder:D6}{c.Code}");
                prefix ??= c.ItemCodePrefix;
                current = c.ParentId;
            }
            return (string.Join(" / ", names), prefix, string.Join("/", sortKeys));
        }

        return categories
            .Select(c =>
            {
                var (path, prefix, sortKey) = Describe(c.Id);
                return (Row: new CategoryRow(c.Id, c.ParentId, c.Code, c.Name, path, c.ItemCodePrefix, prefix, c.SortOrder, c.IsActive, c.Version), sortKey);
            })
            .OrderBy(x => x.sortKey, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToList();
    }

    public async Task<Guid> CreateCategoryAsync(CategoryInput input, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockCategoryTreeAsync(cancellationToken);
        if (input.ParentId is { } parentId)
        {
            await EnsureParentUsableAsync(Guid.NewGuid(), parentId, cancellationToken);
        }
        var prefixInput = string.IsNullOrWhiteSpace(input.ItemCodePrefix) ? null : input.ItemCodePrefix.Trim();
        await EnsurePrefixNotIssuedAsync(prefixInput, cancellationToken);
        var category = new ItemCategory(
            Guid.NewGuid(), input.ParentId, input.Code?.Trim() ?? string.Empty, input.Name, input.ItemCodePrefix, input.SortOrder);
        db.ItemCategories.Add(category);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return category.Id;
    }

    public async Task UpdateCategoryAsync(
        Guid id, uint expectedVersion, CategoryInput input, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockCategoryTreeAsync(cancellationToken);
        var category = await LoadAsync(db.ItemCategories, id, expectedVersion, "category", cancellationToken);

        if (input.ParentId != category.ParentId && input.ParentId is { } parentId)
        {
            await EnsureParentUsableAsync(category.Id, parentId, cancellationToken);
        }
        var newPrefix = string.IsNullOrWhiteSpace(input.ItemCodePrefix) ? null : input.ItemCodePrefix.Trim();
        if (category.ItemCodePrefix is { } oldPrefix && oldPrefix != newPrefix
            && await db.ItemCodeCounters.AnyAsync(c => c.Prefix == oldPrefix, cancellationToken))
        {
            throw new DomainRuleException($"前綴 {oldPrefix} 已發過正式料號，不可修改或移除。");
        }
        if (newPrefix != category.ItemCodePrefix)
        {
            await EnsurePrefixNotIssuedAsync(newPrefix, cancellationToken);
        }

        category.Update(input.ParentId, input.Name, newPrefix, input.SortOrder);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetCategoryActiveAsync(Guid id, uint expectedVersion, bool isActive, CancellationToken cancellationToken)
    {
        var category = await LoadAsync(db.ItemCategories, id, expectedVersion, "category", cancellationToken);
        if (isActive)
        {
            category.Reactivate();
        }
        else
        {
            category.Deactivate();
        }
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnitRow>> ListUnitsAsync(CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking()
            .OrderBy(u => u.Code)
            .Select(u => new UnitRow(
                u.Id, u.Code, u.Name, u.DecimalPlaces, u.IsActive,
                db.Items.Any(i => i.BaseUnitId == u.Id),
                EF.Property<uint>(u, "Version")))
            .ToListAsync(cancellationToken);

    public async Task<Guid> CreateUnitAsync(UnitInput input, CancellationToken cancellationToken)
    {
        var unit = new Unit(Guid.NewGuid(), input.Code?.Trim() ?? string.Empty, input.Name, input.DecimalPlaces);
        db.Units.Add(unit);
        await SaveAsync(cancellationToken);
        return unit.Id;
    }

    public async Task UpdateUnitAsync(Guid id, uint expectedVersion, UnitInput input, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var unit = await LoadAsync(db.Units, id, expectedVersion, "unit", cancellationToken);
        var isInUse = await db.Items.AnyAsync(i => i.BaseUnitId == id, cancellationToken);
        unit.Update(input.Name, input.DecimalPlaces, isInUse);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetUnitActiveAsync(Guid id, uint expectedVersion, bool isActive, CancellationToken cancellationToken)
    {
        var unit = await LoadAsync(db.Units, id, expectedVersion, "unit", cancellationToken);
        if (isActive)
        {
            unit.Reactivate();
        }
        else
        {
            unit.Deactivate();
        }
        await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// 新的上層必須存在、啟用，而且不能是自己或自己的下層（往上走不能遇到自己），層數不超過上限。
    /// </summary>
    private async Task EnsureParentUsableAsync(Guid categoryId, Guid parentId, CancellationToken cancellationToken)
    {
        var parents = await db.ItemCategories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentId, c.IsActive })
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        if (!parents.TryGetValue(parentId, out var parent) || !parent.IsActive)
        {
            throw new DomainRuleException("上層分類不存在或已停用。");
        }

        // 新上層本身在第幾層（頂層 = 1），途中遇到自己就是循環
        var parentDepth = 0;
        Guid? current = parentId;
        while (current is { } cid)
        {
            if (cid == categoryId)
            {
                throw new DomainRuleException("不可以把分類移到自己的下層分類底下（會形成循環）。");
            }
            if (++parentDepth > MaxCategoryDepth)
            {
                break;
            }
            current = parents.TryGetValue(cid, out var c) ? c.ParentId : null;
        }

        // 被搬移的分類連同底下的子樹一起移動：新上層深度＋子樹高度不可超過上限（M1-03 審查 R3），
        // 否則最底層的物料核准時往上找不到料號前綴
        var children = parents.Values.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value, c => c.Id);
        int SubtreeHeight(Guid id, int guard) =>
            guard > MaxCategoryDepth ? guard : 1 + children[id].Select(child => SubtreeHeight(child, guard + 1)).DefaultIfEmpty(0).Max();
        if (parentDepth + SubtreeHeight(categoryId, 1) > MaxCategoryDepth)
        {
            throw new DomainRuleException($"分類最多 {MaxCategoryDepth} 層（含底下的子分類）。");
        }
    }

    /// <summary>新前綴不可是已發過號的前綴（例如曾屬於別的分類、後來被移除的前綴），避免同一前綴跨兩個分類。</summary>
    private async Task EnsurePrefixNotIssuedAsync(string? prefix, CancellationToken cancellationToken)
    {
        if (prefix is not null && await db.ItemCodeCounters.AnyAsync(c => c.Prefix == prefix, cancellationToken))
        {
            throw new DomainRuleException($"前綴 {prefix} 已發過正式料號，不能再給其他分類使用。");
        }
    }

    private Task LockCategoryTreeAsync(CancellationToken cancellationToken) =>
        AdvisoryLocks.LockCategoryTreeAsync(db, exclusive: true, cancellationToken);

    private async Task<T> LoadAsync<T>(
        IQueryable<T> set, Guid id, uint expectedVersion, string kind, CancellationToken cancellationToken)
        where T : class
    {
        var entity = await set.SingleOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, cancellationToken)
            ?? throw new NotFoundException($"{kind}.not_found", kind == "unit" ? "找不到單位。" : "找不到分類。");
        var version = db.Entry(entity).Property<uint>("Version");
        if (version.CurrentValue != expectedVersion)
        {
            throw Conflict(kind);
        }
        return entity;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict("master_data");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new DomainRuleException(pg.ConstraintName switch
            {
                "ix_units_code" => "單位代碼已存在。",
                "ix_item_categories_code" => "分類代碼已存在。",
                "ix_item_categories_item_code_prefix" => "料號前綴已被其他分類使用。",
                _ => "資料重複。",
            });
        }
    }

    private static ConcurrencyConflictException Conflict(string kind) =>
        new($"{kind}.concurrency_conflict", "資料已被他人修改，請重新載入後再操作。");
}
