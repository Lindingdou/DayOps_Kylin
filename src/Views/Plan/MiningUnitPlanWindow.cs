using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad.Plan;        // CoalSinkAdapter（装卸点台账 → 出矿点）· MonthlyTargetStore（月煤量/排弃量/能力的唯一来源）
using PitMine3D.Kylin.Cad.Units;       // UnitPlanEngine / MineUnitAdapter / DumpOrder / FacePriority
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Views.GeoDb;
using PitMine3D.Kylin.Views.Road;
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 采掘单元台账（移植原 <c>PlanLib.Views.MiningUnitPlanWindow</c>）—— 采场采矿模型（煤 + 岩）与排土模型<b>合在一张表</b>里管，<b>按期次存取</b>。
///
/// <para><b>两层，分工是硬的</b>（见 <see cref="MonthlyUnitLedgerStore"/>）：<b>基表</b>是全量煤 + 岩 + 排土位置，跟着模型重算刷新；
/// <b>月度台账</b>一个期次一份，只存那个期次涉及的单元。</para>
/// <para><b>读写全部走 <see cref="MiningUnitLedger"/> 一份实现</b>；几何列以模型为准，推进序/期次/状态/完成度/去向/备注是人填或算法填的，重算时按 UnitId 保住。</para>
/// <para>原版 2026-08-18/19 现场令后主动作行只剩「一键排本月」+「分步▾」+「本期一览…」；派生比选 / 三维预览 / 指煤卸点 三个入口已撤（对应窗口原版也够不着，未移植）。</para>
/// </summary>
internal sealed class MiningUnitPlanWindow : Window
{
    private readonly ObservableCollection<UnitRow> _rows = new();
    private readonly DataGrid _grid = new();
    private readonly ComboBox _filterKind = new(), _filterStatus = new(), _filterPeriod = new();

    /// <summary>期次 = <b>选年 + 选月</b>拼出来，<b>不给手打</b>：选出来的东西不可能不合法。</summary>
    private readonly ComboBox _periodYear = new() { Width = 82, Height = 26, MinHeight = 0, Margin = new Thickness(0, 0, 4, 0) };
    private readonly ComboBox _periodMonth = new() { Width = 62, Height = 26, MinHeight = 0, Margin = new Thickness(0, 0, 8, 0) };
    /// <summary>该年月在台账目录里有没有存过 —— 「读回/删除」点下去之前就该看得见。</summary>
    private readonly TextBlock _periodHint = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly HashSet<string> _savedPeriods = new(StringComparer.Ordinal);

    /// <summary>几何明细列（带/幅/长/宽/厚/中心Z/流数）—— 默认收起来，勾上才显示。</summary>
    private readonly List<DataGridColumn> _geomCols = new();
    private DataGridColumn? _flowCol;
    private readonly CheckBox _showGeom = new() { Content = new TextBlock { Text = "几何明细列" }, IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    /// <summary>本期<b>落没落盘</b>的状态灯。</summary>
    private readonly TextBlock _landed = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
    private string _lastSaveNote = "";
    /// <summary>排产成功后顺手把当期落盘 —— 省掉"排完忘了存"这一步。</summary>
    private readonly CheckBox _autoSavePeriod = new() { Content = new TextBlock { Text = "排产后存期次" }, IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
    private readonly TextBlock _stat = new() { TextWrapping = TextWrapping.Wrap, FontFamily = RoadUi.Mono, FontSize = 12, LineHeight = 17 };
    private readonly Border _statCard = new();
    private readonly TextBlock _where = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };

    private MonthlyUnitLedgerStore _store = new();

    /// <summary>设备指派面板 —— <b>排产之后</b>把量摊到具体设备的具体工日上。它<b>不</b>参与排产。</summary>
    private readonly EquipmentAssignPanel _equip = new();

    // ── 月度排产的输入（U9/U12 那几条轴 + 目标）──
    private readonly TextBox _tCoal = Tb(70, PlanCase.MonthlyCoalWanT.ToString("0.#")), _tStrip = Tb(70, PlanCase.MonthlyStripWanM3.ToString("0.#")), _tCap = Tb(70, ""),
                             _tFill = Tb(46, PlanCase.VoidFillFactor.ToString("0.##"));
    private readonly ComboBox _cDump, _cFace, _cStrike, _cPair, _cSeg;
    /// <summary>上面三个框的数是<b>哪儿来的</b> —— 逐月配置表的那一行，还是手填的。</summary>
    private readonly TextBlock _tTargetInfo = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = "逐月配置表：尚未取" };

    private static TextBox Tb(double w, string t) => new() { Width = w, Height = 26, MinHeight = 0, Text = t, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private static TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 };
    private static ComboBox Cb(IEnumerable<string> items, double w) { var c = PlanUi.Combo(items, 0, w); c.Height = 26; c.MinHeight = 0; c.Margin = new Thickness(0, 0, 10, 0); return c; }

    /// <summary>表格一行 = 一个采掘单元 / 一个排土位置。<b>它包着 <see cref="MiningUnitLedger.Row"/> 而不是拷贝它</b>。</summary>
    public sealed class UnitRow : INotifyPropertyChanged
    {
        public MiningUnitLedger.Row Src { get; }
        public UnitRow(MiningUnitLedger.Row src) { Src = src; }

        public string UnitId => Src.UnitId;
        public string Kind => Src.KindText;
        public string Region => Src.Region;
        public string Seam => Src.Seam;
        public int Band => Src.Band;
        public int Panel => Src.Panel;
        public double CenterZ => Src.Cz;
        public double LengthM => Src.LengthM;
        public double WidthM => Src.WidthM;
        public double ThickM => Src.ThickM;
        public double Qty => Src.Qty;
        public string QtyUnit => Src.QtyUnit;

        public int Seq { get => Src.Seq; set { Src.Seq = value; Raise(nameof(Seq)); } }
        public string Period { get => Src.Period; set { Src.Period = value ?? ""; Raise(nameof(Period)); } }
        public string Status { get => Src.Status; set { Src.Status = value ?? ""; Raise(nameof(Status)); Raise(nameof(DonePct)); } }
        /// <summary>完成度按百分数显示。</summary>
        public double DonePct { get => Math.Round(Src.Done * 100, 1); set { Src.Done = Math.Max(0, Math.Min(1, value / 100.0)); Raise(nameof(DonePct)); } }
        public string Destination { get => Src.Destination; set { Src.Destination = value ?? ""; Raise(nameof(Destination)); } }
        /// <summary>运距。<b>没算过时显示空，不显示 0</b>。</summary>
        public string HaulText => Src.HaulKm.HasValue ? Src.HaulKm.Value.ToString("0.##") : "";
        /// <summary>拆到几个去向。<b>&gt;1 说明这个单元分流了</b>。</summary>
        public string FlowCount => Src.Flows.Count > 1 ? Src.Flows.Count.ToString() : "";
        public string Note { get => Src.Note; set { Src.Note = value ?? ""; Raise(nameof(Note)); } }

