using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>网格诊断结果。</summary>
public readonly record struct MeshDiagnoseResult(
    int TriangleCount, int EdgeCount, int BoundaryEdges, int NonManifoldEdges,
    int DegenerateTriangles, int BoundaryLoops, bool IsClosed,
    int IsolatedVertices = 0, int DuplicateVertices = 0,   // 孤立点(无三角引用) + 重复点(坐标重合), 忠实原 DIAGNOSE
    int SelfIntersectTriangles = 0);   // 自交三角数(横切另一非相邻三角), 忠实原 PMDR SelfIntersect; -1=网格过大未检

/// <summary>
/// 网格拓扑诊断（对应 MeshEditLib DiagnoseReport）——按无向边的三角关联数判边界边(1)/非流形边(≥3)，
/// 统计退化三角、边界环(洞)数、是否闭合(无边界且无非流形)。纯拓扑、可单测。
/// </summary>
public static class MeshDiagnose
{
    /// <param name="selfIntersect">是否做自交检测（网格 broad-phase + 逐对精测, 6 万三角以内才做, 仍是整条流水线里最贵的一步）。
    /// 只要开放边/非流形数的调用方(修复拓扑前后对比等)传 false。</param>
    public static MeshDiagnoseResult Analyze(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris, bool selfIntersect = true)
    {
        if (tris == null) return new MeshDiagnoseResult(0, 0, 0, 0, 0, 0, true);
        var inc = new Dictionary<(int, int), int>();     // 无向边 → 关联三角数
        int degen = 0, nt = 0;
        var valid = new List<(int a, int b, int c)>();   // 非退化三角(供自交检测)
        void Edge(int u, int v) { var k = u < v ? (u, v) : (v, u); inc[k] = inc.TryGetValue(k, out var c) ? c + 1 : 1; }
        foreach (var (a, b, c) in tris)
        {
            if (a == b || b == c || a == c) { degen++; continue; }
            // 零面积也算退化(若有顶点)
            if (verts != null && a >= 0 && b >= 0 && c >= 0 && a < verts.Count && b < verts.Count && c < verts.Count)
            {
                var p = verts[a]; var q = verts[b]; var r = verts[c];
                double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                if (cx * cx + cy * cy + cz * cz < 1e-18) { degen++; continue; }
            }
            Edge(a, b); Edge(b, c); Edge(c, a);
            valid.Add((a, b, c));
            nt++;
        }

        int boundary = 0, nonMani = 0;
        var boundaryEdges = new List<(int u, int v)>();
        foreach (var kv in inc)
        {
            if (kv.Value == 1) { boundary++; boundaryEdges.Add(kv.Key); }
            else if (kv.Value >= 3) nonMani++;
        }

        // 边界环数 = 边界边图的连通分量数（并查集）
        int loops = 0;
        if (boundaryEdges.Count > 0)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x) { if (!parent.ContainsKey(x)) parent[x] = x; while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { parent[Find(a)] = Find(b); }
            foreach (var (u, v) in boundaryEdges) { Find(u); Find(v); Union(u, v); }
            var roots = new HashSet<int>();
            foreach (var k in parent.Keys) roots.Add(Find(k));
            loops = roots.Count;
        }

        // 孤立点(无任何三角引用) + 重复点(坐标按 tol 量化重合), 忠实原 DIAGNOSE 孤立点/重复点检查
        int isolated = 0, duplicate = 0;
        if (verts != null && verts.Count > 0)
        {
            var referenced = new bool[verts.Count];
            foreach (var (a, b, c) in tris)
            {
                if (a >= 0 && a < verts.Count) referenced[a] = true;
                if (b >= 0 && b < verts.Count) referenced[b] = true;
                if (c >= 0 && c < verts.Count) referenced[c] = true;
            }
            foreach (var f in referenced) if (!f) isolated++;
            const double tol = 1e-6;
            var seen = new HashSet<(long, long, long)>();
            foreach (var v in verts)
            {
                var key = ((long)Math.Round(v.x / tol), (long)Math.Round(v.y / tol), (long)Math.Round(v.z / tol));
                if (!seen.Add(key)) duplicate++;
            }
        }

        // 自交三角: 横切另一【非相邻(不共顶点)】三角。网格 broad-phase(AABB 落格) 后逐候选对精测。
        int selfInt = selfIntersect ? CountSelfIntersect(verts, valid) : -1;

