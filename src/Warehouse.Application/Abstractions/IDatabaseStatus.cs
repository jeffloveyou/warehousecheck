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
}
