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

    /// <summary>失敗的表與原因（其他表照常清理）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// 資料保留清理（M7，ADR-0021）：依系統設定的期限刪除舊紀錄。庫存單據、異動、結存、物料不在範圍內。
/// - 操作日誌與登入紀錄只能經資料庫函式 purge_audit_logs 刪除（下限寫在資料庫，網站帳號不能直接 DELETE）。
/// - 每批最多 <see cref="BatchSize"/> 筆、各自一個短交易；一次執行最多 <see cref="MaxBatchesPerTable"/> 批，剩下的明天繼續。
/// - A/B 兩個實例同時執行時以 session advisory lock 只讓一個做（另一個回傳 Skipped）。
/// - 每一批刪除與它的 system.retention 稽核在同一交易（操作日誌在資料庫函式內）；各表獨立，一張失敗不影響其他。
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
                try
                {
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
                }
                catch (Exception)
                {
                    // 連線已斷時解鎖也會失敗，但 session 結束鎖就會釋放；不要蓋掉原本的例外（審查 L2）
                }
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
        var auditYears = Math.Max(s.RetentionAuditYears, RetentionLimits.AuditYearsMin);
        var loginDays = Math.Max(s.RetentionLoginDays, RetentionLimits.LoginDaysMin);
        var commandsBefore = now.AddDays(-Math.Max(s.RetentionProcessedCommandsDays, RetentionLimits.ProcessedCommandsDaysMin));
        var failuresBefore = now.AddDays(-Math.Max(s.RetentionPostingFailuresDays, RetentionLimits.PostingFailuresDaysMin));
        var importsBefore = now.AddDays(-Math.Max(s.RetentionImportBatchesDays, RetentionLimits.ImportBatchesDaysMin));
        var errors = new List<string>();

        // 每張表各自處理，一張失敗不影響其他（審查 H2）。操作日誌：截止時間由資料庫依保留期間計算，刪除與稽核在函式內同一交易
        var audit = await GuardAsync("操作日誌", errors, () => RepeatAsync(() => db.Database
            .SqlQuery<int>($"SELECT purge_audit_logs(false, make_interval(years => {auditYears}), {BatchSize}) AS \"Value\"")
            .SingleAsync(cancellationToken)));
        var logins = await GuardAsync("登入紀錄", errors, () => RepeatAsync(() => db.Database
            .SqlQuery<int>($"SELECT purge_audit_logs(true, make_interval(days => {loginDays}), {BatchSize}) AS \"Value\"")
            .SingleAsync(cancellationToken)));
        var commands = await GuardAsync("冪等紀錄", errors, () => RepeatAsync(() => AuditedBatchAsync("processed_commands", commandsBefore,
            () => db.Database.ExecuteSqlAsync($"""
                DELETE FROM processed_commands WHERE key IN (
                    SELECT key FROM processed_commands WHERE created_at < {commandsBefore} ORDER BY created_at LIMIT {BatchSize})
                """, cancellationToken), cancellationToken)));
        var failures = await GuardAsync("過帳失敗紀錄", errors, () => RepeatAsync(() => AuditedBatchAsync("stock_posting_failures", failuresBefore,
            () => db.Database.ExecuteSqlAsync($"""
                DELETE FROM stock_posting_failures WHERE id IN (
                    SELECT id FROM stock_posting_failures WHERE occurred_at < {failuresBefore} ORDER BY occurred_at LIMIT {BatchSize})
                """, cancellationToken), cancellationToken)));
        var imports = await GuardAsync("匯入批次", errors, () => RepeatAsync(() => AuditedBatchAsync("import_batches", importsBefore,
            () => db.Database.ExecuteSqlAsync($"""
                DELETE FROM import_batches WHERE id IN (
                    SELECT id FROM import_batches WHERE created_at < {importsBefore} ORDER BY created_at LIMIT {BatchSize})
                """, cancellationToken), cancellationToken)));

        return new RetentionResult(false, audit, logins, commands, failures, imports) { Errors = errors };
    }

    private async Task<int> GuardAsync(string name, List<string> errors, Func<Task<int>> purge)
    {
        try
        {
            return await purge();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"{name}：{ex.Message}");
            db.ChangeTracker.Clear(); // 失敗那一批的稽核實體不能留到下一張表的 SaveChanges
            return 0;
        }
    }

    /// <summary>刪一批並在同一交易寫 system.retention 稽核（審查 H1：刪了就一定有紀錄）。</summary>
    private async Task<int> AuditedBatchAsync(string table, DateTimeOffset before, Func<Task<int>> delete, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await delete();
        if (deleted > 0)
        {
            AuditLog.Add(db, SystemActor, clock.GetUtcNow(), "system.retention", "system", table, null, new { deleted, before });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>重複刪除一批，直到不足一批或達到上限；每批各自一個短交易。</summary>
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
