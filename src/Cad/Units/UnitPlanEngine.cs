// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/UnitPlanEngine.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>作业面优先级 —— 派生轴之一（U9 轴 1）。</summary>
public enum FacePriority
{
    /// <summary>长带优先：走向长的面先上（主推进面，设备摆得开）。</summary>
    LongestFirst = 0,
    /// <summary>剥采比低优先：同样出煤，先采压覆薄的（省剥离）。</summary>
    LowestRatioFirst = 1,
    /// <summary>近路优先：离出矿点近的先采（省运输功）。</summary>
    NearestFirst = 2,
}

/// <summary>
/// <b>排弃顺序</b> —— 派生轴（U12）。在「自下而上」这条<b>硬约束之内</b>，同一级里先填哪个位置。
///
/// <para><b>为什么它必须单独成轴</b>：<see cref="PairingStrategy"/> 挑的是<b>去哪个排土场</b>，
/// 只有一个场时那条轴必然塌（实测：1048 个位置全属「内排土场1」，三种策略答案逐位相同）。
/// 但一个场里有几百上千个位置，<b>先填哪个</b>是真实的方案差别 —— 它决定推进形态、
/// 运距随时间的变化、以及内排能不能跟上采空区。</para>
///
/// <para><b>硬约束不受影响</b>：无论取哪个值，同一去向同一时刻仍然只有<b>最低的未满级</b>在收
/// （排土台阶自下而上）。轴只动"同一级内的先后"。</para>
/// </summary>
public enum DumpOrder
{
    /// <summary>逐带推进：先填满当前排土带的各幅，再整体往外推一带（现场常规，也是原实现）。</summary>
    StepThenPanel = 0,
    /// <summary>逐幅到底：一幅从里推到外填满，再换下一幅（设备少转场，但排土线不齐）。</summary>
    PanelThenStep = 1,
    /// <summary>就近优先：级内挑离<b>本笔源单元</b>最近的位置（省运距，排土面会跳）。</summary>
    NearestToSource = 2,
    /// <summary>摊平：级内挑剩余库容最大的位置（各幅均匀上升，避免局部先满）。</summary>
    MostRoom = 3,
}

/// <summary>沿走向的推进方式 —— 派生轴之二（U9 轴 2）。只影响同一条带内各幅的先后。</summary>
public enum StrikeAdvance
{
    /// <summary>单向：幅号由小到大。</summary>
    OneWay = 0,
    /// <summary>两头对采：两端往中间。</summary>
    BothEnds = 1,
    /// <summary>中间开切：中间往两端。</summary>
    FromMiddle = 2,
}

/// <summary>
/// 一个<b>出矿位置</b>（破碎站 / 原煤仓 / 储煤场）。煤流的汇。
/// <para>排土位置那边有 <see cref="DumpSlot"/>，煤这边一直没有对应物 ——
/// 没有它，「出矿位置」这半个运输账就落不了地，而运输能力是<b>煤岩共用</b>一个车队的。</para>
/// </summary>
public sealed class CoalSink
{
    public string Name = "";
    /// <summary>与下游去向目录对齐的码（空则用 <see cref="Name"/>）。</summary>
    public string Code = "";
    /// <summary>位置（世界坐标）—— 运距的汇点。</summary>
    public double Cx, Cy, Cz;
    /// <summary>月通过能力（t）。≤0 = 不限。</summary>
    public double CapacityT;
    /// <summary>静态兜底运距（km）。路网解不出来且没坐标时用。</summary>
    public double HaulKm;

    internal double Used;
    internal double Remain => CapacityT > 0 ? Math.Max(0, CapacityT - Used) : double.PositiveInfinity;
}

/// <summary>
/// O-D 运距查询：(源 xyz, 汇 xyz) → <b>坡度等效公里</b>。
/// <para>解不出来<b>返回 null</b>（落不上路网节点 / 不可达 / 空图），由引擎退回几何兜底
/// 并<b>计入命中率</b>。绝不拿一个编出来的数冒充路网运距 —— 「接了路网」和
/// 「接了但一笔没命中」算出来的数一模一样，不报没人分得出。</para>
/// </summary>
public delegate double? HaulQuery(double sx, double sy, double sz, double dx, double dy, double dz);

/// <summary>一个月的排产输入。<b>煤量是自由参数</b>（U7）—— 改了整条重跑，不做增量更新。</summary>
public sealed class UnitPlanInput
{
    /// <summary>采掘单元（煤 + 岩混在一起）。</summary>
    public List<MineUnit> Units = new();

    /// <summary>先后关系图。null = 引擎按 <see cref="GraphOptions"/> 自己建。</summary>
    public UnitGraph? Graph;
    /// <summary>
    /// 建图选项。<b>刻意无入口</b>：压覆判定的口径（走廊宽、推进方位）是<b>几何事实</b>不是计划选项，
    /// 由上游的采矿模型决定；放到排产界面上会变成"两处各调一套压覆"。判据/台架可以直接给。
    /// </summary>
    public UnitGraphBuilder.Options? GraphOptions;

    /// <summary>排土位置（<see cref="DumpSlotAdapter"/> 从排土条带转过来的）。</summary>
    public List<DumpSlot> Slots = new();
    /// <summary>出矿位置。空 = 煤不计运输功（并留条说明）。</summary>
    public List<CoalSink> CoalSinks = new();

    /// <summary>逐物料的 ρ / Kr / 允许去向，索引 = <see cref="MineUnit.MaterialIndex"/>。</summary>
    public GapMaterial[] Materials = Array.Empty<GapMaterial>();

    /// <summary>★ <b>本月指定煤量</b>（t）。界面上调的就是这一个数。</summary>
    public double CoalTargetT;
    /// <summary>
    /// 煤量容差下限 %（默认 0.5，与 R1 同口径 —— 少了就是欠产）。
    /// <para><b>刻意无入口（暂）</b>：容差带是<b>现场口径</b>（超采/欠产的判定线），不是每次排产要调的旋钮。
    /// 要不要放上界面待现场拍板 —— 见 `docs/参数与选项排查_台账.md` §二。</para>
    /// </summary>
    public double CoalTolLoPct = 0.5;
    /// <summary>煤量容差上限 %（默认 2.0 —— 多了就是超采）。<b>刻意无入口（暂）</b>，同上。</summary>
    public double CoalTolHiPct = 2.0;

    /// <summary>
    /// ★ <b>本月指定排弃量</b>（m³ <b>原位实方</b>，U13）—— 与 <see cref="CoalTargetT"/> 并列的第二个给定量。
    /// <b>≤0 = 没给</b>（退回 <see cref="TargetStripRatio"/>，再退回必剥闭包，见 U2 三级来源）。
    ///
    /// <para><b>口径三条，都是现场定的</b>：
    /// ① <b>原位实方</b>，就是逐月配置表「剥离」那一列 —— 库容侧仍按 <c>占容方 = 实方 × Kr</c> 扣，
    ///    换算只在配对那一步做一次，两侧各用各的口径；
    /// ② 它是<b>目标</b>不是上限 —— 上限是另一道闸 <see cref="StripCapM3"/>，两者不许合并成一个数；
    /// ③ 与煤量<b>脱钩</b>：实际选到的煤量偏离目标时照这个绝对量剥，<b>不等比缩放</b>
    ///    ⇒ 剥采比是<b>结果</b>不是输入。</para>
    ///
    /// <para><b>为什么不能按比缩</b>：那等于排弃量又退回由剥采比驱动，用户填的数不会原样兑现，
    /// 而每一项校核仍然是 ✓。</para>
    /// </summary>
    public double StripTargetM3;

    /// <summary>
    /// <b>目标剥采比</b>（m³实方/t，U2 第②级）。≤0 = 不给。
    /// <para><b>只在 <see cref="StripTargetM3"/> 没给时才起作用</b>（U13.4）；两个都给时以排弃量为准，
    /// 并在 <c>Notes</c> 里报出等效比 —— 悄悄用哪一个都不行。</para>
    /// <para><b>刻意无入口</b>：界面这条链只给排弃量（一个量一个来源）。它留着是给
    /// <b>没有排弃量的调用方</b>兜底的第②级来源（判据、台架、以及将来量层直接插 C(t) 的口子）。</para>
    /// </summary>
    public double TargetStripRatio;

    /// <summary>本月剥离能力上限（m³实方）。≤0 = 不限。</summary>
    public double StripCapM3;

    /// <summary>★ 本月<b>车队能力</b>（万t·km 折成 t·km）。≤0 = 不限（U4 的回环不启用）。</summary>
    public double FleetCapTKm;

    /// <summary>煤视密度（t/m³）。</summary>
    public double CoalDensity = 1.35;

    public PairingStrategy Strategy = PairingStrategy.MinHaul;
    /// <summary>排弃顺序（U12）—— 同一级内先填哪个位置。只有一个排土场时，<b>差别全在这条轴上</b>。</summary>
    public DumpOrder DumpOrder = DumpOrder.StepThenPanel;
    public FacePriority FacePriority = FacePriority.LongestFirst;
    public StrikeAdvance StrikeAdvance = StrikeAdvance.OneWay;

    /// <summary>真路网运距。null = 全部按直线 × <see cref="FallbackDetour"/> 兜底（会留条）。</summary>
    public HaulQuery? Haul;
    /// <summary>兜底迂回系数（直线距离 × 它）。矿区道路一般 1.2~1.5。</summary>
    public double FallbackDetour = 1.3;

    /// <summary>本月月序（内排启用时机 <see cref="DumpSlot.AvailableFromMonth"/> 按它判）。</summary>
    public int Month = 1;

    /// <summary>内排累计占容上限（m³ 占容方）—— 坑还没挖出来就没法回填。≤0 = 不卡。</summary>
    public double InternalCumCapM3;

    /// <summary>
    /// <b>面级配额</b>（U1″）—— 把本月煤量拆到各作业面上。<b>空 = 不拆</b>，退回"一个全局目标"。
    ///
    /// <para><b>为什么必须由调用方给</b>：目标是一个全局数时，贪心只会在最后一个面上切一刀 ⇒
    /// 月末永远只有一个锋面幅，而现场是<b>几台铲在几个面上同时推</b>。要出现"每个面各留一个锋面"，
    /// 必须先有面级的量。份额在「确定开采程序」那张表里，单元归属在 <c>FaceUnitResolver</c> 里，
    /// <b>两者都在 PlanLib</b> —— 引擎不引 PlanLib，也<b>不猜归属</b>（归属拒绝猜是那边定死的口径），
    /// 所以这里只收结果：面 + 份额 + 属于它的单元号。</para>
    /// </summary>
    public List<FaceQuota> FaceQuotas = new();

    /// <summary>
    /// <b>一条工作线切几个标段</b>（沿走向，1 = 不切；现场常用 2~3）。
    ///
    /// <para>现场口径（2026-08-18）：「可以把工作线分成两三个标段，但是还是要沿着台阶线采掘块体的，
    /// 得考虑作业的规整性和作业设备的作业方便」。所以标段是<b>沿走向把一条带切成几段</b>，
    /// 一段一台（组）设备，<b>段内照旧沿台阶线连续推进</b> —— 不是把带横过来切、更不是跳着采。</para>
    ///
    /// <para>两条直接后果：① 锋面幅上限从"每个面 1 个"变成<b>"每个标段 1 个"</b>
    /// （几台设备同时推，月末各留一个半截幅，这才是现场形态）；
    /// ② 段界按<b>幅号连续均分</b>，不按量分 —— 按量分会让段界落在幅中间，
    /// 图上就是犬牙交错的作业面，设备互相别着走。</para>
    ///
    /// <para><b>&gt;1 才有意义的前提是这条带的幅够多</b>：幅数 &lt; 段数时按幅数取，
    /// 并在 <c>Notes</c> 里说出来（切出空段等于凭空多报一个作业面）。</para>
    /// </summary>
    public int FaceSegments = 1;
}

/// <summary>
/// 一个作业面的本月配额（U1″）。<b>归属是给进来的，不是引擎算的</b>。
/// </summary>
public sealed class FaceQuota
{
    /// <summary>作业面身份（「确定开采程序」里的面名）。</summary>
    public string FaceId = "";

    /// <summary>产能份额（%）。引擎按<b>实际有煤可采的面</b>重新归一，并把归一前后都报出来。</summary>
    public double SharePct;

