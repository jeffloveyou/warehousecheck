using System.Text.RegularExpressions;

namespace Warehouse.Domain;

/// <summary>Domain 實體共用的輸入檢查；失敗丟出 <see cref="DomainRuleException"/>（訊息給使用者看，繁體中文）。</summary>
internal static class Rules
{
    /// <summary>必填文字：去除前後空白後不得為空，且不超過長度上限。</summary>
    public static string Required(string? value, int maxLength, string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainRuleException($"{fieldName}為必填。");
        }
        return MaxLength(trimmed, maxLength, fieldName);
    }

    /// <summary>選填文字：空白視為 null；有值時不超過長度上限。</summary>
    public static string? Optional(string? value, int maxLength, string fieldName)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : MaxLength(trimmed, maxLength, fieldName);
    }

    /// <summary>代碼類欄位：必須完全符合格式（不做大小寫轉換，避免同一代碼有多種寫法）。</summary>
    public static string Matches(string? value, Regex pattern, string fieldName, string formatDescription)
    {
        if (value is null || !pattern.IsMatch(value))
        {
            throw new DomainRuleException($"{fieldName}格式不正確，應為{formatDescription}。");
        }
        return value;
    }

    private static string MaxLength(string value, int maxLength, string fieldName) =>
        value.Length <= maxLength ? value : throw new DomainRuleException($"{fieldName}不可超過 {maxLength} 字。");
}
