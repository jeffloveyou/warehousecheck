using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Web.Security;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Items;

namespace Warehouse.Web.Pages.Items;

/// <summary>
/// 物料明細、審核紀錄與審核動作。按鈕依角色與狀態顯示，但權限一律由 <see cref="IItemWorkflowService"/> 再檢查
/// （只隱藏按鈕擋不住直接送出的請求）。每個動作都帶回讀取時的版本（xmin），別人先改過會顯示衝突。
/// </summary>
public class DetailsModel(
    IItemQueryService queries, IItemWorkflowService workflow, IItemIndexService index, IAuthorizationService authorization,
    IItemLocationService itemLocations, IWarehouseService warehouses, IItemNegativePolicyService negativePolicy)
    : PageModel
{
    /// <summary>負庫存設定表單（M4-08）。</summary>
    [BindProperty]
    public bool AllowNegative { get; set; }

    [BindProperty]
    public decimal NegativeLimit { get; set; }

    /// <summary>可編輯負庫存設定：物料管理員、有效物料、不追蹤批號／序號。</summary>
    public bool CanEditNegativePolicy { get; private set; }

    public Task<IActionResult> OnPostNegativePolicyAsync(Guid id, CancellationToken cancellationToken) =>
        RunNegativeAsync(id, cancellationToken);

    private async Task<IActionResult> RunNegativeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.NegativePolicy)).Succeeded)
        {
            return Forbid();
        }
        return await RunAsync(id, async actor =>
        {
            await negativePolicy.SetAsync(id, Version, AllowNegative, AllowNegative ? NegativeLimit : 0, actor, cancellationToken);
            return AllowNegative ? "已更新負庫存設定。" : "已關閉負庫存。";
        }, cancellationToken);
    }

    /// <summary>預設／常用倉位（M3-07）。</summary>
    public IReadOnlyList<ItemLocationRow> ItemLocations { get; private set; } = [];

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public bool CanManageLocations { get; private set; }

    public async Task<IActionResult> OnPostAddItemLocationAsync(
        Guid id, Guid warehouseId, string locationCode, bool isDefault, CancellationToken cancellationToken) =>
        !await CanManageLocationsAsync() ? Forbid() : await RunAsync(id, async actor =>
        {
            await itemLocations.AddAsync(id, warehouseId, locationCode ?? string.Empty, isDefault, actor, cancellationToken);
            return "已新增常用倉位。";
        }, cancellationToken);

    public async Task<IActionResult> OnPostDefaultItemLocationAsync(Guid id, Guid itemLocationId, CancellationToken cancellationToken) =>
        !await CanManageLocationsAsync() ? Forbid() : await RunAsync(id, async actor =>
        {
            await itemLocations.SetDefaultAsync(id, itemLocationId, actor, cancellationToken);
            return "已設為預設倉位。";
        }, cancellationToken);

    public async Task<IActionResult> OnPostRemoveItemLocationAsync(Guid id, Guid itemLocationId, CancellationToken cancellationToken) =>
        !await CanManageLocationsAsync() ? Forbid() : await RunAsync(id, async actor =>
        {
            await itemLocations.RemoveAsync(id, itemLocationId, actor, cancellationToken);
            return "已移除常用倉位。";
        }, cancellationToken);

    private async Task<bool> CanManageLocationsAsync() =>
        (await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded;

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

    public IReadOnlyList<ItemIndexRow> Indexes { get; private set; } = [];

    /// <summary>新增索引的文字；從搜尋頁「把搜尋文字加到物料」過來時以 ?indexText= 預先填入（M2-04）。</summary>
    [BindProperty(SupportsGet = true)]
    public string? IndexText { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemIndexKind IndexKind { get; set; } = ItemIndexKind.Keyword;

    /// <summary>原廠條碼的包裝數量（基本單位，空白＝1）；從掃碼查料頁「登錄為原廠條碼」過來時以 ?indexKind=Barcode 預先選好類型。</summary>
    [BindProperty]
    public decimal? IndexPackQuantity { get; set; }

    /// <summary>別名被其他物料占用時，畫面提供「改加為共用關鍵字」。</summary>
    public string? SuggestKeyword { get; private set; }

    public bool IsManager => User.IsInRole(Roles.ItemManager);

    public bool CanAddIndex => Item.Status == ItemStatus.Active && (IsManager || User.IsInRole(Roles.Operator));

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

    public async Task<IActionResult> OnPostAddIndexAsync(Guid id, CancellationToken cancellationToken) =>
        !await CanMaintainIndexAsync() ? Forbid() : await RunAsync(id, async actor =>
        {
            try
            {
                await index.AddAsync(id, IndexKind, IndexText ?? string.Empty, actor, cancellationToken, IndexPackQuantity ?? 1m);
            }
            catch (AliasTakenException)
            {
                SuggestKeyword = IndexText;
                throw;
            }
            return $"已新增{Display.Of(IndexKind)}「{IndexText?.Trim()}」。";
        }, cancellationToken);

    public async Task<IActionResult> OnPostDeactivateIndexAsync(Guid id, Guid entryId, ItemIndexKind kind, CancellationToken cancellationToken) =>
        !await CanMaintainIndexAsync() ? Forbid() : await RunAsync(id, async actor =>
        {
            await index.DeactivateAsync(id, kind, entryId, actor, cancellationToken);
            return "已停用這筆搜尋索引。";
        }, cancellationToken);

    /// <summary>
    /// 頁面資料夾只要求登入（Items.Read）；新增、停用索引在伺服器端另外檢查 Items.Index 政策，
    /// 與 API 相同（M2 審查 R1：只隱藏表單擋不住直接送出的請求）。
    /// </summary>
    private async Task<bool> CanMaintainIndexAsync() =>
        (await authorization.AuthorizeAsync(User, Policies.ItemsIndex)).Succeeded;

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
        Indexes = await index.ListAsync(id, cancellationToken);
        ItemLocations = await itemLocations.ListAsync(id, cancellationToken);
        CanManageLocations = item.Status == ItemStatus.Active && await CanManageLocationsAsync();
        CanEditNegativePolicy = item.Status == ItemStatus.Active && item.TrackingMode == TrackingMode.None
            && (await authorization.AuthorizeAsync(User, Policies.NegativePolicy)).Succeeded;
        if (HttpMethods.IsGet(Request.Method))
        {
            // 只在 GET 時從資料庫帶入；送出失敗重新顯示時保留使用者剛輸入的值（M4-03 審查 #14）
            AllowNegative = item.AllowNegativeStock;
            NegativeLimit = item.NegativeStockLimit;
        }
        if (CanManageLocations)
        {
            var operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
            Warehouses = (await warehouses.ListWarehousesAsync(cancellationToken)).Where(w => w.IsActive && operable.Allows(w.Id)).ToList();
        }
        // 待核准時列出重複候選，供管理員核准前確認、或拒絕時選擇「重複於」
        if (item.Status == ItemStatus.PendingApproval && IsManager)
        {
            Candidates = await workflow.FindDuplicateCandidatesAsync(item.Name, item.ManufacturerPartNo, item.Id, cancellationToken);
        }
        return true;
    }
}
