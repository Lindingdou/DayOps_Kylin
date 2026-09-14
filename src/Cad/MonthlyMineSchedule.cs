using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>剥离节奏（派生轴之一，忠实原 <c>StripPace</c>）：方案的多样性来自规则的取值，不来自求解器的随机性。</summary>
public enum StripPace
{
    /// <summary>贴底：只剥非剥不可的量（C = F）。</summary>
    Hug = 0,
    /// <summary>拉平：走廊内拉绳（默认）。</summary>
    Level = 1,
    /// <summary>前重：剥到 R34 天花板（回采煤量前界）。</summary>
    FrontLoad = 2,
}

/// <summary>逐月采剥接续的输入（规则与条件都在这里；几何来自 <see cref="RockProfile"/>）。忠实原 <c>MonthlyScheduleInput</c>。</summary>
public sealed class MonthlyScheduleInput
{
    public StripPace Pace = StripPace.Level;
    public RockProfile Rock = null!;
    public double AlphaDeg = 15;
    public double ZDatum;
    /// <summary>逐月煤量目标（万t）。长度 = 计划月数 T。</summary>
    public double[] CoalTargetWt = Array.Empty<double>();
    /// <summary>逐月逐层煤量目标（万t），[月-1][层]。空 = 引擎按统一前界摊（会打标，R36）。</summary>
    public double[][] CoalTargetBySeam = Array.Empty<double[]>();
    /// <summary>逐月剥离能力（m³ 实方）。0 = 该月不能剥；&lt;0 或数组空 = 不限。</summary>
    public double[] StripCapM3 = Array.Empty<double>();
    /// <summary>外部给的累计剥离上限（m³，长度 T+1）。空 = 不卡。典型来源：排土容量。</summary>
    public double[] ExtraCumCapM3 = Array.Empty<double>();
    /// <summary>备采保有月数 N（R35）。</summary>
    public int LookaheadMonths = 3;
    /// <summary>回采煤量总量（万t）—— 超前剥离的上界（R34）。≤0 = 不卡。</summary>
    public double RecoveryTotalWt;
    /// <summary>经济合理剥采比（m³/t）。≤0 = 不判。</summary>
    public double RatioCeiling;
    public double CoalStartWt;
    /// <summary>期初工作帮是否已处于稳态（已建立 N 个月超前）。false = 裸起始工作帮（第1月含基建剥离）。</summary>
    public bool StartInSteadyState;
    /// <summary>期初各标高格的实际位置（u，与 Rock.Levels() 同序）。非空则优先。</summary>
    public double[] InitialBenchX = Array.Empty<double>();
    public int MonthCount => CoalTargetWt?.Length ?? 0;
}

/// <summary>一个月的采剥接续结果。</summary>
public sealed class MonthRow
{
    public int    Month;
    public double CoalWt, CoalCumWt;
    public double RockM3, RockCumM3;
    public double MustStripCumM3;
    public double LeadStockM3;
    public double CoalFrontU;
    public double PreparedWt;
    public double PreparedMonths;
    /// <summary>本月新露出的煤（万t）；备采(t) = 备采(t−1) + 新露(t) − 采出(t) 是恒等式。</summary>
    public double NewlyExposedWt;
    /// <summary>本月剥离量的体积加权平均源标高（m）。</summary>
    public double MeanSourceZ;
    public double[] BenchX = Array.Empty<double>();
    /// <summary>本月剥离量按层间标签分账（m³，索引 = GapCode 的 g）。</summary>
    public double[] RockByGapM3 = Array.Empty<double>();
    public bool   OnLowerBound;
    public bool   OnUpperBound;
    public double Ratio => CoalWt > 1e-9 ? RockM3 / (CoalWt * 1e4) : 0;
}

public sealed class ConstraintCheck
{
    public string Code = "";
    public string Name = "";
    public bool   Ok;
    public string Detail = "";
    public override string ToString() => $"{(Ok ? "✓" : "✗")} {Code} {Name}：{Detail}";
}

