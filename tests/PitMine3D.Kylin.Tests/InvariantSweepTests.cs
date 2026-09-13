// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/InvariantSweepTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Tests.Synth;
using BlockModel = PitMine3D.Kylin.Tests.Synth.BlockModel;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// INV 组 · <b>随机不变量扫描</b>。
///
/// <para><b>为什么要它</b>：前面 121 条判据都用<b>手挑的夹具</b>——而手挑的夹具总是"干净"的
/// （整数月量、均匀分布、水平或单一倾角）。端到端判据第一次跑就抓到一个逐件判据看不见的错
/// （取整边界 39.499→40），根子正是<b>夹具撞不到真实链路上的边界值</b>。</para>
///
/// <para>这一组换个思路：<b>随机生成几十组</b>（地质 × 目标 × 能力 × 规则取值），
/// 只判「任何情况下都必须成立」的性质。不判具体数值——判<b>不变量</b>：
/// 守恒、单调、台阶超前、三量递推、可行性口径自洽。</para>
///
/// <para><b>失败时必须能复现</b>：每个算例带种子，报错信息里打出来，照着能单独重跑。</para>
/// </summary>
public sealed class InvariantSweepTests
{
    private readonly ITestOutputHelper _out;
    public InvariantSweepTests(ITestOutputHelper o) => _out = o;

    private sealed class Case
    {
        public int Seed;
        public int NX, NY, NZ, Seams, Months, Lookahead;
        public double Cell, RockH, Alpha, Dip, ZDatum, WlX, CoalFrac, CapFactor;
        public bool Steady;
        public StripPace Pace;
        public override string ToString()
            => $"seed={Seed} 块体{NX}×{NY}×{NZ}@{Cell:0} 层{Seams} 月{Months} α={Alpha:0.#}° 倾={Dip:0.###} "
             + $"h={RockH:0} N={Lookahead} 采{CoalFrac:P0} 能力×{CapFactor:0.##} {(Steady ? "稳态" : "裸帮")} {Pace}";
    }

    private static Case Roll(int seed)
    {
        var r = new Random(seed);
        double D(double a, double b) => a + r.NextDouble() * (b - a);
        return new Case
        {
            Seed = seed,
            NX = 40 + r.Next(40), NY = 3 + r.Next(4), NZ = 20 + r.Next(16),
            Cell = 10, RockH = new[] { 10.0, 15.0, 20.0 }[r.Next(3)],
            Seams = 1 + r.Next(3),
            Months = 6 + r.Next(13),
            Alpha = D(12, 30),
            Dip = r.Next(3) == 0 ? 0 : D(0.02, 0.20),      // 1/3 概率水平，其余各种倾角
            ZDatum = D(100, 300),
            WlX = -D(500, 3000),
            CoalFrac = D(0.2, 0.7),
            CapFactor = r.Next(2) == 0 ? 0 : D(0.9, 3.0),  // 0 = 不限能力
            Lookahead = 1 + r.Next(5),
            Steady = r.Next(4) != 0,                        // 3/4 稳态（生产接续是常态）
            Pace = (StripPace)r.Next(3),
        };
    }

    private static TinSampler DipPlane(double z0, double dip)
    {
        double h = 8000;
        double Z(double x) => z0 - dip * x;
        return TinSampler.TryBuild(
            new[] { -h, -h, Z(-h), h, -h, Z(h), h, h, Z(h), -h, h, Z(-h) }, new[] { 0, 1, 2, 0, 2, 3 })!;
    }

