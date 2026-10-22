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

/// <summary>stock_document_attachments：依單據查有效附件；刪除與送出後的移除由 trigger 禁止（M4_03）。</summary>
public class DocumentAttachmentConfiguration : IEntityTypeConfiguration<DocumentAttachment>
{
    public void Configure(EntityTypeBuilder<DocumentAttachment> builder)
    {
        builder.ToTable("stock_document_attachments", t =>
            t.HasCheckConstraint("ck_stock_document_attachments_kind", "(is_pdf AND thumb_key IS NULL) OR (NOT is_pdf AND thumb_key IS NOT NULL)"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.FileName).HasMaxLength(DocumentAttachment.FileNameMaxLength);
        builder.Property(a => a.FileKey).HasMaxLength(200);
        builder.Property(a => a.ThumbKey).HasMaxLength(200);
        builder.Property(a => a.UploadedByName).HasMaxLength(100);
        builder.HasIndex(a => new { a.DocumentId, a.IsActive });
        builder.HasOne<StockDocument>().WithMany().HasForeignKey(a => a.DocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}
