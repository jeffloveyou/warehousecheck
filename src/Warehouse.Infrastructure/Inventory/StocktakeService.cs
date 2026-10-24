using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Settings;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Warehousing;
using Warehouse.Infrastructure.Persistence.Configurations;
using Warehouse.Infrastructure.Scanning;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IStocktakeService"/> 實作（M6-02，ADR-0018）。
/// 鎖順序（與過帳一致，不會互相等待成環）：盤點單列 → 儲位（FOR SHARE；過帳是 FOR UPDATE）→ 過帳服務內的鎖。
/// - 開始：鎖範圍內儲位 FOR SHARE，等進行中的過帳 commit，再由結存建快照行。
/// - 計數：盤點單 FOR SHARE（多人可同時盤）、該儲位 FOR SHARE，讀「此刻」結存當作計數當下帳面量。
/// - 送審、重盤、取消、核准：盤點單 FOR UPDATE（等所有計數交易結束）。
/// - 核准：產生調整單並經 <see cref="IInventoryPostingService.ApplyAsync"/> 過帳（IgnoreReservations），結存只在這裡改變。
/// </summary>
public sealed class StocktakeService(
    WarehouseDbContext db, IInventoryPostingService posting, ISettingsService settings, TimeProvider clock,
    IPostingFailureLog? failures = null) : IStocktakeService
{
    private const string ApproveCommand = "stocktake.approve";

    public async Task<PagedResult<StocktakeRow>> ListAsync(
        Guid? warehouseId, StocktakeStatus? status, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var query = db.Stocktakes.AsNoTracking();
        if (warehouseId is { } w)
        {
            query = query.Where(s => s.WarehouseId == w);
        }
        if (status is { } st)
        {
            query = query.Where(s => s.Status == st);
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(s => new StocktakeRow(
                s.Id, s.StocktakeNo, s.WarehouseId, db.Warehouses.Where(x => x.Id == s.WarehouseId).Select(x => x.Code).First(),
                s.Status, s.AllLocations, s.Lines.Count(), s.Lines.Count(l => l.CountedQuantity != null), s.CreatedByName, s.CreatedAt,
                s.ApprovedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<StocktakeRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<StocktakeDetails?> GetAsync(Guid id, StocktakeLineFilter filter, Actor viewer, CancellationToken cancellationToken)
    {
        var s = await db.Stocktakes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (s is null)
        {
            return null;
        }
        var version = await db.Stocktakes.Where(x => x.Id == id).Select(x => EF.Property<uint>(x, "Version")).SingleAsync(cancellationToken);
        var warehouseCode = await db.Warehouses.Where(w => w.Id == s.WarehouseId).Select(w => w.Code).SingleAsync(cancellationToken);
        var locationCodes = await (from sl in db.StocktakeLocations
                                   join l in db.Locations on sl.LocationId equals l.Id
                                   where sl.StocktakeId == id
                                   orderby l.Code
                                   select l.Code).ToListAsync(cancellationToken);
        var blind = (await settings.GetAsync(cancellationToken)).StocktakeBlindCount;
        // 盲盤：盤點中只有倉庫主管看得到帳面量；送審後（含核准、取消）結果公開給有權限看盤點單的人
        var hidden = blind && !viewer.IsWarehouseManager && s.Status == StocktakeStatus.Counting;

        var lines = db.Set<StocktakeLine>().AsNoTracking().Where(l => l.StocktakeId == id);
        var lineCount = await lines.CountAsync(cancellationToken);
        var uncounted = await lines.CountAsync(l => l.CountedQuantity == null || l.RecountRequested, cancellationToken);
        var differences = await lines.CountAsync(l => l.CountedQuantity != null && l.CountedQuantity != l.BookQuantityAtCount, cancellationToken);
        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
        {
            var code = ScanCodes.Location(filter.LocationCode).Code;
            lines = lines.Where(l => db.Locations.Any(x => x.Id == l.LocationId && x.Code == code));
        }
        if (filter.OnlyUncounted)
        {
            lines = lines.Where(l => l.CountedQuantity == null || l.RecountRequested);
        }
        if (filter.OnlyDifferences && !hidden)
        {
            lines = lines.Where(l => l.CountedQuantity != null && l.CountedQuantity != l.BookQuantityAtCount);
        }
        var page = Paging.ClampPage(filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, Domain.Inventory.Stocktake.MaxLines);
        var filtered = await lines.CountAsync(cancellationToken);
        var rows = await (from l in lines
                          join loc in db.Locations on l.LocationId equals loc.Id
                          join i in db.Items on l.ItemId equals i.Id
                          orderby loc.Code, l.LineNo
                          select new
                          {
                              l, LocationCode = loc.Code, ItemCode = i.ItemCode ?? i.RequestNo, i.Name,
                              Unit = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.Code).First(),
                          })
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        var result = rows.Select(x => new StocktakeLineRow(
            x.l.Id, x.l.LineNo, x.l.LocationId, x.LocationCode, x.l.ItemId, x.ItemCode, x.Name, x.Unit, x.l.LotNo, x.l.SerialNo,
            x.l.StockStatus, x.l.IsAdded,
            hidden ? null : x.l.SnapshotQuantity,
            x.l.CountedQuantity,
            hidden ? null : x.l.BookQuantityAtCount,
            hidden ? null : x.l.Difference,
            hidden || x.l.BookQuantityAtCount is null ? null : x.l.BookQuantityAtCount - x.l.SnapshotQuantity,
            x.l.CountedByName, x.l.CountedAt, x.l.RecountRequested)).ToList();
        var adjustmentNo = s.AdjustmentDocumentId is { } adj
            ? await db.StockDocuments.Where(d => d.Id == adj).Select(d => d.DocNo).SingleAsync(cancellationToken)
            : null;
        return new StocktakeDetails(
            s.Id, s.StocktakeNo, s.WarehouseId, warehouseCode, s.Status, s.AllLocations, s.Note, s.SnapshotAt, s.CreatedById,
            s.CreatedByName, s.CreatedAt, s.SubmittedByName, s.SubmittedAt, s.ApprovedByName, s.ApprovedAt, s.AdjustmentDocumentId,
            adjustmentNo, s.RecountComment, locationCodes, hidden, version,
            new PagedResult<StocktakeLineRow>(result, page, pageSize, filtered), lineCount, uncounted, hidden ? null : differences);
    }

    public async Task<Guid> StartAsync(
        Guid warehouseId, IReadOnlyList<string> locationCodes, string? note, Actor actor, CancellationToken cancellationToken,
        Guid? requestId = null)
    {
        RequireWarehouseManager(actor);
        if (requestId is { } existing && await db.Stocktakes.AsNoTracking().Where(s => s.Id == existing)
                .Select(s => (Guid?)s.CreatedById).SingleOrDefaultAsync(cancellationToken) is { } creator)
        {
            return creator == actor.Id ? existing : throw new ConflictException("stocktake.request_id_conflict", "這個請求識別碼已被使用，請重新整理頁面。");
        }
        try
        {
            return await StartCoreAsync(warehouseId, locationCodes, note, actor, requestId, cancellationToken);
        }
        catch (ConflictException ex) when (requestId is { } rid && ex.ErrorCode == "stocktake.request_id_conflict")
        {
            // 同一請求同時送兩次：後到的撞主鍵，回傳先到的那張（別人用同一個 ID 照樣拒絕，M6-02 審查 Y8）
            db.ChangeTracker.Clear();
            var owner = await db.Stocktakes.AsNoTracking().Where(s => s.Id == rid).Select(s => (Guid?)s.CreatedById).SingleOrDefaultAsync(cancellationToken);
            if (owner != actor.Id)
            {
                throw;
            }
            return rid;
        }
    }

    private Task<Guid> StartCoreAsync(
        Guid warehouseId, IReadOnlyList<string> locationCodes, string? note, Actor actor, Guid? requestId, CancellationToken cancellationToken) =>
        BusyGuardAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            // 先鎖倉庫、後鎖儲位（和刪除倉庫同一順序，避免死結；M8-01 審查 #1）
            var warehouseCode = await WarehouseLock.LockUsableAsync(db, warehouseId, "倉庫已停用，不能盤點。", cancellationToken);
            await WarehouseAccess.EnsureAsync(db, actor, warehouseId, cancellationToken);

            var all = locationCodes.Count == 0;
            var codes = locationCodes.Select(c => ScanCodes.Location(c).Code).Where(c => c.Length > 0).Distinct().ToList();
            var scope = await db.Locations.AsNoTracking()
                .Where(l => l.WarehouseId == warehouseId && l.Status != LocationStatus.Inactive && (all || codes.Contains(l.Code)))
                .Select(l => new { l.Id, l.Code }).ToListAsync(cancellationToken);
            var missing = codes.Except(scope.Select(l => l.Code)).ToList();
            if (missing.Count > 0)
            {
                throw new DomainRuleException($"倉庫 {warehouseCode} 沒有這些有效儲位：{string.Join("、", missing.Take(10))}。");
            }
            if (scope.Count == 0)
            {
                throw new DomainRuleException("盤點範圍沒有任何有效儲位。");
            }
            var locationIds = scope.Select(l => l.Id).Order().ToArray();
            // 等進行中的過帳（持有儲位 FOR UPDATE）commit，快照才包含它們
            await ExecuteAsync(db, "SELECT 1 FROM locations WHERE id = ANY(@ids) ORDER BY id FOR SHARE", cancellationToken,
                Param("ids", locationIds));
            var balances = (await QueryAsync(db,
                "SELECT count(*) FROM stock_balances WHERE location_id = ANY(@ids) AND quantity <> 0",
                r => r.GetInt64(0), cancellationToken, Param("ids", locationIds))).Single();
            if (balances > Stocktake.MaxLines)
            {
                throw new ConflictException("stocktake.too_large",
                    $"範圍內有 {balances} 筆結存，超過一張盤點單的上限 {Stocktake.MaxLines} 行；請依儲位分成多張盤點單。");
            }

            var now = clock.GetUtcNow();
            var number = (await QueryAsync(db, $"SELECT nextval('{WarehouseDbContext.StocktakeNoSequence}')", r => r.GetInt64(0), cancellationToken)).Single();
            var stocktake = Stocktake.Start(requestId ?? Guid.NewGuid(), $"ST-{number:D6}", warehouseId, all, note, actor.Id, actor.Name, now);
            db.Stocktakes.Add(stocktake);
            db.StocktakeLocations.AddRange(locationIds.Select(l => new StocktakeLocation { StocktakeId = stocktake.Id, LocationId = l }));
            await SaveAsync(cancellationToken);
            var created = await ExecuteAsync(db, """
                INSERT INTO stocktake_lines (id, stocktake_id, line_no, location_id, item_id, lot_id, serial_id, lot_no, serial_no,
                    stock_status, snapshot_quantity, is_added, recount_requested)
                SELECT gen_random_uuid(), @st,
                    row_number() OVER (ORDER BY l.code, i.item_code, lot.lot_no, s.serial_no, b.stock_status),
                    b.location_id, b.item_id, b.lot_id, b.serial_id, lot.lot_no, s.serial_no, b.stock_status, b.quantity, false, false
                FROM stock_balances b
                JOIN locations l ON l.id = b.location_id
                JOIN items i ON i.id = b.item_id
                LEFT JOIN inventory_lots lot ON lot.id = b.lot_id
                LEFT JOIN item_serials s ON s.id = b.serial_id
                WHERE b.location_id = ANY(@ids) AND b.quantity <> 0
                """, cancellationToken, Param("st", stocktake.Id), Param("ids", locationIds));
            AuditLog.Add(db, actor, now, "stocktake.start", "stocktake", stocktake.Id.ToString(), null,
                new { stocktake.StocktakeNo, WarehouseCode = warehouseCode, Locations = locationIds.Length, Lines = created, AllLocations = all });
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return stocktake.Id;
        });

    public async Task<Guid> CountAsync(Guid id, StocktakeCountInput input, Actor actor, CancellationToken cancellationToken)
    {
        RequireCounter(actor);
        return await BusyGuardAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            var stocktake = await LockForCountAsync(id, actor, cancellationToken);

            var (code, qrWarehouse) = ScanCodes.Location(input.LocationCode);
            var location = await db.Locations.AsNoTracking()
                .SingleOrDefaultAsync(l => l.WarehouseId == stocktake.WarehouseId && l.Code == code, cancellationToken)
                ?? throw new DomainRuleException($"這個倉庫沒有儲位 {code}。");
            if (qrWarehouse is not null && !await db.Warehouses.AnyAsync(w => w.Id == stocktake.WarehouseId && w.Code == qrWarehouse, cancellationToken))
            {
                throw new DomainRuleException($"掃到的是倉庫 {qrWarehouse} 的儲位 QR，與這張盤點單的倉庫不同。");
            }
            if (!await db.StocktakeLocations.AnyAsync(x => x.StocktakeId == id && x.LocationId == location.Id, cancellationToken))
            {
                throw new DomainRuleException($"儲位 {code} 不在這張盤點單的範圍內。");
            }
            var (items, display) = await ScanCodes.ItemsAsync(db, input.ItemCode, cancellationToken);
            var item = await items.AsNoTracking().Select(i => new
                {
                    i.Id, i.TrackingMode, Code = i.ItemCode ?? i.RequestNo, i.Status,
                    DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => (int)u.DecimalPlaces).First(),
                })
                .SingleOrDefaultAsync(cancellationToken) ?? throw new DomainRuleException($"找不到料號 {display}。");
            var lotNo = Upper(input.LotNo);
            var serialNo = Upper(input.SerialNo);
            if (serialNo is not null)
            {
                // 已登記的序號：批號以登記為準（只掃序號也找得到原本那一行；填了不同批號直接拒絕，M6-02 審查 3）
                var known = await db.ItemSerials.AsNoTracking().Where(s => s.ItemId == item.Id && s.SerialNo == serialNo)
                    .Select(s => new { LotNo = db.InventoryLots.Where(l => l.Id == s.LotId).Select(l => l.LotNo).FirstOrDefault() })
                    .SingleOrDefaultAsync(cancellationToken);
                if (known is not null)
                {
                    if (lotNo is not null && lotNo != known.LotNo)
                    {
                        throw new DomainRuleException($"序號 {serialNo} 登記在{(known.LotNo is null ? "沒有批號" : $"批號 {known.LotNo}")}，與輸入的批號不同。");
                    }
                    lotNo = known.LotNo;
                }
            }

            // 同一張盤點單的計數排隊（每筆只要幾毫秒）：兩台 PDA 同時掃到同一個新物料時，不會建出兩行或撞行號
            await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", cancellationToken,
                Param("key", "stocktake-count:" + id));
            await LockLocationAsync(location.Id, cancellationToken);
            var line = await db.Set<StocktakeLine>().SingleOrDefaultAsync(l => l.StocktakeId == id && l.LocationId == location.Id
                && l.ItemId == item.Id && l.LotNo == lotNo && l.SerialNo == serialNo && l.StockStatus == StockStatus.Available, cancellationToken)
                ?? await db.Set<StocktakeLine>().Where(l => l.StocktakeId == id && l.LocationId == location.Id
                    && l.ItemId == item.Id && l.LotNo == lotNo && l.SerialNo == serialNo).OrderBy(l => l.LineNo).FirstOrDefaultAsync(cancellationToken);
            var now = clock.GetUtcNow();
            if (line is null)
            {
                if (item.Status == ItemStatus.PendingApproval)
                {
                    throw new DomainRuleException($"料號 {item.Code} 還在待核准，不能盤點入帳。");
                }
                var count = await db.Set<StocktakeLine>().CountAsync(l => l.StocktakeId == id, cancellationToken);
                if (count >= Stocktake.MaxLines)
                {
                    throw new ConflictException("stocktake.too_large", $"一張盤點單最多 {Stocktake.MaxLines} 行，請另開盤點單。");
                }
                var lineNo = await db.Set<StocktakeLine>().Where(l => l.StocktakeId == id).MaxAsync(l => (int?)l.LineNo, cancellationToken) ?? 0;
                line = StocktakeLine.CreateFound(Guid.NewGuid(), id, lineNo + 1, location.Id, item.Id, item.TrackingMode, lotNo, serialNo, null, null);
                db.Add(line);
            }
            await RefreshDimensionsAsync(line, cancellationToken);
            var book = await BookQuantityAsync(line, cancellationToken);
            var before = line.CountedQuantity;
            line.Count(input.CountedQuantity, book, item.TrackingMode, item.DecimalPlaces, actor.Id, actor.Name, now);
            AuditLog.Add(db, actor, now, before is null ? "stocktake.count" : "stocktake.recount", "stocktake", id.ToString(),
                before is null ? null : new { Counted = before },
                new { LineId = line.Id, line.LineNo, Location = code, ItemCode = item.Code, lotNo, serialNo, Counted = input.CountedQuantity, Added = line.IsAdded });
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return line.Id;
        });
    }

    public Task CountLineAsync(Guid id, Guid lineId, decimal countedQuantity, Actor actor, CancellationToken cancellationToken)
    {
        RequireCounter(actor);
        return BusyGuardAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            await LockForCountAsync(id, actor, cancellationToken);
            var line = await db.Set<StocktakeLine>().SingleOrDefaultAsync(l => l.Id == lineId && l.StocktakeId == id, cancellationToken)
                ?? throw new NotFoundException("stocktake.line_not_found", "找不到這行盤點明細。");
            var item = await db.Items.AsNoTracking().Where(i => i.Id == line.ItemId).Select(i => new
                {
                    i.TrackingMode, Code = i.ItemCode ?? i.RequestNo,
                    DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => (int)u.DecimalPlaces).First(),
                }).SingleAsync(cancellationToken);
            await LockLocationAsync(line.LocationId, cancellationToken);
            await RefreshDimensionsAsync(line, cancellationToken);
            var book = await BookQuantityAsync(line, cancellationToken);
            var before = line.CountedQuantity;
            var now = clock.GetUtcNow();
            line.Count(countedQuantity, book, item.TrackingMode, item.DecimalPlaces, actor.Id, actor.Name, now);
            AuditLog.Add(db, actor, now, before is null ? "stocktake.count" : "stocktake.recount", "stocktake", id.ToString(),
                before is null ? null : new { Counted = before },
                new { LineId = line.Id, line.LineNo, ItemCode = item.Code, line.LotNo, line.SerialNo, Counted = countedQuantity });
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public Task SubmitAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, actor, (stocktake, now) =>
        {
            RequireCounter(actor);
            stocktake.Submit(actor.Id, actor.Name, now);
            return ("stocktake.submit", new { Lines = stocktake.Lines.Count, Differences = stocktake.Lines.Count(l => l.Difference != 0) });
        }, cancellationToken);

    public Task RequestRecountAsync(
        Guid id, uint expectedVersion, IReadOnlyList<Guid> lineIds, string comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, actor, (stocktake, now) =>
        {
            RequireWarehouseManager(actor);
            stocktake.RequestRecount(lineIds.ToHashSet(), comment);
            return ("stocktake.recount_request", new { Lines = lineIds.Count, Comment = comment });
        }, cancellationToken);

    public Task CancelAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, actor, (stocktake, now) =>
        {
            RequireWarehouseManager(actor);
            stocktake.Cancel();
            return ("stocktake.cancel", null);
        }, cancellationToken, releaseLocations: true);

    public async Task<Guid?> ApproveAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken)
    {
        var key = DocumentSupport.NormalizeKey(idempotencyKey);
        var requestHash = DocumentSupport.Hash($"{ApproveCommand}|{id}|{expectedVersion}|{actor.Id}");
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, ApproveCommand, requestHash, cancellationToken))
        {
            return await AdjustmentOfAsync(id, cancellationToken);
        }
        try
        {
            return await BusyGuardAsync(() => ApproveCoreAsync(id, expectedVersion, key, requestHash, actor, cancellationToken));
        }
        catch (Exception ex) when (failures is not null && ex is AppException or DomainRuleException)
        {
            // 被拒絕的核准另外記錄（交易已回滾；獨立連線寫入，M4-05），documentId 欄位存盤點單 ID
            await failures.RecordAsync(ApproveCommand, id, actor, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<Guid?> ApproveCoreAsync(
        Guid id, uint expectedVersion, string key, string requestHash, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
        await ExecuteAsync(db, "SELECT 1 FROM stocktakes WHERE id = @id FOR UPDATE", cancellationToken, Param("id", id));
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, ApproveCommand, requestHash, cancellationToken))
        {
            return await AdjustmentOfAsync(id, cancellationToken);
        }
        var stocktake = await LoadAsync(id, expectedVersion, actor, cancellationToken);
        RequireWarehouseManager(actor);
        var current = await settings.GetAsync(cancellationToken);
        stocktake.EnsureCanApprove(actor.Id, current.AllowSelfApproveStocktake);
        var now = clock.GetUtcNow();

        var diffs = stocktake.Lines.Where(l => l.Difference is { } d && d != 0).OrderBy(l => l.LineNo).ToList();
        var duplicate = diffs.GroupBy(l => (l.LocationId, l.ItemId, l.LotNo, l.SerialNo)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainRuleException($"第 {string.Join("、", duplicate.Select(l => l.LineNo))} 行是同一物料在同一儲位、不同結存狀態的差異，請要求重盤其中一行，或改用移庫處理。");
        }
        Guid? adjustmentId = null;
        if (diffs.Count > 0)
        {
            var itemIds = diffs.Select(l => l.ItemId).Distinct().ToList();
            var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
                .Select(i => new { i.Id, i.TrackingMode, DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => (int)u.DecimalPlaces).First() })
                .ToDictionaryAsync(i => i.Id, cancellationToken);
            var number = (await QueryAsync(db, $"SELECT nextval('{WarehouseDbContext.AdjustmentDocNoSequence}')", r => r.GetInt64(0), cancellationToken)).Single();
            var doc = StockDocument.CreateAdjustment(Guid.NewGuid(), $"ADJ-{number:D6}", stocktake.WarehouseId,
                $"盤點 {stocktake.StocktakeNo}", actor.Id, actor.Name, now);
            var statusOf = new Dictionary<Guid, StockStatus>();
            foreach (var line in diffs)
            {
                var item = items[line.ItemId];
                var docLine = doc.AddAdjustmentLine(Guid.NewGuid(), line.ItemId, item.TrackingMode, item.DecimalPlaces, line.LocationId,
                    line.LotNo, line.SerialNo, Math.Abs(line.Difference!.Value), line.Difference < 0);
                statusOf[docLine.Id] = line.StockStatus;
            }
            db.StockDocuments.Add(doc);
            await SaveAsync(cancellationToken);

            // 批號、序號：盤盈的新號碼在這裡建立（與進倉相同的批次 upsert）；既有號碼取回原 ID
            var lotIds = await DocumentSupport.ResolveLotsAsync(db, doc.Lines, create: true, now, cancellationToken);
            var serialIds = await DocumentSupport.ResolveSerialsAsync(db, doc.Lines, lotIds, create: true, now, cancellationToken);
            await DocumentSupport.FillLineDimensionsAsync(db, lotIds, serialIds, cancellationToken);
            var changes = doc.Lines.Select(l => new StockChange(
                l.Id, l.ItemId!.Value, l.LocationId,
                lotIds.TryGetValue(l.Id, out var lot) ? lot : null,
                serialIds.TryGetValue(l.Id, out var serial) ? serial : null,
                statusOf[l.Id], l.IsDecrease ? -l.Quantity : l.Quantity)).ToList();
            // 鎖順序與其他過帳相同：儲位依 id FOR UPDATE 在過帳服務內（ADR-0004）
            await posting.ApplyAsync(doc.Id, changes, actor, now, cancellationToken, new StockApplyOptions(IgnoreReservations: true));
            doc.PostAdjustment(actor.Id, actor.Name, now);
            adjustmentId = doc.Id;
            AuditLog.Add(db, actor, now, "document.adjustment_post", "stock_document", doc.Id.ToString(), null,
                new { doc.DocNo, Stocktake = stocktake.StocktakeNo, Lines = doc.Lines.Count });
        }
        stocktake.Approve(actor.Id, actor.Name, current.AllowSelfApproveStocktake, adjustmentId, now);
        await ExecuteAsync(db, "UPDATE stocktake_locations SET is_open = false WHERE stocktake_id = @id", cancellationToken, Param("id", id));
        AuditLog.Add(db, actor, now, "stocktake.approve", "stocktake", id.ToString(), null,
            new { stocktake.StocktakeNo, Differences = diffs.Count, AdjustmentDocumentId = adjustmentId });
        db.ProcessedCommands.Add(ProcessedCommand.Create(key, ApproveCommand, requestHash, id.ToString(), now));
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return adjustmentId;
    }

    private async Task<Guid?> AdjustmentOfAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Stocktakes.AsNoTracking().Where(s => s.Id == id).Select(s => s.AdjustmentDocumentId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>送審、重盤、取消的共用流程（鎖盤點單 → 載入 → 範圍 → 版本 → 動作 → 稽核 → 儲存）。</summary>
    private Task MutateAsync(
        Guid id, uint expectedVersion, Actor actor, Func<Stocktake, DateTimeOffset, (string Action, object? After)> action,
        CancellationToken cancellationToken, bool releaseLocations = false) =>
        BusyGuardAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            await ExecuteAsync(db, "SELECT 1 FROM stocktakes WHERE id = @id FOR UPDATE", cancellationToken, Param("id", id));
            var stocktake = await LoadAsync(id, expectedVersion, actor, cancellationToken);
            var now = clock.GetUtcNow();
            var (name, after) = action(stocktake, now);
            if (releaseLocations)
            {
                await ExecuteAsync(db, "UPDATE stocktake_locations SET is_open = false WHERE stocktake_id = @id", cancellationToken, Param("id", id));
            }
            AuditLog.Add(db, actor, now, name, "stocktake", id.ToString(), null, after);
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

    private async Task<Stocktake> LoadAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken)
    {
        var stocktake = await db.Stocktakes.Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException("stocktake.not_found", "找不到盤點單。");
        await WarehouseAccess.EnsureAsync(db, actor, stocktake.WarehouseId, cancellationToken);
        if (db.Entry(stocktake).Property<uint>("Version").CurrentValue != expectedVersion)
        {
            throw new ConcurrencyConflictException("stocktake.concurrency_conflict", "盤點單已被他人修改，請重新載入後再操作。");
        }
        return stocktake;
    }

    /// <summary>計數：盤點單 FOR SHARE（送審、核准要等計數結束），檢查狀態與倉庫範圍。</summary>
    private async Task<Stocktake> LockForCountAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, "SELECT 1 FROM stocktakes WHERE id = @id FOR SHARE", cancellationToken, Param("id", id));
        var stocktake = await db.Stocktakes.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException("stocktake.not_found", "找不到盤點單。");
        await WarehouseAccess.EnsureAsync(db, actor, stocktake.WarehouseId, cancellationToken);
        stocktake.EnsureCounting();
        return stocktake;
    }

    /// <summary>儲位 FOR SHARE：等這個儲位進行中的過帳 commit，之後讀到的結存就是「計數當下」的帳面量。</summary>
    private Task LockLocationAsync(Guid locationId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, "SELECT 1 FROM locations WHERE id = @id FOR SHARE", cancellationToken, Param("id", locationId));

    /// <summary>現場找到的行：每次計數都重查號碼對應的批號／序號 ID（號碼可能在建立這行之後才因進倉而存在）。</summary>
    private async Task RefreshDimensionsAsync(StocktakeLine line, CancellationToken cancellationToken)
    {
        if (!line.IsAdded)
        {
            return;
        }
        var lotId = line.LotNo is null ? null : await db.InventoryLots.Where(l => l.ItemId == line.ItemId && l.LotNo == line.LotNo)
            .Select(l => (Guid?)l.Id).SingleOrDefaultAsync(cancellationToken);
        var serialId = line.SerialNo is null ? null : await db.ItemSerials.Where(s => s.ItemId == line.ItemId && s.SerialNo == line.SerialNo)
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken);
        line.RefreshDimensions(lotId, serialId);
    }

    private async Task<decimal> BookQuantityAsync(StocktakeLine line, CancellationToken cancellationToken)
    {
        if ((line.LotNo is not null && line.LotId is null) || (line.SerialNo is not null && line.SerialId is null))
        {
            return 0m; // 新批號／序號：帳上一定沒有
        }
        return (await QueryAsync(db, """
            SELECT COALESCE(SUM(quantity), 0) FROM stock_balances
            WHERE item_id = @item AND location_id = @loc AND lot_id IS NOT DISTINCT FROM @lot
              AND serial_id IS NOT DISTINCT FROM @serial AND stock_status = @status
            """, r => r.GetDecimal(0), cancellationToken,
            Param("item", line.ItemId), Param("loc", line.LocationId), Uuid("lot", line.LotId), Uuid("serial", line.SerialId),
            Param("status", line.StockStatus.ToString()))).Single();
    }

    private static string? Upper(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.ToUpperInvariant();
    }

    private static void RequireWarehouseManager(Actor actor)
    {
        if (!actor.IsWarehouseManager)
        {
            throw new PermissionDeniedException("stocktake.permission_denied", "只有倉庫主管可以執行這個動作。");
        }
    }

    /// <summary>
    /// 只擋系統帳號。Actor 只帶物料管理員、倉庫主管兩個旗標，沒有「作業員」資訊：盤點的角色限制（作業員或倉庫主管）
    /// 由端點／頁面的政策 Stocktake.Count 負責；新增其他入口（API、背景工作）時必須套用同一政策（M6-02 審查 Y3）。
    /// </summary>
    private static void RequireCounter(Actor actor)
    {
        if (actor.Id == Guid.Empty)
        {
            throw new PermissionDeniedException("stocktake.permission_denied", "系統帳號不能盤點。");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("stocktake.concurrency_conflict", "盤點單已被他人修改，請重新載入後再操作。");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw pg.ConstraintName switch
            {
                "ux_stocktake_locations_open" => new ConflictException("stocktake.location_busy", "有儲位已在其他未結束的盤點單中，請先完成或取消那張盤點。"),
                "ux_stocktake_lines_key" => new ConflictException("stocktake.line_exists", "這個儲位已經有相同物料、批號／序號的盤點行，請再試一次。"),
                "pk_stocktakes" => new ConflictException("stocktake.request_id_conflict", "這個請求識別碼已被使用，請重新整理頁面。"),
                "pk_processed_commands" => new ConflictException("idempotency.key_reused", "這個請求識別碼已被使用。"),
                _ => ex,
            };
        }
    }

    private static async Task<T> BusyGuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConcurrencyConflictException("stocktake.busy", "有其他人同時在處理相同的盤點單或儲位，請稍後再試一次。");
        }
    }

    private static async Task BusyGuardAsync(Func<Task> action) =>
        await BusyGuardAsync(async () => { await action(); return true; });
}