    private static RockProfile? Scan(Case c)
    {
        var spec = new BlockModelSpec
        {
            Origin = new Vec3d(0, 0, 0), BlockSize = new Vec3d(c.Cell, c.Cell, c.Cell),
            Dimensions = new Vec3i(c.NX, c.NY, c.NZ),
        };
        var m = new BlockModel { Name = $"S{c.Seed}", Spec = spec };
        var cd = m.EnsureCellData();
        double top = c.NZ * c.Cell;
        // 各层顶板从上往下均匀铺开，厚度 2 个 cell
        var roof0 = new double[c.Seams];
        for (int j = 0; j < c.Seams; j++) roof0[j] = top * (0.80 - 0.28 * j);
        double thick = 2 * c.Cell;
        for (int j = 0; j < c.Seams; j++) cd.SetConstant($"c{j}", 0);
        long nxy = (long)c.NX * c.NY;
        for (int k = 0; k < c.NZ; k++)
        {
            double cz = k * c.Cell + c.Cell * 0.5;
            for (int i = 0; i < c.NX; i++)
            {
                double cx = i * c.Cell + c.Cell * 0.5;
                int hit = -1;
                for (int j = 0; j < c.Seams; j++)
                {
                    double rf = roof0[j] - c.Dip * cx;
                    if (cz <= rf && cz >= rf - thick) { hit = j; break; }
                }
                if (hit < 0) continue;
                for (int jy = 0; jy < c.NY; jy++) cd.SetCell($"c{hit}", k * nxy + (long)jy * c.NX + i, 1.0);
            }
        }
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((c.WlX, -100, c.ZDatum)); wl.Baseline.Add((c.WlX, c.NY * c.Cell + 100, c.ZDatum));
        wl.Samples.Add((c.WlX, c.NY * c.Cell * 0.5, c.ZDatum, 1, 0));
        var seams = Enumerable.Range(0, c.Seams).Select(j => new SeamSurfaces
        {
            Name = $"层{j}", Attribute = $"c{j}", Density = 1.35,
            Roof = DipPlane(roof0[j], c.Dip), Floor = DipPlane(roof0[j] - thick, c.Dip),
        }).ToList();
        var p = InclineVolumeEngine.BuildProfile(m, new[] { wl }, DipPlane(1e5, 0), seams,
                                                 c.Alpha, 1, 0.5, c.RockH);
        return p.Success ? p.Rock : null;
    }

