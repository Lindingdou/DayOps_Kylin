// 忠实移植自原 PitMine3D Modules/TaskLib/Features/DynamicAdjustWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF GridSplitter/GroupBox → Avalonia GridSplitter/TaskUi.GroupBox；MessageBox → CoalMsgBox；OpenFileDialog → StorageProvider；
// 三维面板 SimPanelHost 为 Avalonia 自绘 Control（见 Simulation/SimPanelOverlay.cs）。
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PitMine3D.Kylin.TaskLib.Adjust;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 生产任务动态调整（逐日 · 分区域 · 分环节）。
///
/// <para><b>三件事在一个窗口里闭环</b>：
/// ① 跨天甘特把「区域 × 工序」铺到日历天上 —— 哪个区域的哪道工序哪天干、干多少、哪天出岔子；
/// ② 点开任一格，在三维里放那一天那道工序的推进演示（可先贴一张 GeoTIFF 正射影像当底图）；
/// ③ 异常格给原因码路由建议，并保留原有的「滚动重排 + 欠量回摊」（<see cref="TaskRescheduler"/>）。</para>
///
/// <para><b>两条口径纪律</b>：
/// · <b>真实天 vs 推算天</b>：只有当日盘子那一天带真任务与实绩，其余天是按同一套结构摊出来的。
///   甘特上真实格实心实边、推算格淡色虚边；重排也只动真实那一天。
/// · <b>解不出就不画</b>：推进距离缺 H/L 时轮廓原地不动并写明原因，不拿缺省值伪造推进。</para>
/// </summary>
public sealed class DynamicAdjustWindow : Window
{
    // 原因码 → 调整动作（与 docs/日常生产组织_设计.md §6 一致）
    private static readonly Dictionary<IncompleteReason, string> Route = new()
    {
        [IncompleteReason.Fault] = "欠量转同面其它编组 / 派维修队 / 重排剩余任务",
        [IncompleteReason.TruckShortage] = "补车 / 降铲产对齐车队 / 调邻组卡车",
        [IncompleteReason.ProcessWait] = "优先调度上游穿孔·爆破 / 该面顺延",
        [IncompleteReason.OreShortage] = "切面（换作业面）/ 报采准不断档预警",
        [IncompleteReason.Weather] = "全盘降效回摊 / 启用保守工作历",
        [IncompleteReason.RoadCongestion] = "调卸点 / 错峰 / 联动 RoadLib",
        [IncompleteReason.Absence] = "顶班 / 降编组",
        [IncompleteReason.Maintenance] = "工作历未对齐 → 修班次日历",
        [IncompleteReason.OverPlanned] = "反馈①编制：收紧设备分析预测口径",
    };

    /// <summary>播放一遍本日推进的时长（秒）。</summary>
    private const double PlaySeconds = 2.2;

    private DayStageTimeline _tl = new();
    private StageGanttModel _model = new();
    private StageGanttCell? _selected;

    // ── 两条落地端 ── 演示图元由 StageSimPlayer 推给 ISimDynamicOverlay：
    //   · _panel（默认）：画进本窗的 SimPanelHost，**自带相机** ⇒ 选中一格就能自动装满；
    //   · SimDynamicOverlay.Current：画到内核主视口 —— 那边没有相机 API，只能人工转过去。
    private readonly SimPanelOverlay _panel = new();
    private readonly SimBasemapRaster _photo = new();
    private SimPanelHost? _host;
    private readonly StageSimPlayer _player;

    /// <summary>换格之后还没给相机装过 —— 下一帧装一次（之后就由人自己转，不再抢镜头）。</summary>
    private bool _needFit;
    /// <summary>「展开」前甘特行的高度（还原用）。</summary>
    private GridLength _ganttKeep = new(1, GridUnitType.Star);
    private double _ganttKeepMin;

    /// <summary>当前画在窗内面板上？（取消勾选＝画到主视口）</summary>
    private bool InPanel => chkInView.IsChecked == true;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private DateTime _playStart;
    private bool _playing;
    private bool _suppressSlider;
    private bool _loaded;

