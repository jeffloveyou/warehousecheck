using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Items;

/// <summary>
/// 物料清單與明細查詢：AsNoTracking＋只投影需要的欄位，篩選、排序、分頁都在資料庫執行（warehouse-performance Skill）。
/// </summary>
public sealed class ItemQueryService(WarehouseDbContext db) : IItemQueryService
{
    public async Task<PagedResult<ItemListRow>> SearchAsync(ItemSearchQuery query, CancellationToken cancellationToken)
    {
        var page = Paging.ClampPage(query.Page);
        var pageSize = Paging.ClampPageSize(query.PageSize);
        var items = Filter(db, query);

        var total = await items.CountAsync(cancellationToken);
        var rows = await items
            .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new ItemListRow(
                i.Id,
                i.RequestNo,
                i.ItemCode,
                i.Name,
                i.Brand,
                i.ManufacturerPartNo,
                i.Status,
                db.ItemCategories.Where(c => c.Id == i.CategoryId).Select(c => c.Name).First(),
                db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.Code).First(),
                i.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<ItemListRow>(rows, page, pageSize, total);
    }

    /// <summary>物料清單的篩選條件（畫面與匯出共用，M6-03）。</summary>
    internal static IQueryable<Item> Filter(WarehouseDbContext db, ItemSearchQuery query)
    {
        var items = db.Items.AsNoTracking();

        if (query.Status is { } status)
        {
            items = items.Where(i => i.Status == status);
        }
        if (query.CategoryId is { } categoryId)
        {
            items = items.Where(i => i.CategoryId == categoryId);
        }
        var keyword = query.Keyword?.Trim();
        if (keyword?.Length > Paging.MaxKeywordLength)
        {
            keyword = keyword[..Paging.MaxKeywordLength];
        }
        if (!string.IsNullOrEmpty(keyword))
        {
            // 申請單號、料號完全相符（不分大小寫）；品名、型號部分相符。
            // ILIKE '%…%' 無法用一般索引，資料量大時的搜尋改由 M2 的搜尋索引負責（docs/03）。
            var upper = keyword.ToUpperInvariant();
            var pattern = LikePattern.Contains(keyword);
            var partNo = TextNormalizer.NormalizePartNo(keyword);
            // 保留中的歷史料號（D-32）也找得到：同一號碼以最新一筆判斷
            var oldCodeItemIds = db.ItemCodeHistory
                .Where(h => h.Code == upper && !h.Released
                    && !db.ItemCodeHistory.Any(n => n.Code == h.Code && n.Seq > h.Seq))
                .Select(h => h.ItemId);
            items = items.Where(i =>
                i.RequestNo == upper
                || i.ItemCode == upper
                || oldCodeItemIds.Contains(i.Id)
                || EF.Functions.ILike(i.Name, pattern, LikePattern.Escape)
                || (i.ManufacturerPartNo != null && EF.Functions.ILike(i.ManufacturerPartNo, pattern, LikePattern.Escape))
                || (partNo != null && i.NormalizedPartNo == partNo));
        }
        return items;
    }

    /// <summary>目前的料號；找不到時改查保留中的歷史料號（D-32），對應到改號後的同一物料。</summary>
    public async Task<Guid?> FindIdByItemCodeAsync(string itemCode, CancellationToken cancellationToken) =>
        await db.Items.AsNoTracking().Where(i => i.ItemCode == itemCode).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(cancellationToken)
        ?? (Item.IsValidItemCode(itemCode) ? await new ItemCodeRules(db).ReservedOwnerAsync(itemCode, cancellationToken) : null);

    public async Task<ItemDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new
            {
                Item = i,
                Version = EF.Property<uint>(i, "Version"),
                CategoryName = db.ItemCategories.Where(c => c.Id == i.CategoryId).Select(c => c.Name).First(),
                UnitCode = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.Code).First(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        var reviews = await db.ItemReviews.AsNoTracking()
            .Where(r => r.ItemId == id)
            .OrderBy(r => r.OccurredAt)
            .Select(r => new ItemReviewRow(
                r.Action,
                r.FromStatus,
                r.ToStatus,
                r.ItemCode,
                r.DuplicateOfItemId,
                r.DuplicateOfItemId == null
                    ? null
                    : db.Items.Where(d => d.Id == r.DuplicateOfItemId)
                        .Select(d => (d.ItemCode ?? d.RequestNo) + " " + d.Name).FirstOrDefault(),
                r.Comment,
                r.ActorName,
                r.OccurredAt))
            .ToListAsync(cancellationToken);

        var i = item.Item;
        return new ItemDetails(
            i.Id, i.RequestNo, i.ItemCode, i.Status, i.Name, i.CategoryId, item.CategoryName, i.BaseUnitId, item.UnitCode,
            i.TrackingMode, i.Brand, i.ManufacturerPartNo, i.Specification, i.AllowNegativeStock, i.NegativeStockLimit,
            i.CreatedById, i.CreatedAt, i.UpdatedAt, item.Version, reviews);
    }
}
