using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 模擬瀏覽器操作網站：保留 Cookie、不自動轉址（測試要看 302 去哪裡）、自動帶 antiforgery token 送表單。
/// 基底網址用 https，因為登入 Cookie 設定為 Secure，http 下不會送出。
/// </summary>
public sealed partial class WebSession(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public static WebSession Create(WarehouseWebFactory factory) =>
        new(factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        }));

    /// <summary>建立帳號（直接用 UserManager，不經命令列），回傳使用者 ID。</summary>
    public static async Task<Guid> CreateUserAsync(
        WarehouseWebFactory factory, string userName, string password, string role, bool mustChangePassword = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName, DisplayName = userName + " 顯示名", MustChangePassword = mustChangePassword };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }

    /// <summary>登入；回傳登入表單送出後的回應（成功時是 302）。</summary>
    public Task<HttpResponseMessage> LoginAsync(string userName, string password) =>
        PostFormAsync("/Account/Login", "/Account/Login", new Dictionary<string, string>
        {
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
        });

    /// <summary>先 GET 表單頁取得 antiforgery token，再 POST 到 <paramref name="postUrl"/>。</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string formPageUrl, string postUrl, IDictionary<string, string> fields)
    {
        var page = await Client.GetAsync(formPageUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var token = TokenPattern().Match(html);
        Assert.True(token.Success, $"{formPageUrl} 找不到 antiforgery token");

        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) };
        return await Client.PostAsync(postUrl, new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    public async Task<string> GetStringAsync(string url)
    {
        var response = await Client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
