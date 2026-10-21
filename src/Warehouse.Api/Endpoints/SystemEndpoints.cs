using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Warehouse.Application.Abstractions;

namespace Warehouse.Api.Endpoints;

public record SystemInfoResponse(string Version, bool DatabaseConnected, string? DatabaseVersion, DateTimeOffset ServerTimeUtc);

public static class SystemEndpoints
{
    private static readonly TimeSpan DbTimeout = TimeSpan.FromSeconds(3);

    public static void MapSystemEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/system/info", GetSystemInfoAsync);
    }

    private static async Task<SystemInfoResponse> GetSystemInfoAsync(
        IDatabaseStatus databaseStatus, CancellationToken requestAborted)
    {
        var connected = false;
        string? dbVersion = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(DbTimeout);
        try
        {
            connected = await databaseStatus.CanConnectAsync(cts.Token);
            if (connected)
            {
                dbVersion = await databaseStatus.GetServerVersionAsync(cts.Token);
            }
        }
        catch (Exception)
        {
            // 不拋出、不洩漏例外訊息；前端只需知道無法連線
            connected = false;
            dbVersion = null;
        }

        return new SystemInfoResponse(AppVersion.Current, connected, dbVersion, DateTimeOffset.UtcNow);
    }
}
