// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/EquipmentAssigner.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

// ══════════════════════════════════════════════════════════════════════════
//  设备指派 —— 把「单元排产」的量换算成「哪台设备、干哪几天、几个台班」
//
//  上游：UnitPlanEngine.Solve() 出的 UnitAssignment[]（单元 → 本月量 → 去向）
//  下游：派车单 / 生产模拟 / 台班计划
//
//  ⚠ 这里出的是【计划】指派，不是实绩。任何一行都不许回写进 equipment_kpi_monthly /
//    capacity_monthly 这类实测台账 —— 一旦混进去，下个月的台效就是拿自己的计划喂自己，
//    而每一项校核仍然是「✓」。
//
//  纯算法、脱 GUI、不碰数据库：设备清单/台效/编组规则全部由调用方以 POCO 喂进来
//  （数据适配在 PlanLib 侧的 EquipmentFleetProvider）。
// ══════════════════════════════════════════════════════════════════════════

/// <summary>设备类别。与 <c>GeoDataBase.Public.EquipmentCategory</c> 逐项对齐，但<b>不引用它</b>
/// —— 引擎侧不吃数据库的类型，映射在 PlanLib 的适配器里做（那里也只做这一处映射）。</summary>
public enum MachineKind
{
    /// <summary>电铲 —— 挖装主力。</summary>
    Shovel = 0,
    /// <summary>矿用卡车 —— 运输，绑在挖装设备上。</summary>
    Truck = 1,
    /// <summary>钻机 —— 穿孔。<b>本引擎不排产</b>，见 <see cref="EquipmentAssignInput"/> 的说明。</summary>
    Drill = 2,
    /// <summary>前装机 —— 也能当挖装主力（小面 / 煤）。</summary>
    Loader = 3,
    Dozer = 4,
    Grader = 5,
    WaterTruck = 6,
    Other = 7,
}

/// <summary>一笔指派在干什么。<b>量守恒只对 <see cref="Excavate"/> 结账</b> ——
/// 把运输笔也加进去就是同一方土算两遍。
///
/// <para><b>四个出量的角色是四个不同的体积口径，绝不许相加</b>：
/// <list type="table">
///   <item><term><see cref="Excavate"/></term><description>原位实方 V实 —— 唯一的「采出量」</description></item>
///   <item><term><see cref="Haul"/></term><description>它承运的那一份 V实（只为出派车单）</description></item>
///   <item><term><see cref="Drill"/></term><description><b>控制方量</b> —— 一次穿爆管住的那一方岩</description></item>
///   <item><term><see cref="Dump"/></term><description><b>排弃占容</b> V实×Kr —— 与排土场库容同口径</description></item>
/// </list>
/// 把排土笔加进采出量的和，是本模块最容易犯、也最难发现的错：总账仍然平（因为它自洽），
/// 只是那个"平"的数不再是任何一个真实的量。</para></summary>
public enum MachineRole
{
    /// <summary>挖装：这一笔的 <see cref="MachineAssignment.AssignedM3"/> 就是采出的实方。</summary>
    Excavate = 0,
    /// <summary>运输：绑在某台挖装设备上，量是它承运的那一份（<b>不计入采出量</b>）。</summary>
    Haul = 1,
    /// <summary>转场：占设备工日但不出量。<b>单独成行</b>，这样「同一天不能在两处」的判据
    /// 能把转场日一起扫进去 —— 藏在字段里的占用，判据扫不到。</summary>
    Relocate = 2,
    /// <summary>
    /// 穿孔：钻机在某单元上打眼，量是<b>控制方量</b>（m³）。
    /// <para>它必须<b>超前</b>于同一单元的挖装 —— 见 <see cref="EquipmentAssignInput.BlastLeadDays"/>。
    /// 煤单元与 <see cref="EquipmentAssignInput.NoBlastUnitIds"/> 里的单元不穿孔。</para>
    /// </summary>
    Drill = 3,
    /// <summary>
    /// 排土：推土机在某个<b>去向</b>（不是单元）上推平当天到的料，量是<b>排弃占容</b>（V实×Kr）。
    /// <para><b>这一笔的 <c>UnitId</c> 装的是去向码</b>，不是单元号 —— 推土机守的是排土场，
    /// 一天推的是当天各单元汇过来的料。按单元拆笔会让「同一天不能在两处」误报。</para>
    /// </summary>
    Dump = 4,
}

/// <summary>欠产是哪个工序的。<b>三个工序的量口径不同，欠产也不能混在一起看</b>。</summary>
public enum ProcessKind
{
    /// <summary>采装（原位实方）。</summary>
    Excavation = 0,
    /// <summary>穿孔（控制方量）。</summary>
    Drilling = 1,
    /// <summary>排土（排弃占容）。</summary>
    Dumping = 2,
}

/// <summary>
/// 一笔台效是<b>哪一级</b>来的。数越小越贴近这台设备的实测。
/// <para><b>为什么必须逐笔记下来</b>：「接了实测台效」和「接了但一条都没命中、全走缺省」
/// 算出来的排班表一模一样，不报没人分得出（本仓库有前科）。</para>
/// </summary>
public enum RateSource
{
    /// <summary>没解出来 —— 这台设备<b>不可派</b>（不是台效为 0）。</summary>
    Unresolved = 0,
    /// <summary>实测·这台·这个月。</summary>
    MachineMonth = 1,
    /// <summary>实测·这台·各月中位数（这个月没记录）。</summary>
    MachineMedian = 2,
    /// <summary>实测·同型号·这个月·各台中位数（这台一条记录都没有）。</summary>
    ModelMonth = 3,
    /// <summary>实测·同型号·全部月份·中位数。</summary>
    ModelMedian = 4,
    /// <summary>型号字典缺省（<c>equipment_model.std_daily_cap_wan_m3</c>）—— <b>不是实测</b>。</summary>
    ModelDefault = 5,
    /// <summary>调用方给的类别兜底 —— <b>不是实测</b>，是拍的。</summary>
    KindDefault = 6,
}

/// <summary>卡车配多少台。</summary>
public enum TruckSizing
{
    /// <summary>按现场编组规则 <c>dispatch_rule.recommended_truck_count</c>（缺省）。</summary>
    ByDispatchRule = 0,
    /// <summary>按台效配平：<c>ceil(铲日台效 ÷ 卡车日台效)</c>。</summary>
    ByRateBalance = 1,
}

// ──────────────────────────────────────────────────────────────────────────
//  输入 POCO
// ──────────────────────────────────────────────────────────────────────────

/// <summary>一台在籍设备。<b>台效不在这里</b> —— 它在 <see cref="RateRecord"/>，一个量一个来源。</summary>
public sealed class Machine
{
    /// <summary>设备编号（<c>equipment.equipment_id</c>）—— 身份。</summary>
    public string MachineId = "";
    public MachineKind Kind;
    /// <summary>型号（<c>equipment.model</c>）—— 台效回退到型号级、编组规则匹配都靠它。</summary>
    public string Model = "";
    /// <summary>本月是否可派（一般 = 状态「在用」）。false 的设备<b>连清单都不进</b>排班。</summary>
    public bool Dispatchable = true;
    /// <summary>当前作业区（<c>equipment.operating_area</c>）—— 只做展示/诊断，<b>不参与择优</b>。</summary>
    public string HomeArea = "";
    /// <summary>
    /// 完好率 %（<c>equipment_kpi_monthly.availability×100</c>）。<b>只带着走，不参与算量</b>。
    /// <para>实测台效（月产量）本身已经含了当月的完好率与实动率，再乘一次就是<b>重复打折</b>。
    /// 留这一列是为了在报表里能看见「这台设备的台效是在多高的完好率下取得的」。</para>
    /// </summary>
    public double AvailabilityPct = -1;
}

/// <summary>
/// 一条<b>台效</b>记录 —— 单位统一为<b>日实方 m³/日</b>。
///
/// <para><b>为什么是日不是月</b>：排班的粒度是天，月量除以作业日这一步必须发生在<b>一个地方</b>。
/// 放在引擎里就要求引擎知道「本月有几个作业日」，那是现场参数（<c>FieldParams</c>）的事；
/// 所以除法放在适配器里做，并把除数写进 <see cref="RecordKey"/>。</para>
///
/// <para>粒度由字段是否为空决定：
/// <list type="bullet">
///   <item><see cref="MachineId"/> 非空 → 台机级</item>
///   <item><see cref="MachineId"/> 空、<see cref="Model"/> 非空 → 型号级</item>
///   <item>两者都空 → 类别兜底</item>
/// </list>
/// <see cref="Month"/> = 0 表示不限月。</para>
/// </summary>
public sealed class RateRecord
{
    public string MachineId = "";
    public string Model = "";
    public MachineKind Kind;
    /// <summary>年 / 月。<c>Month = 0</c> = 不限月（型号缺省、类别兜底都是这一档）。</summary>
    public int Year, Month;
    /// <summary>日台效（原位实方 m³/日）。</summary>
    public double M3PerDay;
    /// <summary>是不是从<b>实测台账</b>派生的。false = 字典缺省 / 拍的兜底。</summary>
    public bool Measured;
    /// <summary>溯源键 —— 写清是哪一条记录、除了多少作业日。例：
    /// <c>capacity_monthly[3008,2025,6]=10327912m³ ÷ 25 作业日</c>。<b>不许留空</b>。</summary>
    public string RecordKey = "";
}

/// <summary>
/// 铲车编组规则（<c>dispatch_rule</c>）。<b>只管配几台、跟谁配</b>，不管台效。
/// </summary>
public sealed class FleetPairing
{
    /// <summary>挖装设备型号（<c>shovel_model</c>）。</summary>
    public string LoaderModel = "";
    /// <summary>卡车型号（<c>truck_model</c>）。</summary>
    public string TruckModel = "";
    /// <summary>推荐卡车数（<c>recommended_truck_count</c>）。</summary>
    public int TruckCount = 3;
    /// <summary>单循环分钟（<c>cycle_time_min</c>）—— 只带着走。</summary>
    public double CycleTimeMin;
    /// <summary>效率评分 1–100（<c>efficiency_score</c>）—— 同型号多种卡车可选时按它挑。</summary>
    public int EfficiencyScore;
}

/// <summary>单元的平面位置 —— <b>只为算转场距离</b>。<see cref="UnitAssignment"/> 不带质心，所以要单独喂。</summary>
public sealed class UnitSite
{
    public string UnitId = "";
    public double Cx, Cy, Cz;
}

/// <summary>
/// 设备指派的输入。
///
/// <para><b>排哪几类，明说</b>：缺省只排<b>挖装（电铲 / 前装机）+ 与之绑定的卡车</b>。
/// 打开 <see cref="ScheduleDrilling"/> / <see cref="ScheduleDozing"/> 之后另排<b>穿孔（钻机）</b>
/// 与<b>排土（推土机）</b>。平路机、洒水车<b>始终不排</b> —— 它们在实测台效表里一条记录都没有，
/// 合成一个出来就是编数。未排的类别台数会在 <see cref="EquipmentAssignResult.Notes"/> 里如实列出。</para>
///
/// <para><b>两个开关都默认关</b>，理由与「工作历轴默认关」同源：打开穿孔之后，挖装的最早开工日
/// 被穿爆超前顶到后面去，全月的量与欠产会整体变一遍。默认开 = 把一次口径变更伪装成算法行为。
/// 关着的时候，本引擎的输出与加这两个工序之前<b>逐字节一致</b>。</para>
/// </summary>
public sealed class EquipmentAssignInput
{
    /// <summary>上游排产结果（<c>UnitPlanEngine.Solve().Assignments</c>）。</summary>
    public List<UnitAssignment> Units = new();

    /// <summary>单元质心 —— 转场距离用。缺了就当「换单元必转场」（保守），并留条。</summary>
    public List<UnitSite> Sites = new();

    /// <summary>在籍设备清单。</summary>
    public List<Machine> Machines = new();

    /// <summary>台效记录（台机级 / 型号级 / 类别兜底混在一起，<see cref="RateBook"/> 自己分层）。</summary>
    public List<RateRecord> Rates = new();

    /// <summary>编组规则。空 = 全部按 <see cref="DefaultTrucksPerLoader"/> 配，任意卡车可用。</summary>
    public List<FleetPairing> Pairings = new();

    /// <summary>先后约束（<c>Before</c> 干完了 <c>After</c> 才能开工）。空 = 不约束，会留条。</summary>
    public List<(string Before, string After)> Precedence = new();

    /// <summary>目标年 / 月 —— 挑台效记录用。</summary>
    public int Year, Month;

    /// <summary>本月<b>作业日</b>数。排班的天数上限就是它（第 1 天 … 第 N 天）。</summary>
    public int WorkdayCount = 25;

    /// <summary>每日班次 —— 台班数 = 工日 × 它。</summary>
    public double ShiftsPerDay = 3;

    /// <summary>一个单元本月最多摆几台挖装设备。</summary>
    public int MaxLoadersPerUnit = 1;

    /// <summary>
    /// 没有编组规则可查时，一台挖装设备配几台卡车。
    /// <para><b>刻意无入口</b>：配车的<b>一级来源是编组规则台账</b>（`dispatch_rule`，库里 40 条），
    /// 这只是查不到时的兜底。把它放上界面 = 让人随手改一个数，就把"兜底"变成了"口径"，
    /// 而报告里仍然写着按编组规则配 —— 要调配车口径请改 <see cref="TruckSizing"/>。</para>
    /// </summary>
    public int DefaultTrucksPerLoader = 3;

    public TruckSizing TruckSizing = TruckSizing.ByDispatchRule;

    /// <summary>转场免费距离（m）。两个单元质心近于它就当没挪窝。</summary>
    public double FreeRelocationM = 300;

    /// <summary>履带/步行机械（电铲、钻机、推土机）转场占几个工日。</summary>
    public int RelocationDaysTracked = 1;

    /// <summary>轮式机械（前装机）转场占几个工日。</summary>
    public int RelocationDaysWheeled = 0;

    /// <summary>
    /// 台效下限（m³/日）。<b>&gt;0 时</b>把低于它的记录夹回到它并标 <c>Clamped</c>；
    /// <b>=0（缺省）时</b>把非正的记录<b>丢掉</b>、回退到下一级来源 —— 不拿一个编出来的正数顶上。
    /// <para><b>刻意无入口</b>：缺省 0 已经是"最诚实"的那一档（宁可少一条记录，也不拿夹出来的台效排班）。
    /// 给它开界面只会诱人填一个下限把坏记录"救活"，而救活的那台设备的班表是编的。
    /// 台效有问题该去修 `capacity_monthly`，不是在这儿夹。</para>
    /// </summary>
    public double MinRateM3PerDay = 0;

    /// <summary>
    /// 台效整体缩放。<b>缺省 1.0 = 照单全收</b>。
    /// <para>留这个口子是因为 <c>capacity_monthly.output_m3</c> 的量级现场需要认一次
    /// （见 <see cref="EquipmentAssignResult.RateScaleAudit"/> 的体检）。
    /// 要改就在这一处改，别在适配器里再乘一遍 —— 两处各乘一次谁也看不出来。</para>
    /// </summary>
    public double CapacityScale = 1.0;

    /// <summary>日产体检的合理带（m³/台·日）：挖装设备的日台效落在带外就报。0 = 不体检。</summary>
    public double AuditLoaderDayLo = 3000, AuditLoaderDayHi = 60000;