public sealed class MonthlyScheduleResult
{
    public bool   Success;
    public string Error = "";
    public readonly List<MonthRow> Months = new();
    public int[]  Levels = Array.Empty<int>();
    public readonly List<ConstraintCheck> Checks = new();
    public readonly List<TautString.Pivot> Pivots = new();
    public double TotalCoalWt, TotalRockM3;
    public double OverallRatio => TotalCoalWt > 1e-9 ? TotalRockM3 / (TotalCoalWt * 1e4) : 0;
    public double RatioCv;
    public double StripCv;
    public double EndLeadStockM3;
    public double RecoveryFrontU;
    public bool   TailExtrapolated;
    /// <summary>逐月煤前界（u，下标 0..T）。即使整体求解失败也会填。</summary>
    public double[] CoalFrontU = Array.Empty<double>();
    public double[][] SeamFrontU = Array.Empty<double[]>();
    /// <summary>期初逐标高格台阶位置（下游拿它当第 1 个月的"上月位置"）。</summary>
    public double[] InitialBenchX = Array.Empty<double>();
    public bool CoalAllocatedByEngine;
    public string CoalAllocationMethod = "";
    public double BoxCutM3;
    public string InitialPosture = "";
    /// <summary>参数被引擎悄悄改过/忽略过的地方。</summary>
    public readonly List<string> Notes = new();
    public bool AllChecksOk { get { foreach (var c in Checks) if (!c.Ok) return false; return true; } }
}

