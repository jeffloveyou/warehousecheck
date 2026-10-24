using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Web.Security;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.Reservations;

/// <summary>
/// 手動預留清單與建立（任何登入者可看；作業員、倉庫主管可建立與取消自己的預留，主管可取消任何人的）。
/// 出倉單送審自動建立的預留（ReferenceType=StockDocument）不能在這裡取消，要到該單據取消或退回。
/// 建立表單帶開頁時產生的冪等鍵：連點或重新整理後重送，只會建立一筆。
/// 到期時間以 datetime-local 輸入，當作台灣當地時間轉成 UTC。
/// </summary>
public class IndexModel(IStockReservationService reservations, IWarehouseService warehouses, IAuthorizationService authorization) : PageModel
{
    /// <summary>出倉單送審自動建立的預留的 ReferenceType。</summary>
    public const string StockDocumentRef = "StockDocument";

    [BindProperty(SupportsGet = true)]
    public string? ItemCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    /// <summary>狀態篩選：Open（預設）、Consumed、Released、All。</summary>
    [BindProperty(SupportsGet = true, Name = "status")]
    public string? StatusText { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public CreateForm Create { get; set; } = new();

    [BindProperty]
    public string? IdempotencyKey { get; set; }

    public sealed class CreateForm
    {
        public string? ItemCode { get; set; }

        public Guid? WarehouseId { get; set; }

        public decimal Quantity { get; set; }

        public string? Note { get; set; }

        /// <summary>datetime-local 的值（當地時間）；空白＝不到期。</summary>
        public DateTime? ExpiresAt { get; set; }
    }

    public PagedResult<ReservationRow> Result { get; private set; } = null!;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    /// <summary>可作業的倉庫（null＝全部），建單下拉選單只列這些（M6-01b）。</summary>
    public IReadOnlySet<Guid>? Operable { get; private set; }

    public bool CanManage { get; private set; }

    public Actor CurrentActor { get; private set; } = null!;

    public string? Error { get; private set; }

    /// <summary>目前套用的狀態篩選；null＝全部。</summary>
    public ReservationStatus? StatusFilter =>
        string.Equals(StatusText, "All", StringComparison.OrdinalIgnoreCase) ? null
        : Enum.TryParse<ReservationStatus>(StatusText, true, out var s) ? s
        : ReservationStatus.Open;

    public string StatusValue => StatusFilter?.ToString() ?? "All";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        IdempotencyKey = Guid.NewGuid().ToString();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.ReservationManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            if (Create.WarehouseId is not { } warehouseId)
            {
                throw new DomainRuleException("請選擇倉庫。");
            }
            var input = new ReservationInput(
                Create.ItemCode ?? string.Empty, warehouseId, Create.Quantity, Create.Note, TaipeiTime.FromLocalInput(Create.ExpiresAt));
            await reservations.CreateAsync(input, IdempotencyKey ?? string.Empty, User.ToActor(), cancellationToken);
            TempData["Message"] = "已建立預留。";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.ReservationManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            await reservations.CancelAsync(id, User.ToActor(), cancellationToken);
            TempData["Message"] = "已取消預留。";
            return RedirectToPage(new { ItemCode, WarehouseId, status = StatusText, p = PageNumber });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    /// <summary>可否顯示取消鈕：有效、手動建立（非單據）、本人或主管。伺服器端另會再檢查。</summary>
    public bool CanCancel(ReservationRow r) =>
        CanManage && r.Status == ReservationStatus.Open && r.ReferenceType != StockDocumentRef
        && (CurrentActor.IsWarehouseManager || r.CreatedById == CurrentActor.Id);

    public string PageUrl(int page) => Url.Page("/Reservations/Index", new { ItemCode, WarehouseId, status = StatusValue, p = page })!;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        CurrentActor = User.ToActor();
        Warehouses = await warehouses.ListWarehousesAsync(includeDeleted: true, cancellationToken); // 歷史查詢可選已刪除的倉庫（新增用的選單另外篩掉停用）
        Operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        Result = await reservations.ListAsync(ItemCode, WarehouseId, StatusFilter, PageNumber, cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.ReservationManage)).Succeeded;
    }
}
