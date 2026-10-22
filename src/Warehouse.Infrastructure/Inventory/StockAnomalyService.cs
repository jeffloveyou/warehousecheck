using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// 被拒絕的過帳嘗試（stock_posting_failures，M4-05）。只能新增；保存期限與 processed_commands 一起在 M7 處理（docs/09）。
/// </summary>
public sealed class PostingFailure
{
    public const int MessageMaxLength = 500;

    private PostingFailure()
    {
    }

    public static PostingFailure Create(
        string command, Guid? documentId, string? docNo, Actor actor, string errorCode, string message, string? traceId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            OccurredAt = now.ToUniversalTime(),
            ActorId = actor.Id,
            ActorName = actor.Name,
            Command = command,
            DocumentId = documentId,
            DocNo = docNo,
            ErrorCode = errorCode,
            Message = message.Length > MessageMaxLength ? message[..MessageMaxLength] : message,
            TraceId = traceId,
        };

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid ActorId { get; private set; }

    public string ActorName { get; private set; } = string.Empty;

    public string Command { get; private set; } = string.Empty;

    public Guid? DocumentId { get; private set; }

    public string? DocNo { get; private set; }

    public string ErrorCode { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    /// <summary>W3C trace id（與 API 錯誤回應、日誌的 traceId 相同），用來找到同一個請求的日誌。</summary>
    public string? TraceId { get; private set; }
}

public class PostingFailureConfiguration : IEntityTypeConfiguration<PostingFailure>
{
    public void Configure(EntityTypeBuilder<PostingFailure> builder)
    {
        builder.ToTable("stock_posting_failures");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.ActorName).HasMaxLength(100);
        builder.Property(f => f.Command).HasMaxLength(50);
        builder.Property(f => f.DocNo).HasMaxLength(20);
        builder.Property(f => f.ErrorCode).HasMaxLength(60);
        builder.Property(f => f.Message).HasMaxLength(PostingFailure.MessageMaxLength);
        builder.Property(f => f.TraceId).HasMaxLength(64);
        builder.HasIndex(f => f.OccurredAt);
    }
}

/// <summary>
/// <see cref="IPostingFailureLog"/> 實作：開一個新的 scope（新的 DbContext、新連線）寫入，
/// 原本的交易已回滾也照樣留下紀錄。寫入失敗只記日誌，不影響原本要回給使用者的錯誤。
/// </summary>
public sealed class PostingFailureLog(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PostingFailureLog> logger) : IPostingFailureLog
{
    public async Task RecordAsync(string command, Guid? documentId, Actor actor, Exception error, CancellationToken cancellationToken)
    {
        var code = error switch
        {
            AppException app => app.ErrorCode,
            DomainRuleException => "domain_rule",
            _ => "unexpected",
        };
        // 資料庫連不上時最多多等 3 秒，不讓原本的錯誤回應拖太久（M4-03 審查 #15）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        cancellationToken = timeout.Token;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var docNo = documentId is { } id
                ? await db.StockDocuments.AsNoTracking().Where(d => d.Id == id).Select(d => d.DocNo).FirstOrDefaultAsync(cancellationToken)
                : null;
            db.Add(PostingFailure.Create(command, documentId, docNo, actor, code, error.Message,
                Activity.Current?.TraceId.ToHexString(), clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "無法記錄被拒絕的過帳嘗試 {Command} {DocumentId} {ErrorCode}", command, documentId, code);
        }
    }
}

/// <summary><see cref="IStockAnomalyService"/> 實作（唯讀、伺服器分頁）。</summary>
public sealed class StockAnomalyService(WarehouseDbContext db, TimeProvider clock) : IStockAnomalyService
{
    /// <summary>單據事件的稽核動作；修改時要同步改 Migration M4_05 的部分索引條件與 Display.OfDocumentEvent。</summary>
    public static readonly string[] DocumentActions = ["document.cancel", "document.return", "document.reversal_create"];

