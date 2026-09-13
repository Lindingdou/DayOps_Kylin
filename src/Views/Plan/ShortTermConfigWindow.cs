using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.GeoDb;
using PitMine3D.Kylin.Views.Road;
using CalendarScenario = PitMine3D.Kylin.Cad.Plan.CalendarScenario;
using DispatchStrategy = PitMine3D.Kylin.Cad.Plan.DispatchStrategy;
using ShortTermScheduler = PitMine3D.Kylin.Cad.Plan.ShortTermScheduler;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「短期生产计划编制 · 基础约束」窗口（短期组第 1 按钮；移植原 <c>ShortTermConfigWindow</c>）。只编**一套** <see cref="ShortTermBase"/>
/// （来源年度目标 / 时间骨架 / 现场参数 / 逐月配置表 / 月度均衡·比选权重 / 约束）——不随候选方案变的给定条件。
/// 候选月度计划由「派生计划方案」按作业组织×工作历生成。基础约束与逐月配置表均落盘（软件目录 Data\短期计划配置）。
/// </summary>
internal sealed class ShortTermConfigWindow : Window
{
    private readonly ShortTermBase _base;
    private readonly IReadOnlyList<LongTermPlan>? _longTerms;

    private readonly ComboBox _sourceLtCombo = new() { Height = 28, Width = 260, FontSize = 13 };
    private readonly TextBox _planYear = PlanUi.Box("2027", 64), _annualCoal = PlanUi.Box("1000", 70), _annualStrip = PlanUi.Box("6500", 80);
    private readonly TextBlock _sourceInfo = RoadUi.Hint("（无来源：用样例年度目标）");
    private readonly TextBox _startMonth = PlanUi.Box("1", 50), _monthCount = PlanUi.Box("12", 50);
    private readonly TextBlock _baseRatioText = RoadUi.Hint("基准剥采比 = —", 12.5);
    private readonly TextBox _workdays = PlanUi.Box("25", 56), _shifts = PlanUi.Box("3", 50), _winterMonths = PlanUi.Box("12,1,2", 80), _winterDerate = PlanUi.Box("20", 50),
                             _maintMonth = PlanUi.Box("7", 50), _maintDerate = PlanUi.Box("30", 50), _equipCount = PlanUi.Box("4", 50), _avail = PlanUi.Box("82", 50), _equipCap = PlanUi.Box("28", 56),
                             _ytdCoal = PlanUi.Box("0", 56), _ytdStrip = PlanUi.Box("0", 60);
    private readonly TextBlock _fieldPreview = RoadUi.Hint("", 12);
    private readonly DataGrid _monthlyGrid;
    private readonly TextBlock _monthlyCaption = RoadUi.Hint("", 12), _monthlyReconcile = RoadUi.Hint("", 12);
    private readonly StackPanel _monthlyNotes = new();
    private readonly TextBox _balOut = PlanUi.Box("0.45", 56), _balRatio = PlanUi.Box("0.30", 56), _balEquip = PlanUi.Box("0.25", 56);
    private readonly TextBlock _balSum = Sum();
    private readonly TextBox _ceilCoal = PlanUi.Box("120", 56), _ceilStrip = PlanUi.Box("800", 60), _ratioCeil = PlanUi.Box("12", 50), _tol = PlanUi.Box("3", 50), _minPrep = PlanUi.Box("2", 50), _benchH = PlanUi.Box("12", 50), _workLine = PlanUi.Box("1100", 60);
    private readonly TextBox _dwComp = PlanUi.Box("0.28", 54), _dwBal = PlanUi.Box("0.22", 54), _dwUtil = PlanUi.Box("0.18", 54), _dwPeak = PlanUi.Box("0.16", 54), _dwAdv = PlanUi.Box("0.16", 54);
    private readonly TextBlock _dwSum = Sum();
    private readonly TextBlock _status = RoadUi.Hint("");
    private static TextBlock Sum() => new() { Text = "1.00", FontWeight = FontWeight.Bold, Foreground = PlanUi.OrangeBrush, VerticalAlignment = VerticalAlignment.Center };

