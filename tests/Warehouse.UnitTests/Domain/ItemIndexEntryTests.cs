using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;

namespace Warehouse.UnitTests.Domain;

/// <summary>M2-01 專屬別名、共用關鍵字的規則（T-M2-01-01、02）。</summary>
public class ItemIndexEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("EL-000125", true)]
    [InlineData("el-000125", false)]   // 呼叫端先正規化（轉大寫）才判斷
    [InlineData("MR-000001", true)]
    [InlineData("MR-J4-40B", false)]
    [InlineData("EL-12", false)]
    public void 料號格式判斷(string normalized, bool expected)
    {
        Assert.Equal(expected, TextNormalizer.LooksLikeItemIdentifier(normalized));
    }

    [Fact]
    public void 建立別名_保留原文_正規化比對文字_記錄建立者()
    {
        var alias = ItemAlias.Create(Guid.NewGuid(), "  ｓｅｒｖｏ　Amp ", Guid.NewGuid(), "王小明", Now);

        Assert.Equal("ｓｅｒｖｏ　Amp", alias.Text);
        Assert.Equal("SERVO AMP", alias.NormalizedText);
        Assert.True(alias.IsActive);
        Assert.Equal("王小明", alias.CreatedByName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("el-000125")]   // 正規化後是料號格式
    [InlineData("ＭＲ－０００００１")] // 全形也一樣
    public void 空白或料號格式_被拒(string text)
    {
        Assert.Throws<DomainRuleException>(() => ItemKeyword.Create(Guid.NewGuid(), text, Guid.NewGuid(), "王小明", Now));
    }

    [Fact]
    public void 長度上限_別名100_關鍵字50()
    {
        ItemAlias.Create(Guid.NewGuid(), new string('伺', 100), Guid.NewGuid(), "王小明", Now);
        Assert.Throws<DomainRuleException>(() => ItemAlias.Create(Guid.NewGuid(), new string('伺', 101), Guid.NewGuid(), "王小明", Now));
        Assert.Throws<DomainRuleException>(() => ItemKeyword.Create(Guid.NewGuid(), new string('伺', 51), Guid.NewGuid(), "王小明", Now));
    }

    [Fact]
    public void 停用_記錄停用者_不能重複停用()
    {
        var keyword = ItemKeyword.Create(Guid.NewGuid(), "伺服AMP", Guid.NewGuid(), "王小明", Now);
        var actor = Guid.NewGuid();

        keyword.Deactivate(actor, "陳管理", Now);

        Assert.False(keyword.IsActive);
        Assert.Equal(actor, keyword.DeactivatedById);
        Assert.Equal("陳管理", keyword.DeactivatedByName);
        Assert.Throws<DomainRuleException>(() => keyword.Deactivate(actor, "陳管理", Now));
    }
}
