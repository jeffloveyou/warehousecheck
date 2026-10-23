using Warehouse.Application.Abstractions;

namespace Warehouse.Application.Audit;

/// <summary>
/// 操作日誌查詢條件（M6-01c）。期間必填（[From, To)，UTC），最長 <see cref="MaxDays"/> 天，避免全表掃描。
/// 操作者是名稱部分符合；動作是開頭符合（例如 <c>auth.</c>、<c>document.</c>）。
/// </summary>
public sealed record AuditQuery(
    DateTimeOffset From, DateTimeOffset To, string? ActorName = null, string? ActionPrefix = null,
    string? EntityType = null, string? EntityId = null, int Page = 1)
{
    public const int MaxDays = 366;

    /// <summary>沒有指定期間時，畫面預設查最近幾天。</summary>
    public const int DefaultDays = 7;

    /// <summary>計數上限：超過時總筆數顯示為這個值，請使用者縮小條件（避免大表 COUNT 與深分頁，M6-01c 審查 1）。</summary>
    public const int MaxCount = 10_000;
}

/// <summary>一筆操作日誌（audit_logs，前後值為 JSON 文字）。</summary>
public sealed record AuditRow(
    Guid Id, DateTimeOffset OccurredAt, string ActorName, string Action, string EntityType, string EntityId,
    string? Before, string? After);

/// <summary>操作日誌查詢（限系統管理員，Policies.AuditRead）。新到舊、伺服器分頁。</summary>
public interface IAuditQueryService
{
    Task<PagedResult<AuditRow>> SearchAsync(AuditQuery query, CancellationToken cancellationToken);
}
