using Warehouse.Domain.Items;

namespace Warehouse.Domain.Inventory;

/// <summary>盤點單狀態：盤點中 →（送審）待核准 → 已核准；待核准可要求重盤（回到盤點中）；盤點中或待核准可取消。</summary>
public enum StocktakeStatus
{
    Counting,
    Submitted,
    Approved,
    Cancelled,
}

/// <summary>
/// 盤點單（M6-02，ADR-0018）：一個倉庫的全部或指定儲位。不凍結儲位：
/// 開始時記錄快照量（參考用），每一行記錄實盤量時，同時記下「計數當下」的帳面量；
/// 差異＝實盤量 − 計數當下帳面量，所以快照後到計數前的進出不會被算成盤差。
/// 核准時以差異（相對量）產生調整單，核准前後的其他異動不會被覆蓋。
/// 結存只在核准時經過帳服務改變；本類別只管盤點單本身的狀態與明細規則。
/// </summary>
public sealed class Stocktake
{
    public const int NoteMaxLength = 500;

    /// <summary>
    /// 一張盤點單的行數上限：核准時每個盤虧的料號＋倉庫要一把 advisory 鎖，太多會吃光 PostgreSQL 共用鎖表
    /// （max_locks_per_transaction，ADR-0004）。範圍太大請依儲位分成多張盤點單。
    /// </summary>
    public const int MaxLines = 2000;

    private readonly List<StocktakeLine> _lines = [];

    private Stocktake()
    {
    }