    // ══════════════════════════════════════════════════════════════════════
    //  穿孔（钻机）—— 默认关
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 排穿孔。<b>缺省 false</b>：打开之后挖装的最早开工日被穿爆超前顶后，全月的量与欠产会整体变一遍。
    /// </summary>
    public bool ScheduleDrilling = false;

    /// <summary>
    /// 穿完到能采装之间隔几个工日（起爆 + 清场 + 撤警戒）。<b>爆破本身不排设备</b> ——
    /// 它是个窗口不是一台机器（现场的爆破班在 <c>shift_calendar</c> 里），这里只把它抽象成这个间隔。
    /// <para>0 = 穿完当天就能采（不现实，但允许 —— 判据要能证伪，锁死就试不出差别了）。</para>
    /// </summary>
    public int BlastLeadDays = 2;

    /// <summary>
    /// <b>不需要爆破</b>的单元（表土 / 风化软岩 —— 直接铲装）。煤单元一律不穿孔，不必列在这里。
    /// <para>空 = <b>所有岩单元都穿孔</b>。这是个偏保守的缺省，会在 Notes 里明说 ——
    /// 露天煤矿的剥离里本来就有一截表土不用爆破，全算成要穿会高估钻机需求。
    /// 口径来源是物料（<c>MaterialSpec.NeedsBlasting</c>），而 <see cref="UnitAssignment"/> 只带
    /// 煤/岩两分，分不出表土，所以由调用方按物料填这一份。</para>
    /// </summary>
    public HashSet<string> NoBlastUnitIds = new(StringComparer.Ordinal);

    /// <summary>一个单元本月最多摆几台钻机。</summary>
    public int MaxDrillsPerUnit = 1;

    /// <summary>钻机日台效体检的合理带（m³控制方量/台·日）。0 = 不体检。</summary>
    public double AuditDrillDayLo = 2000, AuditDrillDayHi = 80000;

    // ══════════════════════════════════════════════════════════════════════
    //  排土（推土机）—— 默认关
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 排推土机。<b>缺省 false</b>。打开后按<b>去向</b>（不是单元）逐日配推土机，
    /// 量走<b>排弃占容</b>口径（<c>UnitFlow.DumpM3</c> = V实×Kr），<b>绝不进采出量的账</b>。
    /// </summary>
    public bool ScheduleDozing = false;

    /// <summary>一个排土位置同一天最多摆几台推土机。</summary>
    public int MaxDozersPerSink = 2;

    /// <summary>
    /// 推土机日台效体检的合理带（m³占容/台·日）。0 = 不体检。
    /// <para>ProcessQuota 里现场给的是 250 m³/h ⇒ 三班约 4500 m³/日，带取得比它宽。</para>
    /// </summary>
    public double AuditDozerDayLo = 1000, AuditDozerDayHi = 40000;

    // ══════════════════════════════════════════════════════════════════════
    //  面级设备型号约束（「确定开采程序」钉的那四列）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单元 → 允许用哪些型号。<b>空 = 不约束</b>（全矿在册设备里挑），这是缺省。
    ///
    /// <para><b>为什么键是单元不是作业面</b>：引擎排的是单元，它不知道"作业面"这回事，
    /// 也<b>不该</b>知道 —— 让它去猜单元属于哪个面（按标高？按带号？），
    /// 猜出来的归属每一项校核都会是 ✓，而它对应不上现场任何一个面。
    /// 归属是调用方的事：把面上钉的型号<b>按它自己知道的对应关系</b>摊到单元上再喂进来。</para>
    /// </summary>
    public Dictionary<string, UnitFacePin> Pins = new(StringComparer.Ordinal);
}

/// <summary>
/// 一个单元从它所属<b>作业面</b>继承来的约束 —— 设备型号 + 工艺参数，来自「确定开采程序」。
///
/// <para><b>为什么设备和工艺装在同一个对象里</b>：两者来自同一个面、走同一份归属。
/// 拆成两个对象就会出现"型号按 A 面摊、超前期按 B 面摊"——两边各自都对，合起来是错的，
/// 而且不会有任何东西报错。同一份归属只该产出一份约束。</para>
///
/// <para>每一项留空/为 0 = 该项不约束。<b>钉了却一台都挑不到，是欠产，不是静默放行</b> ——
/// 悄悄退回「全矿挑」的话，界面上钉着的那个型号就成了摆设，而报表还显示它生效了。</para>
/// </summary>
public sealed class UnitFacePin
{
    /// <summary>这条约束是哪个作业面来的 —— 只为把欠产原因写清楚（「XX面钉了YY型号」）。</summary>
    public string FaceName = "";

    // ── 设备维 ──
    public string DrillModel = "";
    public string LoaderModel = "";
    public string TruckModel = "";
    public string DozerModel = "";
    /// <summary>面级覆盖的配车数。0 = 按现场编组规则。</summary>
    public int TrucksPerLoader;

    // ── 工艺维 ──
    /// <summary>
    /// 面级<b>穿爆超前期</b>（工日）。0 = 用全局 <see cref="EquipmentAssignInput.BlastLeadDays"/>。
    /// <para>硬岩大区爆破和煤层控制爆破的超前期本来就不一样，一个全局常数表达不了。</para>
    /// </summary>
    public int BlastLeadDays;

    public bool Any => DrillModel.Length > 0 || LoaderModel.Length > 0
                    || TruckModel.Length > 0 || DozerModel.Length > 0
                    || TrucksPerLoader > 0 || BlastLeadDays > 0;
}

// ──────────────────────────────────────────────────────────────────────────
//  输出 POCO
// ──────────────────────────────────────────────────────────────────────────

/// <summary>一笔指派 = 一台设备在一个单元上连续干的一段。<b>起止天是闭区间，且区间内每天都真占着</b>
/// —— 中间有空档就拆成两笔，这样「同一天不能在两处」的判据可以直接扫区间。</summary>
public sealed class MachineAssignment
{
    /// <summary>
    /// 作业对象。<b>角色 <see cref="MachineRole.Dump"/> 时这里装的是<u>去向码</u></b>（排土位置），
    /// 其余角色装的是采掘单元号 —— 推土机守的是排土场，不是某一个单元。
    /// <para>合成一个字段是<b>刻意的</b>：「同一天不能在两处」这条判据扫的就是它，
    /// 把排土的作业对象另放一个字段，那条判据就扫不到推土机的占用了。</para>
    /// </summary>
    public string UnitId = "";
    public UnitKind UnitKind;
    /// <summary>上游给的月内推进序（<see cref="UnitAssignment.Seq"/>），原样带出。</summary>
    public int Seq;

    public string MachineId = "";
    public MachineKind MachineKind;
    public string Model = "";
    public MachineRole Role;

    /// <summary>运输笔绑在哪台挖装设备上（<see cref="MachineRole.Haul"/> 时非空）。</summary>
    public string ServesMachineId = "";

    /// <summary>起 / 止工日（1 起，闭区间）。</summary>
    public int StartDay, EndDay;
    /// <summary>占几个工日。</summary>
    public int Days => EndDay - StartDay + 1;
    /// <summary>台班数 = 工日 × 每日班次。</summary>
    public double Shifts;

    /// <summary>本笔承担的原位实方（m³）。运输笔 = 它承运的那一份，<b>不计入采出量</b>。</summary>
    public double AssignedM3;

    /// <summary>用的日台效（m³/日）。转场笔为 0。</summary>
    public double RateM3PerDay;
    /// <summary>这条台效是哪一级来的。</summary>
    public RateSource RateSource;
    /// <summary>台效的溯源键 —— 具体到哪一条记录。</summary>
    public string RateRecord = "";
    /// <summary>台效被下限夹过。</summary>
    public bool RateClamped;

    /// <summary>
    /// 本段没用掉的铲能力：<c>Days × 台效 − 本笔量</c>（不结转）。
    /// <para>来源有两处：末日没干满、以及被卡车运力压住的那些天。</para>
    /// <para><b>故意不夹到 0</b>：夹了之后「派的量超过了台效」这条判据就永远不会红，
    /// 而那正是它唯一要抓的东西 —— 只判成功的判据会空过。</para>
    /// </summary>
    public double SpareCapM3;

    /// <summary>本笔是不是被<b>运力</b>卡住的（卡车不够 / 卡车台效低于铲）。</summary>
    public bool HaulLimited;

    /// <summary>是实测台效吗。</summary>
    public bool IsMeasuredRate => RateBook.IsMeasured(RateSource);

    public string Caption =>
        $"{UnitId} ← {MachineId}({Model}) 第{StartDay}~{EndDay}日 {Days}工日/{Shifts:0.#}台班 "
        + $"{AssignedM3 / 1e4:0.00}万m³ 台效{RateM3PerDay:0}m³/日[{RateSource}]";
}

/// <summary>一个单元（排土是一个去向）没派够的量。<b>如实报，不摊平</b>。</summary>
public sealed class UnitShortfall
{
    public string UnitId = "";
    public UnitKind UnitKind;
    /// <summary>这是哪个工序欠的 —— <b>三个工序量口径不同，欠产不能混着加</b>。</summary>
    public ProcessKind Process = ProcessKind.Excavation;
    public double DemandM3, AssignedM3;
    public double ShortM3 => Math.Max(0, DemandM3 - AssignedM3);
    /// <summary>为什么没派够 —— 这一列<b>不许空</b>，"不可行却说不出原因"本身就是个缺陷。</summary>
    public string Reason = "";

    /// <summary>本笔量的口径文案 —— 报表里三种欠产并排显示时必须能分开读。</summary>
    public string BasisText => Process switch
    {
        ProcessKind.Drilling => "控制方量",
        ProcessKind.Dumping => "排弃占容",
        _ => "原位实方",
    };
}

public sealed class EquipmentAssignResult
{
    public bool Success;
    public string Error = "";

    public readonly List<MachineAssignment> Assignments = new();
    public readonly List<UnitShortfall> Shortfalls = new();
    /// <summary>引擎替用户做的每一个决定。<b>一条都不许静默</b>。</summary>
    public readonly List<string> Notes = new();

    public int WorkdayCount;
    public double ShiftsPerDay;

    /// <summary>采装的需求 / 已派 / 欠产（<b>原位实方</b> m³）。穿孔与排土<b>不进这本账</b>。</summary>
    public double DemandM3, AssignedM3;
    public double ShortM3 => Math.Max(0, DemandM3 - AssignedM3);

    /// <summary>穿孔的需求 / 已派（<b>控制方量</b> m³）。<b>与采出量不是一个口径，别相加</b>。</summary>
    public double DrillDemandM3, DrilledM3;
    public double DrillShortM3 => Math.Max(0, DrillDemandM3 - DrilledM3);

    /// <summary>排土的需求 / 已派（<b>排弃占容</b> m³ = V实×Kr）。<b>与采出量不是一个口径，别相加</b>。</summary>
    public double DozeDemandM3, DozedM3;
    public double DozeShortM3 => Math.Max(0, DozeDemandM3 - DozedM3);

    public IEnumerable<MachineAssignment> Excavation => Assignments.Where(a => a.Role == MachineRole.Excavate);
    public IEnumerable<MachineAssignment> Haulage => Assignments.Where(a => a.Role == MachineRole.Haul);
    public IEnumerable<MachineAssignment> Relocations => Assignments.Where(a => a.Role == MachineRole.Relocate);
    public IEnumerable<MachineAssignment> Drilling => Assignments.Where(a => a.Role == MachineRole.Drill);
    public IEnumerable<MachineAssignment> Dumping => Assignments.Where(a => a.Role == MachineRole.Dump);

    /// <summary>穿孔 / 排土这一轮排没排（对应两个开关）。<b>没排 ≠ 没需求</b>，报表要能分清。</summary>
    public bool DrillingScheduled, DozingScheduled;

    /// <summary>被穿爆超前推迟了开工的单元数 —— 这是打开穿孔之后采装量变化的<b>唯一来源</b>。</summary>
    public int UnitsDelayedByBlastLead;
    /// <summary>因为穿孔没排完、超前期压根排不下而<b>整个单元采不了</b>的个数。</summary>
    public int UnitsBlockedByDrilling;

    /// <summary>用到的钻机 / 推土机台数。</summary>
    public int DrillsUsed => Drilling.Select(a => a.MachineId).Distinct().Count();
    public int DozersUsed => Dumping.Select(a => a.MachineId).Distinct().Count();

    public IEnumerable<UnitShortfall> ExcavationShortfalls => Shortfalls.Where(s => s.Process == ProcessKind.Excavation);
    public IEnumerable<UnitShortfall> DrillShortfalls => Shortfalls.Where(s => s.Process == ProcessKind.Drilling);
    public IEnumerable<UnitShortfall> DozeShortfalls => Shortfalls.Where(s => s.Process == ProcessKind.Dumping);

    /// <summary>台效来源分布（挖装笔）—— 实测占比低就是「接了但没命中」。</summary>
    public readonly Dictionary<RateSource, int> RateSourceHist = new();
    /// <summary>穿孔 / 排土的台效来源分布 —— <b>逐角色分开记</b>：
    /// 混进挖装那一大堆实测笔里，「钻机台效全走缺省」就再也看不出来了。</summary>
    public readonly Dictionary<RateSource, int> DrillRateHist = new();
    public readonly Dictionary<RateSource, int> DozeRateHist = new();
    public int MeasuredRateHits => RateSourceHist.Where(kv => RateBook.IsMeasured(kv.Key)).Sum(kv => kv.Value);
    public int RateTotal => RateSourceHist.Sum(kv => kv.Value);

    /// <summary>台效量级体检的结论（空 = 没开体检或全在带内）。</summary>
    public string RateScaleAudit = "";

    /// <summary>被运力卡住的挖装工日数。</summary>
    public int HaulLimitedDays;
    /// <summary>因为一台空闲卡车都没有而<b>整天开不了工</b>的挖装工日数。</summary>
    public int HaulBlockedDays;

    /// <summary>按<b>单一车型</b>成组的挖装工日 / 不得不<b>混编车型</b>的挖装工日。
    /// <para>混编不是"正常编组"：<c>recommended_truck_count</c> 是对某一对 (铲型, 车型) 说的，
    /// 混编那天用的是一条谁也没写过的规则，所以单独计数。</para></summary>
    public int SingleModelCrewDays, MixedModelCrewDays;

    /// <summary>当天空闲车少于编组规则要求的台数、<b>没配够</b>就开工的挖装工日。</summary>
    public int UnderCrewedDays;

    /// <summary>转场占掉的工日合计。</summary>
    public int RelocationDays => Relocations.Sum(a => a.Days);

    /// <summary>用到的挖装 / 卡车台数。</summary>
    public int LoadersUsed => Excavation.Select(a => a.MachineId).Distinct().Count();
    public int TrucksUsed => Haulage.Select(a => a.MachineId).Distinct().Count();

    /// <summary>全部量都派出去了（三个工序都算 —— 穿孔欠了照样是这个月干不完）。
    /// <b>两个开关关着时 <c>DrillShortM3</c>/<c>DozeShortM3</c> 恒为 0，此式与加工序之前等价。</b></summary>
    public bool Feasible => Success && ShortM3 <= 1e-6 && DrillShortM3 <= 1e-6 && DozeShortM3 <= 1e-6;

