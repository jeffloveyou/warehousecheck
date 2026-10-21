using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Poc.Shared;

namespace Poc.RazorPages.Pages;

public class SearchModel : PageModel
{
    public string Q { get; private set; } = string.Empty;
    public string? Hint { get; private set; }
    public PocSearchPage? Result { get; private set; }

    // 注意：Razor Pages 路由值已有名為 "page" 的項目（頁面路徑），
    // 所以明確用 FromQuery 指定從查詢字串取值，避免綁到路由值。
    public void OnGet([FromQuery(Name = "q")] string? q, [FromQuery(Name = "page")] int pageNumber = 1)
    {
        Q = (q ?? string.Empty).Trim();
        if (Q.Length == 0)
        {
            Hint = "請輸入料號或品名關鍵字";
            return;
        }

        if (Q.Length > FakeItemCatalog.MaxQueryLength)
        {
            Hint = $"查詢字串不可超過 {FakeItemCatalog.MaxQueryLength} 字";
            return;
        }

        Result = FakeItemCatalog.Search(Q, pageNumber);
    }
}
