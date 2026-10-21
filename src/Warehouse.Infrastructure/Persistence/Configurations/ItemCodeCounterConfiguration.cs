using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Warehouse.Infrastructure.Persistence.Configurations;

public class ItemCodeCounterConfiguration : IEntityTypeConfiguration<ItemCodeCounter>
{
    public void Configure(EntityTypeBuilder<ItemCodeCounter> builder)
    {
        builder.ToTable("item_code_counters", t =>
        {
            t.HasCheckConstraint("ck_item_code_counters_prefix_format", "prefix ~ '^[A-Z0-9]{2,4}$'");
            t.HasCheckConstraint("ck_item_code_counters_last_number", "last_number >= 1");
        });
        builder.HasKey(c => c.Prefix);
        builder.Property(c => c.Prefix).HasMaxLength(4);
    }
}
