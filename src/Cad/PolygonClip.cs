using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多边形裁剪（Sutherland-Hodgman）—— 用凸多边形 clip(逆时针) 裁剪 subject，返回交集多边形。
/// 用于采区/境界裁剪。纯逻辑、可单测。clip 须凸+逆时针(可先取凸包)。
/// </summary>
public static class PolygonClip
{
    public static List<(double x, double y)> Clip(
        IReadOnlyList<(double x, double y)> subject, IReadOnlyList<(double x, double y)> clip)
    {
        var output = new List<(double x, double y)>(subject);
        if (clip.Count < 3) return output;
        for (int i = 0; i < clip.Count; i++)
        {
            var a = clip[i]; var b = clip[(i + 1) % clip.Count];   // 裁剪边 a→b
            var input = output;
            output = new List<(double x, double y)>();
            if (input.Count == 0) break;
            for (int j = 0; j < input.Count; j++)
            {
                var P = input[j]; var Q = input[(j + 1) % input.Count];
                bool pin = Inside(P, a, b), qin = Inside(Q, a, b);
                if (qin)
                {
                    if (!pin) output.Add(Isect(P, Q, a, b));
                    output.Add(Q);
                }
                else if (pin) output.Add(Isect(P, Q, a, b));
            }
        }
        return output;
    }

    private static bool Inside((double x, double y) p, (double x, double y) a, (double x, double y) b)
        => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) >= -1e-9;   // a→b 左侧(逆时针内)

    private static (double x, double y) Isect((double x, double y) p, (double x, double y) q, (double x, double y) a, (double x, double y) b)
    {
        var r = LineMath.IntersectInfinite(p.x, p.y, q.x, q.y, a.x, a.y, b.x, b.y);
        return r ?? q;
    }
}
