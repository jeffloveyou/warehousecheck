namespace Warehouse.Domain.Items;

/// <summary>
/// 物料狀態（ADR-0003 D-02、ADR-0011）。資料庫以文字儲存。
/// 只有 Active 可以交易；Active／Inactive 必有正式料號，其餘狀態一定沒有。
/// </summary>
public enum ItemStatus
{
    /// <summary>已提出、等待物料管理員核准；只有申請單號。</summary>
    PendingApproval,

    /// <summary>退回修改，可再次提出。</summary>
    Returned,

    /// <summary>拒絕（含判定與既有物料重複）；不占用正式料號，紀錄保留。</summary>
    Rejected,

    /// <summary>已核准生效，有正式料號，可交易。</summary>
    Active,

    /// <summary>停用；保留料號與歷史，不可再交易。</summary>
    Inactive,
}
