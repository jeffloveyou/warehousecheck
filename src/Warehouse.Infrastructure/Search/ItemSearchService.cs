using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Search;
using Warehouse.Domain.Items;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Text;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Search;

/// <summary>
/// <see cref="IItemSearchService"/> 實作（docs/03 §5、ADR-0013）。一句 SQL 完成篩選與排序：
/// <code>
/// WHERE 精確條件（料號、別名、型號、品名、關鍵字） OR 每個詞都部分符合
/// ORDER BY CASE …（1 料號 2 別名 3 型號／品名 4 關鍵字 5 部分符合）, 料號
/// </code>
/// 命中原因的文字（哪個別名、哪些欄位）只對目前這一頁的資料在記憶體中計算，不增加資料庫負擔。
/// 比對都用正規化後的文字（<see cref="TextNormalizer"/>），所以全形、大小寫、空白不影響結果。
/// </summary>
public sealed class ItemSearchService(WarehouseDbContext db) : IItemSearchService
{
    public async Task<PagedResult<ItemSearchHit>> SearchAsync(ItemSearchRequest query, CancellationToken cancellationToken)
    {
        var page = Paging.ClampPage(query.Page);
        var pageSize = query.PageSize < 1 ? ItemSearch.DefaultPageSize : Math.Min(query.PageSize, ItemSearch.MaxPageSize);
        // 先去控制字元（掃碼尾碼、中間夾的 Tab／GS），與條碼登錄、掃碼解析同一套前處理（M5-01 審查 S6）
        var raw = ScanInput.StripControl(query.Text);
        if (raw.Length > ItemSearch.MaxTextLength)
        {
            raw = raw[..ItemSearch.MaxTextLength];
        }
        var text = TextNormalizer.Normalize(raw);
        if (text.Length == 0)
        {
            return new PagedResult<ItemSearchHit>([], page, pageSize, 0);
        }
        var compact = TextNormalizer.NormalizePartNo(raw);
        // 太短的詞（單一英數或符號）不做部分比對，否則「A」會比中幾乎每一筆（M2 審查 R2）；中文單字仍可（例「閥」）
        var terms = ItemSearch.SplitTerms(text).Used;

        var items = query.IncludeInactive
            ? db.Items.AsNoTracking().Where(i => i.Status == ItemStatus.Active || i.Status == ItemStatus.Inactive)
            : db.Items.AsNoTracking().Where(i => i.Status == ItemStatus.Active);

        // 精確比中別名、關鍵字的物料 ID 寫成 IN (子查詢)：PostgreSQL 可以先用 normalized_text 索引找出少數幾筆，
        // 不必對每個物料各跑一次 EXISTS（M2 審查 R3）
        var aliasItemIds = db.ItemAliases.Where(a => a.IsActive && a.NormalizedText == text).Select(a => a.ItemId);
        // 原廠條碼（M5-01）與專屬別名同屬第 2 層：兩者互斥，都是全系統唯一的精確識別碼
        var barcodeItemIds = db.ItemBarcodes.Where(b => b.IsActive && b.NormalizedText == text).Select(b => b.ItemId);
        var keywordItemIds = db.ItemKeywords.Where(k => k.IsActive && k.NormalizedText == text).Select(k => k.ItemId);
        // 保留中的歷史料號（D-32）：同一號碼以最新一筆判斷，已釋出的不算
        var oldCodeItemIds = db.ItemCodeHistory
            .Where(h => h.Code == text && !h.Released
                && !db.ItemCodeHistory.Any(n => n.Code == h.Code && n.Seq > h.Seq))
            .Select(h => h.ItemId);
        Expression<Func<Item, bool>> exact = i =>
            i.ItemCode == text || i.RequestNo == text || oldCodeItemIds.Contains(i.Id)
            || aliasItemIds.Contains(i.Id) || barcodeItemIds.Contains(i.Id)
            || (compact != null && i.NormalizedPartNo == compact)
            || i.NormalizedName == text
            || keywordItemIds.Contains(i.Id);
        var matched = items.Where(terms.Count == 0 ? exact : Or(exact, AllTermsMatch(terms)));

        var total = await matched.CountAsync(cancellationToken);
        var rows = await matched
            .Select(i => new
            {
                i.Id, i.ItemCode, i.RequestNo, i.Name, i.Brand, i.ManufacturerPartNo, i.Specification, i.Status,
                i.NormalizedName, i.NormalizedPartNo, i.NormalizedBrand, i.NormalizedSpecification,
                CategoryName = db.ItemCategories.Where(c => c.Id == i.CategoryId).Select(c => c.Name).First(),
                UnitCode = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.Code).First(),
                // 與上面的 exact 條件逐項對應（改其中一邊時兩邊都要改）
                Tier = i.ItemCode == text || i.RequestNo == text || oldCodeItemIds.Contains(i.Id) ? 1
                    : aliasItemIds.Contains(i.Id) || barcodeItemIds.Contains(i.Id) ? 2
                    : (compact != null && i.NormalizedPartNo == compact) || i.NormalizedName == text ? 3
                    : keywordItemIds.Contains(i.Id) ? 4
                    : 5,
            })
            .OrderBy(x => x.Tier).ThenBy(x => x.ItemCode ?? x.RequestNo).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // 只取這一頁物料的有效索引，用來說明命中原因
        var ids = rows.Select(r => r.Id).ToList();
        var aliases = await db.ItemAliases.AsNoTracking()
            .Where(a => ids.Contains(a.ItemId) && a.IsActive)
            .Select(a => new IndexText(a.ItemId, a.Text, a.NormalizedText))
            .ToListAsync(cancellationToken);
        var keywords = await db.ItemKeywords.AsNoTracking()
            .Where(k => ids.Contains(k.ItemId) && k.IsActive)
            .Select(k => new IndexText(k.ItemId, k.Text, k.NormalizedText))
            .ToListAsync(cancellationToken);

