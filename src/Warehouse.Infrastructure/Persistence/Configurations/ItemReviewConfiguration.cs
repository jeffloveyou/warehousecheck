using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>
/// item_reviews 審核紀錄（M1-02）。只能新增：trigger trg_item_reviews_append_only（Migration M1_02 以 SQL 建立）
/// 拒絕 UPDATE／DELETE。物料有審核紀錄後，外鍵 RESTRICT 也讓物料無法被刪除（使用者要求保留完整審核紀錄）。
/// </summary>
public class ItemReviewConfiguration : IEntityTypeConfiguration<ItemReview>
{
    private const int EnumTextMaxLength = 20;
    private const string Statuses = "'PendingApproval', 'Returned', 'Rejected', 'Active', 'Inactive'";

    public void Configure(EntityTypeBuilder<ItemReview> builder)
    {
        builder.ToTable("item_reviews", t =>
        {
            t.HasCheckConstraint("ck_item_reviews_action",
                "action IN ('Submit', 'Update', 'Resubmit', 'Approve', 'Return', 'Reject', 'Withdraw', 'Deactivate', 'Reactivate')");
            t.HasCheckConstraint("ck_item_reviews_from_status", $"from_status IN ({Statuses})");
            t.HasCheckConstraint("ck_item_reviews_to_status", $"to_status IN ({Statuses})");
            t.HasCheckConstraint("ck_item_reviews_comment_required",
                "action NOT IN ('Return', 'Reject') OR btrim(coalesce(comment, '')) <> ''");
            t.HasCheckConstraint("ck_item_reviews_item_code_on_approve", "(action = 'Approve') = (item_code IS NOT NULL)");
            t.HasCheckConstraint("ck_item_reviews_duplicate_on_reject",
                "duplicate_of_item_id IS NULL OR (action = 'Reject' AND duplicate_of_item_id <> item_id)");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Action).HasConversion<string>().HasMaxLength(EnumTextMaxLength);
        builder.Property(r => r.FromStatus).HasConversion<string>().HasMaxLength(EnumTextMaxLength);
        builder.Property(r => r.ToStatus).HasConversion<string>().HasMaxLength(EnumTextMaxLength);
        builder.Property(r => r.ItemCode).HasMaxLength(Item.ItemCodeMaxLength);
        builder.Property(r => r.Comment).HasMaxLength(ItemReview.CommentMaxLength);
        builder.Property(r => r.ActorName).HasMaxLength(ItemReview.ActorNameMaxLength);

        // 查一個物料的歷程：WHERE item_id = ? ORDER BY occurred_at
        builder.HasIndex(r => new { r.ItemId, r.OccurredAt });

        builder.HasOne<Item>().WithMany().HasForeignKey(r => r.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Item>().WithMany().HasForeignKey(r => r.DuplicateOfItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
