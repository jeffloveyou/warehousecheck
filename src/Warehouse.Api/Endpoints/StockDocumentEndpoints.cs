using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Warehouse.Api.Authorization;
using Warehouse.Application.Inventory;
using Warehouse.Application.MasterData;
using Warehouse.Domain.Inventory;

namespace Warehouse.Api.Endpoints;

public sealed record CreateDocumentRequest(
    StockDocumentType Type, Guid WarehouseId, Guid? PurposeId, Guid? PartnerId, string? ExternalNo, string? Note);

public sealed record UpdateDocumentHeaderRequest(uint Version, Guid? PurposeId, Guid? PartnerId, string? ExternalNo, string? Note);

public sealed record DocumentVersionRequest(uint Version);

public sealed record ReturnDocumentRequest(uint Version, string Comment);

public sealed record CreatePurposeRequest(PurposeDirection Direction, string Code, string Name, int SortOrder);

public sealed record UpdatePurposeRequest(uint Version, string Name, int SortOrder);

public sealed record CreatePartnerRequest(PartnerType Type, string Code, string Name);

public sealed record RenamePartnerRequest(uint Version, string Name);

public sealed record SetActiveRequest(uint Version, bool IsActive);

/// <summary>
/// /api/stock/documents：進倉、出倉、移庫單（M4-09～11）。送審、確認、核准會改變預留或結存，
/// 一律需要 <c>Idempotency-Key</c> 標頭（同一鍵重送回 204、不會重複過帳）。
/// /api/master/purposes、/api/master/partners：用途類型與往來對象主檔（M4-10）。
/// </summary>
public static class StockDocumentEndpoints
{
    public static void MapStockDocumentEndpoints(this IEndpointRouteBuilder api)
    {
        var docs = api.MapGroup("/stock/documents").RequireAuthorization(Policies.StockRead).AddEndpointFilter(JsonOnlyFilter.InvokeAsync);

        docs.MapGet("", (IStockDocumentService documents, StockDocumentType? type, StockDocumentStatus? status, Guid? warehouseId,
                int? page, CancellationToken ct) => documents.ListAsync(type, status, warehouseId, page ?? 1, ct));

        docs.MapGet("/{id:guid}", async Task<Results<Ok<DocumentDetails>, NotFound>> (
                IStockDocumentService documents, Guid id, CancellationToken ct) =>
            await documents.GetAsync(id, ct) is { } doc ? TypedResults.Ok(doc) : TypedResults.NotFound());

        docs.MapPost("", async Task<Created<Guid>> (
                IStockDocumentService documents, CreateDocumentRequest request, HttpContext context, CancellationToken ct) =>
            {
                var id = await documents.CreateAsync(request.Type, request.WarehouseId,
                    new StockDocumentHeader(request.PurposeId, request.PartnerId, request.ExternalNo, request.Note), context.User.ToActor(), ct);
                return TypedResults.Created($"/api/stock/documents/{id}", id);
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapPut("/{id:guid}/header", async Task<NoContent> (
                IStockDocumentService documents, Guid id, UpdateDocumentHeaderRequest request, HttpContext context, CancellationToken ct) =>
            {
                await documents.UpdateHeaderAsync(id, request.Version,
                    new StockDocumentHeader(request.PurposeId, request.PartnerId, request.ExternalNo, request.Note), context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapPost("/{id:guid}/lines", async Task<Created<Guid>> (
                IStockDocumentService documents, Guid id, DocumentLineInput request, HttpContext context, CancellationToken ct) =>
            {
                var lineId = await documents.AddLineAsync(id, request, context.User.ToActor(), ct);
                return TypedResults.Created($"/api/stock/documents/{id}", lineId);
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapDelete("/{id:guid}/lines/{lineId:guid}", async Task<NoContent> (
                IStockDocumentService documents, Guid id, Guid lineId, HttpContext context, CancellationToken ct) =>
            {
                await documents.RemoveLineAsync(id, lineId, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapPost("/{id:guid}/submit", async Task<NoContent> (
                IStockDocumentService documents, Guid id, DocumentVersionRequest request,
                [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, HttpContext context, CancellationToken ct) =>
            {
                await documents.SubmitAsync(id, request.Version, idempotencyKey, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapPost("/{id:guid}/confirm", async Task<NoContent> (
                IStockDocumentService documents, Guid id, DocumentVersionRequest request,
                [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, HttpContext context, CancellationToken ct) =>
            {
                await documents.ConfirmAsync(id, request.Version, idempotencyKey, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapPost("/{id:guid}/approve", async Task<NoContent> (
                IStockDocumentService documents, Guid id, DocumentVersionRequest request,
                [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, HttpContext context, CancellationToken ct) =>
            {
                await documents.ApproveAsync(id, request.Version, idempotencyKey, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentApprove);

        docs.MapPost("/{id:guid}/return", async Task<NoContent> (
                IStockDocumentService documents, Guid id, ReturnDocumentRequest request, HttpContext context, CancellationToken ct) =>
            {
                await documents.ReturnAsync(id, request.Version, request.Comment, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentApprove);

        docs.MapPost("/{id:guid}/cancel", async Task<NoContent> (
                IStockDocumentService documents, Guid id, DocumentVersionRequest request, HttpContext context, CancellationToken ct) =>
            {
                await documents.CancelAsync(id, request.Version, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.DocumentEdit);

        docs.MapPost("/{id:guid}/reverse", async Task<Created<Guid>> (
                IStockDocumentService documents, Guid id, HttpContext context, CancellationToken ct) =>
            {
                var reversal = await documents.CreateReversalAsync(id, context.User.ToActor(), ct);
                return TypedResults.Created($"/api/stock/documents/{reversal}", reversal);
            })
            .RequireAuthorization(Policies.DocumentEdit);

        var master = api.MapGroup("/master").RequireAuthorization(Policies.StockRead).AddEndpointFilter(JsonOnlyFilter.InvokeAsync);

        master.MapGet("/purposes", (IDocumentMasterDataService service, PurposeDirection? direction, bool? includeInactive,
                CancellationToken ct) => service.ListPurposesAsync(direction, includeInactive ?? false, ct));

        master.MapPost("/purposes", async Task<Created<Guid>> (
                IDocumentMasterDataService service, CreatePurposeRequest request, HttpContext context, CancellationToken ct) =>
            {
                var id = await service.CreatePurposeAsync(
                    new PurposeInput(request.Direction, request.Code, request.Name, request.SortOrder), context.User.ToActor(), ct);
                return TypedResults.Created($"/api/master/purposes/{id}", id);
            })
            .RequireAuthorization(Policies.PartnersManage);

        master.MapPut("/purposes/{id:guid}", async Task<NoContent> (
                IDocumentMasterDataService service, Guid id, UpdatePurposeRequest request, HttpContext context, CancellationToken ct) =>
            {
                await service.UpdatePurposeAsync(id, request.Version, request.Name, request.SortOrder, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.PartnersManage);

        master.MapPut("/purposes/{id:guid}/active", async Task<NoContent> (
                IDocumentMasterDataService service, Guid id, SetActiveRequest request, HttpContext context, CancellationToken ct) =>
            {
                await service.SetPurposeActiveAsync(id, request.Version, request.IsActive, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.PartnersManage);

        master.MapGet("/partners", (IDocumentMasterDataService service, PartnerType? type, string? q, bool? includeInactive,
                int? page, CancellationToken ct) => service.ListPartnersAsync(type, q, includeInactive ?? false, page ?? 1, ct));

        master.MapPost("/partners", async Task<Created<Guid>> (
                IDocumentMasterDataService service, CreatePartnerRequest request, HttpContext context, CancellationToken ct) =>
            {
                var id = await service.CreatePartnerAsync(new PartnerInput(request.Type, request.Code, request.Name), context.User.ToActor(), ct);
                return TypedResults.Created($"/api/master/partners/{id}", id);
            })
            .RequireAuthorization(Policies.PartnersManage);

        master.MapPut("/partners/{id:guid}", async Task<NoContent> (
                IDocumentMasterDataService service, Guid id, RenamePartnerRequest request, HttpContext context, CancellationToken ct) =>
            {
                await service.RenamePartnerAsync(id, request.Version, request.Name, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.PartnersManage);

        master.MapPut("/partners/{id:guid}/active", async Task<NoContent> (
                IDocumentMasterDataService service, Guid id, SetActiveRequest request, HttpContext context, CancellationToken ct) =>
            {
                await service.SetPartnerActiveAsync(id, request.Version, request.IsActive, context.User.ToActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.PartnersManage);
    }
}
