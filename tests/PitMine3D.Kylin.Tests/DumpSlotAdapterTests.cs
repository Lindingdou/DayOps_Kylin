// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/DumpSlotAdapterTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// G22 组 · 排土位置适配器（<c>DumpStripPlanner.Cell</c> → <see cref="DumpSlot"/>）。
/// <para>这一组的重点只有一个：<b>极性</b>。<c>LevelIndex 1 = 最上一级</c>，
/// 而排土自下而上承接 —— 翻错了图上有台阶、现场没法卸，而且每一项校核都还是"✓"。</para>
/// </summary>
public sealed class DumpSlotAdapterTests
{
    private readonly ITestOutputHelper _out;
    public DumpSlotAdapterTests(ITestOutputHelper o) => _out = o;

    /// <summary>4 级 × 2 幅 × 3 带的合成位置清单。LevelIndex 1 在最上（标高最高）。</summary>
    private static List<DumpStripPlanner.Cell> Cells(int levels = 4, int panels = 2, int steps = 3,
                                                     double topZ = 1300, double benchH = 20)
    {
        var list = new List<DumpStripPlanner.Cell>();
        for (int lv = 1; lv <= levels; lv++)
            for (int p = 1; p <= panels; p++)
                for (int s = 1; s <= steps; s++)
                {
                    double crest = topZ - (lv - 1) * benchH;
                    list.Add(new DumpStripPlanner.Cell
                    {
                        LevelIndex = lv, PanelIndex = p, PanelCount = panels, StepIndex = s,
                        CrestZ = crest, ToeZ = crest - benchH,
                        CapacityM3 = 1e4 * lv,                     // 各级容量不同，便于分辨
                        Cx = 1000 + s * 40, Cy = 500 + p * 60, Cz = crest - benchH / 2,
                        StrikeLenM = 200, StripWidthM = 40,
                        Code = $"内排1-L{lv}-P{p:00}-S{s:00}",
                        IsWorkingFace = s == 1,
                    });
                }
        return list;
    }

    // ── 带内子格：一带切成几个位置之后，交到排产手里不能丢、不能并列 ──────────

    /// <summary>
    /// 【带内子格必须完整过桥，且位次不并列】
    ///
    /// 外凸拐角把带撑长之后，超过分割长度 L 的带会在带内再切 —— 切出来的几个位置
    /// (级,幅,带) 完全相同、只差子号。这一条盯两件事：
    ///   ① 一个都不能丢，库容一方不能少 —— 排产吃的是这份清单，丢了就是凭空少库容；
    ///   ② 位次不能并列 —— 分配器靠 <c>s.Order &lt; head.Order</c> 挑先后，
    ///      并列的话同一份输入排出来的顺序不定，比选结果没法复现。
    ///
    /// 【为什么单列一条】同一个"键里没有子号"的坑已经栽过两次：dump_strip 的唯一索引
    /// （落库时行被直接挡掉、清单和图上却还在）、位置编号本身（幅号按壳子数导致撞号）。
    /// 排产这一端是同一族的第三处。
    /// </summary>
    [Fact]
    public void SubSlicedBands_SurviveTheHandoff_WithNoTiedOrder()
    {
        var cells = Cells(levels: 2, panels: 2, steps: 2);
        int plain = cells.Count;

        // 给 L1-P01-S02 那一带补 4 个子格（模拟一带被切成 5 个位置）
        var seed = cells.First(c => c.LevelIndex == 1 && c.PanelIndex == 1 && c.StepIndex == 2);
        seed.SubIndex = 1; seed.SubCount = 5;
        for (int s = 2; s <= 5; s++)
            cells.Add(new DumpStripPlanner.Cell
            {
                LevelIndex = 1, PanelIndex = 1, PanelCount = 2, StepIndex = 2,
                SubIndex = s, SubCount = 5,
                CrestZ = seed.CrestZ, ToeZ = seed.ToeZ,
                CapacityM3 = seed.CapacityM3,
                Cx = seed.Cx + s, Cy = seed.Cy, Cz = seed.Cz,
                StrikeLenM = 80, StripWidthM = 40,
                Code = $"内排1-L1-P01-S02-{s:00}",
            });

        var slots = DumpSlotAdapter.ToSlots(cells, dumpName: "内排1", isInternal: false);
        _out.WriteLine(DumpSlotAdapter.Summary(slots));

        // ① 一个都不能丢，库容一方不能少
        Assert.Equal(plain + 4, slots.Count);
        Assert.Equal(cells.Sum(c => c.CapacityM3), slots.Sum(s => s.CapacityM3), 6);

        // ② 同一级内位次必须是全序 —— 并列一个都不许有
        foreach (var g in slots.GroupBy(s => s.Level))
        {
            var orders = g.Select(s => s.Order).ToList();
            var tied = orders.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
            Assert.True(tied.Count == 0,
                        $"Level {g.Key} 有并列位次 {string.Join(",", tied)} —— 分配器挑不出先后，排产不可复现");
        }

        // ③ 幅之间的先后不能被子号搅乱：同一带里 P01 的所有子格都排在 P02 前面
        var s2 = slots.Zip(cells).Where(t => t.Second.LevelIndex == 1 && t.Second.StepIndex == 2).ToList();
        int maxP1 = s2.Where(t => t.Second.PanelIndex == 1).Max(t => t.First.Order);
        int minP2 = s2.Where(t => t.Second.PanelIndex == 2).Min(t => t.First.Order);
        Assert.True(maxP1 < minP2, $"子号把幅的先后搅乱了：P01 最大位次 {maxP1} ≥ P02 最小位次 {minP2}");
    }

