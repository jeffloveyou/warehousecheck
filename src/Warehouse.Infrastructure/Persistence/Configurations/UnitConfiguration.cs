using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>units 資料表：代碼唯一、小數位數 0～4。</summary>
public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable("units", t =>
        {
            t.HasCheckConstraint("ck_units_decimal_places", $"decimal_places BETWEEN 0 AND {Unit.MaxDecimalPlaces}");
            t.HasCheckConstraint("ck_units_code_format", "code ~ '^[A-Z0-9]{1,16}$'");
        });
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.Code).HasMaxLength(Unit.CodeMaxLength);
        builder.Property(u => u.Name).HasMaxLength(Unit.NameMaxLength);
        builder.Property(u => u.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        builder.HasIndex(u => u.Code).IsUnique();

        builder.HasData(SeedData.Units.Select(u => new Unit(u.Id, u.Code, u.Name, u.DecimalPlaces)));
    }
}