    /// <summary>
    /// INV1 <b>随机算例上，六条不变量必须永远成立</b>。
    /// <list type="number">
    ///   <item>月煤量 = 目标（本层是按目标反解前界，恒等）</item>
    ///   <item>台阶位置逐月不后退</item>
    ///   <item>相邻标高格超前 ≥ 一级横距（C2）</item>
    ///   <item>三量递推：备采(t) = 备采(t−1) + 新露(t) − 采出(t)</item>
    ///   <item>按层间分账合计 = 月剥离量</item>
    ///   <item>解不出来时必须有 Error，且不留半份结果</item>
    /// </list>
    /// </summary>
    [Fact]
    public void INV1_CoreInvariantsHoldOnRandomCases()
    {
        int ok = 0, infeasible = 0, noRock = 0;
        var reasons = new Dictionary<string, int>();

        for (int seed = 1; seed <= 60; seed++)
        {
            var c = Roll(seed);
            var rock = Scan(c);
            if (rock == null || rock.RockCellCount == 0 || rock.TotalCoalWt() < 1e-6) { noRock++; continue; }

            double total = rock.TotalCoalWt();
            var q = new double[c.Months];
            for (int i = 0; i < c.Months; i++) q[i] = total * c.CoalFrac / c.Months;
            var inp = new MonthlyScheduleInput
            {
                Rock = rock, AlphaDeg = c.Alpha, ZDatum = c.ZDatum, CoalTargetWt = q,
                LookaheadMonths = c.Lookahead, RecoveryTotalWt = total * c.CoalFrac / c.Months * c.Lookahead,
                StartInSteadyState = c.Steady, Pace = c.Pace,
            };
            if (c.CapFactor > 0)
            {
                // 以"贴底所需"为基准放缩，制造从紧到松的各种能力
                var probe = MonthlyMineScheduler.Solve(inp);
                double baseCap = probe.Success && probe.Months.Count > 0
                    ? probe.Months.Max(m => m.RockM3) : 1e9;
                inp.StripCapM3 = Enumerable.Repeat(baseCap * c.CapFactor, c.Months).ToArray();
            }

            var r = MonthlyMineScheduler.Solve(inp);
            if (!r.Success)
            {
                // ⑥ 解不出来 ⇒ 必须有原因，且不许留半份结果
                Assert.False(string.IsNullOrWhiteSpace(r.Error), $"[{c}] 失败却没给原因");
                Assert.Empty(r.Months);
                infeasible++;
                string key = r.Error.Contains("倒挂") ? "走廊倒挂" : r.Error.Split('—')[0].Trim();
                reasons[key] = reasons.GetValueOrDefault(key) + 1;
                continue;
            }

            double tanA = Math.Tan(c.Alpha * Math.PI / 180);
            double stepLead = c.RockH / tanA;

            Assert.Equal(c.Months, r.Months.Count);
            double prevPrepared = 0;
            for (int t = 0; t < r.Months.Count; t++)
            {
                var m = r.Months[t];
                // ① 月煤量
                Assert.True(Math.Abs(m.CoalWt - q[t]) <= 1e-3 * Math.Max(1, q[t]),
                            $"[{c}] 第{t + 1}月煤 {m.CoalWt:0.000} ≠ 目标 {q[t]:0.000}");
                // ② 不后退
                if (t > 0)
                    for (int i = 0; i < m.BenchX.Length; i++)
                        Assert.True(m.BenchX[i] >= r.Months[t - 1].BenchX[i] - 1e-6,
                                    $"[{c}] 第{t + 1}月 格{r.Levels[i]} 后退");
                // ③ 台阶超前（C2）
                for (int i = 1; i < r.Levels.Length; i++)
                {
                    if (r.Levels[i] != r.Levels[i - 1] + 1) continue;
                    Assert.True(m.BenchX[i] - m.BenchX[i - 1] >= stepLead - 0.5,
                                $"[{c}] 第{t + 1}月 格{r.Levels[i]} 超前 {m.BenchX[i] - m.BenchX[i - 1]:0.#}m < {stepLead:0.#}m");
                }
                // ④ 三量递推
                if (t == 0) prevPrepared = m.PreparedWt + m.CoalWt - m.NewlyExposedWt;
                double want = prevPrepared + m.NewlyExposedWt - m.CoalWt;
                Assert.True(Math.Abs(m.PreparedWt - want) <= 0.03 * Math.Max(1, Math.Abs(want)),
                            $"[{c}] 第{t + 1}月三量递推：{prevPrepared:0.00}+{m.NewlyExposedWt:0.00}−{m.CoalWt:0.00}"
                          + $"={want:0.00} vs 备采 {m.PreparedWt:0.00}");
                prevPrepared = m.PreparedWt;
                // ⑤ 分账合计
                Assert.True(Math.Abs(m.RockByGapM3.Sum() - m.RockM3) <= 1e-3 * Math.Max(1, m.RockM3),
                            $"[{c}] 第{t + 1}月分账合计 {m.RockByGapM3.Sum():0.0} ≠ 月剥离 {m.RockM3:0.0}");
            }
            ok++;
        }

        _out.WriteLine($"跑通 {ok} 组 · 无解 {infeasible} 组 · 地质退化跳过 {noRock} 组");
        foreach (var kv in reasons.OrderByDescending(x => x.Value)) _out.WriteLine($"  无解原因「{kv.Key}」× {kv.Value}");
        Assert.True(ok >= 20, $"只有 {ok} 组跑通 —— 随机算例大多退化了，这一组等于没判");
    }

