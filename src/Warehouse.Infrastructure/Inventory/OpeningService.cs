using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Settings;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IOpeningService"/> 實作（M3-03）。每個寫入動作：開交易 → 鎖單據列（FOR UPDATE）→ 檢查狀態與權限 → 修改 → 稽核 → commit。
/// 核准（過帳）的完整步驟見 <see cref="ApproveAsync"/>。
/// </summary>
public sealed class OpeningService(
    WarehouseDbContext db, IInventoryPostingService posting, ISettingsService settings, TimeProvider clock) : IOpeningService
{
    private const string CommandType = "opening.approve";

    public async Task<PagedResult<OpeningRow>> ListAsync(
        Guid? warehouseId, StockDocumentStatus? status, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var docs = db.StockDocuments.AsNoTracking().Where(d => d.DocType == StockDocumentType.Opening);
        if (warehouseId is { } w)
        {
            docs = docs.Where(d => d.WarehouseId == w);
        }
        if (status is { } s)
        {
            docs = docs.Where(d => d.Status == s);
        }
        var total = await docs.CountAsync(cancellationToken);
        var rows = await docs
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * Paging.DefaultPageSize)
            .Take(Paging.DefaultPageSize)
            .Select(d => new OpeningRow(
                d.Id, d.DocNo, db.Warehouses.Where(x => x.Id == d.WarehouseId).Select(x => x.Code).First(), d.Status,
                d.Lines.Count, d.CreatedByName, d.CreatedAt, d.ApprovedByName, d.PostedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<OpeningRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<OpeningDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await db.StockDocuments.AsNoTracking()
            .Where(d => d.Id == id && d.DocType == StockDocumentType.Opening)
            .Select(d => new
            {
                Doc = d,
                Version = EF.Property<uint>(d, "Version"),
                WarehouseCode = db.Warehouses.Where(x => x.Id == d.WarehouseId).Select(x => x.Code).First(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (doc is null)
        {
            return null;
        }
        var lines = await (
            from l in db.Set<StockDocumentLine>().AsNoTracking()
            where l.DocumentId == id
            join loc in db.Locations on l.LocationId equals loc.Id
            from item in db.Items.Where(i => i.Id == l.ItemId).DefaultIfEmpty()
            from unit in db.Units.Where(u => u.Id == item.BaseUnitId).DefaultIfEmpty()
            orderby l.LineNo
            select new OpeningLineRow(
                l.Id, l.LineNo, l.LocationId, loc.Code, l.ItemId, item.ItemCode, item.Name, unit.Code,
                (TrackingMode?)item.TrackingMode, l.LotNo, l.SerialNo, l.ExpiryDate, l.Quantity))
            .ToListAsync(cancellationToken);

        var d = doc.Doc;
        return new OpeningDetails(
            d.Id, d.DocNo, d.WarehouseId, doc.WarehouseCode, d.Status, d.Note, d.CreatedById, d.CreatedByName, d.CreatedAt,
            d.SubmittedAt, d.ApprovedByName, d.PostedAt, d.ReturnComment, doc.Version, lines);
    }

    public async Task<Guid> CreateAsync(Guid warehouseId, string? note, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken)
            ?? throw new NotFoundException("warehouse.not_found", "找不到倉庫。");
        if (!warehouse.IsActive)
        {
            throw new DomainRuleException("倉庫已停用，不能建立期初單。");
        }
        var number = (await QueryAsync(db, $"SELECT nextval('{WarehouseDbContext.OpeningDocNoSequence}')",
            r => r.GetInt64(0), cancellationToken)).Single();
        var now = clock.GetUtcNow();
        var doc = StockDocument.CreateOpening(Guid.NewGuid(), $"OB-{number:D6}", warehouseId, note, actor.Id, actor.Name, now);
        db.StockDocuments.Add(doc);
        AuditLog.Add(db, actor, now, "opening.create", "stock_document", doc.Id.ToString(), null,
            new { doc.DocNo, WarehouseCode = warehouse.Code, doc.Note });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return doc.Id;
    }

    public Task AddLineAsync(Guid documentId, OpeningLineInput input, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(documentId, null, actor, editorOnly: true, cancellationToken, async (doc, now) =>
        {
            var location = await FindLocationAsync(doc.WarehouseId, input.LocationCode, cancellationToken);
            var lineId = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(input.ItemCode))
            {
                doc.AddEmptyLocation(lineId, location.Id);
                return ("opening.line_add", new { Location = location.Code, Empty = true });
            }

            var code = TextNormalizer.Normalize(input.ItemCode);
            var item = await db.Items.AsNoTracking()
                .Where(i => i.ItemCode == code)
                .Select(i => new
                {
                    i.Id, i.Status, i.TrackingMode,
                    DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => (int)u.DecimalPlaces).First(),
                })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new DomainRuleException($"找不到料號 {code}。");
            if (item.Status != ItemStatus.Active)
            {
                throw new DomainRuleException($"料號 {code} 不是有效狀態，不能建帳。");
            }
            doc.AddLine(lineId, item.Id, item.TrackingMode, item.DecimalPlaces, location.Id,
                input.LotNo, input.SerialNo, input.ExpiryDate, input.Quantity);
            return ("opening.line_add", new { Location = location.Code, ItemCode = code, input.LotNo, input.SerialNo, input.Quantity });
        });

    public Task RemoveLineAsync(Guid documentId, Guid lineId, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(documentId, null, actor, editorOnly: true, cancellationToken, (doc, now) =>
        {
            doc.RemoveLine(lineId);
            return Task.FromResult<(string, object?)>(("opening.line_remove", new { LineId = lineId }));
        });

    public Task SubmitAsync(Guid documentId, uint expectedVersion, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(documentId, expectedVersion, actor, editorOnly: true, cancellationToken, (doc, now) =>
        {
            doc.Submit(now);
            return Task.FromResult<(string, object?)>(("opening.submit", null));
        });

    public Task ReturnAsync(Guid documentId, uint expectedVersion, string comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(documentId, expectedVersion, actor, editorOnly: false, cancellationToken, (doc, now) =>
        {
            RequireWarehouseManager(actor);
            doc.Return(comment);
            return Task.FromResult<(string, object?)>(("opening.return", new { Comment = comment }));
        });

    public Task CancelAsync(Guid documentId, uint expectedVersion, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(documentId, expectedVersion, actor, editorOnly: true, cancellationToken, (doc, now) =>
        {
            doc.Cancel();
            return Task.FromResult<(string, object?)>(("opening.cancel", null));
        });

    /// <summary>
    /// 核准＝過帳。全部在同一個交易：
    /// 1. 冪等：同一鍵已處理過且內容相同 → 直接回傳（重送、連點、逾時重試都只過帳一次）；內容不同 → 拒絕。
    /// 2. 鎖單據列，再檢查一次冪等（兩個同鍵請求同時送達時，後到的在這裡看到前者的結果）。
    /// 3. 檢查狀態、版本、權限、D-07 自核設定。
    /// 4. 依 id 排序鎖儲位列（避免互相等待）、共享鎖倉庫列：重新確認儲位屬於這個倉庫、沒停用、沒做過期初，倉庫仍啟用。
    /// 5. 批號、序號依（物料, 號碼）排序後 upsert（沒有就建立）；結存一律 Available，可用與否看儲位狀態。
    /// 6. 交給 <see cref="IInventoryPostingService"/> 更新結存、寫異動帳。
    /// 7. 標記儲位已期初、單據已過帳、寫稽核與冪等紀錄，commit。
    /// </summary>
    public async Task ApproveAsync(
        Guid documentId, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken)
    {
        try
        {
            await ApproveCoreAsync(documentId, expectedVersion, idempotencyKey, actor, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable)
        {
            // 同時有其他人在處理相同的儲位、批號或序號；交易已回滾，重試即可
            throw new ConcurrencyConflictException("opening.busy", "有其他人同時在處理相同的儲位或批號，請稍後再按一次核准。");
        }
    }

    private async Task ApproveCoreAsync(
        Guid documentId, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken)
    {
        var key = DocumentSupport.NormalizeKey(idempotencyKey);
        // 內容含操作者：別人拿同一個鍵送同一張單，不會被當成「自己已經做過」（M3-03 審查 #5）
        var requestHash = DocumentSupport.Hash($"{CommandType}|{documentId}|{expectedVersion}|{actor.Id}");
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, CommandType, requestHash, cancellationToken))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // 等鎖最多 5 秒，不無限等待（逾時轉成「請稍後再試」）
        await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
        await LockDocumentAsync(documentId, cancellationToken);
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, CommandType, requestHash, cancellationToken))
        {
            return;
        }
        var doc = await LoadAsync(documentId, expectedVersion, cancellationToken);
        RequireWarehouseManager(actor);
        var now = clock.GetUtcNow();
        var allowSelfApprove = (await settings.GetAsync(cancellationToken)).AllowSelfApproveOpening;
        doc.MarkPosted(actor.Id, actor.Name, allowSelfApprove, now); // 先檢查狀態與自核，失敗就不做後面的事

        var locationIds = doc.Lines.Select(l => l.LocationId).Distinct().OrderBy(id => id).ToArray();
        var locations = await QueryAsync(db,
            "SELECT id, warehouse_id, status, opening_document_id FROM locations WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
            r => new
            {
                Id = r.GetGuid(0), WarehouseId = r.GetGuid(1), Status = Enum.Parse<LocationStatus>(r.GetString(2)),
                Opened = !r.IsDBNull(3),
            },
            cancellationToken, Param("ids", locationIds));
        var warehouseActive = (await QueryAsync(db, "SELECT is_active FROM warehouses WHERE id = @id FOR SHARE",
            r => r.GetBoolean(0), cancellationToken, Param("id", doc.WarehouseId))).Single();
        if (!warehouseActive)
        {
            throw new DomainRuleException("倉庫已停用，不能過帳。");
        }
        var codes = await db.Locations.AsNoTracking().Where(l => locationIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);
        foreach (var location in locations)
        {
            if (location.WarehouseId != doc.WarehouseId)
            {
                throw new DomainRuleException($"儲位 {codes[location.Id]} 不屬於這張單的倉庫。");
            }
            if (location.Status == LocationStatus.Inactive)
            {
                throw new DomainRuleException($"儲位 {codes[location.Id]} 已停用，不能過帳。");
            }
            if (location.Opened)
            {
                throw new DomainRuleException($"儲位 {codes[location.Id]} 已經做過期初；更正請用盤點或調整單（M6）。");
            }
        }

        var itemIds = doc.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList();
        var inactive = await db.Items.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id) && i.Status != ItemStatus.Active)
            .Select(i => i.ItemCode ?? i.RequestNo)
            .ToListAsync(cancellationToken);
        if (inactive.Count > 0)
        {
            throw new DomainRuleException($"物料 {string.Join("、", inactive)} 已不是有效狀態，不能過帳。");
        }

        // 批號、序號依（物料, 號碼）排序後才 upsert：兩張單同時核准、批號順序相反時，才不會互相等待（M3-03 審查 #1）
        var postingLines = doc.Lines.Where(l => l.ItemId is not null && l.Quantity > 0).ToList();
        var lotIds = new Dictionary<Guid, Guid>();
        foreach (var line in postingLines.Where(l => l.LotNo is not null)
                     .OrderBy(l => l.ItemId).ThenBy(l => l.LotNo, StringComparer.Ordinal))
        {
            lotIds[line.Id] = await DocumentSupport.ResolveLotAsync(db, line, now, cancellationToken);
        }
        var serialIds = new Dictionary<Guid, Guid>();
        foreach (var line in postingLines.Where(l => l.SerialNo is not null)
                     .OrderBy(l => l.ItemId).ThenBy(l => l.SerialNo, StringComparer.Ordinal))
        {
            Guid? lotOfSerial = lotIds.TryGetValue(line.Id, out var lot) ? lot : null; // 沒有批號是 null，不是 Guid.Empty
            serialIds[line.Id] = await DocumentSupport.ResolveSerialAsync(db, line, lotOfSerial, now, cancellationToken);
        }

        var changes = new List<StockChange>();
        foreach (var line in postingLines)
        {
            Guid? lotId = lotIds.TryGetValue(line.Id, out var l) ? l : null;
            Guid? serialId = serialIds.TryGetValue(line.Id, out var s) ? s : null;
            await ExecuteAsync(db, "UPDATE stock_document_lines SET lot_id = @lot, serial_id = @serial WHERE id = @id",
                cancellationToken, Uuid("lot", lotId), Uuid("serial", serialId), Param("id", line.Id));
            // 期初一律寫 Available；是否可用由儲位目前狀態決定（ADR-0014 決策 7，M3-03 審查 #4）
            changes.Add(new StockChange(line.Id, line.ItemId!.Value, line.LocationId, lotId, serialId, StockStatus.Available, line.Quantity));
        }
        await posting.ApplyAsync(doc.Id, changes, actor, now, cancellationToken);
        await ExecuteAsync(db, "UPDATE locations SET opening_document_id = @doc WHERE id = ANY(@ids)",
            cancellationToken, Param("doc", doc.Id), Param("ids", locationIds));

        AuditLog.Add(db, actor, now, "opening.approve", "stock_document", doc.Id.ToString(), null,
            new { doc.DocNo, Lines = doc.Lines.Count, Locations = locationIds.Length });
        db.ProcessedCommands.Add(ProcessedCommand.Create(key, CommandType, requestHash, doc.Id.ToString(), now));
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>修改單據的共用流程（鎖單據 → 載入 → 版本 → 權限 → 動作 → 稽核 → 儲存）。</summary>
    private async Task MutateAsync(
        Guid documentId, uint? expectedVersion, Actor actor, bool editorOnly, CancellationToken cancellationToken,
        Func<StockDocument, DateTimeOffset, Task<(string Action, object? After)>> action)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockDocumentAsync(documentId, cancellationToken);
        var doc = await LoadAsync(documentId, expectedVersion, cancellationToken);
        if (editorOnly && !actor.IsWarehouseManager && doc.CreatedById != actor.Id)
        {
            throw new PermissionDeniedException("opening.permission_denied", "只有建立者或倉庫主管可以修改這張期初單。");
        }
        var now = clock.GetUtcNow();
        var (name, after) = await action(doc, now);
        AuditLog.Add(db, actor, now, name, "stock_document", doc.Id.ToString(), null, after);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task LockDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var found = await ExecuteAsync(db, "SELECT 1 FROM stock_documents WHERE id = @id FOR UPDATE", cancellationToken,
            Param("id", documentId));
        _ = found; // 不存在的單在下一步 LoadAsync 回報
    }

    private async Task<StockDocument> LoadAsync(Guid documentId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var doc = await db.StockDocuments.Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.Id == documentId && d.DocType == StockDocumentType.Opening, cancellationToken)
            ?? throw new NotFoundException("opening.not_found", "找不到期初單。");
        if (expectedVersion is { } v && db.Entry(doc).Property<uint>("Version").CurrentValue != v)
        {
            throw new ConcurrencyConflictException("opening.concurrency_conflict", "期初單已被他人修改，請重新載入後再操作。");
        }
        return doc;
    }

    private async Task<Location> FindLocationAsync(Guid warehouseId, string locationCode, CancellationToken cancellationToken)
    {
        var code = TextNormalizer.Normalize(locationCode);
        var location = await db.Locations.AsNoTracking()
            .SingleOrDefaultAsync(l => l.WarehouseId == warehouseId && l.Code == code, cancellationToken)
            ?? throw new DomainRuleException($"這個倉庫沒有儲位 {code}。");
        if (location.Status == LocationStatus.Inactive)
        {
            throw new DomainRuleException($"儲位 {code} 已停用。");
        }
        if (location.OpeningDocumentId is not null)
        {
            throw new DomainRuleException($"儲位 {code} 已經做過期初。");
        }
        return location;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("opening.concurrency_conflict", "期初單已被他人修改，請重新載入後再操作。");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw pg.ConstraintName switch
            {
                "ux_stock_document_lines_key" => new DomainRuleException("這張單已經有相同儲位、物料、批號／序號的明細。"),
                "pk_processed_commands" => new ConflictException("idempotency.key_reused", "這個請求識別碼已被使用。"),
                _ => new DomainRuleException("資料重複。"),
            };
        }
    }

    private static void RequireWarehouseManager(Actor actor)
    {
        if (!actor.IsWarehouseManager)
        {
            throw new PermissionDeniedException("opening.permission_denied", "只有倉庫主管可以核准或退回期初單。");
        }
    }

}
