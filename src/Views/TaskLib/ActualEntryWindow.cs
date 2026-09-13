// 忠实移植自原 PitMine3D Modules/TaskLib/Features/ActualEntryWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF grid.Items.Refresh() → 重设 ItemsSource；表头 ToolTip 挂在 TaskUi.Head 上。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 实绩录入：班末把「实际干了多少」灌回台账并落盘。
///
/// <para>本窗口从桩变真的三处：</para>
/// <list type="number">
/// <item><b>保存是真保存</b>：写 <see cref="ActualRecord"/> 到 %LOCALAPPDATA%/PitMine/actuals/，
///   同时回灌内存里的 <see cref="ProductionTask"/>（达成度评价、甘特、报表消费的是同一份对象）。</item>
/// <item><b>排弃类去向按占容方扣库容</b>：<c>SinkRegistryLoader.AddFilled(去向, ResolvedMix.ToDumpM3(实方))</c>。
///   传的是【占容方 V容 = V实×Kr】，不是实方也不是松方——排土场吃的是沉降稳定后的体积。
///   而且只补【增量】：盘子装配时样例实绩已经扣过一次，再按全量扣就是重复记账。</item>
/// <item><b>故障工时不再硬编码 0.8</b>：由「设备状态·故障报修」写下的 <see cref="FaultEvent"/>
///   按本任务时段求重叠小时数汇总而来。没有故障记录就是空白，不编一个数出来。</item>
/// <item><b>未完成原因录的是原因码</b>：原先是一列自由文本，<see cref="IncompleteReason"/> 永远只能是
///   盘子装配时带进来的那批——现场真正知道"为啥没干完"的人填的字，一个也进不了下游。
///   现在按顿号分隔解析回枚举并<b>写回任务本体</b>，达成度评价的归因、动态调整的策略路由才吃得到；
///   认不出来的词点名报出来，不静默丢。备注另立一列，<see cref="Fill"/> 不再把它盖掉。</item>
/// </list>
/// <para>
/// 自动回灌皮带秤/汽车衡/卡调系统仍未接通——按钮改成「载入已存实绩」（真读盘），
/// 不做假装成功的"自动回灌"。
/// </para>
/// </summary>
public sealed class ActualEntryWindow : Window
{
    public sealed class Row
    {
        public string Equip { get; set; } = "";
        public string Shift { get; set; } = "";
        public string Process { get; set; } = "";
        public string Zone { get; set; } = "";
        public string Destination { get; set; } = "";
        /// <summary>量的单位：采装/排土 m³实方，穿孔 m 延米。</summary>
        public string Unit { get; set; } = "";
        /// <summary>本行是不是穿孔（量走延米、无去向、无煤质）。</summary>
        public bool IsDrill { get; init; }
        public string PlanM3 { get; set; } = "";
        public string ActualM3 { get; set; } = "";
        public string Hours { get; set; } = "";
        public string FaultHours { get; set; } = "";
        public string Trucks { get; set; } = "";
        public string Ash { get; set; } = "";
        /// <summary>实测热值 MJ/kg。</summary>
        public string Cv { get; set; } = "";
        /// <summary>实测硫分 %。</summary>
        public string Sulfur { get; set; } = "";
        public string Attain { get; set; } = "";

        /// <summary>未完成原因码（顿号分隔的标签串，存盘前解析回 <see cref="IncompleteReason"/>）。</summary>
        public string ReasonText { get; set; } = "";
        /// <summary>自由备注。<b>Fill 不许碰它</b>——它不是任务上的派生量，是人写的字。</summary>
        public string Remark { get; set; } = "";

        public ProductionTask Task { get; init; } = new();
        public string Key { get; init; } = "";
        /// <summary>本窗口打开（或上次保存）时台账里的实绩量——扣库容只补这之后的增量。</summary>
        public double Baseline { get; set; }
    }

    private readonly string _date = SampleTaskBoard.DateLabel;
    private readonly List<Row> _all = new();
    private List<FaultEvent> _faults = new();

