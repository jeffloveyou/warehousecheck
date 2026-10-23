namespace Warehouse.Application.Abstractions;

/// <summary>
/// 資料庫連線狀態查詢（供 health check 與系統資訊使用），由 Infrastructure 實作。
/// </summary>
public interface IDatabaseStatus
{
    /// <summary>是否可連線到資料庫。</summary>
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);

    /// <summary>取得資料庫伺服器版本字串；無法取得時為 null。</summary>
    Task<string?> GetServerVersionAsync(CancellationToken cancellationToken);

    /// <summary>系統狀態頁（M7-02，限系統管理員）：資料庫名稱、大小與 WAL 封存狀態。</summary>
    Task<DatabaseDetails> GetDetailsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 資料庫細節。WAL 封存（<c>pg_stat_archiver</c>）是 15 分鐘 RPO 的依據（ADR-0021）：
/// 失敗次數增加且最後失敗晚於最後成功，代表封存目前卡住。
/// </summary>
public sealed record DatabaseDetails(
    string Name,
    string ServerVersion,
    long SizeBytes,
    string ArchiveMode,
    long ArchivedCount,
    DateTimeOffset? LastArchivedAt,
    long FailedCount,
    DateTimeOffset? LastFailedAt);
