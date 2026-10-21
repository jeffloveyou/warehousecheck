using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Web.Pages.Account;

/// <summary>改密碼。命令列建立或重設的帳號（MustChangePassword）登入後只能先到這裡。</summary>
public class ChangePasswordModel(SignInManager<AppUser> signInManager, UserManager<AppUser> userManager) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool MustChange { get; private set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "請輸入目前密碼。")]
        [DataType(DataType.Password)]
        [Display(Name = "目前密碼")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入新密碼。")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "新密碼至少 8 個字元。")]
        [DataType(DataType.Password)]
        [Display(Name = "新密碼")]
        public string NewPassword { get; set; } = string.Empty;

        [Compare(nameof(NewPassword), ErrorMessage = "兩次輸入的新密碼不同。")]
        [DataType(DataType.Password)]
        [Display(Name = "再輸入一次")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }
        MustChange = user.MustChangePassword;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }
        MustChange = user.MustChangePassword;
        if (!ModelState.IsValid)
        {
            return Page();
        }
        if (Input.NewPassword == Input.CurrentPassword)
        {
            ModelState.AddModelError(string.Empty, "新密碼不可與目前密碼相同。");
            return Page();
        }

        // 先清旗標：ChangePasswordAsync 成功時會連同旗標一次寫入（避免密碼已改、旗標卻沒清）
        var mustChangeBefore = user.MustChangePassword;
        user.MustChangePassword = false;
        var result = await userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            user.MustChangePassword = mustChangeBefore;
            if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
            {
                // 目前密碼錯誤也計入失敗次數：Cookie 被盜時不能在這裡無限次猜密碼（審查 S2）
                await userManager.AccessFailedAsync(user);
                if (await userManager.IsLockedOutAsync(user))
                {
                    await signInManager.SignOutAsync();
                    return RedirectToPage("/Account/Login");
                }
            }
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Code == "PasswordMismatch" ? "目前密碼錯誤。" : error.Description);
            }
            return Page();
        }
        // 重新發 Cookie：拿掉「需改密碼」claim，並套用新的 security stamp
        await signInManager.RefreshSignInAsync(user);
        TempData["Message"] = "密碼已變更。";
        return RedirectToPage("/Items/Index");
    }
}
