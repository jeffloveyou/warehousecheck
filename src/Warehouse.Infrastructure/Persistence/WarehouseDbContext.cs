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

    public DbSet<ItemReview> ItemReviews => Set<ItemReview>();

    public DbSet<ItemCodeCounter> ItemCodeCounters => Set<ItemCodeCounter>();

    /// <summary>申請單號 MR-000125 的流水號來源（PostgreSQL sequence，併發安全；交易失敗會跳號，可接受）。</summary>
    public const string RequestNoSequence = "item_request_no_seq";

    // 不論 DbContext 從哪裡建立（Web、dotnet ef、測試），資料表與欄位一律用 snake_case（ADR-0011）
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(RequestNoSequence);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WarehouseDbContext).Assembly);
    }
}
