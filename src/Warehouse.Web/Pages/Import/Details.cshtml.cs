using Microsoft.AspNetCore.Authorization;
using System.Text;
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
    /// <summary>畫面最多列出的問題筆數；完整問題以 CSV 串流下載。</summary>
    public const int MaxIssues = 500;

    public ImportBatchView Batch { get; private set; } = null!;

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : NotFound();

    public async Task<IActionResult> OnGetIssuesAsync(Guid id, CancellationToken cancellationToken)
    {
        var batch = await imports.GetAsync(id, User.ToActor(), User.IsInRole(Roles.SystemAdmin), 0, cancellationToken);
        if (batch is null) return NotFound();
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=import-issues-{id:N}.csv";
        Response.Headers.CacheControl = "no-store";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(true), 4096, leaveOpen: true);
        await writer.WriteLineAsync("來源列,類型,說明".AsMemory(), cancellationToken);
        await foreach (var issue in imports.ReadIssuesAsync(id, User.ToActor(), User.IsInRole(Roles.SystemAdmin), cancellationToken))
            await writer.WriteLineAsync($"{issue.RowNo},{(issue.IsError ? "錯誤" : "警告")},{CsvCell(issue.Message)}".AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        return new EmptyResult();
    }

    /// <summary>引號與公式前綴都處理，避免用試算表開啟報告時執行公式。</summary>
    internal static string CsvCell(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

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
