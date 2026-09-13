// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimBuilder.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  时间轴构建器 —— 把「计划」变成「逐期推演」。
//
//  三条数据通路（全部软接，任一层缺失只降级不抛）：
//    L1 年   ← PlanLib.LongTerm.LongTermSchemeStore.Confirmed.Periods（逐年采剥）
//    L2 月   ← PlanLib.ShortTerm.ShortTermSchemeStore.Confirmed.Months（逐月采剥）
//    L3 日班 ← SampleTaskBoard.Day() 的 ProductionTask 按班次聚合（当日盘子）
//
//  年/月两级只给「全矿采出量 + 全矿剥离量」两个总数，落不到面、更落不到汇。
//  本类补上这两步，推演才谈得上「采场逐期挖除、排土场逐期堆填」：
//    ① 按当日盘子里各采装面的煤/岩产出**份额**，把期总量摊到各面（物料 Kr/ρ 随面走）；
//    ② 把摊好的供给交 <see cref="FlowAssigner"/> 求解去向（运输功最小 + 库容/通过能力约束），
//       并把结果**逐期累加回排土场库容**——这才是「排土场逐期堆填」的真正含义：
//       第 N 期排土场满了，第 N+1 期的剥离就得改投别处或者根本排不下，缺口会在配对校核里露出来。
//
//  ── 采排配对校核在两级上的口径（两侧必须独立取数，否则是恒等式）──
//    L3 日班：采侧 = 采装任务（Load）按物料折出的应排占容；排侧 = 排土任务（Dump）的作业量。
//             两者分别来自采装面台账与排土面台账，实绩轨下会因欠产而真的对不上。
//    L1/L2  ：采侧 = 计划剥离量 × Kr（计划给的**需求**）；排侧 = 求解器实际排下去的占容
//             （受库容/通过能力约束的**供给**）。差额 = 本期排不下的量。
// ─────────────────────────────────────────────────────────────────────────────

public static class SimBuilder
{
    private const double Eps = 1e-6;

    /// <summary>年推演在没有中长远计划时的最大外推期数（避免造一串没有信息量的相同年份）。</summary>
    private const int EstimatedYearCount = 1;
    /// <summary>月推演在没有短期计划时的外推期数。</summary>
    private const int EstimatedMonthCount = 12;

    // ── 主入口 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 构建一条推演时间轴。任何数据源缺失都降级并把原因写进
    /// <see cref="SimTimeline.SourceLabel"/> / <see cref="SimTimeline.Notes"/>，绝不抛。
    /// </summary>
    /// <param name="granularity">Trip 粒度按 Shift 构建（车次层的信息面板复用日班账）。</param>
    /// <param name="prm">采场几何反算参数；null = 现读（<see cref="SimMiningParams.Load"/>）。</param>
    /// <param name="scheme">
    /// **年粒度专用**：演哪一套中长远方案（方案名）。null/空 = 按
    /// <see cref="LongTermPlanLink.Pick"/> 的顺序自动挑（已确定 → 综合得分最高的已排产 → 第一套）。
    /// <para>年粒度上"选哪一期"是没有意义的（一套方案的逐年表本来就是一条轴），
    /// 要选的是**哪一套方案** —— 与月粒度按 <paramref name="onlyPeriod"/> 选月份不是一回事。</para>
    /// </param>
    public static SimTimeline Build(SimGranularity granularity, SimTrack track, SimMiningParams? prm = null,
                                    string? onlyPeriod = null, string? scheme = null)
    {
        var p = prm ?? SimMiningParams.Load();
        try
        {
            return granularity switch
            {
                SimGranularity.Year => BuildPeriodic(SimGranularity.Year, track, p, onlyPeriod, scheme),
                SimGranularity.Month => BuildPeriodic(SimGranularity.Month, track, p, onlyPeriod, null),
                _ => BuildShift(track, p),
            };
        }
        catch (Exception ex)
        {
            var tl = new SimTimeline
            {
                Granularity = granularity, Track = track, MiningParams = p,
                SourceLabel = $"推演构建失败：{Short(ex)}",
            };
            tl.Notes.Add("时间轴未能构建，界面已降级为空推演。请检查计划数据与去向台账是否可读。");
            return tl;
        }
    }

    // ═════════════════════════ L3 日班 ═════════════════════════

