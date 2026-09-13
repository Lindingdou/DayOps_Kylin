// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/FleetMatcher.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Road;                 // HaulMetrics / TruckProfile（与运距层同一套速度口径）
using PitMine3D.Kylin.TaskLib.Domain;
using CsvDataStore = PitMine3D.Kylin.Data.Legacy.CsvDataStore;
using DispatchRule = PitMine3D.Kylin.Data.Legacy.DispatchRule;   // 注意：是 CsvDataStore 的富 POCO
                                                           // （带斗容/载重），不是 Public.Entities 的裸表实体

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  铲—车编组求解 —— 本工作包的核心。
//  理论依据：docs/日常生产组织_编组方法_理论报告.md §3~§6；落地设计：编组融合_设计.md §5。
//
//  一句话：把「这个面配几台车」从经验表变成由【物料密度 × 真运距 × 设备参数】解出的物理量。
//
//  ── 公式链（全部出自理论报告，符号沿用原文）──
//   §3.3 装车节拍   m  = W_t /(V_b · ρ_m · η_b)          每车斗数
//                   τ_L = m · t_s                        装满一车用时(min/车)
//   §3.1 循环时间   T_c = t_装 + 60·L_eq/v_重 + t_卸 + 60·L_eq/v_空 + t_调
//   §4.2 匹配系数   MF  = λ/μ = (n/T_c)/(1/τ_L) = n·τ_L / T_c
//                   MF<1 铲等车(运力瓶颈) · MF≈1 最优 · MF>1 车排队(采装瓶颈)
//   §4.3 有效产能   P_sh = 60·W_t/τ_L  ,  P_fl = n·60·W_t/T_c  ,  P = min(P_sh, P_fl)
//                   Q_g  = P/ρ · H · η
//   §4.4 利用率     ρ_sh = min(1,MF) ,  ρ_tk = MF>1 ? 1/MF : 1
//   §4.5 排队概率   P_w  = Erlang-C(c, ρ)
//   §5   最优配车   n* = T_c / τ_L，取整后按排队损失择优
//
//  ── 密度的两个口径（本文件最容易搞错的地方）──
//  物料本体给的 BlendedDensity 是【原位实方密度 ρ实】(t/m³)，但：
//   · 铲斗里装的是爆破后的松散料，斗容 V_b 要配【松方密度 ρ松 = ρ实 / Ks】，
//     用 ρ实 算每车斗数会少算约 Ks 倍(岩石 1.5)，配车数直接错 50%。
//   · 反过来，产能回算实方时【不需要再除 Ks】：质量流 P(t/h) ÷ ρ实(t/m³实方) 就是 m³实方/h。
//     等价推导：P/ρ松 = m³松方/h，再 ÷Ks = m³实方/h —— 两条路殊途同归，
//     因为 ρ松 = ρ实/Ks。吨量在三个体积口径间是守恒量，这是唯一不会错的中间量。
//  加权 Ks 由 mix.ToLooseM3(1.0) 取得（= Σ Ks_i·份额_i，按实方体积加权，与定义一致）。
//
//  ── 混采面的两条腿（本次改造的核心）──
//  一台电铲同一时窗挖「煤7∶岩3」，煤去破碎站 2.6km、岩去内排场 1.26km：车队要跑两条不同运距的线。
//  循环时间与装车节拍因此都不是一个数，而是按各线加权：
//      T_c(加权) = Σ_m w_m · T_c(m) ,  τ_L(加权) = Σ_m w_m · τ_L(m)
//      T_c(m)    = t_装(m) + 60·L_eq(m)/v_重 + t_卸 + 60·L_eq(m)/v_空 + t_调
//      τ_L(m)    = m(m)·t_s , m(m) = W_t/(V_b·ρ松(m)·η_b)   ← ρ松 随物料变：煤轻岩重，每车斗数不同
//  然后照旧 n* = T_c/τ_L、MF = n·τ_L/T_c、Q_g = P/ρ实·η。
//
//  ★ 份额的两个口径（这里最容易搞错，也是本包与 Splits 对账的关键）：
//    ResolvedMix 给的是【实方体积份额 f_m】，但 T_c 与 τ_L 都是**每车次**的量，
//    而某条线上的车次数 ∝ 该物料的吨量（矿卡是载重受限，每趟都拉满 W_t 吨），故权必须是【车次份额】：
//        w_m = f_m·ρ实_m / Σ_i f_i·ρ实_i          （= 吨量份额）
//    直接拿实方份额当权会高估轻物料那条线：煤6∶岩4 的面实方 60% 是煤，吨量却只有 45%；
//    按 60% 分摊车次就等于让派车单多签 4 趟根本不存在的煤——本仓口径铁律
//    「吨量是实方/松方/占容三者间唯一的守恒量」，落到车次上就是这条。
//    展示层两个数都给（Explain 里写「实方60%·车次45%」），免得看的人以为算错了。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>单个作业面的编组求解结果。</summary>
public sealed class FleetMatchResult
{
    /// <summary>解出的编组（Trucks 是入参的实配拷贝，RecommendedTrucks/GroupCapacityM3PerH 是本次求解值）。</summary>
    public EquipmentGroup Group { get; set; } = new();
    /// <summary>匹配系数 MF = n·τ_L/T_c。</summary>
    public double MatchFactor { get; set; }
    /// <summary>理论最优配车数 n* = T_c/τ_L（取整并按排队损失择优后）。</summary>
    public int OptimalTrucks { get; set; }
    /// <summary>卡车循环时间 T_c（min）。</summary>
    public double CycleTimeMin { get; set; }
    /// <summary>装车节拍 τ_L（min/车）。</summary>
    public double LoadTaktMin { get; set; }
    /// <summary>电铲利用率 ρ_sh ∈[0,1]。</summary>
    public double ShovelUtil { get; set; }
    /// <summary>卡车利用率 ρ_tk ∈[0,1]。</summary>
    public double TruckUtil { get; set; }
    /// <summary>到达卡车需排队的概率 P_w（Erlang-C）。</summary>
    public double WaitProbability { get; set; }
    /// <summary>瓶颈判定文案。</summary>
    public string Bottleneck { get; set; } = "";
    /// <summary>给 UI 显示的一句话解释。</summary>
    public string Explain { get; set; } = "";
    /// <summary>本次求解采用的编组规则（型号溯源）。</summary>
    public string RuleCaption { get; set; } = "";

