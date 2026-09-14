using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 坑线落地（§三六二）：贴面 + 限坡 / 走廊放样 / 挖填方分账 / 平盘联络道求解。
///
/// 三条头号判据：
///   ① <b>限坡优先</b>：照抄地面就成了过山车，冲突时按 i_max 拉离地面并如实报出偏差；
///      首末两点在 i_max 下到不了时<b>不偷偷放宽坡度</b>，单独报出来。
///   ② <b>采不到不外推</b>：站点不在面上的按两侧线性过渡并计数，不当 0；边坡找不到落地点的站
///      不出边坡、不按上一站的高度外推。
///   ③ <b>挖填方分账不合并</b>：足迹里没有现状面的部分单列成 UncoveredM2，不摊进挖填方。
/// </summary>
public class RoadLandingTests
{
    /// <summary>一张 200×200 的斜面 z = a + b·x（两三角），或水平面。</summary>
    private static IRoadZSampler Plane(double a = 100, double b = 0, double size = 200)
    {
        var v = new double[] { 0, 0, a, size, 0, a + b * size, size, size, a + b * size, 0, size, a };
        var t = new[] { 0, 1, 2, 0, 2, 3 };
        return MeshZSampler.Build(v, t)!;
    }

    /// <summary>一张有台阶的面：x&lt;100 高 z=120，x&gt;110 低 z=100，中间斜坡。</summary>
    private static IRoadZSampler Bench()
    {
        var verts = new List<double>();
        var tris = new List<int>();
        double[] xs = { 0, 100, 110, 200 };
        double[] zs = { 120, 120, 100, 100 };
        for (int i = 0; i < xs.Length; i++)
        {
            verts.AddRange(new[] { xs[i], 0.0, zs[i] });
            verts.AddRange(new[] { xs[i], 200.0, zs[i] });
        }
        for (int i = 0; i + 1 < xs.Length; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = (i + 1) * 2, d = (i + 1) * 2 + 1;
            tris.AddRange(new[] { a, c, b, b, c, d });
        }
        return MeshZSampler.Build(verts.ToArray(), tris.ToArray())!;
    }

    private static double[] Line(params (double x, double y, double z)[] p)
    {
        var a = new double[p.Length * 3];
        for (int i = 0; i < p.Length; i++) { a[i * 3] = p[i].x; a[i * 3 + 1] = p[i].y; a[i * 3 + 2] = p[i].z; }
        return a;
    }

    // ── 贴面 + 限坡 ──────────────────────────────────────────
    [Fact]
    public void 贴面_水平面上的线贴上去就是那个高程()
    {
        var r = RoadSurfaceProfiler.Fit(Line((10, 100, 0), (190, 100, 0)), Plane(a: 100), 8);
        Assert.True(r.Ok, r.Error);
        Assert.All(Enumerable.Range(0, r.Centerline.Length / 3), k => Assert.Equal(100, r.Centerline[k * 3 + 2], 6));
        Assert.Equal(0, r.MaxGradePct, 6);
        Assert.Equal(0, r.Missed);
    }

    [Fact]
    public void 贴面_首末可达时逐段纵坡不超过限坡()
    {
        // 斜面 5% < i_max 8% ⇒ 贴得上，逐段 ≤ 8%
        var r = RoadSurfaceProfiler.Fit(Line((10, 100, 0), (190, 100, 0)), Plane(a: 100, b: 0.05), 8);
        Assert.True(r.Ok, r.Error);
        Assert.True(r.EndpointsFeasible);
        Assert.True(r.MaxGradePct <= 8 + 1e-6, $"实达 {r.MaxGradePct}");
    }

    [Fact]
    public void 贴面_首末不可达时不偷偷放宽而是点名超坡集中在端点()
    {
        // ★ 斜面 20% > i_max 8%：首末锚死 + Δz > i·S ⇒ 至少一段必然超坡 —— 这不是算法错，是选点错
        var r = RoadSurfaceProfiler.Fit(Line((10, 100, 0), (190, 100, 0)), Plane(a: 100, b: 0.2), 8);
        Assert.True(r.Ok, r.Error);
        Assert.False(r.EndpointsFeasible);              // 首末高差 36m / 180m = 20% > 8%
        Assert.True(r.MaxGradePct > 8);                 // 如实报超坡，不悄悄把首末放开
        Assert.Equal(100 + 0.2 * 10, r.Centerline[2], 6);      // 首末仍锚在拾取标高
        Assert.Equal(100 + 0.2 * 190, r.Centerline[^1], 6);
        Assert.Contains(r.Diagnostics, d => d.StartsWith("⚠") && d.Contains("再怎么绕都不可能"));
        Assert.Contains(r.Diagnostics, d => d.Contains("超坡必然集中在端点附近"));
    }

    [Fact]
    public void 贴面_限坡把路拉离地面时如实报偏差()
    {
        var r = RoadSurfaceProfiler.Fit(Line((10, 100, 0), (190, 100, 0)), Plane(a: 100, b: 0.2), 8);
        Assert.True(r.LimitedStations > 0);
        Assert.True(Math.Abs(r.MaxDeviationM) > 1);
        Assert.Contains(r.Diagnostics, d => d.Contains("被拉离地面"));
    }

