using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Warehouse.Domain.Text;

/// <summary>
/// 比對用文字正規化（docs/03「正規化」）。結果只存在 normalized_* 欄位供比對，畫面一律顯示原文。
/// M1-02 用於重複候選；M2-01 的別名／關鍵字沿用並擴充這裡的規則。
/// </summary>
public static partial class TextNormalizer
{
    /// <summary>
    /// 一般文字：Unicode NFKC（全形英數轉半形）→ 轉大寫 → 連續空白合併為一個 → 去頭尾空白。
    /// 空白或 null 回傳空字串。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            // 零寬空白（U+200B）、方向標記等看不見的字元直接移除：
            // 否則「伺服AMP」和「伺服AMP＋零寬空白」外觀相同，卻會被當成兩個不同的別名（M2 審查 S7）
            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control)
            {
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 型號：在 <see cref="Normalize"/> 之後再去掉空白與 - . / _，讓 MR-J4-10A、mr j4 10a、ＭＲ／Ｊ４．１０Ａ 視為相同。
    /// 去掉後沒有內容時回傳 null（表示沒有型號可比對）。
    /// </summary>
    public static string? NormalizePartNo(string? value)
    {
        var normalized = Normalize(value);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (ch is not (' ' or '-' or '.' or '/' or '_'))
            {
                builder.Append(ch);
            }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// 正規化後的文字是否長得像正式料號或申請單號（EL-000125、MR-000125）。
    /// 別名、關鍵字不可使用這種格式，避免和正式識別碼混淆（ADR-0013）。
    /// </summary>
    public static bool LooksLikeItemIdentifier(string normalized) => ItemIdentifierPattern().IsMatch(normalized);

    [GeneratedRegex("^[A-Z0-9]{2,4}-[0-9]{6,}$")]
    private static partial Regex ItemIdentifierPattern();
}