using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Application.Settings;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>warehouses：代碼全系統唯一、大寫英數。</summary>
public class WarehouseSiteConfiguration : IEntityTypeConfiguration<WarehouseSite>
{
    public void Configure(EntityTypeBuilder<WarehouseSite> builder)
    {
        builder.ToTable("warehouses", t => t.HasCheckConstraint("ck_warehouses_code_format", "code ~ '^[A-Z0-9]{1,8}$'"));
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.Code).HasMaxLength(WarehouseSite.CodeMaxLength);
        builder.Property(w => w.Name).HasMaxLength(WarehouseSite.NameMaxLength);
        builder.Property(w => w.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(w => w.Code).IsUnique();
    }
}

/// <summary>
/// locations：儲位碼在同一倉庫內唯一（ux_locations_warehouse_code）。狀態、類型以文字儲存。
/// 倉庫被儲位使用時不可刪除（RESTRICT）；一律用停用。
/// </summary>
public class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("locations", t =>
        {
            t.HasCheckConstraint("ck_locations_code_format", "code ~ '^[A-Z0-9]+(-[A-Z0-9]+){0,5}$'");
            t.HasCheckConstraint("ck_locations_status", "status IN ('Available', 'Inspection', 'Inactive')");
            t.HasCheckConstraint("ck_locations_type",
                "location_type IS NULL OR location_type IN ('Shelf', 'Floor', 'Pallet', 'Cold', 'Hazmat')");
            t.HasCheckConstraint("ck_locations_max_load", "max_load_kg IS NULL OR max_load_kg >= 0");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Code).HasMaxLength(Location.CodeMaxLength);
        builder.Property(l => l.Name).HasMaxLength(Location.NameMaxLength);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Note).HasMaxLength(Location.NoteMaxLength);
        builder.Property(l => l.Zone).HasMaxLength(Location.SegmentMaxLength);
        builder.Property(l => l.Rack).HasMaxLength(Location.SegmentMaxLength);
        builder.Property(l => l.Level).HasMaxLength(Location.SegmentMaxLength);
        builder.Property(l => l.Bin).HasMaxLength(Location.SegmentMaxLength);
        builder.Property(l => l.LocationType).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.MaxLoadKg).HasPrecision(12, 2);
        builder.Property(l => l.TemperatureControlled).HasDefaultValue(false).ValueGeneratedNever();
        builder.Property(l => l.Hazardous).HasDefaultValue(false).ValueGeneratedNever();
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(l => new { l.WarehouseId, l.Code }).IsUnique().HasDatabaseName("ux_locations_warehouse_code");
        builder.HasOne<WarehouseSite>().WithMany().HasForeignKey(l => l.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Warehouse.Domain.Inventory.StockDocument>().WithMany()
            .HasForeignKey(l => l.OpeningDocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>system_settings：鍵值；預設值以種子資料寫入（之後只能新增鍵，不能刪改種子，見 SeedData 規則）。</summary>
public class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("system_settings");
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).HasMaxLength(1000);
        builder.Property(s => s.UpdatedByName).HasMaxLength(100);
        builder.HasData(
            SystemSetting.Seed(SettingKeys.AllowSelfApproveOpening, "false"),
            SystemSetting.Seed(SettingKeys.LocationNotesEnabled, "true"),
            SystemSetting.Seed(SettingKeys.LocationAttributesEnabled, "true"),
            SystemSetting.Seed(SettingKeys.LocationImagesEnabled, "true"));
    }
}

/// <summary>audit_logs：只能新增（trigger 在 Migration M3_01 以 SQL 建立）；依對象與時間查詢。</summary>
public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.ActorName).HasMaxLength(100);
        builder.Property(a => a.Action).HasMaxLength(50);
        builder.Property(a => a.EntityType).HasMaxLength(50);
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.Before).HasColumnType("jsonb");
        builder.Property(a => a.After).HasColumnType("jsonb");
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAt });
        builder.HasIndex(a => a.OccurredAt);
        // 異常追蹤「單據事件」用（M4-03 審查 #2）；動作清單同 StockAnomalyService.DocumentActions
        builder.HasIndex(a => a.OccurredAt, "ix_audit_logs_document_events").HasDatabaseName("ix_audit_logs_document_events")
            .HasFilter("entity_type = 'stock_document' AND action IN ('document.cancel', 'document.return', 'document.reversal_create')");
    }
}
