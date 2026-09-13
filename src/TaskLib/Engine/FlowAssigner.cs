// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/FlowAssigner.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  流向分配求解器 —— 采—运—排的第三方层（源产出 × 汇容量 × OD 运距 → 谁运到哪）。
//
//  露天矿的排产链缺了这一层，「配车/装箱/甘特」就只能拍脑袋定去向：
//      各面产出 Q_i（按物料拆）          ← 采装侧（TaskExploder 的输入）
//      各汇容量 C_j（占容方 / 通过能力）  ← 排弃侧（SinkRegistry）
//      OD 等效运距 L_eq(i,j)             ← 运输侧（路网/坡度折算，HaulResolver）
//  三者交汇后才谈得上「这一方岩从哪个面、走哪条路、排到哪个场」。
//
//  目标函数（露天矿方案比选的经典口径）：**运输功最小**
//      min  Σ_ij  Q_ij[t] × L_eq_ij[km]          （t·km）
//  运输功是全矿运输成本最稳的代理量（油耗/轮胎/折旧几乎线性于 t·km），
//  且天然把「内排下排、外排上排」的坡度差异折进 L_eq —— 内排降本的真正来源
//  不是里程短 20%，而是重车下坡使 L_eq 再降一截。
//
//  约束：
//   1) 物料—去向兼容：表土只进表土堆场（复垦资源），煤/低质煤不进排土场；
//   2) 排土库容按【占容方 V容 = V实×Kr】扣，不是按实方扣（Kr 已含沉降）；
//   3) 卸点通过能力按【吨量】扣（t/h × 有效小时）；
//   4) 时空可用性：Status=active、期次已到（内排须等采空区形成）。
//
//  求解：带容量约束的运输问题（最小费用流）。数据规模小（源 ≤ 几十 × 汇 ≤ 十几），
//  不引第三方求解器：**最小元素法贪心 + 2-opt 对偶交换局部改进**（见 Assign 内注释）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一份待分配的供给：某源、某物料、某实方量。混采面按 MaterialMix.Split 拆成多份。</summary>
public sealed class FlowSupply
{
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public double BenchElevationM { get; set; }
    public string MaterialCode { get; set; } = "";
    /// <summary>本期产出（实方 m³）。</summary>
    public double InSituM3 { get; set; }

    /// <summary>手工锁定去向（已人工指定的面）。非空则不参与优化，先按锁定汇分配。</summary>
    public string PinnedSinkId { get; set; } = "";

    /// <summary>回指来源作业面（AssignForConfig 填；供 HaulResolver 接线与写回用，不参与算法）。</summary>
    public object? Tag { get; set; }

    public MaterialSpec Spec => MaterialCatalog.Resolve(MaterialCode);
    public string Caption => $"{SourceName}·{Spec.Name} {InSituM3 / 1e4:0.##}万m³";
}

/// <summary>流向分配问题。</summary>
public sealed class FlowProblem
{
    public List<FlowSupply> Supplies { get; set; } = new();
    public SinkRegistry Sinks { get; set; } = new();

    /// <summary>OD 实际运距 km。null 或返回 ≤0 → 回落 SinkNode.FallbackHaulKm（三层兜底最后一层）。</summary>
    public Func<FlowSupply, SinkNode, double>? HaulKmProvider { get; set; }

    /// <summary>OD 等效运距 km（含坡阻折算）。null 或返回 ≤0 → 按高程差内置折算（见 EquivOf）。</summary>
    public Func<FlowSupply, SinkNode, double>? EquivHaulKmProvider { get; set; }

    /// <summary>本期有效作业小时（卸点通过能力按此折成吨量上限）。≤0 视为不限通过能力。</summary>
    public double PeriodHours { get; set; } = 24;

    public string Period { get; set; } = "";

    /// <summary>是否把分配结果写回 Sinks.FilledM3（多期连续推演时置 true；默认只算不改）。</summary>
    public bool CommitToSinks { get; set; }

    /// <summary>局部改进的最大接受次数（0=关闭改进，只做贪心）。</summary>
    public int MaxImproveMoves { get; set; } = 500;
}

/// <summary>一个汇在本期的受载明细（供库容预警、排土推进反算、三维模拟用）。</summary>
public sealed class SinkLoad
{
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind Kind { get; set; }
    public double InSituM3 { get; set; }
    public double DumpM3 { get; set; }       // 占容方（排土场按此扣库容）
    public double TonnageT { get; set; }
    public double RemainingBeforeM3 { get; set; }
    public double RemainingAfterM3 { get; set; }
    public double ThroughputCapT { get; set; }
    public bool VolumeBound { get; set; }     // 本期被库容顶住
    public bool ThroughputBound { get; set; } // 本期被通过能力顶住
    /// <summary>按占容方反算的排土推进距离 m。</summary>
    public double AdvanceM { get; set; }

    public string Caption =>
        $"{SinkName}：进 {InSituM3 / 1e4:0.##}万m³实方（占容 {DumpM3 / 1e4:0.##}万m³）" +
        (double.IsInfinity(RemainingAfterM3) ? "，容量不限" : $"，余 {RemainingAfterM3 / 1e4:0.##}万m³") +
        (VolumeBound ? " · 库容见顶" : "") + (ThroughputBound ? " · 卸点见顶" : "");
}

/// <summary>流向分配结果。</summary>
public sealed class FlowPlan
{
    public List<MaterialFlow> Flows { get; set; } = new();
    public List<PlanViolation> Violations { get; set; } = new();

    /// <summary>总运输功 t·km（目标函数值）。</summary>
    public double TotalTransportWorkTKm { get; set; }
    /// <summary>吨量加权平均运距 km。</summary>
    public double WeightedAvgHaulKm { get; set; }
    /// <summary>内排率 % = 内排占容方 / 全部排弃占容方。</summary>
    public double InternalDumpPct { get; set; }
    /// <summary>是否全部分配得下（有缺口即 false）。</summary>
    public bool Feasible { get; set; } = true;
    /// <summary>人读方案摘要 + 缺口与建议。</summary>
    public string Explain { get; set; } = "";

    // ── 附加诊断 ──
    public double AssignedInSituM3 { get; set; }
    public double UnassignedInSituM3 { get; set; }
    public List<SinkLoad> SinkLoads { get; set; } = new();
    /// <summary>贪心解的运输功（改进前），用于评估局部改进收益。</summary>
    public double GreedyWorkTKm { get; set; }
    /// <summary>局部改进接受的交换次数。</summary>
    public int ImproveMoves { get; set; }

    public PeriodBalance ToBalance(string? period = null)
        => new() { Period = period ?? (Flows.Count > 0 ? Flows[0].Period : ""), Flows = Flows.ToList() };
}

public static class FlowAssigner
{
    private const double Eps = 1e-6;          // 体积/吨量零阈（m³）
    private const double CostEps = 1e-6;      // 运输功改进零阈（t·km）

