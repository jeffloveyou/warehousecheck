using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Warehouse.Api.Authorization;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Web.Security;

/// <summary>
/// 登入設定（ADR-0012）：ASP.NET Core Identity＋Cookie，不使用 Identity UI 套件（頁面自己寫在 Pages/Account）。
/// Cookie 加密金鑰是 M0-12 的共用 Data Protection 金鑰，所以 A/B 切換後不需要重新登入。
/// </summary>
public static class IdentitySetup
{
    public const string CookieName = "Warehouse.Auth";

    public static IServiceCollection AddWarehouseIdentity(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<WarehouseDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<WarehouseClaimsPrincipalFactory>();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            // API 用狀態碼回應，不轉址到登入頁（手機／PDA 程式才能判斷）
            options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
        });

        // 每 5 分鐘重新檢查一次帳號（停用、改密碼、角色變更會讓舊 Cookie 失效）
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
        return services;
    }

    /// <summary>必須先改密碼的帳號，只能使用改密碼、登出頁面；API 回 403。</summary>
    public static IApplicationBuilder UseMustChangePassword(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (context.User.HasClaim(ActorClaims.MustChangePassword, "true")
                && !path.StartsWithSegments("/Account/ChangePassword")
                && !path.StartsWithSegments("/Account/Logout")
                && !path.StartsWithSegments("/health")
                && !path.StartsWithSegments("/sw.js")) // Service Worker 腳本不含資料，轉址會讓註冊失敗（M5-02）
            {
                if (path.StartsWithSegments("/api"))
                {
                    // 帶錯誤碼，讓手機／PDA 程式分辨「要先改密碼」與「權限不足」
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(
                        new { status = 403, errorCode = "auth.must_change_password", detail = "請先登入網頁變更初始密碼。" },
                        options: null, contentType: "application/problem+json");
                    return;
                }
                context.Response.Redirect("/Account/ChangePassword");
                return;
            }
            await next(context);
        });

    private static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = status;
        }
        else
        {
            // RedirectUri 是完整網址（含主機），經 YARP 代理時主機會是內部槽位（127.0.0.1:5080），
            // 外部裝置連不到；只送相對路徑，瀏覽器會沿用原本的網址（M1-03 審查 R1）
            context.Response.Redirect(new Uri(context.RedirectUri).PathAndQuery);
        }
        return Task.CompletedTask;
    }
}

/// <summary>登入時把顯示名稱與「需改密碼」放進 Cookie 的 claims，之後每個請求不必查資料庫。</summary>
public sealed class WarehouseClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ActorClaims.DisplayName, user.DisplayName));
        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(ActorClaims.MustChangePassword, "true"));
        }
        return identity;
    }
}
