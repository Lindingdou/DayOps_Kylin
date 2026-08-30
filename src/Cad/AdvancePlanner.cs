using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>工作线推进方式。</summary>
public enum AdvanceMode { Parallel, FixedPivot, MovingPivot }

/// <summary>
/// 工作线推进几何生成（忠实移植原 <c>PlanLib.BoundaryOptimization.AdvancePlanner</c>）——
/// 从初始拉沟(开段沟)工作线, 按推进方式生成各推进步的工作线序列(XY 平面)：
///   平行推进 = 沿推进方向平移 k·b；定点回转 = 绕固定瞬心转 k·Δθ(Δθ=b/R_ref, 转角守恒)；
///   动点回转 = 逐顶点沿(指向 advance 侧)法向偏移 b(采宽守恒)。纯函数、确定性、可单测。
/// </summary>
public static class AdvancePlanner
{
    /// <param name="boxcut">初始工作线 [x0,y0,x1,y1,...](开段沟)。</param>
    /// <param name="stepB">采宽 b(每步推进量 m)。</param>
    /// <returns>各推进步工作线(含初始拉沟, index0)。</returns>
    public static List<double[]> GenerateWorkingLines(double[] boxcut, AdvanceMode mode,
        double advanceAzDeg, double pivotX, double pivotY, double stepB, int maxSteps)
    {
        var result = new List<double[]>();
        if (boxcut == null || boxcut.Length < 4 || stepB <= 1e-6 || maxSteps <= 0) return result;
        result.Add((double[])boxcut.Clone());

        double az = advanceAzDeg * Math.PI / 180.0;
        double ux = Math.Cos(az), uy = Math.Sin(az);

        switch (mode)
        {
            case AdvanceMode.Parallel:
                for (int k = 1; k <= maxSteps; k++)
                    result.Add(Translate(boxcut, k * stepB * ux, k * stepB * uy));
                break;

            case AdvanceMode.FixedPivot:
            {
                double rref = AvgRadius(boxcut, pivotX, pivotY);
                if (rref < 1e-6) rref = stepB;
                double dth = stepB / rref;
                for (int k = 1; k <= maxSteps; k++)
                    result.Add(Rotate(boxcut, pivotX, pivotY, k * dth));
                break;
            }

            case AdvanceMode.MovingPivot:
            {
                var cur = (double[])boxcut.Clone();
                for (int k = 1; k <= maxSteps; k++)
                {
                    cur = OffsetByNormal(cur, stepB, ux, uy);
                    result.Add(cur);
                }
                break;
            }
        }
        return result;
    }

    private static double[] Translate(double[] p, double dx, double dy)
    {
        var r = new double[p.Length];
        for (int i = 0; i < p.Length; i += 2) { r[i] = p[i] + dx; r[i + 1] = p[i + 1] + dy; }
        return r;
    }

    private static double[] Rotate(double[] p, double cx, double cy, double ang)
    {
        double c = Math.Cos(ang), s = Math.Sin(ang);
        var r = new double[p.Length];
        for (int i = 0; i < p.Length; i += 2)
        {
            double x = p[i] - cx, y = p[i + 1] - cy;
            r[i] = cx + x * c - y * s;
            r[i + 1] = cy + x * s + y * c;
        }
        return r;
    }

    private static double AvgRadius(double[] p, double cx, double cy)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < p.Length; i += 2) { double dx = p[i] - cx, dy = p[i + 1] - cy; sum += Math.Sqrt(dx * dx + dy * dy); n++; }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>逐顶点沿(指向 advance 侧)法向偏移 b。直线→平移; 折线→各顶点沿角平分法向移动。</summary>
    private static double[] OffsetByNormal(double[] p, double b, double ux, double uy)
    {
        int n = p.Length / 2;
        var r = new double[p.Length];
        for (int i = 0; i < n; i++)
        {
            double nx = 0, ny = 0;
            if (i > 0) AddEdgeNormal(p, i - 1, i, ux, uy, ref nx, ref ny);
            if (i < n - 1) AddEdgeNormal(p, i, i + 1, ux, uy, ref nx, ref ny);
            double len = Math.Sqrt(nx * nx + ny * ny);
            if (len < 1e-9) { nx = ux; ny = uy; len = 1; }
            nx /= len; ny /= len;
            r[i * 2] = p[i * 2] + b * nx;
            r[i * 2 + 1] = p[i * 2 + 1] + b * ny;
        }
        return r;
    }

    private static void AddEdgeNormal(double[] p, int a, int c, double ux, double uy, ref double nx, ref double ny)
    {
        double ex = p[c * 2] - p[a * 2], ey = p[c * 2 + 1] - p[a * 2 + 1];
        double n1x = -ey, n1y = ex;
        if (n1x * ux + n1y * uy < 0) { n1x = -n1x; n1y = -n1y; }
        double l = Math.Sqrt(n1x * n1x + n1y * n1y); if (l < 1e-9) l = 1;
        nx += n1x / l; ny += n1y / l;
    }
}
