using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Warehouse.Api.Endpoints;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Search;
using Warehouse.Api.Authorization;
using Warehouse.Domain.Text;

namespace Warehouse.Web.Pages.Search;

/// <summary>
/// 物料搜尋（作業員的主要入口，M2-03）。按「搜尋」才查詢（不做邊打字邊查，舊電腦較省資源）。
/// 結果只是候選：每筆顯示命中原因、料號與規格，由人點選確認（docs/03 §5）。
/// </summary>
[EnableRateLimiting(SearchEndpoints.RateLimitPolicy)]
public class IndexModel(IItemSearchService search, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeInactive { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    public PagedResult<ItemSearchHit>? Result { get; private set; }

    /// <summary>是否顯示「加為搜尋索引」連結：與加索引頁相同的 Items.Index 政策（單一來源）。</summary>
    public bool CanAddIndex { get; private set; }

    /// <summary>沒有用於部分比對的詞（太短、或超過 5 個），畫面提示使用者。</summary>
    public IReadOnlyList<string> IgnoredTerms { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        CanAddIndex = (await authorization.AuthorizeAsync(User, Policies.ItemsIndex)).Succeeded;
        if (!string.IsNullOrWhiteSpace(Q))
        {
            IgnoredTerms = ItemSearch.SplitTerms(TextNormalizer.Normalize(Q)).Ignored;
            Result = await search.SearchAsync(new ItemSearchRequest(Q, IncludeInactive, PageNumber), cancellationToken);
        }
    }

    public string PageUrl(int page) => Url.Page("/Search/Index", new { Q, IncludeInactive, p = page })!;
}
