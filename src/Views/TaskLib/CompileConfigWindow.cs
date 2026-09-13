// 忠实移植自原 PitMine3D Modules/TaskLib/Features/CompileConfigWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：WPF GroupBox/Expander 样式 → TaskUi.GroupBox / Avalonia Expander；MF 列 ProgressBar 模板 → 同构 FuncDataTemplate；
// Visibility → IsVisible；能力条的 ColumnDefinition 星比 → 同一套 GridLength(Star)。
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;
using WorkCalendar = PitMine3D.Kylin.TaskLib.Engine.WorkCalendar;   // 与 Kylin 旧切片 Data.WorkCalendar 消歧
using ProductionPlanContext = PitMine3D.Kylin.TaskLib.Engine.ProductionPlanContext;

/// <summary>
/// 编制配置：裂解装箱的盘子（人工锚点）+ 铲—车编组联动面板。
///
/// ── 从「假联动」到「真联动」──
/// 原实现是从**已生成的任务**反推编组，可用列恒为「可用」——那不是联动，是把结果再念一遍。
/// 现改为对每个采装面现算：
///     运距 = HaulResolver.ResolveAll(face, cfg.Sinks)   ← 逐物料分项，各自走 路网/手填/兜底 三层
///     编组 = FleetMatcher.Match(face, legs)             ← τ_L / T_c 按车次份额加权 → n* / MF
/// 于是「改去向 → 运距变 → T_c 变 → 荐车数变 → 班产变」这条链在界面上真的会动。
/// 混采面（煤去破碎站、岩去排土场）走多腿，与甘特车次条、派车单同一套口径——
/// 单腿口径会让同一个面在三个界面上显示不同的 T_c 与荐车数。
///
/// 匹配系数 MF = n·τ_L/T_c 是本窗口最有价值的一列：
///   MF &lt; 1  铲等车（运力瓶颈，加车能提产）
///   MF ≈ 1  最优（铲车两侧都不闲）
///   MF &gt; 1  车排队（采装瓶颈，加车只是排更长的队，白烧油）
///
/// 求解器任一环不可用即整体退回原「按任务反推」的只读显示，并在状态栏说明原因。
/// </summary>
public sealed class CompileConfigWindow : Window
{
    // MF 偏离 1 的容差：≤0.10 均衡（绿）· ≤0.30 可接受（黄）· 更远（红）
    private const double MfBalancedTol = 0.10;
    private const double MfWarnTol = 0.30;

    private static readonly IBrush OkBrush = TaskUi.Green;
    private static readonly IBrush WarnBrush = TaskUi.Amber;
    private static readonly IBrush BadBrush = TaskUi.Red;

    /// <summary>兜底视图（求解器不可用时）的一行。</summary>
    public sealed class EquipRow
    {
        public string Equip { get; set; } = "";
        public string Category { get; set; } = "";
        public string Cap { get; set; } = "";
        public string Group { get; set; } = "";
        public string Avail { get; set; } = "";
    }

