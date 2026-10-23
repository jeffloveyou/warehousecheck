using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// IDatabaseStatus 的 EF Core／PostgreSQL 實作，只做唯讀探測。
/// </summary>
public class DatabaseStatus(WarehouseDbContext db) : IDatabaseStatus
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken)
        => db.Database.CanConnectAsync(cancellationToken);

    public async Task<string?> GetServerVersionAsync(CancellationToken cancellationToken)
    {
        // 無參數的 FormattableString；SqlQuery<string> 要求結果欄位名稱為 Value
        return await db.Database
            .SqlQuery<string>($"SELECT current_setting('server_version') AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    public async Task<DatabaseDetails> GetDetailsAsync(CancellationToken cancellationToken)
    {
        // 欄位別名用 snake_case（DbContext 採命名慣例）；pg_stat_archiver、pg_database_size(current_database()) 一般帳號（非擁有者的網站帳號）也能讀
        var row = await db.Database.SqlQuery<DetailsRow>($"""
            SELECT current_database() AS name,
                   current_setting('server_version') AS server_version,
                   pg_database_size(current_database()) AS size_bytes,
                   current_setting('archive_mode') AS archive_mode,
                   a.archived_count AS archived_count,
                   a.last_archived_time AS last_archived_at,
                   a.failed_count AS failed_count,
                   a.last_failed_time AS last_failed_at
            FROM pg_stat_archiver a
            """).SingleAsync(cancellationToken);
        return new DatabaseDetails(row.Name, row.ServerVersion, row.SizeBytes, row.ArchiveMode,
            row.ArchivedCount, row.LastArchivedAt, row.FailedCount, row.LastFailedAt);
    }

    private sealed class DetailsRow
    {
        public string Name { get; init; } = "";

        public string ServerVersion { get; init; } = "";

        public long SizeBytes { get; init; }

        public string ArchiveMode { get; init; } = "";

        public long ArchivedCount { get; init; }

        public DateTimeOffset? LastArchivedAt { get; init; }

        public long FailedCount { get; init; }

        public DateTimeOffset? LastFailedAt { get; init; }
    }
}
