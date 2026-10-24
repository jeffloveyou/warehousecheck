using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Settings;

/// <summary>
/// <see cref="ISettingsService"/> 實作：設定存 system_settings，變更與稽核在同一交易寫入。
/// 鍵不存在時用預設值（與種子資料相同），新版本新增的鍵即使舊資料庫還沒有也能運作。
/// </summary>
public sealed class SettingsService(WarehouseDbContext db, TimeProvider clock) : ISettingsService
{
    private static readonly SystemSettings Defaults = new(
        AllowSelfApproveOpening: false,
        LocationNotesEnabled: true,
        LocationAttributesEnabled: true,
        LocationImagesEnabled: true);

    public async Task<SystemSettings> GetAsync(CancellationToken cancellationToken)
    {
        var values = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        return From(values);
    }

    public async Task UpdateAsync(
        SystemSettings original, SystemSettings updated, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // 鎖住設定表的所有列：同時儲存的兩個請求排隊執行，後到的一方看得到先到的結果
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM system_settings FOR UPDATE", cancellationToken);
        var now = clock.GetUtcNow();
        var existing = await db.SystemSettings.ToDictionaryAsync(s => s.Key, cancellationToken);
        var current = From(existing.ToDictionary(e => e.Key, e => e.Value.Value));

        foreach (var (key, before, after, inDatabase) in Pairs(original, updated, current))
        {
            if (before == after)
            {
                continue; // 使用者沒有改這一項：不動它（避免把別人剛改的值蓋回去）
            }
            if (inDatabase != before)
            {
                throw new ConcurrencyConflictException(
                    "setting.concurrency_conflict", "設定已被他人修改，請重新載入後再儲存。");
            }
            var text = after;
            if (existing.TryGetValue(key, out var row))
            {
                AuditLog.Add(db, actor, now, "setting.update", "setting", key, new { value = row.Value }, new { value = text });
                row.Set(text, actor.Name, now);
            }
            else
            {
                // 舊資料庫還沒有這個鍵：新增一列並記錄（變更前為空）
                row = SystemSetting.Seed(key, text);
                row.Set(text, actor.Name, now);
                db.SystemSettings.Add(row);
                AuditLog.Add(db, actor, now, "setting.update", "setting", key, null, new { value = text });
            }
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConcurrencyConflictException("setting.concurrency_conflict", "設定已被他人修改，請重新載入後再儲存。");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static SystemSettings From(IReadOnlyDictionary<string, string> values)
    {
        bool Flag(string key, bool fallback) => values.TryGetValue(key, out var v) ? v == "true" : fallback;
        int Number(string key, int fallback) => values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
        return new SystemSettings(
            Flag(SettingKeys.AllowSelfApproveOpening, Defaults.AllowSelfApproveOpening),
            Flag(SettingKeys.LocationNotesEnabled, Defaults.LocationNotesEnabled),
            Flag(SettingKeys.LocationAttributesEnabled, Defaults.LocationAttributesEnabled),
            Flag(SettingKeys.LocationImagesEnabled, Defaults.LocationImagesEnabled))
        {
            DocumentApprovalEnabled = Flag(SettingKeys.DocumentApprovalEnabled, Defaults.DocumentApprovalEnabled),
            AllowSelfApproveDocuments = Flag(SettingKeys.AllowSelfApproveDocuments, Defaults.AllowSelfApproveDocuments),
            DocumentPurposeEnabled = Flag(SettingKeys.DocumentPurposeEnabled, Defaults.DocumentPurposeEnabled),
            DocumentPurposeRequired = Flag(SettingKeys.DocumentPurposeRequired, Defaults.DocumentPurposeRequired),
            DocumentPartnerEnabled = Flag(SettingKeys.DocumentPartnerEnabled, Defaults.DocumentPartnerEnabled),
            DocumentPartnerRequired = Flag(SettingKeys.DocumentPartnerRequired, Defaults.DocumentPartnerRequired),
            DocumentExternalNoEnabled = Flag(SettingKeys.DocumentExternalNoEnabled, Defaults.DocumentExternalNoEnabled),
            DocumentExternalNoRequired = Flag(SettingKeys.DocumentExternalNoRequired, Defaults.DocumentExternalNoRequired),
            DocumentNoteEnabled = Flag(SettingKeys.DocumentNoteEnabled, Defaults.DocumentNoteEnabled),
            DocumentNoteRequired = Flag(SettingKeys.DocumentNoteRequired, Defaults.DocumentNoteRequired),
            DocumentAttachmentsEnabled = Flag(SettingKeys.DocumentAttachmentsEnabled, Defaults.DocumentAttachmentsEnabled),
            AllowSelfApproveStocktake = Flag(SettingKeys.AllowSelfApproveStocktake, Defaults.AllowSelfApproveStocktake),
            StocktakeBlindCount = Flag(SettingKeys.StocktakeBlindCount, Defaults.StocktakeBlindCount),
            PrintCompanyName = values.TryGetValue(SettingKeys.PrintCompanyName, out var company) ? company : Defaults.PrintCompanyName,
            RetentionAuditYears = Number(SettingKeys.RetentionAuditYears, Defaults.RetentionAuditYears),
            RetentionLoginDays = Number(SettingKeys.RetentionLoginDays, Defaults.RetentionLoginDays),
            RetentionProcessedCommandsDays = Number(SettingKeys.RetentionProcessedCommandsDays, Defaults.RetentionProcessedCommandsDays),
            RetentionPostingFailuresDays = Number(SettingKeys.RetentionPostingFailuresDays, Defaults.RetentionPostingFailuresDays),
            RetentionImportBatchesDays = Number(SettingKeys.RetentionImportBatchesDays, Defaults.RetentionImportBatchesDays),
        };
    }

    /// <summary>每個鍵的（開表單時的值、送出的值、資料庫目前的值）。</summary>
    private static IEnumerable<(string Key, string Before, string After, string Current)> Pairs(
        SystemSettings original, SystemSettings updated, SystemSettings current) =>
        Flags(original, updated, current)
            .Select(p => (p.Key, Text(p.Before), Text(p.After), Text(p.Current)))
            .Append((SettingKeys.PrintCompanyName, Clean(original.PrintCompanyName), Clean(updated.PrintCompanyName), Clean(current.PrintCompanyName)))
            .Concat(Numbers(original, updated, current)
                .Select(p => (p.Key, p.Before.ToString(),
                    p.After == p.Before ? p.After.ToString() : Checked(p.After, p.Min, p.Max, p.Label), p.Current.ToString())));

    /// <summary>數字設定：超出範圍丟錯（不默默改成下限），訊息說明下限的理由在設定頁。</summary>
    private static string Checked(int value, int min, int max, string label) =>
        value >= min && value <= max
            ? value.ToString()
            : throw new DomainRuleException($"{label}必須介於 {min} 到 {max}。");

    private static IEnumerable<(string Key, int Before, int After, int Current, int Min, int Max, string Label)> Numbers(
        SystemSettings original, SystemSettings updated, SystemSettings current) =>
    [
        (SettingKeys.RetentionAuditYears, original.RetentionAuditYears, updated.RetentionAuditYears, current.RetentionAuditYears,
            RetentionLimits.AuditYearsMin, RetentionLimits.YearsMax, "操作日誌保留年數"),
        (SettingKeys.RetentionLoginDays, original.RetentionLoginDays, updated.RetentionLoginDays, current.RetentionLoginDays,
            RetentionLimits.LoginDaysMin, RetentionLimits.DaysMax, "登入紀錄保留天數"),
        (SettingKeys.RetentionProcessedCommandsDays, original.RetentionProcessedCommandsDays, updated.RetentionProcessedCommandsDays, current.RetentionProcessedCommandsDays,
            RetentionLimits.ProcessedCommandsDaysMin, RetentionLimits.DaysMax, "冪等紀錄保留天數"),
        (SettingKeys.RetentionPostingFailuresDays, original.RetentionPostingFailuresDays, updated.RetentionPostingFailuresDays, current.RetentionPostingFailuresDays,
            RetentionLimits.PostingFailuresDaysMin, RetentionLimits.DaysMax, "過帳失敗紀錄保留天數"),
        (SettingKeys.RetentionImportBatchesDays, original.RetentionImportBatchesDays, updated.RetentionImportBatchesDays, current.RetentionImportBatchesDays,
            RetentionLimits.ImportBatchesDaysMin, RetentionLimits.DaysMax, "匯入批次保留天數"),
    ];

    private static string Text(bool value) => value ? "true" : "false";

    /// <summary>文字設定：去頭尾空白、限制長度（超過丟錯，不默默截斷）。</summary>
    private static string Clean(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= SettingKeys.TextMaxLength
            ? trimmed
            : throw new DomainRuleException($"文字設定不可超過 {SettingKeys.TextMaxLength} 字。");
    }

    private static IEnumerable<(string Key, bool Before, bool After, bool Current)> Flags(
        SystemSettings original, SystemSettings updated, SystemSettings current) =>
    [
        (SettingKeys.AllowSelfApproveOpening, original.AllowSelfApproveOpening, updated.AllowSelfApproveOpening, current.AllowSelfApproveOpening),
        (SettingKeys.LocationNotesEnabled, original.LocationNotesEnabled, updated.LocationNotesEnabled, current.LocationNotesEnabled),
        (SettingKeys.LocationAttributesEnabled, original.LocationAttributesEnabled, updated.LocationAttributesEnabled, current.LocationAttributesEnabled),
        (SettingKeys.LocationImagesEnabled, original.LocationImagesEnabled, updated.LocationImagesEnabled, current.LocationImagesEnabled),
        (SettingKeys.DocumentApprovalEnabled, original.DocumentApprovalEnabled, updated.DocumentApprovalEnabled, current.DocumentApprovalEnabled),
        (SettingKeys.AllowSelfApproveDocuments, original.AllowSelfApproveDocuments, updated.AllowSelfApproveDocuments, current.AllowSelfApproveDocuments),
        (SettingKeys.DocumentPurposeEnabled, original.DocumentPurposeEnabled, updated.DocumentPurposeEnabled, current.DocumentPurposeEnabled),
        (SettingKeys.DocumentPurposeRequired, original.DocumentPurposeRequired, updated.DocumentPurposeRequired, current.DocumentPurposeRequired),
        (SettingKeys.DocumentPartnerEnabled, original.DocumentPartnerEnabled, updated.DocumentPartnerEnabled, current.DocumentPartnerEnabled),
        (SettingKeys.DocumentPartnerRequired, original.DocumentPartnerRequired, updated.DocumentPartnerRequired, current.DocumentPartnerRequired),
        (SettingKeys.DocumentExternalNoEnabled, original.DocumentExternalNoEnabled, updated.DocumentExternalNoEnabled, current.DocumentExternalNoEnabled),
        (SettingKeys.DocumentExternalNoRequired, original.DocumentExternalNoRequired, updated.DocumentExternalNoRequired, current.DocumentExternalNoRequired),
        (SettingKeys.DocumentNoteEnabled, original.DocumentNoteEnabled, updated.DocumentNoteEnabled, current.DocumentNoteEnabled),
        (SettingKeys.DocumentNoteRequired, original.DocumentNoteRequired, updated.DocumentNoteRequired, current.DocumentNoteRequired),
        (SettingKeys.DocumentAttachmentsEnabled, original.DocumentAttachmentsEnabled, updated.DocumentAttachmentsEnabled, current.DocumentAttachmentsEnabled),
        (SettingKeys.AllowSelfApproveStocktake, original.AllowSelfApproveStocktake, updated.AllowSelfApproveStocktake, current.AllowSelfApproveStocktake),
        (SettingKeys.StocktakeBlindCount, original.StocktakeBlindCount, updated.StocktakeBlindCount, current.StocktakeBlindCount),
    ];
}
