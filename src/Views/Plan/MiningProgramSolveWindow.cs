using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AdvanceMode = PitMine3D.Kylin.Cad.AdvanceMode;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using StripRatioField = PitMine3D.Kylin.Cad.StripRatioField;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「采区划分 — 计算 · 比选 · 确定开采程序」窗口（优化开采设计 按钮④「开采程序确定」；移植原 <c>MiningProgramSolveWindow</c>）。
/// 对一组开采程序方案：一键划分 / 求解（剥采比场 → 拉沟·推进候选 → 切采区 → 评价 → 级2评分）→ 指标对比矩阵 + 拉沟候选表（采用 / 视口布置工作线·推进 /
/// 工作线形态）+ 四维度论证图表 + 采区表 → 确定（落地采区边界/拉沟/推进箭头为图纸实体）/ 导出 CSV 报表。
/// </summary>
internal sealed class MiningProgramSolveWindow : Window
{
    private readonly ObservableCollection<MiningProgramPlan> _schemes;
    private readonly IPlanEntityHost _host;
    private bool _formLoading;

    private readonly DataGrid _schemeList, _compareGrid, _boxcutGrid, _panelGrid;
    private readonly ComboBox _formCombo;
    private readonly ProgressBar _progress = new() { Width = 160, Height = 14, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _toolStatus = RoadUi.Hint(""), _boxcutStatus = RoadUi.Hint(""), _bottomStatus = RoadUi.Hint("选定方案后点「确定开采程序」→ 落地采区边界/拉沟/推进箭头为文档实体（采区方案完成）");
    private readonly TextBlock _detailTitle = RoadUi.Text("选中方案：", 13, bold: true);
    private readonly TextBlock _pPeak = Bold("—"), _pInner = Bold("—", PlanUi.TealBrush), _pTtc = Bold("—"), _pLife = Bold("—"), _pBalance = Bold("—"),
                               _pBasic = Bold("—"), _pHaul = Bold("—"), _pNpv = Bold("—", PlanUi.TealBrush), _pScore = Bold("—", PlanUi.TealBrush);
    private readonly Canvas _chartSr = Chart(), _chartRadar = Chart(), _chartDump = Chart(), _chartGantt = Chart();

    private static Canvas Chart() => new() { Height = 150, ClipToBounds = true, Background = Brushes.Transparent };
    private static TextBlock Bold(string t, IBrush? fg = null)
    {
        var tb = new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 2) };
        if (fg != null) tb.Foreground = fg; else RoadUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        return tb;
    }

    public MiningProgramSolveWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "采区划分 — 计算 · 比选 · 确定开采程序";
        PlanUi.Place(this, 1320, 820);
        _schemes = MiningProgramStore.Schemes;

        _schemeList = Grid3(new (string, string, double)[] { ("方案", "Name", 0), ("状态", "SolvedText", 62) }, check: true);
        _schemeList.ItemsSource = _schemes;
        _schemeList.SelectionChanged += (_, _) => { if (Current != null) RefreshDetail(Current); };

        _compareGrid = Grid3(new (string, string, double)[]
        {
            ("方案", "Name", 0), ("采区数", "RPanelCount", 62), ("峰值生产剥采比", "RPeak", 110), ("内排率%", "RInner", 74), ("达产(a)", "RTtc", 64),
            ("服务年限(a)", "RLife", 90), ("储量均衡", "RBalance", 76), ("基建剥离(亿m³)", "RBasic", 106), ("综合得分", "RScore", 76), ("备注", "Note", 60),
        });
        _compareGrid.MaxHeight = 220;
        _compareGrid.ItemsSource = _schemes;

        _boxcutGrid = Grid3(new (string, string, double)[]
        {
            ("候选", "Name", 0), ("来源", "SourceText", 68), ("方位(°)", "AzText", 52), ("推进方式", "AdvanceModeText", 74), ("工作线长(m)", "WlText", 86),
            ("剥采比", "SrText", 56), ("运输", "HaulText", 50), ("内排", "InnerText", 50), ("工作线", "WlScoreText", 54), ("总分", "TotalText", 52), ("", "RecoText", 66),
        });
        _boxcutGrid.MaxHeight = 170;
        _boxcutGrid.SelectionChanged += (_, _) => OnBoxcutSelected();
        _formCombo = PlanUi.Combo(new[] { "直线平推", "定点回转", "动点回转" }, 0, 104);
        _formCombo.SelectionChanged += (_, _) => OnBoxcutFormChanged();

        _panelGrid = Grid3(new (string, string, double)[]
        {
            ("序", "OrderText", 76), ("采区", "Name", 0), ("煤量(万t)", "CoalText", 84), ("剥采比", "SrText", 64), ("推进(°)", "AzText", 54),
            ("工作线L(m)", "WlText", 82), ("推进v(m/a)", "RateText", 82), ("排弃", "DumpText", 88),
        });

        Content = PlanUi.Shell(
            PlanUi.Header("采区划分 — 计算 · 比选 · 确定开采程序", "一键划分 → 采区平面图 + 指标 → 多方案比选 → 确定后落地采区/拉沟/推进为文档实体 ·  未配置则全自动默认"),
            BuildBody(), BuildFooter());

        if (_schemes.Count > 0) _schemeList.SelectedIndex = 0;
        foreach (var c in new[] { _chartSr, _chartRadar, _chartDump, _chartGantt })
            c.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) RefreshCharts(); };
        RefreshCharts();
    }

    private static DataGrid Grid3(IReadOnlyList<(string Header, string Path, double Width)> cols, bool check = false)
    {
        var dg = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = !check, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5,
        };
        if (check) dg.Columns.Add(new DataGridCheckBoxColumn { Header = "比选", Binding = new Avalonia.Data.Binding("Participate") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(44) });
        foreach (var (h, p, w) in cols)
            // 原 XAML 像素宽在 Kylin 14px 正文下压表头(见记忆 datagrid-star-column-fit)，统一放宽 1.3 倍
            dg.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Avalonia.Data.Binding(p), Width = w <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w * 1.3), IsReadOnly = true });
        return dg;
    }

    private MiningProgramPlan? Current => _schemeList.SelectedItem as MiningProgramPlan;

    private Control BuildBody()
    {
        var outer = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var tool = new DockPanel();
        var b1 = RoadUi.Btn("⚡ 一键划分(全自动)", () => Solve("一键划分(全自动)", true), 140, bold: true);
        var b2 = RoadUi.Btn("求解选中方案", () => Solve("求解选中", false), 110);
        var b3 = RoadUi.Btn("求解全部方案", () => Solve("求解全部", true), 110);
        foreach (var b in new[] { b1, b2, b3 }) { DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); tool.Children.Add(b); }
        DockPanel.SetDock(_progress, Avalonia.Controls.Dock.Left); tool.Children.Add(_progress);
        _toolStatus.VerticalAlignment = VerticalAlignment.Center; tool.Children.Add(_toolStatus);
        var toolBorder = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        RoadUi.Theme(toolBorder, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(toolBorder, Border.BackgroundProperty, "Theme.Panel.Background");
        Grid.SetRow(toolBorder, 0); outer.Children.Add(toolBorder);

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,384") };
        var left = PlanUi.Group("方案列表（勾选参与比选）", _schemeList, new Thickness(12, 12, 6, 12), 6);
        Grid.SetColumn(left, 0); g.Children.Add(left);

        // 中：对比矩阵 / 拉沟候选 / 图表
        var mid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(6, 12, 6, 12) };
        var gCmp = PlanUi.Group("方案对比（指标 × 方案 ·  每行最优即推荐）", _compareGrid, new Thickness(0, 0, 0, 6), 6);
        Grid.SetRow(gCmp, 0); mid.Children.Add(gCmp);

        var bcDock = new DockPanel();
        var bcTool = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bcTool.Children.Add(RoadUi.Btn("采用选中拉沟·推进", OnAdoptBoxcut, 150));
        var bPlace = RoadUi.Btn("视口布置工作线·推进", async () => await OnPlaceWorkingLineAsync(), 150);
        ToolTip.SetTip(bPlace, "在视口点开段沟两端：算推进方位 + 工作线长，并按这条人工拉沟·推进即时重划采区");
        bcTool.Children.Add(bPlace);
        var fl = RoadUi.Hint("工作线形态"); fl.VerticalAlignment = VerticalAlignment.Center; fl.Margin = new Thickness(14, 0, 4, 0);
        bcTool.Children.Add(fl); bcTool.Children.Add(_formCombo);
        _boxcutStatus.VerticalAlignment = VerticalAlignment.Center; _boxcutStatus.Margin = new Thickness(12, 0, 0, 0);
        bcTool.Children.Add(_boxcutStatus);
        DockPanel.SetDock(bcTool, Avalonia.Controls.Dock.Top); bcDock.Children.Add(bcTool);
        bcDock.Children.Add(_boxcutGrid);
        var gBc = PlanUi.Group("拉沟·推进候选（不同版本 ·  约束打分排序 ·  ★推荐 ·  可采用）", bcDock, new Thickness(0, 0, 0, 6), 6);
        Grid.SetRow(gBc, 1); mid.Children.Add(gBc);

        var charts = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        charts.Children.Add(ChartBox("① 生产剥采比削峰曲线 SR(t)", _chartSr));
        charts.Children.Add(ChartBox("② 多指标综合雷达", _chartRadar));
        charts.Children.Add(ChartBox("③ 内外排土方平衡", _chartDump));
        charts.Children.Add(ChartBox("④ 采区接续甘特", _chartGantt));
        var gCharts = PlanUi.Group("对比论证图表（四维度 · 本功能自算）", new ScrollViewer { Content = charts }, new Thickness(0), 6);
        Grid.SetRow(gCharts, 2); mid.Children.Add(gCharts);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);

        // 右：指标 / 采区表 / 平面图占位
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,*"), Margin = new Thickness(6, 12, 12, 12) };
        var detail = new StackPanel();
        _detailTitle.Margin = new Thickness(0, 0, 0, 8);
        detail.Children.Add(_detailTitle);
        var ug = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        void Pair(string k, TextBlock v) { ug.Children.Add(RoadUi.Hint(k, 12.5)); ug.Children.Add(v); }
        Pair("峰值生产剥采比", _pPeak); Pair("内排率", _pInner); Pair("达产时间", _pTtc); Pair("服务年限", _pLife); Pair("储量均衡系数", _pBalance);
        Pair("基建剥离量", _pBasic); Pair("平均运距", _pHaul); Pair("累计NPV", _pNpv); Pair("综合得分", _pScore);
        detail.Children.Add(ug);
        var gDetail = PlanUi.Group("开采程序指标", detail, new Thickness(0, 0, 0, 6), 8);
        Grid.SetRow(gDetail, 0); right.Children.Add(gDetail);
        var gPanels = PlanUi.Group("采区（开采序 / 首采区 / 拉沟 / 推进 / 排弃）", _panelGrid, new Thickness(0, 0, 0, 6), 6);
        Grid.SetRow(gPanels, 1); right.Children.Add(gPanels);
        var ph = RoadUi.Hint("（平面图占位）求解后显示采区边界、首采区高亮、拉沟线与推进方向箭头");
        ph.HorizontalAlignment = HorizontalAlignment.Center; ph.VerticalAlignment = VerticalAlignment.Center; ph.TextAlignment = TextAlignment.Center;
        var gPlan = PlanUi.Group("采区平面图（叠剥采比热力图 + 拉沟 + 推进箭头）", ph, new Thickness(0), 6);
        Grid.SetRow(gPlan, 2); right.Children.Add(gPlan);
        Grid.SetColumn(right, 2); g.Children.Add(right);

        Grid.SetRow(g, 1); outer.Children.Add(g);
        return outer;
    }

    private static Control ChartBox(string title, Canvas c)
    {
        var d = new DockPanel { Margin = new Thickness(4) };
        var t = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = PlanUi.TealBrush, Margin = new Thickness(0, 0, 0, 2), FontSize = 12 };
        DockPanel.SetDock(t, Avalonia.Controls.Dock.Top);
        d.Children.Add(t); d.Children.Add(c);
        return d;
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var confirm = new Button
        {
            Content = "✔ 确定开采程序", MinWidth = 150, Height = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(PlanUi.Teal, 0), new GradientStop(PlanUi.TealDark, 1) },
            },
        };
        confirm.Click += (_, _) => OnConfirmProgram();
        DockPanel.SetDock(confirm, Avalonia.Controls.Dock.Right); dock.Children.Add(confirm);
        var export = RoadUi.Btn("导出报表", async () => await OnExportReportAsync(), 90);
        export.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(export, Avalonia.Controls.Dock.Right); dock.Children.Add(export);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(_bottomStatus);
        return PlanUi.Footer(dock);
    }

    private void RefreshDetail(MiningProgramPlan p)
    {
        _detailTitle.Text = $"选中方案：{p.Name}";
        var r = p.Result;
        _pPeak.Text = r != null ? $"{r.ProductionRatioPeak:0.0} m³/t" : "—";
        _pInner.Text = r != null ? $"{r.InnerDumpPct:0} %" : "—";
        _pTtc.Text = r != null ? $"{r.TimeToCapacityYears:0} a" : "—";
        _pLife.Text = r != null ? $"{r.ServiceLifeYears:0} a" : "—";
        _pBalance.Text = r != null ? r.ReserveBalanceCoef.ToString("0.00") : "—";
        _pBasic.Text = r != null ? $"{r.BasicStrippingYiM3:0.0} 亿m³" : "—";
        _pHaul.Text = r != null ? $"{r.AvgHaulKm:0.0} km" : "—";
        _pNpv.Text = r != null ? $"{r.Npv:N0} 万元" : "—";
        _pScore.Text = r != null ? $"{r.CompositeScore:0} / 100" : "—";
        _panelGrid.ItemsSource = null; _panelGrid.ItemsSource = p.Panels;
        _boxcutGrid.ItemsSource = null; _boxcutGrid.ItemsSource = p.BoxcutOptions;
        _boxcutGrid.SelectedItem = p.SelectedBoxcut;
        _boxcutStatus.Text = p.SelectedBoxcut is { } b
            ? $"推荐：{b.Name}（{b.SourceText} · 总分 {b.TotalScore:0}）"
            : "（无候选——在「采区划分设置」点「推荐候选」生成）";
    }

    private void RefreshLists()
    {
        var sel = Current;
        _schemeList.ItemsSource = null; _schemeList.ItemsSource = _schemes;
        _compareGrid.ItemsSource = null; _compareGrid.ItemsSource = _schemes;
        if (sel != null) _schemeList.SelectedItem = sel;
    }

    // ── 拉沟·推进候选：采用选中 ──
    private void OnAdoptBoxcut()
    {
        if (Current is not { } cur) return;
        if (_boxcutGrid.SelectedItem is not BoxcutAdvanceOption o) { _boxcutStatus.Text = "请先在候选表中选一行再采用"; return; }
        foreach (var x in cur.BoxcutOptions) x.Recommended = false;
        o.Recommended = true;
        cur.SelectedBoxcut = o;
        var src = cur.BoxcutOptions; _boxcutGrid.ItemsSource = null; _boxcutGrid.ItemsSource = src; _boxcutGrid.SelectedItem = o;
        _boxcutStatus.Text = $"已采用「{o.Name}」（方位 {o.AdvanceAzimuthDeg:0}° · 工作线长 {o.WorkingLineLengthM:0} m · {o.AdvanceModeText}）作为本方案的拉沟·推进";
    }

    private void OnBoxcutSelected()
    {
        if (_boxcutGrid.SelectedItem is BoxcutAdvanceOption o)
        { _formLoading = true; _formCombo.SelectedIndex = (int)o.AdvanceMode; _formLoading = false; }
    }

    private void OnBoxcutFormChanged()
    {
        if (_formLoading) return;
        if (_boxcutGrid.SelectedItem is BoxcutAdvanceOption o && _formCombo.SelectedIndex >= 0)
        {
            o.AdvanceMode = (AdvanceMode)_formCombo.SelectedIndex;
            var src = _boxcutGrid.ItemsSource; _boxcutGrid.ItemsSource = null; _boxcutGrid.ItemsSource = src; _boxcutGrid.SelectedItem = o;
            _boxcutStatus.Text = $"已设「{o.Name}」工作线形态 = {o.AdvanceModeText}（手动）";
        }
    }

    // ── 视口布置工作线·推进（点取式）：点开段沟两端 → 算方位+工作线长 → 即时重划采区 ──
    private async Task OnPlaceWorkingLineAsync()
    {
        if (Current is not { } cur) { _toolStatus.Text = "请先选一个方案"; return; }
        var field = TrySampleField(out _);
        double centerX = field != null ? field.Ox + field.Nx * field.Dx / 2 : 0;
        double centerY = field != null ? field.Oy + field.Ny * field.Dy / 2 : 0;
        _toolStatus.Text = "视口布置：① 点开段沟一端";
        var a = await _host.PickPointAsync("视口布置工作线·推进：① 点开段沟一端（Esc 取消）");
        if (a == null) { _toolStatus.Text = "（视口布置已取消）"; Activate(); return; }
        _toolStatus.Text = "视口布置：② 点开段沟另一端";
        var b = await _host.PickPointAsync("视口布置工作线·推进：② 点开段沟另一端（Esc 取消）");
        Activate();
        if (b == null) { _toolStatus.Text = "（视口布置已取消）"; return; }
        if (!CommitPlacement(cur, a.Value.x, a.Value.y, b.Value.x, b.Value.y, centerX, centerY)) _toolStatus.Text = "（视口布置已取消：开段沟太短）";
    }

    /// <summary>按两端点算推进方位（垂直开段沟、朝场内）+ 工作线长，加入人工候选并即时重划（纯逻辑可单测）。</summary>
    public bool CommitPlacement(MiningProgramPlan cur, double ax, double ay, double bx, double by, double centerX, double centerY)
    {
        var o = BuildPlacedOption(cur, ax, ay, bx, by, centerX, centerY, ProgramPanelDelineator.FormFromOutline(ProgramPanelDelineator.ResolveOutline(cur, _host)));
        if (o == null) return false;
        foreach (var x in cur.BoxcutOptions) x.Recommended = false;
        cur.BoxcutOptions.Add(o);
        cur.SelectedBoxcut = o;
        cur.BoxcutMode = BoxcutMode.Manual;
        cur.ManualAdvanceAzimuthDeg = o.AdvanceAzimuthDeg;
        Solve("视口布置工作线·推进", allParticipating: false);
        return true;
    }

    public static BoxcutAdvanceOption? BuildPlacedOption(MiningProgramPlan cur, double ax, double ay, double bx, double by, double centerX, double centerY, AdvanceMode form)
    {
        double ux = bx - ax, uy = by - ay, len = Math.Sqrt(ux * ux + uy * uy);
        if (len < 1) return null;
        ux /= len; uy /= len;
        double mx = (ax + bx) / 2, my = (ay + by) / 2;
        double px = -uy, py = ux;
        if (px * (centerX - mx) + py * (centerY - my) < 0) { px = -px; py = -py; }
        double az = (Math.Atan2(px, py) * 180.0 / Math.PI + 360) % 360;
        var o = new BoxcutAdvanceOption
        {
            Name = "人工·视口布置", IsAuto = false, BoxcutDesc = "视口点取开段沟",
            AdvanceAzimuthDeg = Math.Round(az, 0), WorkingLineLengthM = Math.Round(len, 0), AdvanceMode = form,
            SrScore = 0.8, ShallowScore = 0.8, HaulScore = 0.8, InnerDumpScore = 0.8,
            WorkLineScore = Math.Min(1.0, len / Math.Max(1, cur.MinWorkingLineM)), GeoScore = 0.8,
            Feasible = len >= cur.MinWorkingLineM, Recommended = true,
            Rationale = $"视口人工布置：开段沟长 {len:0}m、推进方位 {az:0}°",
        };
        o.Recompute(cur.BoxcutWeights);
        return o;
    }

    // ── 求解（剥采比场 → 拉沟·推进 → 切采区 → 评价 → 级2评分）──
    /// <summary>命令行「开采程序确定 一键」/自检直通：等价点「⚡ 一键划分(全自动)」。</summary>
    public void OneClickAuto() => Solve("一键划分(全自动)", true);

    private void Solve(string what, bool allParticipating)
    {
        var field = TrySampleField(out string note);
        var scope = allParticipating
            ? _schemes.Where(s => s.Participate).ToList()
            : (Current != null ? new List<MiningProgramPlan> { Current } : new List<MiningProgramPlan>());

        int real = 0;
        if (field != null)
        {
            foreach (var plan in scope)
            {
                if (plan.SelectedBoxcut == null)
                {
                    var outline = ProgramPanelDelineator.ResolveOutline(plan, _host);
                    var opts = ProgramPanelDelineator.Recommend(plan, field, outline);
                    plan.BoxcutOptions.Clear();
                    foreach (var o in opts) plan.BoxcutOptions.Add(o);
                    plan.SelectedBoxcut = opts.FirstOrDefault(o => o.Recommended) ?? opts.FirstOrDefault();
                }
                if (plan.SelectedBoxcut == null) continue;

                var panels = ProgramPanelSplitter.Split(plan, field, plan.SelectedBoxcut);
                plan.Panels.Clear();
                foreach (var p in panels) plan.Panels.Add(p);
                plan.Result = ProgramPlanEvaluator.Evaluate(plan);
                if (plan.Result != null) plan.Result.DrawZ = field.TopZ;
                real++;
            }
        }

        var ranked = _schemes.Where(s => s.Participate && s.Result != null).ToList();
        string top = ProgramPlanComparer.Score(ranked);

        RefreshLists();
        if (Current != null) RefreshDetail(Current);
        RefreshCharts();
        _toolStatus.Text = real > 0
            ? $"（{what}）已对 {real} 个方案从剥采比场真切采区 + 综合评分 → 推荐「{top}」（采区/剥采比/储量均衡/基建剥离/内排率 自算）"
            : $"（{what}）{note}；保留样例指标";
    }

    /// <summary>取激活带煤块体采样剥采比场；无则 null + 说明。</summary>
    private StripRatioField? TrySampleField(out string note)
    {
        var m = _host.ActiveBlockModel;
        if (m == null) { note = "无激活块体模型（请先在「块体模型」激活带煤模型）"; return null; }
        try
        {
            var f = PlanStripRatioFieldSampler.Sample(m, MiningProgramPlan.DefaultCoalDensity);
            if (f is { CoalColumns: > 0 }) { note = "剥采比场=真值"; return f; }
            note = "激活块体无煤属性"; return null;
        }
        catch (Exception ex) { note = $"采样失败：{ex.Message}"; return null; }
    }

    private void RefreshCharts()
    {
        var s = _schemes.Where(p => p.Participate && p.Result != null && p.Panels.Count > 0).ToList();
        MiningProgramCharts.DrawSrCurve(_chartSr, s);
        MiningProgramCharts.DrawRadar(_chartRadar, s);
        MiningProgramCharts.DrawDumpBars(_chartDump, s);
        MiningProgramCharts.DrawGantt(_chartGantt, s);
    }

    // ── 底部：确定 / 导出 ──
    private void OnConfirmProgram()
    {
        if (Current == null) return;
        var outcome = ProgramMaterializer.Materialize(Current, _host);
        _bottomStatus.Text = outcome.Ok ? $"✔ {outcome.Message}" : $"未落地：{outcome.Message}";
        _host.Echo((outcome.Ok ? "确定开采程序：" : "确定开采程序失败：") + outcome.Message, !outcome.Ok);
        if (outcome.Ok) _host.Refresh();
    }

    private async Task OnExportReportAsync()
    {
        if (Current is not { } cur) { _bottomStatus.Text = "请先选一个方案再导出"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出开采程序报表", SuggestedFileName = $"采区划分_{cur.Name}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } }, new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } } },
        });
        if (file == null) return;
        try
        {
            System.IO.File.WriteAllText(file.Path.LocalPath, BuildReport(cur), System.Text.Encoding.UTF8);
            _bottomStatus.Text = $"已导出报表：{file.Path.LocalPath}";
        }
        catch (Exception ex) { _bottomStatus.Text = $"导出失败：{ex.Message}"; }
    }

    /// <summary>方案报表（CSV）：程序指标 + 采区明细 + 拉沟候选 + 内外排平衡。</summary>
    public static string BuildReport(MiningProgramPlan p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"开采程序方案报表,{p.Name}");
        sb.AppendLine();
        sb.AppendLine("=== 程序指标 ===");
        if (p.Result is { } r)
        {
            sb.AppendLine($"采区数,{r.PanelCount}");
            sb.AppendLine($"峰值生产剥采比(m³/t),{r.ProductionRatioPeak:0.0}");
            sb.AppendLine($"内排率(%),{r.InnerDumpPct:0}");
            sb.AppendLine($"达产时间(a),{r.TimeToCapacityYears:0}");
            sb.AppendLine($"服务年限(a),{r.ServiceLifeYears:0}");
            sb.AppendLine($"储量均衡系数,{r.ReserveBalanceCoef:0.00}");
            sb.AppendLine($"基建剥离量(亿m³),{r.BasicStrippingYiM3:0.00}");
            sb.AppendLine($"平均运距(km),{r.AvgHaulKm:0.00}");
            sb.AppendLine($"综合得分,{r.CompositeScore:0}");
            sb.AppendLine($"校核,{r.OkText}");
        }
        else sb.AppendLine("（未求解）");
        sb.AppendLine();
        sb.AppendLine("=== 采区明细 ===");
        sb.AppendLine("序,采区,煤量(万t),岩量(万m³),剥采比(m³/t),工作线长(m),推进度(m/a),排弃,服务年限(a)");
        foreach (var pa in p.Panels.OrderBy(z => z.Order))
            sb.AppendLine($"{pa.OrderText},{pa.Name},{pa.CoalWanT:0},{pa.WasteWanM3:0},{pa.StripRatio:0.0},{pa.WorkingLineLengthM:0},{pa.AdvanceRateMpa:0},{pa.DumpText},{pa.ServiceLifeYears:0.0}");
        sb.AppendLine();
        sb.AppendLine("=== 拉沟·推进候选 ===");
        sb.AppendLine("候选,来源,推进方式,方位(°),工作线长(m),剥采比,运输,内排,工作线,总分,推荐");
        foreach (var b in p.BoxcutOptions)
            sb.AppendLine($"{b.Name},{b.SourceText},{b.AdvanceModeText},{b.AdvanceAzimuthDeg:0},{b.WorkingLineLengthM:0},{b.SrScore:0.00},{b.HaulScore:0.00},{b.InnerDumpScore:0.00},{b.WorkLineScore:0.00},{b.TotalScore:0},{b.RecoText}");
        sb.AppendLine();
        sb.AppendLine("=== 内外排平衡（按采区指定）===");
        double ext = p.Panels.Where(x => x.Dump == DumpMode.External).Sum(x => x.WasteWanM3);
        double inn = p.Panels.Where(x => x.Dump == DumpMode.Internal).Sum(x => x.WasteWanM3);
        sb.AppendLine($"外排(万m³),{ext:0}");
        sb.AppendLine($"内排(万m³),{inn:0}");
        return sb.ToString();
    }
}
