using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 工作线角部搭接（"自动算到相交"，忠实原 <c>WorkLineCornerJoiner</c>）：两两工作线取最近的一对端点，各沿其末端【外向切向】延长到两切向线的
/// 交点，以该交点为【公共角点】分别延长两条基线——使两个分向的工作帮斜面在角部相交搭接、闭合缺口。
///
/// 直接改 <see cref="WorkLineSamples"/> 的 Baseline/Samples（新增 1 点 + 1 段，段方向沿末端推进方向 di），
/// 因构面(InclineSurfaceBuilder)与块体约束(WorkLineProjector)都读同一 wls，故两者自动一致。
/// 仅当交点在两端【外侧】(s&gt;0,u&gt;0)且延伸不过长时才接；近平行/背离/过远则跳过（不乱接）。
/// </summary>
public static class WorkLineCornerJoiner
{
    /// <summary>一处角部搭接：工作线 A(其 AStart 端) 与 B(其 BStart 端) 接到公共角点。供算端部延长量。</summary>
    public readonly struct CornerLink
    {
        public readonly int A; public readonly bool AStart; public readonly int B; public readonly bool BStart;
        public CornerLink(int a, bool aStart, int b, bool bStart) { A = a; AStart = aStart; B = b; BStart = bStart; }
    }

    /// <summary>返回所有成功搭接的角（供 Runner 算两斜面端部延长量，直接延长相交闭合角部）。</summary>
    public static List<CornerLink> JoinCorners(IReadOnlyList<WorkLineSamples> wls)
    {
        var links = new List<CornerLink>();
        if (wls == null || wls.Count < 2) return links;
        for (int a = 0; a < wls.Count; a++)
            for (int b = a + 1; b < wls.Count; b++)
            {
                var r = TryJoinPair(wls[a], wls[b]);
                if (r.HasValue) links.Add(new CornerLink(a, r.Value.aStart, b, r.Value.bStart));
            }
        return links;
    }

    private static (bool aStart, bool bStart)? TryJoinPair(WorkLineSamples A, WorkLineSamples B)
    {
        if (A == null || B == null || !A.Success || !B.Success) return null;
        if (A.Baseline.Count < 2 || B.Baseline.Count < 2) return null;

        // 4 组端点组合，取平面距离最近的一对（那对就是"角部"）
        double best = double.MaxValue; bool aStart = false, bStart = false;
        foreach (bool aS in new[] { true, false })
            foreach (bool bS in new[] { true, false })
            {
                var pa = aS ? A.Baseline[0] : A.Baseline[A.Baseline.Count - 1];
                var pb = bS ? B.Baseline[0] : B.Baseline[B.Baseline.Count - 1];
                double dd = (pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y);
                if (dd < best) { best = dd; aStart = aS; bStart = bS; }
            }

        if (!OutwardTangent(A, aStart, out double pax, out double pay, out double tax, out double tay, out double aLen)) return null;
        if (!OutwardTangent(B, bStart, out double pbx, out double pby, out double tbx, out double tby, out double bLen)) return null;

        // 端点距离过远（两条根本不成角）才不接；否则搭接。阈值收紧，避免连不相干的远线。
        double gap = Math.Sqrt(best);
        double maxGap = Math.Max(aLen, bLen) + 200.0;
        if (gap > maxGap) return null;

        // 尽力而为延长：两切向线在两端【外侧】相交(s>0,u>0)且延伸【很短】(≤半线长)才延；
        // 否则基线不延（角部闭合靠 ComputeEndExtensions 给的斜面端部延长）——避免把基线拉长成一大片。
        double det = tbx * tay - tax * tby;
        if (Math.Abs(det) > 1e-9)
        {
            double dx = pbx - pax, dy = pby - pay;
            double s = (tbx * dy - tby * dx) / det;
            double u = (tax * dy - tay * dx) / det;
            double maxExt = 0.5 * Math.Min(aLen, bLen) + 50.0;
            if (s > 1e-6 && u > 1e-6 && s <= maxExt && u <= maxExt)
            {
                double ix = pax + s * tax, iy = pay + s * tay;   // 公共角点
                ExtendEnd(A, aStart, ix, iy);
                ExtendEnd(B, bStart, ix, iy);
            }
        }
        return (aStart, bStart);   // 无论基线是否延长都返回搭接 → 斜面端部延长量据此计算
    }

