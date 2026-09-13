// 忠实移植自原 PitMine3D Modules/TaskLib/ShiftOps/ShiftProcessWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF RadioButton 模板班次页签 → 同观感的 ToggleButton 样式；DatePicker → Avalonia DatePicker；OpenFileDialog → StorageProvider。
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.ShiftOps;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.Views.TaskLib;
using SinkRegistryLoader = PitMine3D.Kylin.TaskLib.Engine.SinkRegistryLoader;   // 与 Kylin 旧切片 Data.SinkRegistryLoader 消歧
using ProductionPlanContext = PitMine3D.Kylin.TaskLib.Engine.ProductionPlanContext;
using ShiftWindow = PitMine3D.Kylin.TaskLib.Engine.ShiftWindow;
using ChainStage = PitMine3D.Kylin.TaskLib.ShiftOps.ChainStage;   // Engine 里另有同名（生产链体检的阶段），本窗用的是班内工序链的

/// <summary>
/// 班内工艺·工序系统推演 —— <b>任务</b>尺度那一层。
///
/// <para><b>三个尺度各管各的，口径互不通用</b>：中长远（年）回答"这个矿怎么开"；短期（月/旬）回答"这个月挖哪儿"；
/// <b>本窗</b>回答"这个班怎么干、卡在哪"：一个班里工序怎么互锁（穿孔备孔 → 爆破成堆 → 铲装上车 → 车拉去向 → 推土摊平）、
/// 每条工艺线的能力配比、此刻系统卡在哪（铲等车 / 车等铲 / 排土受限）、以及工序内部逐趟的节拍与属性预测。</para>
///
/// <para><b>画面三层</b>：正射影像做地、工艺线与工序标记跑在上面、底部一条班内工序链把时间轴摊开。
/// 班内时钟是主控件 —— 拖到哪一刻，三层同时就是那一刻的状态。</para>
/// </summary>
public sealed class ShiftProcessWindow : Window
{
    private readonly SimPanelOverlay _panel = new();
    private readonly SimBasemapRaster _photo = new();
    private readonly ShiftChainStrip _chain = new();
    private SimPanelHost? _mapHost;

    private DayProcessPlan _plan = new();
    private int _shiftIndex;
    private double _phase;                       // 班内相位 0..1
    private bool _loading = true;
    private bool _photoTried;
    private string _photoWhy = "";
    private string _status = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private double _speed = 1.0;
    private bool _playing;

    private DateTime _day = DateTime.Today;

    // 组名（overlay 批量组，X: 前缀天然隔离）
    private const string GLine = "ops.line";
    private const string GLineTag = "ops.line.tag";
    private const string GStep = "ops.step";
    private const string GStepTag = "ops.step.tag";
    private const string GBottle = "ops.bottleneck";
    private const string GZone = "ops.zone";          // 作业区域填充（面通道）
    private const string GZoneEdge = "ops.zone.edge"; // 作业区域轮廓
    private const string GZoneTag = "ops.zone.tag";   // 区域名 + 来源

    /// <summary>作业区域几何（工序区 → 面级 → 无）。一期一份，Reload 时重取。</summary>
    private ShiftZoneGeometry? _zones;

    /// <summary>本轮配地的结果文案。</summary>
    private string _zoneNote = "";

