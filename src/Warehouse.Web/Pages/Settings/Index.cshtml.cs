using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;

namespace Warehouse.Web.Pages.Settings;

/// <summary>
/// 系統設定（系統管理員；M3 先放四個開關，完整設定頁在 M6-05）。每次變更寫入稽核。
/// 表單同時帶「開頁時的值」（Original*），只更新實際改動的項目；別人先改過同一項時顯示衝突，不會默默蓋掉。
/// 關閉倉位註記、屬性、圖片只隱藏畫面，既有資料保留。
/// </summary>
public class IndexModel(ISettingsService settings) : PageModel
{
    [BindProperty]
    public SettingsForm Form { get; set; } = new();

    [BindProperty]
    public SettingsForm Original { get; set; } = new();

    public string? Error { get; private set; }

    public sealed class SettingsForm
    {
        public bool AllowSelfApproveOpening { get; set; }

        public bool LocationNotesEnabled { get; set; }

        public bool LocationAttributesEnabled { get; set; }

        public bool LocationImagesEnabled { get; set; }

        public SystemSettings ToSettings() =>
            new(AllowSelfApproveOpening, LocationNotesEnabled, LocationAttributesEnabled, LocationImagesEnabled);

        public static SettingsForm From(SystemSettings s) => new()
        {
            AllowSelfApproveOpening = s.AllowSelfApproveOpening,
            LocationNotesEnabled = s.LocationNotesEnabled,
            LocationAttributesEnabled = s.LocationAttributesEnabled,
            LocationImagesEnabled = s.LocationImagesEnabled,
        };
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Form = SettingsForm.From(await settings.GetAsync(cancellationToken));
        Original = SettingsForm.From(Form.ToSettings());
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            await settings.UpdateAsync(Original.ToSettings(), Form.ToSettings(), User.ToActor(), cancellationToken);
            TempData["Message"] = "設定已儲存。";
            return RedirectToPage();
        }
        catch (ConcurrencyConflictException ex)
        {
            Error = ex.Message;
            Form = SettingsForm.From(await settings.GetAsync(cancellationToken));
            Original = SettingsForm.From(Form.ToSettings());
            return Page();
        }
    }
}