    /// <summary>
    /// 本面本月产能上限（t）。<b>≤0 = 不限</b>。
    /// <para>给了它，份额算出来的目标还要再被它夹一道 —— 份额说的是"该干多少"，
    /// 台效说的是"干得动多少"，两者不是一个东西，不许合并成一个数。</para>
    /// <para><b>刻意无入口</b>（2026-08-18）：面级台效要等「设备指派」跑完才有，
    /// 而指派吃的是排产的结果 —— 界面在这儿填一个数就是把那条链倒过来接。
    /// 等设备侧有独立来源（如工作面管理里的主电铲台效）再接，接之前它恒为 0 = 不夹。</para>
    /// </summary>
    public double CapacityT;

    /// <summary>属于这个面的单元号（煤 + 岩都可以给，引擎只用煤那部分拆量）。</summary>
    public List<string> UnitIds = new();

    /// <summary>份额/上限是哪来的，原样回显（表里填的？台效算的？人改的？）。</summary>
    public string Note = "";
}

/// <summary>一行排产结果 = 一个采掘单元本月干多少、往哪儿送。</summary>
public sealed class UnitAssignment
{
    public string UnitId = "";
    public UnitKind Kind;
    public string SeamCode = "";
    public int BandId, PanelIndex;

    /// <summary>月内推进序（1 起）。</summary>
    public int Seq;

    /// <summary>本月采出的原位实方（m³）。</summary>
    public double InSituM3;
    /// <summary>本月采了这一幅的多少（0~1）。</summary>
    public double Fraction;
    /// <summary>本月结束后该幅的累计完成度（0~1）。&lt;1 = 跨月幅。</summary>
    public double DoneAfter;
    /// <summary>是不是被切开的幅（U1 —— 每月每类最多一个）。</summary>
    public bool IsPartial => DoneAfter < 1 - 1e-6;

    /// <summary>吨量（t）。</summary>
    public double TonnageT;

    /// <summary>是<b>必剥闭包</b>里的（不可削）—— 运输闸只削非必剥的那部分（U4）。</summary>
    public bool IsMandatory;

    /// <summary>去向（岩 = 排土位置 Code，煤 = 出矿位置 Code）。一个单元可能拆到多个去向。</summary>
    public readonly List<UnitFlow> Flows = new();

    public double HaulWorkTKm => Flows.Sum(f => f.TransportWorkTKm);
    /// <summary>体积加权平均运距（km）—— 报表显示用。</summary>
    public double MeanHaulKm
    {
        get { double v = Flows.Sum(f => f.InSituM3); return v > 1e-9 ? Flows.Sum(f => f.HaulKm * f.InSituM3) / v : 0; }
    }
    /// <summary>没找到去向的实方（排不下 / 拉不动）。</summary>
    public double UnplacedM3;
}

/// <summary>一个单元流向一个去向的一笔。与 PlanLib 的 <c>PlanFlow</c> 六元组同构（U5）。</summary>
public sealed class UnitFlow
{
    public string DestinationCode = "";
    public string DestinationName = "";
    public bool IsInternalDump;
    public bool IsCoalSink;
    /// <summary>汇的位置（三维层体按它长）。</summary>
    public double Dx, Dy, Dz;

    public double InSituM3;
    /// <summary>排弃占容 = 实方 × Kr（煤流为 0）。</summary>
    public double DumpM3;
    public double TonnageT;
    public double HaulKm;
    /// <summary>本笔运距是不是真路网解出来的（false = 兜底）。</summary>
    public bool HaulFromNetwork;
    public double TransportWorkTKm => TonnageT * HaulKm;

    public string MaterialName = "";
    public string MaterialCode = "";
    public double Density, Kr;
}

public sealed class UnitPlanResult
{
    public bool Success;
    public string Error = "";

    public readonly List<UnitAssignment> Assignments = new();
    /// <summary>引擎替用户做的每一个决定。<b>一条都不许静默</b>。</summary>
    public readonly List<string> Notes = new();

    // ── 达成 ──
    public double CoalT, StripM3, CoalTargetT;
    /// <summary><b>结果</b>剥采比（m³实方/t）—— 不是输入。给了排弃量时它是 剥离÷采出 的结果（U13.3）。</summary>
    public double StripRatio => CoalT > 1e-9 ? StripM3 / CoalT : 0;
    public double MandatoryStripM3;      // 必剥闭包量（硬下界）

    /// <summary>
    /// 本月<b>有效</b>目标剥离量（m³实方）—— 过完「闭包下界 / 能力上限」两道夹之后的那个数。
    /// <para>与用户给的 <see cref="UnitPlanInput.StripTargetM3"/> 不一定相等，差在哪由
    /// <see cref="StripTargetNote"/> 说；<b>缺口只按这个有效目标算</b>（能力夹回不算缺口，U13）。</para>
    /// </summary>
    public double StripTargetM3;
    /// <summary>有效目标是怎么来的（U2 三级来源 + 两道夹），一句话。<b>空 = 还没解</b>。</summary>
    public string StripTargetNote = "";
    /// <summary>剥离量达成偏差 %（正 = 超剥）。有效目标为 0 时返回 0。</summary>
    public double StripDeviationPct => StripTargetM3 > 1e-9 ? (StripM3 - StripTargetM3) / StripTargetM3 * 100 : 0;
    public double TransportWorkTKm;
    public double InternalRatePct = -1;  // −1 = 没有排土流（不是"全外排"这个真实结论）

    // ── 四种缺口，分开记，不许合并（U8）——【四个原因对四个动作：推进 / 推进 / 加排土场 / 加车】──
    /// <summary>煤没凑够（t）—— 前沿上可采的煤不够。</summary>
    public double ShortfallT;
    /// <summary>
    /// 岩剥不够（m³实方，U13）—— <b>前沿上可剥的岩不够</b>，凑不满有效目标。
    /// <para><b>只记这一种原因</b>：能力夹回（用户自己给的闸）与车队闸削超前剥离（走
    /// <see cref="UnhauledM3"/>）都<b>不进</b>这里 —— 混进来的话这一列天天非零，
    /// 真的那条就被淹没了。</para>
    /// </summary>
    public double ShortStripM3;
    /// <summary>岩排不下（m³实方）—— 库容不够。</summary>
    public double UnplacedM3;
    /// <summary>拉不动（m³实方）—— 车队能力不够。</summary>
    public double UnhauledM3;

    /// <summary>
    /// <b>不是缺口</b>：U4 回环里被车队闸主动削掉的超前剥离量（m³实方）。
    /// <para>单列的理由是<b>账要平</b>：<c>达成 + 剥不够 + 主动削 = 有效目标</c>。
    /// 不单列的话，车队闸一削，剥离量的账就对不上，而每一项单独校核仍然是 ✓ ——
    /// 于是只能靠"引擎大概削了点"来解释差额，那正是静默。</para>
    /// </summary>
    public double TrimmedByFleetM3;

    /// <summary>运距命中率：路网解出来的笔数 / 总笔数。</summary>
    public int HaulHits, HaulTotal;
    public string HaulNote = "";

    // ── 作业组织便利性（U11）——「设备好不好干、组织顺不顺」得是【可比的数】，不能只是个说法 ──

    /// <summary>本月开了几个作业面（不同的 层×带 组合）。<b>越少越集中</b>，设备和人好安排。</summary>
    public int FaceCount;
    /// <summary>月内<b>转场次数</b>：相邻两个作业单元不是"同带相邻幅"的次数。越少设备挪动越少。</summary>
    public int MoveCount;
    /// <summary>其中<b>跨层转场</b>次数（换台阶，设备要整体搬）—— 这类最贵，单列。</summary>
    public int CrossLevelMoves;
    /// <summary>转场代价合计（<c>MoveCost</c> 之和）。同带相邻幅计 0。</summary>
    public double MoveCost;
    /// <summary>用到几个排土位置。<b>越少卸点越集中</b>（现场口径：设备作业方便）。</summary>
    public int DumpSlotCount;

    /// <summary>
    /// 卸点<b>换了几次</b>（U11）—— 推土机被挪了几次。<b>与卸点个数不是一回事</b>：
    /// 3 个卸点也可能来回换 30 次，而报表上"卸点 3 个"看着完全正常。
    /// </summary>
    public int DumpSlotSwitches;
    /// <summary>跨月幅个数（U1）。越少交接越干净。</summary>
    public int PartialCount => Assignments.Count(a => a.IsPartial);

    /// <summary>作业组织一行摘要 —— 比选表这一组维度直接用它。</summary>
    public string OrgText =>
        $"作业面 {FaceCount} 个 · 转场 {MoveCount} 次（跨层 {CrossLevelMoves}）· 卸点 {DumpSlotCount} 个（换点 {DumpSlotSwitches} 次）· 跨月幅 {PartialCount} 个";

    public IEnumerable<UnitAssignment> Coal => Assignments.Where(a => a.Kind == UnitKind.Coal);
    public IEnumerable<UnitAssignment> Rock => Assignments.Where(a => a.Kind == UnitKind.Rock);

    /// <summary>煤量达成偏差 %（正 = 超采）。</summary>
    public double CoalDeviationPct => CoalTargetT > 1e-9 ? (CoalT - CoalTargetT) / CoalTargetT * 100 : 0;

    /// <summary>硬约束全过 且 <b>四种缺口全为零</b>（采得出、剥得够、排得下、拉得动）。</summary>
    public bool Feasible => Success && ShortfallT <= 1e-6 && ShortStripM3 <= 1e-6
                                    && UnplacedM3 <= 1e-6 && UnhauledM3 <= 1e-6;

    /// <summary>
    /// 五类自洽（承接 <c>MinePlanExport.Validate</c> 的那份清单）：
    /// ① 量的口径 ② 有序 ③ 引用 ④ 范围 ⑤ 完备。返回空 = 自洽。
    /// </summary>
    public List<string> Validate(double tolPct = 0.001)
    {
        var bad = new List<string>();
        if (!Success) { bad.Add("求解未成功：" + Error); return bad; }

        // ① 量：逐单元合计 = 汇总；每笔流的吨量/占容 = 实方 × 系数
        //    煤对吨量、岩对实方 —— 两侧各用各的口径，别拿一个数去对另一个口径的账。
        double coal = Coal.Sum(a => a.TonnageT);
        if (Rel(coal, CoalT) > tolPct) bad.Add($"煤量对不上：逐单元 {coal:0.##}t vs 汇总 {CoalT:0.##}t");
        double rock = Rock.Sum(a => a.InSituM3);
        if (Rel(rock, StripM3) > tolPct) bad.Add($"剥离量对不上：逐单元 {rock:0.##}m³ vs 汇总 {StripM3:0.##}m³");
        foreach (var a in Assignments)
        {
            double fsum = a.Flows.Sum(f => f.InSituM3);
            if (a.Flows.Count > 0 && Rel(fsum + a.UnplacedM3, a.InSituM3) > tolPct)
            { bad.Add($"{a.UnitId} 流向合计 + 未落地 ≠ 采出量"); break; }
            foreach (var f in a.Flows)
            {
                if (f.Kr > 0 && !f.IsCoalSink && Rel(f.InSituM3 * f.Kr, f.DumpM3) > tolPct)
                { bad.Add($"{a.UnitId} 占容 ≠ 实方×Kr"); break; }
                if (f.Density > 0 && Rel(f.InSituM3 * f.Density, f.TonnageT) > tolPct)
                { bad.Add($"{a.UnitId} 吨量 ≠ 实方×ρ"); break; }
            }
        }

        // ② 有序：推进序 1..n 无重无缺
        var seqs = Assignments.Select(a => a.Seq).OrderBy(v => v).ToList();
        for (int i = 0; i < seqs.Count; i++)
            if (seqs[i] != i + 1) { bad.Add($"推进序不是 1..{seqs.Count} 的排列（第 {i + 1} 位是 {seqs[i]}）"); break; }

        // ③ 引用：UnitId 不重、去向不空
        var dupe = Assignments.GroupBy(a => a.UnitId).FirstOrDefault(g => g.Count() > 1);
        if (dupe != null) bad.Add($"UnitId 重复：{dupe.Key} 出现 {dupe.Count()} 次");
        foreach (var f in Assignments.SelectMany(a => a.Flows))
            if (f.DestinationCode.Length == 0 && f.DestinationName.Length == 0)
            { bad.Add("有流没有去向 —— 缺去向要计入未落地，不许留空流"); break; }

        // ④ 范围：完成度 0~1、量非负、内排率 −1 或 0~100、无 ±∞/NaN
        foreach (var a in Assignments)
        {
            if (a.DoneAfter < -1e-9 || a.DoneAfter > 1 + 1e-9) { bad.Add($"{a.UnitId} 完成度 {a.DoneAfter:0.###} 越界"); break; }
            if (a.InSituM3 < -1e-9) { bad.Add($"{a.UnitId} 采出量为负"); break; }
        }
        if (InternalRatePct < -1.0000001 || InternalRatePct > 100.0000001)
            bad.Add($"内排率 {InternalRatePct:0.0}% 越界（−1 = 没有排土流）");
        foreach (var v in NonFinite())
            { bad.Add($"出现非工程量的数：{v}"); break; }

        // ⑤ 完备：四种缺口非负；不可行时必须有缺口说明；有效目标必须解过（U13）
        if (ShortfallT < -1e-9 || ShortStripM3 < -1e-9 || UnplacedM3 < -1e-9 || UnhauledM3 < -1e-9)
            bad.Add("缺口为负");
        if (!Feasible && ShortfallT <= 1e-6 && ShortStripM3 <= 1e-6 && UnplacedM3 <= 1e-6 && UnhauledM3 <= 1e-6)
            bad.Add("判为不可行却一个缺口都没有 —— 说不出为什么不可行");
        // 剥离量的账要对在【有效目标】上：达成 + 剥不够 + 车队闸主动削 = 有效目标。
        // 这一条是 U13 的兜网 —— 目标被谁悄悄改过、或缺口算错了口径，都会在这里露出来。
        if (TrimmedByFleetM3 < -1e-9) bad.Add("主动削减量为负");
        if (StripTargetM3 > 1e-9
            && Rel(StripM3 + ShortStripM3 + TrimmedByFleetM3, StripTargetM3) > Math.Max(tolPct, 0.005))
            bad.Add($"剥离量的账不平：达成 {StripM3:0.##} + 剥不够 {ShortStripM3:0.##}"
                  + $" + 车队闸削 {TrimmedByFleetM3:0.##} ≠ 有效目标 {StripTargetM3:0.##} m³");
        if (StripTargetM3 > 1e-9 && StripTargetNote.Length == 0)
            bad.Add("有效目标剥离量解出来了却没说是怎么来的（U2 三级来源 + 两道夹要留条）");
        return bad;
    }