/// <summary>
/// 「逐月采剥接续」四步管线（忠实原 <c>MonthlyMineScheduler</c>）：
///   第1步 煤定推进（月煤量目标 → 煤前界，零自由度）；第2步 露煤定必须剥（C5 沿时间前推 N 月 → F(t)）；
///   第3步 剥采比均衡（走廊 [F, Cap] 内拉绳）；第4步 摊回台阶（余量自上而下水填）。全程没有优化器。
/// 压覆关系投到推进轴：x_A − x_B ≥ Δ_AB = (z_A−z_B)/tanα；超前剥离 = 该不等式沿时间前推 N 个月（R35）。
/// </summary>
public static class MonthlyMineScheduler
{
    public static MonthlyScheduleResult Solve(MonthlyScheduleInput inp)
    {
        var r = new MonthlyScheduleResult();
        if (inp?.Rock == null || !inp.Rock.Success) { r.Error = "岩量剖面无效（先跑「采场剖面」）"; return r; }
        int T = inp.MonthCount;
        if (T <= 0) { r.Error = "没有月度煤量目标"; return r; }

        var rock = inp.Rock;
        double h = rock.BenchHeight;
        if (h <= 1e-6) { r.Error = "岩台阶高为 0，标高格无从划分"; return r; }
        double alphaUse = Math.Clamp(inp.AlphaDeg, 1.0, 89.0);
        if (Math.Abs(alphaUse - inp.AlphaDeg) > 1e-9) r.Notes.Add($"⚠ 工作帮坡角 α 填的是 {inp.AlphaDeg:0.###}°，已夹到 {alphaUse:0.###}°（合法区间 1~89°）");
        double tanA = Math.Tan(alphaUse * Math.PI / 180.0);
        int N = Math.Max(0, inp.LookaheadMonths);
        if (N != inp.LookaheadMonths) r.Notes.Add($"⚠ 备采保有月数填的是 {inp.LookaheadMonths}，已按 0 处理");
        if (inp.RecoveryTotalWt < 0) r.Notes.Add($"⚠ 回采煤量填的是 {inp.RecoveryTotalWt:0.#} 万t（负数），已按「不卡上界」处理");

        var levels = rock.Levels();
        if (levels.Count == 0) { r.Error = "岩量剖面里一个标高格都没有"; return r; }
        r.Levels = levels.ToArray();
        int K = levels.Count;

        // ── 第1步 · 煤定推进 ──
        int TE = T + N;
        int M = rock.CoalVolBins.Length;
        var coalCum = new double[TE + 1];
        coalCum[0] = inp.CoalStartWt;
        for (int t = 1; t <= TE; t++)
        {
            double q = t <= T ? inp.CoalTargetWt[t - 1] : inp.CoalTargetWt[T - 1];
            coalCum[t] = coalCum[t - 1] + q;
        }
        r.TailExtrapolated = N > 0;

        bool perSeam = inp.CoalTargetBySeam is { Length: > 0 } && inp.CoalTargetBySeam.Length >= T && inp.CoalTargetBySeam.All(a => a != null && a.Length >= M);
        if (inp.CoalTargetBySeam is { Length: > 0 } && !perSeam)
            r.Notes.Add($"⚠ 逐层煤量目标给了 {inp.CoalTargetBySeam.Length} 个月 × {inp.CoalTargetBySeam.FirstOrDefault()?.Length ?? 0} 层，但需要 {T} 个月 × {M} 层 —— 长度不符，**整个忽略**，改由引擎按统一前界摊（见 CoalAllocationMethod）");
        var seamU = new double[TE + 1][];
        var coalU = new double[TE + 1];
        if (perSeam)
        {
            var seamCum = new double[M];
            seamU[0] = new double[M];
            for (int j = 0; j < M; j++) seamU[0][j] = SolveSeamFront(rock, j, 0);
            for (int t = 1; t <= TE; t++)
            {
                seamU[t] = new double[M];
                for (int j = 0; j < M; j++)
                {
                    seamCum[j] += t <= T ? inp.CoalTargetBySeam[t - 1][j] : inp.CoalTargetBySeam[T - 1][j];
                    seamU[t][j] = SolveSeamFront(rock, j, seamCum[j]);
                }
            }
            r.CoalAllocatedByEngine = false;
            r.CoalAllocationMethod = "逐层煤量由用户给定，引擎未摊";
            for (int t = 1; t <= T; t++)
            {
                double sum = inp.CoalTargetBySeam[t - 1].Take(M).Sum();
                if (Math.Abs(sum - inp.CoalTargetWt[t - 1]) > 0.005 * Math.Max(1, inp.CoalTargetWt[t - 1]))
                { r.CoalAllocationMethod += $"；⚠ 第{t}月逐层之和 {sum:0.00} ≠ 月目标 {inp.CoalTargetWt[t - 1]:0.00} 万t（引擎按逐层走，不按总量走）"; break; }
            }
        }
        else
        {
            for (int t = 0; t <= TE; t++)
            {
                double u = SolveCoalFront(rock, coalCum[t]);
                seamU[t] = new double[M];
                for (int j = 0; j < M; j++) seamU[t][j] = u;
            }
            r.CoalAllocatedByEngine = true;
            r.CoalAllocationMethod = "★引擎摊的：各层取【统一前界】，逐层贡献由各自剖面决定（= Stage1 统一前界 d 的口径）。要按别的分法，请填逐层月度目标覆盖。";
        }
        for (int t = 0; t <= TE; t++) coalU[t] = seamU[t].Length > 0 ? seamU[t].Max() : 0;

        r.CoalFrontU = new double[T + 1];
        Array.Copy(coalU, r.CoalFrontU, T + 1);
        r.SeamFrontU = new double[T + 1][];
        for (int t = 0; t <= T; t++) r.SeamFrontU[t] = (double[])seamU[t].Clone();

        double recoveryCum = coalCum[T] + Math.Max(0, inp.RecoveryTotalWt);
        r.RecoveryFrontU = inp.RecoveryTotalWt > 0 ? SolveCoalFront(rock, recoveryCum) : double.PositiveInfinity;

        // ── 第2步 · 露煤定必须剥 ──
        var xMin = new double[T + 1, K];
        var xCap = new double[K];
        bool hasInit = inp.InitialBenchX != null && inp.InitialBenchX.Length == K;
        if (inp.InitialBenchX is { Length: > 0 } && !hasInit)
            r.Notes.Add($"⚠ 期初台阶位置给了 {inp.InitialBenchX.Length} 个，但本次有 {K} 个标高格 —— 长度不符，**整个忽略**，改用" + (inp.StartInSteadyState ? "稳态推算" : "裸起始工作帮"));
        for (int i = 0; i < K; i++)
        {
            double zk = (levels[i] + 0.5) * h;
            xCap[i] = double.IsPositiveInfinity(r.RecoveryFrontU) ? double.PositiveInfinity : LeadPos(rock, r.RecoveryFrontU, zk, tanA, inp.ZDatum);
            double x0 = hasInit ? inp.InitialBenchX![i]
                      : inp.StartInSteadyState ? LeadPos(rock, seamU[Math.Min(N, TE)], zk, tanA, inp.ZDatum)
                      : StartPos(zk, tanA, inp.ZDatum);
            xMin[0, i] = Math.Max(x0, StartPos(zk, tanA, inp.ZDatum));
            if (xCap[i] < xMin[0, i]) xCap[i] = xMin[0, i];
        }
        double stepLead = h / tanA;
        EnforceStaircase(xMin, 0, K, levels, stepLead);
        r.InitialBenchX = new double[K];
        for (int i = 0; i < K; i++) r.InitialBenchX[i] = xMin[0, i];

        for (int t = 1; t <= T; t++)
        {
            for (int i = 0; i < K; i++)
            {
                double zk = (levels[i] + 0.5) * h;
                double need = LeadPos(rock, seamU[Math.Min(t + N, TE)], zk, tanA, inp.ZDatum);
                double v = Math.Max(need, xMin[t - 1, i]);
                if (v > xCap[i]) v = xCap[i];
                if (v < xMin[t - 1, i]) v = xMin[t - 1, i];
                xMin[t, i] = v;
            }
            EnforceStaircase(xMin, t, K, levels, stepLead);
        }

        var F = new double[T + 1];
        for (int t = 0; t <= T; t++)
        {
            double s = 0;
            for (int i = 0; i < K; i++) s += rock.VolUpTo(levels[i], -1, xMin[t, i]);
            F[t] = s;
        }
        double f0 = F[0];
        for (int t = 0; t <= T; t++) F[t] -= f0;
        r.BoxCutM3 = f0;
        r.InitialPosture = hasInit ? "显式给定的期初台阶位置" : inp.StartInSteadyState ? $"按稳态推（期初已建立 {N} 个月超前）" : "裸起始工作帮（无超前）—— 第1月含基建剥离";

        // ── 第3步 · 剥采比均衡 ──
        double ceilByRecovery = 0;
        for (int i = 0; i < K; i++)
            ceilByRecovery += double.IsPositiveInfinity(xCap[i]) ? rock.LevelMaxVol(levels[i]) : rock.VolUpTo(levels[i], -1, xCap[i]);
        ceilByRecovery = Math.Max(0, ceilByRecovery - f0);

        var Cap = new double[T + 1];
        bool capped = inp.StripCapM3 != null && inp.StripCapM3.Length >= T;
        bool extraCap = inp.ExtraCumCapM3 != null && inp.ExtraCumCapM3.Length > T;
        double acc = 0;
        Cap[0] = 0;
        for (int t = 1; t <= T; t++)
        {
            double c = capped && inp.StripCapM3![t - 1] >= 0 ? inp.StripCapM3[t - 1] : double.PositiveInfinity;
            acc = double.IsPositiveInfinity(c) || double.IsPositiveInfinity(acc) ? double.PositiveInfinity : acc + c;
            Cap[t] = Math.Min(acc, ceilByRecovery);
            if (extraCap && inp.ExtraCumCapM3![t] >= 0) Cap[t] = Math.Min(Cap[t], inp.ExtraCumCapM3[t]);
        }
        Cap[0] = Math.Min(Cap[0], ceilByRecovery);
        for (int t = 1; t <= T; t++) if (Cap[t] < Cap[t - 1]) Cap[t] = Cap[t - 1];

        double[]? C;
        if (inp.Pace == StripPace.Hug) C = (double[])F.Clone();
        else if (inp.Pace == StripPace.FrontLoad)
        {
            C = new double[T + 1];
            for (int t = 0; t <= T; t++) C[t] = Math.Min(Cap[t], F[T]);
            C[0] = F[0];
            for (int t = 1; t <= T; t++) if (C[t] < C[t - 1]) C[t] = C[t - 1];
            for (int t = 1; t <= T; t++) if (C[t] < F[t]) C[t] = F[t];
            for (int t = 1; t <= T; t++) if (C[t] > Cap[t] + 1e-6) { C = null; break; }
            if (C == null) { r.Error = "剥离量均衡无解 —— 前重节奏顶穿能力"; return r; }
        }
        else
        {
            C = TautString.Solve(F, Cap, F[T], out string terr, r.Pivots);
            if (C == null) { r.Error = "剥离量均衡无解 —— " + terr; return r; }
        }
        for (int t = 0; t <= T; t++)
            if (C[t] > Cap[t] + 1e-6)
            { r.Error = $"剥离量均衡无解 —— 走廊在第{t}期倒挂：累计必须剥 {F[t] / 1e4:0.#}万m³ > 累计能力 {Cap[t] / 1e4:0.#}万m³"; return r; }

        // ── 第4步 · 摊回台阶位置（余量自上而下发）──
        double edge = (rock.MaxBin + 1) * rock.SliceWidth;
        var xAct = new double[T + 1, K];
        for (int i = 0; i < K; i++) xAct[0, i] = xMin[0, i];
        for (int t = 1; t <= T; t++)
        {
            for (int i = 0; i < K; i++) xAct[t, i] = Math.Max(xMin[t, i], xAct[t - 1, i]);
            double have = 0;
            for (int i = 0; i < K; i++) have += rock.VolUpTo(levels[i], -1, xAct[t, i]);
            double extra = C[t] + f0 - have;
            for (int i = K - 1; i >= 0 && extra > 1e-6; i--)
            {
                double lo = xAct[t, i];
                double hi = double.IsPositiveInfinity(xCap[i]) ? edge : xCap[i];
                if (i + 1 < K && levels[i + 1] == levels[i] + 1) hi = Math.Min(hi, xAct[t, i + 1] - stepLead);
                if (hi <= lo + 1e-9) continue;
                double room = rock.VolUpTo(levels[i], -1, hi) - rock.VolUpTo(levels[i], -1, lo);
                if (room <= 1e-9) continue;
                double take = Math.Min(room, extra);
                xAct[t, i] = SolveLevelPos(rock, levels[i], lo, hi, rock.VolUpTo(levels[i], -1, lo) + take);
                extra -= take;
            }
        }

        int edgeFirst = 0; int edgeLevels = 0;
        for (int t = 1; t <= T && edgeFirst == 0; t++)
            for (int i = 0; i < K; i++)
                if (xAct[t, i] >= edge - 1e-6) { edgeFirst = t; break; }
        if (edgeFirst > 0)
        {
            for (int i = 0; i < K; i++) if (xAct[T, i] >= edge - 1e-6) edgeLevels++;
            r.Notes.Add($"⚠ **工作帮在第 {edgeFirst} 月推到了剖面数据的尽头**（u = {edge:0.#}m，期末有 {edgeLevels}/{K} 个标高格顶在那里）——"
                      + "此后各月的「必须剥」会**自己往下掉**：那不是地质变薄，是**块体模型比计划期短**，越过边界就再也累加不出岩量。这几个月的剥离量与剥采比**不可当真**，要么把块体模型往推进方向扩，要么缩短计划期。");
        }

        // ── 回填逐月行 + 备采保有 ──
        double cumRock = 0;
        var bx0 = new double[K];
        for (int i = 0; i < K; i++) bx0[i] = xAct[0, i];
        PreparedCoalWt(rock, levels, bx0, h, tanA, seamU[0], out var expPrev);
        for (int t = 1; t <= T; t++)
        {
            double vol = 0, zMoment = 0, zWeight = 0;
            var bx = new double[K];
            for (int i = 0; i < K; i++)
            {
                bx[i] = xAct[t, i];
                double v = rock.VolUpTo(levels[i], -1, xAct[t, i]);
                vol += v;
                double dv = v - rock.VolUpTo(levels[i], -1, xAct[t - 1, i]);
                if (dv > 0) { zMoment += dv * ((levels[i] + 0.5) * h); zWeight += dv; }
            }
            double volPrev = 0;
            for (int i = 0; i < K; i++) volPrev += rock.VolUpTo(levels[i], -1, xAct[t - 1, i]);
            double monthRock = Math.Max(0, vol - volPrev);
            cumRock += monthRock;
            double meanSrcZ = zWeight > 1e-9 ? zMoment / zWeight : inp.ZDatum;

            double coalWt = coalCum[t] - coalCum[t - 1];
            double prepared = PreparedCoalWt(rock, levels, bx, h, tanA, seamU[t], out var expNow, expPrev);
            double newlyExposed = 0;
            for (int j = 0; j < expNow.Length && j < expPrev.Length; j++) newlyExposed += rock.CoalWtUpTo(j, expNow[j]) - rock.CoalWtUpTo(j, expPrev[j]);
            expPrev = expNow;

            var byGap = new double[GapCode.Count(rock.SeamCount)];
            var xPrev = new double[K];
            for (int i = 0; i < K; i++) xPrev[i] = xAct[t - 1, i];
            rock.AccumByGap(levels, bx, byGap, +1);
            rock.AccumByGap(levels, xPrev, byGap, -1);
            for (int g = 0; g < byGap.Length; g++) if (byGap[g] < 0) byGap[g] = 0;

            r.Months.Add(new MonthRow
            {
                Month = t, CoalWt = coalWt, CoalCumWt = coalCum[t] - coalCum[0],
                RockM3 = monthRock, RockCumM3 = cumRock, RockByGapM3 = byGap,
                MustStripCumM3 = F[t], LeadStockM3 = Math.Max(0, C[t] - F[t]),
                CoalFrontU = coalU[t], PreparedWt = prepared, PreparedMonths = coalWt > 1e-9 ? prepared / coalWt : 0,
                NewlyExposedWt = newlyExposed, MeanSourceZ = meanSrcZ, BenchX = bx,
                OnLowerBound = r.Pivots.Any(p => p.Period == t && p.OnLower),
                OnUpperBound = r.Pivots.Any(p => p.Period == t && !p.OnLower),
            });
        }
        r.TotalCoalWt = coalCum[T] - coalCum[0];
        r.TotalRockM3 = cumRock;
        r.EndLeadStockM3 = C[T] - F[T];
        r.RatioCv = TautString.Cv(r.Months.Select(m => m.Ratio).ToList());
        r.StripCv = TautString.Cv(r.Months.Select(m => m.RockM3).ToList());

        CheckHard(r, inp, N);
        r.Success = true;
        return r;
    }

