using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

/// <summary>一段衔接的账目 + 几何（渐进过渡段）。忠实移植原 <c>MineAssLib.Driving.EpConnector</c>。</summary>
public sealed class EpConnector
{
    public string TemplateId = "", WallId = "";
    /// <summary>这一段接的是哪一侧（0=A 侧）。</summary>
    public int    Side;
    /// <summary>模板端点标高 / 端帮落点标高。</summary>
    public double TemplateZ, WallZ;
    /// <summary>要爬的高差(m) = 端帮 − 模板。</summary>
    public double Dz => WallZ - TemplateZ;

    /// <summary>过渡段水平长(m) = 模板端点到断口的平距 + 沿端帮吃掉的那一段。</summary>
    public double PlanLength;
    /// <summary>按限坡算出来的最小水平长(m)（含两端相切的形状系数）。</summary>
    public double NeedLength;
    /// <summary>直连（不延长）时的水平长(m)。</summary>
    public double DirectLength;
    /// <summary>沿端帮台阶线吃掉的长度(m)。</summary>
    public double WallEaten;
    /// <summary>那条端帮线上还剩多少可吃(m)（退路总长）。</summary>
    public double WallAvail;

    /// <summary>被吃的那条端帮线的来源（0=不可追溯）。</summary>
    public ulong WallHandle;
    /// <summary>断口坐标。</summary>
    public double CutX, CutY, CutZ;
    /// <summary>平面走线是不是真按两端走势接的（false = 有一端拿不到走势/走势与连线背道而驰，退回直连方向）。</summary>
    public bool ByTrend;

    /// <summary>**角部相交衔接**：两端各沿自己的走势延伸，在交点处折接（而不是用一条缓和曲线兜过去）。</summary>
    public bool ByIntersection;
    /// <summary>交点平面坐标（<see cref="ByIntersection"/>=true 时有效）。</summary>
    public double CornerX, CornerY;
    /// <summary>两端各自延伸出去的长度(m)（到交点）。</summary>
    public double ExtendA, ExtendB;

    /// <summary>**两条线本身已经交叉**：端点各自越过了对方，图上是一个 X。不需要衔接段，要做的是裁掉越过交点的尾巴。</summary>
    public bool   Overlapped;
    /// <summary>越过交点要裁掉的尾巴长(m)：A 端 / B 端各一段。</summary>
    public double TrimA, TrimB;
    /// <summary>拐点用圆弧倒角时**实际**用的半径(m)。0 = 多段线折接（尖角）。两边让不出足够切线长时会自动压小。</summary>
    public double FilletR;
    /// <summary>**接不上**：走势平行、或交点落在两端的背后。不出线、也不硬拗，单列记账。</summary>
    public bool   NoJoin;
    /// <summary>这一条有没有落地几何。</summary>
    public bool   HasGeometry => Pts.Count >= 2;

    /// <summary>实际最大纵坡(%)（两端相切 ⇒ 中点最陡 = 1.5×平均纵坡）。</summary>
    public double MaxGradePct;
    /// <summary>超闸：退路吃到尽头还是缓不下来。**不静默拉平，单列出来给人判**。</summary>
    public bool   OverGrade;

    /// <summary>落地几何（三维点列，首点=模板端点，末点=端帮落点）。</summary>
    public readonly List<(double X, double Y, double Z)> Pts = new();

