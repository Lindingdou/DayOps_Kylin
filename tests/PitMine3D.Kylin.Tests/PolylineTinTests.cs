using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 由多段线建三角网（等值线建面）。此前「创建三角网」只收点实体，
/// 只选多段线会直接提示"需 ≥3 个点"——这里验线的顶点与约束边收集正确。
/// </summary>
public class PolylineTinTests
{
    private static PolylineTin.Line L(bool closed, double z, params (double x, double y)[] pts)
        => new() { Points = pts, FlatZ = z, Closed = closed };

    private static PolylineTin.Line L3(bool closed, (double x, double y, double z)[] pts)
        => new()
        {
            Points = pts.Select(p => (p.x, p.y)).ToList(),
            Z = pts.Select(p => p.z).ToList(),
            Closed = closed,
        };

    [Fact]
    public void OpenLine_takesEveryVertex_andEachSegmentIsAConstraint()
    {
        var r = PolylineTin.Collect(new[] { L(false, 100, (0, 0), (10, 0), (20, 0)) });
        Assert.Equal(3, r.Verts.Count);
        Assert.Equal(2, r.Constraints.Count);          // 3 点 2 段, 不闭合不补收尾段
        Assert.All(r.Verts, v => Assert.Equal(100, v.z, 9));
        Assert.Equal(0, r.MergedVertices);
    }

    [Fact]
    public void ClosedLine_getsTheWrapAroundConstraint()
    {
        var r = PolylineTin.Collect(new[] { L(true, 50, (0, 0), (10, 0), (10, 10)) });
        Assert.Equal(3, r.Verts.Count);
        Assert.Equal(3, r.Constraints.Count);          // 2 段 + 收尾段
        Assert.Contains((2, 0), r.Constraints);
    }

    [Fact]
    public void ClosedLine_thatAlreadyRepeatsFirstPoint_doesNotGetADuplicateConstraint()
    {
        // 首尾点重合的闭合线：末点会被并到首点上，不该再补一条自环约束
        var r = PolylineTin.Collect(new[] { L(true, 0, (0, 0), (10, 0), (10, 10), (0, 0)) });
        Assert.Equal(3, r.Verts.Count);
        Assert.Equal(1, r.MergedVertices);
        Assert.Equal(3, r.Constraints.Count);
        Assert.DoesNotContain(r.Constraints, c => c.u == c.v);
    }

