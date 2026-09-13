using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Transport;

namespace PitMine3D.Kylin.Cad.RoadLayout;

/// <summary>
/// 「运量驱动布线」(一键运输系统布置) 求解器接口 — 设计草案 / 占位。
///
/// 思想:把开拓运输系统当「分层图 + 网络流」——
///   采剥点 = 源(O)、卸载点 = 汇(D)、运量 = OD 需求、约束配置 = 边的可行性与容量。
///
///   给定(边界,不由求解器决定):  采剥点 + 卸载点 + 运量 + 约束(TransportConstraintSettings)
///   动态计算(求解器输出):        线路【条数】、每条坑线【位置/路径】、【车道数】、容量分配、(分期接续)
///
/// 实现见 RoadLayout/(后续):候选边生成 → 建图 → 最小费用网络流 → 校核迭代 → 多方案比选。
/// 现阶段仅有接口与 DTO,Ribbon「运量驱动布线」按钮先占位。
/// </summary>
public interface IRoadLayoutSolver
{
    /// <summary>
    /// 一键求解:源 + 汇 + 运量 + 约束 → 若干可比选方案(Schemes[0] 为推荐)。
    /// 线路条数 / 位置 / 车道全部为求解器动态计算结果。
    /// </summary>
    RoadLayoutResult Solve(RoadLayoutInput input);
}

// ───────────────────────── 输入 ─────────────────────────

/// <summary>采剥点(OD 源):某在采水平的装载点 + 该期产出。位置/产量为给定。</summary>
public sealed class LoadingPoint
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }       // 装载点世界坐标(Z = 所在水平标高)
    public double Level { get; set; }   // 所在水平标高(便于分层;一般 = Z)
    public double OreTons { get; set; } // 本期矿石产出 t
    public double WasteTons { get; set; } // 本期岩石产出 t
}

/// <summary>卸载点类别(决定接收矿石还是岩石)。</summary>
public enum UnloadKind { Crusher, WasteDump, Stockpile }

/// <summary>卸载点(OD 汇):破碎站/排土场/储矿场。可为既定位置或候选位置。</summary>
public sealed class UnloadingPoint
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public UnloadKind Kind { get; set; }
    public double CapacityTons { get; set; } // 本期最大接收能力 t(0 = 不限)
    public bool IsCandidate { get; set; }     // true = 候选(可被求解器取舍/选址)
}

/// <summary>
/// 一条运量需求(OD pair):从某采剥点到某卸载点的本期吨量。
/// 可显式给;若为空,求解器按"矿→破碎/储矿、岩→排土"就近自动从源/汇配出。
///
/// 消费口径(RoadLayoutSolver):<see cref="Tons"/> 进总运量(定车道/拆并行线),
/// <see cref="FromLoadingId"/> 决定坑内运距(该源所在水平沿链爬到坑口的那一段),
/// <see cref="ToUnloadingId"/> 决定地表运距(坑口→该汇的平面直线距离)——两段之和即本条 OD 的运距。
/// <see cref="IsOre"/> 目前**不参与求解**:显式给了 OD 就按 From/To 走,不再按矿岩重配去向;
/// 矿岩分流(不同去向/不同单价)要等成本口径分矿岩后才有得用。
/// </summary>
public sealed class HaulDemand
{
    public string FromLoadingId { get; set; } = "";
    public string ToUnloadingId { get; set; } = "";
    public double Tons { get; set; }   // 本期运量 t
    public bool IsOre { get; set; }    // true = 矿石,false = 岩石(见类注释:显式 OD 时不参与求解)
}

/// <summary>求解输入(单期一次)。</summary>
public sealed class RoadLayoutInput
{
    public IReadOnlyList<LoadingPoint> Sources { get; set; } = new List<LoadingPoint>();
    public IReadOnlyList<UnloadingPoint> Sinks { get; set; } = new List<UnloadingPoint>();
    /// <summary>OD 运量;为空则由 Sources/Sinks + 矿岩去向自动配。</summary>
    public IReadOnlyList<HaulDemand>? Demands { get; set; }
    /// <summary>约束配置(含载重→单条路年运力,定边容量)。</summary>
    public TransportConstraintSettings Constraints { get; set; } = new();
    /// <summary>台阶线(锚定几何):各水平 toe/crest polyline。选线由此生成,起坡点落在台阶线上。</summary>
    public IReadOnlyList<BenchLine> Benches { get; set; } = new List<BenchLine>();
    /// <summary>候选坑线段(可由 IRampRouteGenerator 预生成;为空则求解器内部生成)。</summary>
    public IReadOnlyList<RampCandidate>? Candidates { get; set; }
    /// <summary>
    /// 禁布区(平面多边形列表,各条为一圈顶点;隐式闭合,少于 3 点的忽略;只用 XY,Z 忽略)。
    /// 求解器每级择候选时排除**碰到**禁布区的候选(判据:该候选的起坡点或中心线任一采样点落入多边形内)。
    ///
    /// 口径如实标注:
    ///   · 判定精度 = 中心线采样步距(选线器每直腿 8 段),不做加密插值,也不算路面宽度的外扩包络
    ///     —— 是"中心线穿没穿过禁布区"的近似判定,不是精确的面—面相交;
    ///   · 某一级候选被挡光时**不会悄悄跳过该级**(链凭空变短 = 假的省钱),仍按首条计入运距并出告警判不可行;
    ///   · 要让禁布区真正参与**选线**(把禁布段从可布走廊里扣掉、换个位置重新起坡),
    ///     得并进 RampRouteGenerator.LevelCorridor 的逐格判据,本层做不到,不假装做到了。
    /// null / 空 = 不设禁布区,行为与不给此字段时完全一致(向后兼容)。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>>? NoGoZones { get; set; }
    /// <summary>
    /// 第几期(多期接续时用)。**当前未参与求解,纯预留**:求解器按"单期一次性"求解,
    /// 既不读上期已布坑线沿用/改造,也不为后期预留走廊 —— 填任何值结果都完全一样。
    /// 真接多期须先有"上期方案 → 本期输入"的接续口径(既有坑线走廊 + 分期可布区),现无。
    /// </summary>
    public int PeriodIndex { get; set; }
    /// <summary>
    /// 期标签(如 "2026")。**当前未参与求解,纯预留**,同 <see cref="PeriodIndex"/>;
    /// 仅供调用方自己标记"这份结果算的是哪一期",求解器不读。
    /// </summary>
    public string? PeriodTag { get; set; }
    // 可布区(白名单)、既有固定坑线走廊等后续补;禁布区(黑名单)见 NoGoZones。
}

