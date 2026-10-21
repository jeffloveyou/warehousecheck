using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.WebEncoders;
using Warehouse.Api;
using Warehouse.Infrastructure;
using Warehouse.Web;

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
builder.Services.AddRazorPages();
// Razor 預設把中文等非 ASCII 字元輸出成 &#x....; 實體；允許全部 Unicode 範圍可直接輸出 UTF-8，頁面較小、原始碼可讀。
// 仍會編碼 < > & " 等 HTML 特殊字元，不影響 XSS 防護。
builder.Services.Configure<WebEncoderOptions>(options =>
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseStaticFiles();

app.MapWarehouseApi();
app.MapRazorPages();

app.Run();

// 供整合測試的 WebApplicationFactory<Program> 使用
public partial class Program;
