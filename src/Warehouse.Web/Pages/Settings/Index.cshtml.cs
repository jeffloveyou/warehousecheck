using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Settings;

namespace Warehouse.Web.Pages.Settings;

/// <summary>
/// 系統設定（系統管理員；M3 先放四個開關，完整設定頁在 M6-05）。每次變更寫入稽核。
/// 關閉倉位註記、屬性、圖片只隱藏畫面，既有資料保留。
/// </summary>
public class IndexModel(ISettingsService settings) : PageModel
{
    [BindProperty]
    public bool AllowSelfApproveOpening { get; set; }

    [BindProperty]
    public bool LocationNotesEnabled { get; set; }

    [BindProperty]
    public bool LocationAttributesEnabled { get; set; }

    [BindProperty]
    public bool LocationImagesEnabled { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken);
        (AllowSelfApproveOpening, LocationNotesEnabled, LocationAttributesEnabled, LocationImagesEnabled) = (
            current.AllowSelfApproveOpening, current.LocationNotesEnabled, current.LocationAttributesEnabled,
            current.LocationImagesEnabled);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await settings.UpdateAsync(
            new SystemSettings(AllowSelfApproveOpening, LocationNotesEnabled, LocationAttributesEnabled, LocationImagesEnabled),
            User.ToActor(), cancellationToken);
        TempData["Message"] = "設定已儲存。";
        return RedirectToPage();
    }
}
