using Poc.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

var app = builder.Build();

// PoC 只用 http：刻意不呼叫 UseHttpsRedirection／UseHsts。
app.UseStaticFiles();

// 登入檢查：需登入的頁面沒有 cookie poc_user 一律導向 /login。
// PoC 用單一 middleware 比每頁各自檢查簡單；cookie 值不做任何驗證（假登入）。
var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/", "/index", "/search", "/receipt", "/scan"
};
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (protectedPaths.Contains(path) && !context.Request.Cookies.ContainsKey("poc_user"))
    {
        context.Response.Redirect("/login");
        return;
    }

    await next();
});

app.MapGet("/healthz", () => Results.Text("ok"));

// 掃碼頁的 scan.js 以 fetch 呼叫；找不到回 404。
app.MapGet("/api/items/{code}", (string code) =>
{
    var item = FakeItemCatalog.FindByCode(code);
    return item is null
        ? Results.NotFound()
        : Results.Json(new { itemCode = item.ItemCode, name = item.Name });
});

app.MapRazorPages();

app.Run();
