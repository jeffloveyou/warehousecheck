using Warehouse.Application.Abstractions;

namespace Warehouse.Application.Accounts;

/// <summary>帳號列表的一列（不含密碼雜湊等秘密）。</summary>
public sealed record UserSummary(
    Guid Id,
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    bool IsLockedOut,
    bool MustChangePassword,
    string ConcurrencyStamp);

/// <summary>新增帳號：初始密碼由系統產生，只在回傳結果出現一次。</summary>
public sealed record CreateUserRequest(string UserName, string DisplayName, IReadOnlyList<string> Roles);

/// <summary>修改顯示名稱與角色。<paramref name="ConcurrencyStamp"/> 是開表單時讀到的值，被別人改過就拒絕。</summary>
public sealed record UpdateUserRequest(Guid Id, string DisplayName, IReadOnlyList<string> Roles, string ConcurrencyStamp);

/// <summary>新增帳號或重設密碼的結果：密碼只顯示這一次，不寫入任何紀錄。</summary>
public sealed record UserPasswordResult(Guid UserId, string UserName, string Password);

/// <summary>
/// 帳號管理（M6-01，ADR-0017）：管理畫面與命令列 <c>users …</c> 共用同一套規則。
/// 每個變更都更新 security stamp（已登入的 Cookie 最慢 5 分鐘內失效）並寫稽核 <c>user.*</c>，與資料變更在同一交易。
/// 防呆：不能停用自己、不能移除自己的 SystemAdmin、至少保留一個啟用中的 SystemAdmin。
/// 錯誤碼：<c>user.not_found</c>、<c>user.name_taken</c>、<c>user.concurrency_conflict</c>、<c>user.last_admin</c>、<c>user.self</c>。
/// </summary>
public interface IUserAdminService
{
    Task<PagedResult<UserSummary>> ListAsync(string? keyword, bool includeInactive, int page, CancellationToken cancellationToken);

    Task<UserSummary> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<UserPasswordResult> CreateAsync(CreateUserRequest request, Actor actor, CancellationToken cancellationToken);

    Task UpdateAsync(UpdateUserRequest request, Actor actor, CancellationToken cancellationToken);

    Task SetActiveAsync(Guid id, bool isActive, Actor actor, CancellationToken cancellationToken);

    /// <summary>產生新的隨機密碼、要求下次登入改密碼，並解除鎖定。</summary>
    Task<UserPasswordResult> ResetPasswordAsync(Guid id, Actor actor, CancellationToken cancellationToken);

    Task UnlockAsync(Guid id, Actor actor, CancellationToken cancellationToken);
}

/// <summary>登入結果（寫入稽核的動作名稱）。</summary>
public enum LoginOutcome
{
    Success,
    Failed,
    LockedOut,
}

/// <summary>
/// 登入稽核（M6-01）：成功、失敗、鎖定都寫 audit_logs。帳號存在時記正式帳號名稱，不存在時只記遮罩值；不記密碼、不記 IP。
/// 帳號不存在時 <paramref name="userId"/> 為 null；<paramref name="reason"/> 例如 inactive（帳號已停用）。
/// </summary>
public interface ILoginAuditLog
{
    Task RecordAsync(string userName, Guid? userId, LoginOutcome outcome, string? reason, CancellationToken cancellationToken);
}