    /// <summary>
    /// 从当日盘子按班次构建。计划轨用 TargetVolumeM3，实绩轨用 ActualVolumeM3。
    /// <para>口径：采装任务的量是【实方】；排土任务的量是【占容方】（引擎按入方 ×Kr 推导后写回）。</para>
    /// </summary>
    private static SimTimeline BuildShift(SimTrack track, SimMiningParams prm)
    {
        var tl = new SimTimeline { Granularity = SimGranularity.Shift, Track = track, MiningParams = prm };

        ExploderConfig cfg;
        List<ProductionTask> tasks;
        try
        {
            cfg = SampleTaskBoard.Config();
            tasks = SampleTaskBoard.Day();
        }
        catch (Exception ex)
        {
            tl.SourceLabel = $"当日任务盘子不可读（{Short(ex)}）";
            tl.Notes.Add("日班推演需要「生产任务编制」的当日任务，请先打开一次任务编制窗口或检查引擎接线。");
            return tl;
        }

        if (tasks.Count == 0)
        {
            tl.SourceLabel = "当日无任务";
            tl.Notes.Add("当日盘子里没有任务，日班推演为空。");
            return tl;
        }

        // 去向登记簿是**活引用**（SinkRegistryLoader.Current），逐期扣库容必须在副本上做，
        // 否则推演一次就把台账的已填量改了。
        var reg = (cfg.Sinks != null && cfg.Sinks.All.Count > 0 ? cfg.Sinks : SinkRegistry.Sample()).Clone();

        var shiftNames = cfg.Shifts.OrderBy(s => s.Start).Select(s => s.Name).ToList();
        if (shiftNames.Count == 0)
            shiftNames = tasks.Select(t => t.Shift).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        // ★ 班次名对不上时以**任务**为准，并且必须报出来。
        //
        //   这一段的两个来源是**异步**的：班次名取自现装的盘子（cfg，每次现装），
        //   任务取自缓存的计划（Day()，可能是上一轮编的、也可能是从盘里回读的快照）。
        //   两边的班次名一旦不同，下面 Same() 是严格相等，每一帧都会被 continue 掉 ——
        //   结果是**0 帧、且一个字都不报**，时间轴看着就像"今天没排活"。
        //
        //   这不是假想：班次日历接通时班名是「早班/中班/夜班」，读不到时退回缺省班制「早/中/夜」。
        //   于是「编制时日历接通、演的时候日历读不到」就会静默演出一片空白。
        if (shiftNames.Count > 0 && !tasks.Any(t => shiftNames.Any(n => Same(t.Shift, n))))
        {
            var fromTasks = tasks.Where(t => !string.IsNullOrWhiteSpace(t.Shift))
                                 .GroupBy(t => t.Shift)
                                 .OrderBy(g => g.Min(t => t.StartHour))
                                 .Select(g => g.Key)
                                 .ToList();
            if (fromTasks.Count > 0)
            {
                tl.Notes.Add($"班次名对不上：盘子里是「{string.Join(" / ", shiftNames)}」，任务上写的是「{string.Join(" / ", fromTasks)}」"
                           + " —— 按**任务上的**班次名演。多半是这份计划编制时班次日历还接得通，现在读不到了退回了缺省班制；"
                           + "两边不一致时计划量归属会有出入，建议回「班次日历」确认后重编一次。");
                shiftNames = fromTasks;
            }
        }

        bool actual = track == SimTrack.Actual;
        double cumOre = 0, cumStrip = 0, cumDump = 0, cumWork = 0;
        int idx = 0;

        foreach (var sh in shiftNames)
        {
            var loads = tasks.Where(t => t.Process == ProcessType.Load && Same(t.Shift, sh)).ToList();
            var dumps = tasks.Where(t => t.Process == ProcessType.Dump && Same(t.Shift, sh)).ToList();

            // 本班的**全部**任务（含穿孔/爆破/检修）——它们不搬物料，但它们是这个班在干的活。
            var all = tasks.Where(t => Same(t.Shift, sh)).ToList();

            // ⚠ 判「这个班要不要出帧」看的是**有没有任务**，不是「有没有采装/排土」。
            //   早先按后者判，夜班只排穿孔和检修就被整帧跳过 ——
            //   于是「一天三班」在时间轴上变成两班，而画面上完全看不出少了一班。
            //   一个只有穿孔的班，物料流是空的、推进是零，但它确实存在，必须占一帧。
            if (all.Count == 0) continue;

            string period = sh;
            var flows = new List<MaterialFlow>();
            foreach (var t in loads) flows.AddRange(FlowsOf(t, period, actual));

            // 排土侧（独立台账）：排土任务的量已是占容口径，不再乘 Kr。
            var accepted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in dumps)
            {
                double v = actual ? t.ActualVolumeM3 : t.TargetVolumeM3;
                if (v <= Eps) continue;
                string key = SinkKeyOf(reg, t.DestinationId, t.DestinationName, t.WorkZone);
                if (key.Length == 0) continue;
                accepted[key] = accepted.GetValueOrDefault(key) + v;
            }

            var frame = MakeFrame(idx++, period, $"{SampleTaskBoard.DateLabel}　{sh}", SimGranularity.Shift, track,
                                  flows, accepted, acceptedIsIndependent: dumps.Count > 0,
                                  reg, prm,
                                  expectedWasteInSituM3: flows.Where(f => !f.IsOre).Sum(f => f.InSituM3),
                                  expectedDumpM3: flows.Where(f => !f.IsOre).Sum(f => f.DumpM3),
                                  pairBasis: dumps.Count > 0
                                      ? "采侧＝采装任务按物料 Kr 折出的应排占容；排侧＝排土任务作业量（已是占容口径）"
                                      : "本班无排土任务，排侧按入方推导，配对恒等",
                                  refAdvanceM: 0, estimated: false);

            // ── 本班作业清单（谁在哪个面上干）──
            //  穿孔/爆破/检修不搬物料、没有源汇，不带进来的话，一个只排穿孔的班
            //  在画面上就是一片静止，与「没排班」分不开。
            //  采装/排土也进这张表（见 SimActivity.IsVolumetric）：
            // 信息栏只列非量型，三维层画全部 —— 它回答的是「谁在哪个面上干」，
            // 那件事块体那一层答不了（块体表达的是料的去向，且日班常筛不出本班单元）。
            foreach (var t in all)
            {
                // 作业面自己的坐标：先按工程位置号对，再按面名对。
                // 有它就不必拿名字去套区域轮廓 —— 那三套命名本来就对不上。
                var face = cfg.Faces.FirstOrDefault(f =>
                        f.EngineeringPositionId.Length > 0
                        && string.Equals(f.EngineeringPositionId, t.EngineeringPositionId, StringComparison.OrdinalIgnoreCase))
                    ?? cfg.Faces.FirstOrDefault(f => string.Equals(f.Zone, t.WorkZone, StringComparison.OrdinalIgnoreCase));

                frame.Activities.Add(new SimActivity
                {
                    Process = t.Process,
                    Zone = t.WorkZone,
                    Equipment = t.Group.MainEquipment,
                    StartHour = t.StartHour,
                    EndHour = t.EndHour,
                    Material = t.Material,
                    BenchElevationM = t.BenchElevationM,
                    TaskId = t.Id,
                    EngineeringPositionId = t.EngineeringPositionId,
                    X = face?.SourceX ?? 0,
                    Y = face?.SourceY ?? 0,
                    Z = face is { SourceZ: var sz } && Math.Abs(sz) > 1e-9 ? sz : t.BenchElevationM,
                    HasPosition = face?.HasSourcePosition == true,
                    IsVolumetric = t.Process is ProcessType.Load or ProcessType.Dump,
                    TruckCount = t.Process == ProcessType.Load ? t.Group.Trucks.Count : 0,
                    // 停工原因随任务下沉：中/夜班整班空闲时，「为什么空」是这个窗口必须答的
                    IdleReasons = new List<IncompleteReason>(t.Reasons),
                });
            }

            // 本班涉及的采掘单元号 —— 三维块体靠它筛出「这个班在动哪几个单元」
            frame.UnitIds = all.Select(t => t.UnitId)
                               .Where(u => !string.IsNullOrWhiteSpace(u))
                               .Distinct(StringComparer.Ordinal).ToList();

            frame.Notes.Add("排土任务的作业量是【占容方】（引擎按入方实方×Kr 推导后写回），此处不再乘 Kr。");
            if (actual) frame.Notes.Add("实绩轨：采装用 ActualVolumeM3，排土用排土任务实绩；两侧欠产不同步即为真实的采排偏差。");

            var nonVol = frame.Activities.Where(a => !a.IsVolumetric).ToList();
            if (nonVol.Count > 0)
                frame.Notes.Add($"本班另有 {nonVol.Count} 项非量型作业（"
                              + string.Join("、", nonVol.GroupBy(a => a.Process)
                                    .Select(g => $"{g.Key.Label()}×{g.Count()}"))
                              + "）：它们不搬物料，所以不进剥采比/运输功/库容，但设备确实在这些位置上。");
            if (flows.Count == 0 && accepted.Count == 0)
                frame.Notes.Add("⚠ 本班**没有采装/排土任务** —— 物料流为空、推进为零是真的，"
                              + "不是数据缺失。上面那几项非量型作业就是这个班的全部内容。");

            cumOre += frame.OreWanT; cumStrip += frame.StripWanM3;
            cumDump += frame.DumpedWanM3; cumWork += frame.TransportWorkWanTKm;
            frame.CumOreWanT = cumOre; frame.CumStripWanM3 = cumStrip;
            frame.CumDumpedWanM3 = cumDump; frame.CumTransportWorkWanTKm = cumWork;

            tl.Frames.Add(frame);
        }

