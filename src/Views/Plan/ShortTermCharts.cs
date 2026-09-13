using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 月度计划「对比论证图表」+「方案出图」纯 Canvas 画（原 <c>ShortTermCharts</c>，不引第三方库）。数据全来自已排产方案的逐月表 + 指标：
///  ① 逐月产量 + 年目标月均线　② 生产剥采比削峰曲线 + 上限线　③ 累计完成率曲线 vs 理想线
///  ④ 设备利用率 + 工作日条　⑤ 多指标综合雷达　⑥ 单方案月度计划图（出图用）。主色用短期组的橙。
/// </summary>
internal static class ShortTermCharts
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xEA, 0x58, 0x0C), Color.FromRgb(0x1D, 0x9E, 0x75), Color.FromRgb(0x37, 0x8A, 0xDD),
        Color.FromRgb(0xBA, 0x75, 0x17), Color.FromRgb(0x99, 0x35, 0x56), Color.FromRgb(0x6D, 0x4A, 0xC4),
    };
    private static IBrush SchemeBrush(int i) => new SolidColorBrush(Palette[i % Palette.Length]);
    private static IBrush Axis => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
    private static IBrush Hint => new SolidColorBrush(Color.FromArgb(0xAA, 0x66, 0x66, 0x66));
    private static IBrush Red => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

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
    private static void AddPolyline(Canvas c, IList<Point> pts, IBrush b, double th)
    {
        if (pts.Count == 0) return;
        c.Children.Add(new Polyline { Points = new AvaloniaList<Point>(pts), Stroke = b, StrokeThickness = th });
    }
    private static string Short(string n) => string.IsNullOrEmpty(n) ? "" : n.Split('·').Last();

    private static bool Empty(Canvas c, List<ShortTermPlan> s)
    {
        c.Children.Clear();
        if (s.Count == 0) { var (w, h) = Sz(c); AddText(c, 8, h / 2 - 8, "（排产后显示）", Hint); return true; }
        return false;
    }

    // ── ① 逐月产量 + 年目标月均线 ──
    public static void DrawMonthlyOutput(Canvas c, List<ShortTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 30, y0 = h - 14, x1 = w - 6, y1 = 8;
        int maxN = Math.Max(1, s.Max(p => p.Months.Count));
        double avgTarget = s.Max(p => p.MonthCount > 0 ? p.AnnualCoalTargetWanT / p.MonthCount : 0);
        double maxV = Math.Max(avgTarget, s.SelectMany(p => p.Months).Select(z => z.CoalWanT).DefaultIfEmpty(1).Max()) * 1.15;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, x1 - 14, y0 + 1, "月", Hint); AddText(c, 0, y1 - 2, "万t", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        double SY(double v) => y0 - v / maxV * (y0 - y1);
        AddLine(c, x0, SY(avgTarget), x1, SY(avgTarget), Red, 1, new[] { 3.0, 2.0 });
        AddText(c, x0 + 2, SY(avgTarget) - 11, $"目标月均={avgTarget:0.0}", Red, 8);
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point>(); int k = 0;
            foreach (var z in s[i].Months) pts.Add(new Point(SX(k++), SY(z.CoalWanT)));
            AddPolyline(c, pts, SchemeBrush(i), 2);
            AddText(c, x0 + 4 + i * 70, y1, Short(s[i].Name), SchemeBrush(i), 8);
        }
    }

    // ── ② 生产剥采比削峰曲线 + 上限线 ──
    public static void DrawRatioCurve(Canvas c, List<ShortTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 28, y0 = h - 14, x1 = w - 6, y1 = 8;
        int maxN = Math.Max(1, s.Max(p => p.Months.Count));
        double ceil = s.Max(p => p.RatioCeiling);
        double maxSr = Math.Max(ceil, s.SelectMany(p => p.Months).Select(z => z.Ratio).DefaultIfEmpty(1).Max()) * 1.15;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, x1 - 14, y0 + 1, "月", Hint); AddText(c, 0, y1 - 2, "m³/t", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        double SY(double v) => y0 - v / maxSr * (y0 - y1);
        AddLine(c, x0, SY(ceil), x1, SY(ceil), Red, 1, new[] { 3.0, 2.0 });
        AddText(c, x0 + 2, SY(ceil) - 11, $"上限={ceil:0}", Red, 8);
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point>(); int k = 0;
            foreach (var z in s[i].Months) pts.Add(new Point(SX(k++), SY(z.Ratio)));
            AddPolyline(c, pts, SchemeBrush(i), 2);
        }
    }

    // ── ③ 累计完成率曲线 vs 理想线 ──
    public static void DrawCompletionCurve(Canvas c, List<ShortTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 30, y0 = h - 14, x1 = w - 6, y1 = 8;
        int maxN = Math.Max(1, s.Max(p => p.Months.Count));
        double maxV = 100;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, x1 - 14, y0 + 1, "月", Hint); AddText(c, 0, y1 - 2, "%", Hint, 8);
        double SX(int i) => x0 + (double)i / maxN * (x1 - x0);
        double SY(double v) => y0 - v / maxV * (y0 - y1);
        AddLine(c, SX(0), SY(0), SX(maxN), SY(100), new SolidColorBrush(Color.FromArgb(0x99, 0x88, 0x88, 0x88)), 1, new[] { 2.0, 2.0 });
        AddText(c, x0 + 2, y1, "··· 理想匀速完成", Hint, 8);
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point> { new(SX(0), SY(0)) }; int k = 1;
            foreach (var z in s[i].Months) pts.Add(new Point(SX(k++), SY(z.CompletionPct)));
            AddPolyline(c, pts, SchemeBrush(i), 2);
        }
    }

    // ── ④ 设备利用率折线 + 工作日条（单方案视角：取首个方案）──
    public static void DrawEquipUtil(Canvas c, List<ShortTermPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 30, y0 = h - 16, x1 = w - 6, y1 = 8;
        var p = s[0];
        var ms = p.Months.ToList();
        int n = Math.Max(1, ms.Count);
        double maxUtil = Math.Max(100, ms.Select(z => z.EquipUtilPct).DefaultIfEmpty(100).Max()) * 1.1;
        double maxWd = Math.Max(1, ms.Select(z => z.Workdays).DefaultIfEmpty(25).Max());
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, 0, y1 - 2, "%", Hint, 8);
        AddText(c, x0 + 2, y1, $"{Short(p.Name)}：■工作日  —设备利用率(100%线)", Hint, 8);
        double slot = (x1 - x0) / n;
        var wdBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xEA, 0x58, 0x0C));
        for (int k = 0; k < n; k++)
        {
            double bh = ms[k].Workdays / maxWd * (y0 - y1);
            double bw = Math.Max(2, slot * 0.6), bx = x0 + k * slot + (slot - bw) / 2;
            AddRect(c, bx, y0 - bh, bw, bh, wdBrush);
        }
        AddLine(c, x0, y0 - 100 / maxUtil * (y0 - y1), x1, y0 - 100 / maxUtil * (y0 - y1), Red, 0.8, new[] { 3.0, 2.0 });
        var pts = new List<Point>();
        for (int k = 0; k < n; k++) pts.Add(new Point(x0 + (k + 0.5) * slot, y0 - ms[k].EquipUtilPct / maxUtil * (y0 - y1)));
        AddPolyline(c, pts, new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)), 2);
    }

    // ── ⑤ 多指标综合雷达：完成/均衡/设备/削峰/推进（跨方案归一）──
    public static void DrawRadar(Canvas c, List<ShortTermPlan> s)
    {
        if (Empty(c, s)) return;
        var withR = s.Where(p => p.Result != null).ToList();
        if (withR.Count == 0) { var (ww, hh) = Sz(c); AddText(c, 8, hh / 2 - 8, "（排产后显示）", Hint); return; }
        var (w, h) = Sz(c);
        double cx = w / 2, cy = h / 2 + 2, R = Math.Min(w, h) / 2 - 18;
        string[] ax = { "完成", "均衡", "设备", "削峰", "推进" };
        int m = ax.Length;
        double[] compDev = withR.Select(p => Math.Abs(p.Result!.CompletionRatePct - 100)).ToArray();
        double[] bal = withR.Select(p => p.Result!.BalanceCoef).ToArray();
        double[] utilFit = withR.Select(p => -Math.Abs(p.Result!.AvgEquipUtilPct - 90)).ToArray();
        double[] peak = withR.Select(p => p.Result!.PeakMonthCoalWanT).ToArray();
        double[] adv = withR.Select(p => p.Result!.AdvanceTotalM).ToArray();
        for (int k = 0; k < m; k++)
        {
            double a = -Math.PI / 2 + k * 2 * Math.PI / m;
            double ex = cx + R * Math.Cos(a), ey = cy + R * Math.Sin(a);
            AddLine(c, cx, cy, ex, ey, Axis);
            AddText(c, ex - 8, ey - 6, ax[k], Hint, 8);
        }
        for (int i = 0; i < withR.Count; i++)
        {
            double[] v = { NL(compDev, i), NH(bal, i), NH(utilFit, i), NL(peak, i), NH(adv, i) };
            var pts = new List<Point>();
            for (int k = 0; k < m; k++)
            {
                double a = -Math.PI / 2 + k * 2 * Math.PI / m;
                double rr = R * Math.Clamp(v[k], 0.05, 1);
                pts.Add(new Point(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a)));
            }
            var col = Palette[i % Palette.Length];
            c.Children.Add(new Polygon { Points = new AvaloniaList<Point>(pts), Stroke = new SolidColorBrush(col), StrokeThickness = 1.6, Fill = new SolidColorBrush(Color.FromArgb(0x22, col.R, col.G, col.B)) });
        }
    }

    // ── ⑥ 单方案月度计划图（出图用）：逐月产量柱 + 目标月均线 + 生产剥采比折线 ──
    public static void DrawScheduleSheet(Canvas c, ShortTermPlan? p)
    {
        c.Children.Clear();
        var (w, h) = Sz(c);
        if (p == null || p.Months.Count == 0) { AddText(c, 8, h / 2 - 8, "（请先排产，再选方案出图）", Hint); return; }
        double x0 = 40, y0 = h - 26, x1 = w - 40, y1 = 14;
        var ms = p.Months.ToList();
        int n = ms.Count;
        double avgTarget = p.MonthCount > 0 ? p.AnnualCoalTargetWanT / p.MonthCount : 0;
        double maxV = Math.Max(avgTarget, ms.Select(z => z.CoalWanT).DefaultIfEmpty(1).Max()) * 1.2;
        double maxSr = Math.Max(p.RatioCeiling, ms.Select(z => z.Ratio).DefaultIfEmpty(1).Max()) * 1.2;
        double slot = (x1 - x0) / n;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis); AddLine(c, x1, y1, x1, y0, Axis);
        AddText(c, 2, y1 - 2, "万t", Hint, 9); AddText(c, x1 - 22, y1 - 2, "m³/t", Hint, 9);
        double BX(int i) => x0 + (i + 0.5) * slot;
        double LY(double v) => y0 - v / maxV * (y0 - y1);
        double RY(double v) => y0 - v / maxSr * (y0 - y1);
        for (int k = 0; k < n; k++)
        {
            double bw = Math.Max(2, slot * 0.55), bx = x0 + k * slot + (slot - bw) / 2;
            IBrush b = ms[k].IsMaintenance ? new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A))
                     : ms[k].IsPeak ? new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C))
                     : new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
            AddRect(c, bx, LY(ms[k].CoalWanT), bw, y0 - LY(ms[k].CoalWanT), b);
        }
        AddLine(c, x0, LY(avgTarget), x1, LY(avgTarget), Red, 1, new[] { 4.0, 2.0 });
        AddText(c, x0 + 2, LY(avgTarget) - 12, $"目标月均={avgTarget:0.0}万t", Red, 9);
        AddLine(c, x0, RY(p.RatioCeiling), x1, RY(p.RatioCeiling), new SolidColorBrush(Color.FromArgb(0x99, 0xBA, 0x75, 0x17)), 1, new[] { 2.0, 2.0 });
        var srPts = new List<Point>();
        for (int k = 0; k < n; k++) srPts.Add(new Point(BX(k), RY(ms[k].Ratio)));
        AddPolyline(c, srPts, new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)), 2);
        for (int k = 0; k < n; k++) AddText(c, x0 + k * slot + slot / 2 - 8, y0 + 2, $"{ms[k].Month}月", Hint, 8);
        AddText(c, x0 + 2, y1, "■月产量(万t)  —生产剥采比(m³/t)  灰=检修月 深橙=峰值月", Hint, 8);
    }

    private static double NH(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NL(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}
