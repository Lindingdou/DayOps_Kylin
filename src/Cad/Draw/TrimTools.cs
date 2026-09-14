using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 修剪(TRIM) / 延伸(EXTEND) 的几何内核 —— 两件事【分开】做，各自只做自己那一半（忠实原版
/// AcDbTrimCommand / AcDbExtendCommand：原版这两条也是各自独立的命令，只共用"循环点选"的交互）：
///
/// · 修剪：目标被剪切边切成若干段，点中的那一段去掉 —— 贴着端点则缩短、夹在中间则断成两条、
///   整条不与任何剪切边相交则整体删除（同原版 TrimLine 的 atStart &amp;&amp; atEnd 分支）。
/// · 延伸：靠点击的那一端沿自身走向拉长到最近的边界交点；够不到就什么也不做，【绝不缩短】。
///
/// 排序/定位统一用目标自己的镶嵌折线做参数（累计弧长），故直线/多段线/圆弧/圆/矩形一套逻辑通吃；
/// 交点本身是精确解出来的，镶嵌只用来定先后。纯逻辑、可单测。
/// </summary>
public static class TrimTools
{
    private readonly record struct Seg(double X0, double Y0, double X1, double Y1);

    // ── 公共入口 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 修剪：去掉点击处那一段。
    /// null = 该类型不支持 / 闭合图元交点不足两个（无从切）；空表 = 整条删除；否则 = 剩下的实体(1~2 条)。
    /// </summary>
    public static List<SceneEntity>? Trim(SceneEntity target, IReadOnlyList<SceneEntity> boundaries,
                                          double clickX, double clickY)
    {
        var ts = WorldSegs(target);
        if (ts.Count == 0) return null;
        var cuts = Cuts(ts, boundaries, target, out double total);
        if (total < 1e-12) return null;
        double click = ParamAt(ts, clickX, clickY);

        // 首尾相接 = 闭合图元(圆/矩形/正多边形/闭合多段线)：参数环绕，且至少要两个交点才切得动
        bool closed = Math.Abs(ts[0].X0 - ts[^1].X1) < 1e-9 && Math.Abs(ts[0].Y0 - ts[^1].Y1) < 1e-9;

        if (!closed)
        {
            if (cuts.Count == 0) return new List<SceneEntity>();   // 与任何剪切边都不相交 → 整条删掉(同原版)
            var (prev, next) = Bracket(cuts, click);
            var a = prev ?? (s: 0.0, x: ts[0].X0, y: ts[0].Y0);         // 点击段贴着起点 → 从起点剪起
            var b = next ?? (s: total, x: ts[^1].X1, y: ts[^1].Y1);     // 贴着末端 → 剪到末端
            return target.Break(a.x, a.y, b.x, b.y);
        }

        if (cuts.Count < 2) return null;                           // 闭合图元只有一个交点：切不出段
        var (p0, p1) = BracketCyclic(cuts, click);
        if (target is CircleEntity) return target.Break(p0.x, p0.y, p1.x, p1.y);   // 圆按 CCW 去掉 p0→p1 段
        return new List<SceneEntity> { KeepAroundClosed(target, ts, p0, p1) };
    }

    /// <summary>
    /// 延伸：把靠点击那一端沿自身走向拉长到最近的边界交点。
    /// 够不到边界 / 该类型不支持 / 闭合图元(没有自由端) → null（绝不缩短，这是与修剪的分界）。
    /// </summary>
    public static SceneEntity? Extend(SceneEntity target, IReadOnlyList<SceneEntity> boundaries,
                                      double clickX, double clickY)
    {
        var bs = new List<Seg>();
        foreach (var b in boundaries) if (!ReferenceEquals(b, target)) bs.AddRange(WorldSegs(b));
        if (bs.Count == 0) return null;
        return target switch
        {
            LineEntity l => ExtendLine(l, bs, clickX, clickY),
            PolylineEntity p => ExtendPolyline(p, bs, clickX, clickY),
            ArcEntity a => ExtendArc(a, bs, clickX, clickY),
            _ => null,
        };
    }

