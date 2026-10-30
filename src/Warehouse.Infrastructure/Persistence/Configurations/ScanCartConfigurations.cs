using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>快速進出購物車（ADR-0026）：只是清單，刪除使用者時一併刪除；物料、儲位、倉庫不可在被引用時刪除。</summary>
public class ScanCartConfiguration : IEntityTypeConfiguration<ScanCart>
{
    public void Configure(EntityTypeBuilder<ScanCart> builder)
    {
        builder.ToTable("scan_carts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Note).HasMaxLength(ScanCart.NoteMaxLength);
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(c => new { c.OwnerId, c.UpdatedAt });
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<WarehouseSite>().WithMany().HasForeignKey(c => c.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.CartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class ScanCartLineConfiguration : IEntityTypeConfiguration<ScanCartLine>
{
    public void Configure(EntityTypeBuilder<ScanCartLine> builder)
    {
        builder.ToTable("scan_cart_lines", t => t.HasCheckConstraint("ck_scan_cart_lines_quantity", "quantity > 0"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Quantity).HasPrecision(19, 4);
        builder.HasIndex(l => new { l.CartId, l.LineNo }).IsUnique();
        // 同物料＋同儲位只有一行（沒有儲位的也只有一行）
        builder.HasIndex(l => new { l.CartId, l.ItemId, l.LocationId }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName("ux_scan_cart_lines_key");
        builder.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}
