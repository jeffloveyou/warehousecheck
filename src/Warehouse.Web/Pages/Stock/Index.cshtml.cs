using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Text;

namespace Warehouse.Web.Pages.Stock;

/// <summary>
/// 庫存查詢（任何登入者）：依倉庫、儲位碼、料號查結存與異動；也列出尚未清點的儲位。
/// 「可用」只在結存狀態可用、且儲位目前是有效時才算（待驗、停用儲位的庫存不算可用量）。
/// </summary>
public class IndexModel(
    IStockQueryService stock, IWarehouseService warehouses, IItemQueryService items) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? LocationCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ItemCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? DocumentId { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public PagedResult<BalanceRow>? Balances { get; private set; }

    public PagedResult<MovementRow>? Movements { get; private set; }

    public PagedResult<UncountedLocationRow>? Uncounted { get; private set; }

    public string? Notice { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        Guid? locationId = null;
        Guid? itemId = null;
        if (!string.IsNullOrWhiteSpace(LocationCode))
        {
            var warehouse = Warehouses.FirstOrDefault(w => w.Id == WarehouseId);
            if (warehouse is null)
            {
                Notice = "以儲位碼查詢時請先選擇倉庫。";
                return;
            }
            var location = await warehouses.GetLocationByCodeAsync(warehouse.Code, LocationCode, cancellationToken);
            if (location is null)
            {
                Notice = $"{warehouse.Code} 沒有儲位 {LocationCode}。";
                return;
            }
            locationId = location.Id;
        }
        if (!string.IsNullOrWhiteSpace(ItemCode))
        {
            var code = TextNormalizer.Normalize(ItemCode);
            itemId = await items.FindIdByItemCodeAsync(code, cancellationToken);
            if (itemId is null)
            {
                Notice = $"找不到料號 {code}。";
                return;
            }
        }
        if (WarehouseId is null && locationId is null && itemId is null && DocumentId is null)
        {
            return; // 不做無條件查詢
        }

        // 只給單據時只查這張單的異動（不做不帶條件的結存查詢）
        if (WarehouseId is not null || locationId is not null || itemId is not null)
        {
            Balances = await stock.GetBalancesAsync(WarehouseId, locationId, itemId, PageNumber, cancellationToken);
        }
        Movements = await stock.GetMovementsAsync(itemId, locationId, DocumentId, 1, cancellationToken);
        if (WarehouseId is { } w && locationId is null && itemId is null)
        {
            Uncounted = await stock.GetUncountedLocationsAsync(w, 1, cancellationToken);
        }
    }

    public string PageUrl(int page) => Url.Page("/Stock/Index", new { WarehouseId, LocationCode, ItemCode, DocumentId, p = page })!;
}
