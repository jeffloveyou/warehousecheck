using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Admin.Audit;

/// <summary>
/// 操作日誌（M6-01c，限系統管理員；資料夾授權 Policies.AuditRead）：期間必填（預設最近 7 天、最長 366 天），
/// 操作者、動作開頭、對象類型與 ID 篩選；新到舊、伺服器分頁。日期以台灣當地日期輸入。
/// </summary>
public class IndexModel(IAuditQueryService audit, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ActorName { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ActionPrefix { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? EntityType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? EntityId { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public PagedResult<AuditRow> Result { get; private set; } = new([], 1, Paging.DefaultPageSize, 0);

    public string? Notice { get; private set; }

    /// <summary>常用的動作開頭（下拉選單）。</summary>
    public static readonly IReadOnlyList<(string Prefix, string Label)> ActionGroups =
    [
        ("auth.", "登入"), ("user.", "帳號"), ("setting.", "系統設定"), ("document.", "進出倉單"), ("opening.", "期初"),
        ("reservation.", "預留"), ("stock.", "庫存"), ("warehouse.", "倉庫"), ("location.", "儲位"), ("item_location.", "常用倉位"),
    ];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
        From ??= today.AddDays(-(AuditQuery.DefaultDays - 1));
        To ??= today;
        // 手改網址的極端日期（0001-01-01、9999-12-31）轉 UTC 會溢位：先擋下（M6-01c 審查 2）
        var earliest = new DateOnly(2000, 1, 1);
        if (From < earliest || To < earliest || From > today.AddDays(1) || To > today.AddDays(1))
        {
            Notice = "日期超出可查詢範圍（2000-01-01 至今天）。";
            return;
        }
        try
        {
            Result = await audit.SearchAsync(new AuditQuery(
                TaipeiTime.StartOfDay(From.Value), TaipeiTime.StartOfDay(To.Value.AddDays(1)),
                ActorName, ActionPrefix, EntityType, EntityId, PageNumber), cancellationToken);
        }
        catch (DomainRuleException ex)
        {
            Notice = ex.Message;
        }
    }

    public string PageUrl(int page) => Url.Page("/Admin/Audit/Index",
        new { From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd"), ActorName, ActionPrefix, EntityType, EntityId, p = page })!;
}
