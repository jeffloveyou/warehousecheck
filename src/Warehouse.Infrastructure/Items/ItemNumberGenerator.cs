using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Items;

/// <summary>
/// 申請單號與正式料號產生器（ADR-0011）。兩者分開管理：
/// - 申請單號：PostgreSQL sequence，nextval 在併發下不重號；交易失敗會跳號（申請單號不要求連號）。
/// - 正式料號：item_code_counters 每個前綴一列，在核准交易內遞增；該列鎖到交易結束，
///   同時核准會排隊、不重號；交易回滾時號碼一起退回，所以退回／拒絕／失敗都不消耗料號。
/// 兩個方法都必須在呼叫端開啟的交易內執行。
/// </summary>
internal sealed class ItemNumberGenerator(WarehouseDbContext db)
{
    private const string NextItemNumberSql = """
        INSERT INTO item_code_counters (prefix, last_number) VALUES (@prefix, 1)
        ON CONFLICT (prefix) DO UPDATE SET last_number = item_code_counters.last_number + 1
        RETURNING last_number
        """;

    /// <summary>取得下一個申請單號，例如 MR-000125。</summary>
    public async Task<string> NextRequestNoAsync(CancellationToken cancellationToken)
    {
        var number = await ScalarAsync($"SELECT nextval('{WarehouseDbContext.RequestNoSequence}')", null, cancellationToken);
        return $"MR-{number:D6}";
    }

    /// <summary>
    /// 取得該前綴的下一個正式料號，例如 EL-000125（流水號超過 6 位數時自動加長）。
    /// 號碼已被物料管理員指定給其他物料、或保留為歷史料號時往下跳（D-32，ADR-0025；跳過的號碼不回收）。
    /// </summary>
    public async Task<string> NextItemCodeAsync(string prefix, Guid itemId, CancellationToken cancellationToken)
    {
        await AdvisoryLocks.LockItemCodePrefixesAsync(db, [prefix], cancellationToken);
        var rules = new ItemCodeRules(db);
        for (var attempt = 0; attempt < MaxSkips; attempt++)
        {
            var number = await ScalarAsync(NextItemNumberSql, new NpgsqlParameter("prefix", prefix), cancellationToken);
            var code = $"{prefix}-{number:D6}";
            if (!await rules.IsTakenAsync(code, itemId, cancellationToken))
            {
                return code;
            }
        }
        throw new InvalidOperationException($"前綴 {prefix} 連續 {MaxSkips} 個號碼都已被占用，請檢查料號資料。");
    }

    /// <summary>連續跳號上限（防止資料異常時無窮迴圈；正常情況跳過的只有手動指定的少數號碼）。</summary>
    private const int MaxSkips = 1000;

    private async Task<long> ScalarAsync(string sql, DbParameter? parameter, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("號碼產生必須在資料庫交易內執行。");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.Add(parameter);
        }
        // nextval 與 last_number 都是 bigint
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
