// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/MonthlyStripSession.cs（逐行对应；仅命名空间适配；原 WorkLineSamples 在 Kylin 叫 Cad.WorkLineSamples）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 「量驱动月度采剥接续」的<b>一次会话</b>：把内核那条链
/// （剖面 → 排产 → 采排配对 → 外循环 → 导出契约 → 填短期月度计划）串成一次调用。
///
/// <para><b>为什么要有这一层</b>：内核那 8 个类各自可测、也确实都测过（158 条判据），
/// 但它们之间的<b>装配顺序、缺省值、失败该怎么说</b>不在任何一个类里 ——
/// 以前只存在于测试代码和我脑子里。窗口直接调内核的话，那套装配就散进 XAML 后台，
/// <b>脱 GUI 就验不了</b>（[[kernel-geometry-headless-harness]] 的教训）。</para>
///
/// <para><b>两条纪律</b>：
/// ① <b>不许崩</b> —— 每个入口都兜住，失败给得出原因；
/// ② <b>不许瞒</b> —— <see cref="MonthlyStripSessionResult.Log"/> 逐条记下每一步的结论
/// 和<b>每一处引擎替用户做的决定</b>（用了剖面里的 α、按层间名建了缺省物料表、没给排土位置…）。
/// 界面照着 Log 显示即可，不必自己再解释一遍。</para>
/// </summary>
public sealed class MonthlyStripSessionInput
{
    // ── 备料：岩量剖面 ───────────────────────────────────────────────────────

    /// <summary>中间文件路径（「生成采区台阶面」落的那份）。<see cref="Rock"/> 给了就不读文件。</summary>
    public string ProfilePath = "";
    /// <summary>直接给岩量剖面（跑完扫描还在内存里时用，省一次读盘）。</summary>
    public RockProfile? Rock;
    /// <summary>
    /// 指纹核对基准。给了就逐项比，对不上<b>拒收</b>，绝不拿过期剖面排产。
    /// <para><b>刻意无入口</b>：这是**调用方**（判据/台架/将来的"按当前模型核对"入口）用的核对基准，
    /// 不是人手填的参数 —— 指纹是算出来的，让人填等于让人绕过核对。</para>
    /// </summary>
    public ProfileProvenance? Expect;

    // ── 备料：排土位置 ───────────────────────────────────────────────────────

    /// <summary>排土条带的位置清单（`DumpStripPlanner` 的产物）。</summary>
    public IReadOnlyList<DumpStripPlanner.Cell>? DumpCells;
    /// <summary>已经转好的位置清单（给了就不再从 <see cref="DumpCells"/> 转）。</summary>
    public List<DumpSlot>? Slots;
    public string DumpName = "";
    // 下面两个是 DumpCells → DumpSlot 那一步的**转换口径**，不是计划选项：
    // 内排与否、从哪个月起可用，本来由排土场台账/采空区形成时机决定。
    // 【刻意无入口】给调用方（判据/台架/将来接台账那一步）用；界面上让人手填只会造出第二套口径。
    public bool   DumpIsInternal;
    public int    DumpAvailableFromMonth = 1;
    public double DumpHaulKm;
    /// <summary>工作线几何 —— 把排土位置投到推进轴 u 上用。缺了外循环就只能用静态运距。</summary>
    public IReadOnlyList<WorkLineSamples>? WorkLines;

    // ── 排产规则 ────────────────────────────────────────────────────────────

    /// <summary>逐月煤量目标（万t）。<b>这是整条链唯一的驱动量</b>，必须给。</summary>
    public double[] CoalTargetWt = Array.Empty<double>();
    /// <summary>逐月逐层煤量目标（R36）。不给就由引擎按露煤可行性摊，并<b>打标</b>。</summary>
    public double[][] CoalTargetBySeam = Array.Empty<double[]>();
    /// <summary>备采保有月数 N（R35）。</summary>
    public int LookaheadMonths = 3;
    public StripPace Pace = StripPace.Level;
    /// <summary>期初工作帮姿态（R40）。生产接续必须 true 或显式给 <see cref="InitialBenchX"/>。</summary>
    public bool StartInSteadyState = true;
    public double[] InitialBenchX = Array.Empty<double>();
    /// <summary>逐月剥离能力上限（m³）。<b>0 = 该月不能剥</b>；空数组 = 不限。</summary>
    public double[] StripCapM3 = Array.Empty<double>();
    public double RatioCeiling;
    public double RecoveryTotalWt;
    public double CoalStartWt;
    /// <summary>台阶坡面角 α（°）。<b>0 = 用剖面指纹里的那个</b>（推荐，那是扫描时真用的口径）。</summary>
    public double AlphaDeg;
    public double ZDatum;
    public PairingStrategy Strategy = PairingStrategy.MinHaul;
    /// <summary>逐层间物料表。null = 按层间名建一份缺省表，并在 Log 里说清。</summary>
    public GapMaterial[]? Materials;

    // ── 外循环与运距 ────────────────────────────────────────────────────────

    /// <summary>
    /// 有路网存档就用<b>真路网</b>算逐月 O-D 运距（默认开）。
    /// <para>装不上/一笔都解不出来时<b>自动退回</b> <see cref="RoadTortuosity"/> 那套几何兜底，
    /// 并把原因和命中率写进 Log —— 「接了真路网」和「接了但没命中」的数一模一样，不写没人分得出。</para>
    /// </summary>
    /// <para><b>刻意无入口</b>：默认开，装不上/一笔没命中会自动退兜底并写 Log ——
    /// 没有"故意关掉真路网"这种现场需求；关掉只会让运距悄悄变差而看不出来。判据/台架可显式置 false 做对照。</para>
    public bool UseRoadNetwork = true;
    /// <summary>
    /// 源/汇吸附到路网节点的最大距离（m）。超了判"落不上"。
    /// <para><b>0 = 由路网自己推</b>（2 × 中位节点间距，夹在 [50,1000]）—— 推荐。
    /// 固定数在密路网上过松（连上不该连的，运距偏乐观）、在粗路网上过紧
    /// （几乎全落不上 = 等于没接路网）。填非 0 即覆盖，Log 里会说是谁定的。</para>
    /// </summary>
    public double RoadSnapRadiusM;

    /// <summary>
    /// 兜底迂回系数（平距 → 实际路程的放大）。<b>0 = 由路网实测推</b>，没路网则用
    /// <see cref="DefaultTortuosity"/>。填非 0 即覆盖，Log 里会说是谁定的。
    ///
    /// <para><b>为什么该自动推</b>：这个系数管的是<b>落不到路网节点的那些 O-D</b>，
    /// 可它们走的还是<b>同一张路网</b> —— 那张网自己就量得出迂回系数
    /// （<c>RoadHaulProvider.MeasuredTortuosity</c>：网上距离 ÷ 直线距离）。
    /// 拿实测值去兜底，比拿 1.3 这个猜测去兜底严格地好。
    /// 上一版只把两者的差**报**出来（"两者差得多时兜底运距会系统性偏"），
    /// 报了却照旧按 1.3 算 —— 那句话等于自己承认在偏。</para>
    ///
    /// <para>与「内排退距」那条<b>不同类</b>，别混：退距的几何推导是个<b>下限估计</b>，
    /// 真值取决于现场作业习惯，所以只报不覆盖；迂回系数是<b>对同一个量的实测</b>，
    /// 没有"另一种口径"的问题。吸附半径也是这样处理的（0 = 自推，推荐）。</para>
    /// </summary>
    public double RoadTortuosity;

    /// <summary>没有路网可测时的兜底迂回系数。</summary>
    public const double DefaultTortuosity = 1.3;

    /// <summary>
    /// 提升当量（m 平距 / m 高差）。<b>null = 用 <see cref="HaulModel"/> 的默认</b>（上坡 12 / 下坡 +3）。
    ///
    /// <para><b>⚠ 两边都不是"台账"，都是缺省值</b>（2026-08-06 核过）：
    /// <c>RoadLib.TruckProfile</c> 在所有调用点上都是 <c>TruckProfile.Default</c> ——
    /// 没有任何配置或数据库把它填过；库里 <c>equipment_model</c> 只有载重 <c>load_t</c>，
    /// <b>没有坡阻系数</b>。所以这不是"我的两本台账打架、挑一本"，
    /// 而是<b>两个都是占位数，现场得给一个真的</b>，填在这里。</para>
    ///
    /// <para><b>为什么要能填</b>：这两个系数的注释上写着〔待现场核准〕，而
    /// <c>RoadLib.TruckProfile</c> 那套台账给的是上坡 6 / 下坡 <b>−2（减免）</b> ——
    /// 同一件事两个来源、下坡连正负号都相反。<b>核准之后总得填得进来</b>，
    /// 否则"待核准"就是个永远兑现不了的说法。</para>
    ///
    /// <para><b>下坡填负数 = 按减免算</b>（路网缺省值那套口径）。填 0 = 下坡免费 ——
    /// 注意那是<b>第三种口径</b>，两套台账都不是这么说的，选它要有理由。</para>
    /// </summary>
    public double? UphillEquivalent, DownhillEquivalent;
    public double InternalClearanceM = 200;
    public double VoidFillFactor = 0.9;
    public int    MaxIterations = 5;

    // ── 下游 ────────────────────────────────────────────────────────────────

    /// <summary>放坡参数（喂三维层体）。不给就只填剖面推得出来的台阶高，其余如实缺着。</summary>
    public ExportGeometry? Geometry;
    /// <summary>首月序号（计划标签 M01/M07…）。</summary>
    public int StartMonth = 1;
    /// <summary>
    /// 逐月有效作业日。空 = 不给（会记 Issue <b>I9</b>：三维模拟按<b>缺省 25 天</b>折算卸点通过能力）。
    /// <para>长度 1 = 各月相同；其它长度不符时<b>整个忽略</b>，不截断不补齐。
    /// 真作业历在「短期生产计划编制」的工作历里，本通道不造。</para>
    /// </summary>
    public double[] Workdays = Array.Empty<double>();
    public string PlanName = "量驱动月度采剥接续";
    /// <summary>
    /// 煤去向（洗煤厂/煤仓…）。不给就走导入侧缺省。
    /// <para><b>刻意无入口</b>：煤的去向在本仓库只有一个来源 —— 图上指的煤卸点（`CoalSinkPoint`）
    /// 与装卸点台账。在这儿再开一个输入口就是第二套"煤去哪"。</para>
    /// </summary>
    public CoalDestination? CoalTo;

    public int MonthCount => CoalTargetWt?.Length ?? 0;

