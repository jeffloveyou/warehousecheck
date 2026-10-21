namespace Warehouse.IntegrationTests;

/// <summary>
/// 測試資料庫設定。連線字串來自環境變數 WAREHOUSE_TEST_DB；未設定時明確失敗，不默默略過。
/// </summary>
public static class TestDatabase
{
    public const string EnvironmentVariableName = "WAREHOUSE_TEST_DB";

    /// <summary>必定連不上的連線字串（埠 1、逾時 1 秒），用於模擬資料庫不可用。</summary>
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

    /// <summary>本機測試資料庫連線字串。未設定環境變數時以 Assert.Fail 失敗並說明設定方式。</summary>
    public static string ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                Assert.Fail(
                    "未設定環境變數 WAREHOUSE_TEST_DB，無法執行需要真實 PostgreSQL 的整合測試。" + Environment.NewLine +
                    "請在 PowerShell 執行以下指令（<密碼> 換成實際密碼），然後關閉並重開終端機：" + Environment.NewLine +
                    "[Environment]::SetEnvironmentVariable('WAREHOUSE_TEST_DB', " +
                    "'Host=localhost;Port=5432;Database=warehouse_test;Username=warehouse_app;Password=<密碼>;Timeout=3', 'User')");
            }

            return value!;
        }
    }

    /// <summary>以簡單字串解析取出連線字串中的 Password 值（不依賴 Npgsql）；找不到回傳 null。</summary>
    public static string? ExtractPassword(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = part[..index].Trim();
            if (key.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Pwd", StringComparison.OrdinalIgnoreCase))
            {
                var password = part[(index + 1)..].Trim();
                return password.Length == 0 ? null : password;
            }
        }

        return null;
    }
}
