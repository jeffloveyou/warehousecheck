using System.Globalization;

namespace Warehouse.Web.Pages.Shared;

/// <summary>
/// 掃碼面板加入明細後的回應標頭（doc-scan.js 讀取）：相同的行會累加（使用者 2026-11-02 決定），
/// 畫面據此顯示「累加到第 N 行、現在共幾個」，以及這次填了但沿用該行而沒採用的欄位。
/// </summary>
public static class ScanLineHeaders
{
    public static void Set(HttpResponse response, int lineNo, decimal lineQuantity, bool merged, IEnumerable<string> ignored)
    {
        response.Headers["X-Line-No"] = lineNo.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Line-Quantity"] = lineQuantity.ToString("0.##########", CultureInfo.InvariantCulture);
        if (merged)
        {
            response.Headers["X-Line-Merged"] = "1";
            var list = string.Join(',', ignored);
            if (list.Length > 0)
            {
                response.Headers["X-Line-Ignored"] = list;
            }
        }
    }
}
