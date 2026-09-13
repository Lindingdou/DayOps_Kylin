using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Transport;

namespace PitMine3D.Kylin.Cad.RoadLayout;

/// <summary>
/// 「运量驱动布线」求解器(波4)——把 <see cref="RoadLayoutPlanner"/> 的单线方案升级为
/// **多线 + 成本 + 多方案比选**(对应设计文档第七条 F1/F2/F5)。
///
/// 算法口径(诚实标注):现 <see cref="RampRouteGenerator"/> 同一级的多个候选**展线长与纵坡相同**
/// (RequiredLengthM = ΔH/i,与起坡位置无关),差别在起坡位置、可布走廊长、展线形式与跨帮段数;
/// 故真正的"网络流择路"仍无路可择(每级择哪条都不改运距)——
/// 本求解器每级取一条串成单链(见 <see cref="PickChainPerLevel"/>,禁布区在那里生效),补的是文档 §5 点名的"单线→多线":
///   运量需求 ÷ 单车道运力 → 总车道数 → 按"单线车道上限"拆成若干**并行坑线**,
///   各方案在"少线宽路 / 多线窄路 / 单线基线"间权衡,算运营成本与基建工程量代理,
///   按 <see cref="Objectives"/> 四目标打分比选,Schemes[0]=推荐。
/// 运营成本按**逐条 OD 的实际运距**加权(见 <see cref="BuildOdHauls"/>):源所在水平决定坑内爬升段、
/// 卸载点位置决定地表段 —— 不再是"所有吨量乘同一条链长"。但三方案共用同一条链与同一批 OD,
/// 该值三者必然相同,故只作绝对量参考、不参与排序(见 <see cref="RoadLayoutScheme.TotalHaulCost"/>)。
/// 待richer候选(跨多帮绕行/并行走廊,见 RampRouteGenerator TODO④)落地后,可在此换真最小费用流择路。
/// 压矿(F6)仍属块体侧,留 0 且不参与打分。
/// </summary>
public sealed class RoadLayoutSolver : IRoadLayoutSolver
{
    /// <summary>单条坑线车道上限(超之须加并行坑线)。</summary>
    public const int MaxLanesPerRoad = 4;

    // ── 优化目标口径:唯一定义处 ────────────────────────────────────────────
    // 约束对话框下拉、比选窗下拉、下方 Score() 一律引用这一份,禁止各处自己硬编码字符串数组
    // (历史教训:对话框曾给"最短运距 / 最小基建工程量"三项、比选窗另给四项、Score 只认两项,
    //  选了对不上的目标就静默落 default,用户看不出求解器根本没照他选的算)。
    /// <summary>目标·最小总成本(默认):运营 + 基建;同链下运营恒等,差别全在基建工程量。</summary>
    public const string ObjMinCost = "最小总成本";
    /// <summary>目标·最小运输功:只认吨公里(同一条链时各方案相同,详见 <see cref="Score"/>)。</summary>
    public const string ObjMinWork = "最小运输功";
    /// <summary>目标·均衡:兼顾基建工程量、车道利用率与并行线分流冗余。</summary>
    public const string ObjBalanced = "均衡";
    /// <summary>目标·最小压矿:压矿量属块体侧未接(恒 0),退化为"坑线足迹最小"。</summary>
    public const string ObjMinSterilized = "最小压矿";

    /// <summary>四项优化目标(顺序即下拉顺序,[0] 为默认)。UI 与求解器共用,改这里即全线生效。</summary>
    public static readonly string[] Objectives = { ObjMinCost, ObjMinWork, ObjBalanced, ObjMinSterilized };

    /// <summary>
    /// 目标归一:把外来串收敛到 <see cref="Objectives"/> 之一,免持久化里的历史串静默落 default。
    /// 认不出的(含旧配置里的"最短运距 / 最小基建工程量")一律回 [0] —— 与 TransportConstraintDialog
    /// 载入时的回落口径一致,免"对话框显示 A、比选窗按 B 算"。
    /// </summary>
    public static string NormalizeObjective(string? objective) => objective?.Trim() switch
    {
        ObjMinCost => ObjMinCost,
        ObjMinWork => ObjMinWork,
        ObjBalanced => ObjBalanced,
        ObjMinSterilized => ObjMinSterilized,
        _ => Objectives[0],
    };

    private readonly IRampRouteGenerator _gen;

