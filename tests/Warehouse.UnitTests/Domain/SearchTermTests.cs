using Warehouse.Application.Search;

namespace Warehouse.UnitTests.Domain;

/// <summary>M2-03 查詢拆詞規則（T-M2-03-10）：太短的英數不做部分比對、中文單字保留、最多 5 個詞。</summary>
public class SearchTermTests
{
    [Fact]
    public void 單一英數或符號略過_中文單字保留_重複去除()
    {
        var (used, ignored) = ItemSearch.SplitTerms("閥 A - 伺服 伺服 24V");

        Assert.Equal(["閥", "伺服", "24V"], used);
        Assert.Equal(["A", "-"], ignored);
    }

    [Fact]
    public void 超過5個詞_多的列為略過()
    {
        var (used, ignored) = ItemSearch.SplitTerms("AA BB CC DD EE FF GG");

        Assert.Equal(5, used.Count);
        Assert.Equal(["FF", "GG"], ignored);
    }
}
