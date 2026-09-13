using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Plan;        // EquipmentFleetProvider / EquipmentAssignReport / MonthlyTargetStore
using PitMine3D.Kylin.Cad.Units;       // EquipmentAssigner / UnitAssignment / MineUnit
using PitMine3D.Kylin.Views.Road;
using CalendarScenario = PitMine3D.Kylin.Cad.Plan.CalendarScenario;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「设备指派」面板（移植原 <c>PlanLib.Views.EquipmentAssignPanel</c>）—— 排产<b>之后</b>把单元的量摊到具体设备的具体工日上。
/// <para>本面板<b>不改 <c>UnitPlanEngine</c> 的签名</b>，只吃它吐出来的 <c>Assignments</c>。</para>
/// <para><b>三个口径必须在界面上看得见</b>：台效是哪一级来的 / 整类没有实测的那几类 / 作业日·班次·年月是哪来的。</para>
/// </summary>
internal sealed class EquipmentAssignPanel : Expander
{
    // ── 输入（都带缺省，且缺省是哪来的必须写进报告）──
    private readonly TextBox _ym = Tb(78, ""), _workdays = Tb(52, ""), _shifts = Tb(44, ""), _scale = Tb(52, "1.0");
    private readonly CheckBox _auto = Chk("排产后自动指派", true);
    private readonly ComboBox _role;

    // ── 作业组织口径（决定同时开工几台、转场吃掉几个工日）──
    private readonly TextBox _maxLoaders = Tb(40, PlanCase.MaxLoadersPerUnit.ToString()), _freeReloc = Tb(52, PlanCase.FreeRelocationM.ToString("0")),
                             _relocTracked = Tb(40, PlanCase.RelocationDaysTracked.ToString()), _relocWheeled = Tb(40, PlanCase.RelocationDaysWheeled.ToString());
    private readonly ComboBox _truckSizing;

    /// <summary>本月工序量汇总（穿爆采运排合计）—— 「导出工序量」按钮用。</summary>
    private MonthlyProcessSummary? _proc;
    /// <summary>上一次归属解算结果 —— 「归属覆盖…」按钮拿它列待定单元。</summary>
    private FaceUnitResolution? _faceRes;

    // ── 穿爆 / 排土两个工序的开关（缺省【关】）──
    private readonly CheckBox _doDrill = Chk("排穿孔(钻机)", false), _doDoze = Chk("排排土(推土机)", false);
    private readonly TextBox _blastLead = Tb(40, "2");

    private readonly DataGrid _cov = new();       // 台效来源覆盖（逐类别）
    private readonly DataGrid _grid = new();      // 逐笔指派
    private readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, FontFamily = RoadUi.Mono, FontSize = 11.5, LineHeight = 16, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _head = new() { FontWeight = FontWeight.SemiBold };

    private readonly ObservableCollection<AssignRow> _rows = new();
    private readonly ObservableCollection<RateCoverageRow> _covRows = new();

    // ── 上一次排产交过来的东西（「指派设备」按钮要能自己重跑）──
    private List<UnitAssignment> _plan = new();
    private List<MineUnit> _units = new();
    private string _period = "";
    private EquipmentAssignResult? _last;

    /// <summary>排产成功后要不要顺手指派一次。</summary>
    public bool AutoRun => _auto.IsChecked == true;
    /// <summary>最近一次指派结果（没跑过 = null）。判据/导出用。</summary>
    public EquipmentAssignResult? Last => _last;

