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
public class IndexModel(ISettingsService settings, Warehouse.Application.Audit.IAuditQueryService audit, TimeProvider clock) : PageModel
{
    /// <summary>最近的設定變更（setting.update 稽核，最近 90 天前 20 筆，M6-05）。</summary>
    public IReadOnlyList<Warehouse.Application.Audit.AuditRow> RecentChanges { get; private set; } = [];

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

        public bool LineExpiryEnabled { get; set; }

        public bool LineReservationEnabled { get; set; }

        public bool LineCrossTransferEnabled { get; set; }

        public bool LineLotSerialEnabled { get; set; }

        public bool AllowItemUnitTrackingChangeWithActivity { get; set; }

        public bool AllowItemCodeChange { get; set; }

        /// <summary>列印抬頭的公司名稱（空白＝不印）；表單空白時綁定為 null。</summary>
        public string? PrintCompanyName { get; set; }

        public int RememberMeDays { get; set; }

        public int RetentionAuditYears { get; set; }

        public int RetentionLoginDays { get; set; }

        public int RetentionProcessedCommandsDays { get; set; }

        public int RetentionPostingFailuresDays { get; set; }

        public int RetentionImportBatchesDays { get; set; }

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
                LineExpiryEnabled = LineExpiryEnabled,
                LineReservationEnabled = LineReservationEnabled,
                LineCrossTransferEnabled = LineCrossTransferEnabled,
                LineLotSerialEnabled = LineLotSerialEnabled,
                AllowItemUnitTrackingChangeWithActivity = AllowItemUnitTrackingChangeWithActivity,
                AllowItemCodeChange = AllowItemCodeChange,
                PrintCompanyName = (PrintCompanyName ?? string.Empty).Trim(),
                RememberMeDays = RememberMeDays,
                RetentionAuditYears = RetentionAuditYears,
                RetentionLoginDays = RetentionLoginDays,
                RetentionProcessedCommandsDays = RetentionProcessedCommandsDays,
                RetentionPostingFailuresDays = RetentionPostingFailuresDays,
                RetentionImportBatchesDays = RetentionImportBatchesDays,
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
            LineExpiryEnabled = s.LineExpiryEnabled,
            LineReservationEnabled = s.LineReservationEnabled,
            LineCrossTransferEnabled = s.LineCrossTransferEnabled,
            LineLotSerialEnabled = s.LineLotSerialEnabled,
            AllowItemUnitTrackingChangeWithActivity = s.AllowItemUnitTrackingChangeWithActivity,
            AllowItemCodeChange = s.AllowItemCodeChange,
            PrintCompanyName = s.PrintCompanyName,
            RememberMeDays = s.RememberMeDays,
            RetentionAuditYears = s.RetentionAuditYears,
            RetentionLoginDays = s.RetentionLoginDays,
            RetentionProcessedCommandsDays = s.RetentionProcessedCommandsDays,
            RetentionPostingFailuresDays = s.RetentionPostingFailuresDays,
            RetentionImportBatchesDays = s.RetentionImportBatchesDays,
        };
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Form = SettingsForm.From(await settings.GetAsync(cancellationToken));
        Original = SettingsForm.From(Form.ToSettings());
        await LoadChangesAsync(cancellationToken);
    }

    private async Task LoadChangesAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        RecentChanges = (await audit.SearchAsync(new Warehouse.Application.Audit.AuditQuery(
            now.AddDays(-90), now.AddMinutes(1), ActionPrefix: "setting.", EntityType: "setting"), cancellationToken)).Items;
    }

    /// <summary>設定鍵的中文名稱（最近變更列表用）。</summary>
    public static string LabelOf(string key) => key switch
    {
        SettingKeys.AllowSelfApproveOpening => "期初單允許自核",
        SettingKeys.LocationNotesEnabled => "倉位自由備註",
        SettingKeys.LocationAttributesEnabled => "倉位結構化屬性",
        SettingKeys.LocationImagesEnabled => "倉位圖片",
        SettingKeys.DocumentApprovalEnabled => "單據需要審核",
        SettingKeys.AllowSelfApproveDocuments => "進出倉單允許自核",
        SettingKeys.DocumentPurposeEnabled => "表頭：用途啟用",
        SettingKeys.DocumentPurposeRequired => "表頭：用途必填",
        SettingKeys.DocumentPartnerEnabled => "表頭：往來對象啟用",
        SettingKeys.DocumentPartnerRequired => "表頭：往來對象必填",
        SettingKeys.DocumentExternalNoEnabled => "表頭：外部單號啟用",
        SettingKeys.DocumentExternalNoRequired => "表頭：外部單號必填",
        SettingKeys.DocumentNoteEnabled => "表頭：備註啟用",
        SettingKeys.DocumentNoteRequired => "表頭：備註必填",
        SettingKeys.DocumentAttachmentsEnabled => "單據附件",
        SettingKeys.PrintCompanyName => "列印公司名稱",
        SettingKeys.AllowSelfApproveStocktake => "盤差允許自核",
        SettingKeys.StocktakeBlindCount => "盲盤",
        SettingKeys.LineExpiryEnabled => "明細：有效期限",
        SettingKeys.LineReservationEnabled => "明細：指定預留",
        SettingKeys.LineCrossTransferEnabled => "明細：跨倉移庫",
        SettingKeys.LineLotSerialEnabled => "明細：批號序號欄",
        SettingKeys.AllowItemUnitTrackingChangeWithActivity => "有異動的物料可改單位／追蹤模式",
        SettingKeys.AllowItemCodeChange => "已核准的物料可改料號",
        SettingKeys.RememberMeDays => "記住登入天數",
        SettingKeys.RetentionAuditYears => "操作日誌保留年數",
        SettingKeys.RetentionLoginDays => "登入紀錄保留天數",
        SettingKeys.RetentionProcessedCommandsDays => "冪等紀錄保留天數",
        SettingKeys.RetentionPostingFailuresDays => "過帳失敗紀錄保留天數",
        SettingKeys.RetentionImportBatchesDays => "匯入批次保留天數",
        _ => key,
    };

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
            await LoadChangesAsync(cancellationToken);
            return Page();
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            // 例如公司名稱超過 100 字：保留使用者輸入、顯示原因（M4-03 審查 #3）
            Error = ex.Message;
            await LoadChangesAsync(cancellationToken);
            return Page();
        }
    }
}
