using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Accounts;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Identity;

/// <summary>帳號的常用設定（user_preferences，M8-06）：主鍵 (user_id, key)，帳號刪除時一併刪除。</summary>
public sealed class UserPreference
{
    public Guid UserId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// <see cref="IUserPreferenceService"/> 的實作（scoped：同一個請求內只查一次）。
/// 偏好只是畫面預設值：讀取失敗（例如新版比 Migration 先上線、資料庫短暫錯誤）時記警告並當作沒有設定，
/// 不讓整頁失敗（M8-06 審查 R1）。
/// </summary>
public sealed class UserPreferenceService(WarehouseDbContext db, TimeProvider clock, ILogger<UserPreferenceService> logger)
    : IUserPreferenceService
{
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, string>> _cache = [];

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }
        IReadOnlyDictionary<string, string> result;
        try
        {
            result = await db.UserPreferences.AsNoTracking()
                .Where(p => p.UserId == userId)
                .ToDictionaryAsync(p => p.Key, p => p.Value, cancellationToken);
        }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "讀取常用設定失敗，改用預設值（使用者 {UserId}）", userId);
            result = new Dictionary<string, string>();
        }
        _cache[userId] = result;
        return result;
    }

    public async Task SetAsync(Guid userId, string key, string? value, CancellationToken cancellationToken)
    {
        if (!UserPreferenceKeys.All.Contains(key))
        {
            throw new DomainRuleException($"不支援的設定項目：{key}。");
        }
        _cache.Remove(userId);
        if (value is null)
        {
            await db.UserPreferences.Where(p => p.UserId == userId && p.Key == key).ExecuteDeleteAsync(cancellationToken);
            return;
        }
        if (!UserPreferenceKeys.IsValid(key, value))
        {
            throw new DomainRuleException($"設定項目 {key} 的值格式不正確。");
        }
        if (key == UserPreferenceKeys.DocumentsLastWarehouse)
        {
            value = Guid.Parse(value).ToString("D"); // 統一格式（審查 Y5）
        }
        // 同一帳號在兩台裝置同時改同一項：以後到者為準，不會撞主鍵
        var now = clock.GetUtcNow().ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO user_preferences (user_id, key, value, updated_at) VALUES ({userId}, {key}, {value}, {now})
            ON CONFLICT (user_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """, cancellationToken);
    }

    public async Task TrySetAsync(Guid userId, string key, string? value, CancellationToken cancellationToken)
    {
        try
        {
            await SetAsync(userId, key, value, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or DomainRuleException)
        {
            logger.LogWarning(ex, "儲存常用設定 {Key} 失敗，不影響主要操作（使用者 {UserId}）", key, userId);
        }
    }
}