    // ── 控件（与原 XAML x:Name 一一对应）──
    private readonly RowDefinition rowGantt = new(1, GridUnitType.Star) { MinHeight = 240 };
    private readonly RowDefinition rowBottom = new(360, GridUnitType.Pixel) { MinHeight = 200 };
    private readonly ComboBox cbRange = new() { Width = 128 };
    private readonly TextBlock txtAnchor = new() { VerticalAlignment = VerticalAlignment.Center, Width = 76, TextAlignment = TextAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly Button btnClearBasemap;
    private readonly TextBlock txtBasemap = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 11.5, Opacity = 0.8, Text = "未装底图（演示画在素色地表上）", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    private readonly TextBlock txtSource = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 };
    private readonly Canvas gantt = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel detailPanel = new();
    private readonly Button btnPlay, btnStop, btnFit, btnTop, btnIso, btnExpand;
    private readonly Slider slProgress = new() { Width = 130, Minimum = 0, Maximum = 1, Value = 1, VerticalAlignment = VerticalAlignment.Center, IsEnabled = false };
    private readonly TextBlock txtProgress = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11.5, Opacity = 0.75, Text = "期末" };
    private readonly CheckBox chkInView = new() { Content = "画在窗内", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock txtViewInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel simPanel = new();
    private readonly ComboBox cbOrigin = new() { Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox cbStrategy = new() { Width = 104, Height = 30 };
    private readonly StackPanel suggestPanel = new();

    public DynamicAdjustWindow()
    {
        _player = new StageSimPlayer(_panel);   // 默认画在窗内
        Title = "生产任务动态调整 — 逐日 · 分区域 · 分环节";
        TaskUi.Place(this, 1420, 880);
        MinHeight = 620;
        _timer.Tick += OnTick;

        var header = TaskUi.Header("生产任务动态调整", "逐日排布「区域 × 工序」→ 点开任一格看当日该环节的三维演示（可贴正射影像底图）→ 异常按原因码路由 + 滚动重排");

        // ── 工具条 ──
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "区间", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.75 });
        foreach (var s in new[] { "锚点所在整月", "前后各 15 天", "往后 60 天" }) cbRange.Items.Add(new ComboBoxItem { Content = s });
        cbRange.SelectedIndex = 0;
        cbRange.SelectionChanged += (_, _) => { if (!_loaded) return; StopSim(); Reload(); };
        left.Children.Add(cbRange);
        // 锚点月翻页：班次台账/实绩常常只覆盖某几个月，翻不过去就永远看不到真数据（用文字不用 ◀▶，按内容自适应宽）
        var prev = Small("上月", () => ShiftAnchor(-1)); prev.Margin = new Thickness(8, 0, 0, 0); ToolTip.SetTip(prev, "锚点退一个月"); left.Children.Add(prev);
        left.Children.Add(txtAnchor);
        var next = Small("下月", () => ShiftAnchor(+1)); ToolTip.SetTip(next, "锚点进一个月"); left.Children.Add(next);
        var today = Small("回作业日", OnBackToWorkDate); today.Margin = new Thickness(6, 0, 0, 0); left.Children.Add(today);
        left.Children.Add(new Border { Width = 1, Margin = new Thickness(12, 2), Background = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)) });
        var bm = Small("载入正射影像(TIFF)", OnLoadBasemap); ToolTip.SetTip(bm, "选一张带地理配准的 GeoTIFF 贴到三维地表，作为作业演示的底图"); left.Children.Add(bm);
        btnClearBasemap = Small("清除底图", OnClearBasemap); btnClearBasemap.Margin = new Thickness(6, 0, 0, 0); btnClearBasemap.IsEnabled = false; left.Children.Add(btnClearBasemap);
        TaskUi.Theme(txtBasemap, TextBlock.ForegroundProperty, "Theme.Text.Body");
        left.Children.Add(txtBasemap);
        var tool = new DockPanel();
        DockPanel.SetDock(left, Avalonia.Controls.Dock.Left); tool.Children.Add(left);
        TaskUi.Theme(txtSource, TextBlock.ForegroundProperty, "Theme.Text.Body");
        DockPanel.SetDock(txtSource, Avalonia.Controls.Dock.Right); tool.Children.Add(txtSource);
        var toolBar = new Border { Padding = new Thickness(10, 7), BorderThickness = new Thickness(0, 0, 0, 1), Child = tool };
        TaskUi.Theme(toolBar, Border.BorderBrushProperty, "Theme.Surface.Border");

        // ── 跨天环节甘特 ──
        gantt.PointerPressed += OnGanttClick;
        var sv = new ScrollViewer { Content = gantt, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var ganttBox = new Border { Margin = new Thickness(10, 8, 10, 0), BorderThickness = new Thickness(1), Child = sv };
        TaskUi.Theme(ganttBox, Border.BorderBrushProperty, "Theme.Surface.Border");

        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };

        // ── 下栏：环节明细 | 演示 | 调整建议 ──
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1.15*,330"), Margin = new Thickness(10, 0, 10, 10) };

        var detailBox = TaskUi.GroupBox("环节明细", new ScrollViewer { Content = detailPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, new Thickness(0, 0, 6, 0), 8);
        Grid.SetColumn(detailBox, 0); bottom.Children.Add(detailBox);

        // 三维作业演示：文字行固定高度、面板吃星号
        var sim = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,96") };
        sim.RowDefinitions[2].MinHeight = 70;
        var r0 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        btnPlay = Small("▶ 播放本日三班", OnPlay); btnPlay.IsEnabled = false; ToolTip.SetTip(btnPlay, "按当日时钟从最早开班走到最晚收班，逐班演示；班间不排班的时段轮廓不动"); r0.Children.Add(btnPlay);
        btnStop = Small("抹掉图元", StopSim); btnStop.Margin = new Thickness(6, 0, 0, 0); btnStop.IsEnabled = false; r0.Children.Add(btnStop);
        r0.Children.Add(new TextBlock { Text = "进度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0), Opacity = 0.75, FontSize = 11.5 });
        slProgress.ValueChanged += (_, e) => OnProgressChanged(e.NewValue);
        r0.Children.Add(slProgress); r0.Children.Add(txtProgress);
        Grid.SetRow(r0, 0); sim.Children.Add(r0);

        var r1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        ToolTip.SetTip(chkInView, "勾上＝画进下面这块面板（自带相机，选中一格就自动装满）\n取消＝画到主三维视口（宿主没有相机 API，本窗不会自动飞过去）");
        chkInView.IsCheckedChanged += (_, _) => { if (_loaded) OnRenderTargetChanged(); };
        r1.Children.Add(chkInView);
        btnFit = Small("装满本格", OnFitView, 26); ToolTip.SetTip(btnFit, "把本格的期初环 ∪ 期末环装进面板（不是全矿包围盒）"); r1.Children.Add(btnFit);
        btnTop = Small("俯视", () => SetView(90, 0), 26); btnTop.Margin = new Thickness(5, 0, 0, 0); ToolTip.SetTip(btnTop, "正射俯视（等于平面图）"); r1.Children.Add(btnTop);
        btnIso = Small("轴测", () => SetView(55, 30), 26); btnIso.Margin = new Thickness(5, 0, 0, 0); ToolTip.SetTip(btnIso, "倾角 55°、方位 30°"); r1.Children.Add(btnIso);
        btnExpand = Small("展开", OnToggleExpand, 26); btnExpand.Margin = new Thickness(10, 0, 0, 0); ToolTip.SetTip(btnExpand, "临时收起上面的甘特，把整窗让给演示；再点一次还原"); r1.Children.Add(btnExpand);
        r1.Children.Add(txtViewInfo);
        Grid.SetRow(r1, 1); sim.Children.Add(r1);

        // 演示面板底色：与「班内工艺·工序系统推演」「中长远模拟」同一套（矢量色是中深色，铺在浅色窗底上分不出来）
        _host = new SimPanelHost(_panel);
        var mapGrid = new Grid();
        mapGrid.Children.Add(_host);
        mapGrid.Children.Add(new TextBlock { Text = "右键拖 = 转视角　中键拖 = 平移　滚轮 = 缩放", VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 6), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)), IsHitTestVisible = false });
        var mapBox = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x1F)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x29, 0x38, 0x4F)), Child = mapGrid };
        Grid.SetRow(mapBox, 2); sim.Children.Add(mapBox);

        var sp2 = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(sp2, 3); sim.Children.Add(sp2);
        var simScroll = new ScrollViewer { Content = simPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(simScroll, 4); sim.Children.Add(simScroll);
        var simBox = TaskUi.GroupBox("三维作业演示", sim, new Thickness(0, 0, 6, 0), 8);
        Grid.SetColumn(simBox, 1); bottom.Children.Add(simBox);

        // 调整建议（原因码路由）
        var sug = new DockPanel();
        var sugBottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        // 重排起点：班前会要为下一个班重排时，起点该是那个班的班初；起点一律不早于此刻
        var od = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var ol = new TextBlock { Text = "起点", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(ol, Avalonia.Controls.Dock.Left); od.Children.Add(ol);
        ToolTip.SetTip(cbOrigin, "从哪一刻起重排。选某个班＝从该班班初起（不早于此刻）；「全部」＝从此刻起。");
        cbOrigin.SelectionChanged += (_, _) => { if (_loaded) ShowOriginHint(); };
        od.Children.Add(cbOrigin);
        sugBottom.Children.Add(od);
        var apply = TaskUi.Btn("① 日内重排（当天班次）", OnApply, 100); apply.Height = 30; apply.Margin = new Thickness(0); apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(apply, "对作业日当天的全盘异常面按原因码选策略，跑 TaskRescheduler 真重排（时间轴 0–24h）");
        sugBottom.Children.Add(apply);
        var rd = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var (t, tip) in new[] { ("最早优先", "最早可用日优先填满：欠量尽早补回、后面不滚雪球；代价是紧接着那几天被顶到产能上限"), ("均衡摊平", "按各作业日的余量能力占比分摊：没有哪天被顶满、留着缓冲；代价是欠量拖到期末才补完") })
        { var it = new ComboBoxItem { Content = t }; ToolTip.SetTip(it, tip); cbStrategy.Items.Add(it); }
        cbStrategy.SelectedIndex = 0;
        ToolTip.SetTip(cbStrategy, "两种策略都不改「装不装得下」，只改「摊在哪几天」");
        DockPanel.SetDock(cbStrategy, Avalonia.Controls.Dock.Right); rd.Children.Add(cbStrategy);
        var roll = TaskUi.Btn("② 跨天顺延", OnRollover, 80, bold: true); roll.Height = 30; roll.Margin = new Thickness(0, 0, 5, 0); roll.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(roll, "日内消化不掉的欠量，按后续作业日的余量能力（班窗 − 已排工时）× 编组班产逐日装下去");
        rd.Children.Add(roll);
        sugBottom.Children.Add(rd);
        var clr = TaskUi.Btn("清除顺延", OnClearRollover, 80); clr.Height = 26; clr.Margin = new Thickness(0, 5, 0, 0); clr.HorizontalAlignment = HorizontalAlignment.Stretch;
        sugBottom.Children.Add(clr);
        DockPanel.SetDock(sugBottom, Avalonia.Controls.Dock.Bottom); sug.Children.Add(sugBottom);
        sug.Children.Add(new ScrollViewer { Content = suggestPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var sugBox = TaskUi.GroupBox("调整建议（原因码路由）", sug, new Thickness(0), 8);
        Grid.SetColumn(sugBox, 2); bottom.Children.Add(sugBox);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(rowGantt);          // 甘特是主体（真数据下 5 组 12 行、画布 600+ px），给 MinHeight 免得一拖就压成两三行
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(rowBottom);         // 下栏默认 360：里面多了一块真三维面板
        Grid.SetRow(header, 0); Grid.SetRow(toolBar, 1); Grid.SetRow(ganttBox, 2); Grid.SetRow(splitter, 3); Grid.SetRow(bottom, 4);
        root.Children.Add(header); root.Children.Add(toolBar); root.Children.Add(ganttBox); root.Children.Add(splitter); root.Children.Add(bottom);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = root;

        // 粗细分主次：此刻这一环（GCurrent）最粗，推进带次之，全矿轮廓最细。
        _panel.SetGroupWidth(StageSimPlayer.GCurrent, 2.6);
        _panel.SetGroupWidth(StageSimPlayer.GBand, 1.8);
        _panel.SetGroupWidth(StageSimPlayer.GTarget, 1.6);
        _panel.SetGroupWidth(StageSimPlayer.GBefore, 1.2);
        _panel.SetGroupWidth(StageSimPlayer.GRegions, 1.0);
        _panel.Camera.TiltDeg = 90;    // 俯视起步＝平面图，位置关系最好判

        Opened += (_, _) =>
        {
            // 重排起点的取值域按当日班制填，默认落当前班（＝从此刻起，见 RescheduleFrom）
            ShiftSelector.Bind(cbOrigin, includeAll: true);
            ShowOriginHint();

            try { _anchor = ProjectScope.WorkDate.Date; } catch { _anchor = DateTime.Today; }

            string regionLabel = _player.Reload();
            _loaded = true;
            Reload();

            ShowHint(detailPanel, "在上方甘特里点任一格：看那一天、那个区域、那道工序的明细与三维演示。");
            ShowHint(simPanel, regionLabel + "\n\n" + _player.StatusLabel);
            UpdateViewInfo(null);

            // 工程里已经配过影像的话，开窗就把面板底图铺上（主视口那条另由「载入正射影像」走）
            TryLoadPanelBasemap();
            ShowHint(suggestPanel, "选中带 ⚠ 角标的格子（异常环节）即出原因码路由建议。");
        };
        Closed += (_, _) => { _timer.Stop(); try { _player.Stop(); } catch { } };
    }

    private static Button Small(string text, Action onClick, double height = 28)
    {
        var b = TaskUi.Btn(text, onClick, 0);
        b.Height = height; b.Padding = new Thickness(10, 0); b.Margin = new Thickness(0); b.MinWidth = 0;
        return b;
    }

    // ═════════════════════════ 装载 ═════════════════════════

    /// <summary>区间锚点。默认作业日；上月/下月可以翻到别的月（班次台账、实绩、单元台账常常只覆盖某几个月）。</summary>
    private DateTime _anchor = DateTime.Today;

    /// <summary>「当时排的是什么」× 「现在排的是什么」的对账。欠量 = 计划 − 实绩，计划是活的，尺子得说清楚对着哪一份。</summary>
    private PlanDriftResult _drift = new();

    private void Reload()
    {
        DateTime? from = null, to = null;

        switch (cbRange.SelectedIndex)
        {
            case 1: from = _anchor.AddDays(-15); to = _anchor.AddDays(15); break;
            case 2: from = _anchor; to = _anchor.AddDays(60); break;
            default:
                // 整月：显式给起止，Builder 那边的"默认取作业日所在月"只在两者都为 null 时生效
                from = new DateTime(_anchor.Year, _anchor.Month, 1);
                to = from.Value.AddMonths(1).AddDays(-1);
                break;
        }

        txtAnchor.Text = _anchor.ToString("yyyy-MM");

        _tl = DayStagePlanBuilder.Build(from, to);
        _model = StageGanttModel.From(_tl);
        _selected = null;

        // ★ 欠量口径：拿当日快照与现盘对一遍。没有快照就明说判不了 —— 「判不了」与「没改过」在界面上必须是两句话。
        try
        {
            _drift = PlanDrift.Compare(
                PlanBaseline.For(_anchor.ToString("yyyy-MM-dd")), SampleTaskBoard.Day());
        }
        catch (Exception ex) { _drift = new PlanDriftResult { Headline = $"计划基准取不到（{ex.GetType().Name}）" }; }

        txtSource.Text = _tl.SourceLabel
                       + (!_drift.Comparable ? "　◆ 无计划快照" : (_drift.HasDrift ? "　⚠ 计划已变" : "　✓ 与快照一致"));
        ToolTip.SetTip(txtSource, string.Join("\n\n",
            new[] { _drift.Headline, _drift.BaselineLabel }.Where(x => !string.IsNullOrWhiteSpace(x))
            .Concat(_tl.Notes)));

        RenderGantt();

        if (_model.IsEmpty)
        {
            detailPanel.Children.Clear();
            Title = "生产任务动态调整 — 无可排环节";
            foreach (var n in _tl.Notes) AddNote(detailPanel, n);
        }
    }

    private void RenderGantt()
    {
        try { StageGanttRenderer.Render(gantt, _model, _selected); }
        catch (Exception ex)
        {
            gantt.Children.Clear();
            gantt.Children.Add(new TextBlock { Text = "甘特渲染失败：" + ex.Message, Margin = new Thickness(12) });
        }
    }

    private void ShiftAnchor(int months)
    {
        _anchor = _anchor.AddMonths(months);
        StopSim();
        Reload();
    }

    /// <summary>把锚点拨回当前作业日（翻远了要能一键回来）。</summary>
    private void OnBackToWorkDate()
    {
        try { _anchor = ProjectScope.WorkDate.Date; } catch { _anchor = DateTime.Today; }
        StopSim();
        Reload();
    }

    // ═════════════════════════ 选格 ═════════════════════════

    private void OnGanttClick(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(gantt).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control { Tag: StageGanttCell cell }) return;
        _selected = cell;
        _needFit = true;      // 换格 ⇒ 相机装一次
        RenderGantt();
        ShowDetail(cell);
        ShowSuggest(cell);

        btnPlay.IsEnabled = btnStop.IsEnabled = slProgress.IsEnabled = true;
        // 点开即停在收班时刻（先看结果），要逐班看过程再点播放
        _suppressSlider = true; slProgress.Value = 1; _suppressSlider = false;
        PlayAt(1.0);
    }

    private void ShowDetail(StageGanttCell c)
    {
        detailPanel.Children.Clear();

        var day = _tl.Days.FirstOrDefault(d => d.Date.Date == c.Date.Date);
        AddTitle(detailPanel, $"{c.Date:yyyy-MM-dd} {day?.WeekLabel}　{c.Region}　{c.Process.Label()}");

        // 计划侧与实绩侧各挂各的牌 —— 一条环节可以「计划是推算的、实绩是真录的」
        AddBadge(detailPanel, c.Projected ? "计划：推算（沿用当日盘子结构摊算）" : "计划：当日盘子 · 真任务",
                 c.Projected ? Color.FromRgb(0x85, 0x4F, 0x0B) : Color.FromRgb(0x0F, 0x6E, 0x56));
        if (c.ActualFromLedger)
            AddBadge(detailPanel, "实绩：已落盘班末实绩（actuals/）", Color.FromRgb(0x18, 0x5F, 0xA5));
        if (c.Stages.Any(s => s.ActualOnly))
            AddBadge(detailPanel, "含计划外作业（当日盘子结构里没有这条，由实绩补建）",
                     Color.FromRgb(0x53, 0x4A, 0xB7));

        if (!c.IsWorkday)
        {
            AddNote(detailPanel, "本日在班次日历上没有排班 ⇒ 非作业日，不铺任何环节。");
            return;
        }
        if (!c.HasWork)
        {
            AddNote(detailPanel, "本日该区域没有这道工序。");
            return;
        }

        if (c.IsVolumeProcess)
        {
            string unit = c.Process == ProcessType.Dump ? "m³占容方" : "m³实方";
            AddKv(detailPanel, "计划量", $"{c.TargetVolumeM3:N0} {unit}");
            if (c.RolledInM3 > 1e-6)
                AddKv(detailPanel, "顺延补量", $"＋{c.RolledInM3:N0} ⇒ 合计 {c.PlannedTotalM3:N0} {unit}",
                      Color.FromRgb(0x53, 0x4A, 0xB7));
            if (c.Stages.Any(s => s.CapacityResolved))
            {
                double spare = c.Stages.Sum(s => Math.Max(0, s.SpareCapacityM3 - s.RolledInM3));
                AddKv(detailPanel, "余量能力", $"{spare:N0} {unit}"
                    + $"（编组班产 {c.Stages.Sum(s => s.CapacityM3PerH):0.#} m³/h × 余量工时"
                    + $" {c.Stages.Max(s => s.ShiftSpanH) - c.Stages.Max(s => s.PlannedHours):0.#} h）");
            }
            else
                AddNote(detailPanel, "编组班产/班窗未解出 ⇒ 本环节的余量能力**判不了**，跨天顺延会把它单列。");
            if (c.ActualVolumeM3 > 1e-6)
            {
                double pct = c.ActualVolumeM3 / Math.Max(1e-6, c.TargetVolumeM3) * 100;
                AddKv(detailPanel, "实绩量", $"{c.ActualVolumeM3:N0} {unit}（达成 {pct:0}%）",
                      pct < 95 ? Color.FromRgb(0x99, 0x3C, 0x1D) : (Color?)null);
                double shortfall = c.Stages.Sum(s => s.ShortfallM3);
                if (shortfall > 1e-6)
                {
                    AddKv(detailPanel, "欠量", $"{shortfall:N0} m³　需回摊",
                          Color.FromRgb(0x99, 0x3C, 0x1D));
                    // 欠量 = 计划 − 实绩；计划量是活的 ⇒ 必须说清楚这个差是对着哪一份计划算的
                    if (!_drift.Comparable)
                        AddNote(detailPanel, "◆ 这一天**没有计划快照** ⇒ 这个欠量对的是**现算的计划**："
                                           + "台账一改它就跟着变，改小了欠量会自己消失。"
                                           + "到「生产任务编制」保存一次本日计划即可。");
                    else if (_drift.HasDrift)
                        AddNote(detailPanel, "⚠ " + _drift.Headline, Color.FromRgb(0x99, 0x3C, 0x1D));
                }
            }
        }
        else
        {
            AddKv(detailPanel, "工序", $"{c.Process.Label()}（非量型：按有没有排来画，不谈体积）");
        }

        foreach (var s in c.Stages.OrderBy(s => s.StartHour))
        {
            string span = $"{Hm(s.StartHour)}–{Hm(s.EndHour)}";
            string line = $"{(s.Shift.Length > 0 ? s.Shift + "　" : "")}{span}";
            if (s.MainEquip.Length > 0) line += $"　{s.MainEquip}";
            if (s.EquipCount > 1) line += $"（含配属共 {s.EquipCount} 台）";
            AddKv(detailPanel, "班次", line);
        }

        double actH = c.Stages.Sum(s => s.ActualHours), fltH = c.Stages.Sum(s => s.FaultHours);
        if (actH > 1e-6 || fltH > 1e-6)
        {
            bool fromShiftLedger = c.Stages.Any(s => s.HoursFromShiftLedger);
            AddKv(detailPanel, "工时", $"实绩 {actH:0.#} h"
                + (fltH > 1e-6 ? $"　故障 {fltH:0.#} h" : "")
                + (fromShiftLedger ? "　（班次台账）" : "　（落盘实绩）"),
                  fltH > 1e-6 ? Color.FromRgb(0x99, 0x3C, 0x1D) : (Color?)null);
            if (fromShiftLedger)
                AddNote(detailPanel, "工时来自班次台账 production_record —— **这一列是真的**；"
                                   + "但同一张表的产量列口径未确认，本窗一概不取，所以上面那个量仍是计划/推算值。");
        }

        var dest = c.Stages.Select(s => s.DestinationName).Where(x => x.Length > 0).Distinct().ToList();
        if (dest.Count > 0) AddKv(detailPanel, "去向", string.Join("、", dest));
        var mat = c.Stages.Select(s => s.MaterialLabel).Where(x => x.Length > 0).Distinct().ToList();
        if (mat.Count > 0) AddKv(detailPanel, "物料", string.Join("、", mat));
        double bench = c.Stages.Select(s => s.BenchElevationM).FirstOrDefault(z => Math.Abs(z) > 1e-6);
        if (Math.Abs(bench) > 1e-6) AddKv(detailPanel, "台阶标高", $"{bench:0.##} m");

        if (day != null)
        {
            if (day.HasBlast) AddNote(detailPanel, "本日班次日历标了**爆破班**。");
            if (day.Weather.Length > 0) AddNote(detailPanel, $"天气：{day.Weather}");
            AddNote(detailPanel, $"本日全矿计划 {day.PlanM3 / 1e4:0.##} 万m³"
                               + (day.ActualM3 > 1e-6 ? $"　实绩 {day.ActualM3 / 1e4:0.##} 万m³" : ""));
        }
    }

    // ═════════════════════════ 演示 ═════════════════════════

    private void PlayAt(double progress)
    {
        if (_selected == null) return;

        SyncLabelHeight();
        var info = _player.Play(_selected, _tl, progress);

        // 换格后装一次相机。**装完要再解一遍**：铭牌字高是按相机尺度反推的，装满之前那一帧用的是旧尺度。
        if (_needFit && InPanel && info.HasFocusBox && FitTo(info))
        {
            _needFit = false;
            SyncLabelHeight();
            info = _player.Play(_selected, _tl, progress);
        }
        UpdateViewInfo(info);

        // 进度条走的是**当日时钟**（从最早开班到最晚收班），不是完成度 —— 文案要跟着说时刻
        txtProgress.Text = info.Shifts.Count > 0
            ? info.ClockText + (info.ActiveShift.Length > 0 ? $"　{info.ActiveShift}" : "　空档")
            : (progress >= 1 ? "期末" : progress <= 0 ? "期初" : $"{progress * 100:0}%");

        simPanel.Children.Clear();
        AddTitle(simPanel, info.Headline);

        if (!info.Ok && !info.RegionMatched)
            AddBadge(simPanel, "未画出推进带", Color.FromRgb(0xA3, 0x2D, 0x2D));

        // ── 一天里的三班状态：当前在哪个班、走到几点、各班干多少 ──
        if (info.Shifts.Count > 0)
        {
            AddKv(simPanel, "当日时钟", info.ClockText
                + (info.ActiveShift.Length > 0
                    ? $"　在班：{info.ActiveShift}（本班 {info.ActiveShiftProgress * 100:0}%）"
                    : "　班间空档（不排班，无推进）"),
                  info.ActiveShift.Length > 0 ? (Color?)null : Color.FromRgb(0x88, 0x88, 0x88));

            foreach (var s in info.Shifts)
            {
                bool on = info.ClockHour >= s.StartHour && info.ClockHour < s.EndHour;
                bool passed = info.ClockHour >= s.EndHour;
                var c = s.IsAbnormal ? Color.FromRgb(0x99, 0x3C, 0x1D)
                      : on ? Color.FromRgb(0x18, 0x5F, 0xA5)
                      : (Color?)null;
                string mark = on ? "▶ " : passed ? "✓ " : "· ";
                AddKv(simPanel, mark.Trim(), s.Caption
                    + (s.ActualM3 > 1e-6 ? $"　实绩 {s.ActualM3:N0}" : "")
                    + (s.FaultHours > 1e-6 ? $"　故障 {s.FaultHours:0.#}h" : "")
                    + (s.IsAbnormal ? "　⚠ " + string.Join("、", s.Reasons
                        .Where(r => r != IncompleteReason.OverAchieved).Select(r => r.Label())) : ""),
                    c);
            }
        }

        foreach (var n in info.Notes) AddNote(simPanel, n);
    }

    private void OnPlay()
    {
        if (_selected == null) return;
        _playing = true;
        _playStart = DateTime.UtcNow;
        _timer.Start();
        btnPlay.IsEnabled = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_playing) { _timer.Stop(); return; }
        double t = (DateTime.UtcNow - _playStart).TotalSeconds / PlaySeconds;
        if (t >= 1) { t = 1; _playing = false; _timer.Stop(); btnPlay.IsEnabled = true; }

        _suppressSlider = true; slProgress.Value = t; _suppressSlider = false;
        PlayAt(t);      // 进度文案由 PlayAt 按当日时钟写
    }

    private void OnProgressChanged(double newValue)
    {
        if (_suppressSlider || _selected == null) return;
        _playing = false; _timer.Stop(); btnPlay.IsEnabled = true;
        PlayAt(newValue);
    }

    private void StopSim()
    {
        _playing = false;
        _timer.Stop();
        try { _player.Stop(); } catch { }
        btnPlay.IsEnabled = _selected != null;
        UpdateViewInfo(null);
    }

    // ══════════════════ 窗内面板：落地端切换 / 相机 / 状态 ══════════════════

    /// <summary>铭牌字高（世界米）跟着相机尺度走：按尺度反推成恒定 10 px，不设世界米上限（落地端自己还有 42px 上限兜着）。</summary>
    private void SyncLabelHeight()
    {
        if (!InPanel) { _player.LabelHeightM = 4.5; return; }   // 主视口画面大，按原口径
        double sc = _panel.Camera.Scale;
        _player.LabelHeightM = sc > 1e-9 ? Math.Max(4.5, 10.0 / sc) : 4.5;
    }

    /// <summary>把本格的焦点包围盒装进面板。面板还没布局（宽高为 0）时返回 false —— 这次装不了。</summary>
    private bool FitTo(StagePlayInfo info)
    {
        if (_panel.Camera.Width < 2 || _panel.Camera.Height < 2) return false;
        if (!info.HasFocusBox) return false;
        double z = double.IsNaN(info.CenterZ) ? 0 : info.CenterZ;
        // Z 向给一点余量：环是同一标高的平面环，minZ==maxZ 时轴测下投影跨度为 0。
        _panel.Camera.FitTo(info.FocusMinX, info.FocusMinY, z - 20,
                            info.FocusMaxX, info.FocusMaxY, z + 20);
        _panel.RequestRender();
        return true;
    }

    private void OnRenderTargetChanged()
    {
        // SwitchSink 会先抹掉旧端 —— 不抹的话旧端留一张不再更新的残影，比"没画"更坏。
        _player.SwitchSink(InPanel ? _panel : SimDynamicOverlay.Current);
        _needFit = InPanel;
        _photo.PushTo(_panel, InPanel, PanelPhotoZ());

        btnFit.IsEnabled = btnTop.IsEnabled = btnIso.IsEnabled = InPanel;
        if (_selected != null) PlayAt(slProgress.Value);
        else { ShowHint(simPanel, _player.StatusLabel); UpdateViewInfo(null); }
        _panel.RequestRender();
    }

    private void OnFitView()
    {
        if (!InPanel || _selected == null) return;
        _needFit = true;
        PlayAt(slProgress.Value);
    }

    private void SetView(double tilt, double az)
    {
        _panel.Camera.TiltDeg = tilt;
        _panel.Camera.AzimuthDeg = az;
        if (_selected != null) { _needFit = true; PlayAt(slProgress.Value); }
        else _panel.RequestRender();
    }

    /// <summary>展开 / 还原：临时把甘特行收掉，整窗让给演示（面板只有百来像素时看不出形态）。</summary>
    private void OnToggleExpand()
    {
        bool expanded = rowGantt.Height.Value <= 0.001 && rowGantt.Height.IsAbsolute;
        if (!expanded)
        {
            _ganttKeep = rowGantt.Height; _ganttKeepMin = rowGantt.MinHeight;
            rowGantt.MinHeight = 0;                       // ★ 先松 MinHeight，否则高度压不下去
            rowGantt.Height = new GridLength(0);
            rowBottom.Height = new GridLength(1, GridUnitType.Star);
            btnExpand.Content = "还原";
        }
        else
        {
            rowGantt.Height = _ganttKeep.Value > 0 ? _ganttKeep : new GridLength(1, GridUnitType.Star);
            rowGantt.MinHeight = _ganttKeepMin > 0 ? _ganttKeepMin : 240;
            rowBottom.Height = new GridLength(360);
            btnExpand.Content = "展开";
        }
        if (_selected != null) { _needFit = true; Dispatcher.UIThread.Post(() => PlayAt(slProgress.Value), DispatcherPriority.Loaded); }
    }

    /// <summary>面板状态行：画出来多少段/字，或者说清楚"图不在这儿，在主视口"。</summary>
    private void UpdateViewInfo(StagePlayInfo? info)
    {
        if (!InPanel)
        {
            txtViewInfo.Text = "图元已推到主三维视口 —— 本窗不会自动飞过去，请自行转到区域中心坐标。";
            return;
        }
        if (info == null) { txtViewInfo.Text = "面板：空（点一格即画）"; return; }
        string bm = _photo.IsLoaded ? (_panel.DrawnRasters > 0 ? "　底图已铺" : "　底图未铺（不在视野内）") : "";
        txtViewInfo.Text = $"面板：{_panel.DrawnSegments} 段 / {_panel.DrawnMarkers} 点 / {_panel.DrawnLabels} 字"
                         + $"　1 px ≈ {(_panel.Camera.Scale > 1e-9 ? 1 / _panel.Camera.Scale : 0):0.#} m" + bm;
    }

    /// <summary>面板底图铺在哪个高程面上：跟着本格区域走（俯视下无所谓，轴测下差一个台阶就露馅）。</summary>
    private double PanelPhotoZ()
    {
        try
        {
            var r = _player.Regions.Regions.FirstOrDefault(x => !double.IsNaN(x.Z));
            return r == null ? 0 : r.Z;
        }
        catch { return 0; }
    }

    /// <summary>工程里配过影像就把面板底图铺上（读不到不吭声 —— 主视口那条会把原因说全）。</summary>
    private void TryLoadPanelBasemap()
    {
        try
        {
            string path = Shading.OrthophotoConfig.ResolvePath(out _);
            if (path.Length == 0 || !_photo.Load(path)) return;
            _photo.PushTo(_panel, InPanel, PanelPhotoZ());
            _panel.RequestRender();
        }
        catch { }
    }

    // ═════════════════════════ 正射底图 ═════════════════════════

    private async void OnLoadBasemap()
    {
        // 先用工程配置里的影像（「基础数据 · 影像底图」配一次即可）。配了却读不到时把原因带进提示，再退回选文件。
        string path = Shading.OrthophotoConfig.ResolvePath(out string why);
        if (path.Length == 0)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择正射影像（需带 GeoTIFF 配准标签或同名 .tfw 世界文件）",
                AllowMultiple = false,
                FileTypeFilter = FilterOf(OrthophotoBasemap.FileFilter),
            });
            if (files.Count == 0) return;
            path = files[0].Path.LocalPath;
            why = "本次选择（已记为工程配置）";
        }

        Cursor = new Cursor(StandardCursorType.Wait);
        BasemapResult res;
        try
        {
            res = OrthophotoBasemap.Load(path, _player.Regions);
            // 同一张影像铺两处：主视口走内核纹理，窗内面板走自己的栅格层。两条路各读各的。
            if (_photo.Load(path)) _photo.PushTo(_panel, InPanel, PanelPhotoZ());
        }
        finally { Cursor = Cursor.Default; }

        if (res.Ok) res.Notes.Insert(0, why);
        txtBasemap.Text = res.Message;
        ToolTip.SetTip(txtBasemap, string.Join("\n\n", res.Notes));
        txtBasemap.Foreground = new SolidColorBrush(res.Ok
            ? Color.FromRgb(0x0F, 0x6E, 0x56) : Color.FromRgb(0xA3, 0x2D, 0x2D));
        btnClearBasemap.IsEnabled = res.Ok;

        simPanel.Children.Clear();
        AddTitle(simPanel, res.Ok ? "正射底图已贴" : "正射底图未贴上");
        if (_photo.IsLoaded) AddNote(simPanel, "窗内面板：" + _photo.Message);
        AddNote(simPanel, res.Message);
        foreach (var n in res.Notes) AddNote(simPanel, n);

        // 底图换了，覆盖范围提示要跟着重算
        if (_selected != null) PlayAt(slProgress.Value);
    }

    /// <summary>WPF 文件过滤串（"名|*.tif;*.tiff|…"）→ Avalonia 文件类型。</summary>
    private static List<FilePickerFileType> FilterOf(string wpfFilter)
    {
        var list = new List<FilePickerFileType>();
        var parts = wpfFilter.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
            list.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries) });
        return list;
    }

    private void OnClearBasemap()
    {
        string msg = OrthophotoBasemap.Clear();
        txtBasemap.Text = "未装底图（演示画在素色地表上）";
        ToolTip.SetTip(txtBasemap, null);
        TaskUi.Theme(txtBasemap, TextBlock.ForegroundProperty, "Theme.Text.Body");
        txtBasemap.Opacity = 0.8;
        btnClearBasemap.IsEnabled = false;
        _photo.Clear();
        _photo.PushTo(_panel, false);
        _panel.RequestRender();
        if (_selected != null) PlayAt(slProgress.Value); else ShowHint(simPanel, msg);
    }

    // ═════════════════════════ 原因码路由 + 滚动重排 ═════════════════════════

    private void ShowSuggest(StageGanttCell c)
    {
        suggestPanel.Children.Clear();

        var reasons = c.Stages.SelectMany(s => s.Reasons)
                              .Where(r => r != IncompleteReason.OverAchieved)
                              .Distinct().ToList();
        if (reasons.Count == 0)
        {
            ShowHint(suggestPanel, c.Projected
                ? "推算天不带原因码 —— 只有作业日那一天的真任务才有实绩与偏差。\n选中作业日那一列（红框）里带 ⚠ 的格子看建议。"
                : "本环节无异常原因码。");
            return;
        }

        AddTitle(suggestPanel, $"{c.Region} · {c.Process.Label()}　{c.Date:MM-dd}");
        double shortfall = c.Stages.Sum(s => s.ShortfallM3);
        if (shortfall > 1e-6)
        {
            AddNote(suggestPanel, $"欠量 {shortfall:N0} m³，需回摊", Color.FromRgb(0x99, 0x3C, 0x1D));
            if (!_drift.Comparable)
                AddNote(suggestPanel, "◆ 无当日计划快照 ⇒ 欠量对的是现算的计划（台账一改就变）。");
            else if (_drift.HasDrift)
                AddNote(suggestPanel, "⚠ 计划自快照以来动过 ⇒ 欠量与快照不是同一把尺子。",
                        Color.FromRgb(0x99, 0x3C, 0x1D));
        }

        foreach (var reason in reasons)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = reason.Label(), FontWeight = FontWeight.Bold, FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x79, 0x1F, 0x1F)),
            });
            sp.Children.Add(Body(new TextBlock
            {
                Text = Route.TryGetValue(reason, out var a) ? a : "（路由待补）",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
            }));
            sp.Children.Add(new TextBlock
            {
                Text = $"重排策略：{StratLabel(TaskRescheduler.StrategyFor(reason))}",
                FontSize = 11, Foreground = Sec(), Margin = new Thickness(0, 4, 0, 0),
            });
            card.Child = sp;
            suggestPanel.Children.Add(card);
        }
    }

    /// <summary>重排起点：选了某个班就从该班班初起，但<b>不早于此刻</b>；「全部」＝从此刻起。</summary>
    private double RescheduleFrom()
    {
        double now = SampleTaskBoard.NowHour;
        var w = ShiftScope.Window(ShiftSelector.Selected(cbOrigin));
        return w == null ? now : Math.Max(now, w.Start);
    }

    private void ShowOriginHint()
    {
        double from = RescheduleFrom();
        string sel = ShiftSelector.Selected(cbOrigin);
        var w = ShiftScope.Window(sel);
        ToolTip.SetTip(cbOrigin, w == null
            ? $"从此刻 {DispatchClock.Hm(from)} 起重排当天剩余"
            : (from > w.Start + 1e-6
                ? $"{sel} 已经开始了：起点取此刻 {DispatchClock.Hm(from)}，不是班初 {DispatchClock.Hm(w.Start)}"
                : $"从 {sel} 班初 {DispatchClock.Hm(from)} 起重排"));
    }

    /// <summary>应用建议：对<b>作业日当天</b>的全盘异常面按原因码选策略，跑 TaskRescheduler 真重排。只动作业日那一天。</summary>
    private async void OnApply()
    {
        List<ProductionTask> tasks;
        ExploderConfig cfg;
        double from;
        try
        {
            tasks = SampleTaskBoard.Day();
            cfg = SampleTaskBoard.Config();
            from = RescheduleFrom();
        }
        catch (Exception ex)
        {
            await TaskUi.Info(this, "动态调整", "当日盘子不可读：" + ex.Message);
            return;
        }

        var zoneStrategy = tasks
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
            .Where(t => t.Reasons.Any(x => x != IncompleteReason.OverAchieved))
            .GroupBy(t => t.WorkZone)
            .ToDictionary(g => g.Key, g => TaskRescheduler.StrategyFor(TopReason(g.SelectMany(t => t.Reasons))));

        if (zoneStrategy.Count == 0)
        {
            await TaskUi.Info(this, "动态调整", "作业日当天没有异常任务可重排。");
            return;
        }

        var result = TaskRescheduler.Reschedule(cfg, tasks, from, zoneStrategy);
        RenderResult(result);

        // 重排改的是作业日那天的任务台账 ⇒ 时间轴与甘特要跟着重建
        StopSim();
        Reload();
    }

    private void RenderResult(RescheduleResult res)
    {
        suggestPanel.Children.Clear();
        suggestPanel.Children.Add(new TextBlock
        {
            Text = "✓ 已滚动重排", FontWeight = FontWeight.Bold, FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)),
            Margin = new Thickness(0, 0, 0, 4),
        });
        suggestPanel.Children.Add(new TextBlock
        {
            Text = res.Summary, TextWrapping = TextWrapping.Wrap,
            Foreground = Sec(), Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var n in res.Notes)
            suggestPanel.Children.Add(Body(new TextBlock
            {
                Text = "• " + n, TextWrapping = TextWrapping.Wrap,
                FontSize = 12, Margin = new Thickness(0, 0, 0, 3),
            }));

        var newTasks = res.Plan.Tasks
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump && t.TargetVolumeM3 > 0)
            .OrderBy(t => t.StartHour).ToList();
        if (newTasks.Count > 0)
        {
            suggestPanel.Children.Add(Body(new TextBlock
            {
                Text = "重排后计划", FontWeight = FontWeight.Bold, FontSize = 13,
                Margin = new Thickness(0, 10, 0, 4),
            }));
            foreach (var t in newTasks)
                suggestPanel.Children.Add(Body(new TextBlock
                {
                    Text = $"{t.Shift} · {t.WorkZone} · {t.Group.MainEquipment} → {t.TargetVolumeM3:0} m³（{t.PlannedHours:0.#} h）",
                    FontSize = 12, Margin = new Thickness(0, 0, 0, 2),
                }));
        }

        foreach (var v in res.Plan.Violations)
        {
            bool err = v.Severity == ViolationSeverity.Error;
            suggestPanel.Children.Add(new TextBlock
            {
                Text = $"⚠ {v.Code}：{v.Message}", TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Foreground = new SolidColorBrush(err ? Color.FromRgb(0xC6, 0x28, 0x28) : Color.FromRgb(0x99, 0x6A, 0x1D)),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    // ═════════════════════════ 跨天顺延 ═════════════════════════

    /// <summary>跨天顺延：把作业日当天的欠量按后续作业日的**余量能力**逐日装下去。与「日内重排」是两层，先日内后日间。</summary>
    private async void OnRollover()
    {
        if (_tl.Days.Count == 0) { ShowHint(suggestPanel, "时间轴为空。"); return; }

        int from = _tl.ActualDayIndex;
        if (from < 0)
        {
            await TaskUi.Info(this, "跨天顺延",
                "当前作业日不在本区间内 ⇒ 没有任何一天带真实绩，也就无从谈起「欠了多少」。\n"
                + "把区间切到包含作业日的那一段再试。");
            return;
        }

        var strategy = cbStrategy.SelectedIndex == 1
            ? RolloverStrategy.Even : RolloverStrategy.EarliestFirst;

        DayRollover.Clear(_tl);                 // 重算前先清，避免反复点击累加
        var res = DayRollover.Plan(_tl, from, strategy);
        DayRollover.Apply(_tl, res);

        // 顺延量改了计划 ⇒ 甘特要按新模型重画（选中格失效，重新定位到同一坐标）
        var keep = _selected;
        _model = StageGanttModel.From(_tl);
        _selected = keep == null ? null : _model.Groups.SelectMany(g => g.Lanes).SelectMany(l => l.Cells)
            .FirstOrDefault(c => c.Date == keep.Date && c.Process == keep.Process
                              && string.Equals(c.Region, keep.Region, StringComparison.OrdinalIgnoreCase));
        RenderGantt();

        RenderRollover(res);
        if (_selected != null) { ShowDetail(_selected); PlayAt(slProgress.Value); }
    }

    private void RenderRollover(RolloverResult res)
    {
        suggestPanel.Children.Clear();

        bool clean = res.Ok && res.OverflowM3 <= 1e-6 && res.UnknownM3 <= 1e-6 && res.ShortfallM3 > 1e-6;
        suggestPanel.Children.Add(new TextBlock
        {
            Text = clean ? "✓ 欠量已全部顺延" : (res.ShortfallM3 <= 1e-6 ? "无欠量" : "⚠ 顺延未能全部消化"),
            FontWeight = FontWeight.Bold, FontSize = 15,
            Foreground = new SolidColorBrush(clean ? Color.FromRgb(0x1D, 0x9E, 0x75)
                                                  : Color.FromRgb(0x99, 0x6A, 0x1D)),
            Margin = new Thickness(0, 0, 0, 4),
        });
        suggestPanel.Children.Add(new TextBlock
        {
            Text = res.Summary, TextWrapping = TextWrapping.Wrap,
            Foreground = Sec(), Margin = new Thickness(0, 0, 0, 8),
        });

        if (res.Assignments.Count > 0)
        {
            AddTitle(suggestPanel, "逐笔顺延");
            foreach (var a in res.Assignments.OrderBy(x => x.ToDate).Take(40))
                suggestPanel.Children.Add(Body(new TextBlock
                {
                    Text = "• " + a.Caption, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2),
                }));
            if (res.Assignments.Count > 40)
                AddNote(suggestPanel, $"（另有 {res.Assignments.Count - 40} 笔未列出）");
        }

        foreach (var n in res.Notes) AddNote(suggestPanel, n);
    }

    private void OnClearRollover()
    {
        DayRollover.Clear(_tl);
        var keep = _selected;
        _model = StageGanttModel.From(_tl);
        _selected = keep == null ? null : _model.Groups.SelectMany(g => g.Lanes).SelectMany(l => l.Cells)
            .FirstOrDefault(c => c.Date == keep.Date && c.Process == keep.Process
                              && string.Equals(c.Region, keep.Region, StringComparison.OrdinalIgnoreCase));
        RenderGantt();
        ShowHint(suggestPanel, "顺延量已清除，计划回到原始状态。");
        if (_selected != null) ShowDetail(_selected);
    }

    /// <summary>多原因取优先级最高者（结构性优先：故障 > 运力 > 缺料 > 天气/缺勤 > 过满 > 其它）。</summary>
    private static IncompleteReason TopReason(IEnumerable<IncompleteReason> reasons)
    {
        var order = new[]
        {
            IncompleteReason.Fault, IncompleteReason.TruckShortage, IncompleteReason.OreShortage,
            IncompleteReason.Weather, IncompleteReason.Absence, IncompleteReason.OverPlanned,
            IncompleteReason.ProcessWait, IncompleteReason.RoadCongestion, IncompleteReason.Maintenance,
        };
        var set = reasons.Where(r => r != IncompleteReason.OverAchieved).ToHashSet();
        return order.FirstOrDefault(set.Contains);
    }

    private static string StratLabel(AdjustStrategy s) => s switch
    {
        AdjustStrategy.AddTrucks => "补车到推荐车数",
        AdjustStrategy.ReassignBackup => "备机顶替主设备",
        AdjustStrategy.ReduceCapacity => "全盘降效回摊",
        AdjustStrategy.SwitchFace => "换面（转有料面）",
        AdjustStrategy.ReduceTarget => "削目标到产能上限",
        _ => "剩余量顺延",
    };

    // ═════════════════════════ 小部件 ═════════════════════════

    private static void ShowHint(Panel p, string text)
    {
        p.Children.Clear();
        p.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12.5, Foreground = Sec(), TextWrapping = TextWrapping.Wrap,
        });
    }

    private static void AddTitle(Panel p, string text)
        => p.Children.Add(Body(new TextBlock
        {
            Text = text, FontWeight = FontWeight.Bold, FontSize = 14,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        }));

    private static void AddBadge(Panel p, string text, Color c)
    {
        p.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(c) },
        });
    }

    private static void AddKv(Panel p, string k, string v, Color? valueColor = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 3), ColumnDefinitions = new ColumnDefinitions("64,*") };
        var kt = new TextBlock { Text = k, FontSize = 12, Foreground = Sec() };
        var vt = Body(new TextBlock { Text = v, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (valueColor != null)
        {
            vt.Foreground = new SolidColorBrush(valueColor.Value);
            vt.FontWeight = FontWeight.SemiBold;
        }
        Grid.SetColumn(kt, 0); Grid.SetColumn(vt, 1);
        g.Children.Add(kt); g.Children.Add(vt);
        p.Children.Add(g);
    }

    private static void AddNote(Panel p, string text, Color? color = null)
        => p.Children.Add(new TextBlock
        {
            Text = "• " + text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = color != null ? new SolidColorBrush(color.Value) : Sec(),
            Margin = new Thickness(0, 0, 0, 4),
        });

    /// <summary>正文色跟主题（原 WPF 继承窗体 Foreground；Avalonia 代码建的 TextBlock 要显式挂）。</summary>
    private static TextBlock Body(TextBlock t) { TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Body"); return t; }

    private static string Hm(double h)
    {
        int hh = (int)h; int mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }

    private static IBrush Sec() => new SolidColorBrush(Color.FromArgb(0xAA, 0x88, 0x88, 0x88));
}
