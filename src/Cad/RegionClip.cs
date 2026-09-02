using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 作业区域裁剪几何（需求07：道路中心线只在作业区域内生成）—— 忠实移植原
/// PointCloudLib.RoadCenterline.RegionClip。纯 XY、零依赖：
///   · 点在多边形（射线法，支持凹多边形）；
///   · 输入预筛：按区域外扩包围盒丢弃远处台阶线（性能用，宽松且安全）；
///   · 输出精裁：把中心线折线裁到区域并集内，边界处 Z 线性插值。
/// 供 <see cref="RoadCenterlineExtractor"/> 产出后限定在作业区域内（Kylin 无 RoadCenterlineRunner 编排器，
/// 由调用方按此顺序 ExpandedBBoxes→FilterBenchLinesByBBox→提取→ClipPolyline 组织）。纯逻辑、可单测。
/// </summary>
public static class RegionClip
{
    /// <summary>射线法：点 (x,y) 是否在多边形内（ring 扁平 [x,y,...]，隐式闭合）。</summary>
    public static bool PointInPolygon(double x, double y, double[] ring)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            bool cross = ((yi > y) != (yj > y)) &&
                         (x < (xj - xi) * (y - yi) / (yj - yi) + xi);
            if (cross) inside = !inside;
        }
        return inside;
    }

    /// <summary>点是否落在任一区域内（并集）。</summary>
    public static bool PointInAny(double x, double y, IReadOnlyList<double[]> rings)
    {
        for (int r = 0; r < rings.Count; r++)
            if (PointInPolygon(x, y, rings[r])) return true;
        return false;
    }

    /// <summary>各区域 XY 包围盒（外扩 buffer）。buffer 应 ≥ 配对可达半径，保证预筛不误删边界线。</summary>
    public static List<(double MinX, double MinY, double MaxX, double MaxY)> ExpandedBBoxes(
        IReadOnlyList<double[]> rings, double buffer)
    {
        var list = new List<(double, double, double, double)>(rings.Count);
        foreach (var ring in rings)
        {
            int n = ring.Length / 2;
            if (n == 0) continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double x = ring[2 * i], y = ring[2 * i + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            list.Add((minX - buffer, minY - buffer, maxX + buffer, maxY + buffer));
        }
        return list;
    }

    /// <summary>输入预筛：丢弃所有顶点都落在(外扩)区域包围盒之外的台阶线。
    /// 宽松（按 buffer 外扩）+ 安全（不删任何可能贡献区域内中心线的线），仅为性能/去噪。</summary>
    public static List<double[]> FilterBenchLinesByBBox(
        IReadOnlyList<double[]> lines,
        List<(double MinX, double MinY, double MaxX, double MaxY)> bboxes)
    {
        var kept = new List<double[]>(lines.Count);
        foreach (var ln in lines)
        {
            int n = ln.Length / 3;
            bool keep = false;
            for (int i = 0; i < n && !keep; i++)
            {
                double x = ln[3 * i], y = ln[3 * i + 1];
                foreach (var b in bboxes)
                    if (x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY) { keep = true; break; }
            }
            if (keep) kept.Add(ln);
        }
        return kept;
    }

    /// <summary>把一条扁平 [x,y,z,...] 折线裁剪到区域并集内，返回区域内的子折线（Z 在边界处线性插值）。
    /// 短于 minKeepLen 的碎段丢弃。逐段按"与区域边交点"切分，中点判内/外（支持凹多边形 + 多区域并集）。</summary>
    public static List<double[]> ClipPolyline(double[] flat, IReadOnlyList<double[]> rings, double minKeepLen)
    {
        var result = new List<double[]>();
        int n = flat.Length / 3;
        if (n < 2) return result;

        const double eps = 1e-6;
        var cur = new List<double>();   // 当前区域内连续段的扁平 xyz

        void Flush()
        {
            if (cur.Count >= 6 && PolyLenXY(cur) >= minKeepLen)
                result.Add(cur.ToArray());
            cur = new List<double>();
        }

        void Append(double x, double y, double z)
        {
            int m = cur.Count;
            if (m >= 3 && Math.Abs(cur[m - 3] - x) < eps && Math.Abs(cur[m - 2] - y) < eps) return; // 重合点跳过
            cur.Add(x); cur.Add(y); cur.Add(z);
        }

        for (int i = 1; i < n; i++)
        {
            double ax = flat[3 * (i - 1)], ay = flat[3 * (i - 1) + 1], az = flat[3 * (i - 1) + 2];
            double bx = flat[3 * i], by = flat[3 * i + 1], bz = flat[3 * i + 2];

            // 段 A→B 与所有区域边的交点参数 t，连同端点 0、1。
            var ts = new List<double> { 0.0, 1.0 };
            foreach (var ring in rings)
            {
                int rn = ring.Length / 2;
                for (int k = 0, j = rn - 1; k < rn; j = k++)
                    if (SegSegT(ax, ay, bx, by, ring[2 * j], ring[2 * j + 1], ring[2 * k], ring[2 * k + 1], out double t)
                        && t > eps && t < 1 - eps)
                        ts.Add(t);
            }
            ts.Sort();

            for (int s = 0; s < ts.Count - 1; s++)
            {
                double t0 = ts[s], t1 = ts[s + 1];
                if (t1 - t0 < eps) continue;
                double tm = 0.5 * (t0 + t1);
                if (!PointInAny(ax + (bx - ax) * tm, ay + (by - ay) * tm, rings)) { Flush(); continue; }

                double p0x = ax + (bx - ax) * t0, p0y = ay + (by - ay) * t0, p0z = az + (bz - az) * t0;
                double p1x = ax + (bx - ax) * t1, p1y = ay + (by - ay) * t1, p1z = az + (bz - az) * t1;
                if (cur.Count == 0) Append(p0x, p0y, p0z);
                else
                {
                    int m = cur.Count;
                    if (Math.Abs(cur[m - 3] - p0x) > 1e-3 || Math.Abs(cur[m - 2] - p0y) > 1e-3)
                    { Flush(); Append(p0x, p0y, p0z); }   // run 间断 → 收束再起新段
                }
                Append(p1x, p1y, p1z);
            }
        }
        Flush();
        return result;
    }

    /// <summary>段 A→B 与段 P→Q 的交点在 A→B 上的参数 t（两段都落在 [0,1] 才算相交）。</summary>
    private static bool SegSegT(double ax, double ay, double bx, double by,
        double px, double py, double qx, double qy, out double t)
    {
        t = 0;
        double rx = bx - ax, ry = by - ay;
        double sx = qx - px, sy = qy - py;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return false;   // 平行/共线
        double tt = ((px - ax) * sy - (py - ay) * sx) / denom;
        double uu = ((px - ax) * ry - (py - ay) * rx) / denom;
        if (tt < 0 || tt > 1 || uu < 0 || uu > 1) return false;
        t = tt;
        return true;
    }

    private static double PolyLenXY(List<double> flat)
    {
        double len = 0; int n = flat.Count / 3;
        for (int i = 1; i < n; i++)
        {
            double dx = flat[3 * i] - flat[3 * (i - 1)], dy = flat[3 * i + 1] - flat[3 * (i - 1) + 1];
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }
}
