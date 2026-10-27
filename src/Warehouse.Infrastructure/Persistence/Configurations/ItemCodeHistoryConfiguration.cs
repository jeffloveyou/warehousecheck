using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Domain.Items;

namespace Warehouse.Infrastructure.Persistence.Configurations;

/// <summary>
/// item_code_history 歷史料號（D-32，ADR-0025）。只能新增：trigger trg_item_code_history_append_only
/// （Migration M10_01 以 SQL 建立）拒絕 UPDATE／DELETE。同一號碼可有多筆，以最新一筆判斷是否保留，所以不設唯一索引；
/// 保留與否的最後防線在 items 的 trigger（新料號不可等於別的物料保留中的歷史料號）。
/// </summary>
public class ItemCodeHistoryConfiguration : IEntityTypeConfiguration<ItemCodeHistory>
{
    private const string CodeFormat = "'^[A-Z0-9]{2,4}-[0-9]{6,}$'";

    public void Configure(EntityTypeBuilder<ItemCodeHistory> builder)
    {
        builder.ToTable("item_code_history", t =>
        {
            t.HasCheckConstraint("ck_item_code_history_code_format", $"code ~ {CodeFormat} AND new_code ~ {CodeFormat}");
            t.HasCheckConstraint("ck_item_code_history_reason", "btrim(reason) <> ''");
        });

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.Seq).UseIdentityAlwaysColumn();
        builder.Property(h => h.Code).HasMaxLength(Item.ItemCodeMaxLength);
        builder.Property(h => h.NewCode).HasMaxLength(Item.ItemCodeMaxLength);
        builder.Property(h => h.Reason).HasMaxLength(ItemCodeHistory.ReasonMaxLength);
        builder.Property(h => h.ActorName).HasMaxLength(ItemReview.ActorNameMaxLength);

        // 以舊料號查（搜尋、掃碼、占用檢查）：WHERE code = ? ORDER BY seq DESC
        builder.HasIndex(h => new { h.Code, h.Seq });
        // 物料明細頁列出歷史料號
        builder.HasIndex(h => new { h.ItemId, h.OccurredAt });

        builder.HasOne<Item>().WithMany().HasForeignKey(h => h.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
