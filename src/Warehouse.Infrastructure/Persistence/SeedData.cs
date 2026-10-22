using Warehouse.Domain.Inventory;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 內建計量單位與頂層分類（ADR-0006），以固定 GUID 寫進 Migration。
/// 修改這裡會產生新的 Migration；已發布的 Migration 不可直接修改。
/// **只能新增項目，不可刪除或修改既有項目**：EF 會產生 DeleteData／UpdateData，
/// 刪除會被外鍵 RESTRICT 擋下而讓 Migration 失敗，修改則會覆蓋使用者在畫面上改過的名稱。
/// </summary>
public static class SeedData
{
    public static readonly IReadOnlyList<(Guid Id, string Code, string Name, int DecimalPlaces)> Units =
    [
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000001"), "PCS", "個", 0),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000002"), "SET", "組", 0),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000003"), "BOX", "箱", 0),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000004"), "ROLL", "卷", 0),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000005"), "KG", "公斤", 3),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000006"), "G", "公克", 2),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000007"), "M", "公尺", 3),
        (new Guid("0b1f6a10-0001-4c3a-9a00-000000000008"), "L", "公升", 3),
    ];

    /// <summary>頂層分類；代碼即料號前綴（如 EL → EL-000125）。</summary>
    public static readonly IReadOnlyList<(Guid Id, string Code, string Name, int SortOrder)> Categories =
    [
        (new Guid("0b1f6a10-0002-4c3a-9a00-000000000001"), "ME", "零件", 10),
        (new Guid("0b1f6a10-0002-4c3a-9a00-000000000002"), "EL", "電控", 20),
        (new Guid("0b1f6a10-0002-4c3a-9a00-000000000003"), "RM", "原料", 30),
        (new Guid("0b1f6a10-0002-4c3a-9a00-000000000004"), "SF", "半成品", 40),
        (new Guid("0b1f6a10-0002-4c3a-9a00-000000000005"), "FG", "成品", 50),
        (new Guid("0b1f6a10-0002-4c3a-9a00-000000000006"), "CS", "耗材", 60),
    ];

    /// <summary>進出倉用途類型預設清單（M4-10）；之後由使用者在主檔維護（停用、改名、新增）。</summary>
    public static readonly IReadOnlyList<(Guid Id, PurposeDirection Direction, string Code, string Name, int SortOrder)> Purposes =
    [
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000001"), PurposeDirection.In, "PURCHASE", "採購入庫", 10),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000002"), PurposeDirection.In, "PRODUCTION", "生產入庫", 20),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000003"), PurposeDirection.In, "RETURN-MATERIAL", "退料入庫", 30),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000004"), PurposeDirection.In, "CUSTOMER-RETURN", "客戶退貨", 40),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000005"), PurposeDirection.In, "OTHER-IN", "其他入庫", 90),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000011"), PurposeDirection.Out, "PRODUCTION", "生產領料", 10),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000012"), PurposeDirection.Out, "REPAIR", "維修領用", 20),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000013"), PurposeDirection.Out, "SCRAP", "報廢", 30),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000014"), PurposeDirection.Out, "VENDOR-RETURN", "退貨給廠商", 40),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000015"), PurposeDirection.Out, "SALES", "銷售出貨", 50),
        (new Guid("0b1f6a10-0003-4c3a-9a00-000000000016"), PurposeDirection.Out, "OTHER-OUT", "其他出庫", 90),
    ];
}
