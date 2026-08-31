using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云欧氏聚类分割 —— 标准 Euclidean cluster extraction(同 PCL): 距离 &lt; radius 的点连通成簇(并查集),
/// 网格哈希加速邻域搜。对应原「分割点云」的标准默认变体(原走 native PMSG, 另有 region-grow/normal 变体;
/// 此为标准欧氏变体的托管重实现, 结果与 native 具体变体可能不同)。纯逻辑、可单测。
/// </summary>
public static class PointCluster
{
    /// <summary>欧氏聚类。返回逐点簇 id(0-based, 按簇大小降序); 小于 minSize 的簇归 -1。out clusterCount = 有效簇数。</summary>
    public static int[] Euclidean(IReadOnlyList<(double x, double y, double z)> pts, double radius, int minSize, out int clusterCount)
    {
        clusterCount = 0;
        int n = pts?.Count ?? 0;
        var label = new int[n];
        if (n == 0) return label;
        for (int i = 0; i < n; i++) label[i] = -1;

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        double cell = radius > 1e-9 ? radius : 1;
        var grid = new Dictionary<(long, long, long), List<int>>(n);
        (long, long, long) Key(int i) => ((long)Math.Floor(pts[i].x / cell), (long)Math.Floor(pts[i].y / cell), (long)Math.Floor(pts[i].z / cell));
        for (int i = 0; i < n; i++) { var k = Key(i); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i); }

        double r2 = radius * radius;
        for (int i = 0; i < n; i++)
        {
            var (cx, cy, cz) = Key(i);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                            foreach (int j in bucket)
                            {
                                if (j <= i) continue;
                                double ex = pts[j].x - pts[i].x, ey = pts[j].y - pts[i].y, ez = pts[j].z - pts[i].z;
                                if (ex * ex + ey * ey + ez * ez <= r2) Union(i, j);
                            }
        }

        // 统计每根的簇大小
        var size = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) { int r = Find(i); size[r] = size.TryGetValue(r, out var c) ? c + 1 : 1; }
        // 有效簇(≥minSize)按大小降序 → 紧凑 id
        var valid = new List<KeyValuePair<int, int>>();
        foreach (var kv in size) if (kv.Value >= minSize) valid.Add(kv);
        valid.Sort((a, b) => b.Value.CompareTo(a.Value));
        var rootToId = new Dictionary<int, int>();
        for (int k = 0; k < valid.Count; k++) rootToId[valid[k].Key] = k;
        clusterCount = valid.Count;
        for (int i = 0; i < n; i++) label[i] = rootToId.TryGetValue(Find(i), out var id) ? id : -1;
        return label;
    }
}
