using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 自适应格网规划（对标 AutoCAD 模型空间栅格：随缩放换挡的 1-2-5 间距、每 N 格一条主线、铺满视口看不到边界）。
/// 纯计算, 不含 GL —— 供视口按当前可见范围重建格网几何, 可单测。
/// </summary>
public readonly record struct GridPlan(
    double Minor,      // 细线间距(世界单位)
    double Major,      // 主线间距 = Minor × MajorEvery
    double X0, double Y0, double X1, double Y1,   // 覆盖范围(已按主线对齐并外扩, 世界坐标)
    int VerticalLines, int HorizontalLines)
{
    public int TotalLines => VerticalLines + HorizontalLines;
}

public static class GridPlanner
{
    /// <summary>1-2-5 换挡：不小于 v 的"整"值(…0.1, 0.2, 0.5, 1, 2, 5, 10…)。</summary>
    public static double Nice125(double v)
    {
        if (!(v > 0) || double.IsInfinity(v)) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        double n = m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10;
        return n * p;
    }

    /// <summary>
    /// 按当前视图规划格网。
    /// </summary>
    /// <param name="worldPerPixel">一屏像素对应的世界长度(缩放尺度)。</param>
    /// <param name="viewW">可见宽(世界单位)。</param><param name="viewH">可见高(世界单位)。</param>
    /// <param name="cx">视图中心 X(世界)。</param><param name="cy">视图中心 Y(世界)。</param>
    /// <param name="minPx">细线在屏幕上的最小间距(像素)——低于此就换下一挡, 保证不糊成一片。</param>
    /// <param name="majorEvery">每几格一条主线(AutoCAD 默认 5)。</param>
    /// <param name="coverage">覆盖倍数(1=正好铺满可见区; 3D 用更大值让边界看不见)。</param>
    /// <param name="maxLines">线条上限(超了就整体升一挡, 防大范围下线数爆炸)。</param>
    public static GridPlan For(double worldPerPixel, double viewW, double viewH, double cx, double cy,
                               double minPx = 10, int majorEvery = 5, double coverage = 1.0, int maxLines = 4000)
    {
        if (majorEvery < 2) majorEvery = 2;
        if (coverage < 1) coverage = 1;
        double minor = Nice125(Math.Abs(worldPerPixel) * minPx);
        double w = Math.Max(Math.Abs(viewW), 1e-9) * coverage;
        double h = Math.Max(Math.Abs(viewH), 1e-9) * coverage;

        // 线数超上限 → 逐挡放大间距(1→2→5→10…)，直到落回上限内
        while ((w / minor + h / minor) + 2 > maxLines) minor = Nice125(minor * 1.5);

        double major = minor * majorEvery;
        // 覆盖范围按主线对齐, 使主线落在世界坐标的整数倍处(与 AutoCAD 一致, 原点处即主线)
        double x0 = Math.Floor((cx - w / 2) / major) * major;
        double y0 = Math.Floor((cy - h / 2) / major) * major;
        double x1 = Math.Ceiling((cx + w / 2) / major) * major;
        double y1 = Math.Ceiling((cy + h / 2) / major) * major;

        int nv = (int)Math.Round((x1 - x0) / minor) + 1;
        int nh = (int)Math.Round((y1 - y0) / minor) + 1;
        return new GridPlan(minor, major, x0, y0, x1, y1, nv, nh);
    }

    /// <summary>该坐标是否落在主线上(浮点容差按间距千分之一)。</summary>
    public static bool IsMajor(double v, double major)
        => Math.Abs(v / major - Math.Round(v / major)) < 1e-3;

    /// <summary>已覆盖范围是否仍能罩住新的可见区(用于判断要不要重建几何)。</summary>
    public static bool Covers(in GridPlan p, double cx, double cy, double viewW, double viewH)
        => cx - viewW / 2 >= p.X0 && cx + viewW / 2 <= p.X1
        && cy - viewH / 2 >= p.Y0 && cy + viewH / 2 <= p.Y1;
}
