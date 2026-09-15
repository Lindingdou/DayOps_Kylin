using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 对象捕捉（OSNAP）几何核 —— 忠实原 PitMine3D 六模式：端点/中点/最近/圆心/交点/垂足。
/// 原走 C++ 引擎(EngineInterop.PitMine_SetSnapMode)，但六模式皆标准可见几何 → 托管重算。
/// 输入为从场景抽取的原语(线段/圆/圆弧/点)，纯逻辑、可单测。
/// </summary>
public static partial class ObjectSnap
{
    /// <summary>六捕捉模式（位掩码 = 1&lt;&lt;(int)Mode）。优先级见 Find。</summary>
    public enum Mode { Endpoint = 0, Midpoint = 1, Center = 2, Intersection = 3, Perpendicular = 4, Nearest = 5 }

    public readonly struct Seg
    {
        public readonly double X1, Y1, X2, Y2;
        public Seg(double x1, double y1, double x2, double y2) { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
    }

    public readonly struct Circ
    {
        public readonly double Cx, Cy, R;
        public Circ(double cx, double cy, double r) { Cx = cx; Cy = cy; R = r; }
    }

    /// <summary>圆弧：中心 + 半径 + 起止角(弧度, 逆时针 A0→A1)。端点/中点/最近用它。</summary>
    public readonly struct ArcP
    {
        public readonly double Cx, Cy, R, A0, A1;
        public ArcP(double cx, double cy, double r, double a0, double a1) { Cx = cx; Cy = cy; R = r; A0 = a0; A1 = a1; }
    }

    public readonly struct Hit
    {
        public readonly double X, Y; public readonly Mode Mode;
        public Hit(double x, double y, Mode m) { X = x; Y = y; Mode = m; }
    }

    // 优先级（小=高）：端点 > 交点 > 圆心 > 中点 > 垂足 > 最近（AutoCAD 习惯）。
    private static readonly int[] Priority = { 0, 3, 2, 1, 4, 5 };  // 索引=Mode，值=排序权

    // 交点捕捉是候选线段两两求交，局部线条过密时会退化为 O(n²)。整图视图下一个 12px
    // 捕捉框可能覆盖几百米、装进十万条线；此时没有唯一可辨认的交点，宁可本帧不吸交点，
    // 等用户放大后候选自然降下来再恢复。25 万对把单帧工作量限制在交互级别。
    private const long MaxIntersectionPairChecks = 250_000;

    public static int MaskOf(params Mode[] modes)
    {
        int m = 0; foreach (var x in modes) m |= 1 << (int)x; return m;
    }

    public static readonly int AllModes =
        (1 << 6) - 1;  // 端点|中点|圆心|交点|垂足|最近

    /// <summary>
    /// 在容差 tol 内找最佳捕捉点：先按模式优先级、再按到光标距离。anchor 为进行中绘制的起锚点(垂足需要)，无则跳过垂足。
    /// </summary>
    public static Hit? Find(
        IReadOnlyList<Seg> segs, IReadOnlyList<Circ> circles, IReadOnlyList<ArcP> arcs, IReadOnlyList<(double x, double y)> pts,
        double cx, double cy, double tol, int modeMask, (double x, double y)? anchor)
        => Find(segs, circles, arcs, pts, cx, cy, tol, modeMask, anchor, null);

    // cand≠null 时只遍历网格给的候选(见 Index)：候选是几何超集且按原下标升序，
    // 故命中与全量遍历完全一致，只是不再逐帧扫全场景。
    internal static Hit? Find(
        IReadOnlyList<Seg> segs, IReadOnlyList<Circ> circles, IReadOnlyList<ArcP> arcs, IReadOnlyList<(double x, double y)> pts,
        double cx, double cy, double tol, int modeMask, (double x, double y)? anchor, Cand? cand)
    {
        if (tol <= 0) return null;
        double tol2 = tol * tol;
        Hit? best = null; int bestPrio = int.MaxValue; double bestD2 = double.MaxValue;

        void Consider(double x, double y, Mode m)
        {
            double dx = x - cx, dy = y - cy, d2 = dx * dx + dy * dy;
            if (d2 > tol2) return;
            int prio = Priority[(int)m];
            if (prio < bestPrio || (prio == bestPrio && d2 < bestD2))
            { best = new Hit(x, y, m); bestPrio = prio; bestD2 = d2; }
        }
        bool On(Mode m) => (modeMask & (1 << (int)m)) != 0;

        // 端点 / 中点 / 最近（线段）
        if (segs != null)
            for (int k = 0, kn = cand?.Segs.Count ?? segs.Count; k < kn; k++)
            {
                var s = segs[cand == null ? k : cand.Segs[k]];
                if (On(Mode.Endpoint)) { Consider(s.X1, s.Y1, Mode.Endpoint); Consider(s.X2, s.Y2, Mode.Endpoint); }
                if (On(Mode.Midpoint)) Consider((s.X1 + s.X2) * 0.5, (s.Y1 + s.Y2) * 0.5, Mode.Midpoint);
                if (On(Mode.Nearest)) { var (px, py) = ClosestOnSeg(s, cx, cy); Consider(px, py, Mode.Nearest); }
                if (On(Mode.Perpendicular) && anchor != null)
                { var (px, py) = FootOnLine(s, anchor.Value.x, anchor.Value.y); if (OnSeg(s, px, py)) Consider(px, py, Mode.Perpendicular); }
            }

        // 点原语（=端点/节点捕捉）
        if (pts != null && On(Mode.Endpoint))
            for (int k = 0, kn = cand?.Pts.Count ?? pts.Count; k < kn; k++)
            { var p = pts[cand == null ? k : cand.Pts[k]]; Consider(p.x, p.y, Mode.Endpoint); }

        // 圆：圆心 / 最近（径向投影）
        if (circles != null)
            for (int k = 0, kn = cand?.Circles.Count ?? circles.Count; k < kn; k++)
            {
                var c = circles[cand == null ? k : cand.Circles[k]];
                if (On(Mode.Center)) Consider(c.Cx, c.Cy, Mode.Center);
                if (On(Mode.Nearest)) { var (px, py) = ClosestOnCircle(c, cx, cy); Consider(px, py, Mode.Nearest); }
            }

        // 圆弧：圆心 / 端点 / 中点 / 最近
        if (arcs != null)
            for (int k = 0, kn = cand?.Arcs.Count ?? arcs.Count; k < kn; k++)
            {
                var a = arcs[cand == null ? k : cand.Arcs[k]];
                if (On(Mode.Center)) Consider(a.Cx, a.Cy, Mode.Center);
                if (On(Mode.Endpoint))
                {
                    Consider(a.Cx + a.R * Math.Cos(a.A0), a.Cy + a.R * Math.Sin(a.A0), Mode.Endpoint);
                    Consider(a.Cx + a.R * Math.Cos(a.A1), a.Cy + a.R * Math.Sin(a.A1), Mode.Endpoint);
                }
                if (On(Mode.Midpoint))
                { double am = MidAngle(a.A0, a.A1); Consider(a.Cx + a.R * Math.Cos(am), a.Cy + a.R * Math.Sin(am), Mode.Midpoint); }
                if (On(Mode.Nearest))
                {
                    double ang = Math.Atan2(cy - a.Cy, cx - a.Cx);
                    if (AngleInArc(ang, a.A0, a.A1)) Consider(a.Cx + a.R * Math.Cos(ang), a.Cy + a.R * Math.Sin(ang), Mode.Nearest);
                }
            }

        // 交点：线段∩线段 / 线段∩圆 / 圆∩圆（仅取落在光标容差内的）。为控代价，先快拒远原语。
        if (On(Mode.Intersection) && segs != null)
        {
            var near = cand?.NearBuf ?? new List<Seg>();
            near.Clear();
            for (int k = 0, kn = cand?.SegsWide.Count ?? segs.Count; k < kn; k++)
            { var s = segs[cand == null ? k : cand.SegsWide[k]]; if (SegNearCursor(s, cx, cy, tol)) near.Add(s); }
            var nearCircles = cand?.NearCircleBuf ?? new List<Circ>();
            nearCircles.Clear();
            if (circles != null)
                for (int k = 0, kn = cand?.CirclesWide.Count ?? circles.Count; k < kn; k++)
                {
                    var c = circles[cand == null ? k : cand.CirclesWide[k]];
                    if (CircNearCursor(c, cx, cy, tol)) nearCircles.Add(c);
                }

            // 预算必须基于真正经过光标孔径的原语，而不是网格粗候选；否则同格但孔径外的大量线会吞掉有效交点。
            long segPairChecks = (long)near.Count * (near.Count - 1) / 2;
            if (segPairChecks <= MaxIntersectionPairChecks)
                for (int i = 0; i < near.Count; i++)
                    for (int j = i + 1; j < near.Count; j++)
                        if (SegSegIntersect(near[i], near[j], out double ix, out double iy)) Consider(ix, iy, Mode.Intersection);

            // 两类求交独立限额：圆很多时不能连有效的线—线交点也一并禁掉。
            long segCircleChecks = (long)near.Count * nearCircles.Count;
            if (segCircleChecks <= MaxIntersectionPairChecks)
                foreach (var s in near)
                    foreach (var c in nearCircles)
                        if (SegCircleIntersect(s, c, cx, cy, tol, Consider)) { }
        }

        return best;
    }

    // ── 几何工具 ──
    private static (double x, double y) ClosestOnSeg(Seg s, double px, double py)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return (s.X1, s.Y1);
        double t = ((px - s.X1) * dx + (py - s.Y1) * dy) / len2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return (s.X1 + t * dx, s.Y1 + t * dy);
    }

