using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 凸包（境界/采场圈定）—— Andrew 单调链，散点 → 逆时针凸包顶点。纯逻辑、可单测。
/// </summary>
public static class GeomHull
{
    public static List<(double x, double y)> ConvexHull(IReadOnlyList<(double x, double y)> input)
    {
        var pts = new List<(double x, double y)>(input);
        if (pts.Count < 3) return pts;
        pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));

        double Cross((double x, double y) o, (double x, double y) a, (double x, double y) b)
            => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        var lower = new List<(double x, double y)>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<(double x, double y)>();
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;   // 逆时针凸包
    }
}
