using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 消 T 型接缝（T-junction）。
///
/// 布尔 / 刀切这类"逐三角按交线重剖"的做法有个必然的副作用：交线的端点只落进**被它穿过**的那个三角形，
/// 而这个点常常正好压在它与邻居**共用的那条边**上 —— 邻居没被穿过，就不会插这个点，于是共用边一侧被分成两段、
/// 另一侧还是整条。焊接看顶点重不重合，这种情况顶点是重合的，可边对不上，网就是"看着严丝合缝、拓扑上却裂着"，
/// 诊断出来一堆开放边，体积算得对但成不了闭合体。
///
/// 这里做的就是把这种边补齐：凡是有顶点压在某个三角形的边内部（不含端点）的，就把那个三角形按边上的点重新剖开。
/// 一个三角形可能三条边上都挂着点，找不到"没被分的角"可以拉扇形，所以统一从**形心**拉扇 —— 凸多边形怎么拉都合法，
/// 且每个边上的点必然被引用到，不会再留 T 型口。多出来的形心点无害（后续焊接/诊断都按普通顶点处理）。
/// 纯逻辑、可单测。
/// </summary>
public static class MeshTJunction
{
    /// <summary>
    /// 修掉 T 型接缝。<paramref name="tol"/> 为"点算落在边上"的垂距阈值（0 = 按包围盒对角 × 1e-7 自动）。
    /// </summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris, int splitTris) Fix(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris, double tol = 0)
    {
        var v = new List<(double x, double y, double z)>(verts ?? new List<(double x, double y, double z)>());
        var t = new List<(int a, int b, int c)>(tris ?? new List<(int a, int b, int c)>());
        if (v.Count < 3 || t.Count < 1) return (v, t, 0);

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in v)
        {
            minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y); minZ = Math.Min(minZ, p.z);
            maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y); maxZ = Math.Max(maxZ, p.z);
        }
        double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
        if (tol <= 0) tol = diag > 0 ? diag * 1e-7 : 1e-9;
        double tol2 = tol * tol;

        // 顶点均匀格索引：按边的 AABB 取候选点，避免 O(边 × 顶点)
        double cell = Math.Max(diag / 128, tol * 4);
        if (!(cell > 0)) cell = 1;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key((double x, double y, double z) p)
            => ((int)Math.Floor(p.x / cell), (int)Math.Floor(p.y / cell), (int)Math.Floor(p.z / cell));
        for (int i = 0; i < v.Count; i++)
        {
            var k = Key(v[i]);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
            list.Add(i);
        }

        var outTris = new List<(int a, int b, int c)>(t.Count);
        int splitCount = 0;
        var onEdge = new List<(double t, int vi)>();
        var poly = new List<int>();

        foreach (var (ia, ib, ic) in t)
        {
            if ((uint)ia >= v.Count || (uint)ib >= v.Count || (uint)ic >= v.Count) continue;
            poly.Clear();
            bool any = false;
            var corners = new[] { ia, ib, ic };
            for (int e = 0; e < 3; e++)
            {
                int u = corners[e], w = corners[(e + 1) % 3];
                poly.Add(u);
                onEdge.Clear();
                CollectOnEdge(v, grid, cell, u, w, tol, tol2, onEdge);
                if (onEdge.Count > 0)
                {
                    any = true;
                    onEdge.Sort((p, q) => p.t.CompareTo(q.t));
                    int last = -1;
                    foreach (var (_, vi) in onEdge) { if (vi != last) { poly.Add(vi); last = vi; } }
                }
            }
            if (!any) { outTris.Add((ia, ib, ic)); continue; }

            // 形心拉扇：三条边上都可能挂点，找不到"干净的角"，形心是唯一总能用的扇心
            double cx = 0, cy = 0, cz = 0;
            foreach (int pi in poly) { cx += v[pi].x; cy += v[pi].y; cz += v[pi].z; }
            cx /= poly.Count; cy /= poly.Count; cz /= poly.Count;
            v.Add((cx, cy, cz));
            int center = v.Count - 1;
            for (int i = 0; i < poly.Count; i++)
                outTris.Add((center, poly[i], poly[(i + 1) % poly.Count]));
            splitCount++;
        }
        return (v, outTris, splitCount);
    }

    private static void CollectOnEdge(
        List<(double x, double y, double z)> v, Dictionary<(int, int, int), List<int>> grid, double cell,
        int u, int w, double tol, double tol2, List<(double t, int vi)> hits)
    {
        var A = v[u]; var B = v[w];
        double dx = B.x - A.x, dy = B.y - A.y, dz = B.z - A.z;
        double len2 = dx * dx + dy * dy + dz * dz;
        if (len2 <= tol2) return;
        double len = Math.Sqrt(len2);
        double tMin = tol / len, tMax = 1 - tMin;   // 端点附近的点不算(那是顶点重合, 归焊接管)
        if (tMin >= tMax) return;

        int x0 = (int)Math.Floor((Math.Min(A.x, B.x) - tol) / cell), x1 = (int)Math.Floor((Math.Max(A.x, B.x) + tol) / cell);
        int y0 = (int)Math.Floor((Math.Min(A.y, B.y) - tol) / cell), y1 = (int)Math.Floor((Math.Max(A.y, B.y) + tol) / cell);
        int z0 = (int)Math.Floor((Math.Min(A.z, B.z) - tol) / cell), z1 = (int)Math.Floor((Math.Max(A.z, B.z) + tol) / cell);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 200_000) return;   // 退化保护

        for (int gx = x0; gx <= x1; gx++)
            for (int gy = y0; gy <= y1; gy++)
                for (int gz = z0; gz <= z1; gz++)
                {
                    if (!grid.TryGetValue((gx, gy, gz), out var list)) continue;
                    foreach (int vi in list)
                    {
                        if (vi == u || vi == w) continue;
                        var P = v[vi];
                        double s = ((P.x - A.x) * dx + (P.y - A.y) * dy + (P.z - A.z) * dz) / len2;
                        if (s <= tMin || s >= tMax) continue;
                        double qx = A.x + dx * s, qy = A.y + dy * s, qz = A.z + dz * s;
                        double d2 = (P.x - qx) * (P.x - qx) + (P.y - qy) * (P.y - qy) + (P.z - qz) * (P.z - qz);
                        if (d2 <= tol2) hits.Add((s, vi));
                    }
                }
    }
}
