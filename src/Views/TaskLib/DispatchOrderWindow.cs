// 忠实移植自原 PitMine3D Modules/TaskLib/Features/DispatchOrderWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅两处：① WPF CollectionViewSource 分组 → Avalonia DataGridCollectionView + DataGridPathGroupDescription（组头同样"车号（司机）· N 车次"）；
// ② WPF PrintDialog → QuestPDF 横向 A4 出 PDF（DispatchOrderPdf）。
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 派车单：把「班次任务」下沉到「车次」，这是任务下达最后一公里的可见交付物。
///
/// <para>
/// 任务说的是"这个班采 2450 m³"，司机要的是"我第 3 趟 09:12 到 WK-10 装车、09:26 卸到破碎站"。
/// 中间那一步就是车次展开（见 <see cref="DispatchEngine"/>）：
/// <c>s(i,k) = 起始 + (i−1)·τ_L + (k−1)·T_c</c>，车次数取「时间法」与「量法」的小者。
/// </para>
/// <para>
/// 三口径同屏：载重给【吨】（守恒量）、松方给 m³（车厢容积口径，卡车拉的是爆破后的松散料）、
/// 运距给 km（等效运距优先）。抬头的总运输功 t·km = Σ载重×运距，是评价这班派车合不合理的主指标。
/// </para>
/// </summary>
public sealed class DispatchOrderWindow : Window
{
    public sealed class Row
    {
        public string Truck { get; set; } = "";
        /// <summary>这辆车谁开（来自班组派工）。</summary>
        public string Driver { get; set; } = "";
        /// <summary>分组头文案："T-01（孙宝山）"——司机拿到手先找自己的名字，不是找车号。</summary>
        public string TruckHead { get; set; } = "";
        public string Shovel { get; set; } = "";
        public int Trip { get; set; }
        public string Zone { get; set; } = "";
        public string Material { get; set; } = "";
        public string Sink { get; set; } = "";
        public string LoadAt { get; set; } = "";
        public string DumpAt { get; set; } = "";
        public string PayloadT { get; set; } = "";
        // LooseM3 已随「松方 m³」列一起撤（总量仍在抬头 KPI 里）
        public string Haul { get; set; } = "";
        /// <summary>本趟自己那条线的循环时间 T_c（min）——混采两条线不是一个数。</summary>
        public string Cycle { get; set; } = "";
        public string State { get; set; } = "";

        // ── 实绩（没回填就留空，不拿 0 冒充「准点」）──
        public string ActLoadAt { get; set; } = "";
        public string ActDumpAt { get; set; } = "";
        public string Delay { get; set; } = "";

        /// <summary>本行对应的指令本体（标记实绩改的就是它）。</summary>
        public DispatchOrder Order { get; init; } = new();
    }

    private readonly string _date = SampleTaskBoard.DateLabel;
    private List<DispatchOrder> _orders = new();

    /// <summary>
    /// 同一批任务的**运输笔**（`HaulDumpDeriver` 从采装笔解析派生出来的那一份）。
    /// <para>
    /// 车次在这套系统里有**两个实现**：这里的时刻展开
    /// （<c>s(i,k)=起始+(i−1)τ_L+(k−1)T_c</c>，车次数取时间法与量法之小者）
    /// 和运输笔上的解析值（<c>承运吨 ÷ 单车载重</c>）。两边各自都对，
    /// 也就都证明不了对方 —— 必须拿同一批输入交叉钉一次，差多少写在单据上。
    /// 不钉的话，任务书上写「45 车次」而派车单展开出 38 趟，两张纸都发到现场，没有一处会报错。
    /// </para>
    /// </summary>
    private List<ProductionTask> _haulRef = new();

    /// <summary>
    /// 本次展开中**展不开车次**的任务及其原因（引擎给的，界面不再判一遍）。
    /// 空态面板按它分组 —— 见 <see cref="DispatchPlan.Skips"/> 上那段注释。
    /// </summary>
    private List<TripSkip> _skips = new();

    /// <summary>当前表里的行（打印用；与 grid 的分组视图同源）。</summary>
    private List<Row> _rows = new();

