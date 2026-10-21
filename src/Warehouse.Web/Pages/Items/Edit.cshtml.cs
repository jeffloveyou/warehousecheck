using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Domain;
using Warehouse.Domain.Items;

namespace Warehouse.Web.Pages.Items;

/// <summary>
/// 修改物料。表單把讀取時的版本（xmin）放在隱藏欄位，送出時帶回服務比對（M1-01 審查 #8）：
/// 開表單之後別人先改過，會顯示「資料已被他人修改」，不會默默蓋掉別人的修改。
/// 退回的物料多一個「修改並重新送審」按鈕。
/// </summary>
public class EditModel(IItemQueryService queries, IItemWorkflowService workflow, IMasterDataService masterData) : PageModel
{
    [BindProperty]
    public ItemFormInput Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public List<Guid> AcknowledgedIds { get; set; } = [];

    public ItemDetails Item { get; private set; } = null!;

    public ItemFormOptions Options { get; private set; } = null!;

    public IReadOnlyList<DuplicateCandidate> Candidates { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await queries.GetAsync(id, cancellationToken) is not { } item)
        {
            return NotFound();
        }
        Item = item;
        // 開表單前先判斷能不能改（送出時服務還會再檢查一次）：核准前限提出人或管理員，核准後限管理員，已拒絕不能改
        var actor = User.ToActor();
        var canEdit = item.Status != ItemStatus.Rejected
            && (actor.IsItemManager
                || (item.CreatedById == actor.Id && item.Status is ItemStatus.PendingApproval or ItemStatus.Returned));
        if (!canEdit)
        {
            return Forbid();
        }
        Input = ItemFormInput.From(item);
        Version = item.Version;
        Options = await LoadOptionsAsync(cancellationToken);
        return Page();
    }

    public Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken) =>
        SaveAsync(id, resubmit: false, cancellationToken);

    public Task<IActionResult> OnPostResubmitAsync(Guid id, CancellationToken cancellationToken) =>
        SaveAsync(id, resubmit: true, cancellationToken);

    private async Task<IActionResult> SaveAsync(Guid id, bool resubmit, CancellationToken cancellationToken)
    {
        if (await queries.GetAsync(id, cancellationToken) is not { } item)
        {
            return NotFound();
        }
        Item = item;
        Options = await LoadOptionsAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            if (resubmit)
            {
                await workflow.ResubmitAsync(id, Version, Input.ToDraft(), User.ToActor(), cancellationToken);
                TempData["Message"] = "已修改並重新送審。";
            }
            else
            {
                await workflow.UpdateAsync(id, Version, Input.ToDraft(), AcknowledgedIds, User.ToActor(), cancellationToken);
                TempData["Message"] = "已儲存。";
            }
            return RedirectToPage("/Items/Details", new { id });
        }
        catch (DuplicateCandidatesUnconfirmedException ex)
        {
            Candidates = ex.Candidates;
            ModelState.AddModelError(string.Empty, "修改後的品名或型號與其他物料相同，請逐筆確認不是同一物料後再儲存。");
        }
        catch (ConcurrencyConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message + " 請重新開啟此頁（目前輸入的內容不會保留）。");
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        return Page();
    }

    private Task<ItemFormOptions> LoadOptionsAsync(CancellationToken cancellationToken) =>
        ItemFormOptions.LoadAsync(
            masterData, Input, lockClassification: Item.Status is ItemStatus.Active or ItemStatus.Inactive, cancellationToken);
}
