using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Admin.Users;

/// <summary>
/// 帳號列表與新增帳號（M6-01，限系統管理員；資料夾授權 Policies.UsersManage）。
/// 新增後直接在同一個回應顯示初始密碼（不轉址、不放 TempData），密碼不會寫進 Cookie 或任何紀錄。
/// </summary>
public class IndexModel(IUserAdminService admin) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Keyword { get; set; }

    [BindProperty(SupportsGet = true, Name = "inactive")]
    public bool IncludeInactive { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public CreateForm Input { get; set; } = new();

    public PagedResult<UserSummary> Result { get; private set; } = new([], 1, Paging.DefaultPageSize, 0);

    public UserPasswordResult? Created { get; private set; }

    public string? Error { get; private set; }

    public sealed class CreateForm
    {
        public string UserName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public List<string> Roles { get; set; } = [];
    }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            Created = await admin.CreateAsync(
                new CreateUserRequest(Input.UserName, Input.DisplayName, Input.Roles), User.ToActor(), cancellationToken);
            Input = new CreateForm();
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public string PageUrl(int page) =>
        $"/Admin/Users?q={Uri.EscapeDataString(Keyword ?? string.Empty)}&inactive={IncludeInactive.ToString().ToLowerInvariant()}&p={page}";

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        Result = await admin.ListAsync(Keyword, IncludeInactive, PageNumber, cancellationToken);
}
