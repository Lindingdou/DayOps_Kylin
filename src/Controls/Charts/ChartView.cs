using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PitMine3D.Kylin.Controls.Charts;

/// <summary>系列类型（对应原 LiveCharts2 的 Column/StackedColumn/Line/Area/Scatter/浮动柱/箱线/热力）。</summary>
public enum SeriesKind { Bar, StackedBar, Line, Area, Scatter, FloatingBar, Box, Heatmap, HBar }

public enum LegendPlacement { Hidden, Top, Bottom, Right }

/// <summary>箱线五数（Min/Q1/Med/Q3/Max），Mean 可选。</summary>
public sealed class BoxStats
{
    public double Min, Q1, Median, Q3, Max;
    public double? Mean;
}

/// <summary>一条图表系列。按 Kind 使用 Values(按类目) / Points(数值 X) / Ranges(浮动柱) / Boxes / Matrix。</summary>
public sealed class ChartSeries
{
    public string Name = "";
    public SeriesKind Kind = SeriesKind.Bar;
    public Color Color = Color.Parse("#1976D2");
    /// <summary>按类目索引的值（Bar/StackedBar/Line/Area/HBar）。null = 缺值。</summary>
    public List<double?> Values = new();
    /// <summary>数值 X 的点（Scatter，或 NumericX 模式下的 Line）。</summary>
    public List<(double x, double y)> Points = new();
    /// <summary>浮动柱（瀑布/阶梯）: 每类目 (低, 高)。</summary>
    public List<(double lo, double hi)> Ranges = new();
    /// <summary>箱线: 每类目一组五数。</summary>
    public List<BoxStats?> Boxes = new();
    /// <summary>热力: [row, col]，row 对应 ChartView.YCategories，col 对应 Categories。</summary>
    public double[,]? Matrix;
    public bool SecondaryAxis;
    public double StrokeThickness = 2;
    public bool ShowMarkers = true;
    public bool ShowValueLabels;
    public string ValueFormat = "0.##";
    /// <summary>逐点颜色覆盖（如高亮本机型号）。</summary>
    public List<Color?>? PointColors;
    /// <summary>逐点标签（Scatter 悬停显示）。</summary>
    public List<string>? PointLabels;
    public bool Dashed;
    public double MarkerSize = 5;
    /// <summary>Line: 用平滑折线? 不支持, 保留字段以兼容调用。</summary>
    public double Opacity = 1;
}

/// <summary>
/// 自绘二维图表控件（托管替代原 LiveCharts2 CartesianChart）：分组柱/堆叠柱/横柱/折线/面积/散点/浮动柱/箱线/热力，
/// 双 Y 轴、图例、网格、悬停提示、参考线、点击回调。
/// </summary>
public sealed class ChartView : Control
{
    public List<string> Categories { get; set; } = new();
    public List<string> YCategories { get; set; } = new();     // 热力图行标签
    public List<ChartSeries> Series { get; set; } = new();
    public string? XTitle { get; set; }
    public string? YTitle { get; set; }
    public string? Y2Title { get; set; }
    public LegendPlacement Legend { get; set; } = LegendPlacement.Top;
    public double? YMin { get; set; }
    public double? YMax { get; set; }
    public double? Y2Min { get; set; }
    public double? Y2Max { get; set; }
    public double? XMin { get; set; }
    public double? XMax { get; set; }
    /// <summary>散点/数值 X 折线模式（X 轴为数值而非类目）。</summary>
    public bool NumericX { get; set; }
    public string XFormat { get; set; } = "0.##";
    public string YFormat { get; set; } = "0.##";
    public List<(double y, string label, Color color)> HorizontalLines { get; } = new();
    public List<(double x, string label, Color color)> VerticalLines { get; } = new();
    /// <summary>热力图色带（低→高），默认 蓝→白→红。</summary>
    public Color HeatLow { get; set; } = Color.Parse("#2166AC");
    public Color HeatMid { get; set; } = Color.Parse("#F7F7F7");
    public Color HeatHigh { get; set; } = Color.Parse("#B2182B");
    public bool ShowHeatValues { get; set; } = true;
    public double FontSize { get; set; } = 11;
    public string EmptyText { get; set; } = "暂无数据";

    /// <summary>点击某个数据点（系列索引, 点索引）。</summary>
    public event Action<int, int>? PointClicked;

    private Point? _hover;
    private Rect _plot;
    private readonly List<(Rect r, int s, int i, string tip)> _hits = new();

    public ChartView()
    {
        ClipToBounds = true;
        MinHeight = 60;
    }

