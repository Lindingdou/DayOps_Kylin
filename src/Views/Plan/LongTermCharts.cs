using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 进度计划「对比论证图表」+「方案出图」纯 Canvas 画（原 <c>LongTermCharts</c>）。数据全来自已排产方案的逐期表 + 指标：
///  ① 逐年产量 + 达产线　② 生产剥采比削峰曲线 SR(t) + n经线　③ 累计 NPV 曲线
///  ④ 生产时相甘特（基建/爬坡/稳产/减产）　⑤ 多指标综合雷达　⑥ 单方案进度图（出图用）。
/// </summary>
internal static class LongTermCharts
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x37, 0x8A, 0xDD), Color.FromRgb(0x1D, 0x9E, 0x75), Color.FromRgb(0xD8, 0x5A, 0x30),
        Color.FromRgb(0xBA, 0x75, 0x17), Color.FromRgb(0x99, 0x35, 0x56), Color.FromRgb(0x6D, 0x4A, 0xC4),
    };
    private static IBrush SchemeBrush(int i) => new SolidColorBrush(Palette[i % Palette.Length]);
    private static IBrush Axis => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
    private static IBrush Hint => new SolidColorBrush(Color.FromArgb(0xAA, 0x66, 0x66, 0x66));
    private static readonly Color Red = Color.FromRgb(0xEF, 0x44, 0x44);

    private static (double w, double h) Sz(Canvas c) => (c.Bounds.Width > 12 ? c.Bounds.Width : 300, c.Bounds.Height > 12 ? c.Bounds.Height : 150);

    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, IBrush b, double th = 1, double[]? dash = null)
    {
        var l = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th };
        if (dash != null) l.StrokeDashArray = new AvaloniaList<double>(dash);
        c.Children.Add(l);
    }
    private static void AddText(Canvas c, double x, double y, string t, IBrush b, double size = 9)
    {
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); c.Children.Add(tb);
    }
    private static void AddRect(Canvas c, double x, double y, double w, double h, IBrush b)
    {
        if (h <= 0 || w <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = b };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
    }
    private static string Short(string n) => string.IsNullOrEmpty(n) ? "" : n.Split('·').Last();
    private static bool Empty(Canvas c, List<LongTermPlan> s)
    {
        c.Children.Clear();
        if (s.Count == 0) { var (w, h) = Sz(c); AddText(c, 8, h / 2 - 8, "（排产后显示）", Hint); return true; }
        return false;
    }
    public static Color PhaseColor(PlanPhase ph) => ph switch
    {
        PlanPhase.Basic => Color.FromRgb(0x9A, 0x9A, 0x9A), PlanPhase.RampUp => Color.FromRgb(0x37, 0x8A, 0xDD),
        PlanPhase.Stable => Color.FromRgb(0x1D, 0x9E, 0x75), PlanPhase.Decline => Color.FromRgb(0xBA, 0x75, 0x17), _ => Color.FromRgb(0xAA, 0xAA, 0xAA)
    };

    /// <summary>① 逐年产量 + 达产线。</summary>
    public static void DrawOutputCurve(Canvas c, List<LongTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 30, y0 = h - 14, x1 = w - 6, y1 = 8;
        int maxN = Math.Max(1, s.Max(p => p.Periods.Count));
        double Ap = s.Max(p => p.DesignCapacityWanTa);
        double maxV = Math.Max(Ap, s.SelectMany(p => p.Periods).Select(z => z.CoalWanT).DefaultIfEmpty(1).Max()) * 1.1;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, x1 - 12, y0 + 1, "a", Hint); AddText(c, 0, y1 - 2, "万t", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        double SY(double v) => y0 - v / maxV * (y0 - y1);
        AddLine(c, x0, SY(Ap), x1, SY(Ap), new SolidColorBrush(Red), 1, new[] { 3.0, 2 });
        AddText(c, x0 + 2, SY(Ap) - 11, $"达产 A_p={Ap:0}", new SolidColorBrush(Red), 8);
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point>(); int k = 0;
            foreach (var z in s[i].Periods) pts.Add(new Point(SX(k++), SY(z.CoalWanT)));
            if (pts.Count > 0) c.Children.Add(new Polyline { Points = pts, Stroke = SchemeBrush(i), StrokeThickness = 2 });
            AddText(c, x0 + 4 + i * 64, y1, Short(s[i].Name), SchemeBrush(i), 8);
        }
    }

    /// <summary>② 生产剥采比削峰曲线 SR(t) + 经济合理剥采比线。</summary>
    public static void DrawSrCurve(Canvas c, List<LongTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 28, y0 = h - 14, x1 = w - 6, y1 = 8;
        int maxN = Math.Max(1, s.Max(p => p.Periods.Count));
        double nEco = s.Max(p => p.EconomicStripRatioMax);
        double maxSr = Math.Max(nEco, s.SelectMany(p => p.Periods).Select(z => z.Ratio).DefaultIfEmpty(1).Max()) * 1.1;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, x1 - 12, y0 + 1, "a", Hint); AddText(c, 0, y1 - 2, "m³/t", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        double SY(double v) => y0 - v / maxSr * (y0 - y1);
        AddLine(c, x0, SY(nEco), x1, SY(nEco), new SolidColorBrush(Red), 1, new[] { 3.0, 2 });
        AddText(c, x0 + 2, SY(nEco) - 11, $"n经={nEco:0}", new SolidColorBrush(Red), 8);
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point>(); int k = 0;
            foreach (var z in s[i].Periods) { if (z.CoalWanT > 0) pts.Add(new Point(SX(k), SY(z.Ratio))); k++; }
            if (pts.Count > 0) c.Children.Add(new Polyline { Points = pts, Stroke = SchemeBrush(i), StrokeThickness = 2 });
        }
    }

    /// <summary>③ 累计 NPV 曲线。</summary>
    public static void DrawNpvCurve(Canvas c, List<LongTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 34, y0 = h - 14, x1 = w - 6, y1 = 8;
        int maxN = Math.Max(1, s.Max(p => p.Periods.Count));
        var cums = s.Select(Accum).ToList();
        double maxV = Math.Max(1, cums.SelectMany(z => z).DefaultIfEmpty(1).Max());
        double minV = Math.Min(0, cums.SelectMany(z => z).DefaultIfEmpty(0).Min());
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, 0, y1 - 2, "万元", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        double SY(double v) => y0 - (v - minV) / Math.Max(1, maxV - minV) * (y0 - y1);
        if (minV < 0) AddLine(c, x0, SY(0), x1, SY(0), Axis, 0.6, new[] { 2.0, 2 });
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point>();
            for (int k = 0; k < cums[i].Count; k++) pts.Add(new Point(SX(k), SY(cums[i][k])));
            if (pts.Count > 0) c.Children.Add(new Polyline { Points = pts, Stroke = SchemeBrush(i), StrokeThickness = 2 });
        }
    }
    private static List<double> Accum(LongTermPlan p)
    {
        var list = new List<double>(); double cum = 0;
        foreach (var z in p.Periods) { cum += z.NpvWan; list.Add(cum); }
        return list;
    }

    /// <summary>④ 生产时相甘特。</summary>
    public static void DrawPhaseGantt(Canvas c, List<LongTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 64, y0 = h - 14, x1 = w - 6, y1 = 6;
        int maxN = Math.Max(1, s.Max(p => p.Periods.Count));
        AddLine(c, x0, y0, x1, y0, Axis);
        AddText(c, x1 - 12, y0 + 1, "a", Hint);
        AddText(c, 0, 0, "■基建 ■爬坡 ■稳产 ■减产", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        int n = s.Count; double rowH = (y0 - y1) / Math.Max(1, n);
        for (int i = 0; i < n; i++)
        {
            double ry = y1 + i * rowH + 2, rh = Math.Max(5, rowH - 4);
            AddText(c, 2, ry, Short(s[i].Name), Hint, 8);
            for (int k = 0; k < s[i].Periods.Count; k++)
            {
                double xa = SX(k), xb = SX(k + 1);
                AddRect(c, xa, ry, Math.Max(1, xb - xa - 0.5), rh, new SolidColorBrush(PhaseColor(s[i].Periods[k].Phase)));
            }
        }
    }

    /// <summary>⑤ 多指标综合雷达：削峰/达产/内排/均衡/NPV/稳产。</summary>
    public static void DrawRadar(Canvas c, List<LongTermPlan> s)
    {
        if (Empty(c, s)) return;
        var withR = s.Where(p => p.Result != null).ToList();
        if (withR.Count == 0) { var (ww, hh) = Sz(c); AddText(c, 8, hh / 2 - 8, "（排产后显示）", Hint); return; }
        var (w, h) = Sz(c);
        double cx = w / 2, cy = h / 2 + 2, R = Math.Min(w, h) / 2 - 18;
        string[] ax = { "削峰", "达产", "内排", "均衡", "NPV", "稳产" };
        int m = ax.Length;
        double[] peak = withR.Select(p => p.Result!.ProductionRatioPeak).ToArray();
        double[] ttc = withR.Select(p => p.Result!.TimeToCapacityYears).ToArray();
        double[] inn = withR.Select(p => p.Result!.InnerDumpPct).ToArray();
        double[] bal = withR.Select(p => p.Result!.ReserveBalanceCoef).ToArray();
        double[] npv = withR.Select(p => p.Result!.Npv).ToArray();
        double[] plt = withR.Select(p => p.Result!.StablePlateauYears).ToArray();
        for (int k = 0; k < m; k++)
        {
            double a = -Math.PI / 2 + k * 2 * Math.PI / m;
            double ex = cx + R * Math.Cos(a), ey = cy + R * Math.Sin(a);
            AddLine(c, cx, cy, ex, ey, Axis);
            AddText(c, ex - 8, ey - 6, ax[k], Hint, 8);
        }
        for (int i = 0; i < withR.Count; i++)
        {
            double[] v = { NL(peak, i), NL(ttc, i), NH(inn, i), NH(bal, i), NH(npv, i), NH(plt, i) };
            var pts = new List<Point>();
            for (int k = 0; k < m; k++)
            {
                double a = -Math.PI / 2 + k * 2 * Math.PI / m;
                double rr = R * Math.Clamp(v[k], 0.05, 1);
                pts.Add(new Point(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a)));
            }
            var col = Palette[i % Palette.Length];
            c.Children.Add(new Polygon { Points = pts, Stroke = new SolidColorBrush(col), StrokeThickness = 1.6, Fill = new SolidColorBrush(Color.FromArgb(0x22, col.R, col.G, col.B)) });
        }
    }

    /// <summary>⑥ 单方案进度图（出图用）：逐年产量柱 + 达产线 + 生产剥采比折线 + 时相底色带。</summary>
    public static void DrawScheduleSheet(Canvas c, LongTermPlan? p)
    {
        c.Children.Clear();
        var (w, h) = Sz(c);
        if (p == null || p.Periods.Count == 0) { AddText(c, 8, h / 2 - 8, "（请先排产，再选方案出图）", Hint); return; }
        double x0 = 40, y0 = h - 26, x1 = w - 40, y1 = 14;
        var ps = p.Periods.ToList();
        int n = ps.Count;
        double Ap = p.DesignCapacityWanTa;
        double maxV = Math.Max(Ap, ps.Select(z => z.CoalWanT).DefaultIfEmpty(1).Max()) * 1.15;
        double maxSr = Math.Max(p.EconomicStripRatioMax, ps.Select(z => z.Ratio).DefaultIfEmpty(1).Max()) * 1.15;
        double slot = (x1 - x0) / n;
        for (int k = 0; k < n; k++)
        {
            var col = PhaseColor(ps[k].Phase);
            AddRect(c, x0 + k * slot, y1, slot, y0 - y1, new SolidColorBrush(Color.FromArgb(0x16, col.R, col.G, col.B)));
        }
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis); AddLine(c, x1, y1, x1, y0, Axis);
        AddText(c, 2, y1 - 2, "万t", Hint, 9); AddText(c, x1 - 22, y1 - 2, "m³/t", Hint, 9);
        double BX(int i) => x0 + (i + 0.5) * slot;
        double LY(double v) => y0 - v / maxV * (y0 - y1);
        double RY(double v) => y0 - v / maxSr * (y0 - y1);
        var bar = new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD));
        for (int k = 0; k < n; k++)
        {
            double bw = Math.Max(2, slot * 0.55), bx = x0 + k * slot + (slot - bw) / 2;
            AddRect(c, bx, LY(ps[k].CoalWanT), bw, y0 - LY(ps[k].CoalWanT), bar);
        }
        AddLine(c, x0, LY(Ap), x1, LY(Ap), new SolidColorBrush(Red), 1, new[] { 4.0, 2 });
        AddText(c, x0 + 2, LY(Ap) - 12, $"达产 A_p={Ap:0}万t/a", new SolidColorBrush(Red), 9);
        AddLine(c, x0, RY(p.EconomicStripRatioMax), x1, RY(p.EconomicStripRatioMax), new SolidColorBrush(Color.FromArgb(0x99, 0xBA, 0x75, 0x17)), 1, new[] { 2.0, 2 });
        var srPts = new List<Point>();
        for (int k = 0; k < n; k++) if (ps[k].CoalWanT > 0) srPts.Add(new Point(BX(k), RY(ps[k].Ratio)));
        if (srPts.Count > 0) c.Children.Add(new Polyline { Points = srPts, Stroke = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)), StrokeThickness = 2 });
        for (int k = 0; k < n; k++)
            if (ps[k].IsDesignCalcYear) AddText(c, BX(k) - 14, y0 + 2, "◆达产", new SolidColorBrush(Red), 8);
        int step = Math.Max(1, n / 12);
        for (int k = 0; k < n; k += step) AddText(c, x0 + k * slot, y0 + 12, ps[k].Label, Hint, 8);
        AddText(c, x0 + 2, y1, "■产量(万t)  —生产剥采比(m³/t)  时相底色：基建/爬坡/稳产/减产", Hint, 8);
    }

    private static double NH(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NL(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}