    // ══════════════════════════════════════════════════════════════════
    //  判据 —— 五类自洽。返回空 = 自洽。
    //  ⚠ 每一条都是从【已经吐出去的 Assignments】重建出来再判的，
    //    不看引擎内部那份日历 —— 判据照着实现的内部状态判，等于没判。
    // ══════════════════════════════════════════════════════════════════
    public List<string> Validate(double tolPct = 1e-6)
    {
        var bad = new List<string>();
        if (!Success) { bad.Add("指派未成功：" + Error); return bad; }

        // ① 一台设备同一天不能在两处 —— 从区间重建逐日占用
        var busy = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        foreach (var a in Assignments)
        {
            if (a.MachineId.Length == 0) { bad.Add("有一笔指派没有设备编号"); break; }
            if (!busy.TryGetValue(a.MachineId, out var cal))
                busy[a.MachineId] = cal = new Dictionary<int, string>();
            string tag = (a.Role == MachineRole.Relocate ? "↷" : "") + a.UnitId;
            for (int d = a.StartDay; d <= a.EndDay; d++)
            {
                if (cal.TryGetValue(d, out string? had) && !string.Equals(had, tag, StringComparison.Ordinal))
                { bad.Add($"◆ 设备冲突：{a.MachineId} 第 {d} 日同时在「{had}」和「{tag}」"); goto doneConflict; }
                cal[d] = tag;
            }
        }
        doneConflict:

        // ② 量守恒：三本账【各自】平，绝不相加。
        //    ★ 这里最容易犯的错是把 Shortfalls 整个求和去对采装需求 —— 一旦打开穿孔/排土，
        //      那个和里混进了控制方量与排弃占容两个别的口径，判据当场变红且原因看不懂；
        //      更糟的写法是同时把 Drilling/Dumping 的量加进 exc，那样它<b>不红</b>，而账已经错了。
        double exc = Excavation.Sum(a => a.AssignedM3);
        if (Rel(exc, AssignedM3) > 1e-6)
            bad.Add($"汇总对不上：逐笔挖装 {exc:0.##}m³ vs 汇总 {AssignedM3:0.##}m³");
        double shortSum = ExcavationShortfalls.Sum(s => s.ShortM3);
        if (Rel(exc + shortSum, DemandM3) > Math.Max(tolPct, 1e-6))
            bad.Add($"◆ 量不守恒（采装·原位实方）：已派 {exc:0.##} + 欠产 {shortSum:0.##} = {exc + shortSum:0.##} ≠ 需求 {DemandM3:0.##}（m³）");

        double dr = Drilling.Sum(a => a.AssignedM3), drShort = DrillShortfalls.Sum(s => s.ShortM3);
        if (Rel(dr, DrilledM3) > 1e-6)
            bad.Add($"汇总对不上（穿孔）：逐笔 {dr:0.##}m³ vs 汇总 {DrilledM3:0.##}m³");
        if (Rel(dr + drShort, DrillDemandM3) > Math.Max(tolPct, 1e-6))
            bad.Add($"◆ 量不守恒（穿孔·控制方量）：已派 {dr:0.##} + 欠产 {drShort:0.##} ≠ 需求 {DrillDemandM3:0.##}（m³）");

        double dz = Dumping.Sum(a => a.AssignedM3), dzShort = DozeShortfalls.Sum(s => s.ShortM3);
        if (Rel(dz, DozedM3) > 1e-6)
            bad.Add($"汇总对不上（排土）：逐笔 {dz:0.##}m³ vs 汇总 {DozedM3:0.##}m³");
        if (Rel(dz + dzShort, DozeDemandM3) > Math.Max(tolPct, 1e-6))
            bad.Add($"◆ 量不守恒（排土·排弃占容）：已派 {dz:0.##} + 欠产 {dzShort:0.##} ≠ 需求 {DozeDemandM3:0.##}（m³）");

        // ★「把排土/穿孔的量混进采出量」这个错不必单开一条：上面第一句
        //   Rel(exc, AssignedM3) 就抓得到（exc 只数挖装笔）。
        //   单开一条反而危险 —— 写出来的多半是个恒不成立的式子，看着像多了一道判据，其实从不触发。

        // ③ 逐单元：运输承运量 = 挖装量（每一天的量都摊给了当天的卡车）
        var excByUnit = Excavation.GroupBy(a => a.UnitId).ToDictionary(g => g.Key, g => g.Sum(a => a.AssignedM3), StringComparer.Ordinal);
        var haulByUnit = Haulage.GroupBy(a => a.UnitId).ToDictionary(g => g.Key, g => g.Sum(a => a.AssignedM3), StringComparer.Ordinal);
        foreach (var kv in excByUnit)
        {
            double h = haulByUnit.TryGetValue(kv.Key, out double v) ? v : 0;
            if (Rel(h, kv.Value) > 1e-6)
            { bad.Add($"◆ {kv.Key} 承运量 {h:0.##}m³ ≠ 挖装量 {kv.Value:0.##}m³"); break; }
        }

        // ④ 范围与引用
        foreach (var a in Assignments)
        {
            if (a.StartDay < 1 || a.EndDay < a.StartDay || a.EndDay > WorkdayCount)
            { bad.Add($"◆ {a.MachineId}@{a.UnitId} 起止天 {a.StartDay}~{a.EndDay} 越界（本月 {WorkdayCount} 工日）"); break; }
            if (a.Role != MachineRole.Relocate && !(a.RateM3PerDay > 0))
            { bad.Add($"◆ {a.MachineId}@{a.UnitId} 台效是 {a.RateM3PerDay:0.###}（须为正）"); break; }
            if (a.Role != MachineRole.Relocate && a.RateRecord.Length == 0)
            { bad.Add($"◆ {a.MachineId}@{a.UnitId} 没有台效溯源键 —— 追不到用的是哪一条记录"); break; }
            if (a.Role != MachineRole.Relocate && a.RateSource == RateSource.Unresolved)
            { bad.Add($"◆ {a.MachineId}@{a.UnitId} 台效来源是 Unresolved 却排了班"); break; }
            if (a.AssignedM3 < -1e-9 || double.IsNaN(a.AssignedM3) || double.IsInfinity(a.AssignedM3))
            { bad.Add($"◆ {a.MachineId}@{a.UnitId} 量是 {a.AssignedM3}"); break; }
            // 余能对【三个出量的角色】都要判 —— 只判挖装的话，钻机/推土机派超台效不会红。
            if (a.Role is MachineRole.Excavate or MachineRole.Drill or MachineRole.Dump && a.SpareCapM3 < -1e-6)
            { bad.Add($"◆ {a.MachineId}@{a.UnitId}（{RoleName(a.Role)}）派的量超过了 {a.Days} 个工日的台效（余能 {a.SpareCapM3:0.##}）"); break; }
            if (a.Role == MachineRole.Haul && a.ServesMachineId.Length == 0)
            { bad.Add($"◆ 运输笔 {a.MachineId}@{a.UnitId} 没说绑在哪台挖装设备上"); break; }
        }
        var loaderIds = Excavation.Select(a => a.MachineId).ToHashSet(StringComparer.Ordinal);
        foreach (var a in Haulage)
            if (!loaderIds.Contains(a.ServesMachineId))
            { bad.Add($"◆ 运输笔 {a.MachineId} 绑的 {a.ServesMachineId} 不在挖装清单里"); break; }

        // ⑤ 完备：欠产必须有原因；不可行必须有欠产
        foreach (var s in Shortfalls)
            if (s.ShortM3 > 1e-6 && s.Reason.Length == 0)
            { bad.Add($"◆ {s.UnitId} 欠产 {s.ShortM3:0.##}m³（{s.BasisText}）却说不出原因"); break; }
        // ★ 这里必须用【三个工序合计】：上面的 shortSum 只数了采装，
        //   拿它去判"不可行却没有欠产"，穿孔单独不可行时会误报。
        double allShort = Shortfalls.Sum(s => s.ShortM3);
        if (!Feasible && allShort <= 1e-6)
            bad.Add("判为不可行却一条欠产都没有 —— 说不出为什么不可行");
        if (Shortfalls.Any(s => s.DemandM3 < -1e-9 || s.AssignedM3 < -1e-9))
            bad.Add("欠产表里有负量");

        // ⑥ 穿爆超前【真的挡住了】—— 每个单元的挖装必须晚于它的穿孔 + 间隔期。
        //   没有这一条，"接了穿爆超前"和"接了但一天都没推迟"排出来的班表分不出来
        //   （本模块的先后约束已经栽过一次：约束指向一个已经排过的单元，等于没加）。
        if (DrillingScheduled)
        {
            var drillEnd = Drilling.GroupBy(a => a.UnitId, StringComparer.Ordinal)
                                   .ToDictionary(g => g.Key, g => g.Max(a => a.EndDay), StringComparer.Ordinal);
            foreach (var g in Excavation.GroupBy(a => a.UnitId, StringComparer.Ordinal))
            {
                if (!drillEnd.TryGetValue(g.Key, out int de)) continue;   // 免爆单元，没有穿孔笔
                int excStart = g.Min(a => a.StartDay);
                // ★ 照【这个单元实际用的】超前期判，不是照全局值 ——
                //   面上覆盖过更短超前期的单元，用全局值去判会误报成"没挡住"。
                int uLead = LeadDaysByUnit.TryGetValue(g.Key, out int lv) ? lv : BlastLeadDaysUsed;
                if (excStart < de + uLead + 1)
                {
                    bad.Add($"◆ 穿爆超前没挡住：{g.Key} 第 {de} 日穿完，间隔 {uLead} 工日后"
                          + $"最早第 {de + uLead + 1} 日才能采，实际第 {excStart} 日就开挖了");
                    break;
                }
            }
            // 需要穿爆却一笔穿孔都没有、却照样开挖了 —— 比上一条更隐蔽：它长得像"这个单元不用爆破"。
            foreach (var g in Excavation.GroupBy(a => a.UnitId, StringComparer.Ordinal))
                if (g.First().UnitKind == UnitKind.Rock && !drillEnd.ContainsKey(g.Key)
                    && !NoBlastUnitsUsed.Contains(g.Key)
                    && DrillShortfalls.All(s => !string.Equals(s.UnitId, g.Key, StringComparison.Ordinal)))
                { bad.Add($"◆ {g.Key} 是需爆破的岩单元，既没有穿孔笔也没有穿孔欠产，却排了挖装"); break; }
        }

        return bad;
    }

    /// <summary>本次的<b>全局</b>穿爆间隔（工日）。面上覆盖过的走 <see cref="LeadDaysByUnit"/>。</summary>
    public int BlastLeadDaysUsed;

    /// <summary>逐单元<b>实际用了</b>哪个超前期。判据要照它判 —— 照全局值判的话，
    /// 面级覆盖过的单元会被误判成"超前没挡住"。</summary>
    public Dictionary<string, int> LeadDaysByUnit = new(StringComparer.Ordinal);

    /// <summary>有几个单元的超前期是<b>面级覆盖</b>来的（不是全局值）。</summary>
    public int LeadOverriddenUnits;
    /// <summary>本次实际认定的免爆单元 —— 判据⑥要用，且报表要能说清"哪些面按不爆破算的"。</summary>
    public HashSet<string> NoBlastUnitsUsed = new(StringComparer.Ordinal);

    public static string RoleName(MachineRole r) => r switch
    {
        MachineRole.Excavate => "挖装",
        MachineRole.Haul => "运输",
        MachineRole.Relocate => "转场",
        MachineRole.Drill => "穿孔",
        MachineRole.Dump => "排土",
        _ => "其他",
    };

    internal static string RoleNameOf(ProcessKind p) => p switch
    {
        ProcessKind.Drilling => "穿孔",
        ProcessKind.Dumping => "排土",
        _ => "采装",
    };

    private static double Rel(double a, double b)
    {
        double m = Math.Max(Math.Abs(a), Math.Abs(b));
        return m < 1e-9 ? 0 : Math.Abs(a - b) / m;
    }

    /// <summary>一屏摘要（命令行/日志直接打，别各处再编一遍）。</summary>
    public string Report()
    {
        if (!Success) return "设备指派失败：" + Error;
        var sb = new StringBuilder();
        sb.AppendLine($"采装（原位实方）需求 {DemandM3 / 1e4:0.0}万m³ · 已派 {AssignedM3 / 1e4:0.0}万m³"
                    + (ShortM3 > 1e-6 ? $" · ◆ 欠产 {ShortM3 / 1e4:0.0}万m³（{ExcavationShortfalls.Count(s => s.ShortM3 > 1e-6)} 个单元）" : " · 全部派出"));
        sb.AppendLine($"挖装 {LoadersUsed} 台 / 卡车 {TrucksUsed} 台 · "
                    + $"挖装工日 {Excavation.Sum(a => a.Days)} · 台班 {Excavation.Sum(a => a.Shifts):0.#} · "
                    + $"转场 {RelocationDays} 工日");

        // 三个工序三本账，逐本报 —— 单位不同，绝不并成一行"总量"。
        sb.AppendLine(DrillingScheduled
            ? $"穿孔（控制方量）需求 {DrillDemandM3 / 1e4:0.0}万m³ · 已派 {DrilledM3 / 1e4:0.0}万m³"
              + (DrillShortM3 > 1e-6 ? $" · ◆ 欠 {DrillShortM3 / 1e4:0.0}万m³" : "")
              + $" · 钻机 {DrillsUsed} 台 · 穿爆超前 {BlastLeadDaysUsed} 工日"
              + (UnitsDelayedByBlastLead > 0 ? $" · {UnitsDelayedByBlastLead} 个单元因此推迟开工" : "")
              + (UnitsBlockedByDrilling > 0 ? $" · ◆ {UnitsBlockedByDrilling} 个单元穿不完压根开不了挖" : "")
            : "穿孔：本次<b>没排</b>（ScheduleDrilling 关着）—— 不代表不需要穿爆，只代表这份班表里没有它。");
        sb.AppendLine(DozingScheduled
            ? $"排土（排弃占容）需求 {DozeDemandM3 / 1e4:0.0}万m³ · 已派 {DozedM3 / 1e4:0.0}万m³"
              + (DozeShortM3 > 1e-6 ? $" · ◆ 欠 {DozeShortM3 / 1e4:0.0}万m³（料到了推不平）" : "")
              + $" · 推土机 {DozersUsed} 台"
            : "排土：本次<b>没排</b>（ScheduleDozing 关着）。");
        int measured = MeasuredRateHits, tot = RateTotal;
        sb.AppendLine($"台效来源：实测 {measured}/{tot} 笔"
                    + (tot > measured ? $"，缺省 {tot - measured} 笔（" +
                        string.Join("，", RateSourceHist.Where(kv => !RateBook.IsMeasured(kv.Key) && kv.Key != RateSource.Unresolved)
                                                       .Select(kv => $"{RateBook.Label(kv.Key)}×{kv.Value}")) + "）"
                      : ""));
        if (HaulLimitedDays > 0 || HaulBlockedDays > 0)
            sb.AppendLine($"运力：{HaulLimitedDays} 个挖装工日被卡车拖住，{HaulBlockedDays} 个工日因为一台空闲卡车都没有而开不了工");
        if (RateScaleAudit.Length > 0) sb.AppendLine("◆ 量级体检：" + RateScaleAudit);
        foreach (var nt in Notes) sb.AppendLine("  " + nt);
        // 欠产按工序分组列 —— 混在一起排序会让"欠 30 万控制方量"和"欠 30 万实方"看起来一样严重。
        foreach (var grp in Shortfalls.Where(s => s.ShortM3 > 1e-6).GroupBy(s => s.Process).OrderBy(g => (int)g.Key))
            foreach (var s in grp.OrderByDescending(s => s.ShortM3).Take(8))
                sb.AppendLine($"  ◆ [{RoleNameOf(grp.Key)}] {s.UnitId} 欠 {s.ShortM3 / 1e4:0.00}万m³（{s.BasisText}）—— {s.Reason}");
        return sb.ToString();
    }

