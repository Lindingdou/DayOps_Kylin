using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Transport;
using PitMine3D.Kylin.Cad.RoadLayout;
using StraightRampAutoRouter = PitMine3D.Kylin.Cad.RoadLayout.StraightRampAutoRouter;
using Xunit;

namespace PitMine3D.Kylin.Tests.RoadLayout;

/// <summary>
/// 「选线器 → 求解器/规划器 → 布线器」整条链路的端到端一致性回归(纯逻辑,不碰几何核)。
///
/// 盯的是几条曾经真出过问题、且只在链路两端接起来时才暴露的口径:
///   ① 每级多候选必须每级择一条 —— 整表直接串起来展线总长会翻 CandidatesPerLevel 倍;
///   ② 方案段的中心线必须消费候选的 <see cref="RampCandidate.Centerline"/> —— 只塞起坡点则预览退化成折线;
///   ③ 可布走廊 / 禁布区的收紧判据要真把候选筛掉,不是写了没生效;
///   ④ 螺旋兜底放得下才出螺旋、放不下如实止步并报出是哪条判据没过;
///   ⑤ 约束方案容器从"老单份配置"升级时不丢用户配置。
/// </summary>
public class RoadLayoutPipelineTests
{
    /// <summary>默认约束下的展线长:ΔH 15m ÷ 8% = 187.5m。</summary>
    private const double NeedLenPerLevel = 187.5;

    /// <summary>
    /// 3 级方环台阶线(边长 200m,闭合周长 800m,平盘 40m &gt; 路宽 17m)。
    /// 每级需展线 187.5m &lt; 可布走廊 → 三级都布得下,便于只考察"链长有没有被放大"。
    /// </summary>
    private static RoadLayoutInput MakeThreeLevelInput()
    {
        var benches = new List<BenchLine>();
        foreach (double lv in new[] { 100.0, 85.0, 70.0 })
            benches.Add(new BenchLine
            {
                Level = lv,
                Crest = new List<(double, double, double)>
                {
                    (0, 0, lv), (200, 0, lv), (200, 200, lv), (0, 200, lv)
                },
                BermWidth = 40,
                IsWorkingWall = false,
            });
        return new RoadLayoutInput
        {
            Sources = new List<LoadingPoint> { new() { Id = "S1", Level = 70, OreTons = 1000 } },
            Sinks = new List<UnloadingPoint> { new() { Id = "U1", Kind = UnloadKind.Crusher, X = 500, Y = 0 } },
            Constraints = new TransportConstraintSettings(),
            Benches = benches,
        };
    }

    // ───────────────────── ① 链长不被候选数放大(3 级实测) ─────────────────────

    [Fact]
    public void ThreeLevels_SolverChain_OneSegmentPerLevel_LengthNotTripled()
    {
        var input = MakeThreeLevelInput();

        // 先确认夹具确实是"每级 3 候选"——否则本测试等于什么都没验。
        var cands = new RampRouteGenerator().Generate(input);
        Assert.Equal(2, RampRouteGenerator.GroupByLevelPair(cands).Count);          // 3 级 → 2 对相邻水平
        Assert.Equal(2 * RampRouteGenerator.CandidatesPerLevel, cands.Count);       // 共 6 条候选
        // 走廊够宽 → 6 条全判斜坡道且几何可行(其中一部分是"绕帮段"绕出来的,形式仍是 Straight)。
        Assert.All(cands, c => Assert.Equal(RampForm.Straight, c.Form));
        Assert.All(cands, c => Assert.True(c.GeomFeasible, c.Note));
        Assert.Contains(cands, c => c.WallSpanCount > 1);                           // 确有候选靠跨帮才够长

        var r = new RoadLayoutSolver().Solve(input);
        Assert.True(r.Success);
        var line = r.Schemes[0].Lines[0];

        // 链 = 2 段(每级一条),不是 6 段;总长 375m,不是 1125m。
        Assert.Equal(2, line.Segments.Count);
        Assert.Equal(2 * NeedLenPerLevel, line.LengthM, 3);
        Assert.True(line.LengthM < 3 * 2 * NeedLenPerLevel,
            $"展线总长 {line.LengthM:0.#}m 被候选数放大了(每级择一条应为 {2 * NeedLenPerLevel:0.#}m)");
        // 两段确实分属不同水平,不是同一级串了两遍。
        Assert.Equal(2, line.Segments.Select(s => (s.FromLevel, s.ToLevel)).Distinct().Count());
    }

