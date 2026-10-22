using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IInventoryPostingService"/> 實作：**全系統唯一**寫入 stock_balances 與 stock_reservations 的程式（ADR-0014、ADR-0004）。
/// 過帳（<see cref="ApplyAsync"/>）步驟，全部在呼叫端的同一交易：
/// 1. 儲位依 id FOR UPDATE、倉庫依 id FOR SHARE：確認儲位沒停用、倉庫仍啟用。
/// 2. 有扣減的「料號＋倉庫」取 advisory lock（允許負庫存的物料另取「料號」鎖），依鍵值排序（ADR-0004 併發控制）。
/// 3. 依預留出庫：預留列 FOR UPDATE，沖銷 min(扣減量, 剩餘量)。
/// 4. 同一維度先在程式內合計淨值，依維度排序後一次 upsert（unnest 陣列，500 行也只有一次往返）；
///    資料庫 AFTER trigger trg_stock_balances_floor 檢查維度結存下限。
/// 5. 一次寫入全部異動帳（異動後結存逐行回推）。
/// 6. 寫入後檢查（新語句，看得到排在前面的交易已 commit 的結果）：倉庫可用量、負庫存的全倉合計下限。
/// 任何一步失敗都丟例外，整個交易由呼叫端回滾，不會留下部分扣帳。
/// 稽核（負結存、預留沖銷）以 EF 加入，由呼叫端最後 SaveChanges 一起寫入。
/// </summary>
public sealed class InventoryPostingService(WarehouseDbContext db) : IInventoryPostingService
{
    private const string UpsertBalancesSql = """
        INSERT INTO stock_balances (id, item_id, location_id, lot_id, serial_id, stock_status, quantity, updated_at)
        SELECT u.id, u.item_id, u.location_id, u.lot_id, u.serial_id, u.stock_status, u.delta, @now
        FROM unnest(@ids, @items, @locations, @lots, @serials, @statuses, @deltas)
            WITH ORDINALITY AS u(id, item_id, location_id, lot_id, serial_id, stock_status, delta, ord)
        ORDER BY u.ord
        ON CONFLICT (item_id, location_id, lot_id, serial_id, stock_status)
        DO UPDATE SET quantity = stock_balances.quantity + EXCLUDED.quantity, updated_at = EXCLUDED.updated_at
        RETURNING item_id, location_id, lot_id, serial_id, stock_status, quantity
        """;

    private const string InsertMovementsSql = """
        INSERT INTO stock_movements (id, document_id, line_id, item_id, location_id, lot_id, serial_id, stock_status,
            quantity_delta, balance_after, is_negative, occurred_at, actor_id, actor_name)
        SELECT u.id, @document, u.line_id, u.item_id, u.location_id, u.lot_id, u.serial_id, u.stock_status,
            u.delta, u.after, u.after < 0, @now, @actor, @actor_name
        FROM unnest(@ids, @lines, @items, @locations, @lots, @serials, @statuses, @deltas, @afters)
            AS u(id, line_id, item_id, location_id, lot_id, serial_id, stock_status, delta, after)
        """;

    /// <summary>倉庫可用量（ADR-0004 公式＋ADR-0014 決策 7）：Available 結存且儲位有效 − Open 預留剩餘量。</summary>
    private const string AvailableSql = """
        SELECT p.item_id, p.warehouse_id,
            COALESCE((SELECT SUM(b.quantity) FROM stock_balances b JOIN locations l ON l.id = b.location_id
                      WHERE b.item_id = p.item_id AND l.warehouse_id = p.warehouse_id
                        AND b.stock_status = 'Available' AND l.status = 'Available'), 0)
          - COALESCE((SELECT SUM(r.remaining_quantity) FROM stock_reservations r
                      WHERE r.item_id = p.item_id AND r.warehouse_id = p.warehouse_id AND r.status = 'Open'), 0)
        FROM unnest(@items, @warehouses) AS p(item_id, warehouse_id)
        """;

