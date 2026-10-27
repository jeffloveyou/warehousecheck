using System.Text.RegularExpressions;
using Warehouse.Domain.Text;

namespace Warehouse.Domain.Items;

/// <summary>
/// 物料主檔（ADR-0003、ADR-0004、ADR-0006、ADR-0011）。
/// - <see cref="Id"/>：永久內部識別，所有關聯都用它。
/// - <see cref="RequestNo"/>：提出時產生的申請單號（MR-000125），永久保留。
/// - <see cref="ItemCode"/>：正式料號，核准時才產生（可由物料管理員指定）；核准後經系統設定開放才可變更，
///   舊料號預設保留為歷史料號、不給其他物料使用（D-32，ADR-0025）。
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
    /// 修改基本資料。核准前（待核准、退回）可改全部欄位。
    /// 核准後（有效、停用）分類一律不可改（料號前綴來自分類）；基本單位、追蹤模式只有在服務已檢查過
    /// 異動紀錄、系統設定與安全底線後（<paramref name="unitTrackingChangeChecked"/>）才可改（ADR-0023）。
    /// 已拒絕的物料不可修改。
    /// <paramref name="unitTrackingChangeChecked"/> 只能由 <c>ItemWorkflowService</c> 在鎖住物料列（FOR UPDATE）並檢查完之後傳 true；
    /// 這裡只防「忘記檢查」，不防刻意繞過。
    /// </summary>
    public void UpdateDetails(
        string name,
        Guid categoryId,
        Guid baseUnitId,
        TrackingMode trackingMode,
        string? brand,
        string? manufacturerPartNo,
        string? specification,
        DateTimeOffset now,
        bool unitTrackingChangeChecked = false)
    {
        if (Status == ItemStatus.Rejected)
        {
            throw new DomainRuleException("已拒絕的物料不可修改。");
        }
        if (HasItemCode && categoryId != CategoryId)
        {
            throw new DomainRuleException("物料核准後不可變更分類（正式料號的前綴來自分類）。");
        }
        if (HasItemCode && !unitTrackingChangeChecked && (baseUnitId != BaseUnitId || trackingMode != TrackingMode))
        {
            throw new DomainRuleException("物料核准後變更基本單位或追蹤模式，必須先通過異動紀錄與系統設定的檢查。");
        }
        ApplyDetails(name, categoryId, baseUnitId, trackingMode, brand, manufacturerPartNo, specification, now);
    }

    /// <summary>退回後重新送審：退回 → 待核准。修改內容請先呼叫 <see cref="UpdateDetails"/>。</summary>
    public void Resubmit(DateTimeOffset now) =>
        Transition(ItemStatus.Returned, ItemStatus.PendingApproval, "只有退回的物料可以重新送審。", now);

    /// <summary>核准：待核准 → 有效，同時填入產生器發出（或物料管理員指定）的正式料號。</summary>
    public void Approve(string itemCode, DateTimeOffset now)
    {
        var code = ValidItemCode(itemCode);
        Transition(ItemStatus.PendingApproval, ItemStatus.Active, "只有待核准的物料可以核准。", now);
        ItemCode = code;
    }

    /// <summary>
    /// 變更已核准物料的正式料號（D-32，ADR-0025）。系統設定、權限、前綴是否屬於分類、是否被占用、歷史料號
    /// 由 <c>ItemWorkflowService</c> 檢查；資料庫 trigger 另外要求交易內設定 warehouse.item_code_change。
    /// </summary>
    public void ChangeItemCode(string newItemCode, DateTimeOffset now)
    {
        if (!HasItemCode)
        {
            throw new DomainRuleException("只有已核准的物料可以變更料號。");
        }
        var code = ValidItemCode(newItemCode);
        if (code == ItemCode)
        {
            throw new DomainRuleException("新料號與目前的料號相同。");
        }
        ItemCode = code;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>料號格式：2～4 碼大寫英數前綴、連字號、至少 6 位數字（例如 ME-000123）。</summary>
    public static bool IsValidItemCode(string? code) => code is not null && code.Length <= ItemCodeMaxLength && ItemCodePattern().IsMatch(code);

    /// <summary>料號的前綴（連字號前面的部分）。</summary>
    public static string PrefixOf(string itemCode) => itemCode[..itemCode.IndexOf('-')];

    private static string ValidItemCode(string itemCode)
    {
        var code = Rules.Matches(itemCode, ItemCodePattern(), "正式料號", "2～4 碼前綴、連字號、至少 6 位數字");
        if (code.Length > ItemCodeMaxLength)
        {
            throw new DomainRuleException($"正式料號不可超過 {ItemCodeMaxLength} 字。");
        }
        return code;
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
        // 搜尋部分比對用（M2 審查 R1）：全形「ＤＣ２４Ｖ」也能用「24V」找到；只做 LIKE，不建索引
        NormalizedBrand = Brand is null ? null : TextNormalizer.Normalize(Brand);
        NormalizedSpecification = Specification is null ? null : TextNormalizer.Normalize(Specification);
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

    /// <summary>搜尋用的品牌（<see cref="TextNormalizer.Normalize"/>）；沒有品牌時為 null。</summary>
    public string? NormalizedBrand { get; private set; }

    /// <summary>搜尋用的規格（<see cref="TextNormalizer.Normalize"/>）；沒有規格時為 null。</summary>
    public string? NormalizedSpecification { get; private set; }

    /// <summary>提出人（使用者 ID）；用來判斷誰可以修改、撤回。M1-02 之前建立的資料為 null。</summary>
    public Guid? CreatedById { get; private set; }

    /// <summary>是否允許負庫存（ADR-0004）；只有 <see cref="TrackingMode.None"/> 可開啟。</summary>
    public bool AllowNegativeStock { get; private set; }

    /// <summary>負庫存下限的上限（numeric(19,4) 也放得下，且遠大於實務需要）。</summary>
    public const decimal MaxNegativeStockLimit = 999_999_999m;

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
        if (limit > MaxNegativeStockLimit)
        {
            throw new DomainRuleException($"負庫存下限不可超過 {MaxNegativeStockLimit:N0}。");
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
