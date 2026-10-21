using Microsoft.Extensions.FileProviders;
using Warehouse.Web;

namespace Warehouse.Proxy;

/// <summary>
/// 載入「目前實例」設定檔 upstream.json（由 switch-release 腳本改寫），檔案變更時自動重新載入。
/// 位置：WAREHOUSE_CONFIG_DIR 目錄；未設定時（開發）不載入，改用 appsettings.Development.json 的目的地。
/// 內容只放 ReverseProxy:Clusters:app:Destinations:active:Address 與 ActiveSlot，路由等固定設定在 appsettings.json。
/// </summary>
public static class UpstreamFile
{
    public const string FileName = "upstream.json";

    public static void AddUpstreamFile(this WebApplicationBuilder builder)
    {
        var directory = builder.Configuration[ExternalConfiguration.DirectoryKey];
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        // 放在最後：切換結果優先於其他設定來源。optional: false → 檔案不存在時啟動失敗，不會在沒有目的地的情況下運作。
        builder.Configuration.AddJsonFile(
            new PhysicalFileProvider(Path.GetFullPath(directory)), FileName, optional: false, reloadOnChange: true);
    }
}