    private static (double x, double y) FootOnLine(Seg s, double px, double py)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return (s.X1, s.Y1);
        double t = ((px - s.X1) * dx + (py - s.Y1) * dy) / len2;
        return (s.X1 + t * dx, s.Y1 + t * dy);
    }

    private static bool OnSeg(Seg s, double px, double py)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return false;
        double t = ((px - s.X1) * dx + (py - s.Y1) * dy) / len2;
        return t >= -1e-9 && t <= 1 + 1e-9;
    }

    private static (double x, double y) ClosestOnCircle(Circ c, double px, double py)
    {
        double dx = px - c.Cx, dy = py - c.Cy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12) return (c.Cx + c.R, c.Cy);   // 光标在圆心，任取一点
        return (c.Cx + dx / d * c.R, c.Cy + dy / d * c.R);
    }

    private static double MidAngle(double a0, double a1)
    {
        double sweep = a1 - a0;
        while (sweep < 0) sweep += 2 * Math.PI;
        while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;
        return a0 + sweep * 0.5;
    }

    private static bool AngleInArc(double ang, double a0, double a1)
    {
        double Norm(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
        double a = Norm(ang - a0), sweep = Norm(a1 - a0);
        if (sweep < 1e-12) sweep = 2 * Math.PI;   // 整圆
        return a <= sweep + 1e-9;
    }

    private static bool SegNearCursor(Seg s, double cx, double cy, double tol)
    {
        var (px, py) = ClosestOnSeg(s, cx, cy);
        double dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= tol * tol;
    }

    private static bool CircNearCursor(Circ c, double cx, double cy, double tol)
    {
        double dx = cx - c.Cx, dy = cy - c.Cy, d = Math.Sqrt(dx * dx + dy * dy);
        return Math.Abs(d - c.R) <= tol;
    }

    private static bool SegSegIntersect(Seg a, Seg b, out double ix, out double iy)
    {
        ix = iy = 0;
        double r1 = a.X2 - a.X1, r2 = a.Y2 - a.Y1, s1 = b.X2 - b.X1, s2 = b.Y2 - b.Y1;
        double den = r1 * s2 - r2 * s1;
        if (Math.Abs(den) < 1e-15) return false;   // 平行/共线
        double t = ((b.X1 - a.X1) * s2 - (b.Y1 - a.Y1) * s1) / den;
        double u = ((b.X1 - a.X1) * r2 - (b.Y1 - a.Y1) * r1) / den;
        if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
        ix = a.X1 + t * r1; iy = a.Y1 + t * r2; return true;
    }

    private static bool SegCircleIntersect(Seg s, Circ c, double cx, double cy, double tol, Action<double, double, Mode> consider)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
        double fx = s.X1 - c.Cx, fy = s.Y1 - c.Cy;
        double aa = dx * dx + dy * dy, bb = 2 * (fx * dx + fy * dy), cc = fx * fx + fy * fy - c.R * c.R;
        double disc = bb * bb - 4 * aa * cc;
        if (disc < 0 || aa < 1e-18) return false;
        disc = Math.Sqrt(disc);
        bool any = false;
        foreach (double t in new[] { (-bb - disc) / (2 * aa), (-bb + disc) / (2 * aa) })
        {
            if (t < -1e-9 || t > 1 + 1e-9) continue;
            consider(s.X1 + t * dx, s.Y1 + t * dy, Mode.Intersection); any = true;
        }
        return any;
    }
}
