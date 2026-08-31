using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 图案填充（用户定义线图案）—— 在闭合边界内按 角度+间距 生成平行剖面线, 裁剪到边界。
/// 原 PitMine3D「填充」走 C++ 引擎图案库(GetHatchPatternNames/EngineInterop, 命名图案 ANSI31 等不可见 → 记录);
/// 但**用户定义线填充**(单一角度+间距, 或十字交叉)是标准可见几何, 且本 2D 线框渲染器唯一可行的填充形式。
/// 纯逻辑、可单测。返回一批线段 (x1,y1,x2,y2)。
/// </summary>
public static class HatchPattern
{
    /// <summary>
    /// 生成填充线段：boundary 为闭合多边形顶点(自动闭合), angleDeg 剖面线角度, spacing 线距(&gt;0), cross 是否加正交十字线。
    /// </summary>
    public static List<(double x1, double y1, double x2, double y2)> Generate(
        IReadOnlyList<(double x, double y)> boundary, double angleDeg, double spacing, bool cross = false)
    {
        var result = new List<(double, double, double, double)>();
        if (boundary == null || boundary.Count < 3 || spacing <= 1e-9) return result;
        HatchOneDirection(boundary, angleDeg, spacing, result);
        if (cross) HatchOneDirection(boundary, angleDeg + 90.0, spacing, result);
        return result;
    }

    private static void HatchOneDirection(
        IReadOnlyList<(double x, double y)> boundary, double angleDeg, double spacing,
        List<(double, double, double, double)> result)
    {
        double ang = angleDeg * Math.PI / 180.0;
        double ca = Math.Cos(ang), sa = Math.Sin(ang);
        // 旋转到剖面线水平的坐标系: u 沿线方向, v 垂直(扫描轴)。R(-ang): u= x·ca + y·sa, v= -x·sa + y·ca。
        int n = boundary.Count;
        var uv = new (double u, double v)[n];
        double vmin = double.MaxValue, vmax = double.MinValue, umin = double.MaxValue, umax = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double x = boundary[i].x, y = boundary[i].y;
            double u = x * ca + y * sa, v = -x * sa + y * ca;
            uv[i] = (u, v);
            if (v < vmin) vmin = v; if (v > vmax) vmax = v;
            if (u < umin) umin = u; if (u > umax) umax = u;
        }
        if (vmax - vmin < 1e-9) return;

        // 从**严格**大于 vmin 的首条间距网格线开始(跳过与边界重合的扫描线), 逐条 v = k·spacing 扫。
        double v0 = (Math.Floor(vmin / spacing) + 1) * spacing;
        var xs = new List<double>();
        for (double v = v0; v <= vmax - 1e-9; v += spacing)
        {
            xs.Clear();
            // 求扫描线 v 与各边交点的 u。
            for (int i = 0; i < n; i++)
            {
                var (u1, v1) = uv[i];
                var (u2, v2) = uv[(i + 1) % n];
                // 半开区间避免顶点重复计数: [min,max)
                bool between = (v1 <= v && v2 > v) || (v2 <= v && v1 > v);
                if (!between) continue;
                double t = (v - v1) / (v2 - v1);
                xs.Add(u1 + t * (u2 - u1));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            // 偶奇配对: [xs0,xs1],[xs2,xs3],... 为多边形内部区间。
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                double ua = xs[i], ub = xs[i + 1];
                if (ub - ua < 1e-9) continue;
                // 逆旋回原坐标: x= u·ca - v·sa, y= u·sa + v·ca。
                double ax = ua * ca - v * sa, ay = ua * sa + v * ca;
                double bx = ub * ca - v * sa, by = ub * sa + v * ca;
                result.Add((ax, ay, bx, by));
            }
        }
    }
}
