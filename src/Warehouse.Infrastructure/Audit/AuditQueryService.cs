using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Audit;

/// <summary>
/// <see cref="IAuditQueryService"/> 實作。一定有期間條件，走 ix_audit_logs_occurred_at；
/// 指定對象（類型＋ID）時走 (entity_type, entity_id, occurred_at)。
/// </summary>
public sealed class AuditQueryService(WarehouseDbContext db) : IAuditQueryService
{
    public async Task<PagedResult<AuditRow>> SearchAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var from = query.From.ToUniversalTime();
        var to = query.To.ToUniversalTime();
        if (from >= to)
        {
            throw new DomainRuleException("起日必須早於迄日。");
        }
        if (to - from > TimeSpan.FromDays(AuditQuery.MaxDays))
        {
            throw new DomainRuleException($"查詢期間最長 {AuditQuery.MaxDays} 天，請縮小範圍。");
        }
        var page = Math.Clamp(query.Page, 1, AuditQuery.MaxCount / Paging.DefaultPageSize);
        var logs = db.AuditLogs.AsNoTracking().Where(a => a.OccurredAt >= from && a.OccurredAt < to);
        if (Clean(query.ActorName) is { } actor)
        {
            var pattern = LikePattern.Contains(actor);
            logs = logs.Where(a => EF.Functions.ILike(a.ActorName, pattern, LikePattern.Escape));
        }
        if (Clean(query.ActionPrefix) is { } action)
        {
            var pattern = LikePattern.Escaped(action) + "%";
            logs = logs.Where(a => EF.Functions.Like(a.Action, pattern, LikePattern.Escape));
        }
        if (Clean(query.EntityType) is { } type)
        {
            logs = logs.Where(a => a.EntityType == type);
        }
        if (Clean(query.EntityId) is { } id)
        {
            logs = logs.Where(a => a.EntityId == id);
        }
        var total = await logs.Take(AuditQuery.MaxCount).CountAsync(cancellationToken);
        var rows = await logs.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(a => new AuditRow(a.Id, a.OccurredAt, a.ActorName, a.Action, a.EntityType, a.EntityId, a.Before, a.After))
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditRow>(rows, page, Paging.DefaultPageSize, total);
    }

    private static string? Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text.Length > Paging.MaxKeywordLength ? text[..Paging.MaxKeywordLength] : text;
    }
}