        bool closed = boundary == 0 && nonMani == 0 && nt > 0;
        return new MeshDiagnoseResult(nt, inc.Count, boundary, nonMani, degen, loops, closed, isolated, duplicate, selfInt);
    }

    /// <summary>自交三角数(横切另一非相邻三角)。均匀网格 broad-phase; 三角数超上限返回 -1(未检)。</summary>
    private const int SelfIntersectCap = 60000;
    private static int CountSelfIntersect(IReadOnlyList<(double x, double y, double z)>? verts, List<(int a, int b, int c)> tris)
    {
        int m = tris.Count;
        if (verts == null || m < 2) return 0;
        if (m > SelfIntersectCap) return -1;   // 过大, 交给 native/BVH

        // 各三角 AABB + 全局包围盒。
        var box = new (double x0, double y0, double z0, double x1, double y1, double z1)[m];
        double gx0 = double.MaxValue, gy0 = double.MaxValue, gz0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue, gz1 = double.MinValue;
        for (int i = 0; i < m; i++)
        {
            var (a, b, c) = tris[i];
            if (a >= verts.Count || b >= verts.Count || c >= verts.Count) { box[i] = (0, 0, 0, 0, 0, 0); continue; }
            var p = verts[a]; var q = verts[b]; var r = verts[c];
            box[i] = (Math.Min(p.x, Math.Min(q.x, r.x)), Math.Min(p.y, Math.Min(q.y, r.y)), Math.Min(p.z, Math.Min(q.z, r.z)),
                      Math.Max(p.x, Math.Max(q.x, r.x)), Math.Max(p.y, Math.Max(q.y, r.y)), Math.Max(p.z, Math.Max(q.z, r.z)));
            if (box[i].x0 < gx0) gx0 = box[i].x0; if (box[i].y0 < gy0) gy0 = box[i].y0; if (box[i].z0 < gz0) gz0 = box[i].z0;
            if (box[i].x1 > gx1) gx1 = box[i].x1; if (box[i].y1 > gy1) gy1 = box[i].y1; if (box[i].z1 > gz1) gz1 = box[i].z1;
        }
        // 网格格距 ≈ 每维按 √m 分。
        int grid = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(m)));
        double sx = Math.Max((gx1 - gx0) / grid, 1e-9), sy = Math.Max((gy1 - gy0) / grid, 1e-9), sz = Math.Max((gz1 - gz0) / grid, 1e-9);
        var cells = new Dictionary<(int, int, int), List<int>>();
        // 跨格太多的"巨三角"(补洞扇面 / 长条 sliver 等, 一个就能盖住几百万格)不落格, 单独与全表按 AABB 粗筛后精测:
        // 落格的话光登记就是几千 × 几百万次, 修复拓扑后的「格网质量检测」就是在这儿卡死的。
        const int MaxCellsPerTri = 4096;
        var oversized = new List<int>();
        for (int i = 0; i < m; i++)
        {
            int cx0 = (int)Math.Floor((box[i].x0 - gx0) / sx), cx1 = (int)Math.Floor((box[i].x1 - gx0) / sx);
            int cy0 = (int)Math.Floor((box[i].y0 - gy0) / sy), cy1 = (int)Math.Floor((box[i].y1 - gy0) / sy);
            int cz0 = (int)Math.Floor((box[i].z0 - gz0) / sz), cz1 = (int)Math.Floor((box[i].z1 - gz0) / sz);
            if ((long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1) * (cz1 - cz0 + 1) > MaxCellsPerTri) { oversized.Add(i); continue; }
            for (int X = cx0; X <= cx1; X++) for (int Y = cy0; Y <= cy1; Y++) for (int Z = cz0; Z <= cz1; Z++)
            {
                var key = (X, Y, Z);
                if (!cells.TryGetValue(key, out var l)) { l = new List<int>(); cells[key] = l; }
                l.Add(i);
            }
        }
        var tested = new HashSet<(int, int)>();
        var hit = new HashSet<int>();
        foreach (int i0 in oversized)
        {
            var ba = box[i0]; var (a0, a1, a2) = tris[i0];
            for (int j0 = 0; j0 < m; j0++)
            {
                if (j0 == i0) continue;
                int i = i0, j = j0; if (i > j) (i, j) = (j, i);
                if (!tested.Add((i, j))) continue;
                var (b0, b1, b2) = tris[j0];
                if (Shares(a0, a1, a2, b0, b1, b2)) continue;
                var bb = box[j0];
                if (ba.x1 < bb.x0 || bb.x1 < ba.x0 || ba.y1 < bb.y0 || bb.y1 < ba.y0 || ba.z1 < bb.z0 || bb.z1 < ba.z0) continue;
                if (MeshIntersect.TrianglesIntersect(verts[a0], verts[a1], verts[a2], verts[b0], verts[b1], verts[b2]))
                { hit.Add(i0); hit.Add(j0); }
            }
        }
        foreach (var bucket in cells.Values)
        {
            for (int u = 0; u < bucket.Count; u++)
                for (int w = u + 1; w < bucket.Count; w++)
                {
                    int i = bucket[u], j = bucket[w];
                    if (i > j) (i, j) = (j, i);
                    if (!tested.Add((i, j))) continue;
                    var (a0, a1, a2) = tris[i]; var (b0, b1, b2) = tris[j];
                    if (Shares(a0, a1, a2, b0, b1, b2)) continue;                 // 相邻(共顶点)不算自交
                    var ba = box[i]; var bb = box[j];
                    if (ba.x1 < bb.x0 || bb.x1 < ba.x0 || ba.y1 < bb.y0 || bb.y1 < ba.y0 || ba.z1 < bb.z0 || bb.z1 < ba.z0) continue;
                    if (MeshIntersect.TrianglesIntersect(verts[a0], verts[a1], verts[a2], verts[b0], verts[b1], verts[b2]))
                    { hit.Add(i); hit.Add(j); }
                }
        }
        return hit.Count;
    }

    private static bool Shares(int a0, int a1, int a2, int b0, int b1, int b2)
        => a0 == b0 || a0 == b1 || a0 == b2 || a1 == b0 || a1 == b1 || a1 == b2 || a2 == b0 || a2 == b1 || a2 == b2;
}
