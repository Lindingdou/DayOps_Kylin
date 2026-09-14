using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>命名填充图案库（ANSI31/NET/砖墙… 按 acad.pat 语义重算）回归。</summary>
public class HatchPatternLibraryTests
{
    private static List<(double x, double y)> Square(double s) => new() { (0, 0), (s, 0), (s, s), (0, s) };

    private static List<(double x1, double y1, double x2, double y2)> Gen(string name, double scale, double angle = 0, double side = 10)
        => HatchPatternLibrary.Generate(HatchPatternLibrary.ByName(name)!, Square(side), scale, angle);

    [Fact]
    public void 图案表齐全且名字唯一()
    {
        Assert.True(HatchPatternLibrary.All.Length >= 20, "图案数量不应少于 20 种");
        var names = HatchPatternLibrary.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(HatchPatternLibrary.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Display)));
        Assert.NotNull(HatchPatternLibrary.ByName("ansi31"));      // 大小写不敏感
        Assert.Null(HatchPatternLibrary.ByName("没有这个图案"));
    }

    [Fact]
    public void 实心与用户定义不出线族()
    {
        Assert.Empty(Gen("SOLID", 1));    // 实心由调用方铺面, 不是线
        var user = HatchPatternLibrary.ByName("USER")!;
        Assert.True(user.IsUserDefined);
        Assert.Empty(user.Lines);
    }

    [Fact]
    public void ANSI31是单向45度斜线()
    {
        var lines = Gen("ANSI31", 8);     // 垂距 0.125×8 = 1
        Assert.NotEmpty(lines);
        foreach (var l in lines)
        {
            double dx = l.x2 - l.x1, dy = l.y2 - l.y1;
            Assert.True(Math.Abs(dx - dy) < 1e-6, "45° 斜线的 dx 应等于 dy");
        }
    }

    [Fact]
    public void ANSI37与NET是两组正交线()
    {
        foreach (var name in new[] { "ANSI37", "NET" })
        {
            var lines = Gen(name, 8);
            var dirs = lines.Select(l => Math.Round(Math.Atan2(l.y2 - l.y1, l.x2 - l.x1) * 180 / Math.PI / 90) * 90)
                            .Select(a => ((a % 180) + 180) % 180)
                            .Distinct().OrderBy(a => a).ToList();
            Assert.Equal(2, dirs.Count);                       // 两个方向
            Assert.Equal(90, Math.Abs(dirs[1] - dirs[0]), 6);  // 且正交
        }
    }

    [Fact]
    public void 比例越大线越疏()
    {
        int dense = Gen("ANSI31", 4).Count;
        int sparse = Gen("ANSI31", 16).Count;
        Assert.True(sparse < dense, $"比例大反而更密: {sparse} vs {dense}");
    }

    [Fact]
    public void 整体旋转把45度斜线转成竖线()
    {
        var lines = Gen("ANSI31", 8, angle: 45);   // 45+45 = 90°
        Assert.NotEmpty(lines);
        foreach (var l in lines)
            Assert.True(Math.Abs(l.x2 - l.x1) < 1e-6, "旋转 45° 后应是竖线");
    }

    [Fact]
    public void 短划图案切成断线而不是整条()
    {
        var solidRun = Gen("LINE", 8);       // 无短划: 每条线一段
        var dashed = Gen("ANSI33", 8);       // 第二族带短划
        Assert.NotEmpty(dashed);
        // 断线段应明显短于边界宽度(10)
        Assert.Contains(dashed, l => Math.Sqrt((l.x2 - l.x1) * (l.x2 - l.x1) + (l.y2 - l.y1) * (l.y2 - l.y1)) < 5);
        Assert.All(solidRun, l => Assert.True(Math.Abs(l.x2 - l.x1) > 9.0));   // 水平线整条贯通
    }

    [Fact]
    public void 线段全在边界内()
    {
        foreach (var p in HatchPatternLibrary.All)
        {
            if (p.IsSolid || p.IsUserDefined) continue;
            var lines = HatchPatternLibrary.Generate(p, Square(10), 4, 0);
            Assert.All(lines, l =>
            {
                Assert.InRange(l.x1, -1e-6, 10 + 1e-6);
                Assert.InRange(l.y1, -1e-6, 10 + 1e-6);
                Assert.InRange(l.x2, -1e-6, 10 + 1e-6);
                Assert.InRange(l.y2, -1e-6, 10 + 1e-6);
            });
        }
    }

    [Fact]
    public void 每种图案在常规比例下都出得来线()
    {
        foreach (var p in HatchPatternLibrary.All)
        {
            if (p.IsSolid || p.IsUserDefined) continue;
            var lines = HatchPatternLibrary.Generate(p, Square(10), 4, 0);
            Assert.True(lines.Count > 0, $"{p.Name} 没生成任何线");
        }
    }

    [Fact]
    public void 比例极小也不会卡死_有条数上限()
    {
        var lines = HatchPatternLibrary.Generate(HatchPatternLibrary.ByName("NET")!, Square(10), 1e-6, 0);
        Assert.True(lines.Count <= HatchPatternLibrary.MaxSegments + 8, $"没有截断: {lines.Count}");
    }

    [Fact]
    public void 凹边界也只填在内部()
    {
        // L 形(凹): 扫描线配对必须成对进出, 不能横跨缺口
        var lshape = new List<(double x, double y)> { (0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10) };
        var lines = HatchPatternLibrary.Generate(HatchPatternLibrary.ByName("LINE")!, lshape, 8, 0);
        Assert.NotEmpty(lines);
        foreach (var l in lines)
        {
            double y = l.y1, xmax = Math.Max(l.x1, l.x2);
            if (y > 4) Assert.True(xmax <= 4 + 1e-6, $"y={y} 处填到了缺口里 (x 到 {xmax})");
        }
    }
}