    public string Describe(string sideName)
        => Overlapped
            ? string.Format(CultureInfo.InvariantCulture,
                "{0} · {1:0.##}m ⇄ {2:0.##}m ｜ **两线已交叉**（端点各自越过了对方）：不需要衔接段，在交点处接上即可 —— 要裁掉的尾巴 A 端 {3:0.#}m / B 端 {4:0.#}m",
                sideName, TemplateZ, WallZ, TrimA, TrimB)
        : NoJoin
            ? string.Format(CultureInfo.InvariantCulture,
                "{0} · {1:0.##}m ⇄ {2:0.##}m ｜ **接不上**：两端走势平行、或交点落在延长线背后（直连 {3:0.#}m）。不出线也不硬拗 —— 硬接会画出一条掉头绕回去的自相交线。",
                sideName, TemplateZ, WallZ, DirectLength)
        : ByIntersection
            ? string.Format(CultureInfo.InvariantCulture,
                "{0} · {1:0.##}m ⇄ {2:0.##}m ｜ 角部{9}：两端各沿走势延伸 {3:0.#}m / {4:0.#}m（直连仅 {5:0.#}m）· Δz={6:+0.##;-0.##;0}m · 最大纵坡 {7:0.##}%{8}",
                sideName, TemplateZ, WallZ, ExtendA, ExtendB, DirectLength, Dz,
                MaxGradePct, OverGrade ? " ⚠超闸" : "",
                FilletR > 1e-6 ? $"圆弧倒角 R={FilletR:0.#}m" : "折接")
            : string.Format(CultureInfo.InvariantCulture,
                "{0} · 模板 {1:0.##}m → 端帮 {2:0.##}m ｜ Δz={3:+0.##;-0.##;0}m 过渡长 {4:0.#}m（直连 {5:0.#}m + 吃端帮 {6:0.#}m/可吃 {7:0.#}m）· 最大纵坡 {8:0.##}%{9}",
                sideName, TemplateZ, WallZ, Dz, PlanLength, DirectLength, WallEaten, WallAvail,
                MaxGradePct, OverGrade ? " ⚠超闸" : "");
}

/// <summary>
/// 「创建工程位置」· **衔接段的渐进过渡**（口径 2026-08-07 现场定，忠实移植原 <c>EpConnectorBuilder</c>）。
///
/// 一句话：模板台阶端点与端帮台阶断口之间那点高差，不许压在一根直弦上，要按限坡缓缓爬完。
///  ① **两端相切**：三次缓和曲线 S(t)=3t²−2t³ 过渡，中点最陡 —— 最大纵坡 = 1.5 × 平均纵坡。
///  ② **限坡定长度**：Lneed = 1.5·|Δz| / i。
///  ③ **不够长就沿端帮往外吃**：平面走线取【模板端点 → 断口 → 沿端帮保留线往外】，吃 eat = max(0, Lneed − 直连平距)。
///  ④ **吃不动就报超闸，不静默拉平**。
///  ⑤ 同一级（|Δz| ≤ sameLevelTol）走**角部相交**：两端沿走势延伸、交点处折接（或圆弧倒角）；已交叉的只记裁尾账，走势平行/交点在背后的记 NoJoin。
/// 纯几何、只读、不碰图。
/// </summary>
public static class EpConnectorBuilder
{
    /// <summary>两端相切的三次缓和曲线：最大纵坡 = 形状系数 × 平均纵坡。</summary>
    public const double ShapeFactor = 1.5;
    /// <summary>默认纵坡闸(%)。</summary>
    public const double DefaultGradePct = 8.0;

    private const double SampleStep = 5.0;
    private const int    MaxSamples = 400;

    public static List<EpConnector> Build(EpScene? s, double maxGradePct = DefaultGradePct,
                                          double sameLevelTol = 0.5, double filletRadius = 0)
    {
        var outp = new List<EpConnector>();
        if (s == null || !s.Success) return outp;
        double grade = maxGradePct > 0 ? maxGradePct / 100.0 : DefaultGradePct / 100.0;

        foreach (var l in s.Links)
        {
            var t = s.ById(l.TemplateId); var w = s.ById(l.WallId);
            if (t == null || w == null || w.Members.Count == 0) continue;

            // 【按有没有高差分路】同一级 ⇒ 角部相交；有高差 ⇒ 渐进过渡。不按身份分 —— 所有端点开放之后两头都可能是任一角色。
            bool corner = Math.Abs(w.Z - t.Z) <= Math.Max(1e-9, sameLevelTol);

            foreach (var tm in t.Members)
            {
                EpMember? best = null; double bestD = double.MaxValue;
                foreach (var wm in w.Members)
                {
                    double d = Dist2D(tm.X, tm.Y, wm.X, wm.Y) + 10 * Math.Abs(tm.Z - wm.Z);
                    if (d < bestD) { bestD = d; best = wm; }
                }
                if (best == null) continue;
                var c = corner ? BuildCorner(tm, best, grade, filletRadius) : BuildOne(t, best, tm, grade);
                if (c != null) { c.TemplateId = t.Id; c.WallId = w.Id; c.Side = t.Side; outp.Add(c); }
            }
        }
        return outp;
    }

