using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Domain;
using Warehouse.Domain.Items;

namespace Warehouse.Web.Pages.Items;

/// <summary>
/// 物料明細、審核紀錄與審核動作。按鈕依角色與狀態顯示，但權限一律由 <see cref="IItemWorkflowService"/> 再檢查
/// （只隱藏按鈕擋不住直接送出的請求）。每個動作都帶回讀取時的版本（xmin），別人先改過會顯示衝突。
/// </summary>
public class DetailsModel(IItemQueryService queries, IItemWorkflowService workflow) : PageModel
{
    public ItemDetails Item { get; private set; } = null!;

    public IReadOnlyList<DuplicateCandidate> Candidates { get; private set; } = [];

    public string? Error { get; private set; }

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public string? Comment { get; set; }

    [BindProperty]
    public List<Guid> AcknowledgedIds { get; set; } = [];

    [BindProperty]
    public Guid? DuplicateOfItemId { get; set; }

    public bool IsManager => User.IsInRole(Roles.ItemManager);

    public bool IsSubmitter => Item.CreatedById is { } id && id == User.ToActor().Id;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : NotFound();

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async actor =>
        {
            var code = await workflow.ApproveAsync(id, Version, AcknowledgedIds, Comment, actor, cancellationToken);
            return $"已核准，正式料號 {code}。";
        }, cancellationToken);

    public Task<IActionResult> OnPostReturnAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async actor =>
        {
            await workflow.ReturnAsync(id, Version, Comment ?? string.Empty, actor, cancellationToken);
            return "已退回給提出人。";
        }, cancellationToken);

    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async actor =>
        {
            await workflow.RejectAsync(id, Version, Comment ?? string.Empty, DuplicateOfItemId, actor, cancellationToken);
            return "已拒絕。";
        }, cancellationToken);

    public Task<IActionResult> OnPostWithdrawAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async actor =>
        {
            await workflow.WithdrawAsync(id, Version, Comment, actor, cancellationToken);
            return "已撤回。";
        }, cancellationToken);

    public Task<IActionResult> OnPostDeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async actor =>
        {
            await workflow.DeactivateAsync(id, Version, Comment, actor, cancellationToken);
            return "已停用。";
        }, cancellationToken);

    public Task<IActionResult> OnPostReactivateAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async actor =>
        {
            await workflow.ReactivateAsync(id, Version, Comment, actor, cancellationToken);
            return "已重新啟用。";
        }, cancellationToken);

    /// <summary>執行動作：成功時轉址（PRG，重新整理不會重送）；已知錯誤時重新載入並顯示訊息。</summary>
    private async Task<IActionResult> RunAsync(Guid id, Func<Actor, Task<string>> action, CancellationToken cancellationToken)
    {
        try
        {
            TempData["Message"] = await action(User.ToActor());
            return RedirectToPage(new { id });
        }
        catch (DuplicateCandidatesUnconfirmedException ex)
        {
            Error = ex.Message + " 請在下方逐筆確認後再核准。";
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        return await LoadAsync(id, cancellationToken) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await queries.GetAsync(id, cancellationToken) is not { } item)
        {
            return false;
        }
        Item = item;
        Version = item.Version;
        // 待核准時列出重複候選，供管理員核准前確認、或拒絕時選擇「重複於」
        if (item.Status == ItemStatus.PendingApproval && IsManager)
        {
            Candidates = await workflow.FindDuplicateCandidatesAsync(item.Name, item.ManufacturerPartNo, item.Id, cancellationToken);
        }
        return true;
    }
}
