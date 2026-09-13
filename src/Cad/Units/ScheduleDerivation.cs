// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/ScheduleDerivation.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>采煤节奏 —— 派生轴之一。只改逐月煤量的<b>形状</b>，全期总量不变。</summary>
public enum CoalPace
{
    /// <summary>均衡：各月等量。销售/运输节奏最稳。</summary>
    Balanced = 0,
    /// <summary>抢产：前重后轻。早拿产量、早回款，但前期剥离压力大。</summary>
    Rush = 1,
    /// <summary>保守：后轻前重的反面（前轻后重）。给前期留出剥离窗口，年末冲量。</summary>
    Conservative = 2,
}

/// <summary>
/// 派生轴的取值集合。<b>方案 = 规则取值的组合</b>，不是解的枚举 ——
/// 每套方案仍是该规则组合下的唯一最优，方案之间的差异可归因到具体哪条规则。
/// </summary>
public sealed class ScheduleAxes
{
    /// <summary>备采保有月数 N（唯一驱动超前剥离的旋钮，规则 R35）。</summary>
    public int[] Lookahead = { 2, 3, 4 };
    /// <summary>剥离节奏。</summary>
    public StripPace[] StripPaces = { StripPace.Hug, StripPace.Level, StripPace.FrontLoad };
    /// <summary>
    /// 采煤节奏。<b>它是"没有真工作历时的兜底形状函数"</b>（线性 ramp、总量守恒、无物理驱动）。
    /// <para>有真工作历时，逐月煤量已经由 <c>年目标 × DispatchShape × (工作日/标准工作日) × 设备可用率</c>
    /// 摊过一遍，这条轴再摊就是**同一个决定做两遍**（见 `docs/短期剥采排功能分组.md` §七）。
    /// 会话层在检测到真工作历时会把它收成只留 <see cref="CoalPace.Balanced"/> ——
    /// 除非调用方<b>显式</b>指定（见 <see cref="CoalPacesExplicit"/>）。</para>
    /// </summary>
    public CoalPace[] CoalPaces = { CoalPace.Balanced, CoalPace.Rush, CoalPace.Conservative };

    /// <summary>
    /// 这条轴是调用方<b>显式</b>要的吗。true = 别替他收（有真工作历也照开，但会警告重复摊）。
    /// <para><b>为什么要这个位</b>：默认值与"用户特意选了这三个值"在数组上<b>长得一模一样</b>，
    /// 不留标记就分不出"没表态"和"表过态"—— 而悄悄关掉用户特意开的轴，
    /// 比让他多几套方案糟得多。</para>
    /// </summary>
    public bool CoalPacesExplicit;

    /// <summary>换一组采煤节奏，其余原样（不改调用方那份）。</summary>
    public ScheduleAxes WithCoalPaces(CoalPace[] paces) => new()
    {
        Lookahead = Lookahead, StripPaces = StripPaces, CoalPaces = paces,
        CoalPacesExplicit = CoalPacesExplicit, PairingStrategies = PairingStrategies,
        PaceSkew = PaceSkew, SyncRecoveryWithLookahead = SyncRecoveryWithLookahead,
    };

    /// <summary>
    /// 配对策略 —— <b>第 4 条轴</b>。空（默认）= 不派生这条轴。
    /// <para>要它得给 <c>Derive</c> 传排土输入；否则运输功/内排率两个维度<b>不参与打分</b>
    /// （而不是按 0 参与 —— 那会让"没算"看起来像"很差"）。</para>
    /// <para>⚠ 四轴全开是 3×3×3×3 = <b>81 套</b>。比选表 81 行没人看得完，而且轴之间会互相淹没归因。
    /// 建议：先用三轴定采剥，再固定采剥、单独比配对策略。</para>
    /// </summary>
    public PairingStrategy[] PairingStrategies = Array.Empty<PairingStrategy>();
    /// <summary>抢产/保守的偏斜幅度（0~0.6）。</summary>
    public double PaceSkew = 0.25;

