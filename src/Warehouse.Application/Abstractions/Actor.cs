namespace Warehouse.Application.Abstractions;

/// <summary>
/// 執行操作的使用者（由 Web／Api 層從登入身分建立後傳入）。
/// 名稱會快照寫進審核紀錄；<see cref="IsItemManager"/> 用於「只有物料管理員可以核准」等資源層級檢查，
/// 端點本身另有授權政策（M1-03）。
/// </summary>
/// <param name="IsWarehouseManager">倉庫主管：可修改任何人的期初單、核准與退回（M3）。</param>
public sealed record Actor(Guid Id, string Name, bool IsItemManager, bool IsWarehouseManager = false)
{
    /// <summary>紀錄裡操作者姓名欄的長度上限（與 <c>ItemReview.ActorNameMaxLength</c> 相同）。</summary>
    public const int NameMaxLength = 100;

    /// <summary>
    /// 公用帳號（ADR-0030）：把實際操作人併進姓名，寫成「王小明（倉庫公用）」；超過上限時截短括號內的帳號名稱。
    /// Id 仍是公用帳號本身，權限與自我核准檢查不變。
    /// </summary>
    public Actor WithOperator(string operatorName)
    {
        var person = operatorName.Trim();
        var room = NameMaxLength - person.Length - 2;
        var account = Name.Length > room ? Name[..Math.Max(room, 0)] : Name;
        return this with { Name = account.Length == 0 ? person : $"{person}（{account}）" };
    }
}
