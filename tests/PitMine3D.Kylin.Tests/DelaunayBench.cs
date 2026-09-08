using System;
using System.Collections.Generic;
using System.Diagnostics;
using PitMine3D.Kylin.Cad;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 三角剖分的性能护栏 —— 等值线动辄几万顶点，旧的 Bowyer-Watson 是 O(n²)，
/// 在这个量级上界面直接卡死（用户实测「建网出不来、程序卡死」）。
/// 这里的时限放得很宽（比实测慢一个数量级也过得去），目的只有一个：
/// 谁要是把 O(n log n) 的实现换回逐点扫全表的写法，这几条会立刻红。
/// </summary>
public class DelaunayBench
{
    private readonly ITestOutputHelper _o;
    public DelaunayBench(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void Unconstrained_100k_points_staysUnderSeconds()
    {
        const int n = 100_000;
        var rnd = new Random(1);
        var pts = new List<(double x, double y)>(n);
        for (int i = 0; i < n; i++) pts.Add((rnd.NextDouble() * 5000, rnd.NextDouble() * 5000));

        var sw = Stopwatch.StartNew();
        var tris = Delaunay.Triangulate(pts);
        sw.Stop();
        _o.WriteLine($"无约束 {n} 点: {sw.ElapsedMilliseconds} ms, {tris.Count} 三角");

        // 三角数 ≈ 2n（欧拉公式，凸包上的点少一些）
        Assert.InRange(tris.Count, (int)(1.9 * n), 2 * n);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"10 万点剖分用了 {sw.ElapsedMilliseconds} ms —— 实测约 60 ms, 超这么多说明退回了平方级实现");
    }

    [Fact]
    public void Constrained_contourLike_30k_staysUnderSeconds()
    {
        // 模拟等值线：150 条各 200 点的折线，逐段作约束
        var verts = new List<(double x, double y)>();
        var cons = new List<(int u, int v)>();
        for (int l = 0; l < 150; l++)
        {
            int start = verts.Count;
            for (int i = 0; i < 200; i++) verts.Add((i * 5.0, l * 25.0 + Math.Sin(i * 0.3 + l) * 6));
            for (int i = 0; i + 1 < 200; i++) cons.Add((start + i, start + i + 1));
        }

        var sw = Stopwatch.StartNew();
        var tris = Delaunay.TriangulateConstrained(verts, cons);
        sw.Stop();
        _o.WriteLine($"约束 {verts.Count} 顶点 / {cons.Count} 约束: {sw.ElapsedMilliseconds} ms, {tris.Count} 三角");

        Assert.NotEmpty(tris);
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"3 万顶点等值线建面用了 {sw.ElapsedMilliseconds} ms —— 实测约 35 ms");
    }

    [Fact]
    public void HardConstraints_alwaysFinish_neverHang()
    {
        // 坏情况: 大量**不是现成边**的约束(斜穿网格), 正是实测卡死的形态。
        // 要求: 一定跑完, 并如实回报跳过了几条。
        int g = 120;
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < g; i++) for (int j = 0; j < g; j++) pts.Add((i * 10.0, j * 10.0));
        var cons = new List<(int u, int v)>();
        var rnd = new Random(7);
        for (int k = 0; k < 3000; k++)
        {
            int a = rnd.Next(pts.Count), b = rnd.Next(pts.Count);
            if (a != b) cons.Add((a, b));   // 随机长边: 几乎都要真嵌入
        }
        var sw = Stopwatch.StartNew();
        var tris = Delaunay.TriangulateConstrained(pts, cons, out int ins, out int skip);
        sw.Stop();
        _o.WriteLine($"随机长约束 n={pts.Count} 约束={cons.Count}: {sw.ElapsedMilliseconds} ms, 嵌入{ins} 跳过{skip}, {tris.Count} 三角");

        Assert.NotEmpty(tris);                       // 面必须建出来
        Assert.Equal(cons.Count, ins + skip);        // 每条约束都有交代
        // 关键: 嵌约束**不能把网啃掉**。三角数应仍在 2n 量级(此前会被啃到只剩几百个)
        Assert.InRange(tris.Count, (int)(1.5 * pts.Count), 2 * pts.Count);
        // 面积守恒: 结果仍覆盖整个凸包
        double area = 0;
        foreach (var (a, b, c) in tris)
            area += Math.Abs((pts[b].x - pts[a].x) * (pts[c].y - pts[a].y) - (pts[b].y - pts[a].y) * (pts[c].x - pts[a].x)) / 2;
        Assert.Equal((g - 1) * 10.0 * ((g - 1) * 10.0), area, 3);
        Assert.True(sw.ElapsedMilliseconds < 60000,
            $"用了 {sw.ElapsedMilliseconds} ms —— 预算保护应保证有限时间内收工, 不能卡死");
    }
}