    /// <summary>台架入口：在人为改坏的结果上重跑硬校核。</summary>
    public static void CheckHardForTest(MonthlyScheduleResult r, MonthlyScheduleInput inp) => CheckHard(r, inp, Math.Max(0, inp.LookaheadMonths));

    private static void CheckHard(MonthlyScheduleResult r, MonthlyScheduleInput inp, int N)
    {
        double worst = 0; int worstM = 0;
        for (int i = 0; i < r.Months.Count; i++)
        {
            double tgt = inp.CoalTargetWt[i], got = r.Months[i].CoalWt;
            double d = tgt > 1e-9 ? Math.Abs(got - tgt) / tgt * 100 : 0;
            if (d > worst) { worst = d; worstM = i + 1; }
        }
        r.Checks.Add(new ConstraintCheck { Code = "H1", Name = "月煤量达标", Ok = worst <= 0.5, Detail = worst <= 0.5 ? $"逐月最大偏差 {worst:0.00}%" : $"第{worstM}月偏差 {worst:0.0}%（应 ≤0.5%）" });

        int badMono = 0;
        for (int t = 1; t < r.Months.Count; t++)
            for (int i = 0; i < r.Levels.Length; i++)
                if (r.Months[t].BenchX[i] < r.Months[t - 1].BenchX[i] - 1e-6) badMono++;
        r.Checks.Add(new ConstraintCheck { Code = "H2", Name = "台阶不后退", Ok = badMono == 0, Detail = badMono == 0 ? "逐月逐格均单调不减" : $"{badMono} 处出现后退（台阶不可能往回长）" });

        double minLead = double.MaxValue; int minAt = -1;
        double h = inp.Rock.BenchHeight;
        double tanA = Math.Tan(Math.Clamp(inp.AlphaDeg, 1.0, 89.0) * Math.PI / 180.0);
        double nominal = h / tanA;
        foreach (var m in r.Months)
            for (int i = 1; i < r.Levels.Length; i++)
            {
                if (r.Levels[i] != r.Levels[i - 1] + 1) continue;
                double lead = m.BenchX[i] - m.BenchX[i - 1];
                if (lead < minLead) { minLead = lead; minAt = m.Month; }
            }
        r.Checks.Add(new ConstraintCheck { Code = "H3", Name = "台阶超前 ≥ 一级横距", Ok = minAt < 0 || minLead >= nominal - 0.5, Detail = minAt < 0 ? "无相邻格可判" : $"最小相邻超前 {minLead:0.#}m（名义 {nominal:0.#}m，第{minAt}月）" });

        double minPrep = double.MaxValue; int prepAt = 0;
        foreach (var m in r.Months) if (m.PreparedMonths < minPrep) { minPrep = m.PreparedMonths; prepAt = m.Month; }
        r.Checks.Add(new ConstraintCheck { Code = "H4", Name = $"备采保有 ≥ {N} 月", Ok = r.Months.Count == 0 || minPrep >= N - 0.05, Detail = $"最低 {minPrep:0.0} 月（第{prepAt}月）" + (minPrep < N - 0.05 ? " —— 该月之后会断煤" : "") });

        if (inp.RatioCeiling > 0)
        {
            double mx = 0; int mxAt = 0;
            foreach (var m in r.Months) if (m.Ratio > mx) { mx = m.Ratio; mxAt = m.Month; }
            r.Checks.Add(new ConstraintCheck { Code = "H5", Name = "生产剥采比 ≤ n经", Ok = mx <= inp.RatioCeiling + 1e-6, Detail = $"最大 {mx:0.00} m³/t（第{mxAt}月，上限 {inp.RatioCeiling:0.00}）" });
        }

        {
            string firstBad = "";
            foreach (var m in r.Months)
            {
                foreach (var (nm, v) in new[] { ("采出", m.CoalWt), ("剥离", m.RockM3), ("累计剥离", m.RockCumM3), ("煤前界", m.CoalFrontU), ("备采", m.PreparedWt), ("保有月数", m.PreparedMonths), ("超前储备", m.LeadStockM3) })
                    if (double.IsNaN(v) || double.IsInfinity(v)) { firstBad = $"第{m.Month}月.{nm} = {v}"; break; }
                if (firstBad.Length == 0)
                    for (int i = 0; i < m.BenchX.Length; i++)
                        if (double.IsNaN(m.BenchX[i]) || double.IsInfinity(m.BenchX[i])) { firstBad = $"第{m.Month}月.台阶位置[格{(i < r.Levels.Length ? r.Levels[i] : i)}] = {m.BenchX[i]}"; break; }
                if (firstBad.Length > 0) break;
            }
            r.Checks.Add(new ConstraintCheck { Code = "H6", Name = "无 NaN/±∞", Ok = firstBad.Length == 0, Detail = firstBad.Length == 0 ? "逐月量与台阶位置全部有限" : $"◆ {firstBad} —— 非有限值会让 H1..H5 的比大小**全部失效**（与 NaN 比恒 false）" });
        }
        {
            int want = inp.MonthCount, got = r.Months.Count;
            int badLen = r.Months.Count(m => m.BenchX.Length != r.Levels.Length);
            bool ok = got == want && badLen == 0;
            r.Checks.Add(new ConstraintCheck { Code = "H7", Name = "月份与标高格齐全", Ok = ok, Detail = ok ? $"{got} 个月 × {r.Levels.Length} 个标高格，齐" : (got != want ? $"◆ 要 {want} 个月，只排出 {got} 个" : "") + (badLen > 0 ? $"　◆ {badLen} 个月的台阶位置数与标高格数不符" : "") });
        }
    }

