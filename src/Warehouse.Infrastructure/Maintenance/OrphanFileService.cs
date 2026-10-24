using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Maintenance;

/// <summary>孤兒檔檢查結果。</summary>
/// <param name="Database">比對的資料庫名稱（確認沒有連錯庫）。</param>
/// <param name="SystemFiles">附件目錄裡系統產生的檔案數（不含暫存檔）。</param>
/// <param name="Orphans">資料庫沒有引用、且超過寬限時間的檔案鍵（含寫到一半留下的 .tmp）。</param>
/// <param name="RecentSkipped">沒有引用但還在寬限時間內（可能是正在上傳、資料庫還沒提交）。</param>
/// <param name="OtherFiles">不是系統檔名（含人工放入的檔案、連結），一律不處理。</param>
/// <param name="Missing">資料庫有引用但檔案不存在（畫面會破圖；通常是只還原了資料庫、沒還原附件目錄）。</param>
/// <param name="Refused">拒絕搬移的原因（看起來連錯資料庫等）；null 表示沒有拒絕。</param>
/// <param name="MovedTo">有搬移時的隔離目錄。</param>
/// <param name="Moved">實際搬走的檔案數。</param>
/// <param name="Failed">搬移失敗的檔案鍵與原因（其餘照常搬）。</param>
public sealed record OrphanFileReport(
    string Database, int ReferencedFiles, int SystemFiles, IReadOnlyList<string> Orphans, long OrphanBytes, int RecentSkipped,
    int OtherFiles, IReadOnlyList<string> Missing, string? Refused, string? MovedTo, int Moved, IReadOnlyList<string> Failed);