        public IBrush KindBrush => Kind switch { "煤" => new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), "岩" => new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)), "排土" => new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)), _ => Brushes.Gray };

        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        internal void RaiseAll() { foreach (var n in new[] { nameof(Seq), nameof(Period), nameof(Status), nameof(DonePct), nameof(Destination), nameof(HaulText), nameof(FlowCount), nameof(Note) }) Raise(n); }
    }

    private readonly IPlanEntityHost? _host;
    private CoalSinkPoint? _coalSink;

    public MiningUnitPlanWindow(IPlanEntityHost? host = null)
    {
        _host = host;
        Title = "采掘单元台账 · 采场与排土 · 按期次管理";
        PlanUi.Place(this, 1360, 780);
        MinWidth = 1040; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        // ── 标题栏（与「短期生产计划编制」同一组，用同一套橙色渐变）──
        var banner = PlanUi.Header("采掘单元台账", "采场采矿模型（煤 + 岩）与排土模型合表管理 · 按期次存取 · 按月煤量目标排产",
            Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xEA, 0x58, 0x0C));

        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };

        // ── ① 数据与期次 ──
        var bar1 = new WrapPanel { Orientation = Orientation.Horizontal };
        Btn(bar1, "取模型数据", TakeFromModels, 100, primary: true);
        Btn(bar1, "载入基表", LoadBase, 84);
        Btn(bar1, "保存基表", SaveBase, 84);
        Sep(bar1);
        bar1.Children.Add(Label("期次:"));
        bar1.Children.Add(_periodYear); bar1.Children.Add(Label("年")); bar1.Children.Add(_periodMonth); bar1.Children.Add(Label("月"));
        bar1.Children.Add(_periodHint);
        Btn(bar1, "删除期次", () => _ = DeletePeriodAsync(), 84);
        MenuBtn(bar1, "更多", 68, ("对齐期次标记…", () => _ = AlignPeriodMarksAsync()), ("台账目录…", () => _ = ChooseRootAsync()));
        var box1 = new StackPanel(); box1.Children.Add(bar1); box1.Children.Add(_where);
        var card1 = Card("① 数据与期次", box1);
        Grid.SetRow(card1, 0); root.Children.Add(card1);

        _periodYear.SelectionChanged += (_, _) => { if (!_pickerBusy) OnPeriodPicked(); };
        _periodMonth.SelectionChanged += (_, _) => { if (!_pickerBusy) OnPeriodPicked(); };

        // ── ② 月度排产（三行 WrapPanel：目标 · 规则 · 动作）──
        var barTarget = new WrapPanel { Orientation = Orientation.Horizontal };
        barTarget.Children.Add(Label("月煤量(万t):")); barTarget.Children.Add(_tCoal);
        barTarget.Children.Add(Label("排弃量(万m³):")); barTarget.Children.Add(_tStrip);
        barTarget.Children.Add(Label("剥离能力(万m³):")); barTarget.Children.Add(_tCap);
        barTarget.Children.Add(Label("回填比:"));
        ToolTip.SetTip(_tFill, "采空区回填比：内排累计占容 ≤ 已形成采空区 × 它（与「量驱动采剥接续」的 VoidFillFactor 同源）。\n它是操作性损失（坡道/排土坡面/工作面留空），实测每 0.1 ≈ 内排率 10.9 个百分点。\n留空 = 不卡这道闸（内排只受位置库容限制）。");
        barTarget.Children.Add(_tFill);

        var barAxes = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        barAxes.Children.Add(Label("排弃顺序:"));
        _cDump = Cb(Enum.GetValues<DumpOrder>().Select(UnitSchemeText.Dump), 132); barAxes.Children.Add(_cDump);
        barAxes.Children.Add(Label("作业面:"));
        _cFace = Cb(Enum.GetValues<FacePriority>().Select(UnitSchemeText.Face), 110); barAxes.Children.Add(_cFace);
        barAxes.Children.Add(Label("走向推进:"));
        _cStrike = Cb(Enum.GetValues<StrikeAdvance>().Select(UnitSchemeText.Strike), 96); barAxes.Children.Add(_cStrike);
        barAxes.Children.Add(Label("配对策略:"));
        _cPair = Cb(Enum.GetValues<PairingStrategy>().Select(UnitSchemeText.Pair), 108); barAxes.Children.Add(_cPair);
        barAxes.Children.Add(Label("标段:"));
        _cSeg = Cb(new[] { "1 段（整条带）", "2 段", "3 段" }, 116);
        ToolTip.SetTip(_cSeg, "把一条工作线沿走向切成几个标段，一段一台（组）设备同时推进。\n段内照旧沿台阶线连续采（不横切、不跳幅），段界按幅号整齐分。\n⚠ 切几段就最多有几个锋面幅（月末各留一个半截幅）——那正是几台设备同时干的形态。\n只有配了【作业面份额】时才真的分得开：没有份额时整面一个目标，第一段就把量吃完了。");
        barAxes.Children.Add(_cSeg);

        // 动作并进轴那一行：排产这一条只留【一键排本月】；分步动作收进「分步▾」
        var barAct = barAxes;
        Sep(barAct);
        Btn(barAct, "一键排本月", () => _ = RunMonthEndToEndAsync(), 104, primary: true);
        MenuBtn(barAct, "分步", 66, ("只排产·不落盘（试算）", RunSchedule), ("只落盘·用表里现有的行", () => _ = SavePeriodAsync()), ("读回期次", LoadPeriod));
        barAct.Children.Add(_landed);
        Btn(barAct, "本期一览…", ShowPeriodOverview, 96);
        Sep(barAct);
        barAct.Children.Add(_autoSavePeriod);

        var planBox = new StackPanel();
        planBox.Children.Add(barTarget); planBox.Children.Add(barAct); planBox.Children.Add(_tTargetInfo);
        RoadUi.Theme(_tTargetInfo, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var cardPlan = Card("② 月度排产", planBox, emphasize: true);
        Grid.SetRow(cardPlan, 1); root.Children.Add(cardPlan);

        // ── 筛选 ──
        var fl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        void Filter(string label, ComboBox cb, params string[] items)
        {
            fl.Children.Add(Label(label));
            cb.Width = 110; cb.Height = 26; cb.MinHeight = 0; cb.Margin = new Thickness(0, 0, 16, 0); cb.FontSize = 13;
            cb.Items.Add("(全部)"); foreach (var it in items) cb.Items.Add(it);
            cb.SelectedIndex = 0;
            cb.SelectionChanged += (_, _) => ApplyFilter();
            fl.Children.Add(cb);
        }
        Filter("类型:", _filterKind, "煤", "岩", "排土");
        Filter("状态:", _filterStatus, "未采", "在采", "已采");
        Filter("期次:", _filterPeriod, "(未排产)");
        ToolTip.SetTip(_showGeom, "带 / 幅 / 长 / 宽 / 厚 / 中心Z / 流数 —— 排产时用不上，默认收起来。");
        _showGeom.IsCheckedChanged += (_, _) => SyncGeomCols();
        fl.Children.Add(_showGeom);
        Grid.SetRow(fl, 2); root.Children.Add(fl);

        BuildGrid();
        var gridCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(1), Child = _grid, MinHeight = 150 };
        RoadUi.Theme(gridCard, Border.BackgroundProperty, "Theme.Panel.Background"); RoadUi.Theme(gridCard, Border.BorderBrushProperty, "Theme.Panel.Border");
        Grid.SetRow(gridCard, 3); root.Children.Add(gridCard);

        // ── ⑤ 设备指派（折叠着，排产之后自己展开）──
        Grid.SetRow(_equip, 4); root.Children.Add(_equip);

        _statCard.BorderThickness = new Thickness(1); _statCard.CornerRadius = new CornerRadius(6); _statCard.Padding = new Thickness(12, 8, 12, 9);
        _statCard.Margin = new Thickness(0, 8, 0, 0); _statCard.MaxHeight = 168;
        RoadUi.Theme(_statCard, Border.BackgroundProperty, "Theme.Panel.Background2"); RoadUi.Theme(_statCard, Border.BorderBrushProperty, "Theme.Panel.Border");
        RoadUi.Theme(_stat, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        _statCard.Child = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _stat };
        Grid.SetRow(_statCard, 5); root.Children.Add(_statCard);

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(banner, 0); Grid.SetRow(root, 1); shell.Children.Add(banner); shell.Children.Add(root);
        Content = shell;

        _rows.CollectionChanged += (_, _) => { Recalc(); SyncGeomCols(); };
        SyncGeomCols();
        RefreshWhere();
        SyncTargetRow();

        // 开窗自动载入基表 —— 只在【有基表】时载
        if (_store.HasBase)
        {
            LoadBase();
            _stat.Text += "\n（开窗自动载入基表。排产：选期次 → 按目标排产 → 存为期次。）";
        }
        else
        {
            _stat.Text = "还没有基表 —— 先在「采矿模型」「排土条带」各生成一次，再点「取模型数据」。\n已有台账时点「载入基表」。";
        }
        if (RootNote.Length > 0) _stat.Text = RootNote + "\n\n" + _stat.Text;
    }

    /// <summary>把一条工具栏包成带小标题的白卡。</summary>
    private static Border Card(string title, Control body, bool emphasize = false)
    {
        var head = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        RoadUi.Theme(head, TextBlock.ForegroundProperty, "Theme.Text.Primary");
        var stack = new StackPanel(); stack.Children.Add(head); stack.Children.Add(body);
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = stack };
        RoadUi.Theme(b, Border.BackgroundProperty, emphasize ? "Theme.Panel.Background2" : "Theme.Panel.Background");
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        return b;
    }

    /// <summary>加一个按钮。<paramref name="primary"/> = 这一组里的主操作。任何按钮里抛出来的异常都落到状态区。</summary>
    private void Btn(Panel host, string text, Action act, double w = 96, bool primary = false)
    {
        var b = RoadUi.Btn(text, () => { try { act(); } catch (Exception ex) { _stat.Text = "出错：" + ex.Message; } }, w, bold: primary, primary: primary);
        b.Height = 28;
        host.Children.Add(b);
    }

    private static void Sep(Panel host) => host.Children.Add(new TextBlock { Text = "│", Width = 18, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 });

    /// <summary>「收纳按钮」—— 点开一个菜单，低频动作放里面。</summary>
    private void MenuBtn(Panel host, string text, double w, params (string Label, Action Act)[] items)
    {
        var menu = new MenuFlyout();
        foreach (var (label, act) in items)
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => { try { act(); } catch (Exception ex) { _stat.Text = "出错：" + ex.Message; } };
            menu.Items.Add(mi);
        }
        var b = new Button { Content = text + " ▾", MinWidth = w, Height = 28, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center, Flyout = menu };
        host.Children.Add(b);
    }

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false; _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Extended;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.BorderThickness = new Thickness(0);
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.RowHeight = 26; _grid.FontSize = 12.5;
        _grid.FrozenColumnCount = 2;   // 前两列冻结：横向滚到方量/去向那边时，还看得见这是哪个单元

        DataGridTextColumn Col(string h, string path, double w, string? fmt = null)
        {
            var b = new Binding(path) { Mode = BindingMode.OneWay }; if (fmt != null) b.StringFormat = fmt;
            var c = new DataGridTextColumn { Header = h, Binding = b, Width = new DataGridLength(w), IsReadOnly = true };
            _grid.Columns.Add(c);
            return c;
        }
        DataGridTextColumn Geom(string h, string path, double w, string? fmt = null) { var c = Col(h, path, w, fmt); _geomCols.Add(c); return c; }

        Col("单元号", nameof(UnitRow.UnitId), 132);
        // 类型列上色：煤/岩/排土 三类一眼分得开。**颜色只是辅助**，文字仍在。
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "类型", Width = new DataGridLength(52), IsReadOnly = true, SortMemberPath = nameof(UnitRow.Kind),
            CellTemplate = new FuncDataTemplate<UnitRow>((_, _) =>
            {
                var t = new TextBlock { FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                t.Bind(TextBlock.TextProperty, new Binding(nameof(UnitRow.Kind)));
                t.Bind(TextBlock.ForegroundProperty, new Binding(nameof(UnitRow.KindBrush)));
                return t;
            }),
        });
        Col("采场/排土场", nameof(UnitRow.Region), 92);
        Col("层/台阶", nameof(UnitRow.Seam), 74);
        Geom("带", nameof(UnitRow.Band), 40);
        Geom("幅", nameof(UnitRow.Panel), 40);
        Geom("长m", nameof(UnitRow.LengthM), 54, "{0:0}");
        Geom("宽m", nameof(UnitRow.WidthM), 50, "{0:0}");
        Geom("厚m", nameof(UnitRow.ThickM), 52, "{0:0.0}");
        Col("量", nameof(UnitRow.Qty), 88, "{0:N0}");
        Col("单位", nameof(UnitRow.QtyUnit), 44);
        Geom("中心Z", nameof(UnitRow.CenterZ), 60, "{0:0}");
        Col("运距km", nameof(UnitRow.HaulText), 62);
        _flowCol = Geom("流数", nameof(UnitRow.FlowCount), 46);

        // ── 可编辑的几列 —— 排产就靠它们 ──
        void Edit(string h, string path, double w, string? fmt = null)
        {
            var b = new Binding(path) { Mode = BindingMode.TwoWay }; if (fmt != null) b.StringFormat = fmt;
            _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = b, Width = new DataGridLength(w) });
        }
        Edit("推进序", nameof(UnitRow.Seq), 58);
        Edit("期次", nameof(UnitRow.Period), 100);
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "状态", Width = new DataGridLength(70), IsReadOnly = true, SortMemberPath = nameof(UnitRow.Status),
            CellTemplate = new FuncDataTemplate<UnitRow>((_, _) =>
            {
                var cb = new ComboBox { ItemsSource = new[] { "", "未采", "在采", "已采" }, BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 0, Padding = new Thickness(4, 1), HorizontalAlignment = HorizontalAlignment.Stretch };
                void Sync() { if (cb.DataContext is UnitRow r) cb.SelectedItem = r.Status is "未采" or "在采" or "已采" ? r.Status : ""; }
                cb.DataContextChanged += (_, _) => Sync();
                cb.SelectionChanged += (_, _) => { if (cb.DataContext is UnitRow r && cb.SelectedItem is string s && s != r.Status) { r.Status = s; Dispatcher.UIThread.Post(() => { RefreshPeriodFilter(); Recalc(); }); } };
                Sync();
                return cb;
            }),
        });
        Edit("完成%", nameof(UnitRow.DonePct), 58, "{0:0.#}");
        Edit("去向", nameof(UnitRow.Destination), 150);
        Edit("备注", nameof(UnitRow.Note), 180);
        PlanUi.FitHeaders(_grid);
        _grid.ItemsSource = _rows;

        _grid.CellEditEnded += (_, _) => Dispatcher.UIThread.Post(() => { RefreshPeriodFilter(); Recalc(); }, DispatcherPriority.Background);

        // ── 批量编辑：表格右键 —— 这几个动作全都作用于「选中的行」──
        var cm = new ContextMenu();
        void MI(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => { try { act(); } catch (Exception ex) { _stat.Text = "出错：" + ex.Message; } };
            cm.Items.Add(mi);
        }
        MI("标为在采", () => SetStatus("在采"));
        MI("标为已采", () => SetStatus("已采"));
        MI("清除状态", () => SetStatus(""));
        cm.Items.Add(new Separator());
        MI("设为当前期次", () => SetPeriod(CurrentPeriod));
        MI("清除期次", () => SetPeriod(""));
        cm.Opening += (_, _) =>
        {
            int n = _grid.SelectedItems.Count;
            foreach (var o in cm.Items) if (o is MenuItem mi) mi.IsEnabled = n > 0;
            if (cm.Items.Count > 0 && cm.Items[0] is MenuItem first) ToolTip.SetTip(first, n > 0 ? $"作用于选中的 {n} 行" : "先在表里选中若干行");
        };
        _grid.ContextMenu = cm;
    }

    /// <summary>WPF Items.Refresh 的等价：整表重绑（保住选中）。行本身发通知，只在筛选/整体替换时调。</summary>
    private void RefreshGrid()
    {
        var sel = _grid.SelectedItems.Cast<object>().ToList();
        ApplyFilter();
        foreach (var s in sel) if (_grid.ItemsSource is IList<UnitRow> l && l.Contains((UnitRow)s)) _grid.SelectedItems.Add(s);
    }

    // ══ 基表 ══════════════════════════════════════════════════════

    private async Task ChooseRootAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选台账目录", AllowMultiple = false });
        if (folders.Count == 0) return;
        string dir = folders[0].Path.LocalPath;
        if (string.IsNullOrEmpty(dir)) { _stat.Text = "没取到目录。"; return; }
        _store = new MonthlyUnitLedgerStore(dir);
        RefreshWhere();
        _stat.Text = "台账目录已切到：" + dir + "\n" + _store.Overview();
    }

    private string RootNote => string.Equals(_store.Root, MonthlyUnitLedgerStore.DefaultRoot, StringComparison.OrdinalIgnoreCase) ? MonthlyUnitLedgerStore.DefaultRootNote : "";

    private void RefreshWhere()
    {
        string note = RootNote;
        _where.Text = "台账目录：" + _store.Root + (_store.HasBase ? "" : "（还没有基表）") + (note.Length > 0 ? "　◆ 目录有变动（悬停查看）" : "");
        ToolTip.SetTip(_where, note.Length > 0 ? note : null);
        RefreshPeriodPickers();
    }

    private bool _pickerBusy;

    /// <summary>刷年/月两个选择器。年份表 = 主体案例那一年 ± 2 年 <b>∪ 台账目录里已存在的年份</b>。</summary>
    private void RefreshPeriodPickers()
    {
        _savedPeriods.Clear();
        foreach (var m in _store.ListMonths()) _savedPeriods.Add(m);

        var years = new SortedSet<int>();
        for (int y = PlanCase.PlanYear - 2; y <= PlanCase.PlanYear + 2; y++) years.Add(y);
        foreach (var m in _savedPeriods) if (m.Length >= 4 && int.TryParse(m.Substring(0, 4), out int y)) years.Add(y);
        foreach (var r in _rows) if (r.Period.Length >= 4 && int.TryParse(r.Period.Substring(0, 4), out int y2)) years.Add(y2);

        int keepY = _periodYear.SelectedItem is int sy ? sy : PlanCase.PlanYear;
        int keepM = _periodMonth.SelectedItem is int sm ? sm : int.TryParse(PlanCase.DemoPeriod.Split('-')[1], out int dm) ? dm : 1;

        _pickerBusy = true;
        _periodYear.Items.Clear();
        foreach (int y in years) _periodYear.Items.Add(y);
        if (_periodMonth.Items.Count == 0) for (int m = 1; m <= 12; m++) _periodMonth.Items.Add(m);
        _periodYear.SelectedItem = years.Contains(keepY) ? keepY : years.Max;
        _periodMonth.SelectedItem = keepM;
        _pickerBusy = false;

        RefreshPeriodHint();
    }

    /// <summary>年或月一变：目标三个框换成那一期的行（或退回手填），并说清这一期存没存过。</summary>
    private void OnPeriodPicked() { RefreshPeriodHint(); RefreshLanded(); SyncTargetRow(); }

    /// <summary>「这一期存过没有」摆在按钮旁边。</summary>
    private void RefreshPeriodHint()
    {
        string p = CurrentPeriod;
        bool saved = p.Length > 0 && _savedPeriods.Contains(p);
        _periodHint.Text = p.Length == 0 ? "" : saved ? "◆ 已存过" : "· 未存过";
        _periodHint.Foreground = saved ? new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)) : new SolidColorBrush(Color.FromRgb(0x7a, 0x82, 0x8c));
        ToolTip.SetTip(_periodHint, saved ? $"台账目录里已经有「{p}」这一期：{_store.PeekSummary(p)}\n再存会整文件替换 —— 覆盖前会先把旧的那份拷进「历史」子目录。" : null);
    }

    /// <summary>几何明细列的显隐。<b>有分流单元时「流数」强制留着</b>。</summary>
    private void SyncGeomCols()
    {
        bool show = _showGeom.IsChecked == true;
        foreach (var c in _geomCols) c.IsVisible = show;
        if (!show && _flowCol != null && _rows.Any(r => r.Src.Flows.Count > 1)) _flowCol.IsVisible = true;
    }

    // ══ 预置：从两个生成器直接取，不经过文件 ═══════════════════════

    /// <summary>「取模型数据」—— 采矿模型（煤 + 岩）与排土条带<b>一次取齐</b>。缺哪一趟由这里如实报。</summary>
    private void TakeFromModels()
    {
        var got = new List<string>();
        var missed = new List<string>();

        var mine = MiningModelStore.ToLedgerRows();
        if (mine.Count > 0) { Merge(mine); got.Add($"采矿模型 {mine.Count} 行"); }
        else missed.Add("· 采矿模型：" + MiningModelStore.Caption);

        var cells = DumpStripStore.Last?.Cells;
        if (cells != null && cells.Count > 0)
        {
            Merge(MiningUnitLedger.FromDumpCells(cells));   // 排土场名从 Cell.Code 前缀取
            got.Add($"排土条带 {cells.Count} 个位置");
        }
        else missed.Add("· 排土条带：" + DumpStripStore.Caption);

        if (got.Count == 0) { _stat.Text = "两趟都没取到 —— 先去「采矿模型」「排土条带」各生成一次。\n" + string.Join("\n", missed); return; }

        _stat.Text = "已取：" + string.Join(" + ", got)
                   + (missed.Count > 0 ? "　◆ 另一趟没取到（排产会缺这一半的账）：\n" + string.Join("\n", missed) : "")
                   + "\n" + _stat.Text + "\n" + SaveRails();
    }

    /// <summary>把会话内的真轨落盘到台账目录（与基表同一个目录）—— 真轨与台账同时在手的唯一时刻。</summary>
    private string SaveRails()
    {
        try { UnitRailFile.SaveFromStores(_store.Root, out string note); return "· " + note; }
        catch (Exception ex) { return $"◆ 真轨落盘失败（{ex.GetType().Name}：{ex.Message}）—— 下次打开三维会退成盒子。"; }
    }

    private void LoadBase()
    {
        if (!_store.TryLoadBase(out var rows, out var issues)) { _stat.Text = string.Join("\n", issues); return; }
        Fill(rows);
        _stat.Text = $"基表已载入：{rows.Count} 行 · {MiningUnitLedger.Summary(rows)}" + (issues.Count > 0 ? "\n" + string.Join("\n", issues) : "");
    }

    /// <summary>保存基表 —— <b>按 UnitId 合进去，绝不用当前表整体覆盖</b>。</summary>
    private void SaveBase()
    {
        if (_rows.Count == 0) { _stat.Text = "表是空的，没什么可存的。"; return; }
        _store.TryLoadBase(out var baseRows, out _);
        int had = baseRows.Count;
        var merged = MiningUnitLedger.UpsertInto(baseRows, _rows.Select(r => r.Src).ToList(), out int updated, out int added);
        _store.SaveBaseRaw(merged);
        RefreshWhere();
        int kept = merged.Count - updated - added;
        _stat.Text = $"基表已保存：{merged.Count} 行（原 {had} 行 · 更新 {updated} · 新增 {added} · 原样保留 {kept}）\n→ {_store.BasePath}"
                   + (kept > 0 ? "\n· 当前表里没有的行按原样留在基表里 —— 保存基表不会删行。" : "");
    }

    /// <summary>供「采矿模型」「排土条带」生成后直接灌进来。<paramref name="replaceSameKind"/> 为真时先清掉同类的旧行。</summary>
    public void Merge(IReadOnlyList<MiningUnitLedger.Row> fresh, bool replaceSameKind = true)
    {
        var kinds = fresh.Select(r => r.Kind).Distinct().ToHashSet();
        var keep = replaceSameKind ? _rows.Select(r => r.Src).Where(r => !kinds.Contains(r.Kind)).ToList() : new List<MiningUnitLedger.Row>();
        // ⚠ 只能拿【同类】的旧行去比对
        var old = _rows.Select(r => r.Src).Where(r => kinds.Contains(r.Kind)).ToList();
        var merged = MiningUnitLedger.Merge(fresh, old, out var orphans);
        var final = keep.Concat(merged).ToList();
        Fill(final);
        _stat.Text = $"已并入 {fresh.Count} 行 · 现共 {final.Count} 行 · {MiningUnitLedger.Summary(final)}"
                   + (orphans.Count > 0 ? $"\n◆ 有 {orphans.Count} 个填过排产的单元在新模型里不存在了（如 {string.Join("、", orphans.Take(3).Select(o => o.UnitId))}） —— 它们已从表中移出，对应的排产要重排。" : "");
    }

    private void Fill(IReadOnlyList<MiningUnitLedger.Row> rows)
    {
        _rows.Clear();
        foreach (var r in rows) _rows.Add(new UnitRow(r));
        RefreshPeriodFilter();
        ApplyFilter();
    }

    // ══ 期次（月度台账）══════════════════════════════════════════

    private string CurrentPeriod => _periodYear.SelectedItem is int y && _periodMonth.SelectedItem is int m ? $"{y:0000}-{m:00}" : "";

    private async Task SavePeriodAsync() { _stat.Text = await SavePeriodCoreAsync(interactive: true); RefreshLanded(); }

    private bool _lastSaved;

    /// <summary>存当期，<b>返回回执文本而不自己写状态区</b>。已经存过就不是"保存"，是"顶掉"：交互 → 弹确认；自动 → 一律不覆盖。</summary>
    private async Task<string> SavePeriodCoreAsync(bool interactive)
    {
        _lastSaved = false;
        string p = CurrentPeriod;
        if (!MonthlyUnitLedgerStore.IsValidMonth(p, out string why)) return why;

        if (_store.Exists(p))
        {
            string had = _store.PeekSummary(p);
            int nowCount = _rows.Count(r => string.Equals(r.Period, p, StringComparison.Ordinal));
            if (!interactive)
                return $"◆ 期次「{p}」已经存过一份（{had}）—— **本次没有自动覆盖**。\n　 要用这次排产的结果（{nowCount} 行）替换它，点「存为期次」并确认；旧的那份会先拷进「历史」子目录。";

            bool ok = _selftestAutoConfirm || await CoalMsgBox.ConfirmAsync(this, "这一期已有台账",
                $"期次「{p}」已经存过一份，再存会整文件替换。\n\n已存的：{had}\n本次要写：{nowCount} 行\n\n旧的那份会先拷进「历史」子目录（带时间戳，可找回）。要覆盖吗？");
            if (!ok) return $"没有覆盖 —— 期次「{p}」还是原来那份（{had}）。";
        }
        return SavePeriodWrite(p, out _lastSaved);
    }

    private string SavePeriodWrite(string p, out bool saved)
    {
        saved = false;
        // 存的是【期次列等于它】的行，与当前筛选无关
        var rows = _rows.Where(r => string.Equals(r.Period, p, StringComparison.Ordinal)).Select(r => r.Src).ToList();
        if (rows.Count == 0)
            return $"表里没有期次 =「{p}」的行 —— 先在表里选中若干行，右键「设为当前期次」。（不存空文件）";

        // ★ 本期用到的【排土位置】一并存进去 —— 没有它们，这一期在下游就是【只有源没有汇】。
        int addedSlots = 0;
        var dumpIndex = DumpSlotCode.BuildIndex(_rows.Select(r => r.Src), out _, out var collided);
        if (dumpIndex.Count > 0)
        {
            var have = new HashSet<string>(rows.Select(r => r.UnitId), StringComparer.Ordinal);
            // ★ 去向码必须【先落成表】再遍历 —— 边遍历惰性链边往 rows 里加会抛 Collection was modified
            var destCodes = rows.SelectMany(r => r.Flows).Select(f => f.Destination).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var code in destCodes)
            {
                if (!dumpIndex.TryGetValue(code, out var slot) || !have.Add(slot.UnitId)) continue;
                slot.Period = p;
                rows.Add(slot); addedSlots++;
            }
        }

        string backupNote = "";
        if (_store.Exists(p))
        {
            if (_store.Backup(p, out string arch, out string bErr)) backupNote = "\n· 旧的那份已留档：" + arch;
            else if (bErr.Length > 0) return "◆ " + bErr + " —— 为免顶掉旧计划，本次没有保存。";
        }
        if (!_store.Save(p, rows, out string err, out var dropped)) return err;
        saved = true;
        RefreshPeriodPickers();
        foreach (var r in _rows) r.RaiseAll();
        return $"期次「{p}」已保存：{rows.Count} 行 · {MiningUnitLedger.Summary(rows)}\n→ {_store.MonthPath(p)}"
            + backupNote
            + (addedSlots > 0
                ? $"\n· 顺带把本期流向用到的 {addedSlots} 个【排土位置】一并存了进去（它们的期次列也标成了 {p}） —— 没有它们，这一期在下游就是【只有源没有汇】。"
                : rows.Any(r => r.Flows.Count > 0)
                    ? "\n◆ 本期的流有去向码，但在表里一个排土位置都对不上" + (collided.Count > 0 ? $"（另有 {collided.Count} 个位置因撞码被整条剔除）" : "") + " —— 先「载入基表」把排土位置读进来再存。"
                    : "")
            + (dropped.Count > 0
                ? $"\n◆ 原文件里有 {dropped.Count} 个单元这次没写进去，它们已从本期消失：" + string.Join("、", dropped.Take(5)) + (dropped.Count > 5 ? $" …等 {dropped.Count} 个" : "") + "。若不是有意移出，先「读回期次」看一眼再存。"
                : "");
    }

    private void LoadPeriod()
    {
        string p = CurrentPeriod;
        if (!MonthlyUnitLedgerStore.IsValidMonth(p, out string why)) { _stat.Text = why; return; }
        if (!_store.TryLoad(p, out var rows, out var issues)) { _stat.Text = string.Join("\n", issues); return; }
        Fill(rows);
        _stat.Text = $"期次「{p}」已读回：{rows.Count} 行 · {MiningUnitLedger.Summary(rows)}" + (issues.Count > 0 ? "\n" + string.Join("\n", issues) : "");
    }

    private async Task DeletePeriodAsync()
    {
        string p = CurrentPeriod;
        if (!MonthlyUnitLedgerStore.IsValidMonth(p, out string why)) { _stat.Text = why; return; }
        if (!_store.Exists(p)) { _stat.Text = $"没有这一期的台账：{p}"; return; }
        bool ok = _selftestAutoConfirm || await CoalMsgBox.ConfirmAsync(this, "删除期次", $"要删除期次「{p}」吗？\n\n文件会挪进「已删除」子目录并加时间戳，可以找回。\n基表不受影响。");
        if (!ok) return;
        if (!_store.Delete(p, out string archived, out string err)) { _stat.Text = err; return; }

        // ★ 月度计划台账里那一行也要一起撤 —— 不撤的话三维模拟会拿它凑一帧
        string planNote = "";
        var pp = p.Split('-');
        if (pp.Length >= 2 && int.TryParse(pp[0], out int py) && int.TryParse(pp[1], out int pm))
        {
            try
            {
                var svc = Data.EquipmentDataContext.Plan;
                if (svc.Get(py, pm) != null)
                {
                    svc.Delete(py, pm);
                    planNote = $"\n· 月度计划台账里 {py}-{pm:00} 那一行也撤了 —— 不撤的话三维模拟会拿它凑一帧，看着像还有这一期。";
                }
            }
            catch (Exception ex) { planNote = $"\n◆ 月度计划台账没撤成（{ex.GetType().Name}）——三维模拟可能仍显示这一期的目标值。"; }
        }
        RefreshPeriodPickers(); RefreshLanded();
        _stat.Text = $"期次「{p}」已删除（可找回）：{archived}\n基表未受影响。" + planNote
                   + "\n注意：表里那些行的【期次列】还留着 —— 要一并清掉请选中它们，右键「清除期次」，或用「更多 ▾ → 对齐期次标记」。";
    }

    // ══ 月度排产（UnitPlanEngine 的界面入口）════════════════════

    /// <summary>把当前表装配成排产输入。出矿点先问装卸点台账，问不到才退回默认位置；两种情况的文案必须不一样。</summary>
    private bool BuildInput(out UnitPlanInput inp, out List<string> notes)
    {
        notes = new List<string>();
        inp = null!;
        if (_rows.Count == 0) { notes.Add("表是空的 —— 先「载入基表」或从模型取。"); return false; }

        var (units, slots) = MineUnitAdapter.FromLedger(_rows.Select(r => r.Src).ToList(), notes);
        if (units.Count == 0) { notes.Add("◆ 没有采掘单元（煤/岩），排不了。"); return false; }

        // ── 目标量：先问【逐月配置表】，问不到才用手填 ──
        var trow = TargetRow(out string tgtWhy);
        double coalWanT, stripWanM3, stripCapM3, fleetCapTKm;
        int planMonth;
        if (trow != null)
        {
            coalWanT = trow.CoalWanT;
            stripWanM3 = trow.StripWanM3;      // ★ U13：拿【剥离】那一列当排弃量直接传，**不折成剥采比**
            planMonth = trow.Month;
            stripCapM3 = MonthlyTargetTable.StripCapM3ForUnitEngine(trow, out string? capNote);
            fleetCapTKm = MonthlyTargetTable.FleetCapTKmForUnitEngine(trow, out string? fleetNote);

            notes.Add($"· 月煤量 / 排弃量 / 剥离能力 / 车队能力 / 月序 全部取自【逐月配置表】{trow.PeriodKey}（{trow.SourceText}{(trow.IsManual ? "：" + trow.OverriddenText : "")}）"
                    + $"：采出 {coalWanT:0.###}万t · 排弃 {stripWanM3:0.###}万m³实方（等效剥采比 {trow.Ratio:0.00}，是结果不是输入）· 月序 {planMonth}。界面上那三个框是**只读回显**，要改去「短期生产计划编制 → 逐月配置表」。");
            if (capNote != null) notes.Add(capNote);
            if (fleetNote != null) notes.Add(fleetNote);
            if (stripWanM3 <= 1e-9) notes.Add("· 表里这一行的剥离量是 0 ⇒ 本月只剥【必剥闭包】，不做超前剥离（U2 第③级）。要让本月多剥，去逐月配置表把【剥离】那一格填上。");
            if (planMonth != 1) notes.Add($"· 月序按表里的第 {planMonth} 月判内排位置的启用时机（DumpSlot.AvailableFromMonth）—— 此前这里**恒为 1**，会把还没到启用月的内排位置也算进来。");
        }
        else
        {
            coalWanT = ParseD(_tCoal.Text, 0);
            stripWanM3 = ParseD(_tStrip.Text, 0);
            double capWan = ParseD(_tCap.Text, 0);
            stripCapM3 = capWan > 0 ? capWan * 1e4 : 0;   // ★ ≤0 = 不卡
            fleetCapTKm = 0;
            planMonth = 1;
            notes.Add($"◆ {(tgtWhy.Length > 0 ? tgtWhy : "没填期次")} —— 月煤量/排弃量/剥离能力用的是**界面上手填的值**，不是逐月配置表里的；车队能力不卡；月序按 1。要走同一张表：把期次填成 yyyy-MM，并先在「逐月配置表」里派生到那个月。");
        }
        if (coalWanT <= 0)
        {
            notes.Add(trow != null ? $"◆ 逐月配置表 {trow.PeriodKey} 这一行的月采出是 {coalWanT:0.###} 万t —— 排不出东西，去表里填。" : "◆ 月煤量要填一个正数（万t）。");
            return false;
        }

        // ── 出矿点：先问装卸点台账，空表 / 坐标没录才退回采场质心 ──
        var sink = CoalSinkAdapter.Resolve(units, monthlyOperatingHours: 0);
        var sinks = sink.Sinks;
        foreach (var line in sink.Describe().Split('\n')) if (line.Trim().Length > 0) notes.Add(line.Trim());

        // ── 图上指过的煤卸点【优先于】适配器的降级值 ──
        if (_coalSink == null)
        {
            _coalSink = CoalSinkPoint.Load(_store.Root, out string sinkIssue0);
            if (sinkIssue0.Length > 0) notes.Add(sinkIssue0);
        }
        // ── 装卸点台账里有真出矿点时，把它写进共享的煤卸点（判断规则在 CoalSinkAdapter.TryAutoFill）──
        if (CoalSinkAdapter.TryAutoFill(sink, _coalSink, out var autoSink, out string autoWhy) && autoSink != null)
        {
            try { autoSink.Save(_store.Root); _coalSink = autoSink; notes.Add("· " + autoWhy); }
            catch (Exception ex) { notes.Add($"◆ 煤卸点自动落盘失败（{ex.GetType().Name}）—— 模拟里煤流仍不画。"); }
        }
        if (_coalSink is { IsPicked: true })
        {
            sinks = new List<CoalSink> { new() { Name = _coalSink.Name, Code = "CR-PICKED", Cx = _coalSink.X, Cy = _coalSink.Y, Cz = _coalSink.Z } };
            notes.RemoveAll(s => s.Contains("采场质心") || s.Contains("默认位置"));
            notes.Add("· 煤卸点（图上拾取）：" + _coalSink.Caption);
        }

        inp = new UnitPlanInput
        {
            Units = units, Slots = slots, CoalSinks = sinks,
            Materials = new[] { new GapMaterial { Name = "岩", Code = "rock", Density = 2.5, Kr = 1.15 } },
            CoalTargetT = coalWanT * 1e4,
            StripTargetM3 = stripWanM3 > 0 ? stripWanM3 * 1e4 : 0,   // ★ 排弃量直接给（U13），**不给剥采比**
            StripCapM3 = stripCapM3, FleetCapTKm = fleetCapTKm,
            CoalDensity = MiningUnitLedger.DefaultCoalDensity,
            DumpOrder = (DumpOrder)Math.Max(0, _cDump.SelectedIndex),
            FacePriority = (FacePriority)Math.Max(0, _cFace.SelectedIndex),
            StrikeAdvance = (StrikeAdvance)Math.Max(0, _cStrike.SelectedIndex),
            Strategy = (PairingStrategy)Math.Max(0, _cPair.SelectedIndex),
            FaceSegments = Math.Max(1, _cSeg.SelectedIndex + 1),
            Month = planMonth,
        };

        // ★ 面级配额（U1″）：把月煤量按「确定开采程序」的份额拆到各作业面上
        inp.FaceQuotas = BuildFaceQuotas(units, notes);

        // ── 一个不起作用的控件必须自己说出来：只有一个场时配对策略这条轴是塌的 ──
        int outer = slots.Count(s => !s.IsInternal);
        var dumpNames = slots.Select(s => s.DumpName).Distinct(StringComparer.Ordinal).ToList();
        bool pairAlive = dumpNames.Count > 1;
        _cPair.IsEnabled = pairAlive;
        ToolTip.SetTip(_cPair, pairAlive
            ? "配对策略：运输功最小 / 内排优先 / 库容均衡 —— 挑的是【去哪个排土场】。"
            : $"当前台账只有 {dumpNames.Count} 个排土场（外排 {outer} 个位置），三种策略必然挑同一个 ⇒ 这条轴在这份数据上无效，已禁用。\n要让它活过来：在「排土条带」里把外排土场也切一遍，或补上第二个排土场。");
        if (!pairAlive)
            notes.Add($"· 【配对策略】这条轴在当前台账上**必然塌**（只有 {dumpNames.Count} 个排土场、外排位置 {outer} 个）⇒ 下拉已禁用。同理，内排率会恒为 100%。这是数据侧的事，不是算法问题 —— 接上外排土场后它才有区别。");

        // ── 内排累计占容上限 = 已形成采空区 × 回填比（口径与 CoupledMinePlanner 同源）──
        double fill = ParseD(_tFill.Text, -1);
        if (fill > 0)
        {
            double done = units.Sum(u => u.InSituM3 * Math.Clamp(u.DoneFraction, 0, 1));
            double thisMonth = inp.StripTargetM3 + (inp.CoalDensity > 0.1 ? inp.CoalTargetT / inp.CoalDensity : 0);
            double voidM3 = done + thisMonth;
            inp.InternalCumCapM3 = voidM3 * fill;
            notes.Add($"· 内排上限 = 采空区 {voidM3 / 1e4:0.0}万m³（已采 {done / 1e4:0.0} + 本月 {thisMonth / 1e4:0.0}） × 回填比 {fill:0.##} = {inp.InternalCumCapM3 / 1e4:0.0}万m³占容。⚠ **单月口径不扣历史内排**（台账里没有这笔账）⇒ 偏松；要严就把回填比调低。");
        }
        else notes.Add("· 回填比留空 ⇒ **不卡内排上限**，内排只受位置库容限制 —— 内排率会偏高（真基表实测 100.0%）。要卡就在「回填比」里填一个 0~1 的数。");

        // ── 真路网运距：单元链两端都有真质心，走点到点 ──
        var road = RoadHaulProvider.TryLoadLatest(null, out string roadWhy);
        if (road != null)
        {
            inp.Haul = road.AsUnitQuery();
            notes.Add($"· 运距接【真路网】：{road.SourceLabel} · {road.SnapRadiusNote}（解不出来的 O-D 逐笔退回直线兜底，命中率见排产报告的「运距」那一行）。");
            if (road.SampledTortuosity is > 1.0 and < 5.0)
            {
                inp.FallbackDetour = road.SampledTortuosity.Value;
                notes.Add($"· 兜底迂回系数按路网**实测**取 {inp.FallbackDetour:0.00}（{road.SampledTortuosityNote}），不是经验值 1.3。");
            }
            else notes.Add($"· 路网采不出迂回系数（{road.SampledTortuosityNote}），兜底仍用经验值 {inp.FallbackDetour:0.##}。");
        }
        else notes.Add($"· 没接真路网（{roadWhy}）⇒ 运距全按直线×{inp.FallbackDetour:0.##} 兜底，「运输功最小」这条配对策略在静态运距上分不出高下。");
        return true;
    }

    private static double ParseD(string? s, double dflt)
        => double.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;

    // ══ 逐月配置表 —— 月煤量 / 排弃量 / 剥离能力 / 车队能力的唯一来源 ══

    private MonthlyTargetRow? TargetRow(out string why)
    {
        why = "";
        string p = CurrentPeriod;
        if (p.Length == 0) { why = "没填当前期次（②里填 yyyy-MM 才能对上逐月配置表）"; return null; }
        try
        {
            var t = MonthlyTargetStore.Current;
            var row = t.Find(p);
            if (row == null)
                why = $"逐月配置表里没有期次「{p}」" + (t.Rows.Count > 0 ? $"（表里是 {t.Rows[0].PeriodKey}…{t.Rows[^1].PeriodKey}，共 {t.Rows.Count} 个月）" : "（表是空的）");
            return row;
        }
        catch (Exception ex) { why = "取不到逐月配置表：" + ex.Message; return null; }
    }

    /// <summary>把「确定开采程序」的<b>面级份额</b> + <c>FaceUnitResolver</c> 的<b>归属</b>装成引擎的面级配额。取不到就明说，不静默退化。</summary>
    private static List<FaceQuota> BuildFaceQuotas(List<MineUnit> units, List<string> notes)
    {
        var faces = ShortTermSchemeStore.Base.Faces?.Where(f => !string.IsNullOrWhiteSpace(f.Name)).ToList() ?? new List<WorkingFace>();
        if (faces.Count == 0)
        {
            notes.Add("· 「确定开采程序」里一个作业面都没有 ⇒ 本月按【全局一个目标】排（锋面幅只会出现一个）。要让各面按份额同时推进，去那儿把面和份额填上。");
            return new List<FaceQuota>();
        }

        var fu = FaceUnitResolver.Resolve(units, faces, ShortTermSchemeStore.Base.FaceAttribution);
        if (fu.UnitToFace.Count == 0)
        {
            notes.Add($"· 有 {faces.Count} 个作业面，但**一个单元都没归上属**（标高对不上 / 物料对不上）⇒ 本月按【全局一个目标】排。去「设备指派 → 归属覆盖」手工指定，或核对面的台阶标高。");
            return new List<FaceQuota>();
        }

        var byFace = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in fu.UnitToFace)
        {
            if (!byFace.TryGetValue(kv.Value, out var lst)) byFace[kv.Value] = lst = new List<string>();
            lst.Add(kv.Key);
        }

        var quotas = new List<FaceQuota>();
        foreach (var f in faces)
        {
            if (!byFace.TryGetValue(f.Name, out var ids) || ids.Count == 0) continue;
            quotas.Add(new FaceQuota { FaceId = f.Name, SharePct = f.SharePct, CapacityT = 0, UnitIds = ids, Note = $"（{ids.Count} 个单元，标高 {f.BenchElevationM:0.#}m）" });
        }

        if (quotas.Count == 0) { notes.Add("· 归属解出来了，但没有一个面同时有份额和单元 ⇒ 按【全局一个目标】排。"); return quotas; }

        int pending = fu.AmbiguousIds.Count + fu.UnmatchedIds.Count;
        notes.Add($"· 面级配额已接上：{quotas.Count} 个作业面（{fu.MatchedCount}/{fu.UnitCount} 个单元有归属" + (pending > 0 ? $"，{pending} 个待定 —— 待定的只在按份额凑不够时才拿来补" : "") + "）。份额取自「确定开采程序」，各面按各自的目标推进 ⇒ 每个在采面各留一个锋面幅。");
        return quotas;
    }

    /// <summary>把表里那一行回显到三个输入框，并按「表里有没有这一期」切只读。</summary>
    private void SyncTargetRow()
    {
        MonthlyTargetRow? row; string why;
        try { row = TargetRow(out why); } catch (Exception ex) { row = null; why = ex.Message; }

        bool has = row != null;
        _tCoal.IsReadOnly = has; _tStrip.IsReadOnly = has; _tCap.IsReadOnly = has;
        foreach (var tb in new[] { _tCoal, _tStrip, _tCap }) tb.Opacity = has ? 0.75 : 1;

        if (row != null)
        {
            _tCoal.Text = row.CoalWanT.ToString("0.###");
            _tStrip.Text = row.StripWanM3.ToString("0.###");   // ★ 取【剥离】那一列
            _tCap.Text = row.StripCapWanM3.HasValue ? row.StripCapWanM3.Value.ToString("0.###") : "";   // 留空就是留空，不写 0
            _tTargetInfo.Text = $"这三个数取自【逐月配置表】{row.PeriodKey}（{row.SourceText}" + (row.IsManual ? "：" + row.OverriddenText : "") + "）· 只读"
                              + $" · 排弃量按原位实方，等效剥采比 {row.Ratio:0.00}（结果，不是输入）"
                              + (row.StripCapWanM3.HasValue ? "" : " · 剥离能力留空 = 不卡")
                              + (row.FleetCapWanTKm.HasValue ? $" · 车队能力 {row.FleetCapWanTKm.Value:0.#} 万t·km" : " · 车队能力没给 = 不卡")
                              + " —— 要改去「短期生产计划编制 → 逐月配置表」。";
        }
        else _tTargetInfo.Text = "◆ " + (why.Length > 0 ? why : "取不到逐月配置表") + " —— 这三个数用**手填**的（不是表里的），车队能力不卡、月序按 1。";
    }

    /// <summary>把这一期排出来的合计量推进<b>月度计划台账</b>（<c>monthly_plan</c>）。<b>⚠ 覆盖必须报出来</b>。</summary>
    private string PushPeriodToMonthlyPlan(string period, UnitPlanResult r)
    {
        var parts = period.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int year) || !int.TryParse(parts[1], out int month))
            return $"◆ 期次「{period}」定不出年月 —— 没有写进月度计划台账，时间轴看不到这次排产。";

        double coalWanT = r.CoalT / 1e4, stripWanM3 = r.StripM3 / 1e4;
        try
        {
            var svc = Data.EquipmentDataContext.Plan;
            var old = svc.Get(year, month);
            var row = old ?? new Data.Entities.MonthlyPlan { Year = year, Month = month };

            string changed = "";
            if (old != null)
            {
                bool dCoal = Math.Abs(old.PlanCoalWanT - coalWanT) > 0.05, dStrip = Math.Abs(old.PlanStripWanM3 - stripWanM3) > 0.5;
                if (dCoal || dStrip)
                    changed = "　◆ **盖掉了台账里原有的数**：" + (dCoal ? $"采出 {old.PlanCoalWanT:0.##} → {coalWanT:0.##} 万t　" : "") + (dStrip ? $"剥离 {old.PlanStripWanM3:0.#} → {stripWanM3:0.#} 万m³　" : "") + "（「月度计划编制」那条链也写这张表，确认哪个才是要的）";
            }
            row.PlanCoalWanT = coalWanT; row.PlanStripWanM3 = stripWanM3;   // 外委剥离原样保留
            svc.Upsert(row);
            return $"✔ 已推进月度计划台账 {year}-{month:00}：采出 {coalWanT:0.##} 万t · 剥离 {stripWanM3:0.#} 万m³　—— 三维模拟的时间轴现在看得到这一期了（**下次开机仍在**）。" + changed;
        }
        catch (Exception ex) { return $"◆ 没能写进月度计划台账（{ex.GetType().Name}: {ex.Message}） —— 排产结果还在台账 CSV 里，但三维模拟的时间轴看不到。"; }
    }

    /// <summary>按目标排一次，并把结果<b>填回表</b>。</summary>
    private void RunSchedule() { _stat.Text = SolveAndWriteBack(null).Message; RefreshLanded(); }

    /// <summary>「一键排本月」—— 表空就先取模型数据 → 按目标排产 → 存为期次 → 打开本期一览。中间任何一步不成就停下并说清停在哪一步。</summary>
    private async Task RunMonthEndToEndAsync()
    {
        var log = new System.Text.StringBuilder();
        string p = CurrentPeriod;

        // ★ 每一次都把整段过程落到台账目录里的一个文本 —— 留痕不许把主流程搞挂
        void Trace(string tail)
        {
            _stat.Text = tail.Length > 0 ? log + "\n" + tail : log.ToString().TrimEnd();
            try
            {
                File.WriteAllText(Path.Combine(_store.Root, "_一键排本月_最近一次.txt"),
                    $"【一键排本月】{DateTime.Now:yyyy-MM-dd HH:mm:ss}　期次 {(p.Length > 0 ? p : "(没选)")}\n"
                    + $"表里 {_rows.Count} 行　本期在表 {_rows.Count(r => string.Equals(r.Period, p, StringComparison.Ordinal))} 行　盘上有这一期：{(p.Length > 0 && _store.Exists(p) ? "是" : "否")}\n"
                    + new string('-', 60) + "\n" + _stat.Text + "\n", new System.Text.UTF8Encoding(true));
            }
            catch { /* 留痕失败不影响排产本身 */ }
        }

        try { await RunMonthBodyAsync(log, p, Trace); }
        catch (Exception ex)
        {
            log.AppendLine("◆ **中途抛异常，整条链断在这里** —— 期次没有落盘。");
            log.AppendLine(ex.GetType().Name + "：" + ex.Message);
            log.AppendLine(ex.StackTrace ?? "");
            Trace("◆ 排产中断（" + ex.GetType().Name + "）—— 详情见台账目录里的 _一键排本月_最近一次.txt");
            RefreshLanded();
            if (!_selftestAutoConfirm)
                await CoalMsgBox.ShowAsync(this, "一键排本月", "「一键排本月」中途出错，这一期**没有落盘**：\n\n" + ex.GetType().Name + "：" + ex.Message + "\n\n完整栈已写进台账目录的 _一键排本月_最近一次.txt。");
        }
    }

    private async Task RunMonthBodyAsync(System.Text.StringBuilder log, string p, Action<string> Trace)
    {
        if (p.Length == 0) { log.AppendLine("◆ 停在第 ⓪ 步：先选年 + 月。"); Trace(""); return; }

        // ① 表是空的就先取模型数据
        if (_rows.Count == 0)
        {
            TakeFromModels();
            log.AppendLine("① 取模型数据：" + _stat.Text.Replace("\n", "  "));
            if (_rows.Count == 0) { Trace("◆ 停在第 ① 步：一个单元都没取到，先去「采矿模型」「排土条带」各生成一次。"); return; }
        }
        else log.AppendLine($"① 表里已有 {_rows.Count} 行，跳过取数（要换数据请自己点「取模型数据」）。");

        // ② 排产
        var res = SolveAndWriteBack(null);
        log.AppendLine("② 排产：").AppendLine(res.Message);
        if (!res.WrittenBack) { Trace("◆ 停在第 ② 步：排产没写回表，后面几步不做了。"); RefreshLanded(); return; }

        // ③ 落盘（已存过会弹确认）
        bool go = !_store.Exists(p) || _selftestAutoConfirm || await CoalMsgBox.ConfirmAsync(this, "一键排本月", $"期次「{p}」已经存过一份，用这次排产的结果替换吗？（旧的先拷进「历史」）");
        if (go)
        {
            string r3 = await SavePeriodCoreAsync(interactive: false);
            bool saved = _lastSaved;
            if (!saved && _store.Exists(p)) r3 = SavePeriodWrite(p, out saved);   // 确认过了就真覆盖
            log.AppendLine("③ 落盘：" + r3);
            if (!saved) { Trace("◆ 停在第 ③ 步：没落盘，三维模拟与采排配对读不到这一期。"); RefreshLanded(); return; }
        }
        else log.AppendLine("③ 落盘：你选了不覆盖 —— 这一期在盘上还是上一版。");

        RefreshLanded();
        Trace("");

        // ④ 顺手把结果摆出来
        ShowPeriodOverview();
    }

    /// <summary>刷新"本期落没落盘"那盏灯（把行数写在灯上）。</summary>
    private void RefreshLanded()
    {
        string p = CurrentPeriod;
        int inTable = _rows.Count(r => string.Equals(r.Period, p, StringComparison.Ordinal));
        bool onDisk = p.Length > 0 && _store.Exists(p);
        if (p.Length == 0 || (inTable == 0 && !onDisk)) { _landed.Text = ""; ToolTip.SetTip(_landed, null); return; }
        _landed.Text = onDisk ? $"✔ 已落盘（{inTable} 行）" : $"◆ 未落盘（表里 {inTable} 行）";
        _landed.Foreground = onDisk ? new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xB4, 0x25, 0x25));
        _landed.FontWeight = onDisk ? FontWeight.Normal : FontWeight.Bold;
        ToolTip.SetTip(_landed, (_lastSaveNote.Length > 0 ? "上次落盘：" + _lastSaveNote + "\n\n" : "")
            + (onDisk ? $"{p} 已经写成期次文件，三维模拟／采排配对／下游台账读的就是它。"
                      : $"表里有 {inTable} 行属于 {p}，但**盘上没有这一期的文件** —— 三维模拟和采排配对读不到，它们会退回「逐月配置表」的目标，数就对不上了。\n\n"
                      + $"◆ 先看这 {inTable} 行够不够一个月：基表共 {_rows.Count} 行（全矿家底），而一期通常是其中的一部分。**这个数明显偏小时，多半是上一期留下的陈旧标记，不是本期排产的结果** —— 直接存会落一份残缺的期次。正确的次序是先「一键排本月」或「按目标排产」（勾着「排产后存期次」就会自动落盘），而不是直接点「存为期次」。"));
    }

    /// <summary>排一次并写回表 —— 「按目标排产」和比选窗口「用这套重跑」走的是同一条。</summary>
    private UnitSchemeApplyOutcome SolveAndWriteBack(UnitSchemeAxes? axes)
    {
        var res = new UnitSchemeApplyOutcome();
        if (!BuildInput(out var inp, out var notes)) { res.Message = string.Join("\n", notes); return res; }
        axes?.ApplyTo(inp);
        string period = CurrentPeriod;
        if (period.Length > 0 && !MonthlyUnitLedgerStore.IsValidMonth(period, out string why)) { res.Message = why; return res; }

        var r = UnitPlanEngine.Solve(inp);
        if (!r.Success) { res.Message = "排产失败：" + r.Error + "\n" + string.Join("\n", notes); return res; }
        res.Result = r;

        var bad = r.Validate();
        if (bad.Count > 0)
        {
            UnitPlanStore.Put(null, period, "自洽校核没过");   // 挡下的也要交一笔
            res.Message = "◆ 排产结果自洽校核没过，未写入表：\n  " + string.Join("\n  ", bad); return res;
        }

        // ★ 交给「采排配对」——【配对只有这一处产出】
        UnitPlanStore.Put(r, period, $"目标 煤 {inp.CoalTargetT / 1e4:0.##}万t · 排弃 {inp.StripTargetM3 / 1e4:0.#}万m³ · {UnitSchemeText.Dump(inp.DumpOrder)} × {UnitSchemeText.Face(inp.FacePriority)}");

        // ★ 写回之前，先把【这一期的旧标记】清干净 —— 重排是替换不是追加
        int cleared = 0;
        if (period.Length > 0)
        {
            var keep = new HashSet<string>(r.Assignments.Select(a => a.UnitId), StringComparer.Ordinal);
            foreach (var row in _rows)
            {
                if (!string.Equals(row.Period, period, StringComparison.Ordinal) || keep.Contains(row.UnitId) || row.Src.Kind == LedgerKind.Dump) continue;
                row.Period = ""; row.Status = ""; row.DonePct = 0; row.Destination = ""; row.Src.Flows.Clear();
                cleared++;
            }
        }

        var byId = _rows.ToDictionary(x => x.UnitId, x => x, StringComparer.Ordinal);
        int wrote = 0, miss = 0;
        foreach (var a in r.Assignments)
        {
            if (!byId.TryGetValue(a.UnitId, out var row)) { miss++; continue; }
            row.Seq = a.Seq; row.Period = period;
            row.Status = a.DoneAfter >= 1 - 1e-6 ? "已采" : "在采";
            row.DonePct = Math.Round(a.DoneAfter * 100, 1);
            row.Src.Flows.Clear();
            foreach (var f in a.Flows)
                row.Src.Flows.Add(new MiningUnitLedger.Flow { Destination = f.DestinationCode, InSituM3 = f.InSituM3, HaulKm = f.HaulKm, MaterialCode = f.MaterialCode });
            row.RaiseAll();
            wrote++;
        }

        RefreshPeriodFilter();
        ApplyFilter();
        var sb = new System.Text.StringBuilder();
        if (axes != null) sb.AppendLine("按比选选中的那套【重跑】：" + axes.Caption);
        sb.AppendLine(r.Report().TrimEnd());
        if (cleared > 0) sb.AppendLine($"· 清掉 {cleared} 行上一次排到、这次没排到的旧标记（期次/状态/完成度/去向/流） —— 重排是替换不是追加，不清的话标记会一次次叠加，采出量会越滚越大。");
        sb.AppendLine($"已写回 {wrote} 行" + (period.Length > 0 ? $"（期次 {period}）" : "（未填期次，只写了状态与流）") + (miss > 0 ? $"　◆ 有 {miss} 个单元在表里找不到" : ""));
        foreach (var n in notes) sb.AppendLine("  " + n);

        // ── 设备维：排产之后再算一层 ──
        string equipText = _equip.OnScheduled(r.Assignments, inp.Units, period);
        if (equipText.Length > 0) sb.AppendLine(equipText.TrimEnd());

        // ── 让这一期的合计量进【月度计划台账】，模拟的时间轴才看得见 ──
        if (period.Length > 0) { string note = PushPeriodToMonthlyPlan(period, r); if (note.Length > 0) sb.AppendLine(note); }
        else sb.AppendLine("· 没填期次 ⇒ **没有写进月度计划台账**，三维模拟的时间轴看不到这次排产。填一个期次（如 2026-08）再排，或先「设为当前期次」。");

        // ── 落盘：省掉"排完忘了存"这一步，但【不许悄悄顶掉已有的那一期】──
        if (_autoSavePeriod.IsChecked == true && period.Length > 0)
        {
            string saveNote = SavePeriodCoreAsync(interactive: false).GetAwaiter().GetResult();   // interactive:false 分支无 await，同步完成
            bool landed = _lastSaved;
            sb.AppendLine(saveNote);
            _lastSaveNote = saveNote;
            if (!landed && !saveNote.Contains("没有自动覆盖", StringComparison.Ordinal) && !_selftestAutoConfirm)
                _ = CoalMsgBox.ShowAsync(this, "未落盘", "排产完成，但**这一期没有落盘**：\n\n" + saveNote + "\n\n下游（三维模拟 / 采排配对 / 月度台账）读不到这一期，它们会退回「逐月配置表」的目标，数就对不上。");
        }
        else { sb.AppendLine("排完记得点「存为期次」落盘 —— 排产只改了内存里的表。"); _lastSaveNote = "没有自动存（「排产后存期次」没勾）。"; }

        res.WrittenBack = true;
        res.Message = sb.ToString().TrimEnd();
        return res;
    }

    /// <summary>「对齐期次标记」—— 以<b>期次台账</b>为准，把基表上残留的旧排产标记对齐掉。先摆差异再改。</summary>
    private async Task AlignPeriodMarksAsync()
    {
        var months = _store.ListMonths();
        if (months.Count == 0) { _stat.Text = "台账目录里一个期次文件都没有 —— 没有可对齐的基准。"; return; }
        if (_rows.Count == 0) { _stat.Text = "表是空的 —— 先「载入基表」。"; return; }

        var stale = new List<UnitRow>(); var missing = new List<string>(); int mismatched = 0;
        var inBase = new HashSet<string>(_rows.Select(r => r.UnitId), StringComparer.Ordinal);

        foreach (string m in months)
        {
            if (!_store.TryLoad(m, out var rows, out _)) continue;
            var ids = new HashSet<string>(rows.Select(r => r.UnitId), StringComparer.Ordinal);
            foreach (var r in _rows) if (string.Equals(r.Period, m, StringComparison.Ordinal) && !ids.Contains(r.UnitId)) stale.Add(r);
            foreach (var id in ids) if (!inBase.Contains(id)) missing.Add($"{m}:{id}");
            foreach (var r in rows)
            {
                if (r.Flows.Count == 0) continue;
                double want = (r.Kind == LedgerKind.Coal ? (r.CoalM3 ?? 0) : r.Kind == LedgerKind.Rock ? (r.NetRockM3 ?? r.GrossM3 ?? 0) : 0) * Math.Clamp(r.Done <= 0 ? 1.0 : r.Done, 0, 1);
                if (want > 1e-6 && Math.Abs(r.FlowSumM3 - want) > Math.Max(1.0, 0.005 * want)) mismatched++;
            }
        }

        if (stale.Count == 0 && missing.Count == 0 && mismatched == 0) { _stat.Text = $"对齐检查：{months.Count} 个期次文件与基表**完全一致**，没有要改的。"; return; }

        double staleCoalT = stale.Where(r => r.Src.Kind == LedgerKind.Coal).Sum(r => r.Src.CoalT ?? 0);
        double staleRockM3 = stale.Where(r => r.Src.Kind == LedgerKind.Rock).Sum(r => r.Src.NetRockM3 ?? 0);
        string msg =
            $"以期次台账为准，基表上有这些对不上的：\n\n"
          + $"① 陈旧期次标记 {stale.Count} 行（煤 {staleCoalT / 1e4:0.00} 万t · 岩 {staleRockM3 / 1e4:0.0} 万m³）\n   它们在基表上标着某个期次，但那一期的文件里没有它们 —— 上一次排产留下的。\n"
          + (stale.Count > 0 ? "   如：" + string.Join("、", stale.Take(5).Select(r => $"{r.UnitId}({r.Period})")) + "\n" : "")
          + $"\n② 期次里有、基表里没有 {missing.Count} 个：模型重算后这些单元没了，那一期是照着不存在的体排的。\n"
          + (missing.Count > 0 ? "   如：" + string.Join("、", missing.Take(5)) + "\n" : "")
          + $"\n③ 流与量对不上 {mismatched} 行：上次排产写的流、量已随模型重算变了（读期次时会自动等比缩放并留条）。\n"
          + "\n点「确定」只做一件事：把①那些行的【计划列】清掉（期次/状态/完成度/去向/流）。\n几何与量一个字节都不动；②③不自动改 —— 那要重排一次才算数。";

        if (!(_selftestAutoConfirm || await CoalMsgBox.ConfirmAsync(this, "对齐期次标记", msg))) { _stat.Text = "没有对齐 —— 基表保持原样。"; return; }

        foreach (var r in stale) { r.Period = ""; r.Status = ""; r.DonePct = 0; r.Destination = ""; r.Src.Flows.Clear(); r.RaiseAll(); }
        RefreshPeriodFilter(); ApplyFilter(); Recalc();
        _stat.Text = $"已清掉 {stale.Count} 行的陈旧期次标记（煤 {staleCoalT / 1e4:0.00} 万t · 岩 {staleRockM3 / 1e4:0.0} 万m³） —— 只动了计划列。**记得「保存基表」落盘**，否则关窗就白清了。\n"
                   + (missing.Count > 0 ? $"◆ 另有 {missing.Count} 个单元只在期次文件里、基表里没有：重排一次那一期，或从期次里移出。\n" : "")
                   + (mismatched > 0 ? $"◆ 另有 {mismatched} 行的流与量对不上：重排那一期才算真正对齐（读的时候只是等比缩放了）。" : "");
    }

    private PeriodPlanOverviewWindow? _overview;

    private void ShowPeriodOverview()
    {
        string p = CurrentPeriod;
        if (p.Length == 0) { _stat.Text = "先选一个期次（年 + 月）。"; return; }
        int n = _rows.Count(r => r.Src.Kind != LedgerKind.Dump && string.Equals(r.Period, p, StringComparison.Ordinal));
        if (n == 0) { _stat.Text = $"期次「{p}」在表里一个采掘单元都没有 —— 先「按目标排产」，或「读回期次」。"; return; }
        var w = new PeriodPlanOverviewWindow(p, _rows.Select(r => r.Src).ToList());
        _overview = w; w.Closed += (_, _) => { if (ReferenceEquals(_overview, w)) _overview = null; };
        w.Show(this);
        GeoDbWindows.NoteLast(w);
        _stat.Text = $"「本期一览」已打开（{p}，{n} 个单元）—— 它是当下这张表的只读快照，重排后请重新打开。";
    }

    // ══ 批量编辑 / 筛选 / 汇总 ════════════════════════════════════

    private IEnumerable<UnitRow> Selected() => _grid.SelectedItems.Count > 0 ? _grid.SelectedItems.Cast<UnitRow>().ToList() : Enumerable.Empty<UnitRow>();

    private void SetStatus(string s)
    {
        var sel = Selected().ToList();
        if (sel.Count == 0) { _stat.Text = "先在表里选中若干行。"; return; }
        foreach (var r in sel)
        {
            r.Status = s;
            // 状态与完成度是同一件事的两个说法，别让它们互相矛盾
            if (s == "已采") r.DonePct = 100; else if (s == "") r.DonePct = 0;
        }
        Recalc();
    }

    private void SetPeriod(string p)
    {
        var sel = Selected().ToList();
        if (sel.Count == 0) { _stat.Text = "先在表里选中若干行。"; return; }
        string v = (p ?? "").Trim();
        if (v.Length > 0 && !MonthlyUnitLedgerStore.IsValidMonth(v, out string why)) { _stat.Text = why; return; }
        foreach (var r in sel) r.Period = v;
        RefreshPeriodFilter();
        Recalc();
        _stat.Text = v.Length > 0 ? $"{sel.Count} 行已设为期次「{v}」。" : $"{sel.Count} 行的期次已清除。";
    }

    private void RefreshPeriodFilter()
    {
        string cur = _filterPeriod.SelectedItem as string ?? "(全部)";
        _filterPeriod.Items.Clear();
        _filterPeriod.Items.Add("(全部)"); _filterPeriod.Items.Add("(未排产)");
        foreach (var p in _rows.Select(r => r.Period).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)) _filterPeriod.Items.Add(p);
        _filterPeriod.SelectedItem = _filterPeriod.Items.Contains(cur) ? cur : "(全部)";
    }

    private List<UnitRow> _shown = new();

    private void ApplyFilter()
    {
        string k = _filterKind.SelectedItem as string ?? "(全部)";
        string s = _filterStatus.SelectedItem as string ?? "(全部)";
        string p = _filterPeriod.SelectedItem as string ?? "(全部)";
        _shown = _rows.Where(r =>
        {
            if (k != "(全部)" && r.Kind != k) return false;
            if (s != "(全部)") { string rs = string.IsNullOrWhiteSpace(r.Status) ? "未采" : r.Status; if (rs != s) return false; }   // 状态空 = 未采
            if (p == "(未排产)") { if (r.Period.Length > 0) return false; }
            else if (p != "(全部)" && r.Period != p) return false;
            return true;
        }).ToList();
        _grid.ItemsSource = _shown;
        Recalc();
    }

    /// <summary>汇总。<b>三类分开报</b> —— 煤是吨、岩和排土是方。</summary>
    private void Recalc()
    {
        if (_rows.Count == 0) { _stat.Text = "表是空的 —— 点「载入基表」，或先去「采矿模型」「排土条带」生成一次。"; return; }

        var shown = _shown.Count > 0 || _rows.Count == 0 ? _shown : _rows.ToList();
        var src = shown.Select(r => r.Src).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append($"显示 {shown.Count} / 共 {_rows.Count} 个 —— {MiningUnitLedger.Summary(src)}");

        sb.Append("\n按状态：");
        foreach (var g in shown.GroupBy(r => string.IsNullOrWhiteSpace(r.Status) ? "未采" : r.Status).OrderBy(g => Array.IndexOf(new[] { "未采", "在采", "已采" }, g.Key)))
        {
            double t = g.Where(r => r.Src.Kind == LedgerKind.Coal).Sum(r => r.Src.CoalT ?? 0);
            double m3 = g.Where(r => r.Src.Kind == LedgerKind.Rock).Sum(r => r.Src.NetRockM3 ?? 0);
            sb.Append($"{g.Key} {g.Count()}个(煤{t / 1e4:0.00}万t·岩{m3 / 1e4:0.0}万m³)　");
        }

        var byPeriod = shown.Where(r => r.Period.Length > 0).GroupBy(r => r.Period).OrderBy(g => g.Key).ToList();
        if (byPeriod.Count > 0)
        {
            sb.Append("\n按期次：");
            foreach (var g in byPeriod)
            {
                double t = g.Where(r => r.Src.Kind == LedgerKind.Coal).Sum(r => r.Src.CoalT ?? 0);
                double m3 = g.Where(r => r.Src.Kind == LedgerKind.Rock).Sum(r => r.Src.NetRockM3 ?? 0);
                double ratio = t > 1e-9 ? m3 / t : 0;
                sb.Append($"{g.Key}: 煤{t / 1e4:0.00}万t·岩{m3 / 1e4:0.0}万m³" + (t > 1e-9 ? $"·剥采比{ratio:0.00}" : "") + $"（{g.Count()}个）　");
            }
        }

        // 排产覆盖率只对【采场单元】算
        var mine = shown.Where(r => r.Src.Kind != LedgerKind.Dump).ToList();
        int unplanned = mine.Count(r => r.Period.Length == 0);
        if (mine.Count > 0) sb.Append($"\n采场单元排产覆盖率 {100.0 * (mine.Count - unplanned) / mine.Count:0.#}%" + (unplanned > 0 ? $"（还有 {unplanned} 个没排）" : ""));

        _stat.Text = sb.ToString();
    }

    // ══ 自检直通 ══════════════════════════════════════════════════

    private bool _selftestAutoConfirm;
    internal string SelftestStatus => _stat.Text ?? "";
    internal int SelftestRowCount => _rows.Count;
    internal string SelftestLanded => _landed.Text ?? "";
    internal string SelftestTargetInfo => _tTargetInfo.Text ?? "";
    internal EquipmentAssignPanel SelftestEquip => _equip;
    internal PeriodPlanOverviewWindow? SelftestOverview => _overview;
    internal void SelftestSetPeriod(int year, int month) { _periodYear.SelectedItem = year; _periodMonth.SelectedItem = month; }
    /// <summary>自检：灌一批合成台账行（等价「取模型数据」并入）。</summary>
    internal void SelftestMerge(IReadOnlyList<MiningUnitLedger.Row> rows) => Merge(rows);
    internal Task SelftestRunMonthAsync() { _selftestAutoConfirm = true; _autoSavePeriod.IsChecked = false; return RunMonthEndToEndAsync(); }
    internal void SelftestRunSchedule() { _selftestAutoConfirm = true; _autoSavePeriod.IsChecked = false; RunSchedule(); }
    internal void SelftestShowOverview() => ShowPeriodOverview();
    internal void SelftestExpandEquip() => _equip.IsExpanded = true;
}
