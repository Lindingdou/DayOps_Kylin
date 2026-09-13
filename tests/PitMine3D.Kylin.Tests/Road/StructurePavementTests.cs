// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/StructurePavementTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>结构路面 v1 等宽外扩 ribbon 几何（需求07④）的纯逻辑测试。</summary>
public class StructurePavementTests
{
    [Fact]
    public void BuildRibbons_StraightLine_OffsetsHalfWidthBothSides()
    {
        // 沿 +X 的水平中线，宽 24 → 半宽 12；法向沿 ±Y。
        var line = new List<Point3d> { new(0, 0, 0), new(100, 0, 0) };
        var ribbons = StructurePavement.BuildRibbons(new[] { line }, 24.0);

        Assert.Single(ribbons);
        var rb = ribbons[0];
        // n=2 → (2n+1)=5 点闭合多边形
        Assert.Equal(5 * 3, rb.Length);

        int pts = rb.Length / 3;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < pts; i++)
        {
            double y = rb[3 * i + 1];
            double z = rb[3 * i + 2];
            Assert.Equal(12.0, Math.Abs(y), 3);   // 每个外扩点离中线 12m
            Assert.Equal(0.0, z, 3);               // Z 沿用中线
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        Assert.Equal(24.0, maxY - minY, 3);        // 全宽 24

        // 闭合：首点 == 末点
        Assert.Equal(rb[0], rb[3 * (pts - 1)], 3);
        Assert.Equal(rb[1], rb[3 * (pts - 1) + 1], 3);
    }

    [Fact]
    public void BuildRibbons_FlatOverload_Equivalent()
    {
        var flat = new double[] { 0, 0, 0, 100, 0, 0 };
        var ribbons = StructurePavement.BuildRibbons(new[] { flat }, 24.0);
        Assert.Single(ribbons);
        Assert.Equal(5 * 3, ribbons[0].Length);
    }

    [Fact]
    public void BuildRibbons_SkipsDegenerateLines()
    {
        var single = new List<Point3d> { new(0, 0, 0) };           // <2 点
        var coincident = new List<Point3d> { new(5, 5, 0), new(5, 5, 0) }; // 全退化（重合）
        var ribbons = StructurePavement.BuildRibbons(new[] { single, coincident }, 24.0);
        Assert.Empty(ribbons);
    }

    [Fact]
    public void BuildRibbons_NonPositiveWidth_Empty()
    {
        var line = new List<Point3d> { new(0, 0, 0), new(100, 0, 0) };
        Assert.Empty(StructurePavement.BuildRibbons(new[] { line }, 0.0));
    }
}