    /// <summary>
    /// 分项明细：混采面的每条运输线一条（煤→破碎站、岩→排土场…），单去向面只有一条。
    /// <list type="bullet">
    /// <item><b>Fraction</b> —— 该线的【车次份额】，也就是加权 T_c/τ_L 用的那个权
    ///   （= 吨量份额；见本类文件头「份额的两个口径」）。实方份额另见任务/作业面的 Splits。</item>
    /// <item><b>HaulKm</b> —— 该线的等效运距 km。</item>
    /// <item><b>CycleMin / TaktMin</b> —— 该线**自己**的 T_c 与 τ_L（不是加权值）；
    ///   甘特上每一趟的条长按它画，两条线的节拍差才看得出来。</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<(string MaterialCode, double Fraction, double HaulKm, double CycleMin, double TaktMin)> Legs { get; set; }
        = Array.Empty<(string, double, double, double, double)>();

    /// <summary>是否多腿（混采面两条以上运输线）。</summary>
    public bool IsMultiLeg => Legs.Count > 1;
}

public static class FleetMatcher
{
    // ── 工程缺省常量（台账没给时才用；与 FleetOptimizer 的缺省保持一致）──
    /// <summary>铲斗充满系数 η_b。</summary>
    private const double BucketFillFactor = 0.85;
    /// <summary>电铲单斗回转周期 t_s（min/斗）。</summary>
    private const double SwingMin = 0.53;
    /// <summary>调车对位时间 t_调（min）。</summary>
    private const double SpotMin = 1.0;
    /// <summary>卸载时间 t_卸（min）。</summary>
    private const double DumpMin = 1.5;
    /// <summary>综合作业效率 η（可用率×利用率；接 KPI 后由台账实测标定）。</summary>
    private const double DefaultEfficiency = 0.80;
    /// <summary>缺省卡车载重 W_t（t）与电铲斗容 V_b（m³）。</summary>
    private const double DefaultPayloadT = 100;
    private const double DefaultBucketM3 = 12;

    /// <summary>编组规则/在籍数据来源文案（供 UI 显示）。</summary>
    public static string LastSourceLabel { get; private set; } = "编组规则未装载";

