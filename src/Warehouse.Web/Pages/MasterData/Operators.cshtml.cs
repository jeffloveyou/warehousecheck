using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.MasterData;

/// <summary>
/// 人員名單維護（ADR-0030）：公用帳號送出進出倉動作時從這裡選操作人。任何登入者可看；
/// 新增、改名與排序、停用／啟用限 PartnersManage（倉庫主管、物料管理員）。不刪除，只停用（已寫入的紀錄保留原姓名）。
/// </summary>
public class OperatorsModel(IDocumentMasterDataService master, IAuthorizationService authorization) : PageModel
{
    public IReadOnlyList<OperatorNameRow> Operators { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = "edit")]
    public Guid? EditId { get; set; }

    [BindProperty]
    public OperatorForm Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    public sealed class OperatorForm
    {
        public string Name { get; set; } = string.Empty;

        public int SortOrder { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (CanManage && EditId is { } id && Operators.FirstOrDefault(o => o.Id == id) is { } row)
        {
            Input = new OperatorForm { Name = row.Name, SortOrder = row.SortOrder };
            Version = row.Version;
        }
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async actor =>
        {
            if (EditId is { } id)
            {
                await master.UpdateOperatorNameAsync(id, Version, Input.Name, Input.SortOrder, actor, cancellationToken);
                return $"已修改人員 {Input.Name.Trim()}。";
            }
            await master.CreateOperatorNameAsync(Input.Name, Input.SortOrder, actor, cancellationToken);
            return $"已新增人員 {Input.Name.Trim()}。";
        }, cancellationToken);

    public Task<IActionResult> OnPostToggleAsync(Guid id, uint version, bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async actor =>
        {
            await master.SetOperatorNameActiveAsync(id, version, isActive, actor, cancellationToken);
            return isActive ? "已啟用人員。" : "已停用人員。";
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
        Operators = await master.ListOperatorNamesAsync(true, cancellationToken);
        CanManage = (await authorization.AuthorizeAsync(User, Policies.PartnersManage)).Succeeded;
    }
}
