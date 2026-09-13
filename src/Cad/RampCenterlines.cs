using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 参数化坑线【中线生成器】——螺旋 / 折返（忠实移植原 <c>MineAssLib.RoadLayout.ParametricCenterlines</c>,
/// 数值口径镜像 C++ GenerateRamp.cpp）。只出 XY 骨架 + 参考 Z(贴面器会重定 Z、横断面另算),
/// 纯几何、不依赖引擎、可单测。落地/切帮部分为内核规模, 不在此。
/// </summary>
public static class RampCenterlines
{
    /// <summary>螺旋：固定半径圆螺旋, 绕 turns 圈沿弧长匀降。ccw=逆时针。gradePct 正=下降。</summary>
    public static List<(double X, double Y, double Z)> Spiral(
        double centerX, double centerY, double startZ,
        double radius, double startAngleDeg, double turns,
        bool ccw, double gradePct, int segPerTurn = 36)
    {
        var pts = new List<(double, double, double)>();
        if (radius <= 1e-6 || turns <= 1e-9) return pts;

        int n = Math.Max(8, (int)Math.Min(20000.0, Math.Ceiling(turns * Math.Max(8, segPerTurn))));
        double sign = ccw ? +1.0 : -1.0;
        double a0 = startAngleDeg * Math.PI / 180.0;
        double span = turns * 2.0 * Math.PI;
        double totalArc = turns * 2.0 * Math.PI * radius;
        double dzTotal = -(gradePct / 100.0) * totalArc;

        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            double a = a0 + sign * t * span;
            pts.Add((centerX + radius * Math.Cos(a), centerY + radius * Math.Sin(a), startZ + t * dzTotal));
        }
        return pts;
    }

    /// <summary>直线：从起点沿方位角直降, 按 gradePct 匀降 length 长度。gradePct 正=下降。忠实原 StraightRampAutoRouter 的直线中线核(全套可行性路由为内核规模, 不在此)。</summary>
    public static List<(double X, double Y, double Z)> Straight(
        double startX, double startY, double startZ,
        double azimuthDeg, double gradePct, double length, double stepM = 8.0)
    {
        var pts = new List<(double, double, double)>();
        if (length <= 1e-6 || stepM <= 1e-6) return pts;
        double th = azimuthDeg * Math.PI / 180.0;
        double hx = Math.Cos(th), hy = Math.Sin(th);
        double g = gradePct / 100.0;
        int n = Math.Max(1, (int)Math.Ceiling(length / stepM));
        for (int i = 0; i <= n; i++)
        {
            double s = Math.Min(length, i * stepM);
            pts.Add((startX + hx * s, startY + hy * s, startZ - g * s));
            if (s >= length) break;
        }
        return pts;
    }

    /// <summary>折返：直腿 + 180° 回头弧往返, 逐腿下降。turnSide +1=向左甩/-1=向右甩; legs≥2。</summary>
    public static List<(double X, double Y, double Z)> Switchback(
        double startX, double startY, double startZ,
        double azimuthDeg, int turnSide, int legs,
        double legLength, double gradePct, double curveGradePct, double radius,
        double legStepM = 8.0, int arcSeg = 18)
    {
        var pts = new List<(double, double, double)>();
        if (legs < 2 || legLength <= 1e-6 || radius <= 1e-6) return pts;

        double sgn = turnSide >= 0 ? +1.0 : -1.0;
        double gLeg = gradePct / 100.0;
        double gArc = curveGradePct / 100.0;
        double th = azimuthDeg * Math.PI / 180.0;
        double hx = Math.Cos(th), hy = Math.Sin(th);
        double cx = startX, cy = startY, z = startZ;

        pts.Add((cx, cy, z));
        for (int k = 0; k < legs; k++)
        {
            int nseg = Math.Max(1, (int)Math.Ceiling(legLength / Math.Max(0.5, legStepM)));
            for (int i = 1; i <= nseg; i++)
            {
                double t = (double)i / nseg;
                pts.Add((cx + hx * legLength * t, cy + hy * legLength * t, z - gLeg * legLength * t));
            }
            cx += hx * legLength; cy += hy * legLength; z -= gLeg * legLength;
            if (k == legs - 1) break;

            double nlx = -hy, nly = hx;
            double ccx = cx + sgn * nlx * radius, ccy = cy + sgn * nly * radius;
            double a0 = Math.Atan2(cy - ccy, cx - ccx);
            double dzArc = gArc * (Math.PI * radius);
            int seg = Math.Max(4, arcSeg);
            for (int i = 1; i <= seg; i++)
            {
                double t = (double)i / seg;
                double a = a0 + sgn * Math.PI * t;
                pts.Add((ccx + radius * Math.Cos(a), ccy + radius * Math.Sin(a), z - dzArc * t));
            }
            double a1 = a0 + sgn * Math.PI;
            cx = ccx + radius * Math.Cos(a1); cy = ccy + radius * Math.Sin(a1); z -= dzArc;
            hx = -hx; hy = -hy;
        }
        return pts;
    }

    /// <summary>
    /// 折返甩向自动判定（忠实原 ParametricCenterlines.AutoTurnSide）：在起点四周探 ∇z，第一腿走向 = 下坡方向转 +90°，
    /// 马步朝下坡的那一侧。平地 / 采不到面 ⇒ false（不许猜）。
    /// </summary>
    public static bool AutoTurnSide(IRoadZSampler? sampler, double x, double y, double probeM,
                                    out double azimuthDeg, out int turnSide)
    {
        azimuthDeg = 0; turnSide = +1;
        if (sampler == null) return false;
        double h = Math.Max(1.0, probeM);
        if (!sampler.TrySample(x + h, y, out double zpx) || !sampler.TrySample(x - h, y, out double znx)
         || !sampler.TrySample(x, y + h, out double zpy) || !sampler.TrySample(x, y - h, out double zny))
            return false;
        double gx = (zpx - znx) / (2.0 * h), gy = (zpy - zny) / (2.0 * h);
        double dnx = -gx, dny = -gy;
        double dl = Math.Sqrt(dnx * dnx + dny * dny);
        if (dl < 1e-9) return false;
        dnx /= dl; dny /= dl;
        double hx = -dny, hy = dnx;
        azimuthDeg = Math.Atan2(hy, hx) * 180.0 / Math.PI;
        double lnx = -hy, lny = hx;
        turnSide = (lnx * dnx + lny * dny >= 0.0) ? +1 : -1;
        return true;
    }
}
