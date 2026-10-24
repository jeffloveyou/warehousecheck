using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Errors;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Warehouses;

/// <summary>
/// 倉庫清單（任何登入者可看）。新增、改名、停用只限倉庫主管：表單依權限顯示，
/// handler 另以 <see cref="IAuthorizationService"/> 檢查 Warehouses.Manage（只隱藏表單擋不住直接送出）。
/// </summary>
public class IndexModel(IWarehouseService warehouses, IAuthorizationService authorization) : PageModel
{
    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    [BindProperty(SupportsGet = true, Name = "edit")]
    public Guid? EditId { get; set; }

    /// <summary>?delete=：顯示刪除確認區（伺服器端兩步確認，不用瀏覽器對話框）。</summary>
    [BindProperty(SupportsGet = true, Name = "delete")]
    public Guid? DeleteId { get; set; }

    /// <summary>?deleted=true：連已刪除的倉庫一起列出（可還原）。</summary>
    [BindProperty(SupportsGet = true, Name = "deleted")]
    public bool ShowDeleted { get; set; }

    public WarehouseRow? DeleteTarget => DeleteId is { } id ? Warehouses.FirstOrDefault(w => w.Id == id && !w.IsDeleted) : null;

    [BindProperty]
    [Required(ErrorMessage = "請輸入倉庫代碼。")]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "請輸入倉庫名稱。")]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public uint Version { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (EditId is { } id && Warehouses.FirstOrDefault(w => w.Id == id) is { } row)
        {
            (Code, Name, Version) = (row.Code, row.Name, row.Version);
        }
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (EditId is { } id)
            {
                await warehouses.RenameWarehouseAsync(id, Version, Name, User.ToActor(), cancellationToken);
                return $"已修改倉庫 {Code}。";
            }
            await warehouses.CreateWarehouseAsync(new WarehouseInput(Code, Name), User.ToActor(), cancellationToken);
            return $"已新增倉庫 {Code.Trim()}。";
        }, cancellationToken);

    public Task<IActionResult> OnPostToggleAsync(Guid id, uint version, bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await warehouses.SetWarehouseActiveAsync(id, version, isActive, User.ToActor(), cancellationToken);
            return isActive ? "已啟用倉庫。" : "已停用倉庫。";
        }, cancellationToken);

    public Task<IActionResult> OnPostDeleteAsync(Guid id, uint version, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var result = await warehouses.DeleteWarehouseAsync(id, version, User.ToActor(), cancellationToken);
            return result == WarehouseDeleteResult.Removed
                ? "已刪除倉庫（從未使用過，連同儲位一起刪除）。"
                : "已刪除倉庫：歷史單據與異動仍保留，可在「顯示已刪除」還原。";
        }, cancellationToken);

    public Task<IActionResult> OnPostRestoreAsync(Guid id, uint version, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await warehouses.RestoreWarehouseAsync(id, version, User.ToActor(), cancellationToken);
            return "已還原倉庫（目前是停用，確認無誤再啟用）。";
        }, cancellationToken);

    private async Task<IActionResult> RunAsync(Func<Task<string>> action, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action();
            return RedirectToPage();
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
        Warehouses = await warehouses.ListWarehousesAsync(ShowDeleted, cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.WarehousesManage)).Succeeded;
    }
}
