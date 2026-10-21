using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Warehouse.Api.Authorization;
using Warehouse.Application.Inventory;

namespace Warehouse.Api.Endpoints;

public sealed record ApproveOpeningRequest(uint Version);

/// <summary>
/// /api/stock：結存查詢、期初單核准。庫存寫入一律需要 <c>Idempotency-Key</c> 標頭（AGENTS.md 第五節），
/// 同一鍵重送回傳同一結果、不會重複過帳。
/// </summary>
public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder api)
    {
        var stock = api.MapGroup("/stock").RequireAuthorization(Policies.StockRead).AddEndpointFilter(JsonOnlyFilter.InvokeAsync);

        stock.MapGet("/balances", (IStockQueryService queries, Guid? warehouseId, Guid? locationId, Guid? itemId, int? page,
                CancellationToken ct) => queries.GetBalancesAsync(warehouseId, locationId, itemId, page ?? 1, ct));

        stock.MapGet("/opening/{id:guid}", async Task<Results<Ok<OpeningDetails>, NotFound>> (
                IOpeningService openings, Guid id, CancellationToken ct) =>
            await openings.GetAsync(id, ct) is { } doc ? TypedResults.Ok(doc) : TypedResults.NotFound());

        stock.MapPost("/opening/{id:guid}/approve", async Task<NoContent> (
                IOpeningService openings, Guid id, ApproveOpeningRequest request,
                [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, HttpContext context, CancellationToken ct) =>
            {
                await openings.ApproveAsync(id, request.Version, idempotencyKey, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.OpeningApprove);
    }
}
