using System;
using System.Collections.Generic;
using System.Diagnostics;
using PitMine3D.Kylin.Cad;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 捕捉空间索引 —— 与线性版的等价性对拍。
/// 索引只是换了"取候选"的方式，命中必须与全量扫描逐字段一致（含平局取舍），
/// 否则光标会吸到另一个点上。
/// </summary>
public class SnapIndexTests
{
    private static float[] RandomVerts(Random r, int n, double span = 5000)
    {
        var v = new float[n * 6];
        for (int i = 0; i < n; i++)
        {
            v[i * 6] = (float)(r.NextDouble() * span);
            v[i * 6 + 1] = (float)(r.NextDouble() * span);
        }
        return v;
    }

    [Fact]
    public void Vertex_index_matches_linear_scan()
    {
        var r = new Random(7);
        var v = RandomVerts(r, 4000);
        var idx = new SnapPoints.Index(v);
        for (int q = 0; q < 3000; q++)
        {
            double cx = r.NextDouble() * 5000, cy = r.NextDouble() * 5000;
            double tol = r.NextDouble() * 60;
            var lin = SnapPoints.FindNearest(v, cx, cy, tol);
            var got = idx.FindNearest(cx, cy, tol);
            Assert.Equal(lin.HasValue, got.HasValue);
            if (lin.HasValue)
            {
                Assert.Equal(lin.Value.x, got!.Value.x);
                Assert.Equal(lin.Value.y, got.Value.y);
            }
        }
    }

    [Fact]
    public void Vertex_index_handles_degenerate_inputs()
    {
        Assert.Null(new SnapPoints.Index(null).FindNearest(0, 0, 1));
        Assert.Null(new SnapPoints.Index(Array.Empty<float>()).FindNearest(0, 0, 1));
        Assert.Null(new SnapPoints.Index(new float[] { 1, 2, 0, 0, 0, 0 }).FindNearest(1, 2, 0));   // tol=0

        // 全部共线（网格一轴退化）+ 一个非有限坐标（走旁路，且不该被吸住）
        var v = new float[] { 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 20, 0, 0, 0, 0, 0, float.NaN, float.NaN, 0, 0, 0, 0 };
        var idx = new SnapPoints.Index(v);
        var h = idx.FindNearest(9.8, 0.1, 1.0);
        Assert.NotNull(h);
        Assert.Equal(10, h!.Value.x, 4);
        Assert.Equal(SnapPoints.FindNearest(v, 9.8, 0.1, 1.0)!.Value.x, h.Value.x);
    }

    [Fact]
    public void Vertex_index_ties_pick_same_vertex_as_linear()
    {
        // 等距的两点：线性版取后出现的那个，索引版必须一致
        var v = new float[] { -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0 };
        var lin = SnapPoints.FindNearest(v, 0, 0, 5);
        var got = new SnapPoints.Index(v).FindNearest(0, 0, 5);
        Assert.Equal(lin!.Value.x, got!.Value.x);
    }

    // ── 原语索引 ──

