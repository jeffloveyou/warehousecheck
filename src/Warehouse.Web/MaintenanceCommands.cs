using System.Globalization;
using Warehouse.Application.Warehousing;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Maintenance;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Web;

/// <summary>
/// 維護命令列（M7），在伺服器上以主程式執行，執行完就結束、不啟動網站：
/// <code>
/// Warehouse.Web.exe maintenance orphan-files                 只檢查（預設）
/// Warehouse.Web.exe maintenance orphan-files --apply         搬到隔離目錄並寫稽核
/// Warehouse.Web.exe maintenance orphan-files --grace-hours 48
/// Warehouse.Web.exe maintenance orphan-files --apply --force 孤兒超過一半等防呆擋下時，確認無誤後強制搬
/// </code>
/// Windows 用 scripts/windows/clean-attachments.ps1 執行（套用服務的設定與附件目錄）。
/// </summary>
public static class MaintenanceCommands
{
    /// <summary>每類最多列出幾個檔名（其餘只計數）。</summary>
    private const int ListLimit = 50;

    private const string Usage = "用法：maintenance orphan-files [--apply [--force]] [--grace-hours 1～8760]";

    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        if (args.Length == 0 || args[0] != "maintenance")
        {
            return false;
        }
        if (args.Length < 2 || args[1] != "orphan-files")
        {
            return Fail(Usage, 2);
        }

        var grace = OrphanFileService.DefaultGrace;
        var apply = false;
        var force = false;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--grace-hours" when i + 1 < args.Length
                    && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours is >= 1 and <= 8760:
                    grace = TimeSpan.FromHours(hours);
                    i++;
                    break;
                default:
                    // 打錯的選項（例如 --grace-hour）不默默忽略，免得以為設定已生效
                    return Fail($"不認得的參數或數值：{args[i]}\n{Usage}", 2);
            }
        }
        if (force && !apply)
        {
            return Fail("--force 只能和 --apply 一起用。", 2);
        }

        await using var scope = app.Services.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IFileStore>() is not LocalFileStore files)
        {
            return Fail("附件不是存在本機目錄，這個命令不適用。", 1);
        }
        var service = new OrphanFileService(
            scope.ServiceProvider.GetRequiredService<WarehouseDbContext>(), files, TimeProvider.System);
        OrphanFileReport report;
        try
        {
            report = await service.RunAsync(grace, apply, force, OrphanFileService.CommandLineActor, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"附件目錄讀寫失敗：{ex.Message}", 1);
        }

        Console.WriteLine($"附件目錄：{files.Root}；資料庫：{report.Database}");
        Console.WriteLine($"資料庫引用 {report.ReferencedFiles} 個檔案；目錄裡系統檔 {report.SystemFiles} 個；超過 {grace.TotalHours:0} 小時沒有引用的孤兒檔 {report.Orphans.Count} 個（{report.OrphanBytes / 1024.0 / 1024:0.0} MB）");
        Console.WriteLine($"寬限時間內、暫不處理 {report.RecentSkipped} 個；不是系統檔名（含人工放入的檔案）、不處理 {report.OtherFiles} 個");
        foreach (var key in report.Orphans.Take(ListLimit))
        {
            Console.WriteLine($"  孤兒：{key}");
        }
        if (report.Orphans.Count > ListLimit)
        {
            Console.WriteLine($"  …另有 {report.Orphans.Count - ListLimit} 個");
        }
        if (report.Missing.Count > 0)
        {
            Console.WriteLine($"資料庫有引用但檔案不存在 {report.Missing.Count} 個（畫面會破圖；請確認附件目錄是否和資料庫一起還原）：");
            foreach (var key in report.Missing.Take(ListLimit))
            {
                Console.WriteLine($"  遺失：{key}");
            }
        }
        if (report.Refused is not null)
        {
            return Fail($"沒有搬移：{report.Refused}", 1);
        }
        if (report.MovedTo is not null)
        {
            Console.WriteLine($"已搬 {report.Moved} 個到 {report.MovedTo}（清單在 manifest.tsv；確認一段時間沒有問題後再自行刪除）");
            foreach (var failure in report.Failed.Take(ListLimit))
            {
                Console.Error.WriteLine($"  搬移失敗：{failure}");
            }
            return Done(report.Failed.Count == 0 ? 0 : 1);
        }
        if (report.Orphans.Count > 0)
        {
            Console.WriteLine("只檢查、沒有搬移；要搬到隔離目錄請加 --apply");
        }
        return Done(0);
    }

    private static bool Fail(string message, int exitCode)
    {
        Console.Error.WriteLine(message);
        return Done(exitCode);
    }

    private static bool Done(int exitCode)
    {
        Environment.ExitCode = exitCode;
        return true;
    }
}