    [Fact]
    public void ThreeLevels_PlannerChain_AlsoPicksOnePerLevel()
    {
        // 规划器(旧单线入口)与求解器共用同一份 PickChainPerLevel,否则同一批候选两边给的链长会打架。
        var r = new RoadLayoutPlanner().BuildScheme(MakeThreeLevelInput());
        Assert.True(r.Success);
        var line = r.Schemes[0].Lines[0];

        Assert.Equal(2, line.Segments.Count);
        Assert.Equal(2 * NeedLenPerLevel, line.LengthM, 3);
        // 水平序:100 → 85 → 70,三个标高不重复。
        Assert.Equal(new[] { 100.0, 85.0, 70.0 }, line.LevelSequence.ToArray());
    }

    // ───────────────────── ② 方案段消费候选中心线 ─────────────────────

    [Fact]
    public void SolverSegments_ConsumeCandidateCenterline_NotJustPortalPoint()
    {
        var r = new RoadLayoutSolver().Solve(MakeThreeLevelInput());
        var line = r.Schemes[0].Lines[0];

        foreach (var sg in line.Segments)
        {
            // 中心线不再是"只有起坡点"的单点占位 —— 否则方案链预览只画得出起坡点折线。
            Assert.True(sg.Centerline.Count >= 2, $"段 {sg.FromLevel:0}→{sg.ToLevel:0} 中心线只有 {sg.Centerline.Count} 点");
            Assert.Equal(sg.FromLevel, sg.Centerline[0].Z, 6);                          // 首点起于本级
            Assert.Equal(sg.ToLevel, sg.Centerline[sg.Centerline.Count - 1].Z, 6);      // 末点落到下级
        }
    }

    [Fact]
    public void PlannerSegments_ConsumeCandidateCenterline_NotJustPortalPoint()
    {
        var r = new RoadLayoutPlanner().BuildScheme(MakeThreeLevelInput());
        Assert.All(r.Schemes[0].Lines[0].Segments, sg => Assert.True(sg.Centerline.Count >= 2));
    }

    [Fact]
    public void SegmentCenterline_FallsBackToPortal_WhenCandidateHasNone()
    {
        // 老调用方自造的候选没有中心线 → 退回 [起坡点] 单点表,不报错也不编造几何。
        var input = MakeThreeLevelInput();
        input.Candidates = new List<RampCandidate>
        {
            new()
            {
                FromLevel = 100, ToLevel = 85, Form = RampForm.Straight, GradePct = 8,
                RequiredLengthM = NeedLenPerLevel, PerLaneCapacityTons = 1e9,
                GeomFeasible = true, PortalStart = (7, 9, 100),
            }
        };
        var sg = new RoadLayoutSolver().Solve(input).Schemes[0].Lines[0].Segments[0];
        Assert.Single(sg.Centerline);
        Assert.Equal((7.0, 9.0, 100.0), sg.Centerline[0]);
    }

    // ───────────────────── ③ 可布走廊 / 禁布区 ─────────────────────

    [Fact]
    public void NarrowBerm_NoDeployableCorridor_CandidatesJudgedInfeasible()
    {
        // 平盘 5m < 路面宽 B(默认 17m) → 全环放不下路面 → 本级无可布走廊,如实判不可行。
        var input = MakeThreeLevelInput();
        foreach (var b in input.Benches) b.BermWidth = 5;

        var cands = new RampRouteGenerator().Generate(input);
        Assert.NotEmpty(cands);
        Assert.All(cands, c =>
        {
            Assert.False(c.GeomFeasible);
            Assert.Equal(0.0, c.CorridorLengthM, 6);
            Assert.Contains("无可布走廊", c.Note);
            Assert.Contains("平盘窄", c.Note);          // 扣掉的原因逐项写在 Note 里
        });

        // 收紧的判据要一路传到方案:不可行候选进 Violations,方案判不可行(宁可保守)。
        var r = new RoadLayoutSolver().Solve(input);
        Assert.True(r.Success);
        Assert.All(r.Schemes, s => Assert.False(s.Feasible));
    }

