using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Warehouse.Application.Abstractions;
using Npgsql;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
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
/// <see cref="IScanCartService"/> 實作（ADR-0026）。購物車只是清單：不寫稽核（使用者決定放棄不留紀錄）、不動結存。
/// 每個修改：開交易 → 鎖購物車列（FOR UPDATE，多裝置同時改時依序處理）→ 只能改自己的 → 修改 → 儲存。
/// </summary>
public sealed partial class ScanCartService(WarehouseDbContext db, TimeProvider clock) : IScanCartService
{
    public async Task<IReadOnlyList<ScanCartSummary>> ListAsync(Actor actor, CancellationToken cancellationToken) =>
        await db.ScanCarts.AsNoTracking()
            .Where(c => c.OwnerId == actor.Id)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ScanCartSummary(
                c.Id, c.Note, db.Warehouses.Where(w => w.Id == c.WarehouseId).Select(w => w.Code).FirstOrDefault(),
                c.Lines.Count, c.CreatedAt, c.UpdatedAt))
            .Take(20).ToListAsync(cancellationToken);

    public Task<ScanCartDetails?> GetAsync(Guid id, Actor actor, CancellationToken cancellationToken) =>
        ReadAsync(id, actor, cancellationToken);

    private async Task<ScanCartDetails?> ReadAsync(Guid id, Actor actor, CancellationToken cancellationToken, Guid? lineId = null)
    {
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken) : null;
        var head = await db.ScanCarts.AsNoTracking()
            .Where(c => c.Id == id && c.OwnerId == actor.Id)
            .Select(c => new { Cart = c, Version = EF.Property<uint>(c, "Version"), WarehouseCode = db.Warehouses.Where(w => w.Id == c.WarehouseId).Select(w => w.Code).FirstOrDefault() })
            .SingleOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }
        var lines = await (
            from l in db.Set<ScanCartLine>().AsNoTracking()
            where l.CartId == id && (lineId == null || l.Id == lineId)
            join item in db.Items on l.ItemId equals item.Id
            join unit in db.Units on item.BaseUnitId equals unit.Id
            from loc in db.Locations.Where(x => x.Id == l.LocationId).DefaultIfEmpty()
            orderby l.LineNo
            select new ScanCartLineRow(
                l.Id, l.LineNo, item.Id, item.ItemCode!, item.Name, item.Specification, unit.Code, unit.DecimalPlaces,
                item.TrackingMode, item.Status == ItemStatus.Active, l.LocationId, loc.Code, l.Quantity))
            .ToListAsync(cancellationToken);
        var c = head.Cart;
        return new ScanCartDetails(c.Id, c.Note, c.WarehouseId, head.WarehouseCode, c.CreatedAt, c.UpdatedAt, lines, head.Version, c.DraftJson);
    }

    public async Task<Guid> CreateAsync(string? note, Actor actor, CancellationToken cancellationToken)
    {
        var cart = ScanCart.Create(Guid.NewGuid(), actor.Id, note, clock.GetUtcNow());
        db.ScanCarts.Add(cart);
        await SaveAsync(cancellationToken);
        return cart.Id;
    }

    public Task SetNoteAsync(Guid id, string? note, Actor actor, CancellationToken cancellationToken, uint? expectedVersion = null) =>
        MutateAsync(id, actor, cancellationToken, (cart, now) =>
        {
            cart.SetNote(note, now);
            return Task.CompletedTask;
        }, expectedVersion);

    public async Task<Guid> AddAsync(Guid id, ScanCartAddInput input, Actor actor, CancellationToken cancellationToken) =>
        (await AddWithResultAsync(id, input, actor, cancellationToken)).LineId;

    public async Task<ScanCartAddResult> AddWithResultAsync(Guid id, ScanCartAddInput input, Actor actor, CancellationToken cancellationToken)
    {
        if (input.OperationId == Guid.Empty) throw new DomainRuleException("請求識別碼不可為空。");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
        var key = input.OperationId is { } op ? "cart.add:" + op.ToString("N") : null;
        const string command = "cart.add";
        var hash = DocumentSupport.Hash(JsonSerializer.Serialize(new { CartId = id, ActorId = actor.Id, Input = input }));
        if (key is not null)
        {
            await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", cancellationToken, Param("key", key));
            if (await DocumentSupport.AlreadyProcessedAsync(db, key, command, hash, cancellationToken))
            {
                var done = await db.ProcessedCommands.AsNoTracking().SingleAsync(c => c.Key == key, cancellationToken);
                var prior = await GetAsync(id, actor, cancellationToken);
                return new(Guid.Parse(done.ResultRef), 0, prior, prior?.Lines.Count ?? 0);
            }
        }
        var cart = await LoadAsync(id, actor, cancellationToken);
        var version = db.Entry(cart).Property<uint>("Version").CurrentValue;
        var item = await FindItemAsync(input.ItemCode, cancellationToken);
        if (input.ItemId is { } selected && item.Id != selected)
            throw new DomainRuleException("所選物料的料號已變更，請重新搜尋核對。");
        var location = await FindLocationAsync(cart, input.LocationCode, actor, cancellationToken);
        var line = cart.Add(Guid.NewGuid(), item.Id, item.DecimalPlaces, location?.Id, location?.WarehouseId, input.Quantity, clock.GetUtcNow());
        db.Entry(cart).Property(c => c.UpdatedAt).IsModified = true;
        if (key is not null) db.ProcessedCommands.Add(ProcessedCommand.Create(key, command, hash, line.Id.ToString(), clock.GetUtcNow()));
        await SaveAsync(cancellationToken);
        var details = await ReadAsync(id, actor, cancellationToken, line.Id);
        await transaction.CommitAsync(cancellationToken);
        return new(line.Id, version, details, cart.Lines.Count);
    }

    public Task UpdateLineAsync(Guid id, Guid lineId, decimal quantity, string? locationCode, Actor actor, CancellationToken cancellationToken, uint? expectedVersion = null) =>
        MutateAsync(id, actor, cancellationToken, async (cart, now) =>
        {
            var line = cart.Lines.SingleOrDefault(l => l.Id == lineId)
                ?? throw new DomainRuleException("找不到這一行（可能已在其他裝置刪除），請重新整理。");
            var decimals = await db.Items.AsNoTracking().Where(i => i.Id == line.ItemId)
                .Select(i => (int)db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.DecimalPlaces).First())
                .SingleAsync(cancellationToken);
            var location = await FindLocationAsync(cart, locationCode, actor, cancellationToken, ignoring: line);
            cart.UpdateLine(lineId, quantity, decimals, location?.Id, location?.WarehouseId, now);
        }, expectedVersion);

    public Task RemoveLineAsync(Guid id, Guid lineId, Actor actor, CancellationToken cancellationToken, uint? expectedVersion = null) =>
        MutateAsync(id, actor, cancellationToken, (cart, now) =>
        {
            cart.RemoveLine(lineId, now);
            return Task.CompletedTask;
        }, expectedVersion);

    public async Task DeleteAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var cart = await LoadAsync(id, actor, cancellationToken);
        db.ScanCarts.Remove(cart);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MutateAsync(Guid id, Actor actor, CancellationToken cancellationToken, Func<ScanCart, DateTimeOffset, Task> action, uint? expectedVersion = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var cart = await LoadAsync(id, actor, cancellationToken);
        if (expectedVersion is { } expected && db.Entry(cart).Property<uint>("Version").CurrentValue != expected)
            throw new ConcurrencyConflictException("scan_cart.concurrency_conflict", "購物車已在其他裝置修改，請重新讀取核對。");
        await action(cart, clock.GetUtcNow());
        db.Entry(cart).Property(c => c.UpdatedAt).IsModified = true;
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<ScanCart> LoadAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, "SELECT 1 FROM scan_carts WHERE id = @id FOR UPDATE", cancellationToken, Param("id", id));
        // 別人的購物車一律當成找不到（不透露是否存在）
        return await db.ScanCarts.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == actor.Id, cancellationToken)
            ?? throw new NotFoundException("scan_cart.not_found", "找不到購物車（可能已在其他裝置轉成單據或放棄）。");
    }

    // 料號、WH:ITEM: QR、原廠條碼、專屬別名都可以；有效或停用（停用的仍可出庫、移庫，轉單時依單據類型再檢查）
    private async Task<(Guid Id, int DecimalPlaces)> FindItemAsync(string itemCode, CancellationToken cancellationToken)
    {
        var (items, code) = await ScanCodes.ItemsAsync(db, itemCode, cancellationToken);
        var item = await items.AsNoTracking()
            .Select(i => new { i.Id, i.Status, i.ItemCode, DecimalPlaces = (int)db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.DecimalPlaces).First() })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainRuleException($"找不到料號 {code}。");
        if (item.Status is not (ItemStatus.Active or ItemStatus.Inactive))
        {
            throw new DomainRuleException($"料號 {item.ItemCode ?? code} 還沒核准，不能使用。");
        }
        return (item.Id, item.DecimalPlaces);
    }

    /// <summary>
    /// 儲位：QR（WH:LOC:倉:儲位）帶倉庫；只有儲位碼時用購物車已鎖定的倉庫。必須可用、在可操作的倉庫。空白＝之後再補（null）。
    /// </summary>
    private async Task<Location?> FindLocationAsync(
        ScanCart cart, string? input, Actor actor, CancellationToken cancellationToken, ScanCartLine? ignoring = null)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        var (code, warehouseCode) = ScanCodes.Location(input);
        Guid warehouseId;
        if (warehouseCode is not null)
        {
            var normalized = TextNormalizer.Normalize(warehouseCode);
            warehouseId = await db.Warehouses.AsNoTracking().Where(w => w.Code == normalized).Select(w => (Guid?)w.Id).SingleOrDefaultAsync(cancellationToken)
                ?? throw new DomainRuleException($"找不到倉庫 {normalized}。");
        }
        else if (cart.Lines.Any(l => l != ignoring && l.LocationId is not null) && cart.WarehouseId is { } locked)
        {
            warehouseId = locked;
        }
        else
        {
            throw new DomainRuleException($"「{code}」只有儲位碼，不知道是哪個倉庫；請掃儲位 QR（含倉庫），或先不填儲位、選動作時再補。");
        }
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId && w.IsActive, cancellationToken))
        {
            throw new DomainRuleException("倉庫已停用。");
        }
        await WarehouseAccess.EnsureAsync(db, actor, warehouseId, cancellationToken);
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.WarehouseId == warehouseId && l.Code == code, cancellationToken)
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
            throw new ConcurrencyConflictException("scan_cart.concurrency_conflict", "購物車已在其他裝置修改，請重新整理後再試。");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConcurrencyConflictException("scan_cart.concurrency_conflict", "購物車已在其他裝置修改，請重新整理後再試。");
        }
    }
}