    private bool _baseLoadedFromDisk;
    private readonly List<string> _loadIssues = new();

    public ShortTermConfigWindow(IReadOnlyList<LongTermPlan>? longTerms = null)
    {
        _longTerms = longTerms;
        _base = ShortTermSchemeStore.Base;
        Title = "短期生产计划编制 — 基础约束（月度）";
        PlanUi.Place(this, 900, 780);
        _sourceLtCombo.ItemsSource = _longTerms;
        _sourceLtCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Name");

        _monthlyGrid = PlanUi.EditableTable(new (string, string, double, bool)[]
        {
            ("期次", "PeriodKey", 84, true), ("采出(万t)", "CoalWanT", 92, false), ("剥离(万m³)", "StripWanM3", 104, false), ("剥采比", "Ratio", 74, true),
            ("作业日", "Workdays", 72, false), ("剥离能力(万m³)", "StripCapWanM3", 130, false), ("车队能力(万t·km)", "FleetCapWanTKm", 140, false),
            ("来源", "SourceText", 84, true), ("覆盖列", "OverriddenText", 150, true), ("备注", "Note", 200, false),
        }, 228);
        // 内排 / 检修 两个勾选列（原 DataGridCheckBoxColumn），插在车队能力之后
        _monthlyGrid.Columns.Insert(7, new DataGridCheckBoxColumn { Header = "内排", Binding = new Avalonia.Data.Binding("InternalDumpEnabled") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(60) });
        _monthlyGrid.Columns.Insert(8, new DataGridCheckBoxColumn { Header = "检修", Binding = new Avalonia.Data.Binding("IsMaintenance") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(60) });
        PlanUi.FitHeaders(_monthlyGrid);
        _monthlyGrid.SelectionMode = DataGridSelectionMode.Extended;

        Content = PlanUi.Shell(
            PlanUi.Header("短期生产计划编制 · 基础约束", "承接中长远某一年的年采剥目标，定月度计划的盘子；现场参数(工作历/设备)与逐月配置表在本窗编辑，可采区域/作业面在各自按钮；候选方案由「派生计划方案」按作业组织×工作历生成", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C)),
            BuildBody(), BuildFooter());