    private static double StartPos(double z, double tanA, double zDatum) => (z - zDatum) / tanA;

    /// <summary>C2 兜死：自下而上把台阶超前钉够 x_i ≥ x_{i−1} + 一级横距（仅相邻标高格）。</summary>
    private static void EnforceStaircase(double[,] x, int t, int K, List<int> levels, double stepLead)
    {
        for (int i = 1; i < K; i++)
        {
            if (levels[i] != levels[i - 1] + 1) continue;
            double need = x[t, i - 1] + stepLead;
            if (x[t, i] < need) x[t, i] = need;
        }
    }

    /// <summary>标高 z 的岩台阶要让煤前界推到 frontBySeam 时该处的煤露出来，自己至少要到哪（u）。</summary>
    private static double LeadPos(RockProfile rock, double[] frontBySeam, double z, double tanA, double zDatum)
    {
        double best = double.NegativeInfinity;
        for (int j = 0; j < rock.CoalVolBins.Length && j < frontBySeam.Length; j++)
        {
            double uj = frontBySeam[j];
            if (!rock.TryCoalZNear(j, uj, out double zj)) continue;
            if (zj >= z) continue;
            double need = uj + (z - zj) / tanA;
            if (need > best) best = need;
        }
        return double.IsNegativeInfinity(best) ? StartPos(z, tanA, zDatum) : best;
    }

