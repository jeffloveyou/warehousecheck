using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>item_categories 資料表：代碼唯一；頂層分類必有料號前綴；有子分類或物料時不可刪除。</summary>
public class ItemCategoryConfiguration : IEntityTypeConfiguration<ItemCategory>
{
    public void Configure(EntityTypeBuilder<ItemCategory> builder)
    {
        builder.ToTable("item_categories", t =>
        {
            t.HasCheckConstraint("ck_item_categories_prefix_required", "parent_id IS NOT NULL OR item_code_prefix IS NOT NULL");
            t.HasCheckConstraint("ck_item_categories_prefix_format", "item_code_prefix ~ '^[A-Z0-9]{2,4}$'");
            t.HasCheckConstraint("ck_item_categories_code_format", "code ~ '^[A-Z0-9][A-Z0-9-]{1,19}$'");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Code).HasMaxLength(ItemCategory.CodeMaxLength);
        builder.Property(c => c.Name).HasMaxLength(ItemCategory.NameMaxLength);
        builder.Property(c => c.ItemCodePrefix).HasMaxLength(ItemCategory.PrefixMaxLength);
        builder.Property(c => c.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(c => c.SortOrder).HasDefaultValue(0).ValueGeneratedNever();
        builder.HasIndex(c => c.Code).IsUnique();

        builder.HasOne<ItemCategory>()
            .WithMany()
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(SeedData.Categories.Select(c => new ItemCategory(c.Id, null, c.Code, c.Name, c.Code, c.SortOrder)));
    }
}
