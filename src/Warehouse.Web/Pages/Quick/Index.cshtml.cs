using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Quick;

/// <summary>
/// 快速進出（ADR-0026）：自己的購物車清單與「新購物車」。先掃一批料，再整批選進倉／出倉／移庫（Quick/Cart、Quick/Convert）。
/// 作業員、倉庫主管（DocumentEdit）；只看得到自己的購物車。
/// </summary>
[Authorize(Policy = Policies.DocumentEdit)]
public class IndexModel(IScanCartService carts) : PageModel
{
    [BindProperty]
    public string? Note { get; set; }

    public IReadOnlyList<ScanCartSummary> Carts { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Carts = await carts.ListAsync(User.ToActor(), cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var id = await carts.CreateAsync(Note, User.ToActor(), cancellationToken);
            return RedirectToPage("/Quick/Cart", new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        Carts = await carts.ListAsync(User.ToActor(), cancellationToken);
        return Page();
    }

    /// <summary>購物車的顯示名稱：建立時間（台灣時間）＋備註。</summary>
    public static string Name(DateTimeOffset createdAt, string? note) =>
        Display.Time(createdAt) + (note is null ? "" : "（" + note + "）");
}