    /// <summary>逐设备的工日占用（含转场）—— 派车单/甘特图直接吃。</summary>
    public Dictionary<string, int> BusyDaysByMachine()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in Assignments)
            d[a.MachineId] = (d.TryGetValue(a.MachineId, out int v) ? v : 0) + a.Days;
        return d;
    }
}

// ──────────────────────────────────────────────────────────────────────────
//  台效账本
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// 台效的<b>唯一查询口</b>。分层回退 + 逐次记下用了哪一条。
///
/// <para><b>回退链</b>（越靠前越贴近这台设备）：
/// ① 这台·本月实测 → ② 这台·各月实测<b>中位数</b> → ③ 同型号·本月各台中位数 →
/// ④ 同型号·全部月份中位数 → ⑤ 型号字典缺省 → ⑥ 类别兜底 → 解不出来 = <b>不可派</b>。</para>
///
/// <para><b>取中位数不取均值</b>：设备台账里总有几个月是大修 / 刚投产 / 记错的极端值，
/// 均值会被它们拽走，而拽走之后排出来的班表看上去仍然合理。</para>
///
/// <para><b>非正台效不许变成 0 也不许编一个数</b>：<c>MinRate = 0</c> 时把那条记录丢掉、
/// 回退到下一级（这就是"夹回"—— 夹回到下一级来源）；<c>MinRate &gt; 0</c> 时才夹到下限并标记。
/// 一直退到底还是没有，就判这台设备<b>不可派</b> —— 不是台效为 0。</para>
/// </summary>
public sealed class RateBook
{
    private readonly Dictionary<string, List<RateRecord>> _byMachine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RateRecord>> _byModel = new(StringComparer.Ordinal);
    private readonly Dictionary<MachineKind, RateRecord> _byKind = new();
    private readonly Dictionary<string, RateRecord> _modelDefault = new(StringComparer.Ordinal);

    /// <summary>丢掉的非正记录 / 夹回的记录条数。</summary>
    public int DroppedNonPositive { get; private set; }
    public int ClampedUp { get; private set; }
    /// <summary>台效整体缩放（原样带出，报表要显示）。</summary>
    public double Scale { get; private set; } = 1.0;

    private double _minRate;

    public static bool IsMeasured(RateSource s)
        => s is RateSource.MachineMonth or RateSource.MachineMedian
              or RateSource.ModelMonth or RateSource.ModelMedian;

    public static string Label(RateSource s) => s switch
    {
        RateSource.MachineMonth => "实测·这台·本月",
        RateSource.MachineMedian => "实测·这台·各月中位",
        RateSource.ModelMonth => "实测·同型号·本月中位",
        RateSource.ModelMedian => "实测·同型号·各月中位",
        RateSource.ModelDefault => "型号字典缺省",
        RateSource.KindDefault => "类别兜底(拍的)",
        _ => "未解出",
    };

    /// <summary>建账本。<paramref name="notes"/> 收所有"引擎替你做的决定"。</summary>
    public static RateBook Build(IEnumerable<RateRecord>? src, double minRate, double scale, List<string> notes)
    {
        var bk = new RateBook { _minRate = Math.Max(0, minRate) };
        bk.Scale = scale > 0 ? scale : 1.0;
        if (!(scale > 0))
            notes.Add($"⚠ 台效缩放填的是 {scale:0.###}（须为正），已按 1.0 处理。");
        if (Math.Abs(bk.Scale - 1.0) > 1e-9)
            notes.Add($"⚠ 全部台效已乘以缩放系数 {bk.Scale:0.####} —— 这是调用方指定的口径修正，不是实测值本身。");

        if (src == null) return bk;
        foreach (var r0 in src)
        {
            if (r0 == null) continue;
            double v = r0.M3PerDay * bk.Scale;
            bool clamped = false;
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
            {
                if (bk._minRate > 0) { v = bk._minRate; clamped = true; bk.ClampedUp++; }
                else { bk.DroppedNonPositive++; continue; }
            }
            else if (v < bk._minRate) { v = bk._minRate; clamped = true; bk.ClampedUp++; }

            var r = new RateRecord
            {
                MachineId = r0.MachineId ?? "", Model = r0.Model ?? "", Kind = r0.Kind,
                Year = r0.Year, Month = r0.Month, M3PerDay = v, Measured = r0.Measured,
                RecordKey = (r0.RecordKey ?? "") + (clamped ? $"｜已夹回下限 {bk._minRate:0}m³/日" : "")
                          + (Math.Abs(bk.Scale - 1.0) > 1e-9 ? $"｜×{bk.Scale:0.####}" : ""),
            };
            if (r.RecordKey.Length == 0) r.RecordKey = "（调用方没给溯源键）";

            if (r.MachineId.Length > 0)
            {
                Add(bk._byMachine, r.MachineId, r);
                // ★ 同一条实测记录<b>同时</b>进型号索引：③/④ 两级问的是「同型号别的台干到多少」，
                //   而实测记录逐条都带着 MachineId。只进 _byMachine 的话，这两级就永远查不到东西 ——
                //   一条从不命中的回退级，和没有这一级，算出来的班表一模一样（台架抓到过一次）。
                if (r.Model.Length > 0 && r.Measured) Add(bk._byModel, r.Model, r);
            }
            else if (r.Model.Length > 0)
            {
                if (r.Measured) Add(bk._byModel, r.Model, r);
                else bk._modelDefault[r.Model] = r;          // 字典缺省每型号只留一条
            }
            else bk._byKind[r.Kind] = r;
        }

        if (bk.DroppedNonPositive > 0)
            notes.Add($"⚠ {bk.DroppedNonPositive} 条台效记录是 0 / 负 / 非数，已<b>丢弃并回退到下一级来源</b>"
                    + "（没设台效下限 —— 设了才会夹到下限而不是丢）。丢弃不等于按 0 算。");
        if (bk.ClampedUp > 0)
            notes.Add($"⚠ {bk.ClampedUp} 条台效低于下限 {minRate:0}m³/日，已夹回下限并在溯源键里标出。");
        return bk;
    }

    private static void Add(Dictionary<string, List<RateRecord>> d, string k, RateRecord r)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<RateRecord>();
        l.Add(r);
    }

    /// <summary>解一台设备在某年月的日台效。</summary>
    public RateHit Resolve(Machine m, int year, int month)
    {
        if (m == null) return default;

        // ① / ② 台机级
        if (m.MachineId.Length > 0 && _byMachine.TryGetValue(m.MachineId, out var mine) && mine.Count > 0)
        {
            var exact = mine.FirstOrDefault(r => r.Year == year && r.Month == month)
                     ?? mine.FirstOrDefault(r => r.Month == 0);
            if (exact != null) return new RateHit(exact.M3PerDay, RateSource.MachineMonth, exact.RecordKey);
            double med = Median(mine.Select(r => r.M3PerDay));
            return new RateHit(med, RateSource.MachineMedian,
                $"{m.MachineId} 各月台效中位数（{mine.Count} 条：{Span(mine)}）");
        }

        // ③ / ④ 型号级实测
        if (m.Model.Length > 0 && _byModel.TryGetValue(m.Model, out var same) && same.Count > 0)
        {
            var thisMonth = same.Where(r => r.Year == year && r.Month == month).ToList();
            if (thisMonth.Count > 0)
                return new RateHit(Median(thisMonth.Select(r => r.M3PerDay)), RateSource.ModelMonth,
                    $"型号 {m.Model} {year}-{month:00} 各台台效中位数（{thisMonth.Count} 台）—— {m.MachineId} 本身没有记录");
            return new RateHit(Median(same.Select(r => r.M3PerDay)), RateSource.ModelMedian,
                $"型号 {m.Model} 全部月份台效中位数（{same.Count} 条）—— {m.MachineId} 本身没有记录");
        }

        // ⑤ 型号字典缺省
        if (m.Model.Length > 0 && _modelDefault.TryGetValue(m.Model, out var def))
            return new RateHit(def.M3PerDay, RateSource.ModelDefault, def.RecordKey);

        // ⑥ 类别兜底
        if (_byKind.TryGetValue(m.Kind, out var kd))
            return new RateHit(kd.M3PerDay, RateSource.KindDefault, kd.RecordKey);

        return default;   // 解不出来 —— 不可派
    }

    private static string Span(List<RateRecord> l)
    {
        var f = l.OrderBy(r => r.Year).ThenBy(r => r.Month).First();
        var t = l.OrderBy(r => r.Year).ThenBy(r => r.Month).Last();
        return $"{f.Year}-{f.Month:00}…{t.Year}-{t.Month:00}";
    }

    internal static double Median(IEnumerable<double> src)
    {
        var v = src.Where(x => x > 0 && !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) * 0.5;
    }
}

/// <summary>一次台效查询的结果 —— <b>值 + 出处</b>，两个永远绑在一起走。</summary>
public readonly struct RateHit
{
    public readonly double M3PerDay;
    public readonly RateSource Source;
    public readonly string RecordKey;

    public RateHit(double v, RateSource s, string key)
    { M3PerDay = v; Source = s; RecordKey = key ?? ""; }

    public bool Ok => Source != RateSource.Unresolved && M3PerDay > 0;
    public bool Measured => RateBook.IsMeasured(Source);
}

// ══════════════════════════════════════════════════════════════════════════
//  指派引擎
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 把单元量摊到具体设备的具体工日上。
///
/// <para><b>时间粒度是"工日"，而且工日是原子的</b>：一台设备的一个工日只属于一个单元。
/// 允许切半天的话，"同一天不能在两处"这条就再也判不了了 —— 一台铲上午 A 下午 B，
/// 每一项校核都是 ✓，而现场根本挪不过来。代价是最后一天干不满会剩<b>余能</b>
/// （记在 <see cref="MachineAssignment.SpareCapM3"/>，<b>不结转</b>）。</para>
///
/// <para><b>卡车绑在铲上</b>：挖装设备每开工一个工日，就按编组规则占住 N 台空闲卡车同一天。
/// 当天的量按卡车台效比例摊给这几台车（只为出派车单，<b>不进采出量的账</b>）。
/// 一台空闲卡车都没有 ⇒ 这个铲这一天<b>开不了工</b>（不是"降效开工"）。</para>
///
/// <para><b>装不下就是装不下</b>：单元的量在本月工日内派不完，剩下的进
/// <see cref="EquipmentAssignResult.Shortfalls"/> 并写明原因。<b>绝不把它摊到别的设备/别的单元上</b>
/// —— 摊平之后总账是平的，而"这个月到底干不干得完"这个问题就永远问不出来了。</para>
/// </summary>
public static class EquipmentAssigner
{
    private const double Eps = 1e-6;

    /// <summary>一台设备的月工日日历。</summary>
    private sealed class Cal
    {
        public Machine M = null!;
        public RateHit Rate;
        public string[] Day = Array.Empty<string>();   // "" = 空闲
        public string LastUnit = "";                   // 上一个干过的单元（转场判定用）
        public int Used;                               // 已占工日（含转场）

        public bool Free(int d) => Day[d].Length == 0;
    }