// ───────────────────────── 输出 ─────────────────────────

/// <summary>坑线段:相邻水平间的一段边(几何落地后生成实体)。</summary>
public sealed class RoadSegment
{
    public double FromLevel { get; set; }
    public double ToLevel { get; set; }
    public RampForm Form { get; set; }          // 斜坡道 / 转弯坡道 / 螺旋
    public double GradePct { get; set; }
    public double LengthM { get; set; }
    public double TurnRadius { get; set; }      // 转弯/回头半径(Straight=0)
    public IReadOnlyList<(double X, double Y, double Z)> Centerline { get; set; }
        = new List<(double, double, double)>();
}

/// <summary>一条线路:串起若干水平的坑线 + 它的容量/承载/利用率。</summary>
public sealed class RoadLine
{
    public string Id { get; set; } = "";
    public IReadOnlyList<double> LevelSequence { get; set; } = new List<double>(); // 串起的水平(标高序)
    public int LaneCount { get; set; }          // 车道数(动态算)
    public double CapacityTons { get; set; }    // 该线年运力 t
    public double AssignedTons { get; set; }    // 实际承载 t
    public double Utilization { get; set; }     // = Assigned / Capacity
    public double LengthM { get; set; }
    public IReadOnlyList<RoadSegment> Segments { get; set; } = new List<RoadSegment>();
}

/// <summary>一个完整布置方案(比选用)。</summary>
public sealed class RoadLayoutScheme
{
    public string Name { get; set; } = "";
    public IReadOnlyList<RoadLine> Lines { get; set; } = new List<RoadLine>(); // 条数 = Lines.Count
    public bool Feasible { get; set; }          // 运量是否全部满足且不超容量/约束
    public IReadOnlyList<string> Violations { get; set; } = new List<string>(); // 不可行/告警明细
    /// <summary>
    /// 运营成本 元/期 = Σ(各 OD 吨量 × 该 OD 运距 km) × 运输单价。
    /// 运距逐条 OD 量:坑内段(该源所在水平沿本方案链爬到坑口,按标高比例分摊到各级)
    ///              + 地表段(坑口 → 该卸载点的**平面直线距离**;地表路网不由本求解器设计,故只是下界)。
    ///
    /// **本口径下三方案同值 —— 仅供绝对量参考,不用于比选**:三套方案共用同一条链几何、同一批 OD,
    /// 只差并行线条数与车道拆法,吨公里必然相同。RoadLayoutSolver 的打分**不取本字段**
    /// (比选看基建工程量 / 车道利用率 / 并行线分流冗余),免比选窗显示看似有别、实则编造的差额。
    /// 要让它有区分度,得等选线器出**几何真不同**的候选(并行走廊/跨帮绕行,见 RampRouteGenerator TODO④)。
    /// </summary>
    public double TotalHaulCost { get; set; }
    public double TotalCapexProxy { get; set; } // 基建工程量代理(坑线土石方等)
    /// <summary>
    /// 坑线压矿 t。**恒 0 —— 未接,不是"算出来是 0"**:压矿量要拿坑线足迹去切块体模型,属块体侧,本求解器无此输入。
    /// 故本字段**不参与打分**(否则三方案都是 0,会造出"压矿都一样"的假象);
    /// 「最小压矿」目标退化为「坑线足迹最小」,以展线总长(<see cref="TotalCapexProxy"/>)作足迹代理,详见 RoadLayoutSolver.Score。
    /// </summary>
    public double SterilizedOreTons { get; set; }
    public double TotalScore { get; set; }      // 综合评分(按 Objective)
}

/// <summary>求解结果:多方案,[0] 为推荐。</summary>
public sealed class RoadLayoutResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<RoadLayoutScheme> Schemes { get; set; } = new List<RoadLayoutScheme>();
}
