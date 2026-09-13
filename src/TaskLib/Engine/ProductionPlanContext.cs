// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ProductionPlanContext.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data;              // EquipmentCategory / EquipmentStatus
using PitMine3D.Kylin.Data.Entities;     // ShiftCalendar / BlastEvent / Equipment
using PitMine3D.Kylin.TaskLib.Domain;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  当日盘子（ExploderConfig）的唯一装配点。
//
//  在它出现之前，全仓唯一的 `new ExploderConfig` 在 SampleTaskBoard 里，作业面清单、
//  班次、爆破窗口、编组全是硬编码样例；台账窗口从数据库读回来的去向/运距/日目标
//  只在那一个窗口内生效，甘特、任务书、派车单、报表、达成评价看到的仍是样例——
//  同一天的去向在两个窗口能显示成两个样。本类把源头改成【台账优先，样例兜底】，
//  并把「哪一段数据是从哪来的」如实写进来源文案。
//
//  装配顺序（不可颠倒，每一步都是下一步的输入）：
//    ① 期次    ← IProjectContext（矿 / 作业日 / 计划期；未接线时用样例种子日）
//    ② 班次    ← shift_calendar        缺 → 样例三班
//    ③ 爆破窗口 ← blast_event.blast_time 缺 → 样例窗口
//    ④ 作业面  ← working_face_routing   缺 → 样例种子面（并合并已存档案）
//    ⑤ 去向    ← dump_site / load_unload_point / sink_profile（SinkRegistryLoader 内部已两层兜底）
//    ⑥ 月计划  → 各面当日目标（ShortTermLink 软读 PlanLib 已确定方案）
//    ⑦ 定去向  → FlowAssigner（运输功最小；手工指定的面锁定不动）
//    ⑧ 定运距  → HaulResolver（路网 → 手填 → 汇兜底）
//    ⑨ 定编组  → FleetMatcher（T_c → n* → 编组班产 = 装箱的 bin）
//
//  实绩两条路，**绝不混用**：
//    · 盘子来自台账 → 只回灌 actuals/ 里**真实录入**的实绩，没录就保持"计划"状态；
//    · 盘子来自样例 → 用样例合成实绩（演示用，SampleTaskBoard 自己的老行为）。
//  在真实台账上合成实绩就是编数据，那正是这套系统最不该做的事。
// ─────────────────────────────────────────────────────────────────────────────

public static class ProductionPlanContext
{
    /// <summary>
    /// 爆破后的清场/警戒解除时长 h（台账只记爆破时刻，不记恢复作业时刻）。★工程缺省。
    /// 口径收在 <see cref="BlastPlanLink.ClearanceH"/>：这里扣掉多少，「钻爆计划衔接」
    /// 那张表就得显示多少，两处各写一个常数迟早会对不上。
    /// </summary>
    private const double BlastClearanceH = BlastPlanLink.ClearanceH;

    private static ExploderResult? _result;
    private static bool _hooked;

    // 最近一次装配的派生值（窗口按需读；首次访问会触发一次装配，避免拿到零值）
    private static double _blastStart = SampleTaskBoard.SeedBlastStart;
    private static double _blastEnd = SampleTaskBoard.SeedBlastEnd;
    private static List<BlastWindow> _blasts = new();
    private static double _nowHour = SampleTaskBoard.SeedNowHour;
    private static List<ShiftWindow> _shifts = new();
    private static bool _assembled;

    /// <summary>本次盘子的作业面是不是来自台账（false = 样例种子面）。决定实绩走真值还是样例。</summary>
    public static bool FacesFromLedger { get; private set; }

    /// <summary>作业面这一盘是<b>哪一层</b>来的。文案会骗人（三层的文案都以"作业面："开头），这个不会。</summary>
    public enum FaceOriginKind
    {
        /// <summary>
        /// <b>没有作业面</b> —— 台账空、本期也没有工序作业区。这一盘计划排不出来。
        /// <para>2026-08-18 之前这一档是「样例盘子」：缺数据时补一盘示例露天矿的面。
        /// 那份假盘子长得和真盘子一模一样，人分不出自己看的是哪个矿 —— 已经去掉。</para>
        /// </summary>
        None = 0,
        /// <summary>样例盘子。<b>已停用</b>，保留枚举值只为老快照读得回来。</summary>
        Sample = 9,
        /// <summary>作业面台账（working_face_routing）。</summary>
        Ledger = 1,
        /// <summary>本期工序作业区派生（月计划 → 剥采清单 → 块段 → 工序区 → 面）。</summary>
        Derived = 2,
        /// <summary>台账为主，工序区补了台账里没有的面。</summary>
        LedgerPlusDerived = 3,
    }

    /// <summary>本盘作业面的来源层。</summary>
    public static FaceOriginKind FaceOrigin { get; private set; } = FaceOriginKind.Sample;

    /// <summary>这一盘面是不是真实数据。<b>「没有」与「样例」都不算</b>。</summary>
    public static bool FacesAreReal =>
        FaceOrigin is FaceOriginKind.Ledger or FaceOriginKind.Derived or FaceOriginKind.LedgerPlusDerived;

    /// <summary>一个作业面都没有 —— 这一盘计划排不出来（与"有面但是样例"是两回事）。</summary>
    public static bool NoFaces => FaceOrigin == FaceOriginKind.None;

    /// <summary>装配过程里要人知道的事（不构成校核违规，但不能只写在某个状态栏上）。</summary>
    private static readonly List<string> _assemblyNotes = new();

    /// <summary>
    /// 一段链路的状态。<b>由装配现场直接记下来，不是从来源文案里正则抠出来的</b> ——
    /// 文案一改就静默抠空，而抠空之后"样例"会被当成"真实"，体检整条失去意义。
    /// </summary>
    public enum ChainState
    {
        /// <summary>没这段数据，而且没有兜底 —— 下游按"本日没有"处理。</summary>
        Missing = 0,
        /// <summary><b>吃的是样例</b>（示例露天矿的数据，不是这个矿的）。</summary>
        Sample = 1,
        /// <summary>真实数据，但只有一部分（另一部分走了兜底）。</summary>
        Partial = 2,
        /// <summary>真实数据。</summary>
        Real = 3,
        /// <summary>这一段没有独立的来源标志，只有文案 —— <b>不猜</b>。</summary>
        Unknown = 4,
    }

    private static readonly Dictionary<string, ChainState> _chain = new(StringComparer.Ordinal);

    /// <summary>逐段状态（键 = 段名）。<see cref="Config"/> 跑过一次之后才有内容。</summary>
    public static IReadOnlyDictionary<string, ChainState> ChainStates => _chain;

    private static void Mark(string stage, ChainState st) => _chain[stage] = st;

    /// <summary>
    /// 工程缺省三班：早 00–08 / 中 08–16 / 夜 16–24。
    /// <para><b>它是参数，不是样例数据</b> —— 不携带任何这个矿特有的信息。
    /// 用它排出来的计划在"谁、在哪、干多少"上仍然全是真的，只有"分几班干"是缺省的，
    /// 而这一点由 <see cref="ChainState.Partial"/> 与装配备注同时标出来。</para>
    /// </summary>
    private static List<ShiftWindow> DefaultThreeShifts() => new()
    {
        new ShiftWindow("早", 0, 8),
        new ShiftWindow("中", 8, 16),
        new ShiftWindow("夜", 16, 24),
    };

    /// <summary>最近一次装配的备注（界面直接显示）。</summary>
    public static IReadOnlyList<string> AssemblyNotes => _assemblyNotes;

    /// <summary>
    /// 装配期产生的校核（作业日口径、剥离分摊依据…）。
    /// <para>
    /// 这些是**装箱之前**就已成立的口径提示，原先随手丢了：<c>ShortTermLink.ApplyToConfig</c>
    /// 的 violations 形参从来没人传，于是"日目标是按哪个作业日摊出来的"这类信息在界面上
    /// 一个字都看不到。现在存这儿，装箱时并进 <see cref="ExploderResult.Violations"/>，
    /// 与装箱期校核一起显示在「计划校核」面板。
    /// </para>
    /// </summary>
    private static readonly List<PlanViolation> _assemblyViolations = new();

    // ── 分段来源文案（UI 显示「这盘数据可不可信」）──
    public static string PeriodSourceLabel { get; private set; } = "";
    public static string ShiftSourceLabel { get; private set; } = "";
    public static string BlastSourceLabel { get; private set; } = "";
    public static string FaceSourceLabel { get; private set; } = "";
    public static string PlanSourceLabel { get; private set; } = "";
    public static string SinkSourceLabel { get; private set; } = "";
    public static string HaulSourceLabel { get; private set; } = "";
    public static string FleetSourceLabel { get; private set; } = "";
    public static string ActualSourceLabel { get; private set; } = "";

    /// <summary>单据回灌来源文案（已下达 / 已撤回 / **已排未下达** 各多少条）。</summary>
    public static string DispatchSourceLabel { get; private set; } = "";

    /// <summary>本盘的单据计数。评价三窗的分母口径按它报数，别各窗各算一遍。</summary>
    public static DispatchStateLink.Stat DispatchStat { get; private set; }

    /// <summary>编制人工锚点（配煤标准 / 天气降效）的来源文案；没设过锚点时为空串。</summary>
    public static string AnchorSourceLabel { get; private set; } = "";

    /// <summary>主设备可用性校核的来源文案（报废/检修/不在台账各几个）。</summary>
    public static string EquipSourceLabel { get; private set; } = "";

