using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 沿多段线分割三角网（忠实原 splitByPolylineVertical；断言口径照原 Tests.xllAcEd/test_split_tin_by_polyline ST1–ST5，
/// 再加"折线拐弯处必须贴线走"——这条是用户报的现象，旧 MeshPlaneSplit(首末两点竖直面)过不了）。
/// </summary>
public class MeshPolylineSplitTests
{
    // N×N 格的平面 TIN，覆盖 [0,size]²
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) GridTin(int n, double size, Func<double, double, double>? z = null)
    {
        var v = new List<(double x, double y, double z)>(); var t = new List<(int a, int b, int c)>();
        double step = size / n;
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add((i * step, j * step, z?.Invoke(i * step, j * step) ?? 0));
        int Idx(int i, int j) => j * (n + 1) + i;
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            t.Add((Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1)));
            t.Add((Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1)));
        }
        return (v, t);
    }

    private static double Area2D(List<(double x, double y, double z)> v, List<(int a, int b, int c)> t)
        => t.Sum(f => Math.Abs((v[f.b].x - v[f.a].x) * (v[f.c].y - v[f.a].y) - (v[f.b].y - v[f.a].y) * (v[f.c].x - v[f.a].x)) * 0.5);

    private static IEnumerable<(double x, double y)> Centroids(List<(double x, double y, double z)> v, List<(int a, int b, int c)> t)
        => t.Select(f => ((v[f.a].x + v[f.b].x + v[f.c].x) / 3, (v[f.a].y + v[f.b].y + v[f.c].y) / 3));

    [Fact]
    public void ST1_CrossingCut_FillsBothSides()
    {
        var (v, t) = GridTin(10, 100);
        var r = MeshPolylineSplit.Split(v, t, new[] { (-10.0, 50.0), (110.0, 50.0) });
        Assert.True(r.Success, r.Error);
        Assert.True(r.HasLeft); Assert.True(r.HasRight);
    }

    [Fact]
    public void ST2_CrossingCut_AreaConserved_offCenter()
    {
        var (v, t) = GridTin(10, 100);
        var r = MeshPolylineSplit.Split(v, t, new[] { (-10.0, 37.0), (110.0, 37.0) });   // 偏心切, 2×0.37≠1 才抓得住错分
        double orig = Area2D(v, t), sum = Area2D(r.LeftVerts, r.LeftTris) + Area2D(r.RightVerts, r.RightTris);
        Assert.InRange(sum, orig * (1 - 1e-6), orig * (1 + 1e-6));
        Assert.InRange(Area2D(r.LeftVerts, r.LeftTris), 100 * 63 - 1e-6, 100 * 63 + 1e-6);   // 左 = 走向左手侧 = y>37
    }

    [Fact]
    public void ST3_CrossingCut_SidesSeparatedByCutLine()
    {
        var (v, t) = GridTin(10, 100);
        var r = MeshPolylineSplit.Split(v, t, new[] { (-10.0, 50.0), (110.0, 50.0) });
        Assert.All(Centroids(r.LeftVerts, r.LeftTris), c => Assert.True(c.y > 50 - 1e-6));
        Assert.All(Centroids(r.RightVerts, r.RightTris), c => Assert.True(c.y < 50 + 1e-6));
    }

    [Fact]
    public void ST4_NonCrossingCut_KeepsMeshOnOneSide()
    {
        var (v, t) = GridTin(10, 100);
        var r = MeshPolylineSplit.Split(v, t, new[] { (-10.0, -50.0), (110.0, -50.0) });   // 整条线在网格南侧之外
        Assert.True(r.Success);
        Assert.True(r.HasLeft != r.HasRight, "切口未横穿时必须恰好一侧为空");
        double kept = Area2D(r.LeftVerts, r.LeftTris) + Area2D(r.RightVerts, r.RightTris);
        Assert.InRange(kept, Area2D(v, t) * (1 - 1e-6), Area2D(v, t) * (1 + 1e-6));
    }

    [Fact]
    public void ST5_PolylineCut_MultiSegmentStillSplits()
    {
        var (v, t) = GridTin(10, 100, (x, y) => 0.1 * x + 0.05 * y);
        var r = MeshPolylineSplit.Split(v, t, new[] { (-10.0, 20.0), (40.0, 45.0), (70.0, 30.0), (110.0, 80.0) });
        Assert.True(r.Success, r.Error);
        Assert.True(r.HasLeft && r.HasRight);
        double orig = Area2D(v, t), sum = Area2D(r.LeftVerts, r.LeftTris) + Area2D(r.RightVerts, r.RightTris);
        Assert.InRange(sum, orig * (1 - 1e-6), orig * (1 + 1e-6));
        // Z 沿源三角平面插值：切口顶点的 z 必须仍在斜面上
        foreach (var p in r.LeftVerts.Concat(r.RightVerts))
            Assert.InRange(p.z, 0.1 * p.x + 0.05 * p.y - 1e-9, 0.1 * p.x + 0.05 * p.y + 1e-9);
    }

    /// <summary>
    /// 用户报的现象：L 形折线 (0,30)→(50,30)→(50,100)，旧实现按首末两点弦 (0,30)→(50,100) 切，
    /// 左片会跨到折线另一侧。严格沿线：左片(走向左手侧 = 折线上方/左方)所有形心必须满足 y>30 且 x<50。
    /// </summary>
    [Fact]
    public void LShapedPolyline_followsEverySegment_notTheChord()
    {
        var (v, t) = GridTin(10, 100);
        var poly = new[] { (-10.0, 30.0), (50.0, 30.0), (50.0, 110.0) };
        var r = MeshPolylineSplit.Split(v, t, poly);
        Assert.True(r.Success, r.Error);
        Assert.All(Centroids(r.LeftVerts, r.LeftTris), c => Assert.True(c.y > 30 - 1e-6 && c.x < 50 + 1e-6, $"左片形心 {c} 跨到了折线另一侧"));
        Assert.All(Centroids(r.RightVerts, r.RightTris), c => Assert.True(c.y < 30 + 1e-6 || c.x > 50 - 1e-6, $"右片形心 {c} 跨到了折线另一侧"));
        double sum = Area2D(r.LeftVerts, r.LeftTris) + Area2D(r.RightVerts, r.RightTris);
        Assert.InRange(sum, 10000 * (1 - 1e-6), 10000 * (1 + 1e-6));
        Assert.InRange(Area2D(r.LeftVerts, r.LeftTris), 50 * 70 - 1e-6, 50 * 70 + 1e-6);   // 左片 = [0,50]×[30,100]
        // 旧实现(首末点弦)在同一输入上必然把左片切错 —— 护住"别再回退成弦切"
        var (l, _) = MeshPlaneSplit.Split(v, t, poly[0].Item1, poly[0].Item2, poly[^1].Item1, poly[^1].Item2);
        Assert.NotInRange(Area2D(l.v, l.t), 50 * 70 - 1e-6, 50 * 70 + 1e-6);
    }

    /// <summary>粗网 + 密折线：一个三角里落多段(走约束剖分路)，切口必须仍是折线本身——每个切口顶点都在折线上或三角边上。</summary>
    [Fact]
    public void DensePolylineOnCoarseMesh_cutVerticesLieOnPolyline()
    {
        var (v, t) = GridTin(2, 100);   // 8 个大三角
        var poly = new List<(double x, double y)>();
        for (int i = 0; i <= 40; i++) poly.Add((-5 + i * 2.75, 50 + 12 * Math.Sin(i * 0.5)));   // 密集波浪线横穿
        var r = MeshPolylineSplit.Split(v, t, poly);
        Assert.True(r.Success, r.Error);
        Assert.True(r.HasLeft && r.HasRight);
        double sum = Area2D(r.LeftVerts, r.LeftTris) + Area2D(r.RightVerts, r.RightTris);
        Assert.InRange(sum, 10000 * (1 - 1e-6), 10000 * (1 + 1e-6));
        // 两片除原网 9 个格点外的新顶点，都得贴在折线上(距离 < 1e-6)
        bool OnPoly((double x, double y, double z) p)
        {
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                double ax = poly[i].x, ay = poly[i].y, sx = poly[i + 1].x - ax, sy = poly[i + 1].y - ay;
                double tt = Math.Clamp(((p.x - ax) * sx + (p.y - ay) * sy) / (sx * sx + sy * sy), 0, 1);
                double dx = p.x - (ax + tt * sx), dy = p.y - (ay + tt * sy);
                if (dx * dx + dy * dy < 1e-12) return true;
            }
            return false;
        }
        bool IsGridPt((double x, double y, double z) p) => Math.Abs(p.x % 50) < 1e-9 && Math.Abs(p.y % 50) < 1e-9;
        foreach (var p in r.LeftVerts.Concat(r.RightVerts))
            Assert.True(IsGridPt(p) || OnPoly(p), $"切口顶点 {p} 不在折线上");
        // 左片形心全在折线左手侧(上方)、右片全在下方
        double PolyY(double x) { int i = (int)Math.Clamp((x + 5) / 2.75, 0, 39); double tt = (x - poly[i].x) / 2.75; return poly[i].y + tt * (poly[i + 1].y - poly[i].y); }
        Assert.All(Centroids(r.LeftVerts, r.LeftTris), c => Assert.True(c.y > PolyY(c.x) - 1e-6));
        Assert.All(Centroids(r.RightVerts, r.RightTris), c => Assert.True(c.y < PolyY(c.x) + 1e-6));
    }

    /// <summary>闭合多段线作边界：左手侧(CCW 时=圈内)恰是圈内面积，切口顶点焊接后两片各自开放边只在切口与外缘。</summary>
    [Fact]
    public void ClosedBoundary_leftIsInside_areaExact()
    {
        var (v, t) = GridTin(10, 100);
        var ring = new[] { (23.0, 27.0), (71.0, 22.0), (66.0, 78.0), (31.0, 64.0), (23.0, 27.0) };   // CCW, 闭合
        var r = MeshPolylineSplit.Split(v, t, ring);
        Assert.True(r.Success, r.Error);
        double ringArea = 0;
        for (int i = 0; i + 1 < ring.Length; i++) ringArea += ring[i].Item1 * ring[i + 1].Item2 - ring[i + 1].Item1 * ring[i].Item2;
        ringArea = Math.Abs(ringArea) / 2;
        Assert.InRange(Area2D(r.LeftVerts, r.LeftTris), ringArea - 1e-6, ringArea + 1e-6);
        Assert.InRange(Area2D(r.RightVerts, r.RightTris), 10000 - ringArea - 1e-6, 10000 - ringArea + 1e-6);
        Assert.All(Centroids(r.LeftVerts, r.LeftTris), c => Assert.True(LineMath.PointInPolygon(c.x, c.y, ring)));
        Assert.All(Centroids(r.RightVerts, r.RightTris), c => Assert.False(LineMath.PointInPolygon(c.x, c.y, ring)));
        // 左片(圈内)是一张连通面：开放边恰好一条环
        var d = MeshDiagnose.Analyze(r.LeftVerts, r.LeftTris);
        Assert.Equal(0, d.NonManifoldEdges);
        Assert.Equal(1, d.BoundaryLoops);
    }

    /// <summary>闭合体(立方体)沿线切：两片各自封盖后仍是闭合体，体积守恒。</summary>
    [Fact]
    public void ClosedSolid_cutIsCapped_bothHalvesWatertight()
    {
        var (v, t) = PrimitiveBox(0, 0, 0, 100, 100, 40);
        var r = MeshPolylineSplit.Split(v, t, new[] { (-10.0, 30.0), (60.0, 30.0), (60.0, 110.0) });   // L 形切一刀
        Assert.True(r.Success, r.Error);
        Assert.True(r.HasLeft && r.HasRight);
        Assert.True(r.CapLoops >= 1, "闭合体切口应封盖");
        var dl = MeshDiagnose.Analyze(r.LeftVerts, r.LeftTris); var dr = MeshDiagnose.Analyze(r.RightVerts, r.RightTris);
        Assert.True(dl.IsClosed, $"左片未闭合: 开放边 {dl.BoundaryEdges}");
        Assert.True(dr.IsClosed, $"右片未闭合: 开放边 {dr.BoundaryEdges}");
        double vol = Math.Abs(MeshOrient.SignedVolume6(v, MeshOrient.MakeConsistent(v, t))) / 6;
        double vl = Math.Abs(MeshOrient.SignedVolume6(r.LeftVerts, MeshOrient.MakeConsistent(r.LeftVerts, r.LeftTris))) / 6;
        double vr = Math.Abs(MeshOrient.SignedVolume6(r.RightVerts, MeshOrient.MakeConsistent(r.RightVerts, r.RightTris))) / 6;
        Assert.InRange(vl + vr, vol * (1 - 1e-6), vol * (1 + 1e-6));
        Assert.InRange(vl, 60 * 70 * 40 * (1 - 1e-6), 60 * 70 * 40 * (1 + 1e-6));   // 左手侧 = 折线上方且左侧的那块
    }

    [Fact]
    public void Degenerate_inputs_reportError()
    {
        var (v, t) = GridTin(2, 10);
        Assert.False(MeshPolylineSplit.Split(v, t, new[] { (1.0, 1.0) }).Success);
        Assert.False(MeshPolylineSplit.Split(v, t, new[] { (1.0, 1.0), (1.0, 1.0) }).Success);
        Assert.False(MeshPolylineSplit.Split(new List<(double, double, double)>(), new List<(int, int, int)>(), new[] { (0.0, 0.0), (1.0, 1.0) }).Success);
    }

    // 轴对齐立方体：12 面，索引化、绕向一致外向
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) PrimitiveBox(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var v = new List<(double x, double y, double z)>
        {
            (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
            (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1),
        };
        var t = new List<(int a, int b, int c)>
        {
            (0, 2, 1), (0, 3, 2),   // 底(朝下)
            (4, 5, 6), (4, 6, 7),   // 顶
            (0, 1, 5), (0, 5, 4),   // 前 y0
            (1, 2, 6), (1, 6, 5),   // 右 x1
            (2, 3, 7), (2, 7, 6),   // 后 y1
            (3, 0, 4), (3, 4, 7),   // 左 x0
        };
        return (v, t);
    }
}

