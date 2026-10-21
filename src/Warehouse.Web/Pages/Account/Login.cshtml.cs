using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Web.Pages.Account;

/// <summary>
/// 登入頁。連續失敗 5 次鎖定 15 分鐘（IdentitySetup）；停用的帳號不能登入。
/// 錯誤訊息不區分「帳號不存在」與「密碼錯誤」，避免被用來猜帳號。
/// </summary>
public class LoginModel(SignInManager<AppUser> signInManager, UserManager<AppUser> userManager) : PageModel
{
    private const string InvalidMessage = "帳號或密碼錯誤。";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "請輸入帳號。")]
        [Display(Name = "帳號")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入密碼。")]
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

        var user = await userManager.FindByNameAsync(Input.UserName.Trim());
        if (user is null || !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, InvalidMessage);
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(user, Input.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "登入失敗次數過多，帳號已暫時鎖定，請 15 分鐘後再試或請管理員重設密碼。");
            return Page();
        }
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, InvalidMessage);
            return Page();
        }

        if (user.MustChangePassword)
        {
            return RedirectToPage("/Account/ChangePassword");
        }
        // 只允許站內網址，避免登入後被導到外部網站（open redirect）
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/Items");
    }
}
