using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Domain.Items;

namespace Warehouse.Api.Endpoints;

/// <summary>提出物料。<see cref="RequestId"/> 由用戶端產生（每張表單一個），重送時回傳同一筆結果。</summary>
public sealed record SubmitItemRequest(
    Guid RequestId,
    string Name,
    Guid CategoryId,
    Guid BaseUnitId,
    TrackingMode TrackingMode,
    string? Brand,
    string? ManufacturerPartNo,
    string? Specification);

public sealed record ApproveItemRequest(uint Version, IReadOnlyList<Guid>? AcknowledgedCandidateIds, string? Comment);

public sealed record ReturnItemRequest(uint Version, string Comment);

public sealed record RejectItemRequest(uint Version, string Comment, Guid? DuplicateOfItemId);

public sealed record ApproveItemResponse(string ItemCode);

/// <summary>新增索引。<see cref="PackQuantity"/>、<see cref="Symbology"/> 只用於原廠條碼（包裝數量預設 1）。</summary>
public sealed record AddIndexRequest(string Text, decimal? PackQuantity = null, string? Symbology = null);

public sealed record AddIndexResponse(Guid EntryId);

/// <summary>
/// /api/items：給手機／PDA 等程式呼叫。網頁畫面直接呼叫同一組 Application 服務，不經過這些端點。
/// 錯誤由 <see cref="Errors.ApiExceptionHandler"/> 轉成 ProblemDetails。
/// 寫入一律只收 JSON（<see cref="JsonOnlyFilter"/>，跨站表單送不出），且沒有開放 CORS，所以不另做 antiforgery（ADR-0012）。
/// </summary>
public static class ItemEndpoints
{
    public static void MapItemEndpoints(this IEndpointRouteBuilder api)
    {
        var items = api.MapGroup("/items").RequireAuthorization(Policies.ItemsRead).AddEndpointFilter(JsonOnlyFilter.InvokeAsync);

        items.MapGet("/", SearchAsync);
        items.MapGet("/duplicate-candidates", FindDuplicatesAsync);
        items.MapGet("/{id:guid}", GetAsync);
        items.MapPost("/", SubmitAsync).RequireAuthorization(Policies.ItemsSubmit);
        items.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(Policies.ItemsReview);
        items.MapPost("/{id:guid}/return", ReturnAsync).RequireAuthorization(Policies.ItemsReview);
        items.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(Policies.ItemsReview);

        // 搜尋索引（M2）：kind 為 aliases（專屬別名）、keywords（共用關鍵字）或 barcodes（原廠條碼，M5-01）
        items.MapGet("/{id:guid}/index", (IItemIndexService index, Guid id, CancellationToken ct) => index.ListAsync(id, ct));
        // 兩組固定路由各自帶入種類（路由比對不分大小寫，不能靠解析網址字串判斷種類，M2 審查 R2）
        foreach (var (segment, kind) in new[] { ("aliases", ItemIndexKind.Alias), ("keywords", ItemIndexKind.Keyword), ("barcodes", ItemIndexKind.Barcode) })
        {
            items.MapPost($"/{{id:guid}}/{segment}",
                    (IItemIndexService index, Guid id, AddIndexRequest request, HttpContext context, CancellationToken ct) =>
                        AddIndexAsync(index, id, kind, request, context, ct))
                .RequireAuthorization(Policies.ItemsIndex);
            items.MapPost($"/{{id:guid}}/{segment}/{{entryId:guid}}/deactivate",
                    (IItemIndexService index, Guid id, Guid entryId, HttpContext context, CancellationToken ct) =>
                        DeactivateIndexAsync(index, id, kind, entryId, context, ct))
                .RequireAuthorization(Policies.ItemsIndex);
        }
    }

    private static async Task<Created<AddIndexResponse>> AddIndexAsync(
        IItemIndexService index, Guid id, ItemIndexKind kind, AddIndexRequest request, HttpContext context,
        CancellationToken cancellationToken)
    {
        var entryId = await index.AddAsync(
            id, kind, request.Text, context.User.ToActor(), cancellationToken, request.PackQuantity ?? 1m, request.Symbology);
        return TypedResults.Created($"/api/items/{id}/index", new AddIndexResponse(entryId));
    }

    private static async Task<NoContent> DeactivateIndexAsync(
        IItemIndexService index, Guid id, ItemIndexKind kind, Guid entryId, HttpContext context, CancellationToken cancellationToken)
    {
        await index.DeactivateAsync(id, kind, entryId, context.User.ToActor(), cancellationToken);
        return TypedResults.NoContent();
    }
    private static Task<PagedResult<ItemListRow>> SearchAsync(
        IItemQueryService queries, string? keyword, ItemStatus? status, Guid? categoryId, int? page, int? pageSize,
        CancellationToken cancellationToken) =>
        queries.SearchAsync(
            new ItemSearchQuery(keyword, status, categoryId, page ?? 1, pageSize ?? Paging.DefaultPageSize),
            cancellationToken);

    private static Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(
        IItemWorkflowService workflow, string? name, string? partNo, Guid? excludeItemId,
        CancellationToken cancellationToken) =>
        workflow.FindDuplicateCandidatesAsync(name ?? string.Empty, partNo, excludeItemId, cancellationToken);

    private static async Task<Results<Ok<ItemDetails>, NotFound>> GetAsync(
        IItemQueryService queries, Guid id, CancellationToken cancellationToken) =>
        await queries.GetAsync(id, cancellationToken) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound();

    private static async Task<Created<SubmitItemResult>> SubmitAsync(
        IItemWorkflowService workflow, SubmitItemRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        var draft = new ItemDraft(
            request.Name, request.CategoryId, request.BaseUnitId, request.TrackingMode,
            request.Brand, request.ManufacturerPartNo, request.Specification);
        var result = await workflow.SubmitAsync(request.RequestId, draft, context.User.ToActor(), cancellationToken);
        return TypedResults.Created($"/api/items/{result.ItemId}", result);
    }

    private static async Task<Ok<ApproveItemResponse>> ApproveAsync(
        IItemWorkflowService workflow, Guid id, ApproveItemRequest request, HttpContext context,
        CancellationToken cancellationToken)
    {
        var code = await workflow.ApproveAsync(
            id, request.Version, request.AcknowledgedCandidateIds ?? [], request.Comment, context.User.ToActor(),
            cancellationToken);
        return TypedResults.Ok(new ApproveItemResponse(code));
    }

    private static async Task<NoContent> ReturnAsync(
        IItemWorkflowService workflow, Guid id, ReturnItemRequest request, HttpContext context,
        CancellationToken cancellationToken)
    {
        await workflow.ReturnAsync(id, request.Version, request.Comment, context.User.ToActor(), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RejectAsync(
        IItemWorkflowService workflow, Guid id, RejectItemRequest request, HttpContext context,
        CancellationToken cancellationToken)
    {
        await workflow.RejectAsync(
            id, request.Version, request.Comment, request.DuplicateOfItemId, context.User.ToActor(), cancellationToken);
        return TypedResults.NoContent();
    }
}
