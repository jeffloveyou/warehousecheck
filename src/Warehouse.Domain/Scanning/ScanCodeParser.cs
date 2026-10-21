using System.Text.RegularExpressions;
using Warehouse.Domain.Text;

namespace Warehouse.Domain.Scanning;

/// <summary>掃到的內容是什麼。</summary>
public enum ScanCodeKind
{
    /// <summary>儲位 QR：<c>WH:LOC:倉庫碼:儲位碼</c>。</summary>
    Location,

    /// <summary>物料 QR：<c>WH:ITEM:料號</c>，或直接掃到料號文字。</summary>
    Item,

    /// <summary>其他文字：交給搜尋（M2）處理，不自動當成任何識別碼。</summary>
    Text,
}

/// <summary>解析結果。<see cref="Raw"/> 保留原始輸入，畫面可顯示「掃到：…」。</summary>
public sealed record ScanCode(ScanCodeKind Kind, string Raw, string? WarehouseCode, string? LocationCode, string? ItemCode);

/// <summary>
/// 掃碼內容解析（M3-04；M5-01 會擴充為相機、HID、PDA 共用的統一 Parser）。
/// 輸入先經 <see cref="TextNormalizer.Normalize"/>：全形、大小寫、前後空白都不影響（HID 掃碼槍常帶尾端空白或換行）。
/// 只有完全符合格式時才判定為儲位或物料；其他一律當成搜尋文字，不猜測。
/// </summary>
public static partial class ScanCodeParser
{
    public const string LocationPrefix = "WH:LOC:";
    public const string ItemPrefix = "WH:ITEM:";

    public static ScanCode Parse(string? raw)
    {
        var original = raw ?? string.Empty;
        var text = TextNormalizer.Normalize(original);

        var location = LocationPattern().Match(text);
        if (location.Success)
        {
            return new ScanCode(ScanCodeKind.Location, original, location.Groups["wh"].Value, location.Groups["loc"].Value, null);
        }
        var item = ItemPattern().Match(text);
        if (item.Success)
        {
            return new ScanCode(ScanCodeKind.Item, original, null, null, item.Groups["code"].Value);
        }
        return new ScanCode(ScanCodeKind.Text, original, null, null, null);
    }

    [GeneratedRegex("^WH:LOC:(?<wh>[A-Z0-9]{1,8}):(?<loc>[A-Z0-9]+(-[A-Z0-9]+){0,5})$")]
    private static partial Regex LocationPattern();

    /// <summary>物料：有前綴的 QR，或直接是料號（例掃到印在包裝上的料號文字）。</summary>
    [GeneratedRegex("^(WH:ITEM:)?(?<code>[A-Z0-9]{2,4}-[0-9]{6,})$")]
    private static partial Regex ItemPattern();
}
