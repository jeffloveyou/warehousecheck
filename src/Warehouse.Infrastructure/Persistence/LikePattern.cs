namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 把使用者輸入轉成 LIKE／ILIKE 的「包含」樣式。% _ \ 是萬用字元，要先跳脫，
/// 否則「100%」會被當成任意字串。跳脫字元固定是反斜線，查詢時要傳 <see cref="Escape"/>。
/// </summary>
internal static class LikePattern
{
    public const string Escape = "\\";

    public static string Contains(string value) => "%" + Escaped(value) + "%";

    /// <summary>只跳脫萬用字元（呼叫端自己加 %，例如「開頭符合」）。</summary>
    public static string Escaped(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
