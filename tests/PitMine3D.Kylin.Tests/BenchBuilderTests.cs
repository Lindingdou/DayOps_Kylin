using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 台阶几何生成（§三六三）：局部台阶 / 排土场放坡的托管几何核。
///
/// 三条头号判据：
///   ① <b>采场向下往内收、排土场向下往外放</b> —— 语义搞反了，坡面照样生成、照样好看，只是挖到境界外头去了。
///   ② <b>退化就停</b>：偏移到相邻边平行或环缩没了，停在上一级并记账，不硬凑一级歪掉的台阶。
///   ③ <b>排土场起算标高由求交定</b>：一个交点都没有 = 拦下，不替用户编一个标高。
/// </summary>
public class BenchBuilderTests
{
    private static List<(double x, double y)> Square(double s = 100) => new() { (0, 0), (s, 0), (s, s), (0, s) };   // CCW
    private static List<(double x, double y)> Open() => new() { (0, 0), (50, 0), (100, 0) };

    private static double Area(IReadOnlyList<(double x, double y)> p) => Math.Abs(BenchLines.SignedArea(p));

    // ── 内外语义 ────────────────────────────────────────────
    [Fact]
    public void 采场_向下往内收()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 2, downward: true, isDump: false);
        Assert.True(r.Ok, r.Error);
        Assert.True(Area(r.Levels[0].Toe) < Area(r.Levels[0].Crest));
        Assert.Equal(90, r.Levels[0].ToeZ, 6);
    }

    [Fact]
    public void 排土场_向下往外放()
    {
        // ★ 语义搞反，坡照样生成、照样好看，只是堆到境界里头去了
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 2, downward: true, isDump: true);
        Assert.True(r.Ok, r.Error);
        Assert.True(Area(r.Levels[0].Toe) > Area(r.Levels[0].Crest));
        Assert.Equal(90, r.Levels[0].ToeZ, 6);
    }

    [Fact]
    public void 采场_向上则往外()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 1, downward: false, isDump: false);
        Assert.True(Area(r.Levels[0].Toe) > Area(r.Levels[0].Crest));
        Assert.Equal(110, r.Levels[0].ToeZ, 6);
    }

    [Fact]
    public void 几何_坡脚偏移量等于H除以tanα()
    {
        // 45° ⇒ 水平投影 = H = 10；正方形每边内收 10
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 1, true, false);
        var toe = r.Levels[0].Toe;
        Assert.Equal(10, toe.Min(p => p.x), 6);
        Assert.Equal(90, toe.Max(p => p.x), 6);
    }

    [Fact]
    public void 几何_平盘宽再收一次得到下一级坡顶()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 2, true, false);
        Assert.Equal(2, r.Levels.Count);
        Assert.Equal(15, r.Levels[1].Crest.Min(p => p.x), 6);      // 10（坡面）+ 5（平盘）
        Assert.Equal(90, r.Levels[1].CrestZ, 6);
        Assert.True(r.BermTris.Count > 0);
    }

    [Fact]
    public void 几何_坡面网与平盘网三角数随点数()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 1, true, false);
        Assert.Equal(8, r.FaceTris.Count);                          // 闭合 4 边 × 2
        Assert.Empty(r.BermTris);                                   // 只 1 级没有平盘
    }

    // ── 退化就停 ────────────────────────────────────────────
    [Fact]
    public void 退化_环收缩没了就停在上一级并记账()
    {
        // 100 的方环、每级内收 15（10 坡面 + 5 平盘）：第 4 级坡脚(60)+平盘(70)… 到第 7 级环翻转/退化
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 20, true, false);
        Assert.True(r.Ok);
        Assert.True(r.Levels.Count < 20);
        Assert.Contains(r.Notes, n => n.Contains("停"));
    }

    [Fact]
    public void 退化_一级都生成不了时报错不给空结果()
    {
        var r = BenchBuilder.Build(Square(5), true, 100, 10, 45, 5, 1, true, false);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void 参数_非法值逐条报()
    {
        Assert.Contains("至少 3 点", BenchBuilder.Build(new List<(double, double)> { (0, 0), (1, 1) }, true, 0, 10, 45, 5, 1, true, false).Error);
        Assert.Contains("H 必须", BenchBuilder.Build(Square(), true, 0, 0, 45, 5, 1, true, false).Error);
        Assert.Contains("α", BenchBuilder.Build(Square(), true, 0, 10, 95, 5, 1, true, false).Error);
        Assert.Contains("W 不能为负", BenchBuilder.Build(Square(), true, 0, 10, 45, -1, 1, true, false).Error);
    }

    // ── 开口线 / 楔形 / 到标高 ──────────────────────────────
    [Fact]
    public void 开口线_按侧偏移且端点不斜切()
    {
        var off = BenchBuilder.OffsetOpen(Open(), 10, +1)!;
        Assert.Equal(3, off.Count);
        Assert.All(off, p => Assert.Equal(10, p.y, 6));          // 左侧 = +y
        var offR = BenchBuilder.OffsetOpen(Open(), 10, -1)!;
        Assert.All(offR, p => Assert.Equal(-10, p.y, 6));
    }

    [Fact]
    public void 开口线_拐角取相邻偏移边交点()
    {
        var l = new List<(double x, double y)> { (0, 0), (100, 0), (100, 100) };
        var off = BenchBuilder.OffsetOpen(l, 10, +1)!;
        Assert.Equal((90, 10), (Math.Round(off[1].x, 6), Math.Round(off[1].y, 6)));   // 内角交点
    }

    [Fact]
    public void 局部台阶_开口线一级成坡面加平盘线()
    {
        var r = BenchBuilder.Build(Open(), false, 100, 10, 45, 5, 1, true, false, side: 1);
        Assert.True(r.Ok, r.Error);
        Assert.Single(r.Levels);
        Assert.Equal(4, r.FaceTris.Count);                          // 开口 2 段 × 2
        Assert.All(r.Levels[0].Toe, p => Assert.Equal(10, p.y, 6));  // 开口线没有内外可言：side=+1 就是行进左侧
    }

    [Fact]
    public void 楔形_坡脚标高沿线从H收到零()
    {
        var r = BenchBuilder.Build(Open(), false, 100, 10, 45, 5, 1, true, false, taper: true);
        Assert.True(r.Ok);
        // 坡面网的下线顶点：起点 z=90、末端回到 100
        var lower = Enumerable.Range(0, r.FaceVerts.Count).Where(i => i % 2 == 1).Select(i => r.FaceVerts[i]).ToList();
        Assert.Equal(90, lower.First().z, 6);
        Assert.Equal(100, lower.Last().z, 6);
    }

    [Fact]
    public void 到标高_总高差截到指定值末级可能不满()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 5, true, false, stopZ: 75);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(25, r.TotalDropM, 6);
        Assert.Equal(5, r.Levels.Last().HeightM, 6);                // 10 + 10 + 5
        Assert.Equal(75, r.Levels.Last().ToeZ, 6);
    }

    [Fact]
    public void 到标高_与基线同高时报错()
        => Assert.Contains("没有高差", BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 5, true, false, stopZ: 100).Error);

    // ── 排土场推到现状面 ────────────────────────────────────
    private static IRoadZSampler Ground(double z)
    {
        var v = new double[] { -500, -500, z, 500, -500, z, 500, 500, z, -500, 500, z };
        return MeshZSampler.Build(v, new[] { 0, 1, 2, 0, 2, 3 })!;
    }

    [Fact]
    public void 排土场_推到现状面为止末级截到面()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 60, true, true, stopAtGround: Ground(76));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(76, r.Levels.Last().ToeZ, 6);
        Assert.Equal(24, r.TotalDropM, 6);
        Assert.Contains(r.Notes, n => n.Contains("落到现状面"));
    }

    [Fact]
    public void 排土场_坡顶已在面上时一级不放()
    {
        var r = BenchBuilder.Build(Square(), true, 100, 10, 45, 5, 60, true, true, stopAtGround: Ground(100));
        Assert.False(r.Ok);
    }

    // ── 起算标高由求交定 ────────────────────────────────────
    [Fact]
    public void 起算标高_取相交坡脚线标高的最大值()
    {
        var drawn = Square(100);
        var existing = new List<(IReadOnlyList<(double x, double y)> pts, double z)>
        {
            (new List<(double x, double y)> { (-10, 50), (110, 50) }, 120),   // 穿过
            (new List<(double x, double y)> { (-10, 20), (110, 20) }, 135),   // 穿过、更高
            (new List<(double x, double y)> { (200, 0), (300, 0) }, 999),     // 不相交
        };
        var (ok, z, hits, why) = BenchBuilder.ResolveDumpCrestZ(drawn, existing);
        Assert.True(ok, why);
        Assert.Equal(135, z, 6);
        Assert.Equal(2, hits);
    }

    [Fact]
    public void 起算标高_一个交点都没有就拦下不编()
    {
        // ★ 编出来的坡照样生成、照样好看，只是整体错在没人会去查的地方
        var (ok, _, hits, why) = BenchBuilder.ResolveDumpCrestZ(Square(),
            new List<(IReadOnlyList<(double x, double y)> pts, double z)> { (new List<(double x, double y)> { (200, 0), (300, 0) }, 120) });
        Assert.False(ok);
        Assert.Equal(0, hits);
        Assert.Contains("不替你编一个", why);
    }
}
