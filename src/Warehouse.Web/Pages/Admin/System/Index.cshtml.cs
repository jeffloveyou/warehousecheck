using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api;
using Warehouse.Application.Abstractions;

namespace Warehouse.Web.Pages.Admin.System;

/// <summary>
/// 系統狀態（M7-02，限系統管理員；資料夾授權 Policies.SystemStatusRead）：程式版本與執行個體、資料庫大小、
/// WAL 封存、最近備份、附件備份、磁碟空間，並列出需要處理的警告。只在開頁時查詢，不輪詢。
/// 故障排查步驟見 docs/12。
/// </summary>
public class IndexModel(IDatabaseStatus database, IConfiguration configuration, IWebHostEnvironment environment, TimeProvider clock)
    : PageModel
{
    /// <summary>完整備份超過這個時間沒有成功就警告（每日 02:00 執行，留 2 小時餘裕）。</summary>
    public static readonly TimeSpan BackupMaxAge = TimeSpan.FromHours(26);

    /// <summary>附件每 15 分鐘備份一次，超過 1 小時沒有成功就警告。</summary>
    public static readonly TimeSpan AttachmentBackupMaxAge = TimeSpan.FromHours(1);

    public const double MinFreeGb = 10;

    public string Version { get; } = AppVersion.Current;

    public string Instance { get; private set; } = "";

    public DateTimeOffset Now { get; private set; }

    public DatabaseDetails? Database { get; private set; }

    public string? DatabaseError { get; private set; }

    public BackupRunStatus? Backup { get; private set; }

    public AttachmentBackupStatus? AttachmentBackup { get; private set; }

    public string? AttachmentsDirectory { get; private set; }

    public double? AttachmentsFreeGb { get; private set; }

    public string? LogDirectory { get; private set; }

    public List<string> Warnings { get; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Now = clock.GetUtcNow();
        Instance = configuration["Logging:File:Name"] ?? Environment.MachineName;
        LogDirectory = configuration["Logging:File:Directory"];
        try
        {
            Database = await database.GetDetailsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DatabaseError = "無法取得資料庫資訊：" + ex.Message;
        }

        var statusDirectory = configuration["Backup:StatusDirectory"];
        Backup = await BackupStatusReader.ReadAsync<BackupRunStatus>(statusDirectory, BackupStatusReader.RunFile, cancellationToken);
        AttachmentBackup = await BackupStatusReader.ReadAsync<AttachmentBackupStatus>(statusDirectory, BackupStatusReader.AttachmentsFile, cancellationToken);

        AttachmentsDirectory = configuration["Storage:AttachmentsDirectory"];
        if (!string.IsNullOrWhiteSpace(AttachmentsDirectory))
        {
            try
            {
                AttachmentsFreeGb = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AttachmentsDirectory))!).AvailableFreeSpace / 1024d / 1024 / 1024;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                AttachmentsFreeGb = null;
            }
        }

        BuildWarnings(statusDirectory);
    }

    private void BuildWarnings(string? statusDirectory)
    {
        if (DatabaseError is not null)
        {
            Warnings.Add(DatabaseError);
        }

        if (Database is { } db)
        {
            if (db.ArchiveMode != "on")
            {
                Warnings.Add("WAL 封存沒有開啟：只能還原到最近一次每日備份（RPO 24 小時，不是 15 分鐘）。執行 enable-wal-archive.ps1。");
            }
            else if (db.FailedCount > 0 && db.LastFailedAt > (db.LastArchivedAt ?? DateTimeOffset.MinValue))
            {
                Warnings.Add($"WAL 封存目前失敗（最後失敗 {Display.Time(db.LastFailedAt)}）：檢查備份目錄的空間與權限。");
            }
        }

        if (!environment.IsProduction())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(statusDirectory))
        {
            Warnings.Add("沒有設定 Backup:StatusDirectory，無法顯示備份狀態。");
        }
        else if (Backup?.FinishedAt is null)
        {
            Warnings.Add("找不到備份紀錄：備份排程可能沒有安裝（install-backup-tasks.ps1）。");
        }
        else
        {
            if (!Backup.Succeeded)
            {
                Warnings.Add($"最近一次備份失敗：{Backup.Message}");
            }

            if (Now - Backup.FinishedAt > BackupMaxAge)
            {
                Warnings.Add($"超過 {BackupMaxAge.TotalHours:0} 小時沒有完成備份（最後 {Display.Time(Backup.FinishedAt)}）。");
            }

            if (string.IsNullOrWhiteSpace(Backup.ExternalCopyPath))
            {
                Warnings.Add("備份只存在這台電腦：硬碟損壞會和資料一起遺失。請設定外部複本路徑（外接硬碟或 NAS）。");
            }
            else if (Backup.ExternalCopySucceeded == false)
            {
                Warnings.Add("備份複製到外部路徑失敗：" + Backup.ExternalCopyPath);
            }

            if (Backup.FreeGb < MinFreeGb)
            {
                Warnings.Add($"備份磁碟可用空間只剩 {Backup.FreeGb:0.0} GB。");
            }
        }

        if (AttachmentBackup?.FinishedAt is { } attachmentsAt && (!AttachmentBackup.Succeeded || Now - attachmentsAt > AttachmentBackupMaxAge))
        {
            Warnings.Add($"附件備份異常（最後 {Display.Time(attachmentsAt)}{(AttachmentBackup.Succeeded ? "" : "，失敗：" + AttachmentBackup.Message)}）。");
        }

        if (AttachmentsFreeGb < MinFreeGb)
        {
            Warnings.Add($"附件磁碟可用空間只剩 {AttachmentsFreeGb:0.0} GB。");
        }
    }
}
