using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>线形后处理结果（阶段①②③）。</summary>
public sealed class LineFormResult
{
    public IReadOnlyList<(double X, double Y, double Z)> Line { get; set; } = new List<(double, double, double)>();
    /// <summary>① 实际达到的最小平曲线半径 m（0 = 无弯 / 未处理）。</summary>
    public double MinRadiusM { get; set; }
    /// <summary>① 半径不达标（段长放不下 R_min，被迫降 R）的转角数。</summary>
    public int Violations { get; set; }

    // ── 阶段② 纵断面（弯道折减 + 合成坡度） ──
    /// <summary>② 直线段实际最大纵坡 %。</summary>
    public double MaxGradeUsedPct { get; set; }
    /// <summary>② 圆曲线段实际最大纵坡 %（应 ≤ 弯道折减限）。</summary>
    public double CurveGradeUsedPct { get; set; }
    /// <summary>② 弯道处最大合成坡度 % = √(纵² + 超高²)。</summary>
    public double ResultantGradePct { get; set; }
    /// <summary>② 路太短：在限坡内（直线 i_max、弯道折减）descend 不满总高差 → 须增长展线。</summary>
    public bool GradeExceedsLimit { get; set; }

    // ── 阶段③ 竖曲线 + 最小坡长 ──
    /// <summary>③ 插入的竖曲线条数。</summary>
    public int VerticalCurveCount { get; set; }
    /// <summary>③ 实际最小竖曲线半径 m（0=未做）。</summary>
    public double MinVerticalRadiusAchievedM { get; set; }
    /// <summary>③ 竖曲线半径不达标数。</summary>
    public int VerticalCurveViolations { get; set; }
    /// <summary>③ 直线段坡长 &lt; 最小坡长 的段数。</summary>
    public int MinGradeSectionViolations { get; set; }
}