    /// <summary>采掘单元对号的来源文案（几个面绑上了 / 号对不上 / 没有基表）。</summary>
    public static string UnitSourceLabel { get; private set; } = "";

    /// <summary>检修档期的来源文案（本日几条 / 台账没有）。</summary>
    public static string MaintenanceSourceLabel { get; private set; } = "";

    /// <summary>穿孔作业计划(V044)的来源文案（本日几条 / 表里没有）。</summary>
    public static string DrillSourceLabel { get; private set; } = "";

    /// <summary>盘子来源总文案（期次 · 班次 · 作业面 · 月计划 · 去向 · 运距 · 编组）。</summary>
    public static string SourceLabel { get; private set; } = "";

    // ── 期次（全部转发项目上下文，未接线时回落样例种子）──
    public static string MineName => ProjectScope.MineName;
    public static string DateLabel => ProjectScope.DateLabel;
    public static string IdPrefix => ProjectScope.IdPrefix;

    /// <summary>
    /// 「此刻」几点（甘特现在线 / 达成度的应完成量 / 重排起点都用它）。
    /// <b>台账盘子</b>取真实时钟（作业日是今天时），<b>样例种子盘子</b>固定在样例时刻——
    /// 演示盘子必须可复现：真实时钟一到深夜，样例里所有任务都会变成"已完成"，
    /// 精心设计的「WK-10 中班故障 + 运力不足」这条演示线就再也走不到了。
    /// <para>
    /// ★ 台账盘子必须<b>每次读都重新取钟</b>，不能返回装配时的快照 <c>_nowHour</c>：
    /// 那个值只有 <see cref="Invalidate"/> 之后才会变，于是整场停在装配那一刻——
    /// 调度看板点刷新时间不动是小事，真正致命的是「设备状态·故障报修」：
    /// 报修与复机读到同一个时刻，<c>FaultEvent.EndHour == StartHour</c>，
    /// 停机时长 <c>DurationHours = EndHour − StartHour</c> 恒等于 0，
    /// 实绩录入按时段汇总出来的故障工时也就全线归零。
    /// </para>
    /// </summary>
    public static double NowHour
    {
        get
        {
            EnsureAssembled();
            // 样例盘子照旧返回冻结的样例时刻（演示可复现）；台账盘子走活钟。
            // ProjectScope.NowHour 自带"作业日不是今天就回落样例时刻"的判定，故排过去/未来某天时也稳。
            return FacesFromLedger ? ProjectScope.NowHour : _nowHour;
        }
    }

    /// <summary>
    /// 当日班制（班次日历口径，读不到时是样例种子三班）。界面的班次下拉框与「当前班」判定
    /// 一律经 <see cref="ShiftScope"/> 读它——别再在各窗 XAML 里各写一份「早/中/夜」。
    /// </summary>
    public static IReadOnlyList<ShiftWindow> Shifts { get { EnsureAssembled(); return _shifts; } }

    /// <summary>本日<b>最早一炮</b>的停产窗口起（兼容视图；全部时窗见 <see cref="BlastWindowsOfDay"/>）。</summary>
    public static double BlastStart { get { EnsureAssembled(); return _blastStart; } }
    /// <summary>本日最早一炮的停产窗口止（含清场）。</summary>
    public static double BlastEnd { get { EnsureAssembled(); return _blastEnd; } }