    private static double LeadPos(RockProfile rock, double coalU, double z, double tanA, double zDatum)
    {
        var arr = new double[rock.CoalVolBins.Length];
        for (int j = 0; j < arr.Length; j++) arr[j] = coalU;
        return LeadPos(rock, arr, z, tanA, zDatum);
    }

    private static double SolveSeamFront(RockProfile rock, int seam, double targetWt)
    {
        double lo = rock.MinBin * rock.SliceWidth;
        double hi = (rock.MaxBin + 1) * rock.SliceWidth;
        if (targetWt <= 1e-12) return lo;
        if (rock.CoalWtUpTo(seam, hi) <= targetWt) return hi;
        for (int i = 0; i < 60; i++) { double mid = 0.5 * (lo + hi); if (rock.CoalWtUpTo(seam, mid) < targetWt) lo = mid; else hi = mid; }
        return hi;
    }

    private static double CoalWtAt(RockProfile rock, double u)
    {
        double t = 0;
        for (int j = 0; j < rock.CoalVolBins.Length; j++) t += rock.CoalWtUpTo(j, u);
        return t;
    }

    private static double SolveCoalFront(RockProfile rock, double targetWt)
    {
        double lo = rock.MinBin * rock.SliceWidth;
        double hi = (rock.MaxBin + 1) * rock.SliceWidth;
        if (targetWt <= 1e-12) return lo;
        if (CoalWtAt(rock, hi) <= targetWt) return hi;
        for (int i = 0; i < 60; i++) { double mid = 0.5 * (lo + hi); if (CoalWtAt(rock, mid) < targetWt) lo = mid; else hi = mid; }
        return hi;
    }

