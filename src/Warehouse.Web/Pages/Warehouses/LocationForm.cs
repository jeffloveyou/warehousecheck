using System.ComponentModel.DataAnnotations;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Warehousing;

namespace Warehouse.Web.Pages.Warehouses;

/// <summary>儲位表單（新增與修改共用）。規則以 Domain <see cref="Location"/> 為準，這裡只做畫面提示。</summary>
public sealed class LocationForm
{
    [StringLength(Location.CodeMaxLength)]
    [Display(Name = "儲位碼")]
    public string Code { get; set; } = string.Empty;

    [StringLength(Location.NameMaxLength)]
    public string? Name { get; set; }

    [StringLength(Location.NoteMaxLength)]
    public string? Note { get; set; }

    [StringLength(Location.SegmentMaxLength)]
    public string? Zone { get; set; }

    [StringLength(Location.SegmentMaxLength)]
    public string? Rack { get; set; }

    [StringLength(Location.SegmentMaxLength)]
    public string? Level { get; set; }

    [StringLength(Location.SegmentMaxLength)]
    public string? Bin { get; set; }

    public LocationType? LocationType { get; set; }

    [Range(0, 9_999_999)]
    public decimal? MaxLoadKg { get; set; }

    public bool TemperatureControlled { get; set; }

    public bool Hazardous { get; set; }

    public LocationDetails ToDetails() =>
        new(Name, Note, Zone, Rack, Level, Bin, LocationType, MaxLoadKg, TemperatureControlled, Hazardous);

    public static LocationForm From(LocationRow row) => new()
    {
        Code = row.Code, Name = row.Name, Note = row.Note, Zone = row.Zone, Rack = row.Rack, Level = row.Level, Bin = row.Bin,
        LocationType = row.LocationType, MaxLoadKg = row.MaxLoadKg,
        TemperatureControlled = row.TemperatureControlled, Hazardous = row.Hazardous,
    };

    /// <summary>
    /// 設定關閉的欄位，表單不會顯示也不會送出；修改時沿用原本的值，資料不會因為關閉功能而被清掉（M6-05 原則）。
    /// </summary>
    public LocationForm KeepHidden(LocationRow original, bool notesEnabled, bool attributesEnabled)
    {
        if (!notesEnabled)
        {
            Note = original.Note;
        }
        if (!attributesEnabled)
        {
            (Zone, Rack, Level, Bin, LocationType, MaxLoadKg, TemperatureControlled, Hazardous) = (
                original.Zone, original.Rack, original.Level, original.Bin, original.LocationType, original.MaxLoadKg,
                original.TemperatureControlled, original.Hazardous);
        }
        return this;
    }
}
