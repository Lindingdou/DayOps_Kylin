using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using Xunit;

namespace PitMine3D.Kylin.Tests
{
    /// <summary>
    /// 「潜在排土位置」切分的脱 GUI 验收。
    ///
    /// 排土场规整，所以算例可以规整到【手算得出答案】——这正是它比采场好验收的地方：
    /// 一条直排土线 300m、台阶高 12m、分割长度 100m、条带宽 40m、边界在 x=250，
    /// 那么位置数、每个位置的库容、质心位置全都是能用笔算出来的定数。
    /// 先把这种算例锁死，再谈闭合环、反向线这些真实图纸上的花样。
    ///
    /// 靶子按类别分（踩过两次"不分类别一锅判"的亏）：
    ///   ① 数量与库容 —— 切得对不对
    ///   ② 极性与标高 —— 往外推还是往里推、同一级各带标高动没动（D3/D4）
    ///   ③ 对应关系   —— 坡顶↔坡底配得对不对（防 loft 拧麻花，这是唯一的几何坑）
    ///   ④ 边界与记账 —— 推到哪停、少了多少说不说得清（D5）
    /// </summary>
    public class DumpStripPlannerTests
    {
        // ── 算例：一条直排土线 ────────────────────────────────────────
        //
        //  坡顶线 x=0,   z=1288   ┐ 台阶高 12m
        //  坡底线 x=4,   z=1276   ┘ 坡面投影 4m（12m/71.6°，排土场缓坡量级）
        //  走向沿 +Y，长 300m；推进方向 = 指坡脚 = +X（往外堆）
        //
        private const double CrestZ = 1288, ToeZ = 1276, BenchH = 12, FaceRun = 4, StrikeTotal = 300;

        private static DumpStripPlanner.BenchInput StraightBench(int level = 1)
            => new()
            {
                LevelIndex = level,
                CrestZ = CrestZ,
                ToeZ = ToeZ,
                CrestXyz = new[] { 0.0, 0.0, CrestZ, 0.0, StrikeTotal, CrestZ },
                ToeXyz = new[] { FaceRun, 0.0, ToeZ, FaceRun, StrikeTotal, ToeZ },
            };

        /// <summary>矩形边界环（扁平 xy）。</summary>
        private static double[] Rect(double x0, double y0, double x1, double y1)
            => new[] { x0, y0, x1, y0, x1, y1, x0, y1 };

        // ═══ ① 数量与库容 ═══════════════════════════════════════════

