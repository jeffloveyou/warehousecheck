using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Scanning;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Text;
using Warehouse.Web.Pages.Shared;

namespace Warehouse.Web.Pages.Opening;

/// <summary>
/// 期初單明細：草稿時新增或刪除明細、送出、取消；倉庫主管可退回或核准（過帳）。
/// 核准按鈕帶一個開頁時產生的冪等鍵：連點、重新整理後重送，都只會過帳一次。
/// 權限一律由服務再檢查；這裡的 IAuthorizationService 只用來決定 handler 能不能執行、按鈕要不要顯示。
/// </summary>
public class DetailsModel(
    IOpeningService openings, IStockQueryService stock, IAuthorizationService authorization,
    IItemQueryService items, IItemLocationService itemLocations) : PageModel
{
    /// <summary>查建議儲位的料號（GET ?Suggest=）。</summary>
    [BindProperty(SupportsGet = true)]
    public string? Suggest { get; set; }

    /// <summary>建議儲位（預設、常用、目前有庫存；M3-07）。只是建議，由人選擇。</summary>
    public IReadOnlyList<LocationSuggestion>? Suggestions { get; private set; }

    public OpeningDetails Doc { get; private set; } = null!;

    public IReadOnlyList<UncountedLocationRow> Uncounted { get; private set; } = [];

    public bool CanEdit { get; private set; }

    public bool CanApprove { get; private set; }

    public string? Error { get; private set; }

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public string? IdempotencyKey { get; set; }

    [BindProperty]
    public LineForm Line { get; set; } = new();

    [BindProperty]
    public string? Comment { get; set; }

    public sealed class LineForm
    {
        public string LocationCode { get; set; } = string.Empty;

        public string? ItemCode { get; set; }

        public string? LotNo { get; set; }

        public string? SerialNo { get; set; }

        public DateOnly? ExpiryDate { get; set; }

        public decimal Quantity { get; set; }

        /// <summary>勾選時忽略物料欄位，整個儲位確認為空。</summary>
        public bool EmptyLocation { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    public Task<IActionResult> OnPostAddLineAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.OpeningEdit, async actor =>
        {
            var input = new OpeningLineInput(
                Line.LocationCode, Line.EmptyLocation ? null : Line.ItemCode, Line.LotNo, Line.SerialNo, Line.ExpiryDate,
                Line.EmptyLocation ? 0 : Line.Quantity);
            await openings.AddLineAsync(id, input, actor, cancellationToken);
            return Line.EmptyLocation ? $"已將儲位 {Line.LocationCode.Trim()} 標記為空。" : "已新增明細。";
        }, cancellationToken);

    /// <summary>連續掃碼面板（M5-03）：期初的步驟同進倉（物料 → 批號／序號 → 儲位 → 數量）。</summary>
    public ScanPanelModel ScanPanel => new(
        "opening", Doc.WarehouseId,
        Url.Page("/Opening/Details", "ScanResolve", new { id = Doc.Id })!,
        Url.Page("/Opening/Details", "ScanAddLine", new { id = Doc.Id })!);

    /// <summary>掃碼面板解析一筆掃碼（GET，回傳 JSON）；物料另附建議儲位。只做精確比對。</summary>
    public async Task<IActionResult> OnGetScanResolveAsync(
        Guid id, string? code, [FromServices] IScanResolver resolver, CancellationToken cancellationToken)
    {
        if (await openings.GetAsync(id, cancellationToken) is not { } doc)
        {
            return NotFound();
        }
        var result = await resolver.ResolveAsync(code, doc.WarehouseId, cancellationToken);
        object? extra = null;
        if (result.Item is { } item)
        {
            var suggestions = await itemLocations.SuggestAsync(item.Id, doc.WarehouseId, cancellationToken);
            extra = new { suggestions = suggestions.Select(s => new { location = s.LocationCode, reason = s.Reason.ToString(), quantity = s.QuantityHere }) };
        }
        return new JsonResult(ScanJson.From(result, extra));
    }

    /// <summary>
    /// 掃碼面板加入期初明細（POST JSON，antiforgery 由 RequestVerificationToken 標頭驗證）。
    /// 成功回傳重繪的明細表（HTML）；規則錯誤回 400 JSON。「確認空儲位」仍用下方表單。
    /// </summary>
    public async Task<IActionResult> OnPostScanAddLineAsync(Guid id, [FromBody] ScanLineRequest request, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.OpeningEdit)).Succeeded)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        try
        {
            if (string.IsNullOrWhiteSpace(request.ItemCode))
            {
                throw new DomainRuleException("請先掃描物料。");
            }
            await openings.AddLineAsync(id, new OpeningLineInput(
                request.LocationCode ?? string.Empty, request.ItemCode, request.LotNo, request.SerialNo, request.ExpiryDate,
                request.Quantity, request.Merge), User.ToActor(), cancellationToken);
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            return new JsonResult(new { message = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
        // 只讀明細表需要的資料（不算未清點儲位、不查建議儲位），與進出倉單相同（M5-03 審查 S3）
        if (await openings.GetAsync(id, cancellationToken) is not { } current)
        {
            return NotFound();
        }
        Doc = current;
        Version = current.Version;
        var actor = User.ToActor();
        CanEdit = current.Status == StockDocumentStatus.Draft && (actor.IsWarehouseManager || current.CreatedById == actor.Id);
        return Partial("_OpeningLines", this);
    }

    /// <summary>掃碼面板送出的期初明細（JSON）。</summary>
    public sealed class ScanLineRequest
    {
        public string? LocationCode { get; set; }

        public string? ItemCode { get; set; }

        public decimal Quantity { get; set; }

        public string? LotNo { get; set; }

        public string? SerialNo { get; set; }

        public DateOnly? ExpiryDate { get; set; }

        /// <summary>逐件累加：相同的草稿行累加數量。</summary>
        public bool Merge { get; set; }
    }

    public Task<IActionResult> OnPostRemoveLineAsync(Guid id, Guid lineId, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.OpeningEdit, async actor =>
        {
            await openings.RemoveLineAsync(id, lineId, actor, cancellationToken);
            return "已刪除明細。";
        }, cancellationToken);

    public Task<IActionResult> OnPostSubmitAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.OpeningEdit, async actor =>
        {
            await openings.SubmitAsync(id, Version, actor, cancellationToken);
            return "已送出，等待倉庫主管核准。";
        }, cancellationToken);

    public Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.OpeningEdit, async actor =>
        {
            await openings.CancelAsync(id, Version, actor, cancellationToken);
            return "已取消。";
        }, cancellationToken);

    public Task<IActionResult> OnPostReturnAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.OpeningApprove, async actor =>
        {
            await openings.ReturnAsync(id, Version, Comment ?? string.Empty, actor, cancellationToken);
            return "已退回給建立者修改。";
        }, cancellationToken);

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.OpeningApprove, async actor =>
        {
            await openings.ApproveAsync(id, Version, IdempotencyKey ?? string.Empty, actor, cancellationToken);
            return "已核准並過帳。";
        }, cancellationToken);

    /// <summary>執行動作：成功轉址（PRG）；已知錯誤重新載入並顯示訊息，保留使用者輸入。</summary>
    private async Task<IActionResult> RunAsync(
        Guid id, string policy, Func<Actor, Task<string>> action, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, policy)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action(User.ToActor());
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await openings.GetAsync(id, cancellationToken) is not { } doc)
        {
            return false;
        }
        Doc = doc;
        Version = doc.Version;
        var actor = User.ToActor();
        CanEdit = doc.Status == StockDocumentStatus.Draft
            && (await authorization.AuthorizeAsync(User, Policies.OpeningEdit)).Succeeded
            && (actor.IsWarehouseManager || doc.CreatedById == actor.Id);
        CanApprove = doc.Status == StockDocumentStatus.Submitted
            && (await authorization.AuthorizeAsync(User, Policies.OpeningApprove)).Succeeded;
        if (doc.Status == StockDocumentStatus.Draft)
        {
            Uncounted = (await stock.GetUncountedLocationsAsync(doc.WarehouseId, 1, cancellationToken)).Items;
            if (!string.IsNullOrWhiteSpace(Suggest)
                && await items.FindIdByItemCodeAsync(SuggestCode(), cancellationToken) is { } itemId)
            {
                Suggestions = await itemLocations.SuggestAsync(itemId, doc.WarehouseId, cancellationToken);
            }
        }
        return true;
    }

    /// <summary>查建議儲位的料號：可以是物料 QR（WH:ITEM:）或料號文字。</summary>
    private string SuggestCode() =>
        ScanCodeParser.Parse(Suggest) is { Kind: ScanCodeKind.Item, ItemCode: { } code } ? code : TextNormalizer.Normalize(Suggest);
}
