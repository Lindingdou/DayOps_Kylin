using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多段线加密纯几何核（对应原 CAD 端 POLYDENSIFY 算子，原走内核, 此为托管重算）：
/// 逐段若长度 &gt; maxStep 则匀分插入内点, 使每子段长 ≤ maxStep。纯函数、可单测。
/// (抽稀/简化见既有 <see cref="PolylineSimplify"/> Douglas-Peucker。)
/// </summary>
public static class PolylineEdit
{
    /// <summary>
    /// 加密：逐段若长度 &gt; maxStep 则匀分插入内点, 使每子段长 ≤ maxStep。closed 时含闭合段。
    /// 返回新点表(不改输入)。maxStep ≤ 0 或点不足时原样返回。
    /// </summary>
    public static List<(double x, double y)> Densify(IReadOnlyList<(double x, double y)> pts, bool closed, double maxStep)
    {
        var outPts = new List<(double, double)>();
        if (pts == null || pts.Count < 2) { if (pts != null) outPts.AddRange(pts); return outPts; }
        if (maxStep <= 1e-12) { outPts.AddRange(pts); return outPts; }
        int n = pts.Count;
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < segCount; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            outPts.Add(a);
            double dx = b.x - a.x, dy = b.y - a.y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > maxStep)
            {
                int div = (int)Math.Ceiling(len / maxStep);   // 子段数, 每段 ≤ maxStep
                for (int k = 1; k < div; k++)
                {
                    double t = (double)k / div;
                    outPts.Add((a.x + dx * t, a.y + dy * t));
                }
            }
        }
        if (!closed) outPts.Add(pts[n - 1]);   // 开线补末点(闭线末点=起点, 由 Closed 隐含)
        return outPts;
    }
}
