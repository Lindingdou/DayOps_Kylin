using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;
using AdvanceMode = PitMine3D.Kylin.Cad.AdvanceMode;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「中长远进度计划编制 · 基础约束」窗口（中长远组第 1 按钮；移植原 <c>LongTermConfigWindow</c>）。只编**一套** <see cref="LongTermBase"/>
/// （来源/时间骨架/达产爬坡政策/均衡·比选权重/经济/内排/约束）——不随候选方案变的给定条件。候选方案一律由「派生计划方案」在此约束上按人为指定的工作线生成。
/// </summary>
internal sealed class LongTermConfigWindow : Window
{
    private readonly LongTermBase _base;
    private readonly IReadOnlyList<MiningProgramPlan> _programs;
    private readonly IPlanEntityHost _host;

    private readonly ComboBox _sourceProgCombo = new() { Height = 28, Width = 260, FontSize = 13 };
    private readonly TextBox _reserve = PlanUi.Box("30000"), _baseRatio = PlanUi.Box("6.5", 60), _nEco = PlanUi.Box("12", 50);
    private readonly TextBlock _sourceInfo = RoadUi.Hint("（无来源：用样例储量）");
    private readonly TextBox _startYear = PlanUi.Box("2027", 70), _horizon = PlanUi.Box("15", 60), _periodLen = PlanUi.Box("1", 50), _capacity = PlanUi.Box("1000", 70), _life = PlanUi.Box("30", 50);
    private readonly TextBlock _lifeCalc = RoadUi.Hint("服务年限 T = 可采储量 ÷ A_p = —");
    private readonly ComboBox _rampCombo;
    private readonly TextBox _basicYears = PlanUi.Box("2", 50), _rampYears = PlanUi.Box("3", 50), _balStage = PlanUi.Box("", 56), _rampFirstPct = PlanUi.Box("35", 56);
    private readonly TextBox _coalPrice = PlanUi.Box("320", 70), _mineCost = PlanUi.Box("95", 70), _stripCost = PlanUi.Box("28", 70), _discount = PlanUi.Box("8", 70);
    private readonly CheckBox _innerEnabled = new() { Content = "启用内排", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly TextBox _innerStart = PlanUi.Box("5", 50), _benchHeight = PlanUi.Box("12", 56), _maxAdv = PlanUi.Box("360", 56), _minAdv = PlanUi.Box("80", 56);
    private readonly TextBox _dwPlateau = PlanUi.Box("0.18", 54), _dwPeak = PlanUi.Box("0.22", 54), _dwEarly = PlanUi.Box("0.15", 54), _dwInner = PlanUi.Box("0.15", 54), _dwNpv = PlanUi.Box("0.18", 54), _dwBal = PlanUi.Box("0.12", 54);
    private readonly TextBlock _dwSum = new() { Text = "1.00", FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF)), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = RoadUi.Hint("");

    public LongTermConfigWindow(IPlanEntityHost host, IReadOnlyList<MiningProgramPlan>? programs)
    {
        _host = host;
        _programs = programs ?? MiningProgramStore.Schemes;
        _base = LongTermSchemeStore.Base;
        Title = "中长远进度计划编制 — 基础约束";
        PlanUi.Place(this, 860, 760);
        _rampCombo = PlanUi.Combo(new[] { "线性爬坡（匀速升）", "阶梯爬坡（分档跳升）", "尽快达产（首年即高）" }, 0, 220);
        _sourceProgCombo.ItemsSource = _programs;
        _sourceProgCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Name");

        Content = PlanUi.Shell(
            PlanUi.Header("中长远进度计划编制 · 基础约束", "设定不随方案变的给定条件（一套盘子）：候选方案一律由「派生计划方案」在此约束上、按人为指定的工作线生成", Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1E, 0x40, 0xAF)),
            BuildBody(), BuildFooter());

