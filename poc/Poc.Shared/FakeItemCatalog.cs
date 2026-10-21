namespace Poc.Shared;

/// <summary>PoC 用的物料資料（不是正式 Domain 模型）。</summary>
public sealed record PocItem(string ItemCode, string Name, string Category, string Location);

/// <summary>搜尋結果的一筆，附命中原因。</summary>
public sealed record PocSearchHit(PocItem Item, string HitReason);

/// <summary>伺服器分頁結果。</summary>
public sealed record PocSearchPage(IReadOnlyList<PocSearchHit> Hits, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => TotalCount == 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
}

/// <summary>
/// 以固定亂數種子產生 20,000 筆假物料，讓兩個前端 PoC 的資料完全相同。
/// 搜尋在伺服器端完成並分頁，模擬正式系統「不把整個清單送到瀏覽器」的原則。
/// </summary>
public static class FakeItemCatalog
{
    public const int ItemCount = 20_000;
    public const int PageSize = 20;
    public const int MaxQueryLength = 50;

    private static readonly string[] Prefixes = ["ME", "EL", "RM", "SF", "FG", "CS"];
    private static readonly string[] Categories = ["零件", "電控", "原料", "半成品", "成品", "耗材"];
    private static readonly string[] Nouns =
        ["伺服放大器", "伺服馬達", "近接開關", "繼電器", "螺絲", "軸承", "皮帶", "感測器", "電磁閥", "端子台", "保險絲", "電線", "鋁板", "包裝箱", "潤滑油"];
    private static readonly string[] Brands = ["Mitsubishi", "Omron", "SMC", "Panasonic", "NSK", "THK", "Keyence", "Schneider"];

    public static IReadOnlyList<PocItem> Items { get; } = Generate();

    private static readonly Dictionary<string, PocItem> ByCode =
        Items.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);

    private static List<PocItem> Generate()
    {
        var random = new Random(20261021);
        var list = new List<PocItem>(ItemCount);
        for (var i = 1; i <= ItemCount; i++)
        {
            var c = random.Next(Prefixes.Length);
            var noun = Nouns[random.Next(Nouns.Length)];
            var brand = Brands[random.Next(Brands.Length)];
            var model = $"{(char)('A' + random.Next(26))}{random.Next(10, 99)}-{random.Next(100, 999)}";
            var location = $"W{random.Next(1, 4)}-{(char)('A' + random.Next(6))}-{random.Next(1, 30):00}-{random.Next(1, 6):00}-{random.Next(1, 10):00}";
            list.Add(new PocItem($"{Prefixes[c]}-{i:000000}", $"{noun} {brand} {model}", Categories[c], location));
        }
        return list;
    }

    /// <summary>依料號精確查詢（掃碼流程使用）。</summary>
    public static PocItem? FindByCode(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : ByCode.GetValueOrDefault(code.Trim());

    /// <summary>料號精確命中優先，其次品名包含；伺服器分頁，每頁 20 筆。</summary>
    public static PocSearchPage Search(string? query, int page)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0 || q.Length > MaxQueryLength)
        {
            return new PocSearchPage([], 0, 1, PageSize);
        }

        page = Math.Max(1, page);
        var exact = FindByCode(q);
        var hits = new List<PocSearchHit>();
        if (exact is not null)
        {
            hits.Add(new PocSearchHit(exact, "料號精確命中"));
        }

        hits.AddRange(Items
            .Where(i => !ReferenceEquals(i, exact) && i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(i => new PocSearchHit(i, "品名部分符合")));

        var pageHits = hits.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        return new PocSearchPage(pageHits, hits.Count, page, PageSize);
    }
}
