using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Items;

/// <summary>
/// 提出新物料（作業員、物料管理員）。流程：
/// 1. 開表單時產生 RequestId（隱藏欄位），送出時當作物料 ID，連點或重送都只會建立一筆（冪等）。
/// 2. 第一次送出先查重複候選；有候選就列出來，勾選「仍要提出」再送一次才建立。
/// </summary>
public class CreateModel(IItemWorkflowService workflow, IMasterDataService masterData) : PageModel
{
    [BindProperty]
    public ItemFormInput Input { get; set; } = new();

    [BindProperty]
    public Guid RequestId { get; set; }

    [BindProperty]
    public bool ConfirmSubmit { get; set; }

    public ItemFormOptions Options { get; private set; } = null!;

    public IReadOnlyList<DuplicateCandidate> Candidates { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        RequestId = Guid.NewGuid();
        Options = await ItemFormOptions.LoadAsync(masterData, null, false, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Options = await ItemFormOptions.LoadAsync(masterData, Input, false, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!ConfirmSubmit)
        {
            Candidates = await workflow.FindDuplicateCandidatesAsync(Input.Name, Input.ManufacturerPartNo, null, cancellationToken);
            if (Candidates.Count > 0)
            {
                return Page();
            }
        }

        try
        {
            var result = await workflow.SubmitAsync(RequestId, Input.ToDraft(), User.ToActor(), cancellationToken);
            TempData["Message"] = $"已提出，申請單號 {result.RequestNo}，等待物料管理員核准。";
            return RedirectToPage("/Items/Details", new { id = result.ItemId });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