    [Fact]
    public void PerVertexElevation_isKept()
    {
        var r = PolylineTin.Collect(new[] { L3(false, new[] { (0.0, 0.0, 10.0), (5.0, 0.0, 20.0), (9.0, 0.0, 30.0) }) });
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, r.Verts.Select(v => v.z).ToArray());
    }

    [Fact]
    public void SharedEndpoints_acrossLines_areMergedIntoOneVertex()
    {
        // 等值线常在端点处首尾相接；不合并会给三角化喂入重合点, 剖分直接失败
        var a = L(false, 0, (0, 0), (10, 0));
        var b = L(false, 0, (10, 0), (20, 0));
        var r = PolylineTin.Collect(new[] { a, b });
        Assert.Equal(3, r.Verts.Count);                // 不是 4
        Assert.Equal(1, r.MergedVertices);
        Assert.Equal(2, r.Constraints.Count);
        Assert.Contains((1, 2), r.Constraints);        // 第二条线接在合并后的顶点上
    }

    [Fact]
    public void ConstraintsSurviveTriangulation_andContourEdgesAreHonoured()
    {
        // 两条等高线：约束边必须出现在剖分结果的边集中，否则地形结构线被跨过
        var lines = new[]
        {
            L(false, 100, (0, 0), (10, 0), (20, 0)),
            L(false, 110, (0, 10), (10, 10), (20, 10)),
        };
        var r = PolylineTin.Collect(lines);
        Assert.Equal(6, r.Verts.Count);
        var tris = Delaunay.TriangulateConstrained(r.Verts.Select(v => (v.x, v.y)).ToList(), r.Constraints);
        Assert.NotEmpty(tris);

        var edges = new HashSet<(int, int)>();
        foreach (var (a, b, c) in tris)
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                edges.Add(u < v ? (u, v) : (v, u));
        foreach (var (u, v) in r.Constraints)
            Assert.Contains(u < v ? (u, v) : (v, u), edges);
    }

    [Fact]
    public void ShortOrEmptyInput_isIgnoredNotCrashed()
    {
        Assert.Empty(PolylineTin.Collect(Array.Empty<PolylineTin.Line>()).Verts);
        var r = PolylineTin.Collect(new[] { L(false, 0, (1, 1)) });   // 只有一个点的线跳过
        Assert.Empty(r.Verts);
        Assert.Empty(r.Constraints);
    }

    // ── 显示态线框缓冲(交错 P3_C3, 每段 2 顶点 × 6 float) ──
    private static float[] Seg(params (double x, double y, double z)[] pts)
    {
        var v = new List<float>();
        foreach (var p in pts) { v.Add((float)p.x); v.Add((float)p.y); v.Add((float)p.z); v.Add(0); v.Add(1); v.Add(1); }
        return v.ToArray();
    }

    [Fact]
    public void ImportedWireframe_becomesVertsAndConstraints()
    {
        // 导入的 .3dm/OFF 等值线走显示通道、选不中；没有这条路这类图纸建不出三角网
        var buf = new List<float>();
        buf.AddRange(Seg((0, 0, 10), (10, 0, 10)));      // 段1
        buf.AddRange(Seg((10, 0, 10), (10, 10, 20)));    // 段2, 与段1 共端点
        var r = PolylineTin.CollectSegments(buf);
        Assert.Equal(3, r.Verts.Count);                   // 共端点合并, 不是 4
        Assert.Equal(1, r.MergedVertices);
        Assert.Equal(2, r.Constraints.Count);
        Assert.Equal(20, r.Verts[2].z, 9);                // 高程取自缓冲第 3 个 float
    }

    [Fact]
    public void ImportedWireframe_zeroLengthSegment_producesNoConstraint()
    {
        var r = PolylineTin.CollectSegments(Seg((5, 5, 0), (5, 5, 0)));
        Assert.Single(r.Verts);
        Assert.Empty(r.Constraints);
    }

    [Fact]
    public void ImportedWireframe_tooShortBuffer_isIgnored()
    {
        Assert.Empty(PolylineTin.CollectSegments(new float[] { 1, 2, 3 }).Verts);
        Assert.Empty(PolylineTin.CollectSegments(Array.Empty<float>()).Verts);
    }

    // ── 约束预清洗(对应原版 preclean_*): 剔退化/重复/交叉并统计 ──
    private static List<(double x, double y, double z)> V(params (double x, double y)[] p)
        => p.Select(q => (q.x, q.y, 0.0)).ToList();

    [Fact]
    public void Clean_dropsDuplicateSegments()
    {
        var verts = V((0, 0), (10, 0));
        var kept = PolylineTin.Clean(verts, new[] { (0, 1), (1, 0), (0, 1) }, out var st);
        Assert.Single(kept);                 // 同一对端点只留一条(方向不论)
        Assert.Equal(2, st.Duplicate);
        Assert.Equal(2, st.Total);
    }

    [Fact]
    public void Clean_dropsDegenerateSegments()
    {
        var verts = V((0, 0), (10, 0));
        var kept = PolylineTin.Clean(verts, new[] { (0, 0), (1, 1), (-1, 0), (0, 9) }, out var st);
        Assert.Empty(kept);
        Assert.Equal(4, st.Degenerate);
    }

    [Fact]
    public void Clean_dropsTheLaterOfTwoCrossingSegments()
    {
        // 两条等值线真交叉 = 数据本身矛盾; 保留先来的那条, 丢后来的(比两条都丢少开洞)
        var verts = V((0, 0), (10, 10), (0, 10), (10, 0));
        var kept = PolylineTin.Clean(verts, new[] { (0, 1), (2, 3) }, out var st);
        Assert.Single(kept);
        Assert.Equal((0, 1), kept[0]);
        Assert.Equal(1, st.Crossing);
    }

    [Fact]
    public void Clean_keepsSegmentsThatOnlyShareAnEndpoint()
    {
        // 共端点是等值线的正常形态, 不能当成交叉剔掉
        var verts = V((0, 0), (10, 0), (10, 10));
        var kept = PolylineTin.Clean(verts, new[] { (0, 1), (1, 2) }, out var st);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0, st.Total);
    }

    [Fact]
    public void Clean_keepsDisjointSegments()
    {
        var verts = V((0, 0), (10, 0), (0, 50), (10, 50));
        var kept = PolylineTin.Clean(verts, new[] { (0, 1), (2, 3) }, out var st);
        Assert.Equal(2, kept.Count);
        Assert.Equal(0, st.Total);
    }

    // ── 体素抽稀(顶点超上限时照原版先抽稀再剖分) ──

    [Fact]
    public void Downsample_leavesSmallInputUntouched()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 1), (10, 0, 2), (0, 10, 3) };
        var outp = PolylineTin.Downsample(v, 100, out double vox);
        Assert.Equal(3, outp.Count);
        Assert.Equal(0, vox);   // 没抽稀
    }

    [Fact]
    public void Downsample_bringsCountUnderTarget_andKeepsExtent()
    {
        var v = new List<(double x, double y, double z)>();
        for (int i = 0; i < 200; i++)
            for (int j = 0; j < 200; j++) v.Add((i, j, i + j));   // 4 万点
        var outp = PolylineTin.Downsample(v, 5000, out double vox);
        Assert.InRange(outp.Count, 1, 5000);
        Assert.True(vox > 0);
        // 抽稀后仍应铺满原范围(不能只剩一角)
        Assert.InRange(outp.Min(p => p.x), 0, 20);
        Assert.InRange(outp.Max(p => p.x), 180, 199);
        Assert.InRange(outp.Min(p => p.y), 0, 20);
        Assert.InRange(outp.Max(p => p.y), 180, 199);
        // 高程要跟着点一起留下(不是重新编的)
        Assert.All(outp, p => Assert.Equal(p.x + p.y, p.z, 9));
    }

    [Fact]
    public void Downsample_keepsOnePerCell_soPointsStaySpread()
    {
        // 一个格子里挤 100 个点, 只应留一个代表
        var v = new List<(double x, double y, double z)>();
        for (int i = 0; i < 100; i++) v.Add((0.001 * i, 0.001 * i, 0));
        v.Add((1000, 1000, 5));
        var outp = PolylineTin.Downsample(v, 2, out _);
        Assert.InRange(outp.Count, 2, 2);
    }
}
