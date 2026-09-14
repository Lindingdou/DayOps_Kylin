using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 工作线的投影几何（对应原 <c>MineAssLib.Models.WorkLineGeometry</c>：内核 <c>PitMine_GetSelectedWorkLineGeometryBin</c> 的产物）。
/// 基线点 + 逐段(锚点=段中点, 单位推进方向)。平行 = 各段同向；扇形 = 各段绕回转中心放射。
///
/// Kylin 无内核：由 <see cref="FromWorkLine"/> 按「创建工作线」落图的 基线 / 结束线 / 回转中心 反算（登记）。
/// </summary>
public sealed class WorkLineSamples
{
    public bool   Success;
    public string Error = "";
    public byte   AdvanceMode;   // 0=平行 2=扇形
    public byte   DirMode;       // 0=统一 1=逐段
    public bool   Closed;
    public float  ArrowLength;   // 原 WorkLineGeometry 的箭头长（只随几何带走，投影不用）
    public readonly List<(double X, double Y, double Z)> Baseline = new();
    /// <summary>逐段：锚点(段中点) + 单位推进方向(XY)。</summary>
    public readonly List<(double Ax, double Ay, double Az, double Dx, double Dy)> Samples = new();

    public bool   HasFanParams;
    public byte   RotDir;         // 0=逆时针 1=顺时针
    public double PivotX, PivotY, PivotZ;

    /// <summary>
    /// 由 Kylin 图上的工作线反算：直线 = 各段同向（推进方向 = 基线中点 → 结束线中点，没结束线就取 <paramref name="fallbackDir"/>）；
    /// 扇形 = 各段推进方向 = 该段中点绕回转中心的切向（旋向按结束线相对基线的转角符号定）。
    /// </summary>
    public static WorkLineSamples FromWorkLine(IReadOnlyList<(double x, double y)> baseXy, double z, bool fan,
                                               IReadOnlyList<(double x, double y)>? endXy, (double x, double y)? pivot,
                                               (double dx, double dy)? fallbackDir = null, bool closed = false)
    {
        var g = new WorkLineSamples { AdvanceMode = (byte)(fan ? 2 : 0), Closed = closed };
        if (baseXy == null || baseXy.Count < 2) { g.Error = "基线点 < 2"; return g; }
        foreach (var p in baseXy) g.Baseline.Add((p.x, p.y, z));
        int cnt = baseXy.Count;
        int nSeg = cnt - 1 + (closed ? 1 : 0);

        var (dx, dy, sweep) = WorkLineModel.Infer(baseXy, endXy ?? Array.Empty<(double, double)>(), pivot);
        if (fan && pivot is { } pv)
        {
            g.HasFanParams = true; g.PivotX = pv.x; g.PivotY = pv.y; g.PivotZ = z;
            double sgn = sweep >= 0 ? 1 : -1;
            g.RotDir = (byte)(sgn > 0 ? 0 : 1);
            g.DirMode = 1;
            for (int i = 0; i < nSeg; i++)
            {
                var a = baseXy[i]; var b = baseXy[(i + 1) % cnt];
                double mx = (a.x + b.x) * 0.5, my = (a.y + b.y) * 0.5;
                double rx = mx - pv.x, ry = my - pv.y, rl = Math.Sqrt(rx * rx + ry * ry);
                double tx = rl < 1e-9 ? dx : -ry / rl * sgn, ty = rl < 1e-9 ? dy : rx / rl * sgn;   // 绕中心转 +θ 时点沿 (−ry, rx) 走
                g.Samples.Add((mx, my, z, tx, ty));
            }
        }
        else
        {
            if (dx * dx + dy * dy < 1e-12 && fallbackDir is { } fd) { dx = fd.dx; dy = fd.dy; }
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) { g.Error = "定不出推进方向（无结束线、无兜底方向）"; return g; }
            dx /= l; dy /= l;
            g.DirMode = 0;
            for (int i = 0; i < nSeg; i++)
            {
                var a = baseXy[i]; var b = baseXy[(i + 1) % cnt];
                g.Samples.Add(((a.x + b.x) * 0.5, (a.y + b.y) * 0.5, z, dx, dy));
            }
        }
        g.Success = true;
        return g;
    }
}

/// <summary>
/// 多工作线「点→推进坐标」投影器（忠实移植原 <c>WorkLineProjector</c>）。
///
/// 给平面点 (x,y)，指派到【最近工作线段】（按纵向落在段内、横向取 |a0| 最小的段），返回：
///   · a0     = 该点沿推进方向 di 的有符号退距（推进坐标；+di=坡顶/前进侧）；
///   · zDatum = 该工作线的基准标高（各顶点 Z 均值）；
///   · s      = 该点沿工作线【走向】的弧长坐标（自基线首顶点起算）。
/// 纵向 |t| ≤ 段半长 + latTol 才算被扫掠，横向不设上限。纯几何、只读、线程安全。
/// </summary>
public sealed class WorkLineProjector
{
    private readonly int _l;
    private readonly double[][] _mx, _my, _tx, _ty, _half, _dx, _dy;
    private readonly double[][] _cum;
    private readonly double[] _z;
    private readonly double _latTol;

