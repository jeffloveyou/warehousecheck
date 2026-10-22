using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>
/// item_locations：同一物料同一儲位只一筆；每個物料在每個倉庫最多一個預設倉位（ux_item_locations_default，WHERE is_default）。
/// </summary>
public class ItemLocationConfiguration : IEntityTypeConfiguration<ItemLocation>
{
    public void Configure(EntityTypeBuilder<ItemLocation> builder)
    {
        builder.ToTable("item_locations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.IsDefault).HasDefaultValue(false).ValueGeneratedNever();
        builder.HasIndex(x => new { x.ItemId, x.LocationId }).IsUnique();
        builder.HasIndex(x => new { x.ItemId, x.WarehouseId }).IsUnique().HasFilter("is_default")
            .HasDatabaseName("ux_item_locations_default");
        builder.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WarehouseSite>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>location_images：只存中繼資料；檔案在附件目錄（Storage:AttachmentsDirectory）。</summary>
public class LocationImageConfiguration : IEntityTypeConfiguration<LocationImage>
{
    public void Configure(EntityTypeBuilder<LocationImage> builder)
    {
        builder.ToTable("location_images", t =>
            t.HasCheckConstraint("ck_location_images_deactivation", "is_active = (deactivated_at IS NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.LargeKey).HasMaxLength(200);
        builder.Property(x => x.ThumbKey).HasMaxLength(200);
        builder.Property(x => x.UploadedByName).HasMaxLength(100);
        builder.Property(x => x.DeactivatedByName).HasMaxLength(100);
        builder.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        builder.HasIndex(x => new { x.LocationId, x.IsActive });
        builder.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}
