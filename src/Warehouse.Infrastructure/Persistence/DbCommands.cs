using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 在 DbContext 目前的交易內執行參數化 SQL（條件式更新、upsert、FOR UPDATE 鎖等 EF 不好表達的語句）。
/// 一律用參數，不拼接使用者輸入。必須先開啟交易（庫存寫入都在交易內，warehouse-inventory-safety）。
/// </summary>
internal static class DbCommands
{
    public static NpgsqlParameter Param(string name, object? value) => new(name, value ?? DBNull.Value);

    /// <summary>可能為 null 的 uuid 參數（DBNull 時要指定型別，PostgreSQL 才知道是 uuid）。</summary>
    public static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value is { } v ? v : DBNull.Value };

    public static async Task<int> ExecuteAsync(
        WarehouseDbContext db, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = Create(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>讀取多列，每列交給 <paramref name="read"/> 轉換。</summary>
    public static async Task<List<T>> QueryAsync<T>(
        WarehouseDbContext db, string sql, Func<DbDataReader, T> read, CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = Create(db, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }
        return rows;
    }

    private static DbCommand Create(WarehouseDbContext db, string sql, NpgsqlParameter[] parameters)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("這個 SQL 必須在資料庫交易內執行。");
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }
}