        /// <summary>
        /// 手算：走向 300m ÷ 分割长度 100m = 3 幅（等分，各 100m）。
        /// 每幅沿 +X 推进，第 k 带的后界在 x = k×40；边界 x=250 →
        /// k=6 时后界 240 还在内，k=7 时 280 出界 → 每幅 6 带。共 3×6 = 18 个位置。
        ///
        /// 库容分两种（这正是"第 1 带是真坡面、其后是格子界"的账）：
        ///   第 1 带 = 梯形：100 × 12 × (40 − 4/2)      = 45,600 m³
        ///   第 2..6 带 = 近矩形：100 × 12 × (40 − 0.5/2) = 47,700 m³
        /// 每幅 45,600 + 5×47,700 = 284,100，三幅合计 852,300 m³。
        /// </summary>
        [Fact]
        public void StraightBench_GridsIntoHandCountablePositions()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            }, "外排1");

            Assert.True(r.Ok, r.Message);
            Assert.Equal(18, r.Cells.Count);
            Assert.Equal(3, r.Cells.Select(c => c.PanelIndex).Distinct().Count());
            Assert.Equal(6, r.Cells.Select(c => c.StepIndex).Distinct().Count());

            Assert.All(r.Cells, c => Assert.Equal(100.0, c.StrikeLenM, 3));
            foreach (var c in r.Cells.Where(x => x.StepIndex == 1))
            {
                Assert.True(c.IsWorkingFace);
                Assert.Equal(FaceRun, c.FaceRunM, 3);        // 第 1 带前脸是真坡面
                Assert.Equal(45_600.0, c.CapacityM3, 0);
            }
            foreach (var c in r.Cells.Where(x => x.StepIndex > 1))
            {
                Assert.False(c.IsWorkingFace);
                Assert.Equal(47_700.0, c.CapacityM3, 0);
            }
            Assert.Equal(852_300.0, r.TotalCapacityM3, 0);
        }

        /// <summary>
        /// 【平铺】所有位置合起来必须盖住整级台阶棱柱，不能带系统性缺口。
        ///
        /// 这条是回归闸门：早先每带都拿【真坡面】当前脸，而内核的后脸是竖直的
        /// （CarveStrip.cpp 里 cb/tb 共用同一 XY），于是相邻两带之间漏掉一个楔子
        /// ½·run·H —— 本算例每带少 10%，全场累计就是一笔说不清的库容，
        /// 而且"所有潜在排土位置"根本没盖住整级台阶。
        /// 现在第 2 带起前脸取竖直断面，缺口只剩两处极小的：
        /// 第 1 带前的坡面楔子 + 每带 ε 缝，合计 &lt; 1.5%。
        /// </summary>
        [Fact]
        public void Positions_TileTheBench_NoSystematicGap()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            Assert.True(r.Ok, r.Message);
            int steps = r.Cells.Select(c => c.StepIndex).Distinct().Count();
            // 整级台阶推 6 带的棱柱：走向 300m × (6×40)m × 12m
            double prism = StrikeTotal * (steps * 40.0) * BenchH;
            Assert.InRange(r.TotalCapacityM3 / prism, 0.985, 1.0);
        }

        /// <summary>
        /// 【平铺·几何】第 k 带的后界（前脸轨 + W）必须正好是第 k+1 带的前界 ——
        /// 相邻两带严丝合缝，不重叠也不留缝。库容对得上不够，几何也得对得上。
        /// </summary>
        [Fact]
        public void ConsecutiveStrips_AbutExactly()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 300,        // 不分幅，只看推进
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            var byStep = r.Cells.OrderBy(c => c.StepIndex).ToList();
            Assert.True(byStep.Count >= 3);
            for (int i = 0; i < byStep.Count; i++)
            {
                // 前脸轨(坡顶)落在 x = i×W；内核把后脸放在它 + W 处 = 下一带的前界
                double frontX = byStep[i].CrestXyz[0];
                Assert.Equal(i * 40.0, frontX, 6);
            }
        }

        /// <summary>编号能定位到"哪个排土场·第几级·第几幅·第几带"—— 落库的自然键就是它。</summary>
        [Fact]
        public void Cells_AreAddressableByCode()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench(level: 3) }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            }, "外排1");

            Assert.Contains(r.Cells, c => c.Code == "外排1-L3-P01-S01");
            Assert.Contains(r.Cells, c => c.Code == "外排1-L3-P03-S06");
            Assert.Equal(r.Cells.Count, r.Cells.Select(c => c.Code).Distinct().Count());   // 编号不撞
        }

        /// <summary>
        /// 分幅是【等分】不是"切满 L 再余一小截"：250m 按 L=100 分，是 3 幅 × 83.3m，
        /// 不是 100+100+50。末尾留个 50m 的零头没法派活，所以口径定成等分（D2）。
        /// </summary>
        [Fact]
        public void Panels_AreEqualLength_NotRemainderTail()
        {
            var b = StraightBench();
            b.CrestXyz = new[] { 0.0, 0.0, CrestZ, 0.0, 250.0, CrestZ };
            b.ToeXyz = new[] { FaceRun, 0.0, ToeZ, FaceRun, 250.0, ToeZ };

            var r = DumpStripPlanner.Plan(new[] { b }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 100, 350),   // 只出第 1 带，专看分幅
            });

            Assert.True(r.Ok, r.Message);
            Assert.All(r.Cells, c => Assert.Equal(3, c.PanelCount));
            Assert.All(r.Cells, c => Assert.Equal(250.0 / 3, c.StrikeLenM, 3));
        }

        /// <summary>
        /// 【张开的拐角要出圆弧，不是出尖】
        ///
        /// 等距偏移在朝偏移侧张开的拐角处，正解是一段【半径 = 偏移量的圆弧】，
        /// 每个偏移点离原顶点恒为 off。拿角平分线上那个交点去补的话，
        /// 点距顶点 off/cos(θ/2)：60° 的角就已经是 2 倍，限幅 2.5 只是把爆炸调慢。
        ///
        /// 【为什么必须钉住】真实数据推到第 50 带时 off=2000m，一个尖角把点甩到 5000m 外，
        /// 图上是横跨全区的扇面 —— 而逐个位置的水密/算量/带号连续全是绿的，数值验收抓不到，
        /// 是平面图看出来的。这条把它变成一跑就红。
        /// </summary>
        [Fact]
        public void OffsetRail_RoundsOpenCorners_NoMiterSpike()
        {
            // 【判据不能用"离原折线的距离"】miter 点对两条【边线】本来就恰好等距 ——
            // 那正是它叫"等距偏移"的原因。尖与弧的差别在【离原顶点多远】：
            // 弧恒为 off，miter 是 off/cos(θ/2)。这一条第一版就是这么写错的。
            //
            // 转角要够尖才触发圆角（限幅 2.5 → cos(θ/2) < 0.4 → 转角 > 133°）。
            // 取 150°：一条近乎对折的尖鼻子，正是真实数据上把点甩到 5000m 外的那种。
            //
            // 【坡底必须跟着线走，不能用固定方向偏置】固定 +X 偏置时，急弯两侧的法向
            // 一个保号一个翻号 —— 代码里"翻号不一致就退回 miter"的保护会生效，弧永远不出。
            // 那不是 bug，是判据不可靠时的正确退让；但用它做算例就验不到弧。
            // 坡底按【左侧】等距偏置，两段才同号。第一版就是这么写错的。
            const double deg = System.Math.PI / 180.0;
            const double turn = 150.0;                       // 段间转角（>133° 才触发圆角）
            double d2x = System.Math.Sin(turn * deg), d2y = System.Math.Cos(turn * deg);
            double ax = 200 * d2x, ay = 200 * d2y;
            var crest = new[] { 0.0, -200.0, CrestZ, 0.0, 0.0, CrestZ, ax, ay, CrestZ };

            // 左法向：第 1 段(+Y) → (−1,0)；第 2 段(d2) → (−d2y, d2x)
            const double g = 4.0;
            double n2x = -d2y, n2y = d2x;
            double bx = (-1 + n2x), by = (0 + n2y);
            double bl = System.Math.Sqrt(bx * bx + by * by);
            if (bl < 1e-9) { bx = n2x; by = n2y; bl = 1; }
            var toe = new[]
            {
                -g,                 -200.0,                 ToeZ,
                bx / bl * g,        by / bl * g,            ToeZ,
                ax + n2x * g,       ay + n2y * g,           ToeZ,
            };

            var dirs = DumpStripPlanner.AdvanceDirs(crest, toe, 200);
            const double off = 100.0;
            var moved = DumpStripPlanner.OffsetRail(crest, dirs, off);

            // 拐角处必须真的加了点（弧是采样出来的，不是一个点）
            Assert.True(moved.Length / 3 > crest.Length / 3,
                        $"偏移后点数 {moved.Length / 3} 没比原来的 {crest.Length / 3} 多 —— 弧没生成");

            // 每个偏移点离【最近的原顶点】都不该超过 off —— 这才是"没有尖"的定义。
            // 限幅 miter 在 150° 处会甩到 off/cos(75°) ≈ 3.9·off，被限幅压到 2.5·off = 250m。
            double worst = 0;
            for (int i = 0; i < moved.Length / 3; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < crest.Length / 3; j++)
                {
                    double dx = moved[i * 3] - crest[j * 3], dy = moved[i * 3 + 1] - crest[j * 3 + 1];
                    double d = System.Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) best = d;
                }
                if (best > worst) worst = best;
            }
            Assert.True(worst <= off * 1.02,
                        $"偏移点离最近的原顶点最远 {worst:0.#}m，超过了偏移量 {off:0}m —— 拐角出的是尖不是弧");
        }

        // ═══ ② 极性与标高 ═══════════════════════════════════════════

        /// <summary>
        /// D3 推进方向指【坡脚/外】：往后每带的质心比前一带往 +X 挪一个 W。
        /// 若极性反了（往坡顶里推，采场那套），质心会往 −X 走 —— 这条就是极性的照妖镜。
        ///
        /// 第 1→2 带的间隔不是整 W：第 1 带的前脸是真坡面（质心被坡面拉偏半个 run），
        /// 第 2 带起前脸是竖直格子界。这是口径差别，不是算错。
        /// </summary>
        [Fact]
        public void Advance_GoesOutwardTowardToe_OneStripPerStep()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 300,        // 不分幅，只看推进
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            var byStep = r.Cells.OrderBy(c => c.StepIndex).ToList();
            Assert.True(byStep.Count >= 3);
            for (int i = 2; i < byStep.Count; i++)
                Assert.Equal(40.0, byStep[i].Cx - byStep[i - 1].Cx, 6);   // 每带 +40m，方向 +X

            // 第 1 带质心 = 本带【平面多边形】的形心：前脸轨 x=0、后界轨 x=W=40 → 20。
            //
            // 【口径换过一次】原先是"坡顶 x=0 与坡底 x=4 的中点 2，再沿推进挪半个 W → 22"。
            // 那是把带当直的平行四边形算的，弯带上点的平均值根本不是面的形心 ——
            // 真实数据上拿内核建出来的体的体积质心去比，**183/1279（14%）偏出 W/4 以外、
            // 最差 45.96m**（比带宽还大），换成多边形形心后 **0 个、最差 2.34m**。
            //
            // 这条直算例上两者都只是近似（真值含坡面楔子是 20.98）：旧口径 22 差 1.02，
            // 新口径 20 差 0.98 —— 直带上打平，弯带上新口径完胜。
            Assert.Equal(20.0, byStep[0].Cx, 6);
            Assert.True(byStep[1].Cx > byStep[0].Cx, "推进方向必须指坡脚/外(+X)");
        }

        /// <summary>
        /// D4 同一级台阶各带【标高不变】——这是"排土比采场规整"的本义。
        /// 折点带进来的 Z 噪声（实测线歪几十厘米）必须被级标高压掉，
        /// 否则同一级各带高矮不一，库容就成了一笔糊涂账。
        /// </summary>
        [Fact]
        public void SameLevel_KeepsElevation_EvenWithNoisyVertexZ()
        {
            var b = StraightBench();
            b.CrestXyz = new[] { 0.0, 0.0, CrestZ + 0.7, 0.0, 150.0, CrestZ - 0.4, 0.0, StrikeTotal, CrestZ + 0.3 };
            b.ToeXyz = new[] { FaceRun, 0.0, ToeZ - 0.5, FaceRun, 150.0, ToeZ + 0.6, FaceRun, StrikeTotal, ToeZ };

            var r = DumpStripPlanner.Plan(new[] { b }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            Assert.True(r.Ok, r.Message);
            foreach (var c in r.Cells)
            {
                Assert.Equal(CrestZ, c.CrestZ, 9);
                Assert.Equal(ToeZ, c.ToeZ, 9);
                Assert.Equal(BenchH, c.BenchHeightM, 9);
                for (int i = 2; i < c.CrestXyz.Length; i += 3) Assert.Equal(CrestZ, c.CrestXyz[i], 9);
                for (int i = 2; i < c.ToeXyz.Length; i += 3) Assert.Equal(ToeZ, c.ToeXyz[i], 9);
            }
        }

        // ═══ ③ 对应关系（防 loft 拧麻花）═════════════════════════════

        /// <summary>圆环（正多边形），可指定方向与起始角 —— 真实排土台阶线就是这种同心环。</summary>
        private static double[] Ring(double r, double z, int n = 72, bool ccw = true, double startDeg = 0)
        {
            var xyz = new List<double>(n * 3);
            for (int i = 0; i < n; i++)
            {
                double a = (startDeg + (ccw ? 1 : -1) * 360.0 * i / n) * Math.PI / 180.0;
                xyz.Add(r * Math.Cos(a)); xyz.Add(r * Math.Sin(a)); xyz.Add(z);
            }
            return xyz.ToArray();
        }

        /// <summary>
        /// 坡底环【反向 + 起点错开 90°】时仍要配对正确。
        ///
        /// 这是唯一的几何坑：内核按归一化弧长把两条线配起来，它不管方向也不管起点
        /// （CarveStrip.cpp 的 lerpAt）。不对齐的话，坡顶的头会配上坡底的尾 ——
        /// loft 出来是横穿整个排土场的麻花体，而且网格照样闭合、体积照样算得出，
        /// 光看"建成了 N 个体"根本发现不了。
        ///
        /// 判据：对齐后逐点对应的间距应处处 ≈ 坡面投影 4m；没对齐时会到几百米量级。
        /// </summary>
        [Fact]
        public void ReversedAndRotatedToe_StillPairsPointToPoint()
        {
            var crest = Ring(200, CrestZ, ccw: true, startDeg: 0);
            var toe = Ring(204, ToeZ, ccw: false, startDeg: 90);      // 反向 + 起点错 90°

            var aligned = DumpStripPlanner.AlignToeToCrest(crest, toe);

            int n = crest.Length / 3;
            var gaps = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double dx = aligned[i * 3] - crest[i * 3], dy = aligned[i * 3 + 1] - crest[i * 3 + 1];
                gaps.Add(Math.Sqrt(dx * dx + dy * dy));
            }
            Assert.All(gaps, g => Assert.InRange(g, 3.0, 6.0));       // 处处贴着走 = 真配对
        }

        /// <summary>没对齐会是什么样 —— 反证：直接按原序配，间距能到几百米。判据本身得能分辨。</summary>
        [Fact]
        public void WithoutAlignment_PairingWouldBeGarbage_SanityCheck()
        {
            var crest = Ring(200, CrestZ, ccw: true, startDeg: 0);
            var toe = Ring(204, ToeZ, ccw: false, startDeg: 90);

            int n = crest.Length / 3;
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = toe[i * 3] - crest[i * 3], dy = toe[i * 3 + 1] - crest[i * 3 + 1];
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
            }
            Assert.True(worst > 100, $"未对齐时最大间距只有 {worst:0.#}m —— 算例区分度不够，判据说明不了问题");
        }

        /// <summary>
        /// 闭合环的【收尾段】必须进分幅：末点→首点那一截不在折点表里，
        /// 不补就整圈漏掉一段（点越少漏越多，而排土台阶线常是十几个折点的粗环）。
        /// 8 点方环，漏一段 = 少 1/8 的周长。
        /// </summary>
        [Fact]
        public void ClosedRing_IncludesWrapSegment()
        {
            var ring = Ring(100, CrestZ, n: 8);
            double openLen = DumpStripPlanner.PlanLength(ring);
            double closedLen = DumpStripPlanner.PlanLength(DumpStripPlanner.CloseIfRing(ring));

            double seg = closedLen / 8;
            Assert.Equal(closedLen - seg, openLen, 6);          // 补的正好是一段
            Assert.True(closedLen > openLen);
        }

        /// <summary>
        /// 闭合判据不能反过来把【真开口线】当成环补一段 —— 那会凭空接一条横穿全场的收尾段。
        /// 30 段的开口排土线，首末隔着整条 300m，跟 10m 的折点间距差一个多量级，分得很开。
        /// </summary>
        [Fact]
        public void GenuinelyOpenLine_IsNotClosedUp()
        {
            var pts = new List<double>();
            for (double y = 0; y <= 300; y += 10) { pts.Add(0); pts.Add(y); pts.Add(CrestZ); }
            var open = pts.ToArray();

            Assert.Same(open, DumpStripPlanner.CloseIfRing(open));                       // 原样返回 = 没补
            Assert.Equal(300.0, DumpStripPlanner.PlanLength(DumpStripPlanner.CloseIfRing(open)), 6);
        }

        // ═══ ③b 向外发展必须【沿着台阶线】═══════════════════════════

        /// <summary>
        /// 【等距】带拐角的台阶线外扩后，每一点到【原线】的距离必须仍等于外扩量 ——
        /// 这就是"按照台阶线向外发展"的量化形式。
        ///
        /// 早先是逐点沿各自法向平移：直线上没差别，一到拐角就错 ——
        /// 拐角处两条边的偏移线交在角平分线上、距原顶点 off/cos(θ/2)，不是 off。
        /// 照法向平移，拐角越推越"瘪"，推二十带（800m）出来的线已经完全不平行于原台阶线。
        /// 90° 直角处误差是 1/cos45° − 1 = 41%，一测就现。
        /// </summary>
        [Fact]
        public void OffsetRail_StaysEquidistant_AtCorners()
        {
            // 一条 L 形台阶线（90° 直角），沿 +Y 走再拐向 +X
            var rail = new[] { 0.0, 0.0, CrestZ, 0.0, 200.0, CrestZ, 200.0, 200.0, CrestZ };
            // 推进方向：外侧（这里取 +X / +Y 那一侧的外角）
            var toe = new[] { 4.0, 0.0, ToeZ, 4.0, 196.0, ToeZ, 200.0, 196.0, ToeZ };
            var dirs = DumpStripPlanner.AdvanceDirs(rail, toe);

            const double off = 100.0;
            var moved = DumpStripPlanner.OffsetRail(rail, dirs, off, out int folded);
            Assert.Equal(0, folded);

            // 逐点量到【原折线】的距离 —— 等距偏移下处处等于 off
            for (int i = 0; i < moved.Length / 3; i++)
            {
                double d = DistPointToPolylineXY(rail, moved[i * 3], moved[i * 3 + 1]);
                Assert.InRange(d, off - 0.5, off + 0.5);
            }
        }

        /// <summary>
        /// 【做全】凹弯太急时不能把整幅丢掉 —— 要把推进夹到几何允许的极限，出一条窄带把空间填上。
        ///
        /// 真实数据上栽过：129/228 个幅（57%）在第 1 带就折回、一个位置都不出，
        /// 排土场上大片空着没有位置，排产自然排不到那儿。内核对同一情形的处置是夹紧不是放弃
        /// （「急弯处的体变窄，但窄的体是真的，自交的体是假的」）。
        ///
        /// 算例：朝推进方向凹的 V 形，凹弯半径远小于 W —— 按 W 推必折回，夹紧后应出一条窄带。
        /// </summary>
        [Fact]
        public void SharpConcave_ClampsAdvance_InsteadOfDroppingWholePanel()
        {
            // 【算例要现实】不能用一个尖折点：那是无穷大曲率，等距偏移在那儿本来就自交，
            // 该丢不该夹（另有 OffsetRail_ReportsFold_* 钉那种情形）。
            // 用一条【光滑凹弧】：半径 50m，推进指向弧心。按 W=40 推，剩余半径只有 10m
            // （压缩到 0.2 < 0.30 的阈值）必被判折回；夹到 (50−d)/50 = 0.30 即 d≈35 时可行。
            const double R = 50.0;
            var cr = new List<double>();
            var to = new List<double>();
            for (int i = 0; i <= 40; i++)
            {
                double a = (-60.0 + 120.0 * i / 40) * Math.PI / 180.0;
                cr.Add(R * Math.Cos(a)); cr.Add(R * Math.Sin(a)); cr.Add(CrestZ);
                to.Add((R - 4) * Math.Cos(a)); to.Add((R - 4) * Math.Sin(a)); to.Add(ToeZ);
            }
            var crest = cr.ToArray();
            var toe = to.ToArray();

            var r = DumpStripPlanner.Plan(new[]
            {
                new DumpStripPlanner.BenchInput
                {
                    LevelIndex = 1, CrestZ = CrestZ, ToeZ = ToeZ,
                    CrestXyz = crest, ToeXyz = toe,
                    CrestClosed = false, ToeClosed = false,
                }
            }, new DumpStripPlanner.Options
            {
                PanelLengthM = 500,        // 不分幅
                StripWidthM = 40,          // 远大于这个 V 形的凹弯半径
                MaxSteps = 5,
                BoundaryRingXy = Rect(-400, -400, 400, 400),
            });

            // 关键：必须出位置，不能因为折回就颗粒无收
            Assert.True(r.Ok, r.Message);
            Assert.NotEmpty(r.Cells);

            var c0 = r.Cells[0];
            // 夹窄的带要如实报窄宽度，不能拿名义 W 冒充
            Assert.True(c0.IsClamped, "这个 V 形按 W=40 推必折回，本该被夹窄");
            Assert.InRange(c0.StripWidthM, 0.25 * 40, 40.0);
            // 容量跟着有效宽度走，不是按名义 W 算
            Assert.InRange(c0.CapacityM3 / (c0.PlanAreaM2 * c0.BenchHeightM), 0.5, 1.0);
            // 记账要说清这是"夹窄后停"，不是"丢弃"
            Assert.Contains(r.Drops, d => d.Reason.Contains("夹窄"));
        }

        /// <summary>凹弯半径小于推进距离时偏移线会翻到另一侧（自交）——必须报出来，不能默默出假几何。</summary>
        [Fact]
        public void OffsetRail_ReportsFold_WhenConcaveRadiusSmallerThanAdvance()
        {
            // 朝推进方向凹的 V 形：外扩超过拐点到两翼的距离就会折回
            var rail = new[] { 0.0, 0.0, CrestZ, 30.0, 40.0, CrestZ, 60.0, 0.0, CrestZ };
            var toe = new[] { 0.0, -4.0, ToeZ, 30.0, 36.0, ToeZ, 60.0, -4.0, ToeZ };
            var dirs = DumpStripPlanner.AdvanceDirs(rail, toe);

            DumpStripPlanner.OffsetRail(rail, dirs, 5.0, out int small);
            DumpStripPlanner.OffsetRail(rail, dirs, 400.0, out int big);
            Assert.Equal(0, small);
            Assert.True(big > 0, "推进 400m 远超这条 V 形的凹弯半径，必须报折回");
        }

        private static double DistPointToPolylineXY(double[] poly, double px, double py)
        {
            double best = double.MaxValue;
            for (int i = 1; i < poly.Length / 3; i++)
            {
                double ax = poly[(i - 1) * 3], ay = poly[(i - 1) * 3 + 1];
                double bx = poly[i * 3], by = poly[i * 3 + 1];
                double vx = bx - ax, vy = by - ay;
                double l2 = vx * vx + vy * vy;
                double t = l2 > 1e-18 ? ((px - ax) * vx + (py - ay) * vy) / l2 : 0;
                t = Math.Max(0, Math.Min(1, t));
                double qx = ax + t * vx, qy = ay + t * vy;
                best = Math.Min(best, Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy)));
            }
            return best;
        }

        // ═══ ③c 按区域裁线 ═════════════════════════════════════════

        /// <summary>
        /// 一条横跨区域边界的线要被【裁开】，只留区域内那一段 —— 而不是拿质心做整条取舍。
        /// 质心口径下这条线（质心在 x=150，区域只到 100）会被整条丢掉，连区域内那 100m 也没了。
        /// </summary>
        [Fact]
        public void ClipToRing_TrimsLineAtBoundary_NotWholeLineVerdict()
        {
            var line = new[] { 0.0, 50.0, CrestZ, 300.0, 50.0, CrestZ };     // 沿 +X 从 0 到 300
            var ring = Rect(0, 0, 100, 100);                                  // 区域只到 x=100

            var segs = DumpStripPlanner.ClipToRing(line, closed: false, ring);

            Assert.Single(segs);
            Assert.False(segs[0].Closed);
            Assert.Equal(100.0, DumpStripPlanner.PlanLength(segs[0].Xyz), 3);  // 只剩区域内那 100m
            Assert.Equal(CrestZ, segs[0].Xyz[2], 6);                           // Z 在交点处正确插值
        }

        /// <summary>穿进穿出会裁成【多段】，各自独立成线 —— 中间在区域外的那截不能被架桥连过去。</summary>
        [Fact]
        public void ClipToRing_SplitsIntoMultiplePieces()
        {
            // 沿 +X 穿过两个区域块之间的空隙：区域是 [0,100]，线在 x=-50..50 和 150..250 各有一段
            var line = new[] { -50.0, 50.0, CrestZ, 50.0, 50.0, CrestZ, 50.0, 150.0, CrestZ,
                               -50.0, 150.0, CrestZ };
            var ring = Rect(0, 0, 100, 100);

            var segs = DumpStripPlanner.ClipToRing(line, closed: false, ring);

            Assert.True(segs.Count >= 1);
            Assert.All(segs, s => Assert.All(Enumerable.Range(0, s.Xyz.Length / 3), i =>
                Assert.True(DumpStripPlanner.PointInRingXY(s.Xyz[i * 3], s.Xyz[i * 3 + 1], ring)
                            || OnRingEdge(s.Xyz[i * 3], s.Xyz[i * 3 + 1], ring),
                            "裁出来的点必须在区域内或恰在边界上")));
        }

        /// <summary>整条都在区域内 → 原样返回，闭合标志必须保住（否则后面补收尾段会凭空接一条弦）。</summary>
        [Fact]
        public void ClipToRing_KeepsClosedFlag_WhenFullyInside()
        {
            var ring0 = Ring(30, CrestZ, n: 16);
            var region = Rect(-200, -200, 200, 200);

            var segs = DumpStripPlanner.ClipToRing(ring0, closed: true, region);

            Assert.Single(segs);
            Assert.True(segs[0].Closed);
            Assert.Equal(ring0.Length, segs[0].Xyz.Length);
        }

        /// <summary>被区域切掉一截的闭合环【不再是环】—— 闭合标志必须翻掉。</summary>
        [Fact]
        public void ClipToRing_ClearsClosedFlag_WhenRingIsCut()
        {
            var ring0 = Ring(100, CrestZ, n: 32);
            var region = Rect(-200, -200, 200, 0);      // 只留下半个环

            var segs = DumpStripPlanner.ClipToRing(ring0, closed: true, region);

            Assert.NotEmpty(segs);
            Assert.All(segs, s => Assert.False(s.Closed));
        }

        private static bool OnRingEdge(double x, double y, double[] ring)
        {
            for (int i = 0, j = ring.Length / 2 - 1; i < ring.Length / 2; j = i++)
            {
                double ax = ring[j * 2], ay = ring[j * 2 + 1], bx = ring[i * 2], by = ring[i * 2 + 1];
                double vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy;
                double t = l2 > 1e-18 ? ((x - ax) * vx + (y - ay) * vy) / l2 : 0;
                t = Math.Max(0, Math.Min(1, t));
                double qx = ax + t * vx, qy = ay + t * vy;
                if ((x - qx) * (x - qx) + (y - qy) * (y - qy) < 1e-6) return true;
            }
            return false;
        }

        /// <summary>
        /// 【分幅必须局部成对】坡顶线与坡底线长度/形状差很多时，按弧长比例切幅会把
        /// "第 k 幅的坡顶"配到几公里外的坡底上 —— loft 出来是横跨全图的三角扇面。
        ///
        /// 实测真实数据：走向长只有 46m 的位置，XY 跨度却是 2886 × 3456m。
        /// 这里用一条【短坡顶 + 长得多的绕行坡底】把那个情形复现出来：
        /// 坡底在坡顶正下方 4m 处有一段，但整条坡底还往远处绕了 2km。
        /// 按弧长切会配到远处那段；逐点投影必须配到近处那段。
        /// </summary>
        [Fact]
        public void Panels_PairLocally_EvenWhenToeIsMuchLonger()
        {
            // 坡顶：x=0，y 0→300
            var crest = new[] { 0.0, 0.0, CrestZ, 0.0, 300.0, CrestZ };
            // 坡底：x=4 与坡顶并排走完，然后甩出去 2km 再回来（模拟带绕过尖灭区/远处地形）
            var toe = new[]
            {
                FaceRun, 0.0, ToeZ,
                FaceRun, 300.0, ToeZ,
                2000.0, 1500.0, ToeZ,
                2000.0, 2500.0, ToeZ,
            };

            var r = DumpStripPlanner.Plan(new[]
            {
                new DumpStripPlanner.BenchInput
                {
                    LevelIndex = 1, CrestZ = CrestZ, ToeZ = ToeZ,
                    CrestXyz = crest, ToeXyz = toe,
                    CrestClosed = false, ToeClosed = false,
                }
            }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            Assert.True(r.Ok, r.Message);
            Assert.NotEmpty(r.Cells);
            // 每个位置的坡面投影都该 ≈ 4m（近处那段），不是几百上千米
            Assert.All(r.Cells, c => Assert.InRange(c.FaceRunM, 0.0, 30.0));
            // 而且整个位置的 XY 跨度不该超出坡顶线本身的量级
            foreach (var c in r.Cells)
            {
                var xs = Enumerable.Range(0, c.CrestXyz.Length / 3).Select(i => c.CrestXyz[i * 3])
                         .Concat(Enumerable.Range(0, c.ToeXyz.Length / 3).Select(i => c.ToeXyz[i * 3])).ToList();
                Assert.True(xs.Max() - xs.Min() < 200,
                            $"位置 {c.Code} 的 X 跨度 {xs.Max() - xs.Min():0} m —— 坡顶/坡底没配上");
            }
        }

        /// <summary>坡顶/坡底实在配不上（投影也救不回来）的幅要丢并记账，不能出体。</summary>
        [Fact]
        public void WildFaceRun_IsDroppedWithAccounting()
        {
            // 坡底整条都在 500m 开外 —— 投影取最近点也有 500m，远超 4×台阶高
            var crest = new[] { 0.0, 0.0, CrestZ, 0.0, 300.0, CrestZ };
            var toe = new[] { 500.0, 0.0, ToeZ, 500.0, 300.0, ToeZ };

            var r = DumpStripPlanner.Plan(new[]
            {
                new DumpStripPlanner.BenchInput
                {
                    LevelIndex = 1, CrestZ = CrestZ, ToeZ = ToeZ,
                    CrestXyz = crest, ToeXyz = toe,
                    CrestClosed = false, ToeClosed = false,
                }
            }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-600, -50, 600, 350),
            });

            Assert.Empty(r.Cells);
            Assert.Contains(r.Drops, d => d.Reason.Contains("坡面投影超上限"));
        }

        /// <summary>
        /// 【算量口径】容量必须按本带【自己的】轨长算，不能用未偏移那条轨的走向长。
        ///
        /// 等距外扩会改变轨长：凸角外圈越推越长。拿 L 形台阶线推 5 带，
        /// 外圈那几带的轨长必然大于第 1 带 —— 容量得跟着涨，否则真体积是清单的好几倍。
        /// 实测真实数据栽过：第 5 带轨长从 57m 拉到 396m，比值 7.46。
        /// </summary>
        [Fact]
        public void Capacity_ScalesWithOwnRailLength_NotOriginalStrike()
        {
            // L 形台阶线：沿 +Y 走 200m 再拐向 +X 200m。
            // 坡底必须放在【凸侧】(-X / +Y)，推进方向才指向凸角、外扩把轨拉长。
            // 放在凹侧的话推进指向凹角，偏移立刻折回 —— 折回检测会正确地停在第 1 带，
            // 那是对的行为，但验不了本条要验的东西（算例的锅，不是算法的）。
            var crest = new[] { 0.0, 0.0, CrestZ, 0.0, 200.0, CrestZ, 200.0, 200.0, CrestZ };
            var toe = new[] { -4.0, 0.0, ToeZ, -4.0, 204.0, ToeZ, 200.0, 204.0, ToeZ };

            var r = DumpStripPlanner.Plan(new[]
            {
                new DumpStripPlanner.BenchInput
                {
                    LevelIndex = 1, CrestZ = CrestZ, ToeZ = ToeZ,
                    CrestXyz = crest, ToeXyz = toe,
                    CrestClosed = false, ToeClosed = false,
                }
            }, new DumpStripPlanner.Options
            {
                PanelLengthM = 500,       // 不分幅，整条一幅，专看推进
                StripWidthM = 40,
                MaxSteps = 4,
                BoundaryRingXy = Rect(-400, -400, 600, 600),
            });

            Assert.True(r.Ok, r.Message);

            // 【按带汇总，不逐格比】外扩把带拉长之后，超过分割长度 L 的带会在带内再切
            // （一个位置沿走向始终 ≤ L）。所以"轨越推越长"这件事要在【带】上看：
            // 逐格比会读到子格的长度，越切越短，把对的行为判成红的。
            var byStep = r.Cells.GroupBy(c => c.StepIndex).OrderBy(g => g.Key)
                          .Select(g => (Step: g.Key,
                                        Strike: g.Sum(c => c.StrikeLenM),
                                        Cap: g.Sum(c => c.CapacityM3),
                                        Subs: g.Count()))
                          .ToList();
            Assert.True(byStep.Count >= 3);

            // 外扩后轨变长 → 走向长与容量都必须跟着涨
            for (int i = 1; i < byStep.Count; i++)
            {
                Assert.True(byStep[i].Strike > byStep[i - 1].Strike,
                            $"第 {i + 1} 带轨长 {byStep[i].Strike:0.#} 应大于第 {i} 带 {byStep[i - 1].Strike:0.#}（凸角外扩）");
                Assert.True(byStep[i].Cap > byStep[i - 1].Cap,
                            "容量必须跟着轨长涨 —— 否则真体积会是清单的好几倍");
            }

            // 顺带钉住带内再切：没有哪个位置沿走向超过 L
            foreach (var c in r.Cells)
                Assert.True(c.StrikeLenM <= 500 * 1.02,
                            $"位置 {c.Code} 走向 {c.StrikeLenM:0.#}m 超过了分割长度 500m");

            // 容量 = 平面面积 × 台阶高（第 1 带另扣坡前的楔子）。
            // 平面面积用【前后两条轨的平均长 × W】—— 弯道上条带是环形扇区，前后轨不等长。
            // 【第 1 带与其后各带口径本就不同】第 1 带前脸是真坡面(扣掉 run/2)，
            // 第 2 带起是竖直格子界(只扣 ε/2)，所以只在第 2 带起之间要求一致。
            var unit = r.Cells.GroupBy(c => c.StepIndex).OrderBy(g => g.Key)
                        .Select(g => g.First())
                        .Select(c => c.CapacityM3 / (c.PlanAreaM2 * c.BenchHeightM)).ToList();
            for (int i = 2; i < unit.Count; i++)
                Assert.Equal(unit[1], unit[i], 3);
            Assert.True(unit[0] < unit[1],
                        $"第 1 带单位容量 {unit[0]:0.###} 应小于其后各带 {unit[1]:0.###}（真坡面要扣掉坡前的楔子）");

            // 平面面积必须落在【前脸轨长 × W】与【后界轨长 × W】之间 —— 它是两者的平均
            Assert.All(r.Cells, c => Assert.True(c.PlanAreaM2 >= c.StrikeLenM * c.StripWidthM - 1e-6,
                        "凸角外扩后界更长，平面面积不该小于 前脸轨长 × W"));
        }

        // ═══ ④ 边界与记账 ═══════════════════════════════════════════

        /// <summary>不给边界就只出第 1 带 —— "所有潜在位置"要有边界才谈得上，无限外推是造数。</summary>
        [Fact]
        public void NoBoundary_EmitsOnlyCurrentStrip_AndSaysSo()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = null,
            });

            Assert.True(r.Ok, r.Message);
            Assert.Equal(3, r.Cells.Count);                      // 3 幅 × 1 带
            Assert.All(r.Cells, c => Assert.Equal(1, c.StepIndex));
            Assert.Contains(r.Notes, s => s.Contains("边界"));
        }

        /// <summary>
        /// 【急弯断点不许拿"第 N 带会折回"去否决第 1 带】
        ///
        /// 断点逻辑把"在探测距离处会折回"的段【整段排除】，排除的地方一个位置都不出。
        /// 探测距离一度接在【推进带数】上（推 8 带就按 8×W 探）—— 于是一段推得动一带的台阶线，
        /// 因为第 5 带会折回而被整段删掉。真实数据上这一条**吃掉了 43% 的台阶线走向长**
        /// （覆盖 57.3% → 80.3%，位置 1995 → 2264）。
        ///
        /// 【为什么当时没被抓到】被删的段**不进任何丢弃计数** —— 丢弃清单看着完全正常，
        /// 逐格的水密/方量/带号连续也全绿（剩下的位置个个都对）。
        /// 是"沿台阶线逐 5m 问这里有没有位置"这张一维覆盖图照出来的。见 [[green-audit-blind-spots]]。
        ///
        /// 算例：600m 直段 + 右转 600m 直段，逐 120m 打折点。
        ///   偏 40m(=1W)：转角处两段各缩到 80m —— 推得动，一段都不该删。
        ///   偏 200m(=5W)：转角两段直接翻向 —— 连同左右余量共 4/10 段被删（480m）。
        /// 两档都判：默认档必须做全，深探档必须【把删掉的量报出来】。
        /// </summary>
        [Fact]
        public void SplitProbe_JudgesTheFirstBandOnly_NotSomeFutureBand()
        {
            const double segLen = 120, legLen = 600, g = 4;   // g = 坡面投影
            var cr = new List<double>();
            var to = new List<double>();
            void P(double x, double y, double tx, double ty)
            { cr.Add(x); cr.Add(y); cr.Add(CrestZ); to.Add(tx); to.Add(ty); to.Add(ToeZ); }

            // 第 1 段沿 +Y，坡底在右手边(+X)；转角后沿 +X，坡底在右手边(−Y)。
            // 右转 = 凹侧在右 —— 往坡底方向偏移会缩短，正是"急弯"该有的样子。
            for (double y = 0; y < legLen; y += segLen) P(0, y, g, y);
            P(0, legLen, g, legLen - g);                                  // 转角，坡底走内角
            for (double x = segLen; x <= legLen; x += segLen) P(x, legLen, x, legLen - g);

            var bench = new DumpStripPlanner.BenchInput
            {
                LevelIndex = 1,
                CrestZ = CrestZ,
                ToeZ = ToeZ,
                CrestXyz = cr.ToArray(),
                ToeXyz = to.ToArray(),
            };

            static double Len(double[] xyz)
            {
                double s = 0;
                for (int i = 3; i < xyz.Length; i += 3)
                    s += Math.Sqrt(Math.Pow(xyz[i] - xyz[i - 3], 2) + Math.Pow(xyz[i + 1] - xyz[i - 2], 2));
                return s;
            }

            DumpStripPlanner.Options Opt(int probe) => new()
            {
                PanelLengthM = segLen,
                StripWidthM = 40,
                MaxSteps = 8,
                SplitProbeSteps = probe,
                BoundaryRingXy = Rect(-60, -60, 1000, 700),
            };

            double crestLen = Len(bench.CrestXyz);
            Assert.True(crestLen > 1100, $"算例自身不对：台阶线只有 {crestLen:0} m");

            // ── 默认档（探 1 带）：第 1 带必须几乎盖满整条线
            var def = DumpStripPlanner.Plan(new[] { bench }, Opt(1), "排土场");
            Assert.True(def.Ok, def.Message);
            double covDef = def.Cells.Where(c => c.StepIndex == 1).Sum(c => Len(c.CrestXyz));
            Assert.True(covDef > crestLen * 0.92,
                        $"探 1 带时第 1 带只盖了 {covDef:0}/{crestLen:0} m —— "
                        + "推得动的台阶线被断点挖掉了，现场看到的就是虚线");

            // ── 深探档（探 5 带）：**覆盖必须一样**
            //
            // 这一条比原来那版强：原来只要求"挖掉多少得报出来"，默认承认探深了就会挖掉一截。
            // 后来急弯段改成照常成幅（Stub，只是排在主推进面后面推），
            // 无声丢地这条路【整个没有了】—— 于是探测距离深浅不再影响覆盖，只影响哪些段走 Stub。
            //
            // 所以判据也升级成更强的那一条：**探测距离不许改变覆盖**。
            // 它一红就说明又有段被整段扔掉了（当初 43% 的损失就是这么来的，而丢弃清单看着完全正常）。
            var deep = DumpStripPlanner.Plan(new[] { bench }, Opt(5), "排土场");
            Assert.True(deep.Ok, deep.Message);
            double covDeep = deep.Cells.Where(c => c.StepIndex == 1).Sum(c => Len(c.CrestXyz));
            Assert.True(covDeep > covDef - 1e-6,
                        $"探 5 带覆盖 {covDeep:0} m < 探 1 带的 {covDef:0} m —— "
                        + "又有台阶线被断点整段扔掉了；急弯段应当照常成幅（Stub）而不是消失");

            // 探深了必然把更多段判成急弯 —— 这些段现在走 Stub，但【必须报出来】，
            // 否则"覆盖没变"会掩盖住"一大截只能出窄带"这件事。
            Assert.Contains(deep.Notes, s => s.Contains("急弯"));
        }

        /// <summary>
        /// 丢弃记账里的"少了多少米"要是【本段】的长度，不是整条台阶线的。
        ///
        /// 原来短幅那条加的是 crestLen（整条线）—— 一条线被断成 N 段就重复计 N 次，
        /// 真实数据上 19,000 m 的台阶线报出 **229,789 m** 的损失。
        /// 判据报离谱大数时先怀疑判据，见 [[criteria-before-geometry]]。
        /// </summary>
        [Fact]
        public void ReportedStrikeLoss_NeverExceedsTheBenchLinesThemselves()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                MinStrikeLenM = 500,          // 比整条线还长 → 每一幅都判短，把记账逼到极端
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            }, "排土场");

            double reported = r.Drops.Sum(d => d.LostStrikeLenM);
            Assert.True(reported <= StrikeTotal + 1e-6,
                        $"台阶线总共才 {StrikeTotal:0} m，却报出丢了 {reported:0} m");
        }

        /// <summary>
        /// 【逐段折回标记必须由 OffsetRail 回填，不许调用方自己拿下标去减】
        ///
        /// 圆角接头会在拐角处插点 —— 偏移轨比原轨长，`moved[i]` 不再是原轨第 i 个顶点。
        /// `SplitAtFoldPoints` 原先就是自己减的下标，于是标坏的段与真正折回的段【错位】。
        ///
        /// 实测这条 150° 拐角的轨（13 点插成 24 点）：真正折回 **0 段**，
        /// 旧写法标出 **8/12 段**。8 > 12×0.5 会触发"坏段过半就整条不断"那道保护 ——
        /// 于是该断的地方不断；ratio 稍有不同就会反过来把大半条线挖掉。
        /// **两种错法都不报错、不崩，只是安静地做错。**
        ///
        /// 真实数据没踩到（5m 重采样后转角都不到 133°，不生成圆弧），
        /// 但图上手画的排土台阶线带个尖鼻子就会踩 —— 而那正是"急弯断开"要伺候的线。
        /// </summary>
        [Fact]
        public void FoldFlags_ComeFromOffsetRail_NotFromHandIndexingIntoTheArcJoinedResult()
        {
            const double deg = Math.PI / 180.0, turn = 150.0;
            var pts = new List<double>();
            void P(double x, double y) { pts.Add(x); pts.Add(y); pts.Add(CrestZ); }

            for (double y = 0; y <= 200; y += 50) P(0, y);
            double dx = -Math.Sin(turn * deg), dy = Math.Cos(turn * deg);
            for (int i = 1; i <= 4; i++) P(dx * 50 * i, 200 + dy * 50 * i);
            double ex = dx * 200, ey = 200 + dy * 200;
            for (int i = 1; i <= 4; i++) P(ex + 50 * i, ey);

            var crest = pts.ToArray();
            int n = crest.Length / 3, m = n - 1;

            // 坡底按右手侧偏 4m（推进指右）
            var toe = (double[])crest.Clone();
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
                double vx = crest[b * 3] - crest[a * 3], vy = crest[b * 3 + 1] - crest[a * 3 + 1];
                double L = Math.Sqrt(vx * vx + vy * vy);
                if (L < 1e-9) L = 1;
                toe[i * 3] = crest[i * 3] + vy / L * 4;
                toe[i * 3 + 1] = crest[i * 3 + 1] - vx / L * 4;
                toe[i * 3 + 2] = ToeZ;
            }

            var dirs = DumpStripPlanner.AdvanceDirs(crest, toe, 40);
            var bad = new bool[m];
            var moved = DumpStripPlanner.OffsetRail(crest, dirs, 40, out int folded, bad);

            // 算例得真的插了弧点，否则这条判据是空过的
            Assert.True(moved.Length / 3 > n,
                        $"偏移轨 {moved.Length / 3} 点没比原轨 {n} 点多 —— 没生成圆弧，验不到错位");
            Assert.Equal(folded, bad.Count(x => x));

            // 旧写法：拿返回值的下标当原轨下标用
            int handBad = 0;
            for (int i = 0; i < m; i++)
            {
                if ((i + 1) * 3 + 1 >= moved.Length) continue;
                double ox = crest[(i + 1) * 3] - crest[i * 3], oy = crest[(i + 1) * 3 + 1] - crest[i * 3 + 1];
                double qx = moved[(i + 1) * 3] - moved[i * 3], qy = moved[(i + 1) * 3 + 1] - moved[i * 3 + 1];
                double la = Math.Sqrt(ox * ox + oy * oy), lb = Math.Sqrt(qx * qx + qy * qy);
                if (ox * qx + oy * qy < 0 || (la > 1e-9 && lb < la * 0.30)) handBad++;
            }
            Assert.True(handBad > folded,
                        "算例没能让两种写法分开 —— 这条判据证明不了什么，换更尖的拐角");

            // 断点也必须跟着正确的那一份走：这条轨推得动，不该被挖掉任何一截
            var runs = DumpStripPlanner.SplitAtFoldPoints(crest, toe, 40, 8);
            Assert.Equal(1, runs.Count);
            Assert.True(runs[0].T1 - runs[0].T0 > 0.999,
                        $"整条推得动却只留下 {(runs[0].T1 - runs[0].T0) * 100:0.#}% —— 断点用的是错位的标记");
        }

        /// <summary>
        /// 【同级的短头不许把主推进面的地先占了】
        ///
        /// 占位栅格逐【级】共享，而推进逐【壳子】跑 —— 先跑的那条把地占完，后跑的可能一个位置都出不来。
        /// 壳子的先后原先取【图上顺序】，于是完全由画图的人决定谁被饿死。
        ///
        /// 真实数据实测：L5 的 4 条壳子里，图上顺序把 **182m 的短头**排在 **1613m 的主线**前面，
        /// 两者只隔 340m。主线单独跑出 108 个位置，混在一起整个 L5 只出 **107** 个，
        /// 主线自己的走向覆盖从 71% 掉到 45%。改成【按线长降序】后 L5 107→115，
        /// **其余 8 个级一个位置都没动** —— 影响精确落在该落的地方。
        ///
        /// 算例：短头 120m 列在前，主线 600m 列在后，平行且相距 200m（短头推 8 带 = 320m 会扫过主线）。
        /// 主线必须拿满自己的 5 个幅 —— 按图上顺序时它一个都拿不到。
        /// </summary>
        [Fact]
        public void LongestShellAdvancesFirst_SoAShortStubCannotStarveTheMainFront()
        {
            const double stubLen = 120, mainLen = 600, mainX = 200, g = 4;

            static DumpStripPlanner.BenchInput Shell(double x, double len)
                => new()
                {
                    LevelIndex = 1,
                    CrestZ = CrestZ,
                    ToeZ = ToeZ,
                    CrestXyz = new[] { x, 0.0, CrestZ, x, len, CrestZ },
                    ToeXyz = new[] { x + g, 0.0, ToeZ, x + g, len, ToeZ },
                };

            // 短头【列在前】—— 这正是真实数据里的情形，也是原先决定先后的那个顺序
            var benches = new[] { Shell(0, stubLen), Shell(mainX, mainLen) };

            var r = DumpStripPlanner.Plan(benches, new DumpStripPlanner.Options
            {
                PanelLengthM = 120,
                StripWidthM = 40,
                MaxSteps = 8,
                BoundaryRingXy = Rect(-60, -60, 900, 700),
            }, "排土场");

            Assert.True(r.Ok, r.Message);

            // 第 1 带的前脸还在各自的坡顶线上，按 x 认得出是谁的
            var mainBand1 = r.Cells
                .Where(c => c.StepIndex == 1 && Math.Abs(c.CrestXyz[0] - mainX) < 1.0)
                .ToList();

            Assert.Equal((int)(mainLen / 120), mainBand1.Count);          // 主线 5 个幅，一个都不能少
            Assert.True(mainBand1.Sum(c => c.StrikeLenM) > mainLen * 0.95,
                        $"主线第 1 带只盖了 {mainBand1.Sum(c => c.StrikeLenM):0}/{mainLen:0} m "
                        + "—— 短头把主推进面的地先占了");

            // 反过来短头该被拦住（否则算例没验到"确实会抢地"这件事）
            var stubCells = r.Cells.Where(c => c.CrestXyz[0] < mainX - 1.0).ToList();
            Assert.NotEmpty(stubCells);
            Assert.True(stubCells.Max(c => c.StepIndex) < 8,
                        "短头一路推到底都没撞上主线 —— 算例没构造出抢地，这条判据是空过的");
        }

        /// <summary>推到边界停下来要【记账】，不静默截断 —— 少了多少、为什么少，必须说得清。</summary>
        [Fact]
        public void BoundaryTruncation_IsRecorded()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            Assert.True(r.Ok, r.Message);
            Assert.Contains(r.Drops, d => d.Reason.Contains("边界"));
        }

        /// <summary>
        /// W 填小一个量级（4 而不是 40）会推出几百带 —— 上限把它挡在这里并记账，
        /// 而不是挡在显存里。这类"参数量级填错"在现场是常事。
        /// </summary>
        [Fact]
        public void TinyStripWidth_HitsStepCap_AndIsRecorded()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 300,
                StripWidthM = 4,                                 // 少填一个 0
                MaxSteps = 20,
                BoundaryRingXy = Rect(-50, -50, 2000, 350),
            });

            Assert.True(r.Ok, r.Message);
            Assert.Equal(20, r.Cells.Count);
            Assert.Contains(r.Drops, d => d.Reason.Contains("触顶"));
        }

        /// <summary>
        /// 【位置编号必须唯一 —— 同一级有多条台阶壳子时也是】
        ///
        /// 一级台阶常常有好几条壳子：从现状面切出来的带被区域边界或坡面角闸门打断，
        /// 就成了同级的两条独立线。先前幅号是【逐条壳子】从 1 数的，于是同一级里
        /// 出现两个 P01 —— 真实数据上 1018 个位置里 60 个撞号、跨 11 个级。
        ///
        /// 【为什么这是硬伤不是小瑕疵】编号是位置的身份：dump_strip 靠
        /// (区,方案,级,幅,带,子号) 做唯一索引、排产按它取用、图上按它挂属性。
        /// 撞号的两个位置在下游就是同一个东西 —— 落库时被唯一索引直接挡掉，
        /// 连同库容一起消失，而清单和图上都还在。
        /// </summary>
        [Fact]
        public void PositionCodes_AreUnique_EvenWhenOneLevelHasSeveralBenchShells()
        {
            // 同一级的两条壳子：一条在 y∈[0,300]，另一条挪到 y∈[500,800]（互不相干）
            var a = StraightBench(level: 7);
            var b = new DumpStripPlanner.BenchInput
            {
                LevelIndex = 7, CrestZ = CrestZ, ToeZ = ToeZ,
                CrestXyz = new[] { 0.0, 500.0, CrestZ, 0.0, 800.0, CrestZ },
                ToeXyz = new[] { FaceRun, 500.0, ToeZ, FaceRun, 800.0, ToeZ },
            };

            var r = DumpStripPlanner.Plan(new[] { a, b }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 900),
            });

            Assert.True(r.Ok, r.Message);
            Assert.True(r.Cells.Count >= 12, $"两条壳子都该出位置，实际 {r.Cells.Count} 个");

            var codes = r.Cells.Select(c => c.Code).ToList();
            var dupCode = codes.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupCode.Count == 0,
                        "位置编号撞号：" + string.Join(", ", dupCode.Take(5)));

            // 落库的唯一索引就是这四元组，算法层先自证一遍
            var dupKey = r.Cells.GroupBy(c => (c.LevelIndex, c.PanelIndex, c.StepIndex, c.SubIndex))
                          .Where(g => g.Count() > 1).ToList();
            Assert.True(dupKey.Count == 0,
                        "(级,幅,带,子号) 撞号 —— dump_strip 的唯一索引会把这些行挡掉："
                        + string.Join(", ", dupKey.Take(3).Select(g => g.Key.ToString())));

            // 两条壳子的幅号必须接着数，不是各自从 1 开始
            var panels = r.Cells.Select(c => c.PanelIndex).Distinct().OrderBy(x => x).ToList();
            Assert.True(panels.Count >= 6, $"两条壳子共该有 ≥6 幅，实际 {panels.Count} 幅");
            Assert.Equal(Enumerable.Range(1, panels.Count).ToList(), panels);
        }

        /// <summary>台阶高为 0（两条线同标高，根本不是一对坡顶/坡底）→ 不出体，且说得清。</summary>
        [Fact]
        public void ZeroBenchHeight_IsDroppedWithReason()
        {
            var b = StraightBench();
            b.ToeZ = CrestZ;

            var r = DumpStripPlanner.Plan(new[] { b }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            Assert.False(r.Ok);
            Assert.Empty(r.Cells);
            Assert.Contains(r.Drops, d => d.Reason.Contains("退化"));
        }

        /// <summary>多级台阶各切各的，级号不串 —— 排土场逐级往上堆，级是调度的第一维。</summary>
        [Fact]
        public void MultipleLevels_AreKeptSeparate()
        {
            var lv1 = StraightBench(level: 1);
            var lv2 = new DumpStripPlanner.BenchInput
            {
                LevelIndex = 2,
                CrestZ = CrestZ - BenchH,
                ToeZ = ToeZ - BenchH,
                CrestXyz = new[] { FaceRun, 0.0, CrestZ - BenchH, FaceRun, StrikeTotal, CrestZ - BenchH },
                ToeXyz = new[] { FaceRun * 2, 0.0, ToeZ - BenchH, FaceRun * 2, StrikeTotal, ToeZ - BenchH },
            };

            var r = DumpStripPlanner.Plan(new[] { lv1, lv2 }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            });

            Assert.True(r.Ok, r.Message);
            Assert.Equal(2, r.LevelCount);
            var per = r.PerLevel().ToList();
            Assert.Equal(2, per.Count);
            Assert.All(per, p => Assert.True(p.Cells > 0));
            Assert.All(r.Cells.Where(c => c.LevelIndex == 2), c => Assert.Equal(CrestZ - BenchH, c.CrestZ, 9));
        }

        /// <summary>清单能导出且行数对得上 —— 落库前先能对着 CSV 核账。</summary>
        [Fact]
        public void Report_HasOneRowPerPosition()
        {
            var r = DumpStripPlanner.Plan(new[] { StraightBench() }, new DumpStripPlanner.Options
            {
                PanelLengthM = 100,
                StripWidthM = 40,
                BoundaryRingXy = Rect(-50, -50, 250, 350),
            }, "外排1");

            var csv = DumpStripPlanner.BuildReport(r, "直线算例");
            int rows = csv.Split('\n').Count(l => l.StartsWith("外排1-", StringComparison.Ordinal));
            Assert.Equal(r.Cells.Count, rows);
        }
    }
}
