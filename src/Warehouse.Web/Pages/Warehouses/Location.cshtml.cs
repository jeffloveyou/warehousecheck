using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Web.Pages.Warehouses;

/// <summary>儲位明細：QR 內容、屬性、圖片；倉庫主管可修改、變更狀態、上傳與停用圖片（表單帶 xmin 版本）。</summary>
[RequestSizeLimit(12 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
public class LocationModel(
    IWarehouseService warehouses, ISettingsService settings, IAuthorizationService authorization,
    ILocationImageService images) : PageModel
{
    public IReadOnlyList<LocationImageRow> Images { get; private set; } = [];

    [BindProperty]
    public IFormFile? Photo { get; set; }

    public LocationRow Location { get; private set; } = null!;

    public SystemSettings Settings { get; private set; } = null!;

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    [BindProperty]
    public LocationForm Form { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public LocationStatus NewStatus { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }
        Form = LocationForm.From(Location);
        Version = Location.Version;
        return Page();
    }

    public Task<IActionResult> OnPostUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async () =>
        {
            var original = await warehouses.GetLocationAsync(id, cancellationToken)
                ?? throw new NotFoundException("location.not_found", "找不到儲位。");
            var details = Form.KeepHidden(original).ToDetails();
            await warehouses.UpdateLocationAsync(id, Version, details, User.ToActor(), cancellationToken);
            return "已儲存。";
        }, cancellationToken);

    public Task<IActionResult> OnPostUploadAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async () =>
        {
            if (Photo is null)
            {
                throw new DomainRuleException("請選擇圖片檔案。");
            }
            await using var stream = Photo.OpenReadStream();
            await images.UploadAsync(id, stream, Photo.Length, User.ToActor(), cancellationToken);
            return "已上傳圖片。";
        }, cancellationToken);

    public Task<IActionResult> OnPostDeactivateImageAsync(Guid id, Guid imageId, CancellationToken cancellationToken) =>
        RunAsync(id, async () =>
        {
            await images.DeactivateAsync(id, imageId, User.ToActor(), cancellationToken);
            return "已停用圖片。";
        }, cancellationToken);

    public Task<IActionResult> OnPostStatusAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, async () =>
        {
            await warehouses.ChangeLocationStatusAsync(id, Version, NewStatus, User.ToActor(), cancellationToken);
            return $"狀態已改為「{Display.Of(NewStatus)}」。";
        }, cancellationToken);

    private async Task<IActionResult> RunAsync(Guid id, Func<Task<string>> action, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action();
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        return await LoadAsync(id, cancellationToken) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await warehouses.GetLocationAsync(id, cancellationToken) is not { } location)
        {
            return false;
        }
        Location = location;
        Settings = await settings.GetAsync(cancellationToken);
        // 關閉圖片功能也要讀：既有圖片仍可檢視（M6-05）
        Images = await images.ListAsync(id, cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded;
        return true;
    }
}
