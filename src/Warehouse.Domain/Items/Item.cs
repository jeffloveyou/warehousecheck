using System.Text.RegularExpressions;
using Warehouse.Domain.Text;

namespace Warehouse.Domain.Items;

/// <summary>
/// 物料主檔（ADR-0003、ADR-0004、ADR-0006、ADR-0011）。
/// - <see cref="Id"/>：永久內部識別，所有關聯都用它。
/// - <see cref="RequestNo"/>：提出時產生的申請單號（MR-000125），永久保留。
/// - <see cref="ItemCode"/>：正式料號，核准時才產生；發放後不得重新分配給其他物料。
/// 申請單號與料號由 Infrastructure 的產生器給號（M1-02）；狀態轉換規則在本類別，見 <see cref="Approve"/> 等方法。
/// </summary>
public sealed partial class Item
{
    public const int RequestNoMaxLength = 16;
    public const int ItemCodeMaxLength = 16;
    public const int NameMaxLength = 200;
    public const int BrandMaxLength = 100;
    public const int ManufacturerPartNoMaxLength = 100;
    public const int SpecificationMaxLength = 1000;
    public const int NormalizedMaxLength = 400;

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
        DateTimeOffset now,
        Guid? createdById = null)
    {
        if (id == Guid.Empty)
        {
            throw new DomainRuleException("物料內部識別碼不可為空。");
        }

        var item = new Item
        {
            Id = id,
            RequestNo = Rules.Matches(requestNo, RequestNoPattern(), "申請單號", "MR- 加至少 6 位數字"),
            Status = ItemStatus.PendingApproval,
            CreatedById = createdById,
            CreatedAt = now.ToUniversalTime(),
        };
        item.ApplyDetails(name, categoryId, baseUnitId, trackingMode, brand, manufacturerPartNo, specification, now);
        return item;
    }

    /// <summary>
    /// 修改基本資料。核准前（待核准、退回）可改全部欄位；
    /// 核准後（有效、停用）只能改品名、品牌、型號、規格——分類、基本單位、追蹤模式不可再改（AGENTS.md 第六節，V1 保守處理）。
    /// 已拒絕的物料不可修改。
    /// </summary>
    public void UpdateDetails(
        string name,
        Guid categoryId,
        Guid baseUnitId,
        TrackingMode trackingMode,
        string? brand,
        string? manufacturerPartNo,
        string? specification,
        DateTimeOffset now)
    {
        if (Status == ItemStatus.Rejected)
        {
            throw new DomainRuleException("已拒絕的物料不可修改。");
        }
        if (HasItemCode && (categoryId != CategoryId || baseUnitId != BaseUnitId || trackingMode != TrackingMode))
        {
            throw new DomainRuleException("物料核准後不可變更分類、基本單位或追蹤模式。");
        }
        ApplyDetails(name, categoryId, baseUnitId, trackingMode, brand, manufacturerPartNo, specification, now);
    }

    /// <summary>退回後重新送審：退回 → 待核准。修改內容請先呼叫 <see cref="UpdateDetails"/>。</summary>
    public void Resubmit(DateTimeOffset now) =>
        Transition(ItemStatus.Returned, ItemStatus.PendingApproval, "只有退回的物料可以重新送審。", now);

    /// <summary>核准：待核准 → 有效，同時填入產生器發出的正式料號。</summary>
    public void Approve(string itemCode, DateTimeOffset now)
    {
        var code = Rules.Matches(itemCode, ItemCodePattern(), "正式料號", "2～4 碼前綴、連字號、至少 6 位數字");
        Transition(ItemStatus.PendingApproval, ItemStatus.Active, "只有待核准的物料可以核准。", now);
        ItemCode = code;
    }

    /// <summary>退回給提出人修改：待核准 → 退回。不占用料號。</summary>
    public void Return(DateTimeOffset now) =>
        Transition(ItemStatus.PendingApproval, ItemStatus.Returned, "只有待核准的物料可以退回。", now);

    /// <summary>拒絕（含判定重複）：待核准 → 已拒絕。不占用料號，申請單號與審核紀錄保留。</summary>
    public void Reject(DateTimeOffset now) =>
        Transition(ItemStatus.PendingApproval, ItemStatus.Rejected, "只有待核准的物料可以拒絕。", now);

    /// <summary>提出人撤回：待核准或退回 → 已拒絕。</summary>
    public void Withdraw(DateTimeOffset now)
    {
        if (Status is not (ItemStatus.PendingApproval or ItemStatus.Returned))
        {
            throw new DomainRuleException("只有待核准或退回的物料可以撤回。");
        }
        SetStatus(ItemStatus.Rejected, now);
    }

    /// <summary>停用：有效 → 停用，料號保留（不可交易、不可重新分配）。</summary>
    public void Deactivate(DateTimeOffset now) =>
        Transition(ItemStatus.Active, ItemStatus.Inactive, "只有有效的物料可以停用。", now);

    /// <summary>重新啟用：停用 → 有效，沿用原料號。</summary>
    public void Reactivate(DateTimeOffset now) =>
        Transition(ItemStatus.Inactive, ItemStatus.Active, "只有停用的物料可以重新啟用。", now);

    private bool HasItemCode => Status is ItemStatus.Active or ItemStatus.Inactive;

    private void Transition(ItemStatus from, ItemStatus to, string error, DateTimeOffset now)
    {
        if (Status != from)
        {
            throw new DomainRuleException(error);
        }
        SetStatus(to, now);
    }

    private void SetStatus(ItemStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now.ToUniversalTime();
    }

    private void ApplyDetails(
        string name,
        Guid categoryId,
        Guid baseUnitId,
        TrackingMode trackingMode,
        string? brand,
        string? manufacturerPartNo,
        string? specification,
        DateTimeOffset now)
    {
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
        if (AllowNegativeStock && trackingMode != TrackingMode.None)
        {
            throw new DomainRuleException("此物料允許負庫存，請先關閉負庫存再改為批號／序號追蹤。");
        }

        Name = Rules.Required(name, NameMaxLength, "品名");
        CategoryId = categoryId;
        BaseUnitId = baseUnitId;
        TrackingMode = trackingMode;
        Brand = Rules.Optional(brand, BrandMaxLength, "品牌");
        ManufacturerPartNo = Rules.Optional(manufacturerPartNo, ManufacturerPartNoMaxLength, "原廠型號");
        Specification = Rules.Optional(specification, SpecificationMaxLength, "規格");
        // NFKC 可能把單一字元展開成多字（例如 U+FDFA → 18 字）；限制長度避免超過索引單列大小
        var normalizedName = TextNormalizer.Normalize(Name);
        var normalizedPartNo = TextNormalizer.NormalizePartNo(ManufacturerPartNo);
        if (normalizedName.Length > NormalizedMaxLength || normalizedPartNo?.Length > NormalizedMaxLength)
        {
            throw new DomainRuleException("品名或型號含有展開後過長的特殊字元，請改用一般文字。");
        }
        NormalizedName = normalizedName;
        NormalizedPartNo = normalizedPartNo;
        UpdatedAt = now.ToUniversalTime();
    }

    [GeneratedRegex("^MR-[0-9]{6,}$")]
    private static partial Regex RequestNoPattern();

    [GeneratedRegex("^[A-Z0-9]{2,4}-[0-9]{6,}$")]
    private static partial Regex ItemCodePattern();

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

    /// <summary>重複候選比對用的品名（<see cref="TextNormalizer.Normalize"/>），隨品名自動更新。</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    /// <summary>重複候選比對用的型號（<see cref="TextNormalizer.NormalizePartNo"/>）；沒有型號時為 null。</summary>
    public string? NormalizedPartNo { get; private set; }

    /// <summary>提出人（使用者 ID）；用來判斷誰可以修改、撤回。M1-02 之前建立的資料為 null。</summary>
    public Guid? CreatedById { get; private set; }

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
