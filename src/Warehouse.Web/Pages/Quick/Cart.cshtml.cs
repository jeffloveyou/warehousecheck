using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Application.MasterData;
using Warehouse.Application.Settings;
using Warehouse.Web.Pages.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Domain;
using Warehouse.Web.Security;
using Warehouse.Application.Accounts;

namespace Warehouse.Web.Pages.Quick;

/// <summary>
/// 購物車的掃碼階段（ADR-0026）：條碼槍、PDA、相機連掃加入（POST JSON，回傳重繪的明細表，頁面不重新載入）；
/// 每行可改數量、儲位或刪除；掃完按「進倉／出倉／移庫」到 Quick/Convert 補齊。只能看、改自己的購物車。
/// </summary>
[Authorize(Policy = Policies.DocumentEdit)]
public partial class CartModel(IScanCartService carts, IStockDocumentService documents, IWarehouseService warehouses, IDocumentMasterDataService master, ISettingsService settings, IUserPreferenceService preferences) : PageModel
{
    public ScanCartDetails Cart { get; private set; } = null!;

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, cancellationToken)) return Page();
        if (await carts.CheckoutResultAsync(id, User.ToActor(), cancellationToken) is { } result)
        {
            TempData["Message"] = $"這台購物車已結帳，單號 {result.DocNo}，目前狀態：{Display.Of(result.Status)}。";
            return RedirectToPage("/Documents/Details", new { id = result.DocumentId });
        }
        return Missing();
    }

    /// <summary>掃碼加入（JSON，antiforgery 由 RequestVerificationToken 標頭驗證）：成功回傳重繪的明細表，規則錯誤回 400 JSON。</summary>
    public Task<IActionResult> OnPostAddAsync(Guid id, [FromBody] AddRequest? request, CancellationToken cancellationToken) =>
        AddCoreAsync(id, request, Request.Query.ContainsKey("delta"), cancellationToken);

    // 新客戶端使用獨立 handler；回退舊版時不會誤走忽略 OperationId 的舊加入方法。
    public Task<IActionResult> OnPostAddDeltaAsync(Guid id, [FromBody] AddRequest? request, CancellationToken cancellationToken) =>
        AddCoreAsync(id, request, true, cancellationToken);

    private async Task<IActionResult> AddCoreAsync(Guid id, AddRequest? request, bool delta, CancellationToken cancellationToken)
    {
        if (request is null || request.ItemCode is not { Length: > 0 and <= 200 } || request.LocationCode is { Length: > 200 })
        {
            return BadRequest(new { message = "請掃物料或輸入料號。" });
        }
        try
        {
            if (!ModelState.IsValid) return BadRequest(new { message = "輸入格式不正確。" });
            if (delta && (request.OperationId is null || request.OperationId == Guid.Empty))
                return BadRequest(new { message = "缺少加入操作識別碼。" });
            var result = await carts.AddWithResultAsync(id, new ScanCartAddInput(request.ItemCode ?? string.Empty, request.LocationCode, request.Quantity, request.OperationId, request.ItemId), User.ToActor(), cancellationToken);
            if (delta)
            {
                var c = result.Cart;
                if (c is null) return NotFound(new { message = "購物車已轉成單據或放棄，請回清單查看。" });
                return new JsonResult(new { result.BaseVersion, c.Version, Line = c.Lines.SingleOrDefault(l => l.Id == result.LineId), Count = result.LineCount, c.WarehouseCode, c.WarehouseId });
            }
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            return BadRequest(new { message = ex.Message });
        }
        return await LoadAsync(id, cancellationToken) ? Partial("_CartLines", Cart) : NotFound();
    }

    /// <summary>重新讀取明細表（加入結果不確定時，畫面用它核對實際內容）。</summary>
    public async Task<IActionResult> OnGetLinesAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Partial("_CartLines", Cart) : NotFound();

    public Task<IActionResult> OnPostUpdateLineAsync(Guid id, Guid lineId, decimal quantity, string? locationCode, uint version, CancellationToken cancellationToken) =>
        RunAsync(id, () => carts.UpdateLineAsync(id, lineId, quantity, locationCode, User.ToActor(), cancellationToken, version), "已更新。", cancellationToken);

    public Task<IActionResult> OnPostRemoveLineAsync(Guid id, Guid lineId, uint version, CancellationToken cancellationToken) =>
        RunAsync(id, () => carts.RemoveLineAsync(id, lineId, User.ToActor(), cancellationToken, version), "已刪除一行。", cancellationToken);

    public Task<IActionResult> OnPostNoteAsync(Guid id, string? note, CancellationToken cancellationToken) =>
        RunAsync(id, () => carts.SetNoteAsync(id, note, User.ToActor(), cancellationToken), "已更新備註。", cancellationToken);

    /// <summary>放棄購物車：直接刪除，不留紀錄（使用者決定）。</summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await carts.DeleteAsync(id, User.ToActor(), cancellationToken);
        }
        catch (NotFoundException)
        {
            // 已在其他裝置放棄或轉單：結果相同
        }
        TempData["Message"] = "已放棄購物車。";
        return RedirectToPage("/Quick/Index");
    }

    public sealed class AddRequest
    {
        public string? ItemCode { get; set; }

        public string? LocationCode { get; set; }

        public decimal Quantity { get; set; }
        public Guid? OperationId { get; set; }
        public Guid? ItemId { get; set; }
    }

    private async Task<IActionResult> RunAsync(Guid id, Func<Task> action, string message, CancellationToken cancellationToken)
    {
        try
        {
            await action();
            TempData["Message"] = message;
            return RedirectToPage(new { id });
        }
        catch (NotFoundException)
        {
            return Missing();
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        return await LoadAsync(id, cancellationToken) ? Page() : Missing();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await carts.GetAsync(id, User.ToActor(), cancellationToken) is not { } cart)
        {
            return false;
        }
        Cart = cart;
        Preferences = await preferences.GetAllAsync(User.ToActor().Id, cancellationToken);
        Options = await DocumentFormSupport.LoadOptionsAsync(master, settings, null, null, null, cancellationToken);
        var allowed = await HttpContext.OperableWarehousesAsync(cancellationToken);
        WarehouseOptions = (await warehouses.ListWarehousesAsync(cancellationToken)).Where(w => w.IsActive && !w.IsDeleted && allowed.Allows(w.Id)).ToList();
        return true;
    }

    private IActionResult Missing()
    {
        TempData["Message"] = "找不到購物車（可能已在其他裝置轉成單據或放棄）。";
        return RedirectToPage("/Quick/Index");
    }
}
