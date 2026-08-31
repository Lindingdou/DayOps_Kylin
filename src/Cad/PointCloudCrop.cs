using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云按闭合多段线边界裁剪 —— 保留(或剔除)落在边界多边形内的点。
/// 忠实原 PointCloudLib「用选中闭合多段线裁剪点云」。裁剪算法平凡(逐点 PointInPolygon)、纯逻辑、可单测。
/// </summary>
public static class PointCloudCrop
{
    /// <summary>按边界多边形裁剪点云。keepInside=true 保留界内点, false 保留界外点。边界 &lt;3 点 → 原样返回。</summary>
    public static List<(double x, double y, double z)> ByPolygon(
        IReadOnlyList<(double x, double y, double z)> pts,
        IReadOnlyList<(double x, double y)> boundary, bool keepInside = true)
    {
        var kept = new List<(double x, double y, double z)>();
        if (pts == null) return kept;
        if (boundary == null || boundary.Count < 3) { kept.AddRange(pts); return kept; }
        foreach (var p in pts)
            if (LineMath.PointInPolygon(p.x, p.y, boundary) == keepInside) kept.Add(p);
        return kept;
    }
}