    private static (List<ObjectSnap.Seg>, List<ObjectSnap.Circ>, List<ObjectSnap.ArcP>, List<(double x, double y)>)
        RandomGeom(Random r, int nSeg, int nCirc, int nArc, int nPt, double span = 2000)
    {
        var segs = new List<ObjectSnap.Seg>();
        for (int i = 0; i < nSeg; i++)
        {
            double x = r.NextDouble() * span, y = r.NextDouble() * span;
            segs.Add(new ObjectSnap.Seg(x, y, x + (r.NextDouble() - 0.5) * 200, y + (r.NextDouble() - 0.5) * 200));
        }
        var circles = new List<ObjectSnap.Circ>();
        for (int i = 0; i < nCirc; i++)
            circles.Add(new ObjectSnap.Circ(r.NextDouble() * span, r.NextDouble() * span, 5 + r.NextDouble() * 80));
        var arcs = new List<ObjectSnap.ArcP>();
        for (int i = 0; i < nArc; i++)
        {
            double a0 = r.NextDouble() * Math.PI * 2;
            arcs.Add(new ObjectSnap.ArcP(r.NextDouble() * span, r.NextDouble() * span, 5 + r.NextDouble() * 80,
                                         a0, a0 + r.NextDouble() * Math.PI * 1.8));
        }
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < nPt; i++) pts.Add((r.NextDouble() * span, r.NextDouble() * span));
        return (segs, circles, arcs, pts);
    }

    [Fact]
    public void Geom_index_matches_linear_find_across_modes()
    {
        var r = new Random(11);
        var (segs, circles, arcs, pts) = RandomGeom(r, 1200, 120, 120, 400);
        var idx = new ObjectSnap.Index(segs, circles, arcs, pts);
        int[] masks =
        {
            ObjectSnap.AllModes,
            ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection),
            ObjectSnap.MaskOf(ObjectSnap.Mode.Nearest),
            ObjectSnap.MaskOf(ObjectSnap.Mode.Perpendicular),
            ObjectSnap.MaskOf(ObjectSnap.Mode.Endpoint, ObjectSnap.Mode.Midpoint),
            ObjectSnap.MaskOf(ObjectSnap.Mode.Center, ObjectSnap.Mode.Intersection),
        };
        for (int q = 0; q < 1500; q++)
        {
            double cx = r.NextDouble() * 2000, cy = r.NextDouble() * 2000;
            double tol = r.NextDouble() * 40;
            var anchor = q % 3 == 0 ? ((double x, double y)?)(r.NextDouble() * 2000, r.NextDouble() * 2000) : null;
            int mask = masks[q % masks.Length];

            var lin = ObjectSnap.Find(segs, circles, arcs, pts, cx, cy, tol, mask, anchor);
            var got = idx.Find(cx, cy, tol, mask, anchor);
            Assert.Equal(lin.HasValue, got.HasValue);
            if (lin.HasValue)
            {
                Assert.Equal(lin.Value.Mode, got!.Value.Mode);
                Assert.Equal(lin.Value.X, got.Value.X);
                Assert.Equal(lin.Value.Y, got.Value.Y);
            }
        }
    }

    [Fact]
    public void Geom_index_matches_on_dense_grid_where_ties_abound()
    {
        // 规则网格：端点/中点/交点大量等距，最能试出平局取舍是否与线性版一致
        var segs = new List<ObjectSnap.Seg>();
        for (int i = 0; i <= 40; i++)
        {
            segs.Add(new ObjectSnap.Seg(0, i * 10, 400, i * 10));
            segs.Add(new ObjectSnap.Seg(i * 10, 0, i * 10, 400));
        }
        var idx = new ObjectSnap.Index(segs, null, null, null);
        var r = new Random(3);
        for (int q = 0; q < 2000; q++)
        {
            double cx = r.NextDouble() * 400, cy = r.NextDouble() * 400, tol = 1 + r.NextDouble() * 12;
            if (q % 7 == 0) { cx = Math.Round(cx / 5) * 5; cy = Math.Round(cy / 5) * 5; }   // 常落在等距位置
            var lin = ObjectSnap.Find(segs, new List<ObjectSnap.Circ>(), new List<ObjectSnap.ArcP>(),
                                      new List<(double, double)>(), cx, cy, tol, ObjectSnap.AllModes, (7, 13));
            var got = idx.Find(cx, cy, tol, ObjectSnap.AllModes, (7, 13));
            Assert.Equal(lin.HasValue, got.HasValue);
            if (lin.HasValue)
            {
                Assert.Equal(lin.Value.Mode, got!.Value.Mode);
                Assert.Equal(lin.Value.X, got.Value.X);
                Assert.Equal(lin.Value.Y, got.Value.Y);
            }
        }
    }

    [Fact]
    public void Geom_index_keeps_oversized_primitives_reachable()
    {
        // 横跨全场景的长线与大圆铺格代价过高 → 走旁路；它们照样要能被捕捉到
        var segs = new List<ObjectSnap.Seg> { new(-100000, 0, 100000, 5) };
        var circles = new List<ObjectSnap.Circ> { new(0, 0, 90000) };
        var idx = new ObjectSnap.Index(segs, circles, null, null);

        var near = idx.Find(500, 2.5, 1.0, ObjectSnap.MaskOf(ObjectSnap.Mode.Nearest), null);
        Assert.NotNull(near);
        Assert.Equal(ObjectSnap.Mode.Nearest, near!.Value.Mode);

        var center = idx.Find(0.2, 0.1, 1.0, ObjectSnap.MaskOf(ObjectSnap.Mode.Center), null);
        Assert.NotNull(center);
        Assert.Equal(ObjectSnap.Mode.Center, center!.Value.Mode);

        var lin = ObjectSnap.Find(segs, circles, new List<ObjectSnap.ArcP>(), new List<(double, double)>(),
                                  500, 2.5, 1.0, ObjectSnap.AllModes, null);
        var got = idx.Find(500, 2.5, 1.0, ObjectSnap.AllModes, null);
        Assert.Equal(lin!.Value.Mode, got!.Value.Mode);
        Assert.Equal(lin.Value.X, got.Value.X);
    }

    [Fact]
    public void Geom_index_handles_empty_and_zero_tolerance()
    {
        var idx = new ObjectSnap.Index(null, null, null, null);
        Assert.Null(idx.Find(0, 0, 1, ObjectSnap.AllModes, null));
        Assert.Null(new ObjectSnap.Index(new List<ObjectSnap.Seg> { new(0, 0, 1, 1) }, null, null, null)
                        .Find(0, 0, 0, ObjectSnap.AllModes, null));
    }

    [Fact]
    public void Intersection_density_guard_ignores_segments_outside_snap_aperture()
    {
        var segs = new List<ObjectSnap.Seg>
        {
            new(-1, -1, 1, 1),
            new(-1, 1, 1, -1),
        };
        for (int i = 0; i < 2000; i++)
        {
            double y = 1.1 + i * 0.001;
            segs.Add(new ObjectSnap.Seg(-10, y, 10, y));
        }
        var idx = new ObjectSnap.Index(segs, null, null, null);

        var hit = idx.Find(0, 0, 1, ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection), null);

        Assert.NotNull(hit);
        Assert.Equal(0, hit!.Value.X, 6);
        Assert.Equal(0, hit.Value.Y, 6);
    }
}