    [Fact]
    public void NarrowSection_ExcludedFromCorridor_ButRestStillDeployable()
    {
        // 下级台阶线只在【底边】贴得很近(净距 5m < 路宽 17m)→ 该区段判不可布,
        // 其余三边净距 30m 仍可布 → 走廊被"部分"扣掉,不是全有或全无。
        var benches = new List<BenchLine>
        {
            new()
            {
                Level = 100, BermWidth = 40, IsWorkingWall = false,
                Crest = new List<(double, double, double)>
                    { (0, 0, 100), (200, 0, 100), (200, 200, 100), (0, 200, 100) },
            },
            new()
            {
                Level = 85, BermWidth = 40, IsWorkingWall = false,
                Crest = new List<(double, double, double)>
                    { (30, 5, 85), (170, 5, 85), (170, 170, 85), (30, 170, 85) },
            },
        };
        var input = new RoadLayoutInput
        {
            Sources = new List<LoadingPoint> { new() { Id = "S1", Level = 85, OreTons = 1000 } },
            Constraints = new TransportConstraintSettings(),
            Benches = benches,
        };

        var cands = new RampRouteGenerator().Generate(input);
        Assert.NotEmpty(cands);
        Assert.All(cands, c =>
        {
            Assert.True(c.CorridorLengthM > 1.0, "走廊被整条扣光了,判据过严");
            Assert.True(c.CorridorLengthM < 800.0 - 1.0,
                $"走廊 {c.CorridorLengthM:0.#}m ≈ 整条周长 800m —— 贴得太近的底边没有被扣掉");
            Assert.Contains("帮台进深不足", c.Note);
        });
    }

    [Fact]
    public void NoGoZone_ExcludesHitCandidates_ChainPicksAnotherPortal()
    {
        var input = MakeThreeLevelInput();
        // 不设禁布区时首条候选的起坡点就是坡顶线首点 (0,0)。
        var baseline = new RoadLayoutSolver().Solve(input).Schemes[0].Lines[0].Segments[0];
        Assert.Equal(0.0, baseline.Centerline[0].X, 6);
        Assert.Equal(0.0, baseline.Centerline[0].Y, 6);

        // 把 (0,0) 那一角圈成禁布区 → 该候选被剔除,链改选别的起坡点。
        input.NoGoZones = new List<IReadOnlyList<(double X, double Y, double Z)>>
        {
            new List<(double, double, double)> { (-50, -50, 0), (50, -50, 0), (50, 50, 0), (-50, 50, 0) }
        };
        var r = new RoadLayoutSolver().Solve(input);
        Assert.True(r.Success);
        var seg = r.Schemes[0].Lines[0].Segments[0];

        double d = Math.Sqrt(seg.Centerline[0].X * seg.Centerline[0].X + seg.Centerline[0].Y * seg.Centerline[0].Y);
        Assert.True(d > 50.0, $"起坡点 ({seg.Centerline[0].X:0.#},{seg.Centerline[0].Y:0.#}) 仍落在禁布区里");
        // 还有别的候选可用 → 不该出"全部落入禁布区"的告警,方案仍可行。
        Assert.All(r.Schemes[0].Violations, v => Assert.DoesNotContain("全部落入禁布区", v));
        Assert.True(r.Schemes[0].Feasible);
    }

