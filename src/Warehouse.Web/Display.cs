using Warehouse.Application.Items;
using Warehouse.Domain.Items;

namespace Warehouse.Web;

/// <summary>列舉值的中文顯示名稱（資料庫與 API 用英文，畫面用中文）。</summary>
public static class Display
{
    public static string Of(ItemStatus status) => status switch
    {
        ItemStatus.PendingApproval => "待核准",
        ItemStatus.Returned => "退回",
        ItemStatus.Rejected => "已拒絕",
        ItemStatus.Active => "有效",
        ItemStatus.Inactive => "停用",
        _ => status.ToString(),
    };

    public static string Of(TrackingMode mode) => mode switch
    {
        TrackingMode.None => "不追蹤",
        TrackingMode.Lot => "批號",
        TrackingMode.Serial => "序號",
        _ => mode.ToString(),
    };

    public static string Of(ItemReviewAction action) => action switch
    {
        ItemReviewAction.Submit => "提出",
        ItemReviewAction.Update => "修改",
        ItemReviewAction.Resubmit => "重新送審",
        ItemReviewAction.Approve => "核准",
        ItemReviewAction.Return => "退回",
        ItemReviewAction.Reject => "拒絕",
        ItemReviewAction.Withdraw => "撤回",
        ItemReviewAction.Deactivate => "停用",
        ItemReviewAction.Reactivate => "重新啟用",
        _ => action.ToString(),
    };

    public static string Of(DuplicateMatchReason reason) => reason switch
    {
        DuplicateMatchReason.SamePartNo => "型號相同",
        DuplicateMatchReason.SameName => "品名相同",
        DuplicateMatchReason.SameAlias => "別名相同",
        _ => reason.ToString(),
    };

    public static string Of(Warehouse.Application.Search.HitReason reason) => reason switch
    {
        Warehouse.Application.Search.HitReason.ItemCode => "料號",
        Warehouse.Application.Search.HitReason.RequestNo => "申請單號",
        Warehouse.Application.Search.HitReason.Alias => "專屬別名",
        Warehouse.Application.Search.HitReason.PartNo => "型號",
        Warehouse.Application.Search.HitReason.Name => "品名",
        Warehouse.Application.Search.HitReason.Keyword => "共用關鍵字",
        Warehouse.Application.Search.HitReason.Partial => "部分符合",
        _ => reason.ToString(),
    };

    public static string Of(Warehouse.Domain.Warehousing.LocationStatus status) => status switch
    {
        Warehouse.Domain.Warehousing.LocationStatus.Available => "有效",
        Warehouse.Domain.Warehousing.LocationStatus.Inspection => "待驗",
        Warehouse.Domain.Warehousing.LocationStatus.Inactive => "停用",
        _ => status.ToString(),
    };

    public static string Of(Warehouse.Domain.Warehousing.LocationType type) => type switch
    {
        Warehouse.Domain.Warehousing.LocationType.Shelf => "貨架",
        Warehouse.Domain.Warehousing.LocationType.Floor => "地面",
        Warehouse.Domain.Warehousing.LocationType.Pallet => "棧板",
        Warehouse.Domain.Warehousing.LocationType.Cold => "冷藏",
        Warehouse.Domain.Warehousing.LocationType.Hazmat => "危險品區",
        _ => type.ToString(),
    };

    public static string Of(Warehouse.Domain.Inventory.StockDocumentStatus status) => status switch
    {
        Warehouse.Domain.Inventory.StockDocumentStatus.Draft => "草稿",
        Warehouse.Domain.Inventory.StockDocumentStatus.Submitted => "待核准",
        Warehouse.Domain.Inventory.StockDocumentStatus.Posted => "已過帳",
        Warehouse.Domain.Inventory.StockDocumentStatus.Cancelled => "已取消",
        _ => status.ToString(),
    };

    public static string Of(Warehouse.Domain.Inventory.StockStatus status) => status switch
    {
        Warehouse.Domain.Inventory.StockStatus.Available => "可用",
        Warehouse.Domain.Inventory.StockStatus.Quarantine => "待驗",
        _ => status.ToString(),
    };

    public static string Of(Warehouse.Domain.Inventory.StockDocumentType type) => type switch
    {
        Warehouse.Domain.Inventory.StockDocumentType.Opening => "期初",
        Warehouse.Domain.Inventory.StockDocumentType.Inbound => "進倉",
        Warehouse.Domain.Inventory.StockDocumentType.Outbound => "出倉",
        Warehouse.Domain.Inventory.StockDocumentType.Transfer => "移庫",
        _ => type.ToString(),
    };

    public static string Of(Warehouse.Domain.Inventory.ReservationStatus status) => status switch
    {
        Warehouse.Domain.Inventory.ReservationStatus.Open => "有效",
        Warehouse.Domain.Inventory.ReservationStatus.Consumed => "已用完",
        Warehouse.Domain.Inventory.ReservationStatus.Released => "已釋放",
        _ => status.ToString(),
    };

    /// <summary>異常追蹤「被拒嘗試」的動作代碼轉中文。</summary>
    public static string OfCommand(string command) => command switch
    {
        "document.confirm" => "確認",
        "document.submit" => "送審",
        "document.approve" => "核准",
        "reservation.create" => "建立預留",
        _ => command,
    };

    /// <summary>單據事件（稽核動作）轉中文。</summary>
    public static string OfDocumentEvent(string action) => action switch
    {
        "document.cancel" => "取消",
        "document.return" => "退回",
        "document.reversal_create" => "建立沖銷單",
        _ => action,
    };

    /// <summary>單號連結：期初單走 /Opening，其他走 /Documents。</summary>
    public static string DocumentUrl(Warehouse.Domain.Inventory.StockDocumentType type, Guid id) =>
        type == Warehouse.Domain.Inventory.StockDocumentType.Opening ? $"/Opening/Details/{id}" : $"/Documents/Details/{id}";

    public static string Of(Warehouse.Domain.Inventory.PartnerType type) => type switch
    {
        Warehouse.Domain.Inventory.PartnerType.Vendor => "廠商",
        Warehouse.Domain.Inventory.PartnerType.Customer => "客戶",
        Warehouse.Domain.Inventory.PartnerType.Department => "部門",
        _ => type.ToString(),
    };

    public static string Of(Warehouse.Domain.Inventory.PurposeDirection direction) => direction switch
    {
        Warehouse.Domain.Inventory.PurposeDirection.In => "進倉用",
        Warehouse.Domain.Inventory.PurposeDirection.Out => "出倉用",
        _ => direction.ToString(),
    };

    /// <summary>數量：去掉多餘的 0（12.5000 → 12.5）。</summary>
    public static string Qty(decimal value) => value.ToString("0.####");

    /// <summary>時間一律存 UTC，顯示時轉成伺服器當地時間（台灣 +08:00）。</summary>
    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
