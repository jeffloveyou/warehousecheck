using Microsoft.EntityFrameworkCore;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 系統唯一的 DbContext。資料表結構只能經 Migration 變更（docs/04 §3、warehouse-db-migration Skill）；
/// 程式啟動時不自動套用 Migration。
/// </summary>
public class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : DbContext(options)
{
    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();

    public DbSet<Item> Items => Set<Item>();

    // 不論 DbContext 從哪裡建立（Web、dotnet ef、測試），資料表與欄位一律用 snake_case（ADR-0011）
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WarehouseDbContext).Assembly);
}
