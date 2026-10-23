using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Text;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Warehousing;

/// <summary>
/// <see cref="IWarehouseService"/> 實作。每個寫入動作都在同一交易內寫稽核（audit_logs）。
/// 儲位查詢在資料庫篩選與分頁（ILIKE 跳脫，見 <see cref="LikePattern"/>）。
/// </summary>
public sealed class WarehouseService(WarehouseDbContext db, TimeProvider clock) : IWarehouseService
{
    public async Task<IReadOnlyList<WarehouseRow>> ListWarehousesAsync(CancellationToken cancellationToken) =>
        await db.Warehouses.AsNoTracking()
            .OrderBy(w => w.Code)
            .Select(w => new WarehouseRow(
                w.Id, w.Code, w.Name, w.IsActive, db.Locations.Count(l => l.WarehouseId == w.Id), EF.Property<uint>(w, "Version")))
            .ToListAsync(cancellationToken);

    public async Task<Guid> CreateWarehouseAsync(WarehouseInput input, Actor actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await WarehouseAccess.EnsureAllAsync(db, actor, cancellationToken);
        // 代碼先正規化（小寫、全形都接受），Domain 仍檢查嚴格格式（M3-01 審查 #7）
        var warehouse = new WarehouseSite(Guid.NewGuid(), TextNormalizer.Normalize(input.Code), input.Name, now);
        db.Warehouses.Add(warehouse);
        AuditLog.Add(db, actor, now, "warehouse.create", "warehouse", warehouse.Id.ToString(), null,
            new { warehouse.Code, warehouse.Name });
        await SaveAsync("warehouse", cancellationToken);
        return warehouse.Id;
    }

