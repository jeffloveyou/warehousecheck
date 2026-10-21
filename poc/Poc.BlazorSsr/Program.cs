using Poc.BlazorSsr.Components;

// M0-08 Blazor 靜態 SSR PoC：只用 AddRazorComponents()，不啟用任何互動渲染模式、不使用 SignalR。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// PoC 只用 http：不呼叫 UseHttpsRedirection / UseHsts。

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/healthz", () => "ok");

// /scan-js 變體使用的料號查詢 API（與 Razor Pages PoC 相同）。
app.MapGet("/api/items/{code}", (string code) =>
{
    var item = Poc.Shared.FakeItemCatalog.FindByCode(code);
    return item is null
        ? Results.NotFound()
        : Results.Json(new { itemCode = item.ItemCode, name = item.Name });
});
app.MapRazorComponents<App>();

app.Run();