    /// <summary>
    /// INV2 <b>配对与导出侧的不变量</b>：守恒、可行性口径自洽。
    /// <list type="number">
    ///   <item>排下的 + 排不下的 = 排产的剥离量</item>
    ///   <item>逐笔 V容 = V实 × Kr</item>
    ///   <item>库容不许排成负</item>
    ///   <item>导出契约永远自洽（<c>Validate</c> 为空）</item>
    ///   <item><c>Feasible</c> ⟺ 硬约束全过 且 全部排得下</item>
    /// </list>
    /// </summary>
    [Fact]
    public void INV2_PairingAndExportInvariantsHold()
    {
        int ok = 0;
        for (int seed = 101; seed <= 150; seed++)
        {
            var c = Roll(seed);
            var rock = Scan(c);
            if (rock == null || rock.RockCellCount == 0 || rock.TotalCoalWt() < 1e-6) continue;

            double total = rock.TotalCoalWt();
            var q = new double[c.Months];
            for (int i = 0; i < c.Months; i++) q[i] = total * c.CoalFrac / c.Months;
            var sched = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
            {
                Rock = rock, AlphaDeg = c.Alpha, ZDatum = c.ZDatum, CoalTargetWt = q,
                LookaheadMonths = c.Lookahead, RecoveryTotalWt = q[0] * c.Lookahead,
                StartInSteadyState = c.Steady, Pace = c.Pace,
            });
            if (!sched.Success) continue;

            // 库容随机：有时够、有时不够
            var rnd = new Random(seed * 7919);
            double need = sched.TotalRockM3 * (0.4 + rnd.NextDouble() * 1.2);
            var slots = new List<DumpSlot>();
            for (int lv = 0; lv < 4; lv++)
                for (int b = 0; b < 3; b++)
                    slots.Add(new DumpSlot
                    {
                        DumpName = b == 0 ? "内排土场" : "外排土场", Level = lv, Order = b,
                        CapacityM3 = need * 1.15 / 12, IsInternal = b == 0,
                        AvailableFromMonth = b == 0 ? 1 + rnd.Next(4) : 1,
                        HaulKm = b == 0 ? 1.2 : 3.8, Cz = 100 + lv * 20,
                    });
            var mats = new GapMaterial[GapCode.Count(c.Seams)];
            for (int g = 0; g < mats.Length; g++)
                mats[g] = new GapMaterial { Name = $"g{g}", Code = "rock", Density = 2.5, Kr = 1.10 + g * 0.01 };

            var dump = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = slots, Materials = mats });
            Assert.True(dump.Success, $"[{c}] 配对失败 {dump.Error}");

            // ① 守恒
            Assert.True(Math.Abs(sched.TotalRockM3 - (dump.TotalInSituM3 + dump.TotalUnplacedM3))
                        <= 1e-3 * Math.Max(1, sched.TotalRockM3), $"[{c}] 配对守恒破了");
            // ② 逐笔换算 ③ 库容不为负
            foreach (var f in dump.Months.SelectMany(m => m.Flows))
                Assert.True(Math.Abs(f.InSituM3 * f.Kr - f.DumpM3) <= 1e-6 * Math.Max(1, f.DumpM3), $"[{c}] Kr 换算错");
            foreach (var kv in dump.RemainByDump)
                Assert.True(kv.Value >= -1e-6, $"[{c}] {kv.Key} 库容排成负 {kv.Value:0.0}");

            // ④⑤ 导出
            var e = MinePlanExport.Build(sched, rock, dump);
            var bad = e.Validate();
            Assert.True(bad.Count == 0, $"[{c}] 导出不自洽：{string.Join(" / ", bad)}");
            Assert.Equal(sched.AllChecksOk && dump.AllPlaced, e.Feasible);
            // JSON 往返
            var back = MinePlanExport.FromJson(e.ToJson(), out string je);
            Assert.True(back != null, $"[{c}] JSON 读不回来 {je}");
            Assert.Empty(back!.Validate());
            ok++;
        }
        _out.WriteLine($"配对+导出不变量：{ok} 组通过");
        Assert.True(ok >= 15, $"只有 {ok} 组跑通 —— 这一组等于没判");
    }

    /// <summary>
    /// INV3 <b>拉绳 / 派生 / 外循环的不变量</b>。
    /// <list type="number">
    ///   <item>拉绳解永远落在走廊内且单调（<c>F ≤ C ≤ Cap</c>）</item>
    ///   <item>派生：指标各异数 ≤ 解出数 ≤ 尝试数；有可行方案时推荐的必可行</item>
    ///   <item>外循环：内排率逐轮不降（单调上升是它收敛的来源）</item>
    /// </list>
    /// </summary>
    [Fact]
    public void INV3_TautStringDeriverAndLoopInvariants()
    {
        int okT = 0, okD = 0, okC = 0, feasSeen = 0;
        for (int seed = 201; seed <= 240; seed++)
        {
            var c = Roll(seed);
            var rock = Scan(c);
            if (rock == null || rock.RockCellCount == 0 || rock.TotalCoalWt() < 1e-6) continue;

            double total = rock.TotalCoalWt();
            var q = new double[c.Months];
            for (int i = 0; i < c.Months; i++) q[i] = total * c.CoalFrac / c.Months;
            var inp = new MonthlyScheduleInput
            {
                Rock = rock, AlphaDeg = c.Alpha, ZDatum = c.ZDatum, CoalTargetWt = q,
                LookaheadMonths = c.Lookahead, RecoveryTotalWt = q[0] * c.Lookahead,
                StartInSteadyState = c.Steady, Pace = c.Pace,
            };
            var r = MonthlyMineScheduler.Solve(inp);
            if (!r.Success) continue;

            // ① 拉绳：累计剥离单调、且不低于"必须剥"
            double prevCum = 0;
            foreach (var m in r.Months)
            {
                Assert.True(m.RockCumM3 >= prevCum - 1e-6, $"[{c}] 第{m.Month}月累计剥离回退");
                Assert.True(m.LeadStockM3 >= -1e-6, $"[{c}] 第{m.Month}月超前储备为负（跌破必须剥）");
                prevCum = m.RockCumM3;
            }
            okT++;

            // ② 派生
            var d = ScheduleDeriver.Derive(inp);
            if (d.Success)
            {
                Assert.True(d.Schemes.Count + d.Failed.Count == d.Attempted, $"[{c}] 派生方案数对不上（有被静默丢的）");
                Assert.True(d.DistinctCount <= d.Schemes.Count, $"[{c}] 指标各异数 > 解出数");
                Assert.NotNull(d.Recommended);
                // 逐个算例允许"这组没有可行方案"（扫的就是退化输入），
                // 但**整轮扫完必须有算例真走到过这一支**，否则"可行优先"这条规则从没被验过。
                // 计数在下面与 okD 一起判。
                if (d.Schemes.Any(s => s.Feasible))
                { feasSeen++; Assert.True(d.Recommended!.Feasible, $"[{c}] 有可行方案却推荐了不可行的"); }
                foreach (var s in d.Schemes) Assert.InRange(s.Score, -1e-9, 100 + 1e-9);
                okD++;
            }

            // ③ 外循环：内排率逐轮不降
            var slots = new List<DumpSlot>();
            var su = new List<double>(); var soff = new List<double>();
            for (int lv = 0; lv < 4; lv++)
                for (int b = 0; b < 3; b++)
                {
                    bool inner = b == 0;
                    slots.Add(new DumpSlot
                    {
                        DumpName = inner ? "内排" : "外排", Level = lv, Order = b,
                        CapacityM3 = r.TotalRockM3 * 0.4, IsInternal = inner,
                        AvailableFromMonth = 1, HaulKm = inner ? 1.0 : 4.0, Cz = 100 + lv * 20,
                    });
                    su.Add(inner ? rock.MinBin * rock.SliceWidth + b * 50 : double.NegativeInfinity);
                    soff.Add(inner ? 0.2 : 2.0);
                }
            var mats = new GapMaterial[GapCode.Count(c.Seams)];
            for (int g = 0; g < mats.Length; g++) mats[g] = new GapMaterial { Name = $"g{g}", Code = "rock", Density = 2.5, Kr = 1.15 };

            var cp = CoupledMinePlanner.Solve(new CoupledPlanInput
            {
                Schedule = inp, Dump = new DumpAllocationInput { Slots = slots, Materials = mats },
                SlotU = su.ToArray(), SlotOffsetKm = soff.ToArray(),
                InternalClearanceM = 100, VoidFillFactor = 0.9, CoalDensity = 1.35,
                MaxIterations = 3, ConvergeTolPct = 1.0,
            });
            if (cp.Success)
            {
                var rounds = cp.Iterations.Where(x => x.ScheduleOk).ToList();
                for (int i = 1; i < rounds.Count; i++)
                    Assert.True(rounds[i].InternalRatePct >= rounds[i - 1].InternalRatePct - 1e-6,
                                $"[{c}] 外循环第{rounds[i].Round}轮内排率反而降了 —— 单调性破了，收敛性就没保证");
                Assert.False(string.IsNullOrWhiteSpace(cp.ConvergenceNote), $"[{c}] 外循环没给收敛说明");
                okC++;
            }
        }
        _out.WriteLine($"拉绳 {okT} 组 · 派生 {okD} 组 · 外循环 {okC} 组");
        Assert.True(okT >= 15 && okD >= 15 && okC >= 10, "跑通的组数太少，这一组等于没判");
        // ★ "可行优先"那条规则藏在 `if (有可行方案)` 里 —— 一个算例都没走到的话，
        //   它从没被验过，而整条判据照样绿。
        Assert.True(feasSeen > 0, "整轮扫下来没有任何算例解出可行方案 —— 「可行优先」这条规则空过了");
    }

    /// <summary>
    /// INV4 <b>退化与极端输入：不许崩，要么成功、要么给出原因</b>。
    ///
    /// <para>生产环境最经典的崩法就是退化输入 —— 空剖面、单月、零煤、极端角度、层序穿插。
    /// 这些在手挑夹具里永远不会出现，但用户点几下就能造出来。
    /// <b>本组不判"结果对不对"，只判"不许抛异常，且失败必须说清"。</b></para>
    /// </summary>
    [Fact]
    public void INV4_DegenerateInputs_NeverThrowAndAlwaysExplain()
    {
        var cases = new List<(string Name, Func<MonthlyScheduleInput> Make)>();

        // 一个正常的底子，各条在它上面改一处
        RockProfile Good()
        {
            var c = Roll(7); c.Dip = 0.05; c.Seams = 2; c.Months = 8;
            return Scan(c) ?? throw new InvalidOperationException("底子造不出来");
        }
        MonthlyScheduleInput Base(RockProfile rk, int months = 8)
        {
            var q = new double[months];
            for (int i = 0; i < months; i++) q[i] = rk.TotalCoalWt() * 0.3 / months;
            return new MonthlyScheduleInput
            {
                Rock = rk, AlphaDeg = 18, ZDatum = 200, CoalTargetWt = q,
                LookaheadMonths = 3, RecoveryTotalWt = q[0] * 3, StartInSteadyState = true,
            };
        }

        cases.Add(("空剖面（无桶）", () => { var i = Base(Good()); i.Rock = new RockProfile { Success = true, SliceWidth = 10, BenchHeight = 15 }; return i; }));
        cases.Add(("剖面标为失败", () => { var i = Base(Good()); i.Rock = new RockProfile { Success = false, Error = "上游没成", SliceWidth = 10, BenchHeight = 15 }; return i; }));
        cases.Add(("台阶高为 0", () => { var rk = Good(); rk.BenchHeight = 0; return Base(rk); }));
        cases.Add(("零个月", () => { var i = Base(Good()); i.CoalTargetWt = Array.Empty<double>(); return i; }));
        cases.Add(("只有 1 个月", () => Base(Good(), 1)));
        cases.Add(("全零煤量目标", () => { var i = Base(Good()); Array.Fill(i.CoalTargetWt, 0.0); return i; }));
        cases.Add(("某月煤量为 0", () => { var i = Base(Good()); i.CoalTargetWt[3] = 0; return i; }));
        cases.Add(("煤量目标超过可采上限", () => { var i = Base(Good()); Array.Fill(i.CoalTargetWt, i.Rock.TotalCoalWt()); return i; }));
        cases.Add(("N = 0", () => { var i = Base(Good()); i.LookaheadMonths = 0; return i; }));
        cases.Add(("N 远大于月数", () => { var i = Base(Good()); i.LookaheadMonths = 99; return i; }));
        cases.Add(("α 极小 (0.01°)", () => { var i = Base(Good()); i.AlphaDeg = 0.01; return i; }));
        cases.Add(("α 极大 (89.99°)", () => { var i = Base(Good()); i.AlphaDeg = 89.99; return i; }));
        cases.Add(("α 为负", () => { var i = Base(Good()); i.AlphaDeg = -30; return i; }));
        // 0 = 该月不能剥（不是"不限"）。`new double[T]` 忘了填就是这个 —— 必须当场无解，不许静默变"无限能力"
        cases.Add(("能力全 0（忘了填）", () => { var i = Base(Good()); i.StripCapM3 = new double[8]; return i; }));
        cases.Add(("能力填负数 = 不限", () => { var i = Base(Good()); i.StripCapM3 = Enumerable.Repeat(-1.0, 8).ToArray(); return i; }));
        cases.Add(("某月停产（能力 0）", () => { var i = Base(Good()); i.StripCapM3 = Enumerable.Repeat(1e9, 8).ToArray(); i.StripCapM3[4] = 0; return i; }));
        cases.Add(("能力极小", () => { var i = Base(Good()); i.StripCapM3 = Enumerable.Repeat(1.0, 8).ToArray(); return i; }));
        cases.Add(("回采煤量为负", () => { var i = Base(Good()); i.RecoveryTotalWt = -100; return i; }));
        cases.Add(("剥采比上限为 0", () => { var i = Base(Good()); i.RatioCeiling = 0; return i; }));
        cases.Add(("期初位置数组长度不对", () => { var i = Base(Good()); i.InitialBenchX = new double[] { 1, 2, 3 }; return i; }));
        cases.Add(("逐层目标长度不对", () => { var i = Base(Good()); i.CoalTargetBySeam = new[] { new double[] { 1 } }; return i; }));
        cases.Add(("外部累计上限全 0", () => { var i = Base(Good()); i.ExtraCumCapM3 = new double[9]; return i; }));

        int okCnt = 0, failCnt = 0;
        foreach (var (name, make) in cases)
        {
            MonthlyScheduleResult r;
            try { r = MonthlyMineScheduler.Solve(make()); }
            catch (Exception ex) { Assert.Fail($"「{name}」抛异常：{ex.GetType().Name} {ex.Message}"); return; }

            if (r.Success)
            {
                okCnt++;
                // 成功就必须自洽：月数对、位置数组齐、无 NaN
                Assert.Equal(Math.Max(0, r.Months.Count), r.Months.Count);
                foreach (var m in r.Months)
                {
                    Assert.False(double.IsNaN(m.RockM3) || double.IsInfinity(m.RockM3), $"「{name}」第{m.Month}月剥离是 NaN/∞");
                    Assert.False(double.IsNaN(m.Ratio) || double.IsInfinity(m.Ratio), $"「{name}」第{m.Month}月剥采比是 NaN/∞");
                    Assert.Equal(r.Levels.Length, m.BenchX.Length);
                    Assert.All(m.BenchX, v => Assert.False(double.IsNaN(v) || double.IsInfinity(v), $"「{name}」台阶位置是 NaN/∞"));
                }
                // 导出/导入也不许崩
                var e = MinePlanExport.Build(r, make().Rock);
                Assert.NotNull(e.ToJson());
            }
            else
            {
                failCnt++;
                Assert.False(string.IsNullOrWhiteSpace(r.Error), $"「{name}」失败却没给原因");
                Assert.Empty(r.Months);
            }
            _out.WriteLine($"{(r.Success ? "✓出计划" : "✗有原因"),-8} {name,-24} {(r.Success ? "" : r.Error.Split('—')[0].Trim())}");
        }
        _out.WriteLine($"\n退化输入 {cases.Count} 例：出计划 {okCnt} · 明确失败 {failCnt} · 抛异常 0");
        Assert.Equal(cases.Count, okCnt + failCnt);
    }

    /// <summary>
    /// INV5 <b>引擎替用户改过的地方，必须留条</b>（<see cref="MonthlyScheduleResult.Notes"/>）。
    ///
    /// <para>退化输入的真正危险不是崩，是<b>静默</b>：α 填成负数被夹到 1°、
    /// 期初位置数组长度不对被整个忽略 —— <b>计划照出、每项校核照绿，没人知道自己填的没生效</b>。
    /// INV4 只判"不崩"，这一条判"不瞒"。</para>
    /// </summary>
    [Theory]
    [InlineData("alpha", "工作帮坡角")]
    [InlineData("lookahead", "备采保有月数")]
    [InlineData("recovery", "回采煤量")]
    [InlineData("initbench", "期初台阶位置")]
    [InlineData("perseam", "逐层煤量目标")]
    public void INV5_SilentFallbacks_AreAlwaysNoted(string which, string expect)
    {
        var c = Roll(7); c.Dip = 0.05; c.Seams = 2; c.Months = 8;
        var rock = Scan(c)!;
        var q = new double[8];
        for (int i = 0; i < 8; i++) q[i] = rock.TotalCoalWt() * 0.3 / 8;
        var inp = new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = 18, ZDatum = 200, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = q[0] * 3, StartInSteadyState = true,
        };
        switch (which)
        {
            case "alpha": inp.AlphaDeg = -30; break;
            case "lookahead": inp.LookaheadMonths = -2; break;
            case "recovery": inp.RecoveryTotalWt = -100; break;
            case "initbench": inp.InitialBenchX = new double[] { 1, 2, 3 }; break;
            case "perseam": inp.CoalTargetBySeam = new[] { new double[] { 1 } }; break;
        }
        var r = MonthlyMineScheduler.Solve(inp);
        Assert.True(r.Success, r.Error);
        foreach (var n in r.Notes) _out.WriteLine("  " + n);
        Assert.Contains(r.Notes, n => n.Contains(expect));
    }

    /// <summary>INV5b 参数都正常时<b>不许乱留条</b> —— 否则告警泛滥，真的那条就被淹没了。</summary>
    [Fact]
    public void INV5b_CleanInput_ProducesNoNoise()
    {
        var c = Roll(7); c.Dip = 0.05; c.Seams = 2; c.Months = 8;
        // ★ 块体必须**够长**，否则"干净输入"根本不干净：工作帮比煤前界超前
        //   (K−1)×H/tanα ≈ 数百米，随机 NX(40~80 ⇒ 400~800m) 撑不到 8 个月，
        //   最上台阶会推出剖面数据的尽头 ⇒「必须剥」自己往下掉（引擎现在会就此告警）。
        //   ⚠ 这条正是那个新告警**第一次跑就抓到的**：本判据此前断言"无告警"，
        //   而它用的算例其实是短的 —— 只是引擎当时还不会说。**把算例改够长，别把告警关掉。**
        c.NX = 140;
        var rock = Scan(c)!;
        var q = new double[8];
        for (int i = 0; i < 8; i++) q[i] = rock.TotalCoalWt() * 0.3 / 8;
        var r = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = 18, ZDatum = 200, CoalTargetWt = q,
            LookaheadMonths = 3, RecoveryTotalWt = q[0] * 3, StartInSteadyState = true,
        });
        Assert.True(r.Success, r.Error);
        _out.WriteLine(r.Notes.Count == 0 ? "干净输入：无告警 ✓" : "意外告警：" + string.Join(" / ", r.Notes));
        Assert.Empty(r.Notes);
    }
}
