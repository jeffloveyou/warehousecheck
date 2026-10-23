using Microsoft.Extensions.Hosting.WindowsServices;
using Warehouse.Proxy;
using Warehouse.Web;
using Yarp.ReverseProxy.Configuration;

// M0-12 反向代理：固定對外 HTTPS 網址，把請求轉給 upstream.json 指定的目前實例（A 或 B）。
// 切換版本＝改寫 upstream.json；檔案變更後自動改用新目的地，舊實例上進行中的請求照常完成。
// M7-10：檔案無效時保留上一份有效設定；回應加 X-Warehouse-Slot 標頭（UpstreamConfigProvider）。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.AddExternalConfigDirectory();
builder.Services.AddWindowsService(options => options.ServiceName = "WarehouseProxy");
builder.Services.AddSystemd();

builder.Services.AddSingleton<UpstreamConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(services => services.GetRequiredService<UpstreamConfigProvider>());
builder.Services.AddReverseProxy();

var app = builder.Build();
app.MapReverseProxy();
app.Run();

// 供 Warehouse.Proxy.Tests 的 WebApplicationFactory<Program> 使用
public partial class Program;
