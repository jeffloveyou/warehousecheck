using System.Text.RegularExpressions;

namespace Warehouse.Domain.Warehousing;

/// <summary>
/// 倉庫（M3-01；類別名稱用 WarehouseSite，避免與根命名空間 Warehouse 衝突，資料表仍是 warehouses）。代碼建立後不可改：儲位 QR 內容（<c>WH:LOC:倉庫碼:儲位碼</c>）會用到。
/// 停用的倉庫不能新增儲位；既有資料保留。
/// </summary>
public sealed partial class WarehouseSite
{
    public const int CodeMaxLength = 8;
    public const int NameMaxLength = 50;

    private WarehouseSite()
    {
    }

    public WarehouseSite(Guid id, string code, string name, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new DomainRuleException("倉庫識別碼不可為空。");
        }
        Id = id;
        Code = Rules.Matches(code?.Trim(), CodePattern(), "倉庫代碼", "1～8 碼大寫英數（例 W1）");
        Name = Rules.Required(name, NameMaxLength, "倉庫名稱");
        CreatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// 刪除時間（M8-01）。用過的倉庫清空後「刪除」只是移除：列表與選單不再出現、不能再用，
    /// 歷史單據與異動照常顯示倉庫碼與名稱；倉庫碼保留不可重用（舊 QR 標籤不會誤對到新倉庫）。從未用過的倉庫則真的刪除資料列。
    /// </summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public void Rename(string name)
    {
        EnsureNotDeleted();
        Name = Rules.Required(name, NameMaxLength, "倉庫名稱");
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate()
    {
        EnsureNotDeleted();
        IsActive = true;
    }

    /// <summary>移除（同時停用）；是否允許（結存為 0 等）由服務在鎖定後檢查。</summary>
    public void MarkDeleted(DateTimeOffset now, string by)
    {
        if (IsDeleted)
        {
            throw new DomainRuleException("倉庫已經刪除。");
        }
        IsActive = false;
        DeletedAt = now.ToUniversalTime();
        DeletedBy = by;
    }

    /// <summary>還原刪除；還原後仍是停用，確認無誤再啟用。</summary>
    public void Restore()
    {
        if (!IsDeleted)
        {
            throw new DomainRuleException("倉庫沒有被刪除。");
        }
        DeletedAt = null;
        DeletedBy = null;
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainRuleException("倉庫已刪除；請先還原。");
        }
    }

    [GeneratedRegex("^[A-Z0-9]{1,8}$")]
    private static partial Regex CodePattern();
}
