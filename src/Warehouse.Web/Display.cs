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

    /// <summary>時間一律存 UTC，顯示時轉成伺服器當地時間（台灣 +08:00）。</summary>
    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
