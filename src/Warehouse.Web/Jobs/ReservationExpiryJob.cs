using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Web.Jobs;

/// <summary>
/// 預留到期釋放（M4-07，使用者 2026-10-22）：每 10 分鐘把到期的手動預留改成 Released 並寫稽核。
/// 可用量計算本來就不計過期預留，這個工作只是讓狀態與稽核跟上（晚幾分鐘不影響正確性）。
/// A／B 兩個實例同時執行也安全：條件式更新，同一筆只會被一個實例改到。
/// 低負載：一次查詢，用部分索引 ix_stock_reservations_expiry；沒有到期的預留時幾乎不耗資源。
/// </summary>
public sealed class ReservationExpiryJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReservationExpiryJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>稽核的操作者（系統自動）。</summary>
    public static readonly Actor SystemActor = new(Guid.Empty, "系統（預留到期）", false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                var count = await RunOnceAsync(stoppingToken);
                if (count > 0)
                {
                    logger.LogInformation("已釋放 {Count} 筆到期預留", count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 資料庫暫時連不上等：記錄後等下一輪，不讓背景工作結束
                logger.LogWarning(ex, "釋放到期預留失敗，下一輪再試");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var posting = scope.ServiceProvider.GetRequiredService<IInventoryPostingService>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var count = await posting.ExpireDueReservationsAsync(SystemActor, clock.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }
}
