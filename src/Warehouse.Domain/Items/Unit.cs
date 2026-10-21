using System.Text.RegularExpressions;

namespace Warehouse.Domain.Items;

/// <summary>
/// 計量單位（PCS、KG…）。庫存一律以物料的基本單位記帳（ADR-0006）；
/// DecimalPlaces 限制此單位的數量可有幾位小數（PCS=0、KG=3）。
/// </summary>
public sealed partial class Unit
{
    public const int CodeMaxLength = 16;
    public const int NameMaxLength = 50;
    public const int MaxDecimalPlaces = 4;

    private Unit()
    {
    }

    public Unit(Guid id, string code, string name, int decimalPlaces)
    {
        Id = id;
        Code = Rules.Matches(code, CodePattern(), "單位代碼", "1～16 碼大寫英數");
        Name = Rules.Required(name, NameMaxLength, "單位名稱");
        if (decimalPlaces is < 0 or > MaxDecimalPlaces)
        {
            throw new DomainRuleException($"單位小數位數必須介於 0～{MaxDecimalPlaces}。");
        }
        DecimalPlaces = (short)decimalPlaces;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public short DecimalPlaces { get; private set; }

    public bool IsActive { get; private set; } = true;

    [GeneratedRegex("^[A-Z0-9]{1,16}$")]
    private static partial Regex CodePattern();
}
