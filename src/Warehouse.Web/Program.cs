using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.WebEncoders;
using Warehouse.Api;
using Warehouse.Api.Authorization;
using Warehouse.Infrastructure;
using Warehouse.Web;
using Warehouse.Web.Security;

// 以 Windows 服務執行時，目前目錄是 C:\Windows\System32，必須把內容根目錄設為程式所在目錄才找得到 wwwroot
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.AddExternalConfigDirectory();
// 不是以 Windows 服務／systemd 服務啟動時（開發、測試）不會有任何作用
builder.Services.AddWindowsService(options => options.ServiceName = "Warehouse");
builder.Services.AddSystemd();

var connectionString = builder.Configuration.GetConnectionString("Warehouse");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "未設定連線字串 ConnectionStrings:Warehouse。開發環境請用 dotnet user-secrets 設定；"
        + "伺服器請放在 WAREHOUSE_CONFIG_DIR 目錄的 appsettings.Production.json，見 docs/04-開發手冊.md §2.1、§10。");
}

builder.Services.AddProblemDetails();
builder.Services.AddWarehouseInfrastructure(connectionString);
builder.Services.AddWarehouseApi();
builder.Services.AddWarehouseIdentity();
builder.Services.AddRazorPages(options =>
{
    // 預設全部頁面要登入（FallbackPolicy）；以下明確開放或要求特定權限（ADR-0012）
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToFolder("/Dev");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AuthorizeFolder("/Items", Policies.ItemsRead);
    options.Conventions.AuthorizePage("/Items/Create", Policies.ItemsSubmit);
    options.Conventions.AuthorizePage("/Items/Edit", Policies.ItemsSubmit);
    options.Conventions.AuthorizeFolder("/MasterData", Policies.MasterDataManage);
    options.Conventions.AuthorizeFolder("/Search", Policies.ItemsRead);
    options.Conventions.AuthorizePage("/Search/AddIndex", Policies.ItemsIndex);
    options.Conventions.AuthorizeFolder("/Warehouses", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Settings", Policies.SettingsManage);
});
// Razor 預設把中文等非 ASCII 字元輸出成 &#x....; 實體；允許全部 Unicode 範圍可直接輸出 UTF-8，頁面較小、原始碼可讀。
// 仍會編碼 < > & " 等 HTML 特殊字元，不影響 XSS 防護。
builder.Services.Configure<WebEncoderOptions>(options =>
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.AddSharedDataProtection();
// 反向代理（Warehouse.Proxy）轉送時帶 X-Forwarded-For／Proto；預設只信任 loopback 來源的代理
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

// 帳號管理命令列（users create …），執行完就結束，不啟動網站
if (await UserCommands.TryRunAsync(app, args))
{
    return;
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseStaticFiles();
app.UseAuthentication();
app.UseMustChangePassword();
app.UseAuthorization();
app.UseRateLimiter();

app.MapWarehouseApi();
app.MapRazorPages();

app.Run();

// 供整合測試的 WebApplicationFactory<Program> 使用
public partial class Program;