    /// <summary>**角部相交衔接**：a、b 各沿自己的走势往外延伸，在交点处折接；已交叉 ⇒ Overlapped；平行/背后 ⇒ NoJoin。</summary>
    private static EpConnector? BuildCorner(EpMember a, EpMember b, double grade, double filletRadius)
    {
        if (TryTrailCross(a.Trail, b.Trail, out double qx, out double qy, out double backA, out double backB))
        {
            return new EpConnector
            {
                TemplateZ = a.Z, WallZ = b.Z,
                DirectLength = Dist2D(a.X, a.Y, b.X, b.Y),
                WallHandle = b.Handle, CutX = b.X, CutY = b.Y, CutZ = b.Z,
                ByTrend = true, ByIntersection = true, Overlapped = true,
                CornerX = qx, CornerY = qy, TrimA = backA, TrimB = backB,
            };
        }

        var da = TrendOut(a.Trail);
        var db = TrendOut(b.Trail);

        EpConnector NoJoin() => new EpConnector
        {
            TemplateZ = a.Z, WallZ = b.Z,
            DirectLength = Dist2D(a.X, a.Y, b.X, b.Y),
            WallHandle = b.Handle, CutX = b.X, CutY = b.Y, CutZ = b.Z,
            NoJoin = true,
        };

        if ((da.X == 0 && da.Y == 0) || (db.X == 0 && db.Y == 0)) return NoJoin();

        double cross = da.X * db.Y - da.Y * db.X;
        if (Math.Abs(cross) < 1e-9) return NoJoin();
        double rx = b.X - a.X, ry = b.Y - a.Y;
        double sA = (rx * db.Y - ry * db.X) / cross;
        double sB = (rx * da.Y - ry * da.X) / cross;
        // 角部 = 两条【向外】射线的交点 ⇒ sA>0 且 sB>0（原版早先把闸门写反过，现场看到的自相交正是那个）
        if (sA <= 1e-6 || sB <= 1e-6) return NoJoin();

        double px = a.X + da.X * sA, py = a.Y + da.Y * sA;
        double extA = sA, extB = sB;
        double total = extA + extB;
        if (total < 1e-6) return null;

        var c = new EpConnector
        {
            TemplateZ = a.Z, WallZ = b.Z,
            PlanLength = total, NeedLength = 0, DirectLength = Dist2D(a.X, a.Y, b.X, b.Y),
            WallEaten = 0, WallAvail = 0,
            WallHandle = b.Handle, CutX = b.X, CutY = b.Y, CutZ = b.Z,
            ByTrend = true, ByIntersection = true,
            CornerX = px, CornerY = py, ExtendA = extA, ExtendB = extB,
        };
        double dz = b.Z - a.Z;
        double maxGrade = total > 1e-9 ? ShapeFactor * Math.Abs(dz) / total : 0;
        c.MaxGradePct = maxGrade * 100.0;
        c.OverGrade = maxGrade > grade + 1e-9;

        var vOut = (X: -db.X, Y: -db.Y);
        var plan = new List<(double X, double Y)>();

        if (filletRadius > 1e-6)
        {
            // 【圆弧倒角】切线长 T = R·tan(δ/2)；两边让不出 T 就把 R 压到能让出的那个值
            double dot = Math.Max(-1, Math.Min(1, da.X * vOut.X + da.Y * vOut.Y));
            double delta = Math.Acos(dot);
            double crs = da.X * vOut.Y - da.Y * vOut.X;
            if (delta > 1e-4 && Math.Abs(crs) > 1e-12)
            {
                double tanHalf = Math.Tan(delta * 0.5);
                double T = filletRadius * tanHalf;
                double tMax = Math.Min(extA, extB) * 0.98;
                if (T > tMax) T = tMax;
                double R = tanHalf > 1e-9 ? T / tanHalf : 0;
                if (R > 1e-6 && T > 1e-6)
                {
                    c.FilletR = R;
                    double sgn = crs > 0 ? 1 : -1;
                    double t1x = px - da.X * T, t1y = py - da.Y * T;
                    double t2x = px + vOut.X * T, t2y = py + vOut.Y * T;
                    double cx = t1x + (-da.Y * sgn) * R, cy = t1y + (da.X * sgn) * R;
                    double a1 = Math.Atan2(t1y - cy, t1x - cx);
                    double a2 = Math.Atan2(t2y - cy, t2x - cx);
                    double sweep = a2 - a1;
                    while (sweep > Math.PI) sweep -= 2 * Math.PI;
                    while (sweep < -Math.PI) sweep += 2 * Math.PI;

                    int segs = Math.Max(6, (int)Math.Ceiling(Math.Abs(sweep) * R / SampleStep));
                    if (segs > 96) segs = 96;
                    plan.Add((a.X, a.Y));
                    plan.Add((t1x, t1y));
                    for (int k = 1; k < segs; k++)
                    {
                        double ang = a1 + sweep * k / segs;
                        plan.Add((cx + R * Math.Cos(ang), cy + R * Math.Sin(ang)));
                    }
                    plan.Add((t2x, t2y));
                    plan.Add((b.X, b.Y));
                }
            }
        }

        if (plan.Count < 2)
        {
            plan.Add((a.X, a.Y));
            int n0 = (int)Math.Ceiling(extA / SampleStep);
            for (int k = 1; k < n0; k++) plan.Add((a.X + da.X * extA * k / n0, a.Y + da.Y * extA * k / n0));
            plan.Add((px, py));
            int n1 = (int)Math.Ceiling(extB / SampleStep);
            for (int k = 1; k < n1; k++) plan.Add((px + vOut.X * extB * k / n1, py + vOut.Y * extB * k / n1));
            plan.Add((b.X, b.Y));
        }

        double acc = 0;
        var cum2 = new double[plan.Count];
        for (int i = 1; i < plan.Count; i++)
        { acc += Dist2D(plan[i - 1].X, plan[i - 1].Y, plan[i].X, plan[i].Y); cum2[i] = acc; }
        c.PlanLength = acc > 1e-9 ? acc : total;
        for (int i = 0; i < plan.Count; i++)
        {
            double u = acc > 1e-9 ? cum2[i] / acc : 0;
            c.Pts.Add((plan[i].X, plan[i].Y, a.Z + dz * Smooth(u)));
        }
        return c.Pts.Count >= 2 ? c : null;
    }