    /// <summary>端点外向切向（= 端点 − 相邻点，指向基线外侧）+ 端点坐标 + 线长。</summary>
    private static bool OutwardTangent(WorkLineSamples w, bool start,
        out double px, out double py, out double tx, out double ty, out double len)
    {
        px = py = tx = ty = len = 0;
        int n = w.Baseline.Count;
        var p0 = start ? w.Baseline[0] : w.Baseline[n - 1];
        var p1 = start ? w.Baseline[1] : w.Baseline[n - 2];
        px = p0.X; py = p0.Y;
        double vx = p0.X - p1.X, vy = p0.Y - p1.Y;
        double l = Math.Sqrt(vx * vx + vy * vy);
        if (l < 1e-9) return false;
        tx = vx / l; ty = vy / l;
        double acc = 0;
        for (int i = 1; i < n; i++)
        {
            var a = w.Baseline[i - 1]; var b = w.Baseline[i];
            acc += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        len = acc;
        return true;
    }

    /// <summary>
    /// 角部两斜面【直接延长相交】所需的每条工作线端部横向延长量(m)。
    /// 原理：高度 z 处斜面边缘 = 基线沿各自推进方向 di 平移 o(z)=d+(z−zDatum)/tanα；两条平移线的交点随 o
    /// 外移，解 s·u − t·v = (p_B−p_A) + o_B·b − o_A·a 得两端各需越过端点的延长量（u/v=端部外向切向，
    /// a/b=端部推进方向），对 zTop/zFloor 两个极端各解一次取大 + 余量 → 两面互相穿插即全高闭合。
    /// 近平行(|u×v| 小)不延：两面近共面本就无角部缺口。
    /// </summary>
    public static (double Start, double End)[] ComputeEndExtensions(
        IReadOnlyList<WorkLineSamples> wls, IReadOnlyList<CornerLink> links,
        double alphaDeg, double zTop, double zFloor, IReadOnlyList<double> advanceByLine)
    {
        var ext = new (double Start, double End)[wls?.Count ?? 0];
        if (wls == null || links == null || links.Count == 0) return ext;
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);

        foreach (var lk in links)
        {
            if (lk.A < 0 || lk.A >= wls.Count || lk.B < 0 || lk.B >= wls.Count) continue;
            var A = wls[lk.A]; var B = wls[lk.B];
            if (A == null || B == null) continue;
            if (!OutwardTangent(A, lk.AStart, out double pax, out double pay, out double ux, out double uy, out _)) continue;
            if (!OutwardTangent(B, lk.BStart, out double pbx, out double pby, out double vx, out double vy, out _)) continue;
            if (!EndDi(A, lk.AStart, out double ax, out double ay)) continue;
            if (!EndDi(B, lk.BStart, out double bx, out double by)) continue;
            double det = uy * vx - ux * vy;
            if (Math.Abs(det) < 0.05) continue;   // 近平行（夹角 <3°）：两面近共面，无缺口

            double zA = DatumZ(A), zB = DatumZ(B);
            double dA = (advanceByLine != null && lk.A < advanceByLine.Count) ? advanceByLine[lk.A] : 0;
            double dB = (advanceByLine != null && lk.B < advanceByLine.Count) ? advanceByLine[lk.B] : 0;
            double sMax = 0, tMax = 0;
            foreach (double z in new[] { zTop, zFloor })
            {
                // 平移量口径同 InclineSurfaceBuilder：上/下退距各自 clamp 到 0
                double oA = dA + (z >= zA ? (z - zA) : -(zA - z)) / tanA;
                double oB = dB + (z >= zB ? (z - zB) : -(zB - z)) / tanA;
                double wx = (pbx - pax) + oB * bx - oA * ax;
                double wy = (pby - pay) + oB * by - oA * ay;
                double s = (vx * wy - vy * wx) / det;
                double t = (ux * wy - uy * wx) / det;
                if (s > sMax) sMax = s;
                if (t > tMax) tMax = t;
            }
            double cap = 5.0 * (Math.Abs(dA) + Math.Abs(dB) + (zTop - Math.Min(zA, zB)) / tanA) + 50.0;
            double eA = Math.Min(sMax * 1.25 + 5.0, cap);   // 余量放大：宁可穿插过头，不留缝
            double eB = Math.Min(tMax * 1.25 + 5.0, cap);
            if (sMax > 0.5) { if (lk.AStart) ext[lk.A].Start = Math.Max(ext[lk.A].Start, eA); else ext[lk.A].End = Math.Max(ext[lk.A].End, eA); }
            if (tMax > 0.5) { if (lk.BStart) ext[lk.B].Start = Math.Max(ext[lk.B].Start, eB); else ext[lk.B].End = Math.Max(ext[lk.B].End, eB); }
        }
        return ext;
    }

    private static double DatumZ(WorkLineSamples w)
    {
        int n = w.Baseline.Count; if (n == 0) return 0;
        double z = 0; for (int i = 0; i < n; i++) z += w.Baseline[i].Z;
        return z / n;
    }

    /// <summary>端部推进方向 di（该端所在段的单位方向）。</summary>
    private static bool EndDi(WorkLineSamples w, bool start, out double dx, out double dy)
    {
        dx = 1; dy = 0;
        if (w.Samples.Count == 0) return false;
        var s = start ? w.Samples[0] : w.Samples[w.Samples.Count - 1];
        double l = Math.Sqrt(s.Dx * s.Dx + s.Dy * s.Dy);
        if (l < 1e-9) return false;
        dx = s.Dx / l; dy = s.Dy / l;
        return true;
    }

    /// <summary>把公共角点 (ix,iy) 接到工作线选定端：新增基线点 + 对应段（段推进方向沿该端 di）。</summary>
    private static void ExtendEnd(WorkLineSamples w, bool start, double ix, double iy)
    {
        int n = w.Baseline.Count;
        var near = start ? w.Baseline[0] : w.Baseline[n - 1];
        double iz = near.Z;   // 保持基准标高一致
        double dux, duy;
        if (w.Samples.Count > 0)
        {
            var smp = start ? w.Samples[0] : w.Samples[w.Samples.Count - 1];
            dux = smp.Dx; duy = smp.Dy;
        }
        else { dux = 1; duy = 0; }
        double amx = (near.X + ix) * 0.5, amy = (near.Y + iy) * 0.5;
        if (start)
        {
            w.Baseline.Insert(0, (ix, iy, iz));
            w.Samples.Insert(0, (amx, amy, iz, dux, duy));
        }
        else
        {
            w.Baseline.Add((ix, iy, iz));
            w.Samples.Add((amx, amy, iz, dux, duy));
        }
    }
}
