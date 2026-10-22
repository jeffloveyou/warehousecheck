using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.MasterData;

/// <summary>
/// 往來對象（廠商、客戶、部門）維護：任何登入者可看；新增、改名、停用／啟用限 PartnersManage。
/// 篩選（類型、關鍵字 q、含停用）與分頁（p）都在伺服器。代碼與類型建立後不可改；不刪除，只停用。
/// 寫入動作的表單帶著目前的篩選條件（隱藏欄位），完成後回到同一個清單位置。
/// </summary>
public class PartnersModel(IDocumentMasterDataService master, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public PartnerType? Type { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Keyword { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeInactive { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")] // 不能用 "page"：Razor Pages 把它當成頁面名稱
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "edit")]
    public Guid? EditId { get; set; }

    [BindProperty]
    public PartnerForm Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    public PagedResult<PartnerRow> Result { get; private set; } = null!;

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    public sealed class PartnerForm
    {
        public PartnerType Type { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (CanManage && EditId is { } id && Result.Items.FirstOrDefault(p => p.Id == id) is { } row)
        {
            Input = new PartnerForm { Type = row.Type, Code = row.Code, Name = row.Name };
            Version = row.Version;
        }
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async actor =>
        {
            if (EditId is { } id)
            {
                await master.RenamePartnerAsync(id, Version, Input.Name, actor, cancellationToken);
                return $"已修改 {Input.Code}。";
            }
            await master.CreatePartnerAsync(new PartnerInput(Input.Type, Input.Code, Input.Name), actor, cancellationToken);
            return $"已新增 {Input.Code}。";
        }, cancellationToken);

    public Task<IActionResult> OnPostToggleAsync(Guid id, uint version, bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async actor =>
        {
            await master.SetPartnerActiveAsync(id, version, isActive, actor, cancellationToken);
            return isActive ? "已啟用。" : "已停用。";
        }, cancellationToken);

    public string PageUrl(int page) => Url.Page("/MasterData/Partners", new { Type, q = Keyword, IncludeInactive, p = page })!;

    public string EditUrl(Guid id) => Url.Page("/MasterData/Partners", new { Type, q = Keyword, IncludeInactive, p = Result.Page, edit = id })!;

    private async Task<IActionResult> RunAsync(Func<Actor, Task<string>> action, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.PartnersManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action(User.ToActor());
            return RedirectToPage(new { Type, q = Keyword, IncludeInactive, p = PageNumber });
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
        Result = await master.ListPartnersAsync(Type, Keyword, IncludeInactive, PageNumber, cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.PartnersManage)).Succeeded;
    }
}
