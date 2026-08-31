using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 道路中线 → 路面路带：把中心折线按路宽等宽双侧外扩，成一条闭合路带多边形。
/// 忠实原 RoadLib「把中心线按路宽外扩生成路面」。逐顶点取相邻段法向均值偏移(平缓路足够)。纯逻辑、可单测。
/// </summary>
public static class RoadSurface
{
    /// <summary>中线折线 + 路宽 → 路带边界闭合多边形(左侧正向 + 右侧反向)。点&lt;2 或宽≤0 → 空。</summary>
    public static List<(double x, double y)> Strip(IReadOnlyList<(double x, double y)> center, double width)
    {
        var poly = new List<(double x, double y)>();
        int n = center?.Count ?? 0;
        if (n < 2 || width <= 0) return poly;
        double h = width / 2;
        var left = new (double x, double y)[n];
        var right = new (double x, double y)[n];
        for (int i = 0; i < n; i++)
        {
            var (mx, my) = MiterOffset(center!, i);   // 斜接偏移向量(转角处按 1/cos(半转角) 拉长, 保持等宽)
            left[i] = (center![i].x + mx * h, center[i].y + my * h);
            right[i] = (center[i].x - mx * h, center[i].y - my * h);
        }
        for (int i = 0; i < n; i++) poly.Add(left[i]);
        for (int i = n - 1; i >= 0; i--) poly.Add(right[i]);
        return poly;
    }

    // 斜接偏移向量：端点=单位法向；内节点=两段法向均值方向、长度 1/cos(半转角)(=1/(m·n1))，使路缘等宽。
    private static (double x, double y) MiterOffset(IReadOnlyList<(double x, double y)> c, int i)
    {
        if (i == 0) return Perp(Dir(c[0], c[1]));
        if (i == c.Count - 1) return Perp(Dir(c[i - 1], c[i]));
        var n1 = Perp(Dir(c[i - 1], c[i]));
        var n2 = Perp(Dir(c[i], c[i + 1]));
        var m = Norm((n1.x + n2.x, n1.y + n2.y));
        double denom = m.x * n1.x + m.y * n1.y;   // cos(半转角)
        if (Math.Abs(denom) < 1e-6) return n1;      // 近 180° 掉头 → 退化用单位法向
        return (m.x / denom, m.y / denom);
    }

    private static (double x, double y) Perp((double x, double y) d) => (-d.y, d.x);

    private static (double x, double y) Dir((double x, double y) a, (double x, double y) b)
        => Norm((b.x - a.x, b.y - a.y));

    private static (double x, double y) Norm((double x, double y) v)
    {
        double l = Math.Sqrt(v.x * v.x + v.y * v.y);
        return l < 1e-12 ? (0, 0) : (v.x / l, v.y / l);
    }
}
