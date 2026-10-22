using Warehouse.Application.Abstractions;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Search;

/// <summary>
/// 命中原因（docs/03 §5 排序）。數字越小越優先；同一物料多種原因時取最優先的一種。
/// </summary>
public enum HitReason
{
    /// <summary>第 1 層：正式料號完全相同。</summary>
    ItemCode = 1,

    /// <summary>第 1 層：申請單號完全相同。</summary>
    RequestNo = 2,

    /// <summary>第 2 層：有效專屬別名完全相同。</summary>
    Alias = 3,

    /// <summary>第 3 層：原廠型號相同（忽略空白與 - . / _）。</summary>
    PartNo = 4,

    /// <summary>第 3 層：品名完全相同。</summary>
    Name = 5,

    /// <summary>第 4 層：有效共用關鍵字完全相同。</summary>
    Keyword = 6,

    /// <summary>第 5 層：每個查詢詞都出現在某個欄位或索引裡（部分符合）。</summary>
    Partial = 7,

    /// <summary>第 2 層：有效原廠條碼完全相同（M5-01；與專屬別名同層，兩者互斥）。</summary>
    Barcode = 8,
}

/// <summary>
/// 搜尋結果的一筆。<see cref="Tier"/> 是排序層級（1～5）；<see cref="HitText"/> 說明比中的內容，
/// 例如別名原文，或部分符合時「品名、關鍵字：伺服AMP」。結果只是候選，必須由人確認料號與規格。
/// </summary>
public sealed record ItemSearchHit(
    Guid Id,
    string? ItemCode,
    string RequestNo,
    string Name,
    string? Brand,
    string? ManufacturerPartNo,
    string? SpecificationSummary,
    string CategoryName,
    string UnitCode,
    ItemStatus Status,
    int Tier,
    HitReason Reason,
    string HitText);

/// <summary>搜尋條件。<see cref="IncludeInactive"/> 為 false 時只找有效物料。</summary>
public sealed record ItemSearchRequest(string? Text, bool IncludeInactive = false, int Page = 1, int PageSize = ItemSearch.DefaultPageSize);

/// <summary>搜尋的限制（docs/03 §8、§9）。</summary>
public static class ItemSearch
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxTextLength = 100;
    public const int MaxTerms = 5;

    /// <summary>規格摘要最多顯示幾個字。</summary>
    public const int SpecificationSummaryLength = 60;

    /// <summary>
    /// 把正規化後的查詢拆成部分比對用的詞：去重複、最多 <see cref="MaxTerms"/> 個。
    /// 單一英數或符號（例「A」「-」）不做部分比對，否則幾乎每筆都會比中；單一中文字（例「閥」）有意義，保留。
    /// 被略過的詞放在 <c>Ignored</c>，畫面據此提示使用者。精確比對（料號、別名、品名、關鍵字）不受影響。
    /// </summary>
    public static (IReadOnlyList<string> Used, IReadOnlyList<string> Ignored) SplitTerms(string normalizedText)
    {
        var used = new List<string>();
        var ignored = new List<string>();
        foreach (var term in normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct())
        {
            var meaningful = term.Length >= 2 || term[0] >= '⺀'; // U+2E80 之後是中日韓文字
            if (meaningful && used.Count < MaxTerms)
            {
                used.Add(term);
            }
            else
            {
                ignored.Add(term);
            }
        }
        return (used, ignored);
    }
}

/// <summary>
/// 物料搜尋（M2-03，ADR-0013）：依料號 → 專屬別名 → 型號／品名 → 共用關鍵字 → 多詞部分符合排序，
/// 篩選、排序、分頁都在資料庫執行。
/// </summary>
public interface IItemSearchService
{
    Task<PagedResult<ItemSearchHit>> SearchAsync(ItemSearchRequest query, CancellationToken cancellationToken);
}
