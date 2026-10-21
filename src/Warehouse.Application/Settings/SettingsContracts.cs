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
    bool LocationImagesEnabled);

/// <summary>設定鍵（資料庫 system_settings.key）。改名會讓既有設定失效，只能新增。</summary>
public static class SettingKeys
{
    /// <summary>期初單（之後含盤差）是否允許建立者自己核准；預設 false（D-07）。</summary>
    public const string AllowSelfApproveOpening = "stock.opening.allow_self_approval";

    public const string LocationNotesEnabled = "location.notes.enabled";
    public const string LocationAttributesEnabled = "location.attributes.enabled";
    public const string LocationImagesEnabled = "location.images.enabled";
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