    /// <summary>
    /// 派生 N 时让<b>回采煤量跟着走</b>（<c>回采 = N × 月均产量</c>）。默认开。
    /// <para><b>为什么必须开</b>：R34（超前上界 = 回采煤量前界）与 R35（备采保有 N 月）
    /// 说的是<b>同一个量</b> —— 回采煤量本来就 ≈ 2–3 个月产量。派生 N 却把回采煤量钉死，
    /// 大 N 的露煤需求会顶穿那个天花板、被静默夹回去，于是「剥离节奏」整条轴塌成一个值
    /// （实测跨度只剩 1.8 分，1/2/3 名指标一模一样）。<b>踩过一次。</b></para>
    /// </summary>
    public bool SyncRecoveryWithLookahead = true;

    public int SchemeCount => Math.Max(1, Lookahead.Length) * Math.Max(1, StripPaces.Length)
                            * Math.Max(1, CoalPaces.Length) * Math.Max(1, PairingStrategies.Length);
}

/// <summary>比选打分权重（归一，前端可实时重算）。方向感知：各维按"越大越好/越小越好"分别归一。</summary>
public sealed class DerivationWeights
{
    /// <summary>剥采比月间均衡（CV ↓）。</summary>
    public double RatioBalance = 1.0;
    /// <summary>峰值月剥离量（↓）—— 设备与人力的峰值压力。</summary>
    public double PeakShaving = 1.0;
    /// <summary>超前剥离储备（↓）—— 已剥未采 = 压资金。</summary>
    public double LeadStock = 1.0;
    /// <summary>断煤风险：最低备采保有月数（↑）。</summary>
    public double CoalSecurity = 1.0;
    /// <summary>本期剥离总量（↓）—— 直接成本。</summary>
    public double StripTotal = 1.0;
    /// <summary>运输功（↓）。<b>只在跑了采排配对时参与</b>。</summary>
    public double TransportWork = 1.0;
    /// <summary>内排率（↑）—— 省运距、省外排库容。<b>只在跑了采排配对时参与</b>。</summary>
    public double InternalRate = 1.0;

    public double Sum => RatioBalance + PeakShaving + LeadStock + CoalSecurity + StripTotal;
    /// <summary>跑了配对时的权重和（多算运输功与内排率两维）。</summary>
    public double SumWithPairing => Sum + TransportWork + InternalRate;
}

/// <summary>一套派生方案。</summary>
/// <remarks>
/// ⚠ <b>这些必须是【属性】不能是字段</b>。比选表要直接绑到这个类上，而 <b>WPF 绑定只认属性</b> ——
/// 写成字段的话，界面上那一列<b>永远是空的</b>，不报错、不抛异常、不写日志。
/// 本类原本全是字段，判据 S7（按 XAML 里的 <c>Binding</c> 路径逐条 <c>GetProperty</c>）
/// 在接界面时当场把它揪了出来。
/// </remarks>
public sealed class ScheduleScheme
{
    public string     Name { get; set; } = "";
    public int        Lookahead { get; set; }
    public StripPace  Strip { get; set; }
    public CoalPace   Coal { get; set; }
    public PairingStrategy? Pairing { get; set; }        // null = 没派生这条轴
    public MonthlyScheduleResult Result { get; set; } = null!;
    public DumpAllocationResult? Dump { get; set; }      // null = 没跑配对

    // ── 打分用的原始指标（方向在 Deriver 里定）──
    public double RatioCv { get; set; }
    public double PeakStripM3 { get; set; }
    public double MeanLeadStockM3 { get; set; }
    public double MinPreparedMonths { get; set; }
    public double TotalRockM3 { get; set; }
    /// <summary>运输功（t·km）—— 跑了配对才有；没跑时不参与打分。</summary>
    public double TransportWorkTKm { get; set; }
    /// <summary>内排率（%）—— 跑了配对才有；没跑时不参与打分。</summary>
    public double InternalRatePct { get; set; }
    public double UnplacedM3 { get; set; }
    /// <summary>0~100，方向感知归一加权。</summary>
    public double Score { get; set; }
    /// <summary>硬约束全过 且（跑了配对时）全部排得下。</summary>
    public bool   Feasible { get; set; }