    public RoadLayoutSolver(IRampRouteGenerator? generator = null)
        => _gen = generator ?? new RampRouteGenerator();

    public RoadLayoutResult Solve(RoadLayoutInput input)
    {
        if (input == null)
            return new RoadLayoutResult { Success = false, Error = "input 为空" };

        var candidates = input.Candidates ?? _gen.Generate(input);
        if (candidates.Count == 0)
            return new RoadLayoutResult { Success = false, Error = "无候选坑线段(检查台阶线 / 非工作帮)" };

        var cons = input.Constraints ?? new TransportConstraintSettings();

        // 每级择一条 → 串成"一条链"。选线器每对相邻台阶产出多个**位置不同**的候选
        // (RampRouteGenerator.CandidatesPerLevel),整表直接串起来会把展线长/运距翻同样倍数
        // (见 RampRouteGenerator.GroupByLevelPair 的说明)。禁布区(input.NoGoZones)也在这一步生效。
        var chainWarnings = new List<string>();
        var chain = PickChainPerLevel(candidates, input.NoGoZones, chainWarnings);
        double perLane = MinPerLaneCapacity(chain);

        // OD 运距:显式 Demands 优先,否则按"矿→破碎/储矿、岩→排土"就近从源/汇配出。
        // 总运量与吨公里都由这一批 OD 汇总而来 —— 各源所在水平不同 → 坑内运距不同,去向不同 → 地表段不同,
        // 不再"所有吨量乘同一条链长"。三方案共用同一条链与同一批 OD,故本值三者相同(见 TotalHaulCost 说明)。
        var hauls = BuildOdHauls(input, chain, chainWarnings);
        double demand = 0, tonKm = 0;
        foreach (var h in hauls) { demand += h.Tons; tonKm += h.Tons * (h.TotalM / 1000.0); }
        double haulCost = tonKm * cons.HaulUnitCost;

        // 三套布置策略(同一条链,不同线/道权衡)供比选。
        var schemes = new List<RoadLayoutScheme>
        {
            BuildScheme(chain, demand, perLane, haulCost, chainWarnings, cons, "方案1·紧凑(少线宽路)", MaxLanesPerRoad, forceSingle: false),
            BuildScheme(chain, demand, perLane, haulCost, chainWarnings, cons, "方案2·均衡(多线窄路)", 2, forceSingle: false),
            BuildScheme(chain, demand, perLane, haulCost, chainWarnings, cons, "方案3·单线(基线)", int.MaxValue, forceSingle: true),
        };

        // 打分:目标先归一(免历史串静默落 default);基准取**可行方案里最省的**基建工程量,
        // 把绝对量折成相对分 —— 免长链下 100/(1+capexKm) 饱和成一片、方案间分不出高下。
        string objective = NormalizeObjective(cons.Objective);
        double bestCapexM = schemes.Where(s => s.Feasible).Select(s => s.TotalCapexProxy)
                                   .DefaultIfEmpty(0.0).Min();
        foreach (var s in schemes) s.TotalScore = Score(s, objective, bestCapexM);

        // 排序:可行优先,再按评分高→低;[0] 为推荐。去重(单线与紧凑在小运量下可能等价)。
        var ordered = schemes
            .GroupBy(s => (s.Lines.Count, s.Feasible))
            .Select(g => g.First())
            .OrderByDescending(s => s.Feasible)
            .ThenByDescending(s => s.TotalScore)
            .ToList();

        return new RoadLayoutResult { Success = true, Schemes = ordered };
    }

