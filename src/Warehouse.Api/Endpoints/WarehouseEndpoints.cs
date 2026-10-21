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
                var found = await warehouses.SearchLocationsAsync(new LocationSearch(null, scan.LocationCode, null, 1, 50), ct);
                location = found.Items.FirstOrDefault(l => l.WarehouseCode == scan.WarehouseCode && l.Code == scan.LocationCode);
            }
            return TypedResults.Ok(new { scan.Kind, scan.WarehouseCode, scan.LocationCode, scan.ItemCode, Location = location });
        });
    }
}
