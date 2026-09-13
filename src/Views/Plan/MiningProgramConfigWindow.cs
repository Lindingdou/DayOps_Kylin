using System;
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
/// 「采区划分设置」窗口（优化开采设计 按钮③「采区划分」；移植原 <c>MiningProgramConfigWindow</c>）。
/// 编辑「开采程序方案」MiningProgramPlan：① 产状/策略（块体 PCA） ② 境界来源（必选·仅限已确定的最终境界，选定即继承产状/走向/策略）
/// ③ 首采区权重归一 ④ 拉沟·推进（全自动推荐 从剥采比场真生成候选 / 人工指定 拾取拉沟线+方位；约束权重归一）
/// ⑤ 采区数与均衡 ⑥ 内排 ⑦ 产能/推进约束（按设备派生 + Q=L·v·H·ρ 校核）⑧ 经济。方案集会话级共享（<see cref="MiningProgramStore"/>）。
/// </summary>
internal sealed class MiningProgramConfigWindow : Window
{
    public ObservableCollection<MiningProgramPlan> Schemes { get; }
    private readonly IPlanEntityHost _host;
    private readonly IReadOnlyList<PitScheme> _boundarySchemes;
    private bool _loading;

    private readonly ListBox _schemeList = new() { MinHeight = 200 };
    private readonly ComboBox _attitudeCombo, _strategyCombo, _sourceCombo, _splitCombo, _equipCombo;
    private readonly TextBlock _dipText = PlanUi.Value("—"), _strikeText = PlanUi.Value("—");
    private readonly TextBlock _sourceInfo = RoadUi.Hint("（必选）请先在「计算·比选·确定最终境界」确定一个方案，再在此选定为来源。");
    private readonly TextBox _wStrip = PlanUi.Box("0.40", 70), _wCoal = PlanUi.Box("0.20", 70), _wDepth = PlanUi.Box("0.15", 70), _wHaul = PlanUi.Box("0.15", 70), _wInner = PlanUi.Box("0.10", 70);
    private readonly TextBlock _weightSum = PlanUi.Value("1.00");
    private readonly RadioButton _autoRadio = new() { Content = "全自动推荐", IsChecked = true, GroupName = "BoxcutMode", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _manualRadio = new() { Content = "人工指定", GroupName = "BoxcutMode" };
    private readonly Grid _manualPanel = new() { IsEnabled = false, Margin = new Thickness(0, 0, 0, 8), ColumnDefinitions = new ColumnDefinitions("100,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
    private readonly TextBlock _manualBoxcutText = PlanUi.Value("（未指定）");
    private readonly TextBox _manualAz = PlanUi.Box("0", 80);
    private readonly TextBox _bcStrip = PlanUi.Box("0.30", 56), _bcShallow = PlanUi.Box("0.15", 56), _bcHaul = PlanUi.Box("0.20", 56), _bcInner = PlanUi.Box("0.15", 56), _bcWorkLine = PlanUi.Box("0.10", 56), _bcGeo = PlanUi.Box("0.10", 56);
    private readonly TextBlock _bcWeightSum = PlanUi.Value("1.00");
    private readonly TextBlock _boxcutSummary = RoadUi.Hint("候选在「计算·比选·确定开采程序」窗口比选；当前推荐：—");
    private readonly TextBox _panelCount = PlanUi.Box("4", 70), _capacity = PlanUi.Box("400", 70), _life = PlanUi.Box("30", 70);
    private readonly CheckBox _innerEnabled = new() { Content = "启用内排", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _innerWidth = PlanUi.Box("300", 100);
    private readonly TextBox _workLine = PlanUi.Box("800", 70), _miningWidth = PlanUi.Box("50", 70), _benchHeight = PlanUi.Box("12", 70), _advRate = PlanUi.Box("300", 70), _maxAdvRate = PlanUi.Box("360", 70);
    private readonly TextBlock _capacityCheck = PlanUi.Value("—");
    private readonly TextBox _coalPrice = PlanUi.Box("320", 70), _mineCost = PlanUi.Box("95", 70), _stripCost = PlanUi.Box("28", 70), _discount = PlanUi.Box("8", 70);
    private readonly TextBlock _status = RoadUi.Hint("");

    private static readonly string[] EquipItems = { "WK-10 电铲 + 矿用卡车", "WK-20 电铲 + 矿用卡车", "拉铲倒堆", "轮斗连续工艺" };

    public MiningProgramConfigWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "采区划分设置 — 开采程序方案配置";
        PlanUi.Place(this, 1040, 780);
        Schemes = MiningProgramStore.Schemes;
        _boundarySchemes = BoundarySchemeStore.Schemes;

        _attitudeCombo = PlanUi.Combo(new[] { "水平 / 近水平（微倾斜）", "缓倾斜", "倾斜", "急倾斜", "多煤层 / 复合", "不规则 / 块状（金属）" }, 0);
        _strategyCombo = PlanUi.Combo(new[] { "条带（拉沟 + 内排）", "纵采转横采（沿倾向超前降深）", "台阶式（以外排为主）" }, 0, 260);
        _sourceCombo = new ComboBox { Height = 28, Width = 260, FontSize = 13 };
        _sourceCombo.SelectionChanged += (_, _) => OnSourceChanged();
        _splitCombo = PlanUi.Combo(new[] { "按目标产能均衡", "按目标服务年限均衡", "固定采区数" }, 1);
        _equipCombo = PlanUi.Combo(EquipItems, 0);
        _autoRadio.IsCheckedChanged += (_, _) => OnBoxcutModeChanged();
        _manualRadio.IsCheckedChanged += (_, _) => OnBoxcutModeChanged();

        Content = PlanUi.Shell(
            PlanUi.Header("采区划分设置", "编辑「开采程序方案」(MiningProgramPlan) ·  续上最终境界，首采区/拉沟/推进/内排自动算 ·  权重可调，可存多方案备比选"),
            BuildBody(), BuildFooter());

        _schemeList.ItemsSource = Schemes;
        _schemeList.SelectionChanged += (_, _) => { if (Current != null) LoadFromScheme(Current); };
        RefreshSourceList();
        Activated += (_, _) => RefreshSourceList();
        if (Schemes.Count > 0) _schemeList.SelectedIndex = 0;
    }

    private MiningProgramPlan? Current => _schemeList.SelectedItem as MiningProgramPlan;

    // ───────────── 布局 ─────────────
    private Control BuildBody()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("232,*") };
        var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        btns.Children.Add(RoadUi.Btn("新建", () => { var s = new MiningProgramPlan { Name = $"程序{Schemes.Count + 1}" }; Schemes.Add(s); _schemeList.SelectedItem = s; }, 50));
        btns.Children.Add(RoadUi.Btn("克隆", () => { if (Current == null) return; var c = Current.Clone(); Schemes.Add(c); _schemeList.SelectedItem = c; }, 50));
        btns.Children.Add(RoadUi.Btn("删除", () => { if (Current != null && Schemes.Count > 1) Schemes.Remove(Current); }, 50));
        var dock = new DockPanel();
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Bottom);
        dock.Children.Add(btns);
        _schemeList.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
        dock.Children.Add(_schemeList);
        var left = PlanUi.Group("方案管理", dock, new Thickness(12, 12, 6, 12), 8);
        Grid.SetColumn(left, 0); g.Children.Add(left);

        var stack = new StackPanel();
        stack.Children.Add(BuildAttitudeGroup());
        stack.Children.Add(BuildSourceGroup());
        stack.Children.Add(BuildFirstWeightsGroup());
        stack.Children.Add(BuildBoxcutGroup());
        stack.Children.Add(BuildSplitGroup());
        stack.Children.Add(BuildInnerGroup());
        stack.Children.Add(BuildCapacityGroup());
        stack.Children.Add(BuildEconGroup());
        var sv = new ScrollViewer { Content = stack, Margin = new Thickness(6, 12, 12, 12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(sv, 1); g.Children.Add(sv);
        return g;
    }

    private Control BuildAttitudeGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("产状决定开采策略（近水平/缓倾斜→条带+内排；倾斜→纵采转横采；急倾斜→台阶式）。可从块体 PCA 自动识别，或从境界来源继承。"));
        sp.Children.Add(PlanUi.LabeledRow("煤层产状", _attitudeCombo, RoadUi.Btn("自动识别(块体PCA)", OnAutoDetectAttitude), 120, 220, new Thickness(0, 10, 0, 8)));
        sp.Children.Add(PlanUi.InfoBox(RoadUi.Row(RoadUi.Hint("自动识别："), RoadUi.Lbl("  平均倾角 "), _dipText, RoadUi.Lbl("   走向方位 "), _strikeText)));
        sp.Children.Add(PlanUi.LabeledRow("开采策略", _strategyCombo, null, 120, 260, new Thickness(0)));
        return PlanUi.Group("① 产状与开采策略", sp);
    }

