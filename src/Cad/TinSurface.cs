using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 2.5D TIN 面装配/统计 —— 把散点(x,y,z) + Delaunay 三角(XY 拓扑)装成一张保留高程的三角网面。
/// 忠实原「创建三角网/快速建模」的 2.5D 建面: XY 内 Delaunay 定拓扑, 顶点各带自身 z(不丢高程),
/// 出可复用 OFF 面(供 快速建模/算量/分析), 而非仅画三角边线框。纯逻辑、可单测。
/// </summary>
public static class TinSurface
{
    public sealed record Stats(int Verts, int Tris, double ProjectedAreaXY, double ZMin, double ZMax);

    /// <summary>面统计: 顶点/三角数 · XY 投影面积(各三角鞋带和) · 高程范围。空面 → 面积0/z 0~0。</summary>
    public static Stats Describe(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        if (verts == null || verts.Count == 0) return new Stats(0, tris?.Count ?? 0, 0, 0, 0);
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var v in verts) { if (v.z < zmin) zmin = v.z; if (v.z > zmax) zmax = v.z; }
        double area = 0;
        if (tris != null)
            foreach (var (a, b, c) in tris)
            {
                if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
                var pa = verts[a]; var pb = verts[b]; var pc = verts[c];
                area += Math.Abs((pb.x - pa.x) * (pc.y - pa.y) - (pc.x - pa.x) * (pb.y - pa.y)) * 0.5;   // 三角 XY 鞋带
            }
        return new Stats(verts.Count, tris?.Count ?? 0, area, zmin, zmax);
    }
}