    // ── G22 · 极性 ──────────────────────────────────────────────────────────

    /// <summary>
    /// G22 <b>极性翻转</b>：图上最下一级（LevelIndex 最大）必须映射成 <c>Level 0</c>（最先承接），
    /// 且 Level 与标高<b>反向单调</b>（Level 越小、标高越低）。
    /// </summary>
    [Fact]
    public void G22_LevelPolarity_IsInverted()
    {
        var cells = Cells();
        var slots = DumpSlotAdapter.ToSlots(cells, isInternal: true);
        _out.WriteLine(DumpSlotAdapter.Summary(slots));

        Assert.Equal(cells.Count, slots.Count);
        int maxLv = cells.Max(c => c.LevelIndex);
        foreach (var (c, s) in cells.Zip(slots))
            Assert.Equal(maxLv - c.LevelIndex, s.Level);

        // Level 0 必须是标高最低的那一级
        var byLevel = slots.GroupBy(s => s.Level).OrderBy(g => g.Key)
                           .Select(g => (Level: g.Key, Z: g.Average(s => s.Cz))).ToList();
        foreach (var x in byLevel) _out.WriteLine($"  Level {x.Level} → 平均标高 {x.Z:0.#}m");
        for (int i = 1; i < byLevel.Count; i++)
            Assert.True(byLevel[i].Z > byLevel[i - 1].Z,
                        $"Level {byLevel[i].Level} 的标高 {byLevel[i].Z:0.#} 不比 Level {byLevel[i - 1].Level} 的 {byLevel[i - 1].Z:0.#} 高 —— 极性翻反了");
    }

