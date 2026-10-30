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
/// 購物車的掃碼階段（ADR-0026）：條碼槍、PDA、相機連掃加入（POST JSON，回傳重繪的明細表，頁面不重新載入）；
/// 每行可改數量、儲位或刪除；掃完按「進倉／出倉／移庫」到 Quick/Convert 補齊。只能看、改自己的購物車。
/// </summary>
[Authorize(Policy = Policies.DocumentEdit)]
public class CartModel(IScanCartService carts) : PageModel
{
    public ScanCartDetails Cart { get; private set; } = null!;

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : Missing();

    /// <summary>掃碼加入（JSON，antiforgery 由 RequestVerificationToken 標頭驗證）：成功回傳重繪的明細表，規則錯誤回 400 JSON。</summary>
    public async Task<IActionResult> OnPostAddAsync(Guid id, [FromBody] AddRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || request.ItemCode is not { Length: > 0 and <= 200 } || request.LocationCode is { Length: > 200 })
        {
            return BadRequest(new { message = "請掃物料或輸入料號。" });
        }
        try
        {
            await carts.AddAsync(id, new ScanCartAddInput(request.ItemCode ?? string.Empty, request.LocationCode, request.Quantity),
                User.ToActor(), cancellationToken);
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

    public Task<IActionResult> OnPostUpdateLineAsync(Guid id, Guid lineId, decimal quantity, string? locationCode, CancellationToken cancellationToken) =>
        RunAsync(id, () => carts.UpdateLineAsync(id, lineId, quantity, locationCode, User.ToActor(), cancellationToken), "已更新。", cancellationToken);

    public Task<IActionResult> OnPostRemoveLineAsync(Guid id, Guid lineId, CancellationToken cancellationToken) =>
        RunAsync(id, () => carts.RemoveLineAsync(id, lineId, User.ToActor(), cancellationToken), "已刪除一行。", cancellationToken);

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
        return true;
    }

    private IActionResult Missing()
    {
        TempData["Message"] = "找不到購物車（可能已在其他裝置轉成單據或放棄）。";
        return RedirectToPage("/Quick/Index");
    }
}
