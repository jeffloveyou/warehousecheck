using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 系統唯一的 DbContext。資料表結構只能經 Migration 變更（docs/04 §3、warehouse-db-migration Skill）；
/// 程式啟動時不自動套用 Migration。
/// 繼承 IdentityDbContext：帳號、角色表與業務資料在同一個資料庫、同一套 Migration（ADR-0012）。
/// </summary>
public class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();

    public DbSet<Item> Items => Set<Item>();

    public DbSet<ItemReview> ItemReviews => Set<ItemReview>();

    public DbSet<ItemCodeCounter> ItemCodeCounters => Set<ItemCodeCounter>();

    public DbSet<ItemAlias> ItemAliases => Set<ItemAlias>();

    public DbSet<ItemKeyword> ItemKeywords => Set<ItemKeyword>();

    public DbSet<WarehouseSite> Warehouses => Set<WarehouseSite>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<InventoryLot> InventoryLots => Set<InventoryLot>();

    public DbSet<ItemSerial> ItemSerials => Set<ItemSerial>();

    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();

    public DbSet<StockBalance> StockBalances => Set<StockBalance>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<ProcessedCommand> ProcessedCommands => Set<ProcessedCommand>();

    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    public DbSet<DocumentPurpose> DocumentPurposes => Set<DocumentPurpose>();

    public DbSet<BusinessPartner> BusinessPartners => Set<BusinessPartner>();

    public DbSet<ItemLocation> ItemLocations => Set<ItemLocation>();

    public DbSet<LocationImage> LocationImages => Set<LocationImage>();

    /// <summary>期初單號 OB-000001 的流水號來源。</summary>
    public const string OpeningDocNoSequence = "opening_doc_no_seq";

    /// <summary>進倉、出倉、移庫單號 IN-／OUT-／TR-000001 的流水號來源（各自獨立；交易失敗會跳號，可接受）。</summary>
    public const string InboundDocNoSequence = "inbound_doc_no_seq";
    public const string OutboundDocNoSequence = "outbound_doc_no_seq";
    public const string TransferDocNoSequence = "transfer_doc_no_seq";

    /// <summary>申請單號 MR-000125 的流水號來源（PostgreSQL sequence，併發安全；交易失敗會跳號，可接受）。</summary>
    public const string RequestNoSequence = "item_request_no_seq";

    // 不論 DbContext 從哪裡建立（Web、dotnet ef、測試），資料表與欄位一律用 snake_case（ADR-0011）
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 先讓 Identity 建立預設模型，再改成較好讀的表名（預設是 asp_net_users 等）
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("users");
            b.Property(u => u.DisplayName).HasMaxLength(AppUser.DisplayNameMaxLength);
            b.Property(u => u.IsActive).HasDefaultValue(true).ValueGeneratedNever();
            // Identity 預設索引名稱是 PascalCase（UserNameIndex），改成與其他表一致的 snake_case
            b.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
            b.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
        });
        modelBuilder.Entity<IdentityRole<Guid>>(b =>
        {
            b.ToTable("roles");
            b.HasIndex(r => r.NormalizedName).HasDatabaseName("ix_roles_normalized_name");
            b.HasData(RoleSeed.All);
        });
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        modelBuilder.HasSequence<long>(RequestNoSequence);
        modelBuilder.HasSequence<long>(OpeningDocNoSequence);
        modelBuilder.HasSequence<long>(InboundDocNoSequence);
        modelBuilder.HasSequence<long>(OutboundDocNoSequence);
        modelBuilder.HasSequence<long>(TransferDocNoSequence);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WarehouseDbContext).Assembly);
    }
}
