using Warehouse.Domain.Text;

namespace Warehouse.UnitTests.Domain;

/// <summary>M1-02 重複候選比對用的正規化（T-M1-02-01）。</summary>
public class TextNormalizerTests
{
    [Theory]
    [InlineData("伺服放大器", "伺服放大器")]
    [InlineData("  伺服   放大器 ", "伺服 放大器")]
    [InlineData("ｓｅｒｖｏ　ａｍｐ", "SERVO AMP")]   // 全形英數與全形空白
    [InlineData("Servo\tAmp\n", "SERVO AMP")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void 一般文字_NFKC_大寫_空白整理(string? input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("MR-J4-10A", "MRJ410A")]
    [InlineData("mr j4 10a", "MRJ410A")]
    [InlineData("ＭＲ／Ｊ４．１０Ａ", "MRJ410A")]
    [InlineData("6ES7_214-1AG40", "6ES72141AG40")]
    [InlineData(" - ", null)]
    [InlineData(null, null)]
    public void 型號_另外去掉分隔符號(string? input, string? expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizePartNo(input));
    }
}