    public string AxisText => $"N={Lookahead} · 剥离{StripText} · 采煤{CoalText}"
                            + (Pairing.HasValue ? $" · 配对{PairText}" : "");
    public string StripText => Strip switch { StripPace.Hug => "贴底", StripPace.FrontLoad => "前重", _ => "拉平" };
    public string CoalText  => Coal  switch { CoalPace.Rush => "抢产", CoalPace.Conservative => "保守", _ => "均衡" };
    public string PairText  => Pairing switch
    { PairingStrategy.InternalFirst => "内排优先", PairingStrategy.LevelCapacity => "库容均衡", _ => "运输功最小" };

    /// <summary>
    /// 内排率 / 运输功的<b>显示文本</b>：<see cref="Dump"/> 为 null（没跑配对）时给「—」。
    /// <para>直接绑数值的话这两列会显示 <b>0.0</b> —— 与"真的全外排 / 真的不用运"<b>看不出区别</b>。
    /// 打分那边已经按"这两维不参与"处理了（不是按 0 参与），比选表也不该在这儿露出一个 0。</para>
    /// </summary>
    public string InternalRateText => Dump == null ? "—" : InternalRatePct.ToString("0.0");
    /// <summary>运输功显示文本（万t·km）；没跑配对给「—」。</summary>
    public string TransportWorkText => Dump == null ? "—" : (TransportWorkTKm / 1e4).ToString("0.0");
}

/// <summary>某一条轴上，各取值的平均得分 —— 归因用。</summary>
public sealed class AxisAttribution
{
    public string Axis = "";
    public readonly List<(string Value, double MeanScore, int Count)> Values = new();
    public string Best => Values.Count == 0 ? "" : Values.OrderByDescending(v => v.MeanScore).First().Value;
    /// <summary>该轴的得分跨度 —— 越大说明这条轴越"说了算"。</summary>
    public double Spread => Values.Count == 0 ? 0 : Values.Max(v => v.MeanScore) - Values.Min(v => v.MeanScore);
}

public sealed class ScheduleDerivationResult
{
    public bool   Success;
    public string Error = "";
    public readonly List<ScheduleScheme> Schemes = new();
    public readonly List<ScheduleScheme> Failed = new();   // 解不出来的（含原因），不静默丢
    public readonly List<AxisAttribution> Attribution = new();
    public ScheduleScheme? Recommended;
    public int Attempted;

    /// <summary>
    /// <b>塌掉的轴</b>：所有取值给出同一套指标 —— 当前规则下这条轴没有活动空间。
    /// 必须如实报出来，否则比选表看着 27 行、其实只有 9 个不同答案，
    /// 用户会以为自己在 27 个选项里挑。
    /// </summary>
    public readonly List<string> CollapsedAxes = new();

    /// <summary>去重后真正不同的方案数（按指标四舍五入比对）。</summary>
    public int DistinctCount;

    /// <summary>是否跑了采排配对（决定运输功/内排率两维参不参与打分）。</summary>
    public bool PairingScored;
    /// <summary>需要用户看见的提示（轴被忽略、方案数过多、某套配对失败…）。</summary>
    public readonly List<string> Notes = new();
}

