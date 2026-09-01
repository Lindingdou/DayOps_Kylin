using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>文字高度归一化（逐实体离群修正，忠实原 TextHeightNormalizer）回归。</summary>
public class TextHeightNormalizerTests
{
    [Fact]
    public void Outlier_and_missing_replaced_with_typical_median()
    {
        // 图幅 1000×1000 → D≈1414; maxAllowed = D·0.05 ≈ 70.7。正常高度 {4,5,6}(中位 5)。
        var heights = new List<double> { 4, 5, 6, 591, 0 };   // 591 离群·0 缺失
        var n = new TextHeightNormalizer(1000, 1000, heights);
        Assert.True(n.IsActive);
        Assert.Equal(5.0, n.TypicalHeight, 6);          // 正常范围中位数
        Assert.Equal(5.0, n.Correct(591), 6);           // 离群 → typical
        Assert.Equal(5.0, n.Correct(0), 6);             // 缺失 → typical
        Assert.Equal(4.0, n.Correct(4), 6);             // 正常 → 原样
        Assert.Equal(6.0, n.Correct(6), 6);             // 正常 → 原样
        Assert.Equal(2, n.CorrectedCount);              // 591 与 0
    }

    [Fact]
    public void All_abnormal_falls_back_to_diagonal_fraction()
    {
        // 全部太大 → 无正常 → typical = D·fallbackFrac(0.005)。D=sqrt(2)·1000≈1414 → ≈7.07
        var n = new TextHeightNormalizer(1000, 1000, new List<double> { 5000, 6000 });
        double expect = System.Math.Sqrt(2) * 1000 * 0.005;
        Assert.Equal(expect, n.TypicalHeight, 6);
        Assert.Equal(expect, n.Correct(5000), 6);       // 离群 → 兜底
    }

    [Fact]
    public void No_geometry_keeps_positive_defaults_nonpositive()
    {
        var n = new TextHeightNormalizer(0, 0, new List<double> { 5 });
        Assert.False(n.IsActive);
        Assert.Equal(5.0, n.Correct(5), 6);             // 正数原样
        Assert.Equal(1.0, n.Correct(-3), 6);            // 非正 → 1.0
        Assert.Equal(1.0, n.Correct(0), 6);
    }
}