    private WorkLineProjector(int l, double[][] mx, double[][] my, double[][] tx, double[][] ty,
                              double[][] half, double[][] dx, double[][] dy, double[][] cum, double[] z, double latTol)
    { _l = l; _mx = mx; _my = my; _tx = tx; _ty = ty; _half = half; _dx = dx; _dy = dy; _cum = cum; _z = z; _latTol = latTol; }

    public bool HasAny
    {
        get { for (int l = 0; l < _l; l++) if (_mx[l].Length > 0) return true; return false; }
    }

    public static WorkLineProjector Build(IReadOnlyList<WorkLineSamples> workLines, double latTol)
    {
        int L = workLines?.Count ?? 0;
        var mx = new double[L][]; var my = new double[L][]; var tx = new double[L][]; var ty = new double[L][];
        var half = new double[L][]; var dx = new double[L][]; var dy = new double[L][]; var cum = new double[L][];
        var z = new double[L];
        for (int l = 0; l < L; l++)
        {
            var wl = workLines![l];
            if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1)
            {
                mx[l] = Array.Empty<double>(); my[l] = Array.Empty<double>(); tx[l] = Array.Empty<double>();
                ty[l] = Array.Empty<double>(); half[l] = Array.Empty<double>(); dx[l] = Array.Empty<double>();
                dy[l] = Array.Empty<double>(); cum[l] = Array.Empty<double>();
                continue;
            }
            int cnt = wl.Baseline.Count;
            int nSeg = Math.Min(wl.Samples.Count, cnt - 1 + (wl.Closed ? 1 : 0));
            mx[l] = new double[nSeg]; my[l] = new double[nSeg]; tx[l] = new double[nSeg]; ty[l] = new double[nSeg];
            half[l] = new double[nSeg]; dx[l] = new double[nSeg]; dy[l] = new double[nSeg]; cum[l] = new double[nSeg + 1];
            double zsum = 0; for (int i = 0; i < cnt; i++) zsum += wl.Baseline[i].Z; z[l] = zsum / cnt;
            for (int i = 0; i < nSeg; i++)
            {
                var a = wl.Baseline[i]; var b = wl.Baseline[(i + 1) % cnt];
                double sx = b.X - a.X, sy = b.Y - a.Y;
                double sl = Math.Sqrt(sx * sx + sy * sy); if (sl < 1e-9) { sx = 1; sy = 0; sl = 1; }
                tx[l][i] = sx / sl; ty[l][i] = sy / sl; half[l][i] = sl * 0.5;
                mx[l][i] = (a.X + b.X) * 0.5; my[l][i] = (a.Y + b.Y) * 0.5;
                cum[l][i + 1] = cum[l][i] + sl;
                double ddx = wl.Samples[i].Dx, ddy = wl.Samples[i].Dy;
                double dl = Math.Sqrt(ddx * ddx + ddy * ddy); if (dl < 1e-9) { ddx = -ty[l][i]; ddy = tx[l][i]; dl = 1; }
                dx[l][i] = ddx / dl; dy[l][i] = ddy / dl;
            }
        }
        return new WorkLineProjector(L, mx, my, tx, ty, half, dx, dy, cum, z, latTol);
    }

    public double StrikeLength(int line) => line >= 0 && line < _l && _cum[line].Length > 0 ? _cum[line][_cum[line].Length - 1] : 0;

    public bool TryProject(double x, double y, out double a0, out double zDatum) => TryProject(x, y, out a0, out zDatum, out _);
    public bool TryProject(double x, double y, out double a0, out double zDatum, out int line) => TryProject(x, y, out a0, out zDatum, out line, out _);

    public bool TryProject(double x, double y, out double a0, out double zDatum, out int line, out double s)
    {
        a0 = 0; zDatum = 0; line = -1; s = 0;
        int bestL = -1; double bestPerp = double.MaxValue, bestA0 = 0, bestS = 0;
        for (int l = 0; l < _l; l++)
        {
            int nSeg = _mx[l].Length;
            for (int i = 0; i < nSeg; i++)
            {
                double rx = x - _mx[l][i], ry = y - _my[l][i];
                double t = rx * _tx[l][i] + ry * _ty[l][i];
                if (Math.Abs(t) > _half[l][i] + _latTol) continue;
                double proj = rx * _dx[l][i] + ry * _dy[l][i];
                if (Math.Abs(proj) < bestPerp)
                { bestPerp = Math.Abs(proj); bestL = l; bestA0 = proj; bestS = _cum[l][i] + (t + _half[l][i]); }
            }
        }
        if (bestL < 0) return false;
        a0 = bestA0; zDatum = _z[bestL]; line = bestL; s = bestS;
        return true;
    }
}
