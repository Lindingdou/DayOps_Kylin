// 忠实移植自原 PitMine3D Modules/TaskLib/Order/TaskOrderWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局；
// 打印 → QuestPDF 出同版式 PDF（Avalonia 无打印对话框，PDF 打印机/查看器打印即原「打印 / 导出 PDF」）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Gantt;   // NeedsDestination / AllMaterialsRouted：与甘特、下达闸门共用同一条缺去向口径

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 生产任务书窗口：按班次从共用任务台账（SampleTaskBoard）生成正式任务书，可打印/导出 PDF。
/// 响应立项条款(13)：自动生成作业任务书、下达至班组/设备。
///
/// 单据的命门是「从哪采 → 拉到哪」：作业地点旁必须紧跟卸载地点，计划量必须给出
/// 实方（作业口径）与吨量（考核口径）双口径，抬头要有运输功与加权平均运距。缺卸点的任务不得下达。
///
/// <para><b>本窗只出单据，不签发。</b>签发唯一入口是「任务下达」，落款处只如实回显那边的结果，见 <see cref="RefreshIssueState"/>。</para>
/// </summary>
public sealed class TaskOrderWindow : Window
{
    private readonly string _date = SampleTaskBoard.DateLabel;
    private List<ProductionTask> _tasks = SampleTaskBoard.Day();
    private List<ProductionTask> _shiftTasks = new();

