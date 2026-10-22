using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;

namespace Warehouse.Web.Pages.Stock;

/// <summary>
/// 異動歷史（任何登入者可看）：料號、倉庫、儲位碼、單號、操作者、日期區間篩選，新到舊、伺服器分頁。
/// 日期以台灣當地日期輸入，查詢時轉成 UTC 的 [起日 00:00, 迄日隔天 00:00)。
/// </summary>
public class MovementsModel(IStockQueryService stock, IWarehouseService warehouses) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ItemCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? LocationCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? DocNo { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ActorName { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    public PagedResult<MovementRow> Result { get; private set; } = null!;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public string? Notice { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        if (From is { } from && To is { } to && from > to)
        {
            Notice = "起日不可晚於迄日。";
            Result = new PagedResult<MovementRow>([], 1, Paging.DefaultPageSize, 0);
            return;
        }
        var query = new MovementQuery(
            ItemCode, WarehouseId, LocationCode, DocNo, ActorName,
            From is { } f ? TaipeiTime.StartOfDay(f) : null,
            To is { } t ? TaipeiTime.StartOfDay(t.AddDays(1)) : null,
            PageNumber);
        Result = await stock.SearchMovementsAsync(query, cancellationToken);
    }

    public string PageUrl(int page) => Url.Page("/Stock/Movements", new
    {
        ItemCode, WarehouseId, LocationCode, DocNo, ActorName,
        From = From?.ToString("yyyy-MM-dd"),
        To = To?.ToString("yyyy-MM-dd"),
        p = page,
    })!;
}
