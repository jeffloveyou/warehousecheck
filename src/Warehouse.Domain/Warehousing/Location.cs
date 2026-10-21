using System.Text.RegularExpressions;

namespace Warehouse.Domain.Warehousing;

/// <summary>儲位管理狀態（FR-03）。</summary>
public enum LocationStatus
{
    /// <summary>有效：可存放、可過帳。</summary>
    Available,

    /// <summary>待驗：可過帳，但結存一律是 Quarantine，不算可用量（ADR-0004）。</summary>
    Inspection,

    /// <summary>停用：不可過帳；資料保留。</summary>
    Inactive,
}

/// <summary>儲位類型（M3-06 結構化屬性）。</summary>
public enum LocationType
{
    Shelf,
    Floor,
    Pallet,
    Cold,
    Hazmat,
}

/// <summary>
/// 儲位（M3-01、M3-06）。儲位碼依 ADR-0006「倉-區-架-層-格」，例 <c>W1-A-01-03-02</c>，段數可省略；
/// 同倉庫內唯一（資料庫 <c>ux_locations_warehouse_code</c>）。儲位碼建立後不可改（已貼的 QR 標籤會失效）。
/// 區／架／層／格等是另外填的結構化欄位，不從儲位碼解析；是否顯示由系統設定開關控制，關閉時資料保留。
/// </summary>
public sealed partial class Location
{
    public const int CodeMaxLength = 40;
    public const int NameMaxLength = 100;
    public const int NoteMaxLength = 500;
    public const int SegmentMaxLength = 10;

    private Location()
    {
    }

    public static Location Create(Guid id, Guid warehouseId, string code, LocationDetails details, DateTimeOffset now)
    {
        if (id == Guid.Empty || warehouseId == Guid.Empty)
        {
            throw new DomainRuleException("儲位必須指定倉庫。");
        }
        var location = new Location
        {
            Id = id,
            WarehouseId = warehouseId,
            Code = Rules.Matches(code?.Trim(), CodePattern(), "儲位碼", "大寫英數，以 - 分段，最多 6 段（例 W1-A-01-03-02）"),
            Status = LocationStatus.Available,
            CreatedAt = now.ToUniversalTime(),
        };
        if (location.Code.Length > CodeMaxLength)
        {
            throw new DomainRuleException($"儲位碼不可超過 {CodeMaxLength} 字。");
        }
        location.Update(details, now);
        return location;
    }

    public Guid Id { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string? Name { get; private set; }

    public LocationStatus Status { get; private set; }

    /// <summary>自由備註（M3-06）。</summary>
    public string? Note { get; private set; }

    public string? Zone { get; private set; }

    public string? Rack { get; private set; }

    public string? Level { get; private set; }

    public string? Bin { get; private set; }

    public LocationType? LocationType { get; private set; }

    /// <summary>承重上限（公斤）；null 表示未設定。</summary>
    public decimal? MaxLoadKg { get; private set; }

    public bool TemperatureControlled { get; private set; }

    public bool Hazardous { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>修改名稱、備註與結構化屬性（儲位碼、倉庫不可改）。</summary>
    public void Update(LocationDetails details, DateTimeOffset now)
    {
        if (details.LocationType is { } type && !Enum.IsDefined(type))
        {
            throw new DomainRuleException("儲位類型不正確。");
        }
        if (details.MaxLoadKg is < 0)
        {
            throw new DomainRuleException("承重上限不可小於 0。");
        }
        Name = Rules.Optional(details.Name, NameMaxLength, "儲位名稱");
        Note = Rules.Optional(details.Note, NoteMaxLength, "備註");
        Zone = Rules.Optional(details.Zone, SegmentMaxLength, "區");
        Rack = Rules.Optional(details.Rack, SegmentMaxLength, "架");
        Level = Rules.Optional(details.Level, SegmentMaxLength, "層");
        Bin = Rules.Optional(details.Bin, SegmentMaxLength, "格");
        LocationType = details.LocationType;
        MaxLoadKg = details.MaxLoadKg;
        TemperatureControlled = details.TemperatureControlled;
        Hazardous = details.Hazardous;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>變更管理狀態（有效、待驗、停用之間可任意切換）。</summary>
    public void ChangeStatus(LocationStatus status, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new DomainRuleException("儲位狀態不正確。");
        }
        if (status == Status)
        {
            throw new DomainRuleException("儲位已經是這個狀態。");
        }
        Status = status;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>儲位 QR Code 的內容（M3-04）：<c>WH:LOC:倉庫碼:儲位碼</c>。V1 只顯示文字，不產生圖片、不列印。</summary>
    public static string QrContent(string warehouseCode, string locationCode) => $"WH:LOC:{warehouseCode}:{locationCode}";

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+){0,5}$")]
    private static partial Regex CodePattern();
}

/// <summary>儲位可修改的內容（名稱、備註、結構化屬性）。</summary>
public sealed record LocationDetails(
    string? Name,
    string? Note,
    string? Zone,
    string? Rack,
    string? Level,
    string? Bin,
    LocationType? LocationType,
    decimal? MaxLoadKg,
    bool TemperatureControlled,
    bool Hazardous);