    public static Stocktake Start(
        Guid id, string stocktakeNo, Guid warehouseId, bool allLocations, string? note, Guid actorId, string actorName, DateTimeOffset now)
    {
        if (id == Guid.Empty || warehouseId == Guid.Empty || actorId == Guid.Empty)
        {
            throw new DomainRuleException("盤點單必須指定倉庫與建立者。");
        }
        return new Stocktake
        {
            Id = id,
            StocktakeNo = Rules.Required(stocktakeNo, 20, "盤點單號"),
            WarehouseId = warehouseId,
            AllLocations = allLocations,
            Status = StocktakeStatus.Counting,
            Note = Rules.Optional(note, NoteMaxLength, "備註"),
            SnapshotAt = now.ToUniversalTime(),
            CreatedById = actorId,
            CreatedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "建立者"),
            CreatedAt = now.ToUniversalTime(),
        };
    }

    public Guid Id { get; private set; }

    public string StocktakeNo { get; private set; } = string.Empty;

    public Guid WarehouseId { get; private set; }

    /// <summary>true＝開始時倉庫內全部未停用的儲位；false＝指定的儲位（stocktake_locations 都會列出實際範圍）。</summary>
    public bool AllLocations { get; private set; }

    public StocktakeStatus Status { get; private set; }

    public string? Note { get; private set; }

    /// <summary>快照時間（開始盤點的時間）。</summary>
    public DateTimeOffset SnapshotAt { get; private set; }

    public Guid CreatedById { get; private set; }

    public string CreatedByName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? SubmittedById { get; private set; }

    public string? SubmittedByName { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? ApprovedById { get; private set; }

    public string? ApprovedByName { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>核准時產生的調整單；沒有任何差異時為 null。</summary>
    public Guid? AdjustmentDocumentId { get; private set; }

    /// <summary>最近一次要求重盤的說明。</summary>
    public string? RecountComment { get; private set; }

    public IReadOnlyList<StocktakeLine> Lines => _lines;

    public bool IsOpen => Status is StocktakeStatus.Counting or StocktakeStatus.Submitted;

    /// <summary>送審：每一行都要有實盤量（0 也要填），且沒有待重盤的行。</summary>
    public void Submit(Guid actorId, string actorName, DateTimeOffset now)
    {
        EnsureCounting();
        if (_lines.Count == 0)
        {
            throw new DomainRuleException("盤點單沒有任何明細；範圍內確實沒有庫存時請取消這張盤點單。");
        }
        var uncounted = _lines.Count(l => l.CountedQuantity is null || l.RecountRequested);
        if (uncounted > 0)
        {
            throw new DomainRuleException($"還有 {uncounted} 行沒有輸入實盤量（或待重盤），沒有庫存請填 0。");
        }
        Status = StocktakeStatus.Submitted;
        SubmittedById = actorId;
        SubmittedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "送審者");
        SubmittedAt = now.ToUniversalTime();
    }

    /// <summary>審核者要求指定行重盤：回到盤點中，那些行必須重新輸入實盤量才能再送審。</summary>
    public void RequestRecount(IReadOnlyCollection<Guid> lineIds, string comment)
    {
        if (Status != StocktakeStatus.Submitted)
        {
            throw new DomainRuleException("只有待核准的盤點單可以要求重盤。");
        }
        var targets = _lines.Where(l => lineIds.Contains(l.Id)).ToList();
        if (targets.Count == 0)
        {
            throw new DomainRuleException("請勾選要重盤的行。");
        }
        RecountComment = Rules.Required(comment, NoteMaxLength, "重盤說明");
        foreach (var line in targets)
        {
            line.MarkRecount();
        }
        Status = StocktakeStatus.Counting;
        SubmittedById = null;
        SubmittedByName = null;
        SubmittedAt = null;
    }

    public void Cancel()
    {
        if (!IsOpen)
        {
            throw new DomainRuleException("只有盤點中或待核准的盤點單可以取消。");
        }
        Status = StocktakeStatus.Cancelled;
    }

    /// <summary>
    /// 核准（呼叫端在同一交易內產生並過帳調整單）。D-07：<paramref name="allowSelfApprove"/> 為 false 時，
    /// 建立者與送審者都不能核准。
    /// </summary>
    public void Approve(Guid approverId, string approverName, bool allowSelfApprove, Guid? adjustmentDocumentId, DateTimeOffset now)
    {
        EnsureCanApprove(approverId, allowSelfApprove);
        Status = StocktakeStatus.Approved;
        ApprovedById = approverId;
        ApprovedByName = Rules.Required(approverName, ItemReview.ActorNameMaxLength, "核准者");
        ApprovedAt = now.ToUniversalTime();
        AdjustmentDocumentId = adjustmentDocumentId;
    }

    /// <summary>核准前的狀態與自核檢查（先檢查，失敗就不產生調整單）。</summary>
    public void EnsureCanApprove(Guid approverId, bool allowSelfApprove)
    {
        if (Status != StocktakeStatus.Submitted)
        {
            throw new DomainRuleException("只有待核准的盤點單可以核准。");
        }
        if (!allowSelfApprove && (approverId == CreatedById || approverId == SubmittedById))
        {
            throw new DomainRuleException("系統設定不允許建立者或送審者核准自己的盤點，請由其他倉庫主管核准。");
        }
    }

    /// <summary>輸入實盤量前檢查（明細的新增與計數由服務逐行處理，不必載入整張盤點單的明細）。</summary>
    public void EnsureCounting()
    {
        if (Status != StocktakeStatus.Counting)
        {
            throw new DomainRuleException("盤點單不是盤點中，不能修改實盤量。");
        }
    }
}

/// <summary>
/// 盤點明細：一個結存維度（儲位、物料、批號／序號、結存狀態）。快照行由開始盤點時的結存產生；
/// 「現場找到」的行由盤點人員新增（<see cref="IsAdded"/>）。
/// </summary>
public sealed class StocktakeLine
{
    private StocktakeLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid StocktakeId { get; private set; }

