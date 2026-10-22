using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Domain.Items;

namespace Warehouse.Web.Pages.Items;

/// <summary>物料清單：關鍵字、狀態、分類篩選；伺服器端分頁（每頁 20 筆，上限 100）。</summary>
public class IndexModel(IItemQueryService queries, IMasterDataService masterData) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Keyword { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? CategoryId { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = Paging.DefaultPageSize;

    public PagedResult<ItemListRow> Result { get; private set; } = null!;

    public IReadOnlyList<CategoryRow> Categories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Result = await queries.SearchAsync(
            new ItemSearchQuery(Keyword, Status, CategoryId, PageNumber, PageSize), cancellationToken);
        Categories = await masterData.ListCategoriesAsync(cancellationToken);
    }

    /// <summary>保留目前篩選條件、只換頁碼的網址。</summary>
    public string PageUrl(int page) =>
        Url.Page("/Items/Index", new { q = Keyword, Status, CategoryId, p = page, PageSize = Result.PageSize })!;
}