    public static EquipmentAssignResult Assign(EquipmentAssignInput inp)
    {
        var r = new EquipmentAssignResult();
        if (inp == null) { r.Error = "没有输入"; return r; }

        int H = inp.WorkdayCount;
        if (H <= 0) { r.Error = $"本月作业日填的是 {inp.WorkdayCount}（须为正整数）"; return r; }
        if (H > 366) { r.Error = $"本月作业日填的是 {H} 天 —— 超过一年，八成是把小时填进来了"; return r; }
        r.WorkdayCount = H;
        r.ShiftsPerDay = inp.ShiftsPerDay > 0 ? inp.ShiftsPerDay : 3;
        if (!(inp.ShiftsPerDay > 0))
            r.Notes.Add($"⚠ 每日班次填的是 {inp.ShiftsPerDay:0.##}（须为正），已按 3 处理。");

        var units = (inp.Units ?? new List<UnitAssignment>())
                    .Where(u => u != null && u.UnitId.Length > 0).ToList();
        if (units.Count == 0) { r.Error = "没有采掘单元指派 —— 先跑一次「按目标排产」。"; return r; }

        // ── 台效账本 ───────────────────────────────────────────────────
        var book = RateBook.Build(inp.Rates, inp.MinRateM3PerDay, inp.CapacityScale, r.Notes);

        // ── 设备清单：分挖装 / 卡车，解台效 ─────────────────────────────
        var machines = (inp.Machines ?? new List<Machine>()).Where(m => m != null && m.MachineId.Length > 0).ToList();
        int offRoll = machines.Count(m => !m.Dispatchable);
        var pool = machines.Where(m => m.Dispatchable).ToList();
        if (offRoll > 0) r.Notes.Add($"· 设备清单 {machines.Count} 台，其中 {offRoll} 台不可派（非在用），本次不排。");

        var loaders = new List<Cal>();
        var trucks = new List<Cal>();
        var drills = new List<Cal>();
        var dozers = new List<Cal>();
        int noRate = 0; var noRateKinds = new Dictionary<MachineKind, int>();
        var skipped = new Dictionary<MachineKind, int>();

        // 这一轮排哪几类：两个开关决定钻机/推土机进不进池子。
        // 关着时它们走 skipped 那一支，和加这两个工序之前一模一样。
        bool wantDrill = inp.ScheduleDrilling, wantDoze = inp.ScheduleDozing;
        r.DrillingScheduled = wantDrill; r.DozingScheduled = wantDoze;

        foreach (var m in pool)
        {
            bool wanted = m.Kind is MachineKind.Shovel or MachineKind.Loader or MachineKind.Truck
                       || (wantDrill && m.Kind == MachineKind.Drill)
                       || (wantDoze && m.Kind == MachineKind.Dozer);
            if (!wanted)
            { skipped[m.Kind] = (skipped.TryGetValue(m.Kind, out int s) ? s : 0) + 1; continue; }

            var hit = book.Resolve(m, inp.Year, inp.Month);
            if (!hit.Ok)
            {
                noRate++;
                noRateKinds[m.Kind] = (noRateKinds.TryGetValue(m.Kind, out int k) ? k : 0) + 1;
                continue;                                  // 不可派 —— 不是台效为 0
            }
            var cal = new Cal { M = m, Rate = hit, Day = NewCal(H) };
            switch (m.Kind)
            {
                case MachineKind.Truck: trucks.Add(cal); break;
                case MachineKind.Drill: drills.Add(cal); break;
                case MachineKind.Dozer: dozers.Add(cal); break;
                default: loaders.Add(cal); break;
            }
        }

        if (skipped.Count > 0)
            r.Notes.Add($"· 本次排的是【挖装（电铲/前装机）+ 卡车{(wantDrill ? " + 穿孔（钻机）" : "")}{(wantDoze ? " + 排土（推土机）" : "")}】。未排："
                      + string.Join("、", skipped.OrderBy(kv => kv.Key).Select(kv => $"{KindName(kv.Key)} {kv.Value} 台"))
                      + " —— "
                      + (!wantDrill ? "钻机的开关（ScheduleDrilling）关着；" : "")
                      + (!wantDoze ? "推土机的开关（ScheduleDozing）关着；" : "")
                      + "平路/洒水在实测台效表里一条记录都没有，合成一个出来就是编数。");
        if (wantDrill && drills.Count == 0)
            r.Notes.Add("◆ 打开了穿孔排产，但<b>一台可派的钻机都没有</b>（不在册 / 非在用 / 解不出台效）。"
                      + "所有需爆破的岩单元都会因为「没穿爆」而开不了挖 —— 这不是算法保守，是设备维真的没数。");
        if (wantDoze && dozers.Count == 0)
            r.Notes.Add("◆ 打开了排土排产，但<b>一台可派的推土机都没有</b>。推土机在 equipment_model 里整类没有标准日产能"
                      + "（取数层已经报过这一条），所以它只能靠 capacity_monthly 的实测台效 —— 实测也没有就不可派。");
        if (noRate > 0)
            r.Notes.Add($"◆ {noRate} 台设备<b>解不出台效</b>（"
                      + string.Join("、", noRateKinds.Select(kv => $"{KindName(kv.Key)} {kv.Value}"))
                      + "），本次不可派。这是【没有这条记录】，不是台效为 0 —— 按 0 排会让它们看起来"
                      + "在岗却永远干不出量，而总账仍然是平的。");

        if (loaders.Count == 0)
        { r.Error = "一台可派的挖装设备都没有（电铲/前装机全都不可派或解不出台效）"; return r; }
        if (trucks.Count == 0)
            r.Notes.Add("◆ 一台可派的卡车都没有 —— 挖装设备将全部开不了工（本引擎不允许无车开采）。");

        // ── 编组规则 ───────────────────────────────────────────────────
        var pairing = BuildPairing(inp.Pairings, r.Notes);

        // ── 单元位置（转场距离）──────────────────────────────────────────
        var site = new Dictionary<string, UnitSite>(StringComparer.Ordinal);
        foreach (var s in inp.Sites ?? new List<UnitSite>())
            if (s != null && s.UnitId.Length > 0) site[s.UnitId] = s;
        int noSite = units.Count(u => !site.ContainsKey(u.UnitId));
        if (noSite > 0)
            r.Notes.Add($"· {noSite} 个单元没给质心，转场距离判不了 —— 这些单元一律按【换单元就转场】处理（偏保守）。"
                      + "要精确就把 MineUnit 的 (Cx,Cy,Cz) 一起喂进来。");

        // ── 先后约束 ───────────────────────────────────────────────────
        var succAfter = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // Before → After[]
        foreach (var (b, a) in inp.Precedence ?? new List<(string, string)>())
        {
            if (string.IsNullOrEmpty(b) || string.IsNullOrEmpty(a)) continue;
            if (!succAfter.TryGetValue(b, out var l)) succAfter[b] = l = new List<string>();
            l.Add(a);
        }
        if (succAfter.Count == 0)
            r.Notes.Add("· 没给先后约束（压覆/边坡），排班只按上游的推进序 Seq 挑设备，"
                      + "<b>不保证被压的单元排在压覆它的单元之后</b>。要保证就把 UnitGraph 的边喂进来。");

        // ── 主循环：按推进序逐单元派设备 ──────────────────────────────────
        var earliest = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = units.OrderBy(u => u.Seq <= 0 ? int.MaxValue : u.Seq)
                           .ThenBy(u => u.UnitId, StringComparer.Ordinal).ToList();
        var scheduled = new HashSet<string>(StringComparer.Ordinal);
        var bookings = new List<Booking>();
        int precViolations = 0;

        // ══ 穿孔遍（在挖装之前）════════════════════════════════════════════
        //  为什么必须先跑完整遍再排挖装：穿爆是【超前】工序 —— 挖装的最早开工日由它决定。
        //  边排边定的话，先排到的单元会拿到还没算出来的超前日，而后排到的拿到算好的，
        //  两种单元排出来的班表看不出区别（都"有个开工日"），只是有一半是错的。
        int lead = Math.Max(0, inp.BlastLeadDays);
        r.BlastLeadDaysUsed = lead;
        var noBlast = inp.NoBlastUnitIds ?? new HashSet<string>(StringComparer.Ordinal);
        r.NoBlastUnitsUsed = new HashSet<string>(noBlast, StringComparer.Ordinal);

        var drillEnd = new Dictionary<string, int>(StringComparer.Ordinal);
        var drillFrac = new Dictionary<string, double>(StringComparer.Ordinal);
        // 逐单元【实际用了】哪个超前期 —— 判据⑥要照它判，不能另取一个缺省值；
        // 面级覆盖了几个也要报，否则"接了面级超前期"和"接了但都是全局值"分不出来。
        var usedLead = new Dictionary<string, int>(StringComparer.Ordinal);
        int leadOverridden = 0;
        // 超前期给出的"最早可开挖日" —— 与 earliest 分开存，因为 earliest 还会被先后约束改。
        // 它只说"约束是这么要求的"，说不了"实际有没有因此推迟"：那要等挖装排完才知道（见下）。
        var leadEarliest = new Dictionary<string, int>(StringComparer.Ordinal);

        if (wantDrill)
        {
            if (noBlast.Count == 0)
                r.Notes.Add("· 穿爆口径：<b>所有岩单元都按需要爆破算</b>（NoBlastUnitIds 是空的）。"
                          + "露天煤矿的剥离里本来就有一截表土/风化层不用爆破，全算成要穿会<b>高估钻机需求</b>，"
                          + "并把那些单元的开工日也一起推后。要精确就按物料把免爆单元喂进来。");
            else
                r.Notes.Add($"· 穿爆口径：{noBlast.Count} 个单元按<b>免爆</b>算（调用方按物料指定），其余岩单元都要穿爆；煤单元一律不穿。");

            foreach (var u in ordered)
            {
                if (!NeedsBlast(u, noBlast)) continue;
                double need = Math.Max(0, u.InSituM3);
                r.DrillDemandM3 += need;
                if (need <= Eps) continue;

                double got = 0; int last = 0;
                var why = new List<string>();
                for (int slot = 0; slot < Math.Max(1, inp.MaxDrillsPerUnit) && need - got > Eps; slot++)
                {
                    var D = PickMachine(drills, u.UnitId, site, 1, H, bookings, inp, MachineRole.Drill);
                    if (D == null) { why.Add("本月工日内没有空闲的钻机了"); break; }
                    var o = RunSolo(D, u.UnitId, u.Kind, u.Seq, MachineRole.Drill, site, 1, H, need - got, inp, bookings, doReloc: true);
                    got += o.Volume; last = Math.Max(last, o.LastDay);
                    if (o.Volume <= Eps) { if (o.Reason.Length > 0) why.Add(o.Reason); break; }
                    if (o.Reason.Length > 0) why.Add(o.Reason);
                }

                r.DrilledM3 += got;
                if (got > Eps) { drillEnd[u.UnitId] = last; drillFrac[u.UnitId] = Math.Min(1.0, got / need); }
                else drillFrac[u.UnitId] = 0;

                if (need - got > Eps)
                {
                    if (why.Count == 0) why.Add("本月工日不够 —— 需穿的控制方量超过了可派钻机在本月的总台效");
                    r.Shortfalls.Add(new UnitShortfall
                    {
                        UnitId = u.UnitId, UnitKind = u.Kind, Process = ProcessKind.Drilling,
                        DemandM3 = need, AssignedM3 = got,
                        Reason = string.Join("；", why.Distinct()),
                    });
                }

                // 穿爆超前 → 这个单元最早哪天能开挖。
                // ★ 超前期【面级优先】：硬岩大区爆破和煤层控制爆破本来就不是一个数，
                //   一个全局常数表达不了。面上没填（0）才回退到全局。
                int uLead = PinOf(inp, u.UnitId)?.BlastLeadDays is > 0 and var pl ? pl : lead;
                if (uLead != lead) leadOverridden++;
                usedLead[u.UnitId] = uLead;

                if (got > Eps)
                {
                    int canStart = last + uLead + 1;
                    if (canStart > H) { earliest[u.UnitId] = H + 1; r.UnitsBlockedByDrilling++; }
                    else { earliest[u.UnitId] = canStart; leadEarliest[u.UnitId] = canStart; }
                }
                else { earliest[u.UnitId] = H + 1; r.UnitsBlockedByDrilling++; }   // 一方都没穿 = 开不了挖
            }

            r.LeadDaysByUnit = usedLead;
            r.LeadOverriddenUnits = leadOverridden;
            if (leadOverridden > 0)
                r.Notes.Add($"· {leadOverridden} 个单元用的是<b>面级穿爆超前期</b>（作业面工艺里填的），"
                          + $"其余用全局 {lead} 工日。硬岩大区爆破和煤层控制爆破本来就不是一个数。");

            if (r.UnitsBlockedByDrilling > 0)
                r.Notes.Add($"◆ {r.UnitsBlockedByDrilling} 个单元<b>因为穿爆没排上而整个采不了</b>"
                          + $"（一方都没穿，或穿完加 {lead} 工日超前期已经出了本月）。"
                          + "这些单元的采装欠产原因写的是穿爆，不是设备不够 —— 别去加铲。");
        }

        foreach (var u in ordered)
        {
            double demand = Math.Max(0, u.InSituM3);
            r.DemandM3 += demand;
            if (demand <= Eps) continue;

            int from = earliest.TryGetValue(u.UnitId, out int e) ? e : 1;
            double placed = 0;
            var reasons = new List<string>();
            int lastEnd = 0;

            // 穿了多少才能采多少 —— 没穿爆的那部分不是"设备不够"，是根本还不能挖。
            double cap = demand;
            if (wantDrill && NeedsBlast(u, noBlast))
            {
                double fr = drillFrac.TryGetValue(u.UnitId, out double f) ? f : 0;
                cap = demand * Math.Max(0, Math.Min(1, fr));
                if (cap < demand - Eps)
                    reasons.Add(fr <= 0
                        ? $"本月一方都没穿爆（{(from > H ? "超前期已出本月" : "没有可派钻机")}），整个单元采不了"
                        : $"只穿爆了 {fr * 100:0.#}%，剩下的还不能挖");
            }
            if (from > H && cap > Eps) { cap = 0; }

            // ★ 循环条件与 Run 的量都改用 cap（可挖上限），不是 demand ——
            //   没穿爆的那部分再多铲也挖不出来。demand 仍然进总账，差额落到欠产里。
            for (int slot = 0; slot < Math.Max(1, inp.MaxLoadersPerUnit) && cap - placed > Eps; slot++)
            {
                var L = PickLoader(loaders, u, site, from, H, bookings, inp);
                if (L == null) { reasons.Add("本月工日内没有空闲的挖装设备了"); break; }

                var got = Run(L, u, site, from, H, cap - placed, trucks, pairing, inp, r, bookings);
                placed += got.Volume;
                lastEnd = Math.Max(lastEnd, got.LastDay);
                if (got.Volume <= Eps && got.Reason.Length > 0) { reasons.Add(got.Reason); break; }
                if (got.Reason.Length > 0) reasons.Add(got.Reason);
            }

            r.AssignedM3 += placed;
            if (demand - placed > Eps)
            {
                if (reasons.Count == 0) reasons.Add("本月工日不够 —— 单元的量超过了可派设备在剩余工日里的总台效");
                r.Shortfalls.Add(new UnitShortfall
                {
                    UnitId = u.UnitId, UnitKind = u.Kind,
                    DemandM3 = demand, AssignedM3 = placed,
                    Reason = string.Join("；", reasons.Distinct()),
                });
            }

            scheduled.Add(u.UnitId);
            if (succAfter.TryGetValue(u.UnitId, out var afters))
            {
                // 先后约束指向一个【已经排过】的单元 ⇒ 这条约束等于没加，如实计数。
                // 不报的话，"接了先后图"和"接了但一条都没挡住"排出来的班表一模一样。
                precViolations += afters.Count(x => scheduled.Contains(x));
                if (lastEnd > 0)
                    foreach (var a in afters)
                        earliest[a] = Math.Max(earliest.TryGetValue(a, out int cur) ? cur : 1, lastEnd + 1);
            }
        }
        if (precViolations > 0)
            r.Notes.Add($"◆ {precViolations} 条先后约束<b>没起作用</b>：后继单元在前驱之前就已经排完了"
                      + "（上游给的 Seq 与先后图不一致）。这几条约束等于没加，别当它挡住了。");

        // ── 穿爆超前【实际】推迟了几个单元 ──────────────────────────────
        //  ★ 必须等挖装排完才数得出来：超前期算出"最早第 4 日"，不等于这个单元真的因此推迟 ——
        //    铲本来就要到第 5 日才腾得出手的话，这条约束一天都没起作用。
        //    照"canStart > 1"去数，会把一堆根本没被挡住的单元报成"因穿爆推迟"，
        //    于是「接了超前约束」和「接了但一个都没挡住」这两件事又分不出来了 —— 本模块的
        //    先后约束已经栽过一次同样的跟头（precViolations 那一条就是那次留下的）。
        if (wantDrill && leadEarliest.Count > 0)
        {
            var firstDay = bookings.Where(b => b.Role == MachineRole.Excavate)
                                   .GroupBy(b => b.UnitId, StringComparer.Ordinal)
                                   .ToDictionary(g => g.Key, g => g.Min(b => b.Day), StringComparer.Ordinal);
            int bound = 0, slack = 0;
            foreach (var kv in leadEarliest)
            {
                if (kv.Value <= 1) continue;                                  // 超前期没往后推
                if (!firstDay.TryGetValue(kv.Key, out int s)) continue;       // 压根没开挖，算在 Blocked 里
                if (s == kv.Value) bound++; else slack++;                     // 相等 = 这条约束正好卡住了它
            }
            r.UnitsDelayedByBlastLead = bound;
            if (bound > 0)
                r.Notes.Add($"· {bound} 个单元的开工日<b>确实被穿爆超前卡住</b>（挖装正好从超前期满的那天起）。"
                          + "这是打开穿孔之后采装量发生变化的来源 —— 关掉开关能回到原来的数。");
            if (slack > 0)
                r.Notes.Add($"· 另有 {slack} 个单元虽然算出了超前期，但<b>实际不是它卡住的</b>"
                          + "（铲本来就要更晚才腾得出手）。对这几个单元，穿爆超前这条约束这个月一天都没起作用。");
            if (bound == 0 && slack > 0)
                r.Notes.Add("◆ 打开了穿爆超前，但<b>没有任何一个单元是被它卡住的</b> —— "
                          + "这份班表和不接超前约束排出来的一模一样。要么超前期填得太小，要么挖装设备本来就是瓶颈。");
        }

        // ══ 排土遍（在挖装之后）════════════════════════════════════════════
        //  作业对象是【去向】不是单元：推土机守的是排土场，一天推的是当天各单元汇过来的料。
        //  按单元拆笔的话，同一台推土机同一天会出现在好几个"单元"上，
        //  「同一天不能在两处」那条判据当场误报 —— 而它误报的是一件本来完全正常的事。
        if (wantDoze) DozePass(units, bookings, dozers, H, inp, r);

        // ── 合并成连续段，吐出去 ────────────────────────────────────────
        Emit(bookings, r);

        // ── 面级型号约束：钉了几条、真起作用几条、有几条根本挑不到设备 ──────────
        //  ★「钉了型号」和「钉了但那个型号一台都没有」排出来的结果差别很大（后者直接欠产），
        //    但界面上长得一模一样。所以这三个数必须都报出来。
        ReportPins(inp, r, loaders, trucks, drills, dozers, wantDrill, wantDoze);

        // ── 台效量级体检（判据要能证伪：一个从不触发的能力上限就是一条死路）──
        r.RateScaleAudit = AuditScale(loaders, inp);
        if (r.RateScaleAudit.Length > 0) r.Notes.Add("◆ " + r.RateScaleAudit);

        // 钻机 / 推土机各自的量级体检 —— 用挖装那条合理带去判钻机是错的，两者口径本来就不同
        //（一个是原位实方，一个是控制方量）。带各配各的。
        string dA = AuditBand(drills, inp.AuditDrillDayLo, inp.AuditDrillDayHi, "钻机", "控制方量");
        if (dA.Length > 0) r.Notes.Add("◆ " + dA);
        string zA = AuditBand(dozers, inp.AuditDozerDayLo, inp.AuditDozerDayHi, "推土机", "排弃占容");
        if (zA.Length > 0) r.Notes.Add("◆ " + zA);

        // ── 配比交叉核对（编组规则 vs 台效配平）────────────────────────────
        CrossCheckTruckRatio(loaders, trucks, pairing, inp, r);

        // ── 编组口径与成组质量，如实报 ──────────────────────────────────
        if (!pairing.Empty && inp.TruckSizing == TruckSizing.ByDispatchRule)
            r.Notes.Add("· 编组口径：recommended_truck_count 是对【一对 (铲型, 车型)】说的，"
                      + "所以本引擎<b>先选车型、再按那个车型自己的推荐台数配</b>，同一台铲当天不混编车型。"
                      + "（混着几种车型再取各规则的最大值，拼出来的是一条谁也没写过的规则。）");
        if (r.MixedModelCrewDays > 0)
            r.Notes.Add($"◆ {r.MixedModelCrewDays} 个挖装工日<b>混编了车型</b>（没有哪一种车型当天能单独凑够推荐台数），"
                      + $"另有 {r.SingleModelCrewDays} 个工日是正常单一车型成组。混编那几天用的配比现场规则里没有。");
        if (r.UnderCrewedDays > 0)
            r.Notes.Add($"◆ {r.UnderCrewedDays} 个挖装工日<b>没配够</b>规则要求的车数（当天空闲车就那么多），"
                      + "这些天的产量已按实配到的车的运力压过，不是按满编算的。");

        r.Success = true;
        return r;
    }

