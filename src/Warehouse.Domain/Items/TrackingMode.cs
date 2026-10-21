namespace Warehouse.Domain.Items;

/// <summary>追蹤模式（ADR-0003 D-06）。已有交易的物料不可變更（AGENTS.md 第六節）。</summary>
public enum TrackingMode
{
    /// <summary>不追蹤批號或序號；唯一可開啟負庫存的模式（ADR-0004）。</summary>
    None,

    /// <summary>批號管理。</summary>
    Lot,

    /// <summary>單件序號管理。</summary>
    Serial,
}
