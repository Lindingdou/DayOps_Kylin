using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 坐标转换 —— 由控制点对(源→目标)最小二乘求 4 参数 Helmert 变换(平移+旋转+均匀缩放)。
/// x' = a·x − b·y + tx，y' = b·x + a·y + ty（a=s·cosθ, b=s·sinθ）。纯逻辑、可单测。
/// </summary>
public static class CoordTransform
{
    /// <summary>≥2 对控制点 → (a,b,tx,ty)；点太少/退化返回 null。</summary>
    public static (double a, double b, double tx, double ty)? Solve(
        IReadOnlyList<(double sx, double sy, double dx, double dy)> pairs)
    {
        int n = pairs.Count;
        if (n < 2) return null;
        double sxm = 0, sym = 0, dxm = 0, dym = 0;
        foreach (var p in pairs) { sxm += p.sx; sym += p.sy; dxm += p.dx; dym += p.dy; }
        sxm /= n; sym /= n; dxm /= n; dym /= n;

        double sd = 0, num_a = 0, num_b = 0;
        foreach (var p in pairs)
        {
            double x = p.sx - sxm, y = p.sy - sym, X = p.dx - dxm, Y = p.dy - dym;
            sd += x * x + y * y;
            num_a += x * X + y * Y;
            num_b += x * Y - y * X;
        }
        if (sd < 1e-12) return null;
        double a = num_a / sd, b = num_b / sd;
        double tx = dxm - (a * sxm - b * sym);
        double ty = dym - (b * sxm + a * sym);
        return (a, b, tx, ty);
    }

    /// <summary>转为可套用的仿射变换。</summary>
    public static Affine2 ToAffine((double a, double b, double tx, double ty) h)
        => new(h.a, h.b, -h.b, h.a, h.tx, h.ty);
}