    [Fact]
    public void NoGoZone_CoveringEverything_KeepsLevelInChain_AndWarns()
    {
        // 某级候选被挡光时不能悄悄跳级(链凭空变短 = 假的省钱):仍按首条计入运距,并出告警判不可行。
        var input = MakeThreeLevelInput();
        input.NoGoZones = new List<IReadOnlyList<(double X, double Y, double Z)>>
        {
            new List<(double, double, double)>
                { (-500, -500, 0), (500, -500, 0), (500, 500, 0), (-500, 500, 0) }
        };
        var r = new RoadLayoutSolver().Solve(input);
        Assert.True(r.Success);
        var scheme = r.Schemes[0];

        Assert.Equal(2, scheme.Lines[0].Segments.Count);                       // 级数没少
        Assert.Equal(2 * NeedLenPerLevel, scheme.Lines[0].LengthM, 3);         // 链长没缩水
        Assert.Contains(scheme.Violations, v => v.Contains("全部落入禁布区"));
        Assert.False(scheme.Feasible);
    }

    // ───────────────────── ④ 螺旋兜底:放得下出螺旋 / 放不下止步 ─────────────────────

    /// <summary>逐级给边长的方环台阶线(标高自 100m 起每级降 15m),供螺旋兜底判据取环周长。</summary>
    private static List<BenchLine> MakeSquareRings(params double[] sides)
    {
        var list = new List<BenchLine>();
        for (int k = 0; k < sides.Length; k++)
        {
            double z = 100.0 - 15.0 * k, side = sides[k];
            list.Add(new BenchLine
            {
                Level = z, BermWidth = 40, IsWorkingWall = false,
                Crest = new List<(double, double, double)>
                    { (0, 0, z), (side, 0, z), (side, side, z), (0, side, z) },
            });
        }
        return list;
    }

    /// <summary>关掉折返兜底(否则 2R+B 恒松于 2πR,螺旋分支轮不到),只考察螺旋判据本身。</summary>
    private static StraightRampRouteOptions SpiralOnlyOptions(double spiralR) => new()
    {
        GradePct = 8,
        RoadWidth = 10,
        AllowSwitchbackFallback = false,   // 折返关掉
        MinTurnRadius = 0,
        AllowSpiralFallback = true,
        SpiralMinRadius = spiralR,
        MinCurveRadiusM = 0,               // 不抬 R,判据可手算
        ApplyLineForm = false,             // 不做线形后处理,中线即螺旋原始点列
    };

    [Fact]
    public void SpiralFallback_RingFitsOneTurn_ProducesSpiralLegWithComputedTurns()
    {
        // 环周长 160m:放不下直线 L=187.5m,但盘得下一圈 2πR=62.8m(R=10) → 出螺旋。
        var rr = StraightRampAutoRouter.Route(MakeSquareRings(40, 40), SpiralOnlyOptions(10));

        Assert.True(rr.Success, rr.Error);
        Assert.True(rr.ReachedBottom);
        Assert.Equal(1, rr.SpiralLegs);
        Assert.Equal(0, rr.StraightLegs);
        Assert.Equal(0, rr.SwitchbackLegs);

        var lv = rr.Levels[0];
        Assert.Equal(RampForm.Spiral, lv.Form);
        Assert.True(lv.Feasible);
        Assert.Equal(10.0, lv.SpiralRadiusM, 6);
        // 圈数是真算的:n = L / (2πR) = 187.5 / 62.83 ≈ 2.98,不是标一个"可行"了事。
        Assert.Equal(NeedLenPerLevel / (2 * Math.PI * 10.0), lv.SpiralTurns, 6);
        Assert.Equal(lv.SpiralTurns, rr.SpiralTurnsTotal, 6);
        // 展线按解析弧长计(圈数 × 一圈周长),与采样密度无关。
        Assert.Equal(lv.SpiralTurns * 2 * Math.PI * 10.0, rr.TotalRunM, 3);

        // 螺旋段不落地:没算出逐面切 O 点就是没有,原因如实回显、不硬编。
        Assert.False(rr.SpiralLandable);
        Assert.False(string.IsNullOrWhiteSpace(rr.SpiralLandingNote));
        Assert.Empty(rr.FaceCutOPoints);
    }

