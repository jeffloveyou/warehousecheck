using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Warehouse.Web;

/// <summary>
/// 讓設定檔（連線字串、憑證路徑等）放在發布目錄以外（AGENTS.md 第四節）。
/// 設定 WAREHOUSE_CONFIG_DIR 指向目錄後，額外載入該目錄的 appsettings.json 與 appsettings.{環境}.json。
/// 載入順序插在環境變數之前，所以環境變數（如 ConnectionStrings__Warehouse）仍可覆寫檔案內容。
/// Windows 服務與 Linux NAS 共用此機制，不寫死任何路徑。
/// </summary>
public static class ExternalConfiguration
{
    public const string DirectoryKey = "WAREHOUSE_CONFIG_DIR";

    public static void AddExternalConfigDirectory(this WebApplicationBuilder builder)
    {
        var directory = builder.Configuration[DirectoryKey];
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"{DirectoryKey} 指向的設定目錄不存在：{directory}");
        }

        var files = new PhysicalFileProvider(Path.GetFullPath(directory));
        var sources = builder.Configuration.Sources;
        var index = sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource);
        if (index < 0)
        {
            index = sources.Count;
        }

        // 先插入環境專屬檔，再插入通用檔到同一位置，結果順序為：appsettings.json → appsettings.{環境}.json → 環境變數
        sources.Insert(index, JsonFile(files, $"appsettings.{builder.Environment.EnvironmentName}.json"));
        sources.Insert(index, JsonFile(files, "appsettings.json"));
    }

    private static JsonConfigurationSource JsonFile(IFileProvider files, string name) => new()
    {
        FileProvider = files,
        Path = name,
        Optional = true,
        ReloadOnChange = false,
    };
}
