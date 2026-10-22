namespace Warehouse.Domain.Scanning;

/// <summary>
/// 掃碼原始字串的前處理（M5-01）：相機、USB 條碼槍、PDA Keyboard Wedge 收到的文字都先經過這裡，再交給 <see cref="ScanCodeParser"/>。
/// - 去掉控制字元（尾碼 CR／LF／Tab、GS 等）與前後空白。
/// - 太短視為漏字（條碼槍字元間隔太小、舊電腦掉字），拒絕而不是猜測。
/// - 非 ASCII（例如中文）只拒絕「當作識別碼」的用途；批號、序號欄仍可手打中文（D-28）。
/// </summary>
public static class ScanInput
{
    /// <summary>短於這個長度的掃碼視為漏字。系統的料號、儲位 QR、原廠條碼都比這長。</summary>
    public const int MinLength = 3;

    /// <summary>單次掃碼長度上限：QR 可以很長，但系統用到的內容都在這以內，超過多半是兩筆黏在一起。</summary>
    public const int MaxLength = 200;

    /// <summary>去掉控制字元（含 Tab、CR、LF）並去頭尾空白；null 回傳空字串。</summary>
    public static string StripControl(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        var buffer = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (!char.IsControl(c))
            {
                buffer.Append(c);
            }
        }
        return buffer.ToString().Trim();
    }

    /// <summary>是否全部是 ASCII 可見字元（! 到 ~，不含空白）。</summary>
    public static bool IsPrintableAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }
        return value.Length > 0;
    }

    /// <summary>
    /// 檢查一筆掃碼：回傳清理後的文字；空白、過短、過長時回傳錯誤訊息（給使用者看）。
    /// </summary>
    public static (string Value, string? Error) Check(string? raw)
    {
        var value = StripControl(raw);
        if (value.Length == 0)
        {
            return (value, "沒有收到內容。");
        }
        if (value.Length < MinLength)
        {
            return (value, $"只收到「{value}」，太短，可能漏字，請重掃。");
        }
        if (value.Length > MaxLength)
        {
            return (value[..MaxLength], "內容過長，可能兩筆條碼黏在一起，請重掃。");
        }
        return (value, null);
    }
}
