using Microsoft.AspNetCore.DataProtection;

namespace Warehouse.Web;

/// <summary>
/// M0-12 A/B 實例共用 Data Protection 金鑰（表單防偽權杖、Cookie 用），讓 A 產生的權杖切到 B 後仍有效。
/// 設定 DataProtection:KeysDirectory（服務環境變數 DataProtection__KeysDirectory）時，金鑰存到該目錄；
/// Windows 另以 DPAPI（電腦層級）加密金鑰檔，Linux 以目錄權限 0750 保護。未設定時（開發、測試）維持框架預設。
/// ApplicationName 固定：A、B 的安裝路徑不同，預設的應用程式識別會不同而無法互相解密。
/// </summary>
public static class SharedDataProtection
{
    public const string KeysDirectoryKey = "DataProtection:KeysDirectory";
    public const string ApplicationName = "Warehouse";

    public static void AddSharedDataProtection(this WebApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(ApplicationName);

        var directory = builder.Configuration[KeysDirectoryKey];
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        dataProtection.PersistKeysToFileSystem(Directory.CreateDirectory(directory));
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
    }
}
