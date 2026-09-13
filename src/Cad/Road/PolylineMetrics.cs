// 忠实移植自原 PitMine3D Modules/RoadLib/Evolution/PolylineMetrics.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 两期演化判定用的纯几何度量（基于 RoadLib 自己的 <see cref="Point3d"/>，不引几何内核）。
/// 全部按平面(XY)算——道路演化看的是平面走向与覆盖，高程只随点带过。无状态、可单测。
/// 「点在对期有没有对应路面」那一问在 <see cref="GeometryIndex"/>，不在这里。
/// </summary>
internal static class PolylineMetrics
{
    /// <summary>平面(XY)累计长度 m。</summary>
    public static double Length2D(IReadOnlyList<Point3d> pl)
    {
        double s = 0;
        for (int i = 1; i < pl.Count; i++) s += pl[i].HorizontalDistanceTo(pl[i - 1]);
        return s;
    }

    /// <summary>按平面弧长等步长重采样（保留首末点）。step&lt;=0 或点不足时原样返回。</summary>
    public static List<Point3d> Resample(IReadOnlyList<Point3d> pl, double step)
    {
        var outPts = new List<Point3d>();
        int cnt = pl.Count;
        if (cnt == 0) return outPts;
        if (cnt < 2 || step <= 0)
        {
            for (int i = 0; i < cnt; i++) outPts.Add(pl[i]);
            return outPts;
        }

        outPts.Add(pl[0]);
        double walked = 0, next = step;
        for (int i = 1; i < cnt; i++)
        {
            var a = pl[i - 1];
            var b = pl[i];
            double segLen = b.HorizontalDistanceTo(a);
            if (segLen < 1e-9) continue;
            double segStart = walked, segEnd = walked + segLen;
            while (next <= segEnd + 1e-9)
            {
                double t = (next - segStart) / segLen;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                outPts.Add(new Point3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t));
                next += step;
            }
            walked = segEnd;
        }

        var last = pl[cnt - 1];
        if (outPts[outPts.Count - 1].HorizontalDistanceTo(last) > step * 0.25) outPts.Add(last);
        return outPts;
    }

    /// <summary>
    /// 取重采样序列上 [<paramref name="fromM"/>, <paramref name="toM"/>] 这一段的几何（端点按弧长插值）。
    /// <paramref name="arc"/> 是 <paramref name="s"/> 的逐点累计弧长。段与段首尾相接、拼起来正好是原线（RE8）。
    /// </summary>
    public static List<Point3d> SubPolyline(IReadOnlyList<Point3d> s, double[] arc, double fromM, double toM)
    {
        var outPts = new List<Point3d>();
        int n = s.Count;
        if (n < 2 || toM <= fromM) return outPts;

        outPts.Add(At(s, arc, fromM));
        for (int i = 0; i < n; i++)
            if (arc[i] > fromM + 1e-9 && arc[i] < toM - 1e-9)
                outPts.Add(s[i]);
        outPts.Add(At(s, arc, toM));
        return outPts;
    }

    /// <summary>重采样序列上指定弧长处的点（线性插值，越界夹到端点）。</summary>
    private static Point3d At(IReadOnlyList<Point3d> s, double[] arc, double m)
    {
        int n = s.Count;
        if (m <= arc[0]) return s[0];
        if (m >= arc[n - 1]) return s[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (arc[mid] <= m) lo = mid; else hi = mid;
        }
        double seg = arc[hi] - arc[lo];
        double t = seg < 1e-12 ? 0 : (m - arc[lo]) / seg;
        var a = s[lo];
        var b = s[hi];
        return new Point3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    }

    /// <summary>采样序列在第 i 点的单位切向(XY，中心差分；端点退化用单边)。</summary>
    public static (double X, double Y) SampleTangent(IReadOnlyList<Point3d> s, int i)
    {
        int a = Math.Max(0, i - 1), b = Math.Min(s.Count - 1, i + 1);
        if (a == b) return (1, 0);
        double dx = s[b].X - s[a].X, dy = s[b].Y - s[a].Y;
        double l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (1.0, 0.0) : (dx / l, dy / l);
    }

    /// <summary>折线某端朝外的单位切向(XY)。atStart=true 取首端朝外，否则末端朝外。</summary>
    public static (double X, double Y) OutwardTangent(IReadOnlyList<Point3d> s, bool atStart)
    {
        int n = s.Count;
        if (n < 2) return (1, 0);
        Point3d a, b;
        if (atStart) { a = s[1]; b = s[0]; }
        else { a = s[n - 2]; b = s[n - 1]; }
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (1.0, 0.0) : (dx / l, dy / l);
    }
}
