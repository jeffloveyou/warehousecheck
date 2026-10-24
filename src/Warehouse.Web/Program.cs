using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.WebEncoders;
using Warehouse.Api;
using Warehouse.Api.Authorization;
using Warehouse.Infrastructure;
using Warehouse.Web;
using Warehouse.Web.Logging;
using Warehouse.Web.Security;

// 以 Windows 服務執行時，目前目錄是 C:\Windows\System32，必須把內容根目錄設為程式所在目錄才找得到 wwwroot
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.AddExternalConfigDirectory();
// 檔案日誌（M7-02）：安裝腳本設定 Logging__File__Directory 才啟用
builder.AddWarehouseFileLogging();
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

// 正式環境一定要明確設定附件目錄：預設的 %LOCALAPPDATA% 在服務帳號下 A/B 槽位各不相同、也不在備份範圍（M3-08 審查 O2）
var attachmentsDirectory = builder.Configuration["Storage:AttachmentsDirectory"];
if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(attachmentsDirectory))
{
    throw new InvalidOperationException(
        "未設定附件目錄 Storage:AttachmentsDirectory。請用安裝腳本設定環境變數 Storage__AttachmentsDirectory，"
        + "見 docs/04-開發手冊.md §15.4。");
}

builder.Services.AddProblemDetails();
builder.Services.AddWarehouseInfrastructure(connectionString, attachmentsDirectory);
builder.Services.AddWarehouseApi();
// 預留到期釋放（M4-07）：每 10 分鐘一次，A／B 兩個實例同時執行也安全（條件式更新）
builder.Services.AddHostedService<Warehouse.Web.Jobs.ReservationExpiryJob>();
// 資料保留清理（M7，ADR-0021）：每日一次，A／B 只有一個實例執行（advisory lock）
builder.Services.AddHostedService<Warehouse.Web.Jobs.DataRetentionJob>();
builder.Services.AddWarehouseIdentity();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<ExportGate>();
builder.Services.AddSingleton<ImportGate>();
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
    // 分類、單位限物料管理員；用途、往來對象任何登入者可看，寫入在 handler 內檢查 PartnersManage（倉庫主管也能維護）
    options.Conventions.AuthorizePage("/MasterData/Categories", Policies.MasterDataManage);
    options.Conventions.AuthorizePage("/MasterData/Units", Policies.MasterDataManage);
    options.Conventions.AuthorizePage("/MasterData/Purposes", Policies.StockRead);
    options.Conventions.AuthorizePage("/MasterData/Partners", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Documents", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Reservations", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Search", Policies.ItemsRead);
    options.Conventions.AuthorizePage("/Search/AddIndex", Policies.ItemsIndex);
    options.Conventions.AuthorizeFolder("/Warehouses", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Settings", Policies.SettingsManage);
    options.Conventions.AuthorizeFolder("/Admin/Users", Policies.UsersManage);
    options.Conventions.AuthorizeFolder("/Admin/Audit", Policies.AuditRead);
    options.Conventions.AuthorizeFolder("/Admin/System", Policies.SystemStatusRead);
    options.Conventions.AuthorizeFolder("/Opening", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Stocktakes", Policies.StockRead);
    // 匯入：頁面要登入；每種資料的權限在 handler 內依種類檢查（ItemsSubmit、ItemsIndex、WarehousesManage、OpeningEdit）
    options.Conventions.AuthorizeFolder("/Import", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Stock", Policies.StockRead);
    options.Conventions.AuthorizeFolder("/Scan", Policies.StockRead);
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
app.MapServiceWorker(app.Configuration);
app.MapRazorPages();

app.Run();

// 供整合測試的 WebApplicationFactory<Program> 使用
public partial class Program;
