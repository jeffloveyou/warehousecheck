using System.Text.Json;
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
using Warehouse.Domain.Items;
using Warehouse.Web.Pages.Shared;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Quick;

/// <summary>
/// 購物車整批選動作後的補齊畫面（ADR-0026）：倉庫（預設上次）、用途（預設上次）、每行儲位（有料的儲位、數量少的在前）、
/// 移庫目的、批號／序號。送出時整批轉成一張草稿（同一交易、規則與手動加行相同），轉到單據頁確認過帳。
/// </summary>
[Authorize(Policy = Policies.DocumentEdit)]
// 每行最多 8 個欄位 × 500 行（含分配量）＋表頭：預設 1024 個欄位不夠。
[RequestFormLimits(ValueCountLimit = ScanCart.MaxLines * 8 + 100)]
public partial class ConvertModel(
    IScanCartService carts, IStockDocumentService documents, IWarehouseService warehouses, IDocumentMasterDataService master,
    ISettingsService settings, IUserPreferenceService preferences) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public StockDocumentType? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty]
    public Guid? PurposeId { get; set; }

    /// <summary>開頁時產生，當作新單據 ID：按兩次或重送不會建兩張。</summary>
    [BindProperty]
    public Guid RequestId { get; set; }

    [BindProperty]
    public string? Note { get; set; }

    [BindProperty]
    public bool HasDocumentNote { get; set; }

    [BindProperty]
    public uint? ExpectedVersion { get; set; }

    [BindProperty]
    public Guid? PreparedWarehouseId { get; set; }

    [BindProperty]
    public List<LineForm> Lines { get; set; } = [];

    [BindProperty]
    public List<LineForm> PreparedLines { get; set; } = [];

    public sealed class LineForm
    {
        public Guid LineId { get; set; }
        public decimal? Quantity { get; set; }

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
    public bool CanSubmit { get; private set; } = true;
    public Guid? LockedWarehouseId => Cart.WarehouseId ?? PreparedWarehouseId;

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
        await InitializeFormAsync(cancellationToken);
        Note = Cart.Note;
        Lines = Cart.Lines.Select(l => new LineForm { LineId = l.Id, LocationCode = l.LocationCode }).ToList();
        return Page();
    }

    /// <summary>以當前的儲位分配開啟補齊頁，不建立單據也不變更購物車。</summary>
    public async Task<IActionResult> OnPostPrepareAsync(Guid id, string? allocationJson, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        if (!ModelState.IsValid || Type is not (StockDocumentType.Inbound or StockDocumentType.Outbound or StockDocumentType.Transfer)
            || ExpectedVersion is null || string.IsNullOrWhiteSpace(allocationJson) || allocationJson.Length > 200_000)
            return PrepareError(StatusCodes.Status400BadRequest, "儲位分配資料不完整，請回購物車重新選擇作業。");
        if (ExpectedVersion != Cart.Version)
            return PrepareError(StatusCodes.Status409Conflict, "購物車已被修改，請回購物車核對後再轉草稿。");
        if (Cart.WarehouseId is { } locked && WarehouseId is { } selected && locked != selected)
            return PrepareError(StatusCodes.Status400BadRequest, "購物車已鎖定另一個倉庫，請回購物車核對。");

        PreparedWarehouseId = Cart.WarehouseId ?? WarehouseId;
        if (PreparedWarehouseId is { } warehouse && WarehouseOptions.All(w => w.Id != warehouse))
            return PrepareError(StatusCodes.Status403Forbidden, "倉庫已停用或沒有作業權限，請回購物車核對。");

        List<ScanCartAllocation?>? allocations;
        try
        {
            allocations = JsonSerializer.Deserialize<List<ScanCartAllocation?>>(allocationJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return PrepareError(StatusCodes.Status400BadRequest, "儲位分配格式不正確，請回購物車核對。");
        }

        if (allocations is null || allocations.Count is 0 or > StockDocument.MaxLines
            || allocations.Any(a => a is null || a.ReservationId is not null || a.LocationCode?.Length > 200))
            return PrepareError(StatusCodes.Status400BadRequest, "儲位分配格式、筆數或指定預留有誤，請回購物車核對。");
        if (PreparedWarehouseId is null && allocations.Any(a => !string.IsNullOrWhiteSpace(a!.LocationCode)))
            return PrepareError(StatusCodes.Status400BadRequest, "已有指定儲位，請先在購物車選擇倉庫再轉草稿。");

        var cartLineIds = Cart.Lines.Select(l => l.Id).ToHashSet();
        if (allocations.Any(a => !cartLineIds.Contains(a!.LineId)))
            return PrepareError(StatusCodes.Status400BadRequest, "分配包含不在購物車的明細，請回購物車核對。");

        var byLine = allocations.Select(a => a!).ToLookup(a => a.LineId);
        foreach (var line in Cart.Lines)
        {
            var parts = byLine[line.Id];
            if (!parts.Any() || (line.TrackingMode != TrackingMode.None && parts.Count() != 1))
                return PrepareError(StatusCodes.Status400BadRequest, $"第 {line.LineNo} 行分配不完整，批號／序號物料不可拆分。");

            var remaining = line.Quantity;
            foreach (var part in parts)
            {
                if (part.Quantity <= 0 || part.Quantity > remaining || decimal.Round(part.Quantity, line.DecimalPlaces) != part.Quantity)
                    return PrepareError(StatusCodes.Status400BadRequest, $"第 {line.LineNo} 行分配量不符，請回購物車核對。");
                remaining -= part.Quantity;
            }
            if (remaining != 0)
                return PrepareError(StatusCodes.Status400BadRequest, $"第 {line.LineNo} 行分配合計不等於購物車數量，請回購物車核對。");
        }

        if (Note?.Length > StockDocument.NoteMaxLength)
            return PrepareError(StatusCodes.Status400BadRequest, $"備註最多 {StockDocument.NoteMaxLength} 字。");
        await InitializeFormAsync(cancellationToken);
        if (!HasDocumentNote) Note = Cart.Note;
        Lines = Cart.Lines.SelectMany(line => byLine[line.Id].Select(part => new LineForm
        {
            LineId = line.Id,
            LocationCode = part.LocationCode?.Trim(),
            Quantity = part.Quantity,
        })).ToList();
        return Page();
    }

    private async Task InitializeFormAsync(CancellationToken cancellationToken)
    {
        RequestId = Guid.NewGuid();
        ExpectedVersion = Cart.Version;
        var prefs = await preferences.GetAllAsync(User.ToActor().Id, cancellationToken);
        WarehouseId = LockedWarehouseId
            ?? (WarehouseId is { } selected && WarehouseOptions.Any(x => x.Id == selected) ? selected : (Guid?)null)
            ?? (WarehouseOptions.Count == 1 ? WarehouseOptions[0].Id : (Guid?)null)
            ?? (prefs.GetValueOrDefault(UserPreferenceKeys.DocumentsLastWarehouse) is { } w && Guid.TryParse(w, out var wid)
                && WarehouseOptions.Any(x => x.Id == wid) ? wid : (Guid?)null);
        var purposeKey = Type == StockDocumentType.Inbound ? UserPreferenceKeys.QuickLastPurposeIn : UserPreferenceKeys.QuickLastPurposeOut;
        PurposeId = prefs.GetValueOrDefault(purposeKey) is { } p && Guid.TryParse(p, out var pid) && Purposes.Any(x => x.Id == pid) ? pid : (Guid?)null;
    }

    private IActionResult PrepareError(int status, string message)
    {
        Response.StatusCode = status;
        Error = message;
        CanSubmit = false;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, Guid? operatorNameId, CancellationToken cancellationToken)
    {
        if (Type is not { } type || type is not (StockDocumentType.Inbound or StockDocumentType.Outbound or StockDocumentType.Transfer))
        {
            return RedirectToPage("/Quick/Cart", new { id });
        }
        try
        {
            // 新表單使用舊版無法綁定的欄名，避免回滾時把拆分當成單筆全量。
            if (ExpectedVersion is not null) Lines = PreparedLines;
            else if (PreparedLines.Count > 0) ModelState.AddModelError(nameof(ExpectedVersion), "缺少清單版本。");
            if (!ModelState.IsValid || RequestId == Guid.Empty)
            {
                CanSubmit = false;
                throw new DomainRuleException("轉草稿資料格式不正確，請回購物車重新選擇作業。");
            }
            if (WarehouseId is not { } warehouseId)
            {
                throw new DomainRuleException("請選擇倉庫。");
            }
            if (PreparedWarehouseId is { } prepared && prepared != warehouseId)
                throw new DomainRuleException("補齊頁倉庫與準備轉單時不同，請回購物車核對。");
            var lines = Lines.Select(l => new ScanCartConvertLine(
                l.LineId, l.LocationCode?.Trim() ?? string.Empty,
                type == StockDocumentType.Transfer ? l.TargetWarehouseCode : null,
                type == StockDocumentType.Transfer ? l.TargetLocationCode : null,
                l.LotNo, SplitSerials(l.SerialNos), type == StockDocumentType.Inbound ? l.ExpiryDate : null, l.Quantity)).ToList();
            var blank = lines.Select((l, i) => (l, i)).Where(x => x.l.LocationCode.Length == 0).Select(x => x.i + 1).ToList();
            if (blank.Count > 0)
            {
                throw new DomainRuleException($"第 {string.Join("、", blank)} 行還沒選儲位。");
            }
            var actor = await OperatorActor.ResolveAsync(User, operatorNameId, master, cancellationToken);
            var docId = await documents.CreateFromScanCartAsync(id, new ScanCartConvertInput(
                type, warehouseId, type == StockDocumentType.Transfer ? null : PurposeId, lines, RequestId, ExpectedVersion, HasDocumentNote ? Note ?? string.Empty : null), actor, cancellationToken);
            // 記住這次的倉庫與用途，下次預設帶入（只是畫面預設，權限與用途仍在轉單時檢查）
            await preferences.TrySetAsync(actor.Id, UserPreferenceKeys.DocumentsLastWarehouse, warehouseId.ToString(), cancellationToken);
            if (type != StockDocumentType.Transfer && PurposeId is { } purposeId)
            {
                await preferences.TrySetAsync(actor.Id,
                    type == StockDocumentType.Inbound ? UserPreferenceKeys.QuickLastPurposeIn : UserPreferenceKeys.QuickLastPurposeOut,
                    purposeId.ToString(), cancellationToken);
            }
            TempData["Message"] = "已由購物車產生草稿，請到單據頁核對後繼續作業。";
            return RedirectToPage("/Documents/Details", new { id = docId });
        }
        catch (NotFoundException)
        {
            return Missing();
        }
        catch (ConcurrencyConflictException ex) when (ex.ErrorCode == "scan_cart.concurrency_conflict")
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            CanSubmit = false;
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        if (!await LoadAsync(id, cancellationToken))
            return Missing();
        var currentIds = Cart.Lines.Select(l => l.Id).ToHashSet();
        if ((ExpectedVersion is { } expected && expected != Cart.Version) || Lines.Any(l => !currentIds.Contains(l.LineId)))
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            CanSubmit = false;
            Error = "購物車已被修改，請回購物車核對後再轉草稿。";
        }
        return Page();
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
        WarehouseOptions = (await warehouses.ListWarehousesAsync(cancellationToken)).Where(w => w.IsActive && !w.IsDeleted && operable.Allows(w.Id)).ToList();
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
