namespace Warehouse.Application.Accounts;

/// <summary>
/// 依倉庫限制作業（M6-01，ADR-0017）：只限制作業（建單、核准、預留、盤點、儲位維護、匯入），查詢不受限制。
/// 真正的檢查在各業務服務內（每次操作查資料庫，權限變更立即生效）；這個介面給畫面過濾下拉選單用。
/// 被拒絕時的錯誤碼是 <c>warehouse.forbidden</c>（HTTP 403）。
/// </summary>
public interface IWarehouseAccess
{
    /// <summary>可作業的倉庫 ID；回傳 null 表示全部倉庫。</summary>
    Task<IReadOnlySet<Guid>?> GetOperableWarehouseIdsAsync(Guid userId, CancellationToken cancellationToken);
}
