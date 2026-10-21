using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Search;

namespace Warehouse.Api.Endpoints;

/// <summary>
/// /api/search 與搜尋限流（docs/03 §8「必要 rate limit」）。
/// 每位使用者每分鐘最多 <c>Search:RateLimitPerMinute</c> 次（預設 60），網頁搜尋頁與 API 共用同一個額度。
/// </summary>
public static class SearchEndpoints
{
    public const string RateLimitPolicy = "search";

    public static IServiceCollection AddSearchRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitPolicy, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IConfiguration>()
                    .GetValue("Search:RateLimitPerMinute", 60);
                // 以登入者 ID 分區；未登入的請求（理論上會先被授權擋下）以 IP 分區
                var key = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                // 固定視窗剩下的秒數（取不到時用 60）
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var message = $"搜尋太頻繁，請 {seconds} 秒後再試。";
                if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
                {
                    await response.WriteAsJsonAsync(
                        new { status = 429, errorCode = "search.rate_limited", detail = message },
                        options: null, contentType: "application/problem+json", cancellationToken);
                }
                else
                {
                    // 網頁：簡單的 HTML，有回到搜尋頁的連結（舊電腦不會看到一片空白）
                    response.ContentType = "text/html; charset=utf-8";
                    await response.WriteAsync(
                        $"<!DOCTYPE html><html lang=\"zh-Hant\"><meta charset=\"utf-8\"><title>請稍候</title>" +
                        $"<body style=\"font-family:sans-serif;padding:16px\"><p>{message}</p><p><a href=\"/Search\">回搜尋</a></p></body></html>",
                        cancellationToken);
                }
            };
        });
        return services;
    }

    public static void MapSearchEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/search", (IItemSearchService search, string? q, bool? includeInactive, int? page, int? pageSize,
                CancellationToken ct) =>
                search.SearchAsync(
                    new ItemSearchRequest(q, includeInactive ?? false, page ?? 1, pageSize ?? ItemSearch.DefaultPageSize), ct))
            .RequireAuthorization(Policies.ItemsRead)
            .RequireRateLimiting(RateLimitPolicy);
    }
}