    /// <summary>
    /// 本日<b>全部</b>爆破停产时窗（甘特爆破带、钻爆衔接、任何"这个时段能不能干活"的判断都用它）。
    /// 只读 <see cref="BlastStart"/>/<see cref="BlastEnd"/> 会漏掉第二炮起的全部清场。
    /// </summary>
    public static IReadOnlyList<BlastWindow> BlastWindowsOfDay
    {
        get { EnsureAssembled(); return _blasts; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  对外主接口
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>裂解装箱结果（缓存；换矿/换作业日或显式 <see cref="Invalidate"/> 后重算）。</summary>
    public static ExploderResult Result() => _result ??= BuildAndApply();

    public static List<ProductionTask> Day() => Result().Tasks;

    public static List<PlanViolation> Violations() => Result().Violations;

    /// <summary>注入外部计划（载入快照 / 落库回读）→ 后续 Day()/Violations() 即返回该计划。</summary>
    public static void SetSnapshot(ExploderResult r) => _result = r;

    /// <summary>丢弃缓存（台账改过、期次变了、实绩重录后调用）。</summary>
    public static void Invalidate() => _result = null;

    /// <summary>
    /// 只重跑**单据回灌**，不重排计划。
    ///
    /// <para>下达 / 撤回之后必须调它：盘子是缓存的（<c>_result</c>），
    /// 不重跑回灌，刚落盘的单据要等下一次 <see cref="Invalidate"/> 才进得来 ——
    /// 而那可能是几个窗口之后的事，中间所有窗看到的都是旧单据状态。
    /// 用 <see cref="Invalidate"/> 也能达到目的，但那是整盘重排（分解器 + 校核），
    /// 为了一次签发把计划重算一遍，还可能因为台账变了排出**另一份**计划。</para>
    /// </summary>
    public static void RefreshDispatch()
    {
        if (_result == null) return;                 // 还没建过盘子，下次建时自然会回灌
        ReflowDispatch(_result.Tasks);
        DispatchStateLink.CheckConflicts(_result.Tasks, _result);
    }

    /// <summary>
    /// 装配一份当日盘子。每次调用都重新装配（与旧 <c>SampleTaskBoard.Config()</c> 语义一致）：
    /// 台账随时可能被别的窗口改，缓存 cfg 会让"改完看不见"重新长出来。
    /// </summary>
    public static ExploderConfig Config()
    {
        EnsureHook();
        _assemblyViolations.Clear();
        _assemblyNotes.Clear();
        _chain.Clear();

        var date = ProjectScope.WorkDate;

        // ★ 2026-08-18：**样例不再兜底**。缺数据就是缺数据 ——
        //   编一份看着正常的假盘子（有面、有量、有编组、有甘特、有达成度，每个数都自洽）
        //   比空着更危险：人分不出自己看的是这个矿还是「示例露天矿」。
        //   下面每一处原来 `?? seed.X` 的地方，现在一律「留空 + 记账 + 给补法」。
        //
        //   ⚠ 这四项不是样例数据，是**工程缺省常数**（交接班坡道时长 / 配煤标准 / 采排守恒开关）：
        //     它们没有"真源"可言，人工锚点会在 ①.5 覆盖掉配煤那一项。所以照旧给缺省值。
        var cfg = new ExploderConfig
        {
            DateLabel = ProjectScope.DateLabel,
            IdPrefix = ProjectScope.IdPrefix,
            NowHour = ProjectScope.NowHour,
            // 交接班坡道时长 / 采排守恒开关 / 配煤标准都在 ExploderConfig 上有**工程缺省**，
            // 不必也不该从样例种子里抄一份 —— 配煤那一项会被 ①.5 的人工锚点覆盖。
        };

        // ── ①.5 人工锚点（配煤标准 / 天气降效）──
        // 放在最前：配煤标准要在装箱前生效，天气降效要在所有面的能力上生效。
        // 没设过锚点返回空串，引擎缺省原样保留。
        AnchorSourceLabel = SafeCall(() => CompileOverrides.ApplyTo(cfg), "编制锚点");

        Mark("期次", ProjectScope.Connected ? ChainState.Real : ChainState.Sample);
        PeriodSourceLabel = ProjectScope.Connected
            ? $"期次：{ProjectScope.MineName} · {ProjectScope.DateLabel}"
            : $"期次：{ProjectScope.MineName} · {ProjectScope.DateLabel}（项目上下文未接线，按样例期次）";

        // ── ② 班次 ──
        var shifts = LoadShifts(date);
        if (shifts is { Count: > 0 })
        {
            Mark("班次", ChainState.Real);
            cfg.Shifts.AddRange(shifts);
        }
        else
        {
            // ★ 班制**可以**兜底，作业面**不可以** —— 判别标准是
            //   「这个兜底会不会让人误以为看到的是真数据」：
            //     · 作业面 / 量 / 位置 / 爆破时窗 / 检修档期 —— 编的是**具体事件**，
            //       编出来人分不清真假 ⇒ 一律不兜。
            //     · 早/中/夜 00–08/08–16/16–24 —— 是**工程缺省参数**，
            //       不携带任何这个矿特有的信息（跟交接班坡道 0.5h 同类）⇒ 可以兜。
            //   2026-08-18 第一版把班制也一起切了，于是"日历没排"就等于"什么都排不出来" ——
            //   那是矫枉过正：三班就是三班，不排它反而挡住了整条链。
            //   但**必须标明是缺省不是日历排的**，且状态记 Partial 而不是 Real。
            cfg.Shifts.AddRange(DefaultThreeShifts());
            Mark("班次", ChainState.Partial);
            _assemblyNotes.Add("· 班次日历里本日一条都没有，已按**工程缺省三班**（早 00–08 / 中 08–16 / 夜 16–24）排。"
                             + "**这不是日历里排的班** —— 实际班制不是三班八小时、或有爆破班/停产班时，"
                             + "算出来的有效时窗与班产都会偏。"
                             + "补法：到「基础数据 · 班次日历」把本月的班排出来。");
        }
        // 界面侧的班次口径（下拉框取值域 / 当前班判定）一律从这儿取，见 ShiftScope。
        // 原先 8 个窗口各在 XAML 里硬编码「早/中/夜」，日历改了班制界面全然不知。
        _shifts = new List<ShiftWindow>(cfg.Shifts);

        // ── ③ 爆破停产时窗（逐炮，不再只取最早一炮）──
        var blasts = LoadBlastWindows(date);
        Mark("爆破时窗", blasts is { Count: > 0 } ? ChainState.Real : ChainState.Sample);
        if (blasts is { Count: > 0 }) cfg.SetBlasts(blasts);
        _blastStart = cfg.BlastStart;
        _blastEnd = cfg.BlastEnd;
        _blasts = new List<BlastWindow>(cfg.BlastWindows());

        // ── ③.2 穿孔作业计划（V044）──
        // 工序链的第一环。台账有就用台账；没有时**只有样例盘子**才回落样例穿孔（见 ④）。
        var drills = LoadDrills(date);
        Mark("穿孔计划", drills is { Count: > 0 } ? ChainState.Real : ChainState.Missing);
        if (drills is { Count: > 0 }) cfg.Drills.AddRange(drills);

        // ── ③.5 检修档期（V042）──
        // 有效时窗 = 班起 − 检修 − 爆破清场 − 交接班损失。三个减项里检修此前**没有表**，
        // 于是台账模式下这一项恒为 0：一台今天上午定修的电铲，计划里从 0 点起就在满负荷干活。
        var maint = LoadMaintenance(date);
        Mark("检修档期", maint is { Count: > 0 } ? ChainState.Real : ChainState.Missing);
        if (maint is { Count: > 0 }) cfg.Maintenance.AddRange(maint);

        // ── ⓪ 去向自动重建（规划链第 0 步，2026-08-20）──
        //
        //   ★ **必须排在作业面派生（④）之前**：排土面要靠去向登记簿绑场
        //     （`BindDumpSink`），而登记簿是从 dump_site 装的。放在 ⑤ 里就晚了一拍 ——
        //     本次装配的排土面全部「去向未定」⇒ 运到场的排弃量没有面接
        //     ⇒ 一条排土条都排不出来，要等下一次装配才对，而那次也许根本不会发生。
        //     实测：放在 ⑤ 时排土 0 笔，提到这里就是 51 笔。
        //
        //   排土场清单是**算得出来的**（采掘单元台账里 2112 个排土位置按场名分组），
        //   库里一个场都没有时才建，人建过/改过的一律不碰。
        int autoSinks = SafeInt(() => SinkAutoRebuild.EnsureSinks(), 0);
        if (!string.IsNullOrWhiteSpace(SinkAutoRebuild.LastLabel))
            _assemblyNotes.Add("· " + SinkAutoRebuild.LastLabel);
        foreach (var note in SinkAutoRebuild.LastNotes) _assemblyNotes.Add(note);

        // ── ④ 作业面（三层：作业面台账 → 本期工序作业区派生 → 样例）──
        //
        //  中间那一层是 2026-08-18 补的，它之前是**断的**：链路本该是
        //  月度计划 → 剥采清单 → 块段模型 → 作业区划分 → 任务编制，
        //  可这里只认 working_face_routing 一张表。那张表空着（新工程、或还没在
        //  「作业面台账」里保存过一次）时，整盘面**整体落到样例** ——
        //  月计划排得再细，任务编制里排的仍是「主采面·东」这种示例露天矿的面，
        //  而界面上只有一行小字说了这件事。
        //
        //  ★ F2 补齐不顶替：台账里已有的面原样保留（去向/运距/设备/煤质都是人填的资产），
        //    派生只补台账里没有、而本期确实有工序区的那些面。反过来做会把人填的冲掉且不报错。
        var faces = FaceLedgerLoader.LoadFaces();
        var derived = SafeDerive();
        if (faces is { Count: > 0 })
        {
            cfg.Faces.AddRange(faces);
            int added = MergeDerivedFaces(cfg, derived);
            FacesFromLedger = true;
            FaceOrigin = added > 0 ? FaceOriginKind.LedgerPlusDerived : FaceOriginKind.Ledger;
            FaceSourceLabel = FaceLedgerLoader.LastSourceLabel
                            + (added > 0
                                ? $"　＋ 本期工序作业区补了 {added} 个面（台账里没有它们）"
                                : "");
            foreach (var n in ProcessZoneFaceSource.LastNotes) _assemblyNotes.Add(n);
            // 穿孔计划自 V044 起有了 drill_plan 表（见 ③.2），检修自 V042 起有 maintenance_window（见 ③.5）：
            // 台账模式下这两类都只从表里来，读不到就是本日没有，不拿样例编。
        }
        else if (derived is { Count: > 0 })
        {
            // ★ 这是**真实数据**，不是样例：面来自本期工序作业区，而工序区来自月计划排出的剥采清单。
            //   所以 FacesFromLedger = true —— 时刻走真实工作日期、实绩走持久化那条，
            //   都与"样例盘子"完全不同。
            cfg.Faces.AddRange(derived);
            FacesFromLedger = true;
            FaceOrigin = FaceOriginKind.Derived;
            // 台账里保存过的去向/运距/煤质仍然生效（人可能先在作业面台账上填过零散几项）
            string merged = FaceLedgerLoader.ApplySaved(cfg, drafts: null, resolveAfter: false);
            FaceSourceLabel = ProcessZoneFaceSource.LastSourceLabel
                            + (string.IsNullOrWhiteSpace(merged) ? "" : " · " + merged);
            foreach (var n in ProcessZoneFaceSource.LastNotes) _assemblyNotes.Add(n);
            _assemblyNotes.Add("· 作业面是**从本期工序作业区派生**的，不是「作业面台账」里的档案 —— "
                             + "去向、运距、主设备、煤质目标这些都还没有人填。"
                             + "在「作业面台账」保存一次即可把它们固化成档案，之后派生只补新面。");
        }
        else
        {
            // ★ 第三层不再是「样例」，是「没有」。
            //   一个作业面都没有 ⇒ 这一盘计划**排不出来**，而不是排出一份别的矿的计划。
            FaceOrigin = FaceOriginKind.None;
            FacesFromLedger = false;
            FaceSourceLabel = FaceLedgerLoader.LastSourceLabel;

            _assemblyNotes.Add("◆◆ **一个作业面都没有 —— 这一盘计划排不出来。** "
                             + "此前这里会补一盘「示例露天矿」的面，于是甘特、任务书、达成度里"
                             + "每一个数都自洽、看着完全正常，而它们说的是别的矿。现在不补了：**空着就是空着**。"
                             + "两条补法，任选其一："
                             + "① 到「采掘单元清单」排一期 → 「作业区划分 · 工序作业区」生成并入库，"
                             + "面就会从本期月度计划派生出来（推荐，位置与量都来自计划）；"
                             + "② 直接在「作业面台账」里建档并保存一次。");
        }
        _nowHour = cfg.NowHour;
        Mark("作业面", FaceOrigin switch
        {
            FaceOriginKind.Ledger => ChainState.Real,
            FaceOriginKind.Derived => ChainState.Real,          // 派生自本期工序区 = 真实数据
            FaceOriginKind.LedgerPlusDerived => ChainState.Real,
            // ★ 「没有作业面」是 Missing 不是 Sample：样例已经不兜底了，
            //   再标成「样例」会让体检报告说一件不存在的事。
            FaceOriginKind.None => ChainState.Missing,
            _ => ChainState.Sample,
        });

        // ── ⑤ 去向登记簿（内部已「DB → 样例」两层兜底）──
        //
        // ★ 先跑一次**自动重建**（规划链第 0 步，2026-08-20）。
        //   排土场清单是**算得出来的**：采掘单元台账里 2112 个排土位置，按场名一分组就是。
        //   库里一个场都没有时（V048 删掉编的两条种子之后就是这个状态），
        //   整条下游全断：去向未定 ⇒ 运距算不出 ⇒ 配车解不出 ⇒ 一条运输任务都没有
        //   ⇒ 排土面零入方。能算出来的东西不该拦在一个按钮后面。
        //   库里已经有场就一个字都不动（SR1：人填的坐标/通过能力/兜底运距是事实，不许被派生值盖）。
        cfg.Sinks = SampleTaskBoard.LoadSinks();
        Mark("去向登记簿", SinkRegistryLoader.FromDatabase ? ChainState.Real : ChainState.Sample);

        // 逐去向的入仓煤质锚点只能盖在这一步之后 —— ①.5 的 ApplyTo 跑的时候登记簿还是空的。
        // 拆成两步是刻意的：合在一起写会"成功"地盖到零个点上，且一声不吭。
        string sinkBlendLabel = SafeCall(() => CompileOverrides.ApplySinkBlend(cfg), "逐去向配煤锚点");
        if (!string.IsNullOrWhiteSpace(sinkBlendLabel))
            AnchorSourceLabel = string.IsNullOrWhiteSpace(AnchorSourceLabel)
                ? sinkBlendLabel
                : AnchorSourceLabel + "　·　" + sinkBlendLabel;

        // ── ⑥⑦⑧⑨ 四步接线（顺序不可颠倒）──
        PlanSourceLabel = SafeCall(() => ShortTermLink.ApplyToConfig(cfg, _assemblyViolations), "月计划");
        SinkSourceLabel = ApplyFlow(cfg);
        HaulSourceLabel = SafeCall(() => HaulResolver.ApplyTo(cfg), "运距");
        FleetSourceLabel = SafeCall(() => FleetMatcher.ApplyTo(cfg), "编组");

        // ★ **按 n\* 自动配车**（2026-08-20）：实配车队本来只有"人在作业面台账上填"这一条来源，
        //   而派生出来的面没有人填过 ⇒ 每个面都是「配 0 车（荐 6）」⇒
        //   推演里系统瓶颈恒报「铲等车·运力不足」，而在用卡车 200 多台 —— 这个瓶颈是假的。
        //   与主设备同一条纪律：台账填过的不动、一台车只上一个面、车不够如实报。
        string truckLabel = SafeCall(
            () => FaceEquipmentAutoAssigner.AssignTrucks(cfg.Faces, EquipmentPool(EquipmentCategory.Truck)),
            "配车");
        if (!string.IsNullOrWhiteSpace(truckLabel)) FleetSourceLabel += "　·　" + truckLabel;

        // ── ⑩ 主设备可用性 ──
        // 排在最后：编组这一步可能改动主设备，要核的是**最终**排上去的那台。
        // 此前这一条根本不存在：一台状态为「检修」甚至「报废」的电铲照样被排满三个班。
        EquipSourceLabel = SafeCall(() => EquipmentAvailability.Check(cfg, _assemblyViolations), "设备可用性");

        // ── ⑪ 采掘单元对号 ──
        // 作业面上填的 UnitId 与采矿模型基表对一次。没有基表就一条都不报（不整屏假告警）。
        UnitSourceLabel = SafeCall(() => MiningUnitLink.Check(cfg, _assemblyViolations), "采掘单元");

        // 这五段各自记了一个 LastFromLedger（2026-08-18 补）—— 在此之前它们只有文案，
        // 体检只能标「未知」，而其中三段实际上正在吃兜底（月计划「用样例日目标」、
        // 编组「内置缺省编组规则」、设备可用性「本次不校核」）。
        // ★ 状态一律读各自的标志，**不从文案里正则抠**：文案一改就静默抠空，
        //   而抠空之后「兜底」会被当成「真实」—— 朝着让人放心的方向失效。
        Mark("月计划", ShortTermLink.LastFromLedger ? ChainState.Real : ChainState.Sample);
        Mark("编组", FleetMatcher.LastFromLedger ? ChainState.Real : ChainState.Sample);
        Mark("设备可用性", EquipmentAvailability.LastFromLedger ? ChainState.Real : ChainState.Missing);
        // 这两段不是"样例 vs 真实"，而是"齐 vs 不齐"：有真数据但有腿走了兜底 / 有面没绑上号。
        Mark("运距", HaulResolver.LastFromLedger ? ChainState.Real : ChainState.Partial);
        Mark("采掘单元对号", MiningUnitLink.LastFromLedger ? ChainState.Real : ChainState.Partial);

        // 编组求解不可用时的兜底班产：只为不让装箱除以 0，且必须说出来
        int patched = FillMissingCapacity(cfg);
        if (patched > 0)
            FleetSourceLabel += $"；{patched} 个面未解出班产，按兜底 {FaceLedgerLoader.FallbackLoadCapacityM3PerH:0} m³/h 估算";

        SourceLabel = string.Join("　·　", new[]
        {
            PeriodSourceLabel, ShiftSourceLabel, BlastSourceLabel, DrillSourceLabel, FaceSourceLabel,
            PlanSourceLabel, SinkSourceLabel, HaulSourceLabel, FleetSourceLabel,
            MaintenanceSourceLabel, EquipSourceLabel, UnitSourceLabel, AnchorSourceLabel,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // ★ 样例这件事要顶到**最前面**，而且要用一个扫一眼就分得出的记号。
        //
        //   为什么不能只靠上面那串来源文案：它是十三段拼起来的长句，"作业面：台账尚无档案，
        //   用样例盘子"夹在第五段里，字号与其余十二段一模一样。而样例盘子长得和真盘子完全一样 ——
        //   有面、有量、有编组、有甘特、有达成度，每个数都自洽。人只会以为自己的矿数据齐了。
        //
        //   这一行是全模块共用的（甘特/任务书/派工/达成度/报表都显示它），
        //   在这里加一次，十几个窗口一起有 —— 逐个窗口去加，漏掉的那个不会报错。
        if (!FacesAreReal)
            SourceLabel = (NoFaces ? "【没有作业面·排不出计划】　" : "【非本矿数据】　") + SourceLabel;

        _assembled = true;
        return cfg;
    }

    /// <summary>
    /// 本日出勤设备清单（甘特一行一台 / 设备状态 / 调度看板）。
    ///
    /// <para>
    /// <b>口径是「今天这盘任务用到的设备」，不是「全矿在籍设备」</b>——真台账里有五百多台，
    /// 一股脑铺进甘特就是五百多行，谁也看不了；而"今天谁在干活"本来就该由当日盘子决定。
    /// 全矿在籍清单另有出口：<see cref="EquipmentPool"/>（补车/顶替的可调配池）。
    /// </para>
    /// <para>
    /// 排法沿用样例花名册：主设备一行，其配属卡车紧随其后作子行；类别与型号从设备台账补齐，
    /// 台账里查不到的设备（临时借调、台账未录）如实列出，只是没有型号后缀。
    /// </para>
    /// </summary>
    public static List<RosterEntry> Roster()
    {
        List<ProductionTask> tasks;
        try { tasks = Result().Tasks; }
        catch { return new List<RosterEntry>(); }   // 样例不再兜底：没任务就是没设备行

        if (tasks == null || tasks.Count == 0) return new List<RosterEntry>();

        var info = EquipmentInfo();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<RosterEntry>();

        // 主设备按「类别序 → 编号」排，卡车紧跟自己的铲——与样例花名册的排法一致
        var mains = tasks
            .Select(t => t.Group?.MainEquipment)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => CategoryOrder(RawCategory(info, id!)))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        // ★ 没有执行人的工序也要有一行（2026-08-20）：爆破按用户定的口径**不指人**
        //   （爆破队台账还没有），而甘特是按"设备行"画条的 —— 没有行，条就画不出来，
        //   于是工序链里"穿孔→爆破→采装"中间那一环在图上永远是空的，而任务其实排了。
        if (tasks.Any(t => t != null && string.IsNullOrWhiteSpace(t.Group?.MainEquipment)))
        {
            var noOwner = tasks.Where(t => t != null && string.IsNullOrWhiteSpace(t.Group?.MainEquipment))
                               .Select(t => t.Process).Distinct().ToList();
            foreach (var proc in noOwner)
            {
                string name = proc switch
                {
                    ProcessType.Blast => "爆破（未指定队组）",
                    _ => proc + "（未指定执行人）",
                };
                if (seen.Add(name)) entries.Add(new RosterEntry(name, name, "爆破", sub: false));
            }
        }

        foreach (var main in mains)
        {
            if (seen.Add(main!)) entries.Add(Entry(info, main!, sub: false));

            var trucks = tasks
                .Where(t => string.Equals(t.Group?.MainEquipment, main, StringComparison.OrdinalIgnoreCase))
                .SelectMany(t => t.Group?.Trucks ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal);

            foreach (var truck in trucks)
                if (seen.Add(truck)) entries.Add(Entry(info, truck, sub: true));
        }

        return entries;   // 空就是空 —— 甘特画不出行时会说"本日没有任务"，而不是画一排别的矿的设备
    }

    /// <summary>
    /// 全矿在籍设备按类别取号（<b>可调配池</b>：补车 / 备机顶替用）。
    /// 台账不可用或该类一台都没有时返回空表，由调用方决定怎么兜底。
    /// </summary>
    public static List<string> FleetByCategoryLabel(string categoryLabel)
    {
        var cat = CategoryOf(categoryLabel);
        if (cat.HasValue)
        {
            var pool = EquipmentPool(cat.Value);
            if (pool.Count > 0) return pool;
        }
        // 台账没有这一类 → 回落样例花名册的同类设备
        return SampleTaskBoard.SeedRoster()
            .Where(r => string.Equals(r.Category, categoryLabel, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.EquipId)
            .ToList();
    }

    /// <summary>设备编号 → 台账记录（读不到返回空字典，显示名退化为"编号 + 设备"）。</summary>
    private static Dictionary<string, Equipment> EquipmentInfo()
    {
        var map = new Dictionary<string, Equipment>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in EquipmentDataContext.Equipment.All())
                if (e != null && !string.IsNullOrWhiteSpace(e.EquipmentId)) map[e.EquipmentId.Trim()] = e;
        }
        catch { /* 台账未接通 → 无型号后缀，不影响排班 */ }
        return map;
    }

    private static string RawCategory(Dictionary<string, Equipment> info, string equipId)
        => info.TryGetValue(equipId, out var e) ? (e.Category ?? "") : "";

    private static RosterEntry Entry(Dictionary<string, Equipment> info, string equipId, bool sub)
    {
        info.TryGetValue(equipId, out var e);
        // 车队行（定车制，`{铲号} 车队`）在设备台账里查不到 —— 它不是一台设备，是一组车。
        // 不认它就会落到"其它"，在按设备类型的视图里被排到最后一组，与卡车分家。
        string cat = e != null ? CategoryLabel(e.Category)
                   : IsCrew(equipId) ? "卡车"
                   : (sub ? "卡车" : "设备");
        string model = string.IsNullOrWhiteSpace(e?.Model) ? "" : $"（{e!.Model}）";
        // 车队行的显示名不再拼一次类别（"1734 车队 卡车" 读起来是两样东西）
        string display = IsCrew(equipId) ? equipId : $"{equipId} {cat}{model}";
        return new RosterEntry(equipId, display, cat, sub);
    }

    /// <summary>是不是车队行（定车制下运输任务的"设备"）。口径与 <c>HaulDumpDeriver.CrewName</c> 同源。</summary>
    private static bool IsCrew(string? id) => (id ?? "").TrimEnd().EndsWith("车队", StringComparison.Ordinal);

    /// <summary>某类在籍设备编号（动态调整补车/顶替的取车池）。台账不可用返回空表，由调用方决定怎么兜底。</summary>
    public static List<string> EquipmentPool(EquipmentCategory category)
    {
        try
        {
            return EquipmentDataContext.Equipment.All()
                .Where(e => e != null
                         && !string.IsNullOrWhiteSpace(e.EquipmentId)
                         && string.Equals(e.Category, category.ToString(), StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(e.Status, nameof(EquipmentStatus.Scrapped), StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(e.Status, nameof(EquipmentStatus.Maintenance), StringComparison.OrdinalIgnoreCase))
                .Select(e => e.EquipmentId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  装配细节
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>期次一变（换矿/换作业日/换计划期）盘子必须重算，否则会拿昨天的任务当今天的。</summary>
    private static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        ProjectScope.Changed += Invalidate;
    }

    private static void EnsureAssembled()
    {
        if (_assembled) return;
        try { Config(); } catch { _assembled = true; /* 装配失败也别让取爆破窗口的地方死循环 */ }
    }

    /// <summary>
    /// 班次窗口 ← <c>shift_calendar</c>（日 × 班）。返回 null 表示台账没有本日班次 → 用样例三班。
    ///
    /// <para>
    /// 跨零点的夜班（如 23:30 起）会被**截到 24:00**：装箱引擎的时窗是同一天内的 [start,end)，
    /// 让它绕回 0 点会把次日的活算进今天。截断这件事写进来源文案，不闷声处理。
    /// </para>
    /// </summary>
    private static List<ShiftWindow>? LoadShifts(DateTime date)
    {
        List<ShiftCalendar> rows;
        try { rows = EquipmentDataContext.ShiftCalendar.ByDate(date).Where(r => r != null).ToList(); }
        catch (Exception ex)
        {
            ShiftSourceLabel = $"班次：台账未接通（{Short(ex)}），用样例三班";
            return null;
        }

        if (rows.Count == 0)
        {
            ShiftSourceLabel = $"班次：{date:MM-dd} 无班次日历，用样例三班";
            return null;
        }

        var parsed = rows
            .Select(r => new { r.Shift, Start = ParseHour(r.StartTime), r.IsBlastShift })
            .Where(x => x.Start.HasValue)
            .OrderBy(x => x.Start!.Value)
            .ToList();

        if (parsed.Count == 0)
        {
            ShiftSourceLabel = $"班次：{date:MM-dd} 有 {rows.Count} 条记录但都没有开班时间，用样例三班";
            return null;
        }

        var list = new List<ShiftWindow>();
        bool truncated = false;
        for (int i = 0; i < parsed.Count; i++)
        {
            double start = parsed[i].Start!.Value;
            double end = i + 1 < parsed.Count ? parsed[i + 1].Start!.Value : 24.0;
            if (end <= start) { end = 24.0; truncated = true; }
            list.Add(new ShiftWindow(ShiftName(parsed[i].Shift), start, end));
        }

        int blast = parsed.Count(x => x.IsBlastShift);
        ShiftSourceLabel = $"班次：日历 {list.Count} 班（{date:MM-dd}"
                         + (blast > 0 ? $" · 爆破班 {blast}" : "")
                         + (truncated ? " · 跨零点班已截到 24:00" : "") + "）";
        return list;
    }

    /// <summary>
    /// 检修档期 ← <c>maintenance_window</c>（V042，日 × 设备 × 起始时刻）。
    ///
    /// <para>
    /// 返回 null 表示本日无档期（<b>不是"读失败"</b>——两者的文案分开写，
    /// 「今天没人检修」和「表读不出来」是完全不同的两件事，混成一句会让人查不下去）。
    /// </para>
    /// <para>
    /// 起止解析不出来的行**丢弃并计数**，不拿 0 冒充 0 点：一条 (0,0) 的档期在
    /// <c>TaskExploder.WorkWindow</c> 里不与任何班次重叠，等于静默失效——
    /// 用户在界面上看得见这条档期，计划里却没扣，最难查。
    /// </para>
    /// </summary>
    private static List<MaintenanceWindow>? LoadMaintenance(DateTime date)
    {
        List<MaintenanceWindowPlan> rows;
        try { rows = EquipmentDataContext.MaintenanceWindows.ByDate(date).Where(r => r != null).ToList(); }
        catch (Exception ex)
        {
            MaintenanceSourceLabel = $"检修：档期表未接通（{Short(ex)}），本日不扣检修";
            return null;
        }

        if (rows.Count == 0)
        {
            MaintenanceSourceLabel = $"检修：{date:MM-dd} 无检修档期";
            return null;
        }

        var list = new List<MaintenanceWindow>();
        int bad = 0;
        foreach (var r in rows)
        {
            double? s = ParseHour(r.StartTime), e = ParseHour(r.EndTime);
            if (s is null || e is null || e.Value <= s.Value) { bad++; continue; }
            list.Add(new MaintenanceWindow
            {
                EquipId = (r.EquipmentId ?? "").Trim(),
                Start = s.Value,
                End = e.Value,
                Label = string.IsNullOrWhiteSpace(r.Kind) ? "检修" : r.Kind.Trim(),
            });
        }

        MaintenanceSourceLabel = $"检修：{list.Count} 条档期（{date:MM-dd}）"
                               + (bad > 0 ? $"；{bad} 条起止时刻非法已丢弃（不按 0 点算）" : "");
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 爆破停产时窗 ← 本日 <c>blast_event.blast_time</c>，<b>逐炮各一段</b> + 清场缺省时长。
    ///
    /// <para>
    /// 2026-08-11 从"取最早一炮"改成"取全部"：原来一天三炮只有第一炮进装箱，后两炮的清场
    /// 在计划里根本不存在（那几个时段仍在满负荷作业）。没记时刻的炮**排不进任何时窗**，
    /// 计数后如实写进来源文案，不猜一个时刻。
    /// </para>
    /// <para>返回 null 表示本日没有可用炮次 → 由调用方保留样例窗口。</para>
    /// </summary>
    private static List<BlastWindow>? LoadBlastWindows(DateTime date)
    {
        List<BlastEvent> rows;
        try { rows = EquipmentDataContext.Blast.ByDate(date).Where(b => b != null).ToList(); }
        catch (Exception ex)
        {
            BlastSourceLabel = $"爆破：台账未接通（{Short(ex)}），用样例窗口";
            return null;
        }

        if (rows.Count == 0)
        {
            BlastSourceLabel = $"爆破：{date:MM-dd} 无爆破事件，用样例窗口";
            return null;
        }

        var windows = new List<BlastWindow>();
        int noTime = 0;
        foreach (var b in rows)
        {
            double? h = ParseHour(b.BlastTime);
            if (h == null) { noTime++; continue; }
            windows.Add(new BlastWindow(h.Value, Math.Min(24, h.Value + BlastClearanceH),
                                        b.BlastSeq.HasValue ? $"第{b.BlastSeq}炮" : (b.LocationCode ?? "").Trim()));
        }

        if (windows.Count == 0)
        {
            BlastSourceLabel = $"爆破：{date:MM-dd} 有 {rows.Count} 炮但都没记时刻，用样例窗口";
            return null;
        }

        var merged = BlastWindow.Merge(windows);
        BlastSourceLabel = $"爆破：台账 {rows.Count} 炮 → {merged.Count} 段停产窗口（"
                         + string.Join("、", merged.Select(w => $"{Hm(w.Start)}–{Hm(w.End)}"))
                         + $"；清场按缺省 {BlastClearanceH * 60:0} 分钟，台账不记恢复时刻）"
                         + (noTime > 0 ? $"；{noTime} 炮没记时刻，排不进时窗" : "");
        return merged;
    }

    /// <summary>
    /// 穿孔作业计划 ← <c>drill_plan</c>（V044，日 × 钻机 × 起始时刻）。
    ///
    /// <para>
    /// 这张表补上之前，工序链的第一环在真库上是空的：装配层明写"穿孔计划暂无台账，本日不排"，
    /// 于是钻机一条任务都排不出来、甘特里没有穿孔条、「工序接续」永远报"有采装但全天无穿孔"。
    /// </para>
    /// <para>
    /// 起止解析不出来的行<b>丢弃并计数</b>（同检修档期）：一条 (0,0) 的穿孔任务在时窗里
    /// 不与任何班次重叠，等于静默失效——界面上看得见，计划里没有，最难查。
    /// </para>
    /// </summary>
    private static List<DrillInput>? LoadDrills(DateTime date)
    {
        List<DrillPlan> rows;
        try { rows = EquipmentDataContext.DrillPlans.ByDate(date).Where(r => r != null).ToList(); }
        catch (Exception ex)
        {
            DrillSourceLabel = $"穿孔：计划表未接通（{Short(ex)}），本日不排穿孔";
            return null;
        }

        if (rows.Count == 0)
        {
            DrillSourceLabel = $"穿孔：{date:MM-dd} 无穿孔计划（在「钻爆计划衔接」里排）";
            return null;
        }

        var list = new List<DrillInput>();
        int bad = 0, cancelled = 0;
        foreach (var r in rows)
        {
            if (string.Equals((r.Status ?? "").Trim(), "取消", StringComparison.Ordinal)) { cancelled++; continue; }
            double? s = ParseHour(r.StartTime), e = ParseHour(r.EndTime);
            if (s is null || e is null || e.Value <= s.Value) { bad++; continue; }
            list.Add(new DrillInput
            {
                EquipId = (r.EquipmentId ?? "").Trim(),
                Zone = (r.Zone ?? "").Trim(),
                BenchElevationM = r.BenchElevationM ?? 0,
                Start = s.Value,
                End = e.Value,
                // 台账记的是孔数与单孔延米 —— 穿孔任务的量就是它俩，不带下来的话
                // 装箱出来的穿孔任务只有时窗没有量，工序进度只能按"完成/未完成"判（而那个状态没有入口）
                HoleCount = r.HoleCount,
                HoleLengthM = r.HoleCount is > 0 && r.HoleLengthM is > 0
                    ? r.HoleCount.Value * r.HoleLengthM.Value      // 台账是**单孔**延米，总延米 = 孔数 × 单孔
                    : r.HoleLengthM,
            });
        }

        int withQty = list.Count(x => x.HoleCount is > 0 || x.HoleLengthM is > 0);
        DrillSourceLabel = $"穿孔：{list.Count} 条计划（{date:MM-dd}）"
                         + (withQty < list.Count ? $"；其中 {list.Count - withQty} 条没录孔数/延米，进度只能按完成与否判" : "")
                         + (cancelled > 0 ? $"；{cancelled} 条已取消不排" : "")
                         + (bad > 0 ? $"；{bad} 条起止时刻非法已丢弃（不按 0 点算）" : "");
        return list.Count > 0 ? list : null;
    }

    /// <summary>流向分配 + 一句来源文案（含去向台账来源、内排率、排弃占容）。</summary>
    /// <summary>
    /// 把**没配上去向的岩**（非矿石物料）配到内排土场——用户给的现场口径。
    /// </summary>
    /// <remarks>
    /// <para>只补空的：分配器配上的一律不动（那是按运输功最小解出来的，比规则更优）。</para>
    /// <para>库里没有内排土场就什么都不做并返回 0 —— <b>不新建场</b>，那是去向台账那一步的事。</para>
    /// </remarks>
    private static int RouteRockToInternalDump(ExploderConfig cfg)
    {
        SinkNode? dump = null;
        try
        {
            dump = cfg.Sinks?.All
                   .Where(s => s != null && s.IsActive
                            && (s.Kind == SinkKind.InternalDump
                             || (s.Name ?? "").Contains("内排", StringComparison.Ordinal)))
                   .OrderByDescending(s => s.RemainingM3).FirstOrDefault();
        }
        catch { }
        if (dump == null) return 0;

        int filled = 0;
        foreach (var f in cfg.Faces.Where(x => x != null && x.Process == ProcessType.Load))
        {
            foreach (var share in f.ResolvedMix.Shares)
            {
                if (share.Fraction <= 1e-6) continue;
                var spec = MaterialCatalog.Resolve(share.MaterialCode);
                if (spec.IsOre) continue;                       // 煤/低品位去破碎站，不进排土场

                var cur = f.DestinationFor(share.MaterialCode);
                if (cur.HasDestination) continue;               // 分配器已经配上了，不动

                if (f.HasSplits)
                {
                    var sp = f.Splits.FirstOrDefault(x => string.Equals(x.MaterialCode, share.MaterialCode,
                                                                        StringComparison.OrdinalIgnoreCase));
                    if (sp == null)
                    {
                        sp = new MaterialDestination { MaterialCode = share.MaterialCode, Fraction = share.Fraction };
                        f.Splits.Add(sp);
                    }
                    sp.DestinationId = dump.Id; sp.DestinationName = dump.Name; sp.DestinationKind = dump.Kind;
                }
                else
                {
                    f.DestinationId = dump.Id; f.DestinationName = dump.Name; f.DestinationKind = dump.Kind;
                }
                filled++;
            }
        }
        return filled;
    }

    private static string ApplyFlow(ExploderConfig cfg)
    {
        string sinkSrc = "";
        try { sinkSrc = SinkRegistryLoader.LastSourceLabel; } catch { }

        try
        {
            FlowAssigner.ApplyTo(cfg);

            // ★ **岩去内排土场**（2026-08-20，用户给的口径）——分配器配不上的兜这一手。
            //   分配器按「运输功最小 + 库容 + 相容」解，解不出来时它**不编**（对的）；
            //   但现场规矩很简单：岩就是排到内排土场。这条规则算不出来，是人给的，
            //   所以按规则补，并**标注是按规则配的、不是分配器算的** ——
            //   不标注的话，人会以为这个去向是优化出来的。
            int byRule = RouteRockToInternalDump(cfg);

            var loads = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();
            // 判据是「逐物料都定了」而非「面上填了一个去向」：混采面只定了煤不叫定完。
            int got = loads.Count(f => f.AllMaterialsRouted);
            int mixed = loads.Count(f => f.HasSplits);
            var bal = cfg.Balance();
            return $"去向：{got}/{loads.Count} 面已逐物料定齐"
                 + (mixed > 0 ? $"（其中 {mixed} 个混采面带物料分项）" : "")
                 + $"（内排率 {bal.InternalDumpPct:0.#}% · 排弃占容 {bal.DumpedWanM3:0.00}万m³）"
                 + (sinkSrc.Length > 0 ? $" · {sinkSrc}" : "");
        }
        catch (Exception ex)
        {
            return $"去向：流向分配未生效（{Short(ex)}），按面上手工去向执行"
                 + (sinkSrc.Length > 0 ? $" · {sinkSrc}" : "");
        }
    }

    /// <summary>
    /// 给没解出班产的面补一个兜底值，返回补了几个。
    /// 班产是装箱的除数，为 0 会让计划工时变成无穷大——但兜底值必须显式说出来，
    /// 否则用户看到的是一份"看着很正常"的假计划。
    /// </summary>
    private static int FillMissingCapacity(ExploderConfig cfg)
    {
        int n = 0;
        foreach (var f in cfg.Faces)
        {
            if (f.Group.GroupCapacityM3PerH > 1e-6) continue;
            f.Group.GroupCapacityM3PerH = f.Process == ProcessType.Dump
                ? FaceLedgerLoader.DefaultDozerCapacityM3PerH
                : FaceLedgerLoader.FallbackLoadCapacityM3PerH;
            n++;
        }
        return n;
    }

    /// <summary>
    /// 本盘任务是<b>逐班推进</b>分解出来的（月度台账 → 班组计划），
    /// 而不是「日目标 ÷ 班数 × 编组班产」那条标量装箱。
    /// <para>差别在于**位置与推进**：分解器给的每一条都带着"这一班在这个单元上推过的那一段地"，
    /// 装箱给的只有量。取不到月度台账时仍退装箱 —— 那时没有单元队列，也就无从推进。</para>
    /// </summary>
    public static bool TasksFromDecomposer { get; private set; }

    /// <summary>分解器这一路的说明（取不到月度台账时说清楚为什么退回装箱）。</summary>
    public static string DecomposerLabel { get; private set; } = "";

    private static ExploderResult BuildAndApply()
    {
        var cfg = Config();

        // ── 先试逐班推进分解（月度台账 → 班组计划）──
        //
        //  为什么放在装箱之前：装箱那条路把月量摊成日目标再按编组班产切，
        //  **整条路上没有位置**，也没有"一个单元采完换下一个"这件事 ——
        //  同一个面每天的目标一样多，而现场不是那样。
        //  分解器吃的是月度台账的单元队列 + 真轨，推到哪儿是算出来的。
        //
        //  取不到月度台账（没排期）时**退回装箱**：那时没有单元队列，谈不上推进。
        //  退了要说清楚 —— 「按位置排的」与「按标量摊的」出来的甘特长得一样。
        TasksFromDecomposer = false;
        DecomposerLabel = "";

        // ★ 「没有作业面」是**整盘的硬闸**，两条产出路径都得过（2026-08-19 补）。
        //
        //   Config() 已经判出 NoFaces 并在顶栏挂了【没有作业面·排不出计划】，
        //   可那句话**拦不住下面任何一条路**：分解器不吃 cfg.Faces（它吃月度台账），
        //   照样能排出一盘只有穿孔的计划 —— 于是同一个窗口上半句说排不出来、
        //   下半句排了一个月的活。闸要落在产出这一侧，不能只落在文案上。
        //
        //   而且这一条要进 Violations，不能只进 Notes：校核面板只读 Violations，
        //   放在 Notes 里它就照旧报「✓ 无异常」。
        if (NoFaces)
        {
            var none = new ExploderResult();
            if (_assemblyViolations.Count > 0) none.Violations.InsertRange(0, _assemblyViolations);
            none.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Error,
                Code = ViolationCodes.NoWorkFace,
                Message = "**一个作业面都没有 —— 本盘不排任何任务。** "
                        + "作业面有三层来源：①「作业面台账」的档案 ②本期工序作业区派生 ③（已取消）样例，"
                        + "三层现在全空。补法：到「作业区划分」把本期工序作业区生成并入库"
                        + "（面会连同位置、量、单元号一起派生出来，主电铲按台阶标高从作业面台账认领），"
                        + "或直接在「作业面台账」建档并保存一次。",
            });
            DecomposerLabel = "没有作业面 —— 分解与装箱都不跑（此前分解器会绕过这一条，只排穿孔）。";
            ActualSourceLabel = "实绩：本盘没有任务，无处回灌。";
            DispatchStat = default;
            DispatchSourceLabel = "单据：本盘没有任务，无处回灌。";
            return none;
        }

        try
        {
            string period = ProjectScope.WorkDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);

            // ★ onlyDate 必须给：分解器排的是**整月**，而 ProductionTask 是**单日契约**
            //   （只有 Shift 与 0..24 的起止，没有日期列）。不切就是把一个月的条
            //   叠在同一根 24 小时轴上 —— 甘特、达成度、派车单每个数都自洽，说的却是一个月。
            var asm = ShiftPlanAssembler.Build(period, cfg, ledgerRoot: null,
                                               onlyDate: ProjectScope.WorkDate);
            // ★ 判的是「**今天**排出来没有」，不是「这个月排出来没有」（2026-08-20 补）。
            //
            //   `asm.Ok` 看的是整月行数 > 0。整月有活、而今天一条都没有时，
            //   这条路照样 return，当日盘子于是**空着**，而顶栏还写着「分解出 44 条」——
            //   一句话说排了 44 条，下面一条都没有。
            //   实测：钻机注入器没装上时，需爆破的单元全被「穿爆超前」挡下，
            //   分解器只剩一台设备的 44 条、且都不在今天 ⇒ 整盘归零，
            //   而在这之前它是靠"分解器返回 0 条"才退回装箱路的 —— 一旦它返回一点点，反而更糟。
            //
            //   今天没有就退回标量装箱那条路，并把原因说出来。今天真的不该有活
            //   （不是作业日）时，装箱那条路自己会排出空盘，那是它说的，不是这里替它说的。
            if (asm.Ok && asm.Tasks.Count == 0)
            {
                _assemblyNotes.Add($"◆ 逐班分解器**整月排出了 {asm.MonthTasks.Count} 条，而今天一条都没有** —— "
                                 + "已退回日目标标量装箱那条路（否则当日盘子会是空的，"
                                 + "而顶栏还写着分解出了多少条）。常见原因：这一天不在分解器用的作业日里，"
                                 + "或今天该干的单元被「穿爆超前」挡着（没有钻机可派时整批需爆破的单元都排不上）。");
            }
            else if (asm.Ok)
            {
                TasksFromDecomposer = true;
                DecomposerLabel = asm.Headline;
                foreach (var n in asm.Notes) _assemblyNotes.Add(n);

                var dr = new ExploderResult();
                dr.Tasks.AddRange(asm.Tasks);
                if (_assemblyViolations.Count > 0) dr.Violations.InsertRange(0, _assemblyViolations);

                // ★ 分解器这条路此前**一条判据都不跑** —— 直接 return，
                //   把 CheckConstraints / SpaceTimeValidator 全绕过去了。
                //   校核面板那句「✓ 无异常」于是不是"没问题"，是"没人去校"。
                try { TaskExploder.CheckOnly(cfg, dr); }
                catch (Exception ex)
                {
                    dr.Violations.Add(new PlanViolation
                    {
                        Severity = ViolationSeverity.Warn, Code = ViolationCodes.ProcessChain,
                        Message = $"本盘校核未跑完（{ex.GetType().Name}）—— 下面的「无异常」不成立，别当已校过。",
                    });
                }

                AppendHaulDump(dr, cfg);

                // ★ 单据回灌必须排在实绩之前（DL5）：实绩置出的 Done/Partial 是执行轴，
                //   比单据抬出来的 Dispatched 强；反过来跑会把已完成的任务打回"已下达"。
                ReflowDispatch(dr.Tasks);
                ApplyPersistedActuals(dr.Tasks);
                DispatchStateLink.CheckConflicts(dr.Tasks, dr);
                return dr;
            }
            DecomposerLabel = asm.Headline;
            _assemblyNotes.Add("· 本盘走的是**日目标标量装箱**，不是逐班推进分解 —— "
                             + asm.Headline
                             + "　两者出来的甘特长得一样，但标量装箱那条路上**没有位置**："
                             + "任务落在哪块地、这台铲今天推到哪儿都说不出来。");
        }
        catch (Exception ex)
        {
            DecomposerLabel = $"分解失败（{ex.GetType().Name}）";
            _assemblyNotes.Add($"· 逐班推进分解抛了 {ex.GetType().Name}，已退回日目标装箱。");
        }

        var r = TaskExploder.Explode(cfg);

        // 装配期校核排在装箱期校核之前：口径问题（日目标是按哪个作业日摊的）先于
        // 由它派生出来的结果问题（当日欠产），看的人才能顺着因果读下去
        if (_assemblyViolations.Count > 0)
            r.Violations.InsertRange(0, _assemblyViolations);

        AppendHaulDump(r, cfg);

        // 单据回灌与"是不是台账盘子"无关：样例盘子上照样可以下达、撤回，
        // 而下游三组全靠这条分界线。实绩才是不许在样例上合成的那一样。
        ReflowDispatch(r.Tasks);

        if (FacesFromLedger) ApplyPersistedActuals(r.Tasks);
        else ActualSourceLabel = "实绩：本盘不是台账盘子，**不合成样例实绩**"
                               + "（合成出来的达成度看着完全正常，而它评的不是任何真实作业）。";

        DispatchStateLink.CheckConflicts(r.Tasks, r);
        return r;
    }

    /// <summary>单据回灌的一层薄封装：把计数与文案落到上下文上，供各窗顶栏统一取用。</summary>
    private static void ReflowDispatch(List<ProductionTask> tasks)
    {
        DispatchStat = DispatchStateLink.ApplyPersistedInstances(tasks, DateLabel);
        DispatchSourceLabel = DispatchStat.Caption;
    }

    /// <summary>
    /// 真实绩回灌：只读 <c>actuals/</c> 里录过的那几条，**没录的任务保持"计划"状态**。
    /// 绝不在真实台账上合成实绩——那正是这套报表最不该做的事。
    /// 库容不在此回灌：实绩录入保存时已按占容方扣过一次，这里再扣就是记两遍。
    /// </summary>
    private static void ApplyPersistedActuals(List<ProductionTask> tasks)
    {
        List<ActualRecord> saved;
        try { saved = TaskPersistence.LoadActualsOfDay(DateLabel); }
        catch (Exception ex) { ActualSourceLabel = $"实绩：读盘失败（{Short(ex)}），全部按计划状态"; return; }

        if (saved.Count == 0)
        {
            ActualSourceLabel = "实绩：本日尚无录入（任务保持「计划」状态，不合成实绩）";
            return;
        }

        var byKey = new Dictionary<string, ActualRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in saved)
            if (!string.IsNullOrWhiteSpace(rec.StableKey)) byKey[rec.StableKey] = rec;

        int hit = 0;
        foreach (var t in tasks)
        {
            if (t.Process == ProcessType.Idle) continue;
            string key = TaskKey.Of(t, DateLabel);
            if (!byKey.TryGetValue(key, out var rec)) continue;

            t.ActualVolumeM3 = Math.Max(0, rec.ActualVolumeM3);
            t.ActualHours = Math.Max(0, rec.ActualHours);
            t.TrucksOnSite = Math.Max(0, rec.TrucksOnSite);
            if (rec.Reasons is { Count: > 0 }) t.Reasons = new List<IncompleteReason>(rec.Reasons);
            if (rec.AshPct is > 0)
            {
                t.QualityActual ??= new CoalQuality();
                t.QualityActual.AshPct = rec.AshPct.Value;
            }
            // 状态口径与实绩录入窗口保持一致（达成 ≥98% 记完成，否则部分完成）
            t.Status = t.ActualVolumeM3 <= 1e-6 ? t.Status
                     : t.AttainmentPct >= 98 ? TaskStatus.Done
                     : TaskStatus.Partial;
            hit++;
        }

        ActualSourceLabel = $"实绩：已回灌 {hit}/{saved.Count} 条录入记录"
                          + (hit < saved.Count ? "（其余记录对不上当日任务，可能是任务改过）" : "");
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    /// <summary>调用一个「返回来源文案」的接线步骤；失败只降级不抛。</summary>

    // ══════════════════════════════════════════════════════════════
    //  作业面第二来源：本期工序作业区
    // ══════════════════════════════════════════════════════════════

    /// <summary>派生本期工序区里的作业面。<b>永不抛</b> —— 派生失败不能把整盘装配拖垮。</summary>
    /// <summary>
    /// 判据用的注入口：非 null 时 <see cref="SafeDerive"/> 走它，不去读台账。
    ///
    /// <para><b>为什么必须有这个缝</b>：裸台架里数据库不可用 ⇒ <c>Derive()</c> 永远返回 null
    /// ⇒ <see cref="Config"/> 里那条「用派生的面顶掉样例」的分支<b>一次都走不到</b>。
    /// 而那条分支正是"不再用示例数据"这件事的落点 —— 整条链上最该被判住的一段，
    /// 反倒是唯一没有判据能碰到的一段。</para>
    ///
    /// <para>用完请置回 null（判据自己负责），否则会污染同一进程里后面的装配。</para>
    /// </summary>
    internal static Func<List<FaceInput>?>? FaceDeriveHook { get; set; }

    private static List<FaceInput>? SafeDerive()
    {
        try { return FaceDeriveHook != null ? FaceDeriveHook() : ProcessZoneFaceSource.Derive(); }
        catch (Exception ex)
        {
            _assemblyNotes.Add($"· 工序作业区派生作业面失败（{ex.GetType().Name}），本项跳过。");
            return null;
        }
    }

    /// <summary>
    /// F2 补齐不顶替：把台账里<b>没有</b>的派生面补进盘子，返回补了几个。
    ///
    /// <para><b>按面名比对，不按单元号</b>：一个面一个月要顺着好几个单元推过去，
    /// 台账那条档案上填的单元号可能还停在上个月那个 —— 按单元号比对会把同一个面补成两条，
    /// 于是同一块地被排了两遍产，而两条各自都"看着对"。</para>
    /// </summary>
    internal static int MergeDerivedFaces(ExploderConfig cfg, List<FaceInput>? derived)
    {
        if (derived == null || derived.Count == 0) return 0;
        var have = new HashSet<string>(
            cfg.Faces.Select(f => (f.Zone ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var f in derived)
        {
            string zone = (f.Zone ?? "").Trim();
            if (zone.Length == 0 || have.Contains(zone)) continue;
            cfg.Faces.Add(f);
            have.Add(zone);
            n++;
        }
        return n;
    }

    /// <summary>
    /// 把**运输**与**排土**从采装那一笔派生出来，追加进本盘（两条产出路径都要过这一步）。
    ///
    /// <para>
    /// 现场的运输与排土不是"另外排的活"，是同一批量的下游：
    /// 采装 → 按物料拆 → 各自去向 → 承运吨 → 车次；排弃那部分 → 到场占容方 → 推土机的活。
    /// 所以它们由采装推出来（推出来才守恒），而不是再排一遍。口径见 <see cref="HaulDumpDeriver"/>。
    /// </para>
    /// <para><b>永不抛</b>：派生不出来就是这一盘没有运输/排土条，原因写进装配提示，不拖垮整盘。</para>
    /// </summary>
    private static void AppendHaulDump(ExploderResult r, ExploderConfig cfg)
    {
        if (r == null || cfg == null) return;
        try
        {
            // ★ **已有的工序不补**：装箱那条路自己会排排土笔（排土面有日目标时），
            //   分解器那条路则一笔都没有。不判就两边叠加 —— 实测排土条会变成 102 笔（正好两份），
            //   而每一笔看着都对，只有合计翻倍。
            bool hasHaul = r.Tasks.Any(t => t != null && t.Process == ProcessType.Haul);
            bool hasDump = r.Tasks.Any(t => t != null && t.Process == ProcessType.Dump);

            // 车池上限：在用卡车台数（定车制下一台车一班只跟一台铲）。
            // 取不到就传 0 = 这一条不判 —— 拿 0 当"一台车都没有"会把整盘削光。
            int trucks = SafeInt(() => EquipmentPool(EquipmentCategory.Truck).Count, 0);

            // ★ 回填**只在这里跑一次**：它会就地削采装量，跑两次就削两次。
            var fb = HaulDumpDeriver.ApplyFeasibility(r.Tasks, cfg, trucks);
            foreach (var note in fb.Notes) _assemblyNotes.Add(note);

            var hd = HaulDumpDeriver.Derive(r.Tasks, cfg, trucks);
            if (!hasHaul) r.Tasks.AddRange(hd.Haul);
            if (!hasDump) r.Tasks.AddRange(hd.Dump);
            foreach (var note in hd.Notes) _assemblyNotes.Add(note);

            if (hasHaul || hasDump)
                _assemblyNotes.Add($"· 本盘里{(hasHaul ? "运输" : "")}{(hasHaul && hasDump ? "与" : "")}{(hasDump ? "排土" : "")}"
                                 + "任务是**装箱那一步自己排的**，派生这一步就不再补（补了会两份叠加，合计翻倍）。");

            if (fb.CutTotalM3 > 1e-6)
                HaulDumpLabel = $"可行性回填削 {fb.CutTotalM3 / 1e4:0.##} 万m³"
                              + $"（卸点 {fb.CutByTipCapacityM3 / 1e4:0.##} · 车池 {fb.CutByTruckPoolM3 / 1e4:0.##}"
                              + $" · 库容 {fb.CutByStorageM3 / 1e4:0.##}）　·　";
            else HaulDumpLabel = "";

            HaulDumpLabel += hd.Haul.Count + hd.Dump.Count == 0
                ? "运输/排土：一条都没派生出来（多半是去向未定 —— 没有去向就算不出运距与配车）"
                : $"运输 {hd.Haul.Count} 笔（承运 {hd.HaulTonnage / 1e4:0.##} 万t"
                  + $"{(hd.Haul.Sum(x => x.TripCount) > 0 ? $" · {hd.Haul.Sum(x => x.TripCount)} 车次" : " · 车次未解出")}）"
                  + $" · 排土 {hd.Dump.Count} 笔（到场 {hd.DumpVolumeM3 / 1e4:0.##} 万m³ 占容）";
        }
        catch (Exception ex)
        {
            HaulDumpLabel = $"运输/排土：派生失败（{ex.GetType().Name}）";
            _assemblyNotes.Add($"◆ 运输/排土派生抛了 {ex.GetType().Name} —— 本盘只有穿孔与采装条。");
        }
    }

    /// <summary>运输/排土派生的来源文案。</summary>
    public static string HaulDumpLabel { get; private set; } = "";

    /// <summary>车流仿真的结论文案（解析解跑不跑得出来，瓶颈在哪）。</summary>
    public static string SimFlowLabel { get; private set; } = "";

    /// <summary>
    /// 对当日盘子跑一遍**车流离散事件仿真**（第二阶段）。
    ///
    /// <para>
    /// 解析解给的是稳态解，算不出多条线抢同一个卸点造成的**排队与反压**。
    /// 仿真按同一份 T_c 的分解跑逐车次事件，出「实际拉得下多少、谁在等谁」。
    /// <b>它不改计划</b>（要削量是可行性回填那一步的事，那一步才有优先级规则），只负责说实话。
    /// </para>
    /// <para>结果同时挂在 <see cref="SimFlowLabel"/> 与装配提示里；跑不动就说为什么，不静默。</para>
    /// </summary>
    public static TaskLib.Simulation.TruckFlowResult SimulateFlow()
    {
        try
        {
            var r = TaskLib.Simulation.TruckFlowSimulator.Run(Day(), Config());
            SimFlowLabel = r.Lanes.Count == 0
                ? "车流仿真：一条线都跑不起来（编组没解出载重/节拍/循环时间，或没有运输笔）"
                : $"车流仿真：实拉 {r.TonnageT / 1e4:0.##} 万t / 计划 {r.AnalyticTonnageT / 1e4:0.##} 万t"
                  + $"（{r.Attainment * 100:0.#}%）· 卸点排队 {r.QueueAtSinkMin / 60:0.#} h"
                  + $" · 铲等车 {r.ShovelIdleMin / 60:0.#} h";
            return r;
        }
        catch (Exception ex)
        {
            SimFlowLabel = $"车流仿真：跑不起来（{ex.GetType().Name}）";
            return new TaskLib.Simulation.TruckFlowResult();
        }
    }

    private static string SafeCall(Func<string> step, string name)
    {
        try { return step() ?? ""; }
        catch (Exception ex) { return $"{name}：接线未生效（{Short(ex)}）"; }
    }

    /// <summary>同 <see cref="SafeCall"/>，只是这一步返回的是个数（失败给缺省值，装配不许因此中断）。</summary>
    private static int SafeInt(Func<int> step, int fallback)
    {
        try { return step(); }
        catch { return fallback; }
    }

    /// <summary>"07:30" / "7:30:00" / "7.5" → 小时数；解析不了返回 null。</summary>
    private static double? ParseHour(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0) return null;
        if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var ts) && ts.TotalHours is >= 0 and <= 24)
            return Math.Round(ts.TotalHours, 3);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h is >= 0 and <= 24)
            return h;
        return null;
    }

    /// <summary>A/B/C → 早/中/夜班。口径在 <see cref="WorkCalendar.ShiftName"/>，此处只转发（班次日历窗口要用同一套）。</summary>
    private static string ShiftName(string? code) => WorkCalendar.ShiftName(code);

    private static string CategoryLabel(string? category) => (category ?? "").Trim() switch
    {
        nameof(EquipmentCategory.Shovel) => "电铲",
        nameof(EquipmentCategory.Truck) => "卡车",
        nameof(EquipmentCategory.Drill) => "钻机",
        nameof(EquipmentCategory.Dozer) => "推土机",
        nameof(EquipmentCategory.Loader) => "前装机",
        nameof(EquipmentCategory.Grader) => "平路机",
        nameof(EquipmentCategory.WaterTruck) => "洒水车",
        _ => "其它",
    };

    /// <summary>中文类别名 → 台账枚举（取池按类别匹配用）；对不上返回 null。</summary>
    private static EquipmentCategory? CategoryOf(string? label) => (label ?? "").Trim() switch
    {
        "电铲" => EquipmentCategory.Shovel,
        "卡车" or "矿卡" => EquipmentCategory.Truck,
        "钻机" => EquipmentCategory.Drill,
        "推土机" => EquipmentCategory.Dozer,
        "前装机" or "液压铲" => EquipmentCategory.Loader,
        "平路机" => EquipmentCategory.Grader,
        "洒水车" or "水车" => EquipmentCategory.WaterTruck,
        _ => null,
    };

    /// <summary>甘特行序：主设备在前、卡车紧随其后（与样例花名册的排法一致）。</summary>
    private static int CategoryOrder(string? category) => (category ?? "").Trim() switch
    {
        nameof(EquipmentCategory.Drill) => 0,
        nameof(EquipmentCategory.Shovel) => 1,
        nameof(EquipmentCategory.Loader) => 2,
        nameof(EquipmentCategory.Truck) => 3,
        nameof(EquipmentCategory.Dozer) => 4,
        nameof(EquipmentCategory.Grader) => 5,
        nameof(EquipmentCategory.WaterTruck) => 6,
        _ => 7,
    };

    private static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        return $"{h:00}:{m:00}";
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
