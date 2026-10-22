using Warehouse.Domain.Items;

namespace Warehouse.Domain.Inventory;

/// <summary>庫存單據種類：期初（M3）、進倉、出倉、移庫（M4）；調整在 M6。</summary>
public enum StockDocumentType
{
    Opening,
    Inbound,
    Outbound,
    Transfer,
}

/// <summary>
/// 進出倉單表頭（M4-10）：用途類型、往來對象（廠商／客戶／部門主檔）、外部單號、備註。
/// 哪些欄位啟用、必填由系統設定決定，檢查在服務層（設定可隨時改，不寫死在 Domain）。
/// </summary>
public sealed record StockDocumentHeader(Guid? PurposeId, Guid? PartnerId, string? ExternalNo, string? Note);

/// <summary>單據狀態：草稿 → 送出 → 過帳（核准即過帳）；草稿或送出可取消；送出可退回成草稿。</summary>
public enum StockDocumentStatus
{
    Draft,
    Submitted,
    Posted,
    Cancelled,
}

/// <summary>結存狀態（ADR-0004）：只有 Available 計入可用量。待驗儲位的結存一律 Quarantine。</summary>
public enum StockStatus
{
    Available,
    Quarantine,
}

/// <summary>
/// 庫存單據（M3-03 期初單）。狀態轉換與明細規則在這裡；真正改變結存的只有過帳服務
/// （<c>IInventoryPostingService</c>），本類別只負責「單據」本身（AGENTS.md 第六節）。
/// 已過帳的單據與明細，資料庫 trigger 禁止修改與刪除。
/// </summary>
public sealed class StockDocument
{
    public const int NoteMaxLength = 500;
    public const int MaxLines = 500;

    private readonly List<StockDocumentLine> _lines = [];

    private StockDocument()
    {
    }