    private static double SolveLevelPos(RockProfile rock, int level, double lo, double hi, double targetVol)
    {
        for (int i = 0; i < 60; i++) { double mid = 0.5 * (lo + hi); if (rock.VolUpTo(level, -1, mid) < targetVol) lo = mid; else hi = mid; }
        return hi;
    }

    /// <summary>备采煤量（万t）= 已露出但还没采的煤。揭露前锋 e_j = min over 压在它上面的标高格 k：x_k − (z_k − z_j)/tanα；揭露单调（不许缩回）。</summary>
    private static double PreparedCoalWt(RockProfile rock, List<int> levels, double[] bx, double h, double tanA, double[] frontBySeam, out double[] frontExposed, double[]? floor = null)
    {
        int M = rock.CoalVolBins.Length;
        frontExposed = new double[M];
        double sum = 0;
        for (int j = 0; j < M; j++)
        {
            double uj = j < frontBySeam.Length ? frontBySeam[j] : 0;
            frontExposed[j] = uj;
            if (!rock.TryCoalZNear(j, uj, out double zj)) continue;
            double e = double.PositiveInfinity;
            for (int i = 0; i < levels.Count; i++)
            {
                double zk = (levels[i] + 0.5) * h;
                if (zk <= zj) continue;
                double reach = bx[i] - (zk - zj) / tanA;
                if (reach < e) e = reach;
            }
            if (double.IsPositiveInfinity(e)) e = (rock.MaxBin + 1) * rock.SliceWidth;
            if (floor != null && j < floor.Length && e < floor[j]) e = floor[j];
            if (e < uj) e = uj;
            frontExposed[j] = e;
            double exposed = rock.CoalWtUpTo(j, e) - rock.CoalWtUpTo(j, uj);
            if (exposed > 0) sum += exposed;
        }
        return sum;
    }

