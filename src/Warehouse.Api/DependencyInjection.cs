using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Warehouse.Api.Authorization;
using Warehouse.Api.Endpoints;
using Warehouse.Api.Errors;
using Warehouse.Api.HealthChecks;

namespace Warehouse.Api;

public static class DependencyInjection
{
    /// <summary>註冊 Api 層服務與 health checks（database 帶 ready 標籤）。</summary>
    public static IServiceCollection AddWarehouseApi(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        services.AddWarehouseAuthorization();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddSearchRateLimiting();
        // 列舉以文字傳遞（"PendingApproval"），與資料庫一致、容易閱讀
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }

    /// <summary>對應 /api 端點與 /health/live、/health/ready（健康檢查與系統資訊不需登入，供代理與監控使用）。</summary>
    public static IEndpointRouteBuilder MapWarehouseApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.MapGroup("").AllowAnonymous().MapSystemEndpoints();
        api.MapItemEndpoints();
        api.MapSearchEndpoints();
        api.MapWarehouseEndpoints();
        api.MapStockEndpoints();
        endpoints.MapFileEndpoints();

        // live：不執行任何檢查，只代表處理程序可回應
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthResponseAsync
        }).AllowAnonymous();

        // ready：執行帶 ready 標籤的檢查（資料庫）；Unhealthy 預設回 503
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthResponseAsync
        }).AllowAnonymous();

        return endpoints;
    }

    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            version = AppVersion.Current,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = (long)e.Value.Duration.TotalMilliseconds
            })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }
}