/// <summary>
/// 附件目錄孤兒檔清理（M7）：上傳時檔案先寫入、資料庫後提交，資料庫失敗會留下沒有人引用的檔案。
/// - 引用清單：倉位圖片（大圖、縮圖）與單據附件（檔案、縮圖），**包含已停用的**（停用圖片的檔案依規定保留）。
/// - 只處理已知功能資料夾裡系統產生的檔名（<see cref="SystemFilePattern"/>）與寫入中斷的暫存檔；
///   **新增會存檔的功能時，必須同時更新這裡的資料夾清單與引用清單**，否則舊版執行清理時不會誤搬（不認得的資料夾一律不處理）。
/// - 最後修改時間超過寬限時間（預設 24 小時）才算孤兒，避免把正在上傳的檔案搬走。
/// - 預設只檢查；apply 時搬到附件目錄旁的隔離目錄（&lt;附件目錄&gt;-orphans\&lt;時間&gt;-&lt;短碼&gt;\），不直接刪除；
///   先寫 manifest.tsv，再逐檔搬（失敗的記下來、繼續），最後寫稽核（中途失敗也會寫）。
/// - 防呆：資料庫沒有任何引用、或孤兒超過系統檔一半時拒絕搬移（多半是連錯資料庫），要加 force 才搬。
/// </summary>
public sealed partial class OrphanFileService(WarehouseDbContext db, LocalFileStore files, TimeProvider clock)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);

    public static readonly TimeSpan MaxGrace = TimeSpan.FromDays(365);

    public static readonly Actor CommandLineActor = new(Guid.Empty, "命令列（附件清理）", false);

    public async Task<OrphanFileReport> RunAsync(TimeSpan grace, bool apply, bool force, Actor actor, CancellationToken cancellationToken)
    {
        if (grace < TimeSpan.FromHours(1) || grace > MaxGrace)
        {
            throw new ArgumentOutOfRangeException(nameof(grace), "寬限時間要在 1 小時到 365 天之間。");
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var referenced = new HashSet<string>(comparer);
        await foreach (var image in db.LocationImages.AsNoTracking().Select(i => new { i.LargeKey, i.ThumbKey }).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            referenced.Add(image.LargeKey);
            referenced.Add(image.ThumbKey);
        }
        await foreach (var attachment in db.DocumentAttachments.AsNoTracking().Select(a => new { a.FileKey, a.ThumbKey }).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            referenced.Add(attachment.FileKey);
            if (attachment.ThumbKey is not null)
            {
                referenced.Add(attachment.ThumbKey);
            }
        }

        var cutoff = clock.GetUtcNow().UtcDateTime - grace;
        var orphans = new List<(string Key, FileInfo File)>();
        var present = new HashSet<string>(comparer);
        var recent = 0;
        var other = 0;
        var root = new DirectoryInfo(files.Root);
        if (root.Exists)
        {
            // 不跟著目錄連結（junction／symlink）走，避免搬到附件目錄以外的檔案
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var file in root.EnumerateFiles("*", enumeration))
            {
                var key = Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var isTemp = TempFilePattern().IsMatch(key);
                if (file.LinkTarget is not null || (!isTemp && !SystemFilePattern().IsMatch(key)))
                {
                    other++;
                    continue;
                }
                if (!isTemp)
                {
                    present.Add(key);
                    if (referenced.Contains(key))
                    {
                        continue;
                    }
                }
                if (file.LastWriteTimeUtc > cutoff)
                {
                    recent++;
                    continue;
                }
                orphans.Add((key, file));
            }
        }
        var missing = referenced.Where(k => !present.Contains(k)).Order(StringComparer.Ordinal).ToList();
        var orphanSystemFiles = orphans.Count(o => !TempFilePattern().IsMatch(o.Key));
        var database = db.Database.GetDbConnection().Database;

        string? refused = null;
        if (apply && orphans.Count > 0 && !force)
        {
            if (referenced.Count == 0)
            {
                refused = $"資料庫 {database} 沒有引用任何附件，卻有 {present.Count} 個系統檔：可能連錯資料庫。確認無誤再加 --force。";
            }
            else if (orphanSystemFiles * 2 > present.Count)
            {
                refused = $"孤兒占系統檔 {orphanSystemFiles}／{present.Count}，超過一半：可能連錯資料庫或附件目錄。確認無誤再加 --force。";
            }
        }
        var parent = root.Parent;
        if (apply && orphans.Count > 0 && refused is null && parent is null)
        {
            refused = "附件目錄是磁碟根目錄，沒有地方放隔離目錄；請把附件目錄改到子資料夾。";
        }

        string? movedTo = null;
        var moved = 0;
        var failed = new List<string>();
        if (apply && orphans.Count > 0 && refused is null)
        {
            movedTo = Path.Combine(parent!.FullName, root.Name + "-orphans",
                $"{clock.GetLocalNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"); // 本地時間＋短碼（同一秒兩次也不衝突）
            try
            {
                Directory.CreateDirectory(movedTo);
                // 先留清單：中途中斷也知道哪些檔案應該在這裡
                var manifest = new StringBuilder("key\tbytes\tlastWriteUtc\n");
                foreach (var (key, file) in orphans)
                {
                    manifest.Append(CultureInfo.InvariantCulture, $"{key}\t{file.Length}\t{file.LastWriteTimeUtc:O}\n");
                }
                await File.WriteAllTextAsync(Path.Combine(movedTo, "manifest.tsv"), manifest.ToString(), CancellationToken.None);
                foreach (var (key, file) in orphans)
                {
                    try
                    {
                        var target = Path.Combine(movedTo, key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        file.MoveTo(target);
                        moved++;
                    }
                    catch (FileNotFoundException)
                    {
                        // 另一個清理程序已經搬走，或剛好被刪除
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed.Add($"{key}：{ex.Message}");
                    }
                }
            }
            finally
            {
                // 搬了多少就記多少（中途例外也寫）
                AuditLog.Add(db, actor, clock.GetUtcNow(), "attachments.orphans_moved", "attachments", "orphans", null,
                    new { moved, failed = failed.Count, candidates = orphans.Count, bytes = orphans.Sum(o => o.File.Length), movedTo, database });
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        return new OrphanFileReport(
            database, referenced.Count, present.Count, orphans.Select(o => o.Key).Order(StringComparer.Ordinal).ToList(),
            orphans.Sum(o => o.File.Length), recent, other, missing, refused, movedTo, moved, failed);
    }

    /// <summary>系統產生的檔名：倉位圖片、單據附件（LocationImageService、DocumentAttachmentService 的鍵格式）。</summary>
    [GeneratedRegex("^(location-images|document-attachments)/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}(-large|-thumb)?\\.(jpg|pdf)$")]
    private static partial Regex SystemFilePattern();

    /// <summary>LocalFileStore.SaveAsync 寫入中斷留下的暫存檔：&lt;系統檔名&gt;.&lt;32 碼&gt;.tmp。</summary>
    [GeneratedRegex("^(location-images|document-attachments)/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}(-large|-thumb)?\\.(jpg|pdf)\\.[0-9a-f]{32}\\.tmp$")]
    private static partial Regex TempFilePattern();
}
