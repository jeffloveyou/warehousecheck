namespace Poc.BlazorSsr;

/// <summary>PoC 的登入檢查：只看 cookie poc_user 是否存在（不是正式驗證機制）。</summary>
public static class PocAuth
{
    public const string CookieName = "poc_user";

    public static string? GetUser(HttpContext? context)
    {
        var value = context?.Request.Cookies[CookieName];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
