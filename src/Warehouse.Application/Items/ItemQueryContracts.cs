using Warehouse.Application.Abstractions;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Items;

/// <summary>
/// 物料清單查詢條件。<see cref="Keyword"/> 比對申請單號、料號、品名、型號；
/// 精準的別名搜尋與命中原因屬於 M2（docs/03）。
/// </summary>
public sealed record ItemSearchQuery(
    string? Keyword,
    ItemStatus? Status,
    Guid? CategoryId,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

/// <summary>清單的一列（精簡欄位，不回傳整個 Entity）。</summary>
public sealed record ItemListRow(
    Guid Id,
    string RequestNo,
    string? ItemCode,
    string Name,
    string? Brand,
    string? ManufacturerPartNo,
    ItemStatus Status,
    string CategoryName,
    string UnitCode,
    DateTimeOffset UpdatedAt);

/// <summary>審核紀錄的一列。</summary>
public sealed record ItemReviewRow(
    ItemReviewAction Action,
    ItemStatus FromStatus,
    ItemStatus ToStatus,
    string? ItemCode,
    Guid? DuplicateOfItemId,
    string? DuplicateOfLabel,
    string? Comment,
    string ActorName,
    DateTimeOffset OccurredAt);

/// <summary>物料明細；<see cref="Version"/> 是 xmin，編輯或審核時要原樣帶回（樂觀併發）。</summary>
public sealed record ItemDetails(
    Guid Id,
    string RequestNo,
    string? ItemCode,
    ItemStatus Status,
    string Name,
    Guid CategoryId,
    string CategoryName,
    Guid BaseUnitId,
    string UnitCode,
    TrackingMode TrackingMode,
    string? Brand,
    string? ManufacturerPartNo,
    string? Specification,
    bool AllowNegativeStock,
    decimal NegativeStockLimit,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version,
    IReadOnlyList<ItemReviewRow> Reviews);

/// <summary>物料查詢（唯讀，AsNoTracking＋投影）。</summary>
public interface IItemQueryService
{
    Task<PagedResult<ItemListRow>> SearchAsync(ItemSearchQuery query, CancellationToken cancellationToken);

    /// <summary>找不到時回傳 null。</summary>
    Task<ItemDetails?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>依正式料號（已正規化）精確查物料 ID；找不到回傳 null（使用 ix_items_item_code 唯一索引）。</summary>
    Task<Guid?> FindIdByItemCodeAsync(string itemCode, CancellationToken cancellationToken);
}
