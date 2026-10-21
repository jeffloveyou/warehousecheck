using System.Text.Json;
using Warehouse.Application.Abstractions;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 稽核紀錄（audit_logs，只能新增；trigger trg_audit_logs_append_only 擋下 UPDATE／DELETE）。
/// 記錄誰、何時、對哪個資料做了什麼，以及前後值（JSON）。不記錄密碼或其他秘密。
/// 由各服務在同一交易內以 <see cref="Add"/> 寫入：業務資料與稽核一起成功或一起失敗。
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid ActorId { get; private set; }

    public string ActorName { get; private set; } = string.Empty;

    /// <summary>動作，例如 <c>warehouse.create</c>、<c>location.status</c>、<c>setting.update</c>。</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public string? Before { get; private set; }

    public string? After { get; private set; }

    public static void Add(
        WarehouseDbContext db, Actor actor, DateTimeOffset now, string action, string entityType, string entityId,
        object? before, object? after)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OccurredAt = now.ToUniversalTime(),
            ActorId = actor.Id,
            ActorName = actor.Name,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
        });
    }
}

/// <summary>系統設定（system_settings）：鍵值字串，鍵見 <c>SettingKeys</c>。</summary>
public sealed class SystemSetting
{
    public string Key { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByName { get; private set; } = string.Empty;

    public static SystemSetting Seed(string key, string value) => new()
    {
        Key = key,
        Value = value,
        UpdatedAt = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
        UpdatedByName = "系統預設",
    };

    public void Set(string value, string actorName, DateTimeOffset now)
    {
        Value = value;
        UpdatedByName = actorName;
        UpdatedAt = now.ToUniversalTime();
    }
}
