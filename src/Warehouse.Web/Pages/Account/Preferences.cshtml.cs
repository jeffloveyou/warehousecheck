using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Accounts;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.Account;

/// <summary>
/// 儲存登入者自己的常用設定（M8-06，ADR-0024）。prefs.js 在背景以 JSON 送出，帶 antiforgery 標頭（Razor Pages 自動驗證）；
/// 只能改自己的設定（使用者 ID 取自登入身分，不從請求讀）。
/// </summary>
[RequestSizeLimit(4096)] // 一項設定最多 500 字：不讓大請求先被完整解析（審查 Y4）
public class PreferencesModel(IUserPreferenceService preferences) : PageModel
{
    public sealed record PreferenceInput(string Key, string? Value);

    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync([FromBody] PreferenceInput? input, CancellationToken cancellationToken)
    {
        if (input is null || string.IsNullOrEmpty(input.Key))
        {
            return BadRequest();
        }
        try
        {
            await preferences.SetAsync(User.ToActor().Id, input.Key, input.Value, cancellationToken);
            return new NoContentResult();
        }
        catch (DomainRuleException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