    public static StockDocument CreateOpening(
        Guid id, string docNo, Guid warehouseId, string? note, Guid actorId, string actorName, DateTimeOffset now)
    {
        if (id == Guid.Empty || warehouseId == Guid.Empty || actorId == Guid.Empty)
        {
            throw new DomainRuleException("期初單必須指定倉庫與建立者。");
        }
        return new StockDocument
        {
            Id = id,
            DocNo = Rules.Required(docNo, 20, "單號"),
            DocType = StockDocumentType.Opening,
            Status = StockDocumentStatus.Draft,
            WarehouseId = warehouseId,
            Note = Rules.Optional(note, NoteMaxLength, "備註"),
            CreatedById = actorId,
            CreatedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "建立者"),
            CreatedAt = now.ToUniversalTime(),
        };
    }

    /// <summary>
    /// 建立進倉、出倉或移庫單（草稿）。<paramref name="reversalOfDocumentId"/>：沖銷單引用的原單。
    /// 移庫單的 <paramref name="warehouseId"/> 是來源倉庫，目的儲位可在其他倉庫。
    /// </summary>
    public static StockDocument CreateMovement(
        Guid id, string docNo, StockDocumentType type, Guid warehouseId, StockDocumentHeader header,
        Guid? reversalOfDocumentId, Guid actorId, string actorName, DateTimeOffset now)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainRuleException("單據類型不正確。");
        }
        if (type == StockDocumentType.Opening)
        {
            throw new DomainRuleException("期初單請用期初建帳功能。");
        }
        if (id == Guid.Empty || warehouseId == Guid.Empty || actorId == Guid.Empty)
        {
            throw new DomainRuleException("單據必須指定倉庫與建立者。");
        }
        var doc = new StockDocument
        {
            Id = id,
            DocNo = Rules.Required(docNo, 20, "單號"),
            DocType = type,
            Status = StockDocumentStatus.Draft,
            WarehouseId = warehouseId,
            ReversalOfDocumentId = reversalOfDocumentId,
            CreatedById = actorId,
            CreatedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "建立者"),
            CreatedAt = now.ToUniversalTime(),
        };
        doc.ApplyHeader(header);
        return doc;
    }

    public Guid Id { get; private set; }

    public string DocNo { get; private set; } = string.Empty;

    public StockDocumentType DocType { get; private set; }

    public StockDocumentStatus Status { get; private set; }

    public Guid WarehouseId { get; private set; }

    public string? Note { get; private set; }

    public Guid CreatedById { get; private set; }

    public string CreatedByName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? ApprovedById { get; private set; }

    public string? ApprovedByName { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    /// <summary>最近一次退回的原因（退回後狀態回到草稿）。</summary>
    public string? ReturnComment { get; private set; }

    /// <summary>用途類型（document_purposes）；期初單為 null。</summary>
    public Guid? PurposeId { get; private set; }

    /// <summary>往來對象：廠商、客戶或部門（business_partners）。</summary>
    public Guid? PartnerId { get; private set; }

    /// <summary>外部單號（採購單、工單等，只記錄文字，不整合 ERP）。</summary>
    public string? ExternalNo { get; private set; }

    /// <summary>這張單是哪一張已過帳單的沖銷單。</summary>
    public Guid? ReversalOfDocumentId { get; private set; }

    public const int ExternalNoMaxLength = 50;

    public IReadOnlyList<StockDocumentLine> Lines => _lines;

    /// <summary>修改表頭（只限草稿）。</summary>
    public void UpdateHeader(StockDocumentHeader header)
    {
        EnsureDraft();
        if (DocType == StockDocumentType.Opening)
        {
            throw new DomainRuleException("期初單沒有這些表頭欄位。");
        }
        ApplyHeader(header);
    }

    /// <summary>
    /// 進出倉、移庫單加一行明細（只限草稿）。數量必須大於 0；
    /// 移庫必須指定目的儲位且不可與來源相同；依預留出庫（<paramref name="reservationId"/>）只限出倉單；
    /// 有效期限只在進倉時有意義（建立新批號）。追蹤模式規則同期初（批號必填、序號每行 1）。
    /// </summary>
    public StockDocumentLine AddMovementLine(
        Guid lineId, Guid itemId, TrackingMode trackingMode, int decimalPlaces, Guid locationId, Guid? targetLocationId,
        string? lotNo, string? serialNo, DateOnly? expiryDate, decimal quantity, Guid? reservationId)
    {
        EnsureDraft();
        if (DocType == StockDocumentType.Opening)
        {
            throw new DomainRuleException("期初單請用期初明細。");
        }
        if (quantity <= 0)
        {
            throw new DomainRuleException("數量必須大於 0。");
        }
        if (DocType == StockDocumentType.Transfer)
        {
            if (targetLocationId is null || targetLocationId == Guid.Empty)
            {
                throw new DomainRuleException("移庫必須指定目的儲位。");
            }
            if (targetLocationId == locationId)
            {
                throw new DomainRuleException("目的儲位不可與來源儲位相同。");
            }
        }
        else if (targetLocationId is not null)
        {
            throw new DomainRuleException("只有移庫單有目的儲位。");
        }
        if (reservationId is not null && DocType != StockDocumentType.Outbound)
        {
            throw new DomainRuleException("只有出倉單可以指定預留。");
        }
        if (expiryDate is not null && DocType != StockDocumentType.Inbound)
        {
            throw new DomainRuleException("有效期限只在進倉時填寫。");
        }
        if (_lines.Count >= MaxLines)
        {
            throw new DomainRuleException($"一張單最多 {MaxLines} 行，請分成多張單。");
        }
        var line = StockDocumentLine.Create(
            lineId, Id, _lines.Count == 0 ? 1 : _lines.Max(l => l.LineNo) + 1, itemId, trackingMode, decimalPlaces,
            locationId, lotNo, serialNo, expiryDate, quantity);
        line.SetMovement(targetLocationId, reservationId);
        if (_lines.Any(l => l.SameKey(line)))
        {
            throw new DomainRuleException("這張單已經有相同儲位、物料、批號／序號的明細，請直接修改數量。");
        }
        _lines.Add(line);
        return line;
    }

    /// <summary>
    /// 不經審核直接過帳（系統設定「單據審核」關閉時，FR-08）：草稿 → 已過帳，確認者記為核准者。
    /// 呼叫端在同一交易內完成實際的結存異動。
    /// </summary>
    public void PostDirect(Guid actorId, string actorName, DateTimeOffset now)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new DomainRuleException("單據至少要有一行明細。");
        }
        Status = StockDocumentStatus.Posted;
        SubmittedAt = now.ToUniversalTime();
        ApprovedById = actorId;
        ApprovedByName = Rules.Required(actorName, ItemReview.ActorNameMaxLength, "確認者");
        PostedAt = now.ToUniversalTime();
    }

    private void ApplyHeader(StockDocumentHeader header)
    {
        PurposeId = header.PurposeId;
        PartnerId = header.PartnerId;
        ExternalNo = Rules.Optional(header.ExternalNo, ExternalNoMaxLength, "外部單號");
        Note = Rules.Optional(header.Note, NoteMaxLength, "備註");
    }

    /// <summary>
    /// 加一行明細（只限草稿）。同一份單內，「儲位＋物料＋批號＋序號」不可重複。
    /// 物料的追蹤模式與單位小數位數由呼叫端從資料庫讀出後傳入。
    /// </summary>
    public StockDocumentLine AddLine(
        Guid lineId, Guid itemId, TrackingMode trackingMode, int decimalPlaces, Guid locationId,
        string? lotNo, string? serialNo, DateOnly? expiryDate, decimal quantity)
    {
        EnsureDraft();
        if (_lines.Count >= MaxLines)
        {
            throw new DomainRuleException($"一張單最多 {MaxLines} 行，請分成多張單。");
        }
        var line = StockDocumentLine.Create(
            lineId, Id, _lines.Count == 0 ? 1 : _lines.Max(l => l.LineNo) + 1, itemId, trackingMode, decimalPlaces,
            locationId, lotNo, serialNo, expiryDate, quantity);
        if (_lines.Any(l => l.ItemId is null && l.LocationId == locationId))
        {
            throw new DomainRuleException("這個儲位已標記為空儲位，請先刪除那一行。");
        }
        if (_lines.Any(l => l.SameKey(line)))
        {
            throw new DomainRuleException("這張單已經有相同儲位、物料、批號／序號的明細，請直接修改數量。");
        }
        _lines.Add(line);
        return line;
    }

    /// <summary>
    /// 確認整個儲位是空的（FR-04「確認為 0」）：沒有物料的明細，過帳後該儲位標記為已清點、不產生結存。
    /// </summary>
    public StockDocumentLine AddEmptyLocation(Guid lineId, Guid locationId)
    {
        EnsureDraft();
        if (_lines.Any(l => l.LocationId == locationId))
        {
            throw new DomainRuleException("這個儲位已經有明細，不能再標記為空儲位。");
        }
        var line = StockDocumentLine.CreateEmptyLocation(
            lineId, Id, _lines.Count == 0 ? 1 : _lines.Max(l => l.LineNo) + 1, locationId);
        _lines.Add(line);
        return line;
    }

    public void RemoveLine(Guid lineId)
    {
        EnsureDraft();
        var line = _lines.SingleOrDefault(l => l.Id == lineId)
            ?? throw new DomainRuleException("找不到這行明細。");
        _lines.Remove(line);
    }

    public void Submit(DateTimeOffset now)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new DomainRuleException(DocType == StockDocumentType.Opening
                ? "期初單至少要有一行明細（確認為 0 也要寫一行）。"
                : "單據至少要有一行明細。");
        }
        Status = StockDocumentStatus.Submitted;
        SubmittedAt = now.ToUniversalTime();
    }

    /// <summary>退回給建立者修改：送出 → 草稿，原因必填。</summary>
    public void Return(string comment)
    {
        if (Status != StockDocumentStatus.Submitted)
        {
            throw new DomainRuleException("只有已送出的單可以退回。");
        }
        ReturnComment = Rules.Required(comment, NoteMaxLength, "退回原因");
        Status = StockDocumentStatus.Draft;
        SubmittedAt = null;
    }

    public void Cancel()
    {
        if (Status is not (StockDocumentStatus.Draft or StockDocumentStatus.Submitted))
        {
            throw new DomainRuleException("只有草稿或已送出的單可以取消；已過帳的單不能取消，要更正請用調整單。");
        }
        Status = StockDocumentStatus.Cancelled;
    }

    /// <summary>
    /// 核准並標記為已過帳（呼叫端在同一交易內完成實際的結存異動）。
    /// D-07：<paramref name="allowSelfApprove"/> 為 false 時，建立者不能核准自己的單。
    /// </summary>
    public void MarkPosted(Guid approverId, string approverName, bool allowSelfApprove, DateTimeOffset now)
    {
        if (Status != StockDocumentStatus.Submitted)
        {
            throw new DomainRuleException("只有已送出的單可以核准。");
        }
        if (!allowSelfApprove && approverId == CreatedById)
        {
            throw new DomainRuleException("系統設定不允許建立者核准自己的單，請由其他倉庫主管核准。");
        }
        Status = StockDocumentStatus.Posted;
        ApprovedById = approverId;
        ApprovedByName = Rules.Required(approverName, ItemReview.ActorNameMaxLength, "核准者");
        PostedAt = now.ToUniversalTime();
    }

    private void EnsureDraft()
    {
        if (Status != StockDocumentStatus.Draft)
        {
            throw new DomainRuleException("只有草稿可以修改明細。");
        }
    }
}

