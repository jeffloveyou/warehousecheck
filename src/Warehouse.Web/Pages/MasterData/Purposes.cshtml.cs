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
/// 進出倉單用途類型維護：任何登入者可看；新增、改名與排序、停用／啟用限 PartnersManage（倉庫主管、物料管理員）。
/// 代碼與方向建立後不可改；不刪除，只停用（停用後新單據不能選，舊單據保留原值）。
/// </summary>
public class PurposesModel(IDocumentMasterDataService master, IAuthorizationService authorization) : PageModel
{
    public IReadOnlyList<PurposeRow> Purposes { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = "edit")]
    public Guid? EditId { get; set; }

    [BindProperty]
    public PurposeForm Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    public sealed class PurposeForm
    {
        public PurposeDirection Direction { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int SortOrder { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (CanManage && EditId is { } id && Purposes.FirstOrDefault(p => p.Id == id) is { } row)
        {
            Input = new PurposeForm { Direction = row.Direction, Code = row.Code, Name = row.Name, SortOrder = row.SortOrder };
            Version = row.Version;
        }
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async actor =>
        {
            if (EditId is { } id)
            {
                await master.UpdatePurposeAsync(id, Version, Input.Name, Input.SortOrder, actor, cancellationToken);
                return $"已修改用途 {Input.Code}。";
            }
            await master.CreatePurposeAsync(new PurposeInput(Input.Direction, Input.Code, Input.Name, Input.SortOrder), actor, cancellationToken);
            return $"已新增用途 {Input.Code}。";
        }, cancellationToken);

    public Task<IActionResult> OnPostToggleAsync(Guid id, uint version, bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async actor =>
        {
            await master.SetPurposeActiveAsync(id, version, isActive, actor, cancellationToken);
            return isActive ? "已啟用用途。" : "已停用用途。";
        }, cancellationToken);

    private async Task<IActionResult> RunAsync(Func<Actor, Task<string>> action, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.PartnersManage)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action(User.ToActor());
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
        Purposes = await master.ListPurposesAsync(null, true, cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.PartnersManage)).Succeeded;
    }
}