    /// <summary>按"每线车道上限"把总运量拆成若干并行坑线,组装一个方案 + 算成本。</summary>
    /// <param name="haulCost">运营成本 元/期,由 <see cref="Solve"/> 按逐条 OD 运距统一算好传入(三方案同值,见其字段说明)。</param>
    /// <param name="chainWarnings">
    /// 择路 + 配 OD 阶段的告警(某级候选全被禁布区挡掉、某批吨量找不到卸载点…),先入本方案 violations。
    /// 注意本类"violations 非空 ⇒ 不可行"的既有口径:这批告警会把方案判成不可行 —— 宁可保守,
    /// 也不让"运距按 0 计"的成本冒充可用结果(要拆成"告警 / 不可行"两档得先给 RoadLayoutScheme 加字段)。
    /// </param>
    private static RoadLayoutScheme BuildScheme(
        IReadOnlyList<RampCandidate> candidates, double demand, double perLane, double haulCost,
        IReadOnlyList<string> chainWarnings,
        TransportConstraintSettings cons, string name, int maxLanesPerLine, bool forceSingle)
    {
        var violations = new List<string>(chainWarnings);

        // 链几何:展线总长 + 逐段(线路间共用同一条链几何,差在车道数)。
        double chainLenM = 0;
        foreach (var c in candidates)
        {
            chainLenM += c.RequiredLengthM;
            if (!c.GeomFeasible) violations.Add($"{c.FromLevel:0}→{c.ToLevel:0}m: {c.Note}");
        }

        // 运量驱动定总车道:总车道 = ⌈需求 / 单车道运力⌉。
        int totalLanes = (perLane > 1e-6 && demand > 0) ? (int)Math.Ceiling(demand / perLane)
                                                        : Math.Max(1, cons.LaneCount);

        // 拆线:单线基线=1 条扛全部车道;否则按每线上限拆成 ⌈总车道/每线上限⌉ 条。
        int nLines = forceSingle ? 1 : Math.Max(1, (int)Math.Ceiling((double)totalLanes / maxLanesPerLine));
        var lines = new List<RoadLine>();
        int lanesLeft = totalLanes;
        double tonsLeft = demand;
        for (int i = 0; i < nLines; i++)
        {
            int linesRemaining = nLines - i;
            int laneOfThis = forceSingle ? totalLanes
                                         : (int)Math.Ceiling((double)lanesLeft / linesRemaining);
            laneOfThis = Math.Max(1, laneOfThis);
            lanesLeft -= laneOfThis;
            double cap = laneOfThis * perLane;
            // 吨量按车道比例分(末线收尾),保各线利用率一致、不超容量。
            double tons = linesRemaining == 1
                ? tonsLeft
                : demand * (laneOfThis / (double)Math.Max(1, totalLanes));
            tonsLeft -= tons;

            lines.Add(new RoadLine
            {
                Id = $"L{i + 1}",
                LevelSequence = BuildLevelSeq(candidates),
                LaneCount = laneOfThis,
                CapacityTons = cap,
                AssignedTons = tons,
                Utilization = cap > 1e-6 ? tons / cap : 0,
                LengthM = chainLenM,
                Segments = BuildSegments(candidates),
            });
        }

        double totalCap = lines.Sum(l => l.CapacityTons);
        bool capacityOk = totalCap + 1e-6 >= demand;
        if (!capacityOk)
            violations.Add($"运力缺口:需 {demand:0} t/期,仅供 {totalCap:0} t/期");
        if (forceSingle && totalLanes > MaxLanesPerRoad)
            violations.Add($"单线需 {totalLanes} 车道(＞上限 {MaxLanesPerRoad}),应加并行坑线");
        foreach (var l in lines)
            if (l.LaneCount > MaxLanesPerRoad)
                violations.Add($"线路 {l.Id} 车道 {l.LaneCount} 超上限 {MaxLanesPerRoad}");

        // 成本(两项口径,如实标注可比性):
        //   · 运营 = Σ(各 OD 吨量 × 该 OD 运距 km) × 运输单价 —— 运距已按 OD 逐条量(坑内爬升段 + 地表直线段),
        //     由 Solve 统一算好传进来。三方案共用同一条链几何与同一批 OD → **数值必然相同**,排不出先后;
        //     不因此编造差额,改由 Score 用可区分项排序(见 Score 说明)。
        //   · 基建代理 = 各线展线长之和 = 线数 × 单链长 —— **随并行坑线条数线性增长**,是三方案的真实差别。
        double capexProxy = lines.Sum(l => l.LengthM);

        return new RoadLayoutScheme
        {
            Name = name,
            Lines = lines,
            Feasible = violations.Count == 0,
            Violations = violations,
            TotalHaulCost = haulCost,
            TotalCapexProxy = capexProxy,
            SterilizedOreTons = 0,   // 压矿属块体侧,现恒 0(未接)——不参与打分,免造出假区别
            TotalScore = 0,          // 由 Solve 统一打分
        };
    }

