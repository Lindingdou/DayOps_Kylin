// 忠实移植自原 PitMine3D Modules/TaskLib/ShiftOps/ShiftChainStrip.cs（逐行对应；WPF FrameworkElement.OnRender → Avalonia Control.Render）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.ShiftOps;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 班内工序链条带：一个班里每条工艺线 / 每道工序在时间上怎么排开，当前时刻一条竖线扫过去。
///
/// <para><b>为什么是自绘而不是拿列表凑</b>：这一层要回答的是"此刻谁在干、下一个接谁"，
/// 那是**时间轴上的相对位置**，列表答不了。</para>
///
/// <para><b>与月度甘特的区别</b>：横轴是**一个班的 8 小时**，不是一个月的 30 天；
/// 行是工艺线（铲 + 车队 + 去向）而不是采掘单元；块的颜色是工序不是物料。</para>
/// </summary>
public sealed class ShiftChainStrip : Control
{
    // ── 版式（与 StageGantt 那套刻意不同：这是班内尺度）──
    public const double LabelW = 168;    // 左侧行名列宽
    public const double HeaderH = 26;    // 顶部时刻刻度
    public const double RowH = 26;       // 一行高
    public const double RowGap = 3;
    public const double BarH = 16;       // 工序块高
    public const double PadX = 10;
    public const double PadBottom = 8;

    private ShiftProcessSystem? _sys;
    private double _clock = double.NaN;

    /// <summary>行命中回调：点了哪一行（行名）。</summary>
    public Action<string>? RowClicked;

    private readonly List<(Rect R, string Key, string Tip)> _hit = new();

    public void SetSystem(ShiftProcessSystem? sys, double clockHour)
    {
        _sys = sys;
        _clock = clockHour;
        InvalidateVisual();
        InvalidateMeasure();
    }

    public void SetClock(double clockHour)
    {
        _clock = clockHour;
        InvalidateVisual();
    }

    /// <summary>行数（工艺线 + 非量型工序各占一行；同一台设备的连续工序合并到一行）。</summary>
    public int RowCount => Rows().Count;

