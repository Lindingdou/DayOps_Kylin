using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PitMine3D.Kylin.Controls.Charts;

public sealed class PieSlice
{
    public string Label = "";
    public double Value;
    public Color? Color;
}

/// <summary>自绘饼/环图（托管替代原 LiveCharts2 PieChart），右侧图例含占比，悬停高亮。</summary>
public sealed class PieChartView : Control
{
    public List<PieSlice> Slices { get; set; } = new();
    public bool Donut { get; set; }
    public bool ShowLegend { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public double FontSize { get; set; } = 11;
    public string EmptyText { get; set; } = "暂无数据";
    public string ValueFormat { get; set; } = "0.##";

    private Point? _hover;
    private readonly List<(double a0, double a1, int i)> _arcs = new();
    private Point _center; private double _radius;

    public PieChartView() { ClipToBounds = true; MinHeight = 60; }
    public void Refresh() => InvalidateVisual();

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); _hover = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = null; InvalidateVisual(); }

    private int HoverIndex()
    {
        if (_hover == null) return -1;
        double dx = _hover.Value.X - _center.X, dy = _hover.Value.Y - _center.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d > _radius || (Donut && d < _radius * 0.55)) return -1;
        double a = Math.Atan2(dy, dx); if (a < -Math.PI / 2) a += 2 * Math.PI;
        foreach (var arc in _arcs) if (a >= arc.a0 && a < arc.a1) return arc.i;
        return -1;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        _arcs.Clear();
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, W, H));
        var data = Slices.Where(s => s.Value > 0).ToList();
        double total = data.Sum(s => s.Value);
        var textBrush = new SolidColorBrush(Color.Parse("#374151"));
        if (data.Count == 0 || total <= 0)
        {
            var ft = new FormattedText(EmptyText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 13, textBrush);
            ctx.DrawText(ft, new Point((W - ft.Width) / 2, (H - ft.Height) / 2));
            return;
        }
        double legendW = 0;
        if (ShowLegend)
        {
            legendW = data.Max(s => Measure($"{s.Label} {s.Value / total * 100:0.#}%").Width) + 28;
            legendW = Math.Min(legendW, W * 0.5);
        }
        double pw = W - legendW;
        _radius = Math.Max(10, Math.Min(pw, H) / 2 - 14);
        _center = new Point(pw / 2, H / 2);
        int hov = HoverIndex();
        double a = -Math.PI / 2;
        for (int i = 0; i < data.Count; i++)
        {
            var s = data[i];
            double sweep = s.Value / total * 2 * Math.PI;
            var col = s.Color ?? ChartView.PaletteAt(i);
            double r = _radius + (i == hov ? 6 : 0);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                var p0 = new Point(_center.X + r * Math.Cos(a), _center.Y + r * Math.Sin(a));
                var p1 = new Point(_center.X + r * Math.Cos(a + sweep), _center.Y + r * Math.Sin(a + sweep));
                if (Donut)
                {
                    double ri = r * 0.55;
                    var q0 = new Point(_center.X + ri * Math.Cos(a), _center.Y + ri * Math.Sin(a));
                    var q1 = new Point(_center.X + ri * Math.Cos(a + sweep), _center.Y + ri * Math.Sin(a + sweep));
                    g.BeginFigure(p0, true);
                    g.ArcTo(p1, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise);
                    g.LineTo(q1);
                    g.ArcTo(q0, new Size(ri, ri), 0, sweep > Math.PI, SweepDirection.CounterClockwise);
                    g.EndFigure(true);
                }
                else
                {
                    g.BeginFigure(_center, true);
                    g.LineTo(p0);
                    g.ArcTo(p1, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise);
                    g.EndFigure(true);
                }
            }
            ctx.DrawGeometry(new SolidColorBrush(col), new Pen(Brushes.White, 1.5), geo);
            _arcs.Add((a, a + sweep, i));
            if (ShowLabels && sweep > 0.25)
            {
                double mid = a + sweep / 2, lr = Donut ? r * 0.78 : r * 0.62;
                var ft = new FormattedText($"{s.Value / total * 100:0.#}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), FontSize, Brushes.White);
                ctx.DrawText(ft, new Point(_center.X + lr * Math.Cos(mid) - ft.Width / 2, _center.Y + lr * Math.Sin(mid) - ft.Height / 2));
            }
            a += sweep;
        }
        if (ShowLegend)
        {
            double ly = Math.Max(4, H / 2 - data.Count * (FontSize + 8) / 2);
            for (int i = 0; i < data.Count; i++)
            {
                var s = data[i];
                double lx = pw + 6;
                ctx.FillRectangle(new SolidColorBrush(s.Color ?? ChartView.PaletteAt(i)), new Rect(lx, ly + 3, 12, FontSize - 1));
                var ft = new FormattedText($"{s.Label} {s.Value / total * 100:0.#}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, i == hov ? FontWeight.Bold : FontWeight.Normal), FontSize, textBrush);
                ctx.DrawText(ft, new Point(lx + 16, ly));
                ly += FontSize + 8;
            }
        }
        if (hov >= 0 && _hover != null)
        {
            var s = data[hov];
            var ft = new FormattedText($"{s.Label}: {s.Value.ToString(ValueFormat, CultureInfo.InvariantCulture)} ({s.Value / total * 100:0.#}%)", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), FontSize, Brushes.White);
            double x = Math.Min(W - ft.Width - 12, _hover.Value.X + 12), y = Math.Max(0, _hover.Value.Y - ft.Height - 14);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#DD1F2937")), null, new Rect(x, y, ft.Width + 10, ft.Height + 6), 3, 3);
            ctx.DrawText(ft, new Point(x + 5, y + 3));
        }
    }

    private FormattedText Measure(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), FontSize, Brushes.Black);
}