    /// <summary>无高程数据时内排的等效运距缺省折减（简化：内排多为下排，重车下坡省功）。</summary>
    private const double InternalDumpEquivFactor = 0.90;
    /// <summary>运距三层兜底的最后一层：全局默认运距 km（与编组融合设计 §4「层3 默认」一致）。</summary>
    private const double DefaultHaulKm = 2.5;
    /// <summary>坡阻系数（理论报告 §3.2）：重车上坡 k_u、重车下坡 k_d；下坡折减下限 0.5。</summary>
    private const double UphillResistK = 6.0, DownhillReliefK = 2.0, DownhillFloor = 0.5;
    /// <summary>平均坡度限幅（超此视为高程数据异常，不参与折算）。</summary>
    private const double MaxGrade = 0.12;

    // 两个本包自用校核码（ViolationCodes 属共享契约、本包不改；建议后续并入其中）
    private const string CodeFlowSplit = "去向拆分";
    private const string CodeTopsoilOk = "表土去向";

    // ── 内部求解态 ─────────────────────────────────────────────────────────

    /// <summary>一个汇的剩余容量台账（求解期间的可变状态，不动 SinkNode 本体）。</summary>
    private sealed class SinkState
    {
        public SinkNode Node = null!;
        public int Index;
        public double CapDumpM3;      // 剩余库容（占容方 m³）；+∞=不限
        public double CapT;           // 剩余通过吨量 t；+∞=不限
        public double UsedInSitu, UsedDump, UsedT;
        public bool VolumeBound, ThroughputBound;
        public double RemainBeforeM3;

        /// <summary>本汇还能接纳多少【实方 m³】的该物料（两维容量取小）。</summary>
        public double MaxIntake(MaterialSpec spec)
        {
            double byVol = double.IsInfinity(CapDumpM3) ? double.PositiveInfinity
                         : CapDumpM3 / Math.Max(1e-6, spec.ResidualSwellFactor);
            double byT = double.IsInfinity(CapT) ? double.PositiveInfinity
                         : CapT / Math.Max(1e-6, spec.InSituDensityTPerM3);
            return Math.Max(0, Math.Min(byVol, byT));
        }

        public void Take(MaterialSpec spec, double inSituM3)
        {
            if (!double.IsInfinity(CapDumpM3)) CapDumpM3 = Math.Max(0, CapDumpM3 - spec.ToDumpM3(inSituM3));
            if (!double.IsInfinity(CapT)) CapT = Math.Max(0, CapT - spec.ToTonnage(inSituM3));
            UsedInSitu += inSituM3; UsedDump += spec.ToDumpM3(inSituM3); UsedT += spec.ToTonnage(inSituM3);
        }

        /// <summary>回吐（局部改进交换时用）。</summary>
        public void Give(MaterialSpec spec, double inSituM3)
        {
            if (!double.IsInfinity(CapDumpM3)) CapDumpM3 += spec.ToDumpM3(inSituM3);
            if (!double.IsInfinity(CapT)) CapT += spec.ToTonnage(inSituM3);
            UsedInSitu = Math.Max(0, UsedInSitu - inSituM3);
            UsedDump = Math.Max(0, UsedDump - spec.ToDumpM3(inSituM3));
            UsedT = Math.Max(0, UsedT - spec.ToTonnage(inSituM3));
        }
    }

    /// <summary>一条分配（源 i → 汇 j 的实方量）。</summary>
    private sealed class Alloc
    {
        public int S, J;          // 供给下标 / 汇下标
        public double M3;
        public bool Pinned;       // 手工锁定，不参与局部改进
    }