    // ── 延伸：各类型 ──────────────────────────────────────────────────────────

    private static LineEntity? ExtendLine(LineEntity l, List<Seg> bs, double cx, double cy)
    {
        bool moveStart = D2(cx, cy, l.X0, l.Y0) < D2(cx, cy, l.X1, l.Y1);
        double fx = moveStart ? l.X1 : l.X0, fy = moveStart ? l.Y1 : l.Y0;   // 不动的那端
        double ex = moveStart ? l.X0 : l.X1, ey = moveStart ? l.Y0 : l.Y1;   // 被拉长的那端(参数 1)
        var hit = NearestBeyond(fx, fy, ex, ey, bs);
        if (hit == null) return null;
        var nl = (LineEntity)l.Apply(Affine2.Translate(0, 0));
        if (moveStart) { nl.X0 = hit.Value.x; nl.Y0 = hit.Value.y; }
        else { nl.X1 = hit.Value.x; nl.Y1 = hit.Value.y; }
        return nl;
    }

    private static PolylineEntity? ExtendPolyline(PolylineEntity p, List<Seg> bs, double cx, double cy)
    {
        var pts = p.Points;
        if (p.Closed || pts.Count < 2) return null;                  // 闭合多段线没有自由端，无处可延伸
        bool atStart = D2(cx, cy, pts[0].x, pts[0].y) < D2(cx, cy, pts[^1].x, pts[^1].y);
        var end = atStart ? pts[0] : pts[^1];
        var adj = atStart ? pts[1] : pts[^2];
        var hit = NearestBeyond(adj.x, adj.y, end.x, end.y, bs);
        if (hit == null) return null;
        var r = new PolylineEntity { Closed = false };
        r.CopyStyleFrom(p);
        r.Points.AddRange(pts);
        if (p.Has3D) r.Zs = new List<double>(p.Zs!);                 // 三维多段线：只改端点平面位置, z 沿用该端点
        if (atStart) r.Points[0] = (hit.Value.x, hit.Value.y);
        else r.Points[^1] = (hit.Value.x, hit.Value.y);
        return r;
    }

    private static ArcEntity? ExtendArc(ArcEntity a, List<Seg> bs, double cx, double cy)
    {
        var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
        if (cc == null) return null;
        var (ox, oy, r) = cc.Value;
        double a1 = Math.Atan2(a.Y1 - oy, a.X1 - ox);
        double am = Math.Atan2(a.Y2 - oy, a.X2 - ox);
        double a3 = Math.Atan2(a.Y3 - oy, a.X3 - ox);
        double sweep = Norm2Pi(a3 - a1), mid = Norm2Pi(am - a1);
        double total = mid <= sweep ? sweep : sweep - 2 * Math.PI;   // 经中点的扫向(带符号, 同 ArcEntity.Tessellate)
        double dir = total >= 0 ? 1 : -1;
        double room = 2 * Math.PI - Math.Abs(total);                 // 最多补满整圆
        bool atStart = D2(cx, cy, a.X1, a.Y1) < D2(cx, cy, a.X3, a.Y3);

        double best = double.MaxValue;
        foreach (var s in bs)
            foreach (var p in LineMath.IntersectLineCircle(s.X0, s.Y0, s.X1, s.Y1, ox, oy, r))
            {
                if (!OnSeg(s, p.x, p.y)) continue;                   // 交点要落在边界段内
                double th = Math.Atan2(p.y - oy, p.x - ox);
                // 延伸端向外走的角增量(取正)：末端顺扫向、起端逆扫向
                double d = Norm2Pi(atStart ? (a1 - th) * dir : (th - a3) * dir);
                if (d <= 1e-9 || d > room + 1e-9) continue;
                if (d < best) best = d;
            }
        if (best == double.MaxValue) return null;

        double ns = atStart ? a1 - dir * best : a1;
        double ne = atStart ? a3 : a3 + dir * best;
        double nt = total + dir * best;                              // 新扫角(两端都只会变长)
        double nm = ns + nt / 2;
        var res = new ArcEntity
        {
            X1 = ox + r * Math.Cos(ns),
            Y1 = oy + r * Math.Sin(ns),
            X2 = ox + r * Math.Cos(nm),
            Y2 = oy + r * Math.Sin(nm),
            X3 = ox + r * Math.Cos(ne),
            Y3 = oy + r * Math.Sin(ne),
            Segments = a.Segments,
        };
        res.CopyStyleFrom(a);
        return res;
    }