    private IEnumerable<string> NonFinite()
    {
        foreach (var (v, n) in new (double, string)[]
                 { (CoalT, nameof(CoalT)), (StripM3, nameof(StripM3)),
                   (StripTargetM3, nameof(StripTargetM3)), (ShortStripM3, nameof(ShortStripM3)),
                   (TrimmedByFleetM3, nameof(TrimmedByFleetM3)),
                   (TransportWorkTKm, nameof(TransportWorkTKm)), (InternalRatePct, nameof(InternalRatePct)) })
            if (double.IsNaN(v) || double.IsInfinity(v)) yield return $"{n}={v}";
        foreach (var f in Assignments.SelectMany(a => a.Flows))
            if (double.IsNaN(f.HaulKm) || double.IsInfinity(f.HaulKm)) { yield return $"运距={f.HaulKm}"; break; }
    }

    private static double Rel(double a, double b)
    {
        double m = Math.Max(Math.Abs(a), Math.Abs(b));
        return m < 1e-9 ? 0 : Math.Abs(a - b) / m;
    }

    /// <summary>一屏摘要（命令行/日志直接打，别各处再编一遍）。</summary>
    public string Report()
    {
        if (!Success) return "单元排产失败：" + Error;
        var sb = new StringBuilder();
        sb.AppendLine($"煤 {CoalT / 1e4:0.00}万t（目标 {CoalTargetT / 1e4:0.00}，偏差 {CoalDeviationPct:+0.0;-0.0;0.0}%）"
                    + $" · 剥离 {StripM3 / 1e4:0.0}万m³"
                    + (StripTargetM3 > 1e-9 ? $"（目标 {StripTargetM3 / 1e4:0.0}，偏差 {StripDeviationPct:+0.0;-0.0;0.0}%，"
                                              + $"其中必剥 {MandatoryStripM3 / 1e4:0.0}）"
                                            : $"（其中必剥 {MandatoryStripM3 / 1e4:0.0}）")
                    + $" · 剥采比 {StripRatio:0.00}（结果）");
        if (StripTargetNote.Length > 0) sb.AppendLine("  目标来源：" + StripTargetNote);
        sb.AppendLine($"单元 {Assignments.Count} 个（煤 {Coal.Count()} / 岩 {Rock.Count()}）· "
                    + $"运输功 {TransportWorkTKm / 1e4:0.0}万t·km · "
                    + (InternalRatePct < 0 ? "内排率 —" : $"内排率 {InternalRatePct:0.0}%"));
        sb.AppendLine("作业组织：" + OrgText);
        if (!Feasible)
            sb.AppendLine($"◆ 缺口： 煤欠 {ShortfallT / 1e4:0.00}万t · 剥不够 {ShortStripM3 / 1e4:0.0}万m³"
                        + $" · 排不下 {UnplacedM3 / 1e4:0.0}万m³ · 拉不动 {UnhauledM3 / 1e4:0.0}万m³");
        else sb.AppendLine("✔ 煤量达成、剥离量达成、全部排得下、车队拉得动");
        if (HaulNote.Length > 0) sb.AppendLine("  运距：" + HaulNote);
        foreach (var nt in Notes) sb.AppendLine("  " + nt);
        return sb.ToString();
    }
}