/// <summary>
/// 中心线线形后处理（GBJ22-87）。
///   阶段①：内移偏置（朝形心移 offsetM，路落平盘内）+ 转角圆弧化（R≥rMin，切线长 T=R·tan(δ/2)）。
///   阶段②：分段限坡纵断面 —— 直线段 ≤ i_max、圆曲线段 ≤ 弯道折减限（且合成坡度 √(纵²+超高²)≤上限）；
///           按比例 α=ΔH/Σ(L·cap) 缩放各段纵坡命中总高差；α>1=限坡内 descend 不满 → 须增长展线(如实报)。
///   阶段③：变坡点抛物线竖曲线（复用 <see cref="RoadVerticalCurve"/>=原 ProfileSmoother）+ 最小坡长校核。
/// 纯几何、不依赖引擎、可单测。grade 参数全为 0 时退化为阶段①（Z 沿弧长线性插值）。
/// </summary>
public static class CenterlineLineForm
{
    public static LineFormResult Apply(
        IReadOnlyList<(double X, double Y, double Z)> pts,
        IReadOnlyList<(double Cx, double Cy)>? centroids,   // 只在 offsetM>0 时用;不内移(手拾档)传 null
        double offsetM, double rMin,
        double maxGradePct = 0, double curveMaxGradePct = 0,
        double maxResultantPct = 0, double superElevPct = 0,
        double vcTriggerPct = 0, double vcRadiusM = 0, double minSecLenM = 0,
        int maxArcSeg = 16)
    {
        var res = new LineFormResult();
        if (pts == null || pts.Count == 0) { return res; }

        // ① 内移偏置（朝形心）+ 去重相邻重合点
        var p = new List<(double X, double Y, double Z)>(pts.Count);
        for (int k = 0; k < pts.Count; k++)
        {
            var s = pts[k];
            if (offsetM > 1e-9 && centroids != null && k < centroids.Count)
            {
                double dx = centroids[k].Cx - s.X, dy = centroids[k].Cy - s.Y;
                double l = Math.Sqrt(dx * dx + dy * dy);
                if (l > 1e-9) s = (s.X + dx / l * offsetM, s.Y + dy / l * offsetM, s.Z);
            }
            if (p.Count == 0 || Dist2(p[p.Count - 1].X, p[p.Count - 1].Y, s.X, s.Y) > 1e-6)
                p.Add(s);
        }

        if (p.Count < 3 || rMin <= 1e-9) { res.Line = p; return res; }

        // ② 转角圆弧化（同时给每段打"直线/圆曲线"标记，供纵断面分段限坡）
        double minR = double.MaxValue;
        int violations = 0;
        var outPts = new List<(double X, double Y, double Z)>();
        var segCurve = new List<bool>();   // segCurve[k] = 段 outPts[k]→outPts[k+1] 属圆曲线
        void Push((double X, double Y, double Z) pt, bool curveSeg)
        {
            if (outPts.Count > 0) segCurve.Add(curveSeg);
            outPts.Add(pt);
        }
        Push(p[0], false);

        for (int k = 1; k + 1 < p.Count; k++)
        {
            var A = p[k - 1]; var B = p[k]; var C = p[k + 1];
            double ux = B.X - A.X, uy = B.Y - A.Y; double ul = Math.Sqrt(ux * ux + uy * uy);
            double vx = C.X - B.X, vy = C.Y - B.Y; double vl = Math.Sqrt(vx * vx + vy * vy);
            if (ul < 1e-9 || vl < 1e-9) { Push(B, false); continue; }
            ux /= ul; uy /= ul; vx /= vl; vy /= vl;

            double dot = Math.Clamp(ux * vx + uy * vy, -1.0, 1.0);
            double delta = Math.Acos(dot);
            if (delta < 0.01) { Push(B, false); continue; }   // 近直线 → 不插弧

            double half = delta / 2.0;
            double maxT = 0.5 * Math.Min(ul, vl);
            double R = rMin;
            double T = R * Math.Tan(half);
            if (T > maxT) { T = maxT; R = T / Math.Tan(half); violations++; }
            if (R < minR) minR = R;

            double pinx = B.X - ux * T, piny = B.Y - uy * T;
            double poutx = B.X + vx * T, pouty = B.Y + vy * T;
            Push((pinx, piny, B.Z), false);                    // 进切点：来段是直线

            double bisx = vx - ux, bisy = vy - uy;
            double bl = Math.Sqrt(bisx * bisx + bisy * bisy);
            if (bl > 1e-9)
            {
                bisx /= bl; bisy /= bl;
                double cx = B.X + bisx * (R / Math.Cos(half));
                double cy = B.Y + bisy * (R / Math.Cos(half));
                double a0 = Math.Atan2(piny - cy, pinx - cx);
                double a1 = Math.Atan2(pouty - cy, poutx - cx);
                double cross = ux * vy - uy * vx;
                double sweep = a1 - a0;
                if (cross > 0) { while (sweep <= 0) sweep += 2 * Math.PI; }
                else { while (sweep >= 0) sweep -= 2 * Math.PI; }
                int segs = Math.Max(2, Math.Min(maxArcSeg, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 12.0))));
                for (int s = 1; s < segs; s++)
                {
                    double a = a0 + sweep * s / segs;
                    Push((cx + R * Math.Cos(a), cy + R * Math.Sin(a), B.Z), true);   // 弧内段：圆曲线
                }
                Push((poutx, pouty, B.Z), true);               // 出切点：弧末段是圆曲线
            }
            else { Push((poutx, pouty, B.Z), false); }
        }
        Push(p[p.Count - 1], false);

        res.MinRadiusM = (minR == double.MaxValue) ? 0.0 : minR;
        res.Violations = violations;

        // ③-a 纵断面（分段限坡，阶段②）
        if (maxGradePct > 1e-9)
        {
            double curveCap = curveMaxGradePct > 1e-9 ? curveMaxGradePct : maxGradePct;
            if (maxResultantPct > 1e-9)                         // 合成坡度反推弯道允许纵坡
            {
                double byRes = maxResultantPct * maxResultantPct - superElevPct * superElevPct;
                if (byRes > 0) curveCap = Math.Min(curveCap, Math.Sqrt(byRes));
            }
            GradeLimitedProfile(outPts, segCurve, p[0].Z, p[p.Count - 1].Z, maxGradePct, curveCap, superElevPct, res);
        }
        else
        {
            ReinterpZ(outPts, p[0].Z, p[p.Count - 1].Z);
        }

        // ③-b 最小坡长：直线段连续坡长 < 限值计违规
        if (minSecLenM > 1e-9)
        {
            int viol = 0; double run = 0;
            for (int k = 0; k < segCurve.Count; k++)
            {
                double Lk = Math.Sqrt(Dist2(outPts[k].X, outPts[k].Y, outPts[k + 1].X, outPts[k + 1].Y));
                if (!segCurve[k]) run += Lk;
                else { if (run > 1e-6 && run < minSecLenM) viol++; run = 0; }
            }
            if (run > 1e-6 && run < minSecLenM) viol++;
            res.MinGradeSectionViolations = viol;
        }

        // ③-c 竖曲线：变坡点抛物线平滑（在 (s,z) 域做，XY 沿弧长重映射回 3D）
        var finalPts = outPts;
        if (vcRadiusM > 1e-9 && vcTriggerPct > 1e-9 && outPts.Count >= 3)
        {
            int n0 = outPts.Count;
            var cum = new double[n0];
            for (int k = 1; k < n0; k++)
                cum[k] = cum[k - 1] + Math.Sqrt(Dist2(outPts[k - 1].X, outPts[k - 1].Y, outPts[k].X, outPts[k].Y));
            var sArr = new double[n0]; var zArr = new double[n0];
            for (int k = 0; k < n0; k++) { sArr[k] = cum[k]; zArr[k] = outPts[k].Z; }

            var vc = RoadVerticalCurve.Smooth(sArr, zArr, vcTriggerPct, vcRadiusM);   // = 原 ProfileSmoother.VerticalCurves
            res.VerticalCurveCount = vc.Count;
            res.MinVerticalRadiusAchievedM = vc.MinRadiusM;
            res.VerticalCurveViolations = vc.Violations;

            if (vc.Count > 0)
            {
                var remapped = new List<(double X, double Y, double Z)>(vc.Profile.Count);
                foreach (var (S, Z) in vc.Profile)
                {
                    var (x, y) = InterpolateXY(outPts, cum, S);
                    remapped.Add((x, y, Z));
                }
                finalPts = remapped;
            }
        }

        res.Line = finalPts;
        return res;
    }

    /// <summary>沿折线（带累计弧长 cum）按弧长 s 取 XY（线性插值）。</summary>
    private static (double X, double Y) InterpolateXY(
        IReadOnlyList<(double X, double Y, double Z)> pts, double[] cum, double s)
    {
        int n = pts.Count;
        if (n == 0) return (0, 0);
        if (s <= cum[0]) return (pts[0].X, pts[0].Y);
        if (s >= cum[n - 1]) return (pts[n - 1].X, pts[n - 1].Y);
        int seg = n - 2;
        for (int k = 1; k < n; k++) { if (cum[k] >= s) { seg = k - 1; break; } }
        double segLen = cum[seg + 1] - cum[seg];
        double t = segLen > 1e-12 ? (s - cum[seg]) / segLen : 0.0;
        return (pts[seg].X + t * (pts[seg + 1].X - pts[seg].X),
                pts[seg].Y + t * (pts[seg + 1].Y - pts[seg].Y));
    }

    // 分段限坡纵断面：cap=直线 i_max / 圆曲线 curveCap；α=ΔH/Σ(L·cap) 缩放命中总高差。
    private static void GradeLimitedProfile(
        List<(double X, double Y, double Z)> pts, List<bool> segCurve,
        double z0, double z1, double iMaxPct, double curveCapPct, double superElevPct, LineFormResult res)
    {
        int n = pts.Count;
        if (n < 2) return;
        int m = n - 1;
        var L = new double[m];
        double Hmax = 0;
        for (int k = 0; k < m; k++)
        {
            L[k] = Math.Sqrt(Dist2(pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y));
            double capPct = (k < segCurve.Count && segCurve[k]) ? curveCapPct : iMaxPct;
            Hmax += L[k] * (capPct / 100.0);
        }
        double dH = Math.Abs(z0 - z1);
        double alpha = Hmax > 1e-9 ? dH / Hmax : 0.0;
        double sign = (z1 <= z0) ? -1.0 : 1.0;

        double z = z0;
        pts[0] = (pts[0].X, pts[0].Y, z0);
        double maxTan = 0, maxCur = 0;
        for (int k = 0; k < m; k++)
        {
            bool cur = (k < segCurve.Count && segCurve[k]);
            double gPct = alpha * (cur ? curveCapPct : iMaxPct);
            z += sign * L[k] * (gPct / 100.0);
            pts[k + 1] = (pts[k + 1].X, pts[k + 1].Y, z);
            if (cur) maxCur = Math.Max(maxCur, gPct); else maxTan = Math.Max(maxTan, gPct);
        }

        res.MaxGradeUsedPct = maxTan;
        res.CurveGradeUsedPct = maxCur;
        res.ResultantGradePct = maxCur > 1e-9 ? Math.Sqrt(maxCur * maxCur + superElevPct * superElevPct) : 0.0;
        res.GradeExceedsLimit = alpha > 1.0 + 1e-6;
    }

    private static void ReinterpZ(List<(double X, double Y, double Z)> pts, double z0, double z1)
    {
        int n = pts.Count;
        if (n < 2) return;
        var cum = new double[n];
        for (int k = 1; k < n; k++)
            cum[k] = cum[k - 1] + Math.Sqrt(Dist2(pts[k - 1].X, pts[k - 1].Y, pts[k].X, pts[k].Y));
        double total = cum[n - 1];
        for (int k = 0; k < n; k++)
        {
            double t = total > 1e-9 ? cum[k] / total : 0.0;
            pts[k] = (pts[k].X, pts[k].Y, z0 + (z1 - z0) * t);
        }
    }

    private static double Dist2(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        return dx * dx + dy * dy;
    }
}