        // ★ 开窗先从盘子读回基础约束 —— Base 是纯内存静态类；不读回来逐月表会按样例重派再贴上去年那份人工覆盖。
        _loadIssues.Clear();
        if (ShortTermBaseStore.TryLoadInto(_base, out var iss)) _baseLoadedFromDisk = true;
        else _loadIssues.AddRange(iss);
        LoadFromBase();
        if (_baseLoadedFromDisk) _status.Text = $"已从 {ShortTermBaseStore.DefaultPath} 读回基础约束。";
        else if (_loadIssues.Count > 0 && _loadIssues.Exists(s => s.StartsWith("◆"))) _status.Text = string.Join("　", _loadIssues);
        _annualCoal.LostFocus += (_, _) => UpdateBaseRatio(); _annualStrip.LostFocus += (_, _) => UpdateBaseRatio();
    }

    private static StackPanel W(string label, TextBox box, double lw, string? tip = null)
    {
        var l = RoadUi.Lbl(label); l.Width = lw; l.VerticalAlignment = VerticalAlignment.Center;
        if (tip != null) ToolTip.SetTip(box, tip);
        var r = RoadUi.Row(l, box); r.Margin = new Thickness(0, 4, 16, 4); return r;
    }

    private Control BuildBody()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };

        var g1 = new StackPanel();
        g1.Children.Add(RoadUi.Hint("选定中长远方案 + 年度后，年采出 / 年剥离目标自动带过来；无来源时可直接填年度目标。"));
        g1.Children.Add(PlanUi.LabeledRow("中长远方案来源", _sourceLtCombo, W("计划年度", _planYear, 60), 120, 260, new Thickness(0, 8, 0, 4)));
        var w1 = new WrapPanel(); w1.Children.Add(W("年采出目标(万t)", _annualCoal, 110)); w1.Children.Add(W("年剥离目标(万m³)", _annualStrip, 120)); w1.Children.Add(RoadUi.Btn("继承中长远年度", OnInheritSource, 120));
        g1.Children.Add(w1); _sourceInfo.Margin = new Thickness(0, 6, 0, 0); g1.Children.Add(_sourceInfo);
        stack.Children.Add(PlanUi.Group("① 来源（承接中长远进度计划某一年的年采剥目标）", g1));

        var g2 = new WrapPanel();
        g2.Children.Add(W("起始月", _startMonth, 80)); g2.Children.Add(W("计划月数", _monthCount, 80));
        _baseRatioText.VerticalAlignment = VerticalAlignment.Center; g2.Children.Add(_baseRatioText);
        stack.Children.Add(PlanUi.Group("② 时间骨架（月度）", g2));

        var g3 = new StackPanel();
        var w3a = new WrapPanel(); w3a.Children.Add(W("月标准作业日", _workdays, 100)); w3a.Children.Add(W("每日班次", _shifts, 80)); w3a.Children.Add(W("季节降效月", _winterMonths, 80, "逗号分隔，如 12,1,2")); w3a.Children.Add(W("季节降效(%)", _winterDerate, 100));
        var w3b = new WrapPanel(); w3b.Children.Add(W("集中检修月", _maintMonth, 80, "0 = 无集中检修")); w3b.Children.Add(W("检修月降效(%)", _maintDerate, 80)); w3b.Children.Add(W("设备台数", _equipCount, 100)); w3b.Children.Add(W("设备完好率(%)", _avail, 80)); w3b.Children.Add(W("单台满月能力(万m³)", _equipCap, 120));
        var w3c = new WrapPanel(); w3c.Children.Add(W("年初已采出(万t)", _ytdCoal, 100, "年初至计划起点已完成量（不计入本表口径，只用于完成率参照）")); w3c.Children.Add(W("年初已剥离(万m³)", _ytdStrip, 110)); w3c.Children.Add(RoadUi.Btn("按现场参数重新派生", OnRefreshFieldPreview, 130));
        g3.Children.Add(w3a); g3.Children.Add(w3b); g3.Children.Add(w3c);
        _fieldPreview.Margin = new Thickness(0, 8, 0, 0); g3.Children.Add(_fieldPreview);
        stack.Children.Add(PlanUi.Group("③ 现场参数（工作历 / 设备 / 季节降效 / 年初已完成）——月度分配的物理基础", g3));

        var g3b = new StackPanel();
        g3b.Children.Add(RoadUi.Hint("一行 = 一个计划月。初值由「月度计划编制」的排产器（均衡型×标准工作历）摊出来；任何一格改过即记为人工覆盖，重新派生时不冲掉。剥采比只读（剥离÷采出）。剥离能力/车队能力留空 = 不限（空不等于 0：下游口径里 0 = 该月不能剥）。"));
        var tb = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var bts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bts.Children.Add(RoadUi.Btn("按当前参数派生", OnRefreshFieldPreview, 106));
        bts.Children.Add(RoadUi.Btn("重置选中行", OnResetSelectedMonthly, 84));
        bts.Children.Add(RoadUi.Btn("全部重置", () => _ = OnResetAllMonthlyAsync(), 72));
        bts.Children.Add(RoadUi.Btn("保存逐月表", OnSaveMonthly, 88));
        bts.Children.Add(RoadUi.Btn("读回逐月表", OnLoadMonthly, 88));
        DockPanel.SetDock(bts, Avalonia.Controls.Dock.Left); tb.Children.Add(bts);
        _monthlyCaption.VerticalAlignment = VerticalAlignment.Center; _monthlyCaption.Margin = new Thickness(10, 0, 0, 0); _monthlyCaption.TextTrimming = TextTrimming.CharacterEllipsis; tb.Children.Add(_monthlyCaption);
        g3b.Children.Add(tb);
        g3b.Children.Add(_monthlyGrid);
        _monthlyReconcile.Margin = new Thickness(0, 6, 0, 0); g3b.Children.Add(_monthlyReconcile);
        _monthlyNotes.Margin = new Thickness(0, 4, 0, 0); g3b.Children.Add(_monthlyNotes);
        g3b.Children.Add(RoadUi.Hint("备采储量（三量保有）由几何算出 →「量驱动采剥接续」跑一次并「确定入库」；作业面接续在「确定开采程序」；去向在「采排配对」。", 11.5));
        stack.Children.Add(PlanUi.Group("③b 逐月配置表（月度剥采量 / 作业日 / 剥离能力 / 车队能力 / 内排 · 可人工覆盖）", g3b));

        var g4 = new StackPanel();
        var w4 = new WrapPanel(); w4.Children.Add(W("月产量均衡", _balOut, 90)); w4.Children.Add(W("剥采比削峰", _balRatio, 90)); w4.Children.Add(W("设备负荷均衡", _balEquip, 90));
        g4.Children.Add(w4);
        var t4 = RoadUi.Row(RoadUi.Btn("归一化", OnNormalizeBalance, 80), RoadUi.Hint("权重和 = "), _balSum); t4.Margin = new Thickness(0, 6, 0, 0); g4.Children.Add(t4);
        stack.Children.Add(PlanUi.Group("④ 月度均衡权重（月产量 / 剥采比削峰 / 设备负荷，归一）", g4));

        var g5 = new WrapPanel();
        g5.Children.Add(W("月采出上限(万t)", _ceilCoal, 110)); g5.Children.Add(W("月剥离上限(万m³)", _ceilStrip, 120)); g5.Children.Add(W("剥采比上限(m³/t)", _ratioCeil, 120)); g5.Children.Add(W("完成率容差(±%)", _tol, 110));
        g5.Children.Add(W("备采保有下限(月)", _minPrep, 120)); g5.Children.Add(W("台阶高 H(m)", _benchH, 90)); g5.Children.Add(W("工作线长 L(m)", _workLine, 100));
        stack.Children.Add(PlanUi.Group("⑤ 约束（硬约束：月产/剥离上限 · 剥采比上限 · 完成率容差 · 备采保有 · 推进换算）", g5));

        var g6 = new StackPanel();
        var w6 = new WrapPanel(); w6.Children.Add(W("完成率", _dwComp, 80)); w6.Children.Add(W("月产均衡", _dwBal, 80)); w6.Children.Add(W("设备利用", _dwUtil, 80)); w6.Children.Add(W("削峰", _dwPeak, 80)); w6.Children.Add(W("推进达标", _dwAdv, 80));
        g6.Children.Add(w6);
        var t6 = RoadUi.Row(RoadUi.Btn("归一化", OnNormalizeDecision, 80), RoadUi.Hint("权重和 = "), _dwSum); t6.Margin = new Thickness(0, 6, 0, 0); g6.Children.Add(t6);
        stack.Children.Add(PlanUi.Group("⑥ 比选权重（联合对比加权评分，归一）", g6, new Thickness(0, 0, 0, 4)));

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private Control BuildFooter()
    {
        var dock = new DockPanel();
        var bPrev = RoadUi.Btn("用默认组织试算", OnPreview, 120);
        ToolTip.SetTip(bPrev, "用默认组织（均衡型·标准工作历）试排一遍，校验约束是否产出合理计划；不存为方案");
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
        if (_longTerms is { Count: > 0 }) _sourceLtCombo.SelectedItem = _longTerms.FirstOrDefault(x => x.Name == s.SourceLongTermName);
        _planYear.Text = s.PlanYear.ToString();
        _annualCoal.Text = PlanUi.Num(s.AnnualCoalTargetWanT); _annualStrip.Text = PlanUi.Num(s.AnnualStripTargetWanM3);
        _sourceInfo.Text = string.IsNullOrEmpty(s.SourceLongTermName) ? "（无来源：用样例年度目标）" : $"来源：{s.SourceLongTermName} · 计划 {s.PlanYear} 年";
        _startMonth.Text = s.StartMonth.ToString(); _monthCount.Text = s.MonthCount.ToString();
        UpdateBaseRatio();
        _balOut.Text = PlanUi.Num(s.Balance.OutputSmooth); _balRatio.Text = PlanUi.Num(s.Balance.RatioSmooth); _balEquip.Text = PlanUi.Num(s.Balance.EquipSmooth);
        _balSum.Text = s.Balance.Sum.ToString("0.00");
        _ceilCoal.Text = PlanUi.Num(s.MonthlyCoalCeilingWanT); _ceilStrip.Text = PlanUi.Num(s.MonthlyStripCeilingWanM3); _ratioCeil.Text = PlanUi.Num(s.RatioCeiling);
        _tol.Text = PlanUi.Num(s.CompletionTolerancePct); _minPrep.Text = PlanUi.Num(s.MinPreparedMonths); _benchH.Text = PlanUi.Num(s.BenchHeightM); _workLine.Text = PlanUi.Num(s.WorkLineLenM);
        _dwComp.Text = PlanUi.Num(s.Decision.Completion); _dwBal.Text = PlanUi.Num(s.Decision.OutputBalance); _dwUtil.Text = PlanUi.Num(s.Decision.EquipUtil);
        _dwPeak.Text = PlanUi.Num(s.Decision.PeakShaving); _dwAdv.Text = PlanUi.Num(s.Decision.AdvanceAttain);
        _dwSum.Text = s.Decision.Sum.ToString("0.00");
        var f = s.Field;
        _workdays.Text = PlanUi.Num(f.StandardWorkdays); _shifts.Text = f.ShiftsPerDay.ToString(); _winterMonths.Text = f.WinterMonthsCsv; _winterDerate.Text = PlanUi.Num(f.WinterDeratePct);
        _maintMonth.Text = f.MaintenanceMonth.ToString(); _maintDerate.Text = PlanUi.Num(f.MaintenanceDeratePct); _equipCount.Text = f.EquipmentCount.ToString();
        _avail.Text = PlanUi.Num(f.EquipmentAvailabilityPct); _equipCap.Text = PlanUi.Num(f.EquipMonthlyCapacityWanM3); _ytdCoal.Text = PlanUi.Num(f.YtdActualCoalWanT); _ytdStrip.Text = PlanUi.Num(f.YtdActualStripWanM3);
        // 开窗只绑、不重派：重派是会动数的动作，得由人点。
        BindMonthly();
    }

    private void SaveToBase()
    {
        var s = _base;
        s.PlanYear = (int)Math.Max(0, PlanUi.D(_planYear));
        s.AnnualCoalTargetWanT = PlanUi.D(_annualCoal); s.AnnualStripTargetWanM3 = PlanUi.D(_annualStrip);
        s.StartMonth = (int)Math.Clamp(PlanUi.D(_startMonth), 1, 12); s.MonthCount = (int)Math.Clamp(PlanUi.D(_monthCount), 1, 24);
        s.Balance.OutputSmooth = PlanUi.D(_balOut); s.Balance.RatioSmooth = PlanUi.D(_balRatio); s.Balance.EquipSmooth = PlanUi.D(_balEquip);
        s.MonthlyCoalCeilingWanT = PlanUi.D(_ceilCoal); s.MonthlyStripCeilingWanM3 = PlanUi.D(_ceilStrip); s.RatioCeiling = PlanUi.D(_ratioCeil);
        s.CompletionTolerancePct = PlanUi.D(_tol); s.MinPreparedMonths = PlanUi.D(_minPrep); s.BenchHeightM = PlanUi.D(_benchH); s.WorkLineLenM = PlanUi.D(_workLine);
        s.Decision.Completion = PlanUi.D(_dwComp); s.Decision.OutputBalance = PlanUi.D(_dwBal); s.Decision.EquipUtil = PlanUi.D(_dwUtil); s.Decision.PeakShaving = PlanUi.D(_dwPeak); s.Decision.AdvanceAttain = PlanUi.D(_dwAdv);
        var f = s.Field;
        f.StandardWorkdays = PlanUi.D(_workdays); f.ShiftsPerDay = (int)Math.Max(1, PlanUi.D(_shifts)); f.WinterMonthsCsv = _winterMonths.Text?.Trim() ?? "";
        f.WinterDeratePct = PlanUi.D(_winterDerate); f.MaintenanceMonth = (int)Math.Clamp(PlanUi.D(_maintMonth), 0, 12); f.MaintenanceDeratePct = PlanUi.D(_maintDerate);
        f.EquipmentCount = (int)Math.Max(1, PlanUi.D(_equipCount)); f.EquipmentAvailabilityPct = PlanUi.D(_avail); f.EquipMonthlyCapacityWanM3 = PlanUi.D(_equipCap);
        f.YtdActualCoalWanT = PlanUi.D(_ytdCoal); f.YtdActualStripWanM3 = PlanUi.D(_ytdStrip);
    }

    // ── ③b 逐月配置表 ──

    /// <summary>「按现场参数重新派生」/「按当前参数派生」共用一个入口 —— 两个按钮，一条口径。</summary>
    private void OnRefreshFieldPreview()
    {
        SaveToBase();
        UpdateBaseRatio();
        var t = MonthlyTargetStore.Rebuild();
        BindMonthly();
        _status.Text = $"已按当前基础约束重新派生逐月配置表：{t.Rows.Count} 个月" + (t.ManualRowCount > 0 ? $"，保住了 {t.ManualRowCount} 行人工覆盖（只更新了它们的派生值）" : "") + "。";
    }

    /// <summary>把当前逐月配置表铺到表格 + 汇总行 + 对账行 + 引擎决定清单。<b>不重新派生</b>。</summary>
    private void BindMonthly()
    {
        var t = MonthlyTargetStore.Current;
        _monthlyGrid.ItemsSource = null; _monthlyGrid.ItemsSource = t.Rows;
        _monthlyCaption.Text = t.Caption;
        var f = ShortTermSchemeStore.Base.Field;
        double totalWd = t.Rows.Sum(r => r.Workdays);
        _fieldPreview.Text =
            $"逐月有效作业日合计 {totalWd:0.0} 天 · 设备可用 {f.EquipmentCount}台×{f.EquipmentAvailabilityPct:0}% · 满月作业能力≈{f.EquipmentCount * f.EquipMonthlyCapacityWanM3 * f.EquipmentAvailabilityPct / 100:0}万m³"
          + $"（季节降效月 {f.WinterMonthsCsv}，检修月 {(f.MaintenanceMonth == 0 ? "无" : f.MaintenanceMonth + "月")}） —— 逐月明细见下面那张表，作业日那一列可以直接改。";
        var rec = t.Reconcile();
        string skew = t.MatchesBasis(ShortTermSchemeStore.Base, out string why) ? "" : "\n" + why;
        _monthlyReconcile.Text = rec.Report() + skew;
        if (rec.Ok && skew.Length == 0) RoadUi.Theme(_monthlyReconcile, TextBlock.ForegroundProperty, "Theme.Text.Secondary");
        else _monthlyReconcile.Foreground = Brushes.OrangeRed;
        _monthlyNotes.Children.Clear();
        foreach (var n in t.Notes) _monthlyNotes.Children.Add(RoadUi.Hint(n, 11));
    }

    /// <summary>把选中行重置为派生值 —— <b>唯一会冲掉人工值的动作</b>，所以说清楚冲掉了哪几列。</summary>
    private void OnResetSelectedMonthly()
    {
        var sel = _monthlyGrid.SelectedItems.OfType<MonthlyTargetRow>().ToList();
        if (sel.Count == 0) { _status.Text = "先在逐月表里选中要重置的行（可多选）。"; return; }
        var hit = sel.Where(r => r.IsManual).Select(r => $"{r.PeriodKey}[{r.OverriddenText}]").ToList();
        int n = MonthlyTargetStore.Current.ResetToDerived(sel);
        BindMonthly();
        _status.Text = n == 0 ? $"选中的 {sel.Count} 行本来就没有人工覆盖，没什么可重置的。" : $"已把 {n} 行重置为派生值：{string.Join("、", hit)}。";
    }

    private async Task OnResetAllMonthlyAsync()
    {
        var t = MonthlyTargetStore.Current;
        int manual = t.ManualRowCount;
        if (manual == 0) { _status.Text = "整张表都没有人工覆盖，没什么可重置的。"; return; }
        if (!await CoalMsgBox.ConfirmAsync(this, "全部重置为派生值", $"要把全部 {manual} 行的人工覆盖清掉、恢复成引擎派生值吗？\n\n这一步不可撤销。")) return;
        int n = t.ResetToDerived();
        BindMonthly();
        _status.Text = $"已把 {n} 行重置为派生值。";
    }

    private void OnSaveMonthly()
    {
        var t = MonthlyTargetStore.Current;
        if (!MonthlyTargetStore.Save(t, out string path, out string err)) { _status.Text = err; return; }
        var rec = t.Reconcile();
        _status.Text = $"逐月配置表已保存（{t.Rows.Count} 行，其中 {t.ManualRowCount} 行人工覆盖）→ {path}" + (rec.Ok ? "" : "\n" + rec.Report());
    }

    private void OnLoadMonthly()
    {
        if (!MonthlyTargetStore.TryLoad(out var loaded, out var issues)) { _status.Text = string.Join("\n", issues); return; }
        // 读回来的是覆盖，派生值仍以当前基础约束为准。
        var t = MonthlyTargetStore.Rebuild();
        int applied = t.ApplyOverrides(loaded, out var more);
        BindMonthly();
        _status.Text = $"已读回逐月配置表：落进 {applied} 行人工覆盖（派生值按**当前**基础约束重算，不用文件里那份）。"
                     + (issues.Count > 0 ? "\n" + string.Join("\n", issues) : "") + (more.Count > 0 ? "\n" + string.Join("\n", more) : "");
    }

    private void UpdateBaseRatio()
    {
        double coal = PlanUi.D(_annualCoal);
        double r = coal > 0 ? PlanUi.D(_annualStrip) / coal : 0;
        _baseRatioText.Text = $"基准剥采比 = {PlanUi.D(_annualStrip):N0} ÷ {coal:N0} = {r:0.00} m³/t";
    }

    private void OnInheritSource()
    {
        if (_sourceLtCombo.SelectedItem is not LongTermPlan lt) { _status.Text = "（继承）请先在下拉里选一个中长远进度计划方案——或直接填年采剥目标"; return; }
        int? year = int.TryParse(_planYear.Text, out var y) && y > 0 ? y : null;
        _base.InheritFrom(lt, year);
        LoadFromBase();
        _status.Text = $"已继承「{lt.Name}」{_base.PlanYear} 年 → 年采出 {_base.AnnualCoalTargetWanT:N0} 万t · 年剥离 {_base.AnnualStripTargetWanM3:N0} 万m³（剥采比 {_base.BaseRatio:0.0}）";
    }

    private void OnNormalizeBalance()
    {
        double sum = PlanUi.D(_balOut) + PlanUi.D(_balRatio) + PlanUi.D(_balEquip);
        if (sum <= 0) { _status.Text = "均衡权重和为 0，无法归一"; return; }
        _balOut.Text = PlanUi.Num(PlanUi.D(_balOut) / sum); _balRatio.Text = PlanUi.Num(PlanUi.D(_balRatio) / sum); _balEquip.Text = PlanUi.Num(PlanUi.D(_balEquip) / sum);
        _balSum.Text = (PlanUi.D(_balOut) + PlanUi.D(_balRatio) + PlanUi.D(_balEquip)).ToString("0.00");
    }

    private void OnNormalizeDecision()
    {
        double sum = PlanUi.D(_dwComp) + PlanUi.D(_dwBal) + PlanUi.D(_dwUtil) + PlanUi.D(_dwPeak) + PlanUi.D(_dwAdv);
        if (sum <= 0) { _status.Text = "比选权重和为 0，无法归一"; return; }
        _dwComp.Text = PlanUi.Num(PlanUi.D(_dwComp) / sum); _dwBal.Text = PlanUi.Num(PlanUi.D(_dwBal) / sum); _dwUtil.Text = PlanUi.Num(PlanUi.D(_dwUtil) / sum);
        _dwPeak.Text = PlanUi.Num(PlanUi.D(_dwPeak) / sum); _dwAdv.Text = PlanUi.Num(PlanUi.D(_dwAdv) / sum);
        _dwSum.Text = (PlanUi.D(_dwComp) + PlanUi.D(_dwBal) + PlanUi.D(_dwUtil) + PlanUi.D(_dwPeak) + PlanUi.D(_dwAdv)).ToString("0.00");
    }

    /// <summary>用默认组织（均衡型·标准工作历）试排一遍，校验约束是否产出合理计划（不存为方案）。</summary>
    private void OnPreview()
    {
        SaveToBase();
        UpdateBaseRatio();
        var probe = _base.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "（试算）");
        ShortTermScheduler.Schedule(probe);
        var r = probe.Result;
        BindMonthly();
        var overrideNotes = r?.Warnings?.Where(w => w.Contains("逐月配置表覆盖")).ToList() ?? new List<string>();
        var summaryNote = r?.Warnings?.FirstOrDefault(w => w.Contains("来自逐月配置表的人工覆盖"));
        _status.Text = (r == null ? "试算失败" :
            $"试算(均衡型·标准)：年采出 {r.TotalCoalWanT:N0}万t · 完成率 {r.CompletionRatePct:0.0}% · 峰值月 {r.PeakMonthLabel}({r.PeakMonthCoalWanT:0.0}万t) · 设备利用 {r.AvgEquipUtilPct:0}% · 备采保有 {r.PreparedMonths:0.0}月 · {r.OkText} → 去「派生计划方案」造多套")
            + "\n" + (overrideNotes.Count > 0
                ? $"注意：试算**已经按逐月配置表里的人工覆盖排过**（覆盖了 {overrideNotes.Count} 个月）——它不是纯粹的年目标摊分。" + (summaryNote != null ? "　" + summaryNote.TrimStart('·', ' ') : "")
                : "注意：逐月配置表里**没有人工覆盖**，本次试算就是年目标按月权重摊分的结果。")
            + (r != null && r.PrepState == PrepCheckState.NotEntered ? "\n◆ 备采储量没有来源 ⇒ 上面那个「备采保有 0.0 月」和「备采保有下限」这道闸**本次不参与判定**；备采储量由「量驱动采剥接续」几何算出。" : "");
    }

    private void OnSaveBase()
    {
        SaveToBase();
        UpdateBaseRatio();
        BindMonthly();
        var t = MonthlyTargetStore.Current;
        bool sync = t.MatchesBasis(_base, out string why);
        bool wrote = ShortTermBaseStore.Save(_base, out string path, out string err);
        _status.Text = $"已保存基础约束（{_base.PlanYear}年 · 年采出 {_base.AnnualCoalTargetWanT:N0}万t · 月上限 {_base.MonthlyCoalCeilingWanT:0}万t · {_base.SourceText}）"
                     + (wrote ? $"　✔ 已写入 {path}（**下次开机仍在**）" : $"　◆ **没能落盘**：{err} —— 这份约束只活在本次会话里，关掉软件就回到默认样例。")
                     + " → 去「派生计划方案」按作业组织×工作历造候选" + (sync ? "" : "\n" + why);
    }

    /// <summary>自检直通：试算并返回状态栏文本。</summary>
    internal string SelftestPreview() { OnPreview(); return _status.Text ?? ""; }
}
