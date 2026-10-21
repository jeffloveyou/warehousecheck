using Microsoft.AspNetCore.Identity;

namespace Warehouse.Infrastructure.Identity;

/// <summary>
/// 登入帳號（ASP.NET Core Identity，ADR-0012）。帳號由命令列 <c>users create</c> 建立（M6-01 才有管理畫面）。
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 100;

    /// <summary>顯示名稱，會快照寫進審核紀錄的 actor_name。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>為 true 時登入後只能先改密碼（命令列產生的初始密碼、重設密碼後）。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>停用的帳號不能登入；不刪除帳號，審核紀錄的操作者仍可追查。</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>內建角色的固定 ID（種子資料，之後不可修改）。</summary>
public static class RoleSeed
{
    public static readonly IReadOnlyList<IdentityRole<Guid>> All =
    [
        Role("0b1f6a10-0003-4c3a-9a00-000000000001", Application.Abstractions.Roles.SystemAdmin),
        Role("0b1f6a10-0003-4c3a-9a00-000000000002", Application.Abstractions.Roles.ItemManager),
        Role("0b1f6a10-0003-4c3a-9a00-000000000003", Application.Abstractions.Roles.Operator),
        Role("0b1f6a10-0003-4c3a-9a00-000000000004", Application.Abstractions.Roles.WarehouseManager),
    ];

    // ConcurrencyStamp 固定，否則每次產生 Migration 都會出現 UpdateData
    private static IdentityRole<Guid> Role(string id, string name) => new()
    {
        Id = Guid.Parse(id),
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = id,
    };
}
