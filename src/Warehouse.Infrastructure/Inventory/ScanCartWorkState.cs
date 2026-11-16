using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

public sealed partial class ScanCartService
{
    public Task SaveDraftAsync(Guid id, ScanCartDraftInput input, Actor actor, CancellationToken cancellationToken)
    {
        if (input.Json is null || input.Json.Length > 200000) throw new DomainRuleException("核對草稿格式不正確或過大。");
        try
        {
            using var parsed = JsonDocument.Parse(input.Json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new DomainRuleException("核對草稿必須是物件。");
        }
        catch (JsonException) { throw new DomainRuleException("核對草稿格式不正確。"); }
        return MutateAsync(id, actor, cancellationToken, (cart, now) =>
        {
            cart.SetDraft(input.Json, now);
            return Task.CompletedTask;
        }, input.ExpectedVersion);
    }

    /// <summary>以帳號＋來源購物車找已提交結果，即使購物車已刪除仍可恢復。</summary>
    public async Task<ScanCartCheckoutResult?> CheckoutResultAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var key = $"cart.result:{actor.Id:N}:{id:N}";
        var result = await db.ProcessedCommands.AsNoTracking().Where(c => c.Key == key).Select(c => c.ResultRef).SingleOrDefaultAsync(cancellationToken);
        if (!Guid.TryParse(result, out var documentId)) return null;
        return await db.StockDocuments.AsNoTracking().Where(d => d.Id == documentId && d.CreatedById == actor.Id)
            .Select(d => new ScanCartCheckoutResult(d.Id, d.DocNo, d.Status)).SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>只撤銷指定加入操作；任何其他修改後必須先核對，不推測要扣哪個人的增量。</summary>
    public async Task UndoScanAsync(Guid id, ScanCartUndoInput input, Actor actor, CancellationToken cancellationToken)
    {
        if (input.OperationId == Guid.Empty || input.Original is null || input.Original.OperationId == Guid.Empty)
            throw new DomainRuleException("缺少撤銷或原加入識別碼。");
        if (input.Original.OperationId is null) throw new DomainRuleException("找不到原加入操作。");
        var key = "cart.undo:" + input.OperationId.ToString("N");
        var originalKey = "cart.add:" + input.Original.OperationId.Value.ToString("N");
        var hash = DocumentSupport.Hash(JsonSerializer.Serialize(new { CartId = id, ActorId = actor.Id, Input = input }));
        var originalHash = DocumentSupport.Hash(JsonSerializer.Serialize(new { CartId = id, ActorId = actor.Id, Input = input.Original }));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", cancellationToken, Param("key", key));
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, "cart.undo", hash, cancellationToken)) return;
        var cart = await LoadAsync(id, actor, cancellationToken);
        if (db.Entry(cart).Property<uint>("Version").CurrentValue != input.ExpectedVersion)
            throw new ConcurrencyConflictException("scan_cart.concurrency_conflict", "清單已有其他修改，請核對後手動修正數量。");
        var original = await db.ProcessedCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Key == originalKey, cancellationToken);
        if (original is null || original.RequestHash != originalHash || !Guid.TryParse(original.ResultRef, out var lineId))
            throw new DomainRuleException("原加入操作與這台購物車不符。");
        var onceKey = "cart.undone:" + input.Original.OperationId.Value.ToString("N");
        if (await db.ProcessedCommands.AnyAsync(c => c.Key == onceKey, cancellationToken)) throw new DomainRuleException("這筆掃碼已撤銷。");
        var line = cart.Lines.SingleOrDefault(l => l.Id == lineId) ?? throw new DomainRuleException("原加入行已移除。");
        if (line.Quantity < input.Original.Quantity) throw new DomainRuleException("原加入行數量已改變，請核對後手動修正。");
        var now = clock.GetUtcNow();
        if (line.Quantity == input.Original.Quantity) cart.RemoveLine(line.Id, now);
        else
        {
            var decimals = await db.Items.Where(i => i.Id == line.ItemId).Select(i => (int)db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.DecimalPlaces).First()).SingleAsync(cancellationToken);
            cart.UpdateLine(line.Id, line.Quantity - input.Original.Quantity, decimals, line.LocationId, cart.WarehouseId, now);
        }
        db.Entry(cart).Property(c => c.UpdatedAt).IsModified = true;
        db.ProcessedCommands.Add(ProcessedCommand.Create(key, "cart.undo", hash, id.ToString(), now));
        db.ProcessedCommands.Add(ProcessedCommand.Create(onceKey, "cart.undone", originalHash, id.ToString(), now));
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
