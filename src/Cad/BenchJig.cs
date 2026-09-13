using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「动态调整」（台阶交互设计 jig）的纯逻辑：原版 <c>StartBenchDesignJig</c> 在内核里闭环——
/// 选 1 条境界线后移动鼠标调开采深度/层数、实时预览坡面，点击/回车确认。内核无源，这里托管等价：
/// <list type="bullet">
/// <item><b>光标 → 深度</b>：取光标到境界线的平面距离 d，按一级台阶的水平占位（W + H/tanα）折成级数 —— 光标离线越远级数越多，
///   预览最内一圈大致跟着光标走，手感与"拖深度"一致；</item>
/// <item><b>预览</b>：每级坡顶/坡脚环（<see cref="BenchBuilder.Build"/> 的 Levels），由调用方镶嵌到预览通道；</item>
/// <item><b>确认</b>：按最后的级数落地（坡面 + 平盘 + 坡脚线），同「批量台阶扩帮」产物。</item>
/// </list>
/// 原版 spike 局限照记：只驱动"深度/层数"一个维度，H/α/W 固定。
/// </summary>
public static class BenchJig
{
    /// <summary>光标到折线（闭合则含收尾段）的最短平面距离。</summary>
    public static double DistanceToPolyline(IReadOnlyList<(double x, double y)> pts, bool closed, double px, double py)
    {
        if (pts == null || pts.Count == 0) return double.PositiveInfinity;
        if (pts.Count == 1) return Math.Sqrt((pts[0].x - px) * (pts[0].x - px) + (pts[0].y - py) * (pts[0].y - py));
        double best = double.PositiveInfinity;
        int n = pts.Count;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            double d = PointSegDist(px, py, a.x, a.y, b.x, b.y);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// 光标距离 → 级数：一级台阶在平面上占 run = H/tanα + W；级数 = round(d / run)，至少 1 级、封顶 maxLevels。
    /// 距离取整（而非取上）是为了"光标停在第 k 圈附近就是 k 级"。
    /// </summary>
    public static int LevelsForDistance(double distance, double benchH, double faceAngleDeg, double bermW, int maxLevels)
    {
        if (maxLevels < 1) maxLevels = 1;
        if (benchH <= 1e-9 || faceAngleDeg <= 1 || faceAngleDeg >= 89.9) return 1;
        double run = benchH / Math.Tan(faceAngleDeg * Math.PI / 180.0) + Math.Max(0, bermW);
        if (run <= 1e-9 || double.IsNaN(distance) || double.IsInfinity(distance)) return 1;
        int k = (int)Math.Round(distance / run);
        return Math.Clamp(k, 1, maxLevels);
    }

    private static double PointSegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double l2 = dx * dx + dy * dy;
        double t = l2 <= 1e-18 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double qx = ax + t * dx, qy = ay + t * dy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }
}
