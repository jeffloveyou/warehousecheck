using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Warehouse.Api.Authorization;
using Warehouse.Api.Endpoints;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages;

/// <summary>依登入權限顯示作業入口與有界待辦；每次載入查詢，不輪詢。</summary>
public class IndexModel(IDatabaseStatus databaseStatus, IScanCartService carts, IStockDocumentService documents,
    IItemQueryService items, IAuthorizationService authorization) : PageModel
{
    public SystemInfoResponse Info { get; private set; } = null!;
    public PagedResult<ScanCartSummary>? Carts { get; private set; }
    public PagedResult<DocumentRow>? Drafts { get; private set; }
    public PagedResult<DocumentRow>? Returned { get; private set; }
    public PagedResult<DocumentRow>? Posted { get; private set; }
    public PagedResult<DocumentRow>? Pending { get; private set; }
    public PagedResult<ItemListRow>? PendingItems { get; private set; }
    public bool CanWork { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Info = await SystemEndpoints.GetSystemInfoAsync(databaseStatus, cancellationToken);
        if (User.Identity?.IsAuthenticated != true || !Info.DatabaseConnected
            || User.HasClaim(ActorClaims.MustChangePassword, "true")) return;
        var actor = User.ToActor();
        CanWork = (await authorization.AuthorizeAsync(User, Policies.DocumentEdit)).Succeeded;
        if (CanWork)
        {
            Carts = await carts.ListPageAsync(actor, 1, cancellationToken);
            Drafts = await documents.ListAsync(null, StockDocumentStatus.Draft, null, 1, cancellationToken,
                filter: new(CreatedById: actor.Id));
            Returned = await documents.ListAsync(null, StockDocumentStatus.Draft, null, 1, cancellationToken,
                filter: new(CreatedById: actor.Id, ReturnedOnly: true));
            Posted = await documents.ListAsync(null, StockDocumentStatus.Posted, null, 1, cancellationToken,
                filter: new(CreatedById: actor.Id));
        }
        if ((await authorization.AuthorizeAsync(User, Policies.DocumentApprove)).Succeeded)
        {
            var operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
            Pending = await documents.ListAsync(null, StockDocumentStatus.Submitted, null, 1, cancellationToken,
                filter: new(AllowedWarehouseIds: operable?.ToArray()));
        }
        if ((await authorization.AuthorizeAsync(User, Policies.ItemsReview)).Succeeded)
            PendingItems = await items.SearchAsync(new(null, ItemStatus.PendingApproval, null, 1, 5), cancellationToken);
    }
}
