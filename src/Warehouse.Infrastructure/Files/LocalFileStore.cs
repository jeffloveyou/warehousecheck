using System.Text.RegularExpressions;
using Warehouse.Application.Warehousing;

namespace Warehouse.Infrastructure.Files;

/// <summary>
/// 本機目錄的 <see cref="IFileStore"/>（M3-08）。附件目錄與發布目錄分開（AGENTS.md 第四節），A／B 槽位共用同一個目錄：
/// - Windows 服務：C:\Warehouse\data\attachments；Linux：/var/lib/warehouse/data/attachments；開發機：%LOCALAPPDATA%\Warehouse\attachments。
/// 安全：鍵只允許小寫英數、-、/ 與 .jpg／.pdf 結尾（單據附件 PDF，M4-03），組成完整路徑後再確認仍在附件目錄之下（擋住 ..\ 之類的路徑穿越）。
/// 寫入先寫暫存檔再改名，讀的人不會讀到寫一半的檔案。
/// </summary>
public sealed partial class LocalFileStore(string rootDirectory) : IFileStore
{
    private readonly string _root = Path.GetFullPath(rootDirectory);

    public string Root => _root;

    public async Task SaveAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: false);
    }

    public Stream? OpenRead(string key)
    {
        var path = Resolve(key);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true) : null;
    }

    /// <summary>把鍵轉成附件目錄下的完整路徑；格式不對或跑出目錄就拒絕。</summary>
    public string Resolve(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"檔案鍵格式不正確：{key}", nameof(key));
        }
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException($"檔案鍵超出附件目錄：{key}", nameof(key));
        }
        return full;
    }

    [GeneratedRegex("^[a-z0-9-]+(/[a-z0-9-]+)*\\.(jpg|pdf)$")]
    private static partial Regex KeyPattern();
}