    // ── 主入口 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 求解流向分配：min Σ Q_ij[t] × L_eq_ij[km]，受物料兼容 / 排土库容(占容方) / 卸点通过能力约束。
    ///
    /// 算法（两阶段，复杂度对本问题规模完全够用）：
    ///   ① **最小元素法贪心**（运输问题经典构造法）：把全部可行 (源,汇) 弧按单位运输功
    ///      c_ij = ρ_物料 × L_eq_ij（t·km / 实方m³）升序排，逐弧灌到「源供尽」或「汇灌满」。
    ///      复杂度 O(A log A)，A = 可行弧数 ≤ |源|×|汇|。
    ///      注意排序用的是全局弧序而非「逐源挑最近汇」——后者会让先到的源抢占廉价汇。
    ///   ② **2-opt 对偶交换局部改进**：任取两条流 (s1→j1, s2→j2)，按较小量 δ=min(q1,q2)
    ///      对等交换目的地（对等交换保证两源产量不变，只搬运目的地），
    ///        ΔW = δ·[ρ1·(L_eq(s1,j2) − L_eq(s1,j1)) + ρ2·(L_eq(s2,j1) − L_eq(s2,j2))]
    ///      ΔW &lt; 0 且交换后两汇的库容/通过能力仍满足即接受。迭代到无改进或达上限。
    ///      这一步修的正是贪心的典型次优：廉价汇被「本来就没别的去处」的物料占满。
    ///      复杂度 O(轮次 × F²)，F = 流条数。
    ///   ③ 改进腾出容量后再灌一次剩余供给；仍分不完 → 记 PlanViolation + Explain 写清缺口。
    /// </summary>
    public static FlowPlan Assign(FlowProblem p)
    {
        var plan = new FlowPlan();
        var supplies = (p.Supplies ?? new List<FlowSupply>()).Where(s => s != null && s.InSituM3 > Eps).ToList();
        var sinks = BuildSinkStates(p, plan);

        if (supplies.Count == 0)
        {
            plan.Explain = "本期无待分配产出。";
            return plan;
        }
        if (sinks.Count == 0)
        {
            plan.Feasible = false;
            plan.UnassignedInSituM3 = supplies.Sum(s => s.InSituM3);
            plan.Violations.Add(V(ViolationSeverity.Error, ViolationCodes.NoDestination,
                $"去向登记簿为空或全部不可用，{plan.UnassignedInSituM3 / 1e4:0.##}万m³ 产出无处可去。"));
            plan.Explain = "无可用去向：请先在「装卸点/排土场设置」录入排土场与卸点，或检查其状态与启用期次。";
            return plan;
        }

        int n = supplies.Count, m = sinks.Count;
        var remain = supplies.Select(s => s.InSituM3).ToArray();

        // OD 等效运距缓存：同一 (源,汇) 反复取值（贪心 + 每次交换评估）
        var eqCache = new double[n, m];
        var kmCache = new double[n, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++) { kmCache[i, j] = double.NaN; eqCache[i, j] = double.NaN; }

        double Km(int i, int j)
        {
            if (double.IsNaN(kmCache[i, j])) ResolveHaul(p, supplies[i], sinks[j].Node, out kmCache[i, j], out eqCache[i, j]);
            return kmCache[i, j];
        }
        double Eq(int i, int j)
        {
            if (double.IsNaN(eqCache[i, j])) ResolveHaul(p, supplies[i], sinks[j].Node, out kmCache[i, j], out eqCache[i, j]);
            return eqCache[i, j];
        }
        // 单位运输功 c_ij：t·km / 实方m³ = ρ × L_eq
        double Cost(int i, int j) => supplies[i].Spec.InSituDensityTPerM3 * Eq(i, j);

        // 物料—去向兼容（不含容量，用于「是不是根本没地方去」的判定）
        bool Compatible(int i, int j) => sinks[j].Node.Accepts(supplies[i].Spec);

        var allocs = new List<Alloc>();

        // ── 0. 手工锁定去向优先落位（人工指定的面不参与优化，但照样吃容量）──
        for (int i = 0; i < n; i++)
        {
            string pin = supplies[i].PinnedSinkId;
            if (string.IsNullOrWhiteSpace(pin)) continue;
            int j = sinks.FindIndex(s => string.Equals(s.Node.Id, pin, StringComparison.OrdinalIgnoreCase));
            if (j < 0)
            {
                plan.Violations.Add(V(ViolationSeverity.Warn, ViolationCodes.SinkClosed,
                    $"{supplies[i].SourceName} 手工指定去向「{pin}」不在可用去向内，改按运输功最优重新分配。"));
                continue;
            }
            if (!Compatible(i, j))
            {
                plan.Violations.Add(V(ViolationSeverity.Error, ViolationCodes.MaterialRejected,
                    $"{supplies[i].SourceName} 手工指定「{sinks[j].Node.Name}」不接纳{supplies[i].Spec.Name}，改按可行汇重新分配。"));
                continue;
            }
            double take = Math.Min(remain[i], sinks[j].MaxIntake(supplies[i].Spec));
            if (take > Eps)
            {
                sinks[j].Take(supplies[i].Spec, take);
                remain[i] -= take;
                allocs.Add(new Alloc { S = i, J = j, M3 = take, Pinned = true });
            }
            if (remain[i] > Eps)
                plan.Violations.Add(V(ViolationSeverity.Warn, ViolationCodes.DumpCapacity,
                    $"{supplies[i].SourceName} 指定去向「{sinks[j].Node.Name}」本期只接得下 {take / 1e4:0.##}万m³，" +
                    $"余 {remain[i] / 1e4:0.##}万m³ 按运输功最优改投其它汇。"));
        }

        // ── 1. 最小元素法贪心 ──
        var arcs = new List<(int i, int j, double c)>(n * m);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                if (Compatible(i, j)) arcs.Add((i, j, Cost(i, j)));
        arcs.Sort((a, b) => a.c.CompareTo(b.c));

        GreedyFill(arcs, supplies, sinks, remain, allocs);

        // ── 2. 2-opt 对偶交换局部改进 ──
        plan.GreedyWorkTKm = allocs.Sum(a => supplies[a.S].Spec.ToTonnage(a.M3) * Eq(a.S, a.J));
        plan.ImproveMoves = Improve(p, supplies, sinks, allocs, Eq, Compatible);

        // ── 3. 改进腾出的容量再灌一次剩余量 ──
        if (remain.Any(r => r > Eps)) GreedyFill(arcs, supplies, sinks, remain, allocs);

        // ── 4. 归并同 (源,汇) 分配 → 物料流六元组 ──
        foreach (var g in allocs.Where(a => a.M3 > Eps).GroupBy(a => (a.S, a.J)))
        {
            int i = g.Key.S, j = g.Key.J;
            double m3 = g.Sum(a => a.M3);
            var node = sinks[j].Node;
            plan.Flows.Add(new MaterialFlow
            {
                Period = p.Period,
                SourceId = supplies[i].SourceId, SourceName = supplies[i].SourceName,
                SourceBenchElevationM = supplies[i].BenchElevationM,
                MaterialCode = supplies[i].MaterialCode, InSituM3 = Math.Round(m3, 2),
                SinkId = node.Id, SinkName = node.Name, SinkKind = node.Kind,
                HaulKm = Math.Round(Km(i, j), 3), EquivHaulKm = Math.Round(Eq(i, j), 3),
            });
        }
        plan.Flows = plan.Flows.OrderBy(f => f.SourceName).ThenBy(f => f.SinkName).ToList();

        // ── 5. 汇侧受载明细 + 库容/通过能力见顶标记 ──
        foreach (var st in sinks)
        {
            if (st.UsedInSitu <= Eps) continue;
            st.VolumeBound = !double.IsInfinity(st.CapDumpM3) && st.CapDumpM3 <= Math.Max(1.0, st.RemainBeforeM3 * 1e-4);
            st.ThroughputBound = !double.IsInfinity(st.CapT) && st.CapT <= Math.Max(1.0, st.UsedT * 1e-4);
            plan.SinkLoads.Add(new SinkLoad
            {
                SinkId = st.Node.Id, SinkName = st.Node.Name, Kind = st.Node.Kind,
                InSituM3 = st.UsedInSitu, DumpM3 = st.UsedDump, TonnageT = st.UsedT,
                RemainingBeforeM3 = st.RemainBeforeM3,
                RemainingAfterM3 = double.IsInfinity(st.CapDumpM3) ? double.PositiveInfinity : st.CapDumpM3,
                ThroughputCapT = st.Node.ThroughputCapT(EffectiveHours(st.Node, p.PeriodHours)),
                VolumeBound = st.VolumeBound, ThroughputBound = st.ThroughputBound,
                AdvanceM = st.Node.IsDumping ? st.Node.AdvanceMetersFor(st.UsedDump) : 0,
            });
            if (p.CommitToSinks) p.Sinks.AddFilled(st.Node.Id, st.UsedDump);
        }

        // ── 6. 指标汇总 ──
        plan.AssignedInSituM3 = plan.Flows.Sum(f => f.InSituM3);
        plan.UnassignedInSituM3 = Math.Max(0, remain.Sum());
        plan.TotalTransportWorkTKm = plan.Flows.Sum(f => f.TransportWorkTKm);
        double tt = plan.Flows.Sum(f => f.TonnageT);
        plan.WeightedAvgHaulKm = tt <= Eps ? 0 : plan.TotalTransportWorkTKm / tt;
        double allDump = plan.Flows.Where(f => f.SinkKind.IsDumping()).Sum(f => f.DumpM3);
        plan.InternalDumpPct = allDump <= Eps ? 0
            : plan.Flows.Where(f => f.SinkKind == SinkKind.InternalDump).Sum(f => f.DumpM3) / allDump * 100;

        // ── 7. 缺口诊断 + 表土专项校核 + 摘要 ──
        DiagnoseGaps(p, supplies, sinks, remain, plan, Compatible);
        CheckTopsoil(supplies, plan);
        plan.Explain = BuildExplain(p, plan, supplies, sinks, remain);
        return plan;
    }

    // ── 便利入口 1：从引擎盘子造问题并求解 ─────────────────────────────────