    // ──────────────────────────────────────────────────────────────────
    //  逐工日推进
    // ──────────────────────────────────────────────────────────────────

    private readonly struct RunOut
    {
        public readonly double Volume; public readonly int LastDay; public readonly string Reason;
        public RunOut(double v, int d, string why) { Volume = v; LastDay = d; Reason = why ?? ""; }
    }

    /// <summary>一笔"记账"：某台设备、某个工日、干哪个单元、多少量。合并成段是最后一步。</summary>
    private sealed class Booking
    {
        public string MachineId = "", Model = "", UnitId = "", Serves = "";
        public MachineKind Kind; public UnitKind UKind; public MachineRole Role;
        public int Day, Seq;
        public double M3, Rate;
        public RateSource Src; public string Key = "";
        public bool HaulLimited;
    }

    private static RunOut Run(Cal L, UnitAssignment u, Dictionary<string, UnitSite> site,
                              int from, int H, double demand,
                              List<Cal> trucks, Pairing pairing,
                              EquipmentAssignInput inp, EquipmentAssignResult r,
                              List<Booking> outp)
    {
        // ① 转场：换单元且距离超过免费半径（或距离判不了）就占工日
        int reloc = RelocDays(L, u, site, inp);
        int d = from;
        int need = reloc;
        var relocDays = new List<int>();
        while (need > 0 && d <= H)
        {
            if (L.Free(d)) { L.Day[d] = "↷" + u.UnitId; L.Used++; relocDays.Add(d); need--; }
            d++;
        }
        if (need > 0)
            return new RunOut(0, 0, $"{L.M.MachineId} 转场要 {reloc} 工日，本月剩下的工日不够转过去");

        foreach (int rd in relocDays)
            outp.Add(new Booking
            {
                MachineId = L.M.MachineId, Model = L.M.Model, Kind = L.M.Kind, Role = MachineRole.Relocate,
                UnitId = u.UnitId, UKind = u.Kind, Seq = u.Seq, Day = rd, M3 = 0, Rate = 0,
                Src = RateSource.Unresolved, Key = "",
            });

        // ② 逐日开工
        double left = demand, done = 0;
        int lastDay = 0, blocked = 0, limited = 0;
        while (left > Eps && d <= H)
        {
            if (!L.Free(d)) { d++; continue; }

            var pick = PickCrew(trucks, L, pairing, d, inp, PinOf(inp, u.UnitId));
            var crew = pick.Trucks;
            if (crew.Count == 0) { blocked++; d++; continue; }     // 无车不开工

            if (pick.Mixed) r.MixedModelCrewDays++; else r.SingleModelCrewDays++;
            if (crew.Count < pick.Wanted) r.UnderCrewedDays++;

            double haulCap = crew.Sum(t => t.Rate.M3PerDay);
            double dayCap = Math.Min(L.Rate.M3PerDay, haulCap);
            bool haulBound = haulCap < L.Rate.M3PerDay - Eps;
            if (haulBound) limited++;

            double take = Math.Min(left, dayCap);
            if (take <= Eps) break;

            L.Day[d] = u.UnitId; L.Used++;
            outp.Add(new Booking
            {
                MachineId = L.M.MachineId, Model = L.M.Model, Kind = L.M.Kind, Role = MachineRole.Excavate,
                UnitId = u.UnitId, UKind = u.Kind, Seq = u.Seq, Day = d,
                M3 = take, Rate = L.Rate.M3PerDay, Src = L.Rate.Source, Key = L.Rate.RecordKey,
                HaulLimited = haulBound,
            });

            // 当天的量按卡车台效比例摊给这几台车 —— 只为出派车单，不进采出量的账
            double denom = crew.Sum(t => t.Rate.M3PerDay);
            for (int k = 0; k < crew.Count; k++)
            {
                var t = crew[k];
                double share = denom > Eps ? take * (t.Rate.M3PerDay / denom) : take / crew.Count;
                if (k == crew.Count - 1)                       // 末位吸收舍入残差，保证逐单元对得上
                    share = take - crew.Take(crew.Count - 1)
                                       .Sum(x => denom > Eps ? take * (x.Rate.M3PerDay / denom) : take / crew.Count);
                t.Day[d] = u.UnitId; t.Used++;
                outp.Add(new Booking
                {
                    MachineId = t.M.MachineId, Model = t.M.Model, Kind = MachineKind.Truck, Role = MachineRole.Haul,
                    UnitId = u.UnitId, UKind = u.Kind, Seq = u.Seq, Day = d, Serves = L.M.MachineId,
                    M3 = share, Rate = t.Rate.M3PerDay, Src = t.Rate.Source, Key = t.Rate.RecordKey,
                    HaulLimited = haulBound,
                });
            }

            left -= take; done += take; lastDay = d; d++;
        }

        L.LastUnit = u.UnitId;
        r.HaulBlockedDays += blocked;
        r.HaulLimitedDays += limited;

        string why = "";
        if (left > Eps)
        {
            if (blocked > 0)
                why = $"{L.M.MachineId} 有 {blocked} 个工日因为一台空闲卡车都没有而开不了工";
            else if (limited > 0)
                why = $"{L.M.MachineId} 的 {limited} 个工日被卡车运力卡住（铲台效 {L.Rate.M3PerDay:0}m³/日，配到的车拉不了这么多）";
            else
                why = $"{L.M.MachineId} 干到本月最后一个工日仍未干完（台效 {L.Rate.M3PerDay:0}m³/日）";
        }
        return new RunOut(done, lastDay, why);
    }

    // ──────────────────────────────────────────────────────────────────
    //  穿孔 / 排土
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 这个单元要不要穿爆。<b>煤单元一律不穿</b>；岩单元除非在免爆清单里，否则都要穿。
    /// <para>为什么不去看物料：<see cref="UnitAssignment"/> 只带煤/岩两分，分不出表土与硬岩
    /// （物料在上游的 <c>MineUnit.MaterialIndex</c> 上）。与其在这里按单元号猜，
    /// 不如让调用方按物料把免爆清单喂进来 —— 猜出来的判别每一项校核都会是 ✓。</para>
    /// </summary>
    private static bool NeedsBlast(UnitAssignment u, HashSet<string> noBlast)
        => u.Kind == UnitKind.Rock && !noBlast.Contains(u.UnitId);

    /// <summary>
    /// 一台设备在一个作业对象上连续干（<b>不配车</b>）—— 穿孔与排土都走这一条。
    /// 与 <see cref="Run"/> 的差别只有"没有卡车这一环"，其余（转场占工日、逐日推进、
    /// 干不完就如实返回原因）一模一样。
    /// </summary>
    private static RunOut RunSolo(Cal L, string siteId, UnitKind ukind, int seq, MachineRole role,
                                  Dictionary<string, UnitSite> site, int from, int H, double demand,
                                  EquipmentAssignInput inp, List<Booking> outp, bool doReloc)
    {
        int d = from;
        if (doReloc)
        {
            int reloc = RelocDaysTo(L, siteId, site, inp);
            int need = reloc;
            var relocDays = new List<int>();
            while (need > 0 && d <= H)
            {
                if (L.Free(d)) { L.Day[d] = "↷" + siteId; L.Used++; relocDays.Add(d); need--; }
                d++;
            }
            if (need > 0)
                return new RunOut(0, 0, $"{L.M.MachineId} 转场要 {reloc} 工日，本月剩下的工日不够转过去");
            foreach (int rd in relocDays)
                outp.Add(new Booking
                {
                    MachineId = L.M.MachineId, Model = L.M.Model, Kind = L.M.Kind, Role = MachineRole.Relocate,
                    UnitId = siteId, UKind = ukind, Seq = seq, Day = rd, M3 = 0, Rate = 0,
                    Src = RateSource.Unresolved, Key = "",
                });
        }

        double left = demand, done = 0; int lastDay = 0;
        while (left > Eps && d <= H)
        {
            if (!L.Free(d)) { d++; continue; }
            double take = Math.Min(left, L.Rate.M3PerDay);
            if (take <= Eps) break;

            L.Day[d] = siteId; L.Used++;
            outp.Add(new Booking
            {
                MachineId = L.M.MachineId, Model = L.M.Model, Kind = L.M.Kind, Role = role,
                UnitId = siteId, UKind = ukind, Seq = seq, Day = d,
                M3 = take, Rate = L.Rate.M3PerDay, Src = L.Rate.Source, Key = L.Rate.RecordKey,
            });
            left -= take; done += take; lastDay = d; d++;
        }
        L.LastUnit = siteId;

        string why = left > Eps
            ? $"{L.M.MachineId} 干到本月最后一个工日仍未干完（台效 {L.Rate.M3PerDay:0}m³/日）"
            : "";
        return new RunOut(done, lastDay, why);
    }

    /// <summary>
    /// 排土遍：从<b>已经排好的挖装记账</b>反推每天每个去向到多少料，再配推土机。
    ///
    /// <para><b>量的口径是排弃占容</b>（<c>UnitFlow.DumpM3</c> = V实×Kr），与排土场库容同一本账。
    /// 严格说推土机推的是刚卸下的<b>松散方</b>（V实×Ks，比占容还大一成多），但 Ks 没有随
    /// <see cref="UnitAssignment"/> 传下来，<b>宁可用一个有出处的口径，也不现编一个系数</b> ——
    /// 这一条写进每一笔的溯源键里，报表上追得到。</para>
    ///
    /// <para><b>同日到料、同日推平</b>：现场排土场有个缓冲，不是当天到当天平。
    /// 这里按同日算是偏紧的一阶近似，会把推土机需求略微算高 —— 如实记在 Notes 里。</para>
    /// </summary>
    private static void DozePass(List<UnitAssignment> units, List<Booking> bookings, List<Cal> dozers,
                                 int H, EquipmentAssignInput inp, EquipmentAssignResult r)
    {
        var byId = new Dictionary<string, UnitAssignment>(StringComparer.Ordinal);
        foreach (var u in units) byId[u.UnitId] = u;

        // day → (destCode → 占容 m³)
        var arrive = new Dictionary<int, Dictionary<string, double>>();
        var destName = new Dictionary<string, string>(StringComparer.Ordinal);
        int noFlowUnits = 0; double noFlowM3 = 0;

        foreach (var b in bookings.Where(x => x.Role == MachineRole.Excavate && x.M3 > Eps))
        {
            if (!byId.TryGetValue(b.UnitId, out var u)) continue;
            double tot = u.Flows.Sum(f => f.InSituM3);
            if (!(tot > Eps))
            {
                // 这个单元根本没有去向流 —— 它的岩没地方排，而挖装已经排上了。
                if (u.Kind == UnitKind.Rock) { noFlowUnits++; noFlowM3 += b.M3; }
                continue;
            }
            foreach (var f in u.Flows)
            {
                if (!(f.DumpM3 > Eps)) continue;                       // 煤流 DumpM3=0，自动不排土
                string code = string.IsNullOrWhiteSpace(f.DestinationCode) ? "(未指定去向)" : f.DestinationCode;
                destName[code] = string.IsNullOrWhiteSpace(f.DestinationName) ? code : f.DestinationName;
                if (!arrive.TryGetValue(b.Day, out var m)) arrive[b.Day] = m = new Dictionary<string, double>(StringComparer.Ordinal);
                m[code] = (m.TryGetValue(code, out double v) ? v : 0) + b.M3 * f.DumpM3 / tot;
            }
        }

        if (noFlowUnits > 0)
            r.Notes.Add($"◆ {noFlowUnits} 笔岩单元的挖装量（合计 {noFlowM3 / 1e4:0.0}万m³实方）<b>没有去向流</b>，"
                      + "排土这一侧一方都没记账 —— 上游「采掘单元清单」还没给它们配去向，先去那儿排一次。");

        if (arrive.Count == 0)
        {
            r.Notes.Add("· 排土：本月没有需要排弃的料（全是煤，或岩单元都没排上挖装）。");
            return;
        }

        var shortByDest = new Dictionary<string, (double dem, double got, string why)>(StringComparer.Ordinal);

        foreach (int day in arrive.Keys.OrderBy(x => x))
        {
            foreach (var kv in arrive[day].OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                string code = kv.Key; double need = kv.Value;
                if (need <= Eps) continue;
                r.DozeDemandM3 += need;

                double got = 0; string why = "";
                for (int slot = 0; slot < Math.Max(1, inp.MaxDozersPerSink) && need - got > Eps; slot++)
                {
                    // 当天空闲、台效最高的那台。推土机不算转场：它在排土场内挪窝，
                    // 而单元质心表里没有排土场的坐标 —— 拿单元坐标去算排土场的转场是编数。
                    var D = dozers.Where(x => x.Free(day))
                                  .OrderByDescending(x => x.Rate.M3PerDay)
                                  .ThenBy(x => x.M.MachineId, StringComparer.Ordinal)
                                  .FirstOrDefault();
                    if (D == null)
                    {
                        why = dozers.Count == 0 ? "一台可派的推土机都没有"
                                                : $"第 {day} 日的推土机全被别的排土位置占着";
                        break;
                    }
                    double take = Math.Min(need - got, D.Rate.M3PerDay);
                    if (take <= Eps) break;
                    D.Day[day] = code; D.Used++; D.LastUnit = code;
                    bookings.Add(new Booking
                    {
                        MachineId = D.M.MachineId, Model = D.M.Model, Kind = MachineKind.Dozer, Role = MachineRole.Dump,
                        UnitId = code, UKind = UnitKind.Rock, Seq = 0, Day = day,
                        M3 = take, Rate = D.Rate.M3PerDay, Src = D.Rate.Source, Key = D.Rate.RecordKey,
                    });
                    got += take;
                }

                r.DozedM3 += got;
                if (need - got > Eps)
                {
                    if (why.Length == 0)
                        why = $"第 {day} 日到料 {need:0}m³ 占容，摆满 {inp.MaxDozersPerSink} 台推土机也推不完";
                    var cur = shortByDest.TryGetValue(code, out var c) ? c : (0.0, 0.0, "");
                    shortByDest[code] = (cur.Item1 + need, cur.Item2 + got, cur.Item3.Length > 0 ? cur.Item3 : why);
                }
                else
                {
                    var cur = shortByDest.TryGetValue(code, out var c) ? c : (0.0, 0.0, "");
                    if (cur.Item1 > 0 || cur.Item2 > 0) shortByDest[code] = (cur.Item1 + need, cur.Item2 + got, cur.Item3);
                }
            }
        }

        // 逐去向汇一条欠产 —— 按 (去向×天) 出行的话，一个月能出上百行，谁也读不完。
        foreach (var kv in shortByDest.OrderByDescending(x => x.Value.dem - x.Value.got))
        {
            double sh = kv.Value.dem - kv.Value.got;
            if (sh <= Eps) continue;
            r.Shortfalls.Add(new UnitShortfall
            {
                UnitId = kv.Key, UnitKind = UnitKind.Rock, Process = ProcessKind.Dumping,
                DemandM3 = kv.Value.dem, AssignedM3 = kv.Value.got,
                Reason = kv.Value.why + $"（去向：{(destName.TryGetValue(kv.Key, out string? nm) ? nm : kv.Key)}）",
            });
        }

        r.Notes.Add("· 排土口径：量走<b>排弃占容</b>（V实×Kr，与排土场库容同一本账），"
                  + "<b>不是</b>推土机实际推的松散方（V实×Ks，还要大一成多）—— Ks 没随单元指派传下来，"
                  + "宁可用一个有出处的口径也不现编系数。每一笔的溯源键里写着这句话。");
        r.Notes.Add("· 排土节拍：按<b>当天到料当天推平</b>算（偏紧）。现场排土场有缓冲，"
                  + "所以这个口径会把推土机需求略微算高 —— 别拿它当采购依据。");
    }

