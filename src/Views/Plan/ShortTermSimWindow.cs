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
/// 「短期进度计划动态模拟」—— §三四七 年轨窗口的月度孪生：
/// 一帧 = 一月，逐月量与剥采比、设备利用率、检修/峰月，逐月扣各去向库容。
///
/// 口径在 <see cref="ShortTermSimTimeline"/>（与年轨同源，见那里的注释）。
///
/// ── 与原版的范围差异（登记）──
/// 原版这个按钮开的是 <c>DynamicSimWindow</c>，除量以外还有**车流 / 设备图标 / 路线标注 / 班次**
/// 那一整层（整套 <c>Sim*Stage</c> 管线）。本轮只移**量与库容**这一支，其余另计，窗口里如实写明。
/// </summary>
internal sealed class ShortTermSimWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static IBrush F(string hex) => Brush.Parse(hex);

    private static readonly IBrush BarCoal = F("#FF3B82F6");
    private static readonly IBrush BarCoalDim = F("#803B82F6");
    private static readonly IBrush BarCursor = F("#FFFBBF24");
    private static readonly IBrush RatioLine = F("#FFF97316");
    private static readonly IBrush UtilLine = F("#FF22D3EE");
    private static readonly IBrush WarnLine = F("#FFE23B3B");
    private static readonly IBrush InnerMark = F("#FF1D9E75");
    private static readonly IBrush AxisBrush = F("#FF475569");
    private static readonly IBrush TextBrush = F("#FFCBD5E1");
    private static readonly IBrush TextDim = F("#FF94A3B8");
    private static readonly IBrush MaintBg = F("#22F59E0B");
    private static readonly IBrush PeakBg = F("#1E22C55E");
    private const double PadL = 48, PadR = 48, PadT = 24, PadB = 28;

    private readonly Func<List<ShortTermPlan>> _plans;
    private readonly Func<SinkRegistry?> _sinks;
    private readonly Action<string> _echo;

    private readonly ComboBox _cmbScheme = new() { MinWidth = 200 };
    private readonly Canvas _canvas = new() { Background = F("#FF0F172A"), MinHeight = 240 };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _head = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = F("#555") };
    private readonly TextBlock _readout = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = F("#333") };
    private readonly Button _btnPlay;

    private ShortTermPlan? _plan;
    private List<ShortTermSimFrame> _frames = new();
    private DispatcherTimer? _timer;
    private int _cursor;

    internal ShortTermSimWindow(Func<List<ShortTermPlan>> plans, Func<SinkRegistry?> sinks, Action<string> echo)
    {
        _plans = plans; _sinks = sinks; _echo = echo;
        Title = "短期进度计划动态模拟";
        Width = 1000; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _btnPlay = new Button { Content = "▶ 播放", Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
        _btnPlay.Click += (_, _) => TogglePlay();

        Content = BuildLayout();
        _canvas.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Draw(); };
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            int v = (int)Math.Round(_slider.Value);
            if (v == _cursor) return;
            _cursor = v; Draw();
        };
        _cmbScheme.SelectionChanged += (_, _) => Rebuild();
        Closed += (_, _) => Stop();
        Reload();
    }

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
                        B("⏮ 首月", () => Seek(0)),
                        B("◀", () => Seek(_cursor - 1)),
                        B("▶", () => Seek(_cursor + 1)),
                        B("⏭ 末月", () => Seek(_frames.Count - 1)),
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
            Background = F("#F3F3F3"), Padding = new Thickness(12, 6, 12, 10),
            BorderBrush = F("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("关闭", Close) },
            },
        };

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

    internal void Reload()
    {
        var all = _plans() ?? new List<ShortTermPlan>();
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
        var all = _plans() ?? new List<ShortTermPlan>();
        _plan = all.FirstOrDefault(p => string.Equals(p.Name, _cmbScheme.SelectedItem as string, StringComparison.Ordinal))
                ?? all.FirstOrDefault();
        _frames = ShortTermSimTimeline.Build(_plan, _sinks());
        _cursor = 0;
        _slider.Maximum = Math.Max(0, _frames.Count - 1);
        _slider.Value = 0;
        _head.Text = _frames.Count == 0
            ? "还没有已排产的月度方案 —— 先跑「短期生产计划 [年煤目标] [基准剥采比]」，再回来推演。"
            : ShortTermSimTimeline.Summary(_plan, _frames)
              + "　|　本窗只演【月粒度量与库容】；车流·设备图标·路线标注·班次那一层另计。";
        Draw();
    }

    private void TogglePlay()
    {
        if (_timer != null) { Stop(); return; }
        if (_frames.Count == 0) return;
        if (_cursor >= _frames.Count - 1) _cursor = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => { if (_cursor >= _frames.Count - 1) Stop(); else Seek(_cursor + 1); };
        _timer.Start();
        _btnPlay.Content = "⏸ 暂停";
    }

    private void Stop()
    {
        _timer?.Stop(); _timer = null;
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
        if (_frames.Count == 0 || _plan == null)
        { AddText(_canvas, 12, h / 2 - 8, "（排产后显示）", TextDim, 11); _readout.Text = ""; return; }

        double x0 = PadL, x1 = w - PadR, y0 = h - PadB, y1 = PadT;
        int n = _frames.Count;
        double ceilCoal = _plan.MonthlyCoalCeilingWanT;
        double maxV = Math.Max(Math.Max(ceilCoal, _frames.Max(f => f.CoalWanT)), 1e-9) * 1.15;
        // 右轴同时画剥采比与设备利用率(%)：两者量纲不同，按各自最大归一后共用一根轴，
        // 故右轴只标"比 / %"，不标具体刻度 —— 标了会让人以为两条线读同一把尺子。
        double maxSr = Math.Max(Math.Max(_plan.RatioCeiling, _frames.Max(f => f.Ratio)), 1e-9) * 1.15;
        double slot = (x1 - x0) / n;

        for (int k = 0; k < n; k++)   // 检修月 / 峰月底色
        {
            if (_frames[k].IsMaintenance) AddRect(_canvas, x0 + k * slot, y1, slot, y0 - y1, MaintBg);
            else if (_frames[k].IsPeak) AddRect(_canvas, x0 + k * slot, y1, slot, y0 - y1, PeakBg);
        }

        AddLine(_canvas, x0, y0, x1, y0, AxisBrush);
        AddLine(_canvas, x0, y1, x0, y0, AxisBrush);
        AddLine(_canvas, x1, y1, x1, y0, AxisBrush);
        AddText(_canvas, 4, y1 - 4, "万t", TextDim);
        AddText(_canvas, x1 - 34, y1 - 4, "比 / %", TextDim);

        double BX(int i) => x0 + (i + 0.5) * slot;
        double LY(double v) => y0 - v / maxV * (y0 - y1);
        double RY(double v) => y0 - v / maxSr * (y0 - y1);
        double UY(double pct) => y0 - Math.Clamp(pct, 0, 100) / 100.0 * (y0 - y1);

        for (int k = 0; k < n; k++)
        {
            double bw = Math.Max(2, slot * 0.6), bx = x0 + k * slot + (slot - bw) / 2;
            AddRect(_canvas, bx, LY(_frames[k].CoalWanT), bw, y0 - LY(_frames[k].CoalWanT),
                    k == _cursor ? BarCursor : k < _cursor ? BarCoal : BarCoalDim);
            if (_frames[k].InnerDump)   // 内排月在柱底打一小截青条
                AddRect(_canvas, bx, y0 - 3, bw, 3, InnerMark);
        }

        if (ceilCoal > 0)
        {
            AddLine(_canvas, x0, LY(ceilCoal), x1, LY(ceilCoal), WarnLine, 1, new double[] { 4, 3 });
            AddText(_canvas, x0 + 3, LY(ceilCoal) - 13, $"月产上限 {ceilCoal.ToString("0", Inv)} 万t", WarnLine);
        }
        if (_plan.RatioCeiling > 0)
            AddLine(_canvas, x0, RY(_plan.RatioCeiling), x1, RY(_plan.RatioCeiling), WarnLine, 1, new double[] { 2, 3 });

        var sr = new List<Point>();
        var ut = new List<Point>();
        for (int k = 0; k < n; k++)
        {
            if (_frames[k].CoalWanT > 0) sr.Add(new Point(BX(k), RY(_frames[k].Ratio)));
            ut.Add(new Point(BX(k), UY(_frames[k].EquipUtilPct)));
        }
        if (sr.Count > 0) _canvas.Children.Add(new Polyline { Points = sr, Stroke = RatioLine, StrokeThickness = 2 });
        if (ut.Count > 0) _canvas.Children.Add(new Polyline { Points = ut, Stroke = UtilLine, StrokeThickness = 1.4, StrokeDashArray = new AvaloniaList<double> { 3, 2 } });

        int spill = ShortTermSimTimeline.FirstSpillIndex(_frames);
        if (spill >= 0)
        {
            AddLine(_canvas, x0 + spill * slot, y1, x0 + spill * slot, y0, WarnLine, 1.4);
            AddText(_canvas, x0 + spill * slot + 3, y1 + 14, "排不下", WarnLine);
        }

        double cx = BX(_cursor);
        AddLine(_canvas, cx, y1, cx, y0, BarCursor, 1);
        int step = Math.Max(1, n / 12);
        for (int k = 0; k < n; k += step) AddText(_canvas, x0 + k * slot, y0 + 6, _frames[k].Label, TextDim, 8);

        var f0 = _frames[_cursor];
        AddText(_canvas, x0 + 3, 2,
            $"{f0.Label}　采出 {f0.CoalWanT:0.#} 万t　剥离 {f0.StripWanM3:0.#} 万m³　剥采比 {f0.Ratio:0.##}　"
          + $"作业日 {f0.Workdays:0.#}　设备利用 {f0.EquipUtilPct:0.#}%　完成 {f0.CompletionPct:0.#}%", TextBrush, 10);
        AddText(_canvas, x1 - 148, 2, "■产量　—剥采比　┄设备利用率", TextDim, 8);

        var remain = f0.RemainWanM3.Count == 0
            ? "（未接去向台账，未做库容校核）"
            : string.Join("　", f0.RemainWanM3.Select(kv => $"{SinkName(kv.Key)} 余 {kv.Value:N0} 万m³"));
        _readout.Text = $"第 {_cursor + 1}/{n} 月　排弃占容 {f0.DumpWanM3:0.#} 万m³　推进 {f0.AdvanceM:0.#} m"
                      + (string.IsNullOrWhiteSpace(f0.ActiveFace) ? "" : $"　主面 {f0.ActiveFace}")
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
    internal void SetCursor(int i) => Seek(i);
}
