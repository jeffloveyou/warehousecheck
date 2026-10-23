using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Errors;
using Warehouse.Application.Exports;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Audit;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Exports;

/// <summary>
/// <see cref="IExportService"/> 實作（M6-03）。篩選沿用畫面的查詢（ItemQueryService.Filter、StockQueryService、AuditQueryService.Filter），
/// 先計數（最多讀到上限＋1 列）再以 AsAsyncEnumerable 串流寫入 <see cref="XlsxWriter"/>。
/// </summary>
public sealed class ExportService(WarehouseDbContext db, IStocktakeService stocktakes, TimeProvider clock) : IExportService
{
    public async Task<ExportFile> ItemsAsync(ItemSearchQuery query, CancellationToken cancellationToken)
    {
        var items = ItemQueryService.Filter(db, query);
        var rows = await EnsureLimitAsync(items, cancellationToken);
        var data = Map(items.OrderBy(i => i.ItemCode ?? i.RequestNo)
            .Select(i => new
            {
                i.ItemCode, i.RequestNo, i.Name,
                Category = db.ItemCategories.Where(c => c.Id == i.CategoryId).Select(c => c.Name).First(),
                i.Brand, i.ManufacturerPartNo, i.Specification,
                Unit = db.Units.Where(u => u.Id == i.BaseUnitId).Select(u => u.Code).First(),
                i.TrackingMode, i.Status, i.AllowNegativeStock, i.NegativeStockLimit, i.CreatedAt, i.UpdatedAt,
            })
            .AsAsyncEnumerable(), i => new object?[]
            {
                i.ItemCode, i.RequestNo, i.Name, i.Category, i.Brand, i.ManufacturerPartNo, i.Specification, i.Unit,
                i.TrackingMode.ToString(), i.Status.ToString(), i.AllowNegativeStock, i.NegativeStockLimit, i.CreatedAt, i.UpdatedAt,
            });
        return await FileAsync("物料", ["料號", "申請單號", "品名", "分類", "品牌", "原廠型號", "規格", "基本單位", "追蹤模式", "狀態",
            "允許負庫存", "負庫存下限", "建立時間", "更新時間"], Translate(data, 8, 9), rows, cancellationToken);
    }

    public async Task<ExportFile> BalancesAsync(Guid? warehouseId, Guid? locationId, Guid? itemId, CancellationToken cancellationToken)
    {
        var (filtered, query) = new StockQueryService(db, clock).QueryBalances(warehouseId, locationId, itemId);
        var rows = await EnsureLimitAsync(filtered, cancellationToken);
        var data = Map(query.AsAsyncEnumerable(), b => new object?[]
        {
            b.WarehouseCode, b.LocationCode, b.ItemCode, b.ItemName, b.LotNo, b.ExpiryDate, b.SerialNo,
            b.Status == StockStatus.Available ? "可用" : "待驗", b.LocationAvailable ? "有效" : "非有效", b.Quantity, b.UnitCode,
        });
        return await FileAsync("庫存結存", ["倉庫", "儲位", "料號", "品名", "批號", "有效期限", "序號", "結存狀態", "儲位狀態", "數量", "單位"],
            data, rows, cancellationToken);
    }

    public async Task<ExportFile> MovementsAsync(MovementQuery query, CancellationToken cancellationToken)
    {
        var stock = new StockQueryService(db, clock);
        var filtered = stock.FilterMovements(query);
        var rows = await EnsureLimitAsync(filtered, cancellationToken);
        var data = Map(stock.MovementRows(filtered).AsAsyncEnumerable(), m => new object?[]
        {
            m.OccurredAt, m.DocNo, m.DocType.ToString(), m.WarehouseCode, m.LocationCode, m.ItemCode, m.ItemName, m.LotNo, m.SerialNo,
            m.Status == StockStatus.Available ? "可用" : "待驗", m.QuantityDelta, m.BalanceAfter, m.ActorName,
        });
        return await FileAsync("異動歷史", ["時間", "單號", "單據類型", "倉庫", "儲位", "料號", "品名", "批號", "序號", "結存狀態", "異動量",
            "異動後結存", "操作者"], Translate(data, docTypeColumn: 2), rows, cancellationToken);
    }

