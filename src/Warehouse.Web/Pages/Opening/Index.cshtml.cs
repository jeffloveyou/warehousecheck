using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Web.Security;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.Opening;

/// <summary>期初單清單（任何登入者可看）；作業員、倉庫主管可以建立（Opening.Edit）。</summary>
public class IndexModel(IOpeningService openings, IWarehouseService warehouses, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public StockDocumentStatus? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public string? Note { get; set; }

    public PagedResult<OpeningRow> Result { get; private set; } = null!;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    /// <summary>可作業的倉庫（null＝全部），建單下拉選單只列這些（M6-01b）。</summary>
    public IReadOnlySet<Guid>? Operable { get; private set; }

    public bool CanEdit { get; private set; }

    public string? Error { get; private set; }

    public Task OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.OpeningEdit)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            if (WarehouseId is not { } warehouseId)
            {
                throw new DomainRuleException("請選擇倉庫。");
            }
            var id = await openings.CreateAsync(warehouseId, Note, User.ToActor(), cancellationToken);
            return RedirectToPage("/Opening/Details", new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public string PageUrl(int page) => Url.Page("/Opening/Index", new { WarehouseId, Status, p = page })!;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        Operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        Result = await openings.ListAsync(WarehouseId, Status, PageNumber, cancellationToken);
        CanEdit = (await authorization.AuthorizeAsync(User, Policies.OpeningEdit)).Succeeded;
    }
}
