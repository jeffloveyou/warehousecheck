using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Web.Security;
using Warehouse.Application.Abstractions;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.MasterData;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.Documents;

/// <summary>
/// 進出倉單清單（任何登入者可看）；作業員、倉庫主管可建立（DocumentEdit）。
/// 篩選與分頁都在伺服器（分頁參數 p）。建立成功轉到明細頁，明細與送審、確認在明細頁做。
/// </summary>
public class IndexModel(
    IStockDocumentService documents, IWarehouseService warehouses, IDocumentMasterDataService master,
    ISettingsService settings, IAuthorizationService authorization) : PageModel
{
    /// <summary>可建立的單據類型（期初走 /Opening）。</summary>
    public static readonly StockDocumentType[] CreatableTypes =
        [StockDocumentType.Inbound, StockDocumentType.Outbound, StockDocumentType.Transfer];

    [BindProperty(SupportsGet = true)]
    public StockDocumentType? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public StockDocumentStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    /// <summary>建立單據的類型與倉庫（欄位名稱 Create.*，避免和 GET 篩選的 WarehouseId 衝突）。</summary>
    [BindProperty]
    public CreateForm Create { get; set; } = new();

    [BindProperty]
    public DocumentHeaderForm Header { get; set; } = new();

    public sealed class CreateForm
    {
        public StockDocumentType? Type { get; set; }

        public Guid? WarehouseId { get; set; }

        /// <summary>開表單時產生，當作新單據的 ID：連點或重送不會建兩張（M4-03 審查 #13）。</summary>
        public Guid? RequestId { get; set; }
    }

    public PagedResult<DocumentRow> Result { get; private set; } = null!;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    /// <summary>可作業的倉庫（null＝全部），建單下拉選單只列這些（M6-01b）。</summary>
    public IReadOnlySet<Guid>? Operable { get; private set; }

    public DocumentHeaderOptions? Options { get; private set; }

    public bool CanEdit { get; private set; }

    public string? Error { get; private set; }

    public Task OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.DocumentEdit)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            if (Create.Type is not { } type || !CreatableTypes.Contains(type))
            {
                throw new DomainRuleException("請選擇單據類型。");
            }
            if (Create.WarehouseId is not { } warehouseId)
            {
                throw new DomainRuleException("請選擇倉庫。");
            }
            var header = await DocumentFormSupport.ToHeaderAsync(Header, master, cancellationToken);
            var id = await documents.CreateAsync(type, warehouseId, header, User.ToActor(), cancellationToken, Create.RequestId);
            return RedirectToPage("/Documents/Details", new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public string PageUrl(int page) => Url.Page("/Documents/Index", new { Type, Status, WarehouseId, p = page })!;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(includeDeleted: true, cancellationToken); // 歷史查詢可選已刪除的倉庫
        Operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        Result = await documents.ListAsync(Type, Status, WarehouseId, PageNumber, cancellationToken);
        CanEdit = (await authorization.AuthorizeAsync(User, Policies.DocumentEdit)).Succeeded;
        if (CanEdit)
        {
            Options = await DocumentFormSupport.LoadOptionsAsync(master, settings, null, null, null, cancellationToken);
        }
    }
}
