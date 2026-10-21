namespace Warehouse.Application.Abstractions;

/// <summary>
/// 執行操作的使用者（由 Web／Api 層從登入身分建立後傳入）。
/// 名稱會快照寫進審核紀錄；<see cref="IsItemManager"/> 用於「只有物料管理員可以核准」等資源層級檢查，
/// 端點本身另有授權政策（M1-03）。
/// </summary>
/// <param name="IsWarehouseManager">倉庫主管：可修改任何人的期初單、核准與退回（M3）。</param>
public sealed record Actor(Guid Id, string Name, bool IsItemManager, bool IsWarehouseManager = false);
