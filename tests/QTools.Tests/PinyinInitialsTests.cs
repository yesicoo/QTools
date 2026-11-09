using QTools.Host.Text;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 拼音首字母检索键：常用汉字取声母、ASCII 原样小写、生僻字跳过；
/// BuildSearchKey 组合原文 + 首字母，前端子串匹配即可同时命中中文/拼音/英文。
/// </summary>
public sealed class PinyinInitialsTests
{
    [Theory]
    [InlineData("计算器", "jsq")]
    [InlineData("待办清单", "dbqd")]
    [InlineData("二维码", "ewm")]
    [InlineData("记事本", "jsb")]
    [InlineData("Visual Studio Code", "visualstudiocode")]
    [InlineData("计算器Calc2024", "jsqcalc2024")]
    [InlineData("JSON 格式化", "jsongsh")]
    public void ExtractsInitialsForCommonChars(string input, string expected)
    {
        Assert.Equal(expected, PinyinInitials.Of(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("！@#￥%")]
    [InlineData("龘骉麤")]   // GB2312 之外的生僻字：不猜测，直接跳过
    public void DegradesToEmptyForNonSearchableInput(string? input)
    {
        Assert.Equal(string.Empty, PinyinInitials.Of(input));
    }

    [Fact]
    public void BuildSearchKeyCombinesRawAndInitials()
    {
        var key = PinyinInitials.BuildSearchKey("计算器", "calc-tool", "一个简单计算器");
        Assert.Contains("计算器", key);
        Assert.Contains("calc-tool", key);
        Assert.Contains("jsq", key);

        // 前端匹配语义：拼音首字母子串命中
        Assert.True(key.Contains("jsq", StringComparison.OrdinalIgnoreCase));
        Assert.True(key.Contains("calc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildSearchKeySkipsBlankParts()
    {
        var key = PinyinInitials.BuildSearchKey(null, "", "  ", "待办", "todo");
        Assert.Contains("待办", key);
        Assert.Contains("todo", key);
        Assert.Contains("db", key);
        Assert.DoesNotContain("  ", key);
    }
}