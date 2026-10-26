using Warehouse.Domain;
using Warehouse.Domain.Items;

namespace Warehouse.UnitTests.Domain;

/// <summary>M1-01 物料主檔不變條件（T-M1-01-01～04）。資料庫另有對應 CHECK，見 IntegrationTests/ItemSchemaTests。</summary>
public class ItemTests
{
    private static readonly Guid CategoryId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static Item Submit(
        string name = "伺服放大器",
        TrackingMode trackingMode = TrackingMode.None,
        Guid? categoryId = null,
        Guid? unitId = null) =>
        Item.Submit(Guid.NewGuid(), "MR-000001", name, categoryId ?? CategoryId, unitId ?? UnitId, trackingMode,
            brand: " 三菱 ", manufacturerPartNo: "", specification: null, Now);

    // T-M1-01-01
    [Fact]
    public void 提出物料_為待核准且沒有正式料號_選填欄位去空白()
    {
        var item = Submit();

        Assert.Equal(ItemStatus.PendingApproval, item.Status);
        Assert.Null(item.ItemCode);
        Assert.Equal("MR-000001", item.RequestNo);
        Assert.Equal("三菱", item.Brand);
        Assert.Null(item.ManufacturerPartNo);
        Assert.False(item.AllowNegativeStock);
    }

    // T-M1-01-02
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 品名空白_被拒(string name)
    {
        var error = Assert.Throws<DomainRuleException>(() => Submit(name: name));
        Assert.Contains("品名", error.Message);
    }

    [Fact]
    public void 品名超過長度_被拒()
    {
        Assert.Throws<DomainRuleException>(() => Submit(name: new string('料', Item.NameMaxLength + 1)));
    }

    [Fact]
    public void 未指定分類或單位_被拒()
    {
        Assert.Throws<DomainRuleException>(() => Submit(categoryId: Guid.Empty));
        Assert.Throws<DomainRuleException>(() => Submit(unitId: Guid.Empty));
    }

    // T-M1-01-03
    [Theory]
    [InlineData(TrackingMode.Lot)]
    [InlineData(TrackingMode.Serial)]
    public void 批號或序號物料_不可允許負庫存(TrackingMode trackingMode)
    {
        var item = Submit(trackingMode: trackingMode);

        Assert.Throws<DomainRuleException>(() => item.SetNegativeStockPolicy(true, 10, Now));
        Assert.False(item.AllowNegativeStock);
    }

    // T-M1-01-04
    [Fact]
    public void 負庫存下限小於0_被拒_關閉時下限歸零()
    {
        var item = Submit();

        Assert.Throws<DomainRuleException>(() => item.SetNegativeStockPolicy(true, -1, Now));

        item.SetNegativeStockPolicy(true, 50, Now);
        Assert.Equal(50, item.NegativeStockLimit);
        item.SetNegativeStockPolicy(false, 50, Now);
        Assert.Equal(0, item.NegativeStockLimit);
    }

    [Fact]
    public void 頂層分類沒有料號前綴_被拒_子分類可沿用上層()
    {
        Assert.Throws<DomainRuleException>(() => new ItemCategory(Guid.NewGuid(), null, "EL", "電控", null));

        var child = new ItemCategory(Guid.NewGuid(), Guid.NewGuid(), "EL-SERVO", "伺服", null);
        Assert.Null(child.ItemCodePrefix);
    }

    [Fact]
    public void 內部識別碼為空_被拒()
    {
        Assert.Throws<DomainRuleException>(() =>
            Item.Submit(Guid.Empty, "MR-000001", "品名", CategoryId, UnitId, TrackingMode.None, null, null, null, Now));
    }

    [Fact]
    public void 傳入當地時間_一律存成UTC且為同一時間點()
    {
        var taipei = new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.FromHours(8));

        var item = Item.Submit(Guid.NewGuid(), "MR-000001", "品名", CategoryId, UnitId, TrackingMode.None, null, null, null, taipei);