/// <summary>
/// 單據明細。數量一律是物料的基本單位，小數位數依單位限制；0 表示「確認為 0」（已清點、沒有庫存）。
/// 批號、序號依物料追蹤模式必填或禁止填；序號物料每行數量必須是 1。
/// <see cref="LotId"/>、<see cref="SerialId"/> 在過帳時才填入（批號、序號沒有就建立）。
/// </summary>
public sealed class StockDocumentLine
{
    public const int CodeMaxLength = 50;

    /// <summary>單行數量上限（資料庫 numeric(19,4) 可到 10^15，留安全空間）。</summary>
    public const decimal MaxQuantity = 1_000_000_000_000m;

    private StockDocumentLine()
    {
    }

    internal static StockDocumentLine Create(
        Guid id, Guid documentId, int lineNo, Guid itemId, TrackingMode trackingMode, int decimalPlaces, Guid locationId,
        string? lotNo, string? serialNo, DateOnly? expiryDate, decimal quantity)
    {
        if (id == Guid.Empty || itemId == Guid.Empty || locationId == Guid.Empty)
        {
            throw new DomainRuleException("明細必須指定物料與儲位。");
        }
        if (quantity < 0)
        {
            throw new DomainRuleException("期初數量不可小於 0。");
        }
        if (quantity > MaxQuantity)
        {
            throw new DomainRuleException("數量過大，請確認單位與輸入。");
        }
        if (decimal.Round(quantity, decimalPlaces) != quantity)
        {
            throw new DomainRuleException(decimalPlaces == 0
                ? "這個物料的單位不可有小數。"
                : $"這個物料的單位最多 {decimalPlaces} 位小數。");
        }
        var lot = Code(lotNo, "批號");
        var serial = Code(serialNo, "序號");
        switch (trackingMode)
        {
            case TrackingMode.None when lot is not null || serial is not null:
                throw new DomainRuleException("這個物料不追蹤批號、序號，請不要填寫。");
            case TrackingMode.Lot when lot is null && quantity > 0:
                throw new DomainRuleException("批號管理的物料必須填寫批號。");
            case TrackingMode.Lot when serial is not null:
                throw new DomainRuleException("批號管理的物料不填序號。");
            case TrackingMode.Serial when serial is null && quantity > 0:
                throw new DomainRuleException("序號管理的物料必須填寫序號。");
            case TrackingMode.Serial when quantity is not (0 or 1):
                throw new DomainRuleException("序號管理的物料每行數量只能是 1（每個序號一行）。");
        }
        if (expiryDate is not null && lot is null)
        {
            throw new DomainRuleException("有效期限只適用於批號。");
        }

        return new StockDocumentLine
        {
            Id = id,
            DocumentId = documentId,
            LineNo = lineNo,
            ItemId = itemId,
            LocationId = locationId,
            LotNo = lot,
            SerialNo = serial,
            ExpiryDate = expiryDate,
            Quantity = quantity,
        };
    }