    private readonly TextBlock subTitle;
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox ruleCombo = new() { Width = 150, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private readonly TextBlock markHint = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Text = "选中车次后回填；回填即落盘，看板的「车次执行」读的就是它。" };
    private readonly WrapPanel kpi = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Border emptyBox = new() { IsVisible = false, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(22, 18), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock emptyTitle = new() { FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock emptyBody = new() { FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid grid = TaskUi.Grid(single: false);
    private readonly TextBlock noteText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private bool _loaded;

    public DispatchOrderWindow()
    {
        Title = "派车单 — 日常生产组织";
        TaskUi.Place(this, 1420, 700);

        var header = TaskUi.Header("派车单", "任务 → 车次：s(i,k)=起始+(i−1)τ_L+(k−1)T_c · 按卡车分组 · 可打印");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        // 工具条第一行：班次 / 派车规则 / 展开·保存·载入·打印 —— 右端状态
        var row1 = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        void L1(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); row1.Children.Add(c); }
        L1(Lbl("班次"));
        // 取值域按当日班制由 ShiftSelector 填、默认落当前班（原先默认「全部」）
        L1(shiftCombo);
        L1(Lbl("派车规则"));
        foreach (var s in new[] { "固定配车", "最小铲饱和度", "最早可装车" }) ruleCombo.Items.Add(new ComboBoxItem { Content = s });
        L1(ruleCombo);
        L1(TaskUi.Btn("展开车次", OnExpand, 90, bold: true));
        L1(TaskUi.Btn("保存派车单", OnSave, 100));
        L1(TaskUi.Btn("载入已存", OnLoad, 86));
        var print = TaskUi.Btn("打印", OnPrint, 66); print.Margin = new Thickness(0); L1(print);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        DockPanel.SetDock(toolStatus, Avalonia.Controls.Dock.Right); row1.Children.Add(toolStatus);

        // 车次实绩回填。★ 在这一行出现之前，DispatchOrderStatus 六个状态只有 Planned 被写过，
        // ActualLoadHour / ActualDumpHour 零写入 —— 于是「状态」列恒显示"计划"、
        // 看板的「车次执行」已卸数恒为 0。卡调系统接通前，这里是唯一的实绩入口。
        var row2 = new DockPanel { LastChildFill = true };
        void L2(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); row2.Children.Add(c); }
        var mk = Lbl("实绩回填"); mk.Margin = new Thickness(0, 0, 8, 0); TaskUi.Theme(mk, TextBlock.ForegroundProperty, "Theme.Text.Muted"); L2(mk);
        var bLoad = TaskUi.Btn("标记装车", OnMarkLoaded, 86); ToolTip.SetTip(bLoad, "选中的车次回填实际装车时刻并置「装车中」"); L2(bLoad);
        var bDump = TaskUi.Btn("标记卸载", OnMarkDumped, 86); ToolTip.SetTip(bDump, "回填实际卸车时刻并置「已卸载」。没标过装车也可以直接卸——不会倒推一个装车时刻出来充数"); L2(bDump);
        L2(TaskUi.Btn("取消车次", OnCancelTrip, 86));
        var bClr = TaskUi.Btn("撤销标记", OnClearActual, 86); bClr.Margin = new Thickness(0, 0, 14, 0); L2(bClr);
        TaskUi.Theme(markHint, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        row2.Children.Add(markHint);
        var toolPanel = new StackPanel(); toolPanel.Children.Add(row1); toolPanel.Children.Add(row2);

        // 打印范围：KPI 抬头 + 车次表
        // 空态面板：一条车次都没展开时，表格区写清**为什么**，而不是留一排光秃秃的表头。
        // 车次展不开在这套系统里只有几种原因（缺去向 / 缺运距 / 缺编组 / 未配车 / 本班无采装），
        // 各自的补法完全不同，混成一句"暂无数据"就等于什么也没说。
        TaskUi.Theme(emptyBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(emptyBox, Border.BorderBrushProperty, "Theme.Surface.Border");
        TaskUi.Theme(emptyTitle, TextBlock.ForegroundProperty, "Theme.Text.Body");
        TaskUi.Theme(emptyBody, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var esp = new StackPanel(); esp.Children.Add(emptyTitle); esp.Children.Add(emptyBody); emptyBox.Child = esp;

        grid.Margin = new Thickness(0);
        // 司机来自「班组派工」的车号↔司机显式配对；没派工就留空，不编名字
        grid.Columns.Add(TaskUi.TextCol("司机", nameof(Row.Driver), 76));
        grid.Columns.Add(TaskUi.TextCol("铲", nameof(Row.Shovel), 72));
        grid.Columns.Add(TaskUi.TextCol("趟次", nameof(Row.Trip), 52));
        grid.Columns.Add(Star("作业面", nameof(Row.Zone), 1.4, 120));
        grid.Columns.Add(TaskUi.TextCol("物料", nameof(Row.Material), 70));
        grid.Columns.Add(Star("卸点", nameof(Row.Sink), 1.4, 120));
        grid.Columns.Add(TaskUi.TextCol("预计装车", nameof(Row.LoadAt), 80));
        grid.Columns.Add(TaskUi.TextCol("预计卸车", nameof(Row.DumpAt), 80));
        // 实绩三列：没回填就是空白，不拿 0 冒充「准点」
        grid.Columns.Add(TaskUi.TextCol("实装", nameof(Row.ActLoadAt), 66));
        grid.Columns.Add(TaskUi.TextCol("实卸", nameof(Row.ActDumpAt), 66));
        grid.Columns.Add(TaskUi.TextCol("延误 min", nameof(Row.Delay), 74));
        // 「松方 m³」列已撤：它与载重 t 是同一车料的两个单位，且松方是**编制期**的车厢容积校核口径，执行单据上司机用不着。
        grid.Columns.Add(TaskUi.TextCol("载重 t", nameof(Row.PayloadT), 70));
        grid.Columns.Add(TaskUi.TextCol("运距 km", nameof(Row.Haul), 70));
        // 混采任务两条线的 T_c 不是一个数（煤 2.6km / 岩 1.26km），逐趟列出来才看得见
        grid.Columns.Add(TaskUi.TextCol("循环 min", nameof(Row.Cycle), 74));
        grid.Columns.Add(TaskUi.TextCol("状态", nameof(Row.State), 76));

        var paper = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(10, 8, 10, 0) };
        Grid.SetRow(kpi, 0); Grid.SetRow(grid, 1); Grid.SetRow(emptyBox, 1);
        paper.Children.Add(kpi); paper.Children.Add(grid); paper.Children.Add(emptyBox);

        TaskUi.Theme(noteText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(new ScrollViewer { Content = noteText, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, top: false, padY: 6);
        foot.MaxHeight = 140;

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(toolPanel, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(paper, 2); Grid.SetRow(foot, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(paper); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => OnParamChanged();
        ruleCombo.SelectionChanged += (_, _) => OnParamChanged();
        Opened += (_, _) =>
        {
            ShiftSelector.Bind(shiftCombo, includeAll: true);   // 默认当前班（原先「全部」）
            ruleCombo.SelectedIndex = 0;
            _loaded = true;
            Expand();
        };
    }

    private static TextBlock Lbl(string t)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
        return l;
    }

    private static DataGridTextColumn Star(string header, string path, double star, double minWidth)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path), Width = new DataGridLength(star, DataGridLengthUnitType.Star), MinWidth = minWidth };

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);

    private DispatchRuleKind Rule => Text(ruleCombo) switch
    {
        "最小铲饱和度" => DispatchRuleKind.MinShovelSaturation,
        "最早可装车" => DispatchRuleKind.EarliestLoad,
        _ => DispatchRuleKind.FixedAssignment,
    };

    private static string Text(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    // ── 展开 / 载入 ──────────────────────────────────────────────────────────

    private void OnParamChanged()
    {
        if (!_loaded) return;
        Expand();
    }

    private void OnExpand()
    {
        DispatchEngine.Invalidate();   // 手动展开＝要最新的运距/编组解，丢缓存重算
        Expand();
    }

    private void Expand()
    {
        DispatchPlan plan;
        List<ProductionTask> day;
        try
        {
            day = SampleTaskBoard.Day();
            plan = DispatchEngine.Expand(day, _date, ShiftFilter, Rule);
        }
        catch (Exception ex)
        {
            _orders = new List<DispatchOrder>();
            _skips = new List<TripSkip>();
            _haulRef = new List<ProductionTask>();
            Bind();
            noteText.Text = "车次展开失败：" + ex.Message;
            toolStatus.Text = "展开失败";
            return;
        }

        _orders = plan.Orders;
        _skips = plan.Skips;
        _haulRef = HaulRefOf(day);
        int unissued = AttachInstances(_orders);      // 展开即对号：哪些车次的任务还没下达，当场就得看见
        Bind();
        noteText.Text = UnissuedWarning(unissued)
                      + string.Join(Environment.NewLine, plan.Notes) + LineBreakdown() + CrossCheck();
        toolStatus.Text = plan.Summary;
        subTitle.Text = $"{_date}{(ShiftFilter.Length > 0 ? " · " + ShiftFilter : " · 全天")} · {Rule.Label()} · "
                      + "s(i,k)=起始+(i−1)τ_L+(k−1)T_c，车次数取时间法与量法之小者";
    }

    private void OnLoad()
    {
        var all = TaskPersistence.LoadOrdersOfDay(_date);
        _orders = ShiftFilter.Length == 0 ? all : all.Where(o => o.Shift == ShiftFilter).ToList();
        // 载入的是**落盘的**派车单，没跑展开 ⇒ 没有 Skips 可分组。
        // 留空让空态说"本班没有可展开的采装任务"是错的，所以这条路上清掉、由下面的说明兜住。
        _skips = new List<TripSkip>();
        try { _haulRef = HaulRefOf(SampleTaskBoard.Day()); } catch { _haulRef = new List<ProductionTask>(); }
        int unissued = AttachInstances(_orders);
        Bind();
        int dumped = _orders.Count(o => o.Status == DispatchOrderStatus.Dumped);
        noteText.Text = UnissuedWarning(unissued)
            + (_orders.Count == 0
                ? $"{TaskPersistence.OrderDir()} 下暂无本日派车单（先「展开车次」再「保存派车单」）。"
                : $"已从 {TaskPersistence.OrderDir()} 载入 {_orders.Count} 条派车指令（已卸 {dumped} 趟）。")
            + LineBreakdown() + CrossCheck();
        toolStatus.Text = $"载入 {_orders.Count} 车次";
    }

    /// <summary>当前班次筛选下的运输笔（交叉核对的另一侧）。</summary>
    private List<ProductionTask> HaulRefOf(IEnumerable<ProductionTask>? day)
        => (day ?? Array.Empty<ProductionTask>())
            .Where(t => t != null && t.Process == ProcessType.Haul)
            .Where(t => ShiftFilter.Length == 0 || string.Equals(t.Shift, ShiftFilter, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// **交叉核对**：时刻展开出来的车次 vs 运输笔上的解析车次（承运吨 ÷ 单车载重）。
    ///
    /// <para>两边是同一件事的两个实现，各自都自洽 —— 所以谁也证明不了谁。
    /// 差值写在单据上：差得多说明 τ_L / T_c / 载重 三个数里至少有一个与运输笔用的不是同一份，
    /// 或者时间法把车次卡住了（班内时窗不够跑那么多趟）。<b>后者是真结论</b>，
    /// 说的是"这一班拉不完"，而不是哪边算错了。</para>
    ///
    /// <para>核不了就明说核不了（没有运输笔 / 载重没解出来），<b>不给一个"✓ 一致"</b> ——
    /// 空过的核对比不核对更坏：它给的是"已经查过了"这个结论。</para>
    /// </summary>
    private string CrossCheck()
    {
        if (_orders.Count == 0) return "";
        if (_haulRef.Count == 0)
            return Environment.NewLine + "◆ 交叉核对：本盘**没有运输笔**（解析式派生那一步没产出），"
                 + "车次只有这一份实现，核不了。";

        int planTrips = _haulRef.Sum(t => t.TripCount);
        double planT = _haulRef.Sum(t => t.HaulTonnageT);
        int gotTrips = _orders.Count(o => o.Status != DispatchOrderStatus.Cancelled);
        double gotT = _orders.Where(o => o.Status != DispatchOrderStatus.Cancelled).Sum(o => o.PayloadT);

        if (planTrips <= 0)
            return Environment.NewLine + $"◆ 交叉核对：运输笔给不出车次（单车载重没解出来，共 {_haulRef.Count} 笔 · "
                 + $"承运 {planT / 1e4:0.##} 万t）—— 只能核吨量：展开 {gotT / 1e4:0.##} 万t。"
                 + "载重解不出来时车次一律不猜，所以这一项核不了。";

        double dTrip = planTrips == 0 ? 0 : (gotTrips - planTrips) * 100.0 / planTrips;
        double dTon = planT <= 1e-6 ? 0 : (gotT - planT) * 100.0 / planT;
        bool ok = Math.Abs(dTrip) <= 5 && Math.Abs(dTon) <= 5;

        return Environment.NewLine
             + $"◆ 交叉核对（时刻展开 vs 运输笔解析）：车次 {gotTrips} / {planTrips}（{dTrip:+0.#;-0.#;0}%）· "
             + $"吨量 {gotT / 1e4:0.##} / {planT / 1e4:0.##} 万t（{dTon:+0.#;-0.#;0}%）"
             + (ok
                ? " —— 两条独立算法一致。"
                : " —— **两边对不上**。展开数偏小通常是班内时窗跑不满那么多趟（那是真结论：这一班拉不完）；"
                + "偏大或吨量对不上则说明 τ_L / T_c / 单车载重 与运输笔用的不是同一份，要回「编制配置」看编组求解。");
    }

    private void OnSave()
    {
        if (_orders.Count == 0) { toolStatus.Text = "无车次可保存（请先展开）"; return; }

        AttachInstances(_orders);
        bool ok = Persist();
        int files = _orders.Select(o => string.IsNullOrWhiteSpace(o.Shift) ? "全天" : o.Shift).Distinct().Count();
        toolStatus.Text = ok
            ? $"已保存 {_orders.Count} 条车次到 {files} 个班次文件 · {TaskPersistence.OrderDir()}"
            : $"⚠ 保存失败：{TaskPersistence.LastIoLabel}";
    }

    /// <summary>
    /// 把车次挂到已下达的任务实例上（按稳定键对号）——"哪一趟属于哪份下达单据"可追溯，
    /// 同时把计划态推成已下达。返回**任务尚未下达**的车次数。
    /// </summary>
    private int AttachInstances(List<DispatchOrder> orders)
    {
        try
        {
            var inst = TaskPersistence.LoadInstancesOfDay(_date)
                .Where(x => !string.IsNullOrWhiteSpace(x.StableKey) && x.IsIssued)
                .GroupBy(x => x.StableKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Version).First().InstanceId, StringComparer.OrdinalIgnoreCase);
            foreach (var o in orders)
                if (inst.TryGetValue(o.StableKey, out var id)) { o.InstanceId = id; o.MarkIssued(); }
        }
        catch { /* 实例读不出来 → 一律按未下达显示，不假装下达过 */ }

        return orders.Count(o => string.IsNullOrWhiteSpace(o.InstanceId));
    }

    /// <summary>未下达车次的醒目提示。派车指令是承诺，跑在下达之前的必须写明。</summary>
    private static string UnissuedWarning(int unissued)
        => unissued <= 0 ? ""
         : $"⚠ 其中 {unissued} 趟属于**尚未下达**的任务 —— 这些是预览稿，"
         + "请先在「任务下达」签发后再据此派车（状态列已逐条标注）。" + Environment.NewLine;

    /// <summary>按班次分文件落盘（回填实绩后立即调用，看板读的就是这份）。</summary>
    private bool Persist()
    {
        bool ok = true;
        foreach (var g in _orders.GroupBy(o => string.IsNullOrWhiteSpace(o.Shift) ? "全天" : o.Shift))
            ok &= TaskPersistence.SaveOrders(_date, g.Key, g.ToList());
        return ok;
    }

    // ── 实绩回填（车次状态机的唯一入口，卡调系统接通前）────────────────────────

    private void OnMarkLoaded() => Mark("标记装车", ReceiptKind.Start, (o, h) => o.MarkLoaded(h));

    private void OnMarkDumped() => Mark("标记卸载", ReceiptKind.Finish, (o, h) => o.MarkDumped(h));

    private void OnCancelTrip() => Mark("取消车次", ReceiptKind.Exception, (o, _) => { o.Cancel(); return true; });

    private void OnClearActual() => Mark("撤销标记", ReceiptKind.Withdraw, (o, _) => { o.ClearActual(); return true; });

    /// <summary>
    /// 对选中的车次施加一次状态转移，然后立即落盘。
    /// 时刻取<b>盘子的当前时钟</b>（与 PlannedLoadHour 同一条时间轴），
    /// 这样"延误 min = 实际 − 计划"才是同一把尺子上的差。
    /// </summary>
    private void Mark(string action, ReceiptKind kind, Func<DispatchOrder, double, bool> apply)
    {
        var sel = grid.SelectedItems.OfType<Row>().Select(r => r.Order).ToList();
        if (sel.Count == 0) { markHint.Text = $"{action}：请先在表里选中车次（可多选）。"; return; }

        double now = SampleTaskBoard.NowHour;
        string by = Environment.UserName;
        var changed = sel.Where(o => apply(o, now)).ToList();
        int skipped = sel.Count - changed.Count;

        // 车次级回执：现场真正发生的事也要进同一本流水，否则流水只记到"任务下达"就断了
        var receipts = changed.Select(o => DispatchReceipt.For(o, kind, by,
            $"{action} {o.TruckId} 第{o.TripNo}趟 → {(string.IsNullOrWhiteSpace(o.SinkName) ? o.SinkId : o.SinkName)}"
          + $" · {o.PayloadT:0.#}t / {o.PayloadInSituM3:0.#}m³实方")).ToList();

        bool ok = Persist();
        bool okR = receipts.Count == 0 || TaskPersistence.AppendReceipts(_date, receipts);
        Bind();

        int dumped = _orders.Count(o => o.Status == DispatchOrderStatus.Dumped);
        markHint.Text = $"{action}：{changed.Count} 趟"
                      + (skipped > 0 ? $"（{skipped} 趟已取消，未改——先「撤销标记」）" : "")
                      + $" · 本班已卸 {dumped}/{_orders.Count} 趟 · "
                      + (ok && okR ? "已落盘（含车次回执），看板与「任务下达」的单据流水刷新即可见"
                                   : $"⚠ 落盘失败：{TaskPersistence.LastIoLabel}");
    }

    // ── 绑定 ─────────────────────────────────────────────────────────────────

    private void Bind()
    {
        // 派工：车号→司机。按班取；「全部」时逐条按各自的班取，别拿一个班的派工套到全天
        var crewByShift = _orders.Select(o => o.Shift).Distinct(StringComparer.Ordinal)
                                 .ToDictionary(s => s, s => CrewLookup.Of(_date, s), StringComparer.Ordinal);

        var rows = _orders
            .OrderBy(o => o.TruckId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.PlannedLoadHour)
            .Select(o => new Row
            {
                Order = o,
                Truck = o.TruckId,
                Driver = crewByShift.TryGetValue(o.Shift, out var cs) ? cs.DriverOf(o.TruckId) : "",
                Shovel = o.ShovelId,
                Trip = o.TripNo,
                Zone = o.WorkZone,
                Material = o.MaterialName,
                Sink = string.IsNullOrWhiteSpace(o.SinkName) ? o.SinkId : $"{o.SinkName}（{o.SinkKind.Label()}）",
                LoadAt = o.LoadText,
                DumpAt = o.DumpText,
                PayloadT = $"{o.PayloadT:0.#}",
                Haul = o.HaulKm > 1e-6 ? $"{o.HaulKm:0.##}" : "—",
                Cycle = o.CycleMin > 1e-6 ? $"{o.CycleMin:0.#}" : "—",
                State = StateText(o),
                ActLoadAt = o.ActualLoadHour.HasValue ? DispatchClock.Hm(o.ActualLoadHour.Value) : "",
                ActDumpAt = o.ActualDumpHour.HasValue ? DispatchClock.Hm(o.ActualDumpHour.Value) : "",
                Delay = DelayText(o),
            })
            .ToList();

        foreach (var r in rows)
            r.TruckHead = r.Driver.Length > 0 ? $"{r.Truck}（{r.Driver}）" : r.Truck;
        _rows = rows;

        // 按卡车分组：一台车一组，组头即该车的当班行程（司机拿到手就是自己这一段）
        var cv = new DataGridCollectionView(rows);
        cv.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Row.TruckHead)));
        grid.ItemsSource = cv;

        // ★ 一条车次都没有时**把表整个藏起来**，换成写明原因的空态面板。
        //   一排空表头答不了"为什么是空的"。
        bool empty = rows.Count == 0;
        grid.IsVisible = !empty;
        emptyBox.IsVisible = empty;
        if (empty) BuildEmptyState(_skips);

        BuildKpi();
    }

    /// <summary>
    /// 空态：一条车次都没排出来时，把**引擎自己给的原因**分组写出来。
    ///
    /// <para><b>不在这里重新判一遍</b>：展不开的判定条件全在 <see cref="DispatchEngine.Solve"/> /
    /// <c>ExpandOne</c> 里，界面再写一份"我猜它是被什么卡的"就是两处说同一件事 ——
    /// 实测踩过：这里说「解不出单车载重」，而下面引擎逐条写着「尚未配车」，
    /// 同一屏两句话互相矛盾。现在只按 <see cref="DispatchPlan.Skips"/> 分组，原话原样带出来。</para>
    /// </summary>
    private void BuildEmptyState(IReadOnlyList<TripSkip>? skips)
    {
        string scope = ShiftFilter.Length > 0 ? ShiftFilter : "全天";

        if (skips == null || skips.Count == 0)
        {
            emptyTitle.Text = $"{scope}没有可展开的采装任务 —— 没有采装就没有车可派。";
            emptyBody.Text = "派车单是「任务 → 车次」的下沉，它不自己产生活。"
                           + "请先到「生产任务编制」确认本班排出了采装任务；"
                           + "本班确实不采（检修班 / 非作业日）时，这里空着是对的。";
            return;
        }

        // 按"被哪一条卡的"分组 —— 四种缺口的补法完全不同，并成一句就等于什么也没说
        var by = skips.GroupBy(x => x.Kind).OrderByDescending(g => g.Count()).ToList();
        var main = by[0];

        emptyTitle.Text = $"{scope}共 {skips.Count} 笔采装展不开车次"
                        + (by.Count > 1 ? $"（{by.Count} 种原因）" : "")
                        + " —— " + HeadOf(main.Key, main.Count());

        var sb = new System.Text.StringBuilder();
        foreach (var g in by)
        {
            var zones = g.Select(x => x.WorkZone).Where(z => !string.IsNullOrWhiteSpace(z)).Distinct().ToList();
            sb.AppendLine($"◆ {HeadOf(g.Key, g.Count())}");
            sb.AppendLine("   作业面：" + string.Join("、", zones.Take(8))
                        + (zones.Count > 8 ? $" 等 {zones.Count} 个" : ""));
            sb.AppendLine("   补法：" + FixOf(g.Key));
            sb.AppendLine();
        }
        sb.Append("逐笔原话见下方「展开说明」——那里写着每一笔各自被哪一条卡住。");
        emptyBody.Text = sb.ToString();
    }

    /// <summary>四种原因各自的一句话（与引擎的分类一一对应，不另起口径）。</summary>
    private static string HeadOf(TripSkipKind k, int n) => k switch
    {
        TripSkipKind.NoDestination =>
            $"{n} 笔**没有卸点**：缺去向 ⇒ 无运距 ⇒ 解不出循环时间 T_c，而车次是 s(i,k)=起始+(i−1)τ_L+(k−1)T_c 排出来的。",
        TripSkipKind.NoHaulDistance =>
            $"{n} 笔**运距没解出来**（路网 / 手填 / 兜底三层全空），算不出循环时间。",
        TripSkipKind.NoFleetSolution =>
            $"{n} 笔**编组没解出来**（τ_L / T_c / 载重），算不出装车节拍。",
        TripSkipKind.NoTrucks =>
            $"{n} 笔**尚未配车**：有铲没车，排不出趟。",
        _ => $"{n} 笔展不开。",
    };

    private static string FixOf(TripSkipKind k) => k switch
    {
        TripSkipKind.NoDestination =>
            "到「作业面台账」给这些面填去向（或在「流向分配」里自动定）。同一批面在「任务下达」那边也正被同一条闸拦着。",
        TripSkipKind.NoHaulDistance =>
            "到「去向台账」补卸点坐标，或在「作业面台账」手填运距。",
        TripSkipKind.NoFleetSolution =>
            "到「编制配置」确认这些面解出了编组 —— 运距解得出来才有 T_c 与配车数。",
        TripSkipKind.NoTrucks =>
            "到「作业面台账」给这些面配车（或在「班组派工」里把车配到编组上），配完回这里点「展开车次」。",
        _ => "见下方「展开说明」。",
    };

    /// <summary>
    /// 抬头 KPI：本班车次总数 / 总吨量 / 总运输功（＋卡车、铲、平均运距、运输线数）。
    /// <para>
    /// 混采任务的车次已按份额分摊到各条线（煤趟填破碎站 2.6km、岩趟填内排场 1.26km），
    /// 故这里的总运输功与加权平均运距是**逐趟累加**出来的真数——
    /// 此前整条任务都按主去向的运距算，混采面的运输功会被系统性地算错一大截。
    /// </para>
    /// </summary>
    private void BuildKpi()
    {
        kpi.Children.Clear();
        double t = _orders.Sum(o => o.PayloadT);
        double work = _orders.Sum(o => o.TransportWorkTKm);
        double loose = _orders.Sum(o => o.PayloadLooseM3);
        int trucks = _orders.Select(o => o.TruckId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int shovels = _orders.Select(o => o.ShovelId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int lines = _orders.Select(LineKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        double avgKm = t > 1e-6 ? work / t : 0;

        // 实绩进度：已卸 / 按此刻应卸。两个数都来自车次状态机，回填一趟就动一次
        double now = SampleTaskBoard.NowHour;
        int dumped = _orders.Count(o => o.Status == DispatchOrderStatus.Dumped);
        int due = _orders.Count(o => o.PlannedDumpHour <= now && o.Status != DispatchOrderStatus.Cancelled);

        // 已卸车次的**实方**——这是派车单侧唯一能与「实绩录入」的实绩 m³ 对账的口径
        //（吨/松方都不是作业量口径）。PayloadInSituM3 原先引擎写了、零读。
        double doneInSitu = _orders.Where(o => o.Status == DispatchOrderStatus.Dumped).Sum(o => o.PayloadInSituM3);

        // 解析车次：运输笔那一侧的数（承运吨 ÷ 载重）。与左边的展开数并排放，
        // 两个数一眼就能对上或对不上 —— 单摆一个就永远不会有人去核。
        int planTrips = _haulRef.Sum(o => o.TripCount);

        // ★ 一条车次都没有时，这些格子写「—」而不是 0（OD2 那条纪律）。
        //   "0 万t" 说的是"算过了，结果是零"；而这里是**根本没排出车次**，那个数没有出处。
        //   两者混成一个 0，看板上就再也分不出"今天不拉料"和"排不出来"。
        bool none = _orders.Count == 0;
        string N(double v, string fmt) => none ? "—" : v.ToString(fmt);

        bool tripGap = planTrips > 0 && Math.Abs(_orders.Count - planTrips) > Math.Max(1, planTrips * 0.05);
        kpi.Children.Add(Tile("本班车次", none ? "—" : $"{_orders.Count}", "趟（时刻展开）", 0x38, 0x7B, 0xD5));
        kpi.Children.Add(Tile("解析车次", planTrips > 0 ? $"{planTrips}" : "—", "趟（运输笔·承运吨÷载重）",
                              tripGap ? (byte)0xD9 : (byte)0x4A, tripGap ? (byte)0x8B : (byte)0x63, tripGap ? (byte)0x1F : (byte)0x7B));
        kpi.Children.Add(Tile("已卸 / 应卸", none ? "—" : $"{dumped}/{due}", "趟", dumped < due ? (byte)0xD9 : (byte)0x1D,
                              dumped < due ? (byte)0x8B : (byte)0x9E, dumped < due ? (byte)0x1F : (byte)0x75));
        kpi.Children.Add(Tile("已卸实方", doneInSitu > 0 ? $"{doneInSitu:N0}" : "—", "m³实方（对账实绩录入）", 0x1D, 0x9E, 0x75));
        kpi.Children.Add(Tile("总吨量", N(t / 1e4, "0.###"), "万 t", 0x1D, 0x9E, 0x75));
        kpi.Children.Add(Tile("总运输功", N(work / 1e4, "0.###"), "万 t·km", 0xB4, 0x7A, 0x1F));
        kpi.Children.Add(Tile("加权平均运距", N(avgKm, "0.##"), "km", 0x6B, 0x5C, 0xC4));
        kpi.Children.Add(Tile("运输松方", N(loose / 1e4, "0.###"), "万 m³松", 0x4A, 0x63, 0x7B));
        kpi.Children.Add(Tile("投入", none ? "—" : $"{trucks} / {shovels}", "车 / 铲", 0x4A, 0x63, 0x7B));
        kpi.Children.Add(Tile("运输线", none ? "—" : $"{lines}", "物料×卸点", 0x4A, 0x63, 0x7B));
    }

    /// <summary>
    /// 状态文案。★ 计划态还要分清是不是**任务已下达**：派车指令是承诺，
    /// 跑在下达之前的车次必须在单据上写明，否则一张预览稿会被当成正式派车单发下去。
    /// </summary>
    private static string StateText(DispatchOrder o)
        => o.Status == DispatchOrderStatus.Planned && string.IsNullOrWhiteSpace(o.InstanceId)
            ? "计划（任务未下达）"
            : o.Status.Label();

    /// <summary>延误文案：卸车优先（那才是这趟到没到位），没有就看装车；都没回填留空。</summary>
    private static string DelayText(DispatchOrder o)
    {
        double? d = o.DumpDelayMin ?? o.LoadDelayMin;
        if (d == null) return "";
        string who = o.DumpDelayMin.HasValue ? "卸" : "装";
        return $"{who} {(d.Value >= 0 ? "+" : "")}{d.Value:0}";
    }

    /// <summary>一条运输线 = 物料 × 卸点（混采任务一条任务会出两条线）。</summary>
    private static string LineKey(DispatchOrder o)
        => $"{o.MaterialCode}|{(string.IsNullOrWhiteSpace(o.SinkId) ? o.SinkName : o.SinkId)}";

    /// <summary>
    /// 分线明细：每条「物料 → 卸点」各发了几趟、多少吨、跑多远。
    /// 混采任务的煤趟与岩趟在这里分得清清楚楚，与甘特上的车次条一一对得上
    /// （两边共用 TruckTripPlanner 的同一个分摊函数）。
    /// </summary>
    private string LineBreakdown()
    {
        if (_orders.Count == 0) return "";

        var groups = _orders.GroupBy(LineKey, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count <= 1) return "";

        var parts = groups
            .Select(g => new
            {
                Name = $"{g.First().MaterialName}→{(string.IsNullOrWhiteSpace(g.First().SinkName) ? g.First().SinkId : g.First().SinkName)}",
                Trips = g.Count(),
                Ton = g.Sum(o => o.PayloadT),
                Km = g.Sum(o => o.PayloadT) > 1e-6 ? g.Sum(o => o.TransportWorkTKm) / g.Sum(o => o.PayloadT) : 0,
            })
            .OrderByDescending(x => x.Trips)
            .Select(x => $"{x.Name} {x.Trips} 趟 · {x.Ton:N0} t · {x.Km:0.##} km");

        return Environment.NewLine + "分线：" + string.Join("　｜　", parts)
             + "（混采任务按车次份额分摊，与甘特车次条同一套分摊）";
    }

    private static Border Tile(string title, string value, string unit, byte r, byte g, byte b)
    {
        var box = new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(14, 8, 14, 9),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            BorderThickness = new Thickness(0, 0, 0, 3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(r, g, b)),
            MinWidth = 130,
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        line.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        line.Children.Add(new TextBlock { Text = unit, FontSize = 11, Margin = new Thickness(4, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)) });
        sp.Children.Add(line);
        box.Child = sp;
        return box;
    }

    // ── 打印（与生产任务书同一套：横向纸张；Avalonia 无打印对话框 → 出横向 A4 PDF）────────────

    private async void OnPrint()
    {
        if (_orders.Count == 0) { toolStatus.Text = "无车次可打印"; return; }

        string scope = ShiftFilter.Length > 0 ? ShiftFilter : "全天";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出派车单 PDF（横向 A4；打印请用 PDF 查看器）",
            SuggestedFileName = $"派车单 {_date} {scope}.pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });
        if (file == null) return;
        try
        {
            var tiles = kpi.Children.OfType<Border>().Select(bd =>
            {
                var sp = (StackPanel)bd.Child!;
                var line = (StackPanel)sp.Children[1];
                return (((TextBlock)sp.Children[0]).Text ?? "", ((TextBlock)line.Children[0]).Text ?? "", ((TextBlock)line.Children[1]).Text ?? "");
            }).ToList();
            DispatchOrderPdf.Save(file.Path.LocalPath, $"派车单 {_date} {scope}", subTitle.Text ?? "", tiles, _rows, noteText.Text ?? "");
            toolStatus.Text = $"已导出 PDF（横向 A4）：{file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            toolStatus.Text = "打印失败：" + ex.Message;
        }
    }
}
