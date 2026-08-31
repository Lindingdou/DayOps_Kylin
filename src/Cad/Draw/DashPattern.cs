using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 线型虚线化 —— 把一条线段按虚线样式(交替 画/空 长度, 世界单位)切成若干"画"子段。
/// 忠实 CAD 线型(实线/虚线/点划线…)。样式为 [画1,空1,画2,空2,…], 首元为"画"。纯逻辑、可单测。
/// </summary>
public static class DashPattern
{
    /// <summary>常见线型的画/空长度(世界单位, 可随 scale 命令缩放)。</summary>
    public static double[]? ByName(string name, double scale = 1.0)
    {
        double[]? p = name switch
        {
            "实线" or "CONTINUOUS" or "连续" => null,
            "虚线" or "DASHED" or "破折线" => new[] { 6.0, 3.0 },
            "点线" or "DOTTED" => new[] { 0.3, 3.0 },
            "点划线" or "DASHDOT" or "中心线" or "CENTER" => new[] { 9.0, 3.0, 0.3, 3.0 },
            "双点划线" or "DIVIDE" => new[] { 9.0, 3.0, 0.3, 3.0, 0.3, 3.0 },
            _ => null,
        };
        if (p == null || scale == 1.0) return p;
        var q = new double[p.Length];
        for (int i = 0; i < p.Length; i++) q[i] = p[i] * scale;
        return q;
    }

    /// <summary>把线段 (x0,y0)->(x1,y1) 按样式切成"画"子段。样式空/零周期/零长线 → 原样一整段。</summary>
    public static List<(double sx, double sy, double ex, double ey)> Dashes(
        double x0, double y0, double x1, double y1, IReadOnlyList<double>? pattern)
    {
        var res = new List<(double, double, double, double)>();
        double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
        if (pattern == null || pattern.Count == 0) { if (len > 0) res.Add((x0, y0, x1, y1)); return res; }
        if (len < 1e-12) return res;
        double period = 0; foreach (var s in pattern) period += Math.Abs(s);
        if (period < 1e-9) { res.Add((x0, y0, x1, y1)); return res; }
        double ux = dx / len, uy = dy / len;
        double pos = 0; int idx = 0; bool draw = true;   // pattern[0] = 画
        // 上限: 满周期数×元素数 + 余量; 防样式含极小元素时迭代过多
        int cap = (int)(len / period) * pattern.Count + pattern.Count + 16;
        for (int guard = 0; pos < len - 1e-12 && guard < cap; guard++)
        {
            double seg = Math.Abs(pattern[idx % pattern.Count]);
            double end = seg < 1e-9 ? pos : Math.Min(pos + seg, len);   // 零长元素原地跳过
            if (draw && end > pos + 1e-12) res.Add((x0 + ux * pos, y0 + uy * pos, x0 + ux * end, y0 + uy * end));
            pos = end; idx++; draw = !draw;
        }
        return res;
    }
}
