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
}
