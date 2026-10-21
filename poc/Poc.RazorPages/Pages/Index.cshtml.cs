using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Poc.RazorPages.Pages;

public class IndexModel : PageModel
{
    public string Username { get; private set; } = string.Empty;

    public void OnGet()
    {
        Username = Request.Cookies["poc_user"] ?? string.Empty;
    }
}