/// <summary>
/// 多方案派生与比选。
///
/// <para><b>为什么方案要从"规则取值"长出来</b>：第三步的拉绳是<b>唯一最优</b>解 —— 唯一解意味着没得比选。
/// 往求解器里加随机性或取次优解，出来的方案没法解释，比选表每一行都是"为什么是它？不知道"。
/// 改成穷举规则取值之后，每套方案仍是该组合下的最优，而差异<b>可归因</b>：
/// 「这套剥采比低是因为 N=2」「那套抗风险强是因为剥离节奏取了前重」。</para>
///
/// <para><b>为什么 27 套仍然瞬时</b>：三条轴一条都不碰块体/工作线/现状面/α，
/// 所以 <see cref="RockProfile"/> 全程共用一份，每套方案只跑四步管线（微秒级）。
/// <b>护栏：别把 α 或工作线放进派生轴</b> —— 那会变成 27 次块体全扫。</para>
///
/// <para><b>比选存在的前提是目标真的互相冲突</b>：剥采比要平就得提前剥（拉绳抬离 F），
/// 少压资金就得贴着 F 走。没有冲突的多方案是假多方案。</para>
/// </summary>
public static class ScheduleDeriver
{
    /// <param name="dumpInput">
    /// 排土输入。给了 <b>且</b> <c>axes.PairingStrategies</c> 非空时，每套方案顺带跑一遍采排配对，
    /// 运输功/内排率两个维度才进打分。不给则那两维<b>不参与</b>（而不是按 0 参与 ——
    /// 那会让"没算"看起来像"很差"）。
    /// </param>
    public static ScheduleDerivationResult Derive(MonthlyScheduleInput baseInput,
                                                  ScheduleAxes? axes = null,
                                                  DerivationWeights? weights = null,
                                                  DumpAllocationInput? dumpInput = null)
    {
        var r = new ScheduleDerivationResult();
        if (baseInput?.Rock == null) { r.Error = "没有岩量剖面"; return r; }
        if (baseInput.MonthCount <= 0) { r.Error = "没有月度煤量目标"; return r; }
        axes ??= new ScheduleAxes();
        weights ??= new DerivationWeights();

        var Ns = axes.Lookahead is { Length: > 0 } ? axes.Lookahead : new[] { baseInput.LookaheadMonths };
        var Sp = axes.StripPaces is { Length: > 0 } ? axes.StripPaces : new[] { baseInput.Pace };
        var Cp = axes.CoalPaces is { Length: > 0 } ? axes.CoalPaces : new[] { CoalPace.Balanced };
        bool doPair = dumpInput is { Slots.Count: > 0 } && axes.PairingStrategies is { Length: > 0 };
        var Pp = doPair ? axes.PairingStrategies : new PairingStrategy[] { default };
        r.PairingScored = doPair;
        if (axes.PairingStrategies is { Length: > 0 } && dumpInput is not { Slots.Count: > 0 })
            r.Notes.Add("⚠ 勾了配对策略这条轴，但没给排土输入 —— 该轴已忽略，运输功/内排率不参与打分。");
        if (Ns.Length * Sp.Length * Cp.Length * Pp.Length > 30)
            r.Notes.Add($"⚠ 派生 {Ns.Length * Sp.Length * Cp.Length * Pp.Length} 套 —— 比选表这么多行没人看得完，"
                      + "而且轴之间会互相淹没归因。建议先用三轴定采剥，再固定采剥单独比配对策略。");

        foreach (int n in Ns)
            foreach (var sp in Sp)
                foreach (var cp in Cp)
                    foreach (var pp in Pp)
                    {
                        r.Attempted++;
                        var inp = Clone(baseInput);
                        inp.LookaheadMonths = n;
                        inp.Pace = sp;
                        inp.CoalTargetWt = Shape(baseInput.CoalTargetWt, cp, axes.PaceSkew);
                        if (axes.SyncRecoveryWithLookahead && baseInput.MonthCount > 0)
                            inp.RecoveryTotalWt = n * baseInput.CoalTargetWt.Sum() / baseInput.MonthCount;

                        var res = MonthlyMineScheduler.Solve(inp);
                        var s = new ScheduleScheme
                        {
                            Lookahead = n, Strip = sp, Coal = cp,
                            Pairing = doPair ? pp : null, Result = res,
                        };
                        s.Name = s.AxisText;
                        if (!res.Success) { r.Failed.Add(s); continue; }

                        s.Feasible = res.AllChecksOk;
                        s.RatioCv = res.RatioCv;
                        s.PeakStripM3 = res.Months.Count == 0 ? 0 : res.Months.Max(m => m.RockM3);
                        s.MeanLeadStockM3 = res.Months.Count == 0 ? 0 : res.Months.Average(m => m.LeadStockM3);
                        s.MinPreparedMonths = res.Months.Count == 0 ? 0 : res.Months.Min(m => m.PreparedMonths);
                        s.TotalRockM3 = res.TotalRockM3;

                        if (doPair)
                        {
                            // 每套方案独立配对：库容是有状态的，必须给每套一份干净的位置副本，
                            // 否则第 2 套开始就在吃第 1 套排剩下的库容 —— 那是把 27 套算成了一条链。
                            var di = new DumpAllocationInput
                            {
                                Slots = dumpInput!.Slots.Select(CloneSlot).ToList(),
                                Materials = dumpInput.Materials, Strategy = pp,
                                HaulProvider = dumpInput.HaulProvider,
                                InternalCumCapM3 = dumpInput.InternalCumCapM3,
                            };
                            var d = DumpAllocator.Allocate(res, di);
                            if (d.Success)
                            {
                                s.Dump = d;
                                s.TransportWorkTKm = d.TotalTransportWorkTKm;
                                s.InternalRatePct = d.OverallInternalRatePct;
                                s.UnplacedM3 = d.TotalUnplacedM3;
                                s.Feasible = s.Feasible && d.AllPlaced;     // 排不下 = 落不了地
                            }
                            else { s.Feasible = false; r.Notes.Add($"{s.Name}：配对失败 —— {d.Error}"); }
                        }
                        r.Schemes.Add(s);
                    }

        if (r.Schemes.Count == 0)
        {
            r.Error = $"{r.Attempted} 套方案全部无解"
                    + (r.Failed.Count > 0 ? "；首个原因：" + r.Failed[0].Result.Error : "");
            return r;
        }

        Score(r, weights);
        Attribute(r);
        // 推荐 = 硬约束全过里分最高的；一套可行的都没有就退回分最高的并标出来
        r.Recommended = r.Schemes.Where(s => s.Feasible).OrderByDescending(s => s.Score).FirstOrDefault()
                     ?? r.Schemes.OrderByDescending(s => s.Score).First();
        r.Success = true;
        return r;
    }