    /// <summary>一段：模板端点 tm → 端帮断口 wm（沿 wm 的退路往外吃到够缓为止）。</summary>
    private static EpConnector? BuildOne(EpNode t, EpMember wm, EpMember tm, double grade)
    {
        var trail = wm.Trail;
        var cut = trail is { Count: > 0 } ? trail[0] : (X: wm.X, Y: wm.Y, Z: wm.Z);

        var path = Lead(tm, cut, TrendOut(tm.Trail), TrendIn(trail), out bool byTrend);
        int leadPts = path.Count;
        if (trail != null) for (int i = 1; i < trail.Count; i++) path.Add(trail[i]);
        if (path.Count < 2) return null;

        var cum = new double[path.Count];
        for (int i = 1; i < path.Count; i++)
            cum[i] = cum[i - 1] + Dist2D(path[i - 1].X, path[i - 1].Y, path[i].X, path[i].Y);
        double direct = cum[leadPts - 1];
        double availPlan = cum[path.Count - 1] - direct;
        if (cum[path.Count - 1] < 1e-6) return null;

        double dz0 = wm.Z - tm.Z;
        double need = ShapeFactor * Math.Abs(dz0) / Math.Max(1e-9, grade);
        double eaten = Math.Max(0.0, Math.Min(need - direct, availPlan));
        double total = direct + eaten;

        var end = PointAt(path, cum, total);
        double dz = end.Z - tm.Z;
        double maxGrade = total > 1e-9 ? ShapeFactor * Math.Abs(dz) / total : double.PositiveInfinity;

        var c = new EpConnector
        {
            TemplateZ = tm.Z, WallZ = end.Z,
            PlanLength = total, NeedLength = need, DirectLength = direct,
            WallEaten = eaten, WallAvail = availPlan,
            MaxGradePct = double.IsInfinity(maxGrade) ? 0 : maxGrade * 100.0,
            OverGrade = maxGrade > grade + 1e-9,
            WallHandle = wm.Handle, CutX = cut.X, CutY = cut.Y, CutZ = cut.Z,
            ByTrend = byTrend,
        };

        var arcs = new List<double> { 0.0 };
        for (int i = 1; i < path.Count; i++) if (cum[i] > 1e-9 && cum[i] < total - 1e-9) arcs.Add(cum[i]);
        int n = (int)Math.Ceiling(total / SampleStep);
        if (n < 8) n = 8; if (n > MaxSamples) n = MaxSamples;
        for (int k = 1; k < n; k++) arcs.Add(total * k / n);
        arcs.Add(total);
        arcs.Sort();

        double last = double.NegativeInfinity;
        foreach (var sArc in arcs)
        {
            if (sArc - last < 1e-6) continue;
            last = sArc;
            var p = PointAt(path, cum, sArc);
            double u = total > 1e-9 ? sArc / total : 0;
            c.Pts.Add((p.X, p.Y, tm.Z + dz * Smooth(u)));
        }
        return c.Pts.Count >= 2 ? c : null;
    }