    /// <summary>射线 fixed→end 上、越过 end(参数 &gt; 1)的最近边界交点；没有返回 null。</summary>
    private static (double x, double y)? NearestBeyond(double fx, double fy, double ex, double ey, List<Seg> bs)
    {
        double dx = ex - fx, dy = ey - fy, len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return null;
        (double x, double y)? best = null; double bestT = double.MaxValue;
        foreach (var s in bs)
        {
            var p = LineMath.IntersectInfiniteWithSegment(fx, fy, ex, ey, s.X0, s.Y0, s.X1, s.Y1);
            if (p == null) continue;
            double t = ((p.Value.x - fx) * dx + (p.Value.y - fy) * dy) / len2;
            if (t <= 1 + 1e-9) continue;                             // 交点落在自身之内 = 那是修剪的活儿，不归延伸
            if (t < bestT) { bestT = t; best = p; }
        }
        return best;
    }

    // ── 修剪：参数化与切点 ────────────────────────────────────────────────────

    /// <summary>目标镶嵌折线上的交点(按累计弧长排序、去重、去掉贴端点的)；同时给出目标总长。</summary>
    private static List<(double s, double x, double y)> Cuts(List<Seg> ts, IReadOnlyList<SceneEntity> boundaries,
                                                            SceneEntity target, out double total)
    {
        var bs = new List<Seg>();
        foreach (var b in boundaries) if (!ReferenceEquals(b, target)) bs.AddRange(WorldSegs(b));
        var cuts = new List<(double s, double x, double y)>();
        double acc = 0;
        foreach (var t in ts)
        {
            double dx = t.X1 - t.X0, dy = t.Y1 - t.Y0, len = Math.Sqrt(dx * dx + dy * dy);
            if (len >= 1e-12)
                foreach (var b in bs)
                {
                    var p = SegSeg(t, b);
                    if (p == null) continue;
                    double u = ((p.Value.x - t.X0) * dx + (p.Value.y - t.Y0) * dy) / (len * len);
                    cuts.Add((acc + Math.Clamp(u, 0, 1) * len, p.Value.x, p.Value.y));
                }
            acc += len;
        }
        total = acc;
        double len0 = acc;                                     // out 参数进不了 lambda，取个本地量
        cuts.Sort((p, q) => p.s.CompareTo(q.s));
        double tol = Math.Max(1e-9, len0 * 1e-9);
        for (int i = cuts.Count - 1; i > 0; i--) if (cuts[i].s - cuts[i - 1].s < tol) cuts.RemoveAt(i);
        cuts.RemoveAll(c => c.s < tol || c.s > len0 - tol);     // 贴着两端的交点剪不出东西，去掉免得把整条判成点击段
        return cuts;
    }

    /// <summary>点击点投到目标镶嵌折线上的参数(累计弧长)。</summary>
    private static double ParamAt(List<Seg> ts, double px, double py)
    {
        double acc = 0, best = 0, bd = double.MaxValue;
        foreach (var t in ts)
        {
            double dx = t.X1 - t.X0, dy = t.Y1 - t.Y0, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
            double u = len2 < 1e-18 ? 0 : Math.Clamp(((px - t.X0) * dx + (py - t.Y0) * dy) / len2, 0, 1);
            double qx = t.X0 + u * dx, qy = t.Y0 + u * dy, d = D2(px, py, qx, qy);
            if (d < bd) { bd = d; best = acc + u * len; }
            acc += len;
        }
        return best;
    }