    /// <summary>作业区域是按期存的 —— 与 StageSimPlayer 的 PeriodOfWorkDate 同一口径。</summary>
    private static string PeriodOf(DateTime day)
        => day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    // ── 控件（与原 XAML x:Name 一一对应）──
    private readonly TextBlock titleText;
    private readonly DatePicker dpDay = new() { Width = 270, Margin = new Thickness(0, 0, 4, 0) };   // Avalonia DatePicker 三栏（月/日/年）比 WPF 的宽，150 只剩「九月」
    private readonly StackPanel shiftTabs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button btnPlay;
    private readonly ComboBox cbSpeed = new() { Width = 72 };
    private readonly Slider clockSlider = new() { Minimum = 0, Maximum = 1, SmallChange = 0.005, LargeChange = 0.05, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock clockText = new() { Text = "—", FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox cbProc = new() { Width = 92, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox chkPhoto = Chk("正射影像", "「基础数据 · 影像底图」里配好的那张，自动装载。\n影像只回答「这块地长什么样」——不参与任何量、不定位任何东西。", 4);
    private readonly CheckBox chkZones = Chk("作业区域", "把每条工艺线/每道工序画在它自己的那块地上（工序作业区 → 可采区域，逐级退）。\n深浅随完成比例走：没开工只描边、干着的逐渐加深、干完压暗。");
    private readonly CheckBox chkLines = Chk("工艺线", "采装面 → 去向 的运料方向。线随班内时钟亮/暗：这条线此刻在不在流。\n线宽 ∝ 在途车数（Little 定律 n = λ×W），不是随便画粗细。");
    private readonly CheckBox chkSteps = Chk("工序标记", "穿孔 / 爆破 / 排土 / 检修·空闲 的位置与状态。");
    private readonly CheckBox chkTags = Chk("铭牌", null);
    private readonly CheckBox chkBottleneck = Chk("瓶颈标注", "把此刻卡住的那条线标出来：铲等车 / 车等铲 / 排土受限。");
    private readonly StackPanel statePanel = new() { Margin = new Thickness(12, 10) };
    private readonly ScrollViewer chainScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 230 };
    private readonly TextBlock statusText = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public ShiftProcessWindow()
    {
        Title = "班内工艺·工序系统推演";
        TaskUi.Place(this, 1560, 900);
        WindowState = WindowState.Maximized;

        _mapHost = new SimPanelHost(_panel);

        // 点工序链上任一行 ⇒ 右侧展开那条线的细衔接（逐趟节拍 + 属性预测）
        _chain.RowClicked = key =>
        {
            _selected = string.Equals(_selected, key, StringComparison.Ordinal) ? "" : key;
            Render();
        };

        _panel.SetGroupWidth(GLine, 3.0);
        _panel.SetGroupWidth(GBottle, 5.0);

        _timer.Tick += OnTick;

        // ① 标题
        var header = TaskUi.Header("班内工艺·工序系统推演", "一个班里工序怎么互锁、系统此刻卡在哪 —— 穿孔备孔 → 爆破成堆 → 铲装上车 → 车拉去向 → 推土摊平");
        titleText = (TextBlock)((StackPanel)header.Child!).Children[0];

        // ② 班次 + 班内时钟
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var c0 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        c0.Children.Add(ToolLabel("日期"));
        var prevDay = Btn("前一天", () => ShiftDay(-1), 0, new Thickness(8, 3)); prevDay.Margin = new Thickness(0, 0, 4, 0); c0.Children.Add(prevDay);
        dpDay.SelectedDate = new DateTimeOffset(_day);
        dpDay.SelectedDateChanged += (_, _) => OnDayPicked();
        c0.Children.Add(dpDay);
        var nextDay = Btn("后一天", () => ShiftDay(+1), 0, new Thickness(8, 3)); nextDay.Margin = new Thickness(0, 0, 14, 0); c0.Children.Add(nextDay);
        c0.Children.Add(ToolLabel("班次"));
        c0.Children.Add(shiftTabs);
        Grid.SetColumn(c0, 0); bar.Children.Add(c0);

        var c1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        btnPlay = Btn("▶ 播放", () => { if (_playing) StopPlay(); else StartPlay(); }, 80, new Thickness(6, 3)); btnPlay.Margin = new Thickness(0, 0, 6, 0); c1.Children.Add(btnPlay);
        var rew = Btn("⏮", () => { _phase = 0; Render(); }, 38, new Thickness(6, 3)); rew.Margin = new Thickness(0, 0, 4, 0); ToolTip.SetTip(rew, "回到班首"); c1.Children.Add(rew);
        var spd = ToolLabel("速度"); spd.Margin = new Thickness(8, 0, 6, 0); c1.Children.Add(spd);
        foreach (var s in new[] { "0.5×", "1×", "2×", "4×" }) cbSpeed.Items.Add(new ComboBoxItem { Content = s });
        cbSpeed.SelectedIndex = 1;
        ToolTip.SetTip(cbSpeed, "一个班（8 小时）压成几秒推完。");
        cbSpeed.SelectionChanged += (_, _) => OnSpeed();
        c1.Children.Add(cbSpeed);
        Grid.SetColumn(c1, 1); bar.Children.Add(c1);

        // 班内时钟：这个窗的主控件 —— 拖它就是拖时间
        ToolTip.SetTip(clockSlider, "班内时钟。拖到哪一刻，图上就是那一刻的工序系统状态。");
        clockSlider.ValueChanged += (_, e) => OnClockDragged(e.NewValue);
        var c2 = new Grid { Margin = new Thickness(18, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        c2.Children.Add(clockSlider);
        Grid.SetColumn(c2, 2); bar.Children.Add(c2);

        var c3 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(clockText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        c3.Children.Add(clockText);
        var pl = new TextBlock { Text = "工序", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        TaskUi.Theme(pl, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        c3.Children.Add(pl);
        foreach (var s in new[] { "全景", "穿孔", "爆破", "采装", "运输", "排土" }) cbProc.Items.Add(new ComboBoxItem { Content = s });
        cbProc.SelectedIndex = 0;
        ToolTip.SetTip(cbProc, "全景 = 五道工序一起看比例；选一道就只画那一道，横向比各个面");
        cbProc.SelectionChanged += (_, _) => Reload();
        c3.Children.Add(cbProc);
        var refresh = Btn("刷新数据", OnRefresh, 76); refresh.Margin = new Thickness(0, 0, 6, 0); c3.Children.Add(refresh);
        c3.Children.Add(Btn("关闭", Close, 60));
        Grid.SetColumn(c3, 3); bar.Children.Add(c3);
        var barBox = TaskUi.Bar(bar, top: true);

        // ③ 显示开关
        var layers = new WrapPanel { Orientation = Orientation.Horizontal };
        var lh = ToolLabel("图面"); lh.FontWeight = FontWeight.Bold; layers.Children.Add(lh);
        foreach (var cb in new[] { chkPhoto, chkZones, chkLines, chkSteps, chkTags, chkBottleneck })
            cb.IsCheckedChanged += (_, _) => { if (!_loading) Render(); };
        layers.Children.Add(chkPhoto);
        var pick = Btn("换影像…", OnPickPhoto, 64, new Thickness(6, 2)); pick.Margin = new Thickness(0, 0, 12, 0); layers.Children.Add(pick);
        layers.Children.Add(chkZones); layers.Children.Add(chkLines); layers.Children.Add(chkSteps); layers.Children.Add(chkTags); layers.Children.Add(chkBottleneck);
        var sep = new Border { Width = 1, Margin = new Thickness(4, 2, 12, 2) }; TaskUi.Theme(sep, Border.BackgroundProperty, "Theme.Surface.Border"); layers.Children.Add(sep);
        layers.Children.Add(ToolLabel("视角"));
        var vt = Btn("俯视", () => { _panel.Camera.TiltDeg = 90; _panel.Camera.AzimuthDeg = 0; Render(); }, 52, new Thickness(6, 2)); vt.Margin = new Thickness(0, 0, 4, 0); layers.Children.Add(vt);
        var vi = Btn("轴测", () => { _panel.Camera.TiltDeg = 55; _panel.Camera.AzimuthDeg = 30; Render(); }, 52, new Thickness(6, 2)); vi.Margin = new Thickness(0, 0, 4, 0); layers.Children.Add(vi);
        layers.Children.Add(Btn("充满", () => { _fitted = false; Render(); }, 52, new Thickness(6, 2)));
        var layersBox = TaskUi.Bar(layers, top: true, padY: 6);

        // ④ 图面 + 系统状态（图面固定深色：正射影像与工序配色按深底调过）
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var mapGrid = new Grid();
        mapGrid.Children.Add(_mapHost);
        mapGrid.Children.Add(new TextBlock { Text = "右键拖 = 转视角　中键拖 = 平移　滚轮 = 缩放", VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 8), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)), IsHitTestVisible = false });
        var mapBox = new Border { Margin = new Thickness(8), CornerRadius = new CornerRadius(3), Background = CanvasBg, BorderBrush = CanvasBorder, BorderThickness = new Thickness(1), Child = mapGrid };
        Grid.SetColumn(mapBox, 0); body.Children.Add(mapBox);
        var stateBox = new Border { Width = 380, Margin = new Thickness(0, 8, 8, 8), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = statePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        TaskUi.Theme(stateBox, Border.BackgroundProperty, "Theme.Surface.Background");
        TaskUi.Theme(stateBox, Border.BorderBrushProperty, "Theme.Surface.Border");
        Grid.SetColumn(stateBox, 1); body.Children.Add(stateBox);

        // ⑤ 班内工序链
        _chain.Margin = new Thickness(0, 4);
        chainScroll.Content = _chain;
        var chainBox = new Border { Margin = new Thickness(8, 0, 8, 8), CornerRadius = new CornerRadius(3), Background = CanvasBg, BorderBrush = CanvasBorder, BorderThickness = new Thickness(1), Child = chainScroll };

        // ⑥ 状态栏
        TaskUi.Theme(statusText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(statusText, top: false, padY: 5);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(header, 0); Grid.SetRow(barBox, 1); Grid.SetRow(layersBox, 2); Grid.SetRow(body, 3); Grid.SetRow(chainBox, 4); Grid.SetRow(foot, 5);
        root.Children.Add(header); root.Children.Add(barBox); root.Children.Add(layersBox); root.Children.Add(body); root.Children.Add(chainBox); root.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = root;

        Opened += (_, _) =>
        {
            _loading = false;
            Reload();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static readonly IBrush CanvasBg = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x1F));
    private static readonly IBrush CanvasBorder = new SolidColorBrush(Color.FromRgb(0x29, 0x38, 0x4F));

    private static TextBlock ToolLabel(string t)
    {
        var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
        TaskUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return tb;
    }

    private static CheckBox Chk(string text, string? tip, double right = 12)
    {
        var cb = new CheckBox { Content = text, IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, right, 0) };
        if (tip != null) ToolTip.SetTip(cb, tip);
        return cb;
    }

    private static Button Btn(string text, Action onClick, double minWidth, Thickness? padding = null)
    {
        var b = TaskUi.Btn(text, onClick, minWidth);
        b.Margin = new Thickness(0); b.MinWidth = minWidth;
        if (padding != null) b.Padding = padding.Value;
        return b;
    }

    private ShiftProcessSystem? Current
        => _shiftIndex >= 0 && _shiftIndex < _plan.Shifts.Count ? _plan.Shifts[_shiftIndex] : null;

    private double ClockHour => Current is { } s ? s.ClockAt(_phase) : double.NaN;

    // ═════════════════════════ 装载 ═════════════════════════

    /// <summary>全景一句话：五道工序各占多少条、多少工时（按**全量**算，与筛选无关）。</summary>
    private string _panorama = "";

    /// <summary>下拉选中的工序；「全景」返回 null。</summary>
    private ProcessType? PickedProcess()
    {
        string s = (cbProc.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全景";
        return s switch
        {
            "穿孔" => ProcessType.Drill,
            "爆破" => ProcessType.Blast,
            "采装" => ProcessType.Load,
            "运输" => ProcessType.Haul,
            "排土" => ProcessType.Dump,
            _ => null,
        };
    }

    private static string ProcZh(ProcessType p) => p switch
    {
        ProcessType.Drill => "穿孔",
        ProcessType.Blast => "爆破",
        ProcessType.Load => "采装",
        ProcessType.Haul => "运输",
        ProcessType.Dump => "排土",
        _ => p.ToString(),
    };

    /// <summary>全景比例：五道工序各自的条数与占用工时（Σ 条长），按工序链序排。<b>比例看的是工时不是条数</b>。</summary>
    private static string Panorama(IReadOnlyList<ProductionTask>? tasks)
    {
        var all = (tasks ?? Array.Empty<ProductionTask>()).Where(t => t != null).ToList();
        if (all.Count == 0) return "";

        var order = new[] { ProcessType.Drill, ProcessType.Blast, ProcessType.Load, ProcessType.Haul, ProcessType.Dump };
        double total = all.Where(t => order.Contains(t.Process)).Sum(t => Math.Max(0, t.EndHour - t.StartHour));
        if (total <= 1e-6) return "";

        var parts = new List<string>();
        foreach (var p in order)
        {
            var g = all.Where(t => t.Process == p).ToList();
            if (g.Count == 0) continue;
            double h = g.Sum(t => Math.Max(0, t.EndHour - t.StartHour));
            parts.Add($"{ProcZh(p)} {g.Count} 条/{h:0.#}h（{h / total * 100:0}%）");
        }
        return parts.Count == 0 ? "" : "全景：" + string.Join("　·　", parts);
    }

    private void Reload()
    {
        if (_loading) return;
        var bits = new List<string>();

        List<ProductionTask> tasks;
        List<ShiftWindow> shifts = new();
        List<FaceInput> faces = new();
        try
        {
            // ★ **读当日真盘子，不读样例盘子**：ProductionPlanContext 才是全模块共用的那一份当日盘子
            tasks = ProductionPlanContext.Day() ?? new List<ProductionTask>();
            var cfg = ProductionPlanContext.Config();
            if (cfg != null) { shifts = cfg.Shifts ?? new(); faces = cfg.Faces ?? new(); }
            // 作业区域几何：一期一份，逐面配地（工序区 → 面级 → 无）
            _zones = ShiftZoneGeometry.Load(PeriodOf(_day), cfg?.Sinks);
        }
        catch (Exception ex)
        {
            tasks = new List<ProductionTask>();
            bits.Add($"当日盘子读不到（{ex.GetType().Name}）—— 请先在「生产任务编制」出一次任务。");
            // 装不出盘子时**不退样例**：样例推演每个数都自洽，而它说的是别的矿。
        }

        // 去向台账：工艺线要画"面 → 去向"的真方向，两端都得有坐标
        SinkRegistry? sinks = null;
        try { sinks = SinkRegistryLoader.Current; }
        catch (Exception ex) { bits.Add($"去向台账读不到（{ex.GetType().Name}）⇒ 运料方向画不出来"); }

        // ── 全景比例（先按**全量**统计，再筛）── 比例必须在筛之前算
        _panorama = Panorama(tasks);

        // ── 单工序筛选 ── 选一道就只画那一道（横向比各个面）；全景则五道一起画。
        var pick = PickedProcess();
        if (pick.HasValue)
        {
            int before = tasks.Count;
            tasks = tasks.Where(t => t != null && t.Process == pick.Value).ToList();
            bits.Add($"只看**{ProcZh(pick.Value)}**：{tasks.Count}/{before} 条"
                   + (tasks.Count == 0 ? "　◆ 这一道今天一条都没有（图是空的，不是没排活）" : ""));
        }

        _plan = DayProcessPlan.Build(tasks, shifts, faces, _day, sinks, _zones);
        if (_shiftIndex >= _plan.Shifts.Count) _shiftIndex = 0;

        BuildShiftTabs();
        EnsurePhoto();
        ComputeAnchor();          // 必须在装完影像之后 —— 参照系就是影像

        bits.Add($"当日盘子 {tasks.Count} 条任务　·　{_plan.Shifts.Count} 个班");
        if (_panorama.Length > 0) bits.Add(_panorama);
        if (_photoWhy.Length > 0) bits.Add(_photoWhy);
        if (_anchorNote.Length > 0) bits.Add(_anchorNote);
        if (_lineNote.Length > 0) bits.Add(_lineNote);
        if (_zones != null && _zones.SourceLabel.Length > 0) bits.Add(_zones.SourceLabel);
        foreach (var n in _zones?.Notes ?? new List<string>()) bits.Add(n);
        foreach (var n in _plan.Notes) bits.Add(n);
        _status = string.Join("　·　", bits);

        _phase = 0;
        Render();
        if (Current is { Lines.Count: > 0 }) StartPlay();
    }

    /// <summary>班次页签（原 WPF ShiftTab 样式：选中蓝底白字粗体，未选 Surface 底灰字）。</summary>
    private void BuildShiftTabs()
    {
        shiftTabs.Children.Clear();
        for (int i = 0; i < _plan.Shifts.Count; i++)
        {
            var s = _plan.Shifts[i];
            int idx = i;
            var tb = new ToggleButton { MinWidth = 74, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(14, 4), IsChecked = i == _shiftIndex, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(3) };
            var tx = new TextBlock { Text = s.ShiftName, HorizontalAlignment = HorizontalAlignment.Center };
            tb.Content = tx;
            ApplyTabLook(tb, tx, i == _shiftIndex);
            ToolTip.SetTip(tb, $"{ShiftChainStrip.Hm(s.StartHour)}–{ShiftChainStrip.Hm(s.EndHour)}　"
                             + $"工艺线 {s.Lines.Count} 条 · 其他工序 {s.Steps.Count} 项");
            tb.IsCheckedChanged += (_, _) =>
            {
                if (tb.IsChecked != true) { if (idx == _shiftIndex) tb.IsChecked = true; return; }   // 单选：不许把选中的取消掉
                _shiftIndex = idx; _phase = 0;
                foreach (var (o, j) in shiftTabs.Children.OfType<ToggleButton>().Select((o, j) => (o, j)))
                { if (j != idx) o.IsChecked = false; ApplyTabLook(o, (TextBlock)o.Content!, j == idx); }
                Render();
            };
            shiftTabs.Children.Add(tb);
        }
    }

    private static void ApplyTabLook(ToggleButton tb, TextBlock tx, bool on)
    {
        if (on)
        {
            tb.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)); tb.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            tx.Foreground = Brushes.White; tx.FontWeight = FontWeight.Bold;
        }
        else
        {
            TaskUi.Theme(tb, BackgroundProperty, "Theme.Surface.Background"); TaskUi.Theme(tb, BorderBrushProperty, "Theme.Surface.Border");
            TaskUi.Theme(tx, TextBlock.ForegroundProperty, "Theme.Text.Muted"); tx.FontWeight = FontWeight.Normal;
        }
    }

    /// <summary>影像：只认「基础数据 · 影像底图」配好的那张，配一次全系统通用。</summary>
    private void EnsurePhoto()
    {
        if (_photoTried) return;
        _photoTried = true;
        string path = Shading.OrthophotoConfig.ResolvePath(out string why);
        _photoWhy = why;
        if (path.Length == 0) return;
        if (!_photo.Load(path)) _photoWhy = _photo.Message;
    }

    // ═════════════════════════ 渲染 ═════════════════════════

    private void Render()
    {
        if (_loading) return;
        var sys = Current;

        double clock = ClockHour;
        clockText.Text = double.IsNaN(clock) ? "—" : ShiftChainStrip.Hm(clock);
        titleText.Text = sys == null
            ? "班内工艺·工序系统推演"
            : $"班内工艺·工序系统推演 · {_day:yyyy-MM-dd} · {sys.ShiftName}";

        if (Math.Abs(clockSlider.Value - _phase) > 1e-6) clockSlider.Value = _phase;

        _photo.PushTo(_panel, chkPhoto.IsChecked == true, PhotoZ(), 1.0);
        PushZones(sys, clock);     // 先画地 —— 面在底层，盖不住上面的线与标记
        PushLines(sys, clock);
        PushSteps(sys, clock);
        FitIfNeeded();

        _chain.SetSystem(sys, clock);
        _chain.Width = Math.Max(600, chainScroll.Bounds.Width > 1 ? chainScroll.Bounds.Width : 900);
        _chain.Height = _chain.DesiredHeight;

        BuildStatePanel(sys, clock);
        statusText.Text = _zoneNote.Length > 0 ? _status + "　·　" + _zoneNote : _status;
        _panel.RequestRender();
    }

    /// <summary>影像铺在最低的作业面之下 —— 它是底图，不能把标记切掉一半。</summary>
    private double PhotoZ()
    {
        var all = _plan.Shifts.SelectMany(s => s.Lines.Select(l => l.Z)
                                    .Concat(s.Steps.Select(x => x.Z)))
                              .Where(z => Math.Abs(z) > 1e-6).ToList();
        return all.Count == 0 ? 0 : all.Min() - 2.0;
    }

    /// <summary>工艺线：采装面 → 去向。线宽 ∝ 在途车数（Little 定律），亮暗 = 此刻流不流。去向没有坐标时不画。</summary>
    private void PushLines(ShiftProcessSystem? sys, double clock)
    {
        if (sys == null || chkLines.IsChecked != true)
        { _panel.Clear(GLine); _panel.Clear(GLineTag); _panel.Clear(GBottle); return; }

        var xyz = new List<double>(); var col = new List<uint>();
        var bx = new List<double>(); var bc = new List<uint>();
        var tz = new List<double>(); var tt = new List<string>();
        var tc = new List<uint>(); var th = new List<float>();
        float h = LabelHeight();

        int noDest = 0;
        foreach (var l in sys.Lines.Where(l => l.HasPosition))
        {
            bool on = l.ActiveAt(clock);
            byte a = on ? (byte)0xFF : (byte)0x55;
            uint rgb = 0x3B82F6u;
            var (fx, fy, fz) = Anchor(l.X, l.Y, l.Z);
            var bn = l.BottleneckAt(clock);

            // 运料方向 = 面 → **去向的真坐标**。没有去向坐标就**不画** —— 指向不明的线比没有线更误导。
            if (l.HasDestPosition)
            {
                var (dx, dy, dz) = Anchor(l.DestX, l.DestY, l.DestZ);
                xyz.Add(fx); xyz.Add(fy); xyz.Add(fz + 3);
                xyz.Add(dx); xyz.Add(dy); xyz.Add(dz + 3);
                col.Add(((uint)a << 24) | rgb);

                if (on && chkBottleneck.IsChecked == true && bn != Bottleneck.None)
                {
                    bx.Add(fx); bx.Add(fy); bx.Add(fz + 5);
                    bx.Add(dx); bx.Add(dy); bx.Add(dz + 5);
                    bc.Add(0xFFEF4444u);
                }
            }
            else noDest++;

            if (on && chkTags.IsChecked == true)
            {
                tz.Add(fx); tz.Add(fy); tz.Add(fz + 8);
                tt.Add($"{l.Shovel}＋{l.Trucks.Count}车　在途 {l.TrucksInTransit:0.#}台"
                     + (bn != Bottleneck.None ? $"　⚠{bn.Label()}" : ""));
                tc.Add(0xFF000000u | (bn != Bottleneck.None ? 0xEF4444u : rgb));
                th.Add(h);
            }
        }
        _lineNote = noDest == 0 ? ""
            : $"⚠ {noDest} 条工艺线**没画运料方向**：去向在台账里没有坐标。"
            + "在「排土场台账 / 卸载点」把坐标填上就能画出真实流向 —— 这一层不拿别的点凑一个方向。";

        _panel.SetLines(GLine, xyz.ToArray(), col.ToArray(), col.Count);
        _panel.SetLines(GBottle, bx.ToArray(), bc.ToArray(), bc.Count);
        _panel.SetLabels(GLineTag, tz.ToArray(), tt, tc.ToArray(), th.ToArray(), null, null, tt.Count);
    }

    /// <summary>
    /// 作业区域：把每条工艺线 / 每道工序**画在它自己的那块地上**。
    /// <para><b>随时钟动</b>：未开工只描边、进行中按完成比例加深、已完成压暗。</para>
    /// <para><b>来源要标在脸上</b>：「工序区」与「面级」画出来都是一块地，说的却是两件事。</para>
    /// </summary>
    private void PushZones(ShiftProcessSystem? sys, double clock)
    {
        if (sys == null || chkZones.IsChecked != true)
        {
            _panel.Clear(GZone); _panel.Clear(GZoneEdge); _panel.Clear(GZoneTag);
            _zoneNote = "";
            return;
        }

        var tri = new List<double>(); var triCol = new List<uint>();
        var seg = new List<double>(); var segCol = new List<uint>();
        var tz = new List<double>(); var tt = new List<string>(); var tc = new List<uint>();
        var th = new List<float>();
        float h = LabelHeight();

        int byProc = 0, byFace = 0, none = 0, broken = 0;

        // 采装线与不搬料的工序统一按「(名字, 环, 颜色, 进度, 在不在干)」摊平处理
        var items = new List<(string Name, IReadOnlyList<SimPoint> Ring, double Z, string Src, uint Rgb, double Prog, bool On)>();

        foreach (var l in sys.Lines)
        {
            if (!l.HasRing) { none++; continue; }
            var c = ShiftChainStrip.StageColor(ChainStage.Load);
            items.Add((l.Zone, l.Ring, l.RingZ, l.RingSource,
                       (uint)((c.R << 16) | (c.G << 8) | c.B), l.ProgressAt(clock), l.ActiveAt(clock)));
        }
        foreach (var st in sys.Steps)
        {
            if (!st.HasRing) { none++; continue; }
            var c = ShiftChainStrip.StageColor(st.Stage);
            uint rgb = st.IsIdle ? 0x9CA3AFu : (uint)((c.R << 16) | (c.G << 8) | c.B);
            double dur = Math.Max(0, st.EndHour - st.StartHour);
            double prog = dur < 1e-9 ? (clock >= st.EndHour ? 1 : 0)
                                     : Math.Clamp((clock - st.StartHour) / dur, 0, 1);
            items.Add(($"{st.Stage.Label()}{(st.Equipment.Length > 0 ? " " + st.Equipment : "")}",
                       st.Ring, st.RingZ, st.RingSource, rgb, prog, st.ActiveAt(clock)));
        }

        foreach (var it in items)
        {
            if (string.Equals(it.Src, ShiftZoneGeometry.SrcProcess, StringComparison.Ordinal)) byProc++;
            else byFace++;

            // 透明度随进度走：没开工 0x1E、干着的按完成比例加深、干完压到 0x5A —— 三态一眼分得出，不靠颜色
            byte a = it.On ? (byte)(0x30 + 0x70 * Math.Clamp(it.Prog, 0, 1))
                   : it.Prog >= 1 ? (byte)0x5A : (byte)0x1E;

            if (!RingMesh.Triangles(it.Ring, it.Z, out var xyz, out int nt)) { broken++; }
            else
            {
                for (int t = 0; t < nt; t++)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        var (ax, ay, az) = Anchor(xyz[t * 9 + v * 3], xyz[t * 9 + v * 3 + 1], xyz[t * 9 + v * 3 + 2]);
                        tri.Add(ax); tri.Add(ay); tri.Add(az);
                    }
                    triCol.Add(((uint)a << 24) | it.Rgb);
                }
            }

            // 轮廓线：填充失败（环自交）时它仍然画得出来，至少"这块地在哪"不会丢
            uint edge = ((uint)(it.On ? 0xFF : 0x88) << 24) | it.Rgb;
            for (int i = 0; i < it.Ring.Count; i++)
            {
                var p = it.Ring[i]; var q = it.Ring[(i + 1) % it.Ring.Count];
                var (x1, y1, z1) = Anchor(p.X, p.Y, it.Z);
                var (x2, y2, z2) = Anchor(q.X, q.Y, it.Z);
                seg.Add(x1); seg.Add(y1); seg.Add(z1 + 0.5);
                seg.Add(x2); seg.Add(y2); seg.Add(z2 + 0.5);
                segCol.Add(edge);
            }

            if (it.On && chkTags.IsChecked == true)
            {
                var (cx, cy) = Centroid(it.Ring);
                var (lx, ly, lz) = Anchor(cx, cy, it.Z);
                tz.Add(lx); tz.Add(ly); tz.Add(lz + 3);
                tt.Add($"{it.Name}　{it.Prog * 100:0}%"
                     + (string.Equals(it.Src, ShiftZoneGeometry.SrcFace, StringComparison.Ordinal) ? "（面级轮廓）" : ""));
                tc.Add(0xFF000000u | it.Rgb);
                th.Add(h);
            }
        }

        _panel.SetFaces(GZone, tri.Count > 0 ? tri.ToArray() : null, triCol.ToArray(), triCol.Count);
        _panel.SetLines(GZoneEdge, seg.ToArray(), segCol.ToArray(), segCol.Count);
        _panel.SetLabels(GZoneTag, tz.ToArray(), tt, tc.ToArray(), th.ToArray(), null, null, tt.Count);

        // ★ 配地结果如实报账：「工序区」「面级」「没有」三档差别很大，画面上却分不出。
        var bits = new List<string>();
        if (byProc > 0) bits.Add($"{byProc} 块按工序区");
        if (byFace > 0) bits.Add($"{byFace} 块退回面级轮廓（整个面一块地，穿孔/爆破的位置对不上）");
        if (none > 0) bits.Add($"⚠ {none} 项没配到地（只能打点）");
        if (broken > 0) bits.Add($"⚠ {broken} 块环自交，填不出面（只描了边）");
        _zoneNote = bits.Count == 0
            ? "作业区域：本班没有可画的地。"
            : "作业区域：" + string.Join(" · ", bits)
              + (_zones != null && _zones.Notes.Count > 0 ? "　" + _zones.Notes[0] : "");
    }

    /// <summary>环的形心（标签落点）。退化环返回首点，不返回 (0,0)。</summary>
    private static (double X, double Y) Centroid(IReadOnlyList<SimPoint> ring)
    {
        if (ring.Count == 0) return (0, 0);
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            double cr = p.X * q.Y - q.X * p.Y;
            a += cr; cx += (p.X + q.X) * cr; cy += (p.Y + q.Y) * cr;
        }
        if (Math.Abs(a) < 1e-9) return (ring[0].X, ring[0].Y);
        return (cx / (3 * a), cy / (3 * a));
    }

    /// <summary>工序标记：穿孔 / 爆破 / 排土 / 检修·空闲。三态与工序链条带同一套读法。</summary>
    private void PushSteps(ShiftProcessSystem? sys, double clock)
    {
        if (sys == null || chkSteps.IsChecked != true)
        { _panel.Clear(GStep); _panel.Clear(GStepTag); return; }

        var pts = new List<double>(); var col = new List<uint>();
        var siz = new List<float>(); var sty = new List<byte>();
        var tz = new List<double>(); var tt = new List<string>();
        var tc = new List<uint>(); var th = new List<float>();
        float h = LabelHeight();

        foreach (var s in sys.Steps.Where(s => s.HasPosition))
        {
            // 与工艺线走**同一套**锚点校正，否则两层会画在相隔几公里的两处
            var (sx, sy, sz) = Anchor(s.X, s.Y, s.Z);
            bool on = s.ActiveAt(clock);
            bool done = clock >= s.EndHour;
            byte a = on ? (byte)0xFF : done ? (byte)0x88 : (byte)0x66;
            var c = ShiftChainStrip.StageColor(s.Stage);
            uint rgb = s.IsIdle ? 0x9CA3AFu : (uint)((c.R << 16) | (c.G << 8) | c.B);

            pts.Add(sx); pts.Add(sy); pts.Add(sz + 2);
            col.Add(((uint)a << 24) | rgb);
            siz.Add(on ? (s.IsIdle ? 7f : 11f) : 5f);
            sty.Add((byte)(s.Stage == ChainStage.Blast ? SimMarkerStyle.Cross
                         : on ? SimMarkerStyle.Dot : SimMarkerStyle.Circle));

            if (on && chkTags.IsChecked == true)
            {
                tz.Add(sx); tz.Add(sy); tz.Add(sz + 6);
                tt.Add(s.IsIdle
                    ? (s.Equipment.Length > 0 ? s.Equipment : "空闲")
                    : $"{s.Stage.Label()}{(s.Equipment.Length > 0 ? " " + s.Equipment : "")}");
                tc.Add(0xFF000000u | rgb);
                th.Add(h);
            }
        }

        _panel.SetMarkers(GStep, pts.ToArray(), col.ToArray(), siz.ToArray(), sty.ToArray(), col.Count);
        _panel.SetLabels(GStepTag, tz.ToArray(), tt, tc.ToArray(), th.ToArray(), null, null, tt.Count);
    }

    /// <summary>铭牌字高按相机比例反算 —— 写死世界米在拉远时会被渲染端整批省绘。</summary>
    private float LabelHeight()
        => (float)Math.Clamp(13.0 / Math.Max(1e-6, _panel.Camera.Scale), 3.0, 400.0);

    private string _lineNote = "";
    private (double Dx, double Dy, double Dz) _anchor;
    private string _anchorNote = "";

    /// <summary>把作业坐标搬到影像所在的坐标系（若判定为不同原点）。</summary>
    private (double X, double Y, double Z) Anchor(double x, double y, double z)
        => (x + _anchor.Dx, y + _anchor.Dy, z + _anchor.Dz);

    /// <summary>锚点校正：作业面坐标与**影像**不是同一个原点时，把整簇刚性搬过去。参照系选影像。</summary>
    private void ComputeAnchor()
    {
        _anchor = (0, 0, 0); _anchorNote = "";
        if (!_photo.IsLoaded) return;

        var pts = _plan.Shifts.SelectMany(s => s.Lines.Where(l => l.HasPosition)
                                    .Select(l => new ActivityAnchor.Pt(l.X, l.Y, l.Z))
                                .Concat(s.Steps.Where(x => x.HasPosition)
                                    .Select(x => new ActivityAnchor.Pt(x.X, x.Y, x.Z))))
                              .ToList();
        if (pts.Count == 0) return;

        // 参照点 = 影像四至的网格采样（影像本身没有"点"，用它的覆盖面代表）
        var (ix0, iy0, ix1, iy1) = _photo.Extent;
        var refPts = new List<ActivityAnchor.Pt>();
        for (int i = 0; i <= 8; i++)
            for (int j = 0; j <= 8; j++)
                refPts.Add(new ActivityAnchor.Pt(ix0 + (ix1 - ix0) * i / 8.0,
                                                 iy0 + (iy1 - iy0) * j / 8.0, double.NaN));

        var r = ActivityAnchor.Compute(pts, refPts);
        if (!r.Fired) return;
        _anchor = (r.Dx, r.Dy, r.Dz);
        _anchorNote = $"⚠ 作业面坐标与**影像不是同一个原点**：整簇落在影像范围外 {r.SeparationM / 1000:0.##} km。"
                    + "作业面台账没填坐标时任务盘子回落**样例盘子**，样例基准点写死在代码里、与本矿无关。"
                    + $"已按刚性 Δ=({r.Dx:+0;-0}, {r.Dy:+0;-0}) m 搬到影像中心：各面**相互**的方位距离原样保留，"
                    + "但**绝对位置是样例的、不是实测的**。在「作业面台账」填 source_x/y 即可落到真位置。";
    }

    private bool _fitted;

    private void FitIfNeeded()
    {
        if (_fitted) return;
        if (!_panel.TryGetWorldBounds(out double x0, out double y0, out double z0,
                                      out double x1, out double y1, out double z1)) return;
        _panel.Camera.FitTo(x0, y0, z0, x1, y1, z1);
        _fitted = true;
    }

    // ═════════════════════════ 右侧：系统状态 ═════════════════════════

    private void BuildStatePanel(ShiftProcessSystem? sys, double clock)
    {
        statePanel.Children.Clear();
        if (sys == null) { Para("尚无本班数据。"); return; }

        Section($"此刻　{ShiftChainStrip.Hm(clock)}　{sys.ShiftName}");

        var act = sys.ActiveLines(clock).ToList();
        var bn = sys.BottleneckAt(clock);
        Kv("在流工艺线", $"{act.Count} / {sys.Lines.Count} 条", bold: true);
        Kv("系统瓶颈", bn.Label(), bold: true,
           brush: bn == Bottleneck.None ? "#FF22C55E" : "#FFEF4444");

        // ── 选中一条线 ⇒ 展开它的细衔接 ──
        var sel = _selected.StartsWith("line:", StringComparison.Ordinal)
            ? sys.Lines.FirstOrDefault(l => $"line:{l.Zone}:{l.Shovel}" == _selected)
            : null;
        if (sel != null) { BuildDetail(sel, clock); return; }

        if (sys.Lines.Count > 0)
            Para("↓ 点下面工序链里的任一行，展开那条线**工序内部**的细衔接：逐趟节拍、每一段的时长/量/速度，以及铲和车各等多久。");

        if (sys.Lines.Count == 0)
            Para("本班没有采装线 —— 工序系统这一层没有料在流。画面上的静止是真的。");

        foreach (var l in sys.Lines.OrderBy(l => l.StartHour))
        {
            bool on = l.ActiveAt(clock);
            Section((on ? "▶ " : "　") + (l.Zone.Length > 0 ? l.Zone : "(未记面)"));
            Kv("　设备", $"{l.Shovel}　配 {l.Trucks.Count} 车（荐 {l.RecommendedTrucks}）");
            Kv("　去向", l.Destination.Length > 0 ? l.Destination : "未定");
            Kv("　时窗", $"{ShiftChainStrip.Hm(l.StartHour)}–{ShiftChainStrip.Hm(l.EndHour)}"
                       + (on ? $"　已完成 {l.ProgressAt(clock):P0}" : ""));
            Kv("　编组班产", $"{l.GroupCapacityM3PerH:0} m³/h　（= min(铲装能力, 车队运力)，排产给的）");
            Kv("　运距 / 周转", $"{l.HaulKm:0.##} km　/　{l.CycleH * 60:0.#} min");
            Kv("　在途车数 n", $"{l.TrucksInTransit:0.##} 台　（λ={l.TripsPerHour:0.#} 车/h × W={l.CycleH:0.###} h）");
            Para(l.BottleneckWhy);
        }

        // 不搬料的那几道
        var steps = sys.Steps.Where(s => !s.IsIdle).ToList();
        if (steps.Count > 0)
        {
            Section($"其他工序（{steps.Count} 项 · 不搬料）");
            foreach (var s in steps.OrderBy(s => s.StartHour))
                Kv((s.ActiveAt(clock) ? "▶ " : "　") + s.Stage.Label(),
                   $"{(s.Zone.Length > 0 ? s.Zone : "—")}　{s.Equipment}　"
                 + $"{ShiftChainStrip.Hm(s.StartHour)}–{ShiftChainStrip.Hm(s.EndHour)}");
        }

        var idle = sys.Steps.Where(s => s.IsIdle).ToList();
        if (idle.Count > 0)
        {
            Section($"停机 / 空闲（{idle.Count} 台）");
            foreach (var g in idle.GroupBy(s => s.IdleWhy.Length > 0 ? s.IdleWhy : "（排产未给原因）"))
                Kv(g.Key, string.Join("、", g.Select(s => s.Equipment).Where(x => x.Length > 0).Distinct()));
        }

        Section("本班量");
        Kv("采装出方", $"{sys.LoadedM3 / 1e4:0.##} 万m³实方");
        Kv("排土承接", $"{sys.DumpAcceptM3 / 1e4:0.##} 万m³占容");
        foreach (var n in sys.Notes) Para(n);

        if (_photo.IsLoaded)
        {
            Section("影像底图");
            Para(_photo.Message);
            Para("影像**不参与任何量、不定位任何东西** —— 作业位置来自作业面档案的源坐标。");
        }
    }

    private string _selected = "";
    private readonly Dictionary<string, LineDetail> _detailCache = new(StringComparer.Ordinal);

    /// <summary>展开一条工艺线**工序内部**的细衔接与属性预测（预测值不是实测：装卸时长与车速是示意参数）。</summary>
    private void BuildDetail(ProcessLine l, double clock)
    {
        string key = $"line:{l.Zone}:{l.Shovel}";
        if (!_detailCache.TryGetValue(key, out var d))
        {
            d = new LineDetail(l).Predict();
            _detailCache[key] = d;
        }

        Section($"▣ {(l.Zone.Length > 0 ? l.Zone : "(未记面)")}　工序内部细衔接");
        Para("（再点一次那一行可收起，回到全线概览）");

        Section("节拍 —— 谁慢谁定速");
        Kv("铲的节拍 t", $"{d.ShovelTactH * 60:0.##} min　（就位 {d.Params.SpotMin:0.#} + 装车 {d.Params.LoadMin:0.#}）");
        Kv("车的节拍 W/n", double.IsInfinity(d.TruckTactH) ? "∞（没配车）"
            : $"{d.TruckTactH * 60:0.##} min　（周转 {d.CycleH * 60:0.#} min ÷ {l.Trucks.Count} 台）");
        Kv("定速方", d.Kind.Label(), bold: true,
           brush: d.Kind == Bottleneck.None ? "#FF22C55E" : "#FFEF4444");
        Kv("铲作业率", $"{d.ShovelUtil:P0}　（空转合计 {d.ShovelWaitH * 60:0.#} min）",
           brush: d.ShovelUtil > 0.85 ? "#FF22C55E" : d.ShovelUtil > 0.6 ? "#FFF59E0B" : "#FFEF4444");
        if (d.TruckQueueH > 1e-9) Kv("车排队合计", $"{d.TruckQueueH * 60:0.#} min");

        Section("整班预测");
        Kv("趟数", $"{d.Trips} 趟");
        Kv("出方", $"{d.PredictedM3 / 1e4:0.###} 万m³实方　（{d.Params.TruckM3:0.#} m³/车 × {d.Trips}）");
        Kv("排产口径", l.GroupCapacityM3PerH > 1e-9
            ? $"{l.GroupCapacityM3PerH * l.DurationH / 1e4:0.###} 万m³　（编组班产 {l.GroupCapacityM3PerH:0} m³/h × {l.DurationH:0.##} h）"
            : "编组班产台账没给");

        Section($"此刻 {ShiftChainStrip.Hm(clock)} 各在干什么");

        // 「无动作」有三种，别混成一句：还没开工 / 已经收工 / 在窗内但这一刻恰好没段。
        string Idle() => clock < l.StartHour
            ? $"未开工（{ShiftChainStrip.Hm(l.StartHour)} 开）"
            : clock >= l.EndHour ? $"已收工（{ShiftChainStrip.Hm(l.EndHour)} 完）"
            : "（本刻无段）";

        var shovelAct = d.ShovelActAt(clock);
        Kv("铲 " + l.Shovel, shovelAct == null ? Idle()
            : $"{shovelAct.Act.Label()}　剩 {(shovelAct.EndH - clock) * 60:0.#} min",
           brush: shovelAct?.Act == CycleAct.ShovelWait ? "#FFEF4444" : null);
        foreach (var tk in l.Trucks)
        {
            var a = d.TruckActAt(tk, clock);
            Kv("　" + tk, a == null ? Idle()
                : $"{a.Act.Label()}"
                + (a.DistanceKm > 1e-9 ? $"　{a.SpeedKmh:0} km/h" : "")
                + $"　剩 {(a.EndH - clock) * 60:0.#} min");
        }
        Kv("在途车数", $"{d.TrucksAwayAt(clock)} / {l.Trucks.Count} 台（离开铲的）");

        Section("时间都花在哪儿了");
        foreach (var b in d.Breakdown())
            Kv(b.Act.Label(), $"{b.Hours * 60:0.#} min　{b.Share:P1}",
               brush: b.Act.IsProductive() ? null : "#FFF59E0B");

        Section("逐趟展开（前几趟）");
        foreach (var g in d.Segments.GroupBy(s => s.TripIndex).Take(6))
        {
            var seq = g.OrderBy(s => s.StartH).ToList();
            Kv($"第 {g.Key} 趟", string.Join(" → ", seq.Select(s =>
                $"{s.Act.Label()}{s.DurationH * 60:0.#}′")),
               tipText: string.Join("\n", seq.Select(s => s.Caption)));
        }

        Section("口径");
        Para($"装卸时长与车速是**示意参数**：就位 {d.Params.SpotMin:0.#} min、装车 {d.Params.LoadMin:0.#} min、"
           + $"卸载 {d.Params.UnloadMin:0.#} min、重车 {d.Params.FullKmh:0} km/h、空车 {d.Params.EmptyKmh:0} km/h、"
           + $"单车 {d.Params.TruckM3:0.#} m³实方。它们把节拍算成有量纲的数，**不进任何账** —— "
           + "要真值得接设备台账的实测循环时间。");
        foreach (var n in d.Notes) Para(n);
    }

    // ── 面板小工具 ───────────────────────────────────────────────────────────

    private void Section(string t)
    {
        var tb = new TextBlock { Text = t, FontWeight = FontWeight.Bold, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 5), TextWrapping = TextWrapping.Wrap };
        TaskUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Body");
        statePanel.Children.Add(tb);
    }