    /// <summary>
    /// 方案评分(0~100,高者优)。不可行直接 0,其余按目标对**三项真实可比量**加权:
    ///   ① 基建工程量 —— 展线总长(<see cref="RoadLayoutScheme.TotalCapexProxy"/> = 线数 × 单链长),越省越高;
    ///      相对本批最省方案归一(<paramref name="bestCapexM"/>),免绝对分在长链下饱和。
    ///   ② 车道利用率 —— 越接近 1 越不浪费。注:现行"按车道比例分吨量 + 总车道由需求唯一确定"口径下,
    ///      同一批方案的利用率通常相同,它表征"选型贴不贴合需求",不是方案间的区分量。
    ///   ③ 分流冗余 —— 并行坑线条数(单线堵断即全矿停运)。这是对"线数"这一**事实**的偏好权重、非实测量,
    ///      故封顶(1 线 0 分,2 线 50,3 线 67…)且各目标权重不同。
    /// **两项如实不参与打分**(免比选窗显示看似有别、实则编造的差额):
    ///   · 运营成本 / 运输功(<see cref="RoadLayoutScheme.TotalHaulCost"/>)= Σ(各 OD 吨量 × 该 OD 运距) × 单价 ——
    ///     运距虽已按 OD 逐条量(比"所有吨量乘同一条链长"准),但三方案共用同一条链几何与同一批 OD
    ///     → 数值仍完全相同,排不出先后;
    ///   · 压矿(<see cref="RoadLayoutScheme.SterilizedOreTons"/>)属块体侧,现恒 0(未接),
    ///     故「最小压矿」退化为「坑线足迹最小」,以展线总长作足迹代理,不拿 0 当真值参与加权。
    /// </summary>
    /// <param name="bestCapexM">本批**可行**方案里最小的基建工程量 m(归一基准;无可行方案时给 0)。</param>
    private static double Score(RoadLayoutScheme s, string? objective, double bestCapexM)
    {
        if (!s.Feasible) return 0;

        // ① 基建:相对最省方案的比值(最省 = 100 分)。退化(链长 0)时不加区分,同给满分。
        double capexScore = (s.TotalCapexProxy > 1e-6 && bestCapexM > 1e-6)
            ? 100.0 * bestCapexM / s.TotalCapexProxy
            : 100.0;
        // ② 利用率:各线均值(超 1 已由可行性判据拦下,此处仍夹紧免异常值放大分数)。
        double avgUtil = s.Lines.Count > 0 ? s.Lines.Average(l => l.Utilization) : 0;
        double utilScore = Math.Max(0.0, Math.Min(1.0, avgUtil)) * 100.0;
        // ③ 冗余:线数越多单线堵断的影响越小,收益递减并封顶。
        double redundancyScore = 100.0 * (1.0 - 1.0 / Math.Max(1, s.Lines.Count));

        // 目标权重(和为 1):
        //   最小总成本 —— 运营恒等 → 总成本差全在基建量,重基建;
        //   最小运输功 —— 吨公里三方案相同、本目标本身分不出先后,退而按基建量 + 利用率排(如实标注);
        //   均衡       —— 兼顾基建量、利用率与分流冗余,故 4 车道单线 vs 2×2 车道会真出现翻盘;
        //   最小压矿   —— 压矿未接,以足迹(展线总长)代理,越短越好,多线不因"冗余"得分。
        (double wCapex, double wUtil, double wRedundancy) = objective switch
        {
            ObjMinCost => (0.70, 0.20, 0.10),
            ObjMinWork => (0.85, 0.15, 0.00),
            ObjBalanced => (0.30, 0.35, 0.35),
            ObjMinSterilized => (0.80, 0.20, 0.00),
            _ => (0.70, 0.20, 0.10),   // 保底 = 最小总成本(目标已由 NormalizeObjective 归一,正常走不到)
        };

        return wCapex * capexScore + wUtil * utilScore + wRedundancy * redundancyScore;
    }

