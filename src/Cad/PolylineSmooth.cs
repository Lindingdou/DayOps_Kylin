using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 曲线平滑（Chaikin 角点切割）—— 每段用 1/4、3/4 两点替换，迭代逼近光滑曲线。
/// 开口保留首末端点；闭合环绕。纯逻辑、可单测。
/// </summary>
public static class PolylineSmooth
{
    public static List<(double x, double y)> Chaikin(IReadOnlyList<(double x, double y)> input, int iterations, bool closed)
    {
        var pts = new List<(double x, double y)>(input);
        if (pts.Count < 2) return pts;
        for (int it = 0; it < iterations; it++)
        {
            var np = new List<(double x, double y)>();
            int n = pts.Count;
            if (!closed) np.Add(pts[0]);
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % n];
                np.Add((p.x + 0.25 * (q.x - p.x), p.y + 0.25 * (q.y - p.y)));
                np.Add((p.x + 0.75 * (q.x - p.x), p.y + 0.75 * (q.y - p.y)));
            }
            if (!closed) np.Add(pts[^1]);
            pts = np;
        }
        return pts;
    }
}