    public double DesiredHeight => HeaderH + RowCount * (RowH + RowGap) + PadBottom;

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width, DesiredHeight);

    // ── 行模型 ───────────────────────────────────────────────────────────────

    private sealed class Row
    {
        public string Key = "";
        public string Name = "";
        public string Sub = "";
        public List<(double Lo, double Hi, ChainStage Stage, bool Idle, string Text, string Tip)> Blocks = new();
    }

    private List<Row> Rows()
    {
        var rows = new List<Row>();
        if (_sys == null) return rows;

        // ① 工艺线：一条线一行（铲 + 车队 + 去向）
        foreach (var l in _sys.Lines.OrderBy(l => l.StartHour))
        {
            var r = new Row
            {
                Key = $"line:{l.Zone}:{l.Shovel}",
                Name = l.Zone.Length > 0 ? l.Zone : "(未记面)",
                Sub = $"{l.Shovel}＋{l.Trucks.Count}车 → {(l.Destination.Length > 0 ? l.Destination : "去向未定")}",
            };
            r.Blocks.Add((l.StartHour, l.EndHour, ChainStage.Load, false,
                $"采装 {l.TargetM3 / 1e4:0.##}万m³",
                $"{l.Zone}\n{l.Shovel}　配 {l.Trucks.Count} 车（荐 {l.RecommendedTrucks}）\n"
              + $"班产 {l.GroupCapacityM3PerH:0} m³/h　运距 {l.HaulKm:0.##} km\n"
              + $"周转 {l.CycleH * 60:0.#} min　在途 {l.TrucksInTransit:0.##} 台\n{l.BottleneckWhy}"));
            rows.Add(r);
        }

        // ② 非量型工序：按设备归行（一台设备一天里的几段接起来看才有意义）
        foreach (var g in _sys.Steps.GroupBy(s => s.Equipment.Length > 0 ? s.Equipment : s.Zone)
                                    .OrderBy(g => g.Min(s => s.StartHour)))
        {
            var r = new Row
            {
                Key = $"step:{g.Key}",
                Name = g.Key,
                Sub = string.Join("、", g.Select(s => s.Stage.Label()).Distinct()),
            };
            foreach (var s in g.OrderBy(s => s.StartHour))
                r.Blocks.Add((s.StartHour, s.EndHour, s.Stage, s.IsIdle,
                    s.IsIdle ? (s.IdleWhy.Length > 0 ? s.IdleWhy : "空闲") : s.Stage.Label(),
                    $"{s.Zone}\n{s.Stage.Label()}　{Hm(s.StartHour)}–{Hm(s.EndHour)}"
                  + (s.IdleWhy.Length > 0 ? $"\n原因：{s.IdleWhy}" : "")));
            rows.Add(r);
        }
        return rows;
    }

    // ── 绘制 ─────────────────────────────────────────────────────────────────

    private static readonly IBrush GridPen = new SolidColorBrush(Color.FromArgb(0x33, 0x94, 0xA3, 0xB8));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly IBrush TextMain = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly IBrush ClockBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    internal static Color StageColor(ChainStage s) => s switch
    {
        ChainStage.Drill => Color.FromRgb(0x8B, 0x5C, 0xF6),
        ChainStage.Blast => Color.FromRgb(0xEF, 0x44, 0x44),
        ChainStage.Load => Color.FromRgb(0x3B, 0x82, 0xF6),
        ChainStage.Haul => Color.FromRgb(0x10, 0xB9, 0x81),
        _ => Color.FromRgb(0xF5, 0x9E, 0x0B),
    };

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        _hit.Clear();
        // 透明底当命中区（自绘控件没有可命中的面积）
        dc.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        double w = Bounds.Width > 1 ? Bounds.Width : 900;
        if (_sys == null || _sys.DurationH < 1e-9)
        {
            dc.DrawText(Text("尚无本班工序数据", 12, TextDim), new Point(PadX, HeaderH / 2));
            return;
        }

        double x0 = LabelW, x1 = w - PadX;
        double span = Math.Max(1e-9, _sys.DurationH);
        double PxOf(double hour) => x0 + (hour - _sys.StartHour) / span * (x1 - x0);

        // ── 顶部刻度：每半小时一个细格，整点标数 ──
        var pen = new Pen(GridPen, 1);
        for (double h = Math.Ceiling(_sys.StartHour * 2) / 2; h <= _sys.EndHour + 1e-9; h += 0.5)
        {
            double px = PxOf(h);
            bool whole = Math.Abs(h - Math.Round(h)) < 1e-6;
            dc.DrawLine(pen, new Point(px, whole ? HeaderH - 8 : HeaderH - 4), new Point(px, DesiredHeight - PadBottom));
            if (whole)
            {
                var ft = Text(Hm(h), 10.5, TextDim);
                dc.DrawText(ft, new Point(px - ft.Width / 2, 2));
            }
        }

        // ── 行 ──
        var rows = Rows();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double top = HeaderH + i * (RowH + RowGap);
            double mid = top + RowH / 2;

            var nm = Text(r.Name, 11.5, TextMain);
            dc.DrawText(nm, new Point(PadX, mid - nm.Height / 2 - 5));
            var sub = Text(r.Sub, 9.5, TextDim);
            dc.DrawText(sub, new Point(PadX, mid - sub.Height / 2 + 6));

            foreach (var b in r.Blocks)
            {
                double bx0 = PxOf(Math.Max(b.Lo, _sys.StartHour));
                double bx1 = PxOf(Math.Min(b.Hi, _sys.EndHour));
                if (bx1 - bx0 < 1) bx1 = bx0 + 1;
                var rect = new Rect(bx0, mid - BarH / 2, bx1 - bx0, BarH);

                var c = StageColor(b.Stage);
                // 空闲画成描边空心：一眼与"在干"分开，而不是靠颜色深浅
                if (b.Idle)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)),
                                     new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x94, 0xA3, 0xB8)), 1,
                                             new DashStyle(new double[] { 3, 2 }, 0)), rect);
                }
                else
                {
                    dc.DrawRectangle(new SolidColorBrush(c), null, rect);
                }

                if (rect.Width > 46)
                {
                    var ft = Text(b.Text, 10, b.Idle ? TextDim : Brushes.White);
                    if (ft.Width < rect.Width - 6)
                        dc.DrawText(ft, new Point(rect.X + 4, rect.Y + (BarH - ft.Height) / 2));
                }
                _hit.Add((rect, r.Key, b.Tip));
            }
        }

        // ── 当前时刻竖线（画在最上层）──
        if (!double.IsNaN(_clock) && _clock >= _sys.StartHour && _clock <= _sys.EndHour)
        {
            double cx = PxOf(_clock);
            dc.DrawLine(new Pen(ClockBrush, 1.6), new Point(cx, HeaderH - 10), new Point(cx, DesiredHeight - PadBottom));
            var ft = Text(Hm(_clock), 10.5, ClockBrush);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x16, 0x26)), null,
                             new Rect(cx - ft.Width / 2 - 3, 1, ft.Width + 6, ft.Height + 2));
            dc.DrawText(ft, new Point(cx - ft.Width / 2, 2));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = _hit.FirstOrDefault(x => x.R.Contains(p));
        ToolTip.SetTip(this, h.Tip is { Length: > 0 } ? h.Tip : null);
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var p = e.GetPosition(this);
            var h = _hit.FirstOrDefault(x => x.R.Contains(p));
            if (h.Key is { Length: > 0 }) RowClicked?.Invoke(h.Key);
        }
        base.OnPointerPressed(e);
    }

    private static FormattedText Text(string s, double size, IBrush b)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               new Typeface("Microsoft YaHei UI"), size, b);

    internal static string Hm(double h)
    {
        if (double.IsNaN(h)) return "—";
        int hh = (int)Math.Floor(h), mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh % 24:00}:{mm:00}";
    }
}
