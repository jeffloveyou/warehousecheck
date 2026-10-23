using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Inventory;
using Warehouse.Application.Settings;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.Stocktakes;

/// <summary>
/// 盤點表列印（M6-02）：依儲位排序，留空白欄位給人手寫實盤量。盲盤時（服務決定）不印帳面量。
/// 一次讀全部行（一張盤點單最多 2000 行），不逐頁往返（M6-02 審查 Y7）。
/// </summary>
public class PrintModel(IStocktakeService stocktakes, ISettingsService settings) : PageModel
{
    public StocktakeDetails Doc { get; private set; } = null!;

    public IReadOnlyList<StocktakeLineRow> Lines => Doc.Lines.Items;

    public string CompanyName { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await stocktakes.GetAsync(id, new StocktakeLineFilter(PageSize: Stocktake.MaxLines), User.ToActor(), cancellationToken) is not { } doc)
        {
            return NotFound();
        }
        Doc = doc;
        CompanyName = (await settings.GetAsync(cancellationToken)).PrintCompanyName.Trim();
        return Page();
    }
}