    private const int LeadSegs = 20;
    private const double TrendCosMin = 0.0;
    /// <summary>走势的回望距离(m)：沿线往回吃满这个距离，按取用长度对单位方向加权平均。</summary>
    private const double TrendLookback = 30.0;

    private static (double X, double Y) TrendAlong(IReadOnlyList<(double X, double Y, double Z)>? trail, bool outward)
    {
        if (trail == null || trail.Count < 2) return (0, 0);
        double acc = 0, sx = 0, sy = 0;
        for (int i = 1; i < trail.Count; i++)
        {
            double dx = trail[i].X - trail[i - 1].X, dy = trail[i].Y - trail[i - 1].Y;
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) continue;
            double take = Math.Min(l, TrendLookback - acc);
            if (take <= 1e-9) break;
            sx += dx / l * take; sy += dy / l * take;
            acc += take;
            if (acc >= TrendLookback - 1e-9) break;
        }
        if (acc < 1e-9) return (0, 0);
        var d = Norm(sx, sy);
        return outward ? (-d.X, -d.Y) : d;
    }

    private static (double X, double Y) TrendOut(IReadOnlyList<(double X, double Y, double Z)>? trail) => TrendAlong(trail, outward: true);
    private static (double X, double Y) TrendIn(IReadOnlyList<(double X, double Y, double Z)>? trail) => TrendAlong(trail, outward: false);

    /// <summary>模板端点 → 断口 的平面引段：三次贝塞尔，起点切向 = 模板台阶线的走势、终点切向 = 端帮线的走势。</summary>
    private static List<(double X, double Y, double Z)> Lead(
        EpMember tm, (double X, double Y, double Z) cut,
        (double X, double Y) tOut, (double X, double Y) wOut, out bool byTrend)
    {
        double cx = cut.X - tm.X, cy = cut.Y - tm.Y;
        double chord = Math.Sqrt(cx * cx + cy * cy);
        var pts = new List<(double X, double Y, double Z)>(LeadSegs + 1);
        if (chord < 1e-9) { byTrend = false; pts.Add((tm.X, tm.Y, tm.Z)); pts.Add(cut); return pts; }

        var ch = (X: cx / chord, Y: cy / chord);
        bool okT = tOut.X != 0 || tOut.Y != 0;
        bool okW = wOut.X != 0 || wOut.Y != 0;
        if (okT && tOut.X * ch.X + tOut.Y * ch.Y < TrendCosMin) okT = false;
        if (okW && wOut.X * ch.X + wOut.Y * ch.Y < TrendCosMin) okW = false;
        byTrend = okT && okW;

        var d0 = okT ? tOut : ch;
        var d1 = okW ? wOut : ch;
        double h = chord / 3.0;
        double p1x = tm.X + d0.X * h, p1y = tm.Y + d0.Y * h;
        double p2x = cut.X - d1.X * h, p2y = cut.Y - d1.Y * h;

        for (int k = 0; k <= LeadSegs; k++)
        {
            double u = k / (double)LeadSegs, v = 1 - u;
            double b0 = v * v * v, b1 = 3 * v * v * u, b2 = 3 * v * u * u, b3 = u * u * u;
            pts.Add((tm.X * b0 + p1x * b1 + p2x * b2 + cut.X * b3,
                     tm.Y * b0 + p1y * b1 + p2y * b2 + cut.Y * b3,
                     tm.Z + (cut.Z - tm.Z) * u));
        }
        return pts;
    }

    /// <summary>两条走位链在平面上有没有真的交叉：取离两端点都最近的那个交点，回传各自要裁的尾巴长。</summary>
    private static bool TryTrailCross(
        IReadOnlyList<(double X, double Y, double Z)>? ta, IReadOnlyList<(double X, double Y, double Z)>? tb,
        out double px, out double py, out double backA, out double backB)
    {
        px = py = backA = backB = 0;
        if (ta == null || tb == null || ta.Count < 2 || tb.Count < 2) return false;

        bool found = false; double bestScore = double.MaxValue;
        double cumA = 0;
        for (int i = 1; i < ta.Count; i++)
        {
            double ax0 = ta[i - 1].X, ay0 = ta[i - 1].Y, ax1 = ta[i].X, ay1 = ta[i].Y;
            double segA = Dist2D(ax0, ay0, ax1, ay1);
            double cumB = 0;
            for (int j = 1; j < tb.Count; j++)
            {
                double bx0 = tb[j - 1].X, by0 = tb[j - 1].Y, bx1 = tb[j].X, by1 = tb[j].Y;
                double segB = Dist2D(bx0, by0, bx1, by1);
                double rX = ax1 - ax0, rY = ay1 - ay0, sX = bx1 - bx0, sY = by1 - by0;
                double den = rX * sY - rY * sX;
                if (Math.Abs(den) > 1e-12)
                {
                    double qpx = bx0 - ax0, qpy = by0 - ay0;
                    double t = (qpx * sY - qpy * sX) / den;
                    double u = (qpx * rY - qpy * rX) / den;
                    if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                    {
                        double la = cumA + segA * t, lb = cumB + segB * u;
                        double score = la + lb;
                        if (score < bestScore)
                        {
                            bestScore = score; found = true;
                            px = ax0 + rX * t; py = ay0 + rY * t;
                            backA = la; backB = lb;
                        }
                    }
                }
                cumB += segB;
            }
            cumA += segA;
        }
        return found;
    }

    private static (double X, double Y) Norm(double x, double y)
    {
        double l = Math.Sqrt(x * x + y * y);
        return l < 1e-9 ? (0, 0) : (x / l, y / l);
    }

    /// <summary>三次缓和曲线 S(t)=3t²−2t³。</summary>
    private static double Smooth(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t * t * (3 - 2 * t);
    }

    private static (double X, double Y, double Z) PointAt(List<(double X, double Y, double Z)> path, double[] cum, double s)
    {
        int last = path.Count - 1;
        if (s <= 0) return path[0];
        if (s >= cum[last]) return path[last];
        int a = 0, b = last;
        while (a + 1 < b) { int mid = (a + b) / 2; if (cum[mid] <= s) a = mid; else b = mid; }
        double seg = cum[a + 1] - cum[a];
        double f = seg < 1e-9 ? 0 : (s - cum[a]) / seg;
        return (path[a].X + (path[a + 1].X - path[a].X) * f,
                path[a].Y + (path[a + 1].Y - path[a].Y) * f,
                path[a].Z + (path[a + 1].Z - path[a].Z) * f);
    }

    private static double Dist2D(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    /// <summary>把一段台阶线从某一端裁掉 dist 米（平面长度）。裁到只剩不足两点返回 null，由调用方记账，不许静默丢。</summary>
    public static List<(double X, double Y, double Z)>? TrimFrom(IReadOnlyList<(double X, double Y, double Z)>? pts, bool fromHead, double dist)
    {
        if (pts == null || pts.Count < 2) return null;
        if (dist <= 1e-9) return new List<(double X, double Y, double Z)>(pts);
        int n = pts.Count;
        (double X, double Y, double Z) At(int i) => fromHead ? pts[i] : pts[n - 1 - i];
        double total = 0;
        for (int i = 0; i + 1 < n; i++) total += Dist2D(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y);
        if (dist >= total - 1e-6) return null;
        double acc = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            var a = At(i); var b = At(i + 1);
            double seg = Dist2D(a.X, a.Y, b.X, b.Y);
            if (acc + seg >= dist - 1e-9)
            {
                double f = seg < 1e-9 ? 1 : (dist - acc) / seg;
                var cutPt = (X: a.X + (b.X - a.X) * f, Y: a.Y + (b.Y - a.Y) * f, Z: a.Z + (b.Z - a.Z) * f);
                var outp = new List<(double X, double Y, double Z)>(n - i) { cutPt };
                for (int k = i + 1; k < n; k++)
                {
                    var q = At(k);
                    if (k == i + 1 && Dist2D(cutPt.X, cutPt.Y, q.X, q.Y) < 1e-9) continue;
                    outp.Add(q);
                }
                if (!fromHead) outp.Reverse();
                return outp.Count >= 2 ? outp : null;
            }
            acc += seg;
        }
        return null;
    }

    /// <summary>一批衔接段的汇总（回显 / 验收）：出线的 / 已交叉只需裁尾的 / 接不上的三类分开报。</summary>
    public static string Summarize(IReadOnlyList<EpConnector> cs, double gradePct)
    {
        if (cs == null || cs.Count == 0) return "没有衔接段。";
        double maxG = 0, sumEat = 0, maxDz = 0, minLen = double.MaxValue, maxLen = 0, sumTrim = 0;
        int over = 0, straight = 0, drawn = 0, corner = 0, overlap = 0, nojoin = 0;
        foreach (var c in cs)
        {
            if (c.Overlapped) { overlap++; sumTrim += c.TrimA + c.TrimB; continue; }
            if (c.NoJoin) { nojoin++; continue; }
            drawn++;
            if (c.ByIntersection) corner++;
            maxG = Math.Max(maxG, c.MaxGradePct);
            sumEat += c.WallEaten;
            maxDz = Math.Max(maxDz, Math.Abs(c.Dz));
            minLen = Math.Min(minLen, c.PlanLength);
            maxLen = Math.Max(maxLen, c.PlanLength);
            if (c.OverGrade) over++;
            if (!c.ByTrend) straight++;
        }
        var ci = CultureInfo.InvariantCulture;
        if (drawn == 0) minLen = maxLen = 0;
        string head = drawn == 0
            ? "没有出线的衔接段"
            : $"{drawn} 段衔接入图（其中角部相交 {corner} 段）：长 {minLen.ToString("0.#", ci)}~{maxLen.ToString("0.#", ci)}m、" +
              $"最大高差 {maxDz.ToString("0.##", ci)}m、最大纵坡 {maxG.ToString("0.##", ci)}%（闸 {gradePct.ToString("0.##", ci)}%）、" +
              $"沿端帮共吃掉 {sumEat.ToString("0.#", ci)}m";
        return head
             + (overlap > 0 ? $" · {overlap} 处两线已交叉（不出线，需裁掉交叉尾巴共 {sumTrim.ToString("0.#", ci)}m）" : "")
             + (nojoin > 0 ? $" · ⚠ {nojoin} 处接不上（走势平行或交点在背后 —— 硬接会画出掉头绕回的自相交线，已不出线）" : "")
             + (straight > 0 ? $" · {straight} 段平面上没按走势接（已退回直连方向）" : "")
             + (drawn > 0 ? (over > 0 ? $" · ⚠ {over} 段超闸（退路吃到尽头仍缓不下来）" : " · 出线的全部在闸内") : "");
    }
}
