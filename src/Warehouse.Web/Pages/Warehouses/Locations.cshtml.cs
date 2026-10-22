using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Web.Pages.Warehouses;

/// <summary>
/// 儲位查詢（任何登入者）與新增（倉庫主管）。查詢在伺服器端分頁；沒有條碼時就用這裡搜尋選擇儲位（ADR-0003）。
/// </summary>
public class LocationsModel(
    IWarehouseService warehouses, ISettingsService settings, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? WarehouseId { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Keyword { get; set; }

    [BindProperty(SupportsGet = true)]
    public LocationStatus? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public LocationForm Form { get; set; } = new();

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public PagedResult<LocationRow> Result { get; private set; } = null!;

    public SystemSettings Settings { get; private set; } = null!;

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    public WarehouseRow? CurrentWarehouse => Warehouses.FirstOrDefault(w => w.Id == WarehouseId);

    public Task OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            if (WarehouseId is not { } warehouseId)
            {
                throw new DomainRuleException("請先選擇倉庫。");
            }
            var id = await warehouses.CreateLocationAsync(
                warehouseId, new LocationInput(Form.Code, Form.ToDetails()), User.ToActor(), cancellationToken);
            TempData["Message"] = $"已新增儲位 {Form.Code.Trim()}。";
            return RedirectToPage(new { WarehouseId });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public string PageUrl(int page) => Url.Page("/Warehouses/Locations", new { WarehouseId, q = Keyword, Status, p = page })!;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Warehouses = await warehouses.ListWarehousesAsync(cancellationToken);
        Settings = await settings.GetAsync(cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded;
        Result = await warehouses.SearchLocationsAsync(
            new LocationSearch(WarehouseId, Keyword, Status, PageNumber), cancellationToken);
    }
}
