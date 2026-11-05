using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Warehousing;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

public sealed partial class StockDocumentService
{
    public async Task<ScanCartCheckoutResult> CheckoutScanCartAsync(
        Guid cartId, ScanCartCheckoutInput input, Actor actor, CancellationToken cancellationToken)
    {
        try { return await CheckoutCoreAsync(cartId, input, actor, cancellationToken); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConcurrencyConflictException("stock.busy", "其他人正在處理相同的物料或單據，請稍後重試原結帳操作。");
        }
    }

    private async Task<ScanCartCheckoutResult> CheckoutCoreAsync(
        Guid cartId, ScanCartCheckoutInput input, Actor actor, CancellationToken cancellationToken)
    {
        if (input.RequestId == Guid.Empty || input.Type is not (StockDocumentType.Inbound or StockDocumentType.Outbound)
            || input.Allocations is null || input.Allocations.Count is 0 or > StockDocument.MaxLines || input.Header is null)
            throw new DomainRuleException("請指定進貨／領料、請求識別碼與儲位分配。");
        var key = "cart.checkout:" + input.RequestId.ToString("N");
        const string command = "cart.checkout";
        var hash = DocumentSupport.Hash(JsonSerializer.Serialize(new { CartId = cartId, ActorId = actor.Id, Input = input }));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
        // 先鎖操作鍵，再鎖購物車：同鍵不同車也不能同時建立單據。
        await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", cancellationToken, Param("key", key));
        if (await DocumentSupport.AlreadyProcessedAsync(db, key, command, hash, cancellationToken))
        {
            var prior = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == input.RequestId && d.CreatedById == actor.Id, cancellationToken);
            return new(prior.Id, prior.DocNo, prior.Status);
        }
        var current = await settings.GetAsync(cancellationToken);
        if (!current.QuickCheckoutEnabled) throw new DomainRuleException("快速結帳已關閉，請使用轉草稿流程。");
        await ExecuteAsync(db, "SELECT 1 FROM scan_carts WHERE id = @id FOR UPDATE", cancellationToken, Param("id", cartId));
        var cart = await db.ScanCarts.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == cartId && c.OwnerId == actor.Id, cancellationToken)
            ?? throw new NotFoundException("scan_cart.not_found", "找不到購物車，請回清單查看。");
        if (db.Entry(cart).Property<uint>("Version").CurrentValue != input.ExpectedVersion)
            throw new ConcurrencyConflictException("scan_cart.concurrency_conflict", "購物車已在其他裝置修改，請重新讀取核對。");
        if (cart.WarehouseId is { } locked && locked != input.WarehouseId)
            throw new DomainRuleException("購物車的儲位屬於另一個倉庫。");
        await WarehouseLock.LockUsableAsync(db, input.WarehouseId, "倉庫已停用。", cancellationToken);
        await WarehouseAccess.EnsureAsync(db, actor, input.WarehouseId, cancellationToken);
        if (cart.Lines.Count == 0 || input.Allocations.Any(a => a.Quantity <= 0 || string.IsNullOrWhiteSpace(a.LocationCode))
            || input.Allocations.Any(a => cart.Lines.All(l => l.Id != a.LineId))
            || cart.Lines.Any(l => input.Allocations.Where(a => a.LineId == l.Id).Sum(a => a.Quantity) != l.Quantity))
            throw new DomainRuleException("每行儲位分配合計必須等於購物車數量，且不能有遺漏或多餘的行。");
        var items = await db.Items.AsNoTracking().Where(i => cart.Lines.Select(l => l.ItemId).Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        if (items.Values.Any(i => i.TrackingMode != TrackingMode.None))
            throw new DomainRuleException("批號／序號物料請使用既有轉草稿流程補齊資料。");
        var recipientId = input.Type == StockDocumentType.Outbound ? input.RecipientUserId ?? actor.Id : (Guid?)null;
        var recipient = recipientId is { } rid
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == rid && u.IsActive, cancellationToken) : null;
        if (recipientId is not null && recipient is null) throw new DomainRuleException("領用帳號不存在或已停用。");
        var header = input.Header with { Note = input.Header.Note ?? cart.Note };
        var effective = await EffectiveHeaderAsync(input.Type, header, null, current, cancellationToken);
        var now = clock.GetUtcNow();
        var doc = StockDocument.CreateMovement(input.RequestId, await NextDocNoAsync(input.Type, cancellationToken),
            input.Type, input.WarehouseId, effective, null, actor.Id, actor.Name, now);
        doc.MarkFromScanCart();
        doc.SetRecipient(recipientId, recipient?.DisplayName, input.ProjectReference);
        foreach (var allocation in input.Allocations)
        {
            var line = cart.Lines.Single(l => l.Id == allocation.LineId);
            await AddLineCoreAsync(doc, new DocumentLineInput(allocation.LocationCode, items[line.ItemId].ItemCode!, allocation.Quantity,
                ReservationId: allocation.ReservationId, MergeIfSame: true), actor, current, Guid.NewGuid(), cancellationToken);
        }
        RequireHeaderFields(doc, current);
        db.StockDocuments.Add(doc);
        // 過帳服務及 DB trigger 需要先看得到草稿與行；仍在同一個未提交交易。
        await SaveAsync(cancellationToken);
        if (current.DocumentApprovalEnabled) await SubmitCoreAsync(doc, actor, now, cancellationToken);
        else
        {
            doc.PostDirect(actor.Id, actor.Name, now);
            await PostCoreAsync(doc, actor, now, cancellationToken);
        }
        db.ScanCarts.Remove(cart);
        AuditLog.Add(db, actor, now, command, "stock_document", doc.Id.ToString(), null,
            new { doc.DocNo, doc.Status, CartId = cartId, doc.RecipientUserId, doc.RecipientName, doc.ProjectReference, doc.Note });
        db.ProcessedCommands.Add(ProcessedCommand.Create(key, command, hash, doc.Id.ToString(), now));
        db.ProcessedCommands.Add(ProcessedCommand.Create($"cart.result:{actor.Id:N}:{cartId:N}", command, hash, doc.Id.ToString(), now));
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(doc.Id, doc.DocNo, doc.Status);
    }
}
