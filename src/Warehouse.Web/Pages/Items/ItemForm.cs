using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using Warehouse.Application.Items;
using Warehouse.Application.MasterData;
using Warehouse.Domain.Items;

namespace Warehouse.Web.Pages.Items;

/// <summary>
/// 提出／修改物料的表單欄位。這裡的 DataAnnotations 只負責「畫面上立即提示」；
/// 真正的規則在 Domain（<see cref="Item"/>），資料庫 CHECK 再保證一次（docs/04 §3.3）。
/// </summary>
public sealed class ItemFormInput
{
    [Required(ErrorMessage = "請輸入品名。")]
    [StringLength(Item.NameMaxLength, ErrorMessage = "品名不可超過 {1} 字。")]
    [Display(Name = "品名")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "請選擇分類。")]
    [Display(Name = "分類")]
    public Guid? CategoryId { get; set; }

    [Required(ErrorMessage = "請選擇基本單位。")]
    [Display(Name = "基本單位")]
    public Guid? BaseUnitId { get; set; }

    [Display(Name = "追蹤模式")]
    public TrackingMode TrackingMode { get; set; } = TrackingMode.None;

    [StringLength(Item.BrandMaxLength, ErrorMessage = "品牌不可超過 {1} 字。")]
    [Display(Name = "品牌")]
    public string? Brand { get; set; }

    [StringLength(Item.ManufacturerPartNoMaxLength, ErrorMessage = "原廠型號不可超過 {1} 字。")]
    [Display(Name = "原廠型號")]
    public string? ManufacturerPartNo { get; set; }

    [StringLength(Item.SpecificationMaxLength, ErrorMessage = "規格不可超過 {1} 字。")]
    [Display(Name = "規格")]
    public string? Specification { get; set; }

    public ItemDraft ToDraft() => new(
        Name, CategoryId ?? Guid.Empty, BaseUnitId ?? Guid.Empty, TrackingMode, Brand, ManufacturerPartNo, Specification);

    public static ItemFormInput From(ItemDetails item) => new()
    {
        Name = item.Name,
        CategoryId = item.CategoryId,
        BaseUnitId = item.BaseUnitId,
        TrackingMode = item.TrackingMode,
        Brand = item.Brand,
        ManufacturerPartNo = item.ManufacturerPartNo,
        Specification = item.Specification,
    };
}

/// <summary>表單下拉選單（只列啟用中的分類與單位；修改時保留目前選擇，即使它已停用）。</summary>
public sealed class ItemFormOptions
{
    public IReadOnlyList<SelectListItem> Categories { get; private init; } = [];

    public IReadOnlyList<SelectListItem> Units { get; private init; } = [];

    public IReadOnlyList<SelectListItem> TrackingModes { get; } =
        Enum.GetValues<TrackingMode>().Select(m => new SelectListItem(Display.Of(m), m.ToString())).ToList();

    /// <summary>核准後不可改分類、單位、追蹤模式（Domain 也會擋），畫面上直接鎖住。</summary>
    public bool LockClassification { get; init; }

    public static async Task<ItemFormOptions> LoadAsync(
        IMasterDataService masterData, ItemFormInput? current, bool lockClassification, CancellationToken cancellationToken)
    {
        var categories = await masterData.ListCategoriesAsync(cancellationToken);
        var units = await masterData.ListUnitsAsync(cancellationToken);
        return new ItemFormOptions
        {
            LockClassification = lockClassification,
            Categories = categories
                .Where(c => c.IsActive || c.Id == current?.CategoryId)
                .Select(c => new SelectListItem($"{c.Path}（{c.EffectivePrefix}）", c.Id.ToString()))
                .ToList(),
            Units = units
                .Where(u => u.IsActive || u.Id == current?.BaseUnitId)
                .Select(u => new SelectListItem($"{u.Code} {u.Name}", u.Id.ToString()))
                .ToList(),
        };
    }
}
