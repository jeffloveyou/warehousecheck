namespace Warehouse.Domain.Inventory;

/// <summary>
/// 快速進出的掃碼購物車（ADR-0026）：先把一批料掃進來（記在帳號，換裝置也在），再整批選進倉／出倉／移庫轉成草稿單。
/// - 只是清單：不動結存、不預留；轉成草稿時才套用單據的全部規則（倉庫權限、用途、儲位狀態、追蹤模式）。
/// - 儲位可空（選動作後再補）；掃到儲位時，購物車鎖定在該儲位的倉庫，之後不能混別倉的儲位。
/// - 同物料＋同儲位合併數量。放棄時實體刪除（使用者決定不留紀錄）。
/// </summary>
public sealed class ScanCart
{
    public const int NoteMaxLength = 100;

    /// <summary>一台購物車的行數上限：與一張單的上限相同（序號物料轉單時會展開成多行，另外檢查）。</summary>
    public const int MaxLines = StockDocument.MaxLines;

    private readonly List<ScanCartLine> _lines = [];

    private ScanCart()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public string? Note { get; private set; }

    /// <summary>購物車裡有儲位時的倉庫（所有有儲位的行都在這個倉庫）；沒有任何儲位時為 null。</summary>
    public Guid? WarehouseId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ScanCartLine> Lines => _lines;

    public static ScanCart Create(Guid id, Guid ownerId, string? note, DateTimeOffset now)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty)
        {
            throw new DomainRuleException("購物車必須指定擁有者。");
        }
        var cart = new ScanCart { Id = id, OwnerId = ownerId, CreatedAt = now, UpdatedAt = now };
        cart.SetNote(note, now);
        return cart;
    }

    public void SetNote(string? note, DateTimeOffset now)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text is { Length: > NoteMaxLength })
        {
            throw new DomainRuleException($"備註最多 {NoteMaxLength} 字。");
        }
        Note = text;
        UpdatedAt = now;
    }

    /// <summary>
    /// 加入一筆：同物料＋同儲位就加到原本那一行（回傳那一行），否則新增一行。
    /// <paramref name="locationWarehouseId"/> 是儲位所在的倉庫（有儲位時必填），必須與購物車已鎖定的倉庫相同。
    /// </summary>
    public ScanCartLine Add(
        Guid lineId, Guid itemId, int decimalPlaces, Guid? locationId, Guid? locationWarehouseId, decimal quantity, DateTimeOffset now)
    {
        RequireLocationWarehouse(locationId, locationWarehouseId);
        ScanCartLine.RequireQuantity(quantity, decimalPlaces);
        var existing = _lines.FirstOrDefault(l => l.ItemId == itemId && l.LocationId == locationId);
        if (existing is not null)
        {
            existing.SetQuantity(existing.Quantity + quantity, decimalPlaces);
            UpdatedAt = now;
            return existing;
        }
        if (_lines.Count >= MaxLines)
        {
            throw new DomainRuleException($"一台購物車最多 {MaxLines} 行，請先轉成單據或另開一台。");
        }
        var line = ScanCartLine.Create(lineId, Id, _lines.Count == 0 ? 1 : _lines.Max(l => l.LineNo) + 1, itemId, locationId, quantity, decimalPlaces);
        _lines.Add(line);
        if (locationWarehouseId is { } w)
        {
            WarehouseId = w;
        }
        UpdatedAt = now;
        return line;
    }

    /// <summary>改數量與儲位。改成同物料、同儲位的另一行時不合併，提示改那一行。</summary>
    public void UpdateLine(Guid lineId, decimal quantity, int decimalPlaces, Guid? locationId, Guid? locationWarehouseId, DateTimeOffset now)
    {
        var line = Find(lineId);
        RequireLocationWarehouse(locationId, locationWarehouseId, ignoring: line);
        if (_lines.Any(l => l.Id != lineId && l.ItemId == line.ItemId && l.LocationId == locationId))
        {
            throw new DomainRuleException("同一個物料在這個儲位已經有一行，請直接改那一行的數量。");
        }
        line.SetQuantity(quantity, decimalPlaces);
        line.SetLocation(locationId);
        RefreshWarehouse(locationWarehouseId);
        UpdatedAt = now;
    }

    public void RemoveLine(Guid lineId, DateTimeOffset now)
    {
        _lines.Remove(Find(lineId));
        RefreshWarehouse(null);
        UpdatedAt = now;
    }

    private ScanCartLine Find(Guid lineId) =>
        _lines.SingleOrDefault(l => l.Id == lineId) ?? throw new DomainRuleException("找不到這一行（可能已在其他裝置刪除），請重新整理。");

    private void RequireLocationWarehouse(Guid? locationId, Guid? locationWarehouseId, ScanCartLine? ignoring = null)
    {
        if (locationId is null)
        {
            return;
        }
        if (locationWarehouseId is not { } w || w == Guid.Empty)
        {
            throw new DomainRuleException("儲位必須指定倉庫。");
        }
        var others = _lines.Any(l => l != ignoring && l.LocationId is not null);
        if (others && WarehouseId is { } locked && locked != w)
        {
            throw new DomainRuleException("這台購物車的儲位都在另一個倉庫；一台購物車只能有一個倉庫，請另開一台。");
        }
    }

    // 沒有任何行有儲位時解除倉庫鎖定；改了儲位時用新儲位的倉庫
    private void RefreshWarehouse(Guid? locationWarehouseId)
    {
        if (!_lines.Any(l => l.LocationId is not null))
        {
            WarehouseId = null;
        }
        else if (locationWarehouseId is { } w)
        {
            WarehouseId = w;
        }
    }
}

public sealed class ScanCartLine
{
    private ScanCartLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid CartId { get; private set; }

    public int LineNo { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid? LocationId { get; private set; }

    public decimal Quantity { get; private set; }

    internal static ScanCartLine Create(Guid id, Guid cartId, int lineNo, Guid itemId, Guid? locationId, decimal quantity, int decimalPlaces)
    {
        if (id == Guid.Empty || itemId == Guid.Empty)
        {
            throw new DomainRuleException("必須指定物料。");
        }
        RequireQuantity(quantity, decimalPlaces);
        return new ScanCartLine { Id = id, CartId = cartId, LineNo = lineNo, ItemId = itemId, LocationId = locationId, Quantity = quantity };
    }

    internal void SetQuantity(decimal quantity, int decimalPlaces)
    {
        RequireQuantity(quantity, decimalPlaces);
        Quantity = quantity;
    }

    internal void SetLocation(Guid? locationId) => LocationId = locationId;

    internal static void RequireQuantity(decimal quantity, int decimalPlaces)
    {
        if (quantity <= 0)
        {
            throw new DomainRuleException("數量必須大於 0。");
        }
        if (quantity > StockDocumentLine.MaxQuantity)
        {
            throw new DomainRuleException("數量過大，請確認單位與輸入。");
        }
        if (decimal.Round(quantity, decimalPlaces) != quantity)
        {
            throw new DomainRuleException(decimalPlaces == 0 ? "這個物料的單位不可有小數。" : $"這個物料的單位最多 {decimalPlaces} 位小數。");
        }
    }
}
