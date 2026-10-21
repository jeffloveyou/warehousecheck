namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 已處理的命令（冪等鍵，processed_commands）。和業務資料在同一交易寫入：
/// - 同鍵＋同內容（RequestHash 相同）→ 回傳上次結果，不再過帳。
/// - 同鍵＋不同內容 → 拒絕。
/// 去重由主鍵唯一約束保證，不只靠「先查再寫」（warehouse-inventory-safety）。
/// </summary>
public sealed class ProcessedCommand
{
    public string Key { get; private set; } = string.Empty;

    public string CommandType { get; private set; } = string.Empty;

    /// <summary>請求內容的 SHA-256（十六進位）。</summary>
    public string RequestHash { get; private set; } = string.Empty;

    /// <summary>處理結果的參照，例如單據 ID。</summary>
    public string ResultRef { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static ProcessedCommand Create(string key, string commandType, string requestHash, string resultRef, DateTimeOffset now) =>
        new() { Key = key, CommandType = commandType, RequestHash = requestHash, ResultRef = resultRef, CreatedAt = now.ToUniversalTime() };
}
