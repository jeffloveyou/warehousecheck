using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;

namespace Warehouse.Api.Authorization;

/// <summary>
/// 授權政策（ADR-0012）。頁面與 API 用同一組政策名稱，伺服器端強制檢查（前端隱藏按鈕只是方便）。
/// 預設政策（FallbackPolicy）要求登入；不需要登入的端點要明確標示 AllowAnonymous。
/// </summary>
public static class Policies
{
    /// <summary>查詢物料：任何登入者。</summary>
    public const string ItemsRead = "Items.Read";

    /// <summary>提出物料：作業員、物料管理員。</summary>
    public const string ItemsSubmit = "Items.Submit";

    /// <summary>核准、退回、拒絕、停用物料：物料管理員。</summary>
    public const string ItemsReview = "Items.Review";

    /// <summary>新增物料的專屬別名、共用關鍵字：作業員、物料管理員（ADR-0013）。</summary>
    public const string ItemsIndex = "Items.Index";

    /// <summary>維護倉庫、儲位、倉位圖片、物料常用倉位：倉庫主管（M3）。</summary>
    public const string WarehousesManage = "Warehouses.Manage";

    /// <summary>查詢倉庫、儲位、結存、異動、期初單：任何登入者。</summary>
    public const string StockRead = "Stock.Read";

    /// <summary>建立、修改、送出、取消期初單：作業員、倉庫主管。</summary>
    public const string OpeningEdit = "Opening.Edit";

    /// <summary>核准、退回期初單：倉庫主管（另依 D-07 設定檢查是否可自核）。</summary>
    public const string OpeningApprove = "Opening.Approve";

    /// <summary>系統設定：系統管理員。</summary>
    public const string SettingsManage = "Settings.Manage";

    /// <summary>維護分類與單位：物料管理員。</summary>
    public const string MasterDataManage = "MasterData.Manage";

    public static IServiceCollection AddWarehouseAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(ItemsRead, p => p.RequireAuthenticatedUser())
            .AddPolicy(ItemsSubmit, p => p.RequireRole(Roles.Operator, Roles.ItemManager))
            .AddPolicy(ItemsReview, p => p.RequireRole(Roles.ItemManager))
            .AddPolicy(ItemsIndex, p => p.RequireRole(Roles.Operator, Roles.ItemManager))
            .AddPolicy(MasterDataManage, p => p.RequireRole(Roles.ItemManager))
            .AddPolicy(WarehousesManage, p => p.RequireRole(Roles.WarehouseManager))
            .AddPolicy(StockRead, p => p.RequireAuthenticatedUser())
            .AddPolicy(OpeningEdit, p => p.RequireRole(Roles.Operator, Roles.WarehouseManager))
            .AddPolicy(OpeningApprove, p => p.RequireRole(Roles.WarehouseManager))
            .AddPolicy(SettingsManage, p => p.RequireRole(Roles.SystemAdmin));
        return services;
    }
}

/// <summary>登入身分（Cookie 內的 claims）與 Application 層 <see cref="Actor"/> 的轉換。</summary>
public static class ActorClaims
{
    /// <summary>顯示名稱 claim（由 Web 的 claims factory 寫入）。</summary>
    public const string DisplayName = "warehouse:display_name";

    /// <summary>值為 "true" 時必須先改密碼。</summary>
    public const string MustChangePassword = "warehouse:must_change_password";

    public static Actor ToActor(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("登入身分缺少使用者 ID。");
        var name = user.FindFirstValue(DisplayName) ?? user.Identity?.Name ?? "（未知）";
        return new Actor(Guid.Parse(id), name, user.IsInRole(Roles.ItemManager));
    }
}
