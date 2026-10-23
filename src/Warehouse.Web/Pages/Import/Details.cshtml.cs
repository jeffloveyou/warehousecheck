using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Import;

/// <summary>
/// 匯入試算結果與確認（M6-03）：只有上傳者（或系統管理員檢視）看得到。沒有錯誤、24 小時內、上傳者本人才能確認；
/// 確認時再依原本的權限檢查一次。重複按「確認匯入」不會匯入兩次。
/// </summary>
public class DetailsModel(IImportService imports, IAuthorizationService authorization) : PageModel
{
    /// <summary>畫面最多列出的問題筆數（其餘請修正後重新上傳）。</summary>
    public const int MaxIssues = 500;

    public ImportBatchView Batch { get; private set; } = null!;

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : NotFound();

    public async Task<IActionResult> OnPostCommitAsync(Guid id, [FromServices] ImportGate gate, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }
        if (!(await authorization.AuthorizeAsync(User, IndexModel.PolicyOf(Batch.Kind))).Succeeded)
        {
            return Forbid();
        }
        using var slot = await gate.TryEnterAsync();
        try
        {
            if (slot is null)
            {
                throw new ConflictException("import.busy", "目前有其他人正在匯入，請稍等約一分鐘再試。");
            }
            var rows = await imports.CommitAsync(id, User.ToActor(), cancellationToken);
            TempData["Message"] = $"已匯入 {rows} 列。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var batch = await imports.GetAsync(id, User.ToActor(), User.IsInRole(Roles.SystemAdmin), MaxIssues, cancellationToken);
        if (batch is null)
        {
            return false;
        }
        Batch = batch;
        return true;
    }
}
