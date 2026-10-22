using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;

namespace Warehouse.Web.Pages.Stock;

/// <summary>
/// 庫存異常追蹤（任何登入者可看）：被拒絕的過帳嘗試、取消／退回／沖銷的單據、久未處理的單據、需要注意的預留。
/// 用 GET <c>tab=</c> 切換分頁籤（只查目前這一個），各自伺服器分頁 <c>p</c>；負庫存在 /Stock/Negative。
/// </summary>
public class AnomaliesModel(IStockAnomalyService anomalies, TimeProvider clock) : PageModel
{
    public const string TabFailures = "failures";
    public const string TabEvents = "events";
    public const string TabStaleDocuments = "stale-documents";
    public const string TabStaleReservations = "stale-reservations";

    [BindProperty(SupportsGet = true, Name = "tab")]
    public string? TabName { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    /// <summary>目前分頁籤；不認得的值當成第一個。</summary>
    public string Tab => TabName is TabEvents or TabStaleDocuments or TabStaleReservations ? TabName : TabFailures;

    public PagedResult<PostingFailureRow>? Failures { get; private set; }

    public PagedResult<DocumentEventRow>? Events { get; private set; }

    public PagedResult<StaleDocumentRow>? StaleDocuments { get; private set; }

    public PagedResult<StaleReservationRow>? StaleReservations { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        switch (Tab)
        {
            case TabEvents:
                Events = await anomalies.GetDocumentEventsAsync(PageNumber, cancellationToken);
                break;
            case TabStaleDocuments:
                StaleDocuments = await anomalies.GetStaleDocumentsAsync(PageNumber, cancellationToken);
                break;
            case TabStaleReservations:
                StaleReservations = await anomalies.GetStaleReservationsAsync(PageNumber, cancellationToken);
                break;
            default:
                Failures = await anomalies.GetPostingFailuresAsync(PageNumber, cancellationToken);
                break;
        }
    }

    /// <summary>目前這一頁的分頁資訊（頁碼、總頁數）。</summary>
    public (int Page, int TotalPages, int TotalCount) Paging =>
        Failures is { } f ? (f.Page, f.TotalPages, f.TotalCount)
        : Events is { } e ? (e.Page, e.TotalPages, e.TotalCount)
        : StaleDocuments is { } d ? (d.Page, d.TotalPages, d.TotalCount)
        : StaleReservations is { } r ? (r.Page, r.TotalPages, r.TotalCount)
        : (1, 1, 0);

    public string PageUrl(int page) => Url.Page("/Stock/Anomalies", new { tab = Tab, p = page })!;

    public int DaysSince(DateTimeOffset since) => Math.Max(0, (int)(clock.GetUtcNow() - since).TotalDays);
}