    // ── 打分：方向感知归一加权（对标 LongTermComparer / ShortTermComparer 的范式）──
    private static void Score(ScheduleDerivationResult r, DerivationWeights w)
    {
        var list = r.Schemes;
        double wsum = r.PairingScored ? w.SumWithPairing : w.Sum; if (wsum <= 1e-9) wsum = 1;

        double NormLow(Func<ScheduleScheme, double> f, ScheduleScheme s)
        {
            double mn = list.Min(f), mx = list.Max(f);
            return mx - mn < 1e-12 ? 1.0 : (mx - f(s)) / (mx - mn);       // 越小越好
        }
        double NormHigh(Func<ScheduleScheme, double> f, ScheduleScheme s)
        {
            double mn = list.Min(f), mx = list.Max(f);
            return mx - mn < 1e-12 ? 1.0 : (f(s) - mn) / (mx - mn);       // 越大越好
        }

        foreach (var s in list)
        {
            double v = w.RatioBalance * NormLow(x => x.RatioCv, s)
                     + w.PeakShaving  * NormLow(x => x.PeakStripM3, s)
                     + w.LeadStock    * NormLow(x => x.MeanLeadStockM3, s)
                     + w.CoalSecurity * NormHigh(x => x.MinPreparedMonths, s)
                     + w.StripTotal   * NormLow(x => x.TotalRockM3, s);
            // 跑了配对才加这两维。没跑就不加 —— 按 0 参与会让"没算"看起来像"很差"。
            if (r.PairingScored)
                v += w.TransportWork * NormLow(x => x.TransportWorkTKm, s)
                   + w.InternalRate  * NormHigh(x => x.InternalRatePct, s);
            s.Score = 100.0 * v / wsum;
        }
    }

    private static DumpSlot CloneSlot(DumpSlot s) => new()
    {
        DumpName = s.DumpName, Level = s.Level, Order = s.Order, CapacityM3 = s.CapacityM3,
        Cx = s.Cx, Cy = s.Cy, Cz = s.Cz, IsInternal = s.IsInternal,
        AvailableFromMonth = s.AvailableFromMonth, HaulKm = s.HaulKm,
    };

