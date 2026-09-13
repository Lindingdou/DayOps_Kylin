using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 剥采比均衡 — VP 曲线窗口（优化开采设计 按钮⑥；移植原 <c>PlanLib.StrippingBalance.VpCurveWindow</c>）。
/// 左侧逐期录入采出量 P / 剥离量 V（基建年 P=0）+ 分阶段均衡结果表；中间画"累计 P → 累计 V"曲线，标注投产点（曲线离开 Y 轴处，
/// Y=基建剥离）与达产点，按"剥采比逐段递增、落在曲线上方、超前剥离最小"用 DP 拟合 K 段均衡折线（阶段分带）；右侧三块伴图：
/// ① 剥采比/生产时相阶梯 ② 年度物料量双柱 ③ 超前剥离柱；底部六项指标 + 校核。原版 LiveCharts 这里用 Canvas 画（滚轮缩放/拖动平移/双击复位同原）。
/// 「从计划提取」：由宿主给出已确定/已排产的短期(月度)或中长远方案逐期 P/V。
/// </summary>
internal sealed class VpCurveWindow : Window
{
    private readonly VpBalanceSession _s = new();
    private readonly Func<(string src, List<(string Label, double Coal, double Strip)> rows)?> _extract;
    private readonly Action<string, bool> _echo;
    private bool _loading;

    private readonly DataGrid _grid, _stageGrid;
    private readonly TextBox _designBox = PlanUi.Box("1000", 58), _commFracBox = PlanUi.Box("0.33", 44), _ecoBox = PlanUi.Box("", 48), _stageBox = PlanUi.Box("", 44);
    private readonly TextBlock _status = RoadUi.Hint("");
    private readonly Canvas _vp = new() { Background = Brushes.White, ClipToBounds = true }, _ratio = new() { Background = Brushes.White, ClipToBounds = true },
                            _mat = new() { Background = Brushes.White, ClipToBounds = true }, _adv = new() { Background = Brushes.White, ClipToBounds = true };
    private readonly TextBlock _stageCountText = Metric("—", 20, PlanUi.TealBrush), _commissionText = Metric("—", 16, C("#059669")), _capacityText = Metric("—", 16, C("#2563EB")),
                               _avgRatioText = Metric("—", 16, null), _prodRatioText = Metric("—", 16, C("#EA580C")), _advanceText = Metric("—", 20, C("#7C3AED")), _checkText = Metric("—", 16, C("#059669"));
    // VP 主图视窗（缩放/平移写在这里，重算不复位）
    private double _vxMin = 0, _vxMax = double.NaN, _vyMin = 0, _vyMax = double.NaN;
    private Point? _dragStart; private (double, double, double, double) _dragView;

    private static readonly Color CurveColor = Color.Parse("#64748B"), CommColor = Color.Parse("#059669"), CapColor = Color.Parse("#2563EB"),
                                  AxisColor = Color.Parse("#374151"), GridColor = Color.Parse("#E5E7EB"), SubGridColor = Color.Parse("#F1F5F9");
    private static readonly Color[] StagePalette = { Color.Parse("#0EA5A4"), Color.Parse("#EA580C"), Color.Parse("#7C3AED"), Color.Parse("#2563EB"), Color.Parse("#DC2626"), Color.Parse("#CA8A04") };
    private static IBrush C(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static TextBlock Metric(string t, double size, IBrush? fg)
    {
        var tb = new TextBlock { Text = t, FontSize = size, FontWeight = FontWeight.Bold };
        if (fg != null) tb.Foreground = fg; else RoadUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        return tb;
    }

    public VpCurveWindow(Func<(string src, List<(string Label, double Coal, double Strip)> rows)?> extract, Action<string, bool> echo)
    {
        _extract = extract; _echo = echo;
        Title = "剥采比均衡 — VP 曲线";
        PlanUi.Place(this, 1400, 820);

        _grid = PlanUi.EditableTable(new (string, string, double, bool)[]
        {
            ("期号", "Label", 72, false), ("阶段", "Phase", 62, true), ("采出P\n(万t)", "Coal", 0, false), ("剥离V\n(万m³)", "Strip", 0, false),
            ("剥采比\n(m³/t)", "RatioText", 80, true), ("超前\n(万m³)", "DeviationText", 86, true),
        }, double.NaN);
        _grid.CellEditEnded += (_, _) => { if (!_loading) Recompute("已按录入重算。"); };
        _stageGrid = PlanUi.Table(new (string, string, double)[]
        {
            ("阶段", "No", 56), ("起", "FromLabel", 64), ("止", "ToLabel", 64), ("生产剥采比\n(m³/t)", "RatioText", 100), ("持续\n(年)", "Years", 64), ("峰值超前\n(万m³)", "AdvanceText", 100),
        }, multi: false);
        _stageGrid.MaxHeight = 170; _stageGrid.Margin = new Thickness(0);

        Content = BuildShell();
        foreach (var c in new[] { _vp, _ratio, _mat, _adv })
            c.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Redraw(); };
        WireVpInteraction();
        foreach (var tb in new[] { _designBox, _commFracBox, _ecoBox, _stageBox }) tb.TextChanged += (_, _) => OnParamChanged();

        LoadSampleData();
    }

