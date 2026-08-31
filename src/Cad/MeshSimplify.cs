using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 网格简化(顶点聚类) —— 忠实原 MeshEditLib「网格简化(顶点聚类占位实现)」：把相距 &lt; 容差的顶点并成一个,
/// 塌陷成线/点的三角自动丢弃, 三角/顶点数随之下降。容差 = 包围盒对角 × cellRatio(越大越简)。
/// 复用已测 <see cref="MeshWeld.Weld"/>(空间哈希容差合并=顶点聚类)。纯逻辑、可单测。
/// </summary>
public static class MeshSimplify
{
    /// <summary>顶点聚类简化。cellRatio∈(0,0.5], 相对包围盒对角的聚类格尺度。返回简化网(带前后计数)。</summary>
    public static MeshWeldResult ByClustering(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t, double cellRatio)
    {
        if (v == null || t == null || v.Count == 0) return new MeshWeldResult();
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in v)
        {
            if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.z < minZ) minZ = p.z;
            if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; if (p.z > maxZ) maxZ = p.z;
        }
        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        double diag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double r = Math.Clamp(cellRatio, 1e-4, 0.5);
        double tol = diag > 1e-9 ? diag * r : 1e-6;
        return MeshWeld.Weld(v, t, tol, dropDuplicateTris: true);
    }
}