    // ── 归因：每条轴上各取值的平均分，跨度越大说明这条轴越说了算 ──
    private static void Attribute(ScheduleDerivationResult r)
    {
        void Add(string axis, Func<ScheduleScheme, string> key)
        {
            var a = new AxisAttribution { Axis = axis };
            foreach (var g in r.Schemes.GroupBy(key).OrderBy(g => g.Key))
                a.Values.Add((g.Key, g.Average(s => s.Score), g.Count()));
            r.Attribution.Add(a);
        }
        Add("备采保有 N", s => $"N={s.Lookahead}");
        Add("剥离节奏",   s => s.StripText);
        Add("采煤节奏",   s => s.CoalText);
        if (r.PairingScored) Add("配对策略", s => s.PairText);
        r.Attribution.Sort((a, b) => b.Spread.CompareTo(a.Spread));   // 影响最大的排前面

        // 塌轴检测：把该轴的取值换掉、其余轴不动，若指标一字不差，这条轴就是摆设。
        string Sig(ScheduleScheme s) => $"{s.TotalRockM3:F3}|{s.RatioCv:F6}|{s.PeakStripM3:F3}|{s.MeanLeadStockM3:F3}"
                                      + (r.PairingScored ? $"|{s.TransportWorkTKm:F1}|{s.InternalRatePct:F2}" : "");
        void Collapse(string axis, Func<ScheduleScheme, string> axisKey, Func<ScheduleScheme, string> restKey)
        {
            bool anyDiff = r.Schemes.GroupBy(restKey)
                .Any(g => g.Select(Sig).Distinct().Count() > 1);
            if (!anyDiff && r.Schemes.GroupBy(axisKey).Count() > 1)
                r.CollapsedAxes.Add(axis);
        }
        Collapse("剥离节奏", s => s.StripText, s => $"{s.Lookahead}|{s.Coal}|{s.Pairing}");
        Collapse("备采保有 N", s => $"N={s.Lookahead}", s => $"{s.Strip}|{s.Coal}|{s.Pairing}");
        Collapse("采煤节奏", s => s.CoalText, s => $"{s.Lookahead}|{s.Strip}|{s.Pairing}");
        if (r.PairingScored)
            Collapse("配对策略", s => s.PairText, s => $"{s.Lookahead}|{s.Strip}|{s.Coal}");
        r.DistinctCount = r.Schemes.Select(Sig).Distinct().Count();
    }

    // ── 采煤节奏：只改形状不改总量 ──
    public static double[] Shape(double[] baseTarget, CoalPace pace, double skew)
    {
        int T = baseTarget?.Length ?? 0;
        if (T == 0) return Array.Empty<double>();
        var q = (double[])baseTarget!.Clone();
        if (pace == CoalPace.Balanced || T < 2) return q;
        skew = Math.Clamp(skew, 0, 0.6);
        double sign = pace == CoalPace.Rush ? 1 : -1;
        double total = q.Sum(), wsum = 0;
        var wgt = new double[T];
        for (int t = 0; t < T; t++)
        {
            double ramp = 1.0 - 2.0 * t / (T - 1.0);          // 首月 +1 → 末月 −1
            wgt[t] = Math.Max(0.05, 1.0 + sign * skew * ramp);
            wsum += wgt[t];
        }
        for (int t = 0; t < T; t++) q[t] = total * wgt[t] / wsum;   // 总量守恒
        return q;
    }

    /// <summary>
    /// 复制一份排产输入（每套方案各改各的规则取值，不能改到基准那份上）。
    /// <para><b>⚠ 加字段就要同步加到这里</b> —— 漏字段是静默的，见判据 G18j。
    /// 实测漏过 <c>CoalTargetBySeam</c>：**每一套派生方案都把用户填的逐层煤量丢了**，
    /// 引擎改按统一前界自己摊；而打分是<b>跨方案归一</b>的，
    /// 所以这不是"某一套偏了"，是整张比选表都建立在另一套输入上。</para>
    /// </summary>
    private static MonthlyScheduleInput Clone(MonthlyScheduleInput s) => new()
    {
        Rock = s.Rock, AlphaDeg = s.AlphaDeg, ZDatum = s.ZDatum,
        CoalTargetWt = (double[])s.CoalTargetWt.Clone(),
        CoalTargetBySeam = s.CoalTargetBySeam?.Select(a => (double[])(a ?? Array.Empty<double>()).Clone())
                                              .ToArray() ?? Array.Empty<double[]>(),
        StripCapM3 = (double[])(s.StripCapM3 ?? Array.Empty<double>()).Clone(),
        ExtraCumCapM3 = (double[])(s.ExtraCumCapM3 ?? Array.Empty<double>()).Clone(),
        LookaheadMonths = s.LookaheadMonths, RecoveryTotalWt = s.RecoveryTotalWt,
        RatioCeiling = s.RatioCeiling, CoalStartWt = s.CoalStartWt,
        StartInSteadyState = s.StartInSteadyState,
        InitialBenchX = (double[])(s.InitialBenchX ?? Array.Empty<double>()).Clone(),
        Pace = s.Pace,
    };