    // ───────────── 布局 ─────────────
    private Control BuildShell()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var header = PlanUi.Header("剥采比均衡 — VP 曲线", "累计采出量 P → 累计剥离量 V ·  投产/达产标注 + 分阶段均衡折线（剥采比逐段递增、超前剥离最小）·  录入或从计划提取逐期物料量");
        Grid.SetRow(header, 0); root.Children.Add(header);

        // 工具条
        var tool = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8) };
        tool.Children.Add(RoadUi.Btn("增加期", () => { var p = _s.AddPeriod(); RefreshGrid(); Redraw(); UpdateMetrics(); SetStatus($"已新增 1 期（{p.Label}）。"); }, 66));
        tool.Children.Add(RoadUi.Btn("删除选中", () =>
        {
            var sel = _grid.SelectedItem as VpPeriod ?? _s.Periods.LastOrDefault();
            if (sel == null) { SetStatus("没有可删除的期。"); return; }
            _s.Periods.Remove(sel); Recompute($"已删除 1 期（{sel.Label}）。");
        }, 76));
        tool.Children.Add(RoadUi.Btn("重置(模拟)", LoadSampleData, 84));
        tool.Children.Add(RoadUi.Sep());
        void Param(string label, TextBox box, string unit, string tip)
        {
            var l = RoadUi.Lbl(label); l.FontWeight = FontWeight.Bold; l.Margin = new Thickness(0, 0, 4, 0); l.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(box, tip);
            var u = RoadUi.Hint(unit); u.VerticalAlignment = VerticalAlignment.Center; u.Margin = new Thickness(3, 0, 12, 0);
            tool.Children.Add(l); tool.Children.Add(box); tool.Children.Add(u);
        }
        Param("设计生产能力:", _designBox, "万t/年", "设计产量 A_p：年采出量首次达到此值即达产点");
        Param("投产系数:", _commFracBox, "×设计", "投产当年产能 A_g / 设计产能 A_p，取 1/3~1/2；大型露天矿取 ~1/3(0.33)");
        Param("经济合理剥采比:", _ecoBox, "m³/t", "各阶段均衡剥采比的上限，超出则报警（留空=不限）");
        Param("均衡期数:", _stageBox, "", "留空 = 自动按曲线形态建议期数");
        var bExtract = RoadUi.Btn("从计划提取", LoadFromEntities, 100);
        ToolTip.SetTip(bExtract, "从已确定的短期(月度)/中长远方案提取逐期采出量 P 与剥离量 V 回填本表；短期方案按物料流聚合（煤流=采出、岩流=剥离）");
        tool.Children.Add(bExtract);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(6, 0, 0, 0);
        tool.Children.Add(_status);
        var toolBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        RoadUi.Theme(toolBorder, Border.BorderBrushProperty, "Theme.Panel.Border");
        Grid.SetRow(toolBorder, 1); root.Children.Add(toolBorder);

        // 主体三栏
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("440,3*,2*") };
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 8, 6, 8) };
        Grid.SetRow(_grid, 0); left.Children.Add(_grid);
        var st = RoadUi.Text("分阶段均衡结果", 13, bold: true); st.Margin = new Thickness(2, 8, 0, 4);
        Grid.SetRow(st, 1); left.Children.Add(st);
        Grid.SetRow(_stageGrid, 2); left.Children.Add(_stageGrid);
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var midDock = new DockPanel { Margin = new Thickness(6, 8, 6, 8) };
        var midHead = new DockPanel();
        var bFit = RoadUi.Btn("复位视图", ResetVpZoom, 70); bFit.Height = 22; bFit.Margin = new Thickness(0);
        ToolTip.SetTip(bFit, "恢复自适应范围（也可在图上双击）；图上滚轮缩放、按住左键拖动平移");
        DockPanel.SetDock(bFit, Avalonia.Controls.Dock.Right); midHead.Children.Add(bFit);
        var mt = RoadUi.Hint("VP 累计曲线 (累计采出 P → 累计剥离 V) · 分带=均衡阶段 · 滚轮缩放/拖动平移"); mt.VerticalAlignment = VerticalAlignment.Center;
        midHead.Children.Add(mt);
        DockPanel.SetDock(midHead, Avalonia.Controls.Dock.Top); midDock.Children.Add(midHead);
        var vpBorder = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0), Child = _vp };
        RoadUi.Theme(vpBorder, Border.BorderBrushProperty, "Theme.Panel.Border");
        midDock.Children.Add(vpBorder);
        Grid.SetColumn(midDock, 1); body.Children.Add(midDock);

        var right = new Grid { RowDefinitions = new RowDefinitions("*,*,*"), Margin = new Thickness(6, 8, 12, 8) };
        Control Box(string title, Canvas c)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var t = new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(2, 0, 0, 2) };
            RoadUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Primary");
            DockPanel.SetDock(t, Avalonia.Controls.Dock.Top); d.Children.Add(t);
            var b = new Border { BorderThickness = new Thickness(1), Child = c };
            RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
            d.Children.Add(b);
            return d;
        }
        var r1 = Box("① 剥采比 / 生产时相 (按年)", _ratio); Grid.SetRow(r1, 0); right.Children.Add(r1);
        var r2 = Box("② 年度物料量 (剥离 V / 采出 P)", _mat); Grid.SetRow(r2, 1); right.Children.Add(r2);
        var r3 = Box("③ 超前剥离 (按年, 万m³)", _adv); Grid.SetRow(r3, 2); right.Children.Add(r3);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        Grid.SetRow(body, 2); root.Children.Add(body);

        // 指标条
        var metrics = new Avalonia.Controls.Primitives.UniformGrid { Columns = 6 };
        Control M(string k, Control v, string? tip = null)
        {
            var sp = new StackPanel { Margin = new Thickness(6, 0) };
            var kt = RoadUi.Hint(k, 11); if (tip != null) ToolTip.SetTip(kt, tip);
            sp.Children.Add(kt); sp.Children.Add(v);
            return sp;
        }
        metrics.Children.Add(M("均衡期数", _stageCountText));
        metrics.Children.Add(M("投产年 / 基建剥离", _commissionText));
        metrics.Children.Add(M("达产年", _capacityText));
        var ratioRow = new StackPanel { Orientation = Orientation.Horizontal };
        ratioRow.Children.Add(_avgRatioText); ratioRow.Children.Add(new TextBlock { Text = " / ", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }); ratioRow.Children.Add(_prodRatioText);
        metrics.Children.Add(M("平均剥采比 / 各阶段", ratioRow, "平均剥采比=总剥离/总采出(含基建)；各阶段=分阶段生产剥采比范围(min~max)"));
        metrics.Children.Add(M("峰值超前剥离", _advanceText, "均衡折线高出实际曲线的最大竖直差(万m³)，即任一时刻最多超前剥离量，越小越省初期工程"));
        metrics.Children.Add(M("校核", _checkText));
        var foot = PlanUi.Footer(metrics);
        Grid.SetRow(foot, 3); root.Children.Add(foot);
        return root;
    }

    // ───────────── 数据装载 ─────────────
    private void LoadSampleData()
    {
        _loading = true;
        _s.LoadSample();
        _loading = false;
        RefreshGrid(); Redraw(); UpdateMetrics();
        SetStatus("已载入模拟物料量(含基建年)，可编辑表格 / 改设计能力 / 改均衡期数。");
    }

    /// <summary>从已确定的计划方案提取逐期 P/V 回填（取数由宿主按 短期确定→短期已排→中长远确定→中长远已排 的优先级给出；都没有明确提示不静默）。</summary>
    private void LoadFromEntities()
    {
        var got = _extract();
        if (got == null || got.Value.rows.Count == 0)
        {
            SetStatus("未找到可提取的计划方案：请先在「短期生产计划 · 月度计划编制」或「中长远进度计划 · 规划计算」排产并确定方案，再点本按钮。当前仍为模拟数据。");
            _ = RoadUi.Info(this, "从计划提取", "没有可提取的计划方案。\n\n请先完成其一：\n  · 短期：月度计划编制 → 一键编制 → 确定月度计划\n  · 中长远：规划计算 → 排产\n\n完成后再点「从计划提取」，本表会按各期的采出量/剥离量自动回填。");
            return;
        }
        var (src, rows) = got.Value;
        _loading = true;
        _s.Load(rows);
        double peak = Math.Round(rows.Max(r => r.Coal), 0);
        if (peak > 0) { _designBox.Text = peak.ToString(CultureInfo.InvariantCulture); _s.DesignCapacity = peak; _s.Recompute(); }
        _loading = false;
        RefreshGrid(); Redraw(); UpdateMetrics();
        SetStatus($"已从 {src} 提取：{rows.Count} 期 · 采出合计 {rows.Sum(r => r.Coal):N0} 万t · 剥离合计 {rows.Sum(r => r.Strip):N0} 万m³（原位实方） · 达产判据已按峰值 {peak:N0} 预填，可改。");
    }

    private void OnParamChanged()
    {
        if (_loading) return;
        _s.DesignCapacity = ParsePositive(_designBox.Text);
        _s.EcoRatio = ParsePositive(_ecoBox.Text);
        var k = ParsePositive(_stageBox.Text);
        _s.StageCount = (k != null && k.Value >= 1) ? (int?)Math.Round(k.Value) : null;
        var f = ParsePositive(_commFracBox.Text);
        if (f != null) _s.CommFrac = Math.Clamp(f.Value, 0.05, 1.0);
        Recompute(null);
    }

    private static double? ParsePositive(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;
    }

    private void Recompute(string? status)
    {
        _s.Recompute();
        RefreshGrid(); Redraw(); UpdateMetrics();
        if (status != null) SetStatus(status);
    }

    private void RefreshGrid()
    {
        var sel = _grid.SelectedItem;
        _grid.ItemsSource = null; _grid.ItemsSource = new ObservableCollection<VpPeriod>(_s.Periods);
        if (sel != null) _grid.SelectedItem = sel;
        _stageGrid.ItemsSource = _s.Stages;
    }

    // ───────────── VP 主图 ─────────────
    private void WireVpInteraction()
    {
        _vp.PointerWheelChanged += (_, e) =>
        {
            var (xmin, xmax, ymin, ymax) = View();
            var p = e.GetPosition(_vp);
            var (wx, wy) = ToWorld(p.X, p.Y, xmin, xmax, ymin, ymax);
            double f = e.Delta.Y > 0 ? 0.8 : 1.25;
            _vxMin = wx - (wx - xmin) * f; _vxMax = wx + (xmax - wx) * f;
            _vyMin = wy - (wy - ymin) * f; _vyMax = wy + (ymax - wy) * f;
            DrawVp(); e.Handled = true;
        };
        _vp.PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2) { ResetVpZoom(); return; }
            if (e.GetCurrentPoint(_vp).Properties.IsLeftButtonPressed) { _dragStart = e.GetPosition(_vp); _dragView = View(); }
        };
        _vp.PointerMoved += (_, e) =>
        {
            if (_dragStart is not { } s0) return;
            var p = e.GetPosition(_vp);
            var (xmin, xmax, ymin, ymax) = _dragView;
            var (w, h) = Sz(_vp);
            double dx = (p.X - s0.X) / Math.Max(1, w - 70) * (xmax - xmin), dy = (p.Y - s0.Y) / Math.Max(1, h - 40) * (ymax - ymin);
            _vxMin = xmin - dx; _vxMax = xmax - dx; _vyMin = ymin + dy; _vyMax = ymax + dy;
            DrawVp();
        };
        _vp.PointerReleased += (_, _) => _dragStart = null;
    }

    private void ResetVpZoom() { _vxMin = 0; _vxMax = double.NaN; _vyMin = 0; _vyMax = double.NaN; DrawVp(); }

    private (double xmin, double xmax, double ymin, double ymax) View()
    {
        double dataX = Math.Max(1, _s.Periods.Count > 0 ? _s.Periods.Max(p => p.CumCoal) : 1) * 1.05;
        double dataY = Math.Max(1, _s.Periods.Count > 0 ? _s.Periods.Max(p => p.CumStrip) : 1) * 1.08;
        return (_vxMin, double.IsNaN(_vxMax) ? dataX : _vxMax, _vyMin, double.IsNaN(_vyMax) ? dataY : _vyMax);
    }

    private const double L = 60, R = 10, T = 12, B = 28;   // 绘图边距
    private static (double w, double h) Sz(Canvas c) => (c.Bounds.Width > 20 ? c.Bounds.Width : 400, c.Bounds.Height > 20 ? c.Bounds.Height : 300);
    private (double, double) ToWorld(double px, double py, double xmin, double xmax, double ymin, double ymax)
    {
        var (w, h) = Sz(_vp);
        return (xmin + (px - L) / (w - L - R) * (xmax - xmin), ymax - (py - T) / (h - T - B) * (ymax - ymin));
    }

    private void Redraw() { DrawVp(); DrawRatio(); DrawMat(); DrawAdv(); }

    private static void Line(Canvas c, double x1, double y1, double x2, double y2, Color col, double th = 1, bool dash = false)
    {
        var l = new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = new SolidColorBrush(col), StrokeThickness = th };
        if (dash) l.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        c.Children.Add(l);
    }
    private static void Text(Canvas c, double x, double y, string t, Color col, double size = 10, bool bold = false)
    {
        var tb = new TextBlock { Text = t, Foreground = new SolidColorBrush(col), FontSize = size };
        if (bold) tb.FontWeight = FontWeight.Bold;
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); c.Children.Add(tb);
    }
    private static void Dot(Canvas c, double x, double y, double d, Color col)
    {
        var e = new Ellipse { Width = d, Height = d, Fill = new SolidColorBrush(col), Stroke = Brushes.White, StrokeThickness = 1.5 };
        Canvas.SetLeft(e, x - d / 2); Canvas.SetTop(e, y - d / 2); c.Children.Add(e);
    }
    private static void Rect(Canvas c, double x, double y, double w, double h, Color col)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(col) };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
    }
    private static double Nice(double range, int ticks)
    {
        double raw = range / Math.Max(1, ticks), mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
    }

    /// <summary>VP 主图：坐标系(原点轴线+主/次网格+刻度) + 实际累计曲线 + 阶段分带 + 分段均衡折线 + 基建剥离/投产/达产标注。</summary>
    private void DrawVp()
    {
        var c = _vp; c.Children.Clear();
        var (w, h) = Sz(c);
        var (xmin, xmax, ymin, ymax) = View();
        double SX(double x) => L + (x - xmin) / (xmax - xmin) * (w - L - R);
        double SY(double y) => T + (ymax - y) / (ymax - ymin) * (h - T - B);

        // 网格与刻度
        double tx = Nice(xmax - xmin, 6), ty = Nice(ymax - ymin, 6);
        for (double x = Math.Ceiling(xmin / tx) * tx; x <= xmax; x += tx)
        {
            for (int k = 1; k < 5; k++) { double sx = SX(x - tx + k * tx / 5); if (sx > L && sx < w - R) Line(c, sx, T, sx, h - B, SubGridColor); }
            Line(c, SX(x), T, SX(x), h - B, GridColor); Line(c, SX(x), h - B, SX(x), h - B + 4, AxisColor, 1.4);
            Text(c, SX(x) - 12, h - B + 5, x.ToString("N0"), AxisColor, 9);
        }
        for (double y = Math.Ceiling(ymin / ty) * ty; y <= ymax; y += ty)
        {
            for (int k = 1; k < 5; k++) { double sy = SY(y - ty + k * ty / 5); if (sy > T && sy < h - B) Line(c, L, sy, w - R, sy, SubGridColor); }
            Line(c, L, SY(y), w - R, SY(y), GridColor); Line(c, L - 4, SY(y), L, SY(y), AxisColor, 1.4);
            Text(c, 2, SY(y) - 7, y.ToString("N0"), AxisColor, 9);
        }
        // 原点轴线
        if (xmin <= 0 && xmax >= 0) Line(c, SX(0), T, SX(0), h - B, AxisColor, 1.8);
        if (ymin <= 0 && ymax >= 0) Line(c, L, SY(0), w - R, SY(0), AxisColor, 1.8);
        Line(c, L, T, L, h - B, AxisColor, 1.4); Line(c, L, h - B, w - R, h - B, AxisColor, 1.4);
        Text(c, w - 150, h - 14, "累计采出量 P (万 t)", AxisColor, 9);
        Text(c, L + 4, T, "累计剥离量 V (万 m³)", AxisColor, 9);

        var xs = _s.Xs; var ys = _s.Ys; var bps = _s.Breakpoints;
        // 阶段分带
        for (int s = 0; s + 1 < bps.Count; s++)
        {
            int a = bps[s], b = bps[s + 1];
            var col = StagePalette[s % StagePalette.Length];
            double x0 = Math.Max(L, SX(xs[a])), x1 = Math.Min(w - R, SX(xs[b]));
            Rect(c, x0, T, x1 - x0, h - T - B, Color.FromArgb(28, col.R, col.G, col.B));
            double r = s < _s.Stages.Count ? _s.Stages[s].Ratio : 0;
            int yrs = s < _s.Stages.Count ? _s.Stages[s].Years : (b - a);
            Text(c, x0 + 3, T + 16 + s * 12, $"阶段{s + 1}  n={r:F2}  {yrs}年", col, 9, true);
        }
        // 实际累计曲线（含基建年在 x=0 的竖直段）
        var pts = new List<Point> { new(SX(0), SY(0)) };
        foreach (var p in _s.Periods) pts.Add(new Point(SX(p.CumCoal), SY(p.CumStrip)));
        c.Children.Add(new Polyline { Points = pts, Stroke = new SolidColorBrush(CurveColor), StrokeThickness = 2 });
        foreach (var p in pts) Dot(c, p.X, p.Y, 6, CurveColor);
        // 分段均衡折线
        for (int s = 0; s + 1 < bps.Count; s++)
        {
            int a = bps[s], b = bps[s + 1];
            var col = StagePalette[s % StagePalette.Length];
            Line(c, SX(xs[a]), SY(ys[a]), SX(xs[b]), SY(ys[b]), col, 3.5);
            Dot(c, SX(xs[a]), SY(ys[a]), 11, col); Dot(c, SX(xs[b]), SY(ys[b]), 11, col);
        }
        // 基建剥离标注
        if (_s.BaseStrip > 1e-9)
        {
            Dot(c, SX(0), SY(_s.BaseStrip * 0.5), 10, Color.Parse("#94A3B8"));
            Text(c, SX(0) + 8, SY(_s.BaseStrip * 0.5) - 7, $"基建剥离 {_s.BaseStrip:N0}", Color.Parse("#64748B"), 10);
        }
        // 投产点 A / 达产点 D
        Dot(c, SX(0), SY(_s.BaseStrip), 17, CommColor);
        Text(c, SX(0) + 12, SY(_s.BaseStrip) - 7, _s.CommIdx >= 0 ? $"投产 {_s.Periods[_s.CommIdx].Label} · 均衡起点" : "投产", CommColor, 10, true);
        if (_s.CapIdx >= 0)
        {
            var cp = _s.Periods[_s.CapIdx];
            Dot(c, SX(cp.CumCoal), SY(cp.CumStrip), 17, CapColor);
            Text(c, SX(cp.CumCoal) - 20, SY(cp.CumStrip) - 24, $"达产 {cp.Label}", CapColor, 10, true);
        }
    }

    /// <summary>① 剥采比阶梯图：柱=逐年剥采比，折线=各年所属阶段均衡剥采比(台阶)，底带=生产时相。</summary>
    private void DrawRatio()
    {
        var c = _ratio; c.Children.Clear();
        var (w, h) = Sz(c);
        int n = _s.Periods.Count; if (n == 0) return;
        double l = 34, r = 6, t = 8, b = 30;
        double maxR = Math.Max(1, _s.Periods.Max(p => p.Ratio)) * 1.15;
        double slot = (w - l - r) / n;
        double SY(double v) => t + (1 - v / maxR) * (h - t - b);
        // 时相分带
        int i0 = 0;
        while (i0 < n)
        {
            string ph = _s.Periods[i0].Phase; int j = i0;
            while (j + 1 < n && _s.Periods[j + 1].Phase == ph) j++;
            var pc = ph switch { "基建" => Color.Parse("#94A3B8"), "过渡" => Color.Parse("#0EA5A4"), "稳产" => Color.Parse("#059669"), "减产" => Color.Parse("#DC2626"), _ => Color.Parse("#94A3B8") };
            Rect(c, l + i0 * slot, t, (j - i0 + 1) * slot, h - t - b, Color.FromArgb(28, pc.R, pc.G, pc.B));
            Text(c, l + i0 * slot + 2, t, ph, pc, 9, true);
            i0 = j + 1;
        }
        Line(c, l, h - b, w - r, h - b, AxisColor); Line(c, l, t, l, h - b, AxisColor);
        double tk = Nice(maxR, 4);
        for (double v = 0; v <= maxR; v += tk) { Line(c, l, SY(v), w - r, SY(v), GridColor); Text(c, 2, SY(v) - 7, v.ToString("0.#"), AxisColor, 9); }
        var stageR = _s.StageRatioPerPeriod();
        var linePts = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            var p = _s.Periods[i];
            double cx = l + (i + 0.5) * slot;
            if (p.Coal > 1e-9) Rect(c, cx - slot * 0.3, SY(p.Ratio), slot * 0.6, (h - b) - SY(p.Ratio), Color.Parse("#CBD5E1"));
            if (stageR[i] is { } sr) linePts.Add(new Point(cx, SY(sr)));
            var lbl = new TextBlock { Text = p.Label, Foreground = new SolidColorBrush(AxisColor), FontSize = 8, RenderTransform = new RotateTransform(90), RenderTransformOrigin = RelativePoint.TopLeft };
            Canvas.SetLeft(lbl, cx + 4); Canvas.SetTop(lbl, h - b + 2); c.Children.Add(lbl);
        }
        if (linePts.Count > 0)
        {
            c.Children.Add(new Polyline { Points = linePts, Stroke = C("#EA580C"), StrokeThickness = 3 });
            foreach (var p in linePts) Dot(c, p.X, p.Y, 9, Color.Parse("#EA580C"));
        }
        Text(c, l + 4, h - 12, "年份", AxisColor, 8);
    }

    /// <summary>② 年度物料量：剥离 V(左轴 teal) + 采出 P(右轴 amber) 双柱。</summary>
    private void DrawMat()
    {
        var c = _mat; c.Children.Clear();
        var (w, h) = Sz(c);
        int n = _s.Periods.Count; if (n == 0) return;
        double l = 40, r = 40, t = 8, b = 30;
        double maxV = Math.Max(1, _s.Periods.Max(p => p.Strip)) * 1.1, maxP = Math.Max(1, _s.Periods.Max(p => p.Coal)) * 1.1;
        double slot = (w - l - r) / n;
        Line(c, l, h - b, w - r, h - b, AxisColor);
        double tv = Nice(maxV, 4), tp = Nice(maxP, 4);
        for (double v = 0; v <= maxV; v += tv) { double y = t + (1 - v / maxV) * (h - t - b); Line(c, l, y, w - r, y, GridColor); Text(c, 2, y - 7, v.ToString("N0"), AxisColor, 8); }
        for (double v = 0; v <= maxP; v += tp) { double y = t + (1 - v / maxP) * (h - t - b); Text(c, w - r + 3, y - 7, v.ToString("N0"), AxisColor, 8); }
        Text(c, 2, 0, "剥离V 万m³", Color.Parse("#0EA5A4"), 8, true);
        Text(c, w - r - 40, 0, "采出P 万t", Color.Parse("#D97706"), 8, true);
        for (int i = 0; i < n; i++)
        {
            var p = _s.Periods[i];
            double cx = l + (i + 0.5) * slot, bw = slot * 0.3;
            double hv = p.Strip / maxV * (h - t - b), hp = p.Coal / maxP * (h - t - b);
            Rect(c, cx - bw, h - b - hv, bw, hv, Color.Parse("#0EA5A4"));
            Rect(c, cx, h - b - hp, bw, hp, Color.Parse("#F59E0B"));
            var lbl = new TextBlock { Text = p.Label, Foreground = new SolidColorBrush(AxisColor), FontSize = 8, RenderTransform = new RotateTransform(90), RenderTransformOrigin = RelativePoint.TopLeft };
            Canvas.SetLeft(lbl, cx + 4); Canvas.SetTop(lbl, h - b + 2); c.Children.Add(lbl);
        }
    }

    /// <summary>③ 超前剥离随年：各年累计偏离(均衡线−实际累计)，正=超前(紫)、负=欠剥(红)。</summary>
    private void DrawAdv()
    {
        var c = _adv; c.Children.Clear();
        var (w, h) = Sz(c);
        int n = _s.Periods.Count; if (n == 0) return;
        double l = 44, r = 6, t = 8, b = 30;
        double maxD = Math.Max(1, _s.Periods.Max(p => Math.Abs(p.Deviation))) * 1.1;
        double minD = Math.Min(0, _s.Periods.Min(p => p.Deviation)) * 1.1, topD = Math.Max(1e-9, _s.Periods.Max(p => p.Deviation)) * 1.1;
        if (topD < maxD * 0.2) topD = maxD * 0.2;
        double SY(double v) => t + (topD - v) / (topD - minD) * (h - t - b);
        double slot = (w - l - r) / n;
        double tk = Nice(topD - minD, 4);
        for (double v = Math.Ceiling(minD / tk) * tk; v <= topD; v += tk) { Line(c, l, SY(v), w - r, SY(v), GridColor); Text(c, 2, SY(v) - 7, v.ToString("N0"), AxisColor, 8); }
        Line(c, l, SY(0), w - r, SY(0), AxisColor);
        Text(c, 2, 0, "超前剥离 万m³", AxisColor, 8, true);
        for (int i = 0; i < n; i++)
        {
            var p = _s.Periods[i];
            double cx = l + (i + 0.5) * slot, bw = slot * 0.6;
            double y0 = SY(0), y1 = SY(p.Deviation);
            Rect(c, cx - bw / 2, Math.Min(y0, y1), bw, Math.Abs(y1 - y0), p.Deviation >= 0 ? Color.Parse("#7C3AED") : Color.Parse("#DC2626"));
            var lbl = new TextBlock { Text = p.Label, Foreground = new SolidColorBrush(AxisColor), FontSize = 8, RenderTransform = new RotateTransform(90), RenderTransformOrigin = RelativePoint.TopLeft };
            Canvas.SetLeft(lbl, cx + 4); Canvas.SetTop(lbl, h - b + 2); c.Children.Add(lbl);
        }
    }

    // ───────────── 指标 ─────────────
    private void UpdateMetrics()
    {
        _stageCountText.Text = _s.UsedK > 0 ? (_s.StageCount.HasValue ? $"{_s.UsedK}" : $"{_s.UsedK}（建议）") : "—";
        _commissionText.Text = _s.CommIdx >= 0 ? $"{_s.Periods[_s.CommIdx].Label} / {_s.BaseStrip:N0} (超前{_s.AheadMonths:F0}月)" : "无采出";
        _capacityText.Text = _s.CapIdx >= 0 ? _s.Periods[_s.CapIdx].Label : (_s.DesignCapacity is null ? "(未设能力)" : "未达产");
        _avgRatioText.Text = _s.AvgRatio.ToString("F2", CultureInfo.InvariantCulture);
        _prodRatioText.Text = _s.Stages.Count > 0 ? $"{_s.Stages.Min(s => s.Ratio):F2}~{_s.Stages.Max(s => s.Ratio):F2}" : "—";
        _advanceText.Text = _s.PeakAdvance.ToString("N0", CultureInfo.InvariantCulture);
        _checkText.Text = _s.CheckOk ? "通过" : string.Join("·", _s.Issues);
        _checkText.Foreground = _s.CheckOk ? C("#059669") : C("#DC2626");
    }

    private void SetStatus(string msg) { _status.Text = msg; _echo("剥采比均衡：" + msg, false); }
}