    public async Task<PagedResult<PostingFailureRow>> GetPostingFailuresAsync(int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var query = db.Set<PostingFailure>().AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(f => f.OccurredAt)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(f => new PostingFailureRow(f.OccurredAt, f.ActorName, f.Command, f.DocumentId, f.DocNo, f.ErrorCode, f.Message, f.TraceId))
            .ToListAsync(cancellationToken);
        return new PagedResult<PostingFailureRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<PagedResult<DocumentEventRow>> GetDocumentEventsAsync(int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        // 兩段查詢（M4-03 審查 #2）：先用部分索引 ix_audit_logs_document_events 取這一頁的稽核，
        // 再以 Guid 清單查單號；不做 uuid::text 的 join，避免掃整張單據稽核
        var events = db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "stock_document" && DocumentActions.Contains(a.Action));
        var total = await events.CountAsync(cancellationToken);
        var audits = await events.OrderByDescending(a => a.OccurredAt).ThenBy(a => a.Id)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(a => new { a.OccurredAt, a.ActorName, a.Action, a.EntityId })
            .ToListAsync(cancellationToken);
        var ids = audits.Select(a => Guid.TryParse(a.EntityId, out var id) ? id : Guid.Empty).Distinct().ToList();
        var docs = await db.StockDocuments.AsNoTracking().Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => new { d.DocNo, d.ReturnComment }, cancellationToken);
        var rows = audits
            .Select(a => (Audit: a, Id: Guid.TryParse(a.EntityId, out var id) ? id : Guid.Empty))
            .Where(x => docs.ContainsKey(x.Id))
            .Select(x => new DocumentEventRow(x.Audit.OccurredAt, x.Audit.ActorName, x.Audit.Action, x.Id, docs[x.Id].DocNo,
                x.Audit.Action == "document.return" ? docs[x.Id].ReturnComment : null))
            .ToList();
        return new PagedResult<DocumentEventRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<PagedResult<StaleDocumentRow>> GetStaleDocumentsAsync(int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var threshold = clock.GetUtcNow().AddDays(-IStockAnomalyService.StaleDocumentDays);
        var query = db.StockDocuments.AsNoTracking()
            .Where(d => (d.Status == StockDocumentStatus.Draft && d.CreatedAt < threshold)
                || (d.Status == StockDocumentStatus.Submitted && d.SubmittedAt < threshold));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(d => new StaleDocumentRow(d.Id, d.DocNo, d.DocType, d.Status, d.CreatedByName,
                d.Status == StockDocumentStatus.Submitted ? d.SubmittedAt!.Value : d.CreatedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<StaleDocumentRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<PagedResult<StaleReservationRow>> GetStaleReservationsAsync(int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var now = clock.GetUtcNow();
        var old = now.AddDays(-IStockAnomalyService.StaleReservationDays);
        var soon = now.AddDays(IStockAnomalyService.ExpiringSoonDays);
        var query =
            from r in db.StockReservations.AsNoTracking()
            where r.Status == ReservationStatus.Open && (r.CreatedAt < old || (r.ExpiresAt != null && r.ExpiresAt < soon))
            join i in db.Items on r.ItemId equals i.Id
            join w in db.Warehouses on r.WarehouseId equals w.Id
            select new { r, i, w };
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.r.ExpiresAt ?? x.r.CreatedAt).ThenBy(x => x.r.Id)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(x => new StaleReservationRow(x.r.Id, x.i.ItemCode ?? x.i.RequestNo, x.w.Code, x.r.RemainingQuantity, x.r.ReferenceType,
                db.StockDocuments.Where(d => d.Id == x.r.ReferenceId).Select(d => d.DocNo).FirstOrDefault(),
                x.r.CreatedByName, x.r.CreatedAt, x.r.ExpiresAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<StaleReservationRow>(rows, page, Paging.DefaultPageSize, total);
    }
}
