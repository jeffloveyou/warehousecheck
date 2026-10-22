using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Inventory;
using Warehouse.Application.Settings;

namespace Warehouse.Web.Pages.Documents;

/// <summary>
/// 進出倉單列印頁（任何登入者可看）：不使用一般版面，A4 直式 print CSS，不依賴 JS（列印按鈕除外）。
/// 未過帳的單據加浮水字樣，避免把草稿當正式單據使用。
/// </summary>
public class PrintModel(IStockDocumentService documents, ISettingsService settings) : PageModel
{
    public DocumentDetails Doc { get; private set; } = null!;

    /// <summary>抬頭公司名稱；空白＝不印。</summary>
    public string CompanyName { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await documents.GetAsync(id, cancellationToken) is not { } doc)
        {
            return NotFound();
        }
        Doc = doc;
        CompanyName = (await settings.GetAsync(cancellationToken)).PrintCompanyName.Trim();
        return Page();
    }
}
