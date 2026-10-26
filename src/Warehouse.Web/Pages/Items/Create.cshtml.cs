using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Items;

/// <summary>
/// 提出新物料（作業員、物料管理員）。流程：
/// 1. 開表單時產生 RequestId（隱藏欄位），送出時當作物料 ID，連點或重送都只會建立一筆（冪等）。
/// 2. 第一次送出先查重複候選；有候選就列出來，勾選「仍要提出」再送一次才建立。
/// 3. 可一起登錄原廠條碼；物料管理員可勾「送出後自動核准」，與提出在同一個交易（M8-05，ADR-0023），
///    勾選狀態記在帳號的常用設定（M8-06）。
/// </summary>
public class CreateModel(
    IItemWorkflowService workflow, IMasterDataService masterData, Warehouse.Application.Accounts.IUserPreferenceService preferences) : PageModel
{

    [BindProperty]
    public ItemFormInput Input { get; set; } = new();

    [BindProperty]
    public Guid RequestId { get; set; }

    [BindProperty]
    public bool ConfirmSubmit { get; set; }

    [BindProperty]
    public bool AutoApprove { get; set; }

    /// <summary>上次列出的重複候選（勾「仍要提出」即代表全部確認過；自動核准時帶給核准檢查）。</summary>
    [BindProperty]
    public List<Guid> AcknowledgedIds { get; set; } = [];

    public ItemFormOptions Options { get; private set; } = null!;

    public IReadOnlyList<DuplicateCandidate> Candidates { get; private set; } = [];

    public bool CanAutoApprove => User.IsInRole(Roles.ItemManager);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        RequestId = Guid.NewGuid();
        // 記住物料管理員上次「自動核准」的選擇（跟著帳號，M8-06；只是畫面預設，權限由服務檢查）
        AutoApprove = CanAutoApprove && (await preferences.GetAllAsync(User.ToActor().Id, cancellationToken))
            .GetValueOrDefault(Warehouse.Application.Accounts.UserPreferenceKeys.ItemAutoApprove) == "1";
        Options = await ItemFormOptions.LoadAsync(masterData, null, false, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Options = await ItemFormOptions.LoadAsync(masterData, Input, false, cancellationToken);
        AutoApprove &= CanAutoApprove;
        if (CanAutoApprove)
        {
            await preferences.TrySetAsync(User.ToActor().Id, Warehouse.Application.Accounts.UserPreferenceKeys.ItemAutoApprove,
                AutoApprove ? "1" : "0", cancellationToken);
        }
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
            var options = new ItemSubmitOptions(Input.ToBarcodeDrafts(), AutoApprove, ConfirmSubmit ? AcknowledgedIds : []);
            var result = await workflow.SubmitAsync(RequestId, Input.ToDraft(), options, User.ToActor(), cancellationToken);
            TempData["Message"] = result.ItemCode is { } code
                ? $"已提出並核准，正式料號 {code}（申請單號 {result.RequestNo}）。"
                : $"已提出，申請單號 {result.RequestNo}，等待物料管理員核准。";
            return RedirectToPage("/Items/Details", new { id = result.ItemId });
        }
        catch (DuplicateCandidatesUnconfirmedException ex)
        {
            // 自動核准時又出現新的疑似重複（剛被別人提出）：整筆沒有建立，列出來再確認一次
            Candidates = ex.Candidates;
            ConfirmSubmit = false;
            ModelState.AddModelError(string.Empty, "又找到新的疑似重複物料，物料沒有建立；請確認後再送出。");
            return Page();
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
