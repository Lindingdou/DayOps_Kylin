using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「中长远规划动态模拟」（移植原 <c>TaskLib.Features.LongTermSimWindow</c> 的年轨那一支）——
/// 一帧 = 一年：逐年量曲线（产量柱 + 达产线 A_p + 生产剥采比折线 + n经报警线 + 时相底色 + 内排起转竖线）
/// 与**同一个游标**联动，逐年扣各去向库容直到排满。
///
/// 口径与推演在 <see cref="LongTermSimTimeline"/>（纯函数）。
/// <b>演的是"推演算出来的那一份"，不是把计划表原样搬过来</b> ——
/// 两者本该一致，不一致正是要看的东西（逐年扣库容之后排不下的量）。
///
/// ── 与原版的范围差异（登记）──
/// 原版这个窗口还带**三维画面**（采场逐年挖除 + 排土场逐年堆填的层体累计堆叠），
/// 那一层依赖整套 <c>Sim*Stage</c> 几何管线（四十来个文件）。本轮只移**时间轴 + 逐年图 + 播放**这一支，
/// 三维推演另计。<b>窗口里如实写明</b>，不假装那部分也在。
/// 车流 / 设备图标 / 路线标注 / 班次那一套本来就不在这里 —— 那是短期的层。
/// </summary>
internal sealed class LongTermSimWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // 配色照原版（深色画布上的读数）
    private static IBrush Frozen(string hex) => Brush.Parse(hex);
    private static IBrush PhaseBg(PlanPhase ph) => ph switch
    {
        PlanPhase.Basic => Frozen("#22F59E0B"),
        PlanPhase.RampUp => Frozen("#1E22C55E"),
        PlanPhase.Stable => Frozen("#14FFFFFF"),
        _ => Frozen("#1E94A3B8"),
    };
    private static readonly IBrush BarCoal = Frozen("#FF3B82F6");
    private static readonly IBrush BarCoalDim = Frozen("#803B82F6");
    private static readonly IBrush BarCursor = Frozen("#FFFBBF24");
    private static readonly IBrush RatioLine = Frozen("#FFF97316");
    private static readonly IBrush CapLine = Frozen("#FF22C55E");
    private static readonly IBrush WarnLine = Frozen("#FFE23B3B");
    private static readonly IBrush InnerMark = Frozen("#FF1D9E75");
    private static readonly IBrush AxisBrush = Frozen("#FF475569");
    private static readonly IBrush TextBrush = Frozen("#FFCBD5E1");
    private static readonly IBrush TextDim = Frozen("#FF94A3B8");
    private const double PadL = 48, PadR = 48, PadT = 24, PadB = 28;

    private readonly Func<List<LongTermPlan>> _plans;
    private readonly Func<SinkRegistry?> _sinks;
    private readonly Action<string> _echo;

    private readonly ComboBox _cmbScheme = new() { MinWidth = 200 };
    private readonly Canvas _canvas = new() { Background = Frozen("#FF0F172A"), MinHeight = 240 };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _head = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Frozen("#555") };
    private readonly TextBlock _readout = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Frozen("#333") };
    private readonly Button _btnPlay;

    private LongTermPlan? _plan;
    private List<LongTermSimFrame> _frames = new();
    private DispatcherTimer? _timer;
    private int _cursor;

    internal LongTermSimWindow(Func<List<LongTermPlan>> plans, Func<SinkRegistry?> sinks, Action<string> echo)
    {
        _plans = plans; _sinks = sinks; _echo = echo;
        Title = "中长远规划动态模拟";
        Width = 1000; Height = 520;   // 高度按屏放得下定(见 §三四五)
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _btnPlay = new Button { Content = "▶ 播放", Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
        _btnPlay.Click += (_, _) => TogglePlay();

        Content = BuildLayout();
        _canvas.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Draw(); };
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase_ValueProperty) return;
            int v = (int)Math.Round(_slider.Value);
            if (v == _cursor) return;
            _cursor = v; Draw();
        };
        _cmbScheme.SelectionChanged += (_, _) => Rebuild();
        Closed += (_, _) => Stop();
        Reload();
    }

    private static readonly AvaloniaProperty RangeBase_ValueProperty = Slider.ValueProperty;

    private Control BuildLayout()
    {
        Button B(string t, Action a)
        {
            var b = new Button { Content = t, Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => a();
            return b;
        }

        var top = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 6),
            Children =
            {
                new WrapPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "方案", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                        _cmbScheme,
                        B("重新载入", Reload),
                        _btnPlay,
                        B("⏮ 首年", () => Seek(0)),
                        B("◀", () => Seek(_cursor - 1)),
                        B("▶", () => Seek(_cursor + 1)),
                        B("⏭ 末年", () => Seek(_frames.Count - 1)),
                    },
                },
                _head,
            },
        };

        var bottom = new StackPanel
        {
            Children =
            {
                new Border { Margin = new Thickness(12, 4, 12, 0), Child = _slider },
                new Border { Padding = new Thickness(12, 2), Child = _readout },
            },
        };
        var footBar = new Border
        {
            Background = Frozen("#F3F3F3"), Padding = new Thickness(12, 6, 12, 10),
            BorderBrush = Frozen("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("关闭", Close) },
            },
        };

        // 页脚与读数各自单独 Dock（见 §三四五）
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(12, 0), Child = _canvas });
        return root;
    }

    // ── 载入 / 推演 ─────────────────────────────────────────────────────────

    internal void Reload()
    {
        var all = _plans() ?? new List<LongTermPlan>();
        string keep = _cmbScheme.SelectedItem as string ?? "";
        var names = all.Select(p => p.Name).ToList();
        _cmbScheme.ItemsSource = names;
        if (names.Contains(keep)) _cmbScheme.SelectedItem = keep;
        else if (names.Count > 0) _cmbScheme.SelectedIndex = 0;
        else { _plan = null; Rebuild(); }
    }

    private void Rebuild()
    {
        Stop();
        var all = _plans() ?? new List<LongTermPlan>();
        _plan = all.FirstOrDefault(p => string.Equals(p.Name, _cmbScheme.SelectedItem as string, StringComparison.Ordinal))
                ?? all.FirstOrDefault();
        _frames = LongTermSimTimeline.Build(_plan, _sinks());
        _cursor = 0;
        _slider.Maximum = Math.Max(0, _frames.Count - 1);
        _slider.Value = 0;

        _head.Text = _frames.Count == 0
            ? "还没有已排产的方案 —— 先跑「中长远进度计划」(加「一键」出多方案)，再回来推演。"
            : LongTermSimTimeline.Summary(_plan, _frames)
              + "　|　本窗只演【年粒度量与库容】；三维逐年挖除/堆填另计，车流·设备·班次那一套在「短期进度计划动态模拟」。";
        Draw();
    }

    // ── 播放 ────────────────────────────────────────────────────────────────

    private void TogglePlay()
    {
        if (_timer != null) { Stop(); return; }
        if (_frames.Count == 0) return;
        if (_cursor >= _frames.Count - 1) _cursor = 0;   // 播完了再点就从头来
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) =>
        {
            if (_cursor >= _frames.Count - 1) { Stop(); return; }
            Seek(_cursor + 1);
        };
        _timer.Start();
        _btnPlay.Content = "⏸ 暂停";
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _btnPlay.Content = "▶ 播放";
    }

    private void Seek(int i)
    {
        if (_frames.Count == 0) return;
        _cursor = Math.Clamp(i, 0, _frames.Count - 1);
        _slider.Value = _cursor;
        Draw();
    }

    // ── 绘制 ────────────────────────────────────────────────────────────────

    private static void AddLine(Canvas c, double x1, double y1, double x2, double y2, IBrush b, double th = 1, double[]? dash = null)
    {
        var l = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th };
        if (dash != null) l.StrokeDashArray = new AvaloniaList<double>(dash);
        c.Children.Add(l);
    }

    private static void AddText(Canvas c, double x, double y, string t, IBrush b, double size = 9)
    {
        var tb = new TextBlock { Text = t, Foreground = b, FontSize = size };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
        c.Children.Add(tb);
    }

    private static void AddRect(Canvas c, double x, double y, double w, double h, IBrush b)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = b };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        double w = _canvas.Bounds.Width > 40 ? _canvas.Bounds.Width : 600;
        double h = _canvas.Bounds.Height > 40 ? _canvas.Bounds.Height : 240;

        // 没有数据画一句话，绝不留空白画布（空白会被当成"图没画出来"）
        if (_frames.Count == 0 || _plan == null)
        { AddText(_canvas, 12, h / 2 - 8, "（排产后显示）", TextDim, 11); _readout.Text = ""; return; }

        double x0 = PadL, x1 = w - PadR, y0 = h - PadB, y1 = PadT;
        int n = _frames.Count;
        double ap = _plan.DesignCapacityWanTa;
        double nEco = _plan.EconomicStripRatioMax;
        double maxV = Math.Max(Math.Max(ap, _frames.Max(f => f.CoalWanT)), 1e-9) * 1.15;
        double maxSr = Math.Max(Math.Max(nEco, _frames.Max(f => f.Ratio)), 1e-9) * 1.15;
        double slot = (x1 - x0) / n;

        for (int k = 0; k < n; k++)   // 时相底色
            AddRect(_canvas, x0 + k * slot, y1, slot, y0 - y1, PhaseBg(_frames[k].Phase));

        AddLine(_canvas, x0, y0, x1, y0, AxisBrush);
        AddLine(_canvas, x0, y1, x0, y0, AxisBrush);
        AddLine(_canvas, x1, y1, x1, y0, AxisBrush);
        AddText(_canvas, 4, y1 - 4, "万t", TextDim);
        AddText(_canvas, x1 - 26, y1 - 4, "m³/t", TextDim);

        double BX(int i) => x0 + (i + 0.5) * slot;
        double LY(double v) => y0 - v / maxV * (y0 - y1);
        double RY(double v) => y0 - v / maxSr * (y0 - y1);

        for (int k = 0; k < n; k++)   // 产量柱：当前年点亮
        {
            double bw = Math.Max(2, slot * 0.6), bx = x0 + k * slot + (slot - bw) / 2;
            AddRect(_canvas, bx, LY(_frames[k].CoalWanT), bw, y0 - LY(_frames[k].CoalWanT),
                    k == _cursor ? BarCursor : k < _cursor ? BarCoal : BarCoalDim);
        }

        AddLine(_canvas, x0, LY(ap), x1, LY(ap), CapLine, 1, new double[] { 4, 3 });
        AddText(_canvas, x0 + 3, LY(ap) - 13, $"达产 A_p={ap.ToString("0", Inv)}", CapLine);
        AddLine(_canvas, x0, RY(nEco), x1, RY(nEco), WarnLine, 1, new double[] { 3, 3 });
        AddText(_canvas, x1 - 60, RY(nEco) - 13, $"n经={nEco.ToString("0", Inv)}", WarnLine);

        var pts = new List<Point>();
        for (int k = 0; k < n; k++) if (_frames[k].CoalWanT > 0) pts.Add(new Point(BX(k), RY(_frames[k].Ratio)));
        if (pts.Count > 0) _canvas.Children.Add(new Polyline { Points = pts, Stroke = RatioLine, StrokeThickness = 2 });

        int inner = LongTermSimTimeline.InnerDumpStartIndex(_frames);
        if (inner >= 0)
        {
            AddLine(_canvas, x0 + inner * slot, y1, x0 + inner * slot, y0, InnerMark, 1.4, new double[] { 5, 3 });
            AddText(_canvas, x0 + inner * slot + 3, y1 + 2, "内排起转", InnerMark);
        }
        int spill = LongTermSimTimeline.FirstSpillIndex(_frames);
        if (spill >= 0)
        {
            AddLine(_canvas, x0 + spill * slot, y1, x0 + spill * slot, y0, WarnLine, 1.4);
            AddText(_canvas, x0 + spill * slot + 3, y1 + 14, "排不下", WarnLine);
        }

        // 游标竖线 + 年标
        double cx = BX(_cursor);
        AddLine(_canvas, cx, y1, cx, y0, BarCursor, 1);
        int step = LongTermChartMath.LabelStep(n);
        for (int k = 0; k < n; k += step)
            AddText(_canvas, x0 + k * slot, y0 + 6, _frames[k].Label, TextDim, 8);

        var f0 = _frames[_cursor];
        AddText(_canvas, x0 + 3, 2,
            $"{f0.Label}　{LongTermChartMath.PhaseLabel(f0.Phase)}　采出 {f0.CoalWanT:0.#} 万t　"
          + $"剥离 {f0.StripWanM3:0.#} 万m³　剥采比 {f0.Ratio:0.##}　达产 {f0.CapacityPct:0.#}%", TextBrush, 10);

        // 读数条：把这一年的话原样列出来，一条不吞
        var remain = f0.RemainWanM3.Count == 0
            ? "（未接去向台账，未做库容校核）"
            : string.Join("　", f0.RemainWanM3.Select(kv => $"{SinkName(kv.Key)} 余 {kv.Value:N0} 万m³"));
        _readout.Text = $"第 {_cursor + 1}/{n} 年　排弃占容 {f0.DumpWanM3:0.#} 万m³　累计采出 {f0.CumCoalWanT:N0} 万t"
                      + $"　{remain}"
                      + (f0.Notes.Count > 0 ? "　◆ " + string.Join("；", f0.Notes) : "");
    }

    private string SinkName(string id)
    {
        var s = _sinks()?.Find(id);
        return s == null ? id : s.Name;
    }

    // ── 自检钩子用 ──
    internal int FrameCount => _frames.Count;
    internal string HeadText => _head.Text ?? "";
    internal string ReadoutText => _readout.Text ?? "";
    internal int CanvasShapes => _canvas.Children.Count;
    internal void SetCursor(int i) => Seek(i);
}
