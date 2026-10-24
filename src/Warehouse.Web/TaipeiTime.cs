namespace Warehouse.Web;

/// <summary>
/// 使用者輸入的當地時間（台灣 +08:00，無日光節約時間）與 UTC 的轉換。
/// 優先用系統時區資料庫（Linux 的 Asia/Taipei、Windows 的 Taipei Standard Time）；
/// 容器或精簡系統找不到時退回固定 +8，結果相同，不依賴伺服器主機時區設定。
/// </summary>
public static class TaipeiTime
{
    private static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Taipei", "Taipei Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // 試下一個 ID
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");
    }

    /// <summary>datetime-local 欄位的值（當地時間）轉成 UTC 時間點；空白回傳 null。</summary>
    public static DateTimeOffset? FromLocalInput(DateTime? local)
    {
        if (local is not { } value)
        {
            return null;
        }
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>當地日期的 00:00 對應的 UTC 時間點。</summary>
    public static DateTimeOffset StartOfDay(DateOnly date) => FromLocalInput(date.ToDateTime(TimeOnly.MinValue))!.Value;

    /// <summary>時間點轉成台灣當地時間（含 +08:00 位移），用於日誌與狀態頁顯示。</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);
}
