using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Views.Modeling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「三维地质建模 → 建模/编辑/工具」组(原 MeshEditLib 窗口)的纯逻辑回归：
/// 地质体建模(QuickModelSampler 采样/直接组装) · 格网质量检测定位(MeshDiagnoseMarkers) · 两期算量封闭体(CutFillSolids) ·
/// 展点解析 · 构建等值线(ContourEngine) · 创建剖面/动态剖面(SectionEngine/SectionBuilder) · 剖面钻孔投影(种子库)。
/// </summary>
[Collection("TextGeometry")]
public class ModelingMeshEditTests
{
    // ── 夹具 ──────────────────────────────────────────────────────
    private static (double[] v, int[] t) Quad(double z, double size = 10)
        => (new[] { 0, 0, z, size, 0, z, size, size, z, 0, size, z }, new[] { 0, 1, 2, 0, 2, 3 });

    /// <summary>规则网格面 z = f(x,y), [0,size]², n×n 格。</summary>
    private static (double[] v, int[] t) GridSurface(Func<double, double, double> f, double size = 100, int n = 10)
    {
        var v = new List<double>(); var t = new List<int>();
        double s = size / n;
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) { double x = i * s, y = j * s; v.Add(x); v.Add(y); v.Add(f(x, y)); }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            t.AddRange(new[] { a, b, d, a, d, c });
        }
        return (v.ToArray(), t.ToArray());
    }

    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Tuples(double[] v, int[] t)
    {
        var lv = new List<(double, double, double)>(); var lt = new List<(int, int, int)>();
        for (int i = 0; i + 2 < v.Length; i += 3) lv.Add((v[i], v[i + 1], v[i + 2]));
        for (int i = 0; i + 2 < t.Length; i += 3) lt.Add((t[i], t[i + 1], t[i + 2]));
        return (lv, lt);
    }

    // ═══════ 地质体建模 ═══════
    [Fact]
    public void Direct_assembly_of_two_quads_is_watertight_box()
    {
        var top = Quad(5); var bot = Quad(0);
        var r = QuickModelSampler.BuildFromMeshesDirect(top.v, top.t, bot.v, bot.t, false, "地质体·层1");
        Assert.True(r.Ok, r.Error);
        Assert.True(r.Watertight);
        Assert.Equal(0, r.OpenEdges); Assert.Equal(0, r.NonManifoldEdges);
        Assert.Equal(12, r.TotalTris);                 // 顶2+底2+4侧壁×2
        Assert.Equal(1, r.WallLoops); Assert.Equal(0, r.CappedLoops); Assert.Equal(0, r.OpenLoops);
        Assert.Equal("地质体·层1", r.LayerName);
        Assert.Equal(5, r.TopMeanZ, 6); Assert.Equal(0, r.BotMeanZ, 6);
        var m = MeshMetrics.Compute(r.Verts, r.Tris);
        Assert.Equal(0, m.MinZ, 6); Assert.Equal(5, m.MaxZ, 6);
    }

    [Fact]
    public void Direct_assembly_caps_unpaired_inner_hole_and_stays_watertight()
    {
        // 顶面: 100×100 网去掉中心一格(nodata 洞) → 内洞环无底面配对 → 封盖; 底面完整
        var top = GridSurface((x, y) => 20, 100, 10);
        var tris = new List<int>();
        for (int k = 0; k + 2 < top.t.Length; k += 3)
        {
            double cx = (top.v[top.t[k] * 3] + top.v[top.t[k + 1] * 3] + top.v[top.t[k + 2] * 3]) / 3;
            double cy = (top.v[top.t[k] * 3 + 1] + top.v[top.t[k + 1] * 3 + 1] + top.v[top.t[k + 2] * 3 + 1]) / 3;
            if (cx > 40 && cx < 50 && cy > 40 && cy < 50) continue;
            tris.Add(top.t[k]); tris.Add(top.t[k + 1]); tris.Add(top.t[k + 2]);
        }
        var bot = GridSurface((x, y) => 0, 100, 10);
        var r = QuickModelSampler.BuildFromMeshesDirect(top.v, tris.ToArray(), bot.v, bot.t, false);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.TopLoops); Assert.Equal(1, r.BotLoops);
        Assert.Equal(1, r.WallLoops);
        Assert.Equal(1, r.CappedLoops);
        Assert.Equal(0, r.OpenLoops);
        Assert.True(r.Watertight, $"open {r.OpenEdges} nonmanifold {r.NonManifoldEdges}");
        var d = MeshDiagnose.Analyze(r.Verts, r.Tris);
        Assert.True(d.IsClosed);
    }

    [Fact]
    public void Direct_assembly_rejects_degenerate_inputs()
    {
        var r = QuickModelSampler.BuildFromMeshesDirect(new double[3], new int[1], Quad(0).v, Quad(0).t, false);
        Assert.False(r.Ok); Assert.Contains("退化", r.Error);
        // 闭合体(无边界环)不能放样
        var box = QuickModelSampler.BuildFromMeshesDirect(Quad(5).v, Quad(5).t, Quad(0).v, Quad(0).t, false);
        var r2 = QuickModelSampler.BuildFromMeshesDirect(FlatV(box.Verts), FlatT(box.Tris), Quad(0).v, Quad(0).t, false);
        Assert.False(r2.Ok); Assert.Contains("边界环", r2.Error);
    }

    private static double[] FlatV(List<(double x, double y, double z)> v) => v.SelectMany(p => new[] { p.x, p.y, p.z }).ToArray();
    private static int[] FlatT(List<(int a, int b, int c)> t) => t.SelectMany(p => new[] { p.a, p.b, p.c }).ToArray();

    private static double[] Ring(double z, double r0, double r1, int n = 24)
    {
        var l = new List<double>();
        for (int i = 0; i < n; i++) { double a = 2 * Math.PI * i / n; l.Add(50 + r0 * Math.Cos(a)); l.Add(50 + r1 * Math.Sin(a)); l.Add(z); }
        return l.ToArray();
    }

    [Theory]
    [InlineData(QuickModelSampler.Interp.Idw)]
    [InlineData(QuickModelSampler.Interp.Kriging)]
    public void Sampling_build_from_contours_makes_watertight_solid_with_expected_volume(QuickModelSampler.Interp method)
    {
        // 顶板等高线均在 z=10, 底板均在 z=0, 闭合边界 [0,100]² → 插值恒定 → 体积 ≈ 100×100×10
        var topC = new[] { Ring(10, 20, 20), Ring(10, 40, 40) };
        var botC = new[] { Ring(0, 20, 20), Ring(0, 40, 40) };
        var bnd = new double[] { 0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0, 0, 0, 0 };
        var r = QuickModelSampler.Build(topC, botC, bnd, method, 10, false);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.TopContours); Assert.Equal(48, r.TopSamples);
        Assert.Equal(0, r.OpenEdges);
        Assert.True(r.SurfTris >= 200);                // 10×10 格×2 三角(含裁剪)
        Assert.Equal(r.SurfTris * 2 + r.WallTris, r.TotalTris);
        var oriented = MeshOrient.MakeConsistent(r.Verts, r.Tris);
        double vol = MeshMetrics.RobustVolume(r.Verts, oriented);
        Assert.InRange(vol, 100000 * 0.98, 100000 * 1.02);
        var m = MeshMetrics.Compute(r.Verts, r.Tris);
        Assert.Equal(0, m.MinZ, 3); Assert.Equal(10, m.MaxZ, 3);
    }

    [Fact]
    public void Sampling_build_reports_insufficient_samples()
    {
        var r = QuickModelSampler.Build(new[] { new double[] { 0, 0, 1 } }, new[] { Ring(0, 10, 10) }, Ring(0, 50, 50), QuickModelSampler.Interp.Idw, 5, false);
        Assert.False(r.Ok); Assert.Contains("顶点不足", r.Error);
    }

    // ═══════ 格网质量检测·问题定位 ═══════
    [Fact]
    public void Markers_match_diagnose_counts_for_each_category()
    {
        // 开放方块 + 退化三角 + 非流形边(第 3 个三角共用边 0-2) + 孤立点(4) + 重复点(5=复制 0)
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0), (50, 50, 50), (0, 0, 0), (5, 5, 8) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3), (1, 1, 2), (0, 2, 6) };
        var r = MeshDiagnoseMarkers.Collect(v, t);
        var d = MeshDiagnose.Analyze(v, t);
        Assert.Equal(d.BoundaryEdges, r.OpenEdges.Count);
        Assert.Equal(d.NonManifoldEdges, r.NonManifoldEdges.Count);
        Assert.Equal(d.DegenerateTriangles, r.DegenerateTris.Count);
        Assert.Equal(d.IsolatedVertices, r.IsolatedVerts.Count);
        Assert.Equal(d.DuplicateVertices, r.DuplicateVerts.Count);
        Assert.Contains((0, 2), r.NonManifoldEdges);
        Assert.Contains(2, r.DegenerateTris);
        Assert.Contains(4, r.IsolatedVerts);
        Assert.Contains(5, r.DuplicateVerts);
        Assert.Equal(1, r.CountOf(1)); Assert.Equal(0, r.CountOf(3));
    }

    [Fact]
    public void Markers_detect_self_intersecting_triangles_and_build_entities()
    {
        // 两个互穿三角: 水平三角 z=0 与竖直三角穿过其内部
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (0, 10, 0), (2, 2, -5), (4, 2, 5), (2, 4, 5) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (3, 4, 5) };
        var r = MeshDiagnoseMarkers.Collect(v, t);
        Assert.Equal(new[] { 0, 1 }, r.SelfIntersectTris);
        Assert.Equal(MeshDiagnose.Analyze(v, t).SelfIntersectTriangles, r.SelfIntersectTris.Count);
        var ents = MeshDiagnoseMarkers.Entities(v, t, r, 3, (1, 0, 0), 1);
        Assert.Equal(2, ents.Count);
        Assert.All(ents, e => { Assert.Equal(MeshDiagnoseMarkers.MarkerLayer, e.LayerName); Assert.True(((PolylineEntity)e).Closed); });
        var edges = MeshDiagnoseMarkers.Entities(v, t, r, 0, (1, 0, 0), 1);
        Assert.Equal(6, edges.Count);                  // 两个独立三角各 3 条开放边
        Assert.True(((PolylineEntity)edges[0]).Has3D);
    }

    // ═══════ 两期三角网算量·封闭体 ═══════
    [Fact]
    public void CutFill_solids_single_fill_body_volume_and_watertight()
    {
        var a = GridSurface((x, y) => 0, 100, 10);
        var b = GridSurface((x, y) => 5, 100, 10);   // 后期整体高 5 → 全填方
        var o = new CutFillSolids.Options { GridCell = 5, RenderCell = 10, MinDz = 1, OpenRadius = 1, MinBenchH = 3 };
        var r = CutFillSolids.Compute(a.v, a.t, b.v, b.t, o);
        Assert.Equal("", r.Error);
        Assert.Single(r.Bodies);
        var body = r.Bodies[0];
        Assert.True(body.IsFill); Assert.Equal(1, body.Index);
        Assert.InRange(body.VolumeM3, 50000 * 0.9, 50000 * 1.02);   // 100×100×5(重叠范围采样边缘略缺)
        Assert.Equal(5, body.MaxDz, 6);
        Assert.Equal(r.FillM3, body.VolumeM3, 6); Assert.Equal(0, r.CutM3, 6);
        Assert.InRange(r.Raw.FillM3, 50000 * 0.9, 50000 * 1.02);
        var d = MeshDiagnose.Analyze(body.Verts, body.Tris);
        Assert.True(d.IsClosed, $"open {d.BoundaryEdges} nm {d.NonManifoldEdges}");
        var m = MeshMetrics.Compute(body.Verts, body.Tris);
        Assert.Equal(0, m.MinZ, 6); Assert.Equal(5, m.MaxZ, 6);
        Assert.InRange(MeshMetrics.RobustVolume(body.Verts, MeshOrient.MakeConsistent(body.Verts, body.Tris)), 50000 * 0.9, 50000 * 1.02);
    }

    [Fact]
    public void CutFill_solids_split_fill_and_cut_and_filter_low_bench()
    {
        var a = GridSurface((x, y) => 0, 100, 20);
        var b = GridSurface((x, y) => x < 50 ? 6 : -6, 100, 20);   // 西半填 / 东半挖
        var o = new CutFillSolids.Options { GridCell = 5, RenderCell = 5, MinDz = 1, OpenRadius = 0, MinBenchH = 3 };
        var r = CutFillSolids.Compute(a.v, a.t, b.v, b.t, o);
        Assert.Equal(2, r.Bodies.Count);
        Assert.Single(r.Bodies, x => x.IsFill); Assert.Single(r.Bodies, x => !x.IsFill);
        Assert.True(r.FillM3 > 0 && r.CutM3 > 0);
        Assert.InRange(r.NetM3, -0.15 * r.FillM3, 0.15 * r.FillM3);
        // 最小台阶高过滤: 高差 2 < 3 → 整块丢弃
        var low = GridSurface((x, y) => 2, 100, 10);
        var r2 = CutFillSolids.Compute(a.v, a.t, low.v, low.t, o);
        Assert.Empty(r2.Bodies); Assert.Equal(1, r2.DroppedLowBench); Assert.Equal(0, r2.FillM3, 6);
        Assert.True(r2.Raw.FillM3 > 0);
        // 无重叠
        var far = GridSurface((x, y) => 0, 100, 10);
        for (int i = 0; i < far.v.Length; i += 3) far.v[i] += 1000;
        Assert.NotEqual("", CutFillSolids.Compute(a.v, a.t, far.v, far.t, o).Error);
    }

    [Fact]
    public void VolumeSplit_validation_and_hex_and_csv()
    {
        Assert.Null(VolumeSplitDialog.Validate("2", "6", "1", "1", "3", out var gc, out var rc, out var dz, out var orad, out var bh));
        Assert.Equal(2, gc); Assert.Equal(6, rc); Assert.Equal(1, dz); Assert.Equal(1, orad); Assert.Equal(3, bh);
        Assert.Contains("格网分辨率", VolumeSplitDialog.Validate("0", "6", "1", "1", "3", out _, out _, out _, out _, out _));
        Assert.Contains("去噪半径", VolumeSplitDialog.Validate("2", "6", "1", "-1", "3", out _, out _, out _, out _, out _));
        Assert.Contains("最小台阶高", VolumeSplitDialog.Validate("2", "6", "1", "1", "x", out _, out _, out _, out _, out _));
        Assert.True(VolumeSplitDialog.TryParseHex("#0078FF", out var r, out var g, out var b));
        Assert.Equal((0, 120, 255), ((int)r, (int)g, (int)b));
        Assert.False(VolumeSplitDialog.TryParseHex("zz", out _, out _, out _));
        var a = GridSurface((x, y) => 0, 100, 10); var top = GridSurface((x, y) => 5, 100, 10);
        var res = CutFillSolids.Compute(a.v, a.t, top.v, top.t, new CutFillSolids.Options { GridCell = 5 });
        var csv = VolumeSplitDialog.ToCsv(res, "A", "B");
        Assert.Contains("填方,1,", csv); Assert.Contains("第一期,A", csv);
    }

    // ═══════ 展点 ═══════
    [Fact]
    public void ShowPoints_parse_validates_rows_and_marker_size()
    {
        var rows = ShowPointsWindow.ParseLines(new[] { "# 注释", "", "1,2,3", "4\t5\t6", "x 7 8", "9;10" });
        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].Valid); Assert.Equal("✓ 坐标", rows[0].Status); Assert.Equal((1.0, 2.0, 3.0), (rows[0].Xv, rows[0].Yv, rows[0].Zv));
        Assert.True(rows[1].Valid);
        Assert.False(rows[2].Valid); Assert.Equal("✗ X 非数值", rows[2].Status);
        Assert.False(rows[3].Valid); Assert.Equal("✗ Z 非数值", rows[3].Status);
        Assert.Equal(4, rows[3].Index);
        Assert.Equal(5.0, ShowPointsWindow.ComputeMarkerSize(new List<double> { 1, 1, 1 }));
        Assert.Equal(4.0, ShowPointsWindow.ComputeMarkerSize(new List<double> { 0, 0, 0, 1000, 0, 0 }), 6);
        Assert.Equal(1.0, ShowPointsWindow.ComputeMarkerSize(new List<double> { 0, 0, 0, 10, 0, 0 }), 6);   // 下限 1
    }

    // ═══════ 构建等值线 ═══════
    [Fact]
    public void Contour_engine_slope_plane_levels_index_lines_and_lengths()
    {
        var g = GridSurface((x, y) => x, 100, 10);   // z = x → 等值线为竖直直线, 长 100
        var opt = new ContourOptions { Interval = 10, BaseLevel = 0, IndexEvery = 5, SimplifyTolerance = 0.2, MinLengthMeters = 10 };
        var lines = ContourEngine.Build(new[] { (g.v, g.t) }, opt, out string warn);
        Assert.Equal("", warn);
        // 数据值域 0..100, 顶点恰在等值面上时微移 → 0 与 100 处于边界; 内部 10..90 稳定
        var levels = lines.Select(l => l.Level).Distinct().OrderBy(v => v).ToList();
        for (double lv = 10; lv <= 90; lv += 10) Assert.Contains(lv, levels);
        foreach (var l in lines.Where(l => l.Level >= 10 && l.Level <= 90))
        {
            Assert.InRange(l.Length2D, 99.9, 100.1);
            Assert.False(l.Closed);
            for (int i = 0; i + 2 < l.Xyz.Count; i += 3) { Assert.Equal(l.Level, l.Xyz[i], 6); Assert.Equal(l.Level, l.Xyz[i + 2], 6); }
        }
        Assert.All(lines.Where(l => l.Level == 50), l => Assert.True(l.IsIndex));
        Assert.All(lines.Where(l => l.Level == 20), l => Assert.False(l.IsIndex));
    }

    [Fact]
    public void Contour_engine_explicit_levels_range_and_smoothing()
    {
        var g = GridSurface((x, y) => x, 100, 10);
        var opt = new ContourOptions { ExplicitLevels = new[] { 25.0, 75.0, 500.0 }, SimplifyTolerance = 0, MinLengthMeters = 1 };
        var lines = ContourEngine.Build(new[] { (g.v, g.t) }, opt, out string warn);
        Assert.Contains("1 个落在数据值域外", warn);
        Assert.Equal(new[] { 25.0, 75.0 }, lines.Select(l => l.Level).Distinct().OrderBy(v => v));
        // 值域裁剪
        var opt2 = new ContourOptions { Interval = 10, RangeMin = 30, RangeMax = 60, MinLengthMeters = 1 };
        var lines2 = ContourEngine.Build(new[] { (g.v, g.t) }, opt2, out _);
        Assert.All(lines2, l => Assert.InRange(l.Level, 30, 60));
        // Chaikin 不越出走廊、点数翻倍; 样条自交回退
        var zig = new List<double> { 0, 0, 5, 10, 10, 5, 20, 0, 5, 30, 10, 5 };
        var ch = ContourEngine.Chaikin(zig, false, 1);
        Assert.Equal(zig.Count / 3 * 2 - 2 + 2, ch.Count / 3);
        Assert.All(Enumerable.Range(0, ch.Count / 3), i => Assert.InRange(ch[i * 3 + 1], 0, 10));
        var cr = ContourEngine.CatmullRom(zig, false, 2);
        Assert.True(cr.Count > zig.Count);
        Assert.False(ContourEngine.SelfIntersects(zig, false));
        var bow = new List<double> { 0, 0, 0, 10, 10, 0, 10, 0, 0, 0, 10, 0 };
        Assert.True(ContourEngine.SelfIntersects(bow, false));
    }

    [Fact]
    public void Contour_densify_triangulate_and_entities()
    {
        var scatter = new List<double>();
        ContourEngine.DensifyInto(scatter, new double[] { 0, 0, 0, 10, 0, 0 }, false, 2.5);
        Assert.Equal(5, scatter.Count / 3);          // 首点 + 4 段
        var closedS = new List<double>();
        ContourEngine.DensifyInto(closedS, new double[] { 0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0 }, true, 0);
        Assert.Equal(4, closedS.Count / 3);          // 闭环末点=首点不重复
        // 散点构网 + 长边剔除
        var pts = new List<double> { 0, 0, 0, 10, 0, 0, 0, 10, 0, 10, 10, 0, 200, 200, 0, 0, 0, 0 };
        var tris = ContourEngine.TriangulateScatter(pts, 50, out int used);
        Assert.Equal(5, used);                       // 重复点去重
        Assert.Equal(2, tris.Length / 3);            // 远点三角被长边剔除
        var trisAll = ContourEngine.TriangulateScatter(pts, 0, out _);
        Assert.True(trisAll.Length / 3 > 2);
        // 实体: 双色 + 标注(仅计曲线)
        var g = GridSurface((x, y) => x, 100, 4);
        var opt = new ContourOptions { Interval = 10, IndexEvery = 5, MinLengthMeters = 1 };
        var lines = ContourEngine.Build(new[] { (g.v, g.t) }, opt, out _);
        var ents = ContourEngine.BuildEntities(lines, "等值线", "等值线标注", 0, opt, true, true, 2, 30);
        var pls = ents.OfType<PolylineEntity>().ToList();
        Assert.Equal(lines.Count, pls.Count);
        Assert.All(pls, p => { Assert.True(p.Has3D); Assert.Equal("等值线", p.LayerName); });
        var texts = ents.OfType<TextEntity>().ToList();
        Assert.True(texts.Count > 0);
        Assert.All(texts, t => { Assert.Equal("等值线标注", t.LayerName); Assert.Contains(t.Text, new[] { "0", "50", "100" }); });   // 仅计曲线(0/50/100)有标注
        Assert.Contains(texts, t => t.Text == "50");
        var idx = pls.First(p => Math.Abs(p.Zs![0] - 50) < 1e-6); var normal = pls.First(p => Math.Abs(p.Zs![0] - 20) < 1e-6);
        Assert.NotEqual(idx.Cr, normal.Cr);
        Assert.Equal(3, ContourEngine.LabelStops(lines.First(l => l.Level == 50), 2, 30).Count);   // 100m/30 → 15,45,75
        Assert.Equal(new[] { 1000.0, 1050, 1187.5 }, ContourBuilderWindow.ParseLevelList("1000, 1050；x 1187.5"));
        Assert.Equal((0f, 0f, 1f), ContourEngine.RampColor(0, 0, 100)); Assert.Equal((1f, 0f, 1f - 1f), ContourEngine.RampColor(100, 0, 100));
    }

    // ═══════ 创建剖面 / 动态剖面 ═══════
    [Fact]
    public void Section_engine_cuts_slope_plane_into_monotonic_chain()
    {
        var g = GridSurface((x, y) => x, 100, 10);
        var section = new double[] { 0, 50, 0, 100, 50, 0 };
        var chains = SectionEngine.Build(new[] { (g.v, g.t) }, section, out double len, out string warn);
        Assert.Equal(100, len, 6); Assert.Equal("", warn);
        Assert.Single(chains);
        var c = chains[0];
        Assert.Equal(0, c.MeshIndex);
        Assert.True(c.Sz.Count / 2 >= 11);
        for (int i = 0; i + 1 < c.Sz.Count; i += 2) { Assert.Equal(c.Sz[i], c.Sz[i + 1], 6); Assert.Equal(50, c.Xyz[i / 2 * 3 + 1], 6); }
        Assert.InRange(c.Sz[0], -1e-6, 1e-6); Assert.InRange(c.Sz[^2], 100 - 1e-6, 100 + 1e-6);
        // 折线剖面: 两段, 里程累计
        var poly = new double[] { 0, 50, 0, 50, 50, 0, 50, 0, 0 };
        var ch2 = SectionEngine.Build(new[] { (g.v, g.t) }, poly, out double len2, out _);
        Assert.Equal(100, len2, 6);
        Assert.True(ch2.Count >= 2);
        Assert.Equal(2, SectionEngine.BuildSegmentTable(poly, out _).Count);
        // 不相交
        Assert.Empty(SectionEngine.Build(new[] { (g.v, g.t) }, new double[] { 500, 500, 0, 600, 500, 0 }, out _, out _));
    }

    [Fact]
    public void Section_column_thickness_and_dynamic_offset()
    {
        var a = GridSurface((x, y) => 20, 100, 5); var b = GridSurface((x, y) => 12, 100, 5);
        var section = DynamicSectionWindow.SectionAtOffset(0, 40, 100, 40, 10);   // 左法向 +y → y=50
        Assert.Equal(50, section[1], 6); Assert.Equal(50, section[4], 6);
        var chains = SectionEngine.Build(new[] { (a.v, a.t), (b.v, b.t) }, section, out _, out _);
        var col = SectionEngine.ColumnAt(chains, 37);
        Assert.Equal(2, col.Count);
        Assert.Equal(20, col[0].z, 6); Assert.Equal(0, col[0].mi);
        Assert.Equal(12, col[1].z, 6); Assert.Equal(1, col[1].mi);
        string text = DynamicSectionWindow.Readout(37, col, new[] { "顶板", "底板" });
        Assert.Contains("顶板 20", text); Assert.Contains("↕8m", text); Assert.Contains("底板 12", text);
        Assert.Contains("无层位", DynamicSectionWindow.Readout(1, new List<(double, int)>(), Array.Empty<string>()));
    }

    [Fact]
    public void Section_builder_entities_frame_labels_boreholes_and_csv()
    {
        var g = GridSurface((x, y) => 100 + x * 0.2, 100, 5);
        var section = new double[] { 0, 50, 0, 100, 50, 0 };
        var chains = SectionEngine.Build(new[] { (g.v, g.t) }, section, out double len, out _);
        var o = new SectionBuilder.Options { Vex = 2, GridZ = 10, GridS = 50, TextH = 2, SecName = "A" };
        var segs = new List<SectionBuilder.BoreSeg> { new(0, 10, "砂岩", null, "sand"), new(10, 15, "煤", null, "煤层") };
        var bores = new List<SectionBuilder.BoreProj> { new("ZK1", 40, 5, 130, 20, segs) };
        var ents = SectionBuilder.BuildEntities(chains, len, section, o, bores, out double bx, out double by);
        Assert.Equal(0, bx, 6);
        Assert.True(by < 50);                                   // 自动基点在剖面线起点南侧
        var l3 = ents.Where(e => e.LayerName == "剖面交线").ToList();
        Assert.Single(l3); Assert.True(((PolylineEntity)l3[0]).Has3D);
        var prof = ents.Where(e => e.LayerName == "剖面图").ToList();
        Assert.Contains(prof, e => e is PolylineEntity p && p.Closed && p.Points.Count == 4);   // 外框
        var texts = prof.OfType<TextEntity>().ToList();
        Assert.Contains(texts, t => t.Text == "A-A′ 剖面图（垂直×2）");
        Assert.Contains(texts, t => t.Text == "A′");
        Assert.Contains(texts, t => t.Text == "0"); Assert.Contains(texts, t => t.Text == "100");   // 里程刻度含终点
        Assert.Contains(texts, t => t.Text == "ZK1 偏5m");
        Assert.Equal(2, prof.OfType<RectEntity>().Count());   // 两个分层色块
        // 分层配色: 煤层深灰, 岩性稳定淡色
        Assert.Equal(((byte)45, (byte)45, (byte)45), SectionBuilder.SegColor(segs[1]));
        Assert.Equal(((byte)0x12, (byte)0x34, (byte)0x56), SectionBuilder.SegColor(new SectionBuilder.BoreSeg(0, 1, "x", "#123456", "")));
        var c1 = SectionBuilder.SegColor(segs[0]); Assert.InRange(c1.r, 150, 240);
        // 投影
        var st = SectionEngine.BuildSegmentTable(section, out _);
        var p = SectionBuilder.Project(st, 40, 55, 10); Assert.NotNull(p); Assert.Equal(40, p!.Value.s, 6); Assert.Equal(5, p.Value.offset, 6);
        Assert.Null(SectionBuilder.Project(st, 40, 80, 10));
        // CSV
        var csv = SectionBuilder.ToCsv(chains, new[] { "地表" });
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("面,链,里程", lines[0]);
        Assert.StartsWith("地表,1,", lines[1]);
        Assert.Equal(chains[0].Sz.Count / 2 + 1, lines.Length);
        // 仅交线
        var only3d = SectionBuilder.BuildEntities(chains, len, section, new SectionBuilder.Options { WantProfile = false }, null, out _, out _);
        Assert.All(only3d, e => Assert.Equal("剖面交线", e.LayerName));
    }

    [Fact]
    public void Section_boreholes_from_seeded_db_project_within_band()
    {
        using var db = GeoDatabase.OpenSeeded();
        var all = GeoDbViews.SectionBoreholesInBounds(db.Connection, -1e9, 1e9, -1e9, 1e9);
        Assert.True(all.Count > 100);
        var h = all.First(r => r.HoleId == "355");
        Assert.Equal(1333.64, h.ZCollar!.Value, 2);
        // 过孔 355 的东西向剖面线, 带宽 50 → 至少投影到该孔; 带宽 0 → 跳过
        var section = new[] { h.X - 200, h.Y + 10, 0, h.X + 200, h.Y + 10, 0 };
        var bores = GeoDbViews.CollectSectionBoreholes(db.Connection, section, 50, out string warn);
        var b355 = bores.Single(b => b.HoleId == "355");
        Assert.Equal(200, b355.S, 3); Assert.Equal(10, b355.Offset, 3);
        Assert.Equal(262.52, b355.Depth, 2);
        Assert.Equal(1333.64 - 262.52, b355.ZBottom, 2);
        Assert.Empty(GeoDbViews.CollectSectionBoreholes(db.Connection, section, 0, out _));
        Assert.Empty(GeoDbViews.CollectSectionBoreholes(null, section, 50, out _));
        Assert.Empty(GeoDbViews.SectionBoreholeSegments(db.Connection, h.Id));   // 种子库无岩性分层 → 柱状只画孔轴
        _ = warn;
    }
}
