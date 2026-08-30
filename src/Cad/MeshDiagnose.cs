using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>网格诊断结果。</summary>
public readonly record struct MeshDiagnoseResult(
    int TriangleCount, int EdgeCount, int BoundaryEdges, int NonManifoldEdges,
    int DegenerateTriangles, int BoundaryLoops, bool IsClosed);

/// <summary>
/// 网格拓扑诊断（对应 MeshEditLib DiagnoseReport）——按无向边的三角关联数判边界边(1)/非流形边(≥3)，
/// 统计退化三角、边界环(洞)数、是否闭合(无边界且无非流形)。纯拓扑、可单测。
/// </summary>
public static class MeshDiagnose
{
    public static MeshDiagnoseResult Analyze(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        if (tris == null) return new MeshDiagnoseResult(0, 0, 0, 0, 0, 0, true);
        var inc = new Dictionary<(int, int), int>();     // 无向边 → 关联三角数
        int degen = 0, nt = 0;
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

        bool closed = boundary == 0 && nonMani == 0 && nt > 0;
        return new MeshDiagnoseResult(nt, inc.Count, boundary, nonMani, degen, loops, closed);
    }
}
