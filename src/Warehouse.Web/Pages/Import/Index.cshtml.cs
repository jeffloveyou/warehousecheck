using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Exports;
using Warehouse.Application.Imports;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Web.Security;

namespace Warehouse.Web.Pages.Import;

/// <summary>
/// 匯入（M6-03）：選種類 → 下載範本 → 上傳 xlsx／UTF-8 CSV → 系統試算後轉到結果頁確認。
/// 每種資料要有對應的權限（與畫面上新增同一種資料相同）；倉庫範圍等規則由服務在試算時逐列檢查。
/// 期初單明細從期初單草稿頁進入（帶 target＝期初單 ID）；儲位要選倉庫。
/// </summary>
// 上傳上限在 Kestrel 接收前就生效（不先收下 30 MB 再檢查，審查 🟡-11）
[RequestSizeLimit(IImportService.MaxBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = IImportService.MaxBytes + 1024 * 1024)]
public class IndexModel(IImportService imports, IWarehouseService warehouses, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public ImportKind Kind { get; set; } = ImportKind.Items;

    [BindProperty(SupportsGet = true)]
    public Guid? Target { get; set; }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public IReadOnlySet<Guid>? Operable { get; private set; }

    public IReadOnlyList<(string Name, bool Required, string Note)> Columns => imports.Columns(Kind);

    public bool Allowed { get; private set; }

    public string? Error { get; private set; }

    /// <summary>每種資料需要的權限（與畫面上新增同一種資料相同）。</summary>
    public static string PolicyOf(ImportKind kind) => kind switch
    {
        ImportKind.Items => Policies.ItemsSubmit,
        ImportKind.Indexes => Policies.ItemsIndex,
        ImportKind.Locations => Policies.WarehousesManage,
        ImportKind.OpeningLines => Policies.OpeningEdit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string LabelOf(ImportKind kind) => kind switch
    {
        ImportKind.Items => "物料主檔（成為待核准物料）",
        ImportKind.Indexes => "別名與共用關鍵字",
        ImportKind.Locations => "儲位",
        _ => "期初單明細",
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(Kind))
        {
            return BadRequest();
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnGetTemplateAsync([FromServices] ExportGate gate, CancellationToken cancellationToken) =>
        !Enum.IsDefined(Kind) ? BadRequest()
            : await gate.RunAsync(this, () => imports.TemplateAsync(Kind, cancellationToken), Url.Page("/Import/Index", new { Kind })!);

    public async Task<IActionResult> OnPostAsync([FromServices] ImportGate gate, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(Kind))
        {
            return BadRequest();
        }
        if (!(await authorization.AuthorizeAsync(User, PolicyOf(Kind))).Succeeded)
        {
            return Forbid();
        }
        using var slot = await gate.TryEnterAsync();
        try
        {
            if (slot is null)
            {
                throw new ConflictException("import.busy", "目前有其他人正在匯入，請稍等約一分鐘再試。");
            }
            if (Upload is null || Upload.Length == 0)
            {
                throw new DomainRuleException("請選擇要上傳的檔案。");
            }
            if (Upload.Length > IImportService.MaxBytes)
            {
                throw new ConflictException("import.too_large", $"檔案超過 {IImportService.MaxBytes / 1024 / 1024} MB，請分成多個檔案。");
            }
            await using var stream = Upload.OpenReadStream();
            var id = await imports.UploadAsync(Kind, Target, Upload.FileName, stream, User.ToActor(), cancellationToken);
            return RedirectToPage("/Import/Details", new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Allowed = (await authorization.AuthorizeAsync(User, PolicyOf(Kind))).Succeeded;
        if (Kind == ImportKind.Locations)
        {
            Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
            Operable = await HttpContext.OperableWarehousesAsync(cancellationToken);
        }
    }
}
