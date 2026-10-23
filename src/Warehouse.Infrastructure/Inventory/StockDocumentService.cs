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
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Scanning;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IStockDocumentService"/> 實作（M4-09～11，ADR-0015）。
/// 修改草稿：開交易 → 鎖單據列 → 版本與權限 → 修改 → 稽核 → commit（同期初）。
/// 過帳（確認或核准）的步驟見 <see cref="PostCoreAsync"/>；結存只經 <see cref="IInventoryPostingService"/> 改變。
/// </summary>
public sealed class StockDocumentService(
    WarehouseDbContext db, IInventoryPostingService posting, ISettingsService settings, TimeProvider clock,
    IPostingFailureLog? failures = null) : IStockDocumentService
{
    /// <summary>出倉單送審時建立的預留來源類型（ADR-0004 決策三）。</summary>
    public const string ReservationReference = "StockDocument";

    private const string SubmitCommand = "document.submit";
    private const string ConfirmCommand = "document.confirm";
    private const string ApproveCommand = "document.approve";

    public async Task<PagedResult<DocumentRow>> ListAsync(
        StockDocumentType? type, StockDocumentStatus? status, Guid? warehouseId, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var docs = db.StockDocuments.AsNoTracking().Where(d => d.DocType != StockDocumentType.Opening);
        if (type is { } t)
        {
            docs = docs.Where(d => d.DocType == t);
        }
        if (status is { } s)
        {
            docs = docs.Where(d => d.Status == s);
        }
        if (warehouseId is { } w)
        {
            docs = docs.Where(d => d.WarehouseId == w);
        }
        var total = await docs.CountAsync(cancellationToken);
        var rows = await docs
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * Paging.DefaultPageSize)
            .Take(Paging.DefaultPageSize)
            .Select(d => new DocumentRow(
                d.Id, d.DocNo, d.DocType, d.Status,
                db.Warehouses.Where(x => x.Id == d.WarehouseId).Select(x => x.Code).First(),
                db.DocumentPurposes.Where(p => p.Id == d.PurposeId).Select(p => p.Name).FirstOrDefault(),
                db.BusinessPartners.Where(p => p.Id == d.PartnerId).Select(p => p.Name).FirstOrDefault(),
                d.ExternalNo, d.Lines.Count, d.CreatedByName, d.CreatedAt, d.PostedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<DocumentRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<DocumentDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var head = await db.StockDocuments.AsNoTracking()
            .Where(d => d.Id == id && d.DocType != StockDocumentType.Opening)
            .Select(d => new
            {
                Doc = d,
                Version = EF.Property<uint>(d, "Version"),
                WarehouseCode = db.Warehouses.Where(x => x.Id == d.WarehouseId).Select(x => x.Code).First(),
                PurposeName = db.DocumentPurposes.Where(p => p.Id == d.PurposeId).Select(p => p.Name).FirstOrDefault(),
                PartnerName = db.BusinessPartners.Where(p => p.Id == d.PartnerId).Select(p => p.Name).FirstOrDefault(),
                ReversalOfDocNo = db.StockDocuments.Where(o => o.Id == d.ReversalOfDocumentId).Select(o => o.DocNo).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }
        var lines = await (
            from l in db.Set<StockDocumentLine>().AsNoTracking()
            where l.DocumentId == id
            join loc in db.Locations on l.LocationId equals loc.Id
            join item in db.Items on l.ItemId equals item.Id
            join unit in db.Units on item.BaseUnitId equals unit.Id
            from target in db.Locations.Where(x => x.Id == l.TargetLocationId).DefaultIfEmpty()
            from targetWarehouse in db.Warehouses.Where(x => x.Id == target.WarehouseId).DefaultIfEmpty()
            orderby l.LineNo
            select new DocumentLineRow(
                l.Id, l.LineNo, l.LocationId, loc.Code, l.TargetLocationId, targetWarehouse.Code, target.Code,
                item.Id, item.ItemCode, item.Name, unit.Code, item.TrackingMode,
                l.LotNo, l.SerialNo, l.ExpiryDate, l.Quantity, l.ReservationId, l.IsDecrease))
            .ToListAsync(cancellationToken);

        var d = head.Doc;
        return new DocumentDetails(
            d.Id, d.DocNo, d.DocType, d.Status, d.WarehouseId, head.WarehouseCode, d.PurposeId, head.PurposeName,
            d.PartnerId, head.PartnerName, d.ExternalNo, d.Note, d.ReversalOfDocumentId, head.ReversalOfDocNo,
            d.CreatedById, d.CreatedByName, d.CreatedAt, d.SubmittedAt, d.ApprovedByName, d.PostedAt, d.ReturnComment,
            head.Version, lines);
    }

    public async Task<Guid> CreateAsync(
        StockDocumentType type, Guid warehouseId, StockDocumentHeader header, Actor actor, CancellationToken cancellationToken,
        Guid? requestId = null)
    {
        if (requestId is { } existingId && await SameRequestAsync(existingId, actor, cancellationToken))
        {
            return existingId;
        }
        try
        {
            return await CreateCoreAsync(type, warehouseId, header, actor, requestId ?? Guid.NewGuid(), cancellationToken);
        }
        catch (ConflictException ex) when (requestId is not null && ex.ErrorCode == "document.request_id_conflict")
        {
            // 同一個請求同時送兩次：後到的撞主鍵，回傳先到的那張（別人用同一個 ID 則照樣拒絕）
            if (await SameRequestAsync(requestId.Value, actor, cancellationToken))
            {
                return requestId.Value;
            }
            throw;
        }
    }

    /// <summary>這個 ID 的單據已存在：同一位建立者＝重送（true）；別人用同一個 ID＝拒絕。</summary>
    private async Task<bool> SameRequestAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var creator = await db.StockDocuments.AsNoTracking().Where(d => d.Id == id).Select(d => (Guid?)d.CreatedById)
            .SingleOrDefaultAsync(cancellationToken);
        return creator is null
            ? false
            : creator == actor.Id ? true : throw new ConflictException("document.request_id_conflict", "這個請求識別碼已被使用，請重新整理頁面。");
    }

    private async Task<Guid> CreateCoreAsync(
        StockDocumentType type, Guid warehouseId, StockDocumentHeader header, Actor actor, Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken)
            ?? throw new NotFoundException("warehouse.not_found", "找不到倉庫。");
        if (!warehouse.IsActive)
        {
            throw new DomainRuleException("倉庫已停用，不能建立單據。");
        }
        await WarehouseAccess.EnsureAsync(db, actor, warehouseId, cancellationToken);
        var current = await settings.GetAsync(cancellationToken);
        var effective = await EffectiveHeaderAsync(type, header, null, current, cancellationToken);
        var now = clock.GetUtcNow();
        var doc = StockDocument.CreateMovement(
            id, await NextDocNoAsync(type, cancellationToken), type, warehouseId, effective, null, actor.Id, actor.Name, now);
        db.StockDocuments.Add(doc);
        AuditLog.Add(db, actor, now, "document.create", "stock_document", doc.Id.ToString(), null,
            new { doc.DocNo, DocType = type.ToString(), WarehouseCode = warehouse.Code, effective.PurposeId, effective.PartnerId, doc.ExternalNo, doc.Note });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return doc.Id;
    }

    public Task UpdateHeaderAsync(Guid id, uint expectedVersion, StockDocumentHeader header, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, actor, editorOnly: true, cancellationToken, async (doc, now) =>
        {
            var current = await settings.GetAsync(cancellationToken);
            var effective = await EffectiveHeaderAsync(doc.DocType, header, doc, current, cancellationToken);
            doc.UpdateHeader(effective);
            return ("document.header_update", new { effective.PurposeId, effective.PartnerId, doc.ExternalNo, doc.Note });
        });

    public async Task<Guid> AddLineAsync(Guid id, DocumentLineInput input, Actor actor, CancellationToken cancellationToken)
    {
        var lineId = Guid.NewGuid();
        await MutateAsync(id, null, actor, editorOnly: true, cancellationToken, async (doc, now) =>
        {
            var source = await FindLocationAsync(doc.WarehouseId, input.LocationCode, cancellationToken);
            Location? target = null;
            if (doc.DocType == StockDocumentType.Transfer)
            {
                if (string.IsNullOrWhiteSpace(input.TargetLocationCode))
                {
                    throw new DomainRuleException("移庫必須指定目的儲位。");
                }
                // 目的倉庫沒填時：掃到的是儲位 QR 就用 QR 上的倉庫（跨倉移庫可以只掃目的儲位），否則同一個倉庫
                var targetWarehouseCode = string.IsNullOrWhiteSpace(input.TargetWarehouseCode)
                    ? ScanCodes.Location(input.TargetLocationCode).WarehouseCode
                    : input.TargetWarehouseCode;
                var targetWarehouseId = targetWarehouseCode is null
                    ? doc.WarehouseId
                    : await FindWarehouseIdAsync(targetWarehouseCode, cancellationToken);
                target = await FindLocationAsync(targetWarehouseId, input.TargetLocationCode, cancellationToken, isTarget: true);
                await WarehouseAccess.EnsureAsync(db, actor, target.WarehouseId, cancellationToken);
            }

            var (items, code) = await ScanCodes.ItemsAsync(db, input.ItemCode, cancellationToken);
            var item = await items.AsNoTracking()
                .Select(i => new
                {
                    i.Id, i.Status, i.TrackingMode, i.ItemCode,
                    DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => (int)u.DecimalPlaces).First(),
                })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new DomainRuleException($"找不到料號 {code}。");
            // 掃條碼加入時，訊息與稽核一律記正式料號（M5-01 審查 S4）
            code = item.ItemCode ?? code;
            // 停用的物料仍可出庫、移庫（清空庫存），但不可再進倉
            if (item.Status != ItemStatus.Active && (doc.DocType == StockDocumentType.Inbound || item.Status != ItemStatus.Inactive))
            {
                throw new DomainRuleException($"料號 {code} 不是有效狀態，不能{(doc.DocType == StockDocumentType.Inbound ? "進倉" : "使用")}。");
            }
            if (input.ReservationId is { } reservationId)
            {
                RequireSelectable(await db.StockReservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken),
                    doc, item.Id);
            }
            var line = doc.AddMovementLine(lineId, item.Id, item.TrackingMode, item.DecimalPlaces, source.Id, target?.Id,
                input.LotNo, input.SerialNo, input.ExpiryDate, input.Quantity, input.ReservationId, input.MergeIfSame);
            var merged = line.Id != lineId;
            lineId = line.Id;
            return (merged ? "document.line_merge" : "document.line_add", new
            {
                LineId = lineId, LineQuantity = line.Quantity, Location = source.Code, Target = target?.Code, ItemCode = code, input.LotNo, input.SerialNo, input.Quantity,
                input.ReservationId,
            });
        });
        return lineId;
    }

    public Task RemoveLineAsync(Guid id, Guid lineId, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, null, actor, editorOnly: true, cancellationToken, (doc, now) =>
        {
            doc.RemoveLine(lineId);
            return Task.FromResult<(string, object?)>(("document.line_remove", new { LineId = lineId }));
        });

    public Task SubmitAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken) =>
        IdempotentAsync(SubmitCommand, id, expectedVersion, idempotencyKey, actor, cancellationToken, async (doc, current, now) =>
        {
            if (!current.DocumentApprovalEnabled)
            {
                throw new ConflictException("document.approval_disabled", "系統未開啟單據審核，請直接按「確認過帳」。");
            }
            RequireEditor(doc, actor);
            RequireHeaderFields(doc, current);
            doc.Submit(now);
            // 出倉單送審即預留（D-26）：依料號合計；指定手動預留的行只補預留「超出手動預留剩餘量」的部分（M4-02 審查 #6）。
            // 可用量不足就無法送審；允許負庫存的物料只預留有的部分（使用者 2026-10-22）
            var created = 0;
            if (doc.DocType == StockDocumentType.Outbound)
            {
                var needs = doc.Lines.Where(l => l.ReservationId is null)
                    .GroupBy(l => l.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
                var manualIds = doc.Lines.Where(l => l.ReservationId is not null).Select(l => l.ReservationId!.Value).Distinct().ToList();
                var manual = await db.StockReservations.AsNoTracking().Where(r => manualIds.Contains(r.Id))
                    .ToDictionaryAsync(r => r.Id, cancellationToken);
                foreach (var group in doc.Lines.Where(l => l.ReservationId is not null).GroupBy(l => l.ReservationId!.Value))
                {
                    var reservation = RequireSelectable(manual.GetValueOrDefault(group.Key), doc, group.First().ItemId!.Value);
                    var excess = group.Sum(l => l.Quantity) - reservation.RemainingQuantity;
                    if (excess > 0)
                    {
                        needs[reservation.ItemId] = needs.GetValueOrDefault(reservation.ItemId) + excess;
                    }
                }
                var requests = needs.OrderBy(n => n.Key)
                    .Select(n => new ReservationRequest(n.Key, doc.WarehouseId, n.Value, ReservationReference, doc.Id, doc.DocNo))
                    .ToList();
                created = (await posting.ReserveManyAsync(requests, actor, now, cancellationToken, forDocument: true)).Count;
            }
            return ("document.submit", (object?)new { Reservations = created });
        });

    public Task ConfirmAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken) =>
        IdempotentAsync(ConfirmCommand, id, expectedVersion, idempotencyKey, actor, cancellationToken, async (doc, current, now) =>
        {
            if (current.DocumentApprovalEnabled)
            {
                throw new ConflictException("document.approval_required", "系統已開啟單據審核，請送審，由倉庫主管核准後過帳。");
            }
            RequireEditor(doc, actor);
            RequireHeaderFields(doc, current);
            doc.PostDirect(actor.Id, actor.Name, now); // 先檢查狀態，失敗就不做後面的事
            await PostCoreAsync(doc, actor, now, cancellationToken);
            return ("document.confirm", (object?)new { doc.DocNo, Lines = doc.Lines.Count });
        });

    public Task ApproveAsync(Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken) =>
        IdempotentAsync(ApproveCommand, id, expectedVersion, idempotencyKey, actor, cancellationToken, async (doc, current, now) =>
        {
            RequireWarehouseManager(actor);
            // 送審後才關閉審核的單，仍由主管核准（不檢查目前的審核開關）
            doc.MarkPosted(actor.Id, actor.Name, current.AllowSelfApproveDocuments, now);
            await PostCoreAsync(doc, actor, now, cancellationToken);
            return ("document.approve", (object?)new { doc.DocNo, Lines = doc.Lines.Count });
        });

    public Task ReturnAsync(Guid id, uint expectedVersion, string comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, actor, editorOnly: false, cancellationToken, async (doc, now) =>
        {
            RequireWarehouseManager(actor);
            doc.Return(comment);
            var released = await ReleaseDocumentReservationsAsync(doc.Id, actor, now, cancellationToken);
            return ("document.return", new { Comment = comment, ReleasedReservations = released });
        });

    public Task CancelAsync(Guid id, uint expectedVersion, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, actor, editorOnly: true, cancellationToken, async (doc, now) =>
        {
            doc.Cancel();
            var released = await ReleaseDocumentReservationsAsync(doc.Id, actor, now, cancellationToken);
            return ("document.cancel", new { ReleasedReservations = released });
        });

    public async Task<Guid> CreateReversalAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockDocumentAsync(id, cancellationToken);
        var original = await LoadAsync(id, null, actor, cancellationToken);
        if (original.Status != StockDocumentStatus.Posted)
        {
            throw new DomainRuleException("只有已過帳的單可以建立沖銷單。");
        }
        if (original.ReversalOfDocumentId is not null)
        {
            throw new DomainRuleException("沖銷單不能再沖銷；要更正請另開新單。");
        }
        if (original.DocType == StockDocumentType.Adjustment)
        {
            throw new DomainRuleException("盤點調整單不能沖銷；盤點有誤請重新盤點該儲位。");
        }
        if (await db.StockDocuments.AnyAsync(d => d.ReversalOfDocumentId == id && d.Status != StockDocumentStatus.Cancelled, cancellationToken))
        {
            throw new ConflictException("document.reversal_exists", "這張單已經有沖銷單（未取消）。");
        }

        var type = original.DocType switch
        {
            StockDocumentType.Inbound => StockDocumentType.Outbound,
            StockDocumentType.Outbound => StockDocumentType.Inbound,
            _ => StockDocumentType.Transfer,
        };
        var warehouseId = original.WarehouseId;
        if (type == StockDocumentType.Transfer)
        {
            // 移庫沖銷：目的儲位變來源，必須都在同一個倉庫
            var targetIds = original.Lines.Select(x => x.TargetLocationId!.Value).Distinct().ToList();
            var targetWarehouses = await db.Locations.Where(l => targetIds.Contains(l.Id))
                .Select(l => l.WarehouseId).Distinct().ToListAsync(cancellationToken);
            if (targetWarehouses.Count != 1)
            {
                throw new DomainRuleException("這張移庫單的目的儲位分屬多個倉庫，請分別手動建立移庫單。");
            }
            warehouseId = targetWarehouses[0];
        }

        var now = clock.GetUtcNow();
        var header = new StockDocumentHeader(null, original.PartnerId, original.ExternalNo, $"沖銷 {original.DocNo}");
        var reversal = StockDocument.CreateMovement(
            Guid.NewGuid(), await NextDocNoAsync(type, cancellationToken), type, warehouseId, header, original.Id, actor.Id, actor.Name, now);
        var itemIds = original.Lines.Select(l => l.ItemId!.Value).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.TrackingMode, DecimalPlaces = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => (int)u.DecimalPlaces).First() })
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        foreach (var line in original.Lines.OrderBy(l => l.LineNo))
        {
            var item = items[line.ItemId!.Value];
            var (location, target) = type == StockDocumentType.Transfer
                ? (line.TargetLocationId!.Value, (Guid?)line.LocationId)
                : (line.LocationId, null);
            reversal.AddMovementLine(Guid.NewGuid(), item.Id, item.TrackingMode, item.DecimalPlaces, location, target,
                line.LotNo, line.SerialNo, null, line.Quantity, null);
        }
        db.StockDocuments.Add(reversal);
        AuditLog.Add(db, actor, now, "document.reversal_create", "stock_document", reversal.Id.ToString(), null,
            new { reversal.DocNo, Original = original.DocNo });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reversal.Id;
    }

    /// <summary>
    /// 過帳（確認或核准，呼叫端已鎖單據並把狀態改為已過帳）。同一交易：
    /// 1. 儲位（來源＋目的）依 id FOR UPDATE，檢查歸屬：進出倉儲位、移庫來源都要在單據倉庫；移庫目的可跨倉。
    ///    必須在批號／序號 upsert 之前鎖（ADR-0004 鎖順序）。
    /// 2. 批號、序號依（物料, 號碼）排序：進倉沒有就建立；出倉、移庫必須已存在。回填明細 lot_id／serial_id。
    /// 3. 組結存變化：進 +q；出 −q（依指定預留或本單送審預留沖銷）；移 −q 來源、+q 目的。
    /// 4. 交給 <see cref="IInventoryPostingService.ApplyAsync"/>：鎖、可用量、負庫存、異動帳都在那裡。
    /// 5. 釋放本單還沒沖銷完的預留（出庫量少於預留時）。
    /// </summary>
    private async Task PostCoreAsync(StockDocument doc, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var locationIds = doc.Lines.Select(l => l.LocationId)
            .Concat(doc.Lines.Where(l => l.TargetLocationId is not null).Select(l => l.TargetLocationId!.Value))
            .Distinct().Order().ToArray();
        var locations = (await QueryAsync(db,
            "SELECT id, warehouse_id, code FROM locations WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
            r => (Id: r.GetGuid(0), WarehouseId: r.GetGuid(1), Code: r.GetString(2)), cancellationToken, Param("ids", locationIds)))
            .ToDictionary(l => l.Id);
        foreach (var line in doc.Lines)
        {
            if (locations[line.LocationId].WarehouseId != doc.WarehouseId)
            {
                throw new DomainRuleException($"儲位 {locations[line.LocationId].Code} 不屬於這張單的倉庫。");
            }
        }
        if (doc.DocType == StockDocumentType.Inbound)
        {
            var itemIds = doc.Lines.Select(l => l.ItemId!.Value).Distinct().ToList();
            var inactive = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.Status != ItemStatus.Active)
                .Select(i => i.ItemCode ?? i.RequestNo).ToListAsync(cancellationToken);
            if (inactive.Count > 0)
            {
                throw new DomainRuleException($"物料 {string.Join("、", inactive)} 已不是有效狀態，不能進倉。");
            }
        }

        var create = doc.DocType == StockDocumentType.Inbound;
        // 批次解析：500 行也只有固定幾次往返（M4-02 審查 #5）；依（物料, 號碼）排序寫入
        var lotIds = await DocumentSupport.ResolveLotsAsync(db, doc.Lines, create, now, cancellationToken);
        var serialIds = await DocumentSupport.ResolveSerialsAsync(db, doc.Lines, lotIds, create, now, cancellationToken);
        await DocumentSupport.FillLineDimensionsAsync(db, lotIds, serialIds, cancellationToken);

        var documentReservations = await OpenDocumentReservationsAsync(doc.Id, cancellationToken);
        // 指定的預留從加明細到過帳之間沒有鎖住：過帳前再確認一次不是別張單的（是否仍 Open 由過帳服務鎖住後檢查）
        var selected = doc.Lines.Where(l => l.ReservationId is not null).Select(l => l.ReservationId!.Value).Distinct().ToList();
        if (await db.StockReservations.AnyAsync(
                r => selected.Contains(r.Id) && r.ReferenceType == ReservationReference && r.ReferenceId != doc.Id, cancellationToken))
        {
            throw new ConflictException("reservation.not_selectable", "明細指定的預留屬於其他出倉單，不能使用。");
        }
        // 只用來分配沖銷量；實際剩餘量由過帳服務鎖住預留列後再取 min，讀到舊值只會讓超出部分多算（偏保守）
        var manualRemaining = await db.StockReservations.AsNoTracking().Where(r => selected.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.RemainingQuantity, cancellationToken);
        var changes = new List<StockChange>();
        foreach (var line in doc.Lines.OrderBy(l => l.LineNo))
        {
            Guid? lotId = lotIds.TryGetValue(line.Id, out var l) ? l : null;
            Guid? serialId = serialIds.TryGetValue(line.Id, out var s) ? s : null;
            var item = line.ItemId!.Value;
            switch (doc.DocType)
            {
                case StockDocumentType.Inbound:
                    changes.Add(new StockChange(line.Id, item, line.LocationId, lotId, serialId, StockStatus.Available, line.Quantity));
                    break;
                case StockDocumentType.Outbound:
                    var own = documentReservations.TryGetValue(item, out var r) ? r : null;
                    var fromManual = 0m;
                    if (line.ReservationId is { } manual)
                    {
                        // 指定手動預留：先沖手動預留的剩餘量，超出部分改沖本單送審時補的預留（M4-02 審查 #6）
                        fromManual = Math.Min(line.Quantity, manualRemaining.GetValueOrDefault(manual));
                        manualRemaining[manual] = manualRemaining.GetValueOrDefault(manual) - fromManual;
                        if (fromManual > 0)
                        {
                            changes.Add(new StockChange(line.Id, item, line.LocationId, lotId, serialId, StockStatus.Available, -fromManual, manual));
                        }
                    }
                    if (line.Quantity > fromManual)
                    {
                        changes.Add(new StockChange(line.Id, item, line.LocationId, lotId, serialId, StockStatus.Available, -(line.Quantity - fromManual), own));
                    }
                    break;
                case StockDocumentType.Transfer:
                    changes.Add(new StockChange(line.Id, item, line.LocationId, lotId, serialId, StockStatus.Available, -line.Quantity));
                    changes.Add(new StockChange(line.Id, item, line.TargetLocationId!.Value, lotId, serialId, StockStatus.Available, line.Quantity));
                    break;
                default:
                    throw new DomainRuleException("期初單請用期初建帳功能。");
            }
        }
        await posting.ApplyAsync(doc.Id, changes, actor, now, cancellationToken);
        await ReleaseDocumentReservationsAsync(doc.Id, actor, now, cancellationToken);
    }

    /// <summary>冪等的寫入動作（送審、確認、核准）：同一鍵重送直接結束；鎖單據後再查一次，兩個同鍵請求同時送達也只做一次。</summary>
    private async Task IdempotentAsync(
        string commandType, Guid id, uint expectedVersion, string idempotencyKey, Actor actor, CancellationToken cancellationToken,
        Func<StockDocument, SystemSettings, DateTimeOffset, Task<(string Action, object? After)>> action)
    {
        var key = DocumentSupport.NormalizeKey(idempotencyKey);
        // 內容含操作者：別人拿同一個鍵送同一張單，不會被當成「自己已經做過」
        var requestHash = DocumentSupport.Hash($"{commandType}|{id}|{expectedVersion}|{actor.Id}");
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, commandType, requestHash, cancellationToken))
        {
            return;
        }
        try
        {
            await IdempotentCoreAsync(commandType, id, expectedVersion, key, requestHash, actor, cancellationToken, action);
        }
        catch (Exception ex) when (failures is not null && ex is AppException or DomainRuleException)
        {
            // 被拒絕的過帳嘗試另外記錄（交易已回滾；在獨立連線寫入，M4-05）
            await failures.RecordAsync(commandType, id, actor, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task IdempotentCoreAsync(
        string commandType, Guid id, uint expectedVersion, string key, string requestHash, Actor actor, CancellationToken cancellationToken,
        Func<StockDocument, SystemSettings, DateTimeOffset, Task<(string Action, object? After)>> action)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            await LockDocumentAsync(id, cancellationToken);
            if (await DocumentSupport.AlreadyProcessedAsync(db, key, commandType, requestHash, cancellationToken))
            {
                return;
            }
            var doc = await LoadAsync(id, expectedVersion, actor, cancellationToken);
            var current = await settings.GetAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var (name, after) = await action(doc, current, now);
            AuditLog.Add(db, actor, now, name, "stock_document", doc.Id.ToString(), null, after);
            db.ProcessedCommands.Add(ProcessedCommand.Create(key, commandType, requestHash, doc.Id.ToString(), now));
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConcurrencyConflictException("stock.busy", "有其他人同時在處理相同的單據或物料，請稍後再試一次。");
        }
    }

    /// <summary>修改單據的共用流程（鎖單據 → 載入 → 版本 → 權限 → 動作 → 稽核 → 儲存）。</summary>
    private async Task MutateAsync(
        Guid id, uint? expectedVersion, Actor actor, bool editorOnly, CancellationToken cancellationToken,
        Func<StockDocument, DateTimeOffset, Task<(string Action, object? After)>> action)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockDocumentAsync(id, cancellationToken);
        var doc = await LoadAsync(id, expectedVersion, actor, cancellationToken);
        if (editorOnly)
        {
            RequireEditor(doc, actor);
        }
        var now = clock.GetUtcNow();
        var (name, after) = await action(doc, now);
        AuditLog.Add(db, actor, now, name, "stock_document", doc.Id.ToString(), null, after);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 依設定整理表頭：關閉的欄位不接受新值（保留既有值，歷史不刪）；用途必須適用這種單據且啟用；往來對象必須啟用。
    /// 必填在送審／確認時才檢查（草稿可以先存）。
    /// </summary>
    private async Task<StockDocumentHeader> EffectiveHeaderAsync(
        StockDocumentType type, StockDocumentHeader input, StockDocument? existing, SystemSettings current, CancellationToken cancellationToken)
    {
        var purposeId = current.DocumentPurposeEnabled && type != StockDocumentType.Transfer ? input.PurposeId : existing?.PurposeId;
        var partnerId = current.DocumentPartnerEnabled ? input.PartnerId : existing?.PartnerId;
        var externalNo = current.DocumentExternalNoEnabled ? input.ExternalNo : existing?.ExternalNo;
        var note = current.DocumentNoteEnabled ? input.Note : existing?.Note;
        if (purposeId is { } p && p != existing?.PurposeId)
        {
            var purpose = await db.DocumentPurposes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == p, cancellationToken)
                ?? throw new DomainRuleException("找不到用途類型。");
            if (!purpose.IsActive || !purpose.AppliesTo(type))
            {
                throw new DomainRuleException($"用途「{purpose.Name}」已停用或不適用這種單據。");
            }
        }
        if (partnerId is { } q && q != existing?.PartnerId
            && !await db.BusinessPartners.AnyAsync(x => x.Id == q && x.IsActive, cancellationToken))
        {
            throw new DomainRuleException("找不到往來對象，或已停用。");
        }
        return new StockDocumentHeader(purposeId, partnerId, externalNo, note);
    }

    /// <summary>送審、確認前檢查必填欄位（沖銷單不檢查用途；移庫沒有用途）。錯誤碼 domain_rule。</summary>
    private static void RequireHeaderFields(StockDocument doc, SystemSettings current)
    {
        var missing = new List<string>();
        if (current.DocumentPurposeEnabled && current.DocumentPurposeRequired && doc.PurposeId is null
            && doc.DocType != StockDocumentType.Transfer && doc.ReversalOfDocumentId is null)
        {
            missing.Add("用途");
        }
        if (current.DocumentPartnerEnabled && current.DocumentPartnerRequired && doc.PartnerId is null)
        {
            missing.Add("往來對象");
        }
        if (current.DocumentExternalNoEnabled && current.DocumentExternalNoRequired && doc.ExternalNo is null)
        {
            missing.Add("外部單號");
        }
        if (current.DocumentNoteEnabled && current.DocumentNoteRequired && doc.Note is null)
        {
            missing.Add("備註");
        }
        if (missing.Count > 0)
        {
            throw new DomainRuleException($"請填寫：{string.Join("、", missing)}。");
        }
    }

    private async Task<Dictionary<Guid, Guid?>> OpenDocumentReservationsAsync(Guid documentId, CancellationToken cancellationToken) =>
        await db.StockReservations.AsNoTracking()
            .Where(r => r.ReferenceType == ReservationReference && r.ReferenceId == documentId && r.Status == ReservationStatus.Open)
            .ToDictionaryAsync(r => r.ItemId, r => (Guid?)r.Id, cancellationToken);

    private async Task<int> ReleaseDocumentReservationsAsync(Guid documentId, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = await db.StockReservations.AsNoTracking()
            .Where(r => r.ReferenceType == ReservationReference && r.ReferenceId == documentId && r.Status == ReservationStatus.Open)
            .OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            await posting.ReleaseReservationAsync(id, actor, now, cancellationToken);
        }
        return ids.Count;
    }

    private async Task<string> NextDocNoAsync(StockDocumentType type, CancellationToken cancellationToken)
    {
        var (sequence, prefix) = type switch
        {
            StockDocumentType.Inbound => (WarehouseDbContext.InboundDocNoSequence, "IN"),
            StockDocumentType.Outbound => (WarehouseDbContext.OutboundDocNoSequence, "OUT"),
            StockDocumentType.Transfer => (WarehouseDbContext.TransferDocNoSequence, "TR"),
            _ => throw new DomainRuleException("期初單請用期初建帳功能。"),
        };
        var number = (await QueryAsync(db, $"SELECT nextval('{sequence}')", r => r.GetInt64(0), cancellationToken)).Single();
        return $"{prefix}-{number:D6}";
    }

    private async Task LockDocumentAsync(Guid id, CancellationToken cancellationToken) =>
        await ExecuteAsync(db, "SELECT 1 FROM stock_documents WHERE id = @id FOR UPDATE", cancellationToken, Param("id", id));

    private async Task<StockDocument> LoadAsync(Guid id, uint? expectedVersion, Actor actor, CancellationToken cancellationToken)
    {
        var doc = await db.StockDocuments.Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.Id == id && d.DocType != StockDocumentType.Opening, cancellationToken)
            ?? throw new NotFoundException("document.not_found", "找不到單據。");
        // 每個修改、送審、核准、沖銷都經過這裡：操作者要能在單據倉庫（與跨倉移庫的目的倉）作業（M6-01b）；
        // 先於版本比對，沒有權限的人一律得到 warehouse.forbidden
        await WarehouseAccess.EnsureDocumentAsync(db, actor, doc, cancellationToken);
        if (expectedVersion is { } v && db.Entry(doc).Property<uint>("Version").CurrentValue != v)
        {
            throw new ConcurrencyConflictException("document.concurrency_conflict", "單據已被他人修改，請重新載入後再操作。");
        }
        return doc;
    }

    private async Task<Guid> FindWarehouseIdAsync(string warehouseCode, CancellationToken cancellationToken)
    {
        var code = TextNormalizer.Normalize(warehouseCode);
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Code == code, cancellationToken)
            ?? throw new DomainRuleException($"找不到倉庫 {code}。");
        if (!warehouse.IsActive)
        {
            throw new DomainRuleException($"倉庫 {code} 已停用。");
        }
        return warehouse.Id;
    }

    private async Task<Location> FindLocationAsync(
        Guid warehouseId, string locationCode, CancellationToken cancellationToken, bool isTarget = false)
    {
        // 可直接收掃到的儲位 QR（M5-01）：QR 上的倉庫必須是這張單的倉庫
        var (code, qrWarehouseCode) = ScanCodes.Location(locationCode);
        if (qrWarehouseCode is not null
            && !await db.Warehouses.AnyAsync(w => w.Id == warehouseId && w.Code == qrWarehouseCode, cancellationToken))
        {
            throw new DomainRuleException(isTarget
                ? $"掃到的是倉庫 {qrWarehouseCode} 的儲位 QR，與填寫的目的倉庫不同。"
                : $"掃到的是倉庫 {qrWarehouseCode} 的儲位 QR，與這張單的倉庫不同。");
        }
        var location = await db.Locations.AsNoTracking()
            .SingleOrDefaultAsync(l => l.WarehouseId == warehouseId && l.Code == code, cancellationToken)
            ?? throw new DomainRuleException($"這個倉庫沒有儲位 {code}。");
        if (location.Status == LocationStatus.Inactive)
        {
            throw new DomainRuleException($"儲位 {code} 已停用。");
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
            throw new ConcurrencyConflictException("document.concurrency_conflict", "單據已被他人修改，請重新載入後再操作。");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw pg.ConstraintName switch
            {
                "ux_stock_document_lines_key" => new DomainRuleException("這張單已經有相同儲位、物料、批號／序號的明細。"),
                "ux_stock_documents_reversal" => new ConflictException("document.reversal_exists", "這張單已經有沖銷單（未取消）。"),
                "pk_processed_commands" => new ConflictException("idempotency.key_reused", "這個請求識別碼已被使用。"),
                "pk_stock_documents" => new ConflictException("document.request_id_conflict", "這個請求識別碼已被使用，請重新整理頁面。"),
                _ => new DomainRuleException("資料重複。"),
            };
        }
    }

    /// <summary>
    /// 明細可以指定的預留：存在、仍 Open、物料與倉庫相同，而且**不是別張出倉單送審自動建立的**
    /// （否則能吃掉對方保留的數量，D-26 的保證被繞過，M4-02 審查 #2）。
    /// </summary>
    private static StockReservation RequireSelectable(StockReservation? reservation, StockDocument doc, Guid itemId)
    {
        if (reservation is null)
        {
            throw new NotFoundException("reservation.not_found", "找不到預留。");
        }
        if (reservation.ReferenceType == ReservationReference && reservation.ReferenceId != doc.Id)
        {
            throw new ConflictException("reservation.not_selectable", "這筆預留屬於其他出倉單，不能指定。");
        }
        if (reservation.Status != ReservationStatus.Open || reservation.ItemId != itemId || reservation.WarehouseId != doc.WarehouseId)
        {
            throw new DomainRuleException("這筆預留已結束，或物料、倉庫與這張單不同。");
        }
        return reservation;
    }

    private static void RequireEditor(StockDocument doc, Actor actor)
    {
        if (!actor.IsWarehouseManager && doc.CreatedById != actor.Id)
        {
            throw new PermissionDeniedException("document.permission_denied", "只有建立者或倉庫主管可以處理這張單。");
        }
    }

    private static void RequireWarehouseManager(Actor actor)
    {
        if (!actor.IsWarehouseManager)
        {
            throw new PermissionDeniedException("document.permission_denied", "只有倉庫主管可以核准或退回單據。");
        }
    }
}