    public void Refresh() => InvalidateVisual();

    // ─── 输入 ──────────────────────────────────────────────────────────
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var h = HitAt(p);
        if (h != null) PointClicked?.Invoke(h.Value.s, h.Value.i);
    }

    private (int s, int i, string tip)? HitAt(Point p)
    {
        double best = 14 * 14; (int s, int i, string tip)? res = null;
        foreach (var h in _hits)
        {
            if (h.r.Width > 2 && h.r.Height > 2 && h.r.Contains(p)) return (h.s, h.i, h.tip);
            var c = h.r.Center; double d = (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y);
            if (d < best) { best = d; res = (h.s, h.i, h.tip); }
        }
        return res;
    }

    // ─── 绘制 ──────────────────────────────────────────────────────────
    private static readonly Typeface Tf = new(FontFamily.Default);
    private FormattedText Ft(string s, double size, IBrush brush, FontWeight w = FontWeight.Normal)
        => new(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, w), size, brush);

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#374151"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#E5E7EB")), 1);
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.Parse("#9CA3AF")), 1);

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        _hits.Clear();
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, W, H));
        if (W < 20 || H < 20) return;
        var visible = Series.Where(s => s != null).ToList();
        bool any = visible.Any(s => s.Values.Any(v => v.HasValue) || s.Points.Count > 0 || s.Ranges.Count > 0 || s.Boxes.Any(b => b != null) || s.Matrix != null);
        if (!any)
        {
            var ft = Ft(EmptyText, 13, MutedBrush);
            ctx.DrawText(ft, new Point((W - ft.Width) / 2, (H - ft.Height) / 2));
            return;
        }

        // 图例
        double top = 6, bottom = 6, left = 6, right = 6;
        var legendItems = visible.Where(s => !string.IsNullOrEmpty(s.Name) && s.Kind != SeriesKind.Heatmap).ToList();
        double legendH = 0, legendW = 0;
        if (Legend != LegendPlacement.Hidden && legendItems.Count > 0)
        {
            if (Legend == LegendPlacement.Right)
            {
                legendW = legendItems.Max(s => Ft(s.Name, FontSize, TextBrush).Width) + 26;
                right += legendW + 4;
            }
            else
            {
                legendH = FontSize + 10;
                if (Legend == LegendPlacement.Top) top += legendH; else bottom += legendH;
            }
        }
        bool heat = visible.Any(s => s.Kind == SeriesKind.Heatmap);
        bool hbar = visible.All(s => s.Kind == SeriesKind.HBar);
        bool hasY2 = visible.Any(s => s.SecondaryAxis);
        if (!string.IsNullOrEmpty(XTitle)) bottom += FontSize + 6;
        if (!string.IsNullOrEmpty(YTitle)) left += FontSize + 4;
        if (!string.IsNullOrEmpty(Y2Title) || hasY2) right += FontSize + 4;

        // 值域
        (double min, double max) = Range(visible.Where(s => !s.SecondaryAxis), YMin, YMax, heat);
        (double min2, double max2) = hasY2 ? Range(visible.Where(s => s.SecondaryAxis), Y2Min, Y2Max, false) : (0, 1);
        double xmin = 0, xmax = 1;
        if (NumericX)
        {
            var xs = visible.SelectMany(s => s.Points.Select(p => p.x)).ToList();
            if (xs.Count > 0) { xmin = xs.Min(); xmax = xs.Max(); }
            if (XMin.HasValue) xmin = XMin.Value; if (XMax.HasValue) xmax = XMax.Value;
            if (xmax - xmin < 1e-12) { xmin -= 1; xmax += 1; }
            double pad = (xmax - xmin) * 0.04; if (!XMin.HasValue) xmin -= pad; if (!XMax.HasValue) xmax += pad;
        }
        var yTicks = heat ? new List<double>() : NiceTicks(min, max, 6);
        var y2Ticks = hasY2 ? NiceTicks(min2, max2, 6) : new List<double>();
        if (!heat && !hbar) { min = Math.Min(min, yTicks.FirstOrDefault(min)); max = Math.Max(max, yTicks.LastOrDefault(max)); }
        if (hasY2) { min2 = Math.Min(min2, y2Ticks.FirstOrDefault(min2)); max2 = Math.Max(max2, y2Ticks.LastOrDefault(max2)); }

        // 轴标签宽度
        double yLabelW = hbar ? (Categories.Count > 0 ? Categories.Max(c => Ft(c, FontSize, TextBrush).Width) : 20)
                              : heat ? (YCategories.Count > 0 ? YCategories.Max(c => Ft(c, FontSize, TextBrush).Width) : 20)
                              : (yTicks.Count > 0 ? yTicks.Max(t => Ft(FmtY(t), FontSize, TextBrush).Width) : 20);
        double y2LabelW = hasY2 && y2Ticks.Count > 0 ? y2Ticks.Max(t => Ft(FmtY(t), FontSize, TextBrush).Width) : 0;
        left += yLabelW + 8; right += y2LabelW + (hasY2 ? 8 : 0);
        double xLabelH = FontSize + 8;
        bool rotate = false;
        if (!NumericX && Categories.Count > 0 && !hbar)
        {
            double plotWGuess = W - left - right;
            double maxLabel = Categories.Max(c => Ft(c, FontSize, TextBrush).Width);
            if (maxLabel > plotWGuess / Math.Max(1, Categories.Count) - 2) { rotate = true; xLabelH = Math.Min(maxLabel, 80) + 8; }
        }
        bottom += xLabelH;
        _plot = new Rect(left, top, Math.Max(10, W - left - right), Math.Max(10, H - top - bottom));

        // 图例绘制
        if (Legend != LegendPlacement.Hidden && legendItems.Count > 0)
        {
            if (Legend == LegendPlacement.Right)
            {
                double ly = _plot.Y + 4;
                foreach (var s in legendItems)
                {
                    double lx = W - legendW - 2;
                    ctx.FillRectangle(new SolidColorBrush(s.Color), new Rect(lx, ly + 2, 12, FontSize - 1));
                    ctx.DrawText(Ft(s.Name, FontSize, TextBrush), new Point(lx + 16, ly));
                    ly += FontSize + 8;
                }
            }
            else
            {
                double totalW = legendItems.Sum(s => Ft(s.Name, FontSize, TextBrush).Width + 26);
                double lx = Math.Max(_plot.X, _plot.X + (_plot.Width - totalW) / 2);
                double ly = Legend == LegendPlacement.Top ? 4 : H - legendH - 2;
                foreach (var s in legendItems)
                {
                    ctx.FillRectangle(new SolidColorBrush(s.Color), new Rect(lx, ly + 3, 12, FontSize - 1));
                    var ft = Ft(s.Name, FontSize, TextBrush);
                    ctx.DrawText(ft, new Point(lx + 16, ly));
                    lx += ft.Width + 26;
                }
            }
        }
        if (!string.IsNullOrEmpty(YTitle))
        {
            var ft = Ft(YTitle!, FontSize, MutedBrush);
            using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(4 + FontSize, _plot.Y + _plot.Height / 2 + ft.Width / 2)))
                ctx.DrawText(ft, new Point(0, -FontSize));
        }
        if (!string.IsNullOrEmpty(Y2Title))
        {
            var ft = Ft(Y2Title!, FontSize, MutedBrush);
            using (ctx.PushTransform(Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(W - 4 - FontSize, _plot.Y + _plot.Height / 2 - ft.Width / 2)))
                ctx.DrawText(ft, new Point(0, -FontSize));
        }
        if (!string.IsNullOrEmpty(XTitle))
        {
            var ft = Ft(XTitle!, FontSize, MutedBrush);
            ctx.DrawText(ft, new Point(_plot.X + (_plot.Width - ft.Width) / 2, H - ft.Height - 2 - (Legend == LegendPlacement.Bottom ? legendH : 0)));
        }

        if (heat) { RenderHeat(ctx, visible.First(s => s.Kind == SeriesKind.Heatmap)); DrawHover(ctx); return; }
        if (hbar) { RenderHBar(ctx, visible, min, max); DrawHover(ctx); return; }

        // 网格 + Y 轴
        foreach (var t in yTicks)
        {
            double y = MapY(t, min, max);
            ctx.DrawLine(GridPen, new Point(_plot.X, y), new Point(_plot.Right, y));
            var ft = Ft(FmtY(t), FontSize, TextBrush);
            ctx.DrawText(ft, new Point(_plot.X - ft.Width - 5, y - ft.Height / 2));
        }
        foreach (var t in y2Ticks)
        {
            double y = MapY(t, min2, max2);
            var ft = Ft(FmtY(t), FontSize, TextBrush);
            ctx.DrawText(ft, new Point(_plot.Right + 5, y - ft.Height / 2));
        }
        ctx.DrawLine(AxisPen, new Point(_plot.X, _plot.Y), new Point(_plot.X, _plot.Bottom));
        ctx.DrawLine(AxisPen, new Point(_plot.X, _plot.Bottom), new Point(_plot.Right, _plot.Bottom));

        // X 轴标签
        int n = Math.Max(1, Categories.Count);
        double slot = _plot.Width / n;
        if (NumericX)
        {
            foreach (var t in NiceTicks(xmin, xmax, 7))
            {
                double x = MapX(t, xmin, xmax);
                if (x < _plot.X - 1 || x > _plot.Right + 1) continue;
                ctx.DrawLine(GridPen, new Point(x, _plot.Y), new Point(x, _plot.Bottom));
                var ft = Ft(t.ToString(XFormat, CultureInfo.InvariantCulture), FontSize, TextBrush);
                ctx.DrawText(ft, new Point(x - ft.Width / 2, _plot.Bottom + 4));
            }
        }
        else
        {
            int step = 1;
            if (!rotate) { double maxW = Categories.Count > 0 ? Categories.Max(c => Ft(c, FontSize, TextBrush).Width) : 0; while (slot * step < maxW + 6 && step < n) step++; }
            for (int i = 0; i < Categories.Count; i++)
            {
                double cx = _plot.X + slot * (i + 0.5);
                if (i % step != 0) continue;
                var ft = Ft(Categories[i], FontSize, TextBrush);
                if (rotate)
                {
                    using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 4) * Matrix.CreateTranslation(cx, _plot.Bottom + 6)))
                        ctx.DrawText(ft, new Point(-ft.Width, 0));
                }
                else ctx.DrawText(ft, new Point(cx - ft.Width / 2, _plot.Bottom + 4));
            }
        }

        // 参考线
        foreach (var hl in HorizontalLines)
        {
            double y = MapY(hl.y, min, max);
            if (y < _plot.Y || y > _plot.Bottom) continue;
            var pen = new Pen(new SolidColorBrush(hl.color), 1.2, new DashStyle(new double[] { 4, 3 }, 0));
            ctx.DrawLine(pen, new Point(_plot.X, y), new Point(_plot.Right, y));
            if (!string.IsNullOrEmpty(hl.label)) ctx.DrawText(Ft(hl.label, FontSize - 1, new SolidColorBrush(hl.color)), new Point(_plot.Right - Ft(hl.label, FontSize - 1, TextBrush).Width - 3, y - FontSize - 2));
        }
        foreach (var vl in VerticalLines)
        {
            double x = NumericX ? MapX(vl.x, xmin, xmax) : _plot.X + slot * (vl.x + 0.5);
            if (x < _plot.X || x > _plot.Right) continue;
            var pen = new Pen(new SolidColorBrush(vl.color), 1.2, new DashStyle(new double[] { 4, 3 }, 0));
            ctx.DrawLine(pen, new Point(x, _plot.Y), new Point(x, _plot.Bottom));
            if (!string.IsNullOrEmpty(vl.label)) ctx.DrawText(Ft(vl.label, FontSize - 1, new SolidColorBrush(vl.color)), new Point(x + 3, _plot.Y + 2));
        }

        using var clip = ctx.PushClip(new Rect(_plot.X - 1, _plot.Y - 8, _plot.Width + 2, _plot.Height + 10));

        // 柱类系列（分组）
        var barSeries = visible.Select((s, idx) => (s, idx)).Where(t => t.s.Kind is SeriesKind.Bar or SeriesKind.FloatingBar or SeriesKind.Box).ToList();
        var stacked = visible.Select((s, idx) => (s, idx)).Where(t => t.s.Kind == SeriesKind.StackedBar).ToList();
        int groups = barSeries.Count + (stacked.Count > 0 ? 1 : 0);
        double groupW = slot * 0.72, barW = groups > 0 ? groupW / groups : groupW;
        int gi = 0;
        if (stacked.Count > 0)
        {
            for (int i = 0; i < n; i++)
            {
                double pos = 0, neg = 0;
                double x0 = _plot.X + slot * i + (slot - groupW) / 2 + barW * gi;
                foreach (var (s, idx) in stacked)
                {
                    double? v = i < s.Values.Count ? s.Values[i] : null;
                    if (!v.HasValue) continue;
                    double lo, hi;
                    if (v.Value >= 0) { lo = pos; hi = pos + v.Value; pos = hi; } else { hi = neg; lo = neg + v.Value; neg = lo; }
                    (double mn, double mx) = s.SecondaryAxis ? (min2, max2) : (min, max);
                    var r = RectY(x0, barW - 1, MapY(lo, mn, mx), MapY(hi, mn, mx));
                    ctx.FillRectangle(Brush(s, i), r);
                    _hits.Add((r, idx, i, $"{Cat(i)} · {s.Name}: {v.Value.ToString(s.ValueFormat, CultureInfo.InvariantCulture)}"));
                }
            }
            gi++;
        }
        foreach (var (s, idx) in barSeries)
        {
            (double mn, double mx) = s.SecondaryAxis ? (min2, max2) : (min, max);
            for (int i = 0; i < n; i++)
            {
                double x0 = _plot.X + slot * i + (slot - groupW) / 2 + barW * gi;
                if (s.Kind == SeriesKind.Bar)
                {
                    double? v = i < s.Values.Count ? s.Values[i] : null;
                    if (!v.HasValue) continue;
                    var r = RectY(x0, barW - 1, MapY(0, mn, mx), MapY(v.Value, mn, mx));
                    ctx.FillRectangle(Brush(s, i), r);
                    if (s.ShowValueLabels)
                    {
                        var ft = Ft(v.Value.ToString(s.ValueFormat, CultureInfo.InvariantCulture), FontSize - 1, TextBrush);
                        ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, v.Value >= 0 ? r.Y - ft.Height - 1 : r.Bottom + 1));
                    }
                    _hits.Add((r, idx, i, $"{Cat(i)} · {s.Name}: {v.Value.ToString(s.ValueFormat, CultureInfo.InvariantCulture)}"));
                }
                else if (s.Kind == SeriesKind.FloatingBar)
                {
                    if (i >= s.Ranges.Count) continue;
                    var (lo, hi) = s.Ranges[i];
                    var r = RectY(x0, barW - 1, MapY(lo, mn, mx), MapY(hi, mn, mx));
                    if (r.Height < 1) r = new Rect(r.X, r.Y - 0.5, r.Width, 1);
                    ctx.FillRectangle(Brush(s, i), r);
                    if (s.ShowValueLabels)
                    {
                        var ft = Ft((hi - lo).ToString(s.ValueFormat, CultureInfo.InvariantCulture), FontSize - 1, TextBrush);
                        ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y - ft.Height - 1));
                    }
                    _hits.Add((r, idx, i, $"{Cat(i)} · {s.Name}: {lo.ToString(s.ValueFormat, CultureInfo.InvariantCulture)} → {hi.ToString(s.ValueFormat, CultureInfo.InvariantCulture)}"));
                }
                else if (s.Kind == SeriesKind.Box)
                {
                    var b = i < s.Boxes.Count ? s.Boxes[i] : null;
                    if (b == null) continue;
                    double cx = x0 + (barW - 1) / 2, bw = Math.Max(4, (barW - 1) * 0.8);
                    var pen = new Pen(new SolidColorBrush(s.Color), 1.4);
                    ctx.DrawLine(pen, new Point(cx, MapY(b.Min, mn, mx)), new Point(cx, MapY(b.Q1, mn, mx)));
                    ctx.DrawLine(pen, new Point(cx, MapY(b.Q3, mn, mx)), new Point(cx, MapY(b.Max, mn, mx)));
                    ctx.DrawLine(pen, new Point(cx - bw / 3, MapY(b.Min, mn, mx)), new Point(cx + bw / 3, MapY(b.Min, mn, mx)));
                    ctx.DrawLine(pen, new Point(cx - bw / 3, MapY(b.Max, mn, mx)), new Point(cx + bw / 3, MapY(b.Max, mn, mx)));
                    var r = RectY(cx - bw / 2, bw, MapY(b.Q1, mn, mx), MapY(b.Q3, mn, mx));
                    ctx.FillRectangle(new SolidColorBrush(s.Color, 0.35), r);
                    ctx.DrawRectangle(pen, r);
                    ctx.DrawLine(new Pen(new SolidColorBrush(s.Color), 2.2), new Point(r.X, MapY(b.Median, mn, mx)), new Point(r.Right, MapY(b.Median, mn, mx)));
                    if (b.Mean.HasValue) ctx.DrawEllipse(Brushes.White, pen, new Point(cx, MapY(b.Mean.Value, mn, mx)), 3, 3);
                    _hits.Add((new Rect(r.X, MapY(b.Max, mn, mx), r.Width, Math.Max(1, MapY(b.Min, mn, mx) - MapY(b.Max, mn, mx))), idx, i,
                        $"{Cat(i)} · {s.Name}: 最小{F(b.Min)} Q1 {F(b.Q1)} 中位{F(b.Median)} Q3 {F(b.Q3)} 最大{F(b.Max)}" + (b.Mean.HasValue ? $" 均{F(b.Mean.Value)}" : "")));
                }
            }
            gi++;
        }

        // 线/面积/散点
        for (int idx = 0; idx < visible.Count; idx++)
        {
            var s = visible[idx];
            if (s.Kind is not (SeriesKind.Line or SeriesKind.Area or SeriesKind.Scatter)) continue;
            (double mn, double mx) = s.SecondaryAxis ? (min2, max2) : (min, max);
            var pts = new List<(Point p, int i, double v, string lbl)>();
            if (NumericX || s.Points.Count > 0)
            {
                for (int i = 0; i < s.Points.Count; i++)
                {
                    var (x, y) = s.Points[i];
                    double px = NumericX ? MapX(x, xmin, xmax) : _plot.X + slot * (x + 0.5);
                    pts.Add((new Point(px, MapY(y, mn, mx)), i, y, $"{s.Name}{(string.IsNullOrEmpty(s.Name) ? "" : " · ")}{(s.PointLabels != null && i < s.PointLabels.Count ? s.PointLabels[i] + " " : "")}({x.ToString(XFormat, CultureInfo.InvariantCulture)}, {y.ToString(s.ValueFormat, CultureInfo.InvariantCulture)})"));
                }
            }
            else
            {
                for (int i = 0; i < s.Values.Count && i < n; i++)
                {
                    if (!s.Values[i].HasValue) continue;
                    double v = s.Values[i]!.Value;
                    pts.Add((new Point(_plot.X + slot * (i + 0.5), MapY(v, mn, mx)), i, v, $"{Cat(i)} · {s.Name}: {v.ToString(s.ValueFormat, CultureInfo.InvariantCulture)}"));
                }
            }
            if (pts.Count == 0) continue;
            var brush = new SolidColorBrush(s.Color, s.Opacity);
            if (s.Kind == SeriesKind.Area && pts.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(pts[0].p.X, MapY(Math.Max(0, mn), mn, mx)), true);
                    foreach (var q in pts) g.LineTo(q.p);
                    g.LineTo(new Point(pts[^1].p.X, MapY(Math.Max(0, mn), mn, mx)));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(s.Color, 0.25 * s.Opacity), null, geo);
            }
            if (s.Kind != SeriesKind.Scatter && pts.Count > 1)
            {
                var pen = new Pen(brush, s.StrokeThickness, s.Dashed ? new DashStyle(new double[] { 4, 3 }, 0) : null, PenLineCap.Round, PenLineJoin.Round);
                var geo = new PolylineGeometry(pts.Select(q => q.p).ToList(), false);
                ctx.DrawGeometry(null, pen, geo);
            }
            foreach (var q in pts)
            {
                double ms = s.MarkerSize;
                if (s.Kind == SeriesKind.Scatter || s.ShowMarkers)
                {
                    var pb = s.PointColors != null && q.i < s.PointColors.Count && s.PointColors[q.i].HasValue ? new SolidColorBrush(s.PointColors[q.i]!.Value, s.Opacity) : brush;
                    ctx.DrawEllipse(pb, s.Kind == SeriesKind.Scatter ? null : new Pen(Brushes.White, 1), q.p, ms / 2 + 0.5, ms / 2 + 0.5);
                }
                if (s.ShowValueLabels)
                {
                    var ft = Ft(q.v.ToString(s.ValueFormat, CultureInfo.InvariantCulture), FontSize - 1, TextBrush);
                    ctx.DrawText(ft, new Point(q.p.X - ft.Width / 2, q.p.Y - ft.Height - 3));
                }
                _hits.Add((new Rect(q.p.X - 4, q.p.Y - 4, 8, 8), idx, q.i, q.lbl));
            }
        }
        clip.Dispose();
        DrawHover(ctx);
    }

    private void RenderHBar(DrawingContext ctx, List<ChartSeries> visible, double min, double max)
    {
        int n = Math.Max(1, Categories.Count);
        double slot = _plot.Height / n;
        foreach (var t in NiceTicks(min, max, 6))
        {
            double x = _plot.X + (t - min) / (max - min) * _plot.Width;
            ctx.DrawLine(GridPen, new Point(x, _plot.Y), new Point(x, _plot.Bottom));
            var ft = Ft(FmtY(t), FontSize, TextBrush);
            ctx.DrawText(ft, new Point(x - ft.Width / 2, _plot.Bottom + 4));
        }
        for (int i = 0; i < Categories.Count; i++)
        {
            var ft = Ft(Categories[i], FontSize, TextBrush);
            ctx.DrawText(ft, new Point(_plot.X - ft.Width - 5, _plot.Y + slot * (i + 0.5) - ft.Height / 2));
        }
        ctx.DrawLine(AxisPen, new Point(_plot.X, _plot.Y), new Point(_plot.X, _plot.Bottom));
        double groupH = slot * 0.7, barH = groupH / Math.Max(1, visible.Count);
        double zeroX = _plot.X + (0 - min) / (max - min) * _plot.Width;
        for (int si = 0; si < visible.Count; si++)
        {
            var s = visible[si];
            for (int i = 0; i < n && i < s.Values.Count; i++)
            {
                if (!s.Values[i].HasValue) continue;
                double v = s.Values[i]!.Value;
                double x1 = _plot.X + (v - min) / (max - min) * _plot.Width;
                double y0 = _plot.Y + slot * i + (slot - groupH) / 2 + barH * si;
                var r = new Rect(Math.Min(zeroX, x1), y0, Math.Abs(x1 - zeroX), Math.Max(1, barH - 1));
                ctx.FillRectangle(Brush(s, i), r);
                if (s.ShowValueLabels)
                {
                    var ft = Ft(v.ToString(s.ValueFormat, CultureInfo.InvariantCulture), FontSize - 1, TextBrush);
                    ctx.DrawText(ft, new Point(r.Right + 3, r.Y + (r.Height - ft.Height) / 2));
                }
                _hits.Add((r, si, i, $"{Cat(i)} · {s.Name}: {v.ToString(s.ValueFormat, CultureInfo.InvariantCulture)}"));
            }
        }
    }

    private void RenderHeat(DrawingContext ctx, ChartSeries s)
    {
        var m = s.Matrix!;
        int rows = m.GetLength(0), cols = m.GetLength(1);
        if (rows == 0 || cols == 0) return;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) { double v = m[r, c]; if (double.IsNaN(v)) continue; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (lo == double.MaxValue) { lo = 0; hi = 1; }
        if (hi - lo < 1e-12) hi = lo + 1;
        double cw = _plot.Width / cols, ch = _plot.Height / rows;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double v = m[r, c];
                var rect = new Rect(_plot.X + c * cw, _plot.Y + r * ch, cw - 1, ch - 1);
                if (double.IsNaN(v)) { ctx.FillRectangle(new SolidColorBrush(Color.Parse("#F3F4F6")), rect); continue; }
                double t = (v - lo) / (hi - lo);
                var col = Lerp3(HeatLow, HeatMid, HeatHigh, t);
                ctx.FillRectangle(new SolidColorBrush(col), rect);
                if (ShowHeatValues && cw > 24 && ch > 12)
                {
                    var ft = Ft(v.ToString(s.ValueFormat, CultureInfo.InvariantCulture), Math.Min(FontSize, ch * 0.6), new SolidColorBrush(t > 0.75 || t < 0.25 ? Colors.White : Color.Parse("#111827")));
                    if (ft.Width < cw) ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
                }
                _hits.Add((rect, 0, r * cols + c, $"{(r < YCategories.Count ? YCategories[r] : r.ToString())} × {Cat(c)}: {v.ToString(s.ValueFormat, CultureInfo.InvariantCulture)}"));
            }
        }
        for (int c = 0; c < cols; c++)
        {
            var ft = Ft(Cat(c), FontSize, TextBrush);
            if (ft.Width < cw + 4) ctx.DrawText(ft, new Point(_plot.X + c * cw + (cw - ft.Width) / 2, _plot.Bottom + 4));
        }
        for (int r = 0; r < rows && r < YCategories.Count; r++)
        {
            var ft = Ft(YCategories[r], FontSize, TextBrush);
            ctx.DrawText(ft, new Point(_plot.X - ft.Width - 5, _plot.Y + r * ch + (ch - ft.Height) / 2));
        }
    }

    private void DrawHover(DrawingContext ctx)
    {
        if (_hover == null) return;
        var h = HitAt(_hover.Value);
        if (h == null) return;
        var ft = Ft(h.Value.tip, FontSize, Brushes.White);
        double x = Math.Min(Bounds.Width - ft.Width - 12, _hover.Value.X + 12), y = Math.Max(0, _hover.Value.Y - ft.Height - 14);
        var r = new Rect(x, y, ft.Width + 10, ft.Height + 6);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#DD1F2937")), null, r, 3, 3);
        ctx.DrawText(ft, new Point(x + 5, y + 3));
    }

    // ─── 工具 ──────────────────────────────────────────────────────────
    private string Cat(int i) => i < Categories.Count ? Categories[i] : i.ToString();
    private string FmtY(double v) => Math.Abs(v) >= 1e6 ? (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M" : v.ToString(YFormat, CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private double MapY(double v, double min, double max) => _plot.Bottom - (v - min) / (max - min) * _plot.Height;
    private double MapX(double v, double min, double max) => _plot.X + (v - min) / (max - min) * _plot.Width;
    private static Rect RectY(double x, double w, double y0, double y1) => new(x, Math.Min(y0, y1), Math.Max(1, w), Math.Max(1, Math.Abs(y1 - y0)));
    private static IBrush Brush(ChartSeries s, int i)
        => new SolidColorBrush(s.PointColors != null && i < s.PointColors.Count && s.PointColors[i].HasValue ? s.PointColors[i]!.Value : s.Color, s.Opacity);

    private static (double, double) Range(IEnumerable<ChartSeries> ss, double? fixMin, double? fixMax, bool heat)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in ss)
        {
            foreach (var v in s.Values) if (v.HasValue) { min = Math.Min(min, v.Value); max = Math.Max(max, v.Value); }
            foreach (var p in s.Points) { min = Math.Min(min, p.y); max = Math.Max(max, p.y); }
            foreach (var r in s.Ranges) { min = Math.Min(min, Math.Min(r.lo, r.hi)); max = Math.Max(max, Math.Max(r.lo, r.hi)); }
            foreach (var b in s.Boxes) if (b != null) { min = Math.Min(min, b.Min); max = Math.Max(max, b.Max); }
            if (s.Kind == SeriesKind.StackedBar)
            {
                for (int i = 0; i < s.Values.Count; i++) { }
            }
        }
        // 堆叠: 按类目求和
        var stacked = ss.Where(s => s.Kind == SeriesKind.StackedBar).ToList();
        if (stacked.Count > 0)
        {
            int n = stacked.Max(s => s.Values.Count);
            for (int i = 0; i < n; i++)
            {
                double pos = 0, neg = 0;
                foreach (var s in stacked) { var v = i < s.Values.Count ? s.Values[i] : null; if (!v.HasValue) continue; if (v.Value >= 0) pos += v.Value; else neg += v.Value; }
                max = Math.Max(max, pos); min = Math.Min(min, neg);
            }
        }
        if (min == double.MaxValue) { min = 0; max = 1; }
        bool bars = ss.Any(s => s.Kind is SeriesKind.Bar or SeriesKind.StackedBar or SeriesKind.HBar or SeriesKind.Area);
        if (bars) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
        if (max - min < 1e-12) { max = min + 1; if (!bars) min -= 1; }
        else if (!bars) { double pad = (max - min) * 0.06; min -= pad; max += pad; }
        if (fixMin.HasValue) min = fixMin.Value;
        if (fixMax.HasValue) max = fixMax.Value;
        if (max <= min) max = min + 1;
        return (min, max);
    }

    public static List<double> NiceTicks(double min, double max, int count)
    {
        var res = new List<double>();
        if (max <= min) return res;
        double raw = (max - min) / Math.Max(1, count);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
        step *= mag;
        double t0 = Math.Ceiling(min / step) * step;
        for (double t = t0; t <= max + step * 1e-6; t += step) res.Add(Math.Round(t, 10));
        return res;
    }

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
    public static Color Lerp3(Color lo, Color mid, Color hi, double t) => t < 0.5 ? Lerp(lo, mid, t * 2) : Lerp(mid, hi, (t - 0.5) * 2);

    /// <summary>常用调色板（与原 LiveCharts 默认序列色接近）。</summary>
    public static readonly Color[] Palette =
    {
        Color.Parse("#1976D2"), Color.Parse("#F57C00"), Color.Parse("#388E3C"), Color.Parse("#C62828"),
        Color.Parse("#7B1FA2"), Color.Parse("#00838F"), Color.Parse("#5D4037"), Color.Parse("#455A64"),
        Color.Parse("#AFB42B"), Color.Parse("#E91E63"),
    };
    public static Color PaletteAt(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>从样本算箱线五数（含均值）。样本为空返回 null。</summary>
    public static BoxStats? BoxOf(IEnumerable<double> values)
    {
        var v = values.Where(d => !double.IsNaN(d)).OrderBy(d => d).ToList();
        if (v.Count == 0) return null;
        double P(double p) { double idx = (v.Count - 1) * p; int lo = (int)Math.Floor(idx); int hi = Math.Min(v.Count - 1, lo + 1); return v[lo] + (v[hi] - v[lo]) * (idx - lo); }
        return new BoxStats { Min = v[0], Q1 = P(0.25), Median = P(0.5), Q3 = P(0.75), Max = v[^1], Mean = v.Average() };
    }
}
