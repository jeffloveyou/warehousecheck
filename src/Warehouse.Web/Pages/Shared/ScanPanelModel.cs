namespace Warehouse.Web.Pages.Shared;

/// <summary>
/// 明細輸入區的設定（<c>_ScanPanel.cshtml</c>，M5-03 連續掃碼，M8-03 合併搜尋與選擇清單）。
/// <see cref="Mode"/>：inbound、outbound、transfer、opening，決定步驟順序。
/// <see cref="WarehouseId"/>：作業的倉庫（掃到別倉儲位時，移庫目的欄改填完整 QR 內容）。<see cref="ResolveUrl"/> 回傳 JSON 解析結果；<see cref="AddUrl"/> 接收 JSON 明細，成功時回傳重繪的明細表（HTML）。
/// <see cref="ItemSearchUrl"/>、<see cref="LocationsUrl"/>、<see cref="LotsUrl"/>：物料搜尋、儲位清單、批號序號清單（JSON，M8-02）；LotsUrl 為 null 時不提供批號選單（進倉、期初是建立新批號）。
/// </summary>
public sealed record ScanPanelModel(string Mode, Guid WarehouseId, string ResolveUrl, string AddUrl)
{
    public string? ItemSearchUrl { get; init; }

    public string? LocationsUrl { get; init; }

    public string? LotsUrl { get; init; }

    public string WarehouseCode { get; init; } = string.Empty;

    /// <summary>移庫可選的目的倉庫（可作業、啟用中）；只有一個（本倉）或關閉跨倉時不顯示選單。</summary>
    public IReadOnlyList<(string Code, string Name)> TargetWarehouses { get; init; } = [];

    /// <summary>全公司的明細欄位開關（設定頁「明細輸入欄位」）。</summary>
    public bool LotSerialEnabled { get; init; } = true;

    public bool ExpiryEnabled { get; init; } = true;

    public bool ReservationEnabled { get; init; } = true;

    public bool ShowExpiry => ExpiryEnabled && Mode is "inbound" or "opening";

    public bool ShowReservation => ReservationEnabled && Mode == "outbound";

    public bool ShowTargetWarehouse => Mode == "transfer" && TargetWarehouses.Count > 1;

    public string LocationLabel => Mode switch
    {
        "inbound" => "入庫儲位",
        "opening" => "儲位",
        _ => "來源儲位",
    };
}
