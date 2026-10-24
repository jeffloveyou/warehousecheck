using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Domain;
using Warehouse.Application.Settings;
using Warehouse.Infrastructure.Maintenance;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>M7 資料保留清理（T-M7-02-10～14，ADR-0021）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class RetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M7-02-10：過期紀錄被刪、未過期保留；庫存資料不在範圍；有刪除時寫 system.retention 稽核
    [Fact]
    public async Task 清除過期紀錄_保留未過期()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;

        await InsertAuditAsync(db, $"auth.login_failed", $"old-login-{tag}", now.AddDays(-400));
        await InsertAuditAsync(db, $"auth.login_failed", $"new-login-{tag}", now.AddDays(-300));
        await InsertAuditAsync(db, "user.create", $"old-audit-{tag}", now.AddYears(-8));
        await InsertAuditAsync(db, "user.create", $"new-audit-{tag}", now.AddYears(-6));
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO processed_commands (key, command_type, request_hash, result_ref, created_at)
            VALUES ({"old-" + tag}, 'test', 'h', 'r', {now.AddDays(-100)}), ({"new-" + tag}, 'test', 'h', 'r', {now.AddDays(-10)})
            """, Ct);
        var oldFailure = Guid.NewGuid();
        var newFailure = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO stock_posting_failures (id, occurred_at, actor_id, actor_name, command, error_code, message)
            VALUES ({oldFailure}, {now.AddDays(-400)}, {Guid.Empty}, 't', 'test', 'e', 'm'),
                   ({newFailure}, {now.AddDays(-10)}, {Guid.Empty}, 't', 'test', 'e', 'm')
            """, Ct);
        var oldBatch = Guid.NewGuid();
        var newBatch = Guid.NewGuid();
        foreach (var (id, at) in new[] { (oldBatch, now.AddDays(-40)), (newBatch, now.AddDays(-3)) })
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO import_batches (id, kind, status, file_name, file_hash, rows_json, issues_json, row_count, error_count,
                    warning_count, created_by_id, created_by_name, created_at)
                VALUES ({id}, 'Items', 'Validated', 'x.csv', 'h', '[]'::jsonb, '[]'::jsonb, 0, 0, 0, {Guid.Empty}, 't', {at})
                """, Ct);
        }

        var result = await scope.ServiceProvider.GetRequiredService<DataRetentionService>().RunAsync(Ct);

        Assert.False(result.Skipped);
        Assert.True(result.LoginLogs >= 1 && result.AuditLogs >= 1 && result.ProcessedCommands >= 1);
        Assert.Equal(0, await CountAuditAsync(db, $"old-login-{tag}"));
        Assert.Equal(1, await CountAuditAsync(db, $"new-login-{tag}"));
        Assert.Equal(0, await CountAuditAsync(db, $"old-audit-{tag}"));
        Assert.Equal(1, await CountAuditAsync(db, $"new-audit-{tag}"));
        Assert.Equal(["new-" + tag], await db.Database
            .SqlQuery<string>($"SELECT key AS \"Value\" FROM processed_commands WHERE key LIKE {"%" + tag}").ToListAsync(Ct));
        Assert.Equal([newFailure], await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM stock_posting_failures WHERE id IN ({oldFailure}, {newFailure})").ToListAsync(Ct));
        Assert.Equal([newBatch], await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM import_batches WHERE id IN ({oldBatch}, {newBatch})").ToListAsync(Ct));
        // 每張表的刪除都有 system.retention 稽核（操作日誌、登入紀錄由資料庫函式在同一交易寫入）
        var recorded = await db.AuditLogs.Where(a => a.Action == "system.retention" && a.OccurredAt > now.AddMinutes(-1)).Select(a => a.EntityId).ToListAsync(Ct);
        Assert.Superset(new HashSet<string> { "audit_logs", "login_logs", "processed_commands", "stock_posting_failures", "import_batches" }, recorded.ToHashSet());
        Assert.Empty(result.Errors);
    }

    // T-M7-02-11：資料庫函式的下限（應用程式設定改不低）；稽核表仍不能直接刪除
    [Fact]
    public async Task 刪除下限寫在資料庫_直接刪除仍被擋()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var now = DateTimeOffset.UtcNow;

        var login = await Assert.ThrowsAsync<PostgresException>(() => db.Database
            .SqlQuery<int>($"SELECT purge_audit_logs(true, make_interval(days => 100), 10) AS \"Value\"").SingleAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, login.SqlState);
        var audit = await Assert.ThrowsAsync<PostgresException>(() => db.Database
            .SqlQuery<int>($"SELECT purge_audit_logs(false, make_interval(years => 6), 10) AS \"Value\"").SingleAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, audit.SqlState);

        var tag = "direct-" + Guid.NewGuid().ToString("N")[..12];
        await InsertAuditAsync(db, "user.create", tag, now.AddYears(-10));
        var direct = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM audit_logs WHERE entity_id = {tag}", Ct));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, direct.SqlState);
    }

    // T-M7-02-12：另一個實例正在清理（持有 advisory lock）時這次略過
    [Fact]
    public async Task 另一個實例清理中_略過()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var holder = new NpgsqlConnection(TestDatabase.ConnectionString);
        await holder.OpenAsync(Ct);
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_lock({DataRetentionService.LockKey})", holder))
        {
            await command.ExecuteScalarAsync(Ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<DataRetentionService>().RunAsync(Ct);
        Assert.True(result.Skipped);
    }

    // T-M7-02-13：保留期限設定低於下限無法儲存；其他設定照常儲存
    [Fact]
    public async Task 保留期限低於下限_拒絕()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var actor = new Actor(Guid.Empty, "測試", false);
        var current = await settings.GetAsync(Ct);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            settings.UpdateAsync(current, current with { RetentionLoginDays = 30 }, actor, Ct));
        Assert.Contains("登入紀錄保留天數", ex.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            settings.UpdateAsync(current, current with { RetentionAuditYears = 3 }, actor, Ct));

        var longer = current.RetentionProcessedCommandsDays + 1;
        await settings.UpdateAsync(current, current with { RetentionProcessedCommandsDays = longer }, actor, Ct);
        var saved = await settings.GetAsync(Ct);
        Assert.Equal(longer, saved.RetentionProcessedCommandsDays);
        await settings.UpdateAsync(saved, saved with { RetentionProcessedCommandsDays = current.RetentionProcessedCommandsDays }, actor, Ct);
    }

    private static Task InsertAuditAsync(WarehouseDbContext db, string action, string entityId, DateTimeOffset at) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO audit_logs (id, occurred_at, actor_id, actor_name, action, entity_type, entity_id)
            VALUES ({Guid.NewGuid()}, {at}, {Guid.Empty}, 't', {action}, 'test', {entityId})
            """, Ct);

    private static Task<int> CountAuditAsync(WarehouseDbContext db, string entityId) =>
        db.AuditLogs.CountAsync(a => a.EntityId == entityId, Ct);
}
