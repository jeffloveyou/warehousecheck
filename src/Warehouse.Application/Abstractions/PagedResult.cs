namespace Warehouse.Application.Abstractions;

/// <summary>伺服器端分頁結果（AGENTS.md 第三節：搜尋、排序、分頁在伺服器處理）。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;
}

/// <summary>分頁參數的共用限制：每頁預設 20 筆、上限 100 筆。</summary>
public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>頁碼上限：避免 (頁碼-1)×每頁筆數 溢位成負的 OFFSET。</summary>
    public const int MaxPage = 100_000;

    /// <summary>搜尋關鍵字長度上限（超過的部分截掉）。</summary>
    public const int MaxKeywordLength = 100;

    public static int ClampPage(int page) => Math.Clamp(page, 1, MaxPage);

    public static int ClampPageSize(int pageSize) =>
        pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
}
