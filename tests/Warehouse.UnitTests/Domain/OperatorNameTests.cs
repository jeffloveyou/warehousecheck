using Warehouse.Application.Abstractions;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.UnitTests.Domain;

/// <summary>公用帳號的人員名單與組合姓名（ADR-0030，T-SA-04）。</summary>
public class OperatorNameTests
{
    [Fact]
    public void 人員姓名_去空白_正規化_長度與必填()
    {
        var entry = OperatorName.Create(Guid.NewGuid(), "  Amy 王 ", 3);
        Assert.Equal("Amy 王", entry.Name);
        Assert.Equal("AMY 王", entry.NormalizedName);
        Assert.True(entry.IsActive);
        Assert.Throws<DomainRuleException>(() => OperatorName.Create(Guid.NewGuid(), " ", 0));
        Assert.Throws<DomainRuleException>(() => OperatorName.Create(Guid.NewGuid(), new string('名', OperatorName.NameMaxLength + 1), 0));
        Assert.Throws<DomainRuleException>(() => OperatorName.Create(Guid.Empty, "王", 0));
    }

    [Fact]
    public void 組合姓名_操作人加帳號名_超長時截短帳號名_身分不變()
    {
        var shared = new Actor(Guid.NewGuid(), "倉庫公用", false, IsWarehouseManager: true);
        var actor = shared.WithOperator(" 王小明 ");
        Assert.Equal("王小明（倉庫公用）", actor.Name);
        Assert.Equal(shared.Id, actor.Id);
        Assert.True(actor.IsWarehouseManager);

        var longAccount = shared with { Name = new string('公', 100) };
        var trimmed = longAccount.WithOperator(new string('名', OperatorName.NameMaxLength));
        Assert.Equal(Actor.NameMaxLength, trimmed.Name.Length);
        Assert.StartsWith(new string('名', OperatorName.NameMaxLength) + "（", trimmed.Name);
        Assert.EndsWith("）", trimmed.Name);
    }
}