    public Guid Id { get; private set; }

    public Guid DocumentId { get; private set; }

    public int LineNo { get; private set; }

    /// <summary>物料；null 表示「整個儲位確認為空」。</summary>
    public Guid? ItemId { get; private set; }

    public Guid LocationId { get; private set; }

    public string? LotNo { get; private set; }

    public string? SerialNo { get; private set; }

    public DateOnly? ExpiryDate { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>過帳時由過帳流程以 SQL 填入（批號沒有就建立）。</summary>
    public Guid? LotId { get; private set; }

    /// <summary>過帳時由過帳流程以 SQL 填入（序號沒有就建立）。</summary>
    public Guid? SerialId { get; private set; }

    /// <summary>移庫的目的儲位（可在其他倉庫）；其他單據為 null。</summary>
    public Guid? TargetLocationId { get; private set; }

    /// <summary>依預留出庫時指定的預留（只限出倉單）。</summary>
    public Guid? ReservationId { get; private set; }

    internal void SetMovement(Guid? targetLocationId, Guid? reservationId)
    {
        TargetLocationId = targetLocationId;
        ReservationId = reservationId;
    }

    internal static StockDocumentLine CreateEmptyLocation(Guid id, Guid documentId, int lineNo, Guid locationId)
    {
        if (id == Guid.Empty || locationId == Guid.Empty)
        {
            throw new DomainRuleException("明細必須指定儲位。");
        }
        return new StockDocumentLine { Id = id, DocumentId = documentId, LineNo = lineNo, LocationId = locationId, Quantity = 0 };
    }


    internal bool SameKey(StockDocumentLine other) =>
        other.LocationId == LocationId && other.TargetLocationId == TargetLocationId && other.ItemId == ItemId
        && other.LotNo == LotNo && other.SerialNo == SerialNo;

    /// <summary>批號、序號：去頭尾空白、轉大寫（掃碼與手輸一致），長度上限 50。</summary>
    private static string? Code(string? value, string label)
    {
        var trimmed = Rules.Optional(value, CodeMaxLength, label);
        return trimmed?.ToUpperInvariant();
    }
}
