using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;

namespace Warehouse.Application.Items;

/// <summary>索引種類：專屬別名（全系統唯一）、共用關鍵字（可掛多個物料）、原廠條碼（全系統唯一，M5-01）。</summary>
public enum ItemIndexKind
{
    Alias,
    Keyword,
    Barcode,
}

/// <summary>
/// 物料的一筆搜尋索引。<see cref="UsageCount"/>：同一個關鍵字目前掛在幾個物料上（別名、條碼一律為 1）。
/// <see cref="PackQuantity"/>、<see cref="Symbology"/> 只有原廠條碼才有。
/// </summary>
public sealed record ItemIndexRow(
    Guid Id,
    ItemIndexKind Kind,
    string Text,
    bool IsActive,
    string CreatedByName,
    Guid CreatedById,
    DateTimeOffset CreatedAt,
    string? DeactivatedByName,
    DateTimeOffset? DeactivatedAt,
    int UsageCount,
    decimal? PackQuantity = null,
    string? Symbology = null);

/// <summary>
/// 別名已被其他物料使用。<see cref="HolderLabel"/> 是占用者的料號與品名，畫面可建議改加為共用關鍵字（docs/03 情境 D）。
/// </summary>
public sealed class AliasTakenException(Guid holderItemId, string holderLabel)
    : ConflictException("index.alias_taken", $"這個專屬別名已被 {holderLabel} 使用；若兩者都適用，請改加為共用關鍵字。")
{
    public Guid HolderItemId { get; } = holderItemId;

    public string HolderLabel { get; } = holderLabel;
}

/// <summary>
/// 物料的搜尋索引（專屬別名、共用關鍵字、原廠條碼）維護（M2-01、M2-02，ADR-0013；條碼 M5-01，ADR-0016）。
/// 只能加在有效（已核准）的物料；新增即生效並記錄建立者；只停用、不刪除。
/// </summary>
public interface IItemIndexService
{
    /// <summary>物料的全部索引（含已停用，供稽核），有效的排前面。</summary>
    Task<IReadOnlyList<ItemIndexRow>> ListAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// 新增索引。別名已被其他物料使用丟 <see cref="AliasTakenException"/>；
    /// 本物料已有相同的別名或關鍵字丟 <see cref="ConflictException"/>（index.duplicate）；
    /// 物料不是有效狀態丟 <see cref="ConflictException"/>（index.item_not_active）。
    /// </summary>
    /// <remarks>
    /// 原廠條碼（<see cref="ItemIndexKind.Barcode"/>）另外使用 <paramref name="packQuantity"/>（預設 1）與 <paramref name="symbology"/>；
    /// 條碼與別名互斥：條碼已被其他物料登錄、或與任何有效別名相同（反之亦然）都丟 <see cref="ConflictException"/>。
    /// </remarks>
    Task<Guid> AddAsync(
        Guid itemId, ItemIndexKind kind, string text, Actor actor, CancellationToken cancellationToken,
        decimal packQuantity = 1m, string? symbology = null);

    /// <summary>停用索引：建立者本人或物料管理員。</summary>
    Task DeactivateAsync(Guid itemId, ItemIndexKind kind, Guid entryId, Actor actor, CancellationToken cancellationToken);
}