    // ── 主求解 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 求解一个作业面的最优编组。rules 用 CsvDataStore 的富 POCO（已 join equipment_model
    /// 补上斗容/载重）；shovelInventory 为型号→在籍台数，用于择优主铲型号。
    /// </summary>
    public static FleetMatchResult Match(FaceInput face, HaulLeg haul,
                                         IEnumerable<DispatchRule> rules,
                                         IReadOnlyDictionary<string, int> shovelInventory,
                                         double efficiency = DefaultEfficiency)
    {
        // ── ① 物料：两个密度口径 ──
        var mix = face.ResolvedMix.Normalized();
        double rhoInSitu = Math.Max(0.1, mix.BlendedDensity);      // ρ实 (t/m³实方)
        double ks = Math.Max(1.0, mix.ToLooseM3(1.0));             // 加权松散系数 Ks
        double rhoLoose = rhoInSitu / ks;                          // ρ松 (t/m³松方) —— 铲斗口径

        // ── ② 设备：选规则，取 W_t / V_b ──
        var rule = PickRule(rules, face, shovelInventory);
        double wt = rule.TruckPayloadT > 0 ? rule.TruckPayloadT : DefaultPayloadT;   // W_t
        double vb = rule.ShovelBucketM3 > 0 ? rule.ShovelBucketM3 : DefaultBucketM3; // V_b

        // ── ③ §3.3 装车节拍 τ_L = m·t_s，m = W_t/(V_b·ρ松·η_b) ──
        double m = BucketsPerTruck(rule, wt, vb, rhoLoose);
        double takt = Math.Max(0.1, m * SwingMin);     // τ_L

        // ── ④ §3.1 循环时间 T_c ──
        double tc = CycleOf(haul, takt, rule);

        var legs = new List<LegSolve>
        {
            new()
            {
                Code = mix.PrimaryCode, Weight = 1.0, VolFraction = 1.0,
                EquivKm = haul.EquivKm, Buckets = m, TaktMin = takt, CycleMin = tc,
            }
        };

        string ruleCap = $"{rule.ShovelModel}（{vb:0.#}m³）＋{rule.TruckModel}（{wt:0.#}t）· {m:0.#}斗/车";
        string head = $"运距 {haul.EquivKm:0.0}km（{haul.Source}）→";
        return Assemble(face, rhoInSitu, wt, takt, tc, legs, efficiency, ruleCap, head);
    }

