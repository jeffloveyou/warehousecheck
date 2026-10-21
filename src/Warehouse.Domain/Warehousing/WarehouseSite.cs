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

    public void Rename(string name) => Name = Rules.Required(name, NameMaxLength, "倉庫名稱");

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    [GeneratedRegex("^[A-Z0-9]{1,8}$")]
    private static partial Regex CodePattern();
}