/// <summary>性能护栏：百万三角 + 200 段折线，逐顶点定侧走段网格、逐三角只查包围盒内的段，应在秒级。</summary>
public class MeshPolylineSplitBench
{
    private readonly Xunit.Abstractions.ITestOutputHelper _o;
    public MeshPolylineSplitBench(Xunit.Abstractions.ITestOutputHelper o) { _o = o; }

    [Fact]
    public void Split_1M_tris_200segPolyline_staysUnderSeconds()
    {
        int n = 800;
        var v = new List<(double x, double y, double z)>((n + 1) * (n + 1)); var t = new List<(int a, int b, int c)>(n * n * 2);
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add((500000 + i * 5.0, 4000000 + j * 5.0, 100 + Math.Sin(i * 0.1) * 10));
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) { int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1; t.Add((a, b, d)); t.Add((a, d, c)); }
        var poly = new List<(double x, double y)>();
        for (int k = 0; k <= 200; k++) poly.Add((500000 - 50 + k * 20.5, 4000000 + 2000 + 600 * Math.Sin(k * 0.15)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MeshPolylineSplit.Split(v, t, poly);
        sw.Stop();
        _o.WriteLine($"{t.Count} 三角 / {poly.Count - 1} 段: {sw.ElapsedMilliseconds} ms · {r.Log} · 左 {r.LeftTris.Count} 右 {r.RightTris.Count}");
        Assert.True(r.Success && r.HasLeft && r.HasRight);
        Assert.True(sw.ElapsedMilliseconds < 20000, $"沿线分割百万三角用了 {sw.ElapsedMilliseconds} ms");
    }
}

/// <summary>
/// 随机不规则 TIN × 折线（含矿区量级大坐标）：两片里每个三角内部随机采样点，用独立实现判侧，必须与所在片一致。
/// 抓的是用户实机看到的锯齿/尖刺/整块跑错侧：原版用 1e-12 绝对阈值判叉积"在线上"，切口顶点有 ~1e-16×坐标² 的
/// 舍入误差，坐标几百起线上点就被随机判成左/右，子三角顶点符号多数派跟着乱。现按 1e-9×坐标量级判在线。
/// </summary>
public class MeshPolylineSplitRandomTests
{
    // 独立判侧：暴力最近段，共享顶点处取 |单位叉积| 大者
    private static (int side, double dist) SideOf(List<(double x, double y)> poly, double px, double py)
    {
        int best = -1; double bd2 = double.MaxValue;
        double D2(int i, out double t)
        {
            double ax = poly[i].x, ay = poly[i].y, sx = poly[i + 1].x - ax, sy = poly[i + 1].y - ay, l2 = sx * sx + sy * sy;
            t = Math.Clamp(((px - ax) * sx + (py - ay) * sy) / l2, 0, 1);
            double dx = px - (ax + t * sx), dy = py - (ay + t * sy); return dx * dx + dy * dy;
        }
        for (int i = 0; i + 1 < poly.Count; i++) { double d2 = D2(i, out _); if (d2 < bd2) { bd2 = d2; best = i; } }
        double UC(int i) { double ax = poly[i].x, ay = poly[i].y, sx = poly[i + 1].x - ax, sy = poly[i + 1].y - ay; return (sx * (py - ay) - sy * (px - ax)) / Math.Sqrt(sx * sx + sy * sy); }
        int pick = best; double bc = Math.Abs(UC(best));
        foreach (int j in new[] { best - 1, best + 1 })
            if (j >= 0 && j + 1 < poly.Count && D2(j, out _) <= bd2 * (1 + 1e-9) + 1e-18 && Math.Abs(UC(j)) > bc) { bc = Math.Abs(UC(j)); pick = j; }
        double c = UC(pick);
        return (c > 0 ? 1 : c < 0 ? -1 : 0, Math.Sqrt(bd2));
    }

