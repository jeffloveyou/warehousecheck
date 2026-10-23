using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Warehouse.Application.Abstractions;

namespace Warehouse.Api.Endpoints;

/// <summary>匿名可讀（首頁、switch-release 冒煙）：不含資料庫版本等環境細節（M6-04 審查 R9），系統管理員另看 /Admin/System。</summary>
public record SystemInfoResponse(string Version, bool DatabaseConnected, DateTimeOffset ServerTimeUtc);

public static class SystemEndpoints
{
    private static readonly TimeSpan DbTimeout = TimeSpan.FromSeconds(3);

    public static void MapSystemEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/system/info", GetSystemInfoAsync);
    }

    /// <summary>取得系統資訊；API 端點與 Web 的系統狀態頁共用此方法。</summary>
    public static async Task<SystemInfoResponse> GetSystemInfoAsync(
        IDatabaseStatus databaseStatus, CancellationToken requestAborted)
    {
        var connected = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(DbTimeout);
        try
        {
            connected = await databaseStatus.CanConnectAsync(cts.Token);
        }
        catch (Exception)
        {
            // 不拋出、不洩漏例外訊息；前端只需知道無法連線
            connected = false;
        }

        return new SystemInfoResponse(AppVersion.Current, connected, DateTimeOffset.UtcNow);
    }
}
