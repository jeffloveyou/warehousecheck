using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warehouse.Infrastructure.Maintenance;

namespace Warehouse.Web.Jobs;

/// <summary>
/// 資料保留清理（M7，ADR-0021）：啟動 15 分鐘後執行一次，之後每 24 小時一次（<see cref="DataRetentionService"/>）。
/// 換版重啟會重新計時，多跑幾次也無妨（刪的都是過期資料）；A/B 同時執行時只有一個實例真的清理。
/// </summary>
public sealed class DataRetentionJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DataRetentionJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, clock, stoppingToken);
            using var timer = new PeriodicTimer(Interval, clock);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<DataRetentionService>().RunAsync(stoppingToken);
                    if (result.Total > 0)
                    {
                        logger.LogInformation(
                            "資料保留清理：操作日誌 {Audit}、登入 {Login}、冪等 {Commands}、過帳失敗 {Failures}、匯入批次 {Imports} 筆",
                            result.AuditLogs, result.LoginLogs, result.ProcessedCommands, result.PostingFailures, result.ImportBatches);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "資料保留清理失敗，下一輪再試");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服務停止
        }
    }
}
