using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Errors;
using Warehouse.Application.Exports;
using Warehouse.Domain;

namespace Warehouse.Web;

/// <summary>
/// 匯出的同時數限制（M6-03）：整個網站同時最多 <c>Export:MaxConcurrent</c> 個（預設 2），滿了回 429，
/// 避免多人同時匯出 5 萬列吃光 NAS／舊電腦的 CPU 與記憶體。單一實例計數（A/B 各自計算）。
/// </summary>
public sealed class ExportGate(IConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim slots = new(Math.Max(1, configuration.GetValue("Export:MaxConcurrent", 2)));

    /// <summary>
    /// 在頁面的 GET handler 執行匯出：成功回傳 xlsx；忙碌回 429；條件太大或輸入錯誤時回到原頁（<paramref name="back"/>）並顯示訊息。
    /// </summary>
    public async Task<IActionResult> RunAsync(PageModel page, Func<Task<ExportFile>> export, string back)
    {
        if (!await slots.WaitAsync(0))
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
                ContentType = "text/html; charset=utf-8",
                Content = $"<p>目前有其他人正在匯出，請稍等約一分鐘再試。</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(back)}\">回上一頁</a></p>",
            };
        }
        try
        {
            var file = await export();
            // FileStreamResult 送完後關閉串流（暫存檔隨之刪除）
            return new FileStreamResult(file.Content, ExportFile.ContentType) { FileDownloadName = file.FileName };
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException and not PermissionDeniedException)
        {
            page.TempData["ExportError"] = ex.Message;
            return new LocalRedirectResult(back);
        }
        finally
        {
            slots.Release();
        }
    }

    public void Dispose() => slots.Dispose();
}
