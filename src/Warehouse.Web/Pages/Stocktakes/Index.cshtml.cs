using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Stocktakes;

/// <summary>
/// 盤點單列表與開始盤點（M6-02）。任何登入者可看；開始盤點限倉庫主管（StocktakeManage），服務層另檢查倉庫範圍。
/// 開始表單帶開頁時產生的 RequestId：連點或重新整理後重送，只會建立一張盤點單。
/// </summary>
public class IndexModel(IStocktakeService stocktakes, IWarehouseService warehouses, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public StocktakeStatus? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public StartForm Start { get; set; } = new();

    public PagedResult<StocktakeRow> Result { get; private set; } = new([], 1, Paging.DefaultPageSize, 0);

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public IReadOnlySet<Guid>? Operable { get; private set; }

    public bool CanStart { get; private set; }

    public string? Error { get; private set; }

    public sealed class StartForm
    {
        public Guid WarehouseId { get; set; }

        /// <summary>儲位碼（逗號、空白或換行分隔）；空白＝倉庫內全部未停用的儲位。</summary>
        public string? LocationCodes { get; set; }

        public string? Note { get; set; }

        public Guid RequestId { get; set; } = Guid.NewGuid();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.StocktakeManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            var codes = (Start.LocationCodes ?? string.Empty)
                .Split([',', '，', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var id = await stocktakes.StartAsync(Start.WarehouseId, codes, Start.Note, User.ToActor(), cancellationToken, Start.RequestId);
            TempData["Message"] = "已開始盤點，快照已建立。";
            return RedirectToPage("/Stocktakes/Details", new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public string PageUrl(int page) => Url.Page("/Stocktakes/Index", new { WarehouseId, Status, p = page })!;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        Operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        CanStart = (await authorization.AuthorizeAsync(User, Policies.StocktakeManage)).Succeeded;
        Result = await stocktakes.ListAsync(WarehouseId, Status, PageNumber, cancellationToken);
    }
}