    /// <summary>
    /// 多腿求解（混采面）：一台铲的车队跑 N 条运距不同的线，循环时间与装车节拍按各线的
    /// 【车次份额】加权（见文件头「混采面的两条腿」）。
    /// <paramref name="legs"/> 由 <see cref="HaulResolver.ResolveAll"/> 给出：每种物料一条腿，
    /// 各挂各自的汇与等效运距。空表时退回单腿口径。
    /// </summary>
    public static FleetMatchResult Match(FaceInput face,
                                         IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)> legs,
                                         IEnumerable<DispatchRule> rules,
                                         IReadOnlyDictionary<string, int> shovelInventory,
                                         double efficiency = DefaultEfficiency)
    {
        if (legs == null || legs.Count == 0)
            return Match(face, new HaulLeg { Source = "兜底" }, rules, shovelInventory, efficiency);

        var mix = face.ResolvedMix.Normalized();
        double rhoInSitu = Math.Max(0.1, mix.BlendedDensity);      // ρ实（混合物，产能回算实方用）

        var rule = PickRule(rules, face, shovelInventory);
        double wt = rule.TruckPayloadT > 0 ? rule.TruckPayloadT : DefaultPayloadT;
        double vb = rule.ShovelBucketM3 > 0 ? rule.ShovelBucketM3 : DefaultBucketM3;

        // ── 逐腿解 τ_L(m) 与 T_c(m)：ρ松 随物料变 ⇒ 每车斗数变 ⇒ 节拍变；运距变 ⇒ 行车时间变 ──
        var solved = new List<LegSolve>();
        foreach (var (dest, leg) in legs)
        {
            var spec = MaterialCatalog.Resolve(dest.MaterialCode);
            double rhoLooseM = spec.InSituDensityTPerM3 / Math.Max(1.0, spec.SwellFactor);   // ρ松(m)
            double buckets = BucketsPerTruck(rule, wt, vb, rhoLooseM);
            double taktM = Math.Max(0.1, buckets * SwingMin);

            solved.Add(new LegSolve
            {
                Code = dest.MaterialCode,
                VolFraction = Math.Max(0, dest.Fraction),
                EquivKm = leg.EquivKm > 1e-6 ? leg.EquivKm : dest.EffectiveHaulKm,
                Buckets = buckets,
                TaktMin = taktM,
                CycleMin = CycleOf(leg, taktM, rule),
            });
        }

        // ── 权 = 车次份额 = 吨量份额（矿卡载重受限，每趟都拉满 W_t 吨 ⇒ 车次数 ∝ 吨量）──
        double tonSum = solved.Sum(l => l.VolFraction * l.Spec.InSituDensityTPerM3);
        foreach (var l in solved)
            l.Weight = tonSum > 1e-9 ? l.VolFraction * l.Spec.InSituDensityTPerM3 / tonSum : 1.0 / solved.Count;

        double takt = Math.Max(0.1, solved.Sum(l => l.Weight * l.TaktMin));   // τ_L(加权)
        double tc = Math.Max(takt + DumpMin, solved.Sum(l => l.Weight * l.CycleMin));  // T_c(加权)

        string ruleCap = $"{rule.ShovelModel}（{vb:0.#}m³）＋{rule.TruckModel}（{wt:0.#}t）· "
                       + string.Join(" ｜ ", solved.Select(l => $"{l.Spec.Name} {l.Buckets:0.#}斗/车"));

        string head = solved.Count > 1
            ? $"混采 {solved.Count} 条线：" + string.Join(" ｜ ", solved.Select(l =>
                  $"{l.Spec.Name} 实方{l.VolFraction * 100:0.#}%·车次{l.Weight * 100:0.#}% "
                + $"{l.EquivKm:0.00}km T_c {l.CycleMin:0.0}min")) + " ⇒ 加权"
            : $"运距 {solved[0].EquivKm:0.0}km（{legs[0].Leg.Source}）→";

        return Assemble(face, rhoInSitu, wt, takt, tc, solved, efficiency, ruleCap, head);
    }

    /// <summary>
    /// 简化重载：编组规则与在籍设备自己从 GeoDataBase 读（dispatch_rule join equipment_model），
    /// 读不到就用内置缺省规则。数据来源见 <see cref="LastSourceLabel"/>。
    /// </summary>
    public static FleetMatchResult Match(FaceInput face, HaulLeg haul)
    {
        var ctx = LoadContext();
        return Match(face, haul, ctx.Rules, ctx.Inventory, ctx.Efficiency);
    }

    /// <summary>简化重载（多腿）：规则/在籍自己读台账。</summary>
    public static FleetMatchResult Match(FaceInput face, IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)> legs)
    {
        var ctx = LoadContext();
        return Match(face, legs, ctx.Rules, ctx.Inventory, ctx.Efficiency);
    }

    // ── 单腿 / 多腿共用的求解件 ───────────────────────────────────────────────

    /// <summary>一条运输线的解（混采面每种物料一条；单去向面只有一条）。</summary>
    private sealed class LegSolve
    {
        public string Code = "";
        /// <summary>车次份额（=吨量份额）—— 加权 T_c/τ_L 用的权。</summary>
        public double Weight;
        /// <summary>实方体积份额（展示与对账用）。</summary>
        public double VolFraction;
        public double EquivKm;
        public double Buckets;
        public double TaktMin;
        public double CycleMin;
        public MaterialSpec Spec => MaterialCatalog.Resolve(Code);
    }

    /// <summary>
    /// §3.3 每车斗数 m = W_t/(V_b·ρ松·η_b)。台账直接给了「装满铲数」就用台账值（现场实测优于反推）；
    /// 否则由物料松方密度反推——同一台铲装煤和装岩，每车斗数不同，节拍就不同。
    /// </summary>
    private static double BucketsPerTruck(DispatchRule rule, double wt, double vb, double rhoLoose)
    {
        double m = rule.BucketLoadsPerTruck > 0
            ? rule.BucketLoadsPerTruck
            : wt / Math.Max(0.1, vb * rhoLoose * BucketFillFactor);
        return Math.Clamp(m, 1.0, 20.0);               // 防台账脏数据把节拍算飞
    }

    /// <summary>
    /// §3.1 循环时间 T_c = t_装 + 60·L_eq/v_重 + t_卸 + 60·L_eq/v_空 + t_调。
    /// 行车时间直接用运距层的解（路网层已含坡度折算，等效运距 L_eq 的影响已内生在 TimeMin 里）；
    /// 运距层没解出时间就按等效运距 + 平路车速补算，保证 T_c 永远有意义。
    /// </summary>
    private static double CycleOf(HaulLeg haul, double takt, DispatchRule rule)
    {
        double travelLoaded = haul.LoadedMin > 0 ? haul.LoadedMin : FlatMin(haul.EquivKm, loaded: true);
        double travelEmpty = haul.EmptyMin > 0 ? haul.EmptyMin : FlatMin(haul.EquivKm, loaded: false);

        // 复用 RoadLib 的循环时间公式；把 t_装 = τ_L 注进「装+调车」项，
        // 避免用它 spotLoadMin 的缺省 3.0min 覆盖掉我们按物料算出来的真节拍（否则重复计装车时间）。
        double tc = HaulMetrics.CycleTimeMin(travelLoaded, travelEmpty,
                                             spotLoadMin: takt + SpotMin,
                                             maneuverDumpMin: DumpMin);
        // 运距三层全空时，退回规则表的经验循环时间（与运距无关的最后兜底）。
        if (!haul.Feasible && rule.CycleTimeMin > 0) tc = rule.CycleTimeMin;
        return Math.Max(takt + DumpMin, tc);           // T_c 不可能小于「装 + 卸」
    }

    /// <summary>
    /// §4.2~4.5 + §5：由（加权）T_c / τ_L 解最优配车数、匹配系数、产能、利用率与排队概率，装配结果。
    /// 单腿与多腿走同一段——两条路只是 T_c/τ_L 的来源不同，后面的物理完全一样。
    /// </summary>
    private static FleetMatchResult Assemble(FaceInput face, double rhoInSitu, double wt,
                                             double takt, double tc, List<LegSolve> legs,
                                             double efficiency, string ruleCaption, string head)
    {
        var res = new FleetMatchResult();

        // ── §5 最优配车数 n* = T_c/τ_L，取整后按 Erlang-C 排队损失择优 ──
        int nStar = ChooseOptimalTrucks(tc, takt);

        // ── 实配车数 n：以现场实配为准；未配车的面按建议值评估，免得班产算成 0 ──
        int trucksOnFace = face.Group.Trucks.Count;
        int n = trucksOnFace > 0 ? trucksOnFace
              : face.Group.RecommendedTrucks > 0 ? face.Group.RecommendedTrucks
              : nStar;
        n = Math.Max(1, n);

        // ── §4.2~4.5 匹配系数 / 产能 / 利用率 / 排队概率 ──
        double mf = n * takt / tc;                     // MF
        double pSh = 60.0 * wt / takt;                 // P_sh 铲装能力 (t/h)
        double pFl = n * 60.0 * wt / tc;               // P_fl 车队运力 (t/h)
        double p = Math.Min(pSh, pFl);                 // 有效产能取瓶颈侧

        // ★ 质量流 → 实方体积流：P(t/h) ÷ ρ实(t/m³实方) = m³实方/h。
        //   吨量在实方/松方/占容三个口径间守恒，故这里不再乘除 Ks（见文件头「两个密度口径」）。
        double qm3h = p / rhoInSitu * Math.Clamp(efficiency, 0.1, 1.0);

        double shovelUtil = Math.Min(1.0, mf);                          // ρ_sh
        double truckUtil = mf > 1 ? 1.0 / mf : 1.0;                     // ρ_tk
        // 单机组 c=1（M/M/1），Erlang-C 退化为 P_w = ρ = MF。
        double pw = ErlangC(1, Math.Min(0.999, mf));

        string bottleneck = mf < 0.9 ? "运力不足·铲待车"
                          : mf <= 1.1 ? "配置均衡"
                          : "铲能力瓶颈·车排队";
        string tag = mf < 0.9 ? "铲待车" : mf <= 1.1 ? "均衡" : "车排队";

        res.Group = new EquipmentGroup
        {
            MainEquipment = face.Group.MainEquipment,
            Trucks = new List<string>(face.Group.Trucks),   // 实配原样带出，绝不在这里改
            Aux = new List<string>(face.Group.Aux),
            RecommendedTrucks = nStar,
            GroupCapacityM3PerH = Math.Round(qm3h, 2),
            // 周期分解随编组下沉：装箱那一步要靠它把班产还原成铲装/车队两侧，才谈得上按环节降效。
            // 与 res.CycleTimeMin/LoadTaktMin/MatchFactor 是同一组数，只是换个地方也放一份——
            // 面上带着的是 Group，装箱拿不到 FleetMatchResult。
            LoadTaktMin = Math.Round(takt, 2),
            CycleTimeMin = Math.Round(tc, 1),
            MatchFactor = Math.Round(mf, 2),
            // 载重带出去：运输那一笔要靠它把吨量折成车次（承运吨 ÷ W_t）。
            // 不带的话下游只能猜一个 100t，而猜出来的车次在派车单上看着完全正常。
            TruckPayloadT = Math.Round(wt, 1),
        };
        res.MatchFactor = Math.Round(mf, 2);
        res.OptimalTrucks = nStar;
        res.CycleTimeMin = Math.Round(tc, 1);
        res.LoadTaktMin = Math.Round(takt, 2);
        res.ShovelUtil = Math.Round(shovelUtil, 3);
        res.TruckUtil = Math.Round(truckUtil, 3);
        res.WaitProbability = Math.Round(pw, 3);
        res.Bottleneck = bottleneck;
        res.RuleCaption = ruleCaption;
        res.Legs = legs.Select(l => (l.Code, Math.Round(l.Weight, 4), Math.Round(l.EquivKm, 3),
                                     Math.Round(l.CycleMin, 1), Math.Round(l.TaktMin, 2))).ToList();

        string cfgText = trucksOnFace > 0 ? $"现配 {trucksOnFace} 车"
                       : face.Group.RecommendedTrucks > 0 ? $"原荐 {face.Group.RecommendedTrucks} 车"
                       : "尚未配车（按荐值评估）";
        res.Explain = $"{head} T_c {tc:0.0}min，τ_L {takt:0.0}min → 荐 {nStar} 车；"
                    + $"{cfgText}，MF {mf:0.00} {tag}";
        return res;
    }

    // ── §5 最优配车数：取整 + 排队损失择优 ──────────────────────────────────

    /// <summary>
    /// n* = T_c/τ_L 取整。理论报告 §5：整数化时要在 ⌊n*⌋（略欠配、免排队）与
    /// ⌈n*⌉（略过配、填满电铲）之间择优。
    /// 判据：⌊n*⌋ 的匹配系数 MF=⌊n*⌋·τ_L/T_c 就等于它相对满载产能的达成率
    /// （因 P_fl/P_sh ≡ MF），故 MF ≥ 0.95 时说明少一台车只损失不到 5% 产能，
    /// 却省一台车、且把排队概率 P_w 从 ≥1 降到 MF —— 取少者；否则取 ⌈n*⌉ 填满电铲。
    /// </summary>
    private static int ChooseOptimalTrucks(double tc, double takt)
    {
        double raw = tc / Math.Max(0.1, takt);
        int lo = Math.Max(1, (int)Math.Floor(raw));
        int hi = Math.Max(1, (int)Math.Ceiling(raw));
        if (lo == hi) return lo;

        double mfLo = lo * takt / tc;          // = P_fl(lo)/P_sh，即产能达成率
        return mfLo >= 0.95 ? lo : hi;
    }

    /// <summary>
    /// §4.5 Erlang-C：M/M/c 中到达顾客需排队的概率。ρ 为单台服务台利用率（&lt;1 才稳定）。
    /// c=1 时退化为 P_w = ρ。
    /// （FleetOptimizer 里同名函数是 private，无法复用，故按标准公式在此重实现。）
    /// </summary>
    private static double ErlangC(int c, double rhoPerServer)
    {
        if (c <= 0) return 1.0;
        if (rhoPerServer >= 1) return 1.0;
        if (rhoPerServer <= 0) return 0.0;

        double a = rhoPerServer * c;           // 话务量(Erlang)
        double sum = 0, term = 1;              // Σ_{k=0}^{c-1} a^k/k!
        for (int k = 0; k < c; k++)
        {
            if (k > 0) term *= a / k;
            sum += term;
        }
        double last = term * a / c;            // a^c/c!
        double top = last / (1 - rhoPerServer);
        return top / (sum + top);
    }

    // ── 规则择优 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 选一条编组规则：① 面上指定了主铲型号偏好就按它；② 否则在【有在籍设备】的型号里
    /// 按效率评分取最高；③ 再否则按效率评分取最高；④ 全空则用内置缺省规则。
    /// </summary>
    private static DispatchRule PickRule(IEnumerable<DispatchRule>? rules, FaceInput face,
                                         IReadOnlyDictionary<string, int>? inventory)
    {
        var list = (rules ?? Enumerable.Empty<DispatchRule>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ShovelModel))
            .ToList();
        if (list.Count == 0) return DefaultRule();

        if (!string.IsNullOrWhiteSpace(face.ShovelModelPref))
        {
            var pref = list.FirstOrDefault(
                r => string.Equals(r.ShovelModel, face.ShovelModelPref, StringComparison.OrdinalIgnoreCase));
            if (pref != null) return pref;
        }

        if (inventory is { Count: > 0 })
        {
            // 先落成非空局部：lambda 捕获不保留外层的可空流分析结论。
            IReadOnlyDictionary<string, int> inv = inventory;
            var owned = list
                .Where(r => inv.TryGetValue(r.ShovelModel, out int c) && c > 0)
                .OrderByDescending(r => r.EfficiencyScore)
                .FirstOrDefault();
            if (owned != null) return owned;
        }

        return list.OrderByDescending(r => r.EfficiencyScore).First();
    }

    /// <summary>
    /// 内置缺省编组规则（台账为空时的兜底）。刻意把「装满铲数 / 推荐车数 / 循环时间」留 0，
    /// 逼求解器全部按物理公式现算——这样即便没有台账，配车数也随物料与运距变化，而不是常数。
    /// </summary>
    private static DispatchRule DefaultRule() => new()
    {
        ShovelModel = "电铲(缺省12m³)",
        ShovelBucketM3 = DefaultBucketM3,
        TruckModel = "矿卡(缺省100t)",
        TruckPayloadT = DefaultPayloadT,
        BucketLoadsPerTruck = 0,      // 0 ⇒ 由 m = W_t/(V_b·ρ松·η_b) 反推
        RecommendedTruckCount = 0,    // 0 ⇒ 由 n* = T_c/τ_L 解出
        CycleTimeMin = 0,             // 0 ⇒ 由 §3.1 现算
        EfficiencyScore = 50,
    };

    // ── 台账装载（容错）─────────────────────────────────────────────────────

    private sealed class MatchContext
    {
        public List<DispatchRule> Rules = new();
        public Dictionary<string, int> Inventory = new(StringComparer.OrdinalIgnoreCase);
        public double Efficiency = DefaultEfficiency;
    }

    /// <summary>读 dispatch_rule / 在籍设备 / KPI 效率；任何失败都回落内置缺省，不抛。</summary>
    private static MatchContext LoadContext()
    {
        var ctx = new MatchContext();
        try
        {
            // CsvDataStore.GetDispatchRules() 已经把 dispatch_rule 与 equipment_model join 好，
            // 补上了斗容 V_b 与载重 W_t —— 裸的 Public.Entities.DispatchRule 没有这两列，物理公式算不了。
            var store = new CsvDataStore();
            ctx.Rules = store.GetDispatchRules();
            ctx.Inventory = store.GetInventoryByModel();
            ctx.Efficiency = store.GetAverageEfficiency();

            if (ctx.Rules.Count == 0)
            {
                ctx.Rules = new List<DispatchRule> { DefaultRule() };
                LastSourceLabel = "内置缺省编组规则（dispatch_rule 表为空，请在「设备编组」录入）";
                LastFromLedger = false;
            }
            else
            {
                LastSourceLabel = $"编组规则台账（DB，{ctx.Rules.Count} 条 · 在籍 {ctx.Inventory.Count} 型 · η={ctx.Efficiency:0.00}）";
                LastFromLedger = true;
            }
        }
        catch (Exception ex)
        {
            ctx.Rules = new List<DispatchRule> { DefaultRule() };
            ctx.Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ctx.Efficiency = DefaultEfficiency;
            LastSourceLabel = $"内置缺省编组规则（DB 未接通：{Short(ex)}）";
            LastFromLedger = false;
        }
        return ctx;
    }

    // ── 批量接线 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 对 cfg 里所有采装面（Process==Load）求解编组，写回 face.Group 的
    /// <c>RecommendedTrucks</c> 与 <c>GroupCapacityM3PerH</c>。
    /// 【不动 Trucks 列表】—— 那是现场实配（谁真的在这个面上），本类只给建议值；
    /// 二者的差就是 TaskExploder 报「运力不足」的依据，覆盖掉就没得比了。
    /// 返回一句汇总文案（同时写进 LastSourceLabel）。
    /// </summary>

    /// <summary>
    /// 上一次装配用的是不是真数据（false = 走了兜底）。
    /// <para><b>链路体检读它，不读来源文案</b> —— 文案一改就静默抠空，
    /// 而抠空之后「兜底」会被当成「真实」，体检朝着让人放心的方向失效。</para>
    /// </summary>
    public static bool LastFromLedger { get; private set; }

    public static string ApplyTo(ExploderConfig cfg)
    {
        var ctx = LoadContext();
        string ruleLabel = LastSourceLabel;

        int solved = 0, under = 0, over = 0, multi = 0;
        foreach (var face in cfg.Faces.Where(f => f.Process == ProcessType.Load))
        {
            FleetMatchResult r;

            if (face.HasSplits)
            {
                // ── 混采面：逐分项解运距 → 多腿加权 T_c/τ_L → n*。
                //    次要物料那条腿（运距不同）就此进了最优配车数；此前它整个被主去向盖掉。
                var legs = HaulResolver.ResolveAll(face, cfg.Sinks);
                r = Match(face, legs, ctx.Rules, ctx.Inventory, ctx.Efficiency);
                if (r.IsMultiLeg) multi++;
            }
            else
            {
                // ── 单去向面：口径完全不变。
                //    此处用 Resolve 而非 HaulResolver.ApplyTo，不产生副作用；
                //    Resolve 内部已经会尊重面上手填的 HaulDistanceKm（第②层）。
                var sink = FindSink(cfg, face);
                var haul = HaulResolver.Resolve(face, sink);
                r = Match(face, haul, ctx.Rules, ctx.Inventory, ctx.Efficiency);
            }

            face.Group.RecommendedTrucks = r.OptimalTrucks;
            face.Group.GroupCapacityM3PerH = r.Group.GroupCapacityM3PerH;
            // ★ 载重与周期也要回写（2026-08-20）：运输那一笔靠载重把吨量折成车次，
            //   靠周期算一趟多久。只回写"推荐车数 + 班产"两项时，下游只能拿 0 载重，
            //   于是**车次数一律给不出来** —— 而派车单上那一栏是空的，没人知道是为什么。
            face.Group.TruckPayloadT = r.Group.TruckPayloadT;
            face.Group.LoadTaktMin = r.Group.LoadTaktMin;
            face.Group.CycleTimeMin = r.Group.CycleTimeMin;
            face.Group.MatchFactor = r.Group.MatchFactor;

            solved++;
            if (r.MatchFactor < 0.9) under++;
            else if (r.MatchFactor > 1.1) over++;
        }

        LastSourceLabel = $"编组：求解 {solved} 面（欠配 {under} · 过配 {over}"
                        + (multi > 0 ? $" · 混采多腿 {multi} 面" : "") + $"）· {ruleLabel}";
        return LastSourceLabel;
    }

    /// <summary>按 DestinationId 找去向，找不到再按 DestinationName 兜一次。</summary>
    private static SinkNode? FindSink(ExploderConfig cfg, FaceInput face)
    {
        var s = cfg.Sinks.Find(face.DestinationId);
        if (s != null) return s;
        if (string.IsNullOrWhiteSpace(face.DestinationName)) return null;
        return cfg.Sinks.All.FirstOrDefault(
            x => string.Equals(x.Name, face.DestinationName, StringComparison.OrdinalIgnoreCase));
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    /// <summary>平路行车时间 min（运距层没给时间时补算，速度口径与 RoadLib 一致）。</summary>
    private static double FlatMin(double km, bool loaded)
        => HaulMetrics.TravelTimeMin(Math.Max(0, km) * 1000.0, gradePct: 0, TruckProfile.Default, loaded);

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
