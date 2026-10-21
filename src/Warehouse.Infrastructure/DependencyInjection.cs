using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure;

public static class DependencyInjection
{
    /// <summary>註冊 DbContext（PostgreSQL）與 Infrastructure 實作。</summary>
    public static IServiceCollection AddWarehouseInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<WarehouseDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IDatabaseStatus, DatabaseStatus>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IItemWorkflowService, ItemWorkflowService>();
        return services;
    }
}
