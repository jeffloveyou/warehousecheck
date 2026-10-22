using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Inventory;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>document_purposes：方向＋代碼唯一；預設資料見 <see cref="SeedData.Purposes"/>。</summary>
public class DocumentPurposeConfiguration : IEntityTypeConfiguration<DocumentPurpose>
{
    public void Configure(EntityTypeBuilder<DocumentPurpose> builder)
    {
        builder.ToTable("document_purposes", t => t.HasCheckConstraint("ck_document_purposes_direction", "direction IN ('In', 'Out')"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Direction).HasConversion<string>().HasMaxLength(10);
        builder.Property(p => p.Code).HasMaxLength(MasterCode.MaxLength);
        builder.Property(p => p.Name).HasMaxLength(DocumentPurpose.NameMaxLength);
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(p => new { p.Direction, p.Code }).IsUnique();
        builder.HasData(SeedData.Purposes.Select(p => DocumentPurpose.Create(p.Id, p.Direction, p.Code, p.Name, p.SortOrder)));
    }
}

/// <summary>business_partners：類型＋代碼唯一。</summary>
public class BusinessPartnerConfiguration : IEntityTypeConfiguration<BusinessPartner>
{
    public void Configure(EntityTypeBuilder<BusinessPartner> builder)
    {
        builder.ToTable("business_partners", t =>
            t.HasCheckConstraint("ck_business_partners_type", "partner_type IN ('Vendor', 'Customer', 'Department')"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.PartnerType).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Code).HasMaxLength(MasterCode.MaxLength);
        builder.Property(p => p.Name).HasMaxLength(BusinessPartner.NameMaxLength);
        builder.Property<uint>("Version").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(p => new { p.PartnerType, p.Code }).IsUnique();
        builder.HasIndex(p => new { p.PartnerType, p.Name });
    }
}