    /// <summary>浅拷贝（派生比选按方案改几个规则字段时用）。数组按引用共享 —— 各方案只读不写。</summary>
    internal MonthlyStripSessionInput MemberwiseClone2()
        => (MonthlyStripSessionInput)MemberwiseClone();
}

/// <summary>派生比选的产物：多套方案 + 归因 + 推荐，外加装配层要说的那些话。</summary>
public sealed class MonthlyStripDeriveResult
{
    public bool   Success;
    public string Error = "";
    public readonly List<string> Log = new();
    public ScheduleDerivationResult? Derivation;
    public RockProfile? Rock;
    /// <summary>装派生用的那份基准输入 —— 选中某套后拿它 + 该套的规则取值重跑完整链路。</summary>
    public MonthlyStripSessionInput? BaseInput;

    public IReadOnlyList<ScheduleScheme> Schemes
        => Derivation?.Schemes ?? (IReadOnlyList<ScheduleScheme>)Array.Empty<ScheduleScheme>();
    /// <summary>
    /// 推荐方案。<b>直接用内核挑的那个</b>（<c>ScheduleDerivationResult.Recommended</c>）——
    /// 规则是<b>可行优先、再比分</b>。
    /// <para>⚠ 别自己写成 <c>Schemes[0]</c>：<c>Schemes</c> 是<b>生成顺序</b>不是分数顺序，
    /// 那样会推荐出一套碰巧排在最前面的方案 —— 它有真实的分数、看上去完全正常，没人会发现。
    /// 更糟的是它会绕过"可行优先"，把一套分高但<b>排不下</b>的方案推给用户。</para>
    /// </summary>
    public ScheduleScheme? Recommended => Derivation?.Recommended;

    public override string ToString()
        => !Success ? "✗ " + Error
         : $"{Derivation!.Attempted} 套里解出 {Schemes.Count} 套 · {Derivation.DistinctCount} 个不同答案"
         + (Recommended != null ? $" · 推荐「{Recommended.AxisText}」{Recommended.Score:0.0} 分" : "");
}

/// <summary>一次会话的全部产物 + 逐步结论。</summary>
public sealed class MonthlyStripSessionResult
{
    public bool   Success;
    public string Error = "";

    /// <summary>逐步结论与<b>每一处引擎替用户做的决定</b>。界面直接显示这一列。</summary>
    public readonly List<string> Log = new();

    /// <summary>跑这一次用的输入 —— 滚动重排要拿它当基准，别让调用方自己保管（保管丢了就只能重填一遍）。</summary>
    public MonthlyStripSessionInput? Input;
    /// <summary>滚动重排的偏差账（首次排产为 null）。</summary>
    public ReplanResult? Replan;

    /// <summary>本次用的真路网（null = 没接上）。排产后可从它读实测迂回系数。</summary>
    internal RoadHaulProvider? Road;

    public RockProfile?      Rock;
    public CoupledPlanResult? Coupled;
    public MinePlanExport?   Export;
    public ImportResult?     Import;
    /// <summary>装配好的短期月度计划。<b>还没入库</b> —— 入库要显式调 <see cref="MonthlyStripSession.Confirm"/>。</summary>
    public ShortTermPlan?    Plan;

    public MonthlyScheduleResult? Schedule => Coupled?.Schedule;
    public DumpAllocationResult?  Dump     => Coupled?.Dump;

    /// <summary>阻断性问题（有一条就不能当"确定方案"入库）。</summary>
    public List<ImportIssue> Blocking
        => Import?.Issues.Where(i => i.Blocking).ToList() ?? new List<ImportIssue>();

    /// <summary>能不能确定入库：跑通了、导出侧判可行、导入侧没有阻断问题。</summary>
    public bool CanConfirm => Success && Plan != null && (Import?.Executable ?? false);

    public override string ToString()
    {
        if (!Success) return "✗ " + Error;
        var e = Export!;
        return $"{e.Months.Count} 个月 · 煤 {e.TotalCoalWanT:0.0}万t · 岩 {e.TotalStripWanM3:0.0}万m³ · "
             + $"剥采比 {e.OverallRatio:0.00} · 内排率 "
             + (e.OverallInternalRatePct < 0 ? "未跑配对" : $"{e.OverallInternalRatePct:0.0}%")
             + $" · {(CanConfirm ? "可确定" : $"◆{Blocking.Count} 条阻断")}";
    }
}

/// <summary>
/// 会话执行器。<b>纯计算 + 一个显式入库动作</b>，与界面无关，可脱 GUI 验收。
/// </summary>
public static class MonthlyStripSession
{
    /// <summary>
    /// 跑完整条链。<b>任何一步失败都返回结果对象而不是抛异常</b>，原因在
    /// <see cref="MonthlyStripSessionResult.Error"/> 与 <c>Log</c> 里。
    /// </summary>
    public static MonthlyStripSessionResult Run(MonthlyStripSessionInput? inp)
    {
        var r = new MonthlyStripSessionResult { Input = inp };
        // 走 Fail 而不是直接 return —— 否则这一条失败在 Log 里是空的，界面就只剩一个空窗口。
        if (inp == null) { r.Error = "没有输入（调用方给了 null）"; return Fail(r); }

        try { return RunCore(inp, r); }
        catch (Exception ex)
        {
            // 兜到这里说明有没想到的路径 —— 把它说清楚，别让界面看见一个空窗口。
            r.Success = false;
            r.Error = $"内部错误（{ex.GetType().Name}）：{ex.Message}";
            r.Log.Add("✗ " + r.Error);
            return r;
        }
    }

    private static MonthlyStripSessionResult RunCore(MonthlyStripSessionInput inp, MonthlyStripSessionResult r)
    {
        if (!Prepare(inp, r, out var coupled)) return r;
        return Solve(inp, r, coupled!);
    }

    /// <summary>
    /// <b>派生比选</b>：按「规则取值的组合」穷举出多套方案，各自求最优，打分排序 + 归因。
    ///
    /// <para><b>方案 = 规则取值的组合，不是解的枚举</b> —— 拉绳在给定规则下是唯一最优，
    /// 所以"多解"只能来自规则本身的取值不同。这样每套仍是该组合下的最优，
    /// 而且方案间的差异<b>可归因到具体哪条规则</b>（往求解器里加随机性出来的方案没法解释）。</para>
    ///
    /// <para>产物是<b>比选表，不是可执行计划</b>。选定某套后要调 <see cref="RunScheme"/>
    /// 把它跑成完整链路。</para>
    /// </summary>
    public static MonthlyStripDeriveResult Derive(MonthlyStripSessionInput? inp,
                                                  ScheduleAxes? axes = null,
                                                  DerivationWeights? weights = null)
    {
        var d = new MonthlyStripDeriveResult();
        if (inp == null) { d.Error = "没有输入（调用方给了 null）"; d.Log.Add("✗ " + d.Error); return d; }
        try
        {
            // 备料与单套排产共用同一份 —— 两边各准备一次，缺省值迟早会漂。
            var prep = new MonthlyStripSessionResult();
            bool ready = Prepare(inp, prep, out CoupledPlanInput? coupled);
            d.Log.AddRange(prep.Log);
            d.Rock = prep.Rock;
            if (!ready) { d.Error = prep.Error; return d; }

            axes ??= new ScheduleAxes();
            weights ??= new DerivationWeights();
            d.Log.Add($"⑥ 派生 {axes.SchemeCount} 套（N {axes.Lookahead.Length} 值 × 剥离节奏 "
                    + $"{axes.StripPaces.Length} × 采煤节奏 {axes.CoalPaces.Length}"
                    + (axes.PairingStrategies is { Length: > 0 } ? $" × 配对策略 {axes.PairingStrategies.Length}" : "")
                    + "）");

            // ⚠「采煤节奏」与「工作历 × 作业组织」**算的是同一件事** —— 都产出逐月煤量分布。
            //   几何侧的 Shape() 是个**没有物理驱动的形状函数**（线性 ramp、总量守恒）；
            //   组织侧是 年目标 × DispatchShape × (工作日/标准工作日) × 设备可用率（驱动是真的）。
            //   逐月煤量已经由真工作历摊出来时，再开这条轴 = **把同一个决定做两遍**。
            //
            //   上一版这里"只报不拦"，理由写的是「有没有真工作历，本层看不到」——
            //   **那句话是错的**：`MonthlyStripSessionInput.Workdays` 就在本层，
            //   调用方给了真作业日它就非空。看得到，就该按看到的办（§七 落地建议①）。
            bool hasCalendar = inp.Workdays is { Length: > 0 } && inp.Workdays.Any(w => w > 0);
            if (hasCalendar && axes.CoalPaces is { Length: > 1 } && !axes.CoalPacesExplicit)
            {
                int was = axes.CoalPaces.Length;
                axes = axes.WithCoalPaces(new[] { CoalPace.Balanced });
                d.Log.Add($"   ⑥ 本次给了**真工作历**（{inp.Workdays.Count(w => w > 0)} 个月有作业日）"
                        + $"，「采煤节奏」轴由 {was} 值收成 1（只留「均衡」）—— "
                        + "逐月煤量已经由工作历摊过一遍，这条轴再摊就是**同一个决定做两遍**"
                        + "（见 docs/短期剥采排功能分组.md §七）。要保留请显式指定该轴。");
            }
            else if (axes.CoalPaces is { Length: > 1 })
                d.Log.Add("   ⚠「采煤节奏」这条轴会**重排逐月煤量**，与「派生计划方案」的"
                        + "「工作历 × 作业组织」是同一件事的两种做法。"
                        + (hasCalendar
                           ? "本次给了真工作历、但该轴是**显式指定**的，按你说的办 —— 注意结果里这一维会与工作历重复。"
                           : "本次**没有**真工作历，所以它是正当的兜底形状函数；"
                             + "等接上工作历后建议只留「均衡」")
                        + "（见 docs/短期剥采排功能分组.md §七）。");

            var r = ScheduleDeriver.Derive(coupled!.Schedule, axes, weights, coupled.Dump);
            d.Derivation = r;
            if (!r.Success)
            { d.Error = "派生失败：" + (r.Error.Length > 0 ? r.Error : "内核没给原因"); d.Log.Add("✗ " + d.Error); return d; }

            // ★ 逐套校配对结果 —— **这才是当初给 `DumpAllocationResult` 加自检的理由**。
            //   `ScheduleDeriver` 直接拿它算运输功/内排率两维的分，不经过契约层。
            //   而且打分是**跨方案归一**的（`NormLow/NormHigh` 取 list.Min/Max）：
            //   **一套的坏数会把 min/max 拉走，进而扭曲【所有】方案的分** —— 污染不是局部的，
            //   所以这里宁可整体拦下，也不能只把那一套摘掉接着排名。
            //   （单套排产那条路上的同一道闸在 `Solve` 里；`Derive` 不走 `Solve`，得单独接。）
            var dirty = new List<string>();
            foreach (var s in r.Schemes.Where(x => x.Dump != null))
                foreach (var b in s.Dump!.Validate())
                { dirty.Add($"「{s.AxisText}」{b}"); break; }      // 每套报一条就够，不刷屏
            foreach (var b in dirty) d.Log.Add("   ✗ 配对结果不自洽：" + b);
            if (dirty.Count > 0)
            {
                d.Error = $"{dirty.Count} 套方案的采排配对结果不自洽 —— "
                        + "打分是跨方案归一的，坏数会扭曲**所有**方案的排名，不能只摘掉那几套接着比";
                d.Log.Add("✗ " + d.Error);
                return d;
            }

            foreach (var n in r.Notes) d.Log.Add("   " + n);
            if (r.Failed.Count > 0)
                d.Log.Add($"   ⚠ {r.Failed.Count} 套解不出来（**不静默丢**，见失败清单）");
            if (r.CollapsedAxes.Count > 0)
                d.Log.Add($"   ⚠ 塌轴：{string.Join("、", r.CollapsedAxes)} —— "
                        + $"{r.Attempted} 套只有 {r.DistinctCount} 个不同答案，别以为是在这么多选项里挑");
            foreach (var a in r.Attribution)
                d.Log.Add($"   归因 {a.Axis}：最优取值「{a.Best}」· 跨度 {a.Spread:0.0} 分"
                        + (a.Spread < 1.0 ? "（**这条轴几乎不影响结果**）" : ""));

            d.BaseInput = inp;
            d.Success = true;
            d.Log.Add("⑦ " + d);
            return d;
        }
        catch (Exception ex)
        {
            d.Success = false;
            d.Error = $"内部错误（{ex.GetType().Name}）：{ex.Message}";
            d.Log.Add("✗ " + d.Error);
            return d;
        }
    }

