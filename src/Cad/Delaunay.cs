using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// Delaunay 三角剖分（Bowyer-Watson）—— 散点 → 三角网（TIN 基础）。
/// 复用 <see cref="ArcMath.Circumcircle"/> 做外接圆判定。纯逻辑、可单测。
/// 返回三角形顶点索引三元组（索引指向输入点表）。
/// </summary>
public static class Delaunay
{
    public static List<(int a, int b, int c)> Triangulate(IReadOnlyList<(double x, double y)> input)
    {
        var result = new List<(int, int, int)>();
        int n = input.Count;
        if (n < 3) return result;

        // 点表 + 超级三角形三个远点(索引 n,n+1,n+2)
        var pts = new List<(double x, double y)>(input);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in input) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        double dmax = System.Math.Max(maxX - minX, maxY - minY); if (dmax < 1e-9) dmax = 1;
        double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
        pts.Add((midX - 20 * dmax, midY - dmax));
        pts.Add((midX + 20 * dmax, midY - dmax));
        pts.Add((midX, midY + 20 * dmax));
        int s0 = n, s1 = n + 1, s2 = n + 2;

        var tris = new List<(int a, int b, int c)> { (s0, s1, s2) };

        for (int ip = 0; ip < n; ip++)
        {
            var (px, py) = pts[ip];
            var bad = new List<(int a, int b, int c)>();
            foreach (var t in tris)
                if (InCircumcircle(pts, t, px, py)) bad.Add(t);

            // 空腔边界 = 只属于一个坏三角形的边
            var edgeCount = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                Bump(edgeCount, t.a, t.b); Bump(edgeCount, t.b, t.c); Bump(edgeCount, t.c, t.a);
            }
            foreach (var t in bad) tris.Remove(t);
            foreach (var kv in edgeCount)
                if (kv.Value == 1) tris.Add((kv.Key.Item1, kv.Key.Item2, ip));
        }

        // 去掉含超级三角形顶点的三角形
        foreach (var t in tris)
            if (t.a < n && t.b < n && t.c < n) result.Add(t);
        return result;
    }

    private static void Bump(Dictionary<(int, int), int> m, int u, int v)
    {
        var key = u < v ? (u, v) : (v, u);
        m[key] = m.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    private static bool InCircumcircle(List<(double x, double y)> pts, (int a, int b, int c) t, double px, double py)
    {
        var A = pts[t.a]; var B = pts[t.b]; var C = pts[t.c];
        var cc = ArcMath.Circumcircle(A.x, A.y, B.x, B.y, C.x, C.y);
        if (cc == null) return false;   // 退化三角形
        double dx = px - cc.Value.cx, dy = py - cc.Value.cy;
        return dx * dx + dy * dy <= cc.Value.r * cc.Value.r + 1e-9;
    }
}
