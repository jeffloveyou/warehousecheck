using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Identity;

/// <summary>
/// <see cref="IUserAdminService"/> 實作（M6-01）。UserManager 與稽核共用同一個 DbContext，包在同一交易內：
/// 帳號變更與 audit_logs 一起成功或一起失敗。
/// 每個寫入動作先鎖住 SystemAdmin 角色列（FOR UPDATE），讓「至少保留一個系統管理員」的檢查不會被兩個同時的請求繞過。
/// </summary>
public sealed class UserAdminService(WarehouseDbContext db, UserManager<AppUser> users, TimeProvider clock) : IUserAdminService
{
    public async Task<PagedResult<UserSummary>> ListAsync(
        string? keyword, bool includeInactive, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var query = db.Users.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            var pattern = LikePattern.Contains(text.Length > Paging.MaxKeywordLength ? text[..Paging.MaxKeywordLength] : text);
            query = query.Where(u => EF.Functions.ILike(u.UserName!, pattern, LikePattern.Escape)
                || EF.Functions.ILike(u.DisplayName, pattern, LikePattern.Escape));
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(u => u.UserName)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .ToListAsync(cancellationToken);
        var ids = rows.Select(u => u.Id).ToArray();
        var roles = await RolesOfAsync(ids, cancellationToken);
        var warehouses = await WarehousesOfAsync(ids, cancellationToken);
        var now = clock.GetUtcNow();
        return new PagedResult<UserSummary>(
            rows.Select(u => ToSummary(u, roles.GetValueOrDefault(u.Id, []), warehouses.GetValueOrDefault(u.Id, []), now)).ToList(),
            page, Paging.DefaultPageSize, total);
    }

