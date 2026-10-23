using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Accounts;
using Warehouse.Infrastructure.Identity;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Account;

/// <summary>
/// 登入頁。連續失敗 5 次鎖定 15 分鐘（IdentitySetup）；停用的帳號不能登入。
/// 錯誤訊息不區分「帳號不存在」與「密碼錯誤」，避免被用來猜帳號；帳號不存在或停用時也做一次密碼雜湊比對，
/// 回應時間與「密碼錯誤」相近（M1-03 審查 S3）。每次結果寫稽核（M6-01），同一 IP 每分鐘嘗試次數有上限（<see cref="LoginThrottle"/>）。
/// </summary>
public class LoginModel(
    SignInManager<AppUser> signInManager, UserManager<AppUser> userManager, ILoginAuditLog audit, LoginThrottle throttle) : PageModel
{
    private const string InvalidMessage = "帳號或密碼錯誤。";

    // 帳號不存在時拿來比對的假雜湊（與真帳號相同的演算法與成本）
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString()));

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "請輸入帳號。")]
        [StringLength(256, ErrorMessage = "帳號太長。")]
        [Display(Name = "帳號")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入密碼。")]
        [StringLength(256, ErrorMessage = "密碼太長。")]
        [DataType(DataType.Password)]
        [Display(Name = "密碼")]
        public string Password { get; set; } = string.Empty;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!throttle.TryAcquire(HttpContext))
        {
            ModelState.AddModelError(string.Empty, "登入嘗試太頻繁，請 1 分鐘後再試。");
            return Page();
        }

        var userName = Input.UserName.Trim();
        // 稽核是安全紀錄：用戶端中途斷線也要寫完（M6-01a 審查 Y-2）
        var cancellationToken = CancellationToken.None;
        var user = await userManager.FindByNameAsync(userName);
        if (user is null || !user.IsActive)
        {
            userManager.PasswordHasher.VerifyHashedPassword(user ?? new AppUser(), DummyHash.Value, Input.Password);
            await audit.RecordAsync(user?.UserName ?? userName, user?.Id, LoginOutcome.Failed, user is null ? null : "inactive", cancellationToken);
            ModelState.AddModelError(string.Empty, InvalidMessage);
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(user, Input.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            await audit.RecordAsync(user.UserName!, user.Id, LoginOutcome.LockedOut, null, cancellationToken);
            ModelState.AddModelError(string.Empty, "登入失敗次數過多，帳號已暫時鎖定，請 15 分鐘後再試或請管理員重設密碼。");
            return Page();
        }
        if (!result.Succeeded)
        {
            await audit.RecordAsync(user.UserName!, user.Id, LoginOutcome.Failed, null, cancellationToken);
            ModelState.AddModelError(string.Empty, InvalidMessage);
            return Page();
        }
        await audit.RecordAsync(user.UserName!, user.Id, LoginOutcome.Success, null, cancellationToken);

        if (user.MustChangePassword)
        {
            return RedirectToPage("/Account/ChangePassword");
        }
        // 只允許站內網址，避免登入後被導到外部網站（open redirect）
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/Items");
    }
}
