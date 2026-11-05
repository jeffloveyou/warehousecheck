using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
using Warehouse.Domain.Items;
using Warehouse.Api.Authorization;

namespace Warehouse.Web.Pages.Items;

/// <summary>選取已啟用物料補印標籤；搜尋與數量限制在伺服器執行，沿用正式掃碼格式。</summary>
[Authorize(Policy = Policies.ItemsRead)]
public class LabelsModel(IItemQueryService items, IQrCodeRenderer qr, ISettingsService settings) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")] public string? Keyword { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;
    [BindProperty] public List<Guid> SelectedIds { get; set; } = [];
    public PagedResult<ItemListRow> Result { get; private set; } = null!;
    public IReadOnlyList<(ItemLabelRow Item, string Svg)> Labels { get; private set; } = [];
    public string CompanyName { get; private set; } = "";
    public string? Error { get; private set; }

    public Task OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public async Task OnPostPrintAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (SelectedIds.Count is 0 or > 100)
        {
            Error = "請選取 1 至 100 個物料。";
            return;
        }
        var rows = await items.LabelsAsync(SelectedIds, cancellationToken);
        if (rows.Count != SelectedIds.Distinct().Count())
        {
            Error = "部分物料不存在、尚未核准或已停用，請重新搜尋並核對。此次沒有產生標籤。";
            return;
        }
        Labels = rows.Select(i => (i, qr.RenderSvg("WH:ITEM:" + i.ItemCode))).ToList();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Result = await items.SearchAsync(new(Keyword, ItemStatus.Active, null, PageNumber, 20), cancellationToken);
        CompanyName = (await settings.GetAsync(cancellationToken)).PrintCompanyName;
    }

    public string PageUrl(int page) => Url.Page("/Items/Labels", new { q = Keyword, p = page })!;
}
