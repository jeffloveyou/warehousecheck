namespace Warehouse.Application.Abstractions;

/// <summary>
/// 系統角色（docs/01「角色與權限」、ADR-0012）。名稱同時是資料庫 roles.name 與授權政策使用的字串，不可任意改名。
/// </summary>
public static class Roles
{
    /// <summary>系統管理員：帳號、系統設定（M6）。</summary>
    public const string SystemAdmin = "SystemAdmin";

    /// <summary>物料管理員：核准物料、維護分類與單位。</summary>
    public const string ItemManager = "ItemManager";

    /// <summary>倉庫主管：維護倉庫與儲位、核准期初單（之後也核准盤點、調整）。</summary>
    public const string WarehouseManager = "WarehouseManager";

    /// <summary>倉庫作業員：查詢、提出物料、建立期初單。</summary>
    public const string Operator = "Operator";

    /// <summary>只讀使用者：只能查詢物料、庫存、儲位與紀錄，沒有任何寫入權限（M6-01，ADR-0017）。</summary>
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All = [SystemAdmin, ItemManager, WarehouseManager, Operator, Viewer];

    /// <summary>畫面顯示用的中文名稱。</summary>
    public static string DisplayName(string role) => role switch
    {
        SystemAdmin => "系統管理員",
        ItemManager => "物料管理員",
        WarehouseManager => "倉庫主管",
        Operator => "倉庫作業員",
        Viewer => "只讀使用者",
        _ => role,
    };
}
