using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>
/// item_aliases（專屬別名）：有效的 normalized_text 全系統唯一（部分唯一索引，WHERE is_active），
/// 兩人同時新增同一個別名時，資料庫只讓一筆成功（ADR-0003 D-03）。
/// </summary>
public class ItemAliasConfiguration : IEntityTypeConfiguration<ItemAlias>
{
    public void Configure(EntityTypeBuilder<ItemAlias> builder)
    {
        ItemIndexMapping.Configure(builder, "item_aliases", ItemAlias.TextMaxLength);
        builder.HasIndex(a => a.NormalizedText)
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_item_aliases_active_normalized_text");
        builder.HasIndex(a => a.ItemId);
    }
}

/// <summary>
/// item_keywords（共用關鍵字）：同一物料的有效關鍵字不重複（部分唯一索引 item_id＋normalized_text），
/// 不同物料可以有相同關鍵字；另有 normalized_text 索引供搜尋精確比對。
/// </summary>
public class ItemKeywordConfiguration : IEntityTypeConfiguration<ItemKeyword>
{
    public void Configure(EntityTypeBuilder<ItemKeyword> builder)
    {
        ItemIndexMapping.Configure(builder, "item_keywords", ItemKeyword.TextMaxLength);
        builder.HasIndex(k => new { k.ItemId, k.NormalizedText })
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_item_keywords_active_item_text");
        builder.HasIndex(k => k.NormalizedText);
    }
}

/// <summary>
/// item_barcodes（原廠條碼，M5-01）：與別名相同，有效的 normalized_text 全系統唯一；
/// 包裝數量 numeric(19,4) 必須大於 0。禁止刪除與改寫的 trigger 在 Migration M5_01 以 SQL 建立。
/// </summary>
public class ItemBarcodeConfiguration : IEntityTypeConfiguration<ItemBarcode>
{
    public void Configure(EntityTypeBuilder<ItemBarcode> builder)
    {
        ItemIndexMapping.Configure(builder, "item_barcodes", ItemBarcode.TextMaxLength);
        builder.ToTable(t => t.HasCheckConstraint("ck_item_barcodes_pack_quantity", "pack_quantity > 0"));
        builder.Property(b => b.PackQuantity).HasPrecision(19, 4);
        builder.Property(b => b.Symbology).HasMaxLength(ItemBarcode.SymbologyMaxLength);
        builder.HasIndex(b => b.NormalizedText)
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_item_barcodes_active_normalized_text");
        builder.HasIndex(b => b.ItemId);
    }
}

/// <summary>三張索引表共用的欄位設定與 CHECK。禁止刪除與改寫的 trigger 在 Migration M2_01 以 SQL 建立。</summary>
internal static class ItemIndexMapping
{
    public static void Configure<T>(EntityTypeBuilder<T> builder, string table, int textMaxLength)
        where T : ItemIndexEntry
    {
        builder.ToTable(table, t =>
        {
            t.HasCheckConstraint($"ck_{table}_text_not_blank", "btrim(text) <> '' AND normalized_text <> ''");
            // 有效 ⇔ 沒有停用資訊；停用時一定記錄停用者與時間
            t.HasCheckConstraint($"ck_{table}_deactivation",
                "is_active = (deactivated_at IS NULL) AND (deactivated_at IS NULL) = (deactivated_by_id IS NULL)");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Text).HasMaxLength(textMaxLength);
        builder.Property(e => e.NormalizedText).HasMaxLength(textMaxLength * 4);
        builder.Property(e => e.CreatedByName).HasMaxLength(ItemReview.ActorNameMaxLength);
        builder.Property(e => e.DeactivatedByName).HasMaxLength(ItemReview.ActorNameMaxLength);
        builder.Property(e => e.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasOne<Item>().WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
