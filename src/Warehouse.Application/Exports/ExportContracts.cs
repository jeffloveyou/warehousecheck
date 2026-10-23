using Warehouse.Application.Abstractions;
using Warehouse.Application.Audit;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;

namespace Warehouse.Application.Exports;

/// <summary>
/// 匯出結果：xlsx 內容（暫存檔串流，關閉時自動刪除）與建議檔名。呼叫端負責 Dispose（網站交給 FileStreamResult）。
/// </summary>
public sealed record ExportFile(Stream Content, string FileName, int Rows) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}

/// <summary>
/// Excel 匯出（M6-03，ADR-0019）。篩選條件與畫面相同（共用查詢），超過 <see cref="MaxRows"/> 列丟 <c>export.too_large</c>
/// 要求縮小條件（不默默截斷）。資料從資料庫串流寫入 xlsx，不把整份結果載入記憶體。
/// 同時進行的匯出數由網站層限制（滿了回 429）。權限：各匯出沿用對應畫面的讀取權限，操作日誌限系統管理員。
/// </summary>
public interface IExportService
{
    const int MaxRows = 50_000;

    Task<ExportFile> ItemsAsync(ItemSearchQuery query, CancellationToken cancellationToken);

    Task<ExportFile> BalancesAsync(Guid? warehouseId, Guid? locationId, Guid? itemId, CancellationToken cancellationToken);

    Task<ExportFile> MovementsAsync(MovementQuery query, CancellationToken cancellationToken);

    /// <summary>盤點明細（含差異）；盲盤時依 <paramref name="viewer"/> 隱藏帳面量，與畫面一致。</summary>
    Task<ExportFile> StocktakeAsync(Guid id, Actor viewer, CancellationToken cancellationToken);

    Task<ExportFile> AuditAsync(AuditQuery query, CancellationToken cancellationToken);
}
