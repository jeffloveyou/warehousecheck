using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.MasterData;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Web.Pages.Shared;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Quick;

/// <summary>
/// 購物車整批選動作後的補齊畫面（ADR-0026）：倉庫（預設上次）、用途（預設上次）、每行儲位（有料的儲位、數量少的在前）、
/// 移庫目的、批號／序號。送出時整批轉成一張草稿（同一交易、規則與手動加行相同），轉到單據頁確認過帳。
/// </summary>
[Authorize(Policy = Policies.DocumentEdit)]
public partial class ConvertModel(
    IScanCartService carts, IStockDocumentService documents, IWarehouseService warehouses, IDocumentMasterDataService master,
    ISettingsService settings, IUserPreferenceService preferences) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public StockDocumentType? Type { get; set; }

    [BindProperty]
    public Guid? WarehouseId { get; set; }

    [BindProperty]
    public Guid? PurposeId { get; set; }

    /// <summary>開頁時產生，當作新單據 ID：按兩次或重送不會建兩張。</summary>
    [BindProperty]
    public Guid RequestId { get; set; }

    [BindProperty]
    public List<LineForm> Lines { get; set; } = [];

    public sealed class LineForm
    {
        public Guid LineId { get; set; }

        public string? LocationCode { get; set; }

        public string? TargetWarehouseCode { get; set; }

        public string? TargetLocationCode { get; set; }

        public string? LotNo { get; set; }

        /// <summary>序號：一行一個（也可用逗號、空白分隔）。</summary>
        public string? SerialNos { get; set; }

        public DateOnly? ExpiryDate { get; set; }
    }

    public ScanCartDetails Cart { get; private set; } = null!;

    public IReadOnlyList<WarehouseRow> WarehouseOptions { get; private set; } = [];

    public IReadOnlyList<PurposeRow> Purposes { get; private set; } = [];

    public SystemSettings Settings { get; private set; } = null!;

    public string? Error { get; private set; }

    public string TypeName => Type switch
    {
        StockDocumentType.Inbound => "進倉",
        StockDocumentType.Outbound => "出倉",
        _ => "移庫",
    };

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Type is not (StockDocumentType.Inbound or StockDocumentType.Outbound or StockDocumentType.Transfer))
        {
            return RedirectToPage("/Quick/Cart", new { id });
        }
        if (!await LoadAsync(id, cancellationToken))
        {
            return Missing();
        }
        if (Cart.Lines.Count == 0)
        {
            TempData["Message"] = "購物車是空的，請先掃碼加入。";
            return RedirectToPage("/Quick/Cart", new { id });
        }
        RequestId = Guid.NewGuid();
        var prefs = await preferences.GetAllAsync(User.ToActor().Id, cancellationToken);
        WarehouseId = Cart.WarehouseId
            ?? (WarehouseOptions.Count == 1 ? WarehouseOptions[0].Id : (Guid?)null)
            ?? (prefs.GetValueOrDefault(UserPreferenceKeys.DocumentsLastWarehouse) is { } w && Guid.TryParse(w, out var wid)
                && WarehouseOptions.Any(x => x.Id == wid) ? wid : (Guid?)null);
        var purposeKey = Type == StockDocumentType.Inbound ? UserPreferenceKeys.QuickLastPurposeIn : UserPreferenceKeys.QuickLastPurposeOut;
        PurposeId = prefs.GetValueOrDefault(purposeKey) is { } p && Guid.TryParse(p, out var pid) && Purposes.Any(x => x.Id == pid) ? pid : (Guid?)null;
        Lines = Cart.Lines.Select(l => new LineForm { LineId = l.Id, LocationCode = l.LocationCode }).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Type is not { } type)
        {
            return RedirectToPage("/Quick/Cart", new { id });
        }
        try
        {
            if (WarehouseId is not { } warehouseId)
            {
                throw new DomainRuleException("請選擇倉庫。");
            }
            var lines = Lines.Select(l => new ScanCartConvertLine(
                l.LineId, l.LocationCode?.Trim() ?? string.Empty,
                type == StockDocumentType.Transfer ? l.TargetWarehouseCode : null,
                type == StockDocumentType.Transfer ? l.TargetLocationCode : null,
                l.LotNo, SplitSerials(l.SerialNos), type == StockDocumentType.Inbound ? l.ExpiryDate : null)).ToList();
            var blank = lines.Select((l, i) => (l, i)).Where(x => x.l.LocationCode.Length == 0).Select(x => x.i + 1).ToList();
            if (blank.Count > 0)
            {
                throw new DomainRuleException($"第 {string.Join("、", blank)} 行還沒選儲位。");
            }
            var actor = User.ToActor();
            var docId = await documents.CreateFromScanCartAsync(id, new ScanCartConvertInput(
                type, warehouseId, type == StockDocumentType.Transfer ? null : PurposeId, lines, RequestId), actor, cancellationToken);
            // 記住這次的倉庫與用途，下次預設帶入（只是畫面預設，權限與用途仍在轉單時檢查）
            await preferences.TrySetAsync(actor.Id, UserPreferenceKeys.DocumentsLastWarehouse, warehouseId.ToString(), cancellationToken);
            if (type != StockDocumentType.Transfer && PurposeId is { } purposeId)
            {
                await preferences.TrySetAsync(actor.Id,
                    type == StockDocumentType.Inbound ? UserPreferenceKeys.QuickLastPurposeIn : UserPreferenceKeys.QuickLastPurposeOut,
                    purposeId.ToString(), cancellationToken);
            }
            TempData["Message"] = "已由購物車產生草稿，請核對明細後確認過帳。";
            return RedirectToPage("/Documents/Details", new { id = docId });
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

    /// <summary>補儲位的選單：這個料號有庫存的儲位排前面，數量少的最前面（附數量）；移庫目的只依關鍵字。只限可作業的倉庫。</summary>
    public async Task<IActionResult> OnGetLocationsAsync(
        Guid? warehouseId, string? warehouseCode, Guid? itemId, string? q, bool target,
        [FromServices] ILineEntryQueryService queries, CancellationToken cancellationToken)
    {
        // 移庫目的可選別的倉庫（畫面傳倉庫碼）
        if (!string.IsNullOrWhiteSpace(warehouseCode))
        {
            var code = Warehouse.Domain.Text.TextNormalizer.Normalize(warehouseCode);
            warehouseId = (await warehouses.ListWarehousesAsync(cancellationToken)).FirstOrDefault(w => w.Code == code)?.Id;
        }
        return warehouseId is { } id && await AllowedAsync(id, cancellationToken)
            ? await LineEntryJson.LocationsAsync(queries, id, q, target ? null : itemId,
                target ? LocationListMode.Plain : LocationListMode.StockAscending, cancellationToken)
            : new JsonResult(new { items = Array.Empty<object>() });
    }

    /// <summary>出倉、移庫的批號／序號選單（先到期先出）。</summary>
    public async Task<IActionResult> OnGetLotsAsync(
        Guid warehouseId, Guid itemId, string? q, [FromServices] ILineEntryQueryService queries, CancellationToken cancellationToken) =>
        await AllowedAsync(warehouseId, cancellationToken)
            ? await LineEntryJson.LotsAsync(queries, warehouseId, itemId, null, q, cancellationToken)
            : new JsonResult(new { items = Array.Empty<object>() });

    public string LocationsUrl => Url.Page("/Quick/Convert", "Locations", new { id = Cart.Id })!;

    public string LotsUrl => Url.Page("/Quick/Convert", "Lots", new { id = Cart.Id })!;

    private async Task<bool> AllowedAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        return operable.Allows(warehouseId)
            && (await warehouses.ListWarehousesAsync(cancellationToken)).Any(w => w.Id == warehouseId && w.IsActive);
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await carts.GetAsync(id, User.ToActor(), cancellationToken) is not { } cart)
        {
            return false;
        }
        Cart = cart;
        var operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        WarehouseOptions = (await warehouses.ListWarehousesAsync(cancellationToken)).Where(w => w.IsActive && operable.Allows(w.Id)).ToList();
        Settings = await settings.GetAsync(cancellationToken);
        Purposes = Type == StockDocumentType.Transfer || !Settings.DocumentPurposeEnabled
            ? []
            : await master.ListPurposesAsync(Type == StockDocumentType.Inbound ? PurposeDirection.In : PurposeDirection.Out, false, cancellationToken);
        return true;
    }

    private static IReadOnlyList<string>? SplitSerials(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : SerialSeparator().Split(text.Trim()).Where(s => s.Length > 0).ToList();

    [GeneratedRegex(@"[\s,，、;；]+")]
    private static partial Regex SerialSeparator();

    private IActionResult Missing()
    {
        TempData["Message"] = "找不到購物車（可能已在其他裝置轉成單據或放棄）。";
        return RedirectToPage("/Quick/Index");
    }
}
