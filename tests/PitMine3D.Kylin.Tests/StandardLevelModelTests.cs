using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using Xunit;

namespace PitMine3D.Kylin.Tests
{
    /// <summary>
    /// 「采矿模型·按标准水平」归级 + 煤/岩打标的脱 GUI 验收。
    ///
    /// 造一套 3 级台阶（1302 / 1290 / 1278，台阶高 12m）+ 一层水平煤（顶板 1290、底板 1284，煤厚 6m），
    /// 于是两幅的答案是【可手算的】：
    ///   幅① 1302→1290：区间 [1290,1302] 与煤 [1284,1290] 无重叠 → 煤厚 0 → 岩台阶
    ///   幅② 1290→1278：区间 [1278,1290] 与煤 [1284,1290] 重叠 6m → 占比 6/12=0.5 → 达阈值 → 煤台阶
    /// 判据先验：先确认这两幅分得开，再谈更复杂的起伏煤层。
    /// </summary>
    public class StandardLevelModelTests
    {
        // ── 测试替身：直接吐 verts/tris，跳过 base64 打包（几何解包不是本测试的靶子）──

        // ── 参数系统台账驱动的标准水平（snap 路径）────────────────────────

        private static List<StandardLevelSpec> Ledger(params double[] elevations)
            => elevations.Select(z => new StandardLevelSpec
            {
                Code = z.ToString("0"),
                Name = $"{z:0} 平盘",
                ElevationM = z,
                BenchHeightM = 12,
                FaceAngleDeg = 70,
            }).ToList();

        /// <summary>
        /// 【回归靶子】实测台阶线顶点起伏 ±1.5m —— 盲聚类会把每条都判成"坡面线"整条剔掉，一级都归不出来
        /// （这正是现场报"分析水平并未分析出标准水平"的根因）。
        /// 而标准水平本就登记在参数系统里，不该由线的质量决定：接上台账后照样归级出幅。
        /// </summary>
        [Fact]
        public void Analyze_UndulatingLines_ClusterPathFindsNothing_ButLedgerSnapsThem()
        {
            // 顶点在 z±1.5 之间起伏 → 单条起伏 3m，远超 FlatTolM 默认 0.5m
            BenchLevelInventory.SourceLine Wavy(ulong h, double z, double half)
            {
                var r = Ring(h, z, half);
                for (int i = 0, k = 0; i + 2 < r.Xyz.Length; i += 3, k++)
                    r.Xyz[i + 2] = z + (k % 2 == 0 ? 1.5 : -1.5);
                return r;
            }
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Wavy(1, 1302, 294), Wavy(2, 1290, 290), Wavy(3, 1278, 286),
            };

            // ① 盲聚类：全被当坡面线剔掉，一级都没有
            var blind = StandardLevelModel.Analyze(lines, sampler: null);
            Assert.False(blind.Ok);
            Assert.Equal("图上台阶线聚类", blind.LevelSource);

            // ② 接上台账：照样归出 3 级、配出 2 幅，且级标高取【台账值】而不是线的中位数
            var res = StandardLevelModel.Analyze(lines, sampler: null,
                new StandardLevelModel.Options { StandardLevels = Ledger(1302, 1290, 1278) });

            Assert.True(res.Ok, res.Message);
            Assert.Equal("参数系统台账", res.LevelSource);
            Assert.Equal(3, res.LevelCount);
            Assert.Equal(2, res.Pairs.Count);
            Assert.Equal(1302.0, res.Pairs[0].CrestZ, 6);   // 台账标高，不带线的 ±1.5 起伏
            Assert.Equal(1290.0, res.Pairs[0].ToeZ, 6);
            Assert.Equal(12.0, res.Pairs[0].BenchHeightM, 6);
        }

        /// <summary>台账里有、图上没线的级要单独报出来 —— 那些级出不了幅，不能默默少几幅。</summary>
        [Fact]
        public void Analyze_LedgerLevelWithoutLines_IsReportedNotSilentlyDropped()
        {
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(1, 1302, 294), Ring(2, 1290, 290),   // 1278 级台账里有，图上没线
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null,
                new StandardLevelModel.Options { StandardLevels = Ledger(1302, 1290, 1278) });

