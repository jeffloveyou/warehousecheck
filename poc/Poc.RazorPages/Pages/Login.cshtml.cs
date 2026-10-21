using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Poc.RazorPages.Pages;

public class LoginModel : PageModel
{
    [BindProperty(Name = "username")]
    public string? Username { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public IActionResult OnPost()
    {
        var name = Username?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Error = "請輸入帳號";
            return Page();
        }

        // PoC 假登入：只設定 HttpOnly cookie，不做密碼驗證。
        Response.Cookies.Append("poc_user", name, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
        return Redirect("/");
    }
}
