using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using PitMine3D.Kylin.Cad.Plan;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「对比论证图表」纯 Canvas 画（原 <c>MiningProgramCharts</c>，不引第三方库）。数据全来自已求解方案的采区 + 指标：
/// ① 生产剥采比削峰曲线 SR(t)　② 多指标综合雷达　③ 内外排土方平衡堆叠柱　④ 采区接续甘特。
/// </summary>
internal static class MiningProgramCharts
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x37, 0x8A, 0xDD), Color.FromRgb(0x1D, 0x9E, 0x75), Color.FromRgb(0xD8, 0x5A, 0x30),
        Color.FromRgb(0xBA, 0x75, 0x17), Color.FromRgb(0x99, 0x35, 0x56),
    };
    private static IBrush SchemeBrush(int i) => new SolidColorBrush(Palette[i % Palette.Length]);
    private static IBrush Axis => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));
    private static IBrush Hint => new SolidColorBrush(Color.FromArgb(0xAA, 0x66, 0x66, 0x66));

    private static (double w, double h) Sz(Canvas c)
        => (c.Bounds.Width > 12 ? c.Bounds.Width : 300, c.Bounds.Height > 12 ? c.Bounds.Height : 150);

    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, IBrush b, double th = 1)
        => c.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th });

    private static void AddText(Canvas c, double x, double y, string t, IBrush b, double size = 9)
    {
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }

    private static void AddRect(Canvas c, double x, double y, double w, double h, IBrush b)
    {
        if (h <= 0 || w <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = b };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    private static string Short(string n) => string.IsNullOrEmpty(n) ? "" : n.Split('·')[0];

    private static bool Empty(Canvas c, List<MiningProgramPlan> s)
    {
        c.Children.Clear();
        if (s.Count == 0) { var (w, h) = Sz(c); AddText(c, 8, h / 2 - 8, "（求解后显示）", Hint); return true; }
        return false;
    }

    /// <summary>① 生产剥采比削峰曲线 SR(t)：每方案逐采区的剥采比随时间步进。</summary>
    public static void DrawSrCurve(Canvas c, List<MiningProgramPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 26, y0 = h - 14, x1 = w - 6, y1 = 6;
        double maxT = Math.Max(1, s.Max(p => p.Panels.Sum(q => q.ServiceLifeYears)));
        double maxSr = Math.Max(1, s.SelectMany(p => p.Panels).Select(q => q.StripRatio).DefaultIfEmpty(1).Max()) * 1.1;
        AddLine(c, x0, y0, x1, y0, Axis); AddLine(c, x0, y1, x0, y0, Axis);
        AddText(c, x1 - 12, y0 - 1, "a", Hint); AddText(c, 0, y1 - 2, "m³/t", Hint, 8);
        double SX(double t) => x0 + t / maxT * (x1 - x0);
        double SY(double v) => y0 - v / maxSr * (y0 - y1);
        for (int i = 0; i < s.Count; i++)
        {
            var pts = new List<Point>();
            double t = 0;
            foreach (var p in s[i].Panels.OrderBy(z => z.Order))
            { pts.Add(new Point(SX(t), SY(p.StripRatio))); t += p.ServiceLifeYears; pts.Add(new Point(SX(t), SY(p.StripRatio))); }
            if (pts.Count > 0) c.Children.Add(new Polyline { Points = pts, Stroke = SchemeBrush(i), StrokeThickness = 2 });
            AddText(c, x0 + 4 + i * 46, y1, Short(s[i].Name), SchemeBrush(i), 8);
        }
    }

    /// <summary>② 多指标综合雷达：削峰/达产/内排/均衡/综合（跨方案归一）。</summary>
    public static void DrawRadar(Canvas c, List<MiningProgramPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double cx = w / 2, cy = h / 2 + 2, R = Math.Min(w, h) / 2 - 16;
        string[] ax = { "削峰", "达产", "内排", "均衡", "综合" };
        int m = ax.Length;
        double[] peak = s.Select(p => p.Result!.ProductionRatioPeak).ToArray();
        double[] ttc = s.Select(p => p.Result!.TimeToCapacityYears).ToArray();
        double[] inn = s.Select(p => p.Result!.InnerDumpPct).ToArray();
        double[] bal = s.Select(p => p.Result!.ReserveBalanceCoef).ToArray();
        double[] sc = s.Select(p => p.Result!.CompositeScore).ToArray();
        for (int k = 0; k < m; k++)
        {
            double a = -Math.PI / 2 + k * 2 * Math.PI / m;
            double ex = cx + R * Math.Cos(a), ey = cy + R * Math.Sin(a);
            AddLine(c, cx, cy, ex, ey, Axis);
            AddText(c, ex - 8, ey - 6, ax[k], Hint, 8);
        }
        for (int i = 0; i < s.Count; i++)
        {
            double[] v = { NL(peak, i), NL(ttc, i), NH(inn, i), NH(bal, i), NH(sc, i) };
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

    /// <summary>③ 内外排土方平衡堆叠柱：每方案 外排(底·amber) + 内排(顶·teal)。</summary>
    public static void DrawDumpBars(Canvas c, List<MiningProgramPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 8, y0 = h - 16, x1 = w - 6, y1 = 16;
        double maxV = Math.Max(1, s.Max(p => p.Panels.Sum(q => q.WasteWanM3)));
        AddLine(c, x0, y0, x1, y0, Axis);
        var ext = new SolidColorBrush(Color.FromRgb(0xBA, 0x75, 0x17));
        var inn = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75));
        AddText(c, x0, 0, "■内排 ■外排(占地)", Hint, 8);
        int n = s.Count; double slot = (x1 - x0) / n, bw = Math.Min(42, slot * 0.5);
        for (int i = 0; i < n; i++)
        {
            double externV = s[i].Panels.Where(p => p.Dump == DumpMode.External).Sum(p => p.WasteWanM3);
            double internV = s[i].Panels.Where(p => p.Dump == DumpMode.Internal).Sum(p => p.WasteWanM3);
            double cx = x0 + (i + 0.5) * slot - bw / 2;
            double he = externV / maxV * (y0 - y1), hi = internV / maxV * (y0 - y1);
            AddRect(c, cx, y0 - he, bw, he, ext);
            AddRect(c, cx, y0 - he - hi, bw, hi, inn);
            AddText(c, cx, y0 + 2, Short(s[i].Name), Hint, 8);
        }
    }

    /// <summary>④ 采区接续甘特：每方案一行，采区按服务年限沿时间排（外排灰/内排teal）。</summary>
    public static void DrawGantt(Canvas c, List<MiningProgramPlan> s)
    {
        if (Empty(c, s)) return;
        var (w, h) = Sz(c);
        double x0 = 40, y0 = h - 14, x1 = w - 6, y1 = 6;
        double maxT = Math.Max(1, s.Max(p => p.Panels.Sum(q => q.ServiceLifeYears)));
        AddLine(c, x0, y0, x1, y0, Axis);
        AddText(c, x1 - 12, y0 + 1, "a", Hint);
        double SX(double t) => x0 + t / maxT * (x1 - x0);
        int n = s.Count; double rowH = (y0 - y1) / Math.Max(1, n);
        var ext = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        var inn = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75));
        for (int i = 0; i < n; i++)
        {
            double ry = y1 + i * rowH + 2, rh = Math.Max(4, rowH - 4);
            AddText(c, 2, ry, Short(s[i].Name), Hint, 8);
            double t = 0;
            foreach (var p in s[i].Panels.OrderBy(z => z.Order))
            {
                double xa = SX(t), xb = SX(t + p.ServiceLifeYears);
                AddRect(c, xa, ry, Math.Max(1, xb - xa - 1), rh, p.Dump == DumpMode.Internal ? inn : ext);
                t += p.ServiceLifeYears;
            }
        }
    }

    private static double NH(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NL(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}