/// <summary>
/// <b>大算法</b>：指定煤量 → 一个月的剥采计划（单元粒度 + 去向 + 运距 + 顺序）。
///
/// <para>规则 U1–U9 见 `docs/采掘单元排产_设计.md`。六步一个回环：
/// ①建图 ②选煤 ③补岩 ④配对 ⑤运输闸 ⑥定序，其中⑤超了要<b>回③</b>削剥离量。</para>
///
/// <para><b>为什么必须是一个算法而不是四个步骤</b>（U4）：运距随去向变、去向随剥离量变、
/// 剥离量又受车队能力约束 —— 三个量互相咬住。拆成"先排产再校核运输"的话，
/// 运距远的月份会排出一份车队根本拉不动的计划，<b>而每一项单独校核都是 ✓</b>。</para>
///
/// <para><b>纯函数</b>（U7）：同一份输入跑两次逐位相同；改煤量重跑即可，不做增量更新 ——
/// 增量更新会让"改回原值"得到与原来不同的结果，而两次都自洽、谁也解释不了谁。</para>
/// </summary>
public static class UnitPlanEngine
{
    public static UnitPlanResult Solve(UnitPlanInput inp)
    {
        var r = new UnitPlanResult();
        if (inp == null) { r.Error = "没有输入"; return r; }
        r.CoalTargetT = inp.CoalTargetT;

        if (inp.Units == null || inp.Units.Count == 0)
        { r.Error = "没有采掘单元 —— 先在「采矿模型」里生成一次，或从台账 CSV 读进来"; return r; }
        if (inp.CoalTargetT < 0)
        { r.Error = $"指定煤量是负数（{inp.CoalTargetT:0.##}t）"; return r; }

        // ── U6 库容是有状态的：拿干净副本，绝不在调用方的对象上累加 ────────────
        // 不复制的话，连着解两次（比如用户拖了两下煤量滑块）第二次就在吃第一次排剩下的，
        // 越靠后越"排不下"，而且看上去像是煤量本身排不下。
        var slots = CloneSlots(inp.Slots, r);
        var sinks = CloneSinks(inp.CoalSinks);
        var mats = CheckMaterials(inp.Materials, r);

        double density = inp.CoalDensity > 0 ? inp.CoalDensity : 1.35;
        if (inp.CoalDensity <= 0) r.Notes.Add($"⚠ 煤视密度填的是 {inp.CoalDensity:0.###}（须为正），已按 1.35 处理");

        // ── ① 建图 ───────────────────────────────────────────────────────
        var g = inp.Graph ?? UnitGraphBuilder.Build(inp.Units, inp.GraphOptions);
        foreach (var nt in g.Notes) r.Notes.Add(nt);
        var units = g.Units;

        // ── ② 选煤（U1′：按作业面粘着推进；面内整幅优先、每面最多一个锤面幅；遗留面最高优先）
        var planned = new HashSet<int>();
        var takeFrac = new Dictionary<int, double>();     // 单元下标 → 本月采这一幅的比例
        double coalT = SelectCoal(g, inp, density, planned, takeFrac, r);

        // ── ③ 补岩 + ⑤ 运输闸的回环 ──────────────────────────────────────
        //     必剥闭包是硬下界，回环只削「超前剥离」那一部分（削了必剥煤就采不出来）。
        //
        // U10：前驱的必剥量按【累计完成度】算，不按整幅。
        //   煤幅只采 70% 时，压在它头上的岩只需剥到 70% —— 剥离和采煤是沿走向同步推进的。
        //   按整幅算会把必剥量放大（只采 5% 的幅拉进整幅的岩）；
        //   按本月增量算又会把上月已剥的那部分重复要求。两种错都不报错，只是数不对。
        var reqCum = new Dictionary<int, double>();
        foreach (int ci in planned)
        {
            double need = MineUnit.Clamp01(units[ci].DoneFraction + takeFrac[ci]);
            foreach (int p in g.Closure(ci))
            {
                if (units[p].IsCoal) continue;
                reqCum[p] = Math.Max(reqCum.TryGetValue(p, out double v) ? v : 0, need);
            }
        }
        var mandFrac = new Dictionary<int, double>();
        foreach (var kv in reqCum)
        {
            double add = kv.Value - units[kv.Key].DoneFraction;
            if (add > 1e-9) mandFrac[kv.Key] = MineUnit.Clamp01(add);
        }
        var mandatory = new HashSet<int>(mandFrac.Keys);
        double mandM3 = mandFrac.Sum(kv => units[kv.Key].InSituM3 * kv.Value);
        r.MandatoryStripM3 = mandM3;

        double target = ResolveStripTarget(coalT, mandM3, inp, r);

        // 超前剥离候选：前沿上、非必剥的岩，按优先级排好
        var extraOrder = ExtraRockOrder(g, inp, mandFrac, planned);

        var rockPick = new Dictionary<int, double>();      // 岩单元下标 → 本月采的比例
        int extraCut = 0;                                  // 回环削掉的笔数
        UnitPlanResult? probe = null;

        // ★ U13 的两个量，必须分开量（合起来就分不清"干不成"和"引擎替你降的"）：
        //   haveFull  = 【完整候选集】那一轮凑到多少 ⇒ target − haveFull = ShortStripM3「剥不够」（前沿真没岩）
        //   haveFinal = 最终采纳的那一轮凑到多少     ⇒ haveFull − haveFinal = 车队闸主动削掉的量（不是缺口）
        //   三者构成恒等式：达成 + 剥不够 + 主动削 = 有效目标，Validate() 拿它当兜网。
        double haveFull = -1, haveFinal = 0;

        for (int round = 0; round < extraOrder.Count + 2; round++)
        {
            // 每轮重来：先按目标补够超前剥离，再配对，再看车队拉不拉得动。
            double have = mandM3;
            rockPick.Clear();
            foreach (var kv in mandFrac) rockPick[kv.Key] = kv.Value;

            for (int k = 0; k < extraOrder.Count - extraCut; k++)
            {
                if (have >= target - 1e-6) break;
                int i = extraOrder[k];
                double room = target - have;
                // 已被必剥占掉的部分不能重复算 —— 同一个岩单元可能既是必剥又想多剥一点
                double already = rockPick.TryGetValue(i, out double f0) ? f0 : 0;
                double cur = MineUnit.Clamp01(units[i].DoneFraction + already);

                // ★ 压覆必须在【比例】上成立，不是在【集合】上（U10 推广到超前剥离这一侧）。
                //   IsFree 判的是"前驱在不在已排集合里"，而 U10 之后必剥是按比例算的 ——
                //   上面那幅只排到 56%，下面这幅照样能被推到 82.8%，"前驱没做完就采了下面"。
                //   判据实测抓到的就是这一条。这里把它做成【构造性】的：
                //   本幅本月末的完成度，不得超过任何一个前驱本月末的完成度。
                double cap = 1.0;
                foreach (int p in g.Pred[i])
                {
                    double pf = MineUnit.Clamp01(units[p].DoneFraction
                              + (rockPick.TryGetValue(p, out double pv) ? pv : 0));
                    if (pf < cap) cap = pf;
                }
                if (cap <= cur + 1e-9) continue;                      // 头上那幅还没剥到这儿，本幅先不动

                double whole = units[i].InSituM3 * (cap - cur);       // 本月还能往上推到的量
                if (whole <= 1e-6) continue;
                double take = whole <= room + 1e-6 ? whole : room;   // U1 同款：整幅优先，够不着才切
                if (take <= 1e-6) continue;
                rockPick[i] = MineUnit.Clamp01(already + Frac(units[i], take));
                have += take;
            }

            if (haveFull < 0) haveFull = have;                        // 完整候选集那一轮（U13 的缺口就量在这里）
            haveFinal = have;

            probe = Assemble(g, inp, mats, slots, sinks, density, planned, takeFrac, rockPick, mandatory, r.Notes.Count);
            if (inp.FleetCapTKm <= 0) break;                          // 不卡车队能力
            if (probe.TransportWorkTKm <= inp.FleetCapTKm + 1e-6) break;
            if (extraCut >= extraOrder.Count) break;                  // 超前剥离已削光
            extraCut++;
            // 清掉本轮在副本上的占用，下一轮从干净状态重来（U6 同一个道理）
            foreach (var s in slots) s.Used = 0;
            foreach (var s in sinks) s.Used = 0;
        }

        var res = probe!;
        res.CoalTargetT = inp.CoalTargetT;
        res.MandatoryStripM3 = mandM3;
        // ★ 前置结果 r 上的东西必须【逐项】搬到最终结果上。
        //   Assemble 造的是【另一个】UnitPlanResult —— 早先只搬了 Notes，
        //   SelectCoal 记的 ShortfallT 就此丢掉，于是"一吨煤都没采到"照样 Feasible==true，
        //   而 U8 那条"三种缺口分开记、不许合并"整个失效。判据当场逮到（煤量欠 100% 判可行）。
        //   ⇒ 以后往 r 上加字段，这里必须同步加一行，否则又是一次静默丢。
        res.ShortfallT = r.ShortfallT;
        res.StripTargetM3 = r.StripTargetM3;
        res.StripTargetNote = r.StripTargetNote;

        // ★ U13：「剥不够」只量【完整候选集】那一轮 —— 后面几轮少的那部分是车队闸主动削的，
        //   记进来就成了同一件事两个口子，用户分不清该推进作业面还是该加车。
        res.ShortStripM3 = Math.Max(0, target - Math.Max(0, haveFull));
        res.TrimmedByFleetM3 = Math.Max(0, haveFull - haveFinal);
        if (res.ShortStripM3 > 1e-6)
            res.Notes.Add($"◆ 剥离量没凑够：目标 {target / 1e4:0.0}万m³，前沿上能剥的岩只有 {haveFull / 1e4:0.0}万m³，"
                        + $"欠 {res.ShortStripM3 / 1e4:0.0}万m³。这是【前沿无岩可剥】（该推进 / 该重算采矿模型），"
                        + "不是排不下也不是拉不动（U8）。"
                        + "两个常见原因：可剥的岩单元本来就不够，或按 U10 的比例封顶 —— 头上那幅还没剥到这儿。");

        foreach (var nt in r.Notes) if (!res.Notes.Contains(nt)) res.Notes.Insert(0, nt);

        if (extraCut > 0)
            res.Notes.Add($"· 车队能力 {inp.FleetCapTKm / 1e4:0.0}万t·km 顶住了：削掉 {extraCut} 个超前剥离单元、"
                        + $"共 {res.TrimmedByFleetM3 / 1e4:0.0}万m³（U4 回环）。"
                        + "必剥闭包一方没削 —— 削了煤就采不出来。"
                        + "<b>这部分不记作缺口</b>（是引擎替你降的，不是干不成），但它让剥离量低于目标。");
        if (inp.FleetCapTKm > 0 && res.TransportWorkTKm > inp.FleetCapTKm + 1e-6)
        {
            double over = res.TransportWorkTKm - inp.FleetCapTKm;
            // 拉不动的量按"超出部分 ÷ 平均运距 ÷ 平均密度"折回实方，只为给出量级
            double meanKm = res.TransportWorkTKm > 1e-9 ? res.TransportWorkTKm / Math.Max(1e-9, res.Assignments.Sum(a => a.TonnageT)) : 0;
            double meanRho = 2.5;
            res.UnhauledM3 = meanKm > 1e-9 ? over / meanKm / meanRho : 0;
            res.Notes.Add($"◆ 车队能力不足以完成【煤 + 必剥岩】：超出 {over / 1e4:0.0}万t·km"
                        + $"（≈{res.UnhauledM3 / 1e4:0.0}万m³ 拉不动）。超前剥离已削光，再削就得动必剥量 ——"
                        + "那会让煤采不出来，所以引擎<b>不替你降煤量</b>：要么加车，要么把煤量调下来。");
        }

        res.Success = true;
        return res;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ② 选煤
    // ══════════════════════════════════════════════════════════════════
    private static double SelectCoal(UnitGraph g, UnitPlanInput inp, double density,
                                     HashSet<int> planned, Dictionary<int, double> takeFrac,
                                     UnitPlanResult r)
    {
        var units = g.Units;
        double lo = inp.CoalTargetT * (1 - Math.Max(0, inp.CoalTolLoPct) / 100.0);
        double hi = inp.CoalTargetT * (1 + Math.Max(0, inp.CoalTolHiPct) / 100.0);

        // ⚠ 【不能只挑"头上没压覆"的煤】——那正是剥采计划要解决的事。
        //   早先这里写 `if (!g.IsFree(i, planned)) continue;`，于是只选前沿上已经露出来的煤，
        //   闭包恒空 ⇒ 必剥恒 0 ⇒ 剥离量全由"超前剥离"随便凑，**采的煤和剥的岩互不相干**。
        //   真数据上跑出来：1355 条压覆边、必剥 0.0 万m³ —— 而每一项校核都是"✓"。
        //   正确做法：压着岩的煤【可以选】，它的闭包进必剥（U2 的硬下界就是这么来的）；
        //   前沿只用来【排优先级】（已露出来的更便宜），不当准入闸门。
        var cands = new List<int>();
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].IsCoal) continue;
            if (units[i].RemainM3 <= 1e-9) continue;
            cands.Add(i);
        }
        if (cands.Count == 0)
        {
            r.Notes.Add("◆ 一个可采的煤单元都没有 —— 全都采完了。");
            r.ShortfallT = inp.CoalTargetT;
            return 0;
        }

        var freeCache = new Dictionary<int, bool>();
        bool Free(int i)
        {
            if (!freeCache.TryGetValue(i, out bool v)) { v = g.IsFree(i, null); freeCache[i] = v; }
            return v;
        }

        // ══ U1′（2026-08-18 现场令重写）：按【作业面】粘着推进，不按幅全局挑 ══════════
        //
        // 现场原话两条：「尽量整条带来控制采出」「尽量不要频繁调度设备，同时就近调度，
        // 采剥顺序要考虑衔接」。这不是给凑量加一道闸，是把凑量的【单位】从幅抬到面：
        //   一个作业面 = 一条带 = (SeamCode, BandId)（`FaceCount` 一直就是这么数的）。
        //
        // 四条：
        //   ① 上月在采的面最优先 —— 遗留幅先采完与"别转场"是同一件事，两条口径同向；
        //   ② 面一旦开了就沿走向【连续】推到底，中途不跳去别的面。跳一次就是一次转场，
        //      而且会在带里啃出锯齿：图上看是一条带在推，现场是设备来回挪，
        //      下个月的推进面还接不上（"接续"说的就是这个）；
        //   ③ 要开新面时按【就近】挑（`MoveCost`：同层换带 3 · 跨台阶 6/10）——
        //      跨台阶设备要整体搬，最贵，所以放到最后才开；
        //   ④ 量凑够就停 ⇒ 每个开过的面里【最多一个锋面幅】是部分的。
        //
        // ⚠ 旧实现是「全局按 U9 轴排序逐幅挑 + 末位切一刀 break」：
        //   轴一变就在带与带之间来回跳，转场次数只被【统计】（U11）从不被【优化】；
        //   而"每月最多一个跨月幅"其实是那个 break 的副产品，不是一条现场口径。
        //   ⚠ 现在的上限是【每个在采面 1 个】。要让多个面在同一个月各留一个锋面幅，
        //     还得有【面级的量】（份额或设备台效）把目标拆开 —— 目标是一个全局数时，
        //     贪心只会在最后一个面上切一刀。那一步要把「确定开采程序」的份额接进来，未做。
        // 面的分组与先后走公用实现（煤、岩、超前剥离都走它）—— 见 StickyFaces。
        // 煤侧多传一个 preferFirst：同等条件下先开【已露出】的面（闭包为空，不用先剥）。
        var faceOrder = StickyFaces(cands, g, inp, Free);
        int faceTotal = faceOrder.Count;

        int carry = cands.Count(i => units[i].IsCarryOver);
        if (carry > 0) r.Notes.Add($"· {carry} 个上月未采完的幅已排在最前，"
                                 + "它们所在的面【整个面】优先推完再开新面（U1′①）。");
        int nFree = cands.Count(Free);
        r.Notes.Add($"· 可采煤 {cands.Count} 条 / {faceTotal} 个作业面（带），其中 {nFree} 条已露出（闭包为空），"
                  + $"{cands.Count - nFree} 条头上还压着岩（选中就把闭包计入必剥）。");

        // ⚠ 【煤压煤】是硬约束，且**不在必剥闭包那条路上**：`mandFrac` 那个循环
        //   `if (units[p].IsCoal) continue` 跳过了煤前驱 —— 它只管"岩要跟着剥"。
        //   上覆煤层的那一幅没采，下伏煤层这一幅在现场就是够不着。
        //   旧的全局排序里这条是【碰巧】成立的（已露出优先 + 按 SeamCode 排），
        //   改成按面推进之后就不成立了：真基表上当场炸出 8 处"前驱没做完就采了下面"。
        //   所以这条得**显式判**，不能靠排序的副作用。
        bool CoalPredsReady(int i, double endFrac)
        {
            foreach (int p in g.Closure(i))
            {
                if (!units[p].IsCoal) continue;
                double after = MineUnit.Clamp01(units[p].DoneFraction + (takeFrac.TryGetValue(p, out double t) ? t : 0));
                if (after + 1e-9 < endFrac) return false;
            }
            return true;
        }

        double acc = 0;
        int opened = 0, partials = 0, blocked = 0;

        // 一次"推进"：在给定的这批面里按 U1′ 取量。
        //   want    = 取到多少就停（容差带下沿）
        //   aim     = 切锋面幅时对准的量（正对目标切，命中最准）
        //   ceiling = 整幅塞得下的上限（容差带上沿）
        // 全局路径与逐面配额路径【共用它】—— 写两份的话，改了一头忘另一头，两边各自都对、合起来错。
        double Advance(IEnumerable<List<int>> bands, double want, double aim, double ceiling, string who)
        {
            double got = 0;
            foreach (var band in bands)
            {
                if (got >= want - 1e-9) break;
                bool touched = false;
                foreach (int i in band)
                {
                    if (got >= want - 1e-9) break;
                    double whole = units[i].RemainM3 * density;
                    // 整幅要采到 100%，那么头上的煤本月末也得到 100%；够不着就跳过这一幅
                    // （跳的是【幅】不是【面】：挡住这一幅的是别的层压在它头上，
                    //   同一条带沿走向的其它幅未必被压着）。
                    if (!CoalPredsReady(i, 1.0)) { blocked++; continue; }
                    if (got + whole <= ceiling + 1e-9)
                    {
                        planned.Add(i); takeFrac[i] = Frac(units[i], units[i].RemainM3); got += whole; touched = true;
                    }
                    else
                    {
                        double need = Math.Max(0, aim - got);
                        double m3 = need / Math.Max(1e-9, density);
                        if (m3 <= 1e-6) break;
                        double endFrac = MineUnit.Clamp01(units[i].DoneFraction + Frac(units[i], m3));
                        // 切一半也一样：头上的煤要采到【本幅本月末的完成度】才够得着
                        if (!CoalPredsReady(i, endFrac)) { blocked++; continue; }
                        planned.Add(i); takeFrac[i] = Frac(units[i], m3); got += m3 * density; touched = true;
                        partials++;
                        r.Notes.Add($"· {who}锋面幅切开：{units[i].UnitId} 本月采 {takeFrac[i] * 100:0.#}%，其余整幅（U1′④）。");
                        break;                                    // 这个面到此为止，锋面停在这儿
                    }
                }
                if (touched) opened++;
            }
            return got;
        }

        var quotas = inp.FaceQuotas ?? new List<FaceQuota>();
        if (quotas.Count == 0)
        {
            // 【没有面级配额】：一个全局目标，贪心推面 —— 锋面幅只会出现在最后那个面上。
            acc = Advance(faceOrder, lo, inp.CoalTargetT, hi, "");
        }
        else
        {
            // ══ U1″ 逐面配额 ══════════════════════════════════════════════
            //   份额来自「确定开采程序」，归属来自 FaceUnitResolver，两者都由调用方给。
            //   每个面按自己的目标推进 ⇒ **每个在采面各留一个锋面幅**，这才是几台铲同时作业的形态。
            var faceOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var q in quotas)
                foreach (var id in q.UnitIds)
                    if (id.Length > 0) faceOf[id] = q.FaceId;

            var byFace = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var residual = new List<int>();
            foreach (int i in cands)
            {
                if (faceOf.TryGetValue(units[i].UnitId, out string? fid))
                {
                    if (!byFace.TryGetValue(fid, out var lst)) byFace[fid] = lst = new List<int>();
                    lst.Add(i);
                }
                else residual.Add(i);        // 归属没配上的（FA5/FA6）—— 不许摊给别人，单独排在最后
            }

            // 份额只在【真有煤可采的面】之间归一 —— 拿全表份额去分，煤会分给一个没料的面然后凭空少掉
            var live = quotas.Where(q => byFace.ContainsKey(q.FaceId) && q.SharePct > 0).ToList();
            double sumShare = live.Sum(q => q.SharePct);
            if (live.Count == 0 || sumShare <= 1e-9)
            {
                r.Notes.Add("◆ 面级配额给了，但没有一个面既有份额又有可采煤 —— 本月退回【全局一个目标】排。");
                acc = Advance(faceOrder, lo, inp.CoalTargetT, hi, "");
            }
            else
            {
                double allShare = quotas.Sum(q => Math.Max(0, q.SharePct));
                if (Math.Abs(allShare - sumShare) > 1e-6)
                    r.Notes.Add($"· 份额按【有煤可采的 {live.Count} 个面】重新归一："
                              + $"表里合计 {allShare:0.#}% → 参与分配 {sumShare:0.#}%。"
                              + "没料的面不参与，否则分给它的那份煤会凭空少掉。");

                // 面的先后仍按 U1′：遗留面优先 → 就近串（配额只决定每个面干多少，不决定先干谁）
                var ordered = live
                    .Select(q => (Q: q, Bands: StickyFaces(byFace[q.FaceId], g, inp, Free)))
                    .OrderBy(x => x.Bands.Count == 0 ? 1 : 0)
                    .ToList();

                foreach (var (q, bands) in ordered)
                {
                    double share = q.SharePct / sumShare;
                    double want = inp.CoalTargetT * share;
                    string capNote = "";
                    if (q.CapacityT > 1e-9 && want > q.CapacityT)
                    {
                        capNote = $"（份额算出 {want / 1e4:0.00}万t，被本面产能上限 {q.CapacityT / 1e4:0.00}万t 夹回）";
                        want = q.CapacityT;
                    }
                    double lof = want * (1 - Math.Max(0, inp.CoalTolLoPct) / 100.0);
                    double hif = want * (1 + Math.Max(0, inp.CoalTolHiPct) / 100.0);

                    double got;
                    if (bands.Count > 1 && inp.FaceSegments > 1)
                    {
                        // ★ 标段各自领一份量（2026-08-18 现场令：一段一台设备，同时推）。
                        //   不分的话第一段就把整面的量吃完，后面几段这个月一动不动 ——
                        //   那等于把几台设备排成一队跟着一台干，"分标段"就白分了。
                        //   **按各段可采量的比例分**，不按段数均分：段有长有短（幅数不整除），
                        //   均分会让短段吃不下、长段推不够，月末推进面参差不齐（不规整）。
                        double totVol = bands.Sum(bd => bd.Sum(i => units[i].RemainM3));
                        got = 0;
                        for (int si = 0; si < bands.Count; si++)
                        {
                            double vol = bands[si].Sum(i => units[i].RemainM3);
                            double w = totVol > 1e-9 ? want * (vol / totVol) : want / bands.Count;
                            got += Advance(new[] { bands[si] },
                                           w * (1 - Math.Max(0, inp.CoalTolLoPct) / 100.0), w,
                                           w * (1 + Math.Max(0, inp.CoalTolHiPct) / 100.0),
                                           $"{q.FaceId} 面第 {si + 1} 标段的");
                        }
                        // 各段都按自己的量收了尾，合起来若还差（某段料不够），拿本面剩下的补齐
                        if (got < lof - 1e-6) got += Advance(bands, lof - got, want - got, hif - got, $"{q.FaceId} 面补量的");
                    }
                    else got = Advance(bands, lof, want, hif, $"{q.FaceId} 面的");
                    acc += got;
                    r.Notes.Add($"· 面「{q.FaceId}」份额 {q.SharePct:0.#}% ⇒ 目标 {want / 1e4:0.00}万t，"
                              + $"实取 {got / 1e4:0.00}万t{capNote}"
                              + (got < lof - 1e-6 ? "　◆ 这个面的料不够，缺的部分下面再找别的面补" : "")
                              + (q.Note.Length > 0 ? "　" + q.Note : ""));
                }

                // 有面没凑够 ⇒ 缺口不能就这么算在头上：先拿【没归属的】补，再拿别的面剩下的补，并如实报。
                if (acc < lo - 1e-6)
                {
                    double before = acc;
                    if (residual.Count > 0)
                        acc += Advance(StickyFaces(residual, g, inp, Free), lo - acc, inp.CoalTargetT - acc, hi - acc,
                                       "未归属单元的");
                    if (acc < lo - 1e-6)
                    {
                        var leftover = cands.Where(i => !planned.Contains(i)).ToList();
                        if (leftover.Count > 0)
                            acc += Advance(StickyFaces(leftover, g, inp, Free), lo - acc, inp.CoalTargetT - acc, hi - acc,
                                           "补量的");
                    }
                    r.Notes.Add($"· 按份额只凑到 {before / 1e4:0.00}万t，差的部分从【别的面 / 未归属单元】补到 {acc / 1e4:0.00}万t"
                              + " —— 补出来的量**不算在原来那个面的份额里**，下月对账时按实际归属看。");
                }
                if (residual.Count > 0)
                    r.Notes.Add($"· 有 {residual.Count} 个可采煤幅【没有归属到任何作业面】（FA5 歧义 / FA6 没命中）。"
                              + "它们只在按份额凑不够时才被拿来补 —— 要让它们正常参与，去「归属覆盖」把面指定上。");
            }
        }

        string segNote = inp.FaceSegments > 1
            ? $"（每条带切 {inp.FaceSegments} 个标段，一段一台设备；幅数不够的带按幅数少切几段）" : "";
        r.Notes.Add($"· 本月开了 {opened} 个作业面{segNote}（共 {faceTotal} 个可选），锋面幅 {partials} 个"
                  + "—— 段内沿台阶线连续推进，不跳幅、不跳带；开新面按就近（同层换带优先于跨台阶，U1′②③）。");
        if (blocked > 0)
            r.Notes.Add($"· {blocked} 个幅本月够不着：头上还压着【别的煤层】没采的幅（煤压煤是硬约束，"
                      + "不是靠剥岩能解决的）。要采它们，得先把上覆那一层对应的幅排进来。");

        if (acc < lo - 1e-6)
        {
            r.ShortfallT = inp.CoalTargetT - acc;
            r.Notes.Add($"◆ 煤没凑够：前沿上可采 {acc / 1e4:0.00}万t，目标 {inp.CoalTargetT / 1e4:0.00}万t，"
                      + $"欠 {r.ShortfallT / 1e4:0.00}万t。这是【采不出来】，不是排不下也不是拉不动（U8）。");
        }
        return acc;
    }

    /// <summary>
    /// <b>U2 三级来源 + 两道硬夹</b> —— 解出本月的<b>有效</b>目标剥离量（m³实方）。
    ///
    /// <para>来源按顺序取<b>第一个给得出来的</b>，一个量只有一个来源：
    /// ① <see cref="UnitPlanInput.StripTargetM3"/> 直接给定的排弃量（U13，常规路线）·
    /// ② 煤量 × <see cref="UnitPlanInput.TargetStripRatio"/> 目标剥采比 ·
    /// ③ 必剥闭包（两个都没给 ⇒ 不做超前剥离）。</para>
    ///
    /// <para>两道夹，每道都留条：<b>必剥闭包是硬下界</b>（少剥一方，压在下面的煤就采不出来）·
    /// <b>剥离能力是硬上界</b>。</para>
    ///
    /// <para><b>⚠ U13.3：给定排弃量时不拿煤量参与运算</b> —— 实际选到的煤量偏离目标时
    /// 照绝对量剥，不等比缩。按比缩等于排弃量又退回由剥采比驱动，用户填的数不会原样兑现，
    /// 而每一项校核仍然是 ✓。所以这里 <c>coalT</c> 只用来<b>算等效比报给用户看</b>，不进目标。</para>
    ///
    /// <para>解完把 <see cref="UnitPlanResult.StripTargetM3"/> / <see cref="UnitPlanResult.StripTargetNote"/>
    /// 落在 <paramref name="r"/> 上 —— 缺口按<b>有效目标</b>算，所以这个数必须带出去。</para>
    /// </summary>
    private static double ResolveStripTarget(double coalT, double mandM3, UnitPlanInput inp, UnitPlanResult r)
    {
        double target;
        string src;

        // 负数是填错，不是"没给" —— 两者在 `≤0` 这个判据下长得一模一样，所以必须说出来（INV5）
        if (inp.StripTargetM3 < 0)
            r.Notes.Add($"⚠ 本月排弃量填的是负数（{inp.StripTargetM3 / 1e4:0.0}万m³），已按【没给】处理。");

        if (inp.StripTargetM3 > 0)
        {
            target = inp.StripTargetM3;
            double equiv = coalT > 1e-9 ? target / coalT : 0;
            src = $"【给定排弃量】{target / 1e4:0.0}万m³实方（U13，等效剥采比 {equiv:0.00}）";
            if (inp.TargetStripRatio > 0)
                r.Notes.Add($"· 排弃量与目标剥采比<b>都给了 ⇒ 以排弃量为准</b>（U13.4）："
                          + $"按 {target / 1e4:0.0}万m³ 排，等效剥采比 {equiv:0.00}，"
                          + $"填的那个 {inp.TargetStripRatio:0.00} 本次<b>没有参与运算</b>。");
            r.Notes.Add("· 排弃量按【绝对量】剥，<b>不随实际煤量等比缩放</b>（U13.3）—— 剥采比是结果不是输入。");
        }
        else if (inp.TargetStripRatio > 0)
        {
            target = coalT * inp.TargetStripRatio;
            src = $"【煤量 × 目标剥采比】{coalT / 1e4:0.00}万t × {inp.TargetStripRatio:0.00} = {target / 1e4:0.0}万m³";
            r.Notes.Add("· 没给本月排弃量，目标剥离量由【剥采比 × 实际选到的煤量】推出（U2 第②级）—— "
                      + "煤量凑不满时剥离量会跟着少。要让剥离量说了算，请直接填本月排弃量。");
        }
        else
        {
            target = mandM3;
            src = $"【必剥闭包】{mandM3 / 1e4:0.0}万m³（排弃量与剥采比都没给）";
            r.Notes.Add("· 排弃量与目标剥采比都没给，本月只剥【必剥闭包】（不做超前剥离）。");
        }

        // ── 硬下界：必剥闭包 ──
        if (target < mandM3 - 1e-6)
        {
            double actual = coalT > 1e-9 ? mandM3 / coalT : 0;
            r.Notes.Add($"⚠ 目标给低了（{src}）：光是必剥闭包就有 {mandM3 / 1e4:0.0}万m³，"
                      + $"本月实际剥采比 {actual:0.00}。<b>按闭包剥，不缩放</b> —— 少剥一方，压在下面的煤就采不出来。");
            target = mandM3;
            src += $" → 被必剥闭包顶到 {mandM3 / 1e4:0.0}万m³";
        }

        // ── 硬上界：剥离能力。⚠ 夹回造成的少剥【不算缺口】（那是用户自己给的闸，已留条）──
        if (inp.StripCapM3 > 0 && target > inp.StripCapM3 + 1e-6)
        {
            if (mandM3 > inp.StripCapM3 + 1e-6)
                r.Notes.Add($"◆ 必剥闭包 {mandM3 / 1e4:0.0}万m³ 已超剥离能力 {inp.StripCapM3 / 1e4:0.0}万m³ —— "
                          + "这个煤量在本月的能力下采不出来。已按闭包排，能力那条如实报。");
            else
                r.Notes.Add($"· 目标剥离量 {target / 1e4:0.0}万m³ 超剥离能力 {inp.StripCapM3 / 1e4:0.0}万m³，已夹回。"
                          + "超前剥离少了，下月备采会紧。<b>夹回的这部分不记作缺口</b> —— 它是你给的闸，不是干不成。");
            target = Math.Max(mandM3, inp.StripCapM3);
            src += $" → 被剥离能力夹到 {target / 1e4:0.0}万m³";
        }

        r.StripTargetM3 = target;
        r.StripTargetNote = src;
        return target;
    }

    /// <summary>
    /// 超前剥离的候选顺序：前沿上、<b>还有余量可剥</b>的岩单元。
    ///
    /// <para><b>⚠ 不能按"在不在必剥集合里"整幅剔除</b>：U10 之后必剥是<b>按比例</b>算的
    /// （煤幅只采 70%，头上的岩也只必剥 70%），所以一个岩幅完全可以<b>一部分必剥、一部分还能多剥</b>。
    /// 早先写成 <c>mandatory.Contains(i) → continue</c>，把这类幅整幅赶出了候选，
    /// 于是补岩循环里那句 <c>double already = rockPick[i]</c>（本意正是给这类幅做去重）
    /// <b>永远是 0，成了死代码</b> —— 判据实测：目标剥离 67.5 万m³，实际只剥到必剥的 14.0 万m³，
    /// 前沿上还有 8.0 万m³ 剥得动的岩没动，而结果照样"成功"。</para>
    ///
    /// <para>正确判据是<b>这一幅还剩多少没排</b>：期初完成度 + 已计入必剥的比例 ≥ 1 才算排满。</para>
    /// </summary>
    private static List<int> ExtraRockOrder(UnitGraph g, UnitPlanInput inp,
                                            IReadOnlyDictionary<int, double> mandFrac, HashSet<int> planned)
    {
        var units = g.Units;
        var free = new HashSet<int>(planned); foreach (int i in mandFrac.Keys) free.Add(i);
        var list = new List<int>();
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].IsCoal) continue;
            double taken = mandFrac.TryGetValue(i, out double mf) ? mf : 0;
            // 还剩多少可排 = 1 − 期初完成度 − 已计入必剥的那部分
            double left = 1.0 - MineUnit.Clamp01(units[i].DoneFraction) - taken;
            if (left <= 1e-9) continue;                       // 这一幅本月已排满，才真的不该再进候选
            if (units[i].InSituM3 * left <= 1e-6) continue;
            if (!g.IsFree(i, free)) continue;
            list.Add(i);
        }
        // ★ 前驱必须排在前面：补岩时要按"前驱本月末的完成度"给本幅封顶，
        //   而前驱如果还没被处理，那个顶就取到了旧值（等于没封）。
        //   按压覆深度升序 ⇒ 处理到某一幅时它的前驱一定已经定完。
        var depth = new Dictionary<int, int>();
        int Depth(int i, int guard)
        {
            if (depth.TryGetValue(i, out int d)) return d;
            if (guard > 256) return 0;                       // 环的兜底（UnitGraph 已经报过环）
            depth[i] = 0;
            int best = 0;
            foreach (int p in g.Pred[i]) best = Math.Max(best, Depth(p, guard + 1) + 1);
            depth[i] = best;
            return best;
        }
        foreach (int i in list) Depth(i, 0);

        // ★ 层内也按【作业面粘着】排（2026-08-18 现场令：「岩和排土的作业也是一样的，
        //   都尽量考虑设备衔接，不要来回来去调度设备」）。
        //   原先层内直接用 CompareUnit ⇒ 按全局轴（长带优先/低剥采比/近路）横扫，
        //   一条剥离带只剥一幅就跳去另一条带 —— 而**剥离才是设备大部分时间在干的事**：
        //   真基表上 81.7 万m³ 的剥离里只有 8.5 万是必剥，九成是这条路上挑出来的。
        //   煤侧改了、岩侧不改，等于只优化了一成的工作量。
        //   ⚠ 深度分层不能动：补岩要按"前驱本月末的完成度"封顶，前驱必须先被处理。
        var ordered = new List<int>(list.Count);
        foreach (var layer in list.GroupBy(i => depth[i]).OrderBy(gr => gr.Key))
            foreach (var face in StickyFaces(layer.ToList(), g, inp))
                ordered.AddRange(face);
        return ordered;
    }

    /// <summary>
    /// 把一批单元按<b>作业面粘着</b>排成"面的序列"，面内是沿走向连续的幅。
    ///
    /// <para><b>煤和岩共用这一份</b>（现场原话：「岩和排土的作业也是一样的，都尽量考虑设备衔接，
    /// 不要来回来去调度设备」）。写两份的话，改了一侧忘了另一侧 ——
    /// 而两侧各自看上去都对，只有把转场次数摆在一起才看得出来。</para>
    ///
    /// <para>三条：① 有遗留幅的面最优先（那台设备本来就在那儿）；
    /// ② 面内沿走向连续（<see cref="PanelRank"/>，U9 轴 2）；
    /// ③ 开新面按 <see cref="MoveCost"/> 就近串（同层换带便宜、跨台阶最贵）。
    /// <paramref name="preferFirst"/> 给"同等条件下先挑谁"再加一道（煤侧用它把已露出的排前面）。</para>
    /// </summary>
    private static List<List<int>> StickyFaces(IReadOnlyList<int> pool, UnitGraph g, UnitPlanInput inp,
                                               Func<int, bool>? preferFirst = null)
    {
        var units = g.Units;
        var faces = pool
            .GroupBy(i => (units[i].SeamCode, units[i].BandId))
            .Select(gr =>
            {
                var panels = gr.ToList();
                panels.Sort((a, b) =>
                {
                    if (units[a].IsCarryOver != units[b].IsCarryOver) return units[a].IsCarryOver ? -1 : 1;
                    int c = PanelRank(units[a], inp.StrikeAdvance).CompareTo(PanelRank(units[b], inp.StrikeAdvance));
                    return c != 0 ? c : string.CompareOrdinal(units[a].UnitId, units[b].UnitId);
                });
                return panels;
            })
            .ToList();

        // ★ 标段（2026-08-18 现场令）：沿走向把一条带切成 N 段，一段一台（组）设备。
        //   段内仍是刚排好的那个走向序 —— **只切段界，不改段内顺序**，否则"沿台阶线采"就没了。
        //   按幅数连续均分（多出来的余数摊给前面几段），段界永远落在幅与幅之间：
        //   按量分会把段界切进一幅中间，图上就是犬牙交错的作业面，两台设备互相别着走。
        int segs = Math.Max(1, inp.FaceSegments);
        if (segs > 1)
        {
            var split = new List<List<int>>(faces.Count * segs);
            foreach (var f in faces)
            {
                int k = Math.Min(segs, f.Count);          // 幅数不够就少切几段，不切空段
                int baseLen = f.Count / k, rem = f.Count % k;
                int at = 0;
                for (int s = 0; s < k; s++)
                {
                    int len = baseLen + (s < rem ? 1 : 0);
                    split.Add(f.GetRange(at, len));
                    at += len;
                }
            }
            faces = split;
        }

        int HeadCmp(List<int> fa, List<int> fb)
        {
            bool ca = fa.Any(i => units[i].IsCarryOver), cb = fb.Any(i => units[i].IsCarryOver);
            if (ca != cb) return ca ? -1 : 1;
            if (preferFirst != null)
            {
                bool ga = preferFirst(fa[0]), gb = preferFirst(fb[0]);
                if (ga != gb) return ga ? -1 : 1;
            }
            return CompareUnit(units[fa[0]], units[fb[0]], inp, g);
        }

        var pending = new List<List<int>>(faces);
        var order = new List<List<int>>(faces.Count);
        List<int>? cur = null;
        while (pending.Count > 0)
        {
            // 还有遗留面就只在遗留面里挑 —— 它们本月必须先干完
            var scope = pending.Where(f => f.Any(i => units[i].IsCarryOver)).ToList();
            if (scope.Count == 0) scope = pending;
            var pick = scope[0];
            foreach (var f in scope)
            {
                if (ReferenceEquals(f, pick)) continue;
                int c = cur == null ? 0 : MoveCost(units[cur[^1]], units[f[0]])
                                          .CompareTo(MoveCost(units[cur[^1]], units[pick[0]]));
                if (c < 0 || (c == 0 && HeadCmp(f, pick) < 0)) pick = f;
            }
            order.Add(pick); pending.Remove(pick); cur = pick;
        }
        return order;
    }

    /// <summary>优先级比较：遗留幅 → U9 轴1 → 同带内按 U9 轴2 → UnitId（保证确定性）。</summary>
    private static int CompareUnit(MineUnit a, MineUnit b, UnitPlanInput inp, UnitGraph g)
    {
        if (a.IsCarryOver != b.IsCarryOver) return a.IsCarryOver ? -1 : 1;
        int c = inp.FacePriority switch
        {
            FacePriority.LongestFirst => b.StrikeLenM.CompareTo(a.StrikeLenM),
            FacePriority.LowestRatioFirst => LocalRatio(a, g).CompareTo(LocalRatio(b, g)),
            FacePriority.NearestFirst => NearestSinkKm(a, inp).CompareTo(NearestSinkKm(b, inp)),
            _ => 0,
        };
        if (c != 0) return c;
        if (a.SeamCode != b.SeamCode) return string.CompareOrdinal(a.SeamCode, b.SeamCode);
        if (a.BandId != b.BandId) return a.BandId.CompareTo(b.BandId);
        c = PanelRank(a, inp.StrikeAdvance).CompareTo(PanelRank(b, inp.StrikeAdvance));
        return c != 0 ? c : string.CompareOrdinal(a.UnitId, b.UnitId);
    }

    /// <summary>本单元的局部剥采比 = 压在它头上的岩量 ÷ 它的吨量。</summary>
    private static double LocalRatio(MineUnit u, UnitGraph g)
    {
        int i = g.IndexOf(u.UnitId);
        if (i < 0 || !u.IsCoal) return double.MaxValue;
        double rock = g.Closure(i).Where(p => !g.Units[p].IsCoal).Sum(p => g.Units[p].RemainM3);
        double t = u.RemainM3 * 1.35;
        return t > 1e-9 ? rock / t : double.MaxValue;
    }

    private static double NearestSinkKm(MineUnit u, UnitPlanInput inp)
    {
        double best = double.MaxValue;
        foreach (var s in inp.CoalSinks) best = Math.Min(best, Dist3(u.Cx, u.Cy, u.Cz, s.Cx, s.Cy, s.Cz));
        foreach (var s in inp.Slots) best = Math.Min(best, Dist3(u.Cx, u.Cy, u.Cz, s.Cx, s.Cy, s.Cz));
        return best == double.MaxValue ? 0 : best;
    }

    /// <summary>沿走向的推进方式（U9 轴 2）—— 只改同一条带内各幅的先后。</summary>
    private static int PanelRank(MineUnit u, StrikeAdvance mode)
    {
        int n = Math.Max(1, u.PanelCount), p = Math.Max(1, u.PanelIndex);
        switch (mode)
        {
            case StrikeAdvance.BothEnds:                     // 两端往中间：离哪一端近就多早上
                return Math.Min(p - 1, n - p);
            case StrikeAdvance.FromMiddle:                   // 中间往两端：离中点越近越早
                return (int)Math.Round(Math.Abs(p - (n + 1) / 2.0) * 2);
            default:
                return p;                                     // 单向：幅号由小到大
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ④ 配对 + ⑥ 定序 —— 把选中的量摊到去向上，排出月内推进序
    // ══════════════════════════════════════════════════════════════════
    private static UnitPlanResult Assemble(UnitGraph g, UnitPlanInput inp, GapMaterial[] mats,
                                           List<DumpSlot> slots, List<CoalSink> sinks, double density,
                                           HashSet<int> coalPick, Dictionary<int, double> coalFrac,
                                           Dictionary<int, double> rockPick, HashSet<int> mandatory,
                                           int noteBase)
    {
        var r = new UnitPlanResult { Success = true };
        var units = g.Units;

        var picked = new List<(int idx, double frac)>();
        foreach (var kv in coalFrac) picked.Add((kv.Key, kv.Value));
        foreach (var kv in rockPick) if (kv.Value > 1e-9) picked.Add((kv.Key, kv.Value));

        // ⑥ 定序（U11）：按【压覆深度】分层，**层内走最少转场的链**。
        //
        // 深度序保证前驱必在前（硬的）；层内则不再单纯按作业面优先级排，
        // 而是从优先级最高的那个单元起，每次挑【转场代价最小】的下一个：
        //   同带相邻幅 = 0（设备原地推进） < 同层同面 < 同采场 < 跨采场/跨层。
        // 现场原话：「尽量整条带作业，减少大规模的设备移运」。
        //
        // ⚠ 它是【目标】不是硬约束：带的量是几何给的，不会正好等于月目标，
        //   写成硬约束会让煤量凑不准。所以只动顺序，不动选谁。
        var depth = Depths(g, picked.Select(p => p.idx).ToHashSet());
        var ordered = new List<(int idx, double frac)>(picked.Count);
        foreach (var band in picked.GroupBy(p => depth[p.idx]).OrderBy(gp => gp.Key))
        {
            var pool = band.ToList();
            pool.Sort((x, y) => CompareUnit(units[x.idx], units[y.idx], inp, g));
            // 贪心最近邻：起点用优先级最高的那个，之后每步挑转场最便宜的
            var chain = new List<(int idx, double frac)>(pool.Count);
            var cur = pool[0];
            pool.RemoveAt(0);
            chain.Add(cur);
            while (pool.Count > 0)
            {
                int best = 0; double bestCost = double.MaxValue;
                for (int k = 0; k < pool.Count; k++)
                {
                    // 代价里带上 k：同代价时保持优先级顺序，保证【同输入同输出】
                    double c = MoveCost(units[cur.idx], units[pool[k].idx]) + k * 1e-6;
                    if (c < bestCost) { bestCost = c; best = k; }
                }
                cur = pool[best];
                pool.RemoveAt(best);
                chain.Add(cur);
            }
            ordered.AddRange(chain);
        }
        picked = ordered;

        var byDump = slots.GroupBy(s => s.DumpName)
                          .ToDictionary(k => k.Key, k => k.OrderBy(s => s.Level).ThenBy(s => s.Order).ToList());
        double intRoom = inp.InternalCumCapM3 > 0 ? inp.InternalCumCapM3 : double.PositiveInfinity;
        foreach (var s in slots) if (s.IsInternal) intRoom -= s.Used;

        int seq = 0;
        // 排土的"设备衔接"：正在卸的那个位置 + 换了几次（见 PickSlot 的粘着）。
        // ⚠ 它是【本轮】的状态：车队回环每轮都把 slot.Used 清零重来，粘着也必须跟着重来。
        DumpSlot? stickySlot = null;
        int slotSwitches = 0;
        foreach (var (i, frac) in picked)
        {
            var u = units[i];
            double m3 = u.InSituM3 * frac;
            if (m3 <= 1e-9) continue;
            var a = new UnitAssignment
            {
                UnitId = u.UnitId, Kind = u.Kind, SeamCode = u.SeamCode,
                BandId = u.BandId, PanelIndex = u.PanelIndex,
                Seq = ++seq,
                InSituM3 = m3, Fraction = frac,
                DoneAfter = MineUnit.Clamp01(u.DoneFraction + frac),
                IsMandatory = mandatory.Contains(i),
            };

            if (u.IsCoal) { a.TonnageT = m3 * density; PlaceCoal(a, u, sinks, inp, r); }
            else
            {
                var mat = u.MaterialIndex < mats.Length ? mats[u.MaterialIndex] : null;
                a.TonnageT = m3 * (mat?.Density ?? 2.5);
                PlaceRock(a, u, mat, byDump, inp, r, ref intRoom, ref stickySlot, ref slotSwitches);
            }
            r.Assignments.Add(a);
        }

        // 汇总
        r.CoalT = r.Coal.Sum(a => a.TonnageT);
        r.StripM3 = r.Rock.Sum(a => a.InSituM3);
        r.TransportWorkTKm = r.Assignments.Sum(a => a.HaulWorkTKm);
        r.UnplacedM3 = r.Rock.Sum(a => a.UnplacedM3);
        double dumpIn = r.Rock.SelectMany(a => a.Flows).Sum(f => f.InSituM3);
        double dumpInternal = r.Rock.SelectMany(a => a.Flows).Where(f => f.IsInternalDump).Sum(f => f.InSituM3);
        // ⚠ 没有排土流时是 −1（"没跑配对"），不是 0（0 是"全外排"这个真实结论）
        r.InternalRatePct = dumpIn > 1e-9 ? dumpInternal / dumpIn * 100 : -1;

        // ── 作业组织便利性：按【实际排出来的顺序】数，不是按选中的集合数 ──
        //    顺序不同、转场次数就不同，这正是 U11 那条轴要体现的差别。
        r.FaceCount = r.Assignments.Select(a => (a.SeamCode, a.BandId)).Distinct().Count();
        r.DumpSlotSwitches = slotSwitches;
        r.DumpSlotCount = r.Rock.SelectMany(a => a.Flows)
                               .Select(f => f.DestinationCode).Distinct(StringComparer.Ordinal).Count();
        for (int i = 1; i < picked.Count; i++)
        {
            var p = units[picked[i - 1].idx];
            var q = units[picked[i].idx];
            double c = MoveCost(p, q);
            r.MoveCost += c;
            if (c > 0) r.MoveCount++;
            // 跨层 = 不同煤层/台阶名：设备要整体搬台阶，这类最贵
            if (!string.Equals(p.SeamCode, q.SeamCode, StringComparison.Ordinal)) r.CrossLevelMoves++;
        }

        r.HaulNote = r.HaulTotal == 0 ? "没有流"
            : inp.Haul == null
                ? $"没接路网，{r.HaulTotal} 笔全按直线×{inp.FallbackDetour:0.##} 兜底（不随推进变）"
                : $"路网命中 {r.HaulHits}/{r.HaulTotal} 笔"
                  + (r.HaulHits < r.HaulTotal ? $"，其余 {r.HaulTotal - r.HaulHits} 笔按直线×{inp.FallbackDetour:0.##} 兜底" : "");
        return r;
    }

    private static void PlaceCoal(UnitAssignment a, MineUnit u, List<CoalSink> sinks, UnitPlanInput inp, UnitPlanResult r)
    {
        if (sinks.Count == 0)
        {
            // 不假装有去向：没有出矿位置就是没有，运输功里煤这一半就缺着，明说。
            a.UnplacedM3 = 0;
            if (!r.Notes.Any(n => n.StartsWith("· 没给出矿位置")))
                r.Notes.Add("· 没给出矿位置，煤流不计运输功 —— 车队能力那条闸只卡住了岩这一半。");
            return;
        }
        double left = a.InSituM3;
        int guard = 0;
        while (left > 1e-6 && ++guard <= sinks.Count + 2)
        {
            CoalSink? best = null; double bestKm = 0;
            foreach (var s in sinks)
            {
                if (s.Remain <= 1e-9) continue;
                double km = HaulKm(u, s.Cx, s.Cy, s.Cz, s.HaulKm, inp, r, out _);
                if (best == null || km < bestKm) { best = s; bestKm = km; }
            }
            if (best == null) break;
            double takeT = Math.Min(left * (a.TonnageT / Math.Max(1e-9, a.InSituM3)), best.Remain);
            double takeM3 = takeT / Math.Max(1e-9, a.TonnageT / Math.Max(1e-9, a.InSituM3));
            if (takeM3 <= 1e-9) break;
            double km2 = HaulKm(u, best.Cx, best.Cy, best.Cz, best.HaulKm, inp, r, out bool hit);
            r.HaulTotal++; if (hit) r.HaulHits++;
            best.Used += takeT;
            left -= takeM3;
            a.Flows.Add(new UnitFlow
            {
                DestinationCode = best.Code.Length > 0 ? best.Code : best.Name,
                DestinationName = best.Name, IsCoalSink = true,
                Dx = best.Cx, Dy = best.Cy, Dz = best.Cz,
                InSituM3 = takeM3, DumpM3 = 0, TonnageT = takeT, HaulKm = km2, HaulFromNetwork = hit,
                MaterialName = "煤", MaterialCode = "coal", Density = inp.CoalDensity, Kr = 0,
            });
        }
        if (left > 1e-6)
        {
            a.UnplacedM3 = left;
            r.Notes.Add($"◆ {a.UnitId}：出矿位置通过能力不够，{left / 1e4:0.00}万m³ 送不出去。");
        }
    }

    private static void PlaceRock(UnitAssignment a, MineUnit u, GapMaterial? mat,
                                  Dictionary<string, List<DumpSlot>> byDump, UnitPlanInput inp,
                                  UnitPlanResult r, ref double intRoom, ref DumpSlot? sticky, ref int switches)
    {
        if (mat == null)
        {
            a.UnplacedM3 = a.InSituM3;
            r.Notes.Add($"◆ {a.UnitId} 没有物料参数（ρ/Kr/允许去向），无法配对，如实计为排不下。");
            return;
        }
        double left = a.InSituM3;
        int guard = 0;
        while (left > 1e-6 && ++guard <= byDump.Sum(kv => kv.Value.Count) + 4)
        {
            var slot = PickSlot(byDump, mat, u, inp, r, intRoom > 1e-6, sticky);
            if (slot == null) break;
            // 换卸点要记账：这个数就是"推土机被挪了几次"，不报的话粘着有没有起作用没人看得出来
            if (!ReferenceEquals(slot, sticky)) { if (sticky != null) switches++; sticky = slot; }
            double roomInSitu = slot.Remain / Math.Max(1e-6, mat.Kr);
            if (slot.IsInternal && !double.IsPositiveInfinity(intRoom))
                roomInSitu = Math.Min(roomInSitu, intRoom / Math.Max(1e-6, mat.Kr));
            double take = Math.Min(roomInSitu, left);
            if (take <= 1e-9) break;

            double km = HaulKm(u, slot.Cx, slot.Cy, slot.Cz, slot.HaulKm, inp, r, out bool hit);
            r.HaulTotal++; if (hit) r.HaulHits++;
            slot.Used += take * mat.Kr;
            if (slot.IsInternal) intRoom -= take * mat.Kr;
            left -= take;
            a.Flows.Add(new UnitFlow
            {
                DestinationCode = SlotCode(slot), DestinationName = slot.DumpName,
                IsInternalDump = slot.IsInternal,
                Dx = slot.Cx, Dy = slot.Cy, Dz = slot.Cz,
                InSituM3 = take, DumpM3 = take * mat.Kr, TonnageT = take * mat.Density,
                HaulKm = km, HaulFromNetwork = hit,
                MaterialName = mat.Name, MaterialCode = mat.Code, Density = mat.Density, Kr = mat.Kr,
            });
        }
        if (left > 1e-6) a.UnplacedM3 = left;
    }

    /// <summary>
    /// 挑位置。<b>去向</b>按策略选，<b>位置</b>不由策略定 —— 同一去向内永远是"最低的未满级、级内按顺序"
    /// （自下而上）。这条与 <see cref="DumpAllocator"/> <b>逐字同口径</b>：两处口径漂了没人看得出来。
    /// </summary>
    /// <summary>
    /// 排土侧的"设备衔接"（2026-08-18 现场令）：<b>正在卸的那个位置没填满就继续往那儿卸</b>，
    /// 填满了才按排弃顺序/配对策略开下一个。
    ///
    /// <para><b>为什么是硬粘着而不是打个分</b>：排土场上作业的是推土机，它跟着卸点走。
    /// 逐笔按"运输功最小"挑的话，相邻两笔岩的质心差几十米就可能挑到不同位置，
    /// 一个月下来卸点换几十次 —— 卡车省下的那点运距，换成推土机来回搬。
    /// 现场是"一个位置填满再挪"，所以这里也这么排。</para>
    ///
    /// <para><b>它不越过任何硬约束</b>：允许去向、自下而上的承接级、启用月份、剩余库容
    /// 四条照判；粘着只在"这个位置仍然合法"时生效。</para>
    /// </summary>
    /// <summary>
    /// 这一级该从哪一端起步：<b>离源远的那一端更近</b>时返回 true（即倒序推进）。
    /// <para>D-R1 只规定"单向"，没规定从哪头。起步端由排弃顺序那条轴定一次，
    /// <b>定了就不许再翻</b> —— 翻一次锋面就掉头，前面留下的半截再也补不上。</para>
    /// </summary>
    private static bool FarEndCloser(List<DumpSlot> slots, int level, MineUnit src, UnitPlanInput inp)
    {
        DumpSlot? lo = null, hi = null;
        foreach (var s in slots)
        {
            if (s.Level != level || s.AvailableFromMonth > inp.Month || s.Remain <= 1e-9) continue;
            if (lo == null || s.Order < lo.Order) lo = s;
            if (hi == null || s.Order > hi.Order) hi = s;
        }
        if (lo == null || hi == null || ReferenceEquals(lo, hi)) return false;
        return Dist3(src.Cx, src.Cy, src.Cz, hi.Cx, hi.Cy, hi.Cz)
             < Dist3(src.Cx, src.Cy, src.Cz, lo.Cx, lo.Cy, lo.Cz);
    }

    private static bool StillUsable(DumpSlot s, Dictionary<string, List<DumpSlot>> byDump, GapMaterial mat,
                                    UnitPlanInput inp, bool internalAllowed)
    {
        if (s.Remain <= 1e-9 || s.AvailableFromMonth > inp.Month) return false;
        if (mat.AllowedDumps.Length > 0 && !mat.AllowedDumps.Contains(s.DumpName)) return false;
        if (!internalAllowed && s.IsInternal) return false;
        if (!byDump.TryGetValue(s.DumpName, out var slots)) return false;
        // 仍然是这个场的【最低未满级】吗 —— 它满了之后就该往上走了
        foreach (var q in slots)
        {
            if (q.AvailableFromMonth > inp.Month || q.Remain <= 1e-9) continue;
            return q.Level == s.Level;
        }
        return false;
    }

    private static DumpSlot? PickSlot(Dictionary<string, List<DumpSlot>> byDump, GapMaterial mat,
                                      MineUnit src, UnitPlanInput inp, UnitPlanResult r, bool internalAllowed,
                                      DumpSlot? sticky = null)
    {
        if (sticky != null && StillUsable(sticky, byDump, mat, inp, internalAllowed)) return sticky;

        DumpSlot? best = null; double bestKey = 0;
        foreach (var kv in byDump)
        {
            if (mat.AllowedDumps.Length > 0 && !mat.AllowedDumps.Contains(kv.Key)) continue;   // 允许去向：硬约束
            if (!internalAllowed && kv.Value.Count > 0 && kv.Value[0].IsInternal) continue;

            // ① 硬约束：同一去向只有【最低的未满级】在收（排土台阶自下而上）。
            //    kv.Value 已按 (Level, Order) 排好，所以第一个可用的就落在最低未满级上。
            int curLevel = int.MaxValue;
            foreach (var s in kv.Value)
            {
                if (s.AvailableFromMonth > inp.Month || s.Remain <= 1e-9) continue;
                curLevel = s.Level; break;
            }
            if (curLevel == int.MaxValue) continue;

            // ★ D-R1【单向推进】（2026-08-18 现场令：「排土是按照一个方向、由不同车辆依次
            //   沿一个方向向另一个方向推进的」）：级内的填充锋面**只朝一个方向走**。
            //
            //   ⇒ 候选恒取【当前级里序号最小的未满位置】（Order = 带×1e6+幅×100，就是沿台阶线的次序）。
            //     多台车同时卸不改变这一点 —— 它们卸在同一个锋面上，不是各挑各的位置。
            //   ⚠ 「离源最近」「余量最多」那两条轴会让锋面在带上来回跳（图上就是东一块西一块），
            //     与本规则冲突：现在它们只用来定**从哪一端起步**（见下），不再逐笔挑位置。
            //   ⚠ 起步端一旦定下就不许再翻 —— 翻一次，锋面就掉头，前面留下的半截永远补不上。
            {
                DumpSlot? front = null;
                // 起步端：MostRoom = 反向（从序号大的那端起）· NearestToSource = 离源近的那端 · 其余 = 正向。
                // ⚠ 别写成 `is A or B && cond`：`&&` 只作用在 B 上，MostRoom 那一支就永远是正向
                //   （判据当场逮到：正反两向序列逐字相同）。
                bool reverse = inp.DumpOrder == DumpOrder.MostRoom
                    || (inp.DumpOrder == DumpOrder.NearestToSource && FarEndCloser(kv.Value, curLevel, src, inp));
                foreach (var s in kv.Value)
                {
                    if (s.Level != curLevel) continue;
                    if (s.AvailableFromMonth > inp.Month || s.Remain <= 1e-9) continue;
                    if (front == null) { front = s; continue; }
                    bool better = reverse ? s.Order > front.Order : s.Order < front.Order;
                    if (better) front = s;
                }
                if (front != null)
                {
                    double hk0 = HaulKm(src, front.Cx, front.Cy, front.Cz, front.HaulKm, inp, r, out _);
                    double key0 = inp.Strategy switch
                    {
                        PairingStrategy.InternalFirst => (front.IsInternal ? 0 : 1e6) + hk0,
                        PairingStrategy.LevelCapacity => -kv.Value.Sum(s => s.Remain),
                        _ => hk0,
                    };
                    if (best == null || key0 < bestKey) { best = front; bestKey = key0; }
                    continue;                       // 这个场的锋面已定，不再逐位置挑
                }
            }

            // ② 软选择（U12 排弃顺序）：级内先填哪个位置。
            //    这条轴是【只有一个排土场时唯一还活着的排弃维度】——
            //    PairingStrategy 挑的是"去哪个场"，一个场就必然塌（实测三种策略答案逐位相同）。
            DumpSlot? head = null; double headKey = 0;
            foreach (var s in kv.Value)
            {
                if (s.Level > curLevel) break;                       // 已越过承接级
                if (s.Level != curLevel) continue;
                if (s.AvailableFromMonth > inp.Month || s.Remain <= 1e-9) continue;
                double ordKey = inp.DumpOrder switch
                {
                    DumpOrder.PanelThenStep   => PanelFirstKey(s.Order),
                    DumpOrder.NearestToSource => Dist3(src.Cx, src.Cy, src.Cz, s.Cx, s.Cy, s.Cz),
                    DumpOrder.MostRoom        => -s.Remain,
                    _                         => s.Order,            // StepThenPanel：Order = 带×1e6 + 幅×100 + 子
                };
                if (head == null || ordKey < headKey) { head = s; headKey = ordKey; }
            }
            if (head == null) continue;
            double hk = HaulKm(src, head.Cx, head.Cy, head.Cz, head.HaulKm, inp, r, out _);
            double key = inp.Strategy switch
            {
                PairingStrategy.InternalFirst => (head.IsInternal ? 0 : 1e6) + hk,
                PairingStrategy.LevelCapacity => -kv.Value.Sum(s => s.Remain),
                _ => hk,
            };
            if (best == null || key < bestKey) { best = head; bestKey = key; }
        }
        return best;
    }

    /// <summary>
    /// 单元质心 → 去向的等效运距（km）。<b>源点是单元的真质心</b>，
    /// 不用像量层那样从推进坐标 u 反推 —— 那个近似在体的粒度上直接消失。
    /// </summary>
    private static double HaulKm(MineUnit u, double dx, double dy, double dz, double staticKm,
                                 UnitPlanInput inp, UnitPlanResult r, out bool fromNetwork)
    {
        fromNetwork = false;
        if (inp.Haul != null)
        {
            double? km = inp.Haul(u.Cx, u.Cy, u.Cz, dx, dy, dz);
            if (km is > 0 && !double.IsInfinity(km.Value) && !double.IsNaN(km.Value))
            { fromNetwork = true; return km.Value; }
        }
        double straight = Dist3(u.Cx, u.Cy, u.Cz, dx, dy, dz);
        if (straight > 1e-9) return straight * Math.Max(1.0, inp.FallbackDetour);
        return Math.Max(0, staticKm);        // 连坐标都没有时才用静态值
    }

    /// <summary>
    /// 把「带优先」的排序键翻成「幅优先」。
    /// <para><c>DumpSlot.Order</c> 的编码是 <c>带×1,000,000 + 幅×100 + 子</c>（见 <see cref="DumpSlotAdapter"/>），
    /// 即默认<b>先填完当前排土带的各幅、再整体往外推一带</b>。这里把带与幅的权重对调，
    /// 得到<b>一幅从里推到外填满再换幅</b>。<b>解码必须与编码同源</b> ——
    /// 两处各写一套位宽，改了编码这里就静默错位，而排出来的顺序看上去仍然合理。</para>
    /// </summary>
    private static double PanelFirstKey(int order)
    {
        int step = order / 1000000;
        int panel = (order % 1000000) / 100;
        int sub = order % 100;
        return panel * 1000000.0 + step * 100.0 + sub;
    }

    private static double Dist3(double ax, double ay, double az, double bx, double by, double bz)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz)) / 1000.0;

    private static string SlotCode(DumpSlot s) => $"{s.DumpName}-L{s.Level}-{s.Order}";

    /// <summary>
    /// 两个作业单元之间的<b>转场代价</b>（U11）。分级而不是按距离，因为现场关心的是
    /// 「要不要把设备整体搬走」，那是台阶式的，不是连续的。
    ///
    /// <para>同带相邻幅 = 0：设备原地往前推，不算转场，这就是「整条带作业」的含义。
    /// 同带但不相邻（跳幅）要给代价，否则算法会在一条带里跳着采，图上看是一条带、
    /// 现场却要来回挪。</para>
    /// </summary>
    private static double MoveCost(MineUnit a, MineUnit b)
    {
        bool sameSeam = string.Equals(a.SeamCode, b.SeamCode, StringComparison.Ordinal);
        if (sameSeam && a.BandId == b.BandId)
            return Math.Abs(a.PanelIndex - b.PanelIndex) <= 1 ? 0.0 : 1.0;   // 相邻幅=原地推进
        if (sameSeam) return 3.0;                                             // 同层换带
        // 跨层：按标高差再分一档 —— 差一个台阶和差半个坑不是一回事
        double dz = Math.Abs(a.Cz - b.Cz);
        return dz <= 20.0 ? 6.0 : 10.0;
    }

    /// <summary>压覆深度：前驱的最大深度 + 1（只在本月选中的子图里算）。深度序保证前驱必在前。</summary>
    private static Dictionary<int, int> Depths(UnitGraph g, HashSet<int> sel)
    {
        var depth = new Dictionary<int, int>();
        foreach (int i in sel) Depth(g, i, sel, depth, 0);
        return depth;
    }

    private static int Depth(UnitGraph g, int i, HashSet<int> sel, Dictionary<int, int> memo, int guard)
    {
        if (memo.TryGetValue(i, out int d)) return d;
        if (guard > 256) return 0;                    // 环的兜底（UnitGraph 已经报过了）
        memo[i] = 0;                                   // 先占位，防环里无限递归
        int best = 0;
        foreach (int p in g.Pred[i])
            if (sel.Contains(p)) best = Math.Max(best, Depth(g, p, sel, memo, guard + 1) + 1);
        memo[i] = best;
        return best;
    }

    private static double Frac(MineUnit u, double m3)
        => u.InSituM3 > 1e-9 ? MineUnit.Clamp01(m3 / u.InSituM3) : 0;

    // ── 输入体检：非正的 ρ/Kr、负库容一律夹回 + 留条（承接 DumpAllocator 的那套）──
    private static GapMaterial[] CheckMaterials(GapMaterial[]? src, UnitPlanResult r)
    {
        if (src == null || src.Length == 0)
        {
            r.Notes.Add("· 没给物料参数，岩按 ρ=2.50 / Kr=1.15 / 去向不限 处理（这是引擎替你定的，核对一下）。");
            return new[] { new GapMaterial { Name = "岩", Code = "rock", Density = 2.5, Kr = 1.15 } };
        }
        var outv = new GapMaterial[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var m = src[i];
            if (m == null) { outv[i] = null!; continue; }
            double kr = m.Kr, rho = m.Density;
            if (!(kr > 0)) { r.Notes.Add($"⚠ {m.Name}：Kr 填的是 {m.Kr:0.###}（须为正），已按 1.15 处理"); kr = 1.15; }
            if (!(rho > 0)) { r.Notes.Add($"⚠ {m.Name}：容重填的是 {m.Density:0.###}（须为正），已按 2.50 处理"); rho = 2.50; }
            outv[i] = kr == m.Kr && rho == m.Density ? m
                : new GapMaterial { Name = m.Name, Code = m.Code, Density = rho, Kr = kr, AllowedDumps = m.AllowedDumps };
        }
        return outv;
    }

    private static List<DumpSlot> CloneSlots(List<DumpSlot>? src, UnitPlanResult r)
    {
        var list = new List<DumpSlot>();
        if (src == null) return list;
        int neg = 0;
        foreach (var s in src)
        {
            if (s == null) continue;
            if (s.CapacityM3 < 0) neg++;
            list.Add(new DumpSlot
            {
                DumpName = s.DumpName, Level = s.Level, Order = s.Order,
                CapacityM3 = Math.Max(0, s.CapacityM3),
                Cx = s.Cx, Cy = s.Cy, Cz = s.Cz,
                IsInternal = s.IsInternal, AvailableFromMonth = s.AvailableFromMonth, HaulKm = s.HaulKm,
            });
        }
        if (neg > 0) r.Notes.Add($"⚠ {neg} 个排土位置的库容是负数，已按 0 处理（那些位置排不进东西）");
        return list;
    }

    private static List<CoalSink> CloneSinks(List<CoalSink>? src)
    {
        var list = new List<CoalSink>();
        if (src == null) return list;
        foreach (var s in src)
            if (s != null)
                list.Add(new CoalSink
                {
                    Name = s.Name, Code = s.Code, Cx = s.Cx, Cy = s.Cy, Cz = s.Cz,
                    CapacityT = s.CapacityT, HaulKm = s.HaulKm,
                });
        return list;
    }
}