    /// <summary>
    /// 每级(上→下水平)择一条候选,串成求解用的"一条链"。
    /// 同级候选的展线长/纵坡相同(择哪条都不改运距),故只取首条几何可行者;
    /// 全不可行时取首条,把它的 Note 带进 violations 让人看见(由 <see cref="BuildScheme"/> 逐段加)。
    /// 【待精化】选线器现已能让同级候选的**形式**(斜坡道/折返/螺旋)与**跨帮段数**不同,
    /// 本步却只认"首个可行",没有"优先 Straight、优先少跨帮"的偏好 —— 不假装比较过。
    ///
    /// 禁布区在本步生效:先剔掉**碰到**禁布区的候选(判据见 <see cref="RoadLayoutInput.NoGoZones"/> ——
    /// 起坡点或中心线任一采样点落入多边形内),再在剩下的里按上面的口径择一条。
    /// 某级候选被挡光时**不跳过该级**(链凭空变短 = 假的省钱):仍按首条计入运距,并出一条告警。
    /// </summary>
    /// <param name="warnings">择路阶段的告警,由调用方并进各方案的 violations。</param>
    internal static List<RampCandidate> PickChainPerLevel(
        IReadOnlyList<RampCandidate> candidates,
        IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>>? noGoZones,
        List<string> warnings)
    {
        var zones = NormalizeZones(noGoZones);
        var chain = new List<RampCandidate>();
        foreach (var group in RampRouteGenerator.GroupByLevelPair(candidates))
        {
            if (group.Count == 0) continue;

            IReadOnlyList<RampCandidate> pool = group;
            if (zones.Count > 0)
            {
                var free = new List<RampCandidate>();
                foreach (var c in group)
                    if (!HitsNoGo(c, zones)) free.Add(c);
                if (free.Count > 0) pool = free;
                else
                    warnings.Add($"{group[0].FromLevel:0}→{group[0].ToLevel:0}m: {group.Count} 条候选全部落入禁布区"
                                 + " —— 仍按首条计入运距(不跳级),本级判不可行");
            }

            var pick = pool[0];
            foreach (var c in pool)
                if (c.GeomFeasible) { pick = c; break; }
            chain.Add(pick);
        }
        return chain;
    }

    /// <summary>禁布区归一:剔掉 null / 不足 3 点的圈(构不成多边形)。回空表 = 不设禁布区。</summary>
    private static List<IReadOnlyList<(double X, double Y, double Z)>> NormalizeZones(
        IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>>? zones)
    {
        var list = new List<IReadOnlyList<(double X, double Y, double Z)>>();
        if (zones == null) return list;
        foreach (var z in zones)
            if (z != null && z.Count >= 3) list.Add(z);
        return list;
    }

    /// <summary>
    /// 候选是否碰到禁布区:起坡点或中心线**任一采样点**落入任一多边形内即算碰上。
    /// 精度 = 中心线采样步距(选线器每直腿 8 段),不加密插值、不外扩路面宽 ——
    /// 与 <see cref="RoadLayoutInput.NoGoZones"/> 标注的口径一致,是近似判定、不是面—面相交。
    /// </summary>
    private static bool HitsNoGo(RampCandidate c, List<IReadOnlyList<(double X, double Y, double Z)>> zones)
    {
        foreach (var z in zones)
        {
            if (PointInPolygonXY(c.PortalStart.X, c.PortalStart.Y, z)) return true;
            if (c.Centerline != null)
                foreach (var p in c.Centerline)
                    if (PointInPolygonXY(p.X, p.Y, z)) return true;
        }
        return false;
    }

