using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Warehouse.Web.Pages.Dev;

/// <summary>
/// /dev/scanner：M0-05 條碼槍（USB HID）與 PDA（Keyboard Wedge）連續掃碼測試頁。
/// 掃碼處理與之後 M5 正式掃碼相同：Enter／Tab 時取值、立刻清空輸入框，再交給 JS 佇列依序處理，
/// 所以下一筆的字元不會被吃掉（docs/04 §9.1）。測完按「送出結果」，摘要寫進伺服器 log，開發者直接讀 log。
/// </summary>
[RequestSizeLimit(256_000)]
public class ScannerModel(IWebHostEnvironment environment, ILogger<ScannerModel> logger) : DevPageModel(environment)
{
    /// <summary>
    /// 預設 50 筆測試碼，與列印用的 QR 測試單相同（產生測試單的工具用 ?handler=Codes 取得）。
    /// 含系統 QR 格式、純料號、批號／序號、長儲位碼，以及 5 筆中文（Keyboard Wedge 常把中文送成亂碼，要實測）。
    /// </summary>
    public static IReadOnlyList<string> TestCodes { get; } =
    [
        .. Enumerable.Range(101, 10).Select(n => $"WH:ITEM:EL-{n:000000}"),
        .. Enumerable.Range(1, 10).Select(n => $"WH:LOC:W1:A-01-{n:00}"),
        .. Enumerable.Range(201, 15).Select(n => $"{(n % 3) switch { 0 => "EL", 1 => "ME", _ => "RM" }}-{n:000000}"),
        .. Enumerable.Range(1, 5).Select(n => $"LOT-20261022-{n:0000}"),
        .. Enumerable.Range(1, 5).Select(n => $"SN-ABCD{n:00000000}"),
        "WH:ITEM:螺絲M3", "WH:LOC:W1:冷藏-01", "伺服AMP", "批號甲-01", "序號乙-02",
    ];

    public void OnGet()
    {
    }

    public IActionResult OnGetCodes() => Content(string.Join('\n', TestCodes), "text/plain; charset=utf-8");

    /// <summary>模擬查詢：回傳原值，可加延遲（最多 1 秒）模擬網路與伺服器處理時間，檢查佇列在慢回應下是否掉碼。</summary>
    public async Task<IActionResult> OnGetEchoAsync(string? code, int delay, CancellationToken cancellationToken)
    {
        await Task.Delay(Math.Clamp(delay, 0, 1000), cancellationToken);
        return new JsonResult(new { code });
    }

    public IActionResult OnPostReport([FromBody] JsonElement report)
    {
        logger.LogInformation("M0-05 掃碼測試結果 {Report}", report.GetRawText());
        return new JsonResult(new { saved = true });
    }
}