    public async Task<UserSummary> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken) ?? throw NotFound();
        var roles = await RolesOfAsync([id], cancellationToken);
        var warehouses = await WarehousesOfAsync([id], cancellationToken);
        return ToSummary(user, roles.GetValueOrDefault(id, []), warehouses.GetValueOrDefault(id, []), clock.GetUtcNow());
    }

    public async Task<UserPasswordResult> CreateAsync(CreateUserRequest request, Actor actor, CancellationToken cancellationToken)
    {
        var userName = (request.UserName ?? string.Empty).Trim();
        var displayName = ValidateDisplayName(request.DisplayName);
        var roles = ValidateRoles(request.Roles);
        if (userName.Length is 0 or > 50)
        {
            throw new DomainRuleException("帳號必須是 1～50 個字元。");
        }
        var password = PasswordGenerator.Generate();
        return await InTransactionAsync(actor, async now =>
        {
            var user = new AppUser { UserName = userName, DisplayName = displayName, MustChangePassword = true };
            Check(await users.CreateAsync(user, password));
            Check(await users.AddToRolesAsync(user, roles));
            AuditLog.Add(db, actor, now, "user.create", "user", user.Id.ToString(), null,
                new { user.UserName, user.DisplayName, Roles = roles });
            return new UserPasswordResult(user.Id, userName, password);
        }, cancellationToken);
    }

    public Task UpdateAsync(UpdateUserRequest request, Actor actor, CancellationToken cancellationToken)
    {
        var displayName = ValidateDisplayName(request.DisplayName);
        var roles = ValidateRoles(request.Roles);
        return InTransactionAsync(actor, async now =>
        {
            var user = await FindAsync(request.Id);
            if (user.ConcurrencyStamp != request.ConcurrencyStamp)
            {
                throw new ConcurrencyConflictException("user.concurrency_conflict", "帳號已被他人修改，請重新載入後再儲存。");
            }
            var before = (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal).ToList();
            if (user.Id == actor.Id && before.Contains(Roles.SystemAdmin) && !roles.Contains(Roles.SystemAdmin))
            {
                throw new ConflictException("user.self", "不能移除自己的系統管理員角色。");
            }
            var beforeName = user.DisplayName;
            if (beforeName == displayName && before.SequenceEqual(roles))
            {
                return true; // 沒有變更：不換 security stamp、不寫稽核
            }
            user.DisplayName = displayName;
            Check(await users.UpdateAsync(user));
            var removed = before.Except(roles).ToList();
            var added = roles.Except(before).ToList();
            if (removed.Count > 0)
            {
                Check(await users.RemoveFromRolesAsync(user, removed));
            }
            if (added.Count > 0)
            {
                Check(await users.AddToRolesAsync(user, added));
            }
            await EnsureActiveAdminAsync(cancellationToken);
            // 角色變更要讓舊 Cookie 失效（Cookie 內的角色是登入當時的快照）
            Check(await users.UpdateSecurityStampAsync(user));
            AuditLog.Add(db, actor, now, "user.update", "user", user.Id.ToString(),
                new { DisplayName = beforeName, Roles = before }, new { user.DisplayName, Roles = roles });
            return true;
        }, cancellationToken);
    }

    public Task SetActiveAsync(Guid id, bool isActive, Actor actor, CancellationToken cancellationToken) =>
        InTransactionAsync(actor, async now =>
        {
            var user = await FindAsync(id);
            if (!isActive && user.Id == actor.Id)
            {
                throw new ConflictException("user.self", "不能停用自己的帳號。");
            }
            if (user.IsActive == isActive)
            {
                return true;
            }
            user.IsActive = isActive;
            Check(await users.UpdateAsync(user));
            await EnsureActiveAdminAsync(cancellationToken);
            Check(await users.UpdateSecurityStampAsync(user));
            AuditLog.Add(db, actor, now, isActive ? "user.enable" : "user.disable", "user", user.Id.ToString(), null,
                new { user.UserName });
            return true;
        }, cancellationToken);

    public Task<UserPasswordResult> ResetPasswordAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var password = PasswordGenerator.Generate();
        return InTransactionAsync(actor, async now =>
        {
            var user = await FindAsync(id);
            user.MustChangePassword = true;
            // RemovePassword／AddPassword 會更新 security stamp：舊的登入 Cookie 在下次檢查時失效
            Check(await users.RemovePasswordAsync(user));
            Check(await users.AddPasswordAsync(user, password));
            Check(await users.SetLockoutEndDateAsync(user, null));
            Check(await users.ResetAccessFailedCountAsync(user));
            AuditLog.Add(db, actor, now, "user.reset_password", "user", user.Id.ToString(), null, new { user.UserName });
            return new UserPasswordResult(user.Id, user.UserName!, password);
        }, cancellationToken);
    }

    public Task UnlockAsync(Guid id, Actor actor, CancellationToken cancellationToken) =>
        InTransactionAsync(actor, async now =>
        {
            var user = await FindAsync(id);
            Check(await users.SetLockoutEndDateAsync(user, null));
            Check(await users.ResetAccessFailedCountAsync(user));
            AuditLog.Add(db, actor, now, "user.unlock", "user", user.Id.ToString(), null, new { user.UserName });
            return true;
        }, cancellationToken);

    public Task SetWarehousesAsync(
        Guid id, bool allWarehouses, IReadOnlyList<Guid> warehouseIds, Actor actor, CancellationToken cancellationToken)
    {
        List<Guid> wanted = allWarehouses ? [] : (warehouseIds ?? []).Distinct().Order().ToList();
        return InTransactionAsync(actor, async now =>
        {

            // 鎖帳號列：兩位管理員同時設定同一帳號時排隊，不會同時新增同一列撞主鍵（審查 Y5）
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {id} FOR UPDATE", cancellationToken);
            var user = await FindAsync(id);
            if (wanted.Count > 0 && await db.Warehouses.CountAsync(w => wanted.Contains(w.Id), cancellationToken) != wanted.Count)
            {
                throw new DomainRuleException("有不存在的倉庫，請重新整理後再選。");
            }
            var existing = await db.UserWarehouses.Where(x => x.UserId == id).ToListAsync(cancellationToken);
            var before = new { user.AllWarehouses, Warehouses = existing.Select(x => x.WarehouseId).Order().ToList() };
            db.UserWarehouses.RemoveRange(existing.Where(x => !wanted.Contains(x.WarehouseId)));
            db.UserWarehouses.AddRange(wanted.Where(w => existing.All(x => x.WarehouseId != w))
                .Select(w => new UserWarehouse { UserId = id, WarehouseId = w }));
            user.AllWarehouses = allWarehouses;
            Check(await users.UpdateAsync(user));
            AuditLog.Add(db, actor, now, "user.warehouses", "user", id.ToString(), before,
                new { AllWarehouses = allWarehouses, Warehouses = wanted });
            return true;
        }, cancellationToken);
    }

    private async Task<T> InTransactionAsync<T>(Actor actor, Func<DateTimeOffset, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var adminRoleId = RoleSeed.SystemAdminId;
        try
        {
            await db.Database.ExecuteSqlAsync($"SET LOCAL lock_timeout = '5s'", cancellationToken);
            // NO KEY UPDATE：帳號管理的寫入彼此排隊，但不擋 user_roles 外鍵檢查需要的 KEY SHARE（M6-01a 審查 Y-3）
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {adminRoleId} FOR NO KEY UPDATE", cancellationToken);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConcurrencyConflictException("user.busy", "有其他人正在修改帳號，請稍後再試。");
        }
        // 操作者本人此刻仍必須是啟用中的系統管理員：Cookie 裡的角色最多 5 分鐘才重新檢查，
        // 被停用或拿掉角色的管理員不能在這段空窗內繼續操作（例如把自己重新啟用，M6-01a 審查 O-1）。命令列（Guid.Empty）不檢查。
        if (actor.Id != Guid.Empty && !await db.Users.AnyAsync(u => u.Id == actor.Id && u.IsActive
                && db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == adminRoleId), cancellationToken))
        {
            throw new PermissionDeniedException("user.forbidden", "你的帳號已停用或不再是系統管理員，請重新登入。");
        }
        // 被限制倉庫範圍的系統管理員不能管理任何帳號：新帳號預設全部倉庫、重設別人密碼都能用來繞過自己的範圍
        // （M6-04 審查 R2）。帳號管理只交給可作業全部倉庫的系統管理員。
        await WarehouseAccess.EnsureAllAsync(db, actor, cancellationToken);
        var now = clock.GetUtcNow();
        try
        {
            var result = await action(now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            // 交易回滾了，但 UserManager 已改過追蹤中的實體（例如新的 ConcurrencyStamp）；清掉，同一範圍內的下一個操作才會重讀資料庫
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>變更後（同一交易內）至少要剩一個啟用中的系統管理員，否則丟例外讓整個交易回滾。</summary>
    private async Task EnsureActiveAdminAsync(CancellationToken cancellationToken)
    {
        var adminRoleId = RoleSeed.SystemAdminId;
        var count = await db.Users.CountAsync(u => u.IsActive
            && db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == adminRoleId), cancellationToken);
        if (count == 0)
        {
            throw new ConflictException("user.last_admin", "至少要保留一個啟用中的系統管理員。");
        }
    }

    private async Task<AppUser> FindAsync(Guid id) =>
        await users.FindByIdAsync(id.ToString()) ?? throw NotFound();

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> RolesOfAsync(Guid[] userIds, CancellationToken cancellationToken) =>
        (await (from ur in db.UserRoles
                join r in db.Roles on ur.RoleId equals r.Id
                where userIds.Contains(ur.UserId)
                select new { ur.UserId, r.Name }).ToListAsync(cancellationToken))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name!).Order(StringComparer.Ordinal).ToList());

    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> WarehousesOfAsync(Guid[] userIds, CancellationToken cancellationToken) =>
        (await db.UserWarehouses.AsNoTracking().Where(x => userIds.Contains(x.UserId)).ToListAsync(cancellationToken))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.WarehouseId).Order().ToList());

    private static UserSummary ToSummary(AppUser user, IReadOnlyList<string> roles, IReadOnlyList<Guid> warehouses, DateTimeOffset now) =>
        new(user.Id, user.UserName!, user.DisplayName, roles, user.IsActive,
            user.LockoutEnd is { } end && end > now, user.MustChangePassword, user.AllWarehouses, warehouses,
            user.ConcurrencyStamp ?? string.Empty);

    private static string ValidateDisplayName(string? displayName)
    {
        var name = (displayName ?? string.Empty).Trim();
        return name.Length is 0 or > AppUser.DisplayNameMaxLength
            ? throw new DomainRuleException($"顯示名稱必須是 1～{AppUser.DisplayNameMaxLength} 個字。")
            : name;
    }

    private static List<string> ValidateRoles(IReadOnlyList<string>? roles)
    {
        var list = (roles ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            throw new DomainRuleException($"請至少選一個角色（只能查詢的帳號請選「{Roles.Viewer}」）。");
        }
        var unknown = list.FirstOrDefault(r => !Roles.All.Contains(r));
        return unknown is not null ? throw new DomainRuleException($"不認得的角色：{unknown}。") : list;
    }

    /// <summary>Identity 的錯誤轉成中文訊息（帳號重複是 409，其他是輸入錯誤）。</summary>
    private static void Check(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }
        var error = result.Errors.First();
        throw error.Code switch
        {
            "DuplicateUserName" => new ConflictException("user.name_taken", "帳號已存在。"),
            "InvalidUserName" => new DomainRuleException("帳號只能使用英文字母、數字與 - . _ @ +。"),
            "ConcurrencyFailure" => new ConcurrencyConflictException("user.concurrency_conflict", "帳號已被他人修改，請重新載入後再儲存。"),
            _ => new DomainRuleException(string.Join(" ", result.Errors.Select(e => e.Description))),
        };
    }

    private static NotFoundException NotFound() => new("user.not_found", "找不到帳號。");
}

