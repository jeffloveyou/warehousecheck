using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 測試資料庫（warehouse_test）重建後套用全部 Migration，等同「空資料庫從頭部署」。
/// 只用於測試；正式程式啟動時不會自動套用 Migration（warehouse-db-migration Skill）。
/// 規則：凡是讀寫資料表的測試，都要加 [Collection(MigratedDatabaseCollection.Name)]，
/// 才會依序執行，不會和這裡的 DROP SCHEMA 或 Migration 降回測試同時進行。
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        // 防呆：只允許清空名稱以 _test 結尾的資料庫，避免 WAREHOUSE_TEST_DB 誤設成開發或正式資料庫
        var database = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString).Database;
        if (database is null || !database.EndsWith("_test", StringComparison.Ordinal))
        {
            Assert.Fail(
                $"WAREHOUSE_TEST_DB 指向的資料庫「{database}」名稱不是以 _test 結尾。" +
                "此測試會刪除整個 public schema，為避免誤刪開發或正式資料，已停止。");
        }

        await using var db = CreateContext();
        // 清除上次測試留下的所有資料表（僅測試庫），再從第一個 Migration 套用
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static WarehouseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(TestDatabase.ConnectionString).Options);
}

[CollectionDefinition(Name)]
public sealed class MigratedDatabaseCollection : ICollectionFixture<MigratedDatabaseFixture>
{
    public const string Name = "MigratedDatabase";
}