    public async Task<ExportFile> StocktakeAsync(Guid id, Actor viewer, CancellationToken cancellationToken)
    {
        var details = await stocktakes.GetAsync(id, new StocktakeLineFilter(PageSize: Stocktake.MaxLines), viewer, cancellationToken)
            ?? throw new NotFoundException("stocktake.not_found", "找不到盤點單。");
        var hidden = details.QuantitiesHidden;
        var data = Map(details.Lines.Items.ToAsyncEnumerable(), l => new object?[]
        {
            l.LineNo, l.LocationCode, l.ItemCode, l.ItemName, l.LotNo, l.SerialNo, l.StockStatus == StockStatus.Available ? "可用" : "待驗",
            l.IsAdded ? "是" : "否", hidden ? null : l.SnapshotQuantity, hidden ? null : l.BookQuantityAtCount, l.CountedQuantity,
            hidden ? null : l.Difference, l.UnitCode, l.CountedByName, l.CountedAt,
        });
        return await FileAsync($"盤點 {details.StocktakeNo}", ["#", "儲位", "料號", "品名", "批號", "序號", "結存狀態", "現場找到",
            "快照量", "計數當下帳面", "實盤量", "差異", "單位", "盤點人員", "計數時間"], data, details.Lines.Items.Count, cancellationToken,
            fileName: details.StocktakeNo);
    }

    public async Task<ExportFile> AuditAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var logs = AuditQueryService.Filter(db, query);
        var rows = await EnsureLimitAsync(logs, cancellationToken);
        var data = Map(AuditQueryService.Rows(logs).AsAsyncEnumerable(), a => new object?[]
        {
            a.OccurredAt, a.ActorName, a.Action, a.EntityType, a.EntityId, a.Before, a.After,
        });
        return await FileAsync("操作日誌", ["時間", "操作者", "動作", "對象類型", "對象 ID", "變更前", "變更後"], data, rows, cancellationToken);
    }

    /// <summary>超過上限就拒絕（要求縮小條件，不截斷）；只數到上限＋1 列，不做整表 COUNT。</summary>
    private static async Task<int> EnsureLimitAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
    {
        var count = await query.Take(IExportService.MaxRows + 1).CountAsync(cancellationToken);
        return count > IExportService.MaxRows
            ? throw new ConflictException("export.too_large",
                $"符合條件的資料超過 {IExportService.MaxRows:N0} 筆，請縮小條件（例如指定倉庫、期間或料號）後再匯出。")
            : count;
    }

    private async Task<ExportFile> FileAsync(
        string sheet, string[] headers, IAsyncEnumerable<object?[]> rows, int count, CancellationToken cancellationToken, string? fileName = null)
    {
        // 寫到「只寫」的暫存檔：System.IO.Packaging 以串流模式壓縮，不把整份未壓縮的工作表留在記憶體
        // （5 萬筆、每筆 3KB 的操作日誌用 MemoryStream 時峰值多 400 MB，M6-03a 審查）。下載完關閉串流即刪除檔案。
        var path = Path.Combine(Path.GetTempPath(), $"warehouse-export-{Guid.NewGuid():N}.xlsx");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await XlsxWriter.WriteAsync(output, sheet.Length > 31 ? sheet[..31] : sheet, headers, rows, cancellationToken);
            }
            var stamp = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
            var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return new ExportFile(content, $"{fileName ?? sheet}-{stamp}.xlsx", count);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static async IAsyncEnumerable<object?[]> Map<T>(IAsyncEnumerable<T> source, Func<T, object?[]> map)
    {
        await foreach (var row in source)
        {
            yield return map(row);
        }
    }

    /// <summary>列舉值轉中文（追蹤模式、物料狀態、單據類型），其餘欄位原樣。</summary>
    private static async IAsyncEnumerable<object?[]> Translate(
        IAsyncEnumerable<object?[]> rows, int trackingColumn = -1, int statusColumn = -1, int docTypeColumn = -1)
    {
        await foreach (var row in rows)
        {
            if (trackingColumn >= 0 && Enum.TryParse<TrackingMode>(row[trackingColumn] as string, out var tracking))
            {
                row[trackingColumn] = tracking switch { TrackingMode.Lot => "批號", TrackingMode.Serial => "序號", _ => "不追蹤" };
            }
            if (statusColumn >= 0 && Enum.TryParse<ItemStatus>(row[statusColumn] as string, out var status))
            {
                row[statusColumn] = status switch
                {
                    ItemStatus.PendingApproval => "待核准", ItemStatus.Returned => "退回", ItemStatus.Rejected => "已拒絕",
                    ItemStatus.Active => "有效", ItemStatus.Inactive => "停用", _ => status.ToString(),
                };
            }
            if (docTypeColumn >= 0 && Enum.TryParse<StockDocumentType>(row[docTypeColumn] as string, out var type))
            {
                row[docTypeColumn] = type switch
                {
                    StockDocumentType.Opening => "期初", StockDocumentType.Inbound => "進倉", StockDocumentType.Outbound => "出倉",
                    StockDocumentType.Transfer => "移庫", StockDocumentType.Adjustment => "盤點調整", _ => type.ToString(),
                };
            }
            yield return row;
        }
    }
}
