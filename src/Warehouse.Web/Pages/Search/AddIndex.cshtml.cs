using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Warehouse.Api.Endpoints;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Search;

namespace Warehouse.Web.Pages.Search;

/// <summary>
/// 搜不到時「把搜尋文字加到既有物料」（M2-04，docs/03 §3、情境 F）：
/// 1. 帶著原本的搜尋文字 <see cref="Text"/> 進來；2. 用另一個關鍵字 <see cref="Target"/> 找出要加的物料；
/// 3. 點選後到物料明細頁（文字已預填），看過完整規格再按「新增」。
/// 這個流程只能把索引加到既有的有效物料，不能建立新物料（情境 H）。
/// </summary>
[EnableRateLimiting(SearchEndpoints.RateLimitPolicy)]
public class AddIndexModel(IItemSearchService search) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Target { get; set; }

    public PagedResult<ItemSearchHit>? Result { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Target))
        {
            Result = await search.SearchAsync(new ItemSearchRequest(Target), cancellationToken);
        }
    }

    public string DetailsUrl(Guid itemId) =>
        $"/Items/Details/{itemId}?indexText={Uri.EscapeDataString(Text ?? string.Empty)}#index";
}
