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

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapWarehouseApi();

app.Run();

// 供整合測試的 WebApplicationFactory<Program> 使用
public partial class Program;
