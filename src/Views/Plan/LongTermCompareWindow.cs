using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「方案综合对比」窗口（中长远组第 4 按钮；移植原 <c>LongTermCompareWindow</c>）。对参与比选的多套进度计划做多维联合对比：
/// 对比矩阵 + 四维论证图表 + 雷达 + 方向感知加权评分排名。单套时即"单方案指标"。
/// </summary>
internal sealed class LongTermCompareWindow : Window
{
    private readonly ObservableCollection<LongTermPlan> _schemes;
    private readonly IPlanEntityHost _host;
    private readonly DataGrid _schemeList, _compareGrid, _rankGrid;
    private readonly Canvas _chartOutput = C(), _chartSr = C(), _chartNpv = C(), _chartGantt = C(), _chartRadar = C();
    private readonly TextBlock _toolStatus = RoadUi.Hint("");
    private readonly TextBlock _topText = new() { Text = "—", FontWeight = FontWeight.Bold, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF)), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    private static Canvas C() => new() { Background = Brushes.White, ClipToBounds = true, MinHeight = 120 };

    public LongTermCompareWindow(IPlanEntityHost host, ObservableCollection<LongTermPlan>? schemes = null)
    {
        _host = host;
        _schemes = schemes is { Count: > 0 } ? schemes : LongTermSchemeStore.Schemes;
        Title = "方案综合对比 — 多套进度计划联合对比";
        PlanUi.Place(this, 1400, 800);

        _schemeList = new DataGrid { AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5 };
        _schemeList.Columns.Add(new DataGridCheckBoxColumn { Header = "比选", Binding = new Avalonia.Data.Binding("Participate") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(50) });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "方案", Binding = new Avalonia.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _schemeList.Columns.Add(new DataGridTextColumn { Header = "得分", Binding = new Avalonia.Data.Binding("RScore"), Width = new DataGridLength(56), IsReadOnly = true });
        PlanUi.FitHeaders(_schemeList);
        _schemeList.ItemsSource = _schemes;

        // ★ 一列星号都不留 + 冻结「方案」列 + 让它真的横滚（原版注释：星号列被压扁时每个表头都被切一刀，横滚条却不出现）
        _compareGrid = PlanUi.Table(new (string, string, double)[]
        {
            ("方案", "Name", 170), ("工作线·推进", "WorkLineCaption", 200), ("服务年限(a)", "RLife", 100), ("达产(a)", "RTtc", 76), ("稳产期(a)", "RPlateau", 92),
            ("峰值剥采比", "RPeak", 100), ("内排率%", "RInner", 84), ("排满年", "RFullYear", 76), ("排不下(万m³)", "ROverflow", 116), ("NPV(万)", "RNpv", 104),
            ("储量均衡", "RBalance", 86), ("综合得分", "RScore", 86),
        }, multi: false);
        _compareGrid.FrozenColumnCount = 1; _compareGrid.MaxHeight = 240; _compareGrid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        _compareGrid.ItemsSource = _schemes;

        _rankGrid = PlanUi.Table(new (string, string, double)[] { ("名次", "Rank", 56), ("方案", "Name", 150), ("综合得分", "ScoreText", 84), ("校核", "OkText", 64) }, multi: false);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var header = PlanUi.Header("方案综合对比", "多套进度计划多维联合对比（服务年限/达产/稳产期/削峰/内排率/NPV/储量均衡）+ 方向感知加权评分排名", Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x1E, 0x40, 0xAF));
        Grid.SetRow(header, 0); root.Children.Add(header);

        var tool = new DockPanel();
        var b = RoadUi.Btn("联合对比评分", Rescore, 120, bold: true); DockPanel.SetDock(b, Avalonia.Controls.Dock.Left); tool.Children.Add(b);
        _toolStatus.VerticalAlignment = VerticalAlignment.Center; _toolStatus.Margin = new Thickness(10, 0, 0, 0); tool.Children.Add(_toolStatus);
        var toolB = new Border { Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        RoadUi.Theme(toolB, Border.BorderBrushProperty, "Theme.Panel.Border"); RoadUi.Theme(toolB, Border.BackgroundProperty, "Theme.Panel.Background");
        Grid.SetRow(toolB, 1); root.Children.Add(toolB);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*,340") };
        var left = PlanUi.Group("方案列表（勾选参与对比）", _schemeList, new Thickness(12, 12, 6, 12), 6);
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var mid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(6, 12, 6, 12) };
        var mg = PlanUi.Group("联合对比矩阵（多维 ·  指标 × 方案）", _compareGrid, new Thickness(0, 0, 0, 6), 6);
        Grid.SetRow(mg, 0); mid.Children.Add(mg);
        var ug = new Avalonia.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2 };
        ug.Children.Add(ChartBox("① 逐年产量 + 达产线", _chartOutput));
        ug.Children.Add(ChartBox("② 生产剥采比削峰 SR(t)", _chartSr));
        ug.Children.Add(ChartBox("③ 累计净现值 NPV(t)", _chartNpv));
        ug.Children.Add(ChartBox("④ 生产时相甘特", _chartGantt));
        var cg = PlanUi.Group("对比论证图表（四维度 · 本功能自算）", ug, new Thickness(0), 6);
        Grid.SetRow(cg, 1); mid.Children.Add(cg);
        Grid.SetColumn(mid, 1); body.Children.Add(mid);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(6, 12, 12, 12) };
        _chartRadar.Height = 190;
        var rg = PlanUi.Group("多指标综合雷达", _chartRadar, new Thickness(0, 0, 0, 6), 6);
        Grid.SetRow(rg, 0); right.Children.Add(rg);
        var sp = new StackPanel(); sp.Children.Add(RoadUi.Hint("推荐方案（综合得分最高且可行）", 12.5)); sp.Children.Add(_topText);
        var info = PlanUi.InfoBox(sp, new Thickness(0, 0, 0, 6));
        Grid.SetRow(info, 1); right.Children.Add(info);
        var rk = PlanUi.Group("评分排名", _rankGrid, new Thickness(0), 6);
        Grid.SetRow(rk, 2); right.Children.Add(rk);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        Grid.SetRow(body, 2); root.Children.Add(body);
        Content = root;

        foreach (var c in new[] { _chartOutput, _chartSr, _chartNpv, _chartGantt, _chartRadar })
            c.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) RefreshCharts(); };
        Rescore();
    }

    private static Border ChartBox(string title, Canvas c)
    {
        var dp = new DockPanel();
        var t = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF)), Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(t, Avalonia.Controls.Dock.Top); dp.Children.Add(t); dp.Children.Add(c);
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(3), Padding = new Thickness(6), Child = dp };
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        return b;
    }

    private List<LongTermPlan> Participating() => _schemes.Where(s => s.Participate && s.Result != null).ToList();

    private void Rescore()
    {
        // 未排产的参与方案先补排产，保证对比有数据
        var missing = _schemes.Where(z => z.Participate && z.Result == null).ToList();
        if (missing.Count > 0)
        {
            var form = LongTermDumpBridge.TryReadForm(_host.Db);
            foreach (var s in missing) LongTermScheduler.Schedule(s, _host.ActiveBlockModel, form);
        }
        var part = Participating();
        string top = LongTermComparer.Score(part);
        _compareGrid.ItemsSource = null; _compareGrid.ItemsSource = _schemes;
        _schemeList.ItemsSource = null; _schemeList.ItemsSource = _schemes;
        RefreshCharts();
        RefreshRanking(part, top);
        _toolStatus.Text = part.Count switch
        {
            // 【LT4】空库不再是"稍后再来"：它必然是因为没人为指定工作线（库不会自己造方案了）
            0 => LongTermSchemeStore.NeedWorkLineHint,
            1 => $"单套：仅「{top}」，显示单方案指标（多套见「派生计划方案」）",
            _ => $"联合对比 {part.Count} 套 → 推荐「{top}」",
        };
    }

    private void RefreshCharts()
    {
        var s = Participating();
        LongTermCharts.DrawOutputCurve(_chartOutput, s);
        LongTermCharts.DrawSrCurve(_chartSr, s);
        LongTermCharts.DrawNpvCurve(_chartNpv, s);
        LongTermCharts.DrawPhaseGantt(_chartGantt, s);
        LongTermCharts.DrawRadar(_chartRadar, s);
    }

    private void RefreshRanking(List<LongTermPlan> part, string top)
    {
        _topText.Text = part.Count == 0 ? "—" : top;
        _rankGrid.ItemsSource = part.OrderByDescending(p => p.Result!.CompositeScore)
            .Select((p, i) => new RankRow { Rank = i + 1, Name = p.Name, Score = p.Result!.CompositeScore, OkText = p.Result!.OkText }).ToList();
    }

    private sealed class RankRow
    {
        public int Rank { get; set; }
        public string Name { get; set; } = "";
        public double Score { get; set; }
        public string ScoreText => Score.ToString("F0");
        public string OkText { get; set; } = "";
    }
}
