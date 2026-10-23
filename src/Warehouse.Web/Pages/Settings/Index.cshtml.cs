using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Settings;

/// <summary>
/// 系統設定（系統管理員；M3 先放四個開關、M4 加進出倉單區塊，完整設定頁在 M6-05）。每次變更寫入稽核。
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

        public bool DocumentApprovalEnabled { get; set; }

        public bool AllowSelfApproveDocuments { get; set; }

        public bool DocumentPurposeEnabled { get; set; }

        public bool DocumentPurposeRequired { get; set; }

        public bool DocumentPartnerEnabled { get; set; }

        public bool DocumentPartnerRequired { get; set; }

        public bool DocumentExternalNoEnabled { get; set; }

        public bool DocumentExternalNoRequired { get; set; }

        public bool DocumentNoteEnabled { get; set; }

        public bool DocumentNoteRequired { get; set; }

        public bool DocumentAttachmentsEnabled { get; set; }

        public bool AllowSelfApproveStocktake { get; set; }

        public bool StocktakeBlindCount { get; set; }

        /// <summary>列印抬頭的公司名稱（空白＝不印）；表單空白時綁定為 null。</summary>
        public string? PrintCompanyName { get; set; }

        public SystemSettings ToSettings() =>
            new(AllowSelfApproveOpening, LocationNotesEnabled, LocationAttributesEnabled, LocationImagesEnabled)
            {
                DocumentApprovalEnabled = DocumentApprovalEnabled,
                AllowSelfApproveDocuments = AllowSelfApproveDocuments,
                DocumentPurposeEnabled = DocumentPurposeEnabled,
                DocumentPurposeRequired = DocumentPurposeRequired,
                DocumentPartnerEnabled = DocumentPartnerEnabled,
                DocumentPartnerRequired = DocumentPartnerRequired,
                DocumentExternalNoEnabled = DocumentExternalNoEnabled,
                DocumentExternalNoRequired = DocumentExternalNoRequired,
                DocumentNoteEnabled = DocumentNoteEnabled,
                DocumentNoteRequired = DocumentNoteRequired,
                DocumentAttachmentsEnabled = DocumentAttachmentsEnabled,
                AllowSelfApproveStocktake = AllowSelfApproveStocktake,
                StocktakeBlindCount = StocktakeBlindCount,
                PrintCompanyName = (PrintCompanyName ?? string.Empty).Trim(),
            };

        public static SettingsForm From(SystemSettings s) => new()
        {
            AllowSelfApproveOpening = s.AllowSelfApproveOpening,
            LocationNotesEnabled = s.LocationNotesEnabled,
            LocationAttributesEnabled = s.LocationAttributesEnabled,
            LocationImagesEnabled = s.LocationImagesEnabled,
            DocumentApprovalEnabled = s.DocumentApprovalEnabled,
            AllowSelfApproveDocuments = s.AllowSelfApproveDocuments,
            DocumentPurposeEnabled = s.DocumentPurposeEnabled,
            DocumentPurposeRequired = s.DocumentPurposeRequired,
            DocumentPartnerEnabled = s.DocumentPartnerEnabled,
            DocumentPartnerRequired = s.DocumentPartnerRequired,
            DocumentExternalNoEnabled = s.DocumentExternalNoEnabled,
            DocumentExternalNoRequired = s.DocumentExternalNoRequired,
            DocumentNoteEnabled = s.DocumentNoteEnabled,
            DocumentNoteRequired = s.DocumentNoteRequired,
            DocumentAttachmentsEnabled = s.DocumentAttachmentsEnabled,
            AllowSelfApproveStocktake = s.AllowSelfApproveStocktake,
            StocktakeBlindCount = s.StocktakeBlindCount,
            PrintCompanyName = s.PrintCompanyName,
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
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            // 例如公司名稱超過 100 字：保留使用者輸入、顯示原因（M4-03 審查 #3）
            Error = ex.Message;
            return Page();
        }
    }
}