        var hits = rows.Select(r =>
        {
            var itemAliases = aliases.Where(a => a.ItemId == r.Id).ToList();
            var itemKeywords = keywords.Where(k => k.ItemId == r.Id).ToList();
            var (reason, hitText) = r.Tier switch
            {
                1 => r.ItemCode == text ? (HitReason.ItemCode, r.ItemCode!)
                    : r.RequestNo == text ? (HitReason.RequestNo, r.RequestNo)
                    : (HitReason.OldItemCode, $"{text} → {r.ItemCode}"),
                // 兩句查詢之間索引可能剛被停用（不同快照），找不到時就顯示查詢文字，不丟例外（M2 審查 R4）
                2 => itemAliases.FirstOrDefault(a => a.Normalized == text) is { } alias
                    ? (HitReason.Alias, alias.Text)
                    : (HitReason.Barcode, raw),
                3 => compact is not null && r.NormalizedPartNo == compact
                    ? (HitReason.PartNo, r.ManufacturerPartNo ?? string.Empty)
                    : (HitReason.Name, r.Name),
                4 => (HitReason.Keyword, itemKeywords.FirstOrDefault(k => k.Normalized == text)?.Text ?? raw),
                _ => (HitReason.Partial, DescribePartial(terms, r.ItemCode, r.NormalizedName, r.NormalizedBrand, r.NormalizedPartNo, r.NormalizedSpecification, itemAliases, itemKeywords)),
            };
            return new ItemSearchHit(
                r.Id, r.ItemCode, r.RequestNo, r.Name, r.Brand, r.ManufacturerPartNo, Summarize(r.Specification),
                r.CategoryName, r.UnitCode, r.Status, r.Tier, reason, hitText);
        }).ToList();

