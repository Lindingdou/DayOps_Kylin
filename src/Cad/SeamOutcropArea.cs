using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.SeamOutcrop;
using OutcropZSampler = PitMine3D.Kylin.Cad.SeamOutcrop.MeshZSampler;

namespace PitMine3D.Kylin.Cad;

/// <summary>一层煤的【现状已露煤】面边界环（世界坐标闭合折线；<see cref="AreaXY"/> 带号，负=洞）。</summary>
public sealed class OutcropRing
{
    public int SeamIndex;
    public string SeamName = "";
    public readonly List<(double X, double Y, double Z)> Pts = new();
    public double AreaXY;
    public bool IsHole => AreaXY < 0;
}

/// <summary>【现状已露煤】(=露头面)计算结果，与入参 seams 同序。</summary>
public sealed class SeamOutcropAreaResult
{
    public bool Success;
    public string Error = "";
    public double[] Area3D = Array.Empty<double>();
    public double[] AreaXY = Array.Empty<double>();
    public int[] TriCount = Array.Empty<int>();
    public readonly List<OutcropRing> Rings = new();
}

/// <summary>
/// 【R9 现场口径】露煤 = 煤层顶板已被揭露的那块面 —— 现状面上落在该层顶底板之间（底板 ≤ z ≤ 顶板）的部分，与推进量无关。
/// 忠实原 <c>SeamOutcropArea</c>：不重写切割/判定，直接调「煤层露头」同一份 <see cref="SeamOutcropRefiner"/>（一个口径一份实现）。
/// </summary>
public static class SeamOutcropArea
{
    public const double DefaultSnapEps = 0.05;

    public static SeamOutcropAreaResult Compute(TinSampler? currentSurface, IReadOnlyList<SeamSurfaces> seams, double snapEps = DefaultSnapEps)
    {
        var res = new SeamOutcropAreaResult();
        int n = seams?.Count ?? 0;
        res.Area3D = new double[n]; res.AreaXY = new double[n]; res.TriCount = new int[n];
        if (currentSurface == null) { res.Error = "无现状面，露煤无从谈起"; return res; }
        if (n == 0) { res.Error = "无煤层"; return res; }

        currentSurface.GetGeometry(out double[] tv, out int[] tt);
        if (tv.Length < 9 || tt.Length < 3) { res.Error = "现状面三角网为空"; return res; }

        var fields = new List<SeamOutcropRefiner.SeamField>(n);
        var live = new List<int>(n);
        for (int k = 0; k < n; k++)
        {
            var s = seams![k];
            if (s.Roof == null || s.Floor == null) continue;
            s.Roof.GetGeometry(out double[] rv, out int[] rt);
            s.Floor.GetGeometry(out double[] fv, out int[] ft);
            fields.Add(new SeamOutcropRefiner.SeamField(new OutcropZSampler(rv, rt), new OutcropZSampler(fv, ft), 0x808080u));
            live.Add(k);
        }
        if (fields.Count == 0) { res.Error = "没有顶底板齐全的煤层"; return res; }

        var r = SeamOutcropRefiner.Build(tv, tt, fields, snapEps, null);
        if (r.TotalTris == 0) { res.Error = "细分失败"; return res; }

        for (int i = 0; i < fields.Count; i++)
        {
            int k = live[i];
            res.Area3D[k] = r.SeamArea3D[i]; res.AreaXY[k] = r.SeamAreaXY[i]; res.TriCount[k] = r.SeamTris[i];
        }
        for (int i = 0; i < fields.Count; i++)
        {
            if (r.SeamTris[i] == 0) continue;
            foreach (var ring in BoundaryRings(r, i))
            {
                ring.SeamIndex = live[i];
                ring.SeamName = seams![live[i]].Name;
                res.Rings.Add(ring);
            }
        }
        res.Success = true;
        return res;
    }

    /// <summary>把第 seamSlot 层的露头三角集合的边界串成闭环（同层内部边被两个三角各用一次，边界边只出现一次）。</summary>
    private static List<OutcropRing> BoundaryRings(SeamOutcropRefiner.Result r, int seamSlot)
    {
        var used = new Dictionary<(int A, int B), int>();
        void Bump(int a, int b) { var key = a < b ? (a, b) : (b, a); used[key] = used.TryGetValue(key, out int v) ? v + 1 : 1; }
        for (int t = 0; t < r.FaceSeam.Length; t++)
        {
            if (r.FaceSeam[t] != seamSlot) continue;
            int a = (int)r.Tris[t * 3], b = (int)r.Tris[t * 3 + 1], c = (int)r.Tris[t * 3 + 2];
            Bump(a, b); Bump(b, c); Bump(c, a);
        }
        var adj = new Dictionary<int, List<int>>();
        void Add(int a, int b) { if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(2); l.Add(b); }
        foreach (var kv in used) { if (kv.Value != 1) continue; Add(kv.Key.A, kv.Key.B); Add(kv.Key.B, kv.Key.A); }

        var rings = new List<OutcropRing>();
        var doneEdge = new HashSet<(int, int)>();
        foreach (int start in adj.Keys)
        {
            foreach (int first in adj[start])
            {
                var e0 = start < first ? (start, first) : (first, start);
                if (doneEdge.Contains(e0)) continue;
                var ring = new OutcropRing();
                int prev = start, cur = first;
                doneEdge.Add(e0);
                AddPt(ring, r, start);
                int guard = 0;
                while (cur != start && guard++ < 1_000_000)
                {
                    AddPt(ring, r, cur);
                    int next = -1;
                    foreach (int cand in adj[cur])
                    {
                        if (cand == prev) continue;
                        var e = cur < cand ? (cur, cand) : (cand, cur);
                        if (doneEdge.Contains(e)) continue;
                        next = cand; doneEdge.Add(e); break;
                    }
                    if (next < 0) break;
                    prev = cur; cur = next;
                }
                if (ring.Pts.Count >= 3) { ring.AreaXY = Shoelace(ring.Pts); rings.Add(ring); }
            }
        }
        return rings;
    }

    private static void AddPt(OutcropRing ring, SeamOutcropRefiner.Result r, int vi)
        => ring.Pts.Add((r.Verts[vi * 3], r.Verts[vi * 3 + 1], r.Verts[vi * 3 + 2]));

    private static double Shoelace(List<(double X, double Y, double Z)> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X * p[i].Y - p[i].X * p[j].Y);
        return 0.5 * a;
    }
}
