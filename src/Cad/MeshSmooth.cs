using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网 Laplacian 光顺 —— 每顶点朝其邻点质心移动 λ 比例，迭代若干次去噪/光顺曲面。
/// 忠实原 MeshEditLib 网格光顺(mesh_smooth)的标准几何核(非变体敏感，可托管，insight #5)。
/// 默认固定边界顶点(保网格轮廓)。纯逻辑、可单测。
/// </summary>
public static class MeshSmooth
{
    /// <summary>Laplacian 光顺。iterations 迭代次数, lambda 步长(0..1), fixBoundary 固定开边界顶点。返回新顶点(索引不变)。</summary>
    public static List<(double x, double y, double z)> Laplacian(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t,
        int iterations = 3, double lambda = 0.5, bool fixBoundary = true)
    {
        int n = v.Count;
        var cur = new (double x, double y, double z)[n];
        for (int i = 0; i < n; i++) cur[i] = v[i];
        if (n == 0 || t.Count == 0 || iterations <= 0) return new List<(double x, double y, double z)>(cur);

        // 邻接表
        var nbr = new HashSet<int>[n];
        for (int i = 0; i < n; i++) nbr[i] = new HashSet<int>();
        void Link(int a, int b) { if (a >= 0 && b >= 0 && a < n && b < n && a != b) { nbr[a].Add(b); nbr[b].Add(a); } }
        // 边界：无向边只被 1 个三角用 → 其两端点为边界点
        var edgeCnt = new Dictionary<(int, int), int>();
        void Bump(int a, int b) { var k = a < b ? (a, b) : (b, a); edgeCnt[k] = edgeCnt.TryGetValue(k, out int c) ? c + 1 : 1; }
        foreach (var tr in t) { Link(tr.a, tr.b); Link(tr.b, tr.c); Link(tr.c, tr.a); Bump(tr.a, tr.b); Bump(tr.b, tr.c); Bump(tr.c, tr.a); }
        var boundary = new bool[n];
        if (fixBoundary) foreach (var kv in edgeCnt) if (kv.Value == 1) { boundary[kv.Key.Item1] = true; boundary[kv.Key.Item2] = true; }

        var next = new (double x, double y, double z)[n];
        for (int it = 0; it < iterations; it++)
        {
            for (int i = 0; i < n; i++)
            {
                if ((fixBoundary && boundary[i]) || nbr[i].Count == 0) { next[i] = cur[i]; continue; }
                double sx = 0, sy = 0, sz = 0;
                foreach (int j in nbr[i]) { sx += cur[j].x; sy += cur[j].y; sz += cur[j].z; }
                int k = nbr[i].Count;
                double cx = sx / k, cy = sy / k, cz = sz / k;   // 邻点质心
                next[i] = (cur[i].x + lambda * (cx - cur[i].x), cur[i].y + lambda * (cy - cur[i].y), cur[i].z + lambda * (cz - cur[i].z));
            }
            (cur, next) = (next, cur);
        }
        return new List<(double x, double y, double z)>(cur);
    }
}
