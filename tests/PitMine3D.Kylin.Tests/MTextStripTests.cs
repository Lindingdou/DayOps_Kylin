using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>MText 格式控制码剥离回归（移植自原 DwgDxfImportService.StripMTextFormatting）。</summary>
public class MTextStripTests
{
    [Theory]
    [InlineData(@"\H2.5;{\C1;ZK}01", "ZK01")]           // 字高+颜色码+分组括号 → 纯文字
    [InlineData(@"{\fSimSun|b0|i0|c134|p2;标高}", "标高")]  // 字体码(到 ;) + 分组
    [InlineData(@"ABC", "ABC")]                          // 无码原样
    [InlineData(@"", "")]                                // 空串
    public void Strips_format_codes(string input, string expected)
    {
        Assert.Equal(expected, DxfImportService.StripMTextFormatting(input));
    }

    [Fact]
    public void Keeps_legitimate_backslash_without_semicolon()
    {
        // "C:\Files" 无 ';' → 反斜杠当字面保留(不误剥)
        Assert.Equal(@"C:\Files", DxfImportService.StripMTextFormatting(@"C:\Files"));
    }

    [Fact]
    public void Paragraph_break_becomes_space()
    {
        Assert.Equal("A B", DxfImportService.StripMTextFormatting(@"A\PB"));
    }
}