            Assert.True(res.Ok, res.Message);
            Assert.Equal(2, res.LevelCount);
            Assert.Single(res.Pairs);
            Assert.Contains(1278.0, res.LevelsWithoutLines);
            Assert.Contains(res.Warnings, w => w.Contains("图上没线"));
        }

        /// <summary>落不进任何标准水平吸附域的线（地形等高线之类）计数上报，不能混进级里。</summary>
        [Fact]
        public void Analyze_LinesOffTheLedger_AreCountedAsUnsnapped()
        {
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(1, 1302, 294), Ring(2, 1290, 290),
                Ring(9, 1233.7, 400),   // 离最近的 1278 差 44m，远超吸附容差 4m
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null,
                new StandardLevelModel.Options { StandardLevels = Ledger(1302, 1290, 1278) });

            Assert.True(res.Ok, res.Message);
            Assert.Equal(1, res.UnsnappedLines);
            Assert.Equal(2, res.LevelCount);          // 那条线没被塞进任何一级
            Assert.Contains(res.Warnings, w => w.Contains("落不进任何标准水平"));
        }



        /// <summary>
        /// 坡面角闸门【默认必须关闭】—— run ≤ H/tan(α) 这条关系只对"按设计角度切出来的坡面"成立。
        /// 沿底板开采的煤台阶，上界顶板、下界底板，坡面就是煤层产状，间距由【煤层倾角】定：
        /// 现场煤层约 3°，5.87m 垂高对应水平距 ≈112m。若默认开着 45°，这类幅会被全部拒掉。
        /// </summary>
        [Fact]
        public void Analyze_GentleSeamFollowingBench_IsNotRejectedByDefault()
        {
            BenchLevelInventory.SourceLine AlongY(ulong h, double z, double x)
                => new() { Handle = h, Xyz = new[] { x, 0.0, z, x, 400.0, z } };

            // 5.87m 台阶、24m 间距 = 13.7°：比设计坡面角缓得多，但对沿煤层走的煤台阶是正常的
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                AlongY(1, 1205.29, 24.0), AlongY(2, 1199.42, 0.0),
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            var p0 = Assert.Single(res.Pairs);
            Assert.Equal(24.0, p0.FaceRunM, 1);
        }

        /// <summary>显式开启坡面角闸门时（明确是岩台阶）才拒 —— 接口留着，但要调用方主动要。</summary>
        [Fact]
        public void Analyze_FaceAngleGate_RejectsOnlyWhenExplicitlyEnabled()
        {
            BenchLevelInventory.SourceLine AlongY(ulong h, double z, double x)
                => new() { Handle = h, Xyz = new[] { x, 0.0, z, x, 400.0, z } };

            var lines = new List<BenchLevelInventory.SourceLine>
            {
                AlongY(1, 1205.29, 24.0), AlongY(2, 1199.42, 0.0),
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null,
                new StandardLevelModel.Options { MinFaceAngleDeg = 45.0 });

            Assert.False(res.Ok, "显式开启 45° 闸门时，13.7° 的坡面应被拒");
            Assert.Empty(res.Pairs);
        }

        /// <summary>同样 5.87m 台阶、间距 2.7m（65° 正常设计坡面角）必须照常配出来。</summary>
        [Fact]
        public void Analyze_NormalDesignFaceAngle_StillPairs()
        {
            BenchLevelInventory.SourceLine AlongY(ulong h, double z, double x)
                => new() { Handle = h, Xyz = new[] { x, 0.0, z, x, 400.0, z } };

            var lines = new List<BenchLevelInventory.SourceLine>
            {
                AlongY(1, 1205.29, 2.7), AlongY(2, 1199.42, 0.0),
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            var p = Assert.Single(res.Pairs);
            Assert.Equal(2.7, p.FaceRunM, 1);
        }


        /// <summary>一条闭合矩形平盘线（世界坐标扁平 xyz），四角同 Z。</summary>
        private static BenchLevelInventory.SourceLine Ring(ulong handle, double z, double half)
        {
            var xyz = new List<double>();
            void P(double x, double y) { xyz.Add(x); xyz.Add(y); xyz.Add(z); }
            P(-half, -half); P(half, -half); P(half, half); P(-half, half); P(-half, -half);
            return new BenchLevelInventory.SourceLine { Handle = handle, Layer = "台阶线", Xyz = xyz.ToArray() };
        }


        [Fact]
        public void Analyze_ThreeLevels_YieldsTwoPairs_WithCorrectBenchHeights()
        {
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(1, 1302, 294), Ring(2, 1290, 290), Ring(3, 1278, 286),
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            Assert.Equal(3, res.LevelCount);
            Assert.Equal(2, res.Pairs.Count);
            Assert.Equal(1302, res.Pairs[0].CrestZ, 3);
            Assert.Equal(1290, res.Pairs[0].ToeZ, 3);
            Assert.Equal(12.0, res.Pairs[0].BenchHeightM, 3);
            Assert.Equal(12.0, res.Pairs[1].BenchHeightM, 3);
        }

        [Fact]
        public void Analyze_WithoutSeamModel_AllBenchesAreRock()
        {
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(1, 1302, 294), Ring(2, 1290, 290), Ring(3, 1278, 286),
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            Assert.False(res.HasSeamModel);
            Assert.Equal(2, res.RockBenches);
            Assert.Equal(0, res.CoalBenches);
            Assert.All(res.Pairs, p => Assert.Equal(StandardLevelModel.BenchKind.Rock, p.Kind));
            Assert.Contains(res.Warnings, w => w.Contains("煤层顶底板面"));
        }






        /// <summary>
        /// 真实露天坑的样子：每个标高上有【两条同心线】——上一台阶的坡底(外环) + 下一台阶的坡顶(内环)。
        /// 质心完全重合，所以"最近质心配对"在这里是退化的。判据是【两线实际水平间距】：
        /// 真坡顶/坡底之间隔的就是坡面水平投影(这里 4m)，比任何错配(≥14m)都小 → 一个间隙恰好一幅。
        /// </summary>
        [Fact]
        public void Analyze_ConcentricRings_PicksTrueFacePair_OnePairPerGap()
        {
            // 坑越往下越窄。台阶 1302→1290 的坡面投影 = 284−280 = 4m；平盘宽 10m。
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(10, 1302, 294), Ring(11, 1302, 284),   // 294 = 上一台阶坡底, 284 = 本台阶坡顶
                Ring(20, 1290, 280), Ring(21, 1290, 270),   // 280 = 本台阶坡底, 270 = 下一台阶坡顶
                Ring(30, 1278, 266),
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            Assert.Equal(3, res.LevelCount);
            Assert.Equal(2, res.Pairs.Count);              // 幅数 = 级数 − 1，不是 4

            Assert.Equal(11ul, res.Pairs[0].CrestHandle);  // 284 ↔ 280，间距 4m
            Assert.Equal(20ul, res.Pairs[0].ToeHandle);
            Assert.Equal(4.0, res.Pairs[0].FaceRunM, 1);
            Assert.Equal(21ul, res.Pairs[1].CrestHandle);  // 270 ↔ 266
            Assert.Equal(30ul, res.Pairs[1].ToeHandle);
        }

        /// <summary>
        /// 间距判据与采/排极性无关：把上例翻成排土场几何(越往上越窄)，最近的一对依旧是同一坡面那对。
        /// 这正是不再需要传 IsDump 的理由 —— 镜像的是几何，不是判据。
        /// </summary>
        [Fact]
        public void Analyze_DumpGeometry_StillPicksTrueFacePair_WithoutPolarityHint()
        {
            // 排土场：下级环更大。台阶 1302→1290 的坡面投影 = 224−220 = 4m。
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(10, 1302, 200), Ring(11, 1302, 220),   // 220 = 本台阶坡顶(外), 200 = 上一台阶坡底(内)
                Ring(20, 1290, 224), Ring(21, 1290, 244),   // 224 = 本台阶坡底
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            Assert.Single(res.Pairs);
            Assert.Equal(11ul, res.Pairs[0].CrestHandle);
            Assert.Equal(20ul, res.Pairs[0].ToeHandle);
            Assert.Equal(4.0, res.Pairs[0].FaceRunM, 1);
        }

        /// <summary>
        /// 回归靶子：两条相距很远、又不平行的线绝不能被 loft 成横穿全图的张合条带 —— 那是
        /// 界面上"体斜穿等高线、时宽时窄"的成因。间距闸门必须把它们挡在外面（宁可不出这一幅）。
        /// </summary>
        [Fact]
        public void Analyze_FarApartUnrelatedLines_AreGatedOut_NotLoftedIntoRibbons()
        {
            BenchLevelInventory.SourceLine Offset(ulong h, double z, double half, double ox)
            {
                var r = Ring(h, z, half);
                for (int i = 0; i < r.Xyz.Length; i += 3) r.Xyz[i] += ox;
                return r;
            }
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(1, 1302, 100), Offset(2, 1290, 100, 5000),   // 相距 5km
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.False(res.Ok);                                  // 一幅都不该出
            Assert.Empty(res.Pairs);
            Assert.Contains("一幅都没配出来", res.Message);
        }



        [Fact]
        public void Analyze_SingleLevel_FailsWithActionableMessage()
        {
            var lines = new List<BenchLevelInventory.SourceLine> { Ring(1, 1290, 200) };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.False(res.Ok);
            Assert.Contains("凑不出一幅", res.Message);
        }

        [Fact]
        public void Analyze_TiltedLinesAreExcludedFromLevels()
        {
            // 坡面线 / 出入沟（起伏 6m）不该定标高，否则级数被撑爆。
            var slope = new BenchLevelInventory.SourceLine
            {
                Handle = 9,
                Layer = "坡面线",
                Xyz = new double[] { 0, 0, 1296, 100, 0, 1290, 200, 0, 1284 },
            };
            var lines = new List<BenchLevelInventory.SourceLine>
            {
                Ring(1, 1302, 294), Ring(2, 1290, 290), Ring(3, 1278, 286), slope,
            };

            var res = StandardLevelModel.Analyze(lines, sampler: null);

            Assert.True(res.Ok, res.Message);
            Assert.Equal(3, res.LevelCount);                 // 斜线没有多归出一级
            Assert.Equal(1, res.Levels!.SkippedTilted);
            Assert.DoesNotContain(res.Pairs, p => p.CrestHandle == 9 || p.ToeHandle == 9);
        }
    }
}