    /// <summary>点是否落在多边形内(射线法,只用 XY,隐式闭合)。边界上的点归属不保证 —— 其量级远小于采样步距。</summary>
    private static bool PointInPolygonXY(double x, double y, IReadOnlyList<(double X, double Y, double Z)> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>一条 OD 的运量与运距(仅本求解器内部用,不外露)。</summary>
    private sealed class OdHaul
    {
        /// <summary>本条 OD 的吨量 t。</summary>
        public double Tons { get; set; }
        /// <summary>坑内段 m:该源所在水平沿本方案链爬到坑口的展线长。</summary>
        public double InPitM { get; set; }
        /// <summary>地表段 m:坑口 → 卸载点的**平面直线距离**(地表路网不由本求解器设计,只是下界)。</summary>
        public double SurfaceM { get; set; }
        /// <summary>本条 OD 的运距 m。</summary>
        public double TotalM => InPitM + SurfaceM;
    }

    /// <summary>
    /// 配 OD 并逐条量运距:显式 <see cref="RoadLayoutInput.Demands"/> 优先,
    /// 否则按"矿石→破碎站/储矿场、岩石→排土场"就近从源/汇配出。
    /// 每条运距 = 坑内段(<see cref="InPitLengthM"/>) + 地表段(坑口 → 卸载点平面直线距离)。
    ///
    /// 三条如实标注的口径:
    ///   · **吨量一条不丢**:源/汇 Id 对不上、或压根没有对应类别的汇时,该段运距按 0 计但吨量照收
    ///     —— 吨量丢了会连车道数一起算少(那是更危险的错);同时逐类出告警说明哪段是 0;
    ///   · "就近"按【坑口→汇】的平面直线距离取最小:坑内段与选哪个汇无关,故这正是本口径下运距最小的配法;
    ///   · 汇的接收能力(<see cref="UnloadingPoint.CapacityTons"/>)**不参与**:本层不做带容量的指派
    ///     (那是真最小费用流的活),不假装做过 —— 一个汇会被所有源共用。
    /// </summary>
    /// <param name="warnings">口径告警,由调用方并进各方案的 violations。</param>
    private static List<OdHaul> BuildOdHauls(
        RoadLayoutInput input, IReadOnlyList<RampCandidate> chain, List<string> warnings)
    {
        var hauls = new List<OdHaul>();

        // 链按标高从高到低排一次(选线器产出即此序,外来 Candidates 未必),坑口 = 最上一级的起坡点。
        var ordered = chain.OrderByDescending(c => c.FromLevel).ToList();
        bool hasPortal = ordered.Count > 0;
        double px = hasPortal ? ordered[0].PortalStart.X : 0.0;
        double py = hasPortal ? ordered[0].PortalStart.Y : 0.0;
        if (!hasPortal)
            warnings.Add("链上没有可用坑线段,坑口位置未知 —— 各 OD 的地表段一律按 0 计");

        var sinks = input.Sinks ?? new List<UnloadingPoint>();

        // ── ① 显式 OD:按 From/To 走,不再按矿岩重配去向(见 HaulDemand.IsOre 的说明)
        if (input.Demands != null && input.Demands.Count > 0)
        {
            var srcById = new Dictionary<string, LoadingPoint>();
            if (input.Sources != null)
                foreach (var s in input.Sources)
                    if (s != null && !string.IsNullOrEmpty(s.Id)) srcById[s.Id] = s;
            var sinkById = new Dictionary<string, UnloadingPoint>();
            foreach (var d in sinks)
                if (d != null && !string.IsNullOrEmpty(d.Id)) sinkById[d.Id] = d;

            int missSrc = 0, missSink = 0;
            foreach (var od in input.Demands)
            {
                if (od == null || od.Tons <= 0) continue;
                double inPit = 0;
                if (srcById.TryGetValue(od.FromLoadingId ?? "", out var src)) inPit = InPitLengthM(ordered, src.Level);
                else missSrc++;
                double surface = 0;
                if (sinkById.TryGetValue(od.ToUnloadingId ?? "", out var dst))
                {
                    if (hasPortal) surface = PlanarDistM(px, py, dst.X, dst.Y);
                }
                else missSink++;
                hauls.Add(new OdHaul { Tons = od.Tons, InPitM = inPit, SurfaceM = surface });
            }
            if (missSrc > 0) warnings.Add($"{missSrc} 条 OD 的装载点在源表里找不到 —— 该条坑内段按 0 计,运营成本偏低");
            if (missSink > 0) warnings.Add($"{missSink} 条 OD 的卸载点在汇表里找不到 —— 该条地表段按 0 计,运营成本偏低");
            return hauls;
        }

        // ── ② 无显式 OD:矿石去破碎站/储矿场、岩石去排土场,各取离坑口最近的一个
        var oreSinks = new List<UnloadingPoint>();
        var wasteSinks = new List<UnloadingPoint>();
        foreach (var d in sinks)
        {
            if (d == null) continue;
            if (d.Kind == UnloadKind.WasteDump) wasteSinks.Add(d); else oreSinks.Add(d);
        }
        var oreNear = NearestSink(px, py, oreSinks);
        var wasteNear = NearestSink(px, py, wasteSinks);
        double oreSurface = (oreNear != null && hasPortal) ? PlanarDistM(px, py, oreNear.X, oreNear.Y) : 0;
        double wasteSurface = (wasteNear != null && hasPortal) ? PlanarDistM(px, py, wasteNear.X, wasteNear.Y) : 0;

        double missOreTons = 0, missWasteTons = 0;
        if (input.Sources != null)
            foreach (var s in input.Sources)
            {
                if (s == null) continue;
                double inPit = InPitLengthM(ordered, s.Level);
                if (s.OreTons > 0)
                {
                    if (oreNear == null) missOreTons += s.OreTons;
                    hauls.Add(new OdHaul { Tons = s.OreTons, InPitM = inPit, SurfaceM = oreSurface });
                }
                if (s.WasteTons > 0)
                {
                    if (wasteNear == null) missWasteTons += s.WasteTons;
                    hauls.Add(new OdHaul { Tons = s.WasteTons, InPitM = inPit, SurfaceM = wasteSurface });
                }
            }
        if (missOreTons > 0) warnings.Add($"没有破碎站/储矿场可去:矿石 {missOreTons:0} t 的地表段按 0 计,运营成本偏低");
        if (missWasteTons > 0) warnings.Add($"没有排土场可去:岩石 {missWasteTons:0} t 的地表段按 0 计,运营成本偏低");
        return hauls;
    }

    /// <summary>取离坑口平面最近的一个汇;候选为空回 null(由调用方如实告警,不编一个位置出来)。</summary>
    private static UnloadingPoint? NearestSink(double px, double py, List<UnloadingPoint> pool)
    {
        UnloadingPoint? best = null;
        double bestD = double.MaxValue;
        foreach (var d in pool)
        {
            double dist = PlanarDistM(px, py, d.X, d.Y);
            if (dist < bestD) { bestD = dist; best = d; }
        }
        return best;
    }

    /// <summary>
    /// 坑内运距 m:从坑口沿本方案链下到 <paramref name="level"/> 所在水平的展线长。
    /// 逐级累加 <see cref="RampCandidate.RequiredLengthM"/>;落在某级中间时按标高比例分摊该级
    /// (该级内纵坡恒定 = i_max,故标高比即长度比)。
    /// 源高于链顶 → 0(就在坑口那一级);低于链底 → 全链长(只按已知的链算,不外推没有的台阶)。
    /// </summary>
    /// <param name="ordered">链,已按 FromLevel 从高到低排。</param>
    private static double InPitLengthM(IReadOnlyList<RampCandidate> ordered, double level)
    {
        double sum = 0;
        foreach (var c in ordered)
        {
            if (level >= c.FromLevel - 1e-6) break;          // 源在本级顶或更高 → 不再往下累
            double dH = c.FromLevel - c.ToLevel;
            if (dH <= 1e-6) continue;
            if (level <= c.ToLevel + 1e-6) { sum += c.RequiredLengthM; continue; }
            sum += c.RequiredLengthM * ((c.FromLevel - level) / dH);   // 源落在本级中间
            break;
        }
        return sum;
    }

    /// <summary>平面(XY)直线距离 m。</summary>
    private static double PlanarDistM(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double MinPerLaneCapacity(IReadOnlyList<RampCandidate> candidates)
    {
        double perLane = double.MaxValue;
        foreach (var c in candidates)
            if (c.PerLaneCapacityTons > 0) perLane = Math.Min(perLane, c.PerLaneCapacityTons);
        return perLane == double.MaxValue ? 0 : perLane;
    }

    private static List<double> BuildLevelSeq(IReadOnlyList<RampCandidate> candidates)
    {
        var levels = new List<double>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i == 0) levels.Add(candidates[i].FromLevel);
            levels.Add(candidates[i].ToLevel);
        }
        return levels;
    }

