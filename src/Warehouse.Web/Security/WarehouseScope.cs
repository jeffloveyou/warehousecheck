using Warehouse.Api.Authorization;
using Warehouse.Application.Accounts;

namespace Warehouse.Web.Security;

/// <summary>
/// 畫面用：登入者可作業的倉庫（M6-01b）。只用來過濾建單等下拉選單，真正的檢查在業務服務內（warehouse.forbidden）。
/// </summary>
public static class WarehouseScope
{
    /// <summary>回傳 null 表示全部倉庫。</summary>
    public static Task<IReadOnlySet<Guid>?> OperableWarehousesAsync(this HttpContext context, CancellationToken cancellationToken) =>
        context.RequestServices.GetRequiredService<IWarehouseAccess>()
            .GetOperableWarehouseIdsAsync(context.User.ToActor().Id, cancellationToken);

    public static bool Allows(this IReadOnlySet<Guid>? operable, Guid warehouseId) =>
        operable is null || operable.Contains(warehouseId);
}
