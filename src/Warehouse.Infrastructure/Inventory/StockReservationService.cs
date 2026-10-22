using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IStockReservationService"/>（手動預留）與 <see cref="IItemNegativePolicyService"/>（負庫存設定）實作。
/// 預留的寫入一律交給 <see cref="IInventoryPostingService"/>（鎖與可用量檢查都在那裡）。
/// </summary>
public sealed class StockReservationService(
    WarehouseDbContext db, IInventoryPostingService posting, TimeProvider clock, IPostingFailureLog? failures = null)
    : IStockReservationService, IItemNegativePolicyService
{
    public const string ManualReference = "Manual";
    private const string CreateCommand = "reservation.create";

    public async Task<PagedResult<ReservationRow>> ListAsync(
        string? itemCode, Guid? warehouseId, ReservationStatus? status, int page, CancellationToken cancellationToken,
        bool selectableOnly = false)
    {
        page = Paging.ClampPage(page);
        var query = db.StockReservations.AsNoTracking();
        if (selectableOnly)
        {
            // 在資料庫篩選（M4-03 審查 #4）：不會因為前 20 筆都是出倉單預留而漏掉手動預留，也不列已過期的
            var now = clock.GetUtcNow();
            query = query.Where(r => r.ReferenceType == ManualReference && r.Status == ReservationStatus.Open
                && (r.ExpiresAt == null || r.ExpiresAt > now));
        }
        if (!string.IsNullOrWhiteSpace(itemCode))
        {
            var code = TextNormalizer.Normalize(itemCode);
            query = query.Where(r => db.Items.Any(i => i.Id == r.ItemId && i.ItemCode == code));
        }
        if (warehouseId is { } w)
        {
            query = query.Where(r => r.WarehouseId == w);
        }
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await (
            from r in query
            join i in db.Items on r.ItemId equals i.Id
            join w2 in db.Warehouses on r.WarehouseId equals w2.Id
            orderby r.CreatedAt descending, r.Id
            select new ReservationRow(
                r.Id, r.ItemId, i.ItemCode ?? i.RequestNo, i.Name, r.WarehouseId, w2.Code, r.Quantity, r.RemainingQuantity,
                r.ReferenceType, r.ReferenceId,
                db.StockDocuments.Where(d => d.Id == r.ReferenceId).Select(d => d.DocNo).FirstOrDefault(),
                r.Note, r.Status, r.CreatedById, r.CreatedByName, r.CreatedAt, r.ExpiresAt, r.ClosedAt))
            .Skip((page - 1) * Paging.DefaultPageSize)
            .Take(Paging.DefaultPageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<ReservationRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<Guid> CreateAsync(ReservationInput input, string idempotencyKey, Actor actor, CancellationToken cancellationToken)
    {
        try
        {
            return await CreateCoreAsync(input, idempotencyKey, actor, cancellationToken);
        }
        catch (Exception ex) when (failures is not null && ex is AppException or DomainRuleException)
        {
            await failures.RecordAsync(CreateCommand, null, actor, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<Guid> CreateCoreAsync(ReservationInput input, string idempotencyKey, Actor actor, CancellationToken cancellationToken)
    {
        var key = DocumentSupport.NormalizeKey(idempotencyKey);
        var code = TextNormalizer.Normalize(input.ItemCode ?? string.Empty);
        // 內容含料號、倉庫、數量、到期與操作者：同鍵不同內容會被拒（ADR-0004 決策二第 6 點）
        var requestHash = DocumentSupport.Hash(
            $"{CreateCommand}|{code}|{input.WarehouseId}|{input.Quantity}|{input.ExpiresAt?.ToUniversalTime():O}|{input.Note}|{actor.Id}");
        if (await ExistingAsync(key, requestHash, cancellationToken) is { } done)
        {
            return done;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // 同一鍵同時送兩次：用 advisory lock 讓第二個等第一個 commit，再查到它的結果
        await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", cancellationToken, Param("key", "reservation-key:" + key));
        if (await ExistingAsync(key, requestHash, cancellationToken) is { } concurrent)
        {
            return concurrent;
        }
        var itemId = await db.Items.Where(i => i.ItemCode == code && i.Status == ItemStatus.Active).Select(i => (Guid?)i.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainRuleException($"找不到有效的料號 {code}。");
        var now = clock.GetUtcNow();
        var id = await posting.ReserveAsync(
            new ReservationRequest(itemId, input.WarehouseId, input.Quantity, ManualReference, null, input.Note, input.ExpiresAt), actor, now, cancellationToken);
        db.ProcessedCommands.Add(ProcessedCommand.Create(key, CreateCommand, requestHash, id.ToString(), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task CancelAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservation = await db.StockReservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("reservation.not_found", "找不到預留。");
        if (reservation.ReferenceType == StockDocumentService.ReservationReference)
        {
            throw new ConflictException("reservation.not_cancellable", "這是出倉單送審時建立的預留，請取消或退回該單據。");
        }
        if (!actor.IsWarehouseManager && reservation.CreatedById != actor.Id)
        {
            throw new PermissionDeniedException("reservation.permission_denied", "只有建立者或倉庫主管可以取消這筆預留。");
        }
        if (!await posting.ReleaseReservationAsync(id, actor, clock.GetUtcNow(), cancellationToken))
        {
            throw new ConflictException("reservation.closed", "這筆預留已沖銷、已釋放或已過期。");
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 修改負庫存設定。鎖順序：料號 advisory 鎖（與造成負數的過帳、預留排隊）→ items FOR UPDATE → 檢查 → 更新。
    /// 物料的其他欄位不受影響；版本（xmin）不符時拒絕。
    /// </summary>
    public async Task SetAsync(Guid itemId, uint expectedVersion, bool allow, decimal limit, Actor actor, CancellationToken cancellationToken)
    {
        if (!actor.IsItemManager)
        {
            throw new PermissionDeniedException("item.permission_denied", "只有物料管理員可以修改負庫存設定。");
        }
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            await AdvisoryLocks.LockStockAsync(db, [AdvisoryLocks.StockItemKey(itemId)], cancellationToken);
            await ExecuteAsync(db, "SELECT 1 FROM items WHERE id = @id FOR UPDATE", cancellationToken, Param("id", itemId));
            var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken)
                ?? throw new NotFoundException("item.not_found", "找不到物料。");
            if (db.Entry(item).Property<uint>("Version").CurrentValue != expectedVersion)
            {
                throw new ConcurrencyConflictException("item.concurrency_conflict", "物料已被他人修改，請重新載入後再操作。");
            }
            var before = new { item.AllowNegativeStock, item.NegativeStockLimit };
            if (!allow && await db.StockBalances.AnyAsync(b => b.ItemId == itemId && b.Quantity < 0, cancellationToken))
            {
                throw new DomainRuleException("這個物料還有負結存，不能關閉負庫存；請先補帳。");
            }
            var decimals = await db.Units.Where(u => u.Id == item.BaseUnitId).Select(u => (int)u.DecimalPlaces).SingleAsync(cancellationToken);
            if (decimal.Round(limit, decimals) != limit)
            {
                throw new DomainRuleException($"下限最多 {decimals} 位小數（依物料的基本單位）。");
            }
            // 關閉時下限視為 0：可用量（扣除預留）已是負數也不可關閉，否則之後所有出庫都會失敗（M4-03 審查 #5）
            var available = (await QueryAsync(db, InventoryPostingService.ItemAvailableSql, r => r.GetDecimal(1), cancellationToken,
                Param("items", new[] { itemId }))).Single();
            var floor = allow ? -limit : 0m;
            if (available < floor)
            {
                throw new DomainRuleException(allow
                    ? $"目前全倉可用量是 {available:0.####}，下限不可小於 {-available:0.####}。"
                    : $"目前全倉可用量是 {available:0.####}（已扣除預留），請先補帳或取消預留再關閉負庫存。");
            }
            var now = clock.GetUtcNow();
            item.SetNegativeStockPolicy(allow, limit, now);
            AuditLog.Add(db, actor, now, "item.negative_policy", "item", itemId.ToString(), before,
                new { item.AllowNegativeStock, item.NegativeStockLimit });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("item.concurrency_conflict", "物料已被他人修改，請重新載入後再操作。");
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConcurrencyConflictException("stock.busy", "這個物料正在過帳，請稍後再試一次。");
        }
    }

    private async Task<Guid?> ExistingAsync(string key, string requestHash, CancellationToken cancellationToken) =>
        await DocumentSupport.AlreadyProcessedAsync(db, key, CreateCommand, requestHash, cancellationToken)
            ? Guid.Parse((await db.ProcessedCommands.AsNoTracking().SingleAsync(c => c.Key == key, cancellationToken)).ResultRef)
            : null;
}
