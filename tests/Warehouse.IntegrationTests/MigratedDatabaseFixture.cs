using Microsoft.EntityFrameworkCore;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 測試資料庫（warehouse_test）重建後套用全部 Migration，等同「空資料庫從頭部署」。
/// 只用於測試；正式程式啟動時不會自動套用 Migration（warehouse-db-migration Skill）。
/// 使用此 fixture 的測試類別放在同一個 collection，依序執行，避免互相干擾。
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
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
