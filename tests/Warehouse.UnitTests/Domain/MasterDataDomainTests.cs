using Warehouse.Domain;
using Warehouse.Domain.Items;

namespace Warehouse.UnitTests.Domain;

/// <summary>M1-03 分類與單位的修改規則（T-M1-03-01）。需要查資料庫的規則（循環、已發號前綴）見整合測試。</summary>
public class MasterDataDomainTests
{
    [Fact]
    public void 單位_使用中不可改小數位數_可改名稱()
    {
        var unit = new Unit(Guid.NewGuid(), "PAIR", "雙", 0);

        Assert.Throws<DomainRuleException>(() => unit.Update("雙", 2, isInUse: true));
        unit.Update("對", 0, isInUse: true);
        unit.Update("對", 2, isInUse: false);

        Assert.Equal("對", unit.Name);
        Assert.Equal(2, unit.DecimalPlaces);
    }

    [Fact]
    public void 單位_停用與啟用()
    {
        var unit = new Unit(Guid.NewGuid(), "PAIR", "雙", 0);

        unit.Deactivate();
        Assert.False(unit.IsActive);
        unit.Reactivate();
        Assert.True(unit.IsActive);
    }

    [Fact]
    public void 分類_修改時仍檢查指向自己_頂層必須有前綴_空白前綴視為沒有()
    {
        var id = Guid.NewGuid();
        var category = new ItemCategory(id, null, "EX", "測試", "EX");

        Assert.Throws<DomainRuleException>(() => category.Update(id, "測試", null, 0));
        Assert.Throws<DomainRuleException>(() => category.Update(null, "測試", "  ", 0));
        category.Update(Guid.NewGuid(), "子分類", " ", 5);

        Assert.Null(category.ItemCodePrefix);
        Assert.Equal(5, category.SortOrder);
        Assert.Equal("EX", category.Code); // 代碼不變
    }
}