    [Theory]
    [InlineData(0.0, 0.0, 7)]
    [InlineData(500000.0, 4000000.0, 11)]
    public void RandomTin_bentPolyline_everyTriangleOnItsOwnSide(double ox, double oy, int seed)
    {
        var rnd = new Random(seed);
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < 3000; i++) pts.Add((rnd.NextDouble() * 1000, rnd.NextDouble() * 1000));
        var tris = Delaunay.Triangulate(pts);
        var v = pts.Select(p => (ox + p.x, oy + p.y, 100 + 20 * Math.Sin(p.x / 90) * Math.Cos(p.y / 70))).ToList();
        double meshArea = tris.Sum(f => Math.Abs((pts[f.b].x - pts[f.a].x) * (pts[f.c].y - pts[f.a].y) - (pts[f.b].y - pts[f.a].y) * (pts[f.c].x - pts[f.a].x)) / 2);
        int totalBad = 0;
        for (int trial = 0; trial < 12; trial++)
        {
            var poly = new List<(double x, double y)> { (ox + rnd.NextDouble() * 1000, oy - 100) };
            for (int k = 0; k < 3; k++) poly.Add((ox + rnd.NextDouble() * 1000, oy + 200 + k * 300 + rnd.NextDouble() * 100));
            poly.Add((ox + rnd.NextDouble() * 1000, oy + 1100));
            var r = MeshPolylineSplit.Split(v, tris, poly);
            Assert.True(r.Success, r.Error);
            Assert.True(r.HasLeft && r.HasRight);
            double A(List<(double x, double y, double z)> vv, List<(int a, int b, int c)> tt) => tt.Sum(f => Math.Abs((vv[f.b].x - vv[f.a].x) * (vv[f.c].y - vv[f.a].y) - (vv[f.b].y - vv[f.a].y) * (vv[f.c].x - vv[f.a].x)) / 2);
            Assert.InRange(A(r.LeftVerts, r.LeftTris) + A(r.RightVerts, r.RightTris), meshArea * (1 - 1e-6), meshArea * (1 + 1e-6));
            foreach (var (vv, tt, want) in new[] { (r.LeftVerts, r.LeftTris, 1), (r.RightVerts, r.RightTris, -1) })
                foreach (var f in tt)
                {
                    var a = vv[f.a]; var b = vv[f.b]; var c = vv[f.c];
                    if (Math.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2 < 1e-6) continue;
                    for (int s = 0; s < 8; s++)
                    {
                        double u = s == 0 ? 1.0 / 3 : rnd.NextDouble(), w = s == 0 ? 1.0 / 3 : rnd.NextDouble() * (1 - u);
                        var (side, dist) = SideOf(poly, a.x + u * (b.x - a.x) + w * (c.x - a.x), a.y + u * (b.y - a.y) + w * (c.y - a.y));
                        if (dist < 1e-3 || side == 0) continue;   // 贴线 1 mm 内不计
                        if (side != want) { totalBad++; break; }
                    }
                }
        }
        Assert.Equal(0, totalBad);
    }
}
