using System.Text;

namespace Warehouse.Domain.Text;

/// <summary>
/// 比對用文字正規化（docs/03「正規化」）。結果只存在 normalized_* 欄位供比對，畫面一律顯示原文。
/// M1-02 用於重複候選；M2-01 的別名／關鍵字沿用並擴充這裡的規則。
/// </summary>
public static class TextNormalizer
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
}
