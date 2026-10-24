using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Web.Pages.Warehouses;

/// <summary>
/// 儲位 QR 標籤列印（M7-03，倉庫主管；頁面授權 Policies.WarehousesManage，ADR-0020）。
/// 條件同儲位查詢（倉庫、關鍵字、狀態），每頁最多 4 張 A4，用瀏覽器列印。QR 內容是 <see cref="LocationRow.QrContent"/>
/// （WH:LOC:倉庫:儲位，ScanCodeParser 解析），下方印倉庫與儲位碼、名稱，沒有掃碼設備時也能人工辨識。
/// </summary>
public class LabelsModel(IWarehouseService warehouses, IQrCodeRenderer qr, ISettingsService settings) : PageModel
{
    /// <summary>標籤版型：A4 每張的欄數與列數（3×8 小標籤貼層架、2×5 大標籤貼儲位牌）。</summary>
    public static readonly IReadOnlyDictionary<string, (int Columns, int Rows, string Label)> Layouts =
        new Dictionary<string, (int, int, string)>
        {
            ["small"] = (3, 8, "小（A4 每張 24 個，3×8）"),
            ["large"] = (2, 5, "大（A4 每張 10 個，2×5）"),
        };

    /// <summary>一次最多 4 張 A4，超過用分頁（每頁分開列印）。</summary>
    public const int SheetsPerPage = 4;

    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Keyword { get; set; }

    [BindProperty(SupportsGet = true)]
    public LocationStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Layout { get; set; } = "small";

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public PagedResult<LocationRow>? Result { get; private set; }

    /// <summary>每個標籤：儲位與它的 QR SVG。</summary>
    public IReadOnlyList<(LocationRow Location, string Svg)> Labels { get; private set; } = [];

    public (int Columns, int Rows, string Label) CurrentLayout => Layouts[Layout];

    public int PerSheet => CurrentLayout.Columns * CurrentLayout.Rows;

    public string CompanyName { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!Layouts.ContainsKey(Layout))
        {
            Layout = "small";
        }

        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        CompanyName = (await settings.GetAsync(cancellationToken)).PrintCompanyName;
        if (WarehouseId is null)
        {
            return; // 先選倉庫，避免一次列出所有倉庫的儲位
        }

        Result = await warehouses.SearchLocationsAsync(
            new LocationSearch(WarehouseId, Keyword, Status, PageNumber, PerSheet * SheetsPerPage), cancellationToken);
        Labels = Result.Items.Select(l => (l, qr.RenderSvg(l.QrContent))).ToList();
    }

    public string PageUrl(int page) =>
        $"/Warehouses/Labels?WarehouseId={WarehouseId}&q={Uri.EscapeDataString(Keyword ?? "")}&Status={Status}&Layout={Layout}&p={page}";
}