    /// <summary>转场工日（对任意作业对象码）。<see cref="RelocDays"/> 的通用版。</summary>
    private static int RelocDaysTo(Cal L, string siteId, Dictionary<string, UnitSite> site, EquipmentAssignInput inp)
    {
        if (string.Equals(L.LastUnit, siteId, StringComparison.Ordinal)) return 0;
        int cost = L.M.Kind == MachineKind.Loader
                 ? Math.Max(0, inp.RelocationDaysWheeled)
                 : Math.Max(0, inp.RelocationDaysTracked);
        if (L.LastUnit.Length == 0) return 0;                     // 本月第一站不算转场
        if (site.TryGetValue(L.LastUnit, out var a) && site.TryGetValue(siteId, out var b))
        {
            double dx = a.Cx - b.Cx, dy = a.Cy - b.Cy, dz = a.Cz - b.Cz;
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= Math.Max(0, inp.FreeRelocationM)) return 0;
        }
        return cost;
    }

    /// <summary>挑一台设备（穿孔用）：最早能开工的优先，其次不用转场的、台效高的。</summary>
    private static Cal? PickMachine(List<Cal> pool, string siteId, Dictionary<string, UnitSite> site,
                                    int from, int H, List<Booking> already, EquipmentAssignInput inp,
                                    MachineRole role)
    {
        var onThis = already.Where(b => string.Equals(b.UnitId, siteId, StringComparison.Ordinal) && b.Role == role)
                            .Select(b => b.MachineId).ToHashSet(StringComparer.Ordinal);
        var pinRec = PinOf(inp, siteId);
        string pin = role == MachineRole.Drill ? pinRec?.DrillModel ?? "" : "";
        Cal? best = null; (int day, int reloc, double negRate, string id) bestKey = default;
        foreach (var L in pool)
        {
            if (onThis.Contains(L.M.MachineId)) continue;
            if (pin.Length > 0 && !string.Equals(L.M.Model, pin, StringComparison.OrdinalIgnoreCase)) continue;
            int f = FirstFree(L, from, H);
            if (f < 0) continue;
            int rl = RelocDaysTo(L, siteId, site, inp);
            if (f + rl > H) continue;
            var key = (f + rl, rl, -L.Rate.M3PerDay, L.M.MachineId);
            if (best == null || Cmp(key, bestKey) < 0) { best = L; bestKey = key; }
        }
        return best;
    }

    // ──────────────────────────────────────────────────────────────────
    //  择设备
    // ──────────────────────────────────────────────────────────────────

    /// <summary>挑挖装设备：<b>最早能开工的</b>优先，其次不用转场的、台效高的。</summary>
    private static Cal? PickLoader(List<Cal> loaders, UnitAssignment u, Dictionary<string, UnitSite> site,
                                   int from, int H, List<Booking> already, EquipmentAssignInput inp)
    {
        var onThis = already.Where(b => b.UnitId == u.UnitId && b.Role == MachineRole.Excavate)
                            .Select(b => b.MachineId).ToHashSet(StringComparer.Ordinal);
        string pin = PinOf(inp, u.UnitId)?.LoaderModel ?? "";
        Cal? best = null; (int day, int reloc, double negRate, string id) bestKey = default;
        foreach (var L in loaders)
        {
            if (onThis.Contains(L.M.MachineId)) continue;         // 同一单元不重复派同一台
            // 面上钉了型号就是【硬约束】：钉了却挑不到，结果是欠产，不是悄悄换一台别的。
            // 静默放行的话，界面上钉着的那个型号就成了摆设，而报表还显示它生效了。
            if (pin.Length > 0 && !string.Equals(L.M.Model, pin, StringComparison.OrdinalIgnoreCase)) continue;
            int f = FirstFree(L, from, H);
            if (f < 0) continue;
            int rl = RelocDays(L, u, site, inp);
            if (f + rl > H) continue;                             // 转完场就没工日了
            var key = (f + rl, rl, -L.Rate.M3PerDay, L.M.MachineId);
            if (best == null || Cmp(key, bestKey) < 0) { best = L; bestKey = key; }
        }
        return best;
    }

    /// <summary>取某单元的型号约束（没钉返回 null）。</summary>
    private static UnitFacePin? PinOf(EquipmentAssignInput inp, string unitId)
        => inp.Pins != null && inp.Pins.TryGetValue(unitId, out var p) && p != null && p.Any ? p : null;

    private static int Cmp((int, int, double, string) a, (int, int, double, string) b)
    {
        int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
        c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
        return string.CompareOrdinal(a.Item4, b.Item4);
    }

    private static int FirstFree(Cal c, int from, int H)
    {
        for (int d = Math.Max(1, from); d <= H; d++) if (c.Free(d)) return d;
        return -1;
    }

    /// <summary>转场工日：同一单元 = 0；质心距离 ≤ 免费半径 = 0；判不了距离 = 按转场算（保守）。</summary>
    private static int RelocDays(Cal L, UnitAssignment u, Dictionary<string, UnitSite> site, EquipmentAssignInput inp)
    {
        if (string.Equals(L.LastUnit, u.UnitId, StringComparison.Ordinal)) return 0;
        int cost = L.M.Kind == MachineKind.Loader
                 ? Math.Max(0, inp.RelocationDaysWheeled)
                 : Math.Max(0, inp.RelocationDaysTracked);
        if (L.LastUnit.Length == 0) return 0;                     // 本月第一站不算转场
        if (site.TryGetValue(L.LastUnit, out var a) && site.TryGetValue(u.UnitId, out var b))
        {
            double dx = a.Cx - b.Cx, dy = a.Cy - b.Cy, dz = a.Cz - b.Cz;
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= Math.Max(0, inp.FreeRelocationM)) return 0;
        }
        return cost;
    }

    // ──────────────────────────────────────────────────────────────────
    //  编组
    // ──────────────────────────────────────────────────────────────────

    private sealed class Pairing
    {
        /// <summary>挖装型号 → (卡车型号 → 规则)。</summary>
        public readonly Dictionary<string, Dictionary<string, FleetPairing>> ByLoader = new(StringComparer.Ordinal);
        public bool Empty => ByLoader.Count == 0;
    }

    private static Pairing BuildPairing(List<FleetPairing>? src, List<string> notes)
    {
        var p = new Pairing();
        int bad = 0;
        foreach (var f in src ?? new List<FleetPairing>())
        {
            if (f == null || f.LoaderModel.Length == 0 || f.TruckModel.Length == 0) { bad++; continue; }
            if (f.TruckCount <= 0) { bad++; continue; }
            if (!p.ByLoader.TryGetValue(f.LoaderModel, out var d))
                p.ByLoader[f.LoaderModel] = d = new Dictionary<string, FleetPairing>(StringComparer.Ordinal);
            d[f.TruckModel] = f;
        }
        if (bad > 0) notes.Add($"⚠ {bad} 条编组规则缺型号或推荐台数非正，已跳过。");
        if (p.Empty) notes.Add("· 没有编组规则，全部按【缺省配比 + 任意卡车】处理。");
        return p;
    }

    /// <summary>一天配到的车组：<b>车 + 本该配几台 + 是不是混编</b>。三个一起走 ——
    /// 只回车列表的话，「配够了」和「只剩这么多」就分不出来了。</summary>
    private readonly struct Crew
    {
        public readonly List<Cal> Trucks;
        /// <summary>按口径<b>本该</b>配几台（不是实际拿到几台）。</summary>
        public readonly int Wanted;
        /// <summary>这一组混了几种车型 —— <b>没有哪条规则说过这种组合</b>。</summary>
        public readonly bool Mixed;
        public Crew(List<Cal> t, int want, bool mixed) { Trucks = t; Wanted = want; Mixed = mixed; }
    }

    /// <summary>
    /// 给一台铲配当天的车。
    ///
    /// <para><b>为什么先选车型再定台数</b>：<c>dispatch_rule.recommended_truck_count</c> 是对
    /// <b>某一对 (铲型, 车型)</b> 说的（现场 40 条规则里，同一台铲对不同车型是 3 台或 4 台）。
    /// 把几种车型混在一起、再取各条规则的最大值，拼出来的是一条<b>谁也没写过的规则</b> ——
    /// 数出得来、每一项校核都是 ✓，而它对应不上现场任何一种编组。
    /// 所以这里：优先<b>单一车型</b>成组，按那个车型自己的推荐台数配；
    /// 实在没有哪个车型能单独凑够，才混编，并<b>单独计数</b>（<see cref="EquipmentAssignResult.MixedModelCrewDays"/>）。</para>
    ///
    /// <para>编组规则是<b>硬约束</b>：规则里没写的车型不给这台铲用。没有规则时才任意车型。</para>
    /// </summary>
    private static Crew PickCrew(List<Cal> trucks, Cal L, Pairing p, int day, EquipmentAssignInput inp,
                                 UnitFacePin? pin = null)
    {
        p.ByLoader.TryGetValue(L.M.Model, out var rule);
        bool hasRule = rule != null && rule.Count > 0;

        // 面上钉了车型 ⇒ 它比编组规则更硬（人明确指定过），且此时不必再谈"混编"。
        string pinTruck = pin?.TruckModel ?? "";

        var free = new List<Cal>();
        foreach (var t in trucks)
        {
            if (!t.Free(day)) continue;
            if (pinTruck.Length > 0)
            {
                if (!string.Equals(t.M.Model, pinTruck, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (hasRule && !rule!.ContainsKey(t.M.Model)) continue;  // 编组规则是硬约束
            free.Add(t);
        }
        if (free.Count == 0) return new Crew(new List<Cal>(), 0, false);

        // 面级覆盖的配车数：填了就照它配，并且【这件事要能在结果里看见】——
        // 覆盖悄悄生效的话，报表上写着"按编组规则配"，而实际用的是另一个数。
        int pinN = pin?.TrucksPerLoader ?? 0;
        if (pinN > 0)
        {
            var byPin = free.OrderByDescending(t => t.Rate.M3PerDay)
                            .ThenBy(t => t.M.MachineId, StringComparer.Ordinal)
                            .Take(pinN).ToList();
            return new Crew(byPin, pinN, Distinct(byPin) > 1);
        }

        // ① 台效配平：要几台由「铲台效 ÷ 车台效」定，车型不限（只要编得上组）
        if (inp.TruckSizing == TruckSizing.ByRateBalance)
        {
            double tr = RateBook.Median(trucks.Select(t => t.Rate.M3PerDay));
            int wantB = tr > Eps ? Math.Max(1, (int)Math.Ceiling(L.Rate.M3PerDay / tr))
                                 : Math.Max(1, inp.DefaultTrucksPerLoader);
            var byRate = free.OrderByDescending(t => t.Rate.M3PerDay)
                             .ThenBy(t => t.M.MachineId, StringComparer.Ordinal)
                             .Take(wantB).ToList();
            return new Crew(byRate, wantB, Distinct(byRate) > 1);
        }

        // ② 现场编组规则：先选车型，再按该车型自己的推荐台数配
        if (hasRule)
        {
            var byModel = free.GroupBy(t => t.M.Model, StringComparer.Ordinal)
                              .Select(g => (Model: g.Key, N: g.Count(), Rule: rule![g.Key], List: g.ToList()))
                              .ToList();
            var enough = byModel.Where(x => x.N >= x.Rule.TruckCount)
                                .OrderByDescending(x => x.Rule.EfficiencyScore)
                                .ThenByDescending(x => x.List.Sum(t => t.Rate.M3PerDay) / x.N)
                                .ThenBy(x => x.Model, StringComparer.Ordinal)
                                .ToList();
            if (enough.Count > 0)
            {
                var w = enough[0];
                var pick = w.List.OrderByDescending(t => t.Rate.M3PerDay)
                                 .ThenBy(t => t.M.MachineId, StringComparer.Ordinal)
                                 .Take(w.Rule.TruckCount).ToList();
                return new Crew(pick, w.Rule.TruckCount, false);
            }

            // 没有哪个车型能单独凑够 —— 混编，如实标记
            int wantM = byModel.Max(x => x.Rule.TruckCount);
            var mix = free.OrderByDescending(t => rule![t.M.Model].EfficiencyScore)
                          .ThenByDescending(t => t.Rate.M3PerDay)
                          .ThenBy(t => t.M.MachineId, StringComparer.Ordinal)
                          .Take(wantM).ToList();
            return new Crew(mix, wantM, Distinct(mix) > 1);
        }

        // ③ 没有编组规则：缺省配比、任意车型
        int wantD = Math.Max(1, inp.DefaultTrucksPerLoader);
        var any = free.OrderByDescending(t => t.Rate.M3PerDay)
                      .ThenBy(t => t.M.MachineId, StringComparer.Ordinal)
                      .Take(wantD).ToList();
        return new Crew(any, wantD, Distinct(any) > 1);
    }

    private static int Distinct(List<Cal> c)
        => c.Select(x => x.M.Model).Distinct(StringComparer.Ordinal).Count();

    // ──────────────────────────────────────────────────────────────────
    //  合并 / 体检
    // ──────────────────────────────────────────────────────────────────

    /// <summary>把逐日记账合并成<b>连续段</b>。段内每一天都真占着 —— 中间有空档就断开，
    /// 这样区间就是占用本身，判据可以直接扫区间而不必再问引擎。</summary>
    private static void Emit(List<Booking> src, EquipmentAssignResult r)
    {
        foreach (var g in src.GroupBy(b => (b.MachineId, b.UnitId, b.Role, b.Serves))
                             .OrderBy(g => g.Key.MachineId, StringComparer.Ordinal))
        {
            var days = g.OrderBy(b => b.Day).ToList();
            int i = 0;
            while (i < days.Count)
            {
                int j = i;
                while (j + 1 < days.Count && days[j + 1].Day == days[j].Day + 1) j++;
                var head = days[i];
                double m3 = 0; bool lim = false;
                for (int k = i; k <= j; k++) { m3 += days[k].M3; lim |= days[k].HaulLimited; }
                int nd = days[j].Day - days[i].Day + 1;
                var a = new MachineAssignment
                {
                    UnitId = head.UnitId, UnitKind = head.UKind, Seq = head.Seq,
                    MachineId = head.MachineId, MachineKind = head.Kind, Model = head.Model,
                    Role = head.Role, ServesMachineId = head.Serves,
                    StartDay = days[i].Day, EndDay = days[j].Day,
                    Shifts = head.Role == MachineRole.Relocate ? 0 : nd * r.ShiftsPerDay,
                    AssignedM3 = m3,
                    RateM3PerDay = head.Rate, RateSource = head.Src, RateRecord = head.Key,
                    // ★ 不夹到 0 —— 夹了「派的量超过台效」那条判据就永远红不了（见 SpareCapM3 的说明）
                    //   三个出量的角色都要算：只算挖装的话，钻机/推土机派超台效不会红。
                    SpareCapM3 = head.Role is MachineRole.Excavate or MachineRole.Drill or MachineRole.Dump
                                 ? nd * head.Rate - m3 : 0,
                    HaulLimited = lim,
                };
                r.Assignments.Add(a);
                // 台效来源分布逐角色分开记 —— 混在一起的话，
                // 「钻机台效全走缺省」会被挖装那一大堆实测笔淹掉，看不出来。
                var hist = a.Role switch
                {
                    MachineRole.Excavate => r.RateSourceHist,
                    MachineRole.Drill => r.DrillRateHist,
                    MachineRole.Dump => r.DozeRateHist,
                    _ => null,
                };
                if (hist != null)
                    hist[a.RateSource] = (hist.TryGetValue(a.RateSource, out int c) ? c : 0) + 1;
                i = j + 1;
            }
        }
        r.Assignments.Sort((x, y) =>
        {
            int c = x.StartDay.CompareTo(y.StartDay); if (c != 0) return c;
            c = x.Seq.CompareTo(y.Seq); if (c != 0) return c;
            c = ((int)x.Role).CompareTo((int)y.Role); if (c != 0) return c;
            return string.CompareOrdinal(x.MachineId, y.MachineId);
        });
    }

    /// <summary>
    /// 台效<b>量级</b>体检。挖装设备的日台效落在合理带外就报。
    /// <para><b>为什么必须有这一条</b>：台效大一个量级，全月的量随便就派完了、一条欠产都不报，
    /// 而「设备够不够」正是这个模块唯一要回答的问题 —— 一条从不触发的能力上限等于没有上限。</para>
    /// </summary>
    private static string AuditScale(List<Cal> loaders, EquipmentAssignInput inp)
    {
        if (!(inp.AuditLoaderDayLo > 0) || !(inp.AuditLoaderDayHi > inp.AuditLoaderDayLo)) return "";
        if (loaders.Count == 0) return "";
        var v = loaders.Select(l => l.Rate.M3PerDay).OrderBy(x => x).ToList();
        double med = RateBook.Median(v);
        if (med >= inp.AuditLoaderDayLo && med <= inp.AuditLoaderDayHi) return "";
        double k = med / ((inp.AuditLoaderDayLo + inp.AuditLoaderDayHi) * 0.5);
        return $"挖装设备日台效中位数 {med:0}m³/台·日，在合理带 [{inp.AuditLoaderDayLo:0}, {inp.AuditLoaderDayHi:0}] 之外"
             + $"（约合理中值的 {k:0.#} 倍，{v.Count} 台，{v[0]:0}…{v[^1]:0}）。"
             + "先核对台效来源的口径（月量÷作业日？是实方还是控制方量？），别急着当设备很强 —— "
             + "口径大一个量级，欠产就永远报不出来。核对完在 CapacityScale 一处改，别在适配器里再乘一遍。";
    }

    /// <summary>
    /// 面级型号约束的落地情况。<b>三个数一起报</b>：钉了几条、其中几条那个型号压根没有可派设备、
    /// 排出来的班表里有几条真按它走了。
    /// <para>只报"钉了 N 条"是不够的 —— 「钉了」与「钉了但一台都挑不到」结果差别很大
    /// （后者直接欠产），而界面上长得一模一样。</para>
    /// </summary>
    private static void ReportPins(EquipmentAssignInput inp, EquipmentAssignResult r,
                                   List<Cal> loaders, List<Cal> trucks, List<Cal> drills, List<Cal> dozers,
                                   bool wantDrill, bool wantDoze)
    {
        var pins = (inp.Pins ?? new Dictionary<string, UnitFacePin>(StringComparer.Ordinal))
                   .Where(kv => kv.Value != null && kv.Value.Any).ToList();
        if (pins.Count == 0) return;

        var loaderModels = loaders.Select(c => c.M.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var truckModels = trucks.Select(c => c.M.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drillModels = drills.Select(c => c.M.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dead = new List<string>();
        int dozerPins = 0;
        foreach (var kv in pins)
        {
            var p = kv.Value;
            string who = p.FaceName.Length > 0 ? $"{p.FaceName}·{kv.Key}" : kv.Key;
            if (p.LoaderModel.Length > 0 && !loaderModels.Contains(p.LoaderModel))
                dead.Add($"{who} 采装钉「{p.LoaderModel}」但可派挖装设备里没有这个型号");
            if (p.TruckModel.Length > 0 && !truckModels.Contains(p.TruckModel))
                dead.Add($"{who} 运输钉「{p.TruckModel}」但可派卡车里没有这个型号");
            if (wantDrill && p.DrillModel.Length > 0 && !drillModels.Contains(p.DrillModel))
                dead.Add($"{who} 穿孔钉「{p.DrillModel}」但可派钻机里没有这个型号");
            if (p.DozerModel.Length > 0) dozerPins++;
        }

        // 排出来的班表里，有几个单元的设备确实是钉的那个型号
        int bound = 0;
        foreach (var kv in pins)
        {
            var p = kv.Value;
            if (p.LoaderModel.Length == 0) continue;
            if (r.Excavation.Any(a => string.Equals(a.UnitId, kv.Key, StringComparison.Ordinal)
                                   && string.Equals(a.Model, p.LoaderModel, StringComparison.OrdinalIgnoreCase)))
                bound++;
        }

        r.Notes.Add($"· 面级型号约束：{pins.Count} 个单元钉了型号，其中采装型号真落到班表上的 {bound} 个。"
                  + "钉了就是<b>硬约束</b> —— 挑不到就欠产，引擎不会悄悄换一台别的型号顶上。");
        foreach (var d in dead.Take(6))
            r.Notes.Add("◆ " + d + " —— 这个单元会因此欠产，根因在「确定开采程序」里选错了型号，不是设备不够。");
        if (dead.Count > 6)
            r.Notes.Add($"◆ 还有 {dead.Count - 6} 条同类的型号约束挑不到设备（只列前 6 条）。");

        if (dozerPins > 0 && wantDoze)
            r.Notes.Add($"· {dozerPins} 个单元钉了推土机型号，但<b>本次没有生效</b>："
                      + "排土的作业对象是【去向】不是单元，同一个排土位置当天可能收着好几个单元的料，"
                      + "各自钉的型号不一定一致 —— 按谁的算都是编的。要约束排土设备，"
                      + "该在<b>去向</b>上钉，不是在作业面上钉。");
    }

    /// <summary>
    /// 钻机 / 推土机的日台效量级体检。<b>各用各的合理带</b> ——
    /// 拿挖装那条带去判钻机是错的：一个是原位实方、一个是控制方量，本来就不是一个量。
    /// </summary>
    private static string AuditBand(List<Cal> pool, double lo, double hi, string what, string basis)
    {
        if (!(lo > 0) || !(hi > lo) || pool.Count == 0) return "";
        var v = pool.Select(x => x.Rate.M3PerDay).OrderBy(x => x).ToList();
        double med = RateBook.Median(v);
        if (med >= lo && med <= hi) return "";
        return $"{what}日台效中位数 {med:0}m³({basis})/台·日，在合理带 [{lo:0}, {hi:0}] 之外"
             + $"（{v.Count} 台，{v[0]:0}…{v[^1]:0}）。先核口径再看结论 —— "
             + $"台效大一个量级，{what}的欠产就永远报不出来。";
    }

    /// <summary>编组规则给的车数 vs 台效配平出来的车数 —— 两个数差太多就报，<b>不替用户选</b>。</summary>
    private static void CrossCheckTruckRatio(List<Cal> loaders, List<Cal> trucks, Pairing p,
                                             EquipmentAssignInput inp, EquipmentAssignResult r)
    {
        if (loaders.Count == 0 || trucks.Count == 0) return;
        double lr = RateBook.Median(loaders.Select(l => l.Rate.M3PerDay));
        double tr = RateBook.Median(trucks.Select(t => t.Rate.M3PerDay));
        if (lr <= Eps || tr <= Eps) return;
        int balanced = Math.Max(1, (int)Math.Ceiling(lr / tr));
        int ruleN = p.Empty ? Math.Max(1, inp.DefaultTrucksPerLoader)
                            : (int)Math.Round(p.ByLoader.Values.SelectMany(d => d.Values).Average(f => f.TruckCount));
        string used = inp.TruckSizing == TruckSizing.ByRateBalance ? "台效配平" : "现场编组规则";
        r.Notes.Add($"· 卡车配比：现场编组规则 ≈{ruleN} 台/铲，按台效配平要 {balanced} 台/铲"
                  + $"（铲 {lr:0} vs 车 {tr:0} m³/日）。本次用的是【{used}】。");
        if (Math.Abs(balanced - ruleN) > Math.Max(1, ruleN * 0.5))
            r.Notes.Add($"◆ 这两个数差了 {(double)balanced / Math.Max(1, ruleN):0.#} 倍 —— "
                      + "编组规则和台效两份数据对不上口径，<b>引擎不替你选</b>。"
                      + $"全矿口径：挖装 {loaders.Count} 台 × {balanced} = 需车 {loaders.Count * balanced} 台，在册可派卡车 {trucks.Count} 台。");
    }

    private static string[] NewCal(int H)
    {
        var a = new string[H + 1];
        for (int i = 0; i <= H; i++) a[i] = "";
        return a;
    }

    private static string KindName(MachineKind k) => k switch
    {
        MachineKind.Shovel => "电铲",
        MachineKind.Truck => "卡车",
        MachineKind.Drill => "钻机",
        MachineKind.Loader => "前装机",
        MachineKind.Dozer => "推土机",
        MachineKind.Grader => "平路机",
        MachineKind.WaterTruck => "洒水车",
        _ => "其他",
    };

    /// <summary>逐设备甘特行（派车单/图表直接吃）：设备 → 按起始天排好的指派。</summary>
    public static Dictionary<string, List<MachineAssignment>> ByMachine(EquipmentAssignResult r)
        => (r?.Assignments ?? new List<MachineAssignment>())
           .GroupBy(a => a.MachineId, StringComparer.Ordinal)
           .ToDictionary(g => g.Key, g => g.OrderBy(a => a.StartDay).ToList(), StringComparer.Ordinal);

    /// <summary>导出成 CSV（一行一笔）。<b>表头带「计划」两个字</b> —— 这份表进不了实绩台账。</summary>
    public static string ToCsv(EquipmentAssignResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 设备指派【计划】—— 非实绩，不得回写 capacity_monthly / equipment_kpi_monthly");
        // ★「量口径」这一列不是装饰：同一张表里并排着原位实方 / 控制方量 / 排弃占容三种体积，
        //   少了它，下游拿去一 SUM 就得到一个不对应任何真实量的数，而且不会有任何东西报错。
        sb.AppendLine("作业对象,类别,推进序,设备,型号,设备类别,角色,量口径,服务于,起日,止日,工日,台班,计划量m3,日台效m3,台效来源,台效溯源,末日余能m3,运力受限");
        foreach (var a in r?.Assignments ?? new List<MachineAssignment>())
            sb.AppendLine(string.Join(",", new[]
            {
                Q(a.UnitId), a.UnitKind == UnitKind.Coal ? "煤" : "岩", a.Seq.ToString(CultureInfo.InvariantCulture),
                Q(a.MachineId), Q(a.Model), KindName(a.MachineKind),
                EquipmentAssignResult.RoleName(a.Role),
                a.Role switch
                {
                    MachineRole.Drill => "控制方量",
                    MachineRole.Dump => "排弃占容(V实×Kr)",
                    MachineRole.Relocate => "—",
                    _ => "原位实方",
                },
                Q(a.ServesMachineId),
                a.StartDay.ToString(CultureInfo.InvariantCulture), a.EndDay.ToString(CultureInfo.InvariantCulture),
                a.Days.ToString(CultureInfo.InvariantCulture), a.Shifts.ToString("0.#", CultureInfo.InvariantCulture),
                a.AssignedM3.ToString("0.##", CultureInfo.InvariantCulture),
                a.RateM3PerDay.ToString("0.##", CultureInfo.InvariantCulture),
                RateBook.Label(a.RateSource), Q(a.RateRecord),
                a.SpareCapM3.ToString("0.##", CultureInfo.InvariantCulture),
                a.HaulLimited ? "是" : "",
            }));
        return sb.ToString();
    }

    private static string Q(string s)
        => s.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}
