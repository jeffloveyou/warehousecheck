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
}
