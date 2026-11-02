namespace Warehouse.Application.Settings;

/// <summary>使用模式（M12，ADR-0027）：審核相關設定的預設組合，只是設定頁上的捷徑，本身不存成設定。</summary>
public enum UsageMode
{
    /// <summary>簡易（小公司）：物料、期初、盤點、進出倉單都不審核；自核全開、不盲盤。</summary>
    Simple,

    /// <summary>標準（＝系統預設）：物料、期初、盤點要審核，進出倉單確認即過帳；不可自核、盲盤。</summary>
    Standard,

    /// <summary>嚴格：四種都要審核；不可自核、盲盤。</summary>
    Strict,
}

public static class UsageModes
{
    public static readonly IReadOnlyList<UsageMode> All = [UsageMode.Simple, UsageMode.Standard, UsageMode.Strict];

    /// <summary>套用模式：只改審核相關的 8 項，欄位開關、保留期限等其他設定不動。</summary>
    public static SystemSettings Apply(SystemSettings settings, UsageMode mode)
    {
        var approval = mode != UsageMode.Simple;
        var selfApprove = mode == UsageMode.Simple;
        return settings with
        {
            ItemApprovalEnabled = approval,
            OpeningApprovalEnabled = approval,
            StocktakeApprovalEnabled = approval,
            DocumentApprovalEnabled = mode == UsageMode.Strict,
            AllowSelfApproveOpening = selfApprove,
            AllowSelfApproveStocktake = selfApprove,
            AllowSelfApproveDocuments = selfApprove,
            StocktakeBlindCount = approval,
        };
    }

    /// <summary>目前設定符合哪個模式；都不符合（管理員逐項調整過）回傳 null，畫面顯示「自訂」。</summary>
    public static UsageMode? Match(SystemSettings settings) =>
        All.Cast<UsageMode?>().FirstOrDefault(m => Apply(settings, m!.Value) == settings);

    public static string Label(UsageMode mode) => mode switch
    {
        UsageMode.Simple => "簡易（小公司）",
        UsageMode.Standard => "標準",
        UsageMode.Strict => "嚴格",
        _ => mode.ToString(),
    };
}