    /// <summary>逐月计划表（命令行 / 报表直接打）。</summary>
    public static string Report(MonthlyScheduleResult r)
    {
        if (!r.Success) return "采剥接续失败：" + r.Error;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("月 |   采出万t |   剥离万m³ | 剥采比 | 必须剥万m³ | 超前储备万m³ | 备采月 | 煤前界m");
        sb.AppendLine("---+-----------+------------+--------+------------+--------------+--------+---------");
        foreach (var m in r.Months)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,2} | {1,9:0.0} | {2,10:0.0} | {3,6:0.00} | {4,10:0.0} | {5,12:0.0} | {6,6:0.0} | {7,7:0.0}{8}",
                m.Month, m.CoalWt, m.RockM3 / 1e4, m.Ratio, m.MustStripCumM3 / 1e4, m.LeadStockM3 / 1e4, m.PreparedMonths, m.CoalFrontU,
                m.OnLowerBound ? "  ◄触底(露煤紧迫)" : m.OnUpperBound ? "  ◄触顶(能力吃紧)" : ""));
        sb.AppendLine($"合计: 煤 {r.TotalCoalWt:0.0}万t · 岩 {r.TotalRockM3 / 1e4:0.0}万m³ · 综合剥采比 {r.OverallRatio:0.00} m³/t");
        sb.AppendLine($"均衡: 剥采比CV {r.RatioCv:0.000} · 月剥离CV {r.StripCv:0.000} · 期末超前储备 {r.EndLeadStockM3 / 1e4:0.0}万m³");
        sb.AppendLine($"期初: {r.InitialPosture} · 基建剥离(不进本期) {r.BoxCutM3 / 1e4:0.0}万m³");
        sb.AppendLine($"分层: {r.CoalAllocationMethod}");
        if (r.TailExtrapolated) sb.AppendLine("注: 尾部露煤前瞻用【末月强度外推】的煤计划 —— 接上中长远下一年目标后可去掉这条外推。");
        foreach (var c in r.Checks) sb.AppendLine("  " + c);
        return sb.ToString();
    }
}