    /// <summary>
    /// 逐受矿点入仓标准表的一行。三个限值一律用**字符串**装：
    /// 空串 = 该项按全矿级缺省判，与 0 是两回事（0 是"灰分上限 0%"，谁都过不去）。
    /// 用 double? 绑 DataGrid 时，人清空格子会得到 null 还是 0 取决于转换器，
    /// 那正是"清空了却还在按 0 判"这类回归的来源 —— 索性在界面层一路走字符串，保存时才解析。
    /// </summary>
    public sealed class SinkBlendRow
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string KindLabel { get; set; } = "";
        public string Ash { get; set; } = "";
        public string Cv { get; set; } = "";
        public string Sulfur { get; set; } = "";
        public string FaceNote { get; set; } = "";
        /// <summary>台账自带标准（锚点不覆盖它）。</summary>
        public bool FromLedger { get; set; }
    }

    /// <summary>编组联动表的一行 = 一个采装面的 FleetMatchResult。</summary>
    public sealed class FleetRow
    {
        public string Zone { get; set; } = "";
        public string Shovel { get; set; } = "";
        public string Material { get; set; } = "";
        public string Sink { get; set; } = "";
        public string Haul { get; set; } = "";
        public string Tc { get; set; } = "";
        public string Takt { get; set; } = "";
        public string Star { get; set; } = "";
        public string OnSite { get; set; } = "";
        public string Cap { get; set; } = "";
        public string Bottleneck { get; set; } = "";

        public double Mf { get; set; }
        /// <summary>进度条取值：MF 截到 [0,2]，1.0 即条形正中，视觉上「偏离中点多远」就是失配多重。</summary>
        public double MfBar => Math.Clamp(Mf, 0, 2);
        public string MfText { get; set; } = "";
        public string MfTip { get; set; } = "";
        public IBrush MfBrush { get; set; } = OkBrush;

        /// <summary>FleetMatcher 给的那句中文解释（选中行详情区显示）。</summary>
        public string Explain { get; set; } = "";
        /// <summary>本次求解采用的编组规则（型号溯源）。</summary>
        public string Rule { get; set; } = "";
    }

    // ── 控件（与原 XAML x:Name 一一对应）──
    private readonly TextBlock budgetHeadline = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ColumnDefinition loadFitCol = new(1, GridUnitType.Star), loadShortCol = new(0, GridUnitType.Star);
    private readonly ColumnDefinition dumpFitCol = new(1, GridUnitType.Star), dumpShortCol = new(0, GridUnitType.Star);
    private readonly TextBlock loadBarText = BarText(), dumpBarText = BarText();
    private readonly TextBlock loadStat = Stat(), dumpStat = Stat();
    private readonly TextBlock budgetCost = Hint(new Thickness(0, 8, 0, 0)), budgetNote = Hint(new Thickness(0, 3, 0, 0));

    private readonly TextBox shiftBox = Echo(420), dayHoursBox = Echo(420), downtimeBox = Echo(420);
    private readonly TextBox handoverBox = Field(80), weatherDerateBox = Field(80);
    private readonly TextBlock handoverHint = InlineHint(), derateHint = InlineHint();
    private readonly TextBox loadDerateBox = Field(56), haulDerateBox = Field(56), dumpDerateBox = Field(56);
    private readonly TextBlock linkDerateHint = Hint(new Thickness(142, 0, 0, 0));

    private readonly RadioButton rbBal = new() { Content = "均衡型", IsChecked = true, Margin = new Thickness(0, 0, 18, 0), GroupName = "org" };
    private readonly RadioButton rbMulti = new() { Content = "多面展开型", Margin = new Thickness(0, 0, 18, 0), GroupName = "org" };
    private readonly RadioButton rbConc = new() { Content = "集中强采型", GroupName = "org" };
    private readonly TextBlock orgHint = Hint(new Thickness(0, 6, 0, 0));
    private readonly TextBox effHoursBox = Field(80);
    private readonly TextBlock effHint = InlineHint();
    private readonly TextBox ashBox = Field(80), cvBox = Field(80), sulfurBox = Field(80);
    private readonly TextBlock blendHint = Hint(new Thickness(0, 4, 0, 0));
    private readonly DataGrid sinkBlendGrid = TaskUi.Grid(readOnly: false);
    private readonly TextBlock sinkBlendHint = Hint(new Thickness(0, 4, 0, 0));

    private readonly TextBox minPreparedBox = Field(80);
    private readonly TextBlock preparedHint = InlineHint();
    private readonly TextBox truckRuleBox = Echo(420);
    private readonly TextBlock truckRuleHint = Hint(new Thickness(142, 0, 0, 0));

    private readonly Expander chainExpander = new() { Margin = new Thickness(0, 0, 0, 8), IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock chainHeadline = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold };
    private readonly TextBox chainBox = new() { Height = 170, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas,Microsoft YaHei UI"), FontSize = 12, AcceptsReturn = true };
    private readonly TextBox dateBox = Echo(280), workdaysBox = Echo(420), blastBox = Echo(420);
    private readonly TextBox srcBox = new() { Width = 640, MaxHeight = 86, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };

    private readonly DataGrid fleetGrid = TaskUi.Grid();
    private readonly Border fleetDetailBox = new() { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 8), BorderThickness = new Thickness(1) };
    private readonly TextBlock fleetDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock fleetRule = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
    private readonly DataGrid equipGrid = TaskUi.Grid();
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Text = "设备池/编组/班产 来自设备分析（联动，不在此重录）；上面三组为人工锚点" };

    public CompileConfigWindow()
    {
        Title = "编制配置 — 日常生产组织";
        TaskUi.Place(this, 980, 760);

        // 副标题照实写：这扇窗只装「切分规则」，盘子里的事实（作业面/去向/设备/爆破）各有其台账。
        var header = TaskUi.Header("编制配置", "裂解装箱用的切分规则 —— 当日能力 / 面间分配 / 校核阈值。盘子里的事实（作业面·去向·设备·爆破）在各自台账里改，此处只回显");

        var body = new StackPanel();

        // ══ 第一层：当日能力预算 ══ 编制员打开这扇窗要答的第一个问题是「这盘排不排得下」。
        // 把那些百分数和小时数统一翻译成 m³，用的就是装箱那笔账（DayCapacityBudget）。
        {
            var sp = new StackPanel();
            sp.Children.Add(budgetHeadline);
            // 采装/排土分两行且**永不相加**：采装是原位实方、排土是排弃占容方，V容 = V实 × Kr。
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            AddBarRow(g, 0, "采装（原位实方）", loadFitCol, loadShortCol, loadBarText, loadStat,
                "整条 = 当日目标，按**逐面**拆成「排得下」与「欠」两段。\n不是总目标÷总能力——A 面闲 3000、B 面欠 3000 时总账刚好平，条形却必须见红。");
            AddBarRow(g, 1, "排土（排弃占容方）", dumpFitCol, dumpShortCol, dumpBarText, dumpStat, null);
            sp.Children.Add(g);
            // 三个旋钮各自吃掉多少方量：这是"改这个数到底值不值"的唯一凭据
            sp.Children.Add(budgetCost); sp.Children.Add(budgetNote);
            body.Children.Add(TaskUi.GroupBox("当日能力预算 — 这盘计划排不排得下", sp, new Thickness(0, 0, 0, 8), 10));
        }

        // ══ 第二层：切分规则（可改锚点）══ 纪律：一条规则要么在这儿能改，要么在这儿能看见它现在取什么值、真源在哪。
        {
            var sp = new StackPanel();
            sp.Children.Add(Fld(Lab("班制"), shiftBox));
            sp.Children.Add(Fld(Lab("当日有效工时", "班时窗 − 检修档期 − 爆破清场 − 非首班交接损失。唯一实现在 WorkWindowCalc，装箱与逐日能力日历共用"), dayHoursBox));
            sp.Children.Add(Fld(Lab("交接班损失 h/班", "非首班每班扣的坡道时长（点名/交接/进场）。落在时窗上，与天气降效正好相反。\n0 = 人工确认本矿不扣交接；清空 = 回引擎缺省 0.5h"), handoverBox, handoverHint));
            sp.Children.Add(Fld(Lab("天气降效 %", "全盘班产 ×(1−降效)。落在能力上不落在时窗上：\n雨雪是「每小时干得少」，检修/爆破才是「少了几个小时」——\n混在一处，甘特上的条形位置会全错"), weatherDerateBox, derateHint));
            // 分环节降效：三格都留空 = 全部跟随上面那个全盘值；填任意一格即进入"分环节"，逐面按其瓶颈侧折算。
            sp.Children.Add(Fld(Lab("　└ 分环节 %", "留空 = 跟随上面的全盘降效。\n雨雪打的是路面车速与卸点排队（运输），大风停的是电铲（采装），\n低温打的是排土场推土 —— 三件事量级不同，一个数只能一起降。"),
                SmallLab("采装", 0), loadDerateBox, SmallLab("运输", 12), haulDerateBox, SmallLab("排土", 12), dumpDerateBox));
            sp.Children.Add(linkDerateHint);
            sp.Children.Add(Fld(Lab("检修 / 爆破", "真源分别是设备检修档期与当日 blast_event，在各自台账里改；此处只回显它们吃掉了多少工时"), downtimeBox));
            body.Children.Add(TaskUi.GroupBox("切分规则 ① 当日能力（决定这盘排不排得下）", sp, new Thickness(0, 0, 0, 8), 10));
        }
        {
            var sp = new StackPanel();
            sp.Children.Add(SubHead("作业组织策略", new Thickness(0, 0, 0, 4)));
            ToolTip.SetTip(rbBal, "不动上游给的分配（月计划份额 / 采掘单元剩余量算出来的比例）");
            ToolTip.SetTip(rbMulti, "按面日产能等比例摊，让每个面都在干：抗单点故障、采准压力分散，代价是设备分散、辅助工程多");
            ToolTip.SetTip(rbConc, "按面日产能从大到小灌满，能少开面就少开：设备集中、辅助工程少，代价是备采消耗快、单点故障影响大");
            var rbs = new StackPanel { Orientation = Orientation.Horizontal }; rbs.Children.Add(rbBal); rbs.Children.Add(rbMulti); rbs.Children.Add(rbConc);
            sp.Children.Add(rbs);
            sp.Children.Add(orgHint);
            var eff = Fld(Lab("面日产能工时 h/日", "面日产能上限 = 编组班产 × 本值 × 天气系数。\n它是作业组织重分配与配煤重分配**共用的同一个闸**——\n「为什么灌不进去 / 为什么只灌了一半」全卡在这个数上。\n清空 = 回引擎缺省 20h（≈三班扣检修/爆破/交接后的有效工时）"), effHoursBox, effHint);
            eff.Margin = new Thickness(0, 10, 0, 4);
            sp.Children.Add(eff);

            sp.Children.Add(SubHead("综合配煤标准（全矿级 · 按各面采出量加权）"));
            sp.Children.Add(Fld(Lab("灰分上限 %"), ashBox));
            sp.Children.Add(Fld(Lab("热值下限 MJ/kg"), cvBox));
            sp.Children.Add(Fld(Lab("硫分上限 %"), sulfurBox));
            var blendNote = Hint(new Thickness(0, 6, 0, 0));
            blendNote.Text = "超标时装箱前会把采出量从高灰面移到低灰面（保总量、不超上面那个面日产能闸）。各作业面自己的煤质目标在「作业面台账」里填。配煤排在作业组织之后：策略是偏好，配煤是约束，约束必须能推翻偏好。";
            sp.Children.Add(blendNote);
            sp.Children.Add(blendHint);

            // 逐受矿点：配煤本来就是按受矿点成立的。两个破碎站各有各的合同指标，把两边的煤混一起算"全矿综合灰分"没有物理含义。
            sp.Children.Add(SubHead("逐受矿点入仓标准（留空 = 按上面的全矿级缺省）"));
            sinkBlendGrid.MaxHeight = 150; sinkBlendGrid.Margin = new Thickness(0, 2, 0, 0);
            ToolTip.SetTip(sinkBlendGrid, "量只在同一受矿点内部的作业面之间移动。\n跨点移量不会改善任何一个点的灰分，却会凭空改掉两个点的到货量。");
            sinkBlendGrid.Columns.Add(StarCol("受矿点", nameof(SinkBlendRow.Name), 1.4, true));
            sinkBlendGrid.Columns.Add(TaskUi.TextCol("类型", nameof(SinkBlendRow.KindLabel), 80));
            sinkBlendGrid.Columns.Add(TaskUi.TextCol("灰分上限 %", nameof(SinkBlendRow.Ash), 92, readOnly: false));
            sinkBlendGrid.Columns.Add(TaskUi.TextCol("热值下限", nameof(SinkBlendRow.Cv), 86, readOnly: false));
            sinkBlendGrid.Columns.Add(TaskUi.TextCol("硫分上限 %", nameof(SinkBlendRow.Sulfur), 92, readOnly: false));
            sinkBlendGrid.Columns.Add(StarCol("今日供矿面", nameof(SinkBlendRow.FaceNote), 1, true));
            sp.Children.Add(sinkBlendGrid);
            sp.Children.Add(sinkBlendHint);
            body.Children.Add(TaskUi.GroupBox("切分规则 ② 面间分配（决定同样的量摊到哪几个面上）", sp, new Thickness(0, 0, 0, 8), 10));
        }
        {
            var sp = new StackPanel();
            sp.Children.Add(Fld(Lab("备采保有下限 天", "采准三量落到日计划：某面按当日强度还能采几天，低于此值即预警。0=不校核"), minPreparedBox, preparedHint));
            var pn = Hint(new Thickness(0, 2, 0, 0));
            pn.Text = "只对录了「备采(m³)」的作业面生效（在「作业面台账」里填）。没录备采的面会在计划校核里如实列为「判不了」，不会拿 0 当采空。";
            sp.Children.Add(pn);
            // 运力告警：**刻意只回显不开可改口子**。荐车数 n* 是 FleetMatcher 由 τ_L/T_c 解出来的物理量。
            var tr = Fld(Lab("运力告警口径"), truckRuleBox); tr.Margin = new Thickness(0, 8, 0, 4);
            sp.Children.Add(tr);
            sp.Children.Add(truckRuleHint);
            body.Children.Add(TaskUi.GroupBox("切分规则 ③ 校核阈值（决定报不报警，不改计划）", sp, new Thickness(0, 0, 0, 8), 10));
        }

        // ══ 第三层：盘子来源（只读事实）══ 体检结论那一行**始终可见**，逐段明细收进折叠区。
        {
            var hd = new DockPanel { LastChildFill = true, Width = 880 };
            var re = TaskUi.Btn("重新体检", OnRecheckChain, 80); re.Margin = new Thickness(10, 0, 0, 0);
            ToolTip.SetTip(re, "重新装配一次盘子并逐段判来源。台账改过之后点它。");
            DockPanel.SetDock(re, Avalonia.Controls.Dock.Right); hd.Children.Add(re);
            hd.Children.Add(chainHeadline);
            chainExpander.Header = hd;
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            ToolTip.SetTip(chainBox, "每一行 = 一段链路：状态｜段名：来源原话　→ 补法。\n「样例」= 这一段吃的是示例露天矿的数据；「缺」= 本日确实没有；「部分真实」= 有真数据但没齐。");
            sp.Children.Add(chainBox);
            sp.Children.Add(Fld(Lab("期次 / 日期"), dateBox));
            sp.Children.Add(Fld(Lab("本月有效作业日", "月→日裂解的除数：日采出 = 月采出 ÷ 作业日。真源是班次日历"), workdaysBox));
            sp.Children.Add(Fld(Lab("爆破窗口", "真源是当日 blast_event；采装/排土作业止于爆破前"), blastBox));
            sp.Children.Add(Fld(Lab("盘子来源", "月计划 → 去向 → 运距 → 编组，四步接线各自的数据来源"), srcBox));
            chainExpander.Content = sp;
            body.Children.Add(chainExpander);
        }

        // ══ 第四层：解算结果（不是配置，是这几条规则算出来的东西）══
        {
            var sp = new StackPanel();
            // 真联动表：每个采装面按 FleetMatcher.Match(face, HaulResolver.Resolve(face, sink)) 现算
            fleetGrid.MaxHeight = 230; fleetGrid.Margin = new Thickness(0);
            fleetGrid.Columns.Add(StarCol("作业面", nameof(FleetRow.Zone), 1.2, true));
            fleetGrid.Columns.Add(TaskUi.TextCol("主铲", nameof(FleetRow.Shovel), 72));
            fleetGrid.Columns.Add(TaskUi.TextCol("物料", nameof(FleetRow.Material), 92));
            fleetGrid.Columns.Add(StarCol("去向", nameof(FleetRow.Sink), 1.2, true));
            fleetGrid.Columns.Add(TaskUi.TextCol("运距(km)", nameof(FleetRow.Haul), 110));
            fleetGrid.Columns.Add(TaskUi.TextCol("T_c(min)", nameof(FleetRow.Tc), 72));
            fleetGrid.Columns.Add(TaskUi.TextCol("τ_L(min)", nameof(FleetRow.Takt), 72));
            fleetGrid.Columns.Add(TaskUi.TextCol("荐 n*", nameof(FleetRow.Star), 52));
            fleetGrid.Columns.Add(TaskUi.TextCol("配车", nameof(FleetRow.OnSite), 52));
            // 匹配系数 MF：本窗口最有价值的一列。条形以 1.0 为基准刻度，偏离 1 越远颜色越红。
            fleetGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = TaskUi.Head("匹配系数 MF"), Width = new DataGridLength(128), SortMemberPath = nameof(FleetRow.Mf),
                CellTemplate = new FuncDataTemplate<FleetRow>((_, _) =>
                {
                    var g = new Grid();
                    g.Bind(ToolTip.TipProperty, new Binding(nameof(FleetRow.MfTip)));
                    var pb = new ProgressBar { Height = 15, Minimum = 0, Maximum = 2, VerticalAlignment = VerticalAlignment.Center };
                    pb.Bind(ProgressBar.ValueProperty, new Binding(nameof(FleetRow.MfBar)) { Mode = BindingMode.OneWay });
                    pb.Bind(ProgressBar.ForegroundProperty, new Binding(nameof(FleetRow.MfBrush)));
                    TaskUi.Theme(pb, ProgressBar.BackgroundProperty, "Theme.Input.Background");
                    TaskUi.Theme(pb, ProgressBar.BorderBrushProperty, "Theme.Surface.Border");
                    var t = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    t.Bind(TextBlock.TextProperty, new Binding(nameof(FleetRow.MfText)));
                    g.Children.Add(pb); g.Children.Add(t);
                    return g;
                }),
            });
            fleetGrid.Columns.Add(StarCol("瓶颈", nameof(FleetRow.Bottleneck), 1.1, true));
            fleetGrid.Columns.Add(TaskUi.TextCol("班产(m³/h)", nameof(FleetRow.Cap), 88));
            fleetGrid.SelectionChanged += (_, _) => OnFleetSelected();
            sp.Children.Add(fleetGrid);

            // 选中行详情：FleetMatcher 那句中文解释 + 采用的编组规则（型号溯源）
            TaskUi.Theme(fleetDetailBox, Border.BackgroundProperty, "Theme.Surface.Background");
            TaskUi.Theme(fleetDetailBox, Border.BorderBrushProperty, "Theme.Surface.Border");
            TaskUi.Theme(fleetDetail, TextBlock.ForegroundProperty, "Theme.Text.Body");
            TaskUi.Theme(fleetRule, TextBlock.ForegroundProperty, "Theme.Text.Muted");
            var dsp = new StackPanel(); dsp.Children.Add(fleetDetail); dsp.Children.Add(fleetRule); fleetDetailBox.Child = dsp;
            sp.Children.Add(fleetDetailBox);

            // 兜底视图：编组/运距求解器不可用时退回原「按已生成任务反推」的只读显示
            equipGrid.IsVisible = false; equipGrid.Margin = new Thickness(0, 8, 0, 0); equipGrid.MaxHeight = 180;
            equipGrid.Columns.Add(TaskUi.TextCol("设备", nameof(EquipRow.Equip), 100));
            equipGrid.Columns.Add(TaskUi.TextCol("类别", nameof(EquipRow.Category), 80));
            equipGrid.Columns.Add(TaskUi.TextCol("预测班产", nameof(EquipRow.Cap), 120));
            equipGrid.Columns.Add(StarCol("编组建议", nameof(EquipRow.Group), 1, true));
            // 列名照实写：这一列取的是"今天有没有排上产"，不是设备可用性
            equipGrid.Columns.Add(TaskUi.TextCol("排产", nameof(EquipRow.Avail), 70));
            sp.Children.Add(equipGrid);
            body.Children.Add(TaskUi.GroupBox("铲—车编组联动（真解：物料密度 × 真运距 × 设备参数 → τ_L / T_c → n* → 班产）", sp, new Thickness(0), 6));
        }

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(14, 12) };

        var footDock = new DockPanel();
        var save = TaskUi.Btn("保存配置", OnSave, 110, bold: true); save.Height = 32; save.Margin = new Thickness(0);
        DockPanel.SetDock(save, Avalonia.Controls.Dock.Right); footDock.Children.Add(save);
        var reset = TaskUi.Btn("重置", OnReset, 80); reset.Height = 32; reset.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(reset, Avalonia.Controls.Dock.Right); footDock.Children.Add(reset);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        footDock.Children.Add(toolStatus);
        var foot = TaskUi.Bar(footDock, top: false, padY: 10);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(header, 0); Grid.SetRow(scroll, 1); Grid.SetRow(foot, 2);
        root.Children.Add(header); root.Children.Add(scroll); root.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = root;

        Opened += (_, _) => Fill();
    }

    // ── 布局小件（对应原 XAML 里的 Lab / Fld / Hint / Echo / SubHead 样式）──

    private static TextBlock Lab(string text, string? tip = null)
    {
        var t = new TextBlock { Text = text, Width = 142, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        if (tip != null) ToolTip.SetTip(t, tip);
        return t;
    }

    private static TextBlock SmallLab(string text, double left)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(left, 0, 4, 0) };
        TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return t;
    }

    private static StackPanel Fld(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4) };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    // 提示语：一律贴在它解释的那一行下面，不另起一段
    private static TextBlock Hint(Thickness margin)
    {
        var t = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = margin };
        TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return t;
    }

    private static TextBlock InlineHint()
    {
        var t = Hint(new Thickness(10, 0, 0, 0)); t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    // 只读回显：外观上与可改框明显不同，免得人对着它改半天
    private static TextBox Echo(double width)
    {
        var b = new TextBox { Width = width, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(b, TextBox.ForegroundProperty, "Theme.Text.Body");
        return b;
    }

    private static TextBox Field(double width) => new() { Width = width, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock SubHead(string text, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = margin ?? new Thickness(0, 10, 0, 4) };
        TaskUi.Theme(t, TextBlock.ForegroundProperty, "Theme.Text.Body");
        return t;
    }

    private static TextBlock BarText() => new() { FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Stat() => new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };

    private static DataGridTextColumn StarCol(string header, string path, double star, bool readOnly)
        => new() { Header = TaskUi.Head(header), Binding = new Binding(path), Width = new DataGridLength(star, DataGridLengthUnitType.Star), IsReadOnly = readOnly };

    private static void AddBarRow(Grid g, int row, string label, ColumnDefinition fit, ColumnDefinition shortCol, TextBlock inner, TextBlock stat, string? tip)
    {
        var lab = new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(lab, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        Grid.SetRow(lab, row); Grid.SetColumn(lab, 0); g.Children.Add(lab);

        var inn = new Grid(); inn.ColumnDefinitions.Add(fit); inn.ColumnDefinitions.Add(shortCol);
        var r1 = new Avalonia.Controls.Shapes.Rectangle { Fill = OkBrush }; Grid.SetColumn(r1, 0);
        var r2 = new Avalonia.Controls.Shapes.Rectangle { Fill = BadBrush }; Grid.SetColumn(r2, 1);
        Grid.SetColumnSpan(inner, 2);
        inn.Children.Add(r1); inn.Children.Add(r2); inn.Children.Add(inner);
        var box = new Border { Margin = new Thickness(0, 3), Height = 17, BorderThickness = new Thickness(1), Child = inn };
        TaskUi.Theme(box, Border.BorderBrushProperty, "Theme.Surface.Border");
        TaskUi.Theme(box, Border.BackgroundProperty, "Theme.Input.Background");
        if (tip != null) ToolTip.SetTip(box, tip);
        Grid.SetRow(box, row); Grid.SetColumn(box, 1); g.Children.Add(box);

        Grid.SetRow(stat, row); Grid.SetColumn(stat, 2); g.Children.Add(stat);
    }

    // ── 装载 ─────────────────────────────────────────────────────────────────

    private void Fill()
    {
        ExploderConfig cfg;
        try { cfg = SampleTaskBoard.Config(); }
        catch (Exception ex)
        {
            FallbackToLegacy($"盘子装载失败（{Short(ex)}）");
            return;
        }

        try { srcBox.Text = SampleTaskBoard.SourceLabel; } catch { }
        FillChain();
        FillAnchors(cfg);

        var rows = new List<FleetRow>();
        string? firstError = null;

        foreach (var face in cfg.Faces.Where(f => f.Process == ProcessType.Load))
        {
            try { rows.Add(Solve(cfg, face)); }
            catch (Exception ex) { firstError ??= Short(ex); }
        }

        if (rows.Count == 0)
        {
            FallbackToLegacy(firstError != null ? $"编组求解不可用（{firstError}）" : "盘子里没有采装面");
            return;
        }

        fleetGrid.ItemsSource = rows;
        fleetGrid.IsVisible = true;
        fleetDetailBox.IsVisible = true;
        equipGrid.IsVisible = false;
        fleetGrid.SelectedIndex = 0;

        int under = rows.Count(r => r.Mf < 1 - MfBalancedTol);
        int over = rows.Count(r => r.Mf > 1 + MfBalancedTol);
        string rule = SafeFleetLabel();

        toolStatus.Text =
            $"编组真联动：{rows.Count} 面已解（铲等车 {under} · 车排队 {over} · 均衡 {rows.Count - under - over}）"
            + (firstError != null ? $"；{firstError}（部分面退回原显示）" : "")
            + (rule.Length > 0 ? $"　|　{rule}" : "");
    }

    /// <summary>
    /// 一个采装面的真解：先解运距（三层兜底），再按物理公式解编组。
    /// 混采面（煤去破碎站、岩去排土场）走多腿：逐物料解各自运距，T_c/τ_L 按车次份额加权，
    /// 与甘特车次条、派车单同一套口径——否则同一个面在三个界面上会显示不同的 T_c 与荐车数。
    /// </summary>
    private static FleetRow Solve(ExploderConfig cfg, FaceInput face)
    {
        var sink = FindSink(cfg, face);
        var haul = HaulResolver.Resolve(face, sink);

        FleetMatchResult m;
        var legs = HaulResolver.ResolveAll(face, cfg.Sinks);
        m = legs.Count > 1 ? FleetMatcher.Match(face, legs) : FleetMatcher.Match(face, haul);

        double mf = m.MatchFactor;
        double dev = Math.Abs(mf - 1);

        return new FleetRow
        {
            Zone = face.Zone,
            Shovel = string.IsNullOrWhiteSpace(face.Group.MainEquipment) ? "—" : face.Group.MainEquipment,
            Material = face.ResolvedMix.Caption,
            Sink = sink != null ? $"{sink.Name}（{sink.Kind.Label()}）"
                 : face.HasDestination ? $"{face.DestinationName}（不在登记簿）" : "未指定卸点",
            // 混采面显示两条线的等效运距，单腿维持原样（"3.2（路网）"）
            Haul = m.IsMultiLeg
                 ? string.Join(" ｜ ", m.Legs.Select(l => $"{MaterialCatalog.Resolve(l.MaterialCode).Name} {l.HaulKm:0.##}"))
                 : haul.Feasible ? $"{haul.EquivKm:0.##}（{haul.Source}）" : "未解出",
            Tc = $"{m.CycleTimeMin:0.#}",
            Takt = $"{m.LoadTaktMin:0.##}",
            Star = $"{m.OptimalTrucks}",
            OnSite = face.Group.Trucks.Count > 0 ? $"{face.Group.Trucks.Count}" : "—",
            Cap = $"{m.Group.GroupCapacityM3PerH:0.#}",
            Bottleneck = m.Bottleneck,

            Mf = mf,
            MfText = $"{mf:0.00}",
            MfBrush = dev <= MfBalancedTol ? OkBrush : dev <= MfWarnTol ? WarnBrush : BadBrush,
            MfTip = MfTip(mf, m),
            Explain = m.Explain,
            Rule = m.RuleCaption,
        };
    }

    private static string MfTip(double mf, FleetMatchResult m)
        => (mf < 1 - MfBalancedTol
                ? $"MF {mf:0.00} < 1：铲等车。车队运力不足，电铲有 {(1 - m.ShovelUtil) * 100:0}% 的时间在空等，加车可直接提产。"
                : mf > 1 + MfBalancedTol
                    ? $"MF {mf:0.00} > 1：车排队。采装能力已是瓶颈，卡车利用率仅 {m.TruckUtil * 100:0}%，再加车只会排更长的队。"
                    : $"MF {mf:0.00} ≈ 1：铲车匹配，两侧都不闲，这是最优区间。")
         + $"\n电铲利用率 {m.ShovelUtil * 100:0.#}% · 卡车利用率 {m.TruckUtil * 100:0.#}% · 到达需排队概率 {m.WaitProbability * 100:0.#}%";

    /// <summary>按 DestinationId 找去向，找不到再按 DestinationName 兜一次（与引擎侧同一套查法）。</summary>
    private static SinkNode? FindSink(ExploderConfig cfg, FaceInput face)
    {
        try
        {
            var s = cfg.Sinks.Find(face.DestinationId);
            if (s != null) return s;
            if (string.IsNullOrWhiteSpace(face.DestinationName)) return null;
            return cfg.Sinks.All.FirstOrDefault(
                x => string.Equals(x.Name, face.DestinationName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private void OnFleetSelected()
    {
        if (fleetGrid.SelectedItem is not FleetRow r)
        {
            fleetDetail.Text = "选中一行查看编组求解详情。";
            fleetRule.Text = "";
            return;
        }
        fleetDetail.Text = $"【{r.Zone}】{r.Explain}";
        fleetRule.Text = $"编组规则：{r.Rule}　|　瓶颈判定：{r.Bottleneck}";
    }

    // ── 兜底：求解器不可用时退回原「按已生成任务反推」的只读显示 ────────────────

    private void FallbackToLegacy(string why)
    {
        fleetGrid.IsVisible = false;
        fleetDetailBox.IsVisible = false;
        equipGrid.IsVisible = true;
        FillEquipLegacy();
        toolStatus.Text = $"编组联动未生效（{why}）——已退回按已生成任务反推的只读显示";
    }

    /// <summary>
    /// 链路体检 —— 逐段的状态 + 原话 + 补法。
    /// <para>放在这个窗口是因为它就在「盘子来源」那一框旁边：那一框装的是十二段拼成的一长句，
    /// 「用样例盘子」夹在第五段里、字号与其余十一段一样。体检把同一件事拆开摆出来。</para>
    /// </summary>
    private void FillChain()
    {
        try
        {
            var rep = ProductionChainStatus.Inspect();
            chainHeadline.Text = rep.Headline;
            // 有样例 = 红；没样例但作业面不真 = 琥珀；全真 = 绿。三档一眼分得出。
            chainHeadline.Foreground = new SolidColorBrush(
                rep.SampleCount > 0 ? Color.FromRgb(0xC0, 0x39, 0x2B)
                : !rep.Usable ? Color.FromRgb(0xD9, 0x77, 0x06)
                : Color.FromRgb(0x16, 0xA3, 0x4A));
            chainBox.Text = string.Join("\n", rep.Stages.Select(x => x.Line));
            // 逐段明细平时收着，**但有段吃样例时自动展开**：那句「这盘计划里有 N 段是示例露天矿的数据」
            // 收在一行折叠头里，等于把最该看见的事藏起来了。
            chainExpander.IsExpanded = rep.SampleCount > 0 || !rep.Usable;
        }
        catch (Exception ex)
        {
            chainHeadline.Text = $"体检做不下去（{ex.GetType().Name}：{ex.Message}）";
            chainBox.Text = "";
            chainExpander.IsExpanded = true;
        }
    }

    private void OnRecheckChain()
    {
        ProductionPlanContext.Invalidate();   // 台账可能刚被别的窗口改过
        FillChain();
    }

    private void FillEquipLegacy()
    {
        try
        {
            var byMain = SampleTaskBoard.Day()
                .GroupBy(t => t.Group.MainEquipment)
                .Select(g => g.First().Group)
                .ToList();
            var roster = SampleTaskBoard.Roster().Where(r => !r.Sub).ToList();

            equipGrid.ItemsSource = roster.Select(r =>
            {
                var grp = byMain.FirstOrDefault(g => g.MainEquipment == r.EquipId);
                return new EquipRow
                {
                    Equip = r.EquipId,
                    Category = r.Category,
                    Cap = grp != null && grp.GroupCapacityM3PerH > 0 ? $"{grp.GroupCapacityM3PerH:0} m³/h" : "—",
                    Group = grp != null && grp.Trucks.Count > 0 ? $"配 {grp.Trucks.Count} 车 / 荐 {grp.RecommendedTrucks}" : "单机",
                    Avail = grp != null ? "已排产" : "未排产",
                };
            }).ToList();
        }
        catch { equipGrid.ItemsSource = null; }
    }

    // ── 人工锚点：配煤三条 + 天气降效 ──────────────────────────────────────────
    //
    //  「只写人改过的那几项」：开窗时记一份文本快照，保存时逐项比对，
    //  没动过的保持 null（= 由引擎缺省接管）。否则第一次保存就会把当前缺省值固化进文件，
    //  日后引擎调缺省时，这份旧文件会把老数字顶回来——那是最难查的一类回归。
    //  框清空 = 撤掉该项锚点，回引擎缺省。

    private string _loadedAsh = "", _loadedCv = "", _loadedSulfur = "", _loadedDerate = "", _loadedPrepared = "";
    private string _loadedEff = "", _loadedHandover = "";
    private string _loadedLoadD = "", _loadedHaulD = "", _loadedDumpD = "";
    private List<SinkBlendRow> _sinkRows = new();
    private WorkOrganization _loadedOrg = WorkOrganization.Balanced;

    private void FillAnchors(ExploderConfig cfg)
    {
        var a = CompileOverrides.Current;

        dateBox.Text = ProjectScope.DateLabel;
        shiftBox.Text = cfg.Shifts.Count > 0
            ? $"{cfg.Shifts.Count} 班：" + string.Join(" / ", cfg.Shifts.Select(s => $"{s.Name} {s.Start:00.##}–{s.End:00.##}"))
            : "本日无班次（在「班次日历」里补）";
        blastBox.Text = cfg.BlastEnd > cfg.BlastStart
            ? $"{cfg.BlastStart:00.##}–{cfg.BlastEnd:00.##}（采装/排土止于爆破前）"
            : "本日无爆破窗口";

        var wd = WorkCalendar.MonthWorkdays(ProjectScope.WorkDate);
        workdaysBox.Text = wd.FromLedger
            ? $"{wd.Workdays} 天（{wd.MonthLabel} · 日历口径）"
            : $"未知 —— {wd.Label}";

        var std = cfg.Blend ?? new BlendStandard();
        ashBox.Text = Num(std.MaxAshPct);
        cvBox.Text = Num(std.MinCalorificMJkg);
        sulfurBox.Text = Num(std.MaxSulfurPct);
        weatherDerateBox.Text = Num(cfg.WeatherDeratePct);
        minPreparedBox.Text = Num(cfg.MinPreparedDays);
        effHoursBox.Text = Num(cfg.EffHoursPerDay);
        handoverBox.Text = Num(cfg.HandoverRampH);

        _loadedAsh = ashBox.Text!; _loadedCv = cvBox.Text!;
        _loadedSulfur = sulfurBox.Text!; _loadedDerate = weatherDerateBox.Text!;
        _loadedPrepared = minPreparedBox.Text!;
        _loadedEff = effHoursBox.Text!; _loadedHandover = handoverBox.Text!;

        loadDerateBox.Text = NumOpt(cfg.LoadDeratePct);
        haulDerateBox.Text = NumOpt(cfg.HaulDeratePct);
        dumpDerateBox.Text = NumOpt(cfg.DumpDeratePct);
        _loadedLoadD = loadDerateBox.Text!; _loadedHaulD = haulDerateBox.Text!; _loadedDumpD = dumpDerateBox.Text!;

        FillLinkDerateHint(cfg);
        FillSinkBlend(cfg);

        effHint.Text = a.EffHoursPerDay.HasValue
            ? "人工锚点（已保存）。清空并保存即回引擎缺省 20h"
            : "引擎缺省 20h。改动并保存后即成为人工锚点";
        handoverHint.Text = a.HandoverRampH.HasValue
            ? (cfg.HandoverRampH > 0.001
                ? $"人工锚点：非首班每班扣 {cfg.HandoverRampH:0.##}h"
                : "人工锚点：本矿不扣交接班（人工确认）")
            : "引擎缺省 0.5h/班";

        FillWindowEcho(cfg);

        rbBal.IsChecked = cfg.Organization == WorkOrganization.Balanced;
        rbConc.IsChecked = cfg.Organization == WorkOrganization.Concentrated;
        rbMulti.IsChecked = cfg.Organization == WorkOrganization.MultiFace;
        _loadedOrg = cfg.Organization;
        // 面日产能这个上限是配煤重分配与作业组织重分配共用的闸，闸门高度取自 ExploderConfig.EffHoursPerDay
        //（缺省 20h ≈ 三班扣检修/爆破/交接后的有效工时）。只读回显，不在这儿再开一个能改的口子。
        double effH = cfg.EffHoursPerDay;
        string capRule = $"面日产能上限 = 编组班产 × {effH:0.#} h × 天气系数 {cfg.WeatherFactor:0.00}";
        orgHint.Text = cfg.Organization == WorkOrganization.Balanced
            ? $"当前：均衡型 —— 各面当日目标按上游给的分配，装箱不做面间重分配。（{capRule}，配煤重分配同样受它约束）"
            : $"当前：{CompileOverrides.OrgLabel(cfg.Organization)} —— 装箱前会把各面当日目标重新灌一遍"
              + $"（总量不变，每面受「{capRule}」与「备采储量」双上限；备采未录的面不受备采限制）。"
              + "灌不下的余量会如实报出来，不塞回任何一个面。";

        // 备采校核有几个面吃得到：录了备采储量的才算数，一个都没录时说清楚，免得以为设了就生效
        int withReserve = cfg.Faces.Count(f => f.Process == ProcessType.Load && f.AvailableReserveM3 > 1e-6);
        int loadFaces = cfg.Faces.Count(f => f.Process == ProcessType.Load);
        preparedHint.Text = cfg.MinPreparedDays <= 1e-6
            ? "0 = 不校核"
            : withReserve == 0
                ? $"⚠ {loadFaces} 个采装面都没录备采量，这条现在一个面也管不到"
                : $"{withReserve}/{loadFaces} 个采装面录了备采量";

        blendHint.Text = a.HasBlend
            ? "当前三条是人工锚点（已保存，覆盖引擎缺省）。清空某一项并保存即撤回该项锚点。"
            : "当前三条是引擎缺省值。改动并保存后即成为人工锚点。";
        derateHint.Text = a.WeatherDeratePct.HasValue
            ? $"人工锚点：全盘班产 ×{cfg.WeatherFactor:0.00}"
            : "未锚定（引擎按 0 算）";
    }

    // ── 分环节降效 / 逐去向配煤 ────────────────────────────────────────────────

    private void FillLinkDerateHint(ExploderConfig cfg)
    {
        if (!cfg.HasLinkDerate)
        {
            linkDerateHint.Text = "三格留空 = 三个环节都跟随上面的全盘降效（与不分环节时逐位相同）。";
            return;
        }

        // 分环节时最要紧的一句话：有多少个面根本吃不到这个区分。
        // 缺 τ_L/T_c 的面只能退回全盘值 —— 不说的话，「我把运输降了 30%，怎么这几个面一点没变」永远解释不清。
        int loads = cfg.Faces.Count(f => f.Process == ProcessType.Load);
        int noCycle = cfg.Faces.Count(f => f.Process == ProcessType.Load && !f.Group.HasCycleBreakdown);
        linkDerateHint.Text =
            $"已分环节：采装 {cfg.LoadDeratePct ?? cfg.WeatherDeratePct:0.#}% · "
          + $"运输 {cfg.HaulDeratePct ?? cfg.WeatherDeratePct:0.#}% · "
          + $"排土 {cfg.DumpDeratePct ?? cfg.WeatherDeratePct:0.#}%。"
          + "逐面按其瓶颈侧折算（运输降效对采装瓶颈的面不生效）。"
          + (noCycle > 0
                ? $"　⚠ {noCycle}/{loads} 个采装面没有编组周期分解（τ_L/T_c），分不了环节，只能按全盘值降。"
                : loads > 0 ? $"　{loads} 个采装面均已解出周期分解。" : "");
    }

    /// <summary>
    /// 逐受矿点入仓标准表：行 = 去向登记簿里的**受矿类**去向（破碎站/煤仓/堆场）。
    /// 排土场不列 —— 那里不入仓，没有煤质指标可言。
    /// </summary>
    private void FillSinkBlend(ExploderConfig cfg)
    {
        var anchors = CompileOverrides.Current.SinkBlend;
        var rows = new List<SinkBlendRow>();

        // 今日各受矿点由哪些面供矿：把"这条标准管着谁"直接写在行上，
        // 否则填了半天不知道它今天到底作不作数。
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.Quality != null))
        {
            string k = OreSinkKeyOf(f);
            if (k.Length == 0) continue;
            if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = new List<string>();
            list.Add(f.Zone);
        }

        try
        {
            foreach (var s in cfg.Sinks.All.Where(x => !x.IsDumping).OrderBy(x => x.Name))
            {
                string key = string.IsNullOrWhiteSpace(s.Id) ? s.Name : s.Id;
                anchors.TryGetValue(key, out var a);
                var faces = byKey.TryGetValue(key, out var byId) ? byId
                          : byKey.TryGetValue(s.Name, out var byName) ? byName : null;

                rows.Add(new SinkBlendRow
                {
                    Key = key,
                    Name = s.Name,
                    KindLabel = s.Kind.Label(),
                    FromLedger = s.HasBlendLimits,
                    Ash = Opt(s.MaxAshPct ?? a?.MaxAshPct),
                    Cv = Opt(s.MinCalorificMJkg ?? a?.MinCalorificMJkg),
                    Sulfur = Opt(s.MaxSulfurPct ?? a?.MaxSulfurPct),
                    FaceNote = faces is { Count: > 0 }
                        ? $"{faces.Count} 个面：{string.Join("、", faces.Take(3))}{(faces.Count > 3 ? " 等" : "")}"
                        : "今日无面供矿",
                });
            }
        }
        catch { /* 去向登记簿不可用时留空表，下面那句提示会说清楚 */ }

        _sinkRows = rows;
        sinkBlendGrid.ItemsSource = null;
        sinkBlendGrid.ItemsSource = rows;

        // ★ 这一段刻意把"空"说清楚：去向登记簿是空表时，表里一行都没有，而全矿级那三条照常生效。
        int anchored = rows.Count(r => !r.FromLedger && (r.Ash.Length > 0 || r.Cv.Length > 0 || r.Sulfur.Length > 0));
        int fromLedger = rows.Count(r => r.FromLedger);
        sinkBlendHint.Text = rows.Count == 0
            ? "⚠ 去向登记簿里没有受矿类去向（破碎站/煤仓/堆场），本表为空 —— 配煤全部按上面的全矿级缺省判。"
            + "补法：在「排土场管理」/「去向台账」里录入受矿点，或在「破碎站位置设置」里定点。"
            : $"{rows.Count} 个受矿点；其中 {fromLedger} 个自带台账标准（不被锚点覆盖）、{anchored} 个由人工锚点给定，"
            + $"其余按全矿级缺省。配煤按受矿点分组判，量只在同组的作业面之间移动。";
    }

    /// <summary>本面的煤去哪（与 TaskExploder.OreSinkKey 同一口径：分项去向里第一个矿石物料的汇）。</summary>
    private static string OreSinkKeyOf(FaceInput f)
    {
        try
        {
            foreach (var (spec, _) in f.ResolvedMix.Split(Math.Max(f.DayTargetM3, 1)))
            {
                if (!spec.IsOre) continue;
                var d = f.DestinationFor(spec.Code);
                if (!string.IsNullOrWhiteSpace(d.DestinationId)) return d.DestinationId;
                if (!string.IsNullOrWhiteSpace(d.DestinationName)) return d.DestinationName;
            }
        }
        catch { }
        return "";
    }

    private static string Opt(double? v) => v.HasValue ? Num(v.Value) : "";
    private static string NumOpt(double? v) => v.HasValue ? Num(v.Value) : "";

    // ── 只读回显：规则的另一半 ────────────────────────────────────────────────
    //  纪律：一条切分规则，要么在这儿能改，要么在这儿能看见它现在取什么值、真源在哪。

    private void FillWindowEcho(ExploderConfig cfg)
    {
        var blasts = cfg.BlastWindows();

        // 当日有效工时：逐设备算（同一个 WorkWindowCalc，与装箱、逐日能力日历共用一处实现），
        // 报区间而不是单值——各设备的检修档期不同，一个平均数会把"有台设备今天全天定修"藏起来。
        var hrs = cfg.Faces
            .Where(f => f.Process is ProcessType.Load or ProcessType.Dump)
            .Select(f => WorkWindowCalc.DayHours(cfg.Shifts, f.Group.MainEquipment,
                                                 cfg.Maintenance, blasts, cfg.HandoverRampH))
            .OrderBy(h => h).ToList();
        double nominal = cfg.Shifts.Sum(sh => Math.Max(0, sh.End - sh.Start));

        dayHoursBox.Text = hrs.Count == 0
            ? "盘子里没有采装/排土面"
            : $"三班名义 {nominal:0.#}h → 逐设备实际可用 {hrs[0]:0.#}–{hrs[^1]:0.#}h"
              + $"（中位 {hrs[hrs.Count / 2]:0.#}h，已扣检修/爆破/交接）"
              + (hrs[0] < 0.5 ? $"　⚠ 有 {hrs.Count(h => h < 0.5)} 台设备当日开不了工" : "");

        int mw = cfg.Maintenance.Count;
        downtimeBox.Text = mw == 0 && blasts.Count == 0
            ? "本日无检修档期、无爆破清场（真源：设备检修台账 / blast_event）"
            : $"检修 {mw} 档 · 爆破清场 {blasts.Count} 段（真源：设备检修台账 / blast_event，在各自台账里改）";

        // 运力告警：只回显不开可改口子
        truckRuleBox.Text = "配车数 < 荐车数 n* 即报「运力不足」（n* 由 τ_L / T_c 解出）";
        int loadFaces = cfg.Faces.Count(f => f.Process == ProcessType.Load);
        int shortTruck = cfg.Faces.Count(f => f.Process == ProcessType.Load
                                           && f.Group.Trucks.Count < f.Group.RecommendedTrucks);
        truckRuleHint.Text =
            $"当前 {shortTruck}/{loadFaces} 个采装面会触发。此处刻意不设阈值：荐车数是求解出来的物理量，"
            + "在这儿再设一个就是与求解打架的第二套口径（同「班产上限」被删的原因）。要调它，调编组规则台账里的设备参数。";

        FillBudget(cfg);
    }

    // ── 当日能力预算 ──────────────────────────────────────────────────────────
    //  把旋钮统一翻译成 m³，用的就是装箱那笔账（DayCapacityBudget 与 ExplodeFace 逐字对齐）。

    private void FillBudget(ExploderConfig cfg)
    {
        CapacityLine load, dump;
        try { (load, dump) = DayCapacityBudget.Of(cfg); }
        catch (Exception ex)
        {
            budgetHeadline.Text = $"能力预算算不出来（{Short(ex)}）";
            budgetHeadline.Foreground = WarnBrush;
            budgetCost.Text = budgetNote.Text = "";
            return;
        }

        bool reshuffled = cfg.Organization != WorkOrganization.Balanced;
        budgetHeadline.Text = DayCapacityBudget.Headline(load, reshuffled);
        budgetHeadline.Foreground = !load.HasAny ? WarnBrush : load.ShortM3 > 1 ? BadBrush : OkBrush;

        SetBar(loadFitCol, loadShortCol, loadBarText, loadStat, load, "m³实方");
        SetBar(dumpFitCol, dumpShortCol, dumpBarText, dumpStat, dump, "m³占容");

        // 三项代价各按「其余条件不变」算，故**不可相加**成一个总数（那会重复计价）
        var costs = new List<string>();
        if (load.WeatherCostM3 > 1) costs.Add($"天气降效 −{load.WeatherCostM3:N0}");
        if (load.HandoverCostM3 > 1) costs.Add($"交接班 −{load.HandoverCostM3:N0}");
        if (load.DowntimeCostM3 > 1) costs.Add($"检修+爆破 −{load.DowntimeCostM3:N0}");
        budgetCost.Text = costs.Count == 0
            ? "当前无降效、无交接扣减、无检修/爆破：采装能力就是三班满开的能力。"
            : "旋钮代价（采装口径 m³实方，各按「其余条件不变」算，不可相加）：" + string.Join(" · ", costs);

        var notes = new List<string>();
        if (load.NoWindowFaces > 0) notes.Add($"⚠ {load.NoWindowFaces} 个采装面当日一小时也开不了工（时窗被检修/爆破吃光）");
        if (load.IdleFaces > 0) notes.Add($"{load.IdleFaces} 个采装面今天没量但有能力（装箱会挂「空闲」条）");
        if (dump.NoWindowFaces > 0) notes.Add($"⚠ {dump.NoWindowFaces} 个排土面当日开不了工");
        notes.Add("排土目标由采装入方按 Kr 折算推导，装箱时才定；此处显示的是盘子里现有的值。");
        budgetNote.Text = string.Join("　", notes);
    }

    /// <summary>
    /// 条形 = 当日目标，按<b>逐面</b>拆成「排得下」（绿）与「欠」（红）两段。
    /// <para>★ <b>刻意不画「总目标 ÷ 总能力」</b>：那个比例会骗人——能力不在面之间流动，
    /// 总账平不代表排得下。视觉与结论必须不可能打架：只要有一个面欠，这条就一定见红。</para>
    /// <para>闲置能力不进条形：它与"目标排不排得下"不是同一根轴。</para>
    /// </summary>
    private static void SetBar(ColumnDefinition fitCol, ColumnDefinition shortCol,
                               TextBlock inner, TextBlock stat, CapacityLine line, string unit)
    {
        if (!line.HasAny || line.TargetM3 <= 1)
        {
            fitCol.Width = new GridLength(1, GridUnitType.Star);
            shortCol.Width = new GridLength(0, GridUnitType.Star);
            inner.Text = "";
            stat.Text = !line.HasAny ? "无此工序作业面"
                      : $"当日无目标（能力 {line.CapacityM3:N0} {unit} 全闲）";
            return;
        }

        double fit = Math.Max(0, line.TargetM3 - line.ShortM3);
        fitCol.Width = new GridLength(fit, GridUnitType.Star);
        shortCol.Width = new GridLength(Math.Max(0, line.ShortM3), GridUnitType.Star);

        inner.Text = line.ShortM3 > 1
            ? $"排得下 {fit / line.TargetM3 * 100:0.#}%"
            : "全部排得下";

        stat.Text = $"目标 {line.TargetM3:N0} / 能力 {line.CapacityM3:N0} {unit}"
                  + (line.ShortM3 > 1 ? $"　欠 {line.ShortM3:N0}" : "")
                  + (line.SlackM3 > 1 ? $"　闲 {line.SlackM3:N0}" : "");
    }

    private void OnSave()
    {
        sinkBlendGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        sinkBlendGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var a = CompileOverrides.Current.Clone();
        var bad = new List<string>();

        if (Changed(ashBox.Text, _loadedAsh) || a.MaxAshPct.HasValue)
            a.MaxAshPct = ParseOpt(ashBox.Text, "灰分上限", bad);
        if (Changed(cvBox.Text, _loadedCv) || a.MinCalorificMJkg.HasValue)
            a.MinCalorificMJkg = ParseOpt(cvBox.Text, "热值下限", bad);
        if (Changed(sulfurBox.Text, _loadedSulfur) || a.MaxSulfurPct.HasValue)
            a.MaxSulfurPct = ParseOpt(sulfurBox.Text, "硫分上限", bad);
        if (Changed(weatherDerateBox.Text, _loadedDerate) || a.WeatherDeratePct.HasValue)
        {
            double? d = ParseOpt(weatherDerateBox.Text, "天气降效", bad);
            if (d is < 0 or > 95) { bad.Add("天气降效须在 0–95% 之间"); d = null; }
            a.WeatherDeratePct = d;
        }
        if (Changed(minPreparedBox.Text, _loadedPrepared) || a.MinPreparedDays.HasValue)
        {
            double? mp = ParseOpt(minPreparedBox.Text, "备采保有下限", bad);
            if (mp is < 0) { bad.Add("备采保有下限不能为负"); mp = null; }
            a.MinPreparedDays = mp;
        }

        if (Changed(effHoursBox.Text, _loadedEff) || a.EffHoursPerDay.HasValue)
        {
            double? eh = ParseOpt(effHoursBox.Text, "面日产能工时", bad);
            // 上界 24：它是"一天里能干几小时"，>24 不是配置而是笔误；下界 0.5 与装箱的 avail≥0.5 门槛对齐
            if (eh is < 0.5 or > 24) { bad.Add("面日产能工时须在 0.5–24 h 之间"); eh = null; }
            a.EffHoursPerDay = eh;
        }
        if (Changed(handoverBox.Text, _loadedHandover) || a.HandoverRampH.HasValue)
        {
            double? hr = ParseOpt(handoverBox.Text, "交接班损失", bad);
            // 上界 4：交接扣的是每个非首班的班头，超过 4h 等于把一个班整个抹掉——那该在班次日历里改
            if (hr is < 0 or > 4) { bad.Add("交接班损失须在 0–4 h 之间"); hr = null; }
            a.HandoverRampH = hr;
        }

        // 分环节降效：空 = 撤回该环节的锚点（回到跟随全盘值），与 0 是两回事
        a.LoadDeratePct = LinkDerate(loadDerateBox.Text, _loadedLoadD, a.LoadDeratePct, "采装降效", bad);
        a.HaulDeratePct = LinkDerate(haulDerateBox.Text, _loadedHaulD, a.HaulDeratePct, "运输降效", bad);
        a.DumpDeratePct = LinkDerate(dumpDerateBox.Text, _loadedDumpD, a.DumpDeratePct, "排土降效", bad);

        // 逐受矿点入仓标准：只收**人在这张表上填过**的行；台账自带标准的行一律不写锚点
        var sinkBlend = new Dictionary<string, SinkBlendAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _sinkRows)
        {
            if (r.FromLedger) continue;
            var lim = new SinkBlendAnchor
            {
                MaxAshPct = ParseOptCell(r.Ash, $"{r.Name} 灰分上限", bad),
                MinCalorificMJkg = ParseOptCell(r.Cv, $"{r.Name} 热值下限", bad),
                MaxSulfurPct = ParseOptCell(r.Sulfur, $"{r.Name} 硫分上限", bad),
            };
            if (!lim.IsEmpty) sinkBlend[r.Key] = lim;
        }
        a.SinkBlend = sinkBlend;

        var org = rbConc.IsChecked == true ? WorkOrganization.Concentrated
                : rbMulti.IsChecked == true ? WorkOrganization.MultiFace
                : WorkOrganization.Balanced;
        if (org != _loadedOrg || !string.IsNullOrWhiteSpace(a.Organization))
            a.Organization = org.ToString();

        if (bad.Count > 0)
        {
            toolStatus.Text = "未保存：" + string.Join("；", bad) + "（其余项未受影响）";
            return;
        }

        if (!CompileOverrides.Save(a))
        {
            toolStatus.Text = $"保存失败：{CompileOverrides.LastIoLabel}";
            return;
        }

        toolStatus.Text = a.IsEmpty
            ? $"锚点已全部撤回，装箱回引擎缺省　|　{CompileOverrides.LastIoLabel}"
            : $"已保存并立即生效（下次装箱按此算）　|　{CompileOverrides.LastIoLabel}";
        Fill();
    }

    private void OnReset()
    {
        if (!CompileOverrides.Save(new CompileAnchors()))
        {
            toolStatus.Text = $"重置失败：{CompileOverrides.LastIoLabel}";
            return;
        }
        Fill();
        toolStatus.Text = "已撤回全部人工锚点：配煤标准（含逐去向）/ 天气降效（含分环节）/ 有效工时 / 交接班损失 / 备采下限 / 作业组织 全回引擎缺省";
    }

    /// <summary>
    /// 一个环节降效格的取值：没动过且原先没锚点 → 保持 null（跟随全盘）；
    /// 清空 → null（撤回该环节的锚点）；填了数 → 校验范围后作为锚点。
    /// </summary>
    private static double? LinkDerate(string? now, string loaded, double? current, string field, List<string> bad)
    {
        if (!Changed(now, loaded) && !current.HasValue) return null;
        double? v = ParseOpt(now, field, bad);
        if (v is < 0 or > 95) { bad.Add($"{field}须在 0–95% 之间"); return current; }
        return v;
    }

    /// <summary>表格单元的可空数值：空串 = 该项按全矿级缺省，解不出记一条错误。</summary>
    private static double? ParseOptCell(string text, string field, List<string> bad)
    {
        double? v = ParseOpt(text, field, bad);
        if (v is < 0 or > 100) { bad.Add($"{field}「{text}」超出 0–100 的合理范围"); return null; }
        return v;
    }

    private static bool Changed(string? now, string? loaded)
        => !string.Equals((now ?? "").Trim(), (loaded ?? "").Trim(), StringComparison.Ordinal);

    /// <summary>空串 = 撤回该项锚点（返回 null）；解不出记一条错误，返回 null 但调用方会整体拒绝保存。</summary>
    private static double? ParseOpt(string? text, string field, List<string> bad)
    {
        string v = (text ?? "").Trim();
        if (v.Length == 0) return null;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
            return d;
        bad.Add($"{field}「{v}」不是数字");
        return null;
    }

    private static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private static string SafeFleetLabel()
    {
        try { return FleetMatcher.LastSourceLabel ?? ""; }
        catch { return ""; }
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
