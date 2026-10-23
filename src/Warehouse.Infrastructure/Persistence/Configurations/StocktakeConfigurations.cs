using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>
/// 盤點單（M6-02，ADR-0018）：stocktakes、stocktake_locations（範圍）、stocktake_lines（明細）。
/// ux_stocktake_locations_open：同一儲位同時只能在一張未結束的盤點單內（否則兩張盤點會重複調整）。
/// </summary>
public class StocktakeConfiguration : IEntityTypeConfiguration<Stocktake>
{
    public void Configure(EntityTypeBuilder<Stocktake> builder)
    {
        builder.ToTable("stocktakes", t =>
            t.HasCheckConstraint("ck_stocktakes_status", "status IN ('Counting', 'Submitted', 'Approved', 'Cancelled')"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.StocktakeNo).HasMaxLength(20);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Note).HasMaxLength(Stocktake.NoteMaxLength);
        builder.Property(s => s.RecountComment).HasMaxLength(Stocktake.NoteMaxLength);
        builder.Property(s => s.CreatedByName).HasMaxLength(100);
        builder.Property(s => s.SubmittedByName).HasMaxLength(100);
        builder.Property(s => s.ApprovedByName).HasMaxLength(100);
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(s => s.StocktakeNo).IsUnique();
        builder.HasIndex(s => new { s.WarehouseId, s.Status, s.CreatedAt });
        builder.HasOne<WarehouseSite>().WithMany().HasForeignKey(s => s.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StockDocument>().WithMany().HasForeignKey(s => s.AdjustmentDocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.StocktakeId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class StocktakeLineConfiguration : IEntityTypeConfiguration<StocktakeLine>
{
    public void Configure(EntityTypeBuilder<StocktakeLine> builder)
    {
        builder.ToTable("stocktake_lines", t =>
        {
            t.HasCheckConstraint("ck_stocktake_lines_counted", "counted_quantity IS NULL OR counted_quantity >= 0");
            t.HasCheckConstraint("ck_stocktake_lines_count_pair", "(counted_quantity IS NULL) = (book_quantity_at_count IS NULL)");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.LotNo).HasMaxLength(StockDocumentLine.CodeMaxLength);
        builder.Property(l => l.SerialNo).HasMaxLength(StockDocumentLine.CodeMaxLength);
        builder.Property(l => l.StockStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.SnapshotQuantity).HasPrecision(19, 4);
        builder.Property(l => l.CountedQuantity).HasPrecision(19, 4);
        builder.Property(l => l.BookQuantityAtCount).HasPrecision(19, 4);
        builder.Property(l => l.CountedByName).HasMaxLength(100);
        builder.Ignore(l => l.Difference);
        builder.HasIndex(l => new { l.StocktakeId, l.LineNo }).IsUnique();
        builder.HasIndex(l => new { l.StocktakeId, l.LocationId, l.ItemId, l.LotNo, l.SerialNo, l.StockStatus })
            .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_stocktake_lines_key");
        builder.HasOne<Location>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(l => l.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ItemSerial>().WithMany().HasForeignKey(l => l.SerialId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class StocktakeLocationConfiguration : IEntityTypeConfiguration<StocktakeLocation>
{
    public void Configure(EntityTypeBuilder<StocktakeLocation> builder)
    {
        builder.ToTable("stocktake_locations");
        builder.HasKey(x => new { x.StocktakeId, x.LocationId });
        builder.Property(x => x.IsOpen).HasDefaultValue(true).ValueGeneratedNever();
        builder.HasIndex(x => x.LocationId).IsUnique().HasFilter("is_open").HasDatabaseName("ux_stocktake_locations_open");
        builder.HasOne<Stocktake>().WithMany().HasForeignKey(x => x.StocktakeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>盤點範圍內的儲位。<see cref="IsOpen"/>：盤點單未結束（核准或取消時改 false，釋放唯一索引）。</summary>
public sealed class StocktakeLocation
{
    public Guid StocktakeId { get; set; }

    public Guid LocationId { get; set; }

    public bool IsOpen { get; set; } = true;
}
