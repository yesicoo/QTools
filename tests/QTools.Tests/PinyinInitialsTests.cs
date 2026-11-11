using QTools.Host.Text;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 拼音检索键：常用汉字取首字母与全拼、ASCII 原样小写、生僻字跳过；
/// BuildSearchKey 组合原文 + 首字母 + 全拼，前端子串匹配即可同时命中中文/拼音/英文。
/// </summary>
public sealed class PinyinInitialsTests
{
    [Theory]
    [InlineData("计算器", "jsq")]
    [InlineData("待办清单", "dbqd")]
    [InlineData("二维码", "ewm")]
    [InlineData("记事本", "jsb")]
    [InlineData("图片压缩", "tpys")]
    [InlineData("追溯码提取", "zsmtq")]
    [InlineData("Visual Studio Code", "visualstudiocode")]
    [InlineData("计算器Calc2024", "jsqcalc2024")]
    [InlineData("JSON 格式化", "jsongsh")]
    public void ExtractsInitialsForCommonChars(string input, string expected)
    {
        Assert.Equal(expected, PinyinInitials.Of(input));
    }

    [Theory]
    [InlineData("计算器", "jisuanqi")]
    [InlineData("图片压缩", "tupianyasuo")]
    [InlineData("追溯码提取", "zhuisumatiqu")]
    public void ExtractsCompactFullPinyin(string input, string expected)
    {
        Assert.Equal(expected, PinyinInitials.FullOf(input));
    }

    [Theory]
    [InlineData("图片压缩", "tu pian ya suo")]
    [InlineData("JSON 格式化", "json ge shi hua")]
    [InlineData("待办清单", "dai ban qing dan")]
    public void ExtractsSpacedFullPinyin(string input, string expected)
    {
        Assert.Equal(expected, PinyinInitials.FullSpacedOf(input));
    }

    [Fact]
    public void KeepsAllReadingsForPolyphonicChars()
    {
        // 主读音进音节全拼；次读音通过「紧凑全拼变体」覆盖
        Assert.Equal("zhong qing", PinyinInitials.FullSpacedOf("重庆"));

        var key = PinyinInitials.BuildSearchKey("重庆");
        Assert.Contains("zhongqing", key);
        Assert.Contains("chongqing", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("！@#￥%")]
    [InlineData("龘骉麤")]   // 静态表之外的生僻字：不猜测，直接跳过
    public void DegradesToEmptyForNonSearchableInput(string? input)
    {
        Assert.Equal(string.Empty, PinyinInitials.Of(input));
        Assert.Equal(string.Empty, PinyinInitials.FullOf(input));
        Assert.Equal(string.Empty, PinyinInitials.FullSpacedOf(input));
    }

    [Fact]
    public void BuildSearchKeyCombinesRawInitialsAndFullPinyin()
    {
        var key = PinyinInitials.BuildSearchKey("计算器", "calc-tool", "一个简单计算器");
        Assert.Contains("计算器", key);
        Assert.Contains("calc-tool", key);

        // 前端匹配语义：首字母 / 紧凑全拼 / 音节全拼都能子串命中
        Assert.Contains("jsq", key);
        Assert.Contains("jisuanqi", key);
        Assert.Contains("ji suan qi", key);
        Assert.Contains("calc", key);
        Assert.Contains("yigejiandan", key);
    }

    [Fact]
    public void BuildSearchKeySupportsManualAndWebToolNames()
    {
        var imported = PinyinInitials.BuildSearchKey("追溯码提取", "imported-e50d0cc6", "提取Code 和 PCode");
        Assert.True(imported.Contains("zsmtq", StringComparison.OrdinalIgnoreCase));
        Assert.True(imported.Contains("zhuisumatiqu", StringComparison.OrdinalIgnoreCase));
        Assert.True(imported.Contains("zhui su ma ti qu", StringComparison.OrdinalIgnoreCase));

        var web = PinyinInitials.BuildSearchKey("图片压缩", "web-c25eba07", null, "https://squoosh.app/");
        Assert.True(web.Contains("tpys", StringComparison.OrdinalIgnoreCase));
        Assert.True(web.Contains("tupianyasuo", StringComparison.OrdinalIgnoreCase));
        Assert.True(web.Contains("squoosh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildSearchKeySkipsBlankParts()
    {
        var key = PinyinInitials.BuildSearchKey(null, "", "  ", "待办", "todo");
        Assert.Contains("待办", key);
        Assert.Contains("todo", key);
        Assert.Contains("db", key);
        Assert.Contains("daiban", key);
        Assert.DoesNotContain("  ", key);
    }
}
