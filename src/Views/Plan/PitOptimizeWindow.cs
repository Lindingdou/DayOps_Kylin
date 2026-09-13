using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「境界优化 — 计算 · 方案比选 · 确定最终境界」窗口（优化开采设计 按钮②「确定境界」；移植原 <c>PitOptimizeWindow</c>）。
/// 对一组方案求解 → 并排对比（指标 × 方案矩阵，每行 ▲=最优，末组综合评分/排名/推荐）→ 选定 → 确定最终境界
/// （落地三维台阶面 + 台阶线为工程位置，并标记方案「已确定」供采区划分取用）。未配置时「一键圈定」用默认方案直接出结果。
/// </summary>
internal sealed class PitOptimizeWindow : Window
{
    public ObservableCollection<PitScheme> Schemes { get; }
    private readonly IPlanEntityHost _host;

    private readonly DataGrid _schemeList;
    private readonly DataGrid _compareGrid;
    private readonly ProgressBar _progress = new() { Width = 160, Height = 14, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _toolStatus = RoadUi.Hint("");
    private readonly TextBlock _bottomStatus = RoadUi.Hint("选定方案后点「确定最终境界」→ 落地坡顶/坡底线为工程位置 + 写储量评价报表");
    private readonly TextBlock _detailTitle = RoadUi.Text("选中方案：", 13, bold: true);
    private readonly TextBlock _dDepth = Bold("—"), _dCoal = Bold("—"), _dWaste = Bold("—"), _dAvg = Bold("—"),
                               _dContour = Bold("—", PlanUi.OrangeBrush), _dValue = Bold("—", PlanUi.TealBrush);
    private bool _busy;

    private static TextBlock Bold(string t, IBrush? fg = null)
    {
        var tb = new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 2) };
        if (fg != null) tb.Foreground = fg; else RoadUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        return tb;
    }

    public PitOptimizeWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "境界优化 — 计算 · 方案比选 · 确定最终境界";
        PlanUi.Place(this, 1320, 800);
        Schemes = BoundarySchemeStore.Schemes;

        _schemeList = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = false, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5,
        };
        _schemeList.Columns.Add(new DataGridCheckBoxColumn { Header = "比选", Binding = new Avalonia.Data.Binding("Participate") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(44) });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "方案", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Avalonia.Data.Binding("SolvedText"), Width = new DataGridLength(62), IsReadOnly = true });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "确定", Binding = new Avalonia.Data.Binding("ConfirmedText"), Width = new DataGridLength(70), IsReadOnly = true });
        _schemeList.ItemsSource = Schemes;
        _schemeList.SelectionChanged += (_, _) => RefreshDetail(Current);

        _compareGrid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All, FontSize = 12.5,
        };

        Content = PlanUi.Shell(
            PlanUi.Header("境界优化 — 计算 · 方案比选 · 确定最终境界", "求解 → 并排对比储量/剥采比/经济 → 选定后确定最终境界并落地为工程位置 ·  未配置则按全自动默认一键圈定"),
            BuildBody(), BuildFooter());

        RebuildComparison();
        if (Schemes.Count > 0) _schemeList.SelectedIndex = 0;
    }

    private PitScheme? Current => _schemeList.SelectedItem as PitScheme;

    private Control BuildBody()
    {
        var outer = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        // 工具条
        var tool = new DockPanel();
        var b1 = RoadUi.Btn("⚡ 一键圈定(全自动)", async () => await OnOneClickAutoAsync(), 140, bold: true);
        var b2 = RoadUi.Btn("求解选中方案", async () => await OnSolveSelectedAsync(), 110);
        var b3 = RoadUi.Btn("求解全部方案", async () => await SolveSchemesAsync(Schemes.Where(s => s.Participate).ToList()), 110);
        foreach (var b in new[] { b1, b2, b3 }) { DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); tool.Children.Add(b); }
        DockPanel.SetDock(_progress, Avalonia.Controls.Dock.Left); tool.Children.Add(_progress);
        _toolStatus.VerticalAlignment = VerticalAlignment.Center; tool.Children.Add(_toolStatus);
        var toolBorder = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        RoadUi.Theme(toolBorder, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(toolBorder, Border.BackgroundProperty, "Theme.Panel.Background");
        Grid.SetRow(toolBorder, 0); outer.Children.Add(toolBorder);

        // 主体三栏
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,384") };
        var left = PlanUi.Group("方案列表（勾选参与比选）", _schemeList, new Thickness(12, 12, 6, 12), 6);
        Grid.SetColumn(left, 0); g.Children.Add(left);
        var mid = PlanUi.Group("方案对比（指标 × 方案 ·  每行 ▲=最优 ·  末组：综合评分 / 排名 / 推荐，多准则加权）", _compareGrid, new Thickness(6, 12, 6, 12), 6);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,*"), Margin = new Thickness(6, 12, 12, 12) };
        var detail = new StackPanel();
        _detailTitle.Margin = new Thickness(0, 0, 0, 8);
        detail.Children.Add(_detailTitle);
        var ug = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        void Pair(string k, TextBlock v) { ug.Children.Add(RoadUi.Hint(k, 12.5)); ug.Children.Add(v); }
        Pair("开采深度", _dDepth); Pair("煤量", _dCoal); Pair("岩量", _dWaste); Pair("平均剥采比", _dAvg); Pair("境界剥采比", _dContour); Pair("净值", _dValue);
        detail.Children.Add(ug);
        var gDetail = PlanUi.Group("储量与剥采比评价", detail, new Thickness(0, 0, 0, 6), 8);
        Grid.SetRow(gDetail, 0); right.Children.Add(gDetail);
        var ph1 = RoadUi.Hint("（预览占位）求解后显示坡顶/坡底线与各剖面圈定结果");
        ph1.HorizontalAlignment = HorizontalAlignment.Center; ph1.VerticalAlignment = VerticalAlignment.Center; ph1.TextAlignment = TextAlignment.Center;
        var gPrev = PlanUi.Group("境界平面 / 横剖面预览", ph1, new Thickness(0, 0, 0, 6), 6);
        Grid.SetRow(gPrev, 1); right.Children.Add(gPrev);
        var ph2 = RoadUi.Hint("（曲线占位）n_境 随深度上升，与 n_经 交点即最终开采深度");
        ph2.HorizontalAlignment = HorizontalAlignment.Center; ph2.VerticalAlignment = VerticalAlignment.Center; ph2.TextAlignment = TextAlignment.Center;
        var gCurve = PlanUi.Group("境界剥采比 – 深度曲线", ph2, new Thickness(0), 6);
        Grid.SetRow(gCurve, 2); right.Children.Add(gCurve);
        Grid.SetColumn(right, 2); g.Children.Add(right);

        Grid.SetRow(g, 1); outer.Children.Add(g);
        return outer;
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var confirm = new Button
        {
            Content = "✔ 确定最终境界", MinWidth = 150, Height = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(PlanUi.Teal, 0), new GradientStop(PlanUi.TealDark, 1) },
            },
        };
        confirm.Click += (_, _) => OnConfirmFinal();
        DockPanel.SetDock(confirm, Avalonia.Controls.Dock.Right); dock.Children.Add(confirm);
        var export = RoadUi.Btn("导出报表", () => Bottom("导出储量与剥采比评价报表（TODO）"), 90);
        export.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(export, Avalonia.Controls.Dock.Right); dock.Children.Add(export);
        _bottomStatus.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(_bottomStatus);
        return PlanUi.Footer(dock);
    }

    /// <summary>重建「指标 × 方案」对比矩阵：列按参与比选且已解的方案动态生成，行按指标分组（分组用标题行表示）。</summary>
    private void RebuildComparison()
    {
        var cmp = ComparisonBuilder.Build(Schemes.Where(s => s.Participate).ToList(), withGroupHeaders: true);

        _compareGrid.Columns.Clear();
        _compareGrid.Columns.Add(new DataGridTextColumn { Header = "指标", Binding = new Avalonia.Data.Binding("Label"), Width = new DataGridLength(180) });
        for (int i = 0; i < Math.Min(8, cmp.Names.Length); i++)
            _compareGrid.Columns.Add(new DataGridTextColumn { Header = cmp.Names[i], Binding = new Avalonia.Data.Binding($"C{i}"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _compareGrid.Columns.Add(new DataGridTextColumn { Header = "最优", Binding = new Avalonia.Data.Binding("Best"), Width = new DataGridLength(110) });
        _compareGrid.ItemsSource = cmp.Rows;

        _toolStatus.Text = cmp.RecommendIndex >= 0
            ? $"推荐方案：{cmp.Names[cmp.RecommendIndex]}（综合评分最高）·  参与比选 {cmp.Names.Length} 个"
            : "无已求解方案——先「求解」再对比。";
    }

    private void RefreshDetail(PitScheme? s)
    {
        if (s == null) return;
        _detailTitle.Text = $"选中方案：{s.Name}" + (s.Result == null ? "（未求解）" : "");
        if (s.Result is not { } r) { foreach (var t in new[] { _dDepth, _dCoal, _dWaste, _dAvg, _dContour, _dValue }) t.Text = "—"; return; }
        _dDepth.Text = $"{r.DepthM:N0} m";
        _dCoal.Text = $"{r.CoalWanT:N0} 万t";
        _dWaste.Text = $"{r.WasteWanM3:N0} 万m³";
        _dAvg.Text = $"{r.AvgRatio:F2} m³/t";
        _dContour.Text = $"{r.ContourRatio:F2} m³/t";
        _dValue.Text = $"{r.NetValue:N0} 万元";
    }

    /// <summary>命令行「确定境界 一键」/自检直通：等价点「⚡ 一键圈定(全自动)」。</summary>
    public Task OneClickAutoAsync() => OnOneClickAutoAsync();

    private async Task OnOneClickAutoAsync()
    {
        if (Schemes.Count == 0) Schemes.Add(new PitScheme { Name = "默认方案" });
        await SolveSchemesAsync(Schemes.Where(s => s.Participate).ToList());
    }
    private async Task OnSolveSelectedAsync()
    {
        if (Current is { } s) await SolveSchemesAsync(new[] { s });
        else Tool("请先在左侧选中一个方案。");
    }

    /// <summary>对一组方案后台求解，回填 Result，刷新对比矩阵与详情。失败方案不中断批次。</summary>
    private async Task SolveSchemesAsync(IReadOnlyList<PitScheme> list)
    {
        if (_busy) return;
        if (list.Count == 0) { Tool("没有参与比选的方案（在左表勾选「比选」）。"); return; }

        _busy = true;
        _progress.IsIndeterminate = true;
        var prog = new Progress<string>(m => _toolStatus.Text = m);
        int ok = 0, fail = 0; string lastFail = "";
        try
        {
            foreach (var s in list)
            {
                var outcome = await PitSolveRunner.SolveAsync(s, _host, prog, CancellationToken.None);
                if (outcome.Ok && outcome.Result != null) { s.Result = outcome.Result; ok++; }
                else { fail++; lastFail = outcome.Message; }
            }
        }
        finally
        {
            _busy = false;
            _progress.IsIndeterminate = false;
        }

        RefreshList();
        RebuildComparison();
        RefreshDetail(Current);
        _toolStatus.Text = fail > 0
            ? $"求解：成功 {ok}，失败 {fail}　—　{lastFail}"
            : $"求解完成：{ok} 个方案，对比矩阵已更新。";
    }

    private void RefreshList()
    {
        var sel = Current;
        _schemeList.ItemsSource = null; _schemeList.ItemsSource = Schemes;
        if (sel != null) _schemeList.SelectedItem = sel;
    }

    private void OnConfirmFinal()
    {
        if (Current is not { } s) { Bottom("请先在左侧选中一个方案。"); return; }
        if (s.Result == null) { Bottom("该方案尚未求解——先「求解」再确定。"); return; }
        var outcome = PitMaterializer.Materialize(s, _host);
        if (outcome.Ok) { s.IsConfirmed = true; _host.Refresh(); }
        _bottomStatus.Text = outcome.Message;
        _host.Echo((outcome.Ok ? "确定境界：" : "确定境界失败：") + outcome.Message, !outcome.Ok);
        RefreshList();
        RebuildComparison();
    }

    private void Tool(string what) => _toolStatus.Text = $"（待实现）{what}";
    private void Bottom(string what) => _bottomStatus.Text = $"（待实现）{what}";
}