        Assert.Equal(TimeSpan.Zero, item.CreatedAt.Offset);
        Assert.Equal(taipei, item.CreatedAt);
    }

    [Fact]
    public void 分類指向自己_被拒()
    {
        var id = Guid.NewGuid();
        Assert.Throws<DomainRuleException>(() => new ItemCategory(id, id, "EL-SELF", "自己", null));
    }

    [Theory]
    [InlineData("pcs", 0)]
    [InlineData("PCS", 5)]
    public void 單位代碼須大寫_小數位數0到4(string code, int decimalPlaces)
    {
        Assert.Throws<DomainRuleException>(() => new Unit(Guid.NewGuid(), code, "個", decimalPlaces));
    }

    // ---- M8-05：核准後改分類／單位／追蹤模式（ADR-0023） ----

    private static Item Approved(TrackingMode trackingMode = TrackingMode.None)
    {
        var item = Submit(trackingMode: trackingMode);
        item.Approve("EL-000001", Now);
        return item;
    }

    private static void Update(Item item, Guid? categoryId = null, Guid? unitId = null, TrackingMode? trackingMode = null,
        string name = "伺服放大器", bool checkedFlag = false) =>
        item.UpdateDetails(name, categoryId ?? item.CategoryId, unitId ?? item.BaseUnitId, trackingMode ?? item.TrackingMode,
            null, null, null, Now, checkedFlag);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 核准後改分類_不論旗標為何都被擋(bool unitTrackingChangeChecked)
    {
        var item = Approved();
        var original = item.CategoryId;

        var error = Assert.Throws<DomainRuleException>(() => Update(item, categoryId: Guid.NewGuid(), checkedFlag: unitTrackingChangeChecked));

        Assert.Contains("分類", error.Message);
        Assert.Equal(original, item.CategoryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 停用後改分類_同樣被擋(bool unitTrackingChangeChecked)
    {
        var item = Approved();
        item.Deactivate(Now);

        Assert.Throws<DomainRuleException>(() => Update(item, categoryId: Guid.NewGuid(), checkedFlag: unitTrackingChangeChecked));
    }

    [Fact]
    public void 核准後改基本單位_旗標false被擋_旗標true成功()
    {
        var item = Approved();
        var original = item.BaseUnitId;
        var newUnit = Guid.NewGuid();

        Assert.Throws<DomainRuleException>(() => Update(item, unitId: newUnit, checkedFlag: false));
        Assert.Equal(original, item.BaseUnitId);

        Update(item, unitId: newUnit, checkedFlag: true);
        Assert.Equal(newUnit, item.BaseUnitId);
    }

    [Fact]
    public void 核准後改追蹤模式_旗標false被擋_旗標true成功()
    {
        var item = Approved();

        Assert.Throws<DomainRuleException>(() => Update(item, trackingMode: TrackingMode.Lot, checkedFlag: false));
        Assert.Equal(TrackingMode.None, item.TrackingMode);

        Update(item, trackingMode: TrackingMode.Lot, checkedFlag: true);
        Assert.Equal(TrackingMode.Lot, item.TrackingMode);
    }

    [Fact]
    public void 核准後只改品名_單位與追蹤模式沒變_不需要旗標()
    {
        var item = Approved();

        Update(item, name: "新品名", checkedFlag: false);

        Assert.Equal("新品名", item.Name);
    }

    [Fact]
    public void 核准前可自由修改分類_單位_追蹤模式_不需要旗標()
    {
        var pending = Submit();
        var category = Guid.NewGuid();
        var unit = Guid.NewGuid();
        Update(pending, categoryId: category, unitId: unit, trackingMode: TrackingMode.Serial, checkedFlag: false);
        Assert.Equal((category, unit, TrackingMode.Serial), (pending.CategoryId, pending.BaseUnitId, pending.TrackingMode));

        // 退回狀態同樣屬於核准前
        var returned = Submit();
        returned.Return(Now);
        var category2 = Guid.NewGuid();
        Update(returned, categoryId: category2, unitId: Guid.NewGuid(), trackingMode: TrackingMode.Lot, checkedFlag: false);
        Assert.Equal(category2, returned.CategoryId);
        Assert.Equal(TrackingMode.Lot, returned.TrackingMode);
    }
}