    [Fact]
    public void SpiralFallback_RingTooSmall_StopsAndReportsWhichCriterionFailed()
    {
        // 前两级方环 200m(周长 800 ≥ L=187.5 → 直线布得下),最深一级缩到 10m(周长 40m):
        // 直线放不下、折返关着、螺旋一圈 62.8m 也盘不进 → 止于该级,不为"看着贯通"放宽判据。
        var rr = StraightRampAutoRouter.Route(MakeSquareRings(200, 200, 10), SpiralOnlyOptions(10));

        Assert.True(rr.Success, rr.Error);
        Assert.False(rr.ReachedBottom);          // 部分贯通
        Assert.Equal(1, rr.LevelsConnected);
        Assert.Equal(2, rr.LevelsTotal);
        Assert.Equal(0, rr.SpiralLegs);          // 盘不下就不产螺旋段
        Assert.Equal(1, rr.StraightLegs);

        var lv = rr.Levels[1];                   // 止步发生在第 2 级
        Assert.False(lv.Feasible);
        Assert.Contains("止步", lv.Note);
        Assert.Contains("螺旋一圈周长", lv.Note);   // 点名是哪一条判据没过(带数)
        // 算出来但放不下的那组螺旋参数仍如实带出,供报告给数。
        Assert.Equal(10.0, lv.SpiralRadiusM, 6);
        Assert.True(lv.SpiralTurns > 0);
    }

    [Fact]
    public void SpiralFallback_Disabled_StopsWithoutPretendingSpiral()
    {
        var opt = SpiralOnlyOptions(10);
        opt.AllowSpiralFallback = false;
        var rr = StraightRampAutoRouter.Route(MakeSquareRings(40, 40), opt);

        // 首级即放不下 → 一条腿都没布下:Success=false + 原因如实回显(既有口径)。
        Assert.False(rr.Success);
        Assert.Contains("首级即不可行", rr.Error);
        Assert.False(rr.ReachedBottom);
        Assert.Equal(0, rr.SpiralLegs);
        Assert.False(rr.Levels[0].Feasible);
        Assert.Contains("未启用螺旋兜底", rr.Levels[0].Note);
        Assert.Equal(0.0, rr.Levels[0].SpiralRadiusM, 6);   // 没启用就不给螺旋参数,不编造
    }

    // ───────────────────── ⑤ 约束方案管理:老配置迁移不丢 ─────────────────────

    [Fact]
    public void ProfileContainer_MigratesLegacySingleSettings_WithoutLoss()
    {
        var legacy = new TransportConstraintSettings { TruckClass = "自定义", MaxGradePct = 6.5, LaneCount = 3 };
        var box = new TransportConstraintProfiles();   // 空容器 = 从"只有一份全局配置"的老版本升上来

        var cur = box.EnsureUsable(legacy);

        Assert.Same(legacy, cur);                                            // 原对象直接收编,不复制走样
        Assert.Equal(TransportConstraintProfiles.DefaultName, box.CurrentName);
        Assert.Single(box.Profiles);
        Assert.Equal(6.5, box.Profiles[TransportConstraintProfiles.DefaultName].MaxGradePct, 6);
        Assert.Equal(3, box.Profiles[TransportConstraintProfiles.DefaultName].LaneCount);
        // 镜像键必须仍是老键:消费侧(插件的布线/落地)只认它,改了就是配置静默失联。
        Assert.Equal("transport.constraints", TransportConstraintProfiles.MirrorKey);
    }

    [Fact]
    public void ProfileContainer_DanglingCurrentName_FallsBackWithoutDroppingProfiles()
    {
        var box = new TransportConstraintProfiles();
        box.EnsureUsable(new TransportConstraintSettings { MaxGradePct = 6.5 });
        box.CurrentName = "指向一个已删掉的方案";

        var cur = box.EnsureUsable(new TransportConstraintSettings());

        Assert.Equal(TransportConstraintProfiles.DefaultName, box.CurrentName);
        Assert.Equal(6.5, cur.MaxGradePct, 6);      // 回落到既有方案,不是拿 fallback 把它冲掉
        Assert.Single(box.Profiles);
    }

