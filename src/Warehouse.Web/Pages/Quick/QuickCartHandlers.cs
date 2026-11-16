using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Scanning;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Scanning;
using Warehouse.Web.Pages.Documents;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Quick;

public partial class CartModel
{
    public IReadOnlyDictionary<string, string> Preferences { get; private set; } = new Dictionary<string, string>();
    public DocumentHeaderOptions Options { get; private set; } = null!;
    public IReadOnlyList<WarehouseRow> WarehouseOptions { get; private set; } = [];
    public Guid ActorId => User.ToActor().Id;
    public string ActorName => User.ToActor().Name;

    public async Task<IActionResult> OnGetCheckoutResultAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await carts.CheckoutResultAsync(id, User.ToActor(), cancellationToken);
        return result is null ? new JsonResult(null) : new JsonResult(new { result.DocumentId, result.DocNo,
            Status = result.Status.ToString(), Url = Url.Page("/Documents/Details", new { id = result.DocumentId }),
            Message = result.Status == StockDocumentStatus.Posted ? "已完成過帳" : result.Status == StockDocumentStatus.Submitted ? "已送審，尚未入帳" : "單據目前狀態：" + Display.Of(result.Status) });
    }

    public sealed record EditRequest(Guid LineId, uint ExpectedVersion, decimal Quantity, string? LocationCode, string? Note);

    public Task<IActionResult> OnPostEditLineAsync(Guid id, [FromBody] EditRequest input, CancellationToken cancellationToken) =>
        EditAsync(id, () => carts.UpdateLineAsync(id, input.LineId, input.Quantity, input.LocationCode, User.ToActor(), cancellationToken, input.ExpectedVersion), cancellationToken);
    public Task<IActionResult> OnPostDeleteLineAsync(Guid id, [FromBody] EditRequest input, CancellationToken cancellationToken) =>
        EditAsync(id, () => carts.RemoveLineAsync(id, input.LineId, User.ToActor(), cancellationToken, input.ExpectedVersion), cancellationToken);
    public Task<IActionResult> OnPostEditNoteAsync(Guid id, [FromBody] EditRequest input, CancellationToken cancellationToken) =>
        EditAsync(id, () => carts.SetNoteAsync(id, input.Note, User.ToActor(), cancellationToken, input.ExpectedVersion), cancellationToken);
    public Task<IActionResult> OnPostDraftAsync(Guid id, [FromBody] ScanCartDraftInput input, CancellationToken cancellationToken) =>
        EditAsync(id, () => carts.SaveDraftAsync(id, input, User.ToActor(), cancellationToken), cancellationToken);
    public Task<IActionResult> OnPostUndoAsync(Guid id, [FromBody] ScanCartUndoInput input, CancellationToken cancellationToken) =>
        EditAsync(id, () => carts.UndoScanAsync(id, input, User.ToActor(), cancellationToken), cancellationToken);

    private async Task<IActionResult> EditAsync(Guid id, Func<Task> action, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest(new { message = "輸入格式不正確。" });
        try { await action(); return new JsonResult(await carts.GetAsync(id, User.ToActor(), cancellationToken)); }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        { return BadRequest(new { message = ex.Message, code = (ex as AppException)?.ErrorCode }); }
    }

    public async Task<IActionResult> OnPostNextAsync(CancellationToken cancellationToken)
    {
        var id = await carts.CreateAsync(null, User.ToActor(), cancellationToken);
        return RedirectToPage("/Quick/Cart", new { id });
    }

    public async Task<IActionResult> OnGetSnapshotAsync(Guid id, CancellationToken cancellationToken) =>
        await carts.GetAsync(id, User.ToActor(), cancellationToken) is { } c ? new JsonResult(c) : NotFound();

    public async Task<IActionResult> OnGetPartnerAsync(Guid id, string? code, CancellationToken cancellationToken) =>
        await carts.OwnsAsync(id, User.ToActor(), cancellationToken) && code is { Length: > 0 and <= 20 }
            ? new JsonResult(await master.FindActivePartnersByCodeAsync(code, cancellationToken)) : BadRequest();

    public async Task<IActionResult> OnGetRecipientsAsync(Guid id, string? q, CancellationToken cancellationToken) =>
        await carts.OwnsAsync(id, User.ToActor(), cancellationToken)
            ? new JsonResult(await carts.RecipientsAsync(q, cancellationToken)) : NotFound();

    public async Task<IActionResult> OnGetStockAsync(Guid id, Guid warehouseId, CancellationToken cancellationToken)
    {
        try { return new JsonResult(await carts.StockAsync(id, warehouseId, User.ToActor(), cancellationToken)); }
        catch (Exception ex) when (ex is DomainRuleException or AppException) { return BadRequest(new { message = ex.Message }); }
    }

    public async Task<IActionResult> OnGetReservationsAsync(Guid id, Guid warehouseId, string itemCode, int p,
        [FromServices] IStockReservationService reservations, CancellationToken cancellationToken)
    {
        if (!await carts.OwnsAsync(id, User.ToActor(), cancellationToken)) return NotFound();
        var allowed = await HttpContext.OperableWarehousesAsync(cancellationToken);
        if (!allowed.Allows(warehouseId)) return Forbid();
        if (itemCode is not { Length: > 0 and <= 200 }) return BadRequest();
        var result = await reservations.ListAsync(itemCode, warehouseId, null, p < 1 ? 1 : p, cancellationToken, selectableOnly: true);
        return new JsonResult(new { Items = result.Items.Select(r => new { r.Id, r.RemainingQuantity, r.Note, r.CreatedByName }), result.Page, result.TotalPages });
    }

    public async Task<IActionResult> OnGetLocationsAsync(Guid id, Guid warehouseId, Guid? itemId, string? q,
        [FromServices] ILineEntryQueryService queries, CancellationToken cancellationToken)
    {
        if (!await carts.OwnsAsync(id, User.ToActor(), cancellationToken)) return NotFound();
        var allowed = await HttpContext.OperableWarehousesAsync(cancellationToken);
        if (!allowed.Allows(warehouseId)) return Forbid();
        return await Warehouse.Web.Pages.Shared.LineEntryJson.LocationsAsync(queries, warehouseId, q, itemId, LocationListMode.StockAscending, cancellationToken);
    }

    // 舊頁可查回已完成的原請求。新版走獨立入口，避免回滾時舊服務忽略審核模式。
    // 公用帳號的操作人放在查詢字串 operatorNameId（ADR-0030），結帳 JSON 格式不變，舊頁仍可送出（一般帳號）。
    public Task<IActionResult> OnPostCheckoutAsync(Guid id, [FromBody] ScanCartCheckoutInput? input, [FromQuery] Guid? operatorNameId, CancellationToken cancellationToken) =>
        CheckoutAsync(id, input, operatorNameId, cancellationToken);

    public Task<IActionResult> OnPostCheckoutReviewedAsync(Guid id, [FromBody] ScanCartCheckoutInput? input, [FromQuery] Guid? operatorNameId, CancellationToken cancellationToken) =>
        CheckoutAsync(id, input, operatorNameId, cancellationToken);

    /// <summary>公用帳號（ADR-0030）：結帳畫面要顯示人員名單；一般帳號為 false，畫面不出現選單。</summary>
    public bool IsSharedAccount => User.IsSharedAccount();

    private async Task<IActionResult> CheckoutAsync(Guid id, ScanCartCheckoutInput? input, Guid? operatorNameId, CancellationToken cancellationToken)
    {
        if (input is null || !ModelState.IsValid) return BadRequest(new { message = "結帳資料格式不正確。" });
        try
        {
            var actor = await OperatorActor.ResolveAsync(User, operatorNameId, master, cancellationToken);
            var result = await documents.CheckoutScanCartAsync(id, input, actor, cancellationToken);
            await preferences.TrySetAsync(actor.Id, Warehouse.Application.Accounts.UserPreferenceKeys.DocumentsLastWarehouse, input.WarehouseId.ToString(), cancellationToken);
            if (input.Header.PurposeId is { } purpose)
                await preferences.TrySetAsync(actor.Id, input.Type == StockDocumentType.Inbound ? Warehouse.Application.Accounts.UserPreferenceKeys.QuickLastPurposeIn : Warehouse.Application.Accounts.UserPreferenceKeys.QuickLastPurposeOut, purpose.ToString(), cancellationToken);
            return new JsonResult(new { result.DocumentId, result.DocNo, Status = result.Status.ToString(),
                Url = Url.Page("/Documents/Details", new { id = result.DocumentId }),
                Message = result.Status == StockDocumentStatus.Posted
                    ? input.Type == StockDocumentType.Inbound ? "已完成入庫" : "已完成出庫"
                    : result.Status == StockDocumentStatus.Submitted ? "已送審，尚未入帳" : "單據目前狀態：" + Display.Of(result.Status) });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            return new JsonResult(new { message = ex.Message, code = (ex as AppException)?.ErrorCode }) { StatusCode = 400 };
        }
    }

    public sealed record RegisterRequest(string Code, string ItemCode, decimal PackQuantity);

    public async Task<IActionResult> OnPostRegisterAsync(Guid id, [FromBody] RegisterRequest? input,
        [FromServices] IAuthorizationService authorization, [FromServices] IScanResolver resolver,
        [FromServices] IItemIndexService indexes, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.ItemsIndex)).Succeeded) return Forbid();
        if (!await carts.OwnsAsync(id, User.ToActor(), cancellationToken)) return NotFound();
        if (input is null || !ModelState.IsValid || input.Code is not { Length: > 0 and <= 200 }
            || input.ItemCode is not { Length: > 0 and <= 200 } || !ScanInput.IsPrintableAscii(input.Code))
            return BadRequest(new { message = "請核對條碼內容。" });
        try
        {
            var code = await resolver.ResolveAsync(input.Code, null, cancellationToken);
            if (code.Kind != ScanResultKind.Text) throw new DomainRuleException("這個碼已登錄或不是可登錄的原廠條碼。");
            foreach (var w in await warehouses.ListWarehousesAsync(cancellationToken))
                if (await warehouses.GetLocationByCodeAsync(w.Code, code.Value, cancellationToken) is not null)
                    throw new DomainRuleException("這是儲位碼，不能登錄成物料條碼。");
            var target = await resolver.ResolveAsync(input.ItemCode, null, cancellationToken);
            if (target.Item is not { } item) throw new DomainRuleException("請明確選擇正式物料並核對規格。");
            await indexes.AddAsync(item.Id, ItemIndexKind.Barcode, code.Value, User.ToActor(), cancellationToken, input.PackQuantity);
            return new JsonResult(new { message = "條碼已登錄，請重試原本的掃碼紀錄。" });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException) { return BadRequest(new { message = ex.Message }); }
    }
}
