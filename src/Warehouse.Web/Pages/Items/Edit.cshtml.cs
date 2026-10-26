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
/// M8-05（ADR-0023）：核准後分類鎖住；基本單位、追蹤模式依異動紀錄與系統設定開放或鎖住；
/// 可新增與停用原廠條碼，與修改在同一個交易。
/// </summary>
public class EditModel(
    IItemQueryService queries, IItemWorkflowService workflow, IItemIndexService index, IMasterDataService masterData) : PageModel
{
    [BindProperty]
    public ItemFormInput Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public List<Guid> AcknowledgedIds { get; set; } = [];

    /// <summary>勾選要停用的既有原廠條碼。</summary>
    [BindProperty]
    public List<Guid> DeactivateBarcodeIds { get; set; } = [];

    [BindProperty]
    public bool ConfirmUnitTrackingChange { get; set; }

    [BindProperty]
    public string? UnitTrackingChangeReason { get; set; }

    public ItemDetails Item { get; private set; } = null!;

    public ItemFormOptions Options { get; private set; } = null!;

    public IReadOnlyList<DuplicateCandidate> Candidates { get; private set; } = [];

    /// <summary>目前有效的原廠條碼（可勾選停用）。</summary>
    public IReadOnlyList<ItemIndexRow> ActiveBarcodes { get; private set; } = [];

    /// <summary>核准後且已有異動紀錄、單位／追蹤模式可改：改的時候要勾選確認並填寫原因。</summary>
    public bool UnitTrackingNeedsConfirm { get; private set; }

    public bool IsApproved => Item.Status is ItemStatus.Active or ItemStatus.Inactive;

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
        await LoadAsync(cancellationToken);
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
        await LoadAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var options = new ItemUpdateOptions(
                Input.ToBarcodeDrafts(), DeactivateBarcodeIds, ConfirmUnitTrackingChange, UnitTrackingChangeReason);
            var version = Version;
            if (!resubmit || options.AddBarcodes.Count > 0 || options.DeactivateBarcodeIds.Count > 0)
            {
                await workflow.UpdateAsync(id, Version, Input.ToDraft(), AcknowledgedIds, options, User.ToActor(), cancellationToken);
                // 重新送審是另一個交易：用剛儲存後的版本（條碼已登錄；送審失敗時修改仍保留，訊息會說明）
                version = (await queries.GetAsync(id, cancellationToken))!.Version;
            }
            if (resubmit)
            {
                await workflow.ResubmitAsync(id, version, Input.ToDraft(), User.ToActor(), cancellationToken);
                TempData["Message"] = "已修改並重新送審。";
            }
            else
            {
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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        string? lockReason = null;
        if (IsApproved)
        {
            // 以目前的單位與追蹤模式檢查：與目標無關的阻擋（設定未開放、未完成單據、預留、盤點）先在畫面鎖住；
            // 與目標有關的（結存不為 0、條碼包裝數量）送出時由服務在交易內檢查並顯示原因
            var check = await workflow.CheckUnitTrackingChangeAsync(Item.Id, Item.BaseUnitId, Item.TrackingMode, cancellationToken);
            lockReason = check.CanChange ? null : string.Join("；", check.Blockers);
            UnitTrackingNeedsConfirm = check.CanChange && check.HasActivity;
        }
        Options = await ItemFormOptions.LoadAsync(masterData, Input, lockCategory: IsApproved, cancellationToken, lockReason);
        ActiveBarcodes = (await index.ListAsync(Item.Id, cancellationToken))
            .Where(r => r.Kind == ItemIndexKind.Barcode && r.IsActive)
            .ToList();
    }
}
