using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Application.Search;
using Warehouse.Application.Scanning;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.MasterData;
using Warehouse.Infrastructure.Search;
using Warehouse.Infrastructure.Scanning;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// 註冊 DbContext（PostgreSQL）與 Infrastructure 實作。
    /// <paramref name="attachmentsDirectory"/>：附件目錄（設定 Storage:AttachmentsDirectory）；未設定時用
    /// %LOCALAPPDATA%\Warehouse\attachments（開發機），正式環境由安裝腳本設定到 data 目錄（與發布目錄分開）。
    /// </summary>
    public static IServiceCollection AddWarehouseInfrastructure(
        this IServiceCollection services, string connectionString, string? attachmentsDirectory = null)
    {
        var attachments = string.IsNullOrWhiteSpace(attachmentsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warehouse", "attachments")
            : attachmentsDirectory;
        services.AddSingleton<IFileStore>(new LocalFileStore(attachments));
        services.AddDbContext<WarehouseDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IDatabaseStatus, DatabaseStatus>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IItemWorkflowService, ItemWorkflowService>();
        services.AddScoped<IItemQueryService, ItemQueryService>();
        services.AddScoped<IItemIndexService, ItemIndexService>();
        services.AddScoped<IItemSearchService, ItemSearchService>();
        services.AddScoped<IScanResolver, ScanResolver>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<IInventoryPostingService, InventoryPostingService>();
        services.AddScoped<IOpeningService, OpeningService>();
        services.AddScoped<IStocktakeService, StocktakeService>();
        services.AddScoped<Warehouse.Application.Exports.IExportService, Warehouse.Infrastructure.Exports.ExportService>();
        services.AddScoped<Warehouse.Application.Imports.IImportService, Warehouse.Infrastructure.Imports.ImportService>();
        services.AddScoped<IStockDocumentService, StockDocumentService>();
        services.AddScoped<IDocumentMasterDataService, DocumentMasterDataService>();
        services.AddScoped<IDocumentAttachmentService, DocumentAttachmentService>();
        services.AddSingleton<IPostingFailureLog, PostingFailureLog>();
        services.AddScoped<IStockAnomalyService, StockAnomalyService>();
        services.AddScoped<Warehouse.Infrastructure.Maintenance.DataRetentionService>();
        services.AddScoped<StockReservationService>();
        services.AddScoped<IStockReservationService>(sp => sp.GetRequiredService<StockReservationService>());
        services.AddScoped<IItemNegativePolicyService>(sp => sp.GetRequiredService<StockReservationService>());
        services.AddScoped<IStockQueryService, StockQueryService>();
        services.AddScoped<IMasterDataService, MasterDataService>();
        services.AddScoped<IItemLocationService, ItemLocationService>();
        services.AddScoped<ILocationImageService, LocationImageService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<ILoginAuditLog, LoginAuditLog>();
        services.AddScoped<IWarehouseAccess, WarehouseAccessService>();
        services.AddScoped<Warehouse.Application.Audit.IAuditQueryService, Warehouse.Infrastructure.Audit.AuditQueryService>();
        return services;
    }
}
