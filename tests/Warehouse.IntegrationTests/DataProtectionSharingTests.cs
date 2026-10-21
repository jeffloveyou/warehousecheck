using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Web;

namespace Warehouse.IntegrationTests;

/// <summary>M0-12 A/B 實例共用 Data Protection 金鑰（T-M0-12-01～02）。不需要資料庫。</summary>
public class DataProtectionSharingTests
{
    private const string Purpose = "M0-12.test";

    // T-M0-12-01
    [Fact]
    public void 共用金鑰目錄_A保護的資料B可解開_且金鑰寫入目錄()
    {
        var keys = Directory.CreateTempSubdirectory("warehouse-keys-");
        try
        {
            using var a = CreateInstance(keys.FullName);
            using var b = CreateInstance(keys.FullName);

            var token = Protector(a).Protect("表單權杖");

            Assert.Equal("表單權杖", Protector(b).Unprotect(token));
            var keyFile = Assert.Single(keys.GetFiles("key-*.xml"));
            if (OperatingSystem.IsWindows())
            {
                // Windows 金鑰檔以 DPAPI 加密，不得出現明文金鑰
                Assert.Contains("DPAPI", File.ReadAllText(keyFile.FullName));
            }
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    // T-M0-12-02
    [Fact]
    public void 不同金鑰目錄_B無法解開A的資料()
    {
        var keysA = Directory.CreateTempSubdirectory("warehouse-keys-a-");
        var keysB = Directory.CreateTempSubdirectory("warehouse-keys-b-");
        try
        {
            using var a = CreateInstance(keysA.FullName);
            using var b = CreateInstance(keysB.FullName);

            var token = Protector(a).Protect("表單權杖");

            Assert.ThrowsAny<CryptographicException>(() => Protector(b).Unprotect(token));
        }
        finally
        {
            keysA.Delete(recursive: true);
            keysB.Delete(recursive: true);
        }
    }

    private static WarehouseWebFactory CreateInstance(string keysDirectory)
    {
        var factory = new WarehouseWebFactory(
            TestDatabase.UnreachableConnectionString,
            settings: new Dictionary<string, string> { [SharedDataProtection.KeysDirectoryKey] = keysDirectory });
        factory.StartServer();
        return factory;
    }

    private static IDataProtector Protector(WarehouseWebFactory factory) =>
        factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
}
