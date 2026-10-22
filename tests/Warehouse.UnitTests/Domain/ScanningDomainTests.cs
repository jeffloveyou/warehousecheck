using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Domain.Scanning;

namespace Warehouse.UnitTests.Domain;

/// <summary>M5-01 掃碼前處理、統一 Parser 與原廠條碼規則（T-M5-01-01～05）。</summary>
public class ScanningDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    // T-M5-01-01
    [Theory]
    [InlineData("4710088430012\r\n", "4710088430012", null)]       // Enter 尾碼
    [InlineData("\t0012345\t", "0012345", null)]                    // Tab 尾碼，前導零保留
    [InlineData("  ABC  ", "ABC", null)]
    [InlineData("0104712345678901\t10LOT\u001d", "010471234567890110LOT", null)]   // 中間夾 Tab、GS（審查 S6）
    [InlineData("A1", "A1", "太短")]                                 // 漏字
    [InlineData("\r\n", "", "沒有收到")]
    [InlineData(null, "", "沒有收到")]
    public void 掃碼前處理_去尾碼與控制字元_過短拒絕(string? raw, string value, string? error)
    {
        var (cleaned, message) = ScanInput.Check(raw);

        Assert.Equal(value, cleaned);
        if (error is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(error, message);
        }
    }

    // T-M5-01-01
    [Fact]
    public void 掃碼過長_視為兩筆黏在一起()
    {
        var (_, message) = ScanInput.Check(new string('7', ScanInput.MaxLength + 1));

        Assert.Contains("黏在一起", message);
    }

    // T-M5-01-02
    [Theory]
    [InlineData("0012345\t", ScanCodeKind.Text, "0012345")]
    [InlineData("ｗｈ：ｉｔｅｍ：ｅｌ－０００１２５\r", ScanCodeKind.Item, "WH:ITEM:EL-000125")]
    [InlineData("wh:loc:w1:a-01\n", ScanCodeKind.Location, "WH:LOC:W1:A-01")]
    [InlineData("abc-12x", ScanCodeKind.Text, "ABC-12X")]
    public void 統一Parser_回傳正規化文字_前導零不變(string raw, ScanCodeKind kind, string normalized)
    {
        var scan = ScanCodeParser.Parse(raw);

        Assert.Equal((kind, normalized), (scan.Kind, scan.Normalized));
    }

    // T-M5-01-03
    [Theory]
    [InlineData("4710088430012", 1, null, TrackingMode.None, 0, null)]
    [InlineData(" 4710088430012\r\n", 10, "EAN_13", TrackingMode.Lot, 0, null)]
    [InlineData("123", 1, null, TrackingMode.None, 0, "至少")]
    [InlineData("4710 0884", 1, null, TrackingMode.None, 0, "英數字")]
    [InlineData("條碼12345", 1, null, TrackingMode.None, 0, "英數字")]
    [InlineData("WH:ITEM:EL-000125", 1, null, TrackingMode.None, 0, "WH:")]
    [InlineData("EL-000125", 1, null, TrackingMode.None, 0, "料號")]
    [InlineData("4710088430012", 0, null, TrackingMode.None, 0, "包裝數量")]
    [InlineData("4710088430012", 1.5, null, TrackingMode.None, 0, "整數")]
    [InlineData("4710088430012", 1.5, null, TrackingMode.None, 1, null)]
    [InlineData("4710088430012", 2, null, TrackingMode.Serial, 0, "序號")]
    [InlineData("4710088430012", 1, "碼制", TrackingMode.None, 0, "英數字")]
    public void 原廠條碼規則(string text, double pack, string? symbology, TrackingMode tracking, int decimals, string? error)
    {
        BarcodeFactory create = () => ItemBarcode.Create(
            Guid.NewGuid(), text, (decimal)pack, symbology, tracking, decimals, Guid.NewGuid(), "作業員", Now);

        if (error is null)
        {
            var barcode = create();
            Assert.Equal(text.Trim(), barcode.Text);
            Assert.Equal(text.Trim().ToUpperInvariant(), barcode.NormalizedText);
            Assert.Equal((decimal)pack, barcode.PackQuantity);
            Assert.Equal(symbology?.ToLowerInvariant(), barcode.Symbology);
            Assert.True(barcode.IsActive);
        }
        else
        {
            Assert.Contains(error, Assert.Throws<DomainRuleException>(() => create()).Message);
        }
    }

    private delegate ItemBarcode BarcodeFactory();
}