    public int LineNo { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid ItemId { get; private set; }

    /// <summary>既有批號／序號（快照行一定有；新增行若號碼已存在也會帶入）。</summary>
    public Guid? LotId { get; private set; }

    public Guid? SerialId { get; private set; }

    public string? LotNo { get; private set; }

    public string? SerialNo { get; private set; }

    public StockStatus StockStatus { get; private set; }

    /// <summary>開始盤點時的帳面量（參考；新增行為 0）。</summary>
    public decimal SnapshotQuantity { get; private set; }

    public bool IsAdded { get; private set; }

    public decimal? CountedQuantity { get; private set; }

    /// <summary>記錄實盤量當下的帳面量（差異的基準）。</summary>
    public decimal? BookQuantityAtCount { get; private set; }

    public DateTimeOffset? CountedAt { get; private set; }

    public Guid? CountedById { get; private set; }

    public string? CountedByName { get; private set; }

    public bool RecountRequested { get; private set; }

    /// <summary>差異（實盤 − 計數當下帳面）；尚未計數為 null。</summary>
    public decimal? Difference => CountedQuantity is { } counted && BookQuantityAtCount is { } book ? counted - book : null;

    /// <summary>「現場找到」的行：<paramref name="lineNo"/> 由呼叫端在鎖住盤點單後取最大行號＋1。</summary>
    public static StocktakeLine CreateFound(
        Guid id, Guid stocktakeId, int lineNo, Guid locationId, Guid itemId, TrackingMode trackingMode,
        string? lotNo, string? serialNo, Guid? lotId, Guid? serialId)
    {
        if (id == Guid.Empty || locationId == Guid.Empty || itemId == Guid.Empty)
        {
            throw new DomainRuleException("盤點明細必須指定儲位與物料。");
        }
        var lot = Code(lotNo, "批號");
        var serial = Code(serialNo, "序號");
        switch (trackingMode)
        {
            case TrackingMode.None when lot is not null || serial is not null:
                throw new DomainRuleException("這個物料不追蹤批號、序號，請不要填寫。");
            case TrackingMode.Lot when lot is null || serial is not null:
                throw new DomainRuleException("批號管理的物料必須填寫批號（不填序號）。");
            case TrackingMode.Serial when serial is null:
                throw new DomainRuleException("序號管理的物料必須填寫序號。");
        }
        return new StocktakeLine
        {
            Id = id,
            StocktakeId = stocktakeId,
            LineNo = lineNo,
            LocationId = locationId,
            ItemId = itemId,
            LotNo = lot,
            SerialNo = serial,
            LotId = lotId,
            SerialId = serialId,
            StockStatus = StockStatus.Available,
            IsAdded = true,
        };
    }

    /// <summary>記錄（或重新記錄）實盤量與計數當下帳面量；呼叫端要先 <see cref="Stocktake.EnsureCounting"/>。</summary>
    public void Count(
        decimal counted, decimal book, TrackingMode trackingMode, int decimalPlaces, Guid actorId, string actorName, DateTimeOffset now)
    {
        if (counted < 0)
        {
            throw new DomainRuleException("實盤量不可小於 0。");
        }
        if (counted > StockDocumentLine.MaxQuantity)
        {
            throw new DomainRuleException("數量過大，請確認單位與輸入。");
        }
        if (decimal.Round(counted, decimalPlaces) != counted)
        {
            throw new DomainRuleException(decimalPlaces == 0 ? "這個物料的單位不可有小數。" : $"這個物料的單位最多 {decimalPlaces} 位小數。");
        }
        if (trackingMode == TrackingMode.Serial && counted is not (0 or 1))
        {
            throw new DomainRuleException("序號物料的實盤量只能是 0 或 1。");
        }
        CountedQuantity = counted;
        BookQuantityAtCount = book;
        CountedAt = now.ToUniversalTime();
        CountedById = actorId;
        CountedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "盤點人員");
        RecountRequested = false;
    }

    internal void MarkRecount() => RecountRequested = true;

    /// <summary>
    /// 「現場找到」的行每次計數前更新批號／序號 ID：建立時號碼還不存在（ID 為 null），之後若有進倉建立了同一號碼，
    /// 帳面量要算到那筆結存（M6-02 審查 2）。快照行的 ID 來自結存，不會變。
    /// </summary>
    public void RefreshDimensions(Guid? lotId, Guid? serialId)
    {
        if (IsAdded)
        {
            LotId = lotId;
            SerialId = serialId;
        }
    }

    private static string? Code(string? value, string label) =>
        Rules.Optional(value, StockDocumentLine.CodeMaxLength, label)?.ToUpperInvariant();
}
