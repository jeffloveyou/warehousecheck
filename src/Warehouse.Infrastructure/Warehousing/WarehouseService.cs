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
    public Task<IReadOnlyList<WarehouseRow>> ListWarehousesAsync(CancellationToken cancellationToken) =>
        ListWarehousesAsync(includeDeleted: false, cancellationToken);

    public async Task<IReadOnlyList<WarehouseRow>> ListWarehousesAsync(bool includeDeleted, CancellationToken cancellationToken) =>
        await db.Warehouses.AsNoTracking()
            .Where(w => includeDeleted || w.DeletedAt == null)
            .OrderBy(w => w.Code)
            .Select(w => new WarehouseRow(
                w.Id, w.Code, w.Name, w.IsActive, db.Locations.Count(l => l.WarehouseId == w.Id), EF.Property<uint>(w, "Version"),
                w.DeletedAt != null))
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

    public async Task<WarehouseDeleteResult> DeleteWarehouseAsync(
        Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken)
    {
        // 和停用相同：先鎖倉庫列（過帳、建立預留、開單、盤點、期初、新增儲位等都會先取倉庫 FOR SHARE，見 WarehouseLock），再檢查，檢查結果在 commit 前不會被改變
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DbCommands.ExecuteAsync(db, "SELECT 1 FROM warehouses WHERE id = @id FOR UPDATE", cancellationToken, DbCommands.Param("id", id));
        var warehouse = await LoadAsync(db.Warehouses, id, expectedVersion, "warehouse", "找不到倉庫。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, id, cancellationToken);
        if (warehouse.IsDeleted)
        {
            throw new DomainRuleException("倉庫已經刪除。");
        }

        var locations = db.Locations.Where(l => l.WarehouseId == id).Select(l => l.Id);
        if (await db.StockBalances.AnyAsync(b => b.Quantity != 0 && locations.Contains(b.LocationId), cancellationToken))
        {
            throw new DomainRuleException("倉庫還有庫存，不能刪除；請先出倉或移庫清空。");
        }
        if (await db.StockReservations.AnyAsync(r => r.WarehouseId == id && r.Status == ReservationStatus.Open, cancellationToken))
        {
            throw new DomainRuleException("倉庫還有未結束的預留，不能刪除；請先取消預留。");
        }
        // 草稿、送審中的單據之後還可能過帳；移庫的目的倉也算（明細的目的儲位在這個倉庫）
        var openDocuments = await db.StockDocuments.AnyAsync(d =>
            (d.Status == StockDocumentStatus.Draft || d.Status == StockDocumentStatus.Submitted)
            && (d.WarehouseId == id || d.Lines.Any(l => l.TargetLocationId != null && locations.Contains(l.TargetLocationId.Value))),
            cancellationToken);
        if (openDocuments)
        {
            throw new DomainRuleException("倉庫還有草稿或送審中的單據（含移入這個倉庫的移庫單），請先確認或取消。");
        }
        if (await db.Stocktakes.AnyAsync(s => s.WarehouseId == id
                && (s.Status == StocktakeStatus.Counting || s.Status == StocktakeStatus.Submitted), cancellationToken))
        {
            throw new DomainRuleException("倉庫還有進行中的盤點，請先核准或取消。");
        }

        var now = clock.GetUtcNow();
        var locationCount = await locations.CountAsync(cancellationToken);
        // 「用過」：有任何單據（含已取消、移入的移庫）、異動、結存列（含 0）、盤點、預留，或儲位做過期初
        var used = await db.StockDocuments.AnyAsync(d => d.WarehouseId == id
                       || d.Lines.Any(l => l.TargetLocationId != null && locations.Contains(l.TargetLocationId.Value)), cancellationToken)
                   || await db.StockMovements.AnyAsync(m => locations.Contains(m.LocationId), cancellationToken)
                   || await db.StockBalances.AnyAsync(b => locations.Contains(b.LocationId), cancellationToken)
                   || await db.Stocktakes.AnyAsync(s => s.WarehouseId == id, cancellationToken)
                   || await db.StockReservations.AnyAsync(r => r.WarehouseId == id, cancellationToken)
                   || await db.Locations.AnyAsync(l => l.WarehouseId == id && l.OpeningDocumentId != null, cancellationToken);
        if (used)
        {
            // 常用倉位是設定、不是歷史：一起清掉，物料頁不再列出已刪除的倉庫（M8-01 審查 #4）
            await db.ItemLocations.Where(x => x.WarehouseId == id).ExecuteDeleteAsync(cancellationToken);
            warehouse.MarkDeleted(now, actor.Name);
            AuditLog.Add(db, actor, now, "warehouse.delete", "warehouse", id.ToString(),
                new { warehouse.Code, warehouse.Name, Locations = locationCount }, new { Mode = "hidden" });
            await SaveAsync("warehouse", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WarehouseDeleteResult.Hidden;
        }

        // 從未用過：連同設定類資料一起刪除。儲位圖片只刪資料列，檔案留給孤兒檔清理（clean-attachments）隔離，
        // 交易失敗時不會誤刪檔案。帳號的倉庫範圍刪掉這一筆只會縮小權限（AllWarehouses 旗標另外記錄）。
        int images;
        int scopes;
        try
        {
            images = await db.LocationImages.Where(i => locations.Contains(i.LocationId)).ExecuteDeleteAsync(cancellationToken);
            await db.ItemLocations.Where(x => x.WarehouseId == id || locations.Contains(x.LocationId)).ExecuteDeleteAsync(cancellationToken);
            scopes = await db.UserWarehouses.Where(x => x.WarehouseId == id).ExecuteDeleteAsync(cancellationToken);
            await db.Locations.Where(l => l.WarehouseId == id).ExecuteDeleteAsync(cancellationToken);
            await db.Warehouses.Where(w => w.Id == id).ExecuteDeleteAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // 保底：上面的檢查漏掉某種引用時，資料庫的外鍵（RESTRICT）會擋下，整個交易回復（資料表名稱只進日誌，不給使用者看）
            throw new DomainRuleException("倉庫的資料仍被其他紀錄引用，不能刪除；請改用停用。");
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable)
        {
            throw new DomainRuleException("倉庫正在被其他作業使用（過帳、盤點等），請稍後再試。");
        }
        AuditLog.Add(db, actor, now, "warehouse.delete", "warehouse", id.ToString(),
            new { warehouse.Code, warehouse.Name, Locations = locationCount, Images = images, UserScopes = scopes }, new { Mode = "removed" });
        db.Entry(warehouse).State = EntityState.Detached;
        await SaveAsync("warehouse", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return WarehouseDeleteResult.Removed;
    }

    public async Task RestoreWarehouseAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken)
    {
        var warehouse = await LoadAsync(db.Warehouses, id, expectedVersion, "warehouse", "找不到倉庫。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, id, cancellationToken);
        warehouse.Restore();
        AuditLog.Add(db, actor, clock.GetUtcNow(), "warehouse.restore", "warehouse", id.ToString(), null, new { warehouse.Code });
        await SaveAsync("warehouse", cancellationToken);
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
        else
        {
            // 不指定倉庫時不列已刪除倉庫的儲位（指定時仍可查看，例如從「顯示已刪除」點進來）
            locations = locations.Where(l => db.Warehouses.Any(w => w.Id == l.WarehouseId && w.DeletedAt == null));
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
        // 匯入會在自己的交易（每列子交易）裡呼叫：已有交易就沿用，FOR SHARE 鎖跟著那個交易
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var warehouseCode = await WarehouseLock.LockUsableAsync(db, warehouseId, "倉庫已停用，不能新增儲位。", cancellationToken);
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
            new { WarehouseCode = warehouseCode, location.Code, input.Details });
        await SaveAsync("location", cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return location.Id;
    }

    public async Task UpdateLocationAsync(
        Guid id, uint expectedVersion, LocationDetails details, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await LockLocationWarehouseAsync(id, cancellationToken);
        var location = await LoadAsync(db.Locations, id, expectedVersion, "location", "找不到儲位。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, location.WarehouseId, cancellationToken);
        var before = Snapshot(location);
        var now = clock.GetUtcNow();
        location.Update(details, now);
        AuditLog.Add(db, actor, now, "location.update", "location", id.ToString(), before, Snapshot(location));
        await SaveAsync("location", cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 修改儲位前先鎖它的倉庫（FOR SHARE，順序「倉庫 → 儲位」與刪除倉庫相同）：已刪除倉庫的儲位不能再改（M8-01 審查 #3）。
    /// 停用的倉庫仍可整理儲位資料。
    /// </summary>
    private async Task LockLocationWarehouseAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var warehouseId = await db.Locations.AsNoTracking().Where(l => l.Id == locationId).Select(l => (Guid?)l.WarehouseId)
            .SingleOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("location.not_found", "找不到儲位。");
        await WarehouseLock.LockNotDeletedAsync(db, [warehouseId], cancellationToken);
    }

    public async Task ChangeLocationStatusAsync(
        Guid id, uint expectedVersion, LocationStatus status, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockLocationWarehouseAsync(id, cancellationToken);
        await DbCommands.ExecuteAsync(db, "SELECT 1 FROM locations WHERE id = @id FOR UPDATE", cancellationToken, DbCommands.Param("id", id));
        var location = await LoadAsync(db.Locations, id, expectedVersion, "location", "找不到儲位。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, location.WarehouseId, cancellationToken);
        // 有庫存的儲位不能停用（停用後無法過帳，庫存會卡住）；改待驗可以，結存立即不算可用量（M3-01 審查 #3）
        if (status == LocationStatus.Inactive
            && await db.StockBalances.AnyAsync(b => b.LocationId == id && b.Quantity != 0, cancellationToken))
        {
            throw new DomainRuleException("儲位還有庫存，不能停用；請先移出（M4）或盤點調整（M6）。");
        }
        // 未結束的盤點範圍內的儲位不能停用：停用後在這裡找到的物料無法過帳，盤點只能取消（M6-02 審查 Y6）
        if (status == LocationStatus.Inactive
            && await db.StocktakeLocations.AnyAsync(x => x.LocationId == id && x.IsOpen, cancellationToken))
        {
            throw new DomainRuleException("儲位正在盤點中，請先完成或取消盤點再停用。");
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new DomainRuleException("資料已被其他操作變更或刪除，請重新整理後再試。");
        }
    }

    private static ConcurrencyConflictException Conflict(string kind) =>
        new($"{kind}.concurrency_conflict", "資料已被他人修改，請重新載入後再操作。");
}