    [Fact]
    public void ProfileContainer_MergeFrom_NeverOverwritesExistingProfile()
    {
        var box = new TransportConstraintProfiles();
        box.EnsureUsable(new TransportConstraintSettings { MaxGradePct = 6.5 });

        var other = new TransportConstraintProfiles();
        other.Profiles[TransportConstraintProfiles.DefaultName] = new TransportConstraintSettings { MaxGradePct = 9.0 };
        var added = box.MergeFrom(other);

        Assert.Single(added);
        Assert.NotEqual(TransportConstraintProfiles.DefaultName, added[0]);   // 重名自动加后缀
        Assert.Equal(2, box.Profiles.Count);
        Assert.Equal(6.5, box.Profiles[TransportConstraintProfiles.DefaultName].MaxGradePct, 6);  // 原方案没被覆盖
        Assert.Equal(9.0, box.Profiles[added[0]].MaxGradePct, 6);
    }

    // ───────────────────── ⑥ BenchLine.Level 的口径:标高,不是台阶序号 ─────────────────────

    [Fact]
    public void BenchLineLevel_IsElevation_DrivesRequiredRunLength()
    {
        // 需展线 = ΔH / i;ΔH 取自 BenchLine.Level 之差,故 Level 必须是【标高 m】。
        var cands = new RampRouteGenerator().Generate(MakeThreeLevelInput());
        Assert.All(cands, c => Assert.Equal(NeedLenPerLevel, c.RequiredLengthM, 6));   // 15m ÷ 8% = 187.5m
    }

    [Fact]
    public void BenchLineLevel_IfFedBenchOrdinal_RequiredRunCollapses()
    {
        // 反例(记录踩过的坑):把【台阶序号 0/1/2】当标高喂进来 → ΔH 恒为 1 → 需展线只剩 12.5m,
        // 每级都"轻松布得下",整套选线/运距/基建量静默失真。
        // 上游 BenchRecord.Level 正是 int 序号,故 MineAssLibPlugin.BuildBenchLines 必须按几何 Z 取标高。
        var input = MakeThreeLevelInput();
        for (int k = 0; k < input.Benches.Count; k++) input.Benches[k].Level = k;   // 2/1/0 → 序号

        var cands = new RampRouteGenerator().Generate(input);
        Assert.All(cands, c => Assert.Equal(1.0 / 0.08, c.RequiredLengthM, 6));     // 12.5m,而非 187.5m
        Assert.True(cands[0].RequiredLengthM < NeedLenPerLevel / 10,
            "喂序号与喂标高居然算出同一个展线长 —— 本反例失效,说明 ΔH 不再取自 Level");
    }

    // ───────────────────── ⑦ 优化目标:唯一口径 ─────────────────────

    [Fact]
    public void Objectives_AreTheSingleSourceOfTruth_AndLegacyStringsNormalize()
    {
        // 对话框下拉 / 比选窗下拉 / Score() 一律取这一份;顺序即下拉顺序,[0] 为默认。
        Assert.Equal(4, RoadLayoutSolver.Objectives.Length);
        Assert.Equal(RoadLayoutSolver.ObjMinCost, RoadLayoutSolver.Objectives[0]);
        Assert.Equal(RoadLayoutSolver.Objectives.Length, RoadLayoutSolver.Objectives.Distinct().Count());

        // 自己的每一项必须原样归一(否则选了 A 求解器按 B 算)。
        foreach (var o in RoadLayoutSolver.Objectives)
            Assert.Equal(o, RoadLayoutSolver.NormalizeObjective(o));

        // 历史串 / 空值一律回落到 [0],不静默落 default 分支。
        Assert.Equal(RoadLayoutSolver.Objectives[0], RoadLayoutSolver.NormalizeObjective("最短运距"));
        Assert.Equal(RoadLayoutSolver.Objectives[0], RoadLayoutSolver.NormalizeObjective("最小基建工程量"));
        Assert.Equal(RoadLayoutSolver.Objectives[0], RoadLayoutSolver.NormalizeObjective(null));
        Assert.Equal(RoadLayoutSolver.Objectives[0], RoadLayoutSolver.NormalizeObjective("  "));
    }
}