    private static TextBox Tb(double w, string t) => new() { Width = w, Height = 24, Text = t, MinHeight = 0, Padding = new Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private static CheckBox Chk(string t, bool on) => new() { Content = new TextBlock { Text = t }, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private static TextBlock Lab(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.85 };

    /// <summary>一笔指派 = 表格一行。<b>全是属性</b>。</summary>
    public sealed class AssignRow
    {
        public string UnitId { get; init; } = "";
        public string UnitKindText { get; init; } = "";
        public int Seq { get; init; }
        public string MachineId { get; init; } = "";
        public string Model { get; init; } = "";
        public string MachineKindText { get; init; } = "";
        public string RoleText { get; init; } = "";
        public string Serves { get; init; } = "";
        public int StartDay { get; init; }
        public int EndDay { get; init; }
        public int Days { get; init; }
        public double Shifts { get; init; }
        /// <summary>本笔量（万m³）。<b>运输笔是承运量，不计入采出量</b>。</summary>
        public double WanM3 { get; init; }
        /// <summary>这一笔的量是<b>哪个体积口径</b>：原位实方 / 控制方量 / 排弃占容。</summary>
        public string BasisText { get; init; } = "";
        public double RateM3PerDay { get; init; }
        public string RateSourceText { get; init; } = "";
        public string MeasuredText { get; init; } = "";
        public string RateRecord { get; init; } = "";
        public string LimitText { get; init; } = "";
        /// <summary>本段没用掉的铲能力（万m³）。<b>负数 = 派的量超了台效</b>。</summary>
        public double SpareWanM3 { get; init; }
        public string DayText => $"{StartDay}~{EndDay}";
    }

    public EquipmentAssignPanel()
    {
        IsExpanded = false;
        Margin = new Thickness(0, 8, 0, 0);
        _head.Text = "③ 设备指派 —— " + EquipmentAssignReport.Headline(null);
        Header = _head;

        var body = new StackPanel { Margin = new Thickness(4, 8, 4, 4) };

        // ── 输入条 ──
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(Lab("年月(yyyy-MM):"));
        ToolTip.SetTip(_ym, "台效按【这一年这个月】挑记录。留空则从当前期次解析；期次解析不出来时必须在这里填，否则台效会静默退到「各月中位数」——那和真的命中了本月，排出来的班表长得一模一样。");
        _ym.TextChanged += (_, _) => { if (!_ymWriting) _ymAuto = false; };   // 人一动这个框，它就不再是"我们按期次填的"了
        bar.Children.Add(_ym);
        bar.Children.Add(Lab("作业日:"));
        ToolTip.SetTip(_workdays, "留空 = 优先取【逐月配置表】同期次那一行，没有就取【现场参数】的工作历；填了就以填的为准（会在报告里注明是手填）。");
        bar.Children.Add(_workdays);
        bar.Children.Add(Lab("班次/日:"));
        ToolTip.SetTip(_shifts, "留空 = 取【现场参数】的每日班次。台班数 = 工日 × 它。");
        bar.Children.Add(_shifts);
        bar.Children.Add(Lab("台效缩放:"));
        ToolTip.SetTip(_scale, "缺省 1.0 = 照单全收。capacity_monthly 的量级现场需要认一次（见报告里的「量级体检」）；要改就改这一个数，别在别处再乘一遍。");
        bar.Children.Add(_scale);
        bar.Children.Add(_auto);
        Btn(bar, "指派设备", () => RunAndShow(manual: true), 88);
        Btn(bar, "导出CSV…", () => _ = ExportCsvAsync(), 88);
        Btn(bar, "导出工序量…", () => _ = ExportProcessCsvAsync(), 96);
        Btn(bar, "班组作业推演…", OpenShiftDuty, 104);
        bar.Children.Add(Lab("只看:"));
        _role = PlanUi.Combo(new[] { "全部", "挖装", "运输", "转场", "穿孔", "排土" }, 0, 84); _role.Height = 24; _role.MinHeight = 0; _role.Margin = new Thickness(0, 0, 10, 0);
        _role.SelectionChanged += (_, _) => ApplyRoleFilter();
        bar.Children.Add(_role);
        body.Children.Add(bar);

        // ── 作业组织口径 ──
        var bar2 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        bar2.Children.Add(Lab("每单元最多铲数:"));
        ToolTip.SetTip(_maxLoaders, "一个采掘单元本月最多摆几台挖装设备。>1 才可能出现【同一个体上多台并行】——三维动态模拟里「几台铲在动」直接由它决定。");
        bar2.Children.Add(_maxLoaders);
        bar2.Children.Add(Lab("转场免费距离(m):"));
        ToolTip.SetTip(_freeReloc, "两个单元质心近于它就当没挪窝，不占工日。");
        bar2.Children.Add(_freeReloc);
        bar2.Children.Add(Lab("转场工日 履带/轮式:"));
        ToolTip.SetTip(_relocTracked, "履带·步行机械（电铲/钻机/推土）转场占几个工日。");
        bar2.Children.Add(_relocTracked);
        ToolTip.SetTip(_relocWheeled, "轮式机械（前装机）转场占几个工日。0 = 当天就能到位。");
        bar2.Children.Add(_relocWheeled);
        bar2.Children.Add(Lab("配车口径:"));
        _truckSizing = PlanUi.Combo(new[] { "按编组规则", "按台效配平" }, 0, 132); _truckSizing.Height = 24; _truckSizing.MinHeight = 0; _truckSizing.Margin = new Thickness(0, 0, 10, 0);
        ToolTip.SetTip(_truckSizing, "按编组规则 = 用 dispatch_rule 台账（一级来源）；查不到时才退到「一铲配 N 车」的兜底。");
        bar2.Children.Add(_truckSizing);
        body.Children.Add(bar2);

        // ── 工序开关：穿爆 / 排土 ──
        var bar3 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(_doDrill, "排钻机。打开后【需爆破的岩单元必须先穿完 + 过超前期才能开挖】——全月的量与欠产会整体变一遍，所以缺省关。免爆单元由作业面的物料自动判（表土/风化层不穿）。");
        bar3.Children.Add(_doDrill);
        ToolTip.SetTip(_doDoze, "排推土机。量走【排弃占容】(V实×Kr)，与排土场库容同一本账，**不进采出量**。");
        bar3.Children.Add(_doDoze);
        bar3.Children.Add(Lab("穿爆超前(工日):"));
        ToolTip.SetTip(_blastLead, "穿完到能采装之间隔几个工日（起爆+清场+撤警戒）。作业面工艺里填了面级超前期的，以面级为准；这里是全局兜底。");
        bar3.Children.Add(_blastLead);
        Btn(bar3, "归属覆盖…", () => _ = OpenAttributionAsync(), 92);
        bar3.Children.Add(new TextBlock { Text = "· 面上钉的设备型号与工艺经「确定开采程序」摊到单元（归属匹配率见下方溯源）", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        body.Children.Add(bar3);

        // ── 台效来源覆盖（逐类别）──
        body.Children.Add(new TextBlock
        {
            Text = "台效来源覆盖 —— 对【在册全部设备】逐台探一次台效账本（同一本账、同一套回退链）。「本次排了」是从结果里数出来的：0 说明这一类本次一台都没进排班。",
            FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4),
        });
        BuildCoverageGrid();
        body.Children.Add(Wrap(_cov, 132));

        // ── 逐笔指派 ──
        body.Children.Add(new TextBlock
        {
            Text = "逐笔指派（计划，非实绩 —— 不得回写 capacity_monthly / equipment_kpi_monthly）。运输笔的量是它承运的那一份，不计入采出量。",
            FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4),
        });
        BuildAssignGrid();
        body.Children.Add(Wrap(_grid, 220));

        body.Children.Add(_msg);
        RoadUi.Theme(_msg, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        _msg.Text = "还没指派。先在上面「按目标排产」——指派吃的是排产的结果。";

        Content = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, MaxHeight = 380, Content = body };   // 原版 460：Avalonia 行高更高，展开后会把上面的单元表挤没
    }

    private static Border Wrap(Control child, double h)
    {
        child.Height = h;
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(1), Child = child };
        RoadUi.Theme(b, Border.BackgroundProperty, "Theme.Panel.Background");
        RoadUi.Theme(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        return b;
    }

    private void Btn(Panel host, string text, Action act, double w)
    {
        var b = new Button { Content = text, MinWidth = w, Height = 24, MinHeight = 0, Padding = new Thickness(8, 0), Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        // 面板里抛出来的异常落到本面板的文字区，不许冒到顶层把窗口带走
        b.Click += (_, _) => { try { act(); } catch (Exception ex) { _msg.Text = "出错：" + ex.Message; } };
        host.Children.Add(b);
    }

    // ══ 表 ════════════════════════════════════════════════════════

    private static DataGridTextColumn C(string h, string path, double w, string? fmt = null)
    {
        var b = new Binding(path) { Mode = BindingMode.OneWay }; if (fmt != null) b.StringFormat = fmt;
        return new DataGridTextColumn { Header = h, Width = new DataGridLength(w), IsReadOnly = true, Binding = b };
    }

    private void BuildCoverageGrid()
    {
        _cov.AutoGenerateColumns = false; _cov.IsReadOnly = true; _cov.HeadersVisibility = DataGridHeadersVisibility.Column; _cov.RowHeight = 24;
        _cov.BorderThickness = new Thickness(0); _cov.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; _cov.FontSize = 12;
        _cov.Columns.Add(C("类别", nameof(RateCoverageRow.KindText), 62));
        _cov.Columns.Add(C("在册", nameof(RateCoverageRow.OnRoll), 46));
        _cov.Columns.Add(C("可派", nameof(RateCoverageRow.Dispatchable), 46));
        _cov.Columns.Add(C("本次排了", nameof(RateCoverageRow.UsedText), 62));
        _cov.Columns.Add(C("自有实测", nameof(RateCoverageRow.OwnMeasured), 62));
        _cov.Columns.Add(C("解析·实测级", nameof(RateCoverageRow.ResolvedMeasured), 76));
        _cov.Columns.Add(C("解析·缺省级", nameof(RateCoverageRow.ResolvedDefault), 76));
        _cov.Columns.Add(C("解不出", nameof(RateCoverageRow.Unresolved), 54));
        _cov.Columns.Add(C("逐级明细", nameof(RateCoverageRow.LevelText), 300));
        // 结论列：整类没实测 / 有台解不出 —— 一眼看见（琥珀色）
        var vc = new DataGridTemplateColumn
        {
            Header = "结论", Width = new DataGridLength(460), IsReadOnly = true,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RateCoverageRow>((_, _) =>
            {
                var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), Padding = new Thickness(4, 0) };
                t.Bind(TextBlock.TextProperty, new Binding(nameof(RateCoverageRow.Verdict)));
                return t;
            }),
        };
        _cov.Columns.Add(vc);
        PlanUi.FitHeaders(_cov);
        _cov.ItemsSource = _covRows;
    }

    private void BuildAssignGrid()
    {
        _grid.AutoGenerateColumns = false; _grid.IsReadOnly = true; _grid.HeadersVisibility = DataGridHeadersVisibility.Column; _grid.RowHeight = 24;
        _grid.BorderThickness = new Thickness(0); _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; _grid.FrozenColumnCount = 2; _grid.FontSize = 12;
        _grid.Columns.Add(C("单元号", nameof(AssignRow.UnitId), 128));
        _grid.Columns.Add(C("设备", nameof(AssignRow.MachineId), 74));
        _grid.Columns.Add(C("型号", nameof(AssignRow.Model), 92));
        _grid.Columns.Add(C("类别", nameof(AssignRow.MachineKindText), 52));
        _grid.Columns.Add(C("角色", nameof(AssignRow.RoleText), 48));
        _grid.Columns.Add(C("服务于", nameof(AssignRow.Serves), 66));
        _grid.Columns.Add(C("工日", nameof(AssignRow.DayText), 62));
        _grid.Columns.Add(C("天", nameof(AssignRow.Days), 38));
        _grid.Columns.Add(C("台班", nameof(AssignRow.Shifts), 46, "{0:0.#}"));
        _grid.Columns.Add(C("量万m³", nameof(AssignRow.WanM3), 62, "{0:0.000}"));
        _grid.Columns.Add(C("量口径", nameof(AssignRow.BasisText), 72));       // ★ 紧挨着量列
        _grid.Columns.Add(C("台效m³/日", nameof(AssignRow.RateM3PerDay), 74, "{0:0}"));
        _grid.Columns.Add(C("台效来源", nameof(AssignRow.RateSourceText), 118));
        _grid.Columns.Add(C("实测?", nameof(AssignRow.MeasuredText), 48));
        _grid.Columns.Add(C("余能万m³", nameof(AssignRow.SpareWanM3), 68, "{0:0.000}"));
        _grid.Columns.Add(C("受限", nameof(AssignRow.LimitText), 44));
        _grid.Columns.Add(C("台效溯源", nameof(AssignRow.RateRecord), 420));
        PlanUi.FitHeaders(_grid);
        ApplyRoleFilter();
    }

    private void ApplyRoleFilter()
    {
        string want = _role?.SelectedItem as string ?? "全部";
        _grid.ItemsSource = want == "全部" ? _rows.ToList() : _rows.Where(r => r.RoleText == want).ToList();
    }

    // ══ 对外：排产成功之后调一次 ═══════════════════════════════════

    /// <summary>排产成功后调。<paramref name="plan"/> = <c>UnitPlanResult.Assignments</c>，<paramref name="units"/> = 这一次排产用的采掘单元。</summary>
    /// <returns>给调用方状态区拼在排产报告后面的一段。<b>不自动指派时返回空串</b>。</returns>
    public string OnScheduled(IReadOnlyList<UnitAssignment>? plan, IReadOnlyList<MineUnit>? units, string period)
    {
        _plan = (plan ?? Array.Empty<UnitAssignment>()).Where(a => a != null).ToList();
        _units = (units ?? Array.Empty<MineUnit>()).Where(u => u != null).ToList();
        _period = (period ?? "").Trim();
        SyncYmFromPeriod();

        if (!AutoRun)
        {
            _msg.Text = "已收到排产结果（" + _plan.Count + " 个单元）。「排产后自动指派」没勾 —— 点「指派设备」再算。";
            _head.Text = "③ 设备指派 —— 排产已就绪，未指派（勾上自动，或点「指派设备」）";
            return "";
        }
        return RunAndShow(manual: false);
    }

    private bool _ymAuto, _ymWriting;

    /// <summary>期次能解析成年月时，把它填进年月框。<b>人手填过的绝不覆盖</b>；<b>我们自己填的要跟着期次走</b>。</summary>
    private void SyncYmFromPeriod()
    {
        if ((_ym.Text ?? "").Trim().Length > 0 && !_ymAuto) return;
        if (!TryParseYm(_period, out int y, out int m)) return;
        _ymWriting = true; _ym.Text = $"{y:0000}-{m:00}"; _ymWriting = false; _ymAuto = true;
    }

    // ══ 跑一次 ════════════════════════════════════════════════════

    private string RunAndShow(bool manual)
    {
        _rows.Clear(); _covRows.Clear(); _last = null;

        if (_plan.Count == 0)
        {
            _msg.Text = "还没有排产结果 —— 先点上面的「按目标排产」。指派吃的是排产吐出来的单元指派。";
            _head.Text = "③ 设备指派 —— " + EquipmentAssignReport.Headline(null);
            return manual ? _msg.Text : "";
        }

        // ── ① 年月：解析不出来就停，不猜 ──
        var prov = new List<string>();
        string ymText = (_ym.Text ?? "").Trim();
        int year, month;
        if (ymText.Length > 0)
        {
            if (!TryParseYm(ymText, out year, out month))
                return Fail($"年月「{ymText}」看不懂 —— 请按 2025-06 这样填。台效是按年月挑记录的，这里猜一个就是编数。");
            prov.Add(TryParseYm(_period, out int py, out int pm) && py == year && pm == month
                ? $"年月 {year}-{month:00}（与期次「{_period}」一致）"
                : $"年月 {year}-{month:00}（本栏手填" + (_period.Length > 0 ? $"，期次是「{_period}」" : "") + "）");
        }
        else if (TryParseYm(_period, out year, out month))
            prov.Add($"年月 {year}-{month:00}（从期次「{_period}」解析）");
        else
            return Fail($"期次「{_period}」解析不出年月，年月栏也是空的 —— 台效要按【这一年这个月】挑记录。请把期次写成 2025-06 这样，或直接在「年月」栏里填。（不填就只能退到「各月中位数」，那和真的命中本月排出来的班表长得一模一样，事后分不出。）");

        // ── ② 作业日：优先逐月配置表的真行，其次现场参数的工作历，手填最优先 ──
        double rawWd; string wdFrom;
        string wdText = (_workdays.Text ?? "").Trim();
        if (wdText.Length > 0)
        {
            if (!double.TryParse(wdText, NumberStyles.Float, CultureInfo.InvariantCulture, out rawWd) || !(rawWd > 0)) return Fail($"作业日「{wdText}」不是正数。");
            wdFrom = "本栏手填";
        }
        else
        {
            MonthlyTargetRow? row = null;
            try { var t = MonthlyTargetStore.Current; row = t?.Find(_period); if (row == null) row = t?.Find($"{year:0000}-{month:00}"); }
            catch { row = null; }      // 逐月配置表取不到不该把指派整条弄死

            if (row != null && row.Workdays > 0) { rawWd = row.Workdays; wdFrom = $"【逐月配置表】{row.PeriodKey} 行（{row.SourceText}）"; }
            else
            {
                var f = ShortTermSchemeStore.Base.Field;
                rawWd = f.WorkdaysFor(month, CalendarScenario.Standard);
                wdFrom = "【现场参数】的标准工作历（含季节/检修降效）" + (row == null ? $" —— 逐月配置表里没有 {year:0000}-{month:00} 这一行" : " —— 那一行的作业日不是正数");
            }
            if (!(rawWd > 0)) return Fail($"算出来的作业日是 {rawWd:0.##} —— 请在「作业日」栏里直接填一个正数。");
            _workdays.Text = rawWd.ToString("0.#", CultureInfo.InvariantCulture);   // 让人看得见用的是哪个数
        }
        int wd = (int)Math.Round(rawWd, MidpointRounding.AwayFromZero);
        if (wd < 1) wd = 1;
        prov.Add($"作业日 {wd} 天，取自 {wdFrom}" + (Math.Abs(wd - rawWd) > 1e-9 ? $"（原值 {rawWd:0.#} 天，已取整 —— 排班的粒度是整天）" : ""));

        // ── ③ 班次 ──
        double shifts; string shFrom;
        string shText = (_shifts.Text ?? "").Trim();
        if (shText.Length > 0)
        {
            if (!double.TryParse(shText, NumberStyles.Float, CultureInfo.InvariantCulture, out shifts) || !(shifts > 0)) return Fail($"班次「{shText}」不是正数。");
            shFrom = "本栏手填";
        }
        else
        {
            shifts = ShortTermSchemeStore.Base.Field.ShiftsPerDay;
            shFrom = "【现场参数】的每日班次";
            if (!(shifts > 0)) return Fail("现场参数里的每日班次不是正数 —— 请在「班次/日」栏里填一个。");
            _shifts.Text = shifts.ToString("0.#", CultureInfo.InvariantCulture);
        }
        prov.Add($"每日 {shifts:0.#} 班（{shFrom}）· 台班数 = 工日 × 它");

        // ── ④ 台效缩放 ──
        double scale = 1.0;
        string scText = (_scale.Text ?? "").Trim();
        if (scText.Length > 0 && (!double.TryParse(scText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || !(scale > 0)))
            return Fail($"台效缩放「{scText}」不是正数。缺省 1.0 = 照单全收。");
        if (scText.Length == 0) scale = 1.0;

        // ── ⑤ 取设备维 ──
        FleetResolution fleet;
        try { fleet = EquipmentFleetProvider.Load(year, month, wd); }
        catch (Exception ex) { return Fail("设备维取数出错：" + ex.Message); }

        var inp = EquipmentFleetProvider.ToInput(fleet, _plan, EquipmentFleetProvider.SitesFrom(_units), year, month, wd, shifts);
        inp.CapacityScale = scale;

        // ── 作业组织口径：填了就用填的，填不出数就保持引擎缺省 ──
        if (int.TryParse((_maxLoaders.Text ?? "").Trim(), out int ml) && ml > 0) inp.MaxLoadersPerUnit = ml;
        if (double.TryParse((_freeReloc.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fr) && fr >= 0) inp.FreeRelocationM = fr;
        if (int.TryParse((_relocTracked.Text ?? "").Trim(), out int rt) && rt >= 0) inp.RelocationDaysTracked = rt;
        if (int.TryParse((_relocWheeled.Text ?? "").Trim(), out int rw) && rw >= 0) inp.RelocationDaysWheeled = rw;
        inp.TruckSizing = _truckSizing.SelectedIndex == 1 ? TruckSizing.ByRateBalance : TruckSizing.ByDispatchRule;

        // ── ⑤b 把「确定开采程序」的面级配置接进来（归属解算 → 型号约束 → 免爆单元清单 → 开工序 走同一份归属）──
        int lead = int.TryParse((_blastLead.Text ?? "").Trim(), out int bl) && bl >= 0 ? bl : 2;
        FaceUnitResolution fu;
        try
        {
            fu = EquipmentFleetProvider.ApplyMiningProgram(inp, fleet, _units, ShortTermSchemeStore.Base.Faces,
                drilling: _doDrill.IsChecked == true, dozing: _doDoze.IsChecked == true, blastLeadDays: lead,
                manualFaceOf: ShortTermSchemeStore.Base.FaceAttribution, drillsPerUnit: 1, dozersPerSink: 2);
        }
        catch (Exception ex)
        {
            fu = new FaceUnitResolution();
            prov.Add($"◆ 面级配置没接上（{ex.GetType().Name}：{ex.Message}）—— 本次排产**不受任何面级约束**，设备在全矿在册里挑。");
        }
        _faceRes = fu;
        if (fu.UnitCount > 0)
            prov.Add("面级配置：" + fu.Summary() + (fu.MatchedCount < fu.UnitCount ? "　◆ 没配上的单元不受面级约束（设备全矿挑、穿爆按「需爆破」算）" : ""));

        // ── ⑤c 月度工序量汇总（任务编制真正要吃的那张表）──
        try
        {
            _proc = MonthlyProcessRollup.Build(_plan, ShortTermSchemeStore.Base.Faces, fu.UnitToFace, year, month, PlanEquipModelCatalog.BenchHeightOf);
            prov.Add(_proc.Summary());
            foreach (var n in _proc.Notes.Where(n => n.StartsWith("◆"))) prov.Add(n);
        }
        catch (Exception ex) { _proc = null; prov.Add($"◆ 月度工序量汇总没做成（{ex.GetType().Name}）—— 指派本身不受影响。"); }

        // ── ⑥ 指派 ──
        EquipmentAssignResult res;
        try { res = EquipmentAssigner.Assign(inp); }
        catch (Exception ex) { return Fail("指派出错：" + ex.Message); }
        _last = res;

        // ── ⑥a 设定能力 vs 实测台效：并排报一次 ──
        try
        {
            var trow = MonthlyTargetStore.Current?.Find($"{year:0000}-{month:00}");
            double setWan = trow?.StripCapWanM3 ?? 0;
            double measWan = fleet.Rates.Where(x => x.Measured && (x.Kind == MachineKind.Shovel || x.Kind == MachineKind.Loader)).Sum(x => x.M3PerDay) * wd / 1e4;
            if (setWan > 1e-9 && measWan > 1e-9)
            {
                double diff = (measWan - setWan) / setWan * 100;
                prov.Add($"能力对账：设定 {setWan:0.#} 万m³（月度计划编制设的，**以它为准**） vs 实测台效口径 {measWan:0.#} 万m³（{fleet.WithMeasuredRate} 台有实测）　差 {diff:+0.#;-0.#;0}%"
                       + (Math.Abs(diff) > 30 ? "　◆ 差得太多 —— 要么设定值是拍的，要么台效口径不对（先看「量级体检」）。" : ""));
            }
            else if (setWan > 1e-9)
                prov.Add($"能力对账：设定 {setWan:0.#} 万m³；**实测台效一台都没有**，这一次没法校核 —— 班表是按下一级来源（缺省/中位数）算的，不是这个矿的真台效。");
            else
                prov.Add("能力对账：逐月配置表这一期没设【剥离能力】（留空 = 不卡）—— 无从校核。要卡就去「月度计划编制 → 逐月配置表」填。");
        }
        catch (Exception ex) { prov.Add($"◆ 能力对账没做成（{ex.GetType().Name}）—— 指派本身不受影响。"); }

        // ── ⑥b 把这次指派摆上三维舞台 ──
        string stageNote = "";
        try { stageNote = EquipStageBridge.Stage(res, year, month); }
        catch (Exception ex) { stageNote = $"◆ 三维设备符号摆位异常（{ex.GetType().Name}）—— 指派本身不受影响。"; }
        if (stageNote.Length > 0) prov.Add(stageNote.TrimStart('·', ' '));

        // ── ⑦ 覆盖表（不管指派成没成功都要显示）──
        var cov = EquipmentAssignReport.Coverage(fleet, inp, res.Success ? res : null);
        foreach (var c in cov) _covRows.Add(c);

        if (res.Success)
        {
            foreach (var a in res.Assignments)
                _rows.Add(new AssignRow
                {
                    UnitId = a.UnitId, UnitKindText = a.UnitKind == UnitKind.Coal ? "煤" : "岩", Seq = a.Seq,
                    MachineId = a.MachineId, Model = a.Model, MachineKindText = EquipmentAssignReport.KindName(a.MachineKind),
                    RoleText = EquipmentAssignReport.RoleName(a.Role), Serves = a.ServesMachineId,
                    StartDay = a.StartDay, EndDay = a.EndDay, Days = a.Days, Shifts = a.Shifts,
                    WanM3 = a.AssignedM3 / 1e4,
                    BasisText = a.Role switch { MachineRole.Drill => "控制方量", MachineRole.Dump => "排弃占容", MachineRole.Relocate => "—", _ => "原位实方" },
                    RateM3PerDay = a.RateM3PerDay,
                    RateSourceText = a.Role == MachineRole.Relocate ? "—（转场笔无台效）" : RateBook.Label(a.RateSource),
                    MeasuredText = a.Role == MachineRole.Relocate ? "—" : (a.IsMeasuredRate ? "实测" : "缺省"),
                    RateRecord = a.RateRecord,
                    SpareWanM3 = a.Role is MachineRole.Excavate or MachineRole.Drill or MachineRole.Dump ? a.SpareCapM3 / 1e4 : 0,
                    LimitText = a.HaulLimited ? "运力" : "",
                });
        }
        ApplyRoleFilter();

        string text = EquipmentAssignReport.Compose(res, fleet, inp, cov, prov);
        _msg.Text = text;
        _head.Text = "③ 设备指派 —— " + EquipmentAssignReport.Headline(res, cov);
        if (!res.Feasible || cov.Any(c => c.NoMeasuredAtAll)) IsExpanded = true;
        return text;
    }

    private string Fail(string why)
    {
        _msg.Text = "◆ 没指派：" + why;
        _head.Text = "③ 设备指派 —— ◆ 没指派（点开看原因）";
        IsExpanded = true;
        return _msg.Text;
    }

    private static bool TryParseYm(string? s, out int year, out int month) => EquipmentAssignReport.TryParseYm(s, out year, out month);

    // ══ 导出 ══════════════════════════════════════════════════════

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async Task ExportCsvAsync()
    {
        if (_last == null || !_last.Success) { _msg.Text = "还没有可导出的指派结果。"; return; }
        var sp = Owner?.StorageProvider; if (sp == null) return;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出设备指派（计划）", SuggestedFileName = (_period.Length > 0 ? _period : "设备指派") + "_设备指派计划.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        try
        {
            File.WriteAllText(file.Path.LocalPath, EquipmentAssigner.ToCsv(_last), new System.Text.UTF8Encoding(true));
            _msg.Text = "已导出：" + file.Path.LocalPath + "\n（表头带「计划」两个字 —— 这份表进不了实绩台账，不得回写 capacity_monthly / equipment_kpi_monthly。）";
        }
        catch (Exception ex) { _msg.Text = "导出失败：" + ex.Message; }
    }

    /// <summary>排产结果 → <b>班组作业推演</b>：每台设备每一班干什么。推的是「照这份排产，班上该干多少」，不是实绩。</summary>
    private void OpenShiftDuty()
    {
        if (_last == null || !_last.Success) { _msg.Text = "还没有排产结果 —— 先「指派设备」跑一次。班组作业是从排产结果推出来的，没有排产就没有班表。"; return; }

        ShiftInferenceResult res;
        try { res = ShiftInference.Infer(_last, _period); }
        catch (Exception ex) { _msg.Text = "班组推演失败：" + ex.Message; return; }

        if (!res.Ok)
        {
            _msg.Text = "推不出班组作业：" + (res.Headline.Length > 0 ? res.Headline : "（没有给出原因）") + (res.Notes.Count > 0 ? "\n· " + string.Join("\n· ", res.Notes) : "");
            return;
        }

        var w = new ShiftDutyWindow(res, _period);
        if (Owner != null) w.Show(Owner); else w.Show();
        GeoDb.GeoDbWindows.NoteLast(w);
        _msg.Text = res.Headline;
    }

    /// <summary>打开归属覆盖 —— 把解算器拒绝配的那些单元交给人来定（FA1）。改完<b>就地重跑一次指派</b>。</summary>
    private async Task OpenAttributionAsync()
    {
        if (_faceRes == null) { _msg.Text = "还没解算过归属 —— 先「指派设备」跑一次。"; return; }
        if (!_faceRes.PendingIds.Any()) { _msg.Text = $"没有待定的单元 —— 归属解算这一轮全配上了（{_faceRes.Summary()}）。"; return; }

        var dlg = new FaceAttributionDialog(_faceRes, _units, ShortTermSchemeStore.Base.Faces, ShortTermSchemeStore.Base.FaceAttribution);
        bool ok = Owner != null ? await dlg.ShowDialog<bool>(Owner) : false;
        if (!ok) { _msg.Text = "归属覆盖未改动。"; return; }

        int n = ShortTermSchemeStore.Base.FaceAttribution.Count;
        _msg.Text = $"归属覆盖已更新（共 {n} 条人工指定）—— 正在按新归属重跑指派…";
        RunAndShow(manual: true);
    }

    /// <summary>导出月度工序量（穿爆采运排合计）—— 任务编制 / 火工品计划直接吃这张表。</summary>
    private async Task ExportProcessCsvAsync()
    {
        if (_proc == null || _proc.Faces.Count == 0) { _msg.Text = "还没有可导出的工序量 —— 先「指派设备」跑一次（它顺带算工序量）。"; return; }
        var sp = Owner?.StorageProvider; if (sp == null) return;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出月度工序量（计划）", SuggestedFileName = (_period.Length > 0 ? _period : "月度") + "_工序量_穿爆采运排.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;
        try
        {
            File.WriteAllText(file.Path.LocalPath, _proc.ToCsv(), new System.Text.UTF8Encoding(true));
            _msg.Text = "已导出：" + file.Path.LocalPath + "\n" + _proc.Summary() + "\n（表里「免爆」与「◆ 算不出」的延米都是 0，靠「状态」列区分 —— 别只看数字。）";
        }
        catch (Exception ex) { _msg.Text = "导出失败：" + ex.Message; }
    }

    internal string SelftestMessage => _msg.Text ?? "";
    internal string SelftestHead => _head.Text ?? "";
    internal int SelftestRows => _rows.Count;
    internal int SelftestCovRows => _covRows.Count;
    internal void SelftestRun() => RunAndShow(manual: true);
}