/// <summary>
/// 捕捉的性能护栏 —— 图元一多，开着捕捉挪鼠标每帧都要重算一遍捕捉点；
/// 线性扫全场景在几十万顶点上就是几十毫秒一帧，光标直接拖不动（用户实测「数量大时卡顿」）。
/// 时限放得很宽，只为拦住"退回逐帧全表扫描"的改动。
/// </summary>
public class SnapIndexBench
{
    private readonly ITestOutputHelper _o;
    public SnapIndexBench(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void Vertex_snap_200k_stays_interactive()
    {
        const int n = 200_000, queries = 2000;
        var r = new Random(5);
        var v = new float[n * 6];
        for (int i = 0; i < n; i++) { v[i * 6] = (float)(r.NextDouble() * 5000); v[i * 6 + 1] = (float)(r.NextDouble() * 5000); }

        var sw = Stopwatch.StartNew();
        var idx = new SnapPoints.Index(v);
        long build = sw.ElapsedMilliseconds;
        sw.Restart();
        for (int q = 0; q < queries; q++) idx.FindNearest(r.NextDouble() * 5000, r.NextDouble() * 5000, 8);
        sw.Stop();
        long idxMs = sw.ElapsedMilliseconds;
        sw.Restart();
        for (int q = 0; q < 100; q++) SnapPoints.FindNearest(v, r.NextDouble() * 5000, r.NextDouble() * 5000, 8);
        sw.Stop();
        _o.WriteLine($"20 万顶点: 建索引 {build} ms, 索引 {queries} 次查询 {idxMs} ms; 线性 100 次 {sw.ElapsedMilliseconds} ms");
        sw.Restart(); sw.Stop();

        Assert.True(build < 3000, $"建索引用了 {build} ms");
        Assert.True(idxMs < 1500,
            $"{queries} 次捕捉查询用了 {idxMs} ms —— 实测约 1 ms, 超这么多说明退回了逐帧全表扫描");
    }

    [Fact]
    public void Object_snap_50k_segments_stays_interactive()
    {
        const int n = 50_000, queries = 1000;
        var r = new Random(6);
        var segs = new List<ObjectSnap.Seg>(n);
        for (int i = 0; i < n; i++)
        {
            double x = r.NextDouble() * 5000, y = r.NextDouble() * 5000;
            segs.Add(new ObjectSnap.Seg(x, y, x + (r.NextDouble() - 0.5) * 50, y + (r.NextDouble() - 0.5) * 50));
        }

        var sw = Stopwatch.StartNew();
        var idx = new ObjectSnap.Index(segs, null, null, null);
        long build = sw.ElapsedMilliseconds;
        sw.Restart();
        for (int q = 0; q < queries; q++)
            idx.Find(r.NextDouble() * 5000, r.NextDouble() * 5000, 8, ObjectSnap.AllModes, (10, 10));
        sw.Stop();
        long idxMs = sw.ElapsedMilliseconds;
        sw.Restart();
        for (int q = 0; q < 100; q++)
            ObjectSnap.Find(segs, null, null, null, r.NextDouble() * 5000, r.NextDouble() * 5000, 8, ObjectSnap.AllModes, (10, 10));
        sw.Stop();
        _o.WriteLine($"5 万线段: 建索引 {build} ms, 索引 {queries} 次查询 {idxMs} ms; 线性 100 次 {sw.ElapsedMilliseconds} ms");

        Assert.True(build < 3000, $"建索引用了 {build} ms");
        Assert.True(idxMs < 1500,
            $"{queries} 次对象捕捉用了 {idxMs} ms —— 实测约 5 ms, 超这么多说明退回了逐帧全表扫描");
    }

    /// <summary>
    /// 整图视图下 12px 会对应几百米，密集采剥图的交点候选可达十万级；若仍两两求交，
    /// 一次 PointerMoved 就会变成数十亿次比较并卡住 UI。过密时宁可本帧不吸交点，放大后自然恢复。
    /// </summary>
    [Fact]
    public void Intersection_snap_in_dense_zoomed_out_view_stays_interactive()
    {
        const int n = 20_000;
        var segs = new List<ObjectSnap.Seg>(n);
        for (int i = 0; i < n; i++)
        {
            double y = (i - n / 2) * 0.005;
            segs.Add(new ObjectSnap.Seg(-1000, y, 1000, y));
        }
        var idx = new ObjectSnap.Index(segs, null, null, null);

        var sw = Stopwatch.StartNew();
        var hit = idx.Find(0, 0, 100, ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection), null);
        sw.Stop();

        Assert.Null(hit); // 全是平行线，本来就没有交点
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"密集视图一次交点捕捉用了 {sw.ElapsedMilliseconds} ms —— 鼠标移动会直接卡死");
    }

    [Fact]
    public void 同网格的大量孔径外线段不能吞掉真实交点()
    {
        var segs = new List<ObjectSnap.Seg>();
        for (int i = 0; i < 1_000; i++)
        {
            double y = 3 + i * 0.0001; // 与光标同网格，但在 tol=1 的精确孔径外
            segs.Add(new ObjectSnap.Seg(-1_000, y, 1_000, y));
        }
        segs.Add(new ObjectSnap.Seg(-10, 0, 10, 0));
        segs.Add(new ObjectSnap.Seg(0, -10, 0, 10));
        var idx = new ObjectSnap.Index(segs, null, null, null);

        var hit = idx.Find(0, 0, 1, ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection), null);

        Assert.NotNull(hit);
        Assert.Equal(ObjectSnap.Mode.Intersection, hit!.Value.Mode);
        Assert.Equal(0, hit.Value.X, 9);
        Assert.Equal(0, hit.Value.Y, 9);
    }

    /// <summary>
    /// 长线占多数时的对拍 + 预算：早先线段按包围盒铺格、超 24 格进旁路，图一大格子一细，稍长的斜线全进旁路，
    /// 每次查询都把它们扫一遍（15 万条长线实测 5 ms/次，开着捕捉光标就拖不动）。改按路径铺格后 0.14 ms/次，且命中与线性版逐点一致。
    /// </summary>
    [Fact]
    public void Geom_index_long_segments_do_not_fall_into_bypass_and_stay_exact()
    {
        var r = new Random(23);
        var segs = new List<ObjectSnap.Seg>();
        for (int i = 0; i < 60000; i++)
        {
            double x = r.NextDouble() * 10000, y = r.NextDouble() * 10000, a = r.NextDouble() * Math.PI * 2;
            segs.Add(new ObjectSnap.Seg(x, y, x + 5 * Math.Cos(a), y + 5 * Math.Sin(a)));
        }
        for (int i = 0; i < 40000; i++)   // 40% 长线：300~2000 m
        {
            double x = r.NextDouble() * 10000, y = r.NextDouble() * 10000, a = r.NextDouble() * Math.PI * 2, L = 300 + r.NextDouble() * 1700;
            segs.Add(new ObjectSnap.Seg(x, y, x + L * Math.Cos(a), y + L * Math.Sin(a)));
        }
        var idx = new ObjectSnap.Index(segs, null, null, null);
        int[] masks = { ObjectSnap.AllModes, ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection), ObjectSnap.MaskOf(ObjectSnap.Mode.Nearest), ObjectSnap.MaskOf(ObjectSnap.Mode.Perpendicular) };
        for (int q = 0; q < 400; q++)
        {
            double cx = r.NextDouble() * 10000, cy = r.NextDouble() * 10000, tol = 2 + r.NextDouble() * 20;
            var anchor = q % 2 == 0 ? ((double x, double y)?)(r.NextDouble() * 10000, r.NextDouble() * 10000) : null;
            int mask = masks[q % masks.Length];
            var lin = ObjectSnap.Find(segs, null, null, null, cx, cy, tol, mask, anchor);
            var got = idx.Find(cx, cy, tol, mask, anchor);
            Assert.Equal(lin.HasValue, got.HasValue);
            if (lin.HasValue) { Assert.Equal(lin.Value.Mode, got!.Value.Mode); Assert.Equal(lin.Value.X, got.Value.X); Assert.Equal(lin.Value.Y, got.Value.Y); }
        }
        // 预算：交点模式 300 次查询 ≤ 300 ms（旧旁路实现同规模要 1.5 s 以上）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int xm = ObjectSnap.MaskOf(ObjectSnap.Mode.Intersection);
        for (int q = 0; q < 300; q++) idx.Find(r.NextDouble() * 10000, r.NextDouble() * 10000, 8, xm, null);
        Assert.True(sw.ElapsedMilliseconds < 300, $"300 次交点捕捉耗时 {sw.ElapsedMilliseconds} ms");
    }
}
