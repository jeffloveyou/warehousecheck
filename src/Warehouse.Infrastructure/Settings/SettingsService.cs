using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Settings;
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
        bool Flag(string key, bool fallback) => values.TryGetValue(key, out var v) ? v == "true" : fallback;
        return new SystemSettings(
            Flag(SettingKeys.AllowSelfApproveOpening, Defaults.AllowSelfApproveOpening),
            Flag(SettingKeys.LocationNotesEnabled, Defaults.LocationNotesEnabled),
            Flag(SettingKeys.LocationAttributesEnabled, Defaults.LocationAttributesEnabled),
            Flag(SettingKeys.LocationImagesEnabled, Defaults.LocationImagesEnabled));
    }

    public async Task UpdateAsync(SystemSettings settings, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var existing = await db.SystemSettings.ToDictionaryAsync(s => s.Key, cancellationToken);
        foreach (var (key, value) in new[]
        {
            (SettingKeys.AllowSelfApproveOpening, settings.AllowSelfApproveOpening),
            (SettingKeys.LocationNotesEnabled, settings.LocationNotesEnabled),
            (SettingKeys.LocationAttributesEnabled, settings.LocationAttributesEnabled),
            (SettingKeys.LocationImagesEnabled, settings.LocationImagesEnabled),
        })
        {
            var text = value ? "true" : "false";
            if (!existing.TryGetValue(key, out var row))
            {
                // 舊資料庫還沒有這個鍵：新增一列並記錄（變更前為空）
                row = SystemSetting.Seed(key, text);
                row.Set(text, actor.Name, now);
                db.SystemSettings.Add(row);
                AuditLog.Add(db, actor, now, "setting.update", "setting", key, null, new { value = text });
                continue;
            }
            if (row.Value == text)
            {
                continue;
            }
            AuditLog.Add(db, actor, now, "setting.update", "setting", key, new { value = row.Value }, new { value = text });
            row.Set(text, actor.Name, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
