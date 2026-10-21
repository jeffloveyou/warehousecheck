using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>inventory_lots：同一物料內批號唯一。</summary>
public class InventoryLotConfiguration : IEntityTypeConfiguration<InventoryLot>
{
    public void Configure(EntityTypeBuilder<InventoryLot> builder)
    {
        builder.ToTable("inventory_lots", t => t.HasCheckConstraint("ck_inventory_lots_lot_no", "btrim(lot_no) <> ''"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.LotNo).HasMaxLength(StockDocumentLine.CodeMaxLength);
        builder.HasIndex(l => new { l.ItemId, l.LotNo }).IsUnique();
        builder.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>item_serials：同一物料內序號唯一。</summary>
public class ItemSerialConfiguration : IEntityTypeConfiguration<ItemSerial>
{
    public void Configure(EntityTypeBuilder<ItemSerial> builder)
    {
        builder.ToTable("item_serials", t => t.HasCheckConstraint("ck_item_serials_serial_no", "btrim(serial_no) <> ''"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.SerialNo).HasMaxLength(StockDocumentLine.CodeMaxLength);
        builder.HasIndex(s => new { s.ItemId, s.SerialNo }).IsUnique();
        builder.HasOne<Item>().WithMany().HasForeignKey(s => s.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(s => s.LotId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// stock_documents／stock_document_lines：已過帳的單據與明細由 trigger 禁止修改刪除（Migration M3_03）。
/// 單內「儲位＋物料＋批號＋序號」唯一（NULLS NOT DISTINCT）。
/// </summary>
public class StockDocumentConfiguration : IEntityTypeConfiguration<StockDocument>
{
    public void Configure(EntityTypeBuilder<StockDocument> builder)
    {
        builder.ToTable("stock_documents", t =>
        {
            t.HasCheckConstraint("ck_stock_documents_type", "doc_type IN ('Opening')");
            t.HasCheckConstraint("ck_stock_documents_status", "status IN ('Draft', 'Submitted', 'Posted', 'Cancelled')");
            t.HasCheckConstraint("ck_stock_documents_posted",
                "(status = 'Posted') = (posted_at IS NOT NULL AND approved_by_id IS NOT NULL)");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.DocNo).HasMaxLength(20);
        builder.Property(d => d.DocType).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Note).HasMaxLength(StockDocument.NoteMaxLength);
        builder.Property(d => d.CreatedByName).HasMaxLength(100);
        builder.Property(d => d.ApprovedByName).HasMaxLength(100);
        builder.Property(d => d.ReturnComment).HasMaxLength(StockDocument.NoteMaxLength);
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(d => d.DocNo).IsUnique();
        builder.HasIndex(d => new { d.DocType, d.Status, d.CreatedAt });
        builder.HasOne<WarehouseSite>().WithMany().HasForeignKey(d => d.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        // Cascade 讓 EF 刪除草稿中移除的明細；單據本身由 trigger 禁止刪除，所以不會連帶刪掉已過帳的明細
        builder.HasMany(d => d.Lines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class StockDocumentLineConfiguration : IEntityTypeConfiguration<StockDocumentLine>
{
    public void Configure(EntityTypeBuilder<StockDocumentLine> builder)
    {
        builder.ToTable("stock_document_lines", t =>
        {
            t.HasCheckConstraint("ck_stock_document_lines_quantity", "quantity >= 0");
            t.HasCheckConstraint("ck_stock_document_lines_empty_location", "item_id IS NOT NULL OR quantity = 0");
        });
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.LotNo).HasMaxLength(StockDocumentLine.CodeMaxLength);
        builder.Property(l => l.SerialNo).HasMaxLength(StockDocumentLine.CodeMaxLength);
        builder.Property(l => l.Quantity).HasPrecision(19, 4);
        builder.HasIndex(l => new { l.DocumentId, l.LineNo }).IsUnique();
        builder.HasIndex(l => new { l.DocumentId, l.LocationId, l.ItemId, l.LotNo, l.SerialNo })
            .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_stock_document_lines_key");
        builder.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(l => l.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ItemSerial>().WithMany().HasForeignKey(l => l.SerialId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// stock_balances：
/// - ux_stock_balances_dimension：物料＋儲位＋批號＋序號＋狀態唯一（NULLS NOT DISTINCT，過帳的 ON CONFLICT 依此合併）。
/// - ux_stock_balances_serial_in_stock：同一序號只能在一個地方數量 &gt; 0（序號不可同時在兩個儲位）。
/// - 序號結存只能是 0 或 1；M3 不允許負數（M4 負庫存例外會調整這條 CHECK，ADR-0004）。
/// </summary>
public class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("stock_balances", t =>
        {
            t.HasCheckConstraint("ck_stock_balances_status", "stock_status IN ('Available', 'Quarantine')");
            t.HasCheckConstraint("ck_stock_balances_non_negative", "quantity >= 0");
            t.HasCheckConstraint("ck_stock_balances_serial_quantity", "serial_id IS NULL OR quantity IN (0, 1)");
        });
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.StockStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.Quantity).HasPrecision(19, 4);
        builder.HasIndex(b => new { b.ItemId, b.LocationId, b.LotId, b.SerialId, b.StockStatus })
            .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_stock_balances_dimension");
        builder.HasIndex(b => b.SerialId).IsUnique().HasFilter("serial_id IS NOT NULL AND quantity > 0")
            .HasDatabaseName("ux_stock_balances_serial_in_stock");
        builder.HasIndex(b => new { b.LocationId, b.ItemId });
        builder.HasOne<Item>().WithMany().HasForeignKey(b => b.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(b => b.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(b => b.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ItemSerial>().WithMany().HasForeignKey(b => b.SerialId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>stock_movements：只能新增（trigger）；依物料、儲位、單據查詢歷程。</summary>
public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements", t =>
        {
            t.HasCheckConstraint("ck_stock_movements_delta", "quantity_delta <> 0");
            t.HasCheckConstraint("ck_stock_movements_status", "stock_status IN ('Available', 'Quarantine')");
        });
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.StockStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.QuantityDelta).HasPrecision(19, 4);
        builder.Property(m => m.BalanceAfter).HasPrecision(19, 4);
        builder.Property(m => m.ActorName).HasMaxLength(100);
        builder.HasIndex(m => new { m.ItemId, m.OccurredAt });
        builder.HasIndex(m => new { m.LocationId, m.OccurredAt });
        builder.HasIndex(m => m.DocumentId);
        builder.HasOne<StockDocument>().WithMany().HasForeignKey(m => m.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StockDocumentLine>().WithMany().HasForeignKey(m => m.LineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Item>().WithMany().HasForeignKey(m => m.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(m => m.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InventoryLot>().WithMany().HasForeignKey(m => m.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ItemSerial>().WithMany().HasForeignKey(m => m.SerialId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>processed_commands：冪等鍵（主鍵唯一，同一鍵只會處理一次）。</summary>
public class ProcessedCommandConfiguration : IEntityTypeConfiguration<ProcessedCommand>
{
    public void Configure(EntityTypeBuilder<ProcessedCommand> builder)
    {
        builder.ToTable("processed_commands");
        builder.HasKey(c => c.Key);
        builder.Property(c => c.Key).HasMaxLength(100);
        builder.Property(c => c.CommandType).HasMaxLength(50);
        builder.Property(c => c.RequestHash).HasMaxLength(64);
        builder.Property(c => c.ResultRef).HasMaxLength(100);
    }
}
