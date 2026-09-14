using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「生成三角网边界」链路(朝向一致化 → 边界环 → 边表)的性能护栏。
/// 用户实测百万三角的面模型点「生成边界」十几分钟出不来：边键 (lo&lt;&lt;32)|hi 走 long 默认散列
/// (高 32 位 ^ 低 32 位), 格网上相邻顶点号只差 1/行宽, 整张网只剩几百个散列值, 字典退化成链表、O(n²)。
/// 这里的时限放得很宽, 目的只有一个：谁把 PackedKeyComparer 拿掉, 这几条立刻红。
/// </summary>
public class MeshBoundaryBench
{
    private readonly ITestOutputHelper _o;
    public MeshBoundaryBench(ITestOutputHelper o) { _o = o; }

    private static MeshEntity Grid(int n)
    {
        var verts = new List<(double x, double y, double z)>((n + 1) * (n + 1));
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) verts.Add((500000 + i * 5.0, 4000000 + j * 5.0, 100 + Math.Sin(i * 0.1) * 10));
        var tris = new List<(int a, int b, int c)>(n * n * 2);
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            tris.Add((a, b, d)); tris.Add((a, d, c));
        }
        return new MeshEntity("t", verts, tris);
    }

    [Fact]
    public void PackedKeyComparer_gridEdges_hashSpreadsOut()
    {
        // 300×300 格网 27 万条边：默认 long 散列只有 ~600 个值(实测 11 s), 混合散列 11 ms。
        var m = Grid(300);
        var keys = new HashSet<long>(PackedKeyComparer.Instance);
        var codes = new HashSet<int>();
        foreach (var (a, b, c) in m.Tris)
            foreach (var (u, w) in new[] { (a, b), (b, c), (c, a) })
            {
                long k = ((long)Math.Min(u, w) << 32) | (uint)Math.Max(u, w);
                if (keys.Add(k)) codes.Add(PackedKeyComparer.Instance.GetHashCode(k));
            }
        _o.WriteLine($"边 {keys.Count} · 散列值 {codes.Count}");
        Assert.True(codes.Count > keys.Count * 0.99, $"27 万条边只散出 {codes.Count} 个散列值");
    }

    [Fact]
    public void Boundary_1M_tris_staysUnderSeconds()
    {
        var m = Grid(800);   // 1.28M 三角
        var sw = Stopwatch.StartNew();
        var oriented = MeshOrient.MakeConsistent(m.Verts, m.Tris);
        long tOrient = sw.ElapsedMilliseconds; sw.Restart();
        var loops = MeshBoundaryLoops.Extract(m.Verts, oriented);
        long tLoops = sw.ElapsedMilliseconds; sw.Restart();
        int edges = m.Edges.Count;
        long tEdges = sw.ElapsedMilliseconds;
        _o.WriteLine($"{m.TriangleCount} 三角: 朝向 {tOrient} ms · 边界环 {tLoops} ms ({loops.Count} 环/{loops.Sum(l => l.Count)} 点) · 边表 {tEdges} ms ({edges} 边)");

        Assert.Single(loops);
        Assert.Equal(800 * 4, loops[0].Count);
        Assert.True(tOrient < 15000, $"朝向一致化 {tOrient} ms —— 实测约 1 s, 超这么多说明边键散列又撞回链表");
        Assert.True(tLoops < 15000, $"边界环提取 {tLoops} ms —— 实测约 1 s");
        Assert.True(tEdges < 15000, $"边表 {tEdges} ms —— 实测 < 1 s");
    }
}