        bool inherited = LongTermSchemeStore.AutoInheritIfNeeded(_programs);
        LoadFromBase();
        if (inherited) _status.Text = $"已自动续上开采程序「{_base.SourceProgramName}」（可采储量/剥采比/经济已带过来，可改）";
        _capacity.LostFocus += (_, _) => UpdateLifeCalc(); _reserve.LostFocus += (_, _) => UpdateLifeCalc();
    }

    private static StackPanel W(string label, TextBox box, double lw) { var l = RoadUi.Lbl(label); l.Width = lw; l.VerticalAlignment = VerticalAlignment.Center; var r = RoadUi.Row(l, box); r.Margin = new Thickness(0, 4, 16, 4); return r; }

    private Control BuildBody()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };

        var g1 = new StackPanel();
        g1.Children.Add(RoadUi.Hint("选定开采程序后，可采储量/基准剥采比/经济/约束自动带过来；无来源时可直接填储量/剥采比。工作线是派生的决策变量，不在此设。"));
        g1.Children.Add(PlanUi.LabeledRow("开采程序来源", _sourceProgCombo, RoadUi.Btn("继承", OnInheritSource), 120, 260, new Thickness(0, 8, 0, 8)));
        var w1 = new WrapPanel(); w1.Children.Add(W("可采储量(万t)", _reserve, 100)); w1.Children.Add(W("基准剥采比", _baseRatio, 90)); w1.Children.Add(W("经济合理剥采比 n经", _nEco, 120));
        g1.Children.Add(w1); _sourceInfo.Margin = new Thickness(0, 6, 0, 0); g1.Children.Add(_sourceInfo);
        stack.Children.Add(PlanUi.Group("① 来源（承接采区划分④确定的开采程序 + 分期境界）", g1));

        var g2 = new StackPanel();
        var w2 = new WrapPanel(); w2.Children.Add(W("起始年", _startYear, 80)); w2.Children.Add(W("计划期(年)", _horizon, 90)); w2.Children.Add(W("期粒度(年)", _periodLen, 90));
        w2.Children.Add(W("设计能力 A_p(万t/a)", _capacity, 120)); w2.Children.Add(W("目标服务年限(a)", _life, 110)); w2.Children.Add(RoadUi.Btn("储量÷能力反算", OnReanalyzeLife, 110));
        g2.Children.Add(w2); g2.Children.Add(PlanUi.InfoBox(_lifeCalc, new Thickness(0, 6, 0, 0)));
        stack.Children.Add(PlanUi.Group("② 时间骨架（计划期 / 设计能力 / 服务年限）", g2));

        var g3 = new StackPanel();
        g3.Children.Add(PlanUi.LabeledRow("爬坡曲线", _rampCombo, null, 120, 220));
        var w3 = new WrapPanel(); w3.Children.Add(W("基建期(a)", _basicYears, 80)); w3.Children.Add(W("爬坡期(a)", _rampYears, 80));
        var l3 = RoadUi.Lbl("投产年达产率(%)"); l3.Width = 110; ToolTip.SetTip(l3, "与④里那个是同一个数，改哪边都行"); var h3 = RoadUi.Hint("见 ④ 均衡"); h3.VerticalAlignment = VerticalAlignment.Center;
        var r3 = RoadUi.Row(l3, h3); r3.Margin = new Thickness(0, 4, 16, 4); w3.Children.Add(r3);
        g3.Children.Add(w3);
        stack.Children.Add(PlanUi.Group("③ 达产爬坡政策（默认；派生可按「达产节奏」轴覆盖）", g3));

        var g4 = new StackPanel();
        var w4 = new WrapPanel();
        var bs = W("均衡期数 K", _balStage, 100); ToolTip.SetTip(_balStage, "分几段均衡。留空 = 按曲线形态自动建议（总超前剥离降到单段的 20% 以下的最小段数）");
        var auto = RoadUi.Hint("留空 = 自动"); auto.VerticalAlignment = VerticalAlignment.Center; auto.Margin = new Thickness(0, 0, 16, 0);
        w4.Children.Add(bs); w4.Children.Add(auto);
        var rf = W("投产年达产率(%)", _rampFirstPct, 110); ToolTip.SetTip(_rampFirstPct, "投产第一年做到设计能力的百分之多少。选上面的节奏档会预填这个数，可直接改");
        w4.Children.Add(rf);
        g4.Children.Add(w4);
        g4.Children.Add(RoadUi.Hint("均衡里要人拿主意的只有【期数 K】。段界在哪儿、每段均衡剥采比多少、超前剥离多少，全由块体沿工作线给出的真实 VP 曲线解出（同「剥采比均衡」窗口那套 DP），不需要权重组、也不需要峰值倍数。"));
        stack.Children.Add(PlanUi.Group("④ 均衡（分阶段均衡 ·  段界与各段剥采比由块体给出的真实 VP 曲线解出）", g4));

        var g5 = new WrapPanel();
        g5.Children.Add(W("煤价 d(元/t)", _coalPrice, 120)); g5.Children.Add(W("采煤成本 a(元/t)", _mineCost, 120)); g5.Children.Add(W("剥离成本 b(元/m³)", _stripCost, 120)); g5.Children.Add(W("折现率(%/a)", _discount, 120));
        stack.Children.Add(PlanUi.Group("⑤ 经济（逐年现金流 → NPV）", g5));

        var g6 = new StackPanel();
        var r6 = RoadUi.Row(_innerEnabled, RoadUi.Lbl("内排起转年(自起始年)"), _innerStart); r6.Margin = new Thickness(0, 0, 0, 6);
        g6.Children.Add(r6);
        var w6 = new WrapPanel(); w6.Children.Add(W("台阶高 H(m)", _benchHeight, 90)); w6.Children.Add(W("推进度上限(m/a)", _maxAdv, 110)); w6.Children.Add(W("推进度下限(m/a)", _minAdv, 110));
        g6.Children.Add(w6);
        stack.Children.Add(PlanUi.Group("⑥ 内排　⑦ 约束（台阶高 / 推进度上下限；工作线是派生的决策变量，不在此设）", g6));

        var g8 = new StackPanel();
        var w8 = new WrapPanel();
        w8.Children.Add(W("稳产期", _dwPlateau, 70)); w8.Children.Add(W("削峰", _dwPeak, 70)); w8.Children.Add(W("早达产", _dwEarly, 70));
        w8.Children.Add(W("内排率", _dwInner, 70)); w8.Children.Add(W("NPV", _dwNpv, 70)); w8.Children.Add(W("储量均衡", _dwBal, 70));
        g8.Children.Add(w8);
        var t8 = RoadUi.Row(RoadUi.Btn("归一化", OnNormalizeDecision, 80), RoadUi.Hint("权重和 = "), _dwSum); t8.Margin = new Thickness(0, 6, 0, 0);
        g8.Children.Add(t8);
        stack.Children.Add(PlanUi.Group("⑧ 比选权重（联合对比加权评分，归一）", g8, new Thickness(0, 0, 0, 4)));

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var bPrev = RoadUi.Btn("试算（校验约束）", OnPreview, 130);
        ToolTip.SetTip(bPrev, "用已指定的第一条工作线（没有则用一条假设线）试排一遍，校验约束是否产出合理计划；不存为方案");
        DockPanel.SetDock(bPrev, Avalonia.Controls.Dock.Left); dock.Children.Add(bPrev);
        var right = RoadUi.Foot(RoadUi.Btn("保存约束", OnSaveBase, 90), RoadUi.Btn("关闭", () => { SaveToBase(); Close(); }, 70));
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right); dock.Children.Add(right);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(14, 0, 0, 0);
        dock.Children.Add(_status);
        return PlanUi.Footer(dock);
    }

    private void LoadFromBase()
    {
        var s = _base;
        _reserve.Text = PlanUi.Num(s.CoalReserveWanT); _baseRatio.Text = PlanUi.Num(s.BaseStripRatio); _nEco.Text = PlanUi.Num(s.EconomicStripRatioMax);
        _sourceInfo.Text = string.IsNullOrEmpty(s.SourceProgramName) ? "（无来源：用样例储量）" : $"来源开采程序：{s.SourceProgramName}";
        _sourceProgCombo.SelectedItem = _programs.FirstOrDefault(x => x.Name == s.SourceProgramName);
        _startYear.Text = s.StartYear.ToString(); _horizon.Text = s.HorizonYears.ToString(); _periodLen.Text = s.PeriodLenYears.ToString();
        _capacity.Text = PlanUi.Num(s.DesignCapacityWanTa); _life.Text = PlanUi.Num(s.TargetServiceLifeYears);
        UpdateLifeCalc();
        _rampCombo.SelectedIndex = (int)s.RampProfile;
        _basicYears.Text = s.BasicStrippingYears.ToString(); _rampYears.Text = s.RampUpYears.ToString();
        _balStage.Text = s.BalanceStageCount?.ToString() ?? ""; _rampFirstPct.Text = PlanUi.Num(s.RampFirstYearPct);
        _coalPrice.Text = PlanUi.Num(s.CoalPriceYuanT); _mineCost.Text = PlanUi.Num(s.MiningCostYuanT); _stripCost.Text = PlanUi.Num(s.StripCostYuanM3); _discount.Text = PlanUi.Num(s.DiscountRatePct);
        _innerEnabled.IsChecked = s.InnerDumpEnabled; _innerStart.Text = s.InnerDumpStartYear.ToString();
        _benchHeight.Text = PlanUi.Num(s.BenchHeightM); _maxAdv.Text = PlanUi.Num(s.MaxAdvanceRateMpa); _minAdv.Text = PlanUi.Num(s.MinAdvanceRateMpa);
        _dwPlateau.Text = PlanUi.Num(s.Decision.StablePlateau); _dwPeak.Text = PlanUi.Num(s.Decision.PeakShaving); _dwEarly.Text = PlanUi.Num(s.Decision.EarlyCapacity);
        _dwInner.Text = PlanUi.Num(s.Decision.InnerDumpRate); _dwNpv.Text = PlanUi.Num(s.Decision.Npv); _dwBal.Text = PlanUi.Num(s.Decision.ReserveBalance);
        _dwSum.Text = s.Decision.Sum.ToString("0.00");
    }

    private void SaveToBase()
    {
        var s = _base;
        s.CoalReserveWanT = PlanUi.D(_reserve); s.BaseStripRatio = PlanUi.D(_baseRatio); s.EconomicStripRatioMax = PlanUi.D(_nEco);
        s.StartYear = (int)Math.Max(0, PlanUi.D(_startYear)); s.HorizonYears = (int)Math.Max(0, PlanUi.D(_horizon)); s.PeriodLenYears = (int)Math.Max(1, PlanUi.D(_periodLen));
        s.DesignCapacityWanTa = PlanUi.D(_capacity); s.TargetServiceLifeYears = PlanUi.D(_life);
        s.RampProfile = (RampProfileKind)Math.Max(0, _rampCombo.SelectedIndex);
        s.BasicStrippingYears = (int)Math.Max(0, PlanUi.D(_basicYears)); s.RampUpYears = (int)Math.Max(0, PlanUi.D(_rampYears));
        s.BalanceStageCount = int.TryParse(_balStage.Text?.Trim(), out int k) && k > 0 ? k : null;
        s.RampFirstYearPct = PlanUi.D(_rampFirstPct) > 0 ? PlanUi.D(_rampFirstPct) : s.RampFirstYearPct;
        s.CoalPriceYuanT = PlanUi.D(_coalPrice); s.MiningCostYuanT = PlanUi.D(_mineCost); s.StripCostYuanM3 = PlanUi.D(_stripCost); s.DiscountRatePct = PlanUi.D(_discount);
        s.InnerDumpEnabled = _innerEnabled.IsChecked == true; s.InnerDumpStartYear = (int)Math.Max(0, PlanUi.D(_innerStart));
        s.BenchHeightM = PlanUi.D(_benchHeight); s.MaxAdvanceRateMpa = PlanUi.D(_maxAdv); s.MinAdvanceRateMpa = PlanUi.D(_minAdv);
        s.Decision.StablePlateau = PlanUi.D(_dwPlateau); s.Decision.PeakShaving = PlanUi.D(_dwPeak); s.Decision.EarlyCapacity = PlanUi.D(_dwEarly);
        s.Decision.InnerDumpRate = PlanUi.D(_dwInner); s.Decision.Npv = PlanUi.D(_dwNpv); s.Decision.ReserveBalance = PlanUi.D(_dwBal);
    }

    private void UpdateLifeCalc()
    {
        double ap = PlanUi.D(_capacity);
        double t = ap > 0 ? PlanUi.D(_reserve) / ap : 0;
        _lifeCalc.Text = $"服务年限 T = 可采储量 {PlanUi.D(_reserve):N0} ÷ A_p {ap:0} = {t:0.0} a";
    }

    private void OnInheritSource()
    {
        if (_sourceProgCombo.SelectedItem is not MiningProgramPlan mp) { _status.Text = "（继承）请先在下拉里选一个开采程序方案——或直接填可采储量/基准剥采比"; return; }
        _base.InheritFrom(mp);
        LoadFromBase();
        _status.Text = $"已继承开采程序「{mp.Name}」→ 可采储量 {_base.CoalReserveWanT:N0} 万t · 基准剥采比 {_base.BaseStripRatio:0.0}";
    }

    private void OnReanalyzeLife()
    {
        UpdateLifeCalc();
        double ap = PlanUi.D(_capacity);
        if (ap > 0) _life.Text = PlanUi.Num(Math.Round(PlanUi.D(_reserve) / ap, 0));
    }

    private void OnNormalizeDecision()
    {
        var boxes = new[] { _dwPlateau, _dwPeak, _dwEarly, _dwInner, _dwNpv, _dwBal };
        double sum = boxes.Sum(PlanUi.D);
        if (sum <= 0) { _status.Text = "比选权重和为 0，无法归一"; return; }
        foreach (var b in boxes) b.Text = PlanUi.Num(PlanUi.D(b) / sum);
        _dwSum.Text = boxes.Sum(PlanUi.D).ToString("0.00");
    }

    /// <summary>试排一遍，校验约束是否产出合理计划（不存为方案）。工作线优先取已指定的第一条；没有时用假设线并明说。</summary>
    private void OnPreview()
    {
        SaveToBase();
        var wl = LongTermSchemeStore.SpecifiedWorkLines().FirstOrDefault();
        bool assumed = wl == null;
        wl ??= new WorkLineAdvanceVariant { Label = "假设", WorkLineLenM = 1100, AdvanceAzimuthDeg = 90, AdvanceMode = AdvanceMode.Parallel };
        var probe = _base.NewCandidate(wl, "（试算）", _base.DesignCapacityWanTa);
        LongTermScheduler.Schedule(probe, _host.ActiveBlockModel, LongTermDumpBridge.TryReadForm(_host.Db));
        var r = probe.Result;
        string wlText = assumed ? $"⚠假设工作线 {wl.Caption}（图上未指定 —— 只用于校验约束，不是方案指标）" : $"按已指定工作线 {wl.Label}·{wl.Caption}";
        _status.Text = r == null ? $"试算没跑出来：{probe.ScheduleNote}" :
            $"试算({wlText})：服务年限 {r.ServiceLifeYears:0}a · 达产 {r.TimeToCapacityYears:0}a({r.DesignCalcYearLabel}) · 峰值剥采比 {r.ProductionRatioPeak:0.0}(n经{_base.EconomicStripRatioMax:0}) · 内排率 {r.InnerDumpPct:0}% · NPV {r.Npv:N0}万 · {r.OkText} → 去「派生计划方案」拾取工作线造多套";
    }

    private void OnSaveBase()
    {
        SaveToBase();
        _status.Text = $"已保存基础约束（A_p {_base.DesignCapacityWanTa:0}万t/a · 可采储量 {_base.CoalReserveWanT:N0}万t · n经 {_base.EconomicStripRatioMax:0} · {_base.SourceText}）→ 去「派生计划方案」拾取图上工作线造候选";
    }
}
