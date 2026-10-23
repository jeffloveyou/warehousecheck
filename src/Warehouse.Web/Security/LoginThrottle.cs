using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Warehouse.Web.Security;

/// <summary>
/// 登入嘗試限流（M6-01）：同一個來源 IP 每分鐘最多 <c>Login:RateLimitPerMinute</c> 次登入送出（預設 20）。
/// 帳號鎖定只保護「存在的帳號」；這裡另外擋住大量猜帳號，也避免登入失敗稽核把 audit_logs 灌爆。
/// 單一實例的記憶體計數：A/B 兩個槽位各算各的，切換時歸零，對內網已足夠。
/// 來源 IP 是 ForwardedHeaders 處理後的值：代理與網站不在同一台主機（例如不同容器）時，
/// 要設定 KnownProxies／KnownNetworks，否則所有人會被算成代理的 IP、共用同一個額度（docs/10，M6-01a 審查 O-2）。
/// 被擋下時寫一筆警告日誌（每個 IP 每分鐘最多一筆），留下暴力嘗試的痕跡。
/// </summary>
public sealed class LoginThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter;
    private readonly ILogger<LoginThrottle> logger;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastWarned = new();

    public LoginThrottle(IConfiguration configuration, ILogger<LoginThrottle> logger, TimeProvider clock)
    {
        this.logger = logger;
        this.clock = clock;
        var limit = Math.Max(1, configuration.GetValue("Login:RateLimitPerMinute", 20));
        limiter = PartitionedRateLimiter.Create<string, string>(ip => RateLimitPartition.GetFixedWindowLimiter(ip,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    /// <summary>回傳 false 表示這個 IP 本分鐘的嘗試次數已用完。</summary>
    public bool TryAcquire(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = limiter.AttemptAcquire(ip);
        if (lease.IsAcquired)
        {
            return true;
        }
        var now = clock.GetUtcNow();
        if (lastWarned.TryGetValue(ip, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            return false;
        }
        lastWarned[ip] = now;
        if (lastWarned.Count > 10_000)
        {
            lastWarned.Clear(); // 防止大量不同來源讓字典無限長大
        }
        logger.LogWarning("登入嘗試太頻繁，已暫時拒絕來源 {RemoteIp}", ip);
        return false;
    }

    public void Dispose() => limiter.Dispose();
}
