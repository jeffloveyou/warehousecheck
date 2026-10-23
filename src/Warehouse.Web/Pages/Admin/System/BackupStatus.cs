using System.Text.Json;

namespace Warehouse.Web.Pages.Admin.System;

/// <summary>備份腳本寫的一個檔案（backup.ps1／backup.sh 的 files 陣列）。</summary>
public sealed record BackupFileStatus(string Kind, string Name, double SizeMb);

/// <summary>
/// 備份狀態（M7-02）：備份腳本（以 SYSTEM／root 執行）每次結束時寫 <c>backup-status.json</c>、附件備份寫
/// <c>backup-attachments-status.json</c> 到 Backup:StatusDirectory（Windows 為 C:\Warehouse\status：SYSTEM 寫入、網站服務帳號只能讀）。
/// 網站只讀這兩個檔，不連備份目錄（備份目錄只有系統管理員能讀）。
/// </summary>
public sealed record BackupRunStatus(
    DateTimeOffset? FinishedAt,
    bool Succeeded,
    string? Message,
    double? DurationSeconds,
    IReadOnlyList<BackupFileStatus>? Files,
    string? BackupRoot,
    double? FreeGb,
    string? ExternalCopyPath,
    DateTimeOffset? ExternalCopiedAt,
    bool? ExternalCopySucceeded);

public sealed record AttachmentBackupStatus(DateTimeOffset? FinishedAt, bool Succeeded, string? Message);

public static class BackupStatusReader
{
    public const string RunFile = "backup-status.json";
    public const string AttachmentsFile = "backup-attachments-status.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>讀狀態檔；沒有設定、檔案不存在或格式不對時回傳 null（狀態頁顯示「沒有紀錄」）。</summary>
    public static async Task<T?> ReadAsync<T>(string? directory, string fileName, CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var path = Path.Combine(directory, fileName);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
