using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.WebEncoders;
using Warehouse.Api;
using Warehouse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Warehouse");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "未設定連線字串 ConnectionStrings:Warehouse。開發環境請用 dotnet user-secrets 設定，見 docs/04-開發手冊.md §2.1。");
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
