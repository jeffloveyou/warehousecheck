using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Warehouse.Api.Authorization;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Inventory;

namespace Warehouse.Api.Endpoints;

public sealed record NegativePolicyRequest(uint Version, bool Allow, decimal Limit);

/// <summary>
/// /api/stock/reservations：手動預留（M4-07；建立需 <c>Idempotency-Key</c>）。
/// /api/stock/negative：待補帳清單；/api/items/{id}/negative-policy：負庫存設定（M4-08，物料管理員）。
/// </summary>
public static class ReservationEndpoints
{
    public static void MapReservationEndpoints(this IEndpointRouteBuilder api)
    {
        var stock = api.MapGroup("/stock").RequireAuthorization(Policies.StockRead).AddEndpointFilter(JsonOnlyFilter.InvokeAsync);

        stock.MapGet("/reservations", (IStockReservationService reservations, string? itemCode, Guid? warehouseId,
                ReservationStatus? status, int? page, CancellationToken ct) =>
            reservations.ListAsync(itemCode, warehouseId, status, page ?? 1, ct));

        stock.MapPost("/reservations", async Task<Created<Guid>> (
                IStockReservationService reservations, ReservationInput request,
                [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, HttpContext context, CancellationToken ct) =>
            {
                var id = await reservations.CreateAsync(request, idempotencyKey, context.User.ToActor(), ct);
                return TypedResults.Created($"/api/stock/reservations/{id}", id);
            })
            .RequireAuthorization(Policies.ReservationManage);

        stock.MapPost("/reservations/{id:guid}/cancel", async Task<NoContent> (
                IStockReservationService reservations, Guid id, HttpContext context, CancellationToken ct) =>
            {
                await reservations.CancelAsync(id, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.ReservationManage);

        stock.MapGet("/negative", (IStockQueryService queries, Guid? warehouseId, int? page, CancellationToken ct) =>
            queries.GetNegativeBalancesAsync(warehouseId, page ?? 1, ct));

        // 異動歷史（M4-05）：日期為 UTC 的 [from, to)
        stock.MapGet("/movements", (IStockQueryService queries, string? itemCode, Guid? warehouseId, string? locationCode,
                string? docNo, string? actor, DateTimeOffset? from, DateTimeOffset? to, int? page, CancellationToken ct) =>
            queries.SearchMovementsAsync(new MovementQuery(itemCode, warehouseId, locationCode, docNo, actor, from, to, page ?? 1), ct));

        // 異常追蹤（M4-05）
        stock.MapGet("/anomalies/failures", (IStockAnomalyService anomalies, int? page, CancellationToken ct) =>
            anomalies.GetPostingFailuresAsync(page ?? 1, ct));
        stock.MapGet("/anomalies/document-events", (IStockAnomalyService anomalies, int? page, CancellationToken ct) =>
            anomalies.GetDocumentEventsAsync(page ?? 1, ct));
        stock.MapGet("/anomalies/stale-documents", (IStockAnomalyService anomalies, int? page, CancellationToken ct) =>
            anomalies.GetStaleDocumentsAsync(page ?? 1, ct));
        stock.MapGet("/anomalies/stale-reservations", (IStockAnomalyService anomalies, int? page, CancellationToken ct) =>
            anomalies.GetStaleReservationsAsync(page ?? 1, ct));

        api.MapPut("/items/{id:guid}/negative-policy", async Task<NoContent> (
                IItemNegativePolicyService policies, Guid id, NegativePolicyRequest request, HttpContext context, CancellationToken ct) =>
            {
                await policies.SetAsync(id, request.Version, request.Allow, request.Limit, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.NegativePolicy)
            .AddEndpointFilter(JsonOnlyFilter.InvokeAsync);
    }
}
