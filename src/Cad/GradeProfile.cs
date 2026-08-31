using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三维折线纵坡(纵向坡度)分析 —— 逐段算 坡度% = Δz/水平距×100，供运输道路/坡道限坡校核
/// (忠实原 RoadLib「中心线按纵坡分档着色」的核)。输入有序 (x,y,z) 折线(如中线 drape 后)。纯逻辑、可单测。
/// </summary>
public static class GradeProfile
{
    /// <summary>一段：两端点 + 纵坡%(有向, 上坡正) + 水平长。</summary>
    public readonly record struct Seg(double X0, double Y0, double X1, double Y1, double GradePct, double HorizLen);

    /// <summary>逐段纵坡。点&lt;2 返回空。水平长≈0 的段坡度记 0(竖直段)。</summary>
    public static List<Seg> Compute(IReadOnlyList<(double x, double y, double z)> line)
    {
        var segs = new List<Seg>();
        if (line == null || line.Count < 2) return segs;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i]; var b = line[i + 1];
            double dh = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
            double grade = dh > 1e-9 ? (b.z - a.z) / dh * 100.0 : 0;
            segs.Add(new Seg(a.x, a.y, b.x, b.y, grade, dh));
        }
        return segs;
    }

    /// <summary>汇总：最大绝对纵坡%、超限段数(|坡|&gt;maxPct)、加权平均绝对纵坡%(按水平长)。</summary>
    public static (double maxAbs, int overCount, double avgAbs) Summary(IReadOnlyList<Seg> segs, double maxPct)
    {
        double maxAbs = 0, wsum = 0, lsum = 0; int over = 0;
        foreach (var s in segs)
        {
            double g = Math.Abs(s.GradePct);
            if (g > maxAbs) maxAbs = g;
            if (g > maxPct + 1e-9) over++;
            wsum += g * s.HorizLen; lsum += s.HorizLen;
        }
        return (maxAbs, over, lsum > 1e-9 ? wsum / lsum : 0);
    }
}