    private void Para(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return;
        var tb = new TextBlock { Text = t, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
        TaskUi.Theme(tb, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        statePanel.Children.Add(tb);
    }

    private void Kv(string k, string v, bool bold = false, string? brush = null, string? tipText = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 3), ColumnDefinitions = new ColumnDefinitions("1*,1.4*") };
        if (!string.IsNullOrWhiteSpace(tipText)) ToolTip.SetTip(g, tipText);
        var kt = new TextBlock { Text = k, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        TaskUi.Theme(kt, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var vt = new TextBlock { Text = v, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        if (brush != null) vt.Foreground = new SolidColorBrush(Color.Parse(brush));
        else TaskUi.Theme(vt, TextBlock.ForegroundProperty, "Theme.Text.Body");
        Grid.SetColumn(vt, 1);
        g.Children.Add(kt); g.Children.Add(vt);
        statePanel.Children.Add(g);
    }

    // ═════════════════════════ 播放 ═════════════════════════

    private void StartPlay()
    {
        _playing = true;
        btnPlay.Content = "⏸ 暂停";
        _timer.Start();
    }

    private void StopPlay()
    {
        _playing = false;
        btnPlay.Content = "▶ 播放";
        _timer.Stop();
    }

    private void OnTick(object? s, EventArgs e)
    {
        // 一个班压成 (8/speed) 秒：60ms 一帧
        double perTick = 0.06 / (8.0 / _speed);
        _phase += perTick;
        if (_phase >= 1.0)
        {
            _phase = 1.0;
            // 演完一个班自动接下一个 —— 一天是连着的，停在班末等人点是多余的一步
            if (_shiftIndex + 1 < _plan.Shifts.Count)
            {
                _shiftIndex++;
                _phase = 0;
                foreach (var (tb, i) in shiftTabs.Children.OfType<ToggleButton>().Select((r, i) => (r, i)))
                { tb.IsChecked = i == _shiftIndex; ApplyTabLook(tb, (TextBlock)tb.Content!, i == _shiftIndex); }
            }
            else StopPlay();
        }
        Render();
    }

    // ═════════════════════════ 事件 ═════════════════════════

    private void OnSpeed()
    {
        if (_loading || cbSpeed.SelectedItem is not ComboBoxItem it) return;
        _speed = (it.Content?.ToString() ?? "1×").TrimEnd('×') switch
        { "0.5" => 0.5, "2" => 2, "4" => 4, _ => 1 };
    }

    private void OnClockDragged(double newValue)
    {
        if (_loading) return;
        if (Math.Abs(newValue - _phase) < 1e-6) return;
        StopPlay();                       // 手拖就是要停下来看，不该跟播放打架
        _phase = Math.Clamp(newValue, 0, 1);
        Render();
    }

    private void ShiftDay(int d)
    {
        _day = _day.AddDays(d);
        dpDay.SelectedDate = new DateTimeOffset(_day);
    }

    private void OnDayPicked()
    {
        if (_loading || dpDay.SelectedDate is not { } d || d.Date == _day.Date) return;
        _day = d.Date;
        _fitted = false;
        Reload();
    }

    private void OnRefresh()
    {
        _photoTried = false;
        _fitted = false;
        Reload();
    }

    private async void OnPickPhoto()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择正射影像（需带地理配准）",
            AllowMultiple = false,
            FileTypeFilter = FilterOf(Shading.OrthophotoConfig.FileFilter),
        });
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;
        _photoTried = true;
        if (_photo.Load(path))
        {
            // 在哪儿选的都算配上了 —— 写回工程配置，别的功能直接就有
            try { Shading.OrthophotoConfig.Remember(path); } catch { }
            _photoWhy = $"本次手选，已写回工程配置：{System.IO.Path.GetFileName(path)}";
            chkPhoto.IsChecked = true;
        }
        else _photoWhy = _photo.Message;
        _fitted = false;
        Render();
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
}
