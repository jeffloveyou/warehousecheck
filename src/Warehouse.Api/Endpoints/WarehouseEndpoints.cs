using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Application.Scanning;
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

        // 單據附件（M4-10）：PDF 一律下載（Content-Disposition: attachment），不在瀏覽器內以同源執行
        endpoints.MapGet("/files/document-attachments/{id:guid}/{variant:regex(^(thumb|file)$)}", async Task<IResult> (
                IDocumentAttachmentService attachments, Guid id, string variant, HttpContext context, CancellationToken ct) =>
            {
                var file = await attachments.OpenAsync(id, string.Equals(variant, "thumb", StringComparison.OrdinalIgnoreCase), ct);
                if (file is null)
                {
                    return TypedResults.NotFound();
                }
                context.Response.Headers.CacheControl = "private, max-age=86400";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return file.ContentType == "application/pdf"
                    ? TypedResults.File(file.Content, file.ContentType, $"{id:N}.pdf")
                    : TypedResults.File(file.Content, file.ContentType);
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

        // 掃碼內容解析（M3-04；M5-01 改用 IScanResolver）：儲位、物料（料號／原廠條碼／專屬別名精確相同）、一般文字或拒絕。
        // 舊欄位 warehouseCode、locationCode、itemCode 保留（A/B 相容窗口內舊版畫面仍可用）；
        // warehouseId 有值時，別的倉庫的儲位 QR 會被拒絕，且可用沒有前綴的儲位碼。
        group.MapGet("/scan", async (IScanResolver resolver, string? code, Guid? warehouseId, CancellationToken ct) =>
        {
            var result = await resolver.ResolveAsync(code, warehouseId, ct);
            return TypedResults.Ok(new
            {
                result.Kind,
                result.Value,
                result.Message,
                WarehouseCode = result.Location?.WarehouseCode,
                LocationCode = result.Location?.Code,
                ItemCode = result.Item?.ItemCode,
                result.Location,
                result.Item,
            });
        }).AddEndpointFilter(JsonOnlyFilter.InvokeAsync);
    }
}
