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
using Warehouse.Domain.Scanning;

namespace Warehouse.Web.Pages.Scan;

/// <summary>
/// 掃碼查料（M5-03）：手機巡倉、條碼槍查詢用。掃物料 → 物料摘要與各倉各儲位結存；掃儲位 → 該儲位的庫存；
/// 掃到未登錄的條碼 → 顯示原始值，有 Items.Index 權限時可輸入正式料號把它登錄為原廠條碼（只用精確料號，不自動選物料）。
/// GET ?code= 也能直接用（沒有 JS 時是一般表單）；結存在伺服器分頁（每頁 20 筆）。
/// </summary>
public class IndexModel(
    IScanResolver resolver, IStockQueryService stock, IItemIndexService index, IItemQueryService items,
    IAuthorizationService authorization, IWarehouseService warehouses) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    public ScanResolution? Result { get; private set; }

    public PagedResult<BalanceRow>? Balances { get; private set; }

    /// <summary>沒有前綴的文字剛好是某些倉庫的儲位碼（掃到沒有 WH:LOC: 前綴的儲位標籤）。</summary>
    public IReadOnlyList<LocationRow> MatchingLocations { get; private set; } = [];

    /// <summary>掃到未登錄的條碼，且使用者可以登錄原廠條碼。</summary>
    public bool CanRegister { get; private set; }

    public string? Error { get; private set; }

    /// <summary>登錄原廠條碼：目標物料的正式料號（可掃物料 QR）。</summary>
    [BindProperty]
    public string? RegisterItemCode { get; set; }

    [BindProperty]
    public decimal? RegisterPackQuantity { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRegisterAsync(CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.ItemsIndex)).Succeeded)
        {
            return Forbid();
        }
        // 伺服器端重新判斷能不能登錄（不信任網址上的 code）：必須是不認得的 ASCII 文字，且不是任何儲位碼（M5-03 審查 M1）
        await LoadAsync(cancellationToken);
        if (!CanRegister)
        {
            Error = "這個內容不能登錄為原廠條碼（已是料號、條碼、儲位碼，或含中文等字元）。";
            return Page();
        }
        if (ModelState.TryGetValue(nameof(RegisterPackQuantity), out var packState) && packState.Errors.Count > 0)
        {
            Error = "包裝數量格式錯誤，請輸入數字。";
            return Page();
        }
        try
        {
            var scan = ScanCodeParser.Parse(RegisterItemCode);
            var itemCode = scan.Kind == ScanCodeKind.Item ? scan.ItemCode! : scan.Normalized;
            var itemId = await items.FindIdByItemCodeAsync(itemCode, cancellationToken)
                ?? throw new DomainRuleException($"找不到料號 {itemCode}，請輸入正式料號（例 EL-000125）。");
            await index.AddAsync(itemId, ItemIndexKind.Barcode, Code ?? string.Empty, User.ToActor(), cancellationToken,
                RegisterPackQuantity ?? 1m);
            TempData["Message"] = $"已把條碼 {ScanInput.StripControl(Code)} 登錄到 {itemCode}。";
            return RedirectToPage(new { code = ScanInput.StripControl(Code) });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>結果的一行摘要（報讀軟體朗讀、也方便手機一眼看懂）。</summary>
    public string Summary => Result switch
    {
        null => string.Empty,
        { Item: { } item } => $"物料 {item.ItemCode} {item.Name}，{Balances?.TotalCount ?? 0} 筆結存",
        { Location: { } location } => $"儲位 {location.WarehouseCode} {location.Code}，{Balances?.TotalCount ?? 0} 筆結存",
        { Kind: ScanResultKind.Text } when MatchingLocations.Count > 0 => $"{Result.Value} 是儲位碼",
        { Kind: ScanResultKind.Text } => $"{Result.Value} 不是已登錄的料號、條碼或儲位",
        _ => Result.Message ?? "無法辨識",
    };

    public string PageUrl(int page) => Url.Page("/Scan/Index", new { code = Code, p = page })!;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return;
        }
        Result = await resolver.ResolveAsync(Code, null, cancellationToken);
        if (Result.Item is { } item)
        {
            Balances = await stock.GetBalancesAsync(null, null, item.Id, PageNumber, cancellationToken);
        }
        else if (Result.Location is { } location)
        {
            Balances = await stock.GetBalancesAsync(location.WarehouseId, location.Id, null, PageNumber, cancellationToken);
        }
        else if (Result.Kind == ScanResultKind.Text)
        {
            // 沒有指定倉庫時，解析不會猜儲位碼；這裡跨倉精確查一次，讓人知道它是儲位，而不是未登錄的條碼
            var matches = new List<LocationRow>();
            foreach (var warehouse in await warehouses.ListWarehousesAsync(cancellationToken))
            {
                if (await warehouses.GetLocationByCodeAsync(warehouse.Code, Result.Value, cancellationToken) is { } match)
                {
                    matches.Add(match);
                }
            }
            MatchingLocations = matches;
            CanRegister = matches.Count == 0 && ScanInput.IsPrintableAscii(Result.Value)
                && (await authorization.AuthorizeAsync(User, Policies.ItemsIndex)).Succeeded;
        }
    }
}