    /// <summary>
    /// G22b 极性翻转后，配对<b>真的从最下一级开始排</b>（端到端验，不只验字段）。
    /// 工程含义：字段对了不代表行为对；这条走完整条链。
    /// </summary>
    [Fact]
    public void G22b_AfterAdapt_PairingStartsAtTheBottomBench()
    {
        var cells = Cells();
        var slots = DumpSlotAdapter.ToSlots(cells, dumpName: "内排1", isInternal: false);
        double lowestZ = slots.Min(s => s.Cz);

        // 造一个只够排一点点的月计划，看第一笔落在哪一级
        var sched = FakeSchedule(oneMonthM3: 5e3);
        var mats = new GapMaterial[GapCode.Count(1)];
        for (int g = 0; g < mats.Length; g++) mats[g] = new GapMaterial { Name = $"g{g}", Density = 2.5, Kr = 1.15 };
        var r = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = slots, Materials = mats });
        Assert.True(r.Success, r.Error);

        var first = r.Months[0].Flows.First();
        var used = slots.Where(s => s.Level == first.Level).ToList();
        _out.WriteLine($"第一笔排到 Level {first.Level}（标高 {used.Average(s => s.Cz):0.#}m，最低级标高 {lowestZ:0.#}m）");
        Assert.Equal(0, first.Level);
        Assert.Equal(lowestZ, used.Min(s => s.Cz), 3);
    }

    /// <summary>
    /// G22c 同一级内<b>先填满当前排土线那一带</b>（StepIndex=1 的各幅），再往前推一带。
    /// 工程含义：排土线是往前推的，跳带排等于中间留了个坑。
    /// </summary>
    [Fact]
    public void G22c_WithinLevel_FillsCurrentBandAcrossPanelsFirst()
    {
        var cells = Cells(levels: 1, panels: 3, steps: 3);
        var slots = DumpSlotAdapter.ToSlots(cells, dumpName: "内排1");
        // 【断言先后关系，不断言位次的字面值】位次的编码方式是实现细节 ——
        // 带内再切之后要把子号也编进去，字面值必然变；变的时候这条不该跟着红。
        // 真正要钉住的是：排土线一带一带往前推，跳带排等于中间留了个坑。
        var ordered = slots.Zip(cells).OrderBy(t => t.First.Order).ToList();
        _out.WriteLine("填序: " + string.Join(" · ",
            ordered.Take(6).Select(t => $"S{t.Second.StepIndex}P{t.Second.PanelIndex}(位次{t.First.Order})")));

        // 前 3 个必须是第 1 带的三幅，而且幅号 1→2→3
        Assert.Equal(new[] { 1, 1, 1 }, ordered.Take(3).Select(t => t.Second.StepIndex).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Take(3).Select(t => t.Second.PanelIndex).ToArray());
        // 接着才是第 2 带的三幅
        Assert.Equal(new[] { 2, 2, 2 }, ordered.Skip(3).Take(3).Select(t => t.Second.StepIndex).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Skip(3).Take(3).Select(t => t.Second.PanelIndex).ToArray());
    }

    // ── G23 · 其余口径 ──────────────────────────────────────────────────────

    /// <summary>G23 库容原样带过来（已是占容方 D6，适配层<b>不再换算</b>），退化位置不进清单。</summary>
    [Fact]
    public void G23_Capacity_IsCarriedAsDumpVolume()
    {
        var cells = Cells();
        cells.Add(new DumpStripPlanner.Cell { LevelIndex = 2, PanelIndex = 9, StepIndex = 9, CapacityM3 = 0, Code = "内排1-L2-P09-S09" });
        var slots = DumpSlotAdapter.ToSlots(cells);
        Assert.Equal(cells.Count - 1, slots.Count);                       // 0 容量的被剔除
        Assert.Equal(cells.Where(c => c.CapacityM3 > 0).Sum(c => c.CapacityM3), slots.Sum(s => s.CapacityM3), 6);
    }

    /// <summary>G23b 去向名：给了就用给的，没给就从 Code 前缀取。</summary>
    [Fact]
    public void G23b_DumpName_FallsBackToCodePrefix()
    {
        var slots = DumpSlotAdapter.ToSlots(Cells());
        Assert.All(slots, s => Assert.Equal("内排1", s.DumpName));
        var named = DumpSlotAdapter.ToSlots(Cells(), dumpName: "外排2");
        Assert.All(named, s => Assert.Equal("外排2", s.DumpName));
        Assert.Equal("外排1", DumpSlotAdapter.DumpNameFromCode("外排1-L3-P02-S05"));
        Assert.Equal("", DumpSlotAdapter.DumpNameFromCode(null));
    }

    /// <summary>
    /// G23c 投不到推进轴上的位置给 <b>−∞</b> 而不是 0。
    /// 工程含义：0 是轴上的一个真实位置，拿它冒充"投不上"会让那些位置在错误的月份启用。
    /// </summary>
    [Fact]
    public void G23c_UnprojectableSlots_AreNegativeInfinityNotZero()
    {
        var slots = DumpSlotAdapter.ToSlots(Cells(), dumpName: "内排1");
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((0, 400, 100)); wl.Baseline.Add((0, 500, 100));    // 很短，只盖 y∈[400,500]
        wl.Samples.Add((0, 450, 100, 1, 0));
        var u = DumpSlotAdapter.ProjectToAdvanceAxis(slots, new[] { wl }, latTol: 5);
        int off = u.Count(double.IsNegativeInfinity);
        _out.WriteLine($"{slots.Count} 个位置里 {off} 个投不上（工作线只盖 y∈[400,500]，位置都在 y≈560..620）");
        Assert.True(off > 0, "全都投上了 —— 这个夹具证明不了兜底路径");
        Assert.DoesNotContain(u, v => v == 0.0);                            // 绝不能悄悄给 0
    }

    /// <summary>G23d 投得上的位置，u 坐标要和几何一致（推进方向 +X ⇒ u 随 Cx 单调增）。</summary>
    [Fact]
    public void G23d_ProjectedU_TracksGeometry()
    {
        var slots = DumpSlotAdapter.ToSlots(Cells(levels: 1, panels: 1, steps: 5), dumpName: "内排1");
        var wl = new WorkLineGeometry { Success = true };
        wl.Baseline.Add((0, 0, 100)); wl.Baseline.Add((0, 1200, 100));
        wl.Samples.Add((0, 600, 100, 1, 0));
        var u = DumpSlotAdapter.ProjectToAdvanceAxis(slots, new[] { wl });
        var pairs = slots.Zip(u).OrderBy(p => p.First.Cx).ToList();
        _out.WriteLine(string.Join(" · ", pairs.Select(p => $"Cx{p.First.Cx:0}→u{p.Second:0}")));
        Assert.All(u, v => Assert.False(double.IsNegativeInfinity(v)));
        for (int i = 1; i < pairs.Count; i++)
            Assert.True(pairs[i].Second > pairs[i - 1].Second, "u 没跟着 Cx 单调增 —— 投影轴接错了");
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>一个最小的月计划（1 个月、单一层间标签），只为驱动配对。</summary>
    private static MonthlyScheduleResult FakeSchedule(double oneMonthM3)
    {
        var r = new MonthlyScheduleResult { Success = true };
        var byGap = new double[GapCode.Count(1)];
        byGap[GapCode.Overburden] = oneMonthM3;
        r.Months.Add(new MonthRow { Month = 1, CoalWt = 1, RockM3 = oneMonthM3, RockByGapM3 = byGap });
        r.TotalRockM3 = oneMonthM3;
        return r;
    }
}
