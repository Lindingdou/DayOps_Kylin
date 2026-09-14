using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>ACI 全表 + 取色器色板 —— 逐值对照原版 Bindings/AcadColorTable.cs。</summary>
public class AcadColorTableTests
{
    [Theory]
    [InlineData(1, 255, 0, 0)]        // 红
    [InlineData(2, 255, 255, 0)]      // 黄
    [InlineData(3, 0, 255, 0)]        // 绿
    [InlineData(4, 0, 255, 255)]      // 青
    [InlineData(5, 0, 0, 255)]        // 蓝
    [InlineData(6, 255, 0, 255)]      // 品红
    [InlineData(7, 255, 255, 255)]    // 白
    [InlineData(8, 128, 128, 128)]    // 深灰
    [InlineData(9, 192, 192, 192)]    // 浅灰
    [InlineData(30, 255, 127, 0)]     // 橙(色板第二行)
    [InlineData(150, 0, 127, 255)]    // 天蓝
    [InlineData(254, 204, 204, 204)]  // 灰阶
    public void Standard_indices_match_autocad_palette(int aci, byte r, byte g, byte b)
        => Assert.Equal((r, g, b), AcadColorTable.Rgb255(aci));

    [Fact]
    public void Out_of_range_index_falls_back_to_white()
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), AcadColorTable.Rgb255(0));     // 0 = 随块占位, 不经本表
        Assert.Equal(((byte)255, (byte)255, (byte)255), AcadColorTable.Rgb255(256));
        Assert.Equal(((byte)255, (byte)255, (byte)255), AcadColorTable.Rgb255(-3));
    }

    [Fact]
    public void Swatch_grid_is_nine_by_three_and_all_indices_valid()
    {
        Assert.Equal(27, AcadColorTable.SwatchIndices.Length);                          // 9 列 × 3 行(同原版)
        Assert.Equal(0, AcadColorTable.SwatchIndices.Length % 9);
        Assert.All(AcadColorTable.SwatchIndices, i => Assert.InRange(i, 1, 255));
        Assert.Equal(AcadColorTable.SwatchIndices.Length, AcadColorTable.SwatchIndices.Distinct().Count());
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, AcadColorTable.SwatchIndices.Take(9));
    }

    [Fact]
    public void RgbF_is_the_same_color_scaled_to_unit_range()
    {
        var (r, g, b) = AcadColorTable.RgbF(5);
        Assert.Equal(0f, r);
        Assert.Equal(0f, g);
        Assert.Equal(1f, b);
    }

    [Fact]
    public void NearestIndex_hits_exact_colors_and_snaps_近似色()
    {
        Assert.Equal(1, AcadColorTable.NearestIndex(255, 0, 0));        // 精确命中取小号(1 与 10 同为纯红)
        Assert.Equal(1, AcadColorTable.NearestIndex(250, 6, 4));        // 近似 → 仍是红
        Assert.Equal(5, AcadColorTable.NearestIndex(0, 0, 255));
        Assert.Equal(254, AcadColorTable.NearestIndex(203, 205, 204));  // 灰阶
    }

    [Fact]
    public void NearestIndex_float_overload_clamps_out_of_range()
    {
        Assert.Equal(AcadColorTable.NearestIndex(255, 0, 0), AcadColorTable.NearestIndex(1.4f, -0.2f, 0f));
    }

    /// <summary>色板里 1-9 的取值必须与命令行色名表(AciPalette)一致 —— 两处对不上就会"选的色 ≠ 显示的名"。</summary>
    [Fact]
    public void Named_palette_agrees_with_aci_table_for_standard_nine()
    {
        foreach (var (aci, _, r, g, b) in AciPalette.Entries.Where(e => e.aci <= 9))
            Assert.Equal((r, g, b), AcadColorTable.Rgb255(aci));
    }
}