    /// <summary>判据入口（G18j 用反射逐字段比）。</summary>
    public static MonthlyScheduleInput CloneForTest(MonthlyScheduleInput s) => Clone(s);

    /// <summary>比选矩阵（命令行 / 报表直接打）。</summary>
    public static string CompareTable(ScheduleDerivationResult r)
    {
        if (!r.Success) return "派生失败：" + r.Error;
        var sb = new StringBuilder();
        sb.AppendLine($"派生 {r.Attempted} 套 · 解出 {r.Schemes.Count} 套 · 硬约束全过 {r.Schemes.Count(s => s.Feasible)} 套"
                    + (r.Failed.Count > 0 ? $" · 无解 {r.Failed.Count} 套" : "")
                    + $" · 指标各异 {r.DistinctCount} 套"
                    + (r.PairingScored ? " · 含采排配对" : " · 未跑配对（运输功/内排率不参与打分）"));
        foreach (var n in r.Notes) sb.AppendLine(n);
        foreach (var a in r.CollapsedAxes)
            sb.AppendLine($"⚠ 「{a}」这条轴塌了：所有取值给出同一套指标 —— 当前规则下它没有活动空间，"
                        + "别把它当成一个真选项。");
        string extraHead = r.PairingScored ? " | 运输功万t·km | 内排% " : "";
        sb.AppendLine("排名 | 方案                                    |  得分 | 剥采比CV | 峰值月万m³ | 超前储备万m³ | 最低备采月 | 本期岩万m³" + extraHead + " | 可行");
        sb.AppendLine(new string('-', 110 + extraHead.Length));
        int rank = 1;
        foreach (var s in r.Schemes.OrderByDescending(x => x.Feasible).ThenByDescending(x => x.Score))
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,4} | {1,-39} | {2,5:0.0} | {3,8:0.000} | {4,10:0.0} | {5,12:0.0} | {6,10:0.0} | {7,10:0.0}",
                rank++, s.Name, s.Score, s.RatioCv, s.PeakStripM3 / 1e4,
                s.MeanLeadStockM3 / 1e4, s.MinPreparedMonths, s.TotalRockM3 / 1e4)
                + (r.PairingScored ? string.Format(CultureInfo.InvariantCulture,
                    " | {0,12:0.0} | {1,5:0.0}", s.TransportWorkTKm / 1e4, s.InternalRatePct) : "")
                + (s.Feasible ? " |  ✓" : " |  ✗"));

        if (r.Recommended != null)
            sb.AppendLine($"\n推荐：{r.Recommended.Name}（{r.Recommended.Score:0.0} 分"
                        + (r.Recommended.Feasible ? "" : "，⚠ 硬约束未全过 —— 没有一套全过的") + "）");

        sb.AppendLine("\n归因（各轴取值的平均分；跨度大 = 这条轴说了算）：");
        foreach (var a in r.Attribution)
            sb.AppendLine($"  {a.Axis}（跨度 {a.Spread:0.0}）：" +
                string.Join(" · ", a.Values.Select(v => $"{v.Value} {v.MeanScore:0.0}")) + $"  → 最优 {a.Best}");

        foreach (var f in r.Failed.Take(5))
            sb.AppendLine($"  ✗ {f.Name}：{f.Result.Error}");
        if (r.Failed.Count > 5) sb.AppendLine($"  …另有 {r.Failed.Count - 5} 套无解（同类原因）");
        return sb.ToString();
    }
}