    /// <summary>点击参数两侧最近的切点(开放图元，可能一侧没有)。</summary>
    private static ((double s, double x, double y)? prev, (double s, double x, double y)? next)
        Bracket(List<(double s, double x, double y)> cuts, double click)
    {
        (double s, double x, double y)? prev = null, next = null;
        foreach (var c in cuts)
        {
            if (c.s <= click) prev = c;
            else { next = c; break; }
        }
        return (prev, next);
    }

    /// <summary>点击参数两侧最近的切点(闭合图元，环绕)。返回 (点击段起点, 点击段终点)。</summary>
    private static ((double s, double x, double y) p0, (double s, double x, double y) p1)
        BracketCyclic(List<(double s, double x, double y)> cuts, double click)
    {
        var (prev, next) = Bracket(cuts, click);
        return (prev ?? cuts[^1], next ?? cuts[0]);
    }

    /// <summary>闭合的折线类图元(矩形/正多边形/闭合多段线)剪掉点击段：留下从 p1 绕回 p0 的开口多段线。</summary>
    private static SceneEntity KeepAroundClosed(SceneEntity target, List<Seg> ts,
                                                (double s, double x, double y) p0,
                                                (double s, double x, double y) p1)
    {
        var r = new PolylineEntity { Closed = false };
        r.CopyStyleFrom(target);
        r.Points.Add((p1.x, p1.y));
        double acc = 0;
        var verts = new List<(double s, double x, double y)>();
        foreach (var t in ts)
        {
            acc += Math.Sqrt(D2(t.X0, t.Y0, t.X1, t.Y1));
            verts.Add((acc, t.X1, t.Y1));   // 每段末点 = 一个顶点
        }
        foreach (var v in verts)
        {
            bool keep = p1.s <= p0.s ? (v.s > p1.s && v.s < p0.s)     // 点击段不跨接缝 → 保留段是中间那截
                                     : (v.s > p1.s || v.s < p0.s);    // 点击段跨接缝 → 保留段绕过接缝
            if (keep) r.Points.Add((v.x, v.y));
        }
        r.Points.Add((p0.x, p0.y));
        return r;
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private static List<Seg> WorldSegs(SceneEntity e)
    {
        using var _ro = RenderOrigin.Suspend();   // 与世界坐标求交，镶嵌须回世界系
        var o = new List<float>();
        e.Tessellate(o);
        var segs = new List<Seg>(o.Count / 12);
        for (int i = 0; i + 11 < o.Count; i += 12) segs.Add(new Seg(o[i], o[i + 1], o[i + 6], o[i + 7]));
        return segs;
    }

    /// <summary>线段-线段交点(两侧参数都要落在 [0,1])；不交返回 null。</summary>
    private static (double x, double y)? SegSeg(Seg a, Seg b)
    {
        double d1x = a.X1 - a.X0, d1y = a.Y1 - a.Y0, d2x = b.X1 - b.X0, d2y = b.Y1 - b.Y0;
        double den = d1x * d2y - d1y * d2x;
        if (Math.Abs(den) < 1e-15) return null;
        double t = ((b.X0 - a.X0) * d2y - (b.Y0 - a.Y0) * d2x) / den;
        double u = ((b.X0 - a.X0) * d1y - (b.Y0 - a.Y0) * d1x) / den;
        if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return null;
        return (a.X0 + t * d1x, a.Y0 + t * d1y);
    }

    private static bool OnSeg(Seg s, double px, double py)
    {
        double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return false;
        double t = ((px - s.X0) * dx + (py - s.Y0) * dy) / len2;
        return t >= -1e-9 && t <= 1 + 1e-9;
    }

    private static double D2(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static double Norm2Pi(double a)
    {
        while (a < 0) a += 2 * Math.PI;
        while (a >= 2 * Math.PI) a -= 2 * Math.PI;
        return a;
    }
}