    public async Task RenameWarehouseAsync(
        Guid id, uint expectedVersion, string name, Actor actor, CancellationToken cancellationToken)
    {
        var warehouse = await LoadAsync(db.Warehouses, id, expectedVersion, "warehouse", "找不到倉庫。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, id, cancellationToken);
        var before = warehouse.Name;
        warehouse.Rename(name);
        AuditLog.Add(db, actor, clock.GetUtcNow(), "warehouse.rename", "warehouse", id.ToString(),
            new { Name = before }, new { warehouse.Name });
        await SaveAsync("warehouse", cancellationToken);
    }

    public async Task SetWarehouseActiveAsync(
        Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken)
    {
        // 先鎖倉庫列再查結存：正在過帳的交易（持有 FOR SHARE）commit 之後才檢查，看得到它寫入的結存（M3-03 審查 #2）
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DbCommands.ExecuteAsync(db, "SELECT 1 FROM warehouses WHERE id = @id FOR UPDATE", cancellationToken, DbCommands.Param("id", id));
        var warehouse = await LoadAsync(db.Warehouses, id, expectedVersion, "warehouse", "找不到倉庫。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, id, cancellationToken);
        if (warehouse.IsActive == isActive)
        {
            throw new DomainRuleException(isActive ? "倉庫已經是啟用狀態。" : "倉庫已經停用。");
        }
        if (!isActive && await db.StockBalances.AnyAsync(
                b => b.Quantity != 0 && db.Locations.Any(l => l.Id == b.LocationId && l.WarehouseId == id), cancellationToken))
        {
            throw new DomainRuleException("倉庫還有庫存，不能停用。");
        }
        // 建立預留先取倉庫 FOR SHARE，會等這裡的 FOR UPDATE；這裡看到的 Open 預留是完整的（M4-01 審查 #7）
        if (!isActive && await db.StockReservations.AnyAsync(
                r => r.WarehouseId == id && r.Status == ReservationStatus.Open, cancellationToken))
        {
            throw new DomainRuleException("倉庫還有未結束的預留，不能停用；請先取消預留。");
        }
        if (isActive)
        {
            warehouse.Reactivate();
        }
        else
        {
            warehouse.Deactivate();
        }
        AuditLog.Add(db, actor, clock.GetUtcNow(), "warehouse.active", "warehouse", id.ToString(),
            new { IsActive = !isActive }, new { IsActive = isActive });
        await SaveAsync("warehouse", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PagedResult<LocationRow>> SearchLocationsAsync(LocationSearch search, CancellationToken cancellationToken)
    {
        var page = Paging.ClampPage(search.Page);
        var pageSize = Paging.ClampPageSize(search.PageSize);
        // 篩選要套在實體上（投影成 record 之後 EF 無法再轉成 SQL）
        var locations = db.Locations.AsNoTracking();
        if (search.WarehouseId is { } warehouseId)
        {
            locations = locations.Where(l => l.WarehouseId == warehouseId);
        }
        if (search.Status is { } status)
        {
            locations = locations.Where(l => l.Status == status);
        }
        var keyword = search.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            if (keyword.Length > Paging.MaxKeywordLength)
            {
                keyword = keyword[..Paging.MaxKeywordLength];
            }
            // 儲位碼一律大寫英數：先正規化（全形、小寫都能找到），再部分比對
            var pattern = LikePattern.Contains(TextNormalizer.Normalize(keyword));
            var raw = LikePattern.Contains(keyword);
            locations = locations.Where(l =>
                EF.Functions.Like(l.Code, pattern, LikePattern.Escape)
                || (l.Name != null && EF.Functions.ILike(l.Name, raw, LikePattern.Escape))
                || (l.Zone != null && EF.Functions.ILike(l.Zone, raw, LikePattern.Escape))
                || (l.Rack != null && EF.Functions.ILike(l.Rack, raw, LikePattern.Escape)));
        }

        var total = await locations.CountAsync(cancellationToken);
        var rows = await ToRows(locations, ordered: true)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<LocationRow>(rows, page, pageSize, total);
    }

    public Task<LocationRow?> GetLocationByCodeAsync(string warehouseCode, string locationCode, CancellationToken cancellationToken)
    {
        var wc = TextNormalizer.Normalize(warehouseCode);
        var lc = TextNormalizer.Normalize(locationCode);
        var locations = db.Locations.AsNoTracking()
            .Where(l => l.Code == lc && db.Warehouses.Any(w => w.Id == l.WarehouseId && w.Code == wc));
        return ToRows(locations, ordered: false).SingleOrDefaultAsync(cancellationToken);
    }

    public Task<LocationRow?> GetLocationAsync(Guid id, CancellationToken cancellationToken) =>
        ToRows(db.Locations.AsNoTracking().Where(l => l.Id == id), ordered: false).SingleOrDefaultAsync(cancellationToken);

    public async Task<Guid> CreateLocationAsync(
        Guid warehouseId, LocationInput input, Actor actor, CancellationToken cancellationToken)
    {
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken)
            ?? throw new NotFoundException("warehouse.not_found", "找不到倉庫。");
        if (!warehouse.IsActive)
        {
            throw new DomainRuleException("倉庫已停用，不能新增儲位。");
        }
        await WarehouseAccess.EnsureAsync(db, actor, warehouseId, cancellationToken);
        var now = clock.GetUtcNow();
        var location = Location.Create(Guid.NewGuid(), warehouseId, TextNormalizer.Normalize(input.Code), input.Details, now);
        // 儲位碼不可與有效原廠條碼相同（掃到時會先比中條碼，M5-03 審查 M1）。
        // 新增儲位只有倉庫主管、次數很少，不另外加鎖；同時新增的極少數情況由條碼登錄端的檢查補上。
        if (await db.ItemBarcodes.AnyAsync(b => b.IsActive && b.NormalizedText == location.Code, cancellationToken))
        {
            throw new DomainRuleException($"儲位碼 {location.Code} 已是某個物料的原廠條碼，請改用其他儲位碼。");
        }
        db.Locations.Add(location);
        AuditLog.Add(db, actor, now, "location.create", "location", location.Id.ToString(), null,
            new { WarehouseCode = warehouse.Code, location.Code, input.Details });
        await SaveAsync("location", cancellationToken);
        return location.Id;
    }

    public async Task UpdateLocationAsync(
        Guid id, uint expectedVersion, LocationDetails details, Actor actor, CancellationToken cancellationToken)
    {
        var location = await LoadAsync(db.Locations, id, expectedVersion, "location", "找不到儲位。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, location.WarehouseId, cancellationToken);
        var before = Snapshot(location);
        var now = clock.GetUtcNow();
        location.Update(details, now);
        AuditLog.Add(db, actor, now, "location.update", "location", id.ToString(), before, Snapshot(location));
        await SaveAsync("location", cancellationToken);
    }

    public async Task ChangeLocationStatusAsync(
        Guid id, uint expectedVersion, LocationStatus status, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DbCommands.ExecuteAsync(db, "SELECT 1 FROM locations WHERE id = @id FOR UPDATE", cancellationToken, DbCommands.Param("id", id));
        var location = await LoadAsync(db.Locations, id, expectedVersion, "location", "找不到儲位。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, location.WarehouseId, cancellationToken);
        // 有庫存的儲位不能停用（停用後無法過帳，庫存會卡住）；改待驗可以，結存立即不算可用量（M3-01 審查 #3）
        if (status == LocationStatus.Inactive
            && await db.StockBalances.AnyAsync(b => b.LocationId == id && b.Quantity != 0, cancellationToken))
        {
            throw new DomainRuleException("儲位還有庫存，不能停用；請先移出（M4）或盤點調整（M6）。");
        }
        var before = location.Status;
        var now = clock.GetUtcNow();
        location.ChangeStatus(status, now);
        AuditLog.Add(db, actor, now, "location.status", "location", id.ToString(),
            new { Status = before.ToString() }, new { Status = status.ToString() });
        await SaveAsync("location", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>已篩選的儲位加上倉庫代碼，投影成畫面用的資料（排序在投影前完成）。</summary>
    private IQueryable<LocationRow> ToRows(IQueryable<Location> locations, bool ordered)
    {
        var joined = locations.Join(db.Warehouses, l => l.WarehouseId, w => w.Id, (l, w) => new { l, WarehouseCode = w.Code });
        if (ordered)
        {
            joined = joined.OrderBy(x => x.WarehouseCode).ThenBy(x => x.l.Code);
        }
        return joined.Select(x => new LocationRow(
            x.l.Id, x.l.WarehouseId, x.WarehouseCode, x.l.Code, x.l.Name, x.l.Status, x.l.Note, x.l.Zone, x.l.Rack,
            x.l.Level, x.l.Bin, x.l.LocationType, x.l.MaxLoadKg, x.l.TemperatureControlled, x.l.Hazardous, x.l.UpdatedAt,
            EF.Property<uint>(x.l, "Version")));
    }

    private static object Snapshot(Location l) => new
    {
        l.Name, l.Note, l.Zone, l.Rack, l.Level, l.Bin, LocationType = l.LocationType?.ToString(), l.MaxLoadKg,
        l.TemperatureControlled, l.Hazardous,
    };

    private async Task<T> LoadAsync<T>(
        IQueryable<T> set, Guid id, uint expectedVersion, string kind, string notFound, CancellationToken cancellationToken)
        where T : class
    {
        var entity = await set.SingleOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, cancellationToken)
            ?? throw new NotFoundException($"{kind}.not_found", notFound);
        if (db.Entry(entity).Property<uint>("Version").CurrentValue != expectedVersion)
        {
            throw Conflict(kind);
        }
        return entity;
    }

    private async Task SaveAsync(string kind, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict(kind);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new DomainRuleException(pg.ConstraintName switch
            {
                "ix_warehouses_code" => "倉庫代碼已存在。",
                "ux_locations_warehouse_code" => "這個倉庫已經有相同的儲位碼。",
                _ => "資料重複。",
            });
        }
    }

    private static ConcurrencyConflictException Conflict(string kind) =>
        new($"{kind}.concurrency_conflict", "資料已被他人修改，請重新載入後再操作。");
}