    /// <summary>料號全倉合計可用量（負庫存物料的下限檢查）：各倉 Available 結存且儲位有效 − 全部 Open 預留剩餘量。</summary>
    private const string ItemAvailableSql = """
        SELECT p.item_id,
            COALESCE((SELECT SUM(b.quantity) FROM stock_balances b JOIN locations l ON l.id = b.location_id
                      WHERE b.item_id = p.item_id AND b.stock_status = 'Available' AND l.status = 'Available'), 0)
          - COALESCE((SELECT SUM(r.remaining_quantity) FROM stock_reservations r
                      WHERE r.item_id = p.item_id AND r.status = 'Open'), 0)
        FROM unnest(@items) AS p(item_id)
        """;

    public async Task ApplyAsync(
        Guid documentId, IReadOnlyList<StockChange> changes, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }
        if (changes.Any(c => c.Delta == 0))
        {
            throw new DomainRuleException("結存變化不可為 0。");
        }
        if (changes.Any(c => c.ReservationId is not null && c.Delta > 0))
        {
            throw new DomainRuleException("只有扣減可以沖銷預留。");
        }
        await BusyGuardAsync(() => ApplyCoreAsync(documentId, changes, actor, now.ToUniversalTime(), cancellationToken));
    }

    public async Task<Guid> ReserveAsync(ReservationRequest request, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (referenceType, note) = StockReservation.ValidateRequest(request.Quantity, request.ReferenceType, request.Note);
        var id = Guid.NewGuid();
        await BusyGuardAsync(async () =>
        {
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            // 鎖順序：倉庫 FOR SHARE → advisory（先倉庫再 advisory，避免與停用倉庫形成三方等待）
            var warehouse = await LockWarehousesAsync([request.WarehouseId], cancellationToken);
            // 另取料號鎖：負庫存物料的全倉可用量檢查（出庫端持有料號鎖）必須看得到這筆預留；物料設定在取鎖後才讀
            await AdvisoryLocks.LockStockAsync(db,
                [AdvisoryLocks.StockKey(request.ItemId, request.WarehouseId), AdvisoryLocks.StockItemKey(request.ItemId)], cancellationToken);
            var item = (await LoadItemsAsync([request.ItemId], cancellationToken))[request.ItemId];
            if (!item.Active)
            {
                throw new DomainRuleException($"物料 {item.Code} 不是有效狀態，不能預留。");
            }
            RequireScale(request.Quantity, item);
            await ExecuteAsync(db, """
                INSERT INTO stock_reservations (id, item_id, warehouse_id, quantity, remaining_quantity, reference_type,
                    reference_id, note, status, created_by_id, created_by_name, created_at)
                VALUES (@id, @item, @warehouse, @qty, @qty, @type, @ref, @note, 'Open', @actor, @actor_name, @now)
                """, cancellationToken,
                Param("id", id), Param("item", request.ItemId), Param("warehouse", request.WarehouseId),
                Param("qty", request.Quantity), Param("type", referenceType), Uuid("ref", request.ReferenceId),
                Param("note", note), Param("actor", actor.Id), Param("actor_name", actor.Name), Param("now", now.ToUniversalTime()));
            // 預留不能預留不存在的庫存：下限一律 0（負庫存物料也一樣，ADR-0004 決策二第 3 點）
            var available = (await QueryAvailableAsync([(request.ItemId, request.WarehouseId)], cancellationToken)).Single().Value;
            if (available < 0)
            {
                throw Insufficient(item.Code, warehouse[request.WarehouseId].Code, available + request.Quantity, request.Quantity);
            }
            // 負庫存物料另要維持全倉合計 ≥ −下限（已持有料號鎖，M4-01 審查 #3）
            if (item.AllowNegative)
            {
                var total = (await QueryAsync(db, ItemAvailableSql, r => r.GetDecimal(1), cancellationToken,
                    Param("items", new[] { request.ItemId }))).Single();
                if (total < -item.NegativeLimit)
                {
                    throw NegativeLimit(item);
                }
            }
            AuditLog.Add(db, actor, now, "reservation.create", "stock_reservation", id.ToString(), null,
                new { ItemCode = item.Code, WarehouseCode = warehouse[request.WarehouseId].Code, request.Quantity, referenceType, request.ReferenceId, note });
        });
        return id;
    }

    public async Task<bool> ReleaseReservationAsync(Guid reservationId, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // 釋放只會讓可用量變大，不取 advisory lock（ADR-0004 併發控制規則 3）；預留列可能正被過帳鎖住，等鎖上限 5 秒
        List<decimal> released = [];
        await BusyGuardAsync(async () =>
        {
            await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);
            released = await QueryAsync(db, """
                UPDATE stock_reservations SET status = 'Released', closed_at = @now
                WHERE id = @id AND status = 'Open'
                RETURNING remaining_quantity
                """, r => r.GetDecimal(0), cancellationToken, Param("id", reservationId), Param("now", now.ToUniversalTime()));
        });
        if (released.Count == 0)
        {
            var exists = await QueryAsync(db, "SELECT 1 FROM stock_reservations WHERE id = @id", r => true, cancellationToken,
                Param("id", reservationId));
            return exists.Count == 0
                ? throw new NotFoundException("reservation.not_found", "找不到預留。")
                : false;
        }
        AuditLog.Add(db, actor, now, "reservation.release", "stock_reservation", reservationId.ToString(), null,
            new { Remaining = released[0] });
        return true;
    }

    private async Task ApplyCoreAsync(
        Guid documentId, IReadOnlyList<StockChange> changes, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, "SET LOCAL lock_timeout = '5s'", cancellationToken);

        // 1. 儲位、倉庫
        var locations = await LockLocationsAsync(changes.Select(c => c.LocationId), cancellationToken);
        var warehouses = await LockWarehousesAsync(locations.Values.Select(l => l.WarehouseId), cancellationToken);
        Guid WarehouseOf(StockChange c) => locations[c.LocationId].WarehouseId;

        // 2. advisory lock：有扣減的料號＋倉庫；允許負庫存的物料另取料號鎖。取鎖後重讀物料設定（取鎖前開啟的不算，偏保守）
        var itemIds = changes.Select(c => c.ItemId).Distinct().ToArray();
        var before = await LoadItemsAsync(itemIds, cancellationToken);
        var decreasing = changes.Where(c => c.Delta < 0).ToList();
        var keys = decreasing.Select(c => AdvisoryLocks.StockKey(c.ItemId, WarehouseOf(c)))
            .Concat(decreasing.Where(c => before[c.ItemId].AllowNegative).Select(c => AdvisoryLocks.StockItemKey(c.ItemId)));
        await AdvisoryLocks.LockStockAsync(db, keys, cancellationToken);
        var after = await LoadItemsAsync(itemIds, cancellationToken);
        if (decreasing.Any(c => !before[c.ItemId].AllowNegative && after[c.ItemId].AllowNegative))
        {
            // 取鎖期間剛開啟負庫存：這筆沒拿到料號鎖，全倉檢查可能看不到別人的扣減（M4-01 審查 #2）；請使用者重試
            throw new ConcurrencyConflictException("stock.busy", "物料的負庫存設定剛被修改，請再試一次。");
        }
        var items = itemIds.ToDictionary(id => id, id => after[id] with { AllowNegative = before[id].AllowNegative && after[id].AllowNegative });
        foreach (var change in changes)
        {
            RequireScale(Math.Abs(change.Delta), items[change.ItemId]);
            if (change.SerialId is not null && Math.Abs(change.Delta) != 1)
            {
                throw new DomainRuleException($"序號物料 {items[change.ItemId].Code} 每筆數量必須是 1。");
            }
        }

        // 3. 依預留出庫：沖銷 min(扣減量, 剩餘量)，超出部分當一般出庫（使用者 2026-10-22 決定）
        var consumed = await ConsumeReservationsAsync(changes, WarehouseOf, items, actor, now, cancellationToken);

        // 4. 結存：同維度合計淨值，扣減的維度先寫、再依維度排序，一次 upsert
        //    扣減先寫：序號唯一索引（數量 > 0）逐列檢查，序號移庫或改狀態時要先把舊位置歸零（M4-01 審查 #1）。
        //    改變列順序不會造成死結：寫同一結存列的交易都先搶同一把儲位 FOR UPDATE（儲位鎖若改 FOR SHARE 要重新檢視）
        var dimensions = changes
            .Select((c, index) => (Change: c, Index: index))
            .GroupBy(x => new Dimension(x.Change.ItemId, x.Change.LocationId, x.Change.LotId, x.Change.SerialId, x.Change.Status))
            .Select(g => (Key: g.Key, Net: g.Sum(x => x.Change.Delta), Lines: g.OrderBy(x => x.Change.Delta < 0).ThenBy(x => x.Index).ToList()))
            .OrderBy(d => d.Net >= 0).ThenBy(d => d.Key.ItemId).ThenBy(d => d.Key.LocationId).ThenBy(d => d.Key.LotId ?? Guid.Empty)
            .ThenBy(d => d.Key.SerialId ?? Guid.Empty).ThenBy(d => d.Key.Status)
            .ToList();
        Dictionary<Dimension, decimal> finals;
        try
        {
            finals = (await QueryAsync(db, UpsertBalancesSql,
                r => (Key: new Dimension(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2),
                    r.IsDBNull(3) ? null : r.GetGuid(3), Enum.Parse<StockStatus>(r.GetString(4))), Quantity: r.GetDecimal(5)),
                cancellationToken,
                Param("now", now),
                Param("ids", dimensions.Select(_ => Guid.NewGuid()).ToArray()),
                Param("items", dimensions.Select(d => d.Key.ItemId).ToArray()),
                Param("locations", dimensions.Select(d => d.Key.LocationId).ToArray()),
                UuidArray("lots", dimensions.Select(d => d.Key.LotId)),
                UuidArray("serials", dimensions.Select(d => d.Key.SerialId)),
                Param("statuses", dimensions.Select(d => d.Key.Status.ToString()).ToArray()),
                Param("deltas", dimensions.Select(d => d.Net).ToArray())))
                .ToDictionary(x => x.Key, x => x.Quantity);
        }
        catch (PostgresException ex) when (ex.ConstraintName is "ux_stock_balances_serial_in_stock" or "ck_stock_balances_serial_quantity")
        {
            throw new DomainRuleException("序號已經在庫（同一序號不能同時在兩個儲位，也不能入庫兩次）。");
        }
        catch (PostgresException ex) when (ex.ConstraintName is "ck_stock_balances_non_negative")
        {
            // 交易已中止，不能再查資料庫；用記憶體中的資料組錯誤訊息
            var failed = changes.FirstOrDefault(c => ex.MessageText.Contains(c.ItemId.ToString(), StringComparison.Ordinal)
                    && ex.MessageText.Contains(c.LocationId.ToString(), StringComparison.Ordinal))
                ?? decreasing.First();
            throw new ConflictException("stock.insufficient",
                $"物料 {items[failed.ItemId].Code} 在儲位 {locations[failed.LocationId].Code} 的結存不足（不可為負）。");
        }

        // 5. 異動帳：異動後結存由「最終值 − 淨值」回推，同維度內增加在前、扣減在後逐行累加
        var movements = new List<(StockChange Change, decimal After)>(changes.Count);
        foreach (var dimension in dimensions)
        {
            var running = finals[dimension.Key] - dimension.Net;
            foreach (var (change, _) in dimension.Lines)
            {
                running += change.Delta;
                movements.Add((change, running));
            }
        }
        await ExecuteAsync(db, InsertMovementsSql, cancellationToken,
            Param("document", documentId), Param("now", now), Param("actor", actor.Id), Param("actor_name", actor.Name),
            Param("ids", movements.Select(_ => Guid.NewGuid()).ToArray()),
            Param("lines", movements.Select(m => m.Change.LineId).ToArray()),
            Param("items", movements.Select(m => m.Change.ItemId).ToArray()),
            Param("locations", movements.Select(m => m.Change.LocationId).ToArray()),
            UuidArray("lots", movements.Select(m => m.Change.LotId)),
            UuidArray("serials", movements.Select(m => m.Change.SerialId)),
            Param("statuses", movements.Select(m => m.Change.Status.ToString()).ToArray()),
            Param("deltas", movements.Select(m => m.Change.Delta).ToArray()),
            Param("afters", movements.Select(m => m.After).ToArray()));

        // 6. 寫入後檢查
        // 6a. 負數只能出現在「Available 結存＋有效儲位」（待驗儲位不可變負）
        foreach (var dimension in dimensions.Where(d => d.Net < 0 && finals[d.Key] < 0))
        {
            var location = locations[dimension.Key.LocationId];
            if (dimension.Key.Status != StockStatus.Available || location.Status != LocationStatus.Available)
            {
                throw new ConflictException("stock.insufficient",
                    $"物料 {items[dimension.Key.ItemId].Code} 在儲位 {location.Code} 的結存不足（待驗結存不可為負）。");
            }
        }

        // 6b. 可用量：本次讓可用量變少（淨效果 < 0）才檢查，避免儲位改待驗造成的既有不足擋住無關單據
        //     - 一般物料：每個料號＋倉庫，可用量 ≥ 0
        //     - 允許負庫存的物料：料號全倉合計可用量 ≥ −下限（使用者 2026-10-22 決定），已持有料號鎖
        var availabilityNet = new Dictionary<(Guid Item, Guid Warehouse), decimal>();
        foreach (var change in changes)
        {
            var countsAsAvailable = change.Status == StockStatus.Available
                && locations[change.LocationId].Status == LocationStatus.Available;
            var pair = (change.ItemId, WarehouseOf(change));
            availabilityNet[pair] = availabilityNet.GetValueOrDefault(pair) + (countsAsAvailable ? change.Delta : 0);
        }
        foreach (var (pair, quantity) in consumed)
        {
            availabilityNet[pair] = availabilityNet.GetValueOrDefault(pair) + quantity;
        }
        var toCheck = availabilityNet.Where(x => x.Value < 0 && !items[x.Key.Item].AllowNegative).Select(x => x.Key).ToList();
        if (toCheck.Count > 0)
        {
            foreach (var (pair, available) in await QueryAvailableAsync(toCheck, cancellationToken))
            {
                if (available < 0)
                {
                    throw Insufficient(items[pair.Item].Code, warehouses[pair.Warehouse].Code,
                        available - availabilityNet[pair], -availabilityNet[pair]);
                }
            }
        }
        var negativeItems = availabilityNet.Where(x => items[x.Key.Item].AllowNegative)
            .GroupBy(x => x.Key.Item, x => x.Value)
            .Where(g => g.Sum() < 0)
            .Select(g => g.Key)
            .ToArray();
        if (negativeItems.Length > 0)
        {
            var totals = await QueryAsync(db, ItemAvailableSql, r => (Id: r.GetGuid(0), Available: r.GetDecimal(1)), cancellationToken,
                Param("items", negativeItems));
            foreach (var (id, available) in totals)
            {
                if (available < -items[id].NegativeLimit)
                {
                    throw NegativeLimit(items[id]);
                }
            }
        }

        // 7. 稽核：結存變負（待補帳來源）
        foreach (var (change, balanceAfter) in movements.Where(m => m.After < 0 && m.Change.Delta < 0))
        {
            AuditLog.Add(db, actor, now, "stock.negative", "stock_document", documentId.ToString(), null,
                new { ItemCode = items[change.ItemId].Code, Location = locations[change.LocationId].Code, change.Delta, BalanceAfter = balanceAfter });
        }
    }

    /// <summary>預留列依 id FOR UPDATE，逐行沖銷；回傳每個料號＋倉庫沖銷的總量（可用量會增加這麼多）。</summary>
    private async Task<Dictionary<(Guid Item, Guid Warehouse), decimal>> ConsumeReservationsAsync(
        IReadOnlyList<StockChange> changes, Func<StockChange, Guid> warehouseOf, Dictionary<Guid, ItemInfo> items,
        Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(Guid, Guid), decimal>();
        var ids = changes.Where(c => c.ReservationId is not null).Select(c => c.ReservationId!.Value).Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return result;
        }
        var reservations = (await QueryAsync(db, """
            SELECT id, item_id, warehouse_id, remaining_quantity, status FROM stock_reservations
            WHERE id = ANY(@ids) ORDER BY id FOR UPDATE
            """, r => (Id: r.GetGuid(0), Item: r.GetGuid(1), Warehouse: r.GetGuid(2), Remaining: r.GetDecimal(3), Status: r.GetString(4)),
            cancellationToken, Param("ids", ids)))
            .ToDictionary(r => r.Id);
        var remaining = reservations.ToDictionary(r => r.Key, r => r.Value.Remaining);
        foreach (var change in changes.Where(c => c.ReservationId is not null))
        {
            if (!reservations.TryGetValue(change.ReservationId!.Value, out var reservation))
            {
                throw new NotFoundException("reservation.not_found", "找不到預留。");
            }
            if (reservation.Status != nameof(ReservationStatus.Open))
            {
                throw new ConflictException("reservation.closed", "這筆預留已沖銷或已釋放，請重新選擇。");
            }
            if (reservation.Item != change.ItemId || reservation.Warehouse != warehouseOf(change))
            {
                throw new DomainRuleException($"預留的物料或倉庫與出庫明細不同（{items[change.ItemId].Code}）。");
            }
            var take = Math.Min(-change.Delta, remaining[reservation.Id]);
            remaining[reservation.Id] -= take;
            var pair = (reservation.Item, reservation.Warehouse);
            result[pair] = result.GetValueOrDefault(pair) + take;
        }
        var used = remaining.Where(r => r.Value != reservations[r.Key].Remaining).ToList();
        if (used.Count > 0)
        {
            await ExecuteAsync(db, """
                UPDATE stock_reservations r
                SET remaining_quantity = u.remaining,
                    status = CASE WHEN u.remaining = 0 THEN 'Consumed' ELSE r.status END,
                    closed_at = CASE WHEN u.remaining = 0 THEN @now ELSE r.closed_at END
                FROM unnest(@ids, @remaining) AS u(id, remaining)
                WHERE r.id = u.id
                """, cancellationToken, Param("now", now),
                Param("ids", used.Select(u => u.Key).ToArray()), Param("remaining", used.Select(u => u.Value).ToArray()));
            foreach (var (id, left) in used)
            {
                AuditLog.Add(db, actor, now, "reservation.consume", "stock_reservation", id.ToString(),
                    new { Remaining = reservations[id].Remaining }, new { Remaining = left });
            }
        }
        return result;
    }

    private async Task<Dictionary<Guid, LocationInfo>> LockLocationsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var sorted = ids.Distinct().Order().ToArray();
        var rows = (await QueryAsync(db,
            "SELECT id, warehouse_id, status, code FROM locations WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
            r => new LocationInfo(r.GetGuid(0), r.GetGuid(1), Enum.Parse<LocationStatus>(r.GetString(2)), r.GetString(3)),
            cancellationToken, Param("ids", sorted)))
            .ToDictionary(l => l.Id);
        if (rows.Count != sorted.Length)
        {
            throw new NotFoundException("location.not_found", "找不到儲位。");
        }
        var inactive = rows.Values.FirstOrDefault(l => l.Status == LocationStatus.Inactive);
        if (inactive is not null)
        {
            throw new DomainRuleException($"儲位 {inactive.Code} 已停用，不能過帳。");
        }
        return rows;
    }

    private async Task<Dictionary<Guid, (string Code, bool Active)>> LockWarehousesAsync(
        IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var sorted = ids.Distinct().Order().ToArray();
        var rows = (await QueryAsync(db, "SELECT id, code, is_active FROM warehouses WHERE id = ANY(@ids) ORDER BY id FOR SHARE",
            r => (Id: r.GetGuid(0), Code: r.GetString(1), Active: r.GetBoolean(2)), cancellationToken, Param("ids", sorted)))
            .ToDictionary(w => w.Id, w => (w.Code, w.Active));
        if (rows.Count != sorted.Length)
        {
            throw new NotFoundException("warehouse.not_found", "找不到倉庫。");
        }
        var inactive = rows.Values.FirstOrDefault(w => !w.Active);
        if (inactive.Code is not null)
        {
            throw new DomainRuleException($"倉庫 {inactive.Code} 已停用，不能過帳或預留。");
        }
        return rows;
    }

    private async Task<Dictionary<Guid, ItemInfo>> LoadItemsAsync(Guid[] ids, CancellationToken cancellationToken)
    {
        var rows = (await QueryAsync(db, """
            SELECT i.id, COALESCE(i.item_code, i.request_no), i.status = 'Active', i.allow_negative_stock, i.negative_stock_limit,
                   u.decimal_places
            FROM items i JOIN units u ON u.id = i.base_unit_id
            WHERE i.id = ANY(@ids)
            """,
            r => new ItemInfo(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetDecimal(4), r.GetInt16(5)),
            cancellationToken, Param("ids", ids)))
            .ToDictionary(i => i.Id);
        if (rows.Count != ids.Length)
        {
            throw new NotFoundException("item.not_found", "找不到物料。");
        }
        return rows;
    }

    private async Task<List<((Guid Item, Guid Warehouse) Pair, decimal Value)>> QueryAvailableAsync(
        IReadOnlyList<(Guid Item, Guid Warehouse)> pairs, CancellationToken cancellationToken) =>
        await QueryAsync(db, AvailableSql, r => ((r.GetGuid(0), r.GetGuid(1)), r.GetDecimal(2)), cancellationToken,
            Param("items", pairs.Select(p => p.Item).ToArray()), Param("warehouses", pairs.Select(p => p.Warehouse).ToArray()));

    /// <summary>死結、等鎖逾時、序列化失敗：交易已回滾、沒有錯帳，請使用者稍後再試。</summary>
    private static async Task BusyGuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable)
        {
            throw new ConcurrencyConflictException("stock.busy", "有其他人同時在處理相同的物料或儲位，請稍後再試一次。");
        }
    }

    private static void RequireScale(decimal quantity, ItemInfo item)
    {
        if (decimal.Round(quantity, item.DecimalPlaces) != quantity)
        {
            throw new DomainRuleException($"物料 {item.Code} 的數量最多 {item.DecimalPlaces} 位小數。");
        }
    }

    private static ConflictException Insufficient(string itemCode, string warehouseCode, decimal available, decimal requested) =>
        new("stock.insufficient",
            $"物料 {itemCode} 在倉庫 {warehouseCode} 的可用量不足：可用 {Format(available)}（已扣除預留），需要 {Format(requested)}。");

    private static ConflictException NegativeLimit(ItemInfo item) =>
        new("stock.negative_limit", $"物料 {item.Code} 超過負庫存下限（全倉合計最低 -{Format(item.NegativeLimit)}）。");

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static NpgsqlParameter UuidArray(string name, IEnumerable<Guid?> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };

    private sealed record Dimension(Guid ItemId, Guid LocationId, Guid? LotId, Guid? SerialId, StockStatus Status);

    private sealed record LocationInfo(Guid Id, Guid WarehouseId, LocationStatus Status, string Code);

    private sealed record ItemInfo(Guid Id, string Code, bool Active, bool AllowNegative, decimal NegativeLimit, int DecimalPlaces);
}
