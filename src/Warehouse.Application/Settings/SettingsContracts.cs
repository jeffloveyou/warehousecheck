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