    public sealed class OrderRow
    {
        public int No { get; set; }
        public string Group { get; set; } = "";
        public string Place { get; set; } = "";
        /// <summary>采掘单元号（作业地点第二行小字）——"点这条任务，图上该亮哪个体"。空 = 该面没绑单元。</summary>
        public string Unit { get; set; } = "";
        public bool HasUnit => Unit.Length > 0;
        public string Destination { get; set; } = "";
        public string Process { get; set; } = "";
        public string Material { get; set; } = "";
        /// <summary>计划量（<b>按工序取自己那本账</b>，见 <see cref="TaskQuantity"/>）。没有出处写「—」，不写 0。</summary>
        public string Plan { get; set; } = "";
        /// <summary>量口径名（控制方量 / 原位实方 / 承运量 / 排弃占容；爆破为"—"）。</summary>
        public string Basis { get; set; } = "";
        /// <summary>量的悬停说明（含"为什么没有这个数"）。</summary>
        public string PlanTip { get; set; } = "";
        public string Haul { get; set; } = "";
        public string Quality { get; set; } = "";
        public string Span { get; set; } = "";
        /// <summary>作业人员（操作手＋司机），来自「班组派工」。没派工留"—"，不编名字。</summary>
        public string Crew { get; set; } = "";
        /// <summary>缺卸点（该行标红、阻止签发）。混采面缺任一物料去向即为真。</summary>
        public bool NoDestination { get; set; }
        /// <summary>混采任务的完整分项去向摘要；单去向为空串。</summary>
        public string Splits { get; set; } = "";
        public bool HasSplits => Splits.Length > 0;
        /// <summary>卸载地点单元格的悬停提示（多去向时列全，主行只写主去向）。</summary>
        public string DestinationTip { get; set; } = "";
    }

    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
    private static readonly IBrush InkDim = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly IBrush RedInk = new SolidColorBrush(Color.FromRgb(0xA3, 0x14, 0x14));
    private static readonly IBrush RedBg = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC));

    private readonly ComboBox shiftCombo = new() { Width = 110 };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly Border docPaper;
    private readonly TextBlock docMine = new() { Text = "示例露天矿", FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock docMeta = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock docMetrics = new() { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
    private readonly DataGrid grid;
    private readonly TextBlock docSummary = new() { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border docNoSinkBox;
    private readonly TextBlock docNoSinkList = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x14, 0x14)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock docIssue = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private List<OrderRow> _rows = new();
    private bool _loaded;

    public TaskOrderWindow()
    {
        Title = "生产任务书 — 日常生产组织";
        TaskUi.Place(this, 1240, 760);

        var header = TaskUi.Header("生产任务书", "按班次生成正式任务书 · 从哪采→拉到哪 · 可打印 / 导出 PDF（签发下达在「任务下达」）");

        var tool = new DockPanel { LastChildFill = true };
        var lbl = TaskUi.Lbl("班次"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left); tool.Children.Add(lbl);
        // 任务书一班一份，故不给「全部」——给了就会印出一张跨班的单据
        DockPanel.SetDock(shiftCombo, Avalonia.Controls.Dock.Left); tool.Children.Add(shiftCombo);
        var print = TaskUi.Btn("🖨 打印 / 导出 PDF", OnPrint, 130); print.Margin = new Thickness(0);
        ToolTip.SetTip(print, "横向纸张 + 按纸面缩放；新增的卸载地点 / 运距 / 吨量列一并打印（出 PDF，用查看器打印）");
        DockPanel.SetDock(print, Avalonia.Controls.Dock.Right); tool.Children.Add(print);
        // 「签发」按钮已撤：签发唯一入口收归「任务下达」
        var refresh = TaskUi.Btn("刷新", OnRefresh, 70); refresh.Margin = new Thickness(0, 0, 10, 0);
        ToolTip.SetTip(refresh, "重读计划与「任务下达」的落盘单据（编制参数改过之后要点一下）");
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Right); tool.Children.Add(refresh);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        tool.Children.Add(toolStatus);

        // 纸面：白纸黑字，不跟随深/浅主题
        grid = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserSortColumns = false, CanUserReorderColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.All,
            Background = Brushes.White, Foreground = Ink, BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), BorderThickness = new Thickness(1),
            FontSize = 11.5, HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            RowBackground = Brushes.White, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        grid.Resources["DataGridSortIconMinWidth"] = 0.0;
        var hs = new Style(x => x.OfType<DataGrid>().Descendant().OfType<DataGridColumnHeader>());
        hs.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1))));
        hs.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, Ink));
        hs.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeight.SemiBold));
        hs.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(4, 4)));
        hs.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 11.5));
        grid.Styles.Add(hs);
        var cs = new Style(x => x.OfType<DataGrid>().Descendant().OfType<DataGridCell>());
        cs.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Ink));
        cs.Setters.Add(new Setter(DataGridCell.FontSizeProperty, 11.5));
        grid.Styles.Add(cs);
        // 缺去向的行标红：任务书是正式单据，缺去向不能签发
        grid.LoadingRow += (_, e) =>
        {
            bool bad = e.Row.DataContext is OrderRow r && r.NoDestination;
            e.Row.Background = bad ? RedBg : Brushes.White;
            e.Row.Foreground = bad ? RedInk : Ink;
            e.Row.FontWeight = bad ? FontWeight.SemiBold : FontWeight.Normal;
        };
        BuildColumns();

        var paperSp = new StackPanel();
        paperSp.Children.Add(docMine);
        paperSp.Children.Add(new TextBlock { Text = "生产任务书", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10) });
        paperSp.Children.Add(docMeta);
        // 抬头指标条：调度签发时判断合理性的关键量（吨量口径 + 运输功 + 加权平均运距）
        paperSp.Children.Add(docMetrics);
        paperSp.Children.Add(grid);
        paperSp.Children.Add(docSummary);
        // 缺卸点告警：不得下达的任务清单
        var noSink = new StackPanel();
        noSink.Children.Add(new TextBlock { Text = "以下任务未指定卸点，不得下达：", FontWeight = FontWeight.Bold, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x14, 0x14)) });
        noSink.Children.Add(docNoSinkList);
        docNoSinkBox = new Border { IsVisible = false, Background = RedBg, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA5, 0xA5)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7), Margin = new Thickness(0, 10, 0, 0), Child = noSink };
        paperSp.Children.Add(docNoSinkBox);
        var sign = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), Margin = new Thickness(0, 24, 0, 0) };
        string[] signs = { "编制：________", "班长：________", "调度：________", "值班矿长：________" };
        for (int i = 0; i < 4; i++) { var t = new TextBlock { Text = signs[i], Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) }; Grid.SetColumn(t, i); sign.Children.Add(t); }
        paperSp.Children.Add(sign);
        // 落款区：签发状态 + 运输口径复述
        paperSp.Children.Add(docIssue);
        docPaper = new Border { Background = Brushes.White, Padding = new Thickness(26, 20), MaxWidth = 1080, HorizontalAlignment = HorizontalAlignment.Center, BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), BorderThickness = new Thickness(1), Child = paperSp };
        var scroll = new ScrollViewer { Content = docPaper, Padding = new Thickness(16), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(scroll, 2);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(scroll);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        docMine.Text = SampleTaskBoard.MineName;
        shiftCombo.SelectionChanged += (_, _) => Rebuild();
        Opened += (_, _) =>
        {
            // 按当日班制填，默认落**当前班**
            ShiftSelector.Bind(shiftCombo, includeAll: false);
            _loaded = true;
            Rebuild();
        };
    }

    private void BuildColumns()
    {
        var c = grid.Columns;
        c.Add(Col("序", nameof(OrderRow.No), new DataGridLength(28), TextAlignment.Center));
        c.Add(Col("设备编组", nameof(OrderRow.Group), new DataGridLength(1.7, DataGridLengthUnitType.Star), wrap: true));
        // 作业地点：主行是面名＋台阶标高，第二行小字给采掘单元号
        c.Add(TwoLine("作业地点", nameof(OrderRow.Place), nameof(OrderRow.Unit), nameof(OrderRow.HasUnit), new DataGridLength(1.15, DataGridLengthUnitType.Star), null));
        // 卸载地点：主行写主去向，混采面在第二行小字列出全部物料分项，单元格 ToolTip 给到量与逐条运距
        c.Add(TwoLine("卸载地点", nameof(OrderRow.Destination), nameof(OrderRow.Splits), nameof(OrderRow.HasSplits), new DataGridLength(1.6, DataGridLengthUnitType.Star), nameof(OrderRow.DestinationTip)));
        c.Add(Col("工序", nameof(OrderRow.Process), new DataGridLength(42), TextAlignment.Center));
        c.Add(Col("物料", nameof(OrderRow.Material), new DataGridLength(64), wrap: true));
        // 计划量：按工序取自己那本账；量口径单独一列
        c.Add(Col("计划量", nameof(OrderRow.Plan), new DataGridLength(94), TextAlignment.Right, wrap: true, tipPath: nameof(OrderRow.PlanTip)));
        c.Add(Col("量口径", nameof(OrderRow.Basis), new DataGridLength(58), TextAlignment.Center));
        c.Add(Col("运距 km", nameof(OrderRow.Haul), new DataGridLength(64), TextAlignment.Right));
        c.Add(Col("质量目标", nameof(OrderRow.Quality), new DataGridLength(1.2, DataGridLengthUnitType.Star), wrap: true));
        // 作业人员：单据发到人头上，不是发给一个设备号。来自「班组派工」
        c.Add(Col("作业人员", nameof(OrderRow.Crew), new DataGridLength(1.25, DataGridLengthUnitType.Star), wrap: true));
        c.Add(Col("时段 / 工时", nameof(OrderRow.Span), new DataGridLength(82), TextAlignment.Center));
    }

    private static DataGridTemplateColumn Col(string header, string path, DataGridLength width, TextAlignment align = TextAlignment.Left, bool wrap = false, string? tipPath = null)
    {
        var col = new DataGridTemplateColumn { Header = TaskUi.Head(header), Width = width };
        col.CellTemplate = new FuncDataTemplate<OrderRow>((_, _) =>
        {
            var tb = new TextBlock { TextAlignment = align, Margin = align == TextAlignment.Right ? new Thickness(4, 2, 6, 2) : new Thickness(4, 2, 4, 2), TextWrapping = wrap || align != TextAlignment.Left ? TextWrapping.Wrap : TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            tb.Bind(TextBlock.TextProperty, new Binding(path));
            if (tipPath != null) tb.Bind(ToolTip.TipProperty, new Binding(tipPath));
            return tb;
        });
        return col;
    }

    private static DataGridTemplateColumn TwoLine(string header, string mainPath, string subPath, string hasSubPath, DataGridLength width, string? tipPath)
    {
        var col = new DataGridTemplateColumn { Header = TaskUi.Head(header), Width = width };
        col.CellTemplate = new FuncDataTemplate<OrderRow>((_, _) =>
        {
            var sp = new StackPanel { Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            var main = new TextBlock { TextWrapping = TextWrapping.Wrap };
            main.Bind(TextBlock.TextProperty, new Binding(mainPath));
            var sub = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = InkDim, Margin = new Thickness(0, 1, 0, 0) };
            sub.Bind(TextBlock.TextProperty, new Binding(subPath));
            sub.Bind(Visual.IsVisibleProperty, new Binding(hasSubPath));
            sp.Children.Add(main); sp.Children.Add(sub);
            if (tipPath != null) sp.Bind(ToolTip.TipProperty, new Binding(tipPath));
            return sp;
        });
        return col;
    }

    private string SelectedShift
    {
        get
        {
            string s = ShiftSelector.Selected(shiftCombo);
            return s.Length > 0 ? s : ShiftScope.Current;
        }
    }

    /// <summary>重读计划与落盘单据。本窗是 modeless 单例，编制参数改过之后这里显示的还是旧盘子。</summary>
    private void OnRefresh()
    {
        _tasks = SampleTaskBoard.Day();
        Rebuild();
        toolStatus.Text += " · 已重读计划与下达单据";
    }

    private void Rebuild()
    {
        if (!_loaded) return;
        string shift = SelectedShift;
        _shiftTasks = _tasks
            .Where(t => t.Shift == shift && t.Process != ProcessType.Idle)
            .OrderBy(t => t.StartHour)
            .ToList();

        // 本班派工：正式单据要能签收到人头，而不是发给一台设备号
        var crew = CrewLookup.Of(_date, shift);

        int no = 1;
        var rows = _shiftTasks.Select(t =>
        {
            var q = TaskQuantity.Of(t);
            return new OrderRow
            {
                No = no++,
                Group = GroupText(t),
                Place = t.LocationCaption,                       // 走派生属性，与甘特/报表同口径
                Unit = (t.UnitId ?? "").Trim(),
                Destination = DestinationText(t),
                Process = t.Process.Label(),
                Material = MaterialText(t),
                Plan = q.Cell,
                Basis = q.Basis.Length > 0 ? q.Basis : "—",
                PlanTip = PlanTipText(t, q),
                Haul = HaulText(t),
                Quality = t.QualityTarget?.Caption ?? "—",
                Span = $"{Hm(t.StartHour)}–{Hm(t.EndHour)}" + (t.PlannedHours > 0 ? $"\n{t.PlannedHours:0.#} h" : ""),
                Crew = CrewText(t, crew),
                NoDestination = MissingDestination(t),
                Splits = SplitsText(t),
                DestinationTip = DestinationTipText(t),
            };
        }).ToList();
        _rows = rows;
        grid.ItemsSource = rows;

        docMeta.Text = $"日期：{SampleTaskBoard.DateLabel}      班次：{shift}      编制时间：{Hm(SampleTaskBoard.NowHour)}";

        // ── 抬头指标条：四本账分列 ──
        var sum = TaskQuantity.Sum(_shiftTasks);
        docMetrics.Text = sum.Caption;

        // ── 结论句 + 去向分布 ──
        string sinkBrief;
        if (sum.HaulTasks > 0)
        {
            sinkBrief = string.Join("、", _shiftTasks
                .Where(t => t.Process == ProcessType.Haul && t.HaulTonnageT > 1e-6)
                .GroupBy(t => string.IsNullOrWhiteSpace(t.DestinationName) ? t.DestinationId : t.DestinationName)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .OrderByDescending(g => g.Sum(x => x.HaulTonnageT))
                .Select(g => $"{g.Key} {g.Sum(x => x.HaulTonnageT) / 1e4:0.##}万t"));
        }
        else
        {
            var outbound = _shiftTasks.Where(DailyGanttModel.NeedsDestination)
                                      .SelectMany(t => t.ToFlows("", t.TargetVolumeM3))
                                      .Where(f => !string.IsNullOrWhiteSpace(f.SinkId) || !string.IsNullOrWhiteSpace(f.SinkName))
                                      .ToList();
            sinkBrief = string.Join("、", outbound
                .GroupBy(f => string.IsNullOrWhiteSpace(f.SinkName) ? f.SinkId : f.SinkName)
                .Select(g => $"{g.Key} {g.Sum(x => x.TonnageT) / 1e4:0.##}万t"));
        }

        docSummary.Text =
            $"本班任务 {rows.Count} 项（" + ProcessBrief(_shiftTasks) + "）。"
          + (sinkBrief.Length > 0 ? $" 去向分布：{sinkBrief}。" : "")
          + " 三本方量账不合并：穿孔记控制方量、采装记原位实方、排土记排弃占容，运输记承运吨——"
          + "任何一处把它们加在一起得到的都不是真实量。"
          + " 请各班组按时段、按编组、按卸载地点组织作业，运距与车次以调度指令为准；遇故障/缺料/卸点拥堵及时报调度。";

        // ── 缺卸点清单（不得下达）──
        var missing = _shiftTasks.Where(MissingDestination).ToList();
        if (missing.Count > 0)
        {
            docNoSinkBox.IsVisible = true;
            docNoSinkList.Text = string.Join("\n", missing.Select(t =>
            {
                var miss = DailyGanttModel.UnroutedMaterials(t);
                string why = miss.Count > 0 && DailyGanttModel.RoutesOf(t).Count > 1
                    ? $"—— 混采面的 {string.Join("、", miss)} 未指定卸点"
                    : "—— 未指定卸点";
                return $"· 第 {_shiftTasks.IndexOf(t) + 1} 项　{t.Id}　{t.Group.MainEquipment}　{t.LocationCaption}　"
                     + $"{t.Process.Label()} {TaskQuantity.Of(t).CaptionWithBasis} {why}";
            }));
        }
        else
        {
            docNoSinkBox.IsVisible = false;
            docNoSinkList.Text = "";
        }

        RefreshIssueState(missing.Count);

        toolStatus.Text = missing.Count > 0
            ? $"{shift}：{rows.Count} 项任务 · {missing.Count} 项缺卸点"
            : $"{shift}：{rows.Count} 项任务";
    }

    /// <summary>落款处的下达状态 —— 读「任务下达」落的 <see cref="TaskInstance"/>，按稳定键 <see cref="TaskKey"/> 对号。</summary>
    private void RefreshIssueState(int missingCount)
    {
        if (_shiftTasks.Count == 0) { docIssue.Text = "下达状态：本班无任务。"; return; }

        Dictionary<string, TaskInstance> inst;
        try
        {
            inst = new Dictionary<string, TaskInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in TaskPersistence.LoadInstancesOfDay(_date))
                if (!string.IsNullOrWhiteSpace(it.StableKey)) inst[it.StableKey] = it;
        }
        catch (Exception ex)
        {
            docIssue.Text = $"下达状态：无法读取下达单据（{ex.Message}）——请到「任务下达」查看。";
            return;
        }

        var issued = _shiftTasks
            .Select(t => inst.TryGetValue(TaskKey.Of(t, _date), out var x) && x.IsIssued ? x : null)
            .Where(x => x != null).Select(x => x!).ToList();

        string tail = missingCount > 0
            ? $"　⚠ {missingCount} 项缺卸点，下达时会被拦下（逐物料判定，混采面缺任一物料去向即算缺）。"
            : "";

        if (issued.Count == 0)
        {
            docIssue.Text = $"下达状态：未下达（0 / {_shiftTasks.Count} 项）· 签发下达请到「任务下达」。" + tail;
            return;
        }

        var last = issued.OrderByDescending(x => x.IssuedAt).First();
        int acked = issued.Count(x => x.AckedAt.HasValue);
        string head = issued.Count == _shiftTasks.Count
            ? $"下达状态：已全部下达（{_shiftTasks.Count} 项）"
            : $"下达状态：部分下达（{issued.Count} / {_shiftTasks.Count} 项）";

        docIssue.Text = head
            + $" · 最近 {last.IssuedBy} {last.IssuedAt:MM-dd HH:mm}（v{last.Version}）"
            + $" · 班组已确认 {acked} 项" + tail;
    }

    // ── 单元格文案 ────────────────────────────────────────────────────────────

    private static string ProcessBrief(IEnumerable<ProductionTask> tasks)
    {
        var order = new[] { ProcessType.Drill, ProcessType.Blast, ProcessType.Load, ProcessType.Haul, ProcessType.Dump };
        var by = tasks.GroupBy(t => t.Process).ToDictionary(g => g.Key, g => g.Count());
        var parts = order.Where(p => by.ContainsKey(p)).Select(p => $"{p.Label()} {by[p]}");
        return parts.Any() ? string.Join(" · ", parts) : "无";
    }

    private static string GroupText(ProductionTask t)
    {
        if (t.Process == ProcessType.Blast && string.IsNullOrWhiteSpace(t.Group.MainEquipment))
            return "不派设备（清场）";
        return t.Group.Caption;
    }

    private static string PlanTipText(ProductionTask t, TaskQuantity.Line q)
    {
        if (q.HasValue)
        {
            string s = $"{q.Value:N0} {q.Unit}　口径：{q.Basis}";
            if (q.Second.Length > 0) s += $"\n{q.Second}";
            if (t.Process == ProcessType.Load)
                s += $"\n其中 计入采出 {t.OreVolumeM3:N0} m³实方 · 计入剥离 {t.WasteVolumeM3:N0} m³实方"
                   + $"\n运输松方 {t.TargetLooseM3:N0} m³松（卡车车厢口径）";
            if (t.Process == ProcessType.Haul && t.EffectiveHaulKm > 1e-6)
                s += $"\n运输功 {t.HaulTonnageT * t.EffectiveHaulKm:N0} t·km（承运吨 × 本笔运距 {t.EffectiveHaulKm:0.##} km）";
            return s + "\n\n三本方量账不合并：控制方量 / 原位实方 / 排弃占容，加在一起不对应任何真实量。";
        }
        return q.Why.Length > 0
            ? $"没有这个数：{q.Why}\n（「—」说的是「这个数没有出处」，不是「量是零」）"
            : "没有这个数。";
    }

    private string CrewText(ProductionTask t, CrewShift crew)
    {
        if (t.Process == ProcessType.Blast && string.IsNullOrWhiteSpace(t.Group.MainEquipment))
            return "不派设备（清场）";

        string main = t.Group.MainEquipment;
        if (t.Process == ProcessType.Haul)
        {
            var src = _shiftTasks.FirstOrDefault(x => x.Process == ProcessType.Load
                                                   && string.Equals(x.Id, t.SourceTaskId, StringComparison.OrdinalIgnoreCase));
            if (src != null) main = src.Group.MainEquipment;
        }

        string c = crew.CaptionOf(main);
        if (c.Length > 0)
            return t.Process == ProcessType.Haul && t.Group.Trucks.Count > 0
                ? $"{c}\n（{t.Group.MainEquipment} {t.Group.Trucks.Count} 台）"
                : c;

        return crew.IsEmpty ? "本班未派工" : "未派工";
    }

    private static bool MissingDestination(ProductionTask t)
        => DailyGanttModel.NeedsDestination(t) && !DailyGanttModel.AllMaterialsRouted(t);

    private static string SplitsText(ProductionTask t)
    {
        if (t.Process == ProcessType.Dump) return "";
        var routes = DailyGanttModel.RoutesOf(t);
        if (routes.Count < 2) return "";
        return string.Join(" · ", routes.Select(d => d.HasDestination
            ? d.Caption
            : $"{d.Spec.Name} {d.Fraction * 100:0.#}% → ⚠ 未指定卸点"));
    }

    private static string DestinationTipText(ProductionTask t)
    {
        var routes = DailyGanttModel.RoutesOf(t);
        if (t.Process == ProcessType.Dump || routes.Count < 2) return "";

        var lines = routes.Select(d =>
        {
            double m3 = t.TargetVolumeM3 * d.Fraction;
            return d.HasDestination
                ? $"{d.Spec.Name} {d.Fraction * 100:0.#}%　→　"
                  + $"{(string.IsNullOrWhiteSpace(d.DestinationName) ? d.DestinationId : d.DestinationName)}"
                  + $"（{d.DestinationKind.Label()}）　{m3:N0} m³实方 · 运距 {d.EffectiveHaulKm:0.##} km"
                : $"{d.Spec.Name} {d.Fraction * 100:0.#}%　→　⚠ 未指定卸点（{m3:N0} m³实方无处可去，不得签发）";
        });

        return "一条任务、多个去向（混采面按物料分项）：\n" + string.Join("\n", lines)
             + $"\n加权运距 {DailyGanttModel.WeightedHaulKm(t):0.##} km"
             + $" · 运输功 {t.TransportWorkBySplitTKm / 1e4:0.###} 万t·km（按各物料各自的运距算）";
    }

    private static string DestinationText(ProductionTask t)
    {
        // 排土任务本身就在卸点上作业（推土机不外运）：写"本场排弃"；占容方走 TaskQuantity
        if (t.Process == ProcessType.Dump)
        {
            string at = t.HasDestination
                ? (string.IsNullOrWhiteSpace(t.DestinationName) ? t.DestinationId : t.DestinationName)
                : t.WorkZone;
            var q = TaskQuantity.Of(t);
            return $"{at}（本场排弃 · 占容 {(q.HasValue ? $"{q.Value:N0} m³" : "—")}）";
        }
        // 混采：主行写主去向 + "另 N 个去向"
        var routes = DailyGanttModel.RoutesOf(t);
        if (routes.Count > 1)
        {
            var withDest = routes.Where(d => d.HasDestination).ToList();
            var main = withDest.OrderByDescending(d => d.Fraction).FirstOrDefault();
            int miss = routes.Count - withDest.Count;

            string head = main == null
                ? "⚠ 未指定卸点"
                : $"{(string.IsNullOrWhiteSpace(main.DestinationName) ? main.DestinationId : main.DestinationName)}"
                  + $"（{main.DestinationKind.Label()}）{main.EffectiveHaulKm:0.##}km";

            return head + (miss > 0
                ? $"　⚠ 另 {miss} 种物料缺去向"
                : $"　另 {withDest.Count - 1} 个去向");
        }

        if (t.HasDestination) return t.DestinationCaption;
        if (DailyGanttModel.NeedsDestination(t)) return "⚠ 未指定卸点";
        return "—";
    }

    private static string HaulText(ProductionTask t)
    {
        if (t.Process == ProcessType.Dump) return "场内";

        var routes = DailyGanttModel.RoutesOf(t);
        if (routes.Count > 1 && routes.Any(d => d.HasDestination))
        {
            double avg = DailyGanttModel.WeightedHaulKm(t);
            string legs = string.Join(" / ", routes.Where(d => d.HasDestination)
                                                   .Select(d => $"{d.Spec.Name}{d.EffectiveHaulKm:0.##}"));
            return $"加权{avg:0.##}" + (legs.Length > 0 ? $"\n{legs}" : "");
        }

        if (!t.HasDestination) return "—";
        string s = $"{t.HaulDistanceKm:0.##}";
        if (t.EquivHaulKm > 1e-6 && Math.Abs(t.EquivHaulKm - t.HaulDistanceKm) > 0.01)
            s += $"\n等{t.EquivHaulKm:0.##}";
        return s;
    }

    private static string MaterialText(ProductionTask t)
    {
        if (t.Mix != null || MaterialCatalog.Exists(t.MaterialCode)) return t.ResolvedMix.Caption;
        if (MaterialCatalog.CodeFromText(t.Material).Length > 0) return t.ResolvedMix.Caption;
        return string.IsNullOrWhiteSpace(t.Material) ? "—" : t.Material;
    }

    // ── 打印 / 导出 PDF（QuestPDF 出同版式横向单据）───────────────────────────

    private async void OnPrint()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出生产任务书 PDF（横向 A4；打印请用 PDF 查看器）",
            SuggestedFileName = $"生产任务书 {SampleTaskBoard.DateLabel} {SelectedShift}.pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });
        if (file == null) return;
        try
        {
            TaskOrderPdf.Save(file.Path.LocalPath, docMine.Text ?? "", docMeta.Text ?? "", docMetrics.Text ?? "", _rows,
                              docSummary.Text ?? "", docNoSinkBox.IsVisible ? docNoSinkList.Text ?? "" : "", docIssue.Text ?? "");
            toolStatus.Text = $"已导出 PDF（横向 A4）：{file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            toolStatus.Text = "导出失败：" + ex.Message;
        }
    }

    private static string Hm(double hh)
    {
        int h = (int)hh; int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }
}
