using Microsoft.Extensions.Diagnostics.HealthChecks;
using Warehouse.Application.Abstractions;

namespace Warehouse.Api.HealthChecks;

/// <summary>
/// 資料庫 readiness 檢查：3 秒內可連線為 Healthy；失敗或逾時一律 Unhealthy，且不洩漏例外或連線字串。
/// </summary>
public class DatabaseHealthCheck(IDatabaseStatus databaseStatus) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            var connected = await databaseStatus.CanConnectAsync(cts.Token);
            return connected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("資料庫無法連線");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("資料庫無法連線");
        }
    }
}
