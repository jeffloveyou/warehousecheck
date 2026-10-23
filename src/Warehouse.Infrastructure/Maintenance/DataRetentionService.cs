using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Settings;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Maintenance;

/// <summary>一次清理的結果（各表刪除筆數）；Skipped＝另一個執行個體正在清理，這次沒做。</summary>
public sealed record RetentionResult(
    bool Skipped, int AuditLogs, int LoginLogs, int ProcessedCommands, int PostingFailures, int ImportBatches)
{
    public int Total => AuditLogs + LoginLogs + ProcessedCommands + PostingFailures + ImportBatches;
}

/// <summary>
/// 資料保留清理（M7，ADR-0021）：依系統設定的期限刪除舊紀錄。庫存單據、異動、結存、物料不在範圍內。
/// - 操作日誌與登入紀錄只能經資料庫函式 purge_audit_logs 刪除（下限寫在資料庫，網站帳號不能直接 DELETE）。
/// - 每批最多 <see cref="BatchSize"/> 筆、各自一個短交易；一次執行最多 <see cref="MaxBatchesPerTable"/> 批，剩下的明天繼續。
/// - A/B 兩個實例同時執行時以 session advisory lock 只讓一個做（另一個回傳 Skipped）。
/// - 有刪除時寫一筆 system.retention 稽核（各表筆數與期限）。
/// </summary>
public sealed class DataRetentionService(WarehouseDbContext db, ISettingsService settings, TimeProvider clock)
{
    public const int BatchSize = 5_000;
    public const int MaxBatchesPerTable = 200;

    /// <summary>advisory lock 鍵（固定值，A/B 兩版必須相同）。</summary>
    public const long LockKey = 0x5741_5245_5445_4E54; // "WARETENT"

    public static readonly Actor SystemActor = new(Guid.Empty, "系統（資料保留）", false);

    public async Task<RetentionResult> RunAsync(CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var locked = await db.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!locked)
            {
                return new RetentionResult(true, 0, 0, 0, 0, 0);
            }

            try
            {
                return await PurgeAsync(cancellationToken);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<RetentionResult> PurgeAsync(CancellationToken cancellationToken)
    {
        var s = await settings.GetAsync(cancellationToken);
        var now = clock.GetUtcNow();
        // 設定值即使被改到下限以下（直接改資料庫）也以下限為準；資料庫函式另有同樣的下限
        var auditBefore = now.AddYears(-Math.Max(s.RetentionAuditYears, RetentionLimits.AuditYearsMin));
        var loginBefore = now.AddDays(-Math.Max(s.RetentionLoginDays, RetentionLimits.LoginDaysMin));
        var commandsBefore = now.AddDays(-Math.Max(s.RetentionProcessedCommandsDays, RetentionLimits.ProcessedCommandsDaysMin));
        var failuresBefore = now.AddDays(-Math.Max(s.RetentionPostingFailuresDays, RetentionLimits.PostingFailuresDaysMin));
        var importsBefore = now.AddDays(-Math.Max(s.RetentionImportBatchesDays, RetentionLimits.ImportBatchesDaysMin));

        var audit = await RepeatAsync(() => db.Database
            .SqlQuery<int>($"SELECT purge_audit_logs({auditBefore}, false, {BatchSize}) AS \"Value\"").SingleAsync(cancellationToken));
        var logins = await RepeatAsync(() => db.Database
            .SqlQuery<int>($"SELECT purge_audit_logs({loginBefore}, true, {BatchSize}) AS \"Value\"").SingleAsync(cancellationToken));
        var commands = await RepeatAsync(() => db.Database.ExecuteSqlAsync($"""
            DELETE FROM processed_commands WHERE key IN (
                SELECT key FROM processed_commands WHERE created_at < {commandsBefore} ORDER BY created_at LIMIT {BatchSize})
            """, cancellationToken));
        var failures = await RepeatAsync(() => db.Database.ExecuteSqlAsync($"""
            DELETE FROM stock_posting_failures WHERE id IN (
                SELECT id FROM stock_posting_failures WHERE occurred_at < {failuresBefore} ORDER BY occurred_at LIMIT {BatchSize})
            """, cancellationToken));
        var imports = await RepeatAsync(() => db.Database.ExecuteSqlAsync($"""
            DELETE FROM import_batches WHERE id IN (
                SELECT id FROM import_batches WHERE created_at < {importsBefore} ORDER BY created_at LIMIT {BatchSize})
            """, cancellationToken));

        var result = new RetentionResult(false, audit, logins, commands, failures, imports);
        if (result.Total > 0)
        {
            AuditLog.Add(db, SystemActor, now, "system.retention", "system", "retention", null, new
            {
                auditLogs = audit, loginLogs = logins, processedCommands = commands, postingFailures = failures, importBatches = imports,
                auditBefore, loginBefore, commandsBefore, failuresBefore, importsBefore,
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        return result;
    }

    /// <summary>重複刪除一批，直到不足一批或達到上限；每批是獨立的自動提交語句（不包外層交易）。</summary>
    private static async Task<int> RepeatAsync(Func<Task<int>> batch)
    {
        var total = 0;
        for (var i = 0; i < MaxBatchesPerTable; i++)
        {
            var deleted = await batch();
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }
}