    [Fact]
    public void 贴面_首末锚在拾取标高()
    {
        var r = RoadSurfaceProfiler.Fit(Line((10, 100, 0), (190, 100, 0)), Plane(a: 100, b: 0.05), 8);
        Assert.True(r.Ok);
        Assert.Equal(100 + 0.05 * 10, r.Centerline[2], 6);
        Assert.Equal(100 + 0.05 * 190, r.Centerline[^1], 6);
    }

    [Fact]
    public void 贴面_采不到的站按两侧过渡并计数不当零()
    {
        // 线从面内伸到面外（x>200 采不到），再回到面内
        var r = RoadSurfaceProfiler.Fit(Line((150, 100, 0), (230, 100, 0), (150, 150, 0)), Plane(a: 100), 8);
        Assert.True(r.Ok, r.Error);
        Assert.True(r.Missed > 0);
        Assert.All(Enumerable.Range(0, r.Centerline.Length / 3), k => Assert.Equal(100, r.Centerline[k * 3 + 2], 3));
        Assert.Contains(r.Diagnostics, d => d.Contains("不外推、不当 0"));
    }

    [Fact]
    public void 贴面_基本不在面上时报错不硬贴()
    {
        var r = RoadSurfaceProfiler.Fit(Line((500, 500, 0), (600, 500, 0)), Plane(), 8);
        Assert.False(r.Ok);
        Assert.Contains("基本不在这张面的范围内", r.Error);
    }

    [Fact]
    public void 贴面_没有面或点不足时报错()
    {
        Assert.Contains("未指定面", RoadSurfaceProfiler.Fit(Line((0, 0, 0), (1, 1, 1)), null, 8).Error);
        Assert.Contains("不足 2", RoadSurfaceProfiler.Fit(Line((0, 0, 0)), Plane(), 8).Error);
    }

    [Fact]
    public void 贴面_站距加密后站点数随长度增长()
    {
        var r = RoadSurfaceProfiler.Fit(Line((10, 100, 0), (190, 100, 0)), Plane(), 8);
        Assert.True(r.Stations >= 180 / RoadSurfaceProfiler.StationStepM);
    }

    // ── 走廊放样 ─────────────────────────────────────────────
    [Fact]
    public void 放样_路面宽度等于路宽且三角数随站数()
    {
        var center = Line((10, 100, 100), (100, 100, 100), (190, 100, 100));
        var c = RoadCorridorLofter.Loft(center, 6, null, null, null, 45, 33.7);
        Assert.True(c.Ok, c.Error);
        Assert.Equal(3, c.Stations);
        Assert.Equal(4, c.DeckTris.Count);                 // (站数−1)×2
        double w = Math.Sqrt(Math.Pow(c.LeftEdge[1].x - c.RightEdge[1].x, 2) + Math.Pow(c.LeftEdge[1].y - c.RightEdge[1].y, 2));
        Assert.Equal(6, w, 6);
    }

    [Fact]
    public void 放样_没有面时只出路面不出边坡()
    {
        var c = RoadCorridorLofter.Loft(Line((10, 100, 100), (190, 100, 100)), 6, null, null, null, 45, 33.7);
        Assert.True(c.Ok);
        Assert.Empty(c.CutTris);
        Assert.Empty(c.FillTris);
    }

    [Fact]
    public void 放样_路在地面之下出挖方坡之上出填方坡()
    {
        // 地面 z=100 的水平面：路 z=95 ⇒ 挖；路 z=105 ⇒ 填
        var cut = RoadCorridorLofter.Loft(Line((20, 100, 95), (180, 100, 95)), 6, null, null, Plane(a: 100), 45, 33.7);
        Assert.True(cut.CutTris.Count > 0);
        Assert.Empty(cut.FillTris);
        Assert.Equal(5, cut.MaxCutHeightM, 1);
        var fill = RoadCorridorLofter.Loft(Line((20, 100, 105), (180, 100, 105)), 6, null, null, Plane(a: 100), 45, 33.7);
        Assert.True(fill.FillTris.Count > 0);
        Assert.Empty(fill.CutTris);
        Assert.Equal(5, fill.MaxFillHeightM, 1);
    }

    [Fact]
    public void 放样_边坡坡脚按坡角落在地面上()
    {
        // 挖 5m、坡角 45° ⇒ 坡顶水平距 5m
        var c = RoadCorridorLofter.Loft(Line((20, 100, 95), (180, 100, 95)), 6, null, null, Plane(a: 100), 45, 33.7);
        var top = c.CutVerts[2];                           // 第一片四边形的第一个落地点
        double dx = Math.Abs(top.y - c.LeftEdge[0].y);
        Assert.Equal(5, dx, 1);
        Assert.Equal(100, top.z, 1);
    }

    [Fact]
    public void 放样_边坡找不到落地点的站不出边坡不外推()
    {
        // 路在面外（y=250 采不到）⇒ 全部站找不到 ⇒ 没有边坡、计数等于站数×2
        var c = RoadCorridorLofter.Loft(Line((20, 250, 95), (180, 250, 95)), 6, null, null, Plane(a: 100), 45, 33.7);
        Assert.True(c.Ok);
        Assert.Empty(c.CutTris);
        Assert.Equal(c.Stations * 2, c.SlopeMissed);
    }

