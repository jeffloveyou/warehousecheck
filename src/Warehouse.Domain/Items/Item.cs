namespace Warehouse.Domain.Items;

/// <summary>
/// 物料主檔（ADR-0003、ADR-0004、ADR-0006、ADR-0011）。
/// - <see cref="Id"/>：永久內部識別，所有關聯都用它。
/// - <see cref="RequestNo"/>：提出時產生的申請單號（MR-000125），永久保留。
/// - <see cref="ItemCode"/>：正式料號，核准時才產生；發放後不得重新分配給其他物料。
/// 申請單號與料號的產生、核准／退回／拒絕流程在 M1-02 實作；M1-01 只建立資料結構與不變條件。
/// </summary>
public sealed class Item
{
    public const int RequestNoMaxLength = 16;
    public const int ItemCodeMaxLength = 16;
    public const int NameMaxLength = 200;
    public const int BrandMaxLength = 100;
    public const int ManufacturerPartNoMaxLength = 100;
    public const int SpecificationMaxLength = 1000;

    private Item()
    {
    }

    /// <summary>建立待核准物料。必填：品名、分類、基本單位、追蹤模式（ADR-0006 D-04）。</summary>
    public static Item Submit(
        Guid id,
        string requestNo,
        string name,
        Guid categoryId,
        Guid baseUnitId,
        TrackingMode trackingMode,
        string? brand,
        string? manufacturerPartNo,
        string? specification,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new DomainRuleException("物料內部識別碼不可為空。");
        }
        if (categoryId == Guid.Empty)
        {
            throw new DomainRuleException("分類為必填。");
        }
        if (baseUnitId == Guid.Empty)
        {
            throw new DomainRuleException("基本單位為必填。");
        }
        if (!Enum.IsDefined(trackingMode))
        {
            throw new DomainRuleException("追蹤模式不正確。");
        }

        return new Item
        {
            Id = id,
            RequestNo = Rules.Required(requestNo, RequestNoMaxLength, "申請單號"),
            Status = ItemStatus.PendingApproval,
            Name = Rules.Required(name, NameMaxLength, "品名"),
            CategoryId = categoryId,
            BaseUnitId = baseUnitId,
            TrackingMode = trackingMode,
            Brand = Rules.Optional(brand, BrandMaxLength, "品牌"),
            ManufacturerPartNo = Rules.Optional(manufacturerPartNo, ManufacturerPartNoMaxLength, "原廠型號"),
            Specification = Rules.Optional(specification, SpecificationMaxLength, "規格"),
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
        };
    }

    public Guid Id { get; private set; }

    public string RequestNo { get; private set; } = string.Empty;

    /// <summary>正式料號；核准前為 null。</summary>
    public string? ItemCode { get; private set; }

    public ItemStatus Status { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid CategoryId { get; private set; }

    public Guid BaseUnitId { get; private set; }

    public TrackingMode TrackingMode { get; private set; }

    public string? Brand { get; private set; }

    public string? ManufacturerPartNo { get; private set; }

    public string? Specification { get; private set; }

    /// <summary>是否允許負庫存（ADR-0004）；只有 <see cref="TrackingMode.None"/> 可開啟。</summary>
    public bool AllowNegativeStock { get; private set; }

    /// <summary>負庫存下限（≥ 0）：結存最低可到 -NegativeStockLimit。</summary>
    public decimal NegativeStockLimit { get; private set; }

    /// <summary>建立時間（一律存 UTC；PostgreSQL timestamptz 只接受 UTC 偏移量，顯示時再轉當地時間）。</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// 設定負庫存例外（ADR-0004）。權限、稽核與「已有負結存時不可關閉」的檢查屬於交易服務（M4-08）。
    /// </summary>
    public void SetNegativeStockPolicy(bool allow, decimal limit, DateTimeOffset now)
    {
        if (limit < 0)
        {
            throw new DomainRuleException("負庫存下限不可小於 0。");
        }
        if (allow && TrackingMode != TrackingMode.None)
        {
            throw new DomainRuleException("只有不追蹤批號／序號的物料可以允許負庫存。");
        }
        AllowNegativeStock = allow;
        NegativeStockLimit = allow ? limit : 0;
        UpdatedAt = now.ToUniversalTime();
    }
}