        return new PagedResult<ItemSearchHit>(hits, page, pageSize, total);
    }

    /// <summary>
    /// 多詞部分符合：每一個詞都要出現在料號、品名、品牌、型號（去符號）、規格、有效別名或有效關鍵字其中之一。
    /// 例如「伺服 驅動器」：品名有「伺服」、關鍵字有「驅動器」就算符合。
    /// </summary>
    private Expression<Func<Item, bool>> AllTermsMatch(IReadOnlyList<string> terms)
    {
        Expression<Func<Item, bool>>? all = null;
        foreach (var term in terms)
        {
            var pattern = LikePattern.Contains(term);
            var compactTerm = TextNormalizer.NormalizePartNo(term);
            var compactPattern = compactTerm is null ? null : LikePattern.Contains(compactTerm);
            Expression<Func<Item, bool>> one = i =>
                (i.ItemCode != null && EF.Functions.Like(i.ItemCode, pattern, LikePattern.Escape))
                || EF.Functions.Like(i.NormalizedName, pattern, LikePattern.Escape)
                || (i.NormalizedBrand != null && EF.Functions.Like(i.NormalizedBrand, pattern, LikePattern.Escape))
                || (compactPattern != null && i.NormalizedPartNo != null
                    && EF.Functions.Like(i.NormalizedPartNo, compactPattern, LikePattern.Escape))
                || (i.NormalizedSpecification != null && EF.Functions.Like(i.NormalizedSpecification, pattern, LikePattern.Escape))
                || db.ItemAliases.Any(a => a.ItemId == i.Id && a.IsActive && EF.Functions.Like(a.NormalizedText, pattern, LikePattern.Escape))
                || db.ItemKeywords.Any(k => k.ItemId == i.Id && k.IsActive && EF.Functions.Like(k.NormalizedText, pattern, LikePattern.Escape));
            all = all is null ? one : Combine(all, one, Expression.AndAlso);
        }
        return all ?? (_ => false);
    }

    /// <summary>部分符合時列出比中的欄位，例如「品名、關鍵字：伺服AMP」。</summary>
    private static string DescribePartial(
        IReadOnlyList<string> terms, string? itemCode, string normalizedName, string? brand, string? normalizedPartNo,
        string? specification, IReadOnlyList<IndexText> aliases, IReadOnlyList<IndexText> keywords)
    {
        bool Has(string? value) => value is not null && terms.Any(t => TextNormalizer.Normalize(value).Contains(t, StringComparison.Ordinal));
        var parts = new List<string>();
        if (Has(itemCode)) parts.Add("料號");
        if (Has(normalizedName)) parts.Add("品名");
        if (Has(brand)) parts.Add("品牌");
        if (normalizedPartNo is not null
            && terms.Any(t => TextNormalizer.NormalizePartNo(t) is { } c && normalizedPartNo.Contains(c, StringComparison.Ordinal)))
        {
            parts.Add("型號");
        }
        if (Has(specification)) parts.Add("規格");
        parts.AddRange(aliases.Where(a => terms.Any(t => a.Normalized.Contains(t, StringComparison.Ordinal))).Select(a => $"別名：{a.Text}"));
        parts.AddRange(keywords.Where(k => terms.Any(t => k.Normalized.Contains(t, StringComparison.Ordinal))).Select(k => $"關鍵字：{k.Text}"));
        return string.Join("、", parts.Take(6));
    }

    private static string? Summarize(string? specification) =>
        specification is null || specification.Length <= ItemSearch.SpecificationSummaryLength
            ? specification
            : specification[..ItemSearch.SpecificationSummaryLength] + "…";

    private static Expression<Func<Item, bool>> Or(Expression<Func<Item, bool>> a, Expression<Func<Item, bool>> b) =>
        Combine(a, b, Expression.OrElse);

    /// <summary>把兩個條件合成一個（換成同一個參數），讓 EF 轉成一句 SQL。</summary>
    private static Expression<Func<Item, bool>> Combine(
        Expression<Func<Item, bool>> a, Expression<Func<Item, bool>> b, Func<Expression, Expression, BinaryExpression> op)
    {
        var parameter = a.Parameters[0];
        var right = new ReplaceParameter(b.Parameters[0], parameter).Visit(b.Body);
        return Expression.Lambda<Func<Item, bool>>(op(a.Body, right), parameter);
    }

    private sealed record IndexText(Guid ItemId, string Text, string Normalized);

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