    /// <summary>
    /// 从 <see cref="ExploderConfig"/> 的采装面（Load）产出造流向分配问题并求解。
    /// 混采面按 <see cref="MaterialMix.Split"/> 拆成多份供给（煤走破碎站/煤仓、岩走排土场）。
    /// </summary>
    /// <param name="respectManual">true=已手工填了去向的面锁定该去向（只吃容量、不参与优化）。</param>
    public static FlowPlan AssignForConfig(ExploderConfig cfg, bool respectManual = true)
    {
        var boot = new List<PlanViolation>();

        // 去向登记簿为空 → 先问去向装载器（台账），再退内置样例；无论哪种都明确告知来源
        if (cfg.Sinks == null || cfg.Sinks.All.Count == 0)
        {
            var (reg, label) = PeerEngines.LoadSinks();
            cfg.Sinks = reg ?? SinkRegistry.Sample();
            boot.Add(V(ViolationSeverity.Info, ViolationCodes.NoDestination,
                $"盘子未带去向登记簿，已自动装载：{(reg != null ? label : "内置样例去向（北排土场/内排场/表土堆场/破碎站/原煤仓）")}。"));
        }

        var supplies = new List<FlowSupply>();
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > Eps))
        {
            string srcId = string.IsNullOrWhiteSpace(f.EngineeringPositionId) ? f.Zone : f.EngineeringPositionId;
            foreach (var (spec, m3) in f.ResolvedMix.Split(f.DayTargetM3))
            {
                if (m3 <= Eps) continue;
                supplies.Add(new FlowSupply
                {
                    SourceId = srcId, SourceName = f.Zone, BenchElevationM = f.BenchElevationM,
                    MaterialCode = spec.Code, InSituM3 = m3,
                    // 锁定按【物料】判，不再按面判：混采面的煤锁破碎站、岩仍参与优化
                    PinnedSinkId = respectManual ? PinnedIdFor(cfg.Sinks!, f, spec) : "",
                    Tag = f,
                });
            }
        }

        var p = new FlowProblem
        {
            Supplies = supplies, Sinks = cfg.Sinks!,
            PeriodHours = PeriodHoursOf(cfg), Period = cfg.Period,
            // 接线点：分项自带的手填运距（最可信）→ HaulResolver（软接，见 PeerEngines.UsableLeg 的采信规则）
            HaulKmProvider = (s, k) =>
            {
                double v = SplitHaulKm(s, k, equiv: false);
                return v > Eps ? v : PeerEngines.HaulKm(s.Tag, k, IsOwnDestination(s, k));
            },
            EquivHaulKmProvider = (s, k) =>
            {
                double v = SplitHaulKm(s, k, equiv: true);
                return v > Eps ? v : PeerEngines.HaulEquivKm(s.Tag, k, IsOwnDestination(s, k));
            },
        };

        var plan = Assign(p);
        if (boot.Count > 0) plan.Violations.InsertRange(0, boot);
        return plan;
    }

    // ── 便利入口 2：把分配结果写回各作业面 ─────────────────────────────────

    /// <summary>求解并把去向/运距写回 cfg.Faces（默认不覆盖已手工指定去向的面）。</summary>
    public static void ApplyTo(ExploderConfig cfg, bool overwriteManual = false)
        => ApplyToAndReport(cfg, overwriteManual);

    /// <summary>
    /// 同 <see cref="ApplyTo"/>，但返回完整方案（调用方要拿校核条目时用）。
    /// <para>
    /// 写回是**双层**的：
    ///  · <see cref="FaceInput.Splits"/>：每物料一条 <see cref="MaterialDestination"/>——这是完整账。
    ///    混采面（煤7∶岩3）煤去破碎站、岩去排土场，一条任务多个去向，不能拆面（同一台铲会撞成"设备双占"）。
    ///    次要物料的去向此前只能丢掉或错记成主去向，报表侧因此少一截运距覆盖率。
    ///  · 单去向五字段：仍写**主去向**（份额最大者），供单据抬头、甘特分组与历史消费方兼容。
    /// </para>
    /// <para>
    /// 同一物料被贪心拆到多个汇时按量加权并成一条（去向取量最大者、运距取加权平均），
    /// 拆分详情另记 Info 级 <c>去向拆分</c>。<paramref name="overwriteManual"/>=false 时
    /// **已手工指定的分项逐条保留**（只在其运距为空时补上求解值）。
    /// </para>
    /// </summary>
    public static FlowPlan ApplyToAndReport(ExploderConfig cfg, bool overwriteManual = false)
    {
        var plan = AssignForConfig(cfg, respectManual: !overwriteManual);

        var bySource = plan.Flows.GroupBy(f => f.SourceId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load))
        {
            string srcId = string.IsNullOrWhiteSpace(f.EngineeringPositionId) ? f.Zone : f.EngineeringPositionId;

            // 进函数前就存在的分项 = 人工指定，overwriteManual=false 时逐条保留
            var manual = overwriteManual
                ? new List<MaterialDestination>()
                : f.Splits.Where(s => s.HasDestination).Select(s => s.Clone()).ToList();

            if (!bySource.TryGetValue(srcId, out var flows) || flows.Count == 0)
            {
                if (f.DayTargetM3 > Eps && !f.AllMaterialsRouted)
                    plan.Violations.Add(V(ViolationSeverity.Warn, ViolationCodes.NoDestination, $"{f.Zone} 未分配到任何去向。"));
                continue;
            }

            // ── ① 分项写回：每物料一条，各挂各自的去向与运距 ──
            var mix = f.ResolvedMix.Normalized();
            var splits = new List<MaterialDestination>();
            foreach (var sh in mix.Shares)
            {
                if (sh.Fraction <= Eps) continue;
                var mine = flows.Where(x => Same(x.MaterialCode, sh.MaterialCode)).ToList();

                var keep = manual.FirstOrDefault(x => Same(x.MaterialCode, sh.MaterialCode));
                if (keep != null)
                {
                    keep.Fraction = sh.Fraction;

                    // 人工给的是「去向」，运距该由求解补齐：实距与等效运距**分别**判空。
                    // 只判 EffectiveHaulKm 会漏掉"填了实距、没填等效运距"这种最常见的情形——
                    // 等效运距是坡阻折算出来的派生量（内排下排的降本正出在这一截），人工本就不会填。
                    var same = mine.FirstOrDefault(x => Same(x.SinkId, keep.DestinationId)) ?? mine.FirstOrDefault();
                    if (same != null)
                    {
                        if (keep.HaulKm <= Eps) keep.HaulKm = Math.Round(same.HaulKm, 3);
                        if (keep.EquivHaulKm <= Eps) keep.EquivHaulKm = Math.Round(same.EquivHaulKm, 3);
                    }
                    splits.Add(keep);
                    continue;
                }

                if (mine.Count == 0) continue;      // 该物料没解出去向：宁可缺一条，也不造一条假去向
                splits.Add(MergeSplit(f, sh.MaterialCode, sh.Fraction, mine, plan));
            }
            f.Splits = splits;

            // ── ② 主去向（单去向五字段）：实方占比最大的汇；手工指定过的面不覆盖 ──
            var byS = flows.GroupBy(x => x.SinkId)
                           .Select(g => (M3: g.Sum(x => x.InSituM3), Ref: g.First()))
                           .OrderByDescending(x => x.M3).ToList();
            var main = byS[0].Ref;
            double total = byS.Sum(x => x.M3);

            bool manualMain = !overwriteManual && PinnedIdOf(cfg.Sinks!, f).Length > 0;
            if (!manualMain)
            {
                f.DestinationId = main.SinkId;
                f.DestinationName = main.SinkName;
                f.DestinationKind = main.SinkKind;
                f.HaulDistanceKm = Math.Round(main.HaulKm, 3);
                f.EquivHaulKm = Math.Round(main.EquivHaulKm, 3);
            }

            if (byS.Count > 1)
                plan.Violations.Add(V(ViolationSeverity.Info, CodeFlowSplit,
                    $"{f.Zone} 本期分投 {byS.Count} 个去向（" +
                    string.Join("、", byS.Select(x => $"{x.Ref.SinkName} {x.M3 / Math.Max(Eps, total) * 100:0}%")) +
                    $"）；面上主去向记「{(string.IsNullOrWhiteSpace(f.DestinationName) ? main.SinkName : f.DestinationName)}」，" +
                    $"完整分项已写回作业面：{string.Join(" · ", splits.Select(s => s.Caption))}。"));
        }

        // 排土面（Dump）：日目标 = 本期投向该汇的占容方（DerivedFromInbound 时才覆盖，保采排守恒）。
        // 这里给出的是**分项账**——TaskExploder.DeriveDumpTargets 会以 cfg.Flows() 对账，一致即不再覆盖。
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Dump && f.DerivedFromInbound))
        {
            var load = plan.SinkLoads.FirstOrDefault(s => MatchesFace(s, f));
            if (load != null) f.DayTargetM3 = Math.Round(load.DumpM3);
        }

        return plan;
    }

    /// <summary>
    /// 把同一物料的若干条流并成一条分项去向：去向取量最大者，运距按实方量加权平均
    /// （同物料下实方权重与吨量权重等价，故不必再折吨）。拆到多汇时另记一条 Info 说明。
    /// </summary>
    private static MaterialDestination MergeSplit(FaceInput f, string code, double fraction,
                                                  List<MaterialFlow> mine, FlowPlan plan)
    {
        double tot = mine.Sum(x => x.InSituM3);
        var top = mine.OrderByDescending(x => x.InSituM3).First();
        double km = tot <= Eps ? top.HaulKm : mine.Sum(x => x.InSituM3 * x.HaulKm) / tot;
        double eq = tot <= Eps ? top.EquivHaulKm : mine.Sum(x => x.InSituM3 * x.EquivHaulKm) / tot;

        if (mine.Count > 1)
            plan.Violations.Add(V(ViolationSeverity.Info, CodeFlowSplit,
                $"{f.Zone} 的{MaterialCatalog.Resolve(code).Name}本期拆投 {mine.Count} 个汇（" +
                string.Join("、", mine.OrderByDescending(x => x.InSituM3)
                                      .Select(x => $"{x.SinkName} {x.InSituM3 / Math.Max(Eps, tot) * 100:0}% · {x.HaulKm:0.##}km")) +
                $"）；分项去向按量最大者「{top.SinkName}」记，运距取加权平均 {km:0.##} km。"));

        return new MaterialDestination
        {
            MaterialCode = code, Fraction = fraction,
            DestinationId = top.SinkId, DestinationName = top.SinkName, DestinationKind = top.SinkKind,
            HaulKm = Math.Round(km, 3), EquivHaulKm = Math.Round(eq, 3),
        };
    }

    // ── 贪心 / 改进 / 诊断 ─────────────────────────────────────────────────

    private static void GreedyFill(List<(int i, int j, double c)> arcs, List<FlowSupply> sup,
                                   List<SinkState> sinks, double[] remain, List<Alloc> allocs)
    {
        foreach (var (i, j, _) in arcs)
        {
            if (remain[i] <= Eps) continue;
            double take = Math.Min(remain[i], sinks[j].MaxIntake(sup[i].Spec));
            if (take <= Eps) continue;
            sinks[j].Take(sup[i].Spec, take);
            remain[i] -= take;
            allocs.Add(new Alloc { S = i, J = j, M3 = take });
        }
    }

    /// <summary>
    /// 2-opt 对偶交换：任取两条非锁定流，按 δ=min(q1,q2) 对等交换目的地。
    /// 对等交换的好处：两个源的产量、两个汇的「进料条数」都不变，只有物料种类换了，
    /// 因此容量校验只需看两种物料的 Kr / ρ 之差（同物料交换恒可行）。
    /// </summary>
    private static int Improve(FlowProblem p, List<FlowSupply> sup, List<SinkState> sinks,
                               List<Alloc> allocs, Func<int, int, double> eq, Func<int, int, bool> compatible)
    {
        int moves = 0, limit = Math.Max(0, p.MaxImproveMoves);
        if (limit == 0) return 0;

        long scanBudget = 2_000_000;   // 评估次数上限（防病态规模）
        bool improved = true;

        while (improved && moves < limit && scanBudget > 0)
        {
            improved = false;
            for (int a = 0; a < allocs.Count && moves < limit; a++)
            {
                var f1 = allocs[a];
                if (f1.Pinned || f1.M3 <= Eps) continue;

                for (int b = a + 1; b < allocs.Count; b++)
                {
                    if (--scanBudget <= 0) break;
                    var f2 = allocs[b];
                    if (f2.Pinned || f2.M3 <= Eps || f2.J == f1.J) continue;

                    var sp1 = sup[f1.S].Spec; var sp2 = sup[f2.S].Spec;
                    if (!compatible(f1.S, f2.J) || !compatible(f2.S, f1.J)) continue;

                    double delta = Math.Min(f1.M3, f2.M3);
                    if (delta <= Eps) continue;

                    // ΔW = δ·[ρ1(L1→2 − L1→1) + ρ2(L2→1 − L2→2)]
                    double dW = delta * (sp1.InSituDensityTPerM3 * (eq(f1.S, f2.J) - eq(f1.S, f1.J))
                                       + sp2.InSituDensityTPerM3 * (eq(f2.S, f1.J) - eq(f2.S, f2.J)));
                    if (dW >= -CostEps) continue;

                    // 容量校验：j1 退 δ 的物料1、收 δ 的物料2；j2 反之
                    if (!CanSwap(sinks[f1.J], sp1, sp2, delta) || !CanSwap(sinks[f2.J], sp2, sp1, delta)) continue;

                    ApplySwap(sinks[f1.J], sp1, sp2, delta);
                    ApplySwap(sinks[f2.J], sp2, sp1, delta);

                    // 记账：整条搬 or 拆一部分搬
                    int j1 = f1.J, j2 = f2.J;
                    if (Math.Abs(f1.M3 - delta) <= Eps) f1.J = j2;
                    else { f1.M3 -= delta; allocs.Add(new Alloc { S = f1.S, J = j2, M3 = delta }); }
                    if (Math.Abs(f2.M3 - delta) <= Eps) f2.J = j1;
                    else { f2.M3 -= delta; allocs.Add(new Alloc { S = f2.S, J = j1, M3 = delta }); }

                    moves++; improved = true;
                    break;      // 首次改进即接受，重开扫描（first-improvement）
                }
                if (scanBudget <= 0) break;
            }
        }
        return moves;
    }

    /// <summary>汇 st 退掉 δ 的 outSpec、收进 δ 的 inSpec 后，两维容量是否仍满足。</summary>
    private static bool CanSwap(SinkState st, MaterialSpec outSpec, MaterialSpec inSpec, double delta)
    {
        double dDump = delta * (inSpec.ResidualSwellFactor - outSpec.ResidualSwellFactor);
        double dT = delta * (inSpec.InSituDensityTPerM3 - outSpec.InSituDensityTPerM3);
        if (!double.IsInfinity(st.CapDumpM3) && dDump > st.CapDumpM3 + Eps) return false;
        if (!double.IsInfinity(st.CapT) && dT > st.CapT + Eps) return false;
        return true;
    }

    private static void ApplySwap(SinkState st, MaterialSpec outSpec, MaterialSpec inSpec, double delta)
    {
        st.Give(outSpec, delta);
        st.Take(inSpec, delta);
    }

    /// <summary>分不完 → 按「根本没兼容汇 / 库容满 / 卸点满」定性，逐物料记校核。</summary>
    private static void DiagnoseGaps(FlowProblem p, List<FlowSupply> sup, List<SinkState> sinks,
                                     double[] remain, FlowPlan plan, Func<int, int, bool> compatible)
    {
        var gaps = new List<int>();
        for (int i = 0; i < sup.Count; i++) if (remain[i] > Math.Max(Eps, sup[i].InSituM3 * 1e-6)) gaps.Add(i);
        if (gaps.Count == 0) return;

        plan.Feasible = false;

        foreach (var g in gaps.GroupBy(i => sup[i].MaterialCode))
        {
            var spec = MaterialCatalog.Resolve(g.Key);
            double left = g.Sum(i => remain[i]);
            string faces = string.Join("、", g.Select(i => sup[i].SourceName).Distinct());

            bool anyCompatible = false, anyVolBound = false, anyTphBound = false;
            for (int j = 0; j < sinks.Count; j++)
            {
                int i0 = g.First();
                if (!compatible(i0, j)) continue;
                anyCompatible = true;
                if (!double.IsInfinity(sinks[j].CapDumpM3) && sinks[j].CapDumpM3 <= Eps) anyVolBound = true;
                if (!double.IsInfinity(sinks[j].CapT) && sinks[j].CapT <= Eps) anyTphBound = true;
            }

            string code = !anyCompatible ? ViolationCodes.MaterialRejected
                        : anyVolBound ? ViolationCodes.DumpCapacity
                        : anyTphBound ? ViolationCodes.SinkThroughput
                        : ViolationCodes.DumpCapacity;

            string why = !anyCompatible
                ? $"无任何在用去向接纳{spec.Name}（允许去向：{string.Join("/", spec.AllowedSinks.Select(k => k.Label()))}）"
                : anyVolBound ? "全部可行去向库容已满"
                : anyTphBound ? "全部可行去向本期通过能力已满" : "可行去向容量不足";

            plan.Violations.Add(V(ViolationSeverity.Error, code,
                $"{faces} 的{spec.Name}尚有 {left / 1e4:0.##}万m³（占容 {spec.ToDumpM3(left) / 1e4:0.##}万m³）无处可去：{why}。"));
        }
    }

    /// <summary>表土专项：复垦资源必须落表土堆场，串到别处或没堆场都要报。</summary>
    private static void CheckTopsoil(List<FlowSupply> sup, FlowPlan plan)
    {
        if (!sup.Any(s => s.Spec.Kind == MaterialKind.Topsoil)) return;
        var ts = plan.Flows.Where(f => f.Spec.Kind == MaterialKind.Topsoil).ToList();
        double got = ts.Sum(f => f.InSituM3), want = sup.Where(s => s.Spec.Kind == MaterialKind.Topsoil).Sum(s => s.InSituM3);
        var stray = ts.Where(f => f.SinkKind != SinkKind.TopsoilYard).ToList();

        if (stray.Count > 0)
            plan.Violations.Add(V(ViolationSeverity.Error, ViolationCodes.MaterialRejected,
                $"表土被排入非表土堆场（{string.Join("、", stray.Select(f => f.SinkName).Distinct())}），复垦资源流失，须改投表土堆场。"));
        else if (got >= want - Eps)
            plan.Violations.Add(V(ViolationSeverity.Info, CodeTopsoilOk,
                $"表土 {want / 1e4:0.##}万m³ 全部进表土堆场（{string.Join("、", ts.Select(f => f.SinkName).Distinct())}），复垦资源已单独堆存。"));
    }

    private static string BuildExplain(FlowProblem p, FlowPlan plan, List<FlowSupply> sup,
                                       List<SinkState> sinks, double[] remain)
    {
        var s = new System.Text.StringBuilder();
        s.Append($"总运输功 {plan.TotalTransportWorkTKm / 1e4:0.##} 万t·km，")
         .Append($"加权平均运距 {plan.WeightedAvgHaulKm:0.##} km，")
         .Append($"内排率 {plan.InternalDumpPct:0.#}%");

        if (plan.ImproveMoves > 0 && plan.GreedyWorkTKm > CostEps)
        {
            double cut = (plan.GreedyWorkTKm - plan.TotalTransportWorkTKm) / plan.GreedyWorkTKm * 100;
            s.Append($"（贪心 {plan.GreedyWorkTKm / 1e4:0.##} 万t·km → 局部改进 {plan.ImproveMoves} 次，降 {Math.Max(0, cut):0.##}%）");
        }
        s.Append('。');

        if (plan.Feasible)
        {
            s.Append($"{plan.Flows.Count} 条流全部落实，{plan.AssignedInSituM3 / 1e4:0.##}万m³ 产出均有去处。");
            return s.ToString();
        }

        // 缺口播报：缺多少 + 各汇还剩多少 + 三条常规对策
        var byMat = new List<string>();
        for (int i = 0; i < sup.Count; i++)
        {
            if (remain[i] <= Eps) continue;
            byMat.Add($"{sup[i].Spec.Name} {remain[i] / 1e4:0.##}万m³");
        }
        s.Append($"【缺口】{string.Join("、", byMat)} 无处可排");

        var rest = sinks.Where(x => x.Node.IsDumping && !double.IsInfinity(x.CapDumpM3))
                        .Select(x => $"{x.Node.Name}余 {x.CapDumpM3 / 1e4:0.##}万m³").ToList();
        if (rest.Count > 0) s.Append($"（{string.Join("、", rest)}）");

        bool internalIdle = sinks.Any(x => x.Node.Kind == SinkKind.InternalDump && x.UsedInSitu <= Eps);
        bool internalClosed = p.Sinks.All.Any(x => x.Kind == SinkKind.InternalDump && !sinks.Any(y => y.Node.Id == x.Id));
        s.Append("。建议：")
         .Append(internalClosed ? "① 启用内排土场（采空区已具备条件即可开区，内排下排还能同步降运输功）；"
                                : internalIdle ? "① 加大内排投放（内排为下排，等效运距更短）；" : "① 扩容/加高现有排土场台阶；")
         .Append("② 新增排土场或提升卸点通过能力；③ 调减本期剥离量并回摊后续期次。");
        return s.ToString();
    }

    // ── 运距与可用性 ───────────────────────────────────────────────────────

    /// <summary>
    /// OD 运距三层兜底：① 外部 Provider（路网/HaulResolver）→ ② 汇的 FallbackHaulKm。
    /// 等效运距：① 外部 Provider → ② 按源汇高程差做坡阻折算 → ③ 内排给缺省折减（简化）。
    /// </summary>
    private static void ResolveHaul(FlowProblem p, FlowSupply s, SinkNode node, out double km, out double eq)
    {
        km = 0;
        try { if (p.HaulKmProvider != null) km = p.HaulKmProvider(s, node); } catch { km = 0; }
        if (double.IsNaN(km) || km <= Eps) km = Math.Max(0, node.FallbackHaulKm);
        // 兜底的兜底：运距为 0 会让该汇在目标函数里变成"免费"，贪心必然全灌过去 —— 宁可用全局默认值
        if (km <= Eps) km = DefaultHaulKm;

        eq = 0;
        try { if (p.EquivHaulKmProvider != null) eq = p.EquivHaulKmProvider(s, node); } catch { eq = 0; }
        if (double.IsNaN(eq) || eq <= Eps) eq = EquivOf(s, node, km);
        if (eq <= Eps) eq = km;
    }

    /// <summary>
    /// 等效运距内置折算（理论报告 §3.2 的一阶线性坡阻模型，取重车方向）：
    ///   上排（汇高于源）L_eq = L(1 + k_u·g)；下排（汇低于源）L_eq = L·max(1 − k_d·g, 0.5)。
    /// 高程数据缺失（汇 Z 未录）时**简化**：内排按 0.90 折减（内排多为下排、重车下坡），其余按实距。
    /// 这一条正是「内排降本」的量化出处——不是里程短 20%，是 L_eq 再降一截。
    /// </summary>
    private static double EquivOf(FlowSupply s, SinkNode node, double km)
    {
        if (km <= Eps) return km;
        bool hasZ = Math.Abs(node.Z) > Eps;          // 汇未录高程（样例台账 Z=0）→ 不用高程模型
        if (hasZ)
        {
            double dz = node.Z - s.BenchElevationM;  // >0：汇高于源 = 上排
            double g = Math.Min(MaxGrade, Math.Abs(dz) / (km * 1000.0));
            double f = dz > 0 ? 1 + UphillResistK * g : Math.Max(DownhillFloor, 1 - DownhillReliefK * g);
            return km * f;
        }
        return node.Kind == SinkKind.InternalDump ? km * InternalDumpEquivFactor : km;
    }

    /// <summary>可行汇：在用 + 期次已到 + 尚有库容；并把两维容量折成本期可用额度。</summary>
    private static List<SinkState> BuildSinkStates(FlowProblem p, FlowPlan plan)
    {
        var list = new List<SinkState>();
        if (p.Sinks == null) return list;

        foreach (var node in p.Sinks.All)
        {
            if (!node.IsActive)
            {
                plan.Violations.Add(V(ViolationSeverity.Info, ViolationCodes.SinkClosed,
                    $"{node.Caption} 状态 {node.Status}，本期不参与分配。"));
                continue;
            }
            if (!PeriodReached(p.Period, node.OpenFromPeriod))
            {
                plan.Violations.Add(V(ViolationSeverity.Info, ViolationCodes.SinkClosed,
                    $"{node.Caption} 自 {node.OpenFromPeriod} 起启用，本期（{p.Period}）尚未开放。"));
                continue;
            }
            if (node.RemainingM3 <= Eps)
            {
                plan.Violations.Add(V(ViolationSeverity.Warn, ViolationCodes.DumpCapacity,
                    $"{node.Caption} 库容已满（{node.CapacityCaption}），本期不再接收。"));
                continue;
            }
            if (node.FallbackHaulKm <= Eps)
                plan.Violations.Add(V(ViolationSeverity.Warn, ViolationCodes.HaulMissing,
                    $"{node.Caption} 未录兜底运距，路网若也解不出将按默认 {DefaultHaulKm:0.#}km 估算，运输功可信度下降。"));

            // 库容口径：只有排弃类去向吃库容（占容方）；破碎站/煤仓是通过型，不占库容。
            // （储煤场若要按库存管控，需另行接入库存台账——此处按通过型处理。）
            double capVol = node.IsDumping && node.IsCapacityLimited ? node.RemainingM3 : double.PositiveInfinity;
            double capT = node.ThroughputCapT(EffectiveHours(node, p.PeriodHours));

            list.Add(new SinkState
            {
                Node = node, Index = list.Count,
                CapDumpM3 = capVol, CapT = capT,
                RemainBeforeM3 = capVol,
            });
        }
        return list;
    }

    /// <summary>本期该汇的有效受排小时（受开放时窗裁剪）。</summary>
    private static double EffectiveHours(SinkNode s, double periodHours)
    {
        if (periodHours <= 0) return 0;                       // 0 → ThroughputCapT 返回 +∞（不限）
        double win = s.OpenToHour - s.OpenFromHour;
        if (win <= 0 || win >= 24) return periodHours;
        return Math.Min(periodHours, win);
    }

    /// <summary>期次是否已到（"2026-06-17 周二" ≥ "2026-06"）。判不了一律放行，宁可多算不可漏算。</summary>
    private static bool PeriodReached(string? period, string? openFrom)
    {
        if (string.IsNullOrWhiteSpace(openFrom)) return true;
        if (string.IsNullOrWhiteSpace(period)) return true;
        string a = period!.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string b = openFrom!.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!a.Any(char.IsDigit) || !b.Any(char.IsDigit)) return true;
        int len = Math.Min(a.Length, b.Length);
        return string.CompareOrdinal(a[..len], b[..len]) >= 0;
    }

    /// <summary>本期有效作业小时（滚动重排时只算 FromHour 之后）。</summary>
    private static double PeriodHoursOf(ExploderConfig cfg)
    {
        double h = 0;
        foreach (var sh in cfg.Shifts) h += Math.Max(0, sh.End - Math.Max(sh.Start, cfg.FromHour));
        if (h <= Eps) h = cfg.FromHour > 0 ? Math.Max(0, 24 - cfg.FromHour) : 24;
        return h;
    }

    /// <summary>
    /// 该候选汇上的「手填运距」是否可采信。
    /// <para>
    /// HaulResolver 的手填层读的是**面级**的 HaulDistanceKm/EquivHaulKm，那个数只描述
    /// 面的主去向这一条腿。所以两个条件都得满足：
    ///  ① 候选汇就是面的主去向；
    ///  ② 该物料没有另指的分项去向（混采面煤走破碎站 2.6km、岩走内排场 1.4km，
    ///     若拿面上的 2.6 去当岩→内排场那条腿的运距，内排"下排省功"这一截就被抹平了，
    ///     运输功、配车数、内排率会一起失真）。
    /// 分项自己填的运距另有 <see cref="SplitHaulKm"/> 直接采信，不走这里。
    /// </para>
    /// </summary>
    private static bool IsOwnDestination(FlowSupply s, SinkNode sink)
    {
        if (s.Tag is not FaceInput f) return false;
        if (!SameSink(f.DestinationId, f.DestinationName, sink)) return false;

        var d = f.DestinationFor(s.MaterialCode);
        return !d.HasDestination || SameSink(d.DestinationId, d.DestinationName, sink);
    }

    /// <summary>该物料在本面分项上手填的运距（且分项去向正是这个汇）。0=没填。</summary>
    private static double SplitHaulKm(FlowSupply s, SinkNode sink, bool equiv)
    {
        if (s.Tag is not FaceInput f) return 0;
        foreach (var d in f.Splits)
        {
            if (!Same(d.MaterialCode, s.MaterialCode)) continue;
            if (!SameSink(d.DestinationId, d.DestinationName, sink)) continue;
            return equiv ? d.EquivHaulKm : d.HaulKm;
        }
        return 0;
    }

    /// <summary>
    /// 该面该物料已手工锁定的汇 Id：
    ///  ① 面上写了这一物料的**分项去向** ⇒ 硬锁定（人工逐项指定，配错了就该实打实报出来）；
    ///  ② 否则回落面级主去向，但**仅当该汇接纳这一物料**才锁——混采面的主去向只对主物料成立，
    ///     拿它去锁次要物料（把岩锁进破碎站）只会凭空造出一条"物料不兼容"的假错。
    /// </summary>
    private static string PinnedIdFor(SinkRegistry reg, FaceInput f, MaterialSpec spec)
    {
        foreach (var d in f.Splits)
        {
            if (!Same(d.MaterialCode, spec.Code)) continue;
            var node = reg.Find(d.DestinationId)
                    ?? reg.All.FirstOrDefault(s => Same(s.Name, d.DestinationName));
            if (node != null) return node.Id;
        }

        string main = PinnedIdOf(reg, f);
        if (main.Length == 0) return "";
        var mainNode = reg.Find(main);
        return mainNode != null && mainNode.Accepts(spec) ? main : "";
    }

    private static bool SameSink(string? id, string? name, SinkNode sink)
        => Same(id, sink.Id) || Same(name, sink.Name);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>作业面已手工指定的去向 → 汇 Id（先认 Id，再按名字反查；查不到返回空=不锁定）。</summary>
    internal static string PinnedIdOf(SinkRegistry reg, FaceInput f)
    {
        if (!string.IsNullOrWhiteSpace(f.DestinationId) && reg.Find(f.DestinationId) != null) return f.DestinationId;
        if (!string.IsNullOrWhiteSpace(f.DestinationName))
        {
            var byName = reg.All.FirstOrDefault(s => string.Equals(s.Name, f.DestinationName, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName.Id;
        }
        return "";
    }

    /// <summary>排土作业面 ↔ 汇 的匹配：先认 DestinationId，再认名字。</summary>
    internal static bool MatchesFace(SinkLoad load, FaceInput f)
    {
        if (!string.IsNullOrWhiteSpace(f.DestinationId))
            return string.Equals(load.SinkId, f.DestinationId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(f.DestinationName))
            return string.Equals(load.SinkName, f.DestinationName, StringComparison.OrdinalIgnoreCase);
        return string.Equals(load.SinkName, f.Zone, StringComparison.OrdinalIgnoreCase)
            || string.Equals(load.SinkId, f.Zone, StringComparison.OrdinalIgnoreCase);
    }

    private static PlanViolation V(ViolationSeverity sev, string code, string msg)
        => new() { Severity = sev, Code = code, Message = msg };
}

// ─────────────────────────────────────────────────────────────────────────────
//  邻居引擎接线（同程序集，强类型直调 + try/catch 保护）——
//  接线点集中在本类：运距求解器 HaulResolver、编组求解器 FleetMatcher、去向装载器 SinkRegistryLoader
//  三者都是「解不出就降级」的设计，本类只负责判断「解出来的东西能不能用」。
// ─────────────────────────────────────────────────────────────────────────────
internal static class PeerEngines
{
    // 一格记忆：ResolveHaul 会为同一 (面,汇) 连着问「实距」和「等效运距」两次，
    // 而路网层每次调用都要跑一遍寻径——存住上一次结果即可省掉一半路径求解。
    private static FaceInput? _memoFace;
    private static SinkNode? _memoSink;
    private static HaulLeg? _memoLeg;

    private static HaulLeg? Leg(object? face, SinkNode sink)
    {
        if (face is not FaceInput f) return null;
        if (ReferenceEquals(f, _memoFace) && ReferenceEquals(sink, _memoSink)) return _memoLeg;

        HaulLeg? leg = null;
        try
        {
            leg = HaulResolver.Resolve(f, sink);
            if (leg != null && !leg.Feasible) leg = null;    // 三层全空 → 交回本包兜底
        }
        catch { leg = null; }

        _memoFace = f; _memoSink = sink; _memoLeg = leg;
        return leg;
    }

    /// <summary>
    /// 只在「这条腿的运距真的是针对该汇解出来的」时才采信外部结果：
    ///  · 路网层：源—汇两端都是真节点，实距与坡度等效运距都可信 → 采信；
    ///  · 手填层：面上填的那个数只对**该面自己的去向**成立，拿它去评估别的候选汇会让所有汇同价
    ///            （流向分配就此变瞎）→ 只在自家去向时采信；
    ///  · 兜底层：给的就是 sink.FallbackHaulKm 且等效运距=实距（无坡度折算），
    ///            与本包自带兜底同源但少了坡阻修正 → 交回本包算，内排「下排」的优势才体现得出来。
    /// </summary>
    private static HaulLeg? UsableLeg(object? face, SinkNode sink, bool ownDestination)
    {
        var leg = Leg(face, sink);
        if (leg == null) return null;
        string src = leg.Source ?? "";
        if (src.Contains("路网")) return leg;
        if (ownDestination && src.Contains("手填")) return leg;
        return null;
    }

    internal static double HaulKm(object? face, SinkNode sink, bool ownDestination)
        => UsableLeg(face, sink, ownDestination)?.Km ?? 0;
    internal static double HaulEquivKm(object? face, SinkNode sink, bool ownDestination)
        => UsableLeg(face, sink, ownDestination)?.EquivKm ?? 0;

    /// <summary>
    /// FleetMatcher.ApplyTo：运距变→T_c 变→n* 变（理论报告 §7 动态重编）。
    /// 它只写「建议车数 + 编组班产」，不动实配的 Trucks/MainEquipment。
    /// </summary>
    internal static bool TryRematchFleet(ExploderConfig cfg)
    {
        try { FleetMatcher.ApplyTo(cfg); return true; }
        catch { return false; }
    }

    /// <summary>SinkRegistryLoader.Current：从台账装载去向登记簿（未接通时它自己会回落样例）。</summary>
    internal static (SinkRegistry? Registry, string Label) LoadSinks()
    {
        try
        {
            var reg = SinkRegistryLoader.Current;
            string label = SinkRegistryLoader.LastSourceLabel;
            return reg != null && reg.All.Count > 0 ? (reg, label) : (null, label);
        }
        catch { return (null, ""); }
    }
}
