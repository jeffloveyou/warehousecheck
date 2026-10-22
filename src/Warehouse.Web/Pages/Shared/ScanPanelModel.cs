namespace Warehouse.Web.Pages.Shared;

/// <summary>
/// 連續掃碼面板的設定（<c>_ScanPanel.cshtml</c>）。<see cref="Mode"/>：inbound、outbound、transfer、opening，決定步驟順序。
/// <see cref="WarehouseId"/>：作業的倉庫（掃到別倉儲位時，移庫目的欄改填完整 QR 內容）。<see cref="ResolveUrl"/> 回傳 JSON 解析結果；<see cref="AddUrl"/> 接收 JSON 明細，成功時回傳重繪的明細表（HTML）。
/// </summary>
public sealed record ScanPanelModel(string Mode, Guid WarehouseId, string ResolveUrl, string AddUrl)
{
    public bool ShowExpiry => Mode is "inbound" or "opening";

    public string LocationLabel => Mode switch
    {
        "inbound" => "入庫儲位",
        "opening" => "儲位",
        _ => "來源儲位",
    };
}
