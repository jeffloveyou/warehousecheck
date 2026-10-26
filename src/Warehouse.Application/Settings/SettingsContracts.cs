using Warehouse.Application.Abstractions;

namespace Warehouse.Application.Settings;

/// <summary>
/// 系統設定（M3 先放四個開關；完整設定頁在 M6-05）。設定值存在 system_settings 表，
/// 每次變更寫一筆 audit_logs（ADR-0003 D-07：「變更此設定本身要留稽核」）。
/// </summary>
public sealed record SystemSettings(
    bool AllowSelfApproveOpening,
    bool LocationNotesEnabled,
    bool LocationAttributesEnabled,
    bool LocationImagesEnabled)
{
    /// <summary>進出倉單是否需要審核（預設 false：確認即過帳，FR-08）。開啟時出倉單送審即預留（D-26）。</summary>
    public bool DocumentApprovalEnabled { get; init; }

    /// <summary>進出倉單審核時，建立者可否核准自己的單（預設 false；與期初的 D-07 開關分開）。</summary>
    public bool AllowSelfApproveDocuments { get; init; }

    /// <summary>表頭欄位開關與必填（M4-10）。關閉的欄位不顯示、不檢查，既有資料保留。</summary>
    public bool DocumentPurposeEnabled { get; init; } = true;

    public bool DocumentPurposeRequired { get; init; } = true;

    public bool DocumentPartnerEnabled { get; init; } = true;

    public bool DocumentPartnerRequired { get; init; }

    public bool DocumentExternalNoEnabled { get; init; } = true;

    public bool DocumentExternalNoRequired { get; init; }

    public bool DocumentNoteEnabled { get; init; } = true;

    public bool DocumentNoteRequired { get; init; }

    /// <summary>單據附件（照片、掃描檔）是否啟用（預設 true）。</summary>
    public bool DocumentAttachmentsEnabled { get; init; } = true;

    /// <summary>盤點差異可否由盤點單的建立者或送審者自己核准（預設 false，D-07；與期初、單據的自核開關分開）。</summary>
    public bool AllowSelfApproveStocktake { get; init; }

    /// <summary>盲盤（預設 true）：輸入實盤量的畫面與 API 不顯示帳面量，倉庫主管審核時才看得到差異。</summary>
    public bool StocktakeBlindCount { get; init; } = true;

    /// <summary>明細輸入欄位開關（M8-03，預設都開）：關閉的欄位在明細輸入區不顯示；料號本身需要批號或序號時仍會顯示並必填。</summary>
    public bool LineExpiryEnabled { get; init; } = true;

    /// <summary>出倉明細可指定手動預留。</summary>
    public bool LineReservationEnabled { get; init; } = true;

    /// <summary>移庫可移到其他倉庫（關閉時目的儲位只能在同一倉庫）。</summary>
    public bool LineCrossTransferEnabled { get; init; } = true;

    /// <summary>顯示批號、序號欄（關閉時只在料號需要時顯示）。</summary>
    public bool LineLotSerialEnabled { get; init; } = true;

    /// <summary>
    /// 已有異動紀錄的物料可否改基本單位、追蹤模式（M8-05，ADR-0023；預設 false）。
    /// 開啟後仍有安全底線：沒有未完成單據、有效預留、進行中盤點；改追蹤模式時結存須為 0。
    /// </summary>
    public bool AllowItemUnitTrackingChangeWithActivity { get; init; }

    /// <summary>列印抬頭的公司名稱（空白＝不印）；最多 <see cref="SettingKeys.TextMaxLength"/> 字。</summary>
    public string PrintCompanyName { get; init; } = string.Empty;

    /// <summary>資料保留（M7-02，ADR-0021）：每日清理工作刪除超過期限的紀錄；不可低於 <see cref="RetentionLimits"/> 的下限。</summary>
    public int RetentionAuditYears { get; init; } = RetentionLimits.AuditYearsDefault;

    public int RetentionLoginDays { get; init; } = RetentionLimits.LoginDaysDefault;

    public int RetentionProcessedCommandsDays { get; init; } = RetentionLimits.ProcessedCommandsDaysDefault;

    public int RetentionPostingFailuresDays { get; init; } = RetentionLimits.PostingFailuresDaysDefault;

    public int RetentionImportBatchesDays { get; init; } = RetentionLimits.ImportBatchesDaysDefault;
}

