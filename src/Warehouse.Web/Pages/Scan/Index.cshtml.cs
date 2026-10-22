using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Scanning;
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
    IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    public ScanResolution? Result { get; private set; }

    public PagedResult<BalanceRow>? Balances { get; private set; }

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
            CanRegister = ScanInput.IsPrintableAscii(Result.Value)
                && (await authorization.AuthorizeAsync(User, Policies.ItemsIndex)).Succeeded;
        }
    }
}