        string src = "";
        try { src = SampleTaskBoard.SourceLabel; } catch { }
        tl.SourceLabel = $"日班推演 · {SampleTaskBoard.DateLabel} · {tl.Frames.Count} 班" + (src.Length > 0 ? $"　·　{src}" : "");
        tl.Notes.Add("日班粒度的采排配对取自两套独立台账（采装面 / 排土面），实绩轨下的偏差是真实偏差，不是舍入。");
        tl.Notes.Add("单班对不上是常态：推土机班产与电铲班产本就不等，排土工序滞后于采装。" +
                     "判守恒要看**全期合计**那一行——采出来的每一方岩迟早都得有地方放。");
        return tl;
    }

    // ═════════════════════════ L1 年 / L2 月 ═════════════════════════

    /// <summary>年/月推演：读计划期次 → 摊到面 → 交流向求解器定汇 → 逐期扣库容。</summary>
    private static SimTimeline BuildPeriodic(SimGranularity g, SimTrack track, SimMiningParams prm,
                                             string? onlyPeriod, string? scheme)
    {
        var tl = new SimTimeline { Granularity = g, Track = track, MiningParams = prm };

        // ── 期次骨架 ──
        var periods = g == SimGranularity.Year ? ReadYearPeriods(scheme, tl) : ReadMonthPeriods();

        // ── 只保留指定的那一期（null/空 = 全要）───────────────────────────────
        //
        //  为什么需要它：采掘单元台账目录里可能躺着**两次不同排产**的结果
        //  （实测 2016-08 与 2026-08 各 190+ 行，单元号只有 2 个重合 —— 那是两次排产，
        //   不是相邻两个月）。把它们排成一条时间轴，等于把两个方案混着演，
        //  而画面上完全看不出"这两帧不是同一套方案的前后两期"。
        //  ⇒ 让调用方指定演哪一期；界面上给选择器，默认取最新那一期。
        if (!string.IsNullOrWhiteSpace(onlyPeriod))
        {
            string want = SimPeriodKey.Parse(onlyPeriod);
            if (want.Length > 0)
            {
                var keep = periods.Where(r => SimPeriodKey.Parse(r.Label) == want).ToList();
                if (keep.Count > 0) periods = keep;   // 过滤后空了就不过滤（宁可全给，也不给一条空时间轴）
            }
        }
        if (periods.Count == 0)
        {
            periods = Extrapolate(g, tl);
            tl.Estimated = true;
        }
        if (periods.Count == 0)
        {
            tl.SourceLabel = g == SimGranularity.Year
                ? "无中长远进度计划，也无当日盘子可外推"
                : "无短期月度计划，也无当日盘子可外推";
            tl.Notes.Add("请先在「进度计划」里确定一份方案，或至少保证当日任务盘子可读。");
            return tl;
        }

        // ── 面份额（把期总量摊到面）──
        ExploderConfig? cfg = null;
        try { cfg = SampleTaskBoard.Config(); } catch { }
        var shares = FaceShares(cfg);
        var reg = (cfg?.Sinks != null && cfg.Sinks.All.Count > 0 ? cfg.Sinks : SinkRegistry.Sample()).Clone();

        if (shares.Count == 0)
            tl.Notes.Add("当日盘子不可读，期总量按「全矿采出 / 全矿剥离」两个虚拟源推演，落不到具体作业面。");

        bool actual = track == SimTrack.Actual;
        double cumOre = 0, cumStrip = 0, cumDump = 0, cumWork = 0;
        int idx = 0;

        foreach (var pp in periods)
        {
            // 实绩轨：期次级实绩需要 ProductionRecord 的期聚合，本包尚未接通 → 明确降级，不造数。
            double oreWanT = pp.CoalWanT, stripWanM3 = pp.StripWanM3;
            double oreM3 = OreM3FromWanT(oreWanT);
            double wasteM3 = stripWanM3 * 1e4;

            // ★ 先看这一期在台账里有没有【排产写回的流】——有就直接搬过来演，不再重新分配一遍。
            //   （2026-08-18：在此之前，指标那条链走的是"期总量 → 任务盘子面份额 → 样例卸点重新分配"，
            //    而排产的真实对位关系只用来画线 ⇒ 画的和算的不是一套。）
            var led = LedgerFlowSource.TryBuild(pp.Label);
            if (led != null)
            {
                var ledFrame = MakeFrame(idx++, pp.Label, PeriodLabel(g, pp), g, track,
                                         led.Flows, led.Accepted, acceptedIsIndependent: true,
                                         led.Sinks, prm,
                                         expectedWasteInSituM3: led.WasteInSituM3,
                                         expectedDumpM3: led.ExpectedDumpM3,
                                         pairBasis: "采侧＝排产写回的流（岩）×Kr＝应排占容；排侧＝台账库容真吃得下的部分。"
                                                  + "差额＝本期【库容顶住排不下】的量 —— 分配这一步排产已经做过，这里不再做第二遍",
                                         refAdvanceM: pp.AdvanceM, estimated: tl.Estimated);
                foreach (var n in led.Notes) ledFrame.Notes.Add(n);
                if (pp.Note.Length > 0) ledFrame.Notes.Add(pp.Note);

                cumOre += ledFrame.OreWanT; cumStrip += ledFrame.StripWanM3;
                cumDump += ledFrame.DumpedWanM3; cumWork += ledFrame.TransportWorkWanTKm;
                ledFrame.CumOreWanT = cumOre; ledFrame.CumStripWanM3 = cumStrip;
                ledFrame.CumDumpedWanM3 = cumDump; ledFrame.CumTransportWorkWanTKm = cumWork;
                tl.Frames.Add(ledFrame);
                continue;
            }

            var supplies = BuildSupplies(shares, oreM3, wasteM3, pp.Label);
            double periodHours = Math.Max(1, pp.Workdays) * 24;

            var fp = new FlowProblem
            {
                Supplies = supplies,
                Sinks = reg,
                Period = pp.Label,
                PeriodHours = periodHours,
                CommitToSinks = true,     // 逐期累加库容：本期排满了，下期就得改投别处
            };

            FlowPlan plan;
            try { plan = FlowAssigner.Assign(fp); }
            catch (Exception ex)
            {
                plan = new FlowPlan { Feasible = false, Explain = $"流向分配失败：{Short(ex)}" };
            }

            var accepted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var sl in plan.SinkLoads.Where(s => s.Kind.IsDumping()))
                accepted[sl.SinkId] = accepted.GetValueOrDefault(sl.SinkId) + sl.DumpM3;

            // 采侧「应排」按**计划剥离量**取（不是按已分配的流），否则与排侧同源、校核退化成恒等式。
            double expectedDump = ExpectedDumpOf(supplies);

            var frame = MakeFrame(idx++, pp.Label, PeriodLabel(g, pp), g, track,
                                  plan.Flows, accepted, acceptedIsIndependent: true,
                                  reg, prm,
                                  expectedWasteInSituM3: wasteM3,
                                  expectedDumpM3: expectedDump,
                                  pairBasis: "采侧＝计划剥离量×Kr（需求）；排侧＝求解器实际排下的占容（受库容/卸点能力约束的供给），差额即本期排不下的量",
                                  refAdvanceM: pp.AdvanceM, estimated: tl.Estimated);

            if (!plan.Feasible && plan.Explain.Length > 0) frame.Notes.Add(plan.Explain);
            if (plan.UnassignedInSituM3 > 1)
                frame.Notes.Add($"本期有 {plan.UnassignedInSituM3 / 1e4:0.##}万m³实方无处可排——排土场库容/卸点能力已顶住，推进到此为止。");
            if (pp.AdvanceM > Eps)
                frame.Notes.Add($"计划自带本期推进 {pp.AdvanceM:0.##} m，可与按 V实/(L×H) 反算值互校。");
            if (pp.Note.Length > 0) frame.Notes.Add(pp.Note);
            if (actual)
                frame.Notes.Add("期次级实绩尚未接通（需 ProductionRecord 的年/月聚合），实绩轨在本粒度下与计划轨同数；日班粒度有真实绩。");

            cumOre += frame.OreWanT; cumStrip += frame.StripWanM3;
            cumDump += frame.DumpedWanM3; cumWork += frame.TransportWorkWanTKm;
            frame.CumOreWanT = cumOre; frame.CumStripWanM3 = cumStrip;
            frame.CumDumpedWanM3 = cumDump; frame.CumTransportWorkWanTKm = cumWork;

            tl.Frames.Add(frame);
        }

        if (tl.SourceLabel.Length == 0)
            tl.SourceLabel = $"{g.Label()}推演 · {periods[0].PlanName} · {tl.Frames.Count} 期";

        // 台账不是"本次会话确定过的方案" —— 这句话必须挂出来，别让人以为是刚排的那一版
        if (periods[0].PlanName.Contains("monthly_plan"))
            tl.Notes.Add("◆ 期次来自**月度计划台账**（库表 monthly_plan），不是本次会话确定的方案 —— "
                       + "会话里没有已确定的短期方案时才走这条。台账里没有的月份不会出现在时间轴上；"
                       + "要以最新排产为准，请到「量驱动采剥接续」把方案**确定入库**。");
        // 两条路的口径完全不同，**必须逐帧说清用的是哪一条** —— 数差着三成，看上去却都正常。
        int fromLedger = tl.Frames.Count(f => f.Pair.Basis.Contains("排产写回的流", StringComparison.Ordinal));
        if (fromLedger == tl.Frames.Count && tl.Frames.Count > 0)
            tl.Notes.Add("去向【全部取自采掘单元台账里排产写回的流】（源/汇/运距原样搬运，不重新分配）；"
                       + "库容按台账的库容列逐期累扣，排不下的量如实报。");
        else if (fromLedger > 0)
            tl.Notes.Add($"◆ {fromLedger}/{tl.Frames.Count} 期取自排产写回的流，其余期次台账里没有流，"
                       + "退回「期总量 → 作业面份额 → 卸点表重新分配」那条路 —— 两条路的量不可直接比。");
        else
            tl.Notes.Add("◆ 台账里没有排产写回的流 ⇒ 期次级去向是**重新分配**出来的（min Σ 吨量×等效运距），"
                       + "并逐期把占容方累加回排土场库容。"
                       + "这不是排产的结果：要让画面与指标同源，请在「采掘单元清单」按目标排产后【存为期次】。");
        if (track == SimTrack.Actual)
            tl.Notes.Add("实绩轨提示：期次级实绩未接通，本粒度下双轨同数；要看真实计划/实绩差异请切到「日班」。");
        return tl;
    }

    // ── 期次骨架读取（软接 PlanLib，无编译期依赖）────────────────────────────

    /// <summary>一期计划行（年/月共用的最小事实）。</summary>
    private sealed class PeriodRow
    {
        public string PlanName = "";
        public string Label = "";
        public double CoalWanT;
        public double StripWanM3;
        public double Workdays = 25;
        public double AdvanceM;
        public string Note = "";
    }

    /// <summary>
    /// 读中长远进度计划的逐年期次（经 <see cref="LongTermPlanLink"/> 软接 PlanLib.LongTerm）。
    ///
    /// <para>★ 2026-08-17 修：原来这里只读 <c>LongTermSchemeStore.Confirmed</c>，读不到就返回空表 →
    /// 调用方直接外推。而 <c>Confirmed</c> 是纯内存的、要点过「确定进度计划」才有值 ——
    /// 于是"派生了几套候选、还没点确定"（最常见的状态）走的是**外推**那条路，
    /// 时间轴上出来一串「推算 20xx」，而它们和真年份在界面上长得一样。
    /// 与月度那条路 2026-08-10 修掉的是同一个死路，只是年这边一直没有入口，所以没人撞上。</para>
    ///
    /// <para>现在：已确定 → 综合得分最高的已排产候选 → 第一套；全都没有才交给外推，
    /// 并把"为什么退到这一步"写进 <see cref="SimTimeline.Notes"/>。</para>
    /// </summary>
    private static List<PeriodRow> ReadYearPeriods(string? wantScheme, SimTimeline tl)
    {
        var list = new List<PeriodRow>();
        var all = LongTermPlanLink.ListSchemes();
        if (LongTermPlanLink.LastNote.Length > 0) tl.Notes.Add(LongTermPlanLink.LastNote);

        var s = LongTermPlanLink.Pick(all, wantScheme);
        if (s == null) return list;

        // 方案自己的口径带过来：达产线用它的 A_p，剥采比报警线用它的 n经，都不另取缺省。
        tl.SchemeName = s.Name;
        tl.DesignCapacityWanTa = s.DesignCapacityWanTa;
        tl.EconomicStripRatioMax = s.EconomicStripRatioMax;

        if (s.Years.Count == 0)
        {
            tl.Notes.Add($"中长远方案「{s.Name}」**还没排产**（没有逐年表）—— 回「规划计算」点一次排产即可演。");
            return list;
        }

        foreach (var y in s.Years)
        {
            var row = new PeriodRow
            {
                PlanName = $"中长远方案「{s.Name}」",
                Label = y.Label,
                CoalWanT = y.CoalWanT,
                StripWanM3 = y.StripWanM3,
                AdvanceM = y.AdvanceM,
                Workdays = 300,     // 年期：有效作业日按 12 × 月标准作业日量级（仅用于卸点通过能力折算）
            };
            row.Note = string.Join(" · ", new[] { y.PhaseText, y.DumpText, y.FlagText }.Where(x => x.Length > 0));
            if (row.Label.Length == 0) row.Label = $"第{list.Count + 1}期";
            // 基建年 P=0、只有剥离，也必须留在轴上 —— 那正是"基建期有多长"要看的东西。
            if (row.CoalWanT > Eps || row.StripWanM3 > Eps) list.Add(row);
        }
        return list;
    }

    /// <summary>
    /// 逐月期次：三条来源按优先级，<b>前一条空了才走后一条</b>。
    ///
    /// <para>★ 2026-08-10 修：两条退路原来写在 <see cref="ReadConfirmedMonths"/> 的
    /// <c>if (plan == null) return list;</c> **之后** —— 而 <c>Confirmed == null</c>
    /// 正是它们要处理的那种情况（那个属性是纯内存的，关一次软件就没了）。
    /// 于是退路**一次都没跑过**，时间轴永远退到"从今年 1 月起外推 12 期"，
    /// 冒出一堆台账里根本没有的月份（2026-09、2026-10…），而它们在时间轴上和真月份长得一样。
    /// 典型的死路：代码在、注释在、就是走不到。</para>
    /// </summary>
    private static List<PeriodRow> ReadMonthPeriods()
    {
        // ★ 顺序是**刻意**的：采掘单元台账在第一位。
        //
        //   三维模拟这个窗口的全部内容 —— 块体 / 运输线 / 设备 —— 都以采掘单元台账为数据源。
        //   时间轴若由别的表定（monthly_plan / 已确定方案），两边的月份可以完全不重合：
        //   实测撞过 monthly_plan 给到 2026-05、台账里只有 2026-08，**一个月都不重合** ⇒
        //   拖到哪一帧都是"本期没有采掘单元"，而每条消息各自都对，合起来指错方向。
        //   ⇒ 谁提供内容，谁定时间轴。台账里有几个月，时间轴就几帧。
        //
        //   台账为空时才依次退到方案 / monthly_plan / 外推 —— 那几条给的是"计划想干什么"，
        //   本窗口画不出实体内容，但至少期次与量是真的，并会打上各自的来源标记。
        //   ⚠ 2026-08-18 修正：**已确定的方案优先定轴**。
        //   台账优先那条在"台账只有一个月、而刚确定的是 12 个月"时会把方案整份吃掉 ——
        //   人刚在「短期生产计划编制」确定了一年的计划，点开推演只有一帧，
        //   而来源标签写着"采掘单元台账"，看上去像是计划没确定成功（判据 S14 实测：期望 12 帧、实得 1 帧）。
        //   新口径：**方案定轴、台账供内容** ——
        //     · 轴上的某个月台账里有流 ⇒ 那一帧走台账（源/汇/运距都是排产的结果，见 LedgerFlowSource）；
        //     · 没有 ⇒ 那一帧用方案的期总量走分配那条路，并在帧上说明。
        //   两条都在，谁也不吃掉谁；月份不重合时人看到的是"这几个月还没排产"，而不是"计划没了"。
        var confirmed = ReadConfirmedMonths();
        var ledger = ReadMonthPeriodsFromUnitLedger();
        if (confirmed.Count > 0)
        {
            // 台账里有的月份，用台账那一行的量（它是真排出来的；方案那份是目标）
            var byKey = ledger.ToDictionary(r => SimPeriodKey.Parse(r.Label), r => r, StringComparer.Ordinal);
            var merged = new List<PeriodRow>(confirmed.Count + ledger.Count);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in confirmed)
            {
                string k = SimPeriodKey.Parse(c.Label);
                if (byKey.TryGetValue(k, out var led)) { merged.Add(led); used.Add(k); }
                else merged.Add(c);
            }
            // 台账里有、方案里没有的月份**不进轴**：轴是"这份方案"，塞进去就成了两份计划混演
            //   （量也会对不上契约总量）。但也不能装作没有 —— 名字挂在第一帧的备注里。
            var extra = ledger.Where(l => !used.Contains(SimPeriodKey.Parse(l.Label))).ToList();
            if (extra.Count > 0 && merged.Count > 0)
                merged[0].Note = (merged[0].Note.Length > 0 ? merged[0].Note + "　" : "")
                    + $"◆ 台账里还有 {extra.Count} 个月排过产但不在本方案里："
                    + string.Join("、", extra.Take(6).Select(e => e.Label))
                    + (extra.Count > 6 ? " …" : "")
                    + " —— 轴按方案走，要看那几期请在期次选择器里单独选。";
            return merged;
        }

        var list = ledger;
        if (list.Count == 0) list.AddRange(ReadMonthPeriodsFromLedger());
        return list;
    }

    /// <summary>读【会话内已确定】的短期月度方案（PlanLib.ShortTerm）。没有就返回空表。</summary>
    private static List<PeriodRow> ReadConfirmedMonths()
    {
        var list = new List<PeriodRow>();
        try
        {
            var t = Type.GetType("PitMine3D.Kylin.Cad.Plan.ShortTermSchemeStore, PitMine3D.Kylin");   // Kylin：确定簿在 Cad.Plan（同程序集）；原字符串 "PlanLib.ShortTerm…, PlanLib" 在这里永远解不到 → 推演静默退回外推
            object? plan = t?.GetProperty("Confirmed", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (plan == null) return list;
            string name = S(plan, "Name");
            if (Prop(plan, "Months") is not IEnumerable rows) return list;

            foreach (var r in rows)
            {
                if (r == null) continue;
                var row = new PeriodRow
                {
                    PlanName = $"短期方案「{name}」",
                    Label = S(r, "Label"),
                    CoalWanT = D(r, "CoalWanT"),
                    StripWanM3 = D(r, "StripWanM3"),
                    AdvanceM = D(r, "AdvanceM"),
                    Workdays = D(r, "Workdays"),
                };
                if (row.Workdays <= 0) row.Workdays = 25;
                string flag = S(r, "FlagText");
                string dump = S(r, "DumpText");
                string face = S(r, "ActiveFace");
                row.Note = string.Join(" · ", new[] { flag, dump, face.Length > 0 ? $"主作业面 {face}" : "" }
                                                  .Where(x => x.Length > 0));
                if (row.Label.Length == 0) row.Label = $"第{list.Count + 1}月";
                if (row.CoalWanT > Eps || row.StripWanM3 > Eps) list.Add(row);
            }
        }
        catch { /* 同上 */ }
        return list;
    }

    /// <summary>
    /// 从**采掘单元台账目录**读实际存在的月份（`2026-08.csv` 这种）。
    /// <para>只在前两条路都空时才用。量取该月台账行的合计 —— 那是真的排产结果，
    /// 不是外推。读不到返回空表，调用方据此退到外推并打「推算」标记。</para>
    /// </summary>
    private static List<PeriodRow> ReadMonthPeriodsFromUnitLedger()
    {
        var list = new List<PeriodRow>();
        try
        {
            var store = new PitMine3D.Kylin.UnitLedger.MonthlyUnitLedgerStore();
            foreach (var m in store.ListMonths().OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!store.TryLoad(m, out var rows, out _) || rows.Count == 0) continue;
                double coalT = 0, rockM3 = 0;
                foreach (var r in rows)
                {
                    if (r == null) continue;
                    if (r.Kind == PitMine3D.Kylin.UnitLedger.LedgerKind.Coal) coalT += r.CoalT ?? 0;
                    else if (r.Kind == PitMine3D.Kylin.UnitLedger.LedgerKind.Rock) rockM3 += r.NetRockM3 ?? r.GrossM3 ?? 0;
                }
                if (coalT <= Eps && rockM3 <= Eps) continue;
                list.Add(new PeriodRow
                {
                    PlanName = "采掘单元台账（按月）",
                    Label = m,
                    CoalWanT = coalT / 1e4,
                    StripWanM3 = rockM3 / 1e4,
                    Workdays = 25,
                    Note = $"{rows.Count} 个单元",
                });
            }
        }
        catch { /* 台账目录不在 → 空表 */ }
        return list;
    }

    /// <summary>
    /// 从 <c>monthly_plan</c> 月度计划台账读逐月期次。<b>只在会话内没有确定方案时才用</b>。
    /// <para>读不到（表空 / 库没接通）返回空表，调用方据此如实说明 —— 不编期次。</para>
    /// </summary>
    private static List<PeriodRow> ReadMonthPeriodsFromLedger()
    {
        var list = new List<PeriodRow>();
        try
        {
            var rows = PitMine3D.Kylin.Data.EquipmentDataContext.Plan.All();
            if (rows == null || rows.Count == 0) return list;

            foreach (var m in rows.OrderBy(x => x.Year).ThenBy(x => x.Month))
            {
                // 自营 + 外委都是本矿要完成的剥离量；只算自营会让达成率虚高（与 ShortTermLink 同口径）
                double strip = m.PlanStripWanM3 + m.PlanOutsourceStripWanM3;
                if (!(m.PlanCoalWanT > Eps) && !(strip > Eps)) continue;
                list.Add(new PeriodRow
                {
                    PlanName = "月度计划台账 monthly_plan",
                    Label = $"{m.Year}-{m.Month:00}",
                    CoalWanT = m.PlanCoalWanT,
                    StripWanM3 = strip,
                    Workdays = 25,          // 台账没有作业日，与 ShortTermLink 的缺省一致
                    Note = m.PlanOutsourceStripWanM3 > Eps
                        ? $"含外委剥离 {m.PlanOutsourceStripWanM3:0.#}万m³"
                        : "",
                });
            }
        }
        catch { /* 库没接通 → 空表，调用方如实说明 */ }
        return list;
    }

    /// <summary>
    /// 没有计划时的**外推期次**：以当日盘子为速率、按有效作业日等速外推。
    /// 结果一律打 <see cref="SimTimeline.Estimated"/> 标记，期标签前缀「推算」，界面必须挂横幅。
    /// 这不是造数——它是「按今天这个干法一直干下去会怎样」的显式外推，输入全部来自真实盘子。
    /// </summary>
    private static List<PeriodRow> Extrapolate(SimGranularity g, SimTimeline tl)
    {
        var list = new List<PeriodRow>();
        ExploderConfig cfg;
        try { cfg = SampleTaskBoard.Config(); }
        catch { return list; }

        double dayOreM3 = 0, dayWasteM3 = 0;
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > Eps))
            foreach (var (spec, m3) in f.ResolvedMix.Split(f.DayTargetM3))
            {
                if (spec.IsOre) dayOreM3 += m3; else dayWasteM3 += m3;
            }
        if (dayOreM3 <= Eps && dayWasteM3 <= Eps) return list;

        double workdays = 25;
        int startMonth = 1, startYear = DateTime.Now.Year;
        try
        {
            var mi = ShortTermLink.GetMonthInfo(SampleTaskBoard.DateLabel);
            if (mi.Workdays > 0) workdays = mi.Workdays;
        }
        catch { }
        var token = SampleTaskBoard.DateLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parts = token.Split('-');
        if (parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) startYear = y;
        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m0) && m0 is >= 1 and <= 12)
            startMonth = m0;

        double coalDensity = MaterialCatalog.Resolve(MaterialCatalog.Coal).InSituDensityTPerM3;
        int n = g == SimGranularity.Year ? EstimatedYearCount : EstimatedMonthCount;

        for (int i = 0; i < n; i++)
        {
            double days = g == SimGranularity.Year ? workdays * 12 : workdays;
            int month = ((startMonth - 1 + i) % 12) + 1;
            int year = startYear + (startMonth - 1 + i) / 12;
            list.Add(new PeriodRow
            {
                PlanName = "推算期次（未确定进度计划）",
                Label = g == SimGranularity.Year ? $"推算 {year}" : $"推算 {year}-{month:00}",
                CoalWanT = dayOreM3 * days * coalDensity / 1e4,
                StripWanM3 = dayWasteM3 * days / 1e4,
                Workdays = days,
                Note = $"按当日盘子（采出 {dayOreM3 / 1e4:0.####}万m³实方 / 剥离 {dayWasteM3 / 1e4:0.####}万m³）× 有效作业日 {days:0.#} 等速外推",
            });
        }

        tl.SourceLabel = g == SimGranularity.Year
            ? $"年推演（推算）· 未确定中长远进度计划，按当日盘子等速外推 {list.Count} 期"
            : $"月推演（推算）· 未确定短期月度方案，按当日盘子等速外推 {list.Count} 期";
        tl.Notes.Add("⚠ 本时间轴为**外推估算**：期次不是计划实数，只是「按今天这个干法一直干下去」的机制演示。" +
                     "在「进度计划」里确定方案后自动切到真计划。");
        return list;
    }

    // ── 面份额与供给 ─────────────────────────────────────────────────────────

    /// <summary>一个采装面在期总量里占的份额（煤/岩各一份），并记住它的物料码。</summary>
    private sealed class FaceShare
    {
        public string SourceId = "";
        public string SourceName = "";
        public double BenchElevationM;
        public double OreFrac;          // 占全矿煤的份额
        public double WasteFrac;        // 占全矿岩的份额
        public string OreCode = MaterialCatalog.Coal;
        public string WasteCode = MaterialCatalog.Rock;
        public string MaterialCaption = "";
    }

    /// <summary>按当日盘子里各采装面的煤/岩产出算份额（份额归一，总量恒守恒）。</summary>
    private static List<FaceShare> FaceShares(ExploderConfig? cfg)
    {
        var list = new List<FaceShare>();
        if (cfg == null) return list;

        double totOre = 0, totWaste = 0;
        var raw = new List<(FaceShare S, double Ore, double Waste)>();
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > Eps))
        {
            double ore = 0, waste = 0;
            string oreCode = "", wasteCode = "";
            double oreMax = 0, wasteMax = 0;
            foreach (var (spec, m3) in f.ResolvedMix.Split(f.DayTargetM3))
            {
                if (spec.IsOre) { ore += m3; if (m3 > oreMax) { oreMax = m3; oreCode = spec.Code; } }
                else { waste += m3; if (m3 > wasteMax) { wasteMax = m3; wasteCode = spec.Code; } }
            }
            var fs = new FaceShare
            {
                SourceId = string.IsNullOrWhiteSpace(f.EngineeringPositionId) ? f.Zone : f.EngineeringPositionId,
                SourceName = f.Zone,
                BenchElevationM = f.BenchElevationM,
                OreCode = oreCode.Length > 0 ? oreCode : MaterialCatalog.Coal,
                WasteCode = wasteCode.Length > 0 ? wasteCode : MaterialCatalog.Rock,
                MaterialCaption = f.ResolvedMix.Caption,
            };
            raw.Add((fs, ore, waste));
            totOre += ore; totWaste += waste;
        }

        foreach (var (s, ore, waste) in raw)
        {
            s.OreFrac = totOre > Eps ? ore / totOre : 0;
            s.WasteFrac = totWaste > Eps ? waste / totWaste : 0;
            if (s.OreFrac > Eps || s.WasteFrac > Eps) list.Add(s);
        }
        return list;
    }

    /// <summary>把期总量（煤 m³实方 / 岩 m³实方）摊到面 → 待分配供给。</summary>
    private static List<FlowSupply> BuildSupplies(List<FaceShare> shares, double oreM3, double wasteM3, string period)
    {
        var sup = new List<FlowSupply>();
        if (shares.Count == 0)
        {
            if (oreM3 > Eps)
                sup.Add(new FlowSupply { SourceId = "ALL-ORE", SourceName = "全矿采出", MaterialCode = MaterialCatalog.Coal, InSituM3 = oreM3 });
            if (wasteM3 > Eps)
                sup.Add(new FlowSupply { SourceId = "ALL-STRIP", SourceName = "全矿剥离", MaterialCode = MaterialCatalog.Rock, InSituM3 = wasteM3 });
            return sup;
        }

        foreach (var s in shares)
        {
            double o = oreM3 * s.OreFrac, w = wasteM3 * s.WasteFrac;
            if (o > Eps)
                sup.Add(new FlowSupply
                {
                    SourceId = s.SourceId, SourceName = s.SourceName, BenchElevationM = s.BenchElevationM,
                    MaterialCode = s.OreCode, InSituM3 = o,
                });
            if (w > Eps)
                sup.Add(new FlowSupply
                {
                    SourceId = s.SourceId, SourceName = s.SourceName, BenchElevationM = s.BenchElevationM,
                    MaterialCode = s.WasteCode, InSituM3 = w,
                });
        }
        return sup;
    }

    /// <summary>供给里非矿部分的应排占容方（逐物料经 Kr 折算，不写死系数）。</summary>
    private static double ExpectedDumpOf(List<FlowSupply> supplies)
        => supplies.Where(s => !s.Spec.IsOre).Sum(s => s.Spec.ToDumpM3(s.InSituM3));

    private static double OreM3FromWanT(double wanT)
    {
        double rho = MaterialCatalog.Resolve(MaterialCatalog.Coal).InSituDensityTPerM3;
        return rho <= 0.1 ? 0 : wanT * 1e4 / rho;
    }

    // ── 帧装配 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 把「本期流 + 本期各汇承接量」装成一帧，并就地扣掉库容（推进到下一期）。
    /// </summary>
    /// <param name="acceptedIsIndependent">
    /// 排侧是否来自独立台账。false ⇒ 采排两侧同源，配对校核记「无法校核」而不是「通过」。
    /// </param>
    private static SimFrame MakeFrame(
        int index, string period, string label, SimGranularity g, SimTrack track,
        List<MaterialFlow> flows, Dictionary<string, double> accepted, bool acceptedIsIndependent,
        SinkRegistry reg, SimMiningParams prm,
        double expectedWasteInSituM3, double expectedDumpM3, string pairBasis,
        double refAdvanceM, bool estimated)
    {
        var frame = new SimFrame
        {
            Index = index, Period = period, Label = label,
            Granularity = g, Track = track, Estimated = estimated,
            Balance = new PeriodBalance { Period = period, Flows = flows.ToList() },
        };

        // ── 源侧：按 SourceId 聚合 + 采场推进反算 ──
        foreach (var grp in flows.GroupBy(f => f.SourceId))
        {
            var any = grp.First();
            var step = new SimSourceStep
            {
                SourceId = grp.Key,
                SourceName = any.SourceName,
                BenchElevationM = any.SourceBenchElevationM,
                MaterialCaption = string.Join("＋", grp.Select(f => f.Spec.Name).Distinct()),
                InSituM3 = grp.Sum(f => f.InSituM3),
                OreInSituM3 = grp.Where(f => f.IsOre).Sum(f => f.InSituM3),
                TonnageT = grp.Sum(f => f.TonnageT),
                LooseM3 = grp.Sum(f => f.LooseM3),
                WasteDumpM3 = grp.Where(f => !f.IsOre).Sum(f => f.DumpM3),
                BenchHeightM = prm.BenchHeightM,
                WorkLineLengthM = prm.WorkLineLengthM,
                RefAdvanceM = refAdvanceM,
            };
            // v = V实 /(L×H)。参数没解出来时给 NaN，界面显示「—」，绝不拿缺省值冒充工程量。
            step.AdvanceM = prm.Resolved ? prm.AdvanceMetersFor(step.InSituM3) : double.NaN;
            frame.Sources.Add(step);
        }
        frame.Sources = frame.Sources.OrderByDescending(s => s.InSituM3).ToList();

        // ── 汇侧：入方 + 承接 + 库容 + 排土推进反算 ──
        var sinkIds = new List<string>();
        foreach (var f in flows) if (!sinkIds.Contains(f.SinkId, StringComparer.OrdinalIgnoreCase)) sinkIds.Add(f.SinkId);
        foreach (var k in accepted.Keys) if (!sinkIds.Contains(k, StringComparer.OrdinalIgnoreCase)) sinkIds.Add(k);

        foreach (var id in sinkIds)
        {
            var node = reg.Find(id);
            var inbound = flows.Where(f => Same(f.SinkId, id)).ToList();
            var anyFlow = inbound.FirstOrDefault();

            var kind = node?.Kind ?? anyFlow?.SinkKind ?? SinkKind.ExternalDump;
            bool dumping = kind.IsDumping();

            double inboundDump = inbound.Sum(f => f.DumpM3);
            bool hasOwn = accepted.TryGetValue(id, out double own);
            double acceptedDump = dumping ? (hasOwn ? own : inboundDump) : 0;

            var step = new SimSinkStep
            {
                SinkId = id,
                SinkName = node?.Name ?? anyFlow?.SinkName ?? id,
                Kind = kind,
                IsDumping = dumping,
                InboundInSituM3 = inbound.Sum(f => f.InSituM3),
                InboundDumpM3 = inboundDump,
                InboundTonnageT = inbound.Sum(f => f.TonnageT),
                AcceptedDumpM3 = acceptedDump,
                AcceptedFromInbound = dumping && !hasOwn,
                BenchHeightM = node?.BenchHeightM ?? 0,
                WorkLineLengthM = node?.WorkLineLengthM ?? 0,
                DesignCapacityM3 = node?.DesignCapacityM3 ?? 0,
                CapacityLimited = node?.IsCapacityLimited ?? false,
            };

            if (node != null)
            {
                step.RemainingBeforeM3 = node.RemainingM3;
                // d = V容 /(L排 × h排)——SinkNode 已实现，直接用。
                step.AdvanceM = dumping && node.WorkLineLengthM > Eps && node.BenchHeightM > Eps
                    ? node.AdvanceMetersFor(acceptedDump)
                    : double.NaN;
            }

            // 扣库容（推进到下一期）。破碎站/煤仓是通过型去向，不占库容，不扣。
            if (node != null && dumping && acceptedDump > Eps) reg.AddFilled(node.Id, acceptedDump);

            if (node != null)
            {
                step.RemainingAfterM3 = node.RemainingM3;
                step.FillRateAfter = node.FillRate;
                step.Overflow = node.IsCapacityLimited && node.RemainingM3 <= Eps;
            }

            frame.Sinks.Add(step);
        }
        frame.Sinks = frame.Sinks.OrderByDescending(s => s.IsDumping).ThenByDescending(s => s.AcceptedDumpM3).ToList();

        // ── 采排配对校核 ──
        double dumpedTotal = frame.Sinks.Where(s => s.IsDumping).Sum(s => s.AcceptedDumpM3);
        bool anyOwn = frame.Sinks.Any(s => s.IsDumping && !s.AcceptedFromInbound);
        frame.Pair = new SimPairCheck
        {
            MinedWasteInSituM3 = expectedWasteInSituM3,
            ExpectedDumpM3 = expectedDumpM3,
            DumpedM3 = dumpedTotal,
            Basis = pairBasis,
        };
        if (acceptedIsIndependent && anyOwn) frame.Pair.Grade();
        else frame.Pair.Level = SimCheckLevel.NotAvailable;

        // 哪些汇是「按入方推导」的：这部分在配对等式两侧同源、恒等，必须点名，
        // 否则看客会把一个恒等式当成校核通过。
        var derived = frame.Sinks.Where(s => s.IsDumping && s.AcceptedFromInbound && s.AcceptedDumpM3 > Eps)
                                 .Select(s => s.SinkName).ToList();
        if (derived.Count > 0 && anyOwn)
            frame.Notes.Add($"{string.Join("、", derived)} 无独立排土台账，其占容按入方推导——这部分在配对等式两侧同源、恒等，" +
                            $"真正被校核的是有独立排土作业的那几个场。");

        // 库容告警
        foreach (var s in frame.Sinks.Where(s => s.Overflow))
            frame.Notes.Add($"⚠ {s.SinkName} 本期库容排穿（期末剩余 0），后续期次必须改投或扩容。");
        foreach (var s in frame.Sinks.Where(s => !s.Overflow && s.NearFull))
            frame.Notes.Add($"{s.SinkName} 期末充填率 {s.FillRateAfter * 100:0.#}%，接近见顶。");

        return frame;
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    /// <summary>任务 → 物料流（计划轨用目标量，实绩轨用实绩量）。</summary>
    private static IEnumerable<MaterialFlow> FlowsOf(ProductionTask t, string period, bool actual)
    {
        double vol = actual ? t.ActualVolumeM3 : t.TargetVolumeM3;
        if (vol <= Eps) yield break;

        foreach (var (spec, m3) in t.ResolvedMix.Split(vol))
        {
            if (m3 <= Eps) continue;
            yield return new MaterialFlow
            {
                Period = period,
                SourceId = string.IsNullOrWhiteSpace(t.EngineeringPositionId) ? t.WorkZone : t.EngineeringPositionId,
                SourceName = t.WorkZone,
                SourceBenchElevationM = t.BenchElevationM,
                MaterialCode = spec.Code,
                InSituM3 = m3,
                SinkId = t.DestinationId,
                SinkName = string.IsNullOrWhiteSpace(t.DestinationName) ? t.DestinationId : t.DestinationName,
                SinkKind = t.DestinationKind,
                HaulKm = t.HaulDistanceKm,
                EquivHaulKm = t.EquivHaulKm,
            };
        }
    }

    /// <summary>排土任务认领哪个汇：先 Id、再去向名、最后拿作业区名去登记簿里对。</summary>
    private static string SinkKeyOf(SinkRegistry reg, string? id, string? name, string? zone)
    {
        if (!string.IsNullOrWhiteSpace(id) && reg.Find(id) != null) return id!;
        foreach (var cand in new[] { name, zone })
        {
            if (string.IsNullOrWhiteSpace(cand)) continue;
            var hit = reg.All.FirstOrDefault(s => Same(s.Name, cand) || Same(s.Id, cand));
            if (hit != null) return hit.Id;
        }
        return id ?? name ?? zone ?? "";
    }

    private static string PeriodLabel(SimGranularity g, PeriodRow p)
        => g == SimGranularity.Year ? $"{p.Label} 年" : $"{p.Label} 月";

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static object? Prop(object o, string p)
    {
        try { return o.GetType().GetProperty(p)?.GetValue(o); }
        catch { return null; }
    }
    private static double D(object o, string p) => Prop(o, p) is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : 0;
    private static string S(object o, string p) => Prop(o, p)?.ToString() ?? "";

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 70 ? m : m[..70] + "…";
    }
}
