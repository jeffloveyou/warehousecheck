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

    /// <summary>倉庫作業員：查詢、提出物料。</summary>
    public const string Operator = "Operator";

    public static readonly IReadOnlyList<string> All = [SystemAdmin, ItemManager, Operator];
}
