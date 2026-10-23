using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Identity;

/// <summary>
/// 倉庫作業範圍檢查（M6-01，ADR-0017）。各業務服務在交易內、載入資料後呼叫 <see cref="EnsureAsync"/>：
/// 每次都查資料庫，管理員改了範圍立即生效（不依賴 Cookie 內的快照）。
/// 不受限制的情況：帳號 <c>AllWarehouses = true</c>（預設）、系統自動操作（Actor.Id = Guid.Empty，例如命令列與背景工作），
/// 以及找不到帳號的 Actor（只有單元／整合測試直接建立的 Actor 會如此；網頁與 API 一定來自已登入的帳號）。
/// </summary>
public static class WarehouseAccess
{
    public static async Task<IReadOnlySet<Guid>?> OperableAsync(WarehouseDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }
        var all = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (bool?)u.AllWarehouses)
            .SingleOrDefaultAsync(cancellationToken);
        if (all is null or true)
        {
            return null;
        }
        return (await db.UserWarehouses.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.WarehouseId)
            .ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>操作者必須能在所有 <paramref name="warehouseIds"/> 作業，否則丟 <c>warehouse.forbidden</c>。</summary>
    public static async Task EnsureAsync(
        WarehouseDbContext db, Actor actor, IEnumerable<Guid> warehouseIds, CancellationToken cancellationToken)
    {
        var allowed = await OperableAsync(db, actor.Id, cancellationToken);
        if (allowed is null)
        {
            return;
        }
        var denied = warehouseIds.Distinct().Where(id => !allowed.Contains(id)).ToList();
        if (denied.Count == 0)
        {
            return;
        }
        var codes = await db.Warehouses.AsNoTracking().Where(w => denied.Contains(w.Id)).Select(w => w.Code)
            .OrderBy(c => c).ToListAsync(cancellationToken);
        throw new PermissionDeniedException("warehouse.forbidden",
            $"你沒有倉庫 {string.Join("、", codes.Count > 0 ? codes : ["（未知）"])} 的作業權限，請洽系統管理員。");
    }

    public static Task EnsureAsync(WarehouseDbContext db, Actor actor, Guid warehouseId, CancellationToken cancellationToken) =>
        EnsureAsync(db, actor, [warehouseId], cancellationToken);

    /// <summary>需要「全部倉庫」範圍的操作（例如新增倉庫）。</summary>
    public static async Task EnsureAllAsync(WarehouseDbContext db, Actor actor, CancellationToken cancellationToken)
    {
        if (await OperableAsync(db, actor.Id, cancellationToken) is not null)
        {
            throw new PermissionDeniedException("warehouse.forbidden", "只有可作業全部倉庫的帳號可以執行這個動作。");
        }
    }

    /// <summary>單據（進出倉、移庫、期初）涉及的倉庫：單據倉庫＋明細目的儲位所在的倉庫（跨倉移庫）。</summary>
    public static async Task EnsureDocumentAsync(
        WarehouseDbContext db, Actor actor, Warehouse.Domain.Inventory.StockDocument doc, CancellationToken cancellationToken)
    {
        var targets = doc.Lines.Where(l => l.TargetLocationId is not null).Select(l => l.TargetLocationId!.Value).Distinct().ToList();
        var warehouses = targets.Count == 0
            ? []
            : await db.Locations.AsNoTracking().Where(l => targets.Contains(l.Id)).Select(l => l.WarehouseId).ToListAsync(cancellationToken);
        await EnsureAsync(db, actor, warehouses.Append(doc.WarehouseId), cancellationToken);
    }
}

/// <summary><see cref="IWarehouseAccess"/> 實作（畫面過濾下拉選單用）。</summary>
public sealed class WarehouseAccessService(WarehouseDbContext db) : IWarehouseAccess
{
    public Task<IReadOnlySet<Guid>?> GetOperableWarehouseIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        WarehouseAccess.OperableAsync(db, userId, cancellationToken);
}
