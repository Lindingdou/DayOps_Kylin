using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Data;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests
{
    /// <summary>
    /// WS 组 · 【工作帮范围】—— 采场 / 排土场范围之内再收一层，本期实际在哪儿干。
    ///
    /// <para><b>前缀为什么是 WS</b>：U（排产）· L（台账存储）· D（排土条带）· F（台阶形态）·
    /// Z（作业区域）· BM（量源）· FA（作业面单元）都已占用，本仓库撞过一次规则号，
    /// 新子系统一律另起字母前缀。</para>
    ///
    /// <para><b>这一组的重点不是"能不能裁"，而是三件容易静默的事</b>：
    /// <list type="number">
    ///   <item><b>不给范围时旧口径必须一字不变</b> —— 新增闸门最容易顺手改掉默认行为；</item>
    ///   <item><b>圈错地方要拦得住</b> —— 范围画到母范围外时，两侧的表现都是"一个都没出来"，
    ///     而各自的原话说的是"没有露头带 / 台阶壳子本身就空"，那两句会把人引去调坡度、查台阶线；</item>
    ///   <item><b>编号不许跟着本期范围重排</b> —— UnitId 是位置的身份，
    ///     同一个位置在不同工作帮下必须还是同一个编号。</item>
    /// </list></para>
    /// </summary>
    public class WorkSlopeRangeTests
    {
        private readonly ITestOutputHelper _out;
        public WorkSlopeRangeTests(ITestOutputHelper o) => _out = o;

        // ── 夹具 ────────────────────────────────────────────────────

        /// <summary>矩形环（扁平 xy，逆时针）。</summary>
        private static double[] Rect(double x0, double y0, double x1, double y1)
            => new[] { x0, y0, x1, y0, x1, y1, x0, y1 };

        /// <summary>一级直排土台阶：走向沿 +Y 共 300m，沿 +X 推进。（同 DumpStripPlannerTests 的基准算例）</summary>
        private const double CrestZ = 1288, ToeZ = 1276, FaceRun = 4, StrikeTotal = 300;

        private static DumpStripPlanner.BenchInput StraightBench(int level = 1)
            => new()
            {
                LevelIndex = level,
                CrestZ = CrestZ,
                ToeZ = ToeZ,
                CrestXyz = new[] { 0.0, 0.0, CrestZ, 0.0, StrikeTotal, CrestZ },
                ToeXyz = new[] { FaceRun, 0.0, ToeZ, FaceRun, StrikeTotal, ToeZ },
            };

        /// <summary>排土场边界：x 推到 250 为止，走向方向不裁。</summary>
        private static double[] DumpSite() => Rect(-50, -50, 250, 350);

        private static DumpStripPlanner.Options DumpOpt(double[]? workRing = null)
            => new()
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = DumpSite(),
                WorkSlopeRingXy = workRing,
            };
        // ═══════════════════════════════════════════════════════════
        //  WS1–WS4 环本身
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void WS1_不合法的环要判得出来_并且说得出为什么()
        {
            // 射线法对这四种输入都不抛，只会给出"点全在外面"这种看着正常的答案 ——
            // 于是结果是"一条都没生成"，而没有一个字提到环有问题。
            var bad = new (string What, double[]? Ring)[]
            {
                ("null",        null),
                ("顶点只有 2 个", new[] { 0.0, 0.0, 10.0, 10.0 }),
                ("坐标个数是奇数", new[] { 0.0, 0.0, 10.0, 10.0, 20.0 }),
                ("三点共线",     new[] { 0.0, 0.0, 10.0, 0.0, 20.0, 0.0 }),
                ("含 NaN",      new[] { 0.0, 0.0, 10.0, double.NaN, 10.0, 10.0 }),
            };

            foreach (var (what, ring) in bad)
            {
                Assert.False(WorkSlopeRange.IsValidRing(ring, out string why), what);
                Assert.False(string.IsNullOrWhiteSpace(why), what + " 判了不合法却没说原因");
                _out.WriteLine($"WS1 ✓ {what} → {why}");
            }

            Assert.True(WorkSlopeRange.IsValidRing(Rect(0, 0, 100, 100), out _));
        }

        [Fact]
        public void WS2_环的基本性质_闭合与否同答案_顺逆时针同面积()
        {
            var open = Rect(0, 0, 100, 100);
            var closed = new List<double>(open) { 0, 0 }.ToArray();     // 首尾重复一个点

            // 闭合多段线首尾同点。两种写法必须给同一个答案 —— 否则"图上圈的线闭没闭合"
            // 就会悄悄改变哪些格算在内。
            foreach (var (x, y) in new[] { (50.0, 50.0), (-1.0, 50.0), (100.5, 50.0), (0.0, 0.0) })
                Assert.Equal(WorkSlopeRange.Contains(open, x, y), WorkSlopeRange.Contains(closed, x, y));

            // RingFromXyz 要把重复的末点去掉（留着不出错，但面积与采样都白算一遍）
            var xyz = new List<double> { 0, 0, 5, 100, 0, 5, 100, 100, 5, 0, 100, 5, 0, 0, 5 };
            var ring = WorkSlopeRange.RingFromXyz(xyz);
            Assert.Equal(4, ring.Length / 2);

            // 顺时针与逆时针只差符号，面积绝对值必须一样
            var cw = new[] { 0.0, 0.0, 0.0, 100.0, 100.0, 100.0, 100.0, 0.0 };
            Assert.Equal(Math.Abs(WorkSlopeRange.SignedArea(open)), Math.Abs(WorkSlopeRange.SignedArea(cw)), 6);
            Assert.Equal(10_000, Math.Abs(WorkSlopeRange.SignedArea(open)), 6);
            _out.WriteLine("WS2 ✓ 闭合/未闭合同答案 · 顺逆时针同面积 10000 m²");
        }

        [Fact]
        public void WS3_四种关系都要分得开_圈错地方必须不可用()
        {
            var parent = Rect(0, 0, 1000, 1000);

            var notGiven = WorkSlopeRange.Diagnose(null, parent);
            Assert.Equal(WorkSlopeRange.Relation.NotGiven, notGiven.Relation);
            Assert.True(notGiven.Usable);                       // 没给 = 旧口径，不是错

            // ★ 这一条是"圈错地方"唯一的拦点
            var outside = WorkSlopeRange.Diagnose(Rect(5000, 5000, 5300, 5300), parent);
            Assert.Equal(WorkSlopeRange.Relation.Outside, outside.Relation);
            Assert.False(outside.Usable, "圈在母范围外还判可用 ⇒ 后面就是一条都不出且没人知道为什么");
            Assert.Contains("不在这个采场", outside.Caption);

            var coversAll = WorkSlopeRange.Diagnose(Rect(-500, -500, 1500, 1500), parent);
            Assert.Equal(WorkSlopeRange.Relation.CoversAll, coversAll.Relation);
            Assert.Contains("等于没收窄", coversAll.Caption);

            var narrows = WorkSlopeRange.Diagnose(Rect(0, 0, 400, 1000), parent);
            Assert.Equal(WorkSlopeRange.Relation.Narrows, narrows.Relation);
            Assert.InRange(narrows.CoverOfParent, 0.30, 0.50);   // 约 40%
            _out.WriteLine($"WS3 ✓ 收窄占比 {narrows.CoverOfParent * 100:0.#}% · {narrows.Caption}");
        }

        [Fact]
        public void WS4_没量过的比例是NaN不是0_太小要说太小而不是说在外面()
        {
            // 母范围没给时比例无从量起。给 0 会被读成"完全不重叠" —— 那是一个没量过的结论。
            var noParent = WorkSlopeRange.Diagnose(Rect(0, 0, 100, 100), null);
            Assert.Equal(WorkSlopeRange.Relation.Narrows, noParent.Relation);
            Assert.True(double.IsNaN(noParent.CoverOfParent), "母范围没给却报出了占比");

            // 比采样格还小的环：是"太小量不出来"，不是"在外面" —— 两者要人做的事完全不同
            var tiny = WorkSlopeRange.Diagnose(Rect(500, 500, 500.5, 500.5), Rect(0, 0, 10_000, 10_000));
            Assert.Equal(WorkSlopeRange.Relation.Invalid, tiny.Relation);
            Assert.Contains("太小", tiny.Why);
            _out.WriteLine("WS4 ✓ " + tiny.Caption);
        }

        // ═══════════════════════════════════════════════════════════
        //  WS5–WS8 排土侧接入
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void WS5_不给工作帮范围时旧口径一字不变()
        {
            // 新增闸门最容易顺手改掉默认行为，所以基线单独判一条。
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, DumpOpt());

            Assert.True(r.Ok, r.Message);
            Assert.Equal(3, r.Cells.Select(c => c.PanelIndex).Distinct().Count());   // 300m ÷ 100m = 3 幅
            Assert.DoesNotContain(r.Drops, d => d.Reason.Contains("工作帮"));
            Assert.DoesNotContain(r.Notes, n => n.Contains("工作帮"));
            _out.WriteLine($"WS5 ✓ 基线 {r.Cells.Count} 个位置 / 3 幅");
        }

        [Fact]
        public void WS6_收窄之后只剩范围内的_丢弃要记账()
        {
            var baseline = DumpStripPlanner.Plan(new[] { StraightBench() }, DumpOpt());

            // 工作帮只圈走向前 100m（第 1 幅那一段）
            var narrowed = DumpStripPlanner.Plan(new[] { StraightBench() },
                                                 DumpOpt(Rect(-50, -10, 250, 100)));

            Assert.True(narrowed.Ok, narrowed.Message);
            Assert.True(narrowed.Cells.Count < baseline.Cells.Count,
                        $"收窄后没变少（{narrowed.Cells.Count} vs {baseline.Cells.Count}）");
            Assert.All(narrowed.Cells, c => Assert.True(c.Cy <= 100 + 1e-6, $"{c.Code} 的质心在范围外"));

            var drop = narrowed.Drops.FirstOrDefault(d => d.Reason.Contains("工作帮"));
            Assert.NotNull(drop);
            Assert.Equal(baseline.Cells.Count - narrowed.Cells.Count, drop!.Count);
            _out.WriteLine($"WS6 ✓ {baseline.Cells.Count} → {narrowed.Cells.Count} 个，挡下 {drop.Count}：{drop.Reason}");
        }

        [Fact]
        public void WS7_编号与幅数不许跟着本期范围重排()
        {
            var baseline = DumpStripPlanner.Plan(new[] { StraightBench() }, DumpOpt());

            // 只留走向中段（第 2 幅），前后两幅都挡掉 —— 这是最容易触发"重排编号"的形状
            var mid = DumpStripPlanner.Plan(new[] { StraightBench() },
                                            DumpOpt(Rect(-50, 100, 250, 200)));
            Assert.True(mid.Ok, mid.Message);
            Assert.NotEmpty(mid.Cells);

            // ★ 同一个位置在不同工作帮下必须还是同一个编号：UnitId 是位置的身份，
            //   落库唯一索引、排产取用、图上挂属性全靠它。按本期范围重编号 ⇒ 一个位置变成两个东西。
            var baseByCode = baseline.Cells.ToDictionary(c => c.Code, c => c);
            foreach (var c in mid.Cells)
            {
                Assert.True(baseByCode.ContainsKey(c.Code), $"{c.Code} 在基线里不存在 ⇒ 编号被重排了");
                var b = baseByCode[c.Code];
                Assert.Equal(b.PanelIndex, c.PanelIndex);
                Assert.Equal(b.StepIndex, c.StepIndex);
                Assert.Equal(b.PanelCount, c.PanelCount);       // 幅数仍是几何总幅数，不是本期剩下的幅数
                Assert.Equal(b.CapacityM3, c.CapacityM3, 6);    // 库容不跟着本期范围变
            }

            // P 号有断档正是"没重排"的证据
            var panels = mid.Cells.Select(c => c.PanelIndex).Distinct().OrderBy(x => x).ToList();
            Assert.DoesNotContain(1, panels);
            _out.WriteLine($"WS7 ✓ 保留幅号 {string.Join("/", panels)}（断档 = 没重排）· PanelCount 仍为 "
                         + mid.Cells[0].PanelCount);
        }

        [Fact]
        public void WS8_圈错地方时报错要指向范围_不能说成台阶壳子空()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() },
                                          DumpOpt(Rect(9000, 9000, 9300, 9300)));

            Assert.False(r.Ok);
            Assert.Empty(r.Cells);
            // ★ 原话"多半是台阶壳子本身就空"会让人去查台阶线 —— 而线是好的，位置也切出来了
            Assert.Contains("工作帮范围", r.Message);
            Assert.DoesNotContain("台阶壳子本身就空", r.Message);
            _out.WriteLine("WS8 ✓ " + r.Message);
        }

        // ═══════════════════════════════════════════════════════════
        //  WS9–WS11 采场侧接入
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 【WS12】类别真值表。
        ///
        /// <para>「采矿模型」原先用黑名单「名字里不含 dump 就算采场」，于是
        /// <c>pit_working_slope</c> 会被当成一个独立采场<b>再建一遍（量算两遍）</b>，
        /// 而 <c>dump_working_slope</c> 因为含 dump 字样反倒被正确排除 ——
        /// 一个中招一个不中，这种半对半错最难发现。三个谓词钉死之后，
        /// <b>以后再加类别默认就该被排除</b>，要算采场必须显式改白名单。</para>
        /// </summary>
        [Fact]
        public void WS12_类别真值表_工作帮既不是采场也不是排土场()
        {
            var all = new[]
            {
                MineableRegions.CatPit,
                MineableRegions.CatExternalDump,
                MineableRegions.CatInternalDump,
                MineableRegions.CatPitWorkingSlope,
                MineableRegions.CatDumpWorkingSlope,
                MineableRegions.CatMineable,
            };
            // 值域不许有重复：撞了就会有两项指同一类，下拉与筛选一起错
            Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());

            // ① 采场是白名单：只有 pit
            foreach (var c in all)
                Assert.Equal(c == MineableRegions.CatPit, MineableRegions.IsPit(c));

            // ② 排土场只有内排/外排 —— dump_working_slope 名字里带 dump，最容易误判
            foreach (var c in all)
                Assert.Equal(c is "external_dump" or "internal_dump", MineableRegions.IsDumpSite(c));

            // ③ 工作帮只有那两类
            foreach (var c in all)
                Assert.Equal(c is "pit_working_slope" or "dump_working_slope", MineableRegions.IsWorkingSlope(c));

            // ④ 三个谓词互斥：一个类别不能既是采场又是排土场/工作帮
            foreach (var c in all)
            {
                int hits = (MineableRegions.IsPit(c) ? 1 : 0)
                         + (MineableRegions.IsDumpSite(c) ? 1 : 0)
                         + (MineableRegions.IsWorkingSlope(c) ? 1 : 0);
                Assert.True(hits <= 1, $"{c} 同时命中 {hits} 个谓词");
            }

            // ⑤ null / 空 / 没见过的类别一律不算任何一种（新类别默认被排除，这正是黑名单当年的错）
            foreach (var c in new string?[] { null, "", "  ", "wide_bench", "pit_extra", "wide_pit_working_slope_x" })
            {
                Assert.False(MineableRegions.IsPit(c), $"「{c}」被当成了采场");
                Assert.False(MineableRegions.IsDumpSite(c), $"「{c}」被当成了排土场");
                Assert.False(MineableRegions.IsWorkingSlope(c), $"「{c}」被当成了工作帮");
            }

            // ⑥ 大小写不敏感（库里的值可能是别处写进去的）
            Assert.True(MineableRegions.IsPit("PIT"));
            Assert.True(MineableRegions.IsWorkingSlope("Pit_Working_Slope"));

            // ⑦ 六个类别的显示名：各不相同、无一落兜底、且"可采区域"这个词不再当类别名
            //    （它是整库的统称 —— "限定可采区域"、"可采区域库"，一词两义会让人以为自己选错了类别）
            var names = all.Select(MineableRegions.DisplayName).ToList();
            Assert.Equal(all.Length, names.Distinct(StringComparer.Ordinal).Count());
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
            Assert.DoesNotContain("可采区域", names);
            Assert.Equal("未分类", MineableRegions.DisplayName(MineableRegions.CatMineable));
            Assert.Equal("剥采工作帮", MineableRegions.DisplayName(MineableRegions.CatPitWorkingSlope));
            Assert.Equal("排土工作帮", MineableRegions.DisplayName(MineableRegions.CatDumpWorkingSlope));
            // 认不出的类别把原码显示出来，别冒充成某一类
            Assert.Equal("some_new_cat", MineableRegions.DisplayName("some_new_cat"));
            Assert.Equal("未分类", MineableRegions.DisplayName(null));

            // ⑧ ★ mineable 的【值】不许改：露头带/斜面模板那条链按值筛
            //    （「自动·可采范围」= mineable + pit），改值它会静默少裁一片。
            Assert.Equal("mineable", MineableRegions.CatMineable);
            _out.WriteLine("WS12 ✓ 六个类别 × 三个谓词，互斥、大小写不敏感、新类别默认排除；"
                         + "显示名各不相同且不再叫「可采区域」");
        }
    }
}