    private readonly TextBlock subTitle;
    private readonly TextBox entryBox = new() { Width = 100, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox shiftCombo = new() { Width = 92, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid grid = TaskUi.Grid(readOnly: false);
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private bool _loaded;

    public ActualEntryWindow()
    {
        Title = "实绩录入 — 日常生产组织";
        TaskUi.Place(this, 1420, 640);

        var header = TaskUi.Header("实绩录入", "班末录入班产/工时/到位车数/实测煤质 → 落盘 + 回灌任务台账 + 按占容方扣排土库容");
        subTitle = (TextBlock)((StackPanel)header.Child!).Children[1];

        var tool = new DockPanel { LastChildFill = false };
        void L(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Left); tool.Children.Add(c); }
        L(TaskUi.Btn("保存实绩", OnSave, 94, bold: true));
        L(TaskUi.Btn("载入已存实绩", OnLoadSaved, 112));
        L(TaskUi.Btn("重算故障工时", OnRecalcFault, 112));
        // DispatchEngine.OnActualDeviation 的界面入口。那个触发器一直写在引擎里、界面上点不到
        var dev = TaskUi.Btn("偏差处置建议", OnDeviationAdvice, 112); dev.Margin = new Thickness(0, 0, 14, 0);
        ToolTip.SetTip(dev, "对达成度明显偏低/偏高的任务跑一次滚动重排，给出顺延·补车·调减·换面等建议。只算建议，不改计划。");
        L(dev);
        L(Lbl("录入人")); entryBox.Text = Environment.UserName; L(entryBox);
        L(Lbl("班次"));
        // 取值域按当日班制由 ShiftSelector 填、默认落当前班（班末录的是本班的实绩）。「全部」保留：次日复核要一次看全天。
        L(shiftCombo);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        L(toolStatus);

        // 列宽口径：写死像素的列一律 Auto + MinWidth=原值 —— Auto 取「表头与各行里最宽的那个」，永远裁不着内容；
        // 星号列补 MinWidth；合计宽度超出窗口时由 DataGrid 自己横向滚动，不再靠压扁列来凑。
        grid.Columns.Add(Auto("设备", nameof(Row.Equip), 70, true));
        grid.Columns.Add(Auto("班次", nameof(Row.Shift), 56, true));
        grid.Columns.Add(Auto("工序", nameof(Row.Process), 50, true));
        grid.Columns.Add(Star("作业面", nameof(Row.Zone), 1.3, 150, true));
        grid.Columns.Add(Star("去向", nameof(Row.Destination), 1.3, 150, true));
        // 量列对三种工序通用：采装/排土是 m³ 实方，穿孔是延米 m。单位单列，值列只放数
        grid.Columns.Add(Auto("单位", nameof(Row.Unit), 46, true));
        grid.Columns.Add(Auto("计划量", nameof(Row.PlanM3), 72, true));
        grid.Columns.Add(Auto("实绩量", nameof(Row.ActualM3), 76, false));
        grid.Columns.Add(Auto("工时h", nameof(Row.Hours), 60, false));
        // 故障工时不再手填：由「设备状态·故障报修」录入的 FaultEvent 按本任务时段汇总
        grid.Columns.Add(Auto("故障h", nameof(Row.FaultHours), 60, true));
        grid.Columns.Add(Auto("到位车", nameof(Row.Trucks), 58, false));
        // 三项配齐：质量标准定的就是 灰/热/硫，只录灰分的话热值不够、硫超标的煤全算"达标"
        grid.Columns.Add(Auto("灰分%", nameof(Row.Ash), 62, false));
        grid.Columns.Add(Auto("热值MJ/kg", nameof(Row.Cv), 80, false));
        grid.Columns.Add(Auto("硫分%", nameof(Row.Sulfur), 62, false));
        grid.Columns.Add(Auto("达成%", nameof(Row.Attain), 64, true));
        // 未完成原因：录的是**原因码**，不是自由文本。顿号分隔可多选；合法取值见表头悬停（由枚举生成，不会与代码走散）。
        var reasonCol = Star("未完成原因", nameof(Row.ReasonText), 1.6, 120, false);
        ToolTip.SetTip((TextBlock)reasonCol.Header!, "顿号分隔可多选。合法取值：" + Environment.NewLine
                             + string.Join("、", TaskEnumLabels.AllReasonLabels) + Environment.NewLine
                             + "认不出来的词会在下方明着报出来，不会被静默丢掉。");
        grid.Columns.Add(reasonCol);
        grid.Columns.Add(Star("备注", nameof(Row.Remark), 1, 90, false));

        TaskUi.Theme(feedback, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(new ScrollViewer { Content = feedback, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, top: false, padY: 6);
        foot.MaxHeight = 120;

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(tool, top: true);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(grid, 2); Grid.SetRow(foot, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(grid); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        shiftCombo.SelectionChanged += (_, _) => { if (_loaded) ApplyFilter(); };
        Opened += (_, _) =>
        {
            // 班末录的是**本班**的实绩，默认就该落当前班（原先「全部」）；「全部」留给次日复核
            ShiftSelector.Bind(shiftCombo, includeAll: true);
            _loaded = true;
            Build();
            ApplyFilter();
        };
    }

    private static TextBlock Lbl(string t)
    {
        var l = TaskUi.Lbl(t); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
        return l;
    }

    private static DataGridTextColumn Auto(string header, string path, double minWidth, bool readOnly)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = minWidth, IsReadOnly = readOnly };

    private static DataGridTextColumn Star(string header, string path, double star, double minWidth, bool readOnly)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay }, Width = new DataGridLength(star, DataGridLengthUnitType.Star), MinWidth = minWidth, IsReadOnly = readOnly };

    private string ShiftFilter => ShiftSelector.Filter(shiftCombo);
    private string EnteredBy => string.IsNullOrWhiteSpace(entryBox.Text) ? Environment.UserName : entryBox.Text!.Trim();

    private void CommitEdits()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);   // 先提交正在编辑的单元格，否则读到的是旧值
    }

    // ── 装载 ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        _all.Clear();
        _faults = TaskPersistence.LoadFaults(_date);

        // ★ 穿孔也要能录实绩。原先只收 Load/Dump，于是穿孔任务永远进不了 Done，
        //   「工序进度跟踪」按 Status==Done 判的穿孔进度就恒为 0% —— 一条谁也走不通的死路。
        foreach (var t in SampleTaskBoard.Day()
                     // 已撤回的任务不列出来 —— 让人给一条撤回的活填实绩，填完还进不了任何口径，
                     // 是这套表单最容易白干的一种。口径见 DispatchStateLink.IsLive。
                     .Where(DispatchStateLink.IsLive)
                     .Where(t => t.Process is ProcessType.Load or ProcessType.Dump or ProcessType.Drill)
                     .OrderBy(t => t.Shift).ThenBy(t => t.StartHour))
        {
            bool drill = t.Process == ProcessType.Drill;
            var r = new Row
            {
                Task = t,
                Key = TaskKey.Of(t, _date),
                IsDrill = drill,
                Equip = t.Group.MainEquipment,
                Shift = t.Shift,
                Process = t.Process.Label(),
                Zone = t.WorkZone,
                Destination = drill ? "—" : (t.HasDestination ? t.DestinationCaption : "—"),
                Unit = drill ? "m 延米" : "m³实方",
                PlanM3 = drill
                    ? (t.Drill?.PlanMeters is > 0 ? $"{t.Drill.PlanMeters:0.#}" : "—")
                    : $"{t.TargetVolumeM3:0}",
                Baseline = t.ActualVolumeM3,
            };
            Fill(r);
            _all.Add(r);
        }

        subTitle.Text = $"{_date} · 实绩落盘 {TaskPersistence.ActualDir()} · 故障工时取自故障记录（{_faults.Count} 条）";
    }

    /// <summary>
    /// 把任务上的值刷到行（保存后/载入后复用）。
    /// <para>
    /// ★ 只刷<b>任务上的派生量</b>。<see cref="Row.Remark"/> 不在其列：它是人写的字，
    /// 任务对象上根本没有对应字段。原先这里连「异常说明」一起重写成 <c>Reasons</c> 的标签串，
    /// 于是保存后紧接着 <c>Fill</c> 一走、载入后 <c>r.Note = rec.Note</c> 再被 <c>Fill</c> 盖掉，
    /// 录进去的说明在界面上一次也留不住。
    /// </para>
    /// </summary>
    private void Fill(Row r)
    {
        var t = r.Task;
        r.ActualM3 = r.IsDrill
            ? (t.Drill?.ActualMeters is > 0 ? $"{t.Drill.ActualMeters:0.#}" : "")
            : (t.ActualVolumeM3 > 0 ? $"{t.ActualVolumeM3:0}" : "");
        r.Hours = t.ActualHours > 0 ? $"{t.ActualHours:0.#}" : "";
        r.Trucks = t.TrucksOnSite > 0 ? $"{t.TrucksOnSite}" : "";
        r.Ash = t.QualityActual is { AshPct: > 0 } ? $"{t.QualityActual.AshPct:0.#}" : "";
        r.Cv = t.QualityActual is { CalorificMJkg: > 0 } ? $"{t.QualityActual.CalorificMJkg:0.##}" : "";
        r.Sulfur = t.QualityActual is { SulfurPct: > 0 } ? $"{t.QualityActual.SulfurPct:0.###}" : "";
        double fh = FaultHoursOf(t);
        r.FaultHours = fh > 1e-6 ? $"{fh:0.#}" : "";
        r.Attain = r.IsDrill
            ? (t.Drill?.AttainmentPct is { } dp ? $"{dp:0}" : "")
            : (t.TargetVolumeM3 > 1e-6 && t.ActualVolumeM3 > 0 ? $"{t.AttainmentPct:0}" : "");
        // 原因码是任务上的量，回刷即归一化（"Fault" → "设备故障"，重复项 ParseReasons 已去过重）
        r.ReasonText = string.Join("、", t.Reasons.Select(x => x.Label()));
    }

    /// <summary>
    /// 本任务时段内该设备的<b>非计划故障</b>工时 h = Σ 故障记录与 [起,止) 的重叠。
    /// 计划检修走 <see cref="MaintHoursOf"/>——两者分账，混在一起会把按计划保养记成设备不行。
    /// </summary>
    private double FaultHoursOf(ProductionTask t) => StopHours(t, planned: false);

    /// <summary>本任务时段内的<b>计划检修</b>停机 h。</summary>
    private double MaintHoursOf(ProductionTask t) => StopHours(t, planned: true);

    private double StopHours(ProductionTask t, bool planned)
        => _faults.Where(f => f.IsPlanned == planned
                           && string.Equals(f.EquipId, t.Group.MainEquipment, StringComparison.OrdinalIgnoreCase))
                  .Sum(f => f.OverlapHours(t.StartHour, t.EndHour));

    private void ApplyFilter()
    {
        var view = _all.Where(r => ShiftFilter.Length == 0 || r.Shift == ShiftFilter).ToList();
        grid.ItemsSource = null;
        grid.ItemsSource = view;

        int done = view.Count(r => !string.IsNullOrWhiteSpace(r.ActualM3));
        int withReason = view.Count(r => r.ReasonText.Length > 0);
        toolStatus.Text = $"共 {view.Count} 条（设备×班）· 已有实绩 {done} 条 · 已填原因 {withReason} 条 · "
                        + "白底列为录入项，故障h 由故障记录汇总";
    }

    // ── 保存 ─────────────────────────────────────────────────────────────────

    private void OnSave()
    {
        CommitEdits();

        var rows = _all.Where(r => ShiftFilter.Length == 0 || r.Shift == ShiftFilter).ToList();
        var records = new List<ActualRecord>();
        var filled = new List<string>();
        var bad = new List<string>();
        string by = EnteredBy;

        foreach (var r in rows)
        {
            var t = r.Task;

            // ── 解析录入值（解析不了就保留原值并记一条，不静默吞掉）──
            if (!TryNum(r.ActualM3, out double act)) { bad.Add($"{t.Id} 实绩m³「{r.ActualM3}」不是数字"); act = t.ActualVolumeM3; }
            if (!TryNum(r.Hours, out double hrs)) { bad.Add($"{t.Id} 工时「{r.Hours}」不是数字"); hrs = t.ActualHours; }
            if (!TryNum(r.Trucks, out double trk)) { bad.Add($"{t.Id} 到位车「{r.Trucks}」不是数字"); trk = t.TrucksOnSite; }
            bool hasAsh = TryNum(r.Ash, out double ash) && ash > 0;
            bool hasCv = TryNum(r.Cv, out double cv) && cv > 0;
            bool hasS = TryNum(r.Sulfur, out double sul) && sul > 0;

            // 原因码：认不出的词点名报出来，认出来的**写回任务本体**——
            // 达成度评价的归因与动态调整的策略路由读的就是 t.Reasons，不回写等于没录。
            var reasons = TaskEnumLabels.ParseReasons(r.ReasonText, out var unknown);
            if (unknown.Count > 0)
                bad.Add($"{t.Id} 未完成原因「{string.Join("、", unknown)}」不是合法原因码（合法取值见表头悬停），本条未计入");

            // ── 回灌任务台账（甘特/达成度/报表消费的是同一份内存对象）──
            if (r.IsDrill)
            {
                // 穿孔的量是延米，不是 m³ —— 写进 Drill 里，别污染 ActualVolumeM3
                //（那个字段被剥采比、库容、运输功一路当实方用）
                t.Drill ??= new DrillQuantity();
                if (act > 0) t.Drill.ActualMeters = act;
                t.ActualHours = Math.Max(0, hrs);
                // 完成判定走延米达成率；没有计划延米时，录了工时就算干过（保守：不置 Done）
                t.Status = t.Drill.AttainmentPct is { } dp
                    ? (dp >= 98 ? TaskStatus.Done : dp > 0 ? TaskStatus.Partial : t.Status)
                    : t.Status;
            }
            else
            {
                t.ActualVolumeM3 = Math.Max(0, act);
                t.ActualHours = Math.Max(0, hrs);
            }
            t.TrucksOnSite = (int)Math.Max(0, Math.Round(trk));
            t.Reasons.Clear();
            t.Reasons.AddRange(reasons);
            // 三项各自独立：只测了灰分就只回灌灰分，别把没测的项写成 0（0 热值会让配煤判定全线不达标）
            if (hasAsh || hasCv || hasS)
            {
                t.QualityActual ??= new CoalQuality();
                if (hasAsh) t.QualityActual.AshPct = ash;
                if (hasCv) t.QualityActual.CalorificMJkg = cv;
                if (hasS) t.QualityActual.SulfurPct = sul;
            }
            // 穿孔的状态上面按延米判过了，这里别用 ActualVolumeM3（它对穿孔恒为 0）把它盖回去
            if (!r.IsDrill)
                t.Status = t.ActualVolumeM3 <= 1e-6 ? t.Status
                         : t.AttainmentPct >= 98 ? TaskStatus.Done
                         : TaskStatus.Partial;

            double faultH = FaultHoursOf(t);
            var mix = t.ResolvedMix;

            // ── 排弃类去向按【占容方】扣库容，且只补增量 ──
            //    盘子装配时已按样例实绩扣过一次；这里再按全量扣就是把同一批料记两遍。
            double delta = t.ActualVolumeM3 - r.Baseline;
            if (t.Process == ProcessType.Load && t.DestinationKind.IsDumping()
                && !string.IsNullOrWhiteSpace(t.DestinationId) && Math.Abs(delta) > 1e-6)
            {
                try
                {
                    double dumpM3 = mix.ToDumpM3(delta);              // ★ V容 = V实 × Kr
                    double rate = SinkRegistryLoader.AddFilled(t.DestinationId, dumpM3);
                    filled.Add($"{t.DestinationName}{(dumpM3 >= 0 ? "+" : "")}{dumpM3:0} m³占容（充填 {rate * 100:0.#}%）");
                }
                catch (Exception ex) { bad.Add($"{t.DestinationName} 库容回灌失败：{ex.Message}"); }
            }
            r.Baseline = t.ActualVolumeM3;

            records.Add(new ActualRecord
            {
                TaskId = t.Id,
                StableKey = r.Key,
                PlanDate = _date,
                Shift = t.Shift,
                EquipId = t.Group.MainEquipment,
                WorkZone = t.WorkZone,
                EngineeringPositionId = t.EngineeringPositionId ?? "",   // 与三维几何的连接键，存盘即定
                Process = t.Process,
                MaterialCode = string.IsNullOrWhiteSpace(t.MaterialCode) ? mix.PrimaryCode : t.MaterialCode,
                DestinationId = t.DestinationId,
                DestinationName = t.DestinationName,
                DestinationKind = t.DestinationKind,
                // 运距与计划工时存盘即定：路网逐期在变，拿今天的路网解上个月那趟车是另一件事
                EffectiveHaulKm = Math.Round(t.EffectiveHaulKm, 4),
                PlanHours = Math.Round(t.PlannedHours, 2),
                PlanDrillMeters = t.Drill?.PlanMeters,
                ActualDrillMeters = t.Drill?.ActualMeters,
                PlanVolumeM3 = Math.Round(t.TargetVolumeM3, 1),
                ActualVolumeM3 = Math.Round(t.ActualVolumeM3, 1),
                ActualTonnageT = Math.Round(t.ActualTonnageT, 1),          // 吨量：三口径间唯一守恒量，存盘即定
                ActualDumpM3 = t.DestinationKind.IsDumping() ? Math.Round(mix.ToDumpM3(t.ActualVolumeM3), 1) : 0,
                ActualHours = Math.Round(t.ActualHours, 2),
                FaultHours = Math.Round(faultH, 2),
                MaintenanceHours = Math.Round(MaintHoursOf(t), 2),
                TrucksOnSite = t.TrucksOnSite,
                AshPct = hasAsh ? ash : null,
                CalorificMJkg = hasCv ? cv : null,
                SulfurPct = hasS ? sul : null,
                Reasons = new List<IncompleteReason>(reasons),
                Note = r.Remark ?? "",
                EnteredBy = by,
            });

            Fill(r);
        }

        // ── 落盘（按班次分文件）──
        bool ok = true;
        foreach (var g in records.GroupBy(x => string.IsNullOrWhiteSpace(x.Shift) ? "全天" : x.Shift))
            ok &= TaskPersistence.AppendActuals(_date, g.Key, g.ToList());

        ApplyFilter();

        // ── 即时反馈：达成度 + 欠产 + 库容回灌 ──
        double plan = records.Sum(x => x.PlanVolumeM3), actual = records.Sum(x => x.ActualVolumeM3);
        double att = plan > 1e-6 ? actual / plan * 100 : 0;
        var short_ = records.Where(x => x.PlanVolumeM3 > 1 && x.AttainmentPct < 95)
                            .OrderBy(x => x.AttainmentPct).Take(3)
                            .Select(x => $"{x.EquipId}·{x.WorkZone} {x.AttainmentPct:0}%").ToList();

        var lines = new List<string>
        {
            (ok ? "✓ 已保存" : "⚠ 保存失败") + $" {records.Count} 条实绩（录入人 {by}）→ {TaskPersistence.ActualDir()}"
              + (ok ? "" : "：" + TaskPersistence.LastIoLabel),
            $"综合达成度 {att:0.#}%（实绩 {actual:N0} / 计划 {plan:N0} m³实方 · {records.Sum(x => x.ActualTonnageT) / 1e4:0.###} 万t）"
              + (short_.Count > 0 ? $"；欠产靠前：{string.Join("、", short_)}" : "；无明显欠产"),
        };
        // 原因码分布：这批数正是达成度归因与动态调整策略路由的输入，存完就报一遍，便于当场核对
        var reasonHits = records.SelectMany(x => x.Reasons)
                                .GroupBy(x => x)
                                .OrderByDescending(g => g.Count())
                                .Select(g => $"{g.Key.Label()} {g.Count()} 条").ToList();
        lines.Add(reasonHits.Count > 0
            ? "未完成原因：" + string.Join("、", reasonHits) + "（已写入任务，达成度归因与动态调整按它路由）"
            : "未完成原因：本批未录（欠产任务不填原因，达成度评价的归因栏就是空的，动态调整也无从选策略）");

        lines.Add(TripReconcile(records.Sum(x => x.ActualVolumeM3)));

        if (filled.Count > 0) lines.Add("排土库容已按占容方回灌：" + string.Join("；", filled));
        double sumFault = records.Sum(x => x.FaultHours), sumMaint = records.Sum(x => x.MaintenanceHours);
        if (sumFault > 1e-6 || sumMaint > 1e-6)
            lines.Add($"停机合计：非计划故障 {sumFault:0.#} h · 计划检修 {sumMaint:0.#} h"
                    + "（均取自故障记录，非手填；两者分账——检修不该记成设备不行）");
        else lines.Add("本日暂无故障记录 —— 故障 h 留空；如确有故障，请在「设备状态·故障报修」录入后回到此处「重算故障工时」。");
        if (bad.Count > 0) lines.Add("⚠ " + string.Join("；", bad));

        feedback.Text = string.Join(Environment.NewLine, lines);
        toolStatus.Text = ok ? $"已保存 {records.Count} 条 · 达成 {att:0.#}%" : "保存失败，见下方说明";
    }

    // ── 载入 / 重算 ──────────────────────────────────────────────────────────

    /// <summary>从落盘的实绩回读（换机/次日复核用）。按稳定键对号，重排过也对得上。</summary>
    private void OnLoadSaved()
    {
        var saved = TaskPersistence.LoadActualsOfDay(_date);
        if (saved.Count == 0)
        {
            feedback.Text = $"{TaskPersistence.ActualDir()} 下暂无本日实绩记录。"
                          + Environment.NewLine + "（皮带秤 / 汽车衡 / 卡调系统的自动采集尚未接通，当前实绩以人工录入为准。）";
            return;
        }

        var map = saved.GroupBy(x => x.StableKey, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EnteredAt).First(), StringComparer.OrdinalIgnoreCase);

        int hit = 0;
        foreach (var r in _all)
        {
            if (!map.TryGetValue(r.Key, out var rec)) continue;
            if (rec.ActualDrillMeters is > 0 || rec.PlanDrillMeters is > 0)
            {
                r.Task.Drill ??= new DrillQuantity();
                if (rec.PlanDrillMeters is > 0) r.Task.Drill.PlanMeters = rec.PlanDrillMeters;
                if (rec.ActualDrillMeters is > 0) r.Task.Drill.ActualMeters = rec.ActualDrillMeters;
            }
            r.Task.ActualVolumeM3 = rec.ActualVolumeM3;
            r.Task.ActualHours = rec.ActualHours;
            r.Task.TrucksOnSite = rec.TrucksOnSite;
            if (rec.AshPct is > 0 || rec.CalorificMJkg is > 0 || rec.SulfurPct is > 0)
            {
                r.Task.QualityActual ??= new CoalQuality();
                if (rec.AshPct is > 0) r.Task.QualityActual.AshPct = rec.AshPct.Value;
                if (rec.CalorificMJkg is > 0) r.Task.QualityActual.CalorificMJkg = rec.CalorificMJkg.Value;
                if (rec.SulfurPct is > 0) r.Task.QualityActual.SulfurPct = rec.SulfurPct.Value;
            }
            r.Baseline = rec.ActualVolumeM3;   // 已落盘的量视为已扣过库容，后续只补增量
            // 原因码回到任务本体（下游按它路由），备注回到行上；
            // Fill 只重刷任务上的派生量，不会再把这两样盖掉。
            r.Task.Reasons.Clear();
            r.Task.Reasons.AddRange(rec.Reasons ?? new List<IncompleteReason>());
            r.Remark = rec.Note ?? "";
            Fill(r);
            hit++;
        }

        ApplyFilter();

        // 谁录的：交接班/次日复核要能问到人。EnteredBy 原先写了零读。
        string who = string.Join("、", saved.Where(x => !string.IsNullOrWhiteSpace(x.EnteredBy))
                                            .GroupBy(x => x.EnteredBy)
                                            .OrderByDescending(g => g.Count())
                                            .Select(g => $"{g.Key} {g.Count()} 条"));

        feedback.Text = $"已载入落盘实绩 {saved.Count} 条，按稳定键对上 {hit} 条（未对上的多半是重排后新生成的任务）。"
                      + (who.Length > 0 ? Environment.NewLine + "录入人：" + who + "。" : "")
                      + Environment.NewLine + TripReconcile(_all.Sum(r => r.Task.ActualVolumeM3));
    }

    /// <summary>
    /// 与派车单对账：本班已卸车次的**实方**合计 vs 这里录的实绩 m³。
    /// <para>
    /// 两个口径本来就该互相印证——一个是逐趟数出来的，一个是班末填进去的。原先派车单侧
    /// 根本没有实绩（车次状态机是死的），实绩侧也没有车次数，两边谁也对不上谁。
    /// 差得多不一定是错（漏点标记、过磅口径差），但**差多少必须看得见**。
    /// </para>
    /// </summary>
    private string TripReconcile(double actualM3)
    {
        List<DispatchOrder> orders;
        try { orders = TaskPersistence.LoadOrdersOfDay(_date); }
        catch { return "派车单对账：读不出本日派车单。"; }

        string shift = ShiftFilter;
        var mine = orders.Where(o => shift.Length == 0 || o.Shift == shift).ToList();
        if (mine.Count == 0) return "派车单对账：本班尚无派车单（先在「派车单」展开并保存）。";

        var dumped = mine.Where(o => o.Status == DispatchOrderStatus.Dumped).ToList();
        if (dumped.Count == 0)
            return $"派车单对账：本班 {mine.Count} 趟，**一趟都没标记卸载** —— "
                 + "车次实绩没回填，这一侧对不了账（去「派车单」标记装/卸）。";

        double byTrip = dumped.Sum(o => o.PayloadInSituM3);
        double diff = actualM3 - byTrip;
        string verdict = Math.Abs(byTrip) < 1e-6 ? ""
            : $"，相差 {diff:+#,##0;-#,##0;0} m³（{(Math.Abs(diff) / Math.Max(1, byTrip) * 100):0.#}%）";

        return $"派车单对账：本班已卸 {dumped.Count}/{mine.Count} 趟 · 车次口径 {byTrip:N0} m³实方 "
             + $"vs 录入 {actualM3:N0} m³实方{verdict}。";
    }

    /// <summary>
    /// 偏差处置建议 —— <see cref="DispatchEngine.OnActualDeviation"/> 的界面入口。
    /// <para>
    /// 那个触发器一直写在引擎里却<b>界面上点不到</b>（三个触发器只有 OnFault 有入口）：
    /// 班末录完实绩，"这个面欠了 30% 怎么办"没有任何地方能问。
    /// 这里对偏差最大的几条各跑一次，把建议摊开；只算不改，执行仍在「生产任务动态调整」。
    /// </para>
    /// </summary>
    private void OnDeviationAdvice()
    {
        CommitEdits();

        var bad = _all
            .Where(r => (ShiftFilter.Length == 0 || r.Shift == ShiftFilter)
                        && r.Task.TargetVolumeM3 > 1 && r.Task.ActualVolumeM3 > 0
                        && (r.Task.AttainmentPct < 90 || r.Task.AttainmentPct > 115))
            .OrderBy(r => r.Task.AttainmentPct)
            .Take(3)
            .ToList();

        if (bad.Count == 0)
        {
            feedback.Text = "偏差处置：本班没有达成度低于 90% 或高于 115% 的任务 —— 没什么要处置的。"
                          + "（还没录实绩的任务不算偏差，先录再点。）";
            return;
        }

        var lines = new List<string>();
        try
        {
            var cfg = SampleTaskBoard.Config();
            var tasks = SampleTaskBoard.Day();
            double from = SampleTaskBoard.NowHour;
            foreach (var r in bad)
            {
                var adv = DispatchEngine.OnActualDeviation(r.Task.Id, cfg, tasks, from);
                lines.Add($"◆ {r.Equip}·{r.Zone}（达成 {r.Task.AttainmentPct:0}%）：{adv.Summary}");
                foreach (var s in adv.Suggestions.Take(3)) lines.Add("    " + s);
            }
        }
        catch (Exception ex)
        {
            lines.Add($"重排引擎不可用（{ex.Message}）——请到「生产任务动态调整」手动处置。");
        }

        lines.Add("（只算建议、不改计划；要执行去「生产任务动态调整」。）");
        feedback.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>重读故障记录并重算各任务的故障工时（在「设备状态·故障报修」录完后点这里）。</summary>
    private void OnRecalcFault()
    {
        _faults = TaskPersistence.LoadFaults(_date);
        foreach (var r in _all) Fill(r);
        ApplyFilter();

        double total = _all.Sum(r => FaultHoursOf(r.Task));
        feedback.Text = _faults.Count == 0
            ? "本日无故障记录（fault_events 为空）。故障工时留空——不编造数字。"
            : $"已按 {_faults.Count} 条故障记录重算：合计故障工时 {total:0.#} h。"
              + Environment.NewLine + string.Join("；", _faults.Take(6).Select(f => $"{f.EquipId} {f.Caption}"));
    }

    private static bool TryNum(string? s, out double v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return true;   // 空 = 未录入，不算错
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            || double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v);
    }
}
