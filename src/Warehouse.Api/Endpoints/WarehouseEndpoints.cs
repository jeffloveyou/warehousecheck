using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Api.Endpoints;

/// <summary>
/// 倉庫與儲位查詢（給手機／PDA 的儲位選擇與掃碼）。維護動作目前只在網頁（倉庫主管）。
/// </summary>
public static class WarehouseEndpoints
{
    /// <summary>
    /// 倉位圖片檔案（M3-08）：/files/location-images/{id}/thumb 或 /large。要登入（Stock.Read）；
    /// 圖片內容不會變（新圖是新 ID），所以讓瀏覽器私人快取一天，舊電腦重看不必重新下載。
    /// </summary>
    public static void MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/files/location-images/{id:guid}/{variant:regex(^(thumb|large)$)}", async Task<IResult> (
                ILocationImageService images, Guid id, string variant, HttpContext context, CancellationToken ct) =>
            {
                var file = await images.OpenAsync(id, string.Equals(variant, "thumb", StringComparison.OrdinalIgnoreCase), ct);
                if (file is null)
                {
                    return TypedResults.NotFound();
                }
                context.Response.Headers.CacheControl = "private, max-age=86400";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return TypedResults.File(file.Content, file.ContentType);
            })
            .RequireAuthorization(Policies.StockRead);
    }

    public static void MapWarehouseEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("").RequireAuthorization(Policies.StockRead);

        group.MapGet("/warehouses", (IWarehouseService warehouses, CancellationToken ct) => warehouses.ListWarehousesAsync(ct));

        group.MapGet("/locations", (IWarehouseService warehouses, Guid? warehouseId, string? q, LocationStatus? status,
                int? page, int? pageSize, CancellationToken ct) =>
            warehouses.SearchLocationsAsync(
                new LocationSearch(warehouseId, q, status, page ?? 1, pageSize ?? Paging.DefaultPageSize), ct));

        group.MapGet("/locations/{id:guid}", async Task<Results<Ok<LocationRow>, NotFound>> (
                IWarehouseService warehouses, Guid id, CancellationToken ct) =>
            await warehouses.GetLocationAsync(id, ct) is { } row ? TypedResults.Ok(row) : TypedResults.NotFound());

        // 掃碼內容解析（M3-04）：判斷是儲位、物料或一般文字；儲位會一併回傳查到的儲位
        group.MapGet("/scan", async (IWarehouseService warehouses, string? code, CancellationToken ct) =>
        {
            var scan = ScanCodeParser.Parse(code);
            LocationRow? location = null;
            if (scan.Kind == ScanCodeKind.Location)
            {
                location = await warehouses.GetLocationByCodeAsync(scan.WarehouseCode!, scan.LocationCode!, ct);
            }
            return TypedResults.Ok(new { scan.Kind, scan.WarehouseCode, scan.LocationCode, scan.ItemCode, Location = location });
        });
    }
}
