using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 顶/底面成体 + 连续多层建模 —— 取顶面、底面开放三角网, 提最大边界环放样侧壁, 焊成闭合地质体。
/// 抽取自 QuickModelAsync 的「2 面→体」核(复用 MeshBoundaryLoops/SideSurface.Loft/MeshWeld), 供
/// 单面成体与「连续多层自动建模」(N 面按均高排, 逐相邻对成 N-1 体)共用。纯逻辑、可单测。
/// </summary>
public static class LayerSolid
{
    /// <summary>2 面(顶 v0/t0, 底 v1/t1)→ 闭合体(顶+底+侧壁焊接)。边界环缺失/放样失败返回 null。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris)? FromSurfaces(
        IReadOnlyList<(double x, double y, double z)> v0, IReadOnlyList<(int a, int b, int c)> t0,
        IReadOnlyList<(double x, double y, double z)> v1, IReadOnlyList<(int a, int b, int c)> t1)
    {
        if (t0 == null || t1 == null || t0.Count == 0 || t1.Count == 0) return null;
        var loop0 = LargestLoop(MeshBoundaryLoops.Extract(v0, t0));
        var loop1 = LargestLoop(MeshBoundaryLoops.Extract(v1, t1));
        if (loop0 == null || loop1 == null) return null;
        var (sv, st) = SideSurface.Loft(loop0, loop1, closed: true, flip: false);
        if (st.Count == 0) return null;
        var (verts, tris) = MeshWeld.Concat(
            new List<(IReadOnlyList<(double x, double y, double z)>, IReadOnlyList<(int a, int b, int c)>)>
            { (v0, t0), (v1, t1), (sv, st) });
        var m = MeshMetrics.Compute(verts, tris);
        double diag = Math.Sqrt((m.MaxX - m.MinX) * (m.MaxX - m.MinX) +
                                (m.MaxY - m.MinY) * (m.MaxY - m.MinY) +
                                (m.MaxZ - m.MinZ) * (m.MaxZ - m.MinZ));
        var w = MeshWeld.Weld(verts, tris, diag > 0 ? diag * 1e-4 : 1e-6, dropDuplicateTris: true);
        // 顶/底/侧壁各自朝向 loft/输入不一致 → BFS 传播统一为外向, 使体积(散度/缠绕)可靠
        var oriented = MeshOrient.MakeConsistent(w.Verts, w.Tris);
        return (w.Verts, oriented);
    }

    /// <summary>面平均 Z（供多层按标高排序）。</summary>
    public static double MeanZ(IReadOnlyList<(double x, double y, double z)> v)
    {
        if (v == null || v.Count == 0) return 0;
        double s = 0; foreach (var p in v) s += p.z; return s / v.Count;
    }

    /// <summary>
    /// 连续多层自动建模：N 张层位面按均高**降序**排(顶→底), 逐相邻对成体 → N-1 个闭合体。
    /// 各面为 (verts, tris)。返回各夹层体(可能含 null 若某对放样失败, 调用方过滤)。
    /// </summary>
    public static List<(List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris)?> MultiLayer(
        IReadOnlyList<(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)> surfaces)
    {
        var res = new List<(List<(double x, double y, double z)>, List<(int a, int b, int c)>)?>();
        if (surfaces == null || surfaces.Count < 2) return res;
        var ordered = new List<(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)>(surfaces);
        ordered.Sort((p, q) => MeanZ(q.v).CompareTo(MeanZ(p.v)));   // 均高降序：顶在前
        for (int i = 0; i + 1 < ordered.Count; i++)
            res.Add(FromSurfaces(ordered[i].v, ordered[i].t, ordered[i + 1].v, ordered[i + 1].t));
        return res;
    }

    private static List<(double x, double y, double z)>? LargestLoop(List<List<(double x, double y, double z)>> loops)
    {
        List<(double x, double y, double z)>? best = null;
        foreach (var l in loops) if (best == null || l.Count > best.Count) best = l;
        return best;
    }
}