    /// <summary>
    /// 把比选表里<b>选中的那一套</b>跑成完整链路（配对 + 外循环 + 契约 + 月度计划）。
    ///
    /// <para><b>不复用派生阶段那次求解</b>：派生为了快，外循环<b>肯定没跑</b>（27 套 × 5 轮不现实），
    /// 配对也可能没跑。拿那份半成品往下游送，用户会得到一份
    /// 「比选表上写着 A、实际执行的是 B」的计划 —— 而且两个数都"对"，谁也解释不了谁。
    /// 所以这里按选中方案的<b>规则取值</b>重跑一遍完整链路：慢一点，但两边一定是同一个东西。</para>
    /// </summary>
    public static MonthlyStripSessionResult RunScheme(MonthlyStripDeriveResult? d, ScheduleScheme? scheme)
    {
        var r = new MonthlyStripSessionResult();
        if (d == null || !d.Success || d.BaseInput == null)
        { r.Error = "没有可用的派生结果 —— 先跑一次「派生比选」"; return Fail(r); }
        if (scheme == null) { r.Error = "没有选中任何方案"; return Fail(r); }

        var run = Run(CloneForScheme(d.BaseInput, scheme));
        run.Log.Insert(0, $"⓪ 按比选表里选中的「{scheme.AxisText}」**重跑**完整链路 —— "
                        + "派生阶段没跑外循环，不能拿那份半成品当计划");
        // 比选表上的量是派生阶段算的（没跑外循环）。重跑后可能不一样 ——
        // **只有真不一样时才说**，否则一条"差异来自…"贴在两个相同的数上，只会让人以为哪里错了。
        if (run.Success && run.Export != null)
        {
            double der = scheme.TotalRockM3 / 1e4, got = run.Export.TotalStripWanM3;
            run.Log.Add(Math.Abs(der - got) <= Math.Max(0.05, Math.Abs(der) * 0.005)
                ? $"◆ 与比选表一致：剥离 {got:0.0}万m³（外循环没有改变这一套的量）"
                : $"◆ 比选表里这一套是 {der:0.0}万m³（派生阶段，**未跑外循环**）；重跑后 {got:0.0}万m³。"
                + "差异来自外循环放开内排容量 —— **以重跑的这份为准**。");
        }
        return run;
    }

    /// <summary>
    /// <b>滚动重排</b>：拿「上期计划 + 实绩」重排剩下的月份，跑<b>完整</b>链路。
    ///
    /// <para><b>为什么不直接调 <c>RollingReplan.Run</c></b>：它只跑到调度器为止，
    /// 配对/外循环/契约/月度计划都没有 —— 而且它自己就注明「上期的外部累计上限按原月轴算，
    /// 重排后已清空，需重跑外循环重给」。所以这里用它做<b>偏差账 + 输入调整</b>，
    /// 后半段照旧走 <see cref="Run"/>。</para>
    ///
    /// <para><b>欠剥比欠采危险</b>：欠采是这个月少卖点煤，欠剥是<b>下个月没煤可采</b>。
    /// 重排会把欠账压进后续月份 —— 压不下去时硬校核会红，那正是要看的。</para>
    /// </summary>
    /// <param name="previous">上一次排产的结果（要带 <see cref="MonthlyStripSessionResult.Input"/>）。</param>
    /// <param name="actual">实绩。<c>MonthsElapsed</c> 决定从第几个月起重排。</param>
    /// <param name="remainingTargets">剩余月份的煤量目标；空 = 沿用上期计划里剩下那几个月的。</param>
    public static MonthlyStripSessionResult Replan(MonthlyStripSessionResult? previous,
                                                   ActualToDate? actual,
                                                   double[]? remainingTargets = null)
    {
        var r = new MonthlyStripSessionResult();
        if (previous?.Input == null || !previous.Success)
        { r.Error = "没有可重排的上期计划 —— 先跑一次排产"; return Fail(r); }
        if (actual == null) { r.Error = "没有实绩，无法重排（首次排产直接用「排产」）"; return Fail(r); }

        try
        {
            var basePrev = previous.Input!;
            // 用内核那套做偏差账 + 输入调整（7 条判据钉在它上面，别在这儿另写一份）
            var (adj, rep) = RollingReplan.Prepare(
                BuildScheduleInput(basePrev, previous.Rock!, previous.Schedule),
                previous.Schedule, actual, remainingTargets);
            r.Replan = rep;
            // 这里**不必**再记 `rep.Notes`：`RollingReplan.Prepare` 里唯一那条设 Error 的路径
            // 排在所有 `Notes.Add` 之前，所以走到这儿时 Notes 必空（判据 S30 钉着）。
            // Prepare 成功、后续排产失败的那种情况，下面 `run.Log.Insert` 是**无条件**执行的，
            // 偏差账照样进日志。
            if (!string.IsNullOrEmpty(rep.Error)) { r.Error = "重排失败：" + rep.Error; return Fail(r); }

            // 把 Prepare 调过的那几项搬回会话输入，其余原样沿用
            var inp = basePrev.MemberwiseClone2();
            inp.CoalTargetWt = adj.CoalTargetWt;
            inp.StripCapM3 = adj.StripCapM3;
            inp.CoalStartWt = adj.CoalStartWt;
            inp.InitialBenchX = adj.InitialBenchX;
            inp.StartInSteadyState = adj.StartInSteadyState;
            // 月标签要接着上期往下排，否则重排出来的表又从 M01 开始，和上期对不上
            inp.StartMonth = basePrev.StartMonth + rep.MonthsElapsed;
            inp.PlanName = $"{basePrev.PlanName} · 第{rep.MonthsElapsed}月后重排";

            var run = Run(inp);
            // ★ `Prepare` 只备料、**不设 Success/Plan** —— 那两个是 `RollingReplan.Run` 设的，
            //   而本层走的是自己的 `Run`（要跑完整链路：外循环+配对+契约，内核那个只排产）。
            //   不补这一步的话 `rep.Success` 恒为 false，于是**每次成功重排都会打出一句
            //   「滚动重排失败：」**（`Error` 还是空的），而它顶掉的正是这一页存在的理由 ——
            //   采出/剥离达成度与剥离欠账。判据 S36。
            rep.Success = run.Success;
            rep.Plan = run.Schedule;
            if (!run.Success && string.IsNullOrEmpty(rep.Error)) rep.Error = run.Error;

            run.Replan = rep;
            run.Log.Insert(0, $"⓪ 滚动重排：已过 {rep.MonthsElapsed} 个月，重排剩余 {inp.MonthCount} 个月"
                            + $"（月标签从 M{inp.StartMonth:00} 起接着上期）");
            run.Log.Insert(1, "　 " + rep.Summary());
            foreach (var n in rep.Notes) run.Log.Insert(2, "　 " + n);
            return run;
        }
        catch (Exception ex)
        {
            r.Success = false;
            r.Error = $"内部错误（{ex.GetType().Name}）：{ex.Message}";
            r.Log.Add("✗ " + r.Error);
            return r;
        }
    }