/// <summary>
/// 資料保留期限的預設值與下限（ADR-0021）。庫存單據、異動、結存不在清理範圍，永久保留。
/// 操作日誌與登入紀錄的下限同時寫在資料庫函式 purge_audit_logs（應用程式改不了）。
/// </summary>
public static class RetentionLimits
{
    /// <summary>操作日誌（登入以外）：D-13 保留 7 年。</summary>
    public const int AuditYearsDefault = 7;
    public const int AuditYearsMin = 7;

    /// <summary>登入紀錄（auth.*）：1 年。</summary>
    public const int LoginDaysDefault = 365;
    public const int LoginDaysMin = 365;

    /// <summary>冪等鍵：同一鍵在期限內重送不會重複過帳；客戶端重試都在幾分鐘內，30 天以上即安全。</summary>
    public const int ProcessedCommandsDaysDefault = 90;
    public const int ProcessedCommandsDaysMin = 30;

    /// <summary>過帳失敗紀錄（庫存異常頁）。</summary>
    public const int PostingFailuresDaysDefault = 365;
    public const int PostingFailuresDaysMin = 90;

    /// <summary>匯入批次（含解析後的整份資料）：試算 24 小時內有效，確認結果另有操作日誌。</summary>
    public const int ImportBatchesDaysDefault = 30;
    public const int ImportBatchesDaysMin = 2;

    /// <summary>上限（避免打錯成天文數字）：100 年。</summary>
    public const int YearsMax = 100;
    public const int DaysMax = 36500;
}

/// <summary>設定鍵（資料庫 system_settings.key）。改名會讓既有設定失效，只能新增。</summary>
public static class SettingKeys
{
    /// <summary>期初單（之後含盤差）是否允許建立者自己核准；預設 false（D-07）。</summary>
    public const string AllowSelfApproveOpening = "stock.opening.allow_self_approval";

    public const string LocationNotesEnabled = "location.notes.enabled";
    public const string LocationAttributesEnabled = "location.attributes.enabled";
    public const string LocationImagesEnabled = "location.images.enabled";

    public const string DocumentApprovalEnabled = "stock.documents.approval_enabled";
    public const string AllowSelfApproveDocuments = "stock.documents.allow_self_approval";
    public const string DocumentPurposeEnabled = "stock.documents.field.purpose.enabled";
    public const string DocumentPurposeRequired = "stock.documents.field.purpose.required";
    public const string DocumentPartnerEnabled = "stock.documents.field.partner.enabled";
    public const string DocumentPartnerRequired = "stock.documents.field.partner.required";
    public const string DocumentExternalNoEnabled = "stock.documents.field.external_no.enabled";
    public const string DocumentExternalNoRequired = "stock.documents.field.external_no.required";
    public const string DocumentNoteEnabled = "stock.documents.field.note.enabled";
    public const string DocumentNoteRequired = "stock.documents.field.note.required";
    public const string DocumentAttachmentsEnabled = "stock.documents.attachments.enabled";
    public const string PrintCompanyName = "print.company_name";
    public const string AllowSelfApproveStocktake = "stock.stocktake.allow_self_approval";
    public const string StocktakeBlindCount = "stock.stocktake.blind_count";
    public const string LineExpiryEnabled = "stock.documents.line.expiry.enabled";
    public const string LineReservationEnabled = "stock.documents.line.reservation.enabled";
    public const string LineCrossTransferEnabled = "stock.documents.line.cross_transfer.enabled";
    public const string LineLotSerialEnabled = "stock.documents.line.lot_serial.enabled";
    public const string AllowItemUnitTrackingChangeWithActivity = "items.allow_unit_tracking_change_with_activity";
    public const string RetentionAuditYears = "retention.audit_years";
    public const string RetentionLoginDays = "retention.login_days";
    public const string RetentionProcessedCommandsDays = "retention.processed_commands_days";
    public const string RetentionPostingFailuresDays = "retention.posting_failures_days";
    public const string RetentionImportBatchesDays = "retention.import_batches_days";

    /// <summary>文字設定的長度上限。</summary>
    public const int TextMaxLength = 100;
}

public interface ISettingsService
{
    Task<SystemSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 儲存設定。<paramref name="original"/> 是開表單時讀到的值：只更新使用者實際改動的項目，
    /// 若該項目在這段期間已被別人改過，丟 ConcurrencyConflictException（不會默默蓋掉別人的修改）。
    /// 每個變更的項目寫一筆稽核（前後值、操作者、時間）。
    /// </summary>
    Task UpdateAsync(SystemSettings original, SystemSettings updated, Actor actor, CancellationToken cancellationToken);
}
