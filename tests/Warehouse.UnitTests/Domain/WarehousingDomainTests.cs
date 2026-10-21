using Warehouse.Domain;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Warehousing;

namespace Warehouse.UnitTests.Domain;

/// <summary>M3-01／M3-04／M3-06 倉庫、儲位、掃碼解析（T-M3-01-01～04）。</summary>
public class WarehousingDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    [Theory]
    [InlineData("W1", true)]
    [InlineData("MAIN2026", true)]
    [InlineData("w1", false)]
    [InlineData("W-1", false)]
    [InlineData("WAREHOUSE9", false)]
    public void 倉庫代碼_1到8碼大寫英數(string code, bool valid)
    {
        var create = () => new WarehouseSite(Guid.NewGuid(), code, "主倉", Now);
        if (valid)
        {
            Assert.Equal(code, create().Code);
        }
        else
        {
            Assert.Throws<DomainRuleException>(create);
        }
    }

    [Theory]
    [InlineData("W1-A-01-03-02", true)]
    [InlineData("A01", true)]
    [InlineData("W1-A-01-03-02-X", true)]     // 6 段
    [InlineData("W1-A-01-03-02-X-Y", false)]  // 7 段
    [InlineData("w1-a", false)]
    [InlineData("W1--A", false)]
    [InlineData("-W1", false)]
    public void 儲位碼_大寫英數以連字號分段最多6段(string code, bool valid)
    {
        var create = () => Location.Create(Guid.NewGuid(), Guid.NewGuid(), code, Empty, Now);
        if (valid)
        {
            Assert.Equal(LocationStatus.Available, create().Status);
        }
        else
        {
            Assert.Throws<DomainRuleException>(create);
        }
    }

    [Fact]
    public void 儲位屬性_承重不可為負_空白欄位存成null_狀態變更不可相同()
    {
        var location = Location.Create(Guid.NewGuid(), Guid.NewGuid(), "W1-A-01", Empty, Now);

        Assert.Throws<DomainRuleException>(() =>
            location.Update(Empty with { MaxLoadKg = -1 }, Now));
        Assert.Throws<DomainRuleException>(() => location.Update(Empty with { MaxLoadKg = 100_000_000_000m }, Now));
        Assert.Throws<DomainRuleException>(() => location.Update(Empty with { MaxLoadKg = 1.005m }, Now));
        location.Update(Empty with { Zone = " A ", Note = "  ", LocationType = LocationType.Cold, Hazardous = true }, Now);
        location.ChangeStatus(LocationStatus.Inspection, Now);

        Assert.Equal("A", location.Zone);
        Assert.Null(location.Note);
        Assert.Equal(LocationType.Cold, location.LocationType);
        Assert.Equal(LocationStatus.Inspection, location.Status);
        Assert.Throws<DomainRuleException>(() => location.ChangeStatus(LocationStatus.Inspection, Now));
    }

    [Theory]
    [InlineData("WH:LOC:W1:W1-A-01-03-02", ScanCodeKind.Location, "W1", "W1-A-01-03-02", null)]
    [InlineData("  wh:loc:w1:w1-a-01 \r\n", ScanCodeKind.Location, "W1", "W1-A-01", null)]   // 小寫、空白、換行（HID）
    [InlineData("ＷＨ：ＬＯＣ：Ｗ１：Ａ０１", ScanCodeKind.Location, "W1", "A01", null)]      // 全形
    [InlineData("WH:ITEM:EL-000125", ScanCodeKind.Item, null, null, "EL-000125")]
    [InlineData("el-000125", ScanCodeKind.Item, null, null, "EL-000125")]
    [InlineData("MR-000125", ScanCodeKind.Text, null, null, null)]                         // 申請單號不是料號
    [InlineData("WH:LOC:W1", ScanCodeKind.Text, null, null, null)]                         // 缺儲位碼
    [InlineData("伺服放大器", ScanCodeKind.Text, null, null, null)]
    [InlineData("", ScanCodeKind.Text, null, null, null)]
    public void 掃碼解析(string raw, ScanCodeKind kind, string? warehouse, string? location, string? item)
    {
        var scan = ScanCodeParser.Parse(raw);

        Assert.Equal((kind, warehouse, location, item), (scan.Kind, scan.WarehouseCode, scan.LocationCode, scan.ItemCode));
        Assert.Equal(raw, scan.Raw);
    }

    [Fact]
    public void QR內容可被解析回同一個儲位()
    {
        var scan = ScanCodeParser.Parse(Location.QrContent("W1", "W1-A-01-03-02"));

        Assert.Equal((ScanCodeKind.Location, "W1", "W1-A-01-03-02"), (scan.Kind, scan.WarehouseCode, scan.LocationCode));
    }
}