    /// <summary>照会话输入重建一份调度器输入（滚动重排要拿它当 <c>RollingReplan.Prepare</c> 的基准）。</summary>
    private static MonthlyScheduleInput BuildScheduleInput(MonthlyStripSessionInput inp, RockProfile rock,
                                                           MonthlyScheduleResult? prev)
    {
        double alpha = inp.AlphaDeg > 0 ? inp.AlphaDeg : (rock.Provenance?.AlphaDeg ?? 15);
        return new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = alpha, ZDatum = inp.ZDatum, Pace = inp.Pace,
            CoalTargetWt = inp.CoalTargetWt, CoalTargetBySeam = inp.CoalTargetBySeam,
            StripCapM3 = inp.StripCapM3, LookaheadMonths = inp.LookaheadMonths,
            RecoveryTotalWt = inp.RecoveryTotalWt, RatioCeiling = inp.RatioCeiling,
            CoalStartWt = inp.CoalStartWt,
            StartInSteadyState = inp.StartInSteadyState, InitialBenchX = inp.InitialBenchX,
        };
    }

    /// <summary>按某套方案的规则取值复制一份输入。</summary>
    private static MonthlyStripSessionInput CloneForScheme(MonthlyStripSessionInput src, ScheduleScheme s)
    {
        var c = (MonthlyStripSessionInput)src.MemberwiseClone2();
        c.LookaheadMonths = s.Lookahead;
        c.Pace = s.Strip;
        if (s.Pairing.HasValue) c.Strategy = s.Pairing.Value;
        // 采煤节奏：**与派生时用同一个 Shape()**，不另写一份（两份迟早漂，而漂了就是"表里不一"）
        c.CoalTargetWt = ScheduleDeriver.Shape(src.CoalTargetWt, s.Coal, new ScheduleAxes().PaceSkew);
        // R41：派生 N 时回采煤量必须跟着走 —— 钉死回采量只动 N，
        // 大 N 的露煤需求会顶穿 R34 天花板被静默夹回去，整条「剥离节奏」轴会塌成一个值。
        if (src.RecoveryTotalWt > 0)
            c.RecoveryTotalWt = src.RecoveryTotalWt / Math.Max(1, src.LookaheadMonths) * Math.Max(1, s.Lookahead);
        c.PlanName = $"{src.PlanName} · {s.AxisText}";
        return c;
    }

    /// <summary>
    /// 备料（①剖面 ②煤量目标 ③排土位置 ④物料表 ⑤外循环输入 + 真路网）。
    /// <para><b>单套排产与派生比选共用这一份</b> —— 两边各准备一次的话，缺省值和告警迟早会漂，
    /// 而漂了以后「比选表上写着 A、实际执行的是 B」这种事没人看得出来。</para>
    /// </summary>
    private static bool Prepare(MonthlyStripSessionInput inp, MonthlyStripSessionResult r,
                                out CoupledPlanInput? coupled)
    {
        coupled = null;
        // 工作线：调用方给的优先；没给就用中间文件里带着的（见下）。**只用局部量，不回写 inp**。
        IReadOnlyList<WorkLineSamples>? workLines = inp.WorkLines;
        // ── ① 岩量剖面 ──────────────────────────────────────────────────────
        var rock = inp.Rock;
        if (rock == null)
        {
            if (string.IsNullOrWhiteSpace(inp.ProfilePath))
            { r.Error = "既没有岩量剖面，也没给中间文件路径 —— 先跑一次「生成采区台阶面」把剖面落盘"; { Fail(r); return false; } }

            var cp = LoadProfileFile(inp.ProfilePath, inp.Expect, out string ferr, out string how,
                                     out var fileLines);
            if (cp == null) { r.Error = "读中间文件失败：" + ferr; { Fail(r); return false; } }
            if (how.Length > 0) r.Log.Add("   " + how);
            // 调用方没给工作线时，用中间文件里带着的那份 —— 界面上没有选工作线的地方，
            // 不接这一路的话推进轴永远缺席，逐月运距就永远不随推进变。
            // **不回写 inp**：`Run` 是纯的，`Derive` 还拿同一份输入派生好几套。
            if ((workLines == null || workLines.Count == 0) && fileLines != null)
            {
                workLines = fileLines;
                r.Log.Add($"   工作线取自同一份中间文件（{fileLines.Count} 条）—— "
                        + "推进轴由它定，排土位置才投得上去");
            }
            rock = cp.Rock;
            if (rock == null)
            { r.Error = "中间文件里只有煤剖面、没有岩剖面 —— 生成台阶面时台阶高给 0 会这样，重跑一次并填台阶高"; { Fail(r); return false; } }
            r.Log.Add($"① 剖面读自 {inp.ProfilePath}"
                    + (inp.Expect != null ? "（指纹已逐项核对）" : "（**未核指纹** —— 块体/工作线/面变过的话这份剖面就是陈的）"));
        }
        else r.Log.Add("① 剖面用内存里那份（刚扫完，未落盘）");

        if (!rock.Success) { r.Error = "岩量剖面本身是失败的：" + rock.Error; { Fail(r); return false; } }
        // 剖面自校：它是**从文件读回来的**，而汇总量与逐桶数据是分开维护的 —— 读盘后可能漂。
        // 漂了之后报表上的总岩量和实际排出来的量对不上，而每一步都"成功"。
        var pbad = rock.Validate();
        foreach (var b in pbad) r.Log.Add("① ✗ 剖面不自洽：" + b);
        if (pbad.Count > 0)
        { r.Error = $"岩量剖面自洽校核没过（{pbad.Count} 条）—— 拿它排产等于把错带进整条链"; { Fail(r); return false; } }
        r.Rock = rock;
        r.Log.Add($"   {rock.SeamCount} 层 · 台阶高 {rock.BenchHeight:0.##}m · "
                + $"标高格 {rock.LevelMin}..{rock.LevelMax} · 岩 {rock.TotalRockM3 / 1e4:0.0}万m³ · "
                + $"煤 {rock.TotalCoalWt():0.0}万t");

        // α：**只有 0 才是"没填"**。负数/≥90 是填错了，不许拿剖面里那个悄悄顶替 ——
        // 用户填 −5 却拿到一份按 20° 算出来的计划，每个数看上去都正常（S2b）。
        double alpha = inp.AlphaDeg;
        if (alpha < 0 || alpha >= 90)
        { r.Error = $"坡面角 α = {alpha:0.###}° 填错了（要 0<α<90；填 0 表示用剖面指纹里的那个）"; { Fail(r); return false; } }
        if (alpha == 0)
        {
            alpha = rock.Provenance?.AlphaDeg ?? 0;
            if (alpha > 0) r.Log.Add($"   α 用剖面指纹里的 {alpha:0.###}°（扫描时真用的口径）");
        }
        if (alpha <= 0 || alpha >= 90)
        { r.Error = $"坡面角 α 取不到（剖面指纹里也没有）—— 请显式填"; { Fail(r); return false; } }

        // ── ② 煤量目标 ──────────────────────────────────────────────────────
        if (inp.MonthCount <= 0)
        { r.Error = "没有逐月煤量目标 —— 这是整条链唯一的驱动量，必须给"; { Fail(r); return false; } }
        double totalTarget = inp.CoalTargetWt.Sum();
        if (totalTarget <= 1e-9)
        { r.Error = "逐月煤量目标全是 0 —— 排不出任何东西"; { Fail(r); return false; } }
        if (totalTarget > rock.TotalCoalWt() + 1e-6)
            r.Log.Add($"⚠ 目标合计 {totalTarget:0.0}万t 已超过剖面里的可采总量 {rock.TotalCoalWt():0.0}万t"
                    + " —— 后面会顶到煤前界上限，硬校核会报出来");
        r.Log.Add($"② 煤量目标 {inp.MonthCount} 个月合计 {totalTarget:0.0}万t · N={inp.LookaheadMonths} · "
                + $"剥离节奏 {PaceText(inp.Pace)} · 期初"
                + (inp.InitialBenchX.Length > 0 ? "按显式给的台阶位置"
                   : inp.StartInSteadyState ? "按稳态推（已建立 N 个月超前）"
                   : "**裸起始工作帮**（= 基建剥离全压第 1 个月，只有新建矿才对，见 R40）"));

        // ── ③ 排土位置 ──────────────────────────────────────────────────────
        var slots = inp.Slots;
        if (slots == null || slots.Count == 0)
        {
            slots = DumpSlotAdapter.ToSlots(inp.DumpCells, inp.DumpName, inp.DumpIsInternal,
                                            inp.DumpAvailableFromMonth, inp.DumpHaulKm);
            if (slots.Count > 0) r.Log.Add("③ " + DumpSlotAdapter.Summary(slots));
        }
        else r.Log.Add($"③ 排土位置用外面给的 {slots.Count} 个");

        if (slots.Count == 0)
        { r.Error = "没有排土位置 —— 先在「排土条带」里生成位置清单（剥下来的岩总得有地方去）"; { Fail(r); return false; } }

        // ── ④ 物料表 ────────────────────────────────────────────────────────
        var mats = inp.Materials;
        if (mats == null || mats.Length == 0)
        {
            mats = DefaultMaterials(rock);
            r.Log.Add($"④ **物料表是按层间名建的缺省表**（{mats.Length} 项）—— "
                    + "「表土只能进表土堆场」这类硬约束在缺省表里是空的，要真约束就得填台账");
            // 逐项把假设摊开说。原来只写"ρ/Kr 取通用值"，听着像个小保留 ——
            // 而**覆岩那一项是按【表土】给的**（ρ1.8/Kr1.25），它是整个顶板以上的岩柱，
            // 现实里绝大部分是岩不是土。这不是四舍五入：
            //   · **运输功按吨算** ⇒ ρ 1.8 vs 2.5 差 39%，比选表的"运输功"这一维直接跟着偏；
            //   · **库容按 Kr 扣** ⇒ 1.25 vs 1.15 差 9%，直接改"排土场够不够用"。
            r.Log.Add("   " + string.Join(" · ", mats.Select(m => $"{m.Name}[{m.Code}] ρ{m.Density:0.##}/Kr{m.Kr:0.##}")));
            var ob = mats.Length > GapCode.Overburden ? mats[GapCode.Overburden] : null;
            if (ob != null && ob.Code == "topsoil")
                r.Log.Add($"   ◆ 其中**「{ob.Name}」是按【表土】口径给的**（ρ{ob.Density:0.##}/Kr{ob.Kr:0.##}）——"
                        + "它其实是顶板以上的**整根岩柱**，现实里多半是岩。"
                        + "按岩（ρ2.5/Kr1.15）算的话**吨量高 39%、占容低 9%**："
                        + "前者直接改运输功与比选排名，后者直接改排土场够不够用。**填台账时先核这一项。**");
        }
        else if (mats.Length < GapCode.Count(rock.SeamCount))
        {
            r.Log.Add($"④ ⚠ 物料表只给了 {mats.Length} 项，本剖面有 {GapCode.Count(rock.SeamCount)} 个层间标签"
                    + " —— 缺的那几项配对时会退回缺省，导出契约里会带 Issue");
        }
        else r.Log.Add($"④ 物料表 {mats.Length} 项（用户台账）");

        // ── ⑤ 外循环 ────────────────────────────────────────────────────────
        var sched = new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = alpha, ZDatum = inp.ZDatum, Pace = inp.Pace,
            CoalTargetWt = inp.CoalTargetWt, CoalTargetBySeam = inp.CoalTargetBySeam,
            StripCapM3 = inp.StripCapM3, LookaheadMonths = inp.LookaheadMonths,
            RecoveryTotalWt = inp.RecoveryTotalWt, RatioCeiling = inp.RatioCeiling,
            CoalStartWt = inp.CoalStartWt,
            StartInSteadyState = inp.StartInSteadyState, InitialBenchX = inp.InitialBenchX,
        };
        coupled = new CoupledPlanInput
        {
            Schedule = sched,
            Dump = new DumpAllocationInput { Slots = slots, Materials = mats, Strategy = inp.Strategy },
            SlotU = DumpSlotAdapter.ProjectToAdvanceAxis(slots, workLines ?? Array.Empty<WorkLineSamples>()),
            SlotOffsetKm = DumpSlotAdapter.LateralOffsetsKm(slots, workLines ?? Array.Empty<WorkLineSamples>()),
            // 先按"用户填的，没填就兜底常数"起手；路网装上后若能实测且用户没指定，下面会改写。
            RoadTortuosity = inp.RoadTortuosity > 0
                           ? inp.RoadTortuosity : MonthlyStripSessionInput.DefaultTortuosity,
            InternalClearanceM = inp.InternalClearanceM,
            VoidFillFactor = inp.VoidFillFactor,
            MaxIterations = inp.MaxIterations,
        };
        // 煤容重：把"采出多少万t"折回采空区体积用，而采空区体积决定内排放得下多少 ——
        // 直接摆动内排率。**这个数不该写死**：各层容重这份剖面自己就带着（扫块体时逐层出来的），
        // 写死 1.35 会让"吨→方"与算吨时的口径对不上，采空区体积系统性偏，而每一步都"成功"。
        double rho = rock.EffectiveCoalDensity();
        if (rho > 0)
        {
            coupled.CoalDensity = rho;
            var d = new CoupledPlanInput();
            if (Math.Abs(rho - d.CoalDensity) > 0.005)
                r.Log.Add($"   煤容重按剖面实测 {rho:0.###} t/m³（体积加权）"
                        + $"，不是缺省的 {d.CoalDensity:0.###} —— 它决定采空区体积，进而决定内排放得下多少");
        }
        // 提升当量：填了就按填的走，并**留条说是谁定的** —— 这两个数直接摆动内排率，
        // 悄悄用默认值和悄悄用别人填的值一样糟。
        if (inp.UphillEquivalent.HasValue || inp.DownhillEquivalent.HasValue)
        {
            var d = new HaulModel();
            coupled.Haul = new HaulModel
            {
                Tortuosity = Math.Max(1.0, coupled.RoadTortuosity),
                UphillEquivalent = inp.UphillEquivalent ?? d.UphillEquivalent,
                DownhillEquivalent = inp.DownhillEquivalent ?? d.DownhillEquivalent,
            };
            r.Log.Add($"⑤ 提升当量按**外面指定**：上坡 {coupled.Haul.UphillEquivalent:0.#}"
                    + $" · 下坡 {coupled.Haul.DownhillEquivalent:0.#}"
                    + (coupled.Haul.DownhillEquivalent < 0 ? "（负 = 按**减免**算，路网缺省值那套口径）"
                     : coupled.Haul.DownhillEquivalent == 0 ? "（0 = 下坡免费 —— **两套台账都不是这么说的**）"
                     : "（正 = 按代价算）"));
        }
        if (workLines == null || workLines.Count == 0)
            r.Log.Add("⑤ ⚠ 没有工作线 —— 排土位置投不到推进轴上，"
                    + "内排启用时机与逐月运距只能走静态兜底（内排率会偏保守）。"
                    + "剖面若选 .case 容器，工作线本来就在里面；.mprof 不带");

        // 真路网：装得上就用，装不上如实说原因，照旧跑几何兜底。
        if (inp.UseRoadNetwork)
        {
            var road = RoadHaulProvider.TryLoadLatest(workLines, out string why);
            if (road != null)
            {
                // 吸附半径：用户显式填了就用他的，否则用路网自己推的 —— 两种都要说是谁定的。
                bool overridden = inp.RoadSnapRadiusM > 0;
                if (overridden) road.SnapRadiusM = inp.RoadSnapRadiusM;
                coupled.RoadHaul = road.AsCallback();
                r.Road = road;                       // 排产完要从它读实测迂回系数
                r.Log.Add("⑤ 真路网已接入：" + road.SourceLabel
                        + (why.Length > 0 ? "　" + why : ""));

                // 兜底迂回系数：用户填了就用他的，没填就用**这张网自己采出来的**。
                // 它管的是落不到节点的那些 O-D，而它们走的还是同一张网 —— 拿实测去兜底，
                // 严格地好过拿 1.3 这个猜测。（事后的 MeasuredTortuosity 在这儿还没有值，
                // 那是排产真去查过 O-D 之后才有的，用它做自动推会一次也生效不了。）
                if (inp.RoadTortuosity <= 0 && road.SampledTortuosity is > 0)
                {
                    coupled.RoadTortuosity = road.SampledTortuosity.Value;
                    if (coupled.Haul != null) coupled.Haul.Tortuosity = coupled.RoadTortuosity;
                    r.Log.Add("   " + road.SampledTortuosityNote + " —— 兜底运距按它算，不用缺省 "
                            + $"{MonthlyStripSessionInput.DefaultTortuosity:0.00}");
                }
                else if (inp.RoadTortuosity > 0)
                    r.Log.Add($"   迂回系数 {inp.RoadTortuosity:0.00}（**用户指定**）"
                            + (road.SampledTortuosity is > 0
                               ? $"，覆盖了按路网采的 {road.SampledTortuosity:0.00}" : ""));
                else r.Log.Add("   " + road.SampledTortuosityNote
                             + $" —— 兜底仍用缺省 {MonthlyStripSessionInput.DefaultTortuosity:0.00}");
                r.Log.Add("   " + (overridden
                    ? $"吸附半径 {road.SnapRadiusM:0}m（**用户指定**，覆盖了按路网推的 {road.DerivedSnapRadiusM:0}m）"
                    : road.SnapRadiusNote));
            }
            else r.Log.Add("⑤ 运距走几何兜底 —— " + why);
        }
        else r.Log.Add("⑤ 真路网已被显式关闭，运距走几何兜底");

        // 内排退距：把「按几何该是多少」算出来摆在旁边，供核准那条阈值时对照。
        // **不覆盖用户的值** —— 悄悄改掉一个直接决定内排率的参数，比让它偏着更糟。
        double derivedClear = DerivedInternalClearanceM(rock, alpha);
        if (derivedClear > 0)
        {
            double used = inp.InternalClearanceM;
            r.Log.Add($"   内排退距用 {used:0}m；**按几何推是 {derivedClear:0}m**"
                    + $"（= 工作帮水平投影 {rock.LevelMax - rock.LevelMin + 1} 级 × 台阶高 {rock.BenchHeight:0.##}m ÷ tan{alpha:0.#}°）"
                    + (Math.Abs(used - derivedClear) > derivedClear * 0.5
                       ? " —— **两者差得多，核准这条阈值时按现场实际取**" : ""));
        }

        return true;
    }

    /// <summary>备料之后的那一半：外循环 → 契约 → 月度计划。</summary>
    private static MonthlyStripSessionResult Solve(MonthlyStripSessionInput inp, MonthlyStripSessionResult r,
                                                   CoupledPlanInput coupled)
    {
        var rock = r.Rock!;
        var cr = CoupledMinePlanner.Solve(coupled);
        r.Coupled = cr;

        // ★ 逐轮记录要写在**失败判断之前**。
        //   原来这几行排在 return 之后 —— 于是"外循环没跑出任何可用结果"这句话
        //   底下**一个字的过程都没有**，而每一轮为什么不成，`cr.Iterations` 里明明都写着。
        //   失败那次恰恰是最需要过程的一次：能剥多少、排不排得下、哪一轮开始退，
        //   全在这几行里。（判据 S29）
        r.Log.Add($"⑤ 外循环 {cr.Iterations.Count} 轮 · "
                + (cr.Success ? (cr.Converged ? "已收敛" : $"**未收敛** —— {cr.ConvergenceNote}")
                              : $"**没跑出可用结果** —— {cr.ConvergenceNote}"));
        foreach (var it in cr.Iterations) r.Log.Add("   " + it);
        if (!string.IsNullOrEmpty(cr.HaulNote)) r.Log.Add("   运距口径：" + cr.HaulNote);

        if (!cr.Success || cr.Schedule == null)
        { r.Error = "排产失败：" + (string.IsNullOrEmpty(cr.Error) ? "内核没给原因" : cr.Error); return Fail(r); }

        // 兜底那部分用的迂回系数，可以用这张网自己量出来的那个校准 —— 比通用经验值贴合。
        // 同样**只报不覆盖**：它和提升当量一起决定内/外排的相对代价。
        if (r.Road != null)
        {
            r.Log.Add("   " + r.Road.TortuosityNote);

            // 提升当量：`HaulModel` 自己有一套，RoadLib 的车型缺省值也有一套 —— 同一件事两个来源。
            // 对不上就要说，尤其**下坡的正负号**：内排通常下坡、外排常爬升，符号反了内排率整体摆动。
            var (upT, downT, tnote) = r.Road.LiftEquivalentFromTruck();
            var hm = new HaulModel();
            r.Log.Add("   " + tnote);
            if (Math.Abs(upT - hm.UphillEquivalent) > 1e-6 || Math.Sign(downT) != Math.Sign(hm.DownhillEquivalent))
                r.Log.Add($"   ◆ 与本模块在用的（上坡 {hm.UphillEquivalent:0.#} · 下坡 {hm.DownhillEquivalent:0.#}）**对不上**："
                        + (Math.Sign(downT) != Math.Sign(hm.DownhillEquivalent)
                           ? "下坡**正负号相反**（这边当代价、路网缺省值当减免）—— 内排下坡、外排爬升，这一条直接摆动内排率；"
                           : "")
                        + "两套口径在算同一件事，请核准以哪一套为准。");
            // 事后实测 vs 真正用掉的那个值（不是 inp 里的 —— 没填时用的是采样值）。
            double used = coupled.RoadTortuosity;
            double? m = r.Road.MeasuredTortuosity;
            if (m is > 0 && used > 0 && Math.Abs(m.Value - used) > used * 0.15)
                r.Log.Add($"   ◆ 兜底用的迂回系数是 {used:0.00}，而**这一轮实际解出的 O-D 实测 {m:0.00}** —— "
                        + "落不到节点的那些 O-D 正按前者算，两者差得多时兜底运距会系统性偏。");
        }
        foreach (var n in cr.Schedule.Notes) r.Log.Add("   " + n);
        foreach (var c in cr.Schedule.Checks) r.Log.Add("   " + c);

        // 配对结果先自校一遍 —— 比选打分是**直接拿它**算的（运输功/内排率两维），
        // 那一步比契约层早，契约的自洽校核那时还没发生。
        if (cr.Dump != null)
        {
            var dbad = cr.Dump.Validate();
            foreach (var b in dbad) r.Log.Add("⑤ ✗ 配对结果不自洽：" + b);
            if (dbad.Count > 0)
            { r.Error = $"采排配对结果自洽校核没过（{dbad.Count} 条）—— 比选打分会照着歪的数排名"; return Fail(r); }
        }

        // ── ⑥ 导出契约 ──────────────────────────────────────────────────────
        // 排土侧台阶高/坡面角：**台账里其实有**。
        //   契约那句注释写着「`dump_site` 表无此列」—— **过期了**：该表现在带
        //   `bench_height_m` / `bench_slope_angle_deg` / `overall_slope_angle_deg`，
        //   实测两行真数据（北排土场 15m/38°、内排土场 12m/36°）。
        //   不接的话下游只能按垂直壁建排土层体，而这几个数就摆在库里。
        //   **只在调用方没显式给几何时才去查**（显式给的永远优先），并留条说是从哪来的。
        var geom = inp.Geometry;
        geom = FillDumpBenchFromLedger(geom, cr.Dump, r);

        // 把外循环结果一并传进去 —— 收敛说明/运距口径要跟着契约走，别让下游以为是真路网。
        var ex = MinePlanExport.Build(cr.Schedule, rock, cr.Dump, cr, geom);
        r.Export = ex;
        var bad = ex.Validate();
        foreach (var b in bad) r.Log.Add("⑥ ✗ 契约不自洽：" + b);
        if (bad.Count > 0)
        { r.Error = $"导出契约自洽校核没过（{bad.Count} 条）—— 不能拿一份自相矛盾的表往下游送"; return Fail(r); }
        r.Log.Add($"⑥ 契约自洽 · {ex.Months.Count} 月 / {ex.Flows.Count} 笔流 / {ex.Benches.Count} 条台阶");
        // ⚠ 契约的 Notes 里现在**含内核那份**（`MinePlanExport.Build` 原样转发过去了），
        //   而内核那份上面 ⑤ 段已经逐条打过 —— 不去重的话同一句会在日志里出现两遍
        //   （实测「工作帮推到剖面数据尽头」那条就打了两次）。**只补契约层新加的那些**。
        foreach (var n in ex.Notes)
            if (!cr.Schedule.Notes.Contains(n)) r.Log.Add("   " + n);

        // ── ⑦ 填短期月度计划 ────────────────────────────────────────────────
        var imp = MinePlanImporter.Import(ex, inp.CoalTo, inp.StartMonth, inp.Workdays);
        r.Import = imp;
        // 不必在这儿记 `imp.Issues`：`MinePlanImporter.Import` 的两条失败返回（契约为空 / 版本不匹配）
        // 都排在第一个 `Issues.Add` 之前，走到这儿时 Issues 必空（判据 S30 钉着）。
        // 那两句 Error 本身就是完整的诊断。
        if (!imp.Success)
        { r.Error = "填月度计划失败：" + imp.Error; return Fail(r); }

        var plan = new ShortTermPlan
        {
            Name = string.IsNullOrWhiteSpace(inp.PlanName) ? "量驱动月度采剥接续" : inp.PlanName,
            BenchHeightM = rock.BenchHeight,
        };
        foreach (var m in imp.Months) plan.Months.Add(m);
        int nGeom = MinePlanImporter.ApplyGeometry(ex, plan, imp);
        // ★ 备采储量必须回写进 plan.Mineable，否则 ShortTermScheduler 的 prepOk 恒 false、
        //   这套方案连同后面派生出来的每一套都判不过（详见 ApplyPreparedReserve 的注释）
        bool prepOk = MinePlanImporter.ApplyPreparedReserve(plan, imp);
        r.Plan = plan;

        r.Log.Add($"⑦ 月度计划「{plan.Name}」{plan.Months.Count} 行 · "
                + (prepOk
                    ? $"备采储量 {imp.PreparedReserveWanT:0.0}万t（几何算，已写入三量校核）"
                    : "◆ **备采储量算不出来**（契约里没有月行）—— 三量保有校核会判不过，"
                    + $"因为下限是 {plan.MinPreparedMonths:0.#} 个月而现在是 0")
                + " · "
                + (nGeom > 0 ? $"放坡参数 {nGeom} 项已写入（三维层体建真台阶）"
                             : "**没有放坡参数** —— 三维层体按垂直壁建"));
        foreach (var i in imp.Issues) r.Log.Add("   " + i);

        r.Success = true;
        if (!r.CanConfirm)
            r.Log.Add($"◆ 这份计划**不能确定入库**：{r.Blocking.Count} 条阻断性问题（见上）。"
                    + "改输入重跑，或先把阻断项处理掉。");
        return r;
    }

    /// <summary>
    /// 把跑出来的计划<b>确定入库</b>（写 <see cref="ShortTermSchemeStore"/>）——
    /// 这是本类唯一有副作用的动作，必须由界面显式调。
    ///
    /// <para><b>不可执行的计划一律拒绝入库</b>：确定簿是下游（作业计划 / 三维动态模拟 /
    /// 采运排一体化）唯一的事实来源，往里放一份有阻断问题的计划，
    /// 下游每一处都会拿它当真的用。</para>
    /// </summary>
    public static bool Confirm(MonthlyStripSessionResult? r, out string err)
    {
        err = "";
        if (r == null || !r.Success || r.Plan == null) { err = "没有可入库的计划（会话没跑成功）"; return false; }

        // ★ 确定入库的实现在 <see cref="ShortTermConfirmService"/> —— **全仓只有那一份**。
        //   本方法此前是三份实现里唯一完整的（带阻断校验、写台账），另外两个按钮
        //   （月度计划编制 / 派生计划方案）只写内存确定簿，关一次软件就没了。
        //   现在校验/写确定簿/写台账都在那边，几何链这边只负责**把自己的阻断项交上去**。
        var o = ShortTermConfirmService.Confirm(
            r.Plan, r.CanConfirm ? null : r.Blocking.Select(i => i.Text).ToList());
        if (!o.Ok) { err = o.Err; return false; }

        r.Log.Add($"✔ 已确定入库：「{r.Plan.Name}」—— 下游（作业计划 / 三维动态模拟 / 采运排一体化）自动接通");
        r.Log.Add(o.LedgerMonths > 0
            ? $"✔ 已写入月度计划台账 monthly_plan：{o.LedgerMonths} 个月 —— **下次开机仍在**。"
            : "◆ **没有写进月度计划台账**：" + (o.LedgerErr.Length > 0 ? o.LedgerErr : "没有可写的月份")
              + " —— 这份确定方案只活在本次会话里，关掉软件就没了。");
        return true;
    }

    // ★ WriteToLedger 已移到 ShortTermConfirmService —— 台账写入是「确定」这个动作的一部分，
    //   留在本类的话另外两个入口走不到它，就永远只有几何链那条路持久化（这次分工要治的正是这个）。
    //   下面的 TrySplitYearMonth 留在本类不动：它是**月序→年月**的换算口径（带 M13 滚年），
    //   自带判据 ConfirmToLedgerTests，与「确定」无关，服务那边反过来调它。

    /// <summary>
    /// 把方案的<b>月序</b>换成台账的 (年, 月)。
    ///
    /// <para><b>⚠ <see cref="MonthPeriod.Month"/> 是月序、不是月份</b>：
    /// <c>MinePlanImporter</c> 填的是 <c>startMonth + em.Month - 1</c>（<c>MinePlanImporter.cs:157</c>），
    /// 6 月起排 12 个月就会得到 6…17，标签是 <c>M06</c>…<c>M17</c>。
    /// 直接当月份用会把 M13 之后整段丢掉；直接对 12 取模又会把它们压回同一年，
    /// 把明年 1 月的量盖到今年 1 月上 —— <b>两种错法都不会报任何异常</b>。所以这里滚年。</para>
    ///
    /// <para>月序不成立时退回从标签里认（有些方案的标签是 <c>2027-01</c> 这种真年月）；
    /// 都认不出就返回 false，<b>不猜</b> —— 猜错的月份会把这份计划盖到别人头上。</para>
    /// </summary>
    public static bool TrySplitYearMonth(int baseYear, int monthOrdinal, string? label,
                                         out int year, out int month)
    {
        year = 0; month = 0;
        if (baseYear <= 1900) return false;

        // ① 月序（1 起）：滚年
        if (monthOrdinal >= 1)
        {
            year = baseYear + (monthOrdinal - 1) / 12;
            month = (monthOrdinal - 1) % 12 + 1;
            return true;
        }

        // ② 退路：标签里带真年月（"2027-01" / "2027年1月"）
        var s = (label ?? "").Trim();
        if (s.Length == 0) return false;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"((?:19|20)\d{2})\s*[-/年]\s*(\d{1,2})");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int y2) && int.TryParse(m.Groups[2].Value, out int mo2)
            && mo2 >= 1 && mo2 <= 12)
        { year = y2; month = mo2; return true; }

        // ③ 只有 "M08" 这种月序标签、而 Month 字段又没填 —— 按月序滚年
        var m3 = System.Text.RegularExpressions.Regex.Match(s, @"^[Mm](\d{1,2})$");
        if (m3.Success && int.TryParse(m3.Groups[1].Value, out int ord) && ord >= 1)
        {
            year = baseYear + (ord - 1) / 12;
            month = (ord - 1) % 12 + 1;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 从<b>导出的契约 JSON</b> 装回一份月度计划（不重算，只重填）。
    ///
    /// <para><b>为什么必须有这个入口</b>：这个子系统的全部状态都是<b>会话内</b>的 ——
    /// 关掉程序，剖面、排产结果、比选表全没了。契约 JSON 是唯一能带走的东西，
    /// 而在此之前它<b>只写不读</b>：导出了也装不回来，等于一条死路。
    /// 有了它才能：换台机器复现同一份计划 · 重启后接着确定入库 · 拿别人给的契约做审阅。</para>
    ///
    /// <para><b>装回来的不是"跑过一遍"</b>：没有剖面、没有外循环、没有比选表，所以
    /// <see cref="MonthlyStripSessionResult.Rock"/>/<c>Coupled</c> 是空的，
    /// <see cref="Replan"/> 也用不了（它要上期的调度结果）。这些在 Log 里说清，
    /// 别让人以为装回来就能接着滚动重排。</para>
    /// </summary>
    public static MonthlyStripSessionResult FromContract(string? json, CoalDestination? coalTo = null,
                                                         int startMonth = 1, double[]? workdays = null)
    {
        var r = new MonthlyStripSessionResult();
        if (string.IsNullOrWhiteSpace(json)) { r.Error = "契约内容是空的"; return Fail(r); }
        try
        {
            var ex = MinePlanExport.FromJson(json!, out string perr);
            if (ex == null) { r.Error = "读契约失败：" + perr; return Fail(r); }
            r.Export = ex;
            r.Log.Add($"① 从契约 JSON 装回：{ex.Months.Count} 月 / {ex.Flows.Count} 笔流 / {ex.Benches.Count} 条台阶");
            if (!string.IsNullOrEmpty(ex.Provenance)) r.Log.Add("   来源指纹：" + ex.Provenance);
            foreach (var n in ex.Notes) r.Log.Add("   " + n);

            // 契约自洽照样要校 —— 别人给的、或者手改过的，不能默认它是对的
            var bad = ex.Validate();
            foreach (var b in bad) r.Log.Add("② ✗ 契约不自洽：" + b);
            if (bad.Count > 0)
            { r.Error = $"契约自洽校核没过（{bad.Count} 条）—— 这份契约本身有问题，不能拿来当计划"; return Fail(r); }
            r.Log.Add("② 契约自洽");

            var imp = MinePlanImporter.Import(ex, coalTo, startMonth, workdays);
            r.Import = imp;
            // 同上：走到这儿 Issues 必空，Error 本身就是完整诊断（S30）
            if (!imp.Success) { r.Error = "填月度计划失败：" + imp.Error; return Fail(r); }

            var plan = new ShortTermPlan
            {
                Name = $"契约装回 {(ex.Months.Count)} 月",
                BenchHeightM = ex.Geometry?.RockBenchHeightM ?? 0,
            };
            foreach (var m in imp.Months) plan.Months.Add(m);
            int nGeom = MinePlanImporter.ApplyGeometry(ex, plan, imp);
            bool prepOk2 = MinePlanImporter.ApplyPreparedReserve(plan, imp);   // 同上：不写这句三量校核恒判不过
            r.Plan = plan;
            r.Log.Add($"③ 月度计划「{plan.Name}」{plan.Months.Count} 行 · "
                    + (prepOk2 ? $"备采储量 {imp.PreparedReserveWanT:0.0}万t（已写入三量校核）"
                               : "◆ **备采储量算不出来** —— 三量保有校核会判不过")
                    + " · "
                    + (nGeom > 0 ? $"放坡参数 {nGeom} 项" : "**没有放坡参数** —— 三维层体按垂直壁建"));
            foreach (var i in imp.Issues) r.Log.Add("   " + i);

            r.Success = true;
            r.Log.Add("◆ 这是**装回来的**，不是重算的：没有岩量剖面、没跑外循环、没有比选表 ⇒ "
                    + "「按实绩重排」用不了（它要上期的排产结果）。要重排请先在本机跑一次排产。");
            if (!r.CanConfirm)
                r.Log.Add($"◆ 不能确定入库：{r.Blocking.Count} 条阻断性问题（见上）。");
            return r;
        }
        catch (Exception ex)
        {
            r.Success = false;
            r.Error = $"内部错误（{ex.GetType().Name}）：{ex.Message}";
            r.Log.Add("✗ " + r.Error);
            return r;
        }
    }

    /// <summary>
    /// 读剖面文件。<b>两种容器都收</b>：
    /// <list type="bullet">
    ///   <item><c>.mprof</c> —— 生产中间文件（magic <c>PMRP</c>），<see cref="MineProfileFile"/> 写的。</item>
    ///   <item><c>.case</c> —— 台架用例（magic <c>PMIC</c>），<see cref="InclineCaseFile"/> 写的，
    ///     里面<b>包着同一份剖面</b>（两者共用 <c>MineProfileFile.WriteProfile</c>）。</item>
    /// </list>
    ///
    /// <para><b>为什么必须两种都收</b>：今天「生成采区台阶面」落的就是
    /// <c>%TEMP%\pitmine_benchdump\REAL_latest.case</c>。只认 <c>.mprof</c> 的话，
    /// 用户手上现成的那份文件读不进来，这个功能<b>一天也用不了</b>。</para>
    ///
    /// <para><c>.case</c> 是<b>快照</b>（冻结的台架用例），所以走这条路时
    /// <paramref name="how"/> 里会说清楚 —— 别让人以为它是活数据。</para>
    /// </summary>
    public static CoalProfile? LoadProfileFile(string path, ProfileProvenance? expect,
                                               out string err, out string how)
        => LoadProfileFile(path, expect, out err, out how, out _);

    /// <summary>
    /// 同上，并把 <c>.case</c> 里<b>本来就带着的工作线</b>一并交出来。
    ///
    /// <para><b>为什么要多这一路</b>：工作线决定推进轴。没有它，排土位置投不到轴上，
    /// 真路网只能把源点摆在基线质心 —— <b>逐月运距不随推进变</b>，
    /// 内排启用时机也只能走静态兜底。而界面上并没有让人选工作线的地方，
    /// 于是<b>整条按推进走的运距，从界面上一天也没走到过</b>。
    /// 偏偏那份 <c>.case</c> 里<see cref="InclineCase.WorkLines"/> 一直躺着 ——
    /// 上一版只取了 <c>.Profile</c>，把它丢了。</para>
    ///
    /// <para><c>.mprof</c> 里<b>没有</b>工作线几何（只存了一个
    /// <c>WorkLineKey</c> 指纹用于校验），所以走那条路 <paramref name="lines"/> 为空 ——
    /// 这不是错，但取用方要把"有没有工作线"如实说出来，因为它直接改内排率。</para>
    /// </summary>
    public static CoalProfile? LoadProfileFile(string path, ProfileProvenance? expect,
                                               out string err, out string how,
                                               out List<WorkLineSamples>? lines)
    {
        lines = null;
        var cp = LoadProfileFileCore(path, expect, out err, out how, out var got);
        if (got != null && got.Count > 0) lines = got.Where(w => w.Success && w.Baseline.Count >= 2).ToList();
        if (lines != null && lines.Count == 0) lines = null;
        return cp;
    }

    private static CoalProfile? LoadProfileFileCore(string path, ProfileProvenance? expect,
                                                    out string err, out string how,
                                                    out List<WorkLineSamples>? lines)
    {
        lines = null;
        how = "";
        // ① 先按中间文件读
        var p = MineProfileFile.TryLoad(path, out err, expect);
        if (p != null) return p;
        if (!err.Contains("magic")) return null;      // 不是"容器认错了"，就是真的坏 —— 原样报

        // ② magic 不对 → 试台架用例容器（里面包着同一份剖面）
        var c = InclineCaseFile.TryLoad(path, out string cerr);
        if (c?.Profile == null)
        {
            err = c == null
                ? $"既不是剖面中间文件也不是台阶用例（{err}；按用例读：{cerr}）"
                : "这份台阶用例里没有剖面 —— 生成台阶面时台阶高给 0 会这样，重跑一次并填台阶高";
            return null;
        }
        // 用例是快照，MineProfileFile 那条指纹校核走不到，这里补上
        if (expect != null)
        {
            var got = c.Profile.Rock?.Provenance;
            if (got == null) { err = "这份用例里的剖面没有指纹（旧版本落的），需重扫块体"; return null; }
            if (!got.Matches(expect, out string reason)) { err = $"剖面缓存失效：{reason} —— 需重扫块体"; return null; }
        }
        err = "";
        how = "（读的是**台阶用例**容器 .case，里面包着同一份剖面 —— 它是快照，块体/面改过就不是当前状态了）";

        // 工作线就在这份容器里，别再丢了 —— 但**交出去之前先确认它是这份剖面的那组线**。
        // 容器里两样东西是分头写的：`WorkLines` 是几何，`Provenance.WorkLineKey` 是扫描当时
        // 那组线的指纹。对不上就说明容器是拼起来的（改完线没重扫、或两次结果拼在一起）。
        // 这时**不能拿它建推进轴**：岩量是按当时那条轴分的格，换一条轴去投排土位置，
        // 每个月的运距和内排时机都会静静地错位 —— 不报错，只是排错。宁可退回静态兜底。
        var prov = c.Profile.Rock?.Provenance;
        if (prov != null && c.WorkLines.Count > 0)
        {
            ulong got = ProfileProvenance.FoldWorkLines(c.WorkLines);
            if (got != prov.WorkLineKey)
            {
                how += "\n   ⚠ 容器里的工作线与剖面指纹对不上（改完工作线没重扫？）——"
                     + " **不拿它建推进轴**，逐月运距退回静态兜底";
                return c.Profile;                    // lines 保持 null
            }
        }
        lines = c.WorkLines.ToList();
        return c.Profile;
    }

    /// <summary>
    /// 选完剖面文件后，那一行状态该说什么。<b>逻辑放在这里、不放在窗口的事件处理里，
    /// 就是为了它能被判据钉住</b>：GUI 里的分支没有判据看着，被谁挪一下顺序也不会红。
    ///
    /// <para><b>顺序是有讲究的，不是随手排的</b>：三种坏法都会让用户
    /// "填完一整屏参数、点了排产才被 Prepare 拦下"，所以要在选文件这一刻全部提前报掉。
    /// 而 ✔ 那一行打的是 <c>SeamCount / BenchHeight / TotalRockM3</c> —— <b>正好是自校
    /// ①守恒 与 ⑤完备性 判坏的那几个字段</b>：<see cref="RockProfile.TotalRockM3"/> 是与
    /// <c>Bins</c> 分开维护的增量和，漂了照样是个像样的有限数；<c>SeamCount</c> 与逐层
    /// 数组不齐时，下游按层下标取容重会<b>取错层而不抛</b>。所以不核就打 ✔，
    /// 等于把坏数当结论印在屏上。</para>
    /// </summary>
    /// <returns>(要显示的话, 是不是坏消息)。<paramref name="cp"/> 为 null 时用 <paramref name="err"/>。</returns>
    /// <param name="nowUtc">当前时刻（判据里钉住它；不给就取 <see cref="DateTime.UtcNow"/>）。</param>
    public static (string Text, bool Bad) DescribeProfile(CoalProfile? cp, string err,
                                                          DateTime? nowUtc = null)
    {
        if (cp == null) return ($"◆ 读不出剖面：{err}", true);
        if (cp.Rock == null)
            return ("◆ **只有煤剖面、没有岩剖面** —— 生成台阶面时台阶高填非 0 再重跑", true);
        string? bad1 = cp.Rock.Validate().FirstOrDefault();
        if (bad1 != null)
            return ($"◆ **剖面自身不自洽**：{bad1} —— 台阶面那步就没出对，别拿它排产", true);
        return ($"✔ {cp.Rock.SeamCount} 层 · 台阶高 {cp.Rock.BenchHeight:0.##}m · "
              + $"岩 {cp.Rock.TotalRockM3 / 1e4:0.0}万m³ · 煤 {cp.Rock.TotalCoalWt():0.0}万t"
              + "　" + SourceNote(cp.Rock.Provenance, nowUtc), false);
    }

    /// <summary>
    /// 把这一次排产的<b>人读报表</b>拼成一份文本。
    ///
    /// <para><b>为什么要有这个出口</b>：内核写了三份给人看的报表 ——
    /// <see cref="MonthlyMineScheduler.Report"/>（逐月表，带 ◄触底/◄触顶 标记）、
    /// <see cref="DumpAllocator.FlowMatrix"/>（<b>源 × 去向的配对矩阵</b>，
    /// 和界面那张平铺的流水表是两种读法）、
    /// <see cref="ScheduleDeriver.CompareTable"/>（比选矩阵，注释上写着"命令行 / <b>报表</b>直接打"）。
    /// 而它们此前<b>只有判据在调</b>：写给人看的东西，人反而拿不到。
    /// 界面能导出的只有契约 JSON —— 那是给下游软件的，不是给会上传阅的。</para>
    ///
    /// <para>不新算任何东西，纯粹是把已有的报表接出来；<paramref name="derive"/> 给了就附比选矩阵。</para>
    /// </summary>
    public static string BuildReport(MonthlyStripSessionResult? r, MonthlyStripDeriveResult? derive = null)
    {
        var sb = new StringBuilder();
        if (r == null) return "还没有排产结果";
        sb.AppendLine($"# {r.Input?.PlanName ?? "月度采剥接续"}");
        if (r.Rock?.Provenance != null) sb.AppendLine("# 来源：" + r.Rock.Provenance.Text());
        sb.AppendLine(r.Success ? "# 排产成功" : "# 排产失败：" + r.Error);
        sb.AppendLine();

        if (r.Schedule != null)
        {
            sb.AppendLine("== 逐月采剥表 ==");
            sb.AppendLine(MonthlyMineScheduler.Report(r.Schedule));
            sb.AppendLine();
        }
        if (r.Dump != null && r.Schedule != null)
        {
            sb.AppendLine("== 采排配对矩阵（源 × 去向）==");
            sb.AppendLine(DumpAllocator.FlowMatrix(r.Dump, r.Schedule));
            sb.AppendLine();
        }
        if (derive?.Derivation is { Success: true })
        {
            sb.AppendLine("== 方案比选 ==");
            sb.AppendLine(ScheduleDeriver.CompareTable(derive.Derivation));
            sb.AppendLine();
        }
        sb.AppendLine("== 过程与结论 ==");
        foreach (var l in r.Log) sb.AppendLine(l);
        return sb.ToString();
    }

    /// <summary>
    /// 从 <c>dump_site</c> 台账补上<b>排土侧</b>台阶高/坡面角。
    ///
    /// <para><b>为什么现在能补了</b>：契约里那句「<c>dump_site</c> 表无此列」是<b>过期的说法</b> ——
    /// 该表带着 <c>bench_height_m</c> / <c>bench_slope_angle_deg</c> / <c>overall_slope_angle_deg</c>，
    /// 而且有真数据。不接的话下游只能按<b>垂直壁</b>建排土层体。</para>
    ///
    /// <para><b>取哪一行</b>：优先按本次真正用到的排土场名字对上；对不上就退回
    /// 「有内排流就取内排那类、否则取外排那类」的第一条 —— 并把取了谁写进 <c>Source</c>。
    /// <b>调用方显式给的几何永远优先</b>，这里只填空缺的那两项。</para>
    ///
    /// <para>台账读不到（无数据库/无该表/字段为空）时<b>原样返回</b>，
    /// 契约那条「没有台账来源」的告警照旧 —— 不假装有。</para>
    /// </summary>
    public static ExportGeometry? FillDumpBenchFromLedger(
        ExportGeometry? geom, DumpAllocationResult? dump, MonthlyStripSessionResult r)
    {
        if (geom is { DumpBenchHeightM: > 0, DumpFaceDeg: > 0 }) return geom;   // 外面给全了
        List<PitMine3D.Kylin.Data.Entities.DumpSite> sites;
        try { sites = PitMine3D.Kylin.Data.EquipmentDataContext.DumpSites.All().ToList(); }
        catch (Exception ex) { r.Log.Add("   排土台账读不到（" + ex.GetType().Name + "）—— 排土侧几何仍缺"); return geom; }
        if (sites.Count == 0) return geom;

        // 本次真正排到哪些场子；对得上名字就用那一条
        var used = dump?.Months.SelectMany(m => m.Flows).Select(f => f.DumpName)
                       .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList()
                   ?? new List<string>();
        var pick = sites.FirstOrDefault(s => used.Any(u => string.Equals(u, s.Name, StringComparison.Ordinal)))
                ?? sites.FirstOrDefault(s => s.BenchHeightM is > 0);
        if (pick?.BenchHeightM is not > 0) return geom;

        // ★ **克隆 + 只改那两项**，不要手抄字段清单 ——
        //   写成 `new ExportGeometry { A = g.A, B = g.B, … }` 的话，
        //   `ExportGeometry` 将来加一个字段就会在这里被悄悄丢掉（G18j 那类，本仓栽过四次）。
        var outp = (geom ?? new ExportGeometry()).Clone();
        if (outp.DumpBenchHeightM <= 0) outp.DumpBenchHeightM = pick.BenchHeightM!.Value;
        if (outp.DumpFaceDeg <= 0 && pick.BenchSlopeAngleDeg is > 0)
            outp.DumpFaceDeg = pick.BenchSlopeAngleDeg.Value;
        if (!string.IsNullOrEmpty(outp.Source)) outp.Source += "；";
        outp.Source += $"排土台阶高/坡面角取自台账 dump_site「{pick.Name}」"
                     + $"（{outp.DumpBenchHeightM:0.##}m / {outp.DumpFaceDeg:0.#}°）";
        r.Log.Add($"   排土侧几何取自台账「{pick.Name}」：台阶高 {outp.DumpBenchHeightM:0.##}m · "
                + $"坡面角 {outp.DumpFaceDeg:0.#}° —— 下游建排土层体不必再退回垂直壁");
        return outp;
    }

    /// <summary>
    /// 剖面的来源一句话：<b>哪个块体、什么时候扫的</b>。
    ///
    /// <para><b>为什么非说不可</b>：<c>.case</c> 是<b>快照</b>。排产窗口没有"当前块体"可比，
    /// 指纹核对（<see cref="ProfileProvenance.Matches"/>）从界面上根本走不到 ——
    /// 那就至少要<b>把判断陈不陈的材料摆给人看</b>，而不是打个 ✔ 就完。
    /// 排土位置那一路（<see cref="DumpStripStore"/>）早就这么做了：
    /// "取用方要把来源显示出来，让人知道这份是什么时候、按哪个排土场生成的"——
    /// 两个输入同为快照，报法不该两样。</para>
    /// </summary>
    public static string SourceNote(ProfileProvenance? pv, DateTime? nowUtc = null)
    {
        // 不带 ◆：这句是**跟在 ✔ 后面**的来源说明，不是坏消息。
        // 「认不出来源」值得说，但它不构成"这份不能用"。
        if (pv == null) return "无指纹（旧版本落的）—— 认不出是哪个块体、几时扫的";
        string who = string.IsNullOrWhiteSpace(pv.BlockModelName) ? "未命名块体" : pv.BlockModelName;
        if (!DateTime.TryParseExact(pv.CreatedUtc, "yyyy-MM-dd HH:mm:ss'Z'",
                                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal
                                    | DateTimeStyles.AssumeUniversal, out var made))
            return $"块体「{who}」（没记扫描时刻）";
        var age = (nowUtc ?? DateTime.UtcNow) - made;
        string howOld = age.TotalDays >= 1 ? $"{age.TotalDays:0} 天前"
                      : age.TotalHours >= 1 ? $"{age.TotalHours:0} 小时前"
                      : "刚刚";
        // 隔天的快照最容易被当成活数据，点一句；同一天的不啰嗦。
        return $"块体「{who}」· {howOld}扫的"
             + (age.TotalDays >= 1 ? "，**期间改过块体/面/工作线的话它就是陈的**" : "");
    }

    /// <summary>
    /// 按几何推「内排退距」：内排位置要落在采煤前界后方多远，才算采空区真的形成了。
    ///
    /// <para><b>物理上被逼出来的下限，就是工作帮的水平投影</b>：
    /// 从坑底煤层到地表那一摞台阶，按坡面角 α 摊开占 <c>K × H / tanα</c> 的平距。
    /// 内排位置若落在这个距离之内，它压的还是<b>没挖完的帮</b> —— 图上有台阶，现场没法卸。</para>
    ///
    /// <para><b>只作为对照值报出来，不覆盖用户填的</b>：这个数直接决定内排率，
    /// 悄悄替用户改掉它，比让它偏着危险得多。核准那条阈值时拿它对照。</para>
    /// </summary>
    public static double DerivedInternalClearanceM(RockProfile? rock, double alphaDeg)
    {
        if (rock == null || rock.BenchHeight <= 0) return 0;
        if (alphaDeg <= 0 || alphaDeg >= 90) return 0;
        int levels = rock.LevelMax - rock.LevelMin + 1;
        if (levels <= 0) return 0;
        double tan = Math.Tan(alphaDeg * Math.PI / 180.0);
        return tan <= 1e-9 ? 0 : levels * rock.BenchHeight / tan;
    }

    /// <summary>
    /// 按层间名建一份缺省物料表。<b>只给通用 ρ/Kr，不猜硬约束</b> ——
    /// <c>AllowedDumps</c> 留空（不限）比瞎填一个去向安全得多。
    /// </summary>
    public static GapMaterial[] DefaultMaterials(RockProfile rock)
    {
        var names = rock.GapNames is { Length: > 0 }
                  ? rock.GapNames
                  : GapCode.Names(rock.SeamNames ?? Array.Empty<string>());
        var mats = new GapMaterial[names.Length];
        for (int g = 0; g < names.Length; g++)
        {
            string n = names[g] ?? $"层间{g}";
            // 覆岩最上面那层按"表土/风化"的口径给（松散、Kr 大），其余按岩。
            bool topsoil = g == GapCode.Overburden;
            mats[g] = new GapMaterial
            {
                Name = n,
                Code = topsoil ? "topsoil" : "rock",
                Density = topsoil ? 1.8 : 2.5,
                Kr = topsoil ? 1.25 : 1.15,
            };
        }
        return mats;
    }

    private static string PaceText(StripPace p) => p switch
    {
        StripPace.Hug       => "贴底（只剥非剥不可的，一点缓冲都没有）",
        StripPace.Level     => "拉平（剥采比月间最均衡）",
        StripPace.FrontLoad => "前重（剥到 R34 天花板，抗风险最强但超前积压）",
        _ => p.ToString(),
    };

    private static MonthlyStripSessionResult Fail(MonthlyStripSessionResult r)
    {
        r.Success = false;
        if (!r.Log.Contains("✗ " + r.Error)) r.Log.Add("✗ " + r.Error);
        return r;
    }
}