    [Fact]
    public void 放样_逐站路宽生效()
    {
        var center = Line((10, 100, 100), (100, 100, 100), (190, 100, 100));
        var c = RoadCorridorLofter.Loft(center, 6, new[] { 6.0, 9.0, 6.0 }, null, null, 45, 33.7);
        double w1 = Math.Sqrt(Math.Pow(c.LeftEdge[1].x - c.RightEdge[1].x, 2) + Math.Pow(c.LeftEdge[1].y - c.RightEdge[1].y, 2));
        Assert.Equal(9, w1, 6);
    }

    [Fact]
    public void 放样_参数非法时报错()
    {
        Assert.Contains("不足 2", RoadCorridorLofter.Loft(Line((0, 0, 0)), 6, null, null, null, 45, 33.7).Error);
        Assert.Contains("路宽", RoadCorridorLofter.Loft(Line((0, 0, 0), (1, 0, 0)), 0, null, null, null, 45, 33.7).Error);
    }

    // ── 挖填方分账 ───────────────────────────────────────────
    [Fact]
    public void 挖填方_路面低于地面全进挖方()
    {
        var c = RoadCorridorLofter.Loft(Line((20, 100, 95), (180, 100, 95)), 6, null, null, null, 45, 33.7);
        var cf = RoadCutFillCalculator.Compute(c.DeckFlat(), c.DeckTrisFlat(), Plane(a: 100));
        Assert.True(cf.Ok, cf.Error);
        Assert.True(cf.CutM3 > 0);
        Assert.Equal(0, cf.FillM3, 6);
        // 160m × 6m × 5m ≈ 4800 m³（格积分误差 ≤ 一格边）
        Assert.InRange(cf.CutM3, 4200, 5400);
    }

    [Fact]
    public void 挖填方_足迹不在面上的部分单列不摊进方量()
    {
        // 路一半在面外（y>200）
        var c = RoadCorridorLofter.Loft(Line((20, 199, 95), (180, 199, 95)), 6, null, null, null, 45, 33.7);
        var cf = RoadCutFillCalculator.Compute(c.DeckFlat(), c.DeckTrisFlat(), Plane(a: 100));
        Assert.True(cf.Ok);
        Assert.True(cf.UncoveredM2 > 0);
        Assert.Contains("未计量", cf.Describe());
    }

    [Fact]
    public void 挖填方_没有面时报错不给零()
    {
        var cf = RoadCutFillCalculator.Compute(new double[9], new[] { 0, 1, 2 }, null);
        Assert.False(cf.Ok);
        Assert.Contains("未指定面", cf.Error);
    }

    // ── 平盘联络道 ──────────────────────────────────────────
    [Fact]
    public void 联络道_在坡面上点一下解出台阶高差与展线长()
    {
        var r = BenchConnectorSolver.Solve(Bench(), 105, 100, 110, 8, false);
        Assert.True(r.Ok, r.Error);
        Assert.InRange(r.BenchHeightM, 9, 11);            // 从 z=110 下到下平盘 100
        Assert.Equal(r.BenchHeightM / 0.08, r.LengthM, 6);
        Assert.True(r.DownX > 0.9, "最陡方向应指向 +x（往低台阶）");
        Assert.True(r.Centerline.Length >= 6);
        Assert.Equal(110, r.Centerline[2], 6);
        Assert.Equal(110 - r.BenchHeightM, r.Centerline[^1], 6);
    }

    [Fact]
    public void 联络道_点在平盘上时如实报错不编台阶高()
    {
        var r = BenchConnectorSolver.Solve(Bench(), 50, 100, 120, 8, false);
        Assert.False(r.Ok);
        Assert.Contains("近乎水平", r.Error);
    }

    [Fact]
    public void 联络道_镜像取另一侧走向()
    {
        var a = BenchConnectorSolver.Solve(Bench(), 105, 100, 110, 8, false);
        var b = BenchConnectorSolver.Solve(Bench(), 105, 100, 110, 8, true);
        // 末点沿平盘走向（y）相反
        Assert.True((a.Centerline[^2] - 100) * (b.Centerline[^2] - 100) < 0);
    }

    [Fact]
    public void 联络道_没有面或纵坡非法时报错()
    {
        Assert.Contains("未指定面", BenchConnectorSolver.Solve(null, 0, 0, 0, 8, false).Error);
        Assert.Contains("纵坡", BenchConnectorSolver.Solve(Bench(), 105, 100, 110, 0, false).Error);
    }

    [Fact]
    public void 参数_缺省值同原版()
    {
        var p = new RampDesignParams();
        Assert.Equal(6.0, p.RoadWidth); Assert.Equal(8.0, p.MaxGradePct); Assert.Equal(15.0, p.MinTurnRadius);
        Assert.Equal(45.0, p.CutSlopeDeg); Assert.Equal(33.7, p.FillSlopeDeg); Assert.Equal(1.5, p.BermHeight);
    }
}
