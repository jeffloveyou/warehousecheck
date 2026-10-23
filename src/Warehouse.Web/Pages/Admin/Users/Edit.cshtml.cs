using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Web.Pages.Admin.Users;

/// <summary>
/// 單一帳號的管理：顯示名稱與角色、停用／啟用、重設密碼、解除鎖定（M6-01，限系統管理員）。
/// 規則都在 <see cref="IUserAdminService"/>（不能停用自己、至少保留一個系統管理員等），畫面只負責顯示錯誤。
/// </summary>
public class EditModel(
    IUserAdminService admin, IWarehouseService warehouseService, UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public EditForm Input { get; set; } = new();

    public UserSummary? Account { get; private set; }

    public UserPasswordResult? NewPassword { get; private set; }

    public string? Error { get; private set; }

    public sealed class EditForm
    {
        public string DisplayName { get; set; } = string.Empty;

        public List<string> Roles { get; set; } = [];

        public string ConcurrencyStamp { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }
        Input = new EditForm { DisplayName = Account!.DisplayName, Roles = [.. Account.Roles], ConcurrencyStamp = Account.ConcurrencyStamp };
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await admin.UpdateAsync(new UpdateUserRequest(Id, Input.DisplayName, Input.Roles, Input.ConcurrencyStamp),
                User.ToActor(), cancellationToken);
            if (Id == User.ToActor().Id && await users.FindByIdAsync(Id.ToString()) is { } self)
            {
                // 改的是自己：立刻換新 Cookie（新顯示名稱、角色），不必等 5 分鐘後被登出（M6-01a 審查 Y-4）
                await signIn.RefreshSignInAsync(self);
            }
            return "已儲存。";
        }, cancellationToken);

    public Task<IActionResult> OnPostActiveAsync(bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await admin.SetActiveAsync(Id, isActive, User.ToActor(), cancellationToken);
            return isActive ? "已啟用帳號。" : "已停用帳號（對方已登入的畫面最慢 5 分鐘內失效）。";
        }, cancellationToken);

    [BindProperty]
    public bool AllWarehouses { get; set; }

    [BindProperty]
    public List<Guid> WarehouseIds { get; set; } = [];

    public IReadOnlyList<WarehouseRow> Warehouses { get; private set; } = [];

    public Task<IActionResult> OnPostWarehousesAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await admin.SetWarehousesAsync(Id, AllWarehouses, WarehouseIds, User.ToActor(), cancellationToken);
            return "已更新可作業的倉庫（立即生效）。";
        }, cancellationToken);

    public Task<IActionResult> OnPostUnlockAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await admin.UnlockAsync(Id, User.ToActor(), cancellationToken);
            return "已解除鎖定。";
        }, cancellationToken);

    /// <summary>重設密碼：新密碼只在這個回應顯示一次（不轉址）。</summary>
    public async Task<IActionResult> OnPostResetPasswordAsync(CancellationToken cancellationToken)
    {
        try
        {
            NewPassword = await admin.ResetPasswordAsync(Id, User.ToActor(), cancellationToken);
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }
        Input = new EditForm { DisplayName = Account!.DisplayName, Roles = [.. Account.Roles], ConcurrencyStamp = Account.ConcurrencyStamp };
        return Page();
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action, CancellationToken cancellationToken)
    {
        try
        {
            TempData["Message"] = await action();
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }
            if (ex is ConcurrencyConflictException)
            {
                // 被別人改過：表單換成目前的資料，請使用者看過再改（不會用舊內容蓋掉別人的修改）
                Input = new EditForm { DisplayName = Account!.DisplayName, Roles = [.. Account.Roles], ConcurrencyStamp = Account.ConcurrencyStamp };
            }
            return Page();
        }
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Account = await admin.GetAsync(Id, cancellationToken);
            Warehouses = await warehouseService.ListWarehousesAsync(cancellationToken);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }
}
