using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 多段线嵌入三角网回归 —— 用户要求"重新计算节点，完全贴在三角网上"：
///   ① 落面后的线**每个节点、每段中点**高程都等于面高程(线严格贴面, 中间不穿山不悬空)；
///   ② 线的每一段都是网的边(严格嵌入)；
///   ③ 面形不变(任意采样点前后高程一致)、无 T 形接头(边计数 1/2)、投影面积与边界周长守恒。
/// </summary>
public class MeshEmbedTests
{
    // 起伏地形 TIN：nx×ny 格点, z = f(x,y), 每格两三角
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Terrain(int nx, int ny, double cell, Func<double, double, double> f)
    {
        var v = new List<(double x, double y, double z)>();
        for (int j = 0; j <= ny; j++) for (int i = 0; i <= nx; i++) { double x = i * cell, y = j * cell; v.Add((x, y, f(x, y))); }
        var t = new List<(int a, int b, int c)>();
        for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++)
        {
            int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
            if ((i + j) % 2 == 0) { t.Add((a, b, d)); t.Add((a, d, c)); } else { t.Add((a, b, c)); t.Add((b, d, c)); }
        }
        return (v, t);
    }

    private static double Hill(double x, double y) => 100 + 8 * Math.Sin(x / 7.0) * Math.Cos(y / 5.0) + 0.3 * x - 0.2 * y;

    /// <summary>测试自用的面高程采样(逐三角重心插值, 小网直接扫)。</summary>
    private sealed class Sampler
    {
        private readonly IReadOnlyList<(double x, double y, double z)> _v; private readonly IReadOnlyList<(int a, int b, int c)> _t;
        public double MinX, MinY, MaxX, MaxY;
        public Sampler(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
        {
            _v = v; _t = t; MinX = v.Min(p => p.x); MaxX = v.Max(p => p.x); MinY = v.Min(p => p.y); MaxY = v.Max(p => p.y);
        }
        public bool TrySampleZ(double x, double y, out double z)
        {
            z = 0;
            foreach (var (a, b, c) in _t)
            {
                var p0 = _v[a]; var p1 = _v[b]; var p2 = _v[c];
                double d = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
                if (Math.Abs(d) < 1e-15) continue;
                double u = ((p1.y - p2.y) * (x - p2.x) + (p2.x - p1.x) * (y - p2.y)) / d;
                double v = ((p2.y - p0.y) * (x - p2.x) + (p0.x - p2.x) * (y - p2.y)) / d;
                double w = 1 - u - v;
                if (u >= -1e-9 && v >= -1e-9 && w >= -1e-9) { z = u * p0.z + v * p1.z + w * p2.z; return true; }
            }
            return false;
        }
    }

    private static (int boundary, int nonManifold, double boundaryLen) Edges(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var count = new Dictionary<(int, int), int>();
        void Bump(int u, int w) { var k = u < w ? (u, w) : (w, u); count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var (a, b, c) in t) { Bump(a, b); Bump(b, c); Bump(c, a); }
        int b1 = 0, b3 = 0; double len = 0;
        foreach (var kv in count)
        {
            if (kv.Value == 1) { b1++; var p = v[kv.Key.Item1]; var q = v[kv.Key.Item2]; len += Math.Sqrt((p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y)); }
            else if (kv.Value > 2) b3++;
        }
        return (b1, b3, len);
    }

    private static double AreaXY(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        double s = 0;
        foreach (var (a, b, c) in t) s += Math.Abs((v[b].x - v[a].x) * (v[c].y - v[a].y) - (v[c].x - v[a].x) * (v[b].y - v[a].y)) * 0.5;
        return s;
    }

    private static HashSet<(int, int)> EdgeSet(IReadOnlyList<(int a, int b, int c)> t)
    {
        var s = new HashSet<(int, int)>();
        foreach (var (a, b, c) in t) { s.Add(a < b ? (a, b) : (b, a)); s.Add(b < c ? (b, c) : (c, b)); s.Add(a < c ? (a, c) : (c, a)); }
        return s;
    }

    private static int VertexAt(IReadOnlyList<(double x, double y, double z)> v, (double x, double y, double z) p)
    {
        for (int i = 0; i < v.Count; i++) if (Math.Abs(v[i].x - p.x) < 1e-7 && Math.Abs(v[i].y - p.y) < 1e-7) return i;
        return -1;
    }

    /// <summary>线严格贴面 + 每段都是网边 + 面形未变。</summary>
    private static void AssertDraped(Sampler before, MeshEmbed.Result r, bool closed, int lineIndex = 0)
    {
        var after = new Sampler(r.Verts, r.Tris);
        var edges = EdgeSet(r.Tris);
        var pl = r.Polylines[lineIndex];
        int m = closed ? pl.Count : pl.Count - 1;
        for (int i = 0; i < m; i++)
        {
            var a = pl[i]; var b = pl[(i + 1) % pl.Count];
            Assert.True(before.TrySampleZ(a.x, a.y, out double za)); Assert.Equal(za, a.z, 6);
            double mx = 0.5 * (a.x + b.x), my = 0.5 * (a.y + b.y);
            Assert.True(before.TrySampleZ(mx, my, out double zm), $"段 {i} 中点落网外");
            Assert.Equal(zm, 0.5 * (a.z + b.z), 6);     // 段中点贴面 ⇒ 整段在某个三角形平面内
            int ia = VertexAt(r.Verts, a), ib = VertexAt(r.Verts, b);
            Assert.True(ia >= 0 && ib >= 0, $"段 {i} 端点不是网顶点");
            Assert.True(edges.Contains(ia < ib ? (ia, ib) : (ib, ia)), $"段 {i} 不是网边");
        }
        // 面形未变：随机采样前后高程一致
        var rnd = new Random(7);
        for (int k = 0; k < 400; k++)
        {
            double x = rnd.NextDouble() * (before.MaxX - before.MinX) + before.MinX, y = rnd.NextDouble() * (before.MaxY - before.MinY) + before.MinY;
            if (before.TrySampleZ(x, y, out double z0)) { Assert.True(after.TrySampleZ(x, y, out double z1)); Assert.Equal(z0, z1, 6); }
        }
    }

    [Fact]
    public void Flat_line_across_hilly_terrain_gets_nodes_at_every_edge_and_lies_on_surface()
    {
        var (v, t) = Terrain(12, 10, 10, Hill);
        var before = new Sampler(v, t);
        var line = new MeshEmbed.Line(new[] { (3.3, 7.1), (61.8, 48.2), (113.4, 22.6) }, null, false);
        var r = MeshEmbed.Embed(v, t, new[] { line })!;
        Assert.NotNull(r);
        Assert.True(r.Strict, $"未嵌入 {r.Unembedded}");
        Assert.True(r.Polylines[0].Count > 20, "跨越十几个格子必须补出大量交点节点");
        AssertDraped(before, r, false);
        var e0 = Edges(v, t); var e1 = Edges(r.Verts, r.Tris);
        Assert.Equal(0, e1.nonManifold);
        Assert.Equal(e0.boundaryLen, e1.boundaryLen, 6);
        Assert.Equal(AreaXY(v, t), AreaXY(r.Verts, r.Tris), 6);
        Assert.True(r.SplitFaces > 10 && r.SplitFaces < t.Count / 2, "只细分被线经过的三角形");
    }

    [Fact]
    public void Nodes_on_mesh_vertex_and_along_mesh_edge_do_not_duplicate_vertices()
    {
        var (v, t) = Terrain(6, 6, 10, Hill);
        var before = new Sampler(v, t);
        // 经过格点 (20,20)、沿格线 y=20 走到 (40,20)、再斜穿
        var line = new MeshEmbed.Line(new[] { (5.0, 12.0), (20.0, 20.0), (40.0, 20.0), (55.0, 47.0) }, null, false);
        var r = MeshEmbed.Embed(v, t, new[] { line })!;
        Assert.True(r.Strict);
        AssertDraped(before, r, false);
        // 顶点 (20,20)/(30,20)/(40,20) 都是原网顶点, 不得新增重复
        int dup = r.Verts.Count(p => Math.Abs(p.x - 20) < 1e-9 && Math.Abs(p.y - 20) < 1e-9);
        Assert.Equal(1, dup);
        Assert.Equal(0, Edges(r.Verts, r.Tris).nonManifold);
        Assert.Equal(AreaXY(v, t), AreaXY(r.Verts, r.Tris), 6);
    }

    [Fact]
    public void Two_crossing_lines_both_embed_strictly_with_shared_crossing_node()
    {
        var (v, t) = Terrain(8, 8, 10, Hill);
        var before = new Sampler(v, t);
        var l1 = new MeshEmbed.Line(new[] { (4.0, 4.0), (76.0, 74.0) }, null, false);
        var l2 = new MeshEmbed.Line(new[] { (6.0, 72.0), (74.0, 8.0) }, null, false);
        var r = MeshEmbed.Embed(v, t, new[] { l1, l2 })!;
        Assert.True(r.Strict, $"未嵌入 {r.Unembedded}");
        AssertDraped(before, r, false, 0);
        AssertDraped(before, r, false, 1);
        // 交叉点是两条线共有的节点
        var shared = r.Polylines[0].Where(p => r.Polylines[1].Any(q => Math.Abs(p.x - q.x) < 1e-7 && Math.Abs(p.y - q.y) < 1e-7)).ToList();
        Assert.Single(shared);
        Assert.Equal(0, Edges(r.Verts, r.Tris).nonManifold);
    }

    [Fact]
    public void Closed_loop_embeds_as_ring_of_edges()
    {
        var (v, t) = Terrain(10, 10, 10, Hill);
        var before = new Sampler(v, t);
        var loop = new MeshEmbed.Line(new[] { (23.0, 27.0), (71.0, 31.0), (66.0, 78.0), (19.0, 64.0) }, null, true);
        var r = MeshEmbed.Embed(v, t, new[] { loop })!;
        Assert.True(r.Strict);
        AssertDraped(before, r, true);
        Assert.Equal(AreaXY(v, t), AreaXY(r.Verts, r.Tris), 6);
    }

    [Fact]
    public void Line_leaving_the_mesh_keeps_outside_nodes_and_reports_them()
    {
        var (v, t) = Terrain(6, 6, 10, Hill);
        var before = new Sampler(v, t);
        var line = new MeshEmbed.Line(new[] { (-15.0, 25.0), (25.0, 25.0), (45.0, 45.0) }, new[] { 50.0, 50.0, 50.0 }, false);
        var r = MeshEmbed.Embed(v, t, new[] { line })!;
        Assert.Equal(1, r.OutsideNodes);
        Assert.Equal(50.0, r.Polylines[0][0].z);          // 网外节点保留输入高程
        Assert.Equal(1, r.Unembedded);                    // 只有网外那一小段未嵌入(边界交点已补)
        var inside = r.Polylines[0].Skip(1).ToList();
        Assert.True(inside.All(p => before.TrySampleZ(p.x, p.y, out double z) && Math.Abs(z - p.z) < 1e-6));
        Assert.Contains(inside, p => Math.Abs(p.x) < 1e-7);   // 进网处(x=0 边界)补了节点
        Assert.Equal(0, Edges(r.Verts, r.Tris).nonManifold);
    }

    [Fact]
    public void Line_entirely_inside_one_triangle_only_splits_that_triangle()
    {
        var (v, t) = Terrain(4, 4, 10, Hill);
        var before = new Sampler(v, t);
        var line = new MeshEmbed.Line(new[] { (11.0, 13.0), (13.0, 16.0) }, null, false);   // 格 (1,1) 对角线上方那个三角形内
        var r = MeshEmbed.Embed(v, t, new[] { line })!;
        Assert.True(r.Strict);
        Assert.Equal(1, r.SplitFaces);
        Assert.Equal(2, r.InsertedPoints);
        AssertDraped(before, r, false);
        Assert.Equal(AreaXY(v, t), AreaXY(r.Verts, r.Tris), 6);
    }

    // ── 线落到面上 (POLYPROJECT) 只重算线节点、网不动 ──
    // 用户截图：二维两点线落面后"只是节点在面上, 中间没有插值在面上"——直段穿山悬空。

    /// <summary>落面后的线每节点、每段中点都贴面(中点贴面 ⇒ 整段在某三角形平面内)。</summary>
    private static void AssertLiesOnSurface(Sampler s, List<(double x, double y, double z)> pl, bool closed)
    {
        int m = closed ? pl.Count : pl.Count - 1;
        for (int i = 0; i < m; i++)
        {
            var a = pl[i]; var b = pl[(i + 1) % pl.Count];
            Assert.True(s.TrySampleZ(a.x, a.y, out double za)); Assert.Equal(za, a.z, 6);
            Assert.True(s.TrySampleZ(0.5 * (a.x + b.x), 0.5 * (a.y + b.y), out double zm), $"段 {i} 中点落网外");
            Assert.Equal(zm, 0.5 * (a.z + b.z), 6);
        }
    }

    [Fact]
    public void Drape_two_point_flat_line_gets_edge_crossings_and_lies_on_surface_without_touching_mesh()
    {
        var (v, t) = Terrain(12, 10, 10, Hill);
        var s = new Sampler(v, t);
        var line = new MeshEmbed.Line(new[] { (3.3, 7.1), (113.4, 82.6) }, null, false);
        var r = MeshEmbed.Drape(v, t, new[] { line })!;
        Assert.NotNull(r);
        var pl = r.Polylines[0];
        Assert.True(pl.Count > 20, $"两点直线跨十几个格子必须补出交点节点, 实得 {pl.Count}");
        Assert.Equal(2, r.InputNodes); Assert.Equal(pl.Count, r.OutputNodes); Assert.Equal(0, r.OutsideNodes);
        // 首尾还是原顶点(XY 不变), 中间节点按线段参数单调排列
        Assert.Equal(3.3, pl[0].x, 9); Assert.Equal(7.1, pl[0].y, 9);
        Assert.Equal(113.4, pl[^1].x, 9); Assert.Equal(82.6, pl[^1].y, 9);
        for (int i = 1; i < pl.Count; i++) Assert.True(pl[i].x > pl[i - 1].x, $"节点 {i} 次序错");
        AssertLiesOnSurface(s, pl, false);
        // 只投顶点的老做法：段中点高程 ≠ 面高程(否则此测试无意义)
        s.TrySampleZ(0.5 * (3.3 + 113.4), 0.5 * (7.1 + 82.6), out double zmSurf);
        Assert.NotEqual(zmSurf, 0.5 * (pl[0].z + pl[^1].z), 1);
    }

    [Fact]
    public void Drape_closed_loop_lies_on_surface_including_closing_segment()
    {
        var (v, t) = Terrain(10, 10, 10, Hill);
        var s = new Sampler(v, t);
        var loop = new MeshEmbed.Line(new[] { (23.0, 27.0), (71.0, 31.0), (66.0, 78.0), (19.0, 64.0) }, null, true);
        var r = MeshEmbed.Drape(v, t, new[] { loop })!;
        var pl = r.Polylines[0];
        Assert.True(pl.Count > 8);
        Assert.False(Math.Abs(pl[0].x - pl[^1].x) < 1e-9 && Math.Abs(pl[0].y - pl[^1].y) < 1e-9, "闭合线不重复首点");
        AssertLiesOnSurface(s, pl, true);
    }

    [Fact]
    public void Drape_keeps_outside_vertices_with_input_z_and_adds_boundary_crossing()
    {
        var (v, t) = Terrain(6, 6, 10, Hill);
        var s = new Sampler(v, t);
        var line = new MeshEmbed.Line(new[] { (-15.0, 25.0), (25.0, 25.0), (45.0, 45.0) }, new[] { 50.0, 50.0, 50.0 }, false);
        var r = MeshEmbed.Drape(v, t, new[] { line })!;
        Assert.Equal(1, r.OutsideNodes);
        var pl = r.Polylines[0];
        Assert.Equal(50.0, pl[0].z);                                  // 网外顶点保留输入高程
        Assert.Contains(pl.Skip(1), p => Math.Abs(p.x) < 1e-7);       // 进网处(x=0 边界)补了节点
        AssertLiesOnSurface(s, pl.Skip(1).ToList(), false);
    }

    [Fact]
    public void Degenerate_inputs_are_safe()
    {
        var (v, t) = Terrain(3, 3, 10, Hill);
        Assert.Null(MeshEmbed.Embed(v, new List<(int, int, int)>(), new[] { new MeshEmbed.Line(new[] { (1.0, 1.0) }, null, false) }));
        var r = MeshEmbed.Embed(v, t, new[] { new MeshEmbed.Line(new[] { (5.0, 5.0), (5.0, 5.0) }, null, false) })!;   // 零长线
        Assert.Equal(t.Count, r.Tris.Count);
        Assert.Single(r.Polylines[0]);
    }
}