    /// <summary>
    /// 候选 → 方案段。中心线**直接消费** <see cref="RampCandidate.Centerline"/>(选线器给的沿帮展线点列),
    /// 不再只塞一个起坡点 —— 否则方案链预览(MineAssLibPlugin.HighlightRoadScheme)只画得出起坡点折线。
    /// 候选没给中心线(老调用方自造的候选)时退回 [起坡点] 单点表,行为与改前一致,不编造几何。
    /// </summary>
    private static List<RoadSegment> BuildSegments(IReadOnlyList<RampCandidate> candidates)
    {
        var segs = new List<RoadSegment>(candidates.Count);
        foreach (var c in candidates)
            segs.Add(new RoadSegment
            {
                FromLevel = c.FromLevel,
                ToLevel = c.ToLevel,
                Form = c.Form,
                GradePct = c.GradePct,
                LengthM = c.RequiredLengthM,
                TurnRadius = c.TurnRadius,
                Centerline = CenterlineOf(c),
            });
        return segs;
    }

    /// <summary>取候选的沿帮中心线;为空则退回只含起坡点的单点表(与首版口径一致)。</summary>
    internal static IReadOnlyList<(double X, double Y, double Z)> CenterlineOf(RampCandidate c)
        => c.Centerline is { Count: > 0 } cl ? cl : new List<(double, double, double)> { c.PortalStart };
}
