using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 侧面三角网放样回归：顶底两线起点错位 / 疏密悬殊 / 一侧绕行时不得缝出横跨拐角的扇形三角。
/// 判据：顶底 XY 同路径的立面, 真侧壁面积 = 周长×高, 严格已知；跨角扇形三角必然把面积撑大。
/// 另查：闭环侧壁是环带 —— 开放边恰为顶环+底环两圈(条数 = 顶点数), 无非流形边, 三角数 = 顶点数之和。
/// </summary>
public class SideSurfaceTests
{
    private static double Area(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        double s = 0;
        foreach (var (a, b, c) in t)
        {
            var p = v[a]; var q = v[b]; var r = v[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            s += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }
        return s;
    }

    private static (int boundary, int nonManifold) Edges(IReadOnlyList<(int a, int b, int c)> tris)
    {
        var count = new Dictionary<(int, int), int>();
        void Bump(int u, int w) { var k = u < w ? (u, w) : (w, u); count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var (a, b, c) in tris) { Bump(a, b); Bump(b, c); Bump(c, a); }
        int b1 = 0, b3 = 0; foreach (var kv in count) { if (kv.Value == 1) b1++; else if (kv.Value > 2) b3++; }
        return (b1, b3);
    }

    // 环带：开放边恰为顶环+底环两圈, 无非流形边
    private static void AssertRingBand(int vertCount, IReadOnlyList<(int a, int b, int c)> t)
    {
        var (b, nm) = Edges(t);
        Assert.Equal(vertCount, b);
        Assert.Equal(0, nm);
    }

    /// <summary>沿 w×h 矩形周界从弧长 start 起、按 step 采样一圈(含 4 角, 首点在 start), 高程 z。</summary>
    private static List<(double x, double y, double z)> RectRing(double w, double h, double step, double start, double z)
    {
        double per = 2 * (w + h);
        var stations = new SortedSet<double> { 0, w, w + h, 2 * w + h };
        for (double s = start; s < start + per - 1e-9; s += step) stations.Add(Math.Round(s % per, 9));
        var ordered = new List<double>();
        foreach (var s in stations) if (s >= start - 1e-9) ordered.Add(s);
        foreach (var s in stations) if (s < start - 1e-9) ordered.Add(s);
        var res = new List<(double x, double y, double z)>(ordered.Count);
        foreach (var t in ordered)
        {
            if (t < w) res.Add((t, 0, z));
            else if (t < w + h) res.Add((w, t - w, z));
            else if (t < 2 * w + h) res.Add((w - (t - w - h), h, z));
            else res.Add((0, h - (t - 2 * w - h), z));
        }
        return res;
    }

    [Fact]
    public void Closed_rings_with_offset_start_and_different_density_have_exact_wall_area()
    {
        // 顶环从角点起每 10m 一点; 底环从中段起每 7m 一点 —— 原弧长拉链在这里漂移, 拐角缝出扇形
        var top = RectRing(100, 60, 10, 0, 10);
        var bot = RectRing(100, 60, 7, 35, 0);
        var (v, t) = SideSurface.Loft(top, bot, closed: true, flip: false);
        Assert.NotEmpty(t);
        Assert.Equal(2 * (100 + 60) * 10.0, Area(v, t), 6);
        Assert.Equal(top.Count + bot.Count, v.Count);          // 闭环首尾不重顶点
        Assert.Equal(top.Count + bot.Count, t.Count);          // 环带三角数 = 顶点数之和
        AssertRingBand(v.Count, t);
    }

    [Fact]
    public void Open_lines_with_mismatched_density_have_exact_wall_area()
    {
        // 开线 L 形：顶线 3 点(只有角点), 底线密采样
        var top = new List<(double x, double y, double z)> { (0, 0, 5), (50, 0, 5), (50, 40, 5) };
        var bot = new List<(double x, double y, double z)>();
        for (double s = 0; s <= 90 + 1e-9; s += 2.5)
            bot.Add(s <= 50 ? (s, 0, 0) : (50, s - 50, 0));
        var (v, t) = SideSurface.Loft(top, bot, closed: false, flip: false);
        Assert.NotEmpty(t);
        Assert.Equal(90 * 5.0, Area(v, t), 6);
        Assert.Equal(top.Count + bot.Count - 2, t.Count);
    }

    [Fact]
    public void Open_flag_but_both_ends_coincide_is_treated_as_ring()
    {
        var top = RectRing(80, 50, 8, 0, 20); top.Add(top[0]);
        var bot = RectRing(80, 50, 6, 27, 0); bot.Add(bot[0]);
        var (v, t) = SideSurface.Loft(top, bot, closed: false, flip: false);
        Assert.NotEmpty(t);
        Assert.Equal(2 * (80 + 50) * 20.0, Area(v, t), 6);
        AssertRingBand(v.Count, t);
    }

    [Fact]
    public void Reversed_bottom_ring_is_reoriented()
    {
        var top = RectRing(100, 60, 10, 0, 10);
        var bot = RectRing(100, 60, 7, 35, 0); bot.Reverse();
        var (v, t) = SideSurface.Loft(top, bot, closed: true, flip: false);
        Assert.Equal(2 * (100 + 60) * 10.0, Area(v, t), 6);
    }

    [Fact]
    public void Inclined_face_with_detour_on_one_side_stays_local()
    {
        // 顶环外扩 5m 的倾斜面; 底环在一条边上多绕一个 10m 的凹口(顶环没有) —— 绕行只能局部扇到最近顶点
        var top = RectRing(110, 70, 10, 0, 10);
        for (int i = 0; i < top.Count; i++) top[i] = (top[i].x - 5, top[i].y - 5, top[i].z);
        var bot = new List<(double x, double y, double z)>();
        foreach (var p in RectRing(100, 60, 5, 0, 0))
        {
            if (p.y == 0 && p.x >= 45 && p.x <= 55) continue;
            bot.Add(p);
            if (p.y == 0 && Math.Abs(p.x - 40) < 1e-9) { bot.Add((45, 0, 0)); bot.Add((45, 10, 0)); bot.Add((55, 10, 0)); bot.Add((55, 0, 0)); }
        }
        var (v, t) = SideSurface.Loft(top, bot, closed: true, flip: false);
        Assert.NotEmpty(t);
        AssertRingBand(v.Count, t);
        // 任一三角的最长边不得超过「相邻采样间距 + 顶底偏移 + 凹口深度」量级 —— 跨角扇形会远超
        Assert.True(MaxEdge(v, t) < 25, $"最长边 {MaxEdge(v, t):0.##} 超界, 疑似跨角扇形");
    }

    [Fact]
    public void Inclined_face_with_larger_bottom_ring_is_exact_planar_panels()
    {
        // 顶环 100×60、底环外扩 8m 且起点错位/间距不同 —— 四个面各是平面梯形, 面积有闭式解;
        // 原弧长拉链在此漂移, 拐角缝出扇形(实测面积 4579 · 最长边 30.8); 最短横档 DP 应精确到平面梯形之和且最长边 = 角点横档
        var top = RectRing(100, 60, 10, 0, 10);
        var bot = RectRing(116, 76, 7, 23, 0);
        for (int i = 0; i < bot.Count; i++) bot[i] = (bot[i].x - 8, bot[i].y - 8, 0);
        var (v, t) = SideSurface.Loft(top, bot, closed: true, flip: false);
        double slant = Math.Sqrt(8 * 8 + 10 * 10);
        double ideal = 2 * 0.5 * (100 + 116) * slant + 2 * 0.5 * (60 + 76) * slant;
        Assert.Equal(ideal, Area(v, t), 6);
        AssertRingBand(v.Count, t);
        Assert.True(MaxEdge(v, t) <= Math.Sqrt(8 * 8 + 8 * 8 + 10 * 10) + 1e-9, $"最长边 {MaxEdge(v, t):0.##} 超过角点横档");
    }

    [Fact]
    public void Bottom_ring_with_deep_notch_does_not_drift_into_corner_fans()
    {
        // 底环一边多绕一个 10 宽 30 深的凹口(顶环没有) → 周长差 60m, 原弧长拉链整体漂移、四角全是扇形
        // (实测面积 4995 · 最长边 47.2)。最短横档 DP 只在凹口处局部扇到最近顶点, 最长边 = 顶点到凹口底的横档。
        var top = RectRing(100, 60, 10, 0, 10);
        var bot = new List<(double x, double y, double z)>();
        foreach (var p in RectRing(100, 60, 5, 0, 0))
        {
            if (p.y == 0 && p.x >= 45 && p.x <= 55) continue;
            bot.Add(p);
            if (p.y == 0 && Math.Abs(p.x - 40) < 1e-9) { bot.Add((45, 0, 0)); bot.Add((45, 30, 0)); bot.Add((55, 30, 0)); bot.Add((55, 0, 0)); }
        }
        var (v, t) = SideSurface.Loft(top, bot, closed: true, flip: false);
        AssertRingBand(v.Count, t);
        Assert.True(Area(v, t) < 3700, $"面积 {Area(v, t):0.#} 偏大, 疑似跨角扇形");
        Assert.True(MaxEdge(v, t) <= Math.Sqrt(5 * 5 + 30 * 30 + 10 * 10) + 1e-9, $"最长边 {MaxEdge(v, t):0.##} 超过凹口横档");
    }

    [Fact]
    public void Large_rings_take_banded_dp_and_still_exact()
    {
        // 6000×6000 > 回溯表预算 → 带状 DP; 立面面积仍须精确
        var top = RectRing(3000, 3000, 2.0, 0, 10);
        var bot = RectRing(3000, 3000, 2.0, 1.3, 0);
        Assert.True(top.Count > 5000 && bot.Count > 5000);
        var (v, t) = SideSurface.Loft(top, bot, closed: true, flip: false);
        Assert.NotEmpty(t);
        Assert.Equal(4 * 3000 * 10.0, Area(v, t), 3);
        AssertRingBand(v.Count, t);
    }

    [Fact]
    public void Parallel_strip_and_square_tube_keep_expected_triangle_counts()
    {
        // 3+3 点开线 → 4 三角; 4+4 点闭方环 → 8 三角(管)
        var top = new List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 10), (20, 0, 10) };
        var bot = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (20, 0, 0) };
        var (v, t) = SideSurface.Loft(top, bot, closed: false, flip: false);
        Assert.Equal(6, v.Count); Assert.Equal(4, t.Count);
        Assert.Equal(200.0, Area(v, t), 6);
        var rt = new List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10) };
        var rb = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        (v, t) = SideSurface.Loft(rt, rb, closed: true, flip: false);
        Assert.Equal(8, v.Count); Assert.Equal(8, t.Count);
        Assert.Equal(400.0, Area(v, t), 6);
        AssertRingBand(8, t);
    }

    [Fact]
    public void Degenerate_inputs_return_empty()
    {
        var one = new List<(double x, double y, double z)> { (0, 0, 0) };
        var two = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0) };
        Assert.Empty(SideSurface.Loft(one, two, false, false).tris);
        Assert.Empty(SideSurface.Loft(two, null!, false, false).tris);
        var dup = new List<(double x, double y, double z)> { (0, 0, 0), (0, 0, 0), (0, 0, 0) };
        Assert.Empty(SideSurface.Loft(dup, two, false, false).tris);
    }

    private static double MaxEdge(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        double e = 0;
        foreach (var (a, b, c) in t) e = Math.Max(e, Math.Max(Dist(v[a], v[b]), Math.Max(Dist(v[b], v[c]), Dist(v[c], v[a]))));
        return e;
    }

    private static double Dist((double x, double y, double z) a, (double x, double y, double z) b)
        => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
}
