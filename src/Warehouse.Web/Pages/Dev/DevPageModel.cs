using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Warehouse.Web.Pages.Dev;

/// <summary>
/// 開發用頁面的基底類別：只在 Development 環境可用，其他環境一律回 404，
/// 避免正式環境露出憑證下載或測試頁。
/// </summary>
public abstract class DevPageModel(IWebHostEnvironment environment) : PageModel
{
    protected IWebHostEnvironment Environment { get; } = environment;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (!Environment.IsDevelopment())
        {
            context.Result = NotFound();
        }
    }
}
