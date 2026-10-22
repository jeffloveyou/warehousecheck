using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;

namespace Warehouse.Web.Pages.Stock;

/// <summary>待補帳（任何登入者可看）：目前結存為負的料號＋儲位，以及最後一筆造成負數的單據。伺服器分頁。</summary>
public class NegativeModel(IStockQueryService stock, IWarehouseService warehouses) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    public PagedResult<NegativeBalanceRow> Result { get; private set; } = null!;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        Result = await stock.GetNegativeBalancesAsync(WarehouseId, PageNumber, cancellationToken);
    }

    public string PageUrl(int page) => Url.Page("/Stock/Negative", new { WarehouseId, p = page })!;
}