/// <summary>隨機初始密碼（12 字，去掉容易看錯的 0 O 1 l I）。只回傳給呼叫端顯示一次。</summary>
public static class PasswordGenerator
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 12;

    public static string Generate() =>
        new(Enumerable.Range(0, Length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}

/// <summary><see cref="ILoginAuditLog"/> 實作：以登入者本人為操作者寫一筆稽核（單獨 SaveChanges）。</summary>
public sealed class LoginAuditLog(WarehouseDbContext db, TimeProvider clock) : ILoginAuditLog
{
    /// <summary>登入稽核的操作者名稱固定：不用使用者輸入的字串，避免被拿來偽造成「命令列」等操作者（M6-01a 審查 Y-1）。</summary>
    public const string ActorName = "（登入）";

    public async Task RecordAsync(string userName, Guid? userId, LoginOutcome outcome, string? reason, CancellationToken cancellationToken)
    {
        var action = outcome switch
        {
            LoginOutcome.Success => "auth.login_success",
            LoginOutcome.LockedOut => "auth.lockout",
            _ => "auth.login_failed",
        };
        // 帳號存在：記正式帳號名稱。帳號不存在：只記前 2 字＋長度（使用者常把密碼誤打進帳號欄，不能原文保存）
        var recorded = userId is null ? Mask(userName) : userName;
        AuditLog.Add(db, new Actor(userId ?? Guid.Empty, ActorName, false), clock.GetUtcNow(), action, "user",
            userId?.ToString() ?? string.Empty, null, new { UserName = recorded, Reason = reason });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>不存在的帳號只保留前 2 個字與長度，例如「wa…（8 字）」。</summary>
    public static string Mask(string userName)
    {
        var text = userName.Trim();
        return $"{(text.Length <= 2 ? text[..Math.Min(1, text.Length)] : text[..2])}…（{text.Length} 字）";
    }
}