    private Control BuildSourceGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("必选：从已「确定最终境界」的方案中选一个续上；选定后产状/走向/策略自动带过来，本页不重填境界项。"));
        var lbl = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        lbl.Inlines!.Add(new Avalonia.Controls.Documents.Run("最终境界方案 "));
        lbl.Inlines.Add(new Avalonia.Controls.Documents.Run("✱") { Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)) });
        RoadUi.Theme(lbl, TextBlock.ForegroundProperty, "Theme.Text.Body");
        ToolTip.SetTip(lbl, "必选；仅列出已『确定最终境界』的方案");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,260"), Margin = new Thickness(0, 10, 0, 8) };
        Grid.SetColumn(lbl, 0); row.Children.Add(lbl);
        Grid.SetColumn(_sourceCombo, 1); row.Children.Add(_sourceCombo);
        sp.Children.Add(row);
        sp.Children.Add(PlanUi.InfoBox(_sourceInfo, new Thickness(0)));
        return PlanUi.Group("② 境界来源（必选 · 仅限已确定的最终境界）", sp);
    }

    private static StackPanel W(string label, TextBox box, double lw = 100)
    {
        var l = RoadUi.Lbl(label); l.Width = lw; l.VerticalAlignment = VerticalAlignment.Center;
        var r = RoadUi.Row(l, box); r.Margin = new Thickness(0, 4, 16, 4);
        return r;
    }

    private Control BuildFirstWeightsGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("首采区 = 剥采比最小 + 煤厚大 + 埋藏浅 + 靠工业场地 + 利内排 的加权打分。权重越大越主导，归一后求解。"));
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        wrap.Children.Add(W("剥采比最小", _wStrip)); wrap.Children.Add(W("煤层厚", _wCoal)); wrap.Children.Add(W("埋藏浅", _wDepth));
        wrap.Children.Add(W("运距短", _wHaul)); wrap.Children.Add(W("利于内排", _wInner));
        sp.Children.Add(wrap);
        var tail = RoadUi.Row(RoadUi.Btn("归一化", OnNormalizeWeights, 80), RoadUi.Hint("权重和 = "), _weightSum);
        tail.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(tail);
        foreach (var b in new[] { _wStrip, _wCoal, _wDepth, _wHaul, _wInner }) b.LostFocus += (_, _) => UpdateWeightSum();
        return PlanUi.Group("③ 首采区打分权重（手册：剥采比最小为主）", sp);
    }

    private Control BuildBoxcutGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("初始拉沟位置 + 推进方向。约束：剥采比最小、煤层最浅、便于开拓运输、利于内排、工作线长足够、避开断层/构造破碎/含水/老窑 + 地形排水有利。全自动按权重打分排序多候选；人工指定拾取拉沟线 + 推进方位。"));
        var modes = RoadUi.Row(_autoRadio, _manualRadio); modes.Margin = new Thickness(0, 8, 0, 8);
        sp.Children.Add(modes);
        var l1 = RoadUi.Lbl("拉沟线"); Grid.SetRow(l1, 0); Grid.SetColumn(l1, 0); _manualPanel.Children.Add(l1);
        Grid.SetRow(_manualBoxcutText, 0); Grid.SetColumn(_manualBoxcutText, 1); _manualPanel.Children.Add(_manualBoxcutText);
        var bPick = RoadUi.Btn("拾取…", () => new SurfaceSelectionDialog(_host, "选择拉沟(开段沟)线", PlanEntityType.Polyline, h =>
        {
            if (Current is not { } cur) return;
            cur.ManualBoxcutLineHandle = h; _manualBoxcutText.Text = $"handle {h}";
        }).Show(this));
        ToolTip.SetTip(bPick, "打开选择对话框：①视口拾取 ②从图纸线清单选 拉沟(开段沟)线");
        Grid.SetRow(bPick, 0); Grid.SetColumn(bPick, 2); _manualPanel.Children.Add(bPick);
        var l2 = RoadUi.Lbl("推进方位(°)"); l2.Margin = new Thickness(0, 6, 0, 0); Grid.SetRow(l2, 1); Grid.SetColumn(l2, 0); _manualPanel.Children.Add(l2);
        _manualAz.Margin = new Thickness(0, 6, 0, 0); Grid.SetRow(_manualAz, 1); Grid.SetColumn(_manualAz, 1); _manualPanel.Children.Add(_manualAz);
        foreach (var c in _manualPanel.Children) c.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(_manualPanel);

        sp.Children.Add(RoadUi.Hint("约束权重（拉沟位置选择，归一）"));
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        wrap.Children.Add(W("剥采比最小", _bcStrip, 78)); wrap.Children.Add(W("煤层浅", _bcShallow, 78)); wrap.Children.Add(W("开拓运输", _bcHaul, 78));
        wrap.Children.Add(W("利于内排", _bcInner, 78)); wrap.Children.Add(W("工作线长", _bcWorkLine, 78)); wrap.Children.Add(W("避构造/地形", _bcGeo, 78));
        sp.Children.Add(wrap);
        var tail = RoadUi.Row(RoadUi.Btn("归一化", OnNormalizeBoxcutWeights, 80), RoadUi.Btn("推荐候选（全自动）", OnRecommendBoxcut, 140), RoadUi.Hint("权重和 = "), _bcWeightSum);
        tail.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(tail);
        _boxcutSummary.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(_boxcutSummary);
        foreach (var b in new[] { _bcStrip, _bcShallow, _bcHaul, _bcInner, _bcWorkLine, _bcGeo }) b.LostFocus += (_, _) => UpdateBoxcutWeightSum();
        return PlanUi.Group("④ 拉沟 · 推进方案（全自动推荐多版本 / 人工指定，遵循初始拉沟位置选择约束）", sp);
    }

    private Control BuildSplitGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("各采区储量/服务年限相对均衡，保证产能接续平稳。切分取向决定按什么把境界切成 N 个采区。"));
        sp.Children.Add(PlanUi.LabeledRow("切分取向", _splitCombo, null, 120, 220, new Thickness(0, 10, 0, 8)));
        var wrap = new WrapPanel();
        wrap.Children.Add(W("采区数 N", _panelCount, 110)); wrap.Children.Add(W("目标产能(万t/a)", _capacity, 110)); wrap.Children.Add(W("目标服务年限(a)", _life, 110));
        sp.Children.Add(wrap);
        return PlanUi.Group("⑤ 采区数与均衡", sp);
    }

    private Control BuildInnerGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("首采区推进到一定宽度形成采空区后转内排，回填采空区缩短运距。内外排容量平衡校核。"));
        _innerEnabled.Margin = new Thickness(0, 8, 0, 8);
        sp.Children.Add(_innerEnabled);
        sp.Children.Add(PlanUi.LabeledRow("内排起转宽度 (m)", _innerWidth, null, 120, 100, new Thickness(0)));
        return PlanUi.Group("⑥ 内排（煤矿第一性）", sp);
    }

    private Control BuildCapacityGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("最小工作线长 / 采宽按采装设备规格自动派生；推进度上限由设备产能约束；台阶高用于产能耦合校核。"));
        sp.Children.Add(PlanUi.LabeledRow("采装设备", _equipCombo, RoadUi.Btn("按设备自动派生", OnAutoDeriveEquip), 120, 220, new Thickness(0, 10, 0, 8)));
        var wrap = new WrapPanel();
        wrap.Children.Add(W("最小工作线长(m)", _workLine, 120)); wrap.Children.Add(W("采宽(m)", _miningWidth, 120)); wrap.Children.Add(W("台阶高 H(m)", _benchHeight, 120));
        wrap.Children.Add(W("目标推进度 v(m/a)", _advRate, 120)); wrap.Children.Add(W("设备上限 v(m/a)", _maxAdvRate, 120));
        sp.Children.Add(wrap);
        var chk = RoadUi.Row(RoadUi.Hint("产能校核 Q = L·v·H·ρ ≈ "), _capacityCheck, RoadUi.Btn("校核", OnCheckCapacity, 56));
        chk.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(PlanUi.InfoBox(chk, new Thickness(0, 8, 0, 0)));
        return PlanUi.Group("⑦ 产能 / 推进约束（Q = 工作线长 L × 推进度 v × 台阶高 H × 容重 ρ）", sp, new Thickness(0, 0, 0, 4));
    }

    private Control BuildEconGroup()
    {
        var sp = new StackPanel();
        sp.Children.Add(RoadUi.Hint("价/成本 → 分期现金流 → NPV（基建剥离年0资本、生产剥离按年折现、内排省运费）。可从境界方案带过来或自填。"));
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        wrap.Children.Add(W("煤价 d(元/t)", _coalPrice, 120)); wrap.Children.Add(W("采煤成本 a(元/t)", _mineCost, 120));
        wrap.Children.Add(W("剥离成本 b(元/m³)", _stripCost, 120)); wrap.Children.Add(W("折现率(%/a)", _discount, 120));
        sp.Children.Add(wrap);
        return PlanUi.Group("⑧ 经济（NPV，本功能内自算分期现金流）", sp, new Thickness(0, 0, 0, 4));
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var bAuto = RoadUi.Btn("全部自动重填", OnAutoRefillAll, 110);
        DockPanel.SetDock(bAuto, Avalonia.Controls.Dock.Left); dock.Children.Add(bAuto);
        var right = RoadUi.Foot(RoadUi.Btn("保存方案", OnSaveScheme, 90), RoadUi.Btn("关闭", Close, 70));
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right); dock.Children.Add(right);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(14, 0, 0, 0);
        dock.Children.Add(_status);
        return PlanUi.Footer(dock);
    }

    // ───────────── 方案 ↔ 控件 ─────────────
    private void LoadFromScheme(MiningProgramPlan s)
    {
        _loading = true;
        _attitudeCombo.SelectedIndex = (int)s.Attitude;
        _strategyCombo.SelectedIndex = (int)s.Strategy;
        _dipText.Text = s.AutoDipDeg is { } d ? $"{d:0.#}°" : "—";
        _strikeText.Text = s.AutoStrikeDeg is { } st ? $"{st:0.#}°" : "—";

        var confirmed = _sourceCombo.ItemsSource as List<PitScheme>;
        _sourceCombo.SelectedItem = confirmed?.FirstOrDefault(x => x.Name == s.SourceSchemeName);
        ShowSourceInfo(_sourceCombo.SelectedItem as PitScheme);

        _wStrip.Text = PlanUi.Num(s.FirstWeights.StripRatio); _wCoal.Text = PlanUi.Num(s.FirstWeights.CoalThickness);
        _wDepth.Text = PlanUi.Num(s.FirstWeights.Depth); _wHaul.Text = PlanUi.Num(s.FirstWeights.HaulDistance); _wInner.Text = PlanUi.Num(s.FirstWeights.InnerDump);
        UpdateWeightSum();

        if (s.BoxcutMode == BoxcutMode.Manual) _manualRadio.IsChecked = true; else _autoRadio.IsChecked = true;
        _manualPanel.IsEnabled = s.BoxcutMode == BoxcutMode.Manual;
        _manualBoxcutText.Text = s.ManualBoxcutLineHandle != 0 ? $"handle {s.ManualBoxcutLineHandle}" : "（未指定）";
        _manualAz.Text = PlanUi.Num(s.ManualAdvanceAzimuthDeg);
        _bcStrip.Text = PlanUi.Num(s.BoxcutWeights.StripRatio); _bcShallow.Text = PlanUi.Num(s.BoxcutWeights.Shallow); _bcHaul.Text = PlanUi.Num(s.BoxcutWeights.Haul);
        _bcInner.Text = PlanUi.Num(s.BoxcutWeights.InnerDump); _bcWorkLine.Text = PlanUi.Num(s.BoxcutWeights.WorkLine); _bcGeo.Text = PlanUi.Num(s.BoxcutWeights.Geology);
        UpdateBoxcutWeightSum();
        RefreshBoxcutSummary(s);

        _splitCombo.SelectedIndex = (int)s.Split;
        _panelCount.Text = s.PanelCount.ToString();
        _capacity.Text = PlanUi.Num(s.TargetCapacityWanTa);
        _life.Text = PlanUi.Num(s.TargetServiceLifeYears);
        _innerEnabled.IsChecked = s.InnerDumpEnabled;
        _innerWidth.Text = PlanUi.Num(s.InnerDumpStartWidthM);

        int ei = Array.IndexOf(EquipItems, s.EquipmentRef); _equipCombo.SelectedIndex = ei >= 0 ? ei : 0;
        _workLine.Text = PlanUi.Num(s.MinWorkingLineM); _miningWidth.Text = PlanUi.Num(s.MiningWidthM); _benchHeight.Text = PlanUi.Num(s.BenchHeightM);
        _advRate.Text = PlanUi.Num(s.TargetAdvanceRateMpa); _maxAdvRate.Text = PlanUi.Num(s.MaxAdvanceRateMpa);
        _coalPrice.Text = PlanUi.Num(s.CoalPriceYuanT); _mineCost.Text = PlanUi.Num(s.MiningCostYuanT); _stripCost.Text = PlanUi.Num(s.StripCostYuanM3); _discount.Text = PlanUi.Num(s.DiscountRatePct);
        _capacityCheck.Text = "—";
        _loading = false;
    }

    private void SaveToScheme(MiningProgramPlan s)
    {
        s.Attitude = (DepositType)Math.Max(0, _attitudeCombo.SelectedIndex);
        s.Strategy = (MiningStrategy)Math.Max(0, _strategyCombo.SelectedIndex);
        if (_sourceCombo.SelectedItem is PitScheme ps) { s.SourceSchemeName = ps.Name; s.SourcePitResult = ps.Result; }

        s.FirstWeights.StripRatio = PlanUi.D(_wStrip); s.FirstWeights.CoalThickness = PlanUi.D(_wCoal); s.FirstWeights.Depth = PlanUi.D(_wDepth);
        s.FirstWeights.HaulDistance = PlanUi.D(_wHaul); s.FirstWeights.InnerDump = PlanUi.D(_wInner);

        s.BoxcutMode = _manualRadio.IsChecked == true ? BoxcutMode.Manual : BoxcutMode.Auto;
        s.ManualAdvanceAzimuthDeg = PlanUi.D(_manualAz);
        ReadBoxcutWeightsInto(s.BoxcutWeights);

        s.Split = (SplitObjective)Math.Max(0, _splitCombo.SelectedIndex);
        s.PanelCount = (int)Math.Max(1, PlanUi.D(_panelCount));
        s.TargetCapacityWanTa = PlanUi.D(_capacity);
        s.TargetServiceLifeYears = PlanUi.D(_life);
        s.InnerDumpEnabled = _innerEnabled.IsChecked == true;
        s.InnerDumpStartWidthM = PlanUi.D(_innerWidth);

        s.EquipmentRef = _equipCombo.SelectedItem as string ?? s.EquipmentRef;
        s.MinWorkingLineM = PlanUi.D(_workLine); s.MiningWidthM = PlanUi.D(_miningWidth); s.BenchHeightM = PlanUi.D(_benchHeight);
        s.TargetAdvanceRateMpa = PlanUi.D(_advRate); s.MaxAdvanceRateMpa = PlanUi.D(_maxAdvRate);
        s.CoalPriceYuanT = PlanUi.D(_coalPrice); s.MiningCostYuanT = PlanUi.D(_mineCost); s.StripCostYuanM3 = PlanUi.D(_stripCost); s.DiscountRatePct = PlanUi.D(_discount);
        s.Result = null;
    }

    // ───────────── ① 产状 ─────────────
    private void OnAutoDetectAttitude()
    {
        var m = _host.ActiveBlockModel;
        if (m == null) { _status.Text = "（自动识别）无激活块体模型，请先在「块体模型」里导入/选择一个模型"; return; }
        (Cad.DepositSignature sig, string attr)? r;
        try { r = BlockModelCoal.DetectAuto(m); }
        catch (Exception ex) { _status.Text = $"（自动识别）失败：{ex.Message}"; return; }
        if (r is not { } rr) { _status.Text = "（自动识别）未找到煤/岩属性或煤单元过少，请在块体模型里指定煤属性（名称含 coal/煤，或分类列含\"煤\"标签）"; return; }
        var s = rr.sig;
        var dep = PitSchemeConfigWindow.ClassifyDeposit(s.DipDeg, s.SeamCount);
        var strat = StrategyFor(dep);
        _dipText.Text = $"{s.DipDeg:0.#}°"; _strikeText.Text = $"{s.StrikeAzimuthDeg:0.#}°";
        _attitudeCombo.SelectedIndex = (int)dep; _strategyCombo.SelectedIndex = (int)strat;
        if (Current is { } cur) { cur.AutoDipDeg = s.DipDeg; cur.AutoStrikeDeg = s.StrikeAzimuthDeg; cur.Attitude = dep; cur.Strategy = strat; }
        _status.Text = $"自动识别：{AttitudeCn(dep)} · 倾角 {s.DipDeg:0.#}° · 走向 {s.StrikeAzimuthDeg:0.#}° · {s.SeamCount} 层 → 派策略「{StrategyCn(strat)}」";
    }

    /// <summary>产状 → 开采策略（手册：近水平/缓/多层=条带；倾斜=纵转横；急倾斜/块状=台阶）。</summary>
    public static MiningStrategy StrategyFor(DepositType d) => d switch
    {
        DepositType.Inclined => MiningStrategy.LongToCross,
        DepositType.SteepDip => MiningStrategy.Bench,
        DepositType.IrregularMassive => MiningStrategy.Bench,
        _ => MiningStrategy.Strip
    };
    private static string AttitudeCn(DepositType d) => d switch
    {
        DepositType.NearHorizontal => "水平/近水平", DepositType.GentleDip => "缓倾斜", DepositType.Inclined => "倾斜",
        DepositType.SteepDip => "急倾斜", DepositType.MultiSeam => "多煤层", DepositType.IrregularMassive => "不规则/块状", _ => "—"
    };
    private static string StrategyCn(MiningStrategy m) => m switch
    { MiningStrategy.Strip => "条带", MiningStrategy.LongToCross => "纵采转横采", MiningStrategy.Bench => "台阶式", _ => "—" };

    // ───────────── ② 境界来源 ─────────────
    private void RefreshSourceList()
    {
        _loading = true;
        try
        {
            var confirmed = _boundarySchemes.Where(x => x.IsConfirmed).ToList();
            var keepName = (_sourceCombo.SelectedItem as PitScheme)?.Name ?? Current?.SourceSchemeName;
            _sourceCombo.ItemsSource = confirmed;
            _sourceCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
            if (keepName != null) _sourceCombo.SelectedItem = confirmed.FirstOrDefault(x => x.Name == keepName);
        }
        finally { _loading = false; }
    }

    private void OnSourceChanged()
    {
        var ps = _sourceCombo.SelectedItem as PitScheme;
        if (ps != null && !_loading) InheritFromBoundary(ps);
        else if (ps != null && Current is { } cur) { cur.SourceSchemeName = ps.Name; cur.SourcePitResult = ps.Result; }
        ShowSourceInfo(ps);
    }

    private void InheritFromBoundary(PitScheme ps)
    {
        if (Current is not { } cur) return;
        cur.SourceSchemeName = ps.Name; cur.SourcePitResult = ps.Result;
        if (ps.AutoDipDeg is { } d) cur.AutoDipDeg = d;
        if (ps.AutoStrikeDeg is { } st) cur.AutoStrikeDeg = st;
        cur.Attitude = ps.Deposit; cur.Strategy = StrategyFor(ps.Deposit);
        _attitudeCombo.SelectedIndex = (int)cur.Attitude; _strategyCombo.SelectedIndex = (int)cur.Strategy;
        _dipText.Text = cur.AutoDipDeg is { } dd ? $"{dd:0.#}°" : "—";
        _strikeText.Text = cur.AutoStrikeDeg is { } ss ? $"{ss:0.#}°" : "—";
        _status.Text = $"已续用境界方案「{ps.Name}」→ 继承产状 {AttitudeCn(cur.Attitude)} / 走向 {(cur.AutoStrikeDeg is { } a ? $"{a:0}°" : "—")} / 策略 {StrategyCn(cur.Strategy)}";
    }

    private void ShowSourceInfo(PitScheme? ps)
    {
        if (ps == null) { _sourceInfo.Text = "（必选）请先在「计算·比选·确定最终境界」确定一个最终境界方案，再在此选定为来源。"; return; }
        _sourceInfo.Text = ps.Result is { } r
            ? $"境界：开采深度 {r.DepthM:0} m · 煤量 {r.CoalWanT:N0} 万t · 岩量 {r.WasteWanM3:N0} 万m³ · 平均剥采比 {r.AvgRatio:0.00} m³/t"
            : $"方案「{ps.Name}」尚未求解（无 PitResult），请先在「计算·比选·确定最终境界」求解";
    }

    // ───────────── ③ 首采区权重 ─────────────
    private void OnNormalizeWeights()
    {
        double sum = PlanUi.D(_wStrip) + PlanUi.D(_wCoal) + PlanUi.D(_wDepth) + PlanUi.D(_wHaul) + PlanUi.D(_wInner);
        if (sum <= 0) { _status.Text = "权重和为 0，无法归一"; return; }
        foreach (var b in new[] { _wStrip, _wCoal, _wDepth, _wHaul, _wInner }) b.Text = PlanUi.Num(PlanUi.D(b) / sum);
        UpdateWeightSum();
    }
    private void UpdateWeightSum() => _weightSum.Text = (PlanUi.D(_wStrip) + PlanUi.D(_wCoal) + PlanUi.D(_wDepth) + PlanUi.D(_wHaul) + PlanUi.D(_wInner)).ToString("0.00");

    // ───────────── ④ 拉沟·推进 ─────────────
    private void OnBoxcutModeChanged()
    {
        bool manual = _manualRadio.IsChecked == true;
        _manualPanel.IsEnabled = manual;
        if (Current is { } cur && !_loading) cur.BoxcutMode = manual ? BoxcutMode.Manual : BoxcutMode.Auto;
    }

    private void OnNormalizeBoxcutWeights()
    {
        double sum = PlanUi.D(_bcStrip) + PlanUi.D(_bcShallow) + PlanUi.D(_bcHaul) + PlanUi.D(_bcInner) + PlanUi.D(_bcWorkLine) + PlanUi.D(_bcGeo);
        if (sum <= 0) { _status.Text = "拉沟约束权重和为 0，无法归一"; return; }
        foreach (var b in new[] { _bcStrip, _bcShallow, _bcHaul, _bcInner, _bcWorkLine, _bcGeo }) b.Text = PlanUi.Num(PlanUi.D(b) / sum);
        UpdateBoxcutWeightSum();
    }
    private void UpdateBoxcutWeightSum() => _bcWeightSum.Text = (PlanUi.D(_bcStrip) + PlanUi.D(_bcShallow) + PlanUi.D(_bcHaul) + PlanUi.D(_bcInner) + PlanUi.D(_bcWorkLine) + PlanUi.D(_bcGeo)).ToString("0.00");
    private void ReadBoxcutWeightsInto(BoxcutWeights w)
    {
        w.StripRatio = PlanUi.D(_bcStrip); w.Shallow = PlanUi.D(_bcShallow); w.Haul = PlanUi.D(_bcHaul);
        w.InnerDump = PlanUi.D(_bcInner); w.WorkLine = PlanUi.D(_bcWorkLine); w.Geology = PlanUi.D(_bcGeo);
    }

    /// <summary>全自动推荐：① 从激活块体的剥采比场真生成候选；② 无带煤块体 → 按约束权重重排已有候选。</summary>
    private void OnRecommendBoxcut()
    {
        if (Current is not { } cur) return;
        ReadBoxcutWeightsInto(cur.BoxcutWeights);
        var m = _host.ActiveBlockModel;
        if (m != null)
        {
            Cad.StripRatioField? field = null;
            try { field = PlanStripRatioFieldSampler.Sample(m, MiningProgramPlan.DefaultCoalDensity); }
            catch (Exception ex) { _status.Text = $"（推荐候选）剥采比场采样失败：{ex.Message}"; }
            if (field is { CoalColumns: > 0 })
            {
                var outline = ProgramPanelDelineator.ResolveOutline(cur, _host);
                var opts = ProgramPanelDelineator.Recommend(cur, field, outline);
                cur.BoxcutOptions.Clear();
                foreach (var o in opts) cur.BoxcutOptions.Add(o);
                cur.SelectedBoxcut = opts.FirstOrDefault(o => o.Recommended) ?? opts.FirstOrDefault();
                RefreshBoxcutSummary(cur);
                _status.Text = $"已从剥采比场生成 {opts.Count} 个候选（剥采比/煤厚/埋深/工作线长=真值，运输/内排/构造=代理待接）→ 推荐「{cur.SelectedBoxcut?.Name}」";
                return;
            }
        }
        if (cur.BoxcutOptions.Count == 0)
        {
            _status.Text = "（推荐候选）无激活带煤块体、且无候选：请先在「块体模型」激活一个带煤属性的模型，再生成拉沟·推进候选";
            RefreshBoxcutSummary(cur);
            return;
        }
        BoxcutAdvanceOption? best = null;
        foreach (var o in cur.BoxcutOptions)
        {
            o.Recompute(cur.BoxcutWeights);
            o.Recommended = false;
            if (o.Feasible && (best == null || o.TotalScore > best.TotalScore)) best = o;
        }
        if (best != null) { best.Recommended = true; cur.SelectedBoxcut = best; }
        RefreshBoxcutSummary(cur);
        _status.Text = $"（无激活带煤块体）按约束权重重排 {cur.BoxcutOptions.Count} 个样例候选 → 推荐「{best?.Name}」；激活块体后可从剥采比场真生成";
    }

    private void RefreshBoxcutSummary(MiningProgramPlan p)
    {
        var best = p.SelectedBoxcut;
        _boxcutSummary.Text = best == null
            ? "候选在「计算·比选·确定开采程序」窗口比选；当前推荐：—"
            : $"候选 {p.BoxcutOptions.Count} 个 · 当前推荐：{best.Name}（总分 {best.TotalScore:0} · 方位 {best.AdvanceAzimuthDeg:0}° · 工作线长 {best.WorkingLineLengthM:0} m）";
    }

    // ───────────── ⑦ 产能耦合 / 设备派生 ─────────────
    private void OnCheckCapacity()
    {
        double l = PlanUi.D(_workLine), v = PlanUi.D(_advRate), h = PlanUi.D(_benchHeight);
        double q = MiningProgramPlan.CapacityWanTaFrom(l, v, h);
        string tgt = Current is { } cur ? $"（目标 {cur.TargetCapacityWanTa:0}）" : "";
        _capacityCheck.Text = $"{q:0} 万t/a {tgt}";
        double max = PlanUi.D(_maxAdvRate);
        _status.Text = (max > 0 && v > max)
            ? $"⚠ 推进度 {v:0} 超设备上限 {max:0} m/a：需降速或加长工作线（L={l:0}m, H={h:0}m → Q={q:0} 万t/a）"
            : $"产能校核 Q = {l:0}×{v:0}×{h:0}×ρ = {q:0} 万t/a";
    }

    /// <summary>采装设备规格（采宽 / 最小工作线长 / 设备年产能万m³），与 ⑦ 设备下拉同序。</summary>
    public readonly record struct EquipSpec(string Name, double WidthM, double MinLineM, double AnnualCapWanM3);
    public static EquipSpec EquipFor(int idx) => idx switch
    {
        1 => new EquipSpec("WK-20 电铲 + 矿用卡车", 55, 1000, 480),
        2 => new EquipSpec("拉铲倒堆", 60, 1200, 400),
        3 => new EquipSpec("轮斗连续工艺", 35, 1500, 700),
        _ => new EquipSpec("WK-10 电铲 + 矿用卡车", 45, 800, 250),
    };

    private void OnAutoDeriveEquip()
    {
        var sp = EquipFor(_equipCombo.SelectedIndex);
        double h = PlanUi.D(_benchHeight) > 0 ? PlanUi.D(_benchHeight) : 12;
        double vmax = Math.Round(sp.AnnualCapWanM3 * 1e4 / (sp.MinLineM * h), 0);
        _workLine.Text = PlanUi.Num(sp.MinLineM); _miningWidth.Text = PlanUi.Num(sp.WidthM); _maxAdvRate.Text = PlanUi.Num(vmax);
        if (Current is { } cur) { cur.MinWorkingLineM = sp.MinLineM; cur.MiningWidthM = sp.WidthM; cur.MaxAdvanceRateMpa = vmax; cur.EquipmentRef = sp.Name; }
        _status.Text = $"按「{sp.Name}」派生：采宽 {sp.WidthM:0}m · 最小工作线长 {sp.MinLineM:0}m · 推进度上限 {vmax:0} m/a（= 年产能 {sp.AnnualCapWanM3:0}万m³ /(L·H)）";
    }

    // ───────────── 底部 ─────────────
    private void OnAutoRefillAll()
    {
        if (Current == null) return;
        var confirmed = _sourceCombo.ItemsSource as List<PitScheme>;
        bool srcOk = confirmed is { Count: > 0 };
        if (srcOk && _sourceCombo.SelectedItem == null) _sourceCombo.SelectedItem = confirmed![0];
        OnAutoDetectAttitude();
        OnAutoDeriveEquip();
        OnNormalizeWeights();
        OnNormalizeBoxcutWeights();
        OnRecommendBoxcut();
        _status.Text = $"全部自动重填完成：续境界{(srcOk ? "✓" : "—")} · 产状PCA · 设备派生 · 权重归一 · 拉沟候选（无激活块体的项已降级）";
    }
    private void OnSaveScheme()
    {
        if (Current == null) return;
        if (_sourceCombo.SelectedItem is not PitScheme)
        { _status.Text = "（必选）境界来源未选定——请先选一个『已确定的最终境界方案』再保存。"; return; }
        SaveToScheme(Current);
        _status.Text = $"已保存方案「{Current.Name}」（{StrategyCn(Current.Strategy)} · {Current.SplitText} · 拉沟{Current.BoxcutModeText}）";
        var sel = Current; _schemeList.ItemsSource = null; _schemeList.ItemsSource = Schemes; _schemeList.SelectedItem = sel;
    }
}
