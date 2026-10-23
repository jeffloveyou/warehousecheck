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
        if (DateError() is { } dateError)
        {
            Notice = dateError;
            Result = new PagedResult<MovementRow>([], 1, Paging.DefaultPageSize, 0);
            return;
        }
        Result = await stock.SearchMovementsAsync(Query(), cancellationToken);
    }

    /// <summary>匯出異動（M6-03）：條件同畫面（沒有料號、儲位、單號與起日時只匯最近 30 天），不分頁。</summary>
    public Task<IActionResult> OnGetExportAsync(
        [FromServices] Warehouse.Application.Exports.IExportService export, [FromServices] ExportGate gate, CancellationToken cancellationToken)
    {
        var back = PageUrl(1);
        if (DateError() is { } dateError)
        {
            TempData["ExportError"] = dateError;
            return Task.FromResult<IActionResult>(LocalRedirect(back));
        }
        return gate.RunAsync(this, () => export.MovementsAsync(Query(), cancellationToken), back);
    }

    public string ExportUrl => Url.Page("/Stock/Movements", "Export", new
    {
        ItemCode, WarehouseId, LocationCode, DocNo, ActorName, From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd"),
    })!;

    /// <summary>日期檢查：起日不可晚於迄日；手改網址的極端日期（0001-01-01、9999-12-31）轉 UTC 會溢位，先擋下。</summary>
    private string? DateError()
    {
        var earliest = new DateOnly(2000, 1, 1);
        var latest = new DateOnly(2100, 12, 31);
        if (From < earliest || To < earliest || From > latest || To > latest)
        {
            return "日期超出可查詢範圍（2000-01-01 至 2100-12-31）。";
        }
        return From is { } from && To is { } to && from > to ? "起日不可晚於迄日。" : null;
    }

    private MovementQuery Query() => new(
        ItemCode, WarehouseId, LocationCode, DocNo, ActorName,
        From is { } f ? TaipeiTime.StartOfDay(f) : null,
        To is { } t ? TaipeiTime.StartOfDay(t.AddDays(1)) : null,
        PageNumber);

    public string PageUrl(int page) => Url.Page("/Stock/Movements", new
    {
        ItemCode, WarehouseId, LocationCode, DocNo, ActorName,
        From = From?.ToString("yyyy-MM-dd"),
        To = To?.ToString("yyyy-MM-dd"),
        p = page,
    })!;
}
