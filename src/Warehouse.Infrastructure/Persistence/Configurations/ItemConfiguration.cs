using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>
/// items 資料表（ADR-0011）。Domain 的檢查之外，資料庫再以 CHECK／唯一約束保證：
/// - request_no 唯一且必填；item_code 唯一（核准前為 NULL，PostgreSQL 唯一索引允許多個 NULL）
/// - Active／Inactive 必有料號；PendingApproval／Returned／Rejected 一定沒有料號
/// - 允許負庫存只限 tracking_mode = None，下限 ≥ 0（ADR-0004）
/// - 分類、單位被物料使用時不可刪除（RESTRICT）
/// - 已發放的料號不可變更、清除，物料不可刪除：trigger trg_items_protect_item_code（Migration M1_01b 以 SQL 建立，EF 模型不描述 trigger）
/// </summary>
public class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    private const int EnumTextMaxLength = 20;

    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("items", t =>
        {
            t.HasCheckConstraint("ck_items_status",
                "status IN ('PendingApproval', 'Returned', 'Rejected', 'Active', 'Inactive')");
            t.HasCheckConstraint("ck_items_tracking_mode", "tracking_mode IN ('None', 'Lot', 'Serial')");
            // 「有料號」⇔「Active／Inactive」；未定義的狀態值交給 ck_items_status 處理，兩條規則不重疊
            t.HasCheckConstraint("ck_items_code_matches_status",
                "(status IN ('Active', 'Inactive')) = (item_code IS NOT NULL)");
            t.HasCheckConstraint("ck_items_negative_stock_limit", "negative_stock_limit >= 0");
            t.HasCheckConstraint("ck_items_negative_stock_tracking",
                "NOT allow_negative_stock OR tracking_mode = 'None'");
            t.HasCheckConstraint("ck_items_negative_stock_limit_when_disabled",
                "allow_negative_stock OR negative_stock_limit = 0");
            t.HasCheckConstraint("ck_items_name_not_blank", "btrim(name) <> ''");
            // 格式依 ADR-0006：申請單號 MR-000125、料號 前綴-流水號（EL-000125）；流水號至少 6 碼，用完可再加長
            t.HasCheckConstraint("ck_items_request_no_format", "request_no ~ '^MR-[0-9]{6,}$'");
            t.HasCheckConstraint("ck_items_item_code_format", "item_code ~ '^[A-Z0-9]{2,4}-[0-9]{6,}$'");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.RequestNo).HasMaxLength(Item.RequestNoMaxLength);
        builder.Property(i => i.ItemCode).HasMaxLength(Item.ItemCodeMaxLength);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(EnumTextMaxLength);
        builder.Property(i => i.Name).HasMaxLength(Item.NameMaxLength);
        builder.Property(i => i.TrackingMode).HasConversion<string>().HasMaxLength(EnumTextMaxLength);
        builder.Property(i => i.Brand).HasMaxLength(Item.BrandMaxLength);
        builder.Property(i => i.ManufacturerPartNo).HasMaxLength(Item.ManufacturerPartNoMaxLength);
        builder.Property(i => i.Specification).HasMaxLength(Item.SpecificationMaxLength);
        // 重複候選比對欄位（M1-02）；Domain 隨品名／型號自動計算。預設值只為了 Migration 對既有資料的回填
        builder.Property(i => i.NormalizedName).HasDefaultValue(string.Empty).ValueGeneratedNever();
        // 資料庫預設值只給手寫 SQL 用；ValueGeneratedNever 讓 EF 一律送出實際值。
        // 否則 EF 會把「等於 CLR 預設值」的欄位省略不送，例如 bool 預設 true 時，false 會被資料庫改成 true。
        builder.Property(i => i.AllowNegativeStock).HasDefaultValue(false).ValueGeneratedNever();
        builder.Property(i => i.NegativeStockLimit).HasPrecision(19, 4).HasDefaultValue(0m).ValueGeneratedNever();

        // 樂觀併發：PostgreSQL 系統欄位 xmin（每次更新自動改變），不需要自己維護版本欄位
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        builder.HasIndex(i => i.RequestNo).IsUnique();
        builder.HasIndex(i => i.ItemCode).IsUnique();
        builder.HasIndex(i => i.CategoryId);
        builder.HasIndex(i => i.Status);
        builder.HasIndex(i => i.NormalizedName);
        builder.HasIndex(i => i.NormalizedPartNo);

        builder.HasOne<ItemCategory>().WithMany().HasForeignKey(i => i.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Unit>().WithMany().HasForeignKey(i => i.BaseUnitId).OnDelete(DeleteBehavior.Restrict);
    }
}
