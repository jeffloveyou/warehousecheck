using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 可加入外層交易的交易（M6-03 匯入）：已經在交易內時（匯入整批試算／確認），服務直接在外層交易裡執行，
/// 不另開、也不自行提交或回滾，由外層決定整批成功或失敗；沒有外層交易時與原本一樣自己開、自己提交。
/// 只用在匯入會呼叫的服務方法（提出物料、新增索引、期初單明細）。
/// </summary>
internal sealed class JoinableTransaction(IDbContextTransaction? owned) : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken cancellationToken) => owned?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public Task RollbackAsync(CancellationToken cancellationToken) => owned?.RollbackAsync(cancellationToken) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal static class JoinableTransactionExtensions
{
    public static async Task<JoinableTransaction> BeginOrJoinTransactionAsync(this DatabaseFacade database, CancellationToken cancellationToken) =>
        new(database.CurrentTransaction is null ? await database.BeginTransactionAsync(cancellationToken) : null);
}
