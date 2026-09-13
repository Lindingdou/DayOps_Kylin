// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/TaskExploder.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

namespace PitMine3D.Kylin.TaskLib.Engine;

/// <summary>
/// 裂解装箱引擎：把各作业面的当日目标量，按班次有效时窗 × 编组班产 装箱成班次任务，
/// 备采用尽则该班空闲、当日能力不足则报欠产（守恒回摊），并校核 运力/设备双占/工序接续。
/// 产物 = ProductionTask[]（计划）+ PlanViolation[]（校核）。实绩回灌是另一步。
/// </summary>
public static class TaskExploder
{
    public static ExploderResult Explode(ExploderConfig cfg)
    {
        var res = new ExploderResult();

        // 天气降效是全盘性的（所有面、所有班一起降），故在这里报一次，不逐面重复
        if (cfg.WeatherDeratePct > 0.01 || cfg.HasLinkDerate)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = ViolationCodes.WeatherDerate,
                Message = cfg.HasLinkDerate
                    ? $"按环节降效：采装 {cfg.LoadDeratePct ?? cfg.WeatherDeratePct:0.#}% · "
                    + $"运输 {cfg.HaulDeratePct ?? cfg.WeatherDeratePct:0.#}% · "
                    + $"排土 {cfg.DumpDeratePct ?? cfg.WeatherDeratePct:0.#}%"
                    + "（逐面按其瓶颈侧折算：运输降效对采装瓶颈的面不生效）"
                    + "，当日干不完的量按「当日欠产」回摊"
                    : $"全盘按天气降效 {cfg.WeatherDeratePct:0.#}%（能力系数 {cfg.WeatherFactor:0.00}）"
                    + "，当日干不完的量按「当日欠产」回摊",
            });

        // 分环节降效但某些面没有周期分解 ⇒ 它们只能退回全盘值。这件事必须说出来：
        // 否则"我把运输降了 30%，怎么这几个面一点没变"永远解释不清（而每一步都不报错）。
        if (cfg.HasLinkDerate)
        {
            var blind = cfg.Faces.Where(f => f.Process == ProcessType.Load && !f.Group.HasCycleBreakdown)
                                 .Select(f => f.Zone).ToList();
            if (blind.Count > 0)
                res.Violations.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Warn, Code = ViolationCodes.WeatherDerate,
                    Message = $"{blind.Count} 个采装面没有编组周期分解（τ_L/T_c），分不了环节，"
                            + $"只能按全盘 {cfg.WeatherDeratePct:0.#}% 降：{string.Join("、", blind.Take(6))}"
                            + (blind.Count > 6 ? " 等" : "")
                            + "。补法：在「编制配置」里确认这些面已解出编组（运距解得出来才有 T_c）",
                });
        }

        // 作业组织策略先于配煤：策略是**偏好**（集中/展开），配煤是**约束**（灰分必须达标）。
        // 顺序反了就是让偏好去推翻约束——集中强采可能把量全灌到高灰面上，配煤再也救不回来。
        ApplyWorkOrganization(cfg, res);

        // 配煤约束先于装箱：把采出量在面间重分配，使综合配煤达标（保总量不变、受面日产能约束）
        ApplyBlendConstraint(cfg, res);

        // 采排守恒先于装箱：排土面的日目标由采装面入方推导（配煤已改过采装量，故必须排在配煤之后）
        DeriveDumpTargets(cfg, res);

        foreach (var face in cfg.Faces)
            ExplodeFace(cfg, face, res);

        // 穿孔任务
        foreach (var d in cfg.Drills)
            res.Tasks.Add(new ProductionTask
            {
                Id = $"{cfg.IdPrefix}-{d.EquipId}-穿",
                Process = ProcessType.Drill,
                Group = new EquipmentGroup { MainEquipment = d.EquipId },
                WorkZone = d.Zone, BenchElevationM = d.BenchElevationM, Material = d.Zone,
                Shift = ShiftOf(cfg, d.Start), StartHour = d.Start, EndHour = d.End, PlannedHours = Math.Round(d.End - d.Start, 1),
                Status = TaskStatus.Planned,
                // 量随任务下沉（与采装面的去向/物料同理）：面上有、任务上没有，这条链就断在装箱这一步
                Drill = d.HoleCount is > 0 || d.HoleLengthM is > 0
                    ? new DrillQuantity { PlanHoles = d.HoleCount, PlanMeters = d.HoleLengthM }
                    : null,
            });

        // 检修任务（Idle）
        foreach (var mw in cfg.Maintenance)
            res.Tasks.Add(new ProductionTask
            {
                Id = $"{cfg.IdPrefix}-{mw.EquipId}-检",
                Process = ProcessType.Idle,
                Group = new EquipmentGroup { MainEquipment = mw.EquipId },
                Material = mw.Label, Shift = ShiftOf(cfg, mw.Start),
                StartHour = mw.Start, EndHour = mw.End, Status = TaskStatus.Planned,
                Reasons = new() { IncompleteReason.Maintenance },
            });

        CheckBlastSegmentation(cfg, res);        // 爆破清场把班切成几段、放弃了多少小时
        CheckConstraints(cfg, res);
        SpaceTimeValidator.Validate(cfg, res);   // 露天矿特有的采排时空约束（内排启用时机/台阶承载/表土专场/时窗）
        return res;
    }

    private static void ExplodeFace(ExploderConfig cfg, FaceInput face, ExploderResult res)
    {
        // 降效落在能力上：班产 × 天气系数。时窗不动（那是检修/爆破/交接班的地盘，见 WorkWindow）
        // 系数按**面**取（WeatherFactorFor）：分环节降效时，运力瓶颈的面与采装瓶颈的面吃到的不一样。
        double cap = Math.Max(1e-6, face.Group.GroupCapacityM3PerH * cfg.WeatherFactorFor(face));

        // 运力校核（采装编组）
        if (face.Process == ProcessType.Load && face.Group.Trucks.Count < face.Group.RecommendedTrucks)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "运力不足",
                Message = $"{face.Zone} {face.Group.MainEquipment} 配 {face.Group.Trucks.Count} 车 < 荐 {face.Group.RecommendedTrucks}，铲将待车",
            });

        CheckPreparedReserve(cfg, face, res);

        // ── 日目标在三班之间怎么摊 ──────────────────────────────────────────────
        //
        //  ★ 原先是**按班次顺序贪心填满**：vol = min(班产×可用工时, 剩余量)，剩余清零后
        //    后面的班一律挂"空闲"。后果是只要一个面的日目标装得下一个班，这个面当天就只在
        //    早班有活 —— 实测样例盘子：早班挖 7746 m³、中班 1354、夜班 **0**，七台设备全空。
        //    三班连续作业的矿不会这么干，这样的计划发下去中班夜班的人来了没活。
        //
        //  改为**按各班可用能力占比摊**：cap_i = 班产 × 本班可用工时（已扣检修/爆破/交接），
        //    日目标 ≥ Σcap_i 时逐班填满、余量报欠产（与原来一致）；
        //    日目标 < Σcap_i 时按 cap_i 占比分，三个班都有活、各自干得少些。
        //
        //  这样"空闲"就只剩它本来的含义：这个面今天真的没量（备采用尽），
        //  而不是"前面的班替它干完了" —— 后者原先挂的是 OreShortage（欠料·采空），
        //  归因错不说，动态调整还会据此路由到「换面」策略，为一个不存在的问题换面。
        var slots = new List<(ShiftWindow Sh, double Ws, double We, double Cap)>();
        foreach (var sh in cfg.Shifts)
        {
            var (ws0, we0) = WorkWindow(cfg, face.Group.MainEquipment, sh);
            double avail0 = Math.Max(0, we0 - ws0);
            slots.Add((sh, ws0, we0, avail0 >= 0.5 ? cap * avail0 : 0));
        }

        double totalCap = slots.Sum(s => s.Cap);
        double target = Math.Max(0, face.DayTargetM3);
        var quota = new double[slots.Count];
        if (totalCap > 1e-6 && target > 1)
        {
            bool full = target >= totalCap;
            for (int i = 0; i < slots.Count; i++)
                quota[i] = full ? slots[i].Cap : target * slots[i].Cap / totalCap;

            // 摊完的零头补给能力最大的那个班，保证 Σquota 与日目标严格相等（不许摊丢方量）
            if (!full)
            {
                int big = 0;
                for (int i = 1; i < slots.Count; i++) if (slots[i].Cap > slots[big].Cap) big = i;
                quota[big] += target - quota.Sum();
            }
        }

        double remaining = target - quota.Sum();   // 只有"日目标 > 全天能力"时才 > 0
        for (int i = 0; i < slots.Count; i++)
        {
            var (sh, ws, we, capI) = slots[i];
            double avail = Math.Max(0, we - ws);

            if (quota[i] <= 1)
            {
                // 本班没量：可用时窗还在（capI>0）才算"空闲"，时窗被检修/爆破吃光的不记空闲条
                if (capI > 0)
                    res.Tasks.Add(IdleTask(cfg, face, ws, we, sh, "空闲", IncompleteReason.OreShortage));
                continue;
            }

            double vol = quota[i];
            double hours = vol / cap;
            double end = ws + hours;

            // ── 量放进**这道工序自己那本账**（2026-08-22 修）──────────────────────
            //
            //  `TargetVolumeM3` 的口径写死在 ProductionTask 上：「m³ 原位实方 —— **只有采装笔用它**」。
            //  而这里原来是无条件写 `TargetVolumeM3 = Math.Round(vol)`，**不看 face.Process** ——
            //  于是排土面装出来的 51 笔 Dump 任务，把 `DayTargetM3` 里的**排弃占容方**
            //  （FlowAssigner / DeriveDumpTargets 明写「日目标 = 本期投向该汇的**占容方**」）
            //  塞进了实方那一列。同一个数于是被下游按实方再算一遍：
            //    · `TargetDumpM3 = ToDumpM3(TargetVolumeM3)` ⇒ **Kr 又乘了一次**（≈+13%），
            //      `DayStagePlan.VolumeOf` 与任务书都走这条；
            //    · `OreVolumeM3` / `WasteVolumeM3` / `ToFlows()` ⇒ 排土笔当成又产了一批料，
            //      而这批料采装笔已经记过一遍 —— **剥离量与运输功凭空多一份**；
            //    · `AttainmentPct` 拿实绩实方去除占容方。
            //  每一处单看都自洽，只有口径是错的。判据 PL1 就是为这件事写的。
            //
            //  ⚠ 只分 Dump 这一档，是因为**只有它有确证的单位**（上面那两处都写着"占容"）。
            //    穿孔（控制方量）/ 运输（承运吨）现在都不从 cfg.Faces 装箱 —— 真出现了
            //    那种面，PL 组会当场红，那时再按它自己那本账分，别在这里凭猜先写一支。
            bool isDumpFace = face.Process == ProcessType.Dump;
            double qty = Math.Round(vol);

            var t = new ProductionTask
            {
                Id = $"{cfg.IdPrefix}-{face.Group.MainEquipment}-{ShiftShort(sh.Name)}",
                Process = face.Process, Group = Clone(face.Group),
                WorkZone = face.Zone, BenchElevationM = face.BenchElevationM, EngineeringPositionId = face.EngineeringPositionId,
                UnitId = face.UnitId,   // 空间身份随任务下沉：任务书写它、图上按它定位、实绩按它核销备采
                Material = face.Material, QualityTarget = face.Quality,
                TargetVolumeM3 = isDumpFace ? 0 : qty,       // 原位实方：只有采装笔用
                DumpVolumeM3 = isDumpFace ? qty : 0,         // 排弃占容方：排土笔自己那本账
                Shift = sh.Name, StartHour = Math.Round(ws, 2), EndHour = Math.Round(end, 2), PlannedHours = Math.Round(hours, 1),
                Status = TaskStatus.Planned,

                // 去向与物料随任务下沉：任务书上要写「拉到哪」，甘特/报表要按它算运输功与配车数，
                // 实绩回灌后还要按它扣排土库容——面上有、任务上没有，这条链就断在装箱这一步。
                DestinationId = face.DestinationId, DestinationName = face.DestinationName,
                DestinationKind = face.DestinationKind,
                HaulDistanceKm = face.HaulDistanceKm, EquivHaulKm = face.EquivHaulKm,
                MaterialCode = face.MaterialCode, Mix = face.Mix,

                // 分项去向同样下沉，且必须**深拷贝**：一个面裂成三个班次任务，
                // 共用同一批 MaterialDestination 实例的话，任一班次改去向会把另外两班一起改掉。
                Splits = face.Splits.Select(s => s.Clone()).ToList(),
            };

            res.Tasks.Add(t);
        }

        // 当日能力不足 → 欠产回摊
        if (remaining > 1)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "当日欠产",
                Message = $"{face.Zone} 当日能力不足，欠 {remaining:0} m³ 需回摊次日",
            });
    }

    /// <summary>
    /// 配煤约束进装箱：综合灰分超标时，把采出量从高灰面移到低灰面（保总采装量不变、不超面日产能），
    /// 使按量加权的综合灰分 ≤ 上限；移得平→记「配煤调整」，移不平(低灰面产能见顶)→记「配煤不达标」。
    /// 装箱前调用，调整后的 DayTargetM3 流入逐面装箱。CV/硫 同理（此处以灰分为约束驱动）。
    ///
    /// <para><b>★ 按受矿点分组判，不是全矿混一堆</b>：
    /// 配煤本来就是按受矿点成立的 —— 两个破碎站各有各的合同指标，把两边的煤混在一起算一个
    /// "全矿综合灰分"没有物理含义，那堆煤永远不会真的混到一起。更要命的是<b>掺配动作</b>：
    /// 把量从"送 A 站的高灰面"移到"送 B 站的低灰面"，A 站的灰分一点没改善，却凭空改了两站的到货量。
    /// 所以量只在**同一受矿点内部**移动，各组各判各的标准
    /// （<see cref="SinkNode.MaxAshPct"/> 等三项，未给则落回全矿级 <see cref="ExploderConfig.Blend"/>）。</para>
    ///
    /// <para>全矿只有一个受矿点、或一个去向都没填时只会分出一组，行为与老口径逐位相同。</para>
    /// </summary>
    private static void ApplyBlendConstraint(ExploderConfig cfg, ExploderResult res)
    {
        if (cfg.Blend is not { } mine) return;

        // ★ 候选面 = 有煤质目标 且（今天有量 **或** 有产能）。
        //
        //   原先的筛法是 `DayTargetM3 > 0`，把「今天暂不采、但有铲有产能」的低灰面排除在外。
        //   在均衡型下这看不出问题（各面都有量）；接上作业组织策略后立刻暴露：
        //   集中强采会把其它面清零，候选面只剩一个，`loads.Count < 2` 直接返回 ——
        //   **策略把约束需要的信息毁掉了**，说好的"配煤能推翻策略"根本没发生（判据 O6 抓到的就是这个）。
        //
        //   零量的低灰面本来就是合法的掺配对象：现场碰上综合灰分超标，做法正是"开一个低灰面掺进来"。
        //   它只做**收方**不做**出方**（出方在下面按"手里真有量"筛），所以不会凭空造量。
        var loads = cfg.Faces.Where(f => f.Process == ProcessType.Load && f.Quality != null
                                      && (f.DayTargetM3 > 0 || f.Group.GroupCapacityM3PerH > 1e-6)).ToList();
        if (loads.Count < 2) return;   // 单面无从掺配

        foreach (var grp in loads.GroupBy(f => OreSinkKey(cfg, f)))
        {
            SinkNode? sink = null;
            if (!string.IsNullOrEmpty(grp.Key))
            {
                try
                {
                    sink = cfg.Sinks.Find(grp.Key)
                        ?? cfg.Sinks.All.FirstOrDefault(x => string.Equals(x.Name, grp.Key, StringComparison.OrdinalIgnoreCase));
                }
                catch { sink = null; }
            }
            string label = sink?.Name ?? (string.IsNullOrEmpty(grp.Key) ? "去向未定" : grp.Key);
            BlendOneSink(cfg, res, grp.ToList(), mine, sink, label, loads.Count);
        }
    }

    /// <summary>
    /// 一个作业面的**煤**去哪（分项去向里第一个矿石物料的汇）。空串 = 去向未定 / 纯岩面。
    /// <para>用 max(日目标,1) 去拆份额：零量的低灰面也要归到它该去的那个站，
    /// 否则它会被甩进"去向未定"组，掺配时永远够不着 —— 而它正是最该被掺进来的那个面。</para>
    /// </summary>
    private static string OreSinkKey(ExploderConfig cfg, FaceInput f)
    {
        foreach (var (spec, _) in f.ResolvedMix.Split(Math.Max(f.DayTargetM3, 1)))
        {
            if (!spec.IsOre) continue;
            var d = f.DestinationFor(spec.Code);
            if (!string.IsNullOrWhiteSpace(d.DestinationId)) return d.DestinationId;
            if (!string.IsNullOrWhiteSpace(d.DestinationName)) return d.DestinationName;
        }
        return "";
    }

    /// <summary>一个受矿点内部的掺配。量只在组内移动，判的是这个点自己的标准。</summary>
    private static void BlendOneSink(ExploderConfig cfg, ExploderResult res, List<FaceInput> loads,
                                     BlendStandard mine, SinkNode? sink, string label, int totalFaces)
    {
        // 该点自己的上限，缺则落回全矿级。灰分是驱动量（CV/硫同理，此处以灰分驱动）。
        double maxAsh = sink?.MaxAshPct ?? mine.MaxAshPct;
        string where = totalFaces == loads.Count ? "" : $"【{label}】";
        string basis = sink?.MaxAshPct.HasValue == true ? "本点标准" : "全矿级缺省";

        if (loads.Count < 2)
        {
            // 组内只有一个面 ⇒ 无从掺配。这件事必须报出来：分组之后"配煤没动"有两种原因，
            // 达标是一种，孤面是另一种；混在一起会让人以为这个点已经判过并且通过了。
            double solo = loads[0].Quality!.AshPct;
            bool bad = solo > maxAsh + 0.01;
            res.Violations.Add(new PlanViolation
            {
                Severity = bad ? ViolationSeverity.Warn : ViolationSeverity.Info,
                Code = bad ? ViolationCodes.BlendFailed : ViolationCodes.BlendOk,
                Message = $"{where}只有 1 个采装面（{loads[0].Zone} 灰分 {solo:0.0}%），无从掺配"
                        + $"；上限 {maxAsh:0.0}%（{basis}）"
                        + (bad ? " —— 需另开一个低灰面掺进来 / 调整该点合同指标" : ""),
            });
            return;
        }

        double Blend() { double q = loads.Sum(f => f.DayTargetM3); return q > 1e-6 ? loads.Sum(f => f.DayTargetM3 * f.Quality!.AshPct) / q : 0; }

        // 收方上限 = min(面日产能, 备采储量)，与作业组织策略同一个天花板（两处口径必须一致）：
        //  · 产能吃天气降效，且**按面取**（分环节降效时各面系数不同）——否则配煤会把量移到
        //    一个装箱时根本装不下的面上（移得平、装不下，白移一场）；
        //  · 备采**未录（≤0）时不参与限制**——未录 ≠ 0，拿 0 当上限会让没录备采的低灰面永远收不了料。
        double Cap(FaceInput f)
        {
            double byCap = f.Group.GroupCapacityM3PerH * cfg.WeatherFactorFor(f) * cfg.EffHoursPerDay;
            return f.AvailableReserveM3 > 1e-6 ? Math.Min(byCap, f.AvailableReserveM3) : byCap;
        }

        double before = Blend();
        if (before <= maxAsh + 0.01)
        {
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Info, Code = ViolationCodes.BlendOk,
                Message = $"{where}综合灰分 {before:0.0}% ≤ 上限 {maxAsh:0.0}%（{basis}）",
            });
            return;
        }

        var orig = loads.ToDictionary(f => f, f => f.DayTargetM3);
        for (int guard = 0; guard < 200 && Blend() > maxAsh; guard++)
        {
            // 出方只能在**手里真有量**的面里挑：候选集现在含零量面（它们只做收方），
            // 若仍按全体挑最高灰，挑中一个零量面就会 step≤0 当场 break，整轮掺配停在第一步。
            var hi = loads.Where(f => f.DayTargetM3 > 1).OrderByDescending(f => f.Quality!.AshPct).FirstOrDefault();
            if (hi == null) break;
            var lo = loads.Where(f => f != hi).OrderBy(f => f.Quality!.AshPct).FirstOrDefault();
            if (lo == null) break;
            double step = Math.Min(Math.Min(Cap(lo) - lo.DayTargetM3, hi.DayTargetM3), 50);
            if (step <= 1) break;     // 低灰面见顶 / 高灰面见底 → 移不动
            hi.DayTargetM3 -= step;
            lo.DayTargetM3 += step;
        }
        foreach (var f in loads) f.DayTargetM3 = Math.Round(f.DayTargetM3);
        double after = Blend();

        string moves = string.Join("、", loads.Where(f => Math.Abs(f.DayTargetM3 - orig[f]) >= 1)
            .Select(f => $"{f.Zone} {orig[f]:0}→{f.DayTargetM3:0}"));
        if (after <= maxAsh + 0.05)
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = ViolationCodes.BlendAdjusted, Message = $"{where}综合灰分 {before:0.00}→{after:0.00}% 达标（上限 {maxAsh:0.0}，{basis}）：{moves}" });
        else
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Warn, Code = ViolationCodes.BlendFailed, Message = $"{where}综合灰分 {after:0.00}% 仍 > 上限 {maxAsh:0.0}%（{basis}，低灰面产能见顶）：需掺低灰煤 / 调采区 / 放宽月配煤指标" });
    }


    // ─────────────────────────────────────────────────────────────────────────
    //  采—排守恒
    //
    //  口径差（本节唯一必须记牢的事）：采装面 DayTargetM3 是原位【实方】，
    //  排土面 DayTargetM3 是排弃【占容方】——推土机平整的是排上去的松散料沉降稳定后的体积，
    //  V容 = V实 × Kr（岩 1.10~1.20）。两侧数值本就不该相等，直接相减对账必错，
    //  必须先把采装侧逐物料折成占容方再汇总。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 排土面的当日目标由采装面入方推导。排土场不是与采装面平级的独立作业面——它排多少，
    /// 完全取决于上游采出多少；两边各填各的，才会出现「采装 6300、排土 4000 而系统不报错」。
    /// DerivedFromInbound=true 者按分项账推导；手填者不覆盖但必须对账；最后再算一次全矿总账。
    ///
    /// ── 与 FlowAssigner 的口径裁决（两处都在推导排土面日目标）──
    /// FlowAssigner 按求解得到的分项账（SinkLoad.DumpM3）先写过一次，本函数再按面级视图重算一遍
    /// 并覆盖，两处口径不同就会互相打架。**裁决：FlowAssigner（分项账）优先。**
    /// 落地做法是把本函数的入方也统一到 <see cref="ExploderConfig.Flows"/> 这一个真相源上
    /// （它现已按 Splits 逐物料取各自去向），并在推导值与上游已有值一致（相对差 &lt;1%）时
    /// **只对账不覆盖**；不一致才覆盖并记 Info 说明来源。这样两处算的是同一笔账，不会来回改写。
    /// </summary>
    private static void DeriveDumpTargets(ExploderConfig cfg, ExploderResult res)
    {
        // ① 采装面 → 各排土场的当日入方（占容方）。唯一真相源 = cfg.Flows()：
        //    它按 Splits 逐物料取各自去向，混采面的岩不会再被记到煤的破碎站上（那正是次要物料去向丢失的病灶）。
        //    煤/矿去破碎站与煤仓，不占排土库容，故只累非矿；SinkId/SinkName 皆空的流是"去向未定"，
        //    不能凭 SinkKind 的默认值（ExternalDump）把它当成外排。
        var inbound = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var fl in cfg.Flows())
        {
            if (fl.IsOre || !fl.SinkKind.IsDumping()) continue;
            if (string.IsNullOrWhiteSpace(fl.SinkId) && string.IsNullOrWhiteSpace(fl.SinkName)) continue;
            if (fl.InSituM3 <= 1e-6) continue;
            string k = SinkKeyOf(cfg, fl.SinkId, fl.SinkName);
            inbound[k] = inbound.GetValueOrDefault(k) + fl.DumpM3;
        }

        var dumps = cfg.Faces.Where(f => f.Process == ProcessType.Dump).ToList();
        // 采装面一个去向都没填 = 调用方还没接线，此时"入方 0"是接线问题不是计划问题，降为 Info 免得刷屏。
        bool anyRouted = cfg.Faces.Any(f => f.Process == ProcessType.Load && AnyRouted(f));

        foreach (var f in dumps)
        {
            // 排土面认领哪个场：先 DestinationId，再去向名，最后拿作业区名去登记簿里对
            //（历史盘子里排土面只有 Zone="内排场" 这一个线索）。
            string key = SinkKeyOf(cfg, f.DestinationId, f.DestinationName, f.Zone);
            double got = inbound.GetValueOrDefault(key), manual = f.DayTargetM3;
            string name = SinkNameOf(cfg, key, f.Zone);

            if (f.DerivedFromInbound)
            {
                if (got > 1)
                {
                    double diff = Math.Abs(manual - got) / got;
                    if (manual > 1 && diff < 0.01)
                        // 上游（FlowAssigner）已按同一笔分项账算好：只对账、不覆盖，避免两处推导来回改写。
                        Flag(res, ViolationSeverity.Info, ViolationCodes.MassImbalance,
                            $"{f.Zone} 日目标 {manual:0} m³占容 与按分项物料流推导的入方 {got:0} 一致（差 {diff * 100:0.##}%），"
                            + "系上游流向分配按同一笔分项账算出，本次只对账不覆盖");
                    else
                    {
                        f.DayTargetM3 = Math.Round(got);
                        Flag(res, ViolationSeverity.Info, ViolationCodes.MassImbalance,
                            $"{f.Zone} 日目标按分项物料流推导 {got:0} m³占容（原填 {manual:0}）：逐物料各按自己的去向汇总"
                            + "（混采面的岩计入排土场、煤计入破碎站），由非矿实方经残余膨胀 Kr 折算而来，采排自动守恒"
                            + (manual > 1
                                ? $"；与上游流向分配给出的 {manual:0} 差 {diff * 100:0.#}%，以本次分项账为准"
                                  + "（上游求解之后，配煤约束/月计划摊派可能又改过采装面的日目标）"
                                : ""));
                    }
                }
                else if (manual > 1)
                {
                    // 分项账里查无入方、但目标已是正数 ⇒ 上游求解时该汇曾接过料（如后被库容顶回改投他处）。
                    // 归零会凭空抹掉一台推土机的活，故保留上游值，只提示口径出处待核。
                    Flag(res, ViolationSeverity.Info, ViolationCodes.MassImbalance,
                        $"{f.Zone} 保留上游流向分配给出的 {manual:0} m³占容：按分项物料流重算无采装面卸向{name}"
                        + "（多半是求解时该汇被库容/通过能力顶回改投他处），未按当前物料流归零，请核对卸点归属");
                }
                else
                {
                    f.DayTargetM3 = 0;
                    Flag(res, anyRouted ? ViolationSeverity.Warn : ViolationSeverity.Info, ViolationCodes.MassImbalance,
                        $"{f.Zone} 按入方推导当日无料可排（无采装面卸向{name}），日目标 0："
                        + (anyRouted ? "本场今日不接料（如内排已接完全部剥离），推土机可另作他用或核对卸点归属" : "采装面尚未指定卸点（去向未接线）"));
                }
            }
            else if (cfg.EnforceMassBalance && (manual > 1 || got > 1))
            {
                // 手填排土量：不覆盖（现场可能另有安排），但必须与入方对账——差得多说明采、排两侧在各说各话。
                // 覆盖是语义（面上勾了「按入方推导」就得算），对账才是校核，故只有对账受 EnforceMassBalance 管。
                if (got <= 1)
                    Flag(res, anyRouted ? ViolationSeverity.Warn : ViolationSeverity.Info, ViolationCodes.MassImbalance,
                        $"{f.Zone} 手填 {manual:0} m³占容，但{name}当日入方为 0"
                        + (anyRouted ? "（无采装面卸向此处）：请给采装面指定卸点，或把本面改为「按入方推导」" : "（采装面尚未指定卸点，去向未接线）"));
                else if (Math.Abs(manual - got) / got > 0.05)
                    Flag(res, ViolationSeverity.Warn, ViolationCodes.MassImbalance,
                        $"{f.Zone} 采排不守恒：入方 {got:0} m³占容 vs 手填 {manual:0} m³占容，差 {Math.Abs(manual - got) / got * 100:0.#}%（>5%，手填"
                        + (manual > got ? "偏多→排土能力空转" : "偏少→入方排不下将压车") + "）；建议改为「按入方推导」");
            }
        }

        // ② 全矿总账：剥离出来的每一方都得有地方排。
        //    已"有家"的 = 排土面接住的 + 虽投向排弃去向但当日无排土面接续的（那种只是没排推土任务，料是有去处的）；
        //    剩下的才是真没家：去向未定，或投向了破碎站/煤仓这类根本不吃岩的点。
        var claimed = new HashSet<string>(dumps.Select(d => SinkKeyOf(cfg, d.DestinationId, d.DestinationName, d.Zone)), StringComparer.OrdinalIgnoreCase);
        double wasteAll = cfg.Faces.Where(f => f.Process == ProcessType.Load).Sum(f => f.WasteDumpM3);
        double homed = dumps.Sum(d => d.DayTargetM3) + inbound.Where(kv => !claimed.Contains(kv.Key)).Sum(kv => kv.Value);
        double orphan = wasteAll - homed;

        if (cfg.EnforceMassBalance && wasteAll > 1 && orphan / wasteAll > 0.05)
        {
            // 归因也走分项账：混采面的岩现在有自己的去向，"去向未定/投错点"要落到那一条分项上，
            // 而不是把整个面的剥离量一股脑算进去。
            var waste = cfg.Flows().Where(fl => !fl.IsOre && fl.InSituM3 > 1e-6).ToList();
            double unrouted = waste
                .Where(fl => string.IsNullOrWhiteSpace(fl.SinkId) && string.IsNullOrWhiteSpace(fl.SinkName))
                .Sum(fl => fl.DumpM3);
            double misrouted = waste
                .Where(fl => (!string.IsNullOrWhiteSpace(fl.SinkId) || !string.IsNullOrWhiteSpace(fl.SinkName)) && !fl.SinkKind.IsDumping())
                .Sum(fl => fl.DumpM3);
            Flag(res, ViolationSeverity.Warn, ViolationCodes.MassImbalance,
                $"全矿采排总账不平：采装侧非矿 {wasteAll:0} m³占容，落到排弃去向的仅 {homed:0} m³，尚有 {orphan:0} m³（{orphan / wasteAll * 100:0.#}%）无处可排"
                + (unrouted > 1 ? $"；其中去向未定 {unrouted:0} m³（混采面须逐物料都定去向）" : "")
                + (misrouted > 1 ? $"；投向非排弃点（破碎站/煤仓不吃岩）{misrouted:0} m³——请在作业面的物料分项上改投排土场" : "")
                + (dumps.Count == 0 ? "；当日无排土作业面" : ""));
        }
    }

    /// <summary>本面是否有任何一种物料已定去向（分项或主去向）。</summary>
    private static bool AnyRouted(FaceInput f)
        => f.HasDestination || f.Splits.Any(s => s.HasDestination);

    /// <summary>本面尚未定去向的物料名（混采面逐项判；全都定了则返回空表）。</summary>
    private static List<string> UnroutedMaterials(FaceInput f)
        => f.ResolvedMix.Normalized().Shares
            .Where(s => s.Fraction > 1e-6 && !f.DestinationFor(s.MaterialCode).HasDestination)
            .Select(s => MaterialCatalog.Resolve(s.MaterialCode).Name)
            .Distinct().ToList();

    /// <summary>
    /// 采—运—排校核（受 cfg.EnforceMassBalance 控制）：去向定没定、料能不能进、场子开不开、
    /// 库容够不够、卸点吃不吃得下、运距有没有——全部落在「面—物料—汇」这条最小事实上。
    /// </summary>
    private static void CheckFlowConstraints(ExploderConfig cfg, ExploderResult res)
    {
        if (!cfg.EnforceMassBalance) return;

        // 登记簿没接线时整段跳过：此时面上必然也没去向，逐面报「去向未定」只是刷屏，说清怎么接即可。
        if (cfg.Sinks.All.Count == 0)
        {
            Flag(res, ViolationSeverity.Info, ViolationCodes.NoDestination,
                "未装载去向登记簿（cfg.Sinks 为空）：物料兼容 / 排土库容 / 卸点能力 / 运距 四项校核本次跳过；接线方式 cfg.Sinks = SinkRegistryLoader.Current");
            return;
        }

        // ① 去向未定：没有汇就算不出运输循环时间 T_c，最优配车数 n* 只能拍脑袋。
        //    判据是 AllMaterialsRouted 而非 HasDestination——混采面只要有一种物料没去向就该报，
        //    否则"煤定了破碎站、岩没人管"这种面会因为主去向非空而静默通过。
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > 1 && !f.AllMaterialsRouted))
        {
            var miss = UnroutedMaterials(f);
            string who = miss.Count == 0 ? "" : string.Join("、", miss);
            bool partial = f.ResolvedMix.Normalized().Shares.Count(s => s.Fraction > 1e-6) > miss.Count;
            Flag(res, ViolationSeverity.Warn, ViolationCodes.NoDestination,
                partial
                    ? $"{f.Zone} 混采面尚有物料未定去向：{who} 无处可去（本面 {f.DayTargetM3:0} m³ 实方 · {f.ResolvedMix.Caption}）；"
                      + "一条任务可以有多个去向，请在物料分项上逐项指定，勿只填主去向"
                    : $"{f.Zone} 未指定卸点：当日 {f.DayTargetM3:0} m³ 实方（{f.ResolvedMix.Caption}）无处可去，"
                      + "运距/编组/运输功均无法核算；请在作业面上选定排土场或破碎站");
        }

        var bal = cfg.Balance();
        var routed = bal.Flows.Where(x => !string.IsNullOrWhiteSpace(x.SinkId) || !string.IsNullOrWhiteSpace(x.SinkName)).ToList();

        // ② 物料不兼容：逐面**逐分项**报（整改动作落在各自的分项上，汇总报没法改）。
        //    每种物料现在都有自己的去向，所以不再有"主物料 Error、次物料降级 Warn"这回事——
        //    分项被拒就是实打实配错了（表土进岩石排土场、煤进排土场、岩进破碎站）。
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > 1))
        {
            var mix = f.ResolvedMix.Normalized();
            foreach (var sh in mix.Shares)
            {
                double m3 = f.DayTargetM3 * sh.Fraction;
                if (m3 <= 1e-6) continue;

                var d = f.DestinationFor(sh.MaterialCode);
                if (!d.HasDestination) continue;                      // 已由 ① 报过
                var sink = cfg.Sinks.Find(SinkKeyOf(cfg, d.DestinationId, d.DestinationName));
                if (sink == null || sink.Accepts(d.Spec)) continue;

                Flag(res, ViolationSeverity.Error, ViolationCodes.MaterialRejected,
                    $"{f.Zone} 的{d.Spec.Name}不得进{sink.Name}（{sink.Kind.Label()}）：{RejectReason(d.Spec, sink)}；"
                    + $"当日 {m3:0} m³ 实方（占本面 {sh.Fraction * 100:0.#}%）须改投 {SuggestSinks(cfg, d.Spec)}"
                    + (f.HasSplits ? "" : "（本面未写物料分项，这一份是跟着主去向走的——混采面须逐物料定汇）"));
            }
        }

        // ③~⑤ 按汇汇总：库容与通过能力本就是汇级别的量，逐面报既刷屏又算不对
        foreach (var g in routed.GroupBy(x => x.SinkId, StringComparer.OrdinalIgnoreCase))
        {
            var sink = cfg.Sinks.Find(g.Key);
            double m3 = g.Sum(x => x.InSituM3);
            if (sink == null)
            {
                Flag(res, ViolationSeverity.Warn, ViolationCodes.NoDestination,
                    $"去向「{(string.IsNullOrWhiteSpace(g.First().SinkName) ? g.Key : g.First().SinkName)}」不在去向登记簿中：{Sources(g)} 当日 {m3:0} m³ 的库容与通过能力无从校核；请先在排土场/装卸点台账登记");
                continue;
            }

            // ③ 去向不可用：封场/排满的场子还往里排，等于当日无处可卸
            if (!sink.IsActive)
                Flag(res, ViolationSeverity.Error, ViolationCodes.SinkClosed,
                    $"{sink.Caption} 状态「{sink.Status}」不接收：{Sources(g)} 当日 {m3:0} m³ 将无处可卸；请改投 {SuggestSinks(cfg, g.First().Spec, sink.Id)} 或先解除"
                    + (string.Equals(sink.Status, "full", StringComparison.OrdinalIgnoreCase) ? "满库（抬升台阶 / 扩容）" : "封场"));

            // ④ 排土库容：按【占容方】扣，不是按实方扣
            if (sink.IsDumping && sink.IsCapacityLimited)
            {
                double need = bal.DumpM3At(sink.Id), left = sink.RemainingM3;
                if (need > left)
                    Flag(res, ViolationSeverity.Error, ViolationCodes.DumpCapacity,
                        $"{sink.Name} 库容不足：当日需排 {need / 1e4:0.###} 万m³占容 > 余 {left / 1e4:0.###} 万m³（已填 {sink.FillRate * 100:0.#}%），缺口 {(need - left) / 1e4:0.###} 万m³；请分流至 {SuggestSinks(cfg, g.First().Spec, sink.Id)} 或抬升排土台阶");
                else if (left > 0 && need > left * 0.8)
                    Flag(res, ViolationSeverity.Warn, ViolationCodes.DumpCapacity,
                        $"{sink.Name} 临近满库：当日 {need / 1e4:0.###} 万m³占容将用掉剩余库容的 {need / left * 100:0.#}%，排后仅余 {(left - need) / 1e4:0.###} 万m³；请提前准备接续排土场");
            }

            // ⑤ 卸点通过能力：有效小时取日有效工时，并受该点开放时窗约束（取小者）
            double hours = Math.Min(cfg.EffHoursPerDay, sink.OpenToHour - sink.OpenFromHour > 0 ? sink.OpenToHour - sink.OpenFromHour : 24);
            double capT = sink.ThroughputCapT(hours), gotT = bal.TonnageAt(sink.Id);
            if (!double.IsInfinity(capT) && gotT > capT)
                Flag(res, ViolationSeverity.Warn, ViolationCodes.SinkThroughput,
                    $"{sink.Name} 通过能力不足：当日接收 {gotT / 1e4:0.###} 万t > 能力 {capT / 1e4:0.###} 万t（{sink.AcceptTph:0} t/h × 有效 {hours:0.#} h），超 {(gotT - capT) / 1e4:0.###} 万t；卡车将在卸点排队，建议增开卸点 / 延长开放时窗 / 分流至 {SuggestSinks(cfg, g.First().Spec, sink.Id)}");
        }

        // ⑥ 运距缺失：运距是循环时间的主项，缺了配车数与运输功都只能按兜底值估。
        //    同样**逐分项**判：混采面煤走 2.6km、岩走 1.4km 是两条独立的运输腿，
        //    只看主去向的运距会把"岩那条腿没运距"这件事整条盖掉。
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > 1))
        {
            foreach (var sh in f.ResolvedMix.Normalized().Shares)
            {
                if (sh.Fraction <= 1e-6) continue;
                var d = f.DestinationFor(sh.MaterialCode);
                if (!d.HasDestination || d.EffectiveHaulKm > 1e-6) continue;

                var sink = cfg.Sinks.Find(SinkKeyOf(cfg, d.DestinationId, d.DestinationName));
                Flag(res, ViolationSeverity.Info, ViolationCodes.HaulMissing,
                    $"{f.Zone} 的{d.Spec.Name} → {(string.IsNullOrWhiteSpace(d.DestinationName) ? d.DestinationId : d.DestinationName)} 未给运距，"
                    + $"将回落该点兜底运距 {(sink?.FallbackHaulKm ?? 2.5):0.##} km；配车数与运输功按此估算，建议接路网或手填实测运距");
            }
        }
    }

    /// <summary>不兼容的理由——要说清「为什么不行」，不能只说「不满足」。</summary>
    private static string RejectReason(MaterialSpec spec, SinkNode sink)
    {
        if (spec.Kind == MaterialKind.Topsoil) return "表土是复垦资源，混入岩石排土场后再也回取不出来，须单独堆存于表土堆场";
        if (spec.IsOre && sink.IsDumping) return "煤/矿排进排土场等于把采出量当废石丢掉";
        if (sink.AcceptedMaterials.Count > 0) return $"该点白名单只接纳 {string.Join("、", sink.AcceptedMaterials.Select(c => MaterialCatalog.Resolve(c).Name))}";
        return $"{spec.Name}的允许去向为 {string.Join("、", spec.AllowedSinks.Select(k => k.Label()))}";
    }

    /// <summary>可行去向建议：登记簿里在用且还有库容的，最多列 3 个；一个都没有则退而给出允许的去向类型。</summary>
    private static string SuggestSinks(ExploderConfig cfg, MaterialSpec spec, string? exceptId = null)
    {
        var names = cfg.Sinks.CandidatesFor(spec).Where(s => !Same(s.Id, exceptId)).Take(3).Select(s => s.Name).ToList();
        if (names.Count > 0) return string.Join(" / ", names);
        return spec.AllowedSinks.Count > 0
            ? $"{string.Join(" / ", spec.AllowedSinks.Select(k => k.Label()))}（登记簿中暂无可用点）"
            : "其他可用去向";
    }

    private static string Sources(IEnumerable<MaterialFlow> flows)
        => string.Join("、", flows.Select(x => x.SourceName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

    /// <summary>
    /// 把面上的去向线索（Id / 去向名 / 作业区名）归一成登记簿里的汇 Id——
    /// 采装侧填 Id、排土侧只有名字这种半接线状态很常见，两侧不归一到同一个键就永远对不上账。
    /// 全都对不上则回落首个非空线索本身作键（至少同名的两侧还能配上对）。
    /// </summary>
    private static string SinkKeyOf(ExploderConfig cfg, params string?[] hints)
    {
        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            var s = cfg.Sinks.Find(h.Trim()) ?? cfg.Sinks.All.FirstOrDefault(x => Same(x.Name, h));
            if (s != null) return s.Id;
        }
        return hints.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h))?.Trim() ?? "";
    }

    private static string SinkNameOf(ExploderConfig cfg, string key, string fallback)
        => cfg.Sinks.Find(key)?.Caption ?? (string.IsNullOrWhiteSpace(key) ? fallback : key);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>记一条校核。</summary>
    private static void Flag(ExploderResult res, ViolationSeverity sev, string code, string msg, string taskId = "")
        => res.Violations.Add(new PlanViolation { Severity = sev, Code = code, Message = msg, TaskId = taskId });

    /// <summary>
    /// 作业组织策略（BR-P4）：同样的日总量，摊在几个面上。
    ///
    /// <para>
    /// <b>总量恒守恒</b>——策略只重分配，动不了月计划定的日采出。做法是把所有采装面的
    /// 当日目标收成一个池子，再按策略重新灌回去：
    /// </para>
    /// <list type="bullet">
    /// <item><b>集中强采</b>：按面日产能从大到小依次灌满（灌到该面的上限为止），能少开面就少开；</item>
    /// <item><b>多面展开</b>：按面日产能等比例摊，让每个面都在干；</item>
    /// <item><b>均衡</b>：直接返回，一个字节都不动。</item>
    /// </list>
    /// <para>
    /// 每个面的上限 = min(面日产能, 备采储量)。<b>备采未录（≤0）时不参与限制</b>——
    /// 未录不等于 0，拿 0 当上限会让所有没录备采的面都分不到量，全矿的活挤到唯一录过的那个面上。
    /// </para>
    /// <para>
    /// 灌不完的余量（所有面都到顶了）如实留在池子里并报出来：那说明**今天的目标本来就排不下**，
    /// 悄悄把它塞回某个面等于伪造一个装得下的计划，装箱那一步照样会报欠产，但原因就查不清了。
    /// </para>
    /// </summary>
    private static void ApplyWorkOrganization(ExploderConfig cfg, ExploderResult res)
    {
        if (cfg.Organization == WorkOrganization.Balanced) return;

        var loads = cfg.Faces.Where(f => f.Process == ProcessType.Load && f.Group.GroupCapacityM3PerH > 1e-6).ToList();
        if (loads.Count < 2) return;   // 单面无从组织

        double pool = loads.Sum(f => f.DayTargetM3);
        if (pool <= 1e-6) return;

        double effHours = cfg.EffHoursPerDay;
        double Ceiling(FaceInput f)
        {
            double byCap = f.Group.GroupCapacityM3PerH * cfg.WeatherFactorFor(f) * effHours;
            // 备采未录（≤0）不参与限制：未录 ≠ 0
            return f.AvailableReserveM3 > 1e-6 ? Math.Min(byCap, f.AvailableReserveM3) : byCap;
        }

        var before = loads.ToDictionary(f => f, f => f.DayTargetM3);
        foreach (var f in loads) f.DayTargetM3 = 0;

        double left = pool;
        if (cfg.Organization == WorkOrganization.Concentrated)
        {
            foreach (var f in loads.OrderByDescending(f => f.Group.GroupCapacityM3PerH))
            {
                if (left <= 1e-6) break;
                double give = Math.Min(Ceiling(f), left);
                f.DayTargetM3 = Math.Round(give);
                left -= give;
            }
        }
        else   // MultiFace：按产能等比例摊，超上限的削平后把余量再摊给还有余地的面
        {
            var remaining = loads.ToList();
            while (left > 1e-6 && remaining.Count > 0)
            {
                double capSum = remaining.Sum(f => f.Group.GroupCapacityM3PerH);
                if (capSum <= 1e-6) break;

                double distributed = 0;
                var filled = new List<FaceInput>();
                foreach (var f in remaining)
                {
                    double want = left * (f.Group.GroupCapacityM3PerH / capSum);
                    double room = Math.Max(0, Ceiling(f) - f.DayTargetM3);
                    double give = Math.Min(want, room);
                    f.DayTargetM3 += give;
                    distributed += give;
                    if (room - give <= 1e-6) filled.Add(f);
                }
                left -= distributed;
                if (distributed <= 1e-6) break;              // 谁都灌不进去了
                foreach (var f in filled) remaining.Remove(f);
            }
            foreach (var f in loads) f.DayTargetM3 = Math.Round(f.DayTargetM3);
        }

        int moved = loads.Count(f => Math.Abs(f.DayTargetM3 - before[f]) > 1);
        int active = loads.Count(f => f.DayTargetM3 > 1e-6);
        string name = cfg.Organization == WorkOrganization.Concentrated ? "集中强采" : "多面展开";

        Flag(res, ViolationSeverity.Info, ViolationCodes.WorkOrg,
            $"作业组织按「{name}」重分配：{moved} 个面的当日目标被改写，本日开 {active}/{loads.Count} 个面（总量不变）");

        if (left > 1)
            Flag(res, ViolationSeverity.Warn, ViolationCodes.WorkOrg,
                $"「{name}」灌不下 {left:0} m³ —— 所有面都到了产能或备采上限，"
              + "说明当日目标本身就排不下（不塞回任一面，免得掩盖原因）");
    }

    /// <summary>
    /// 备采家底校核 —— 采准三量落到日计划上的那一条。
    ///
    /// <para>
    /// 装箱里原有的「备采用尽 → 该班空闲(<see cref="IncompleteReason.OreShortage"/>)」判的是
    /// <b>当日目标</b>用尽，那只说明今天的活干完了；而"这个面脚下还剩多少、还能采几天"
    /// 要的是 <see cref="FaceInput.AvailableReserveM3"/>。两者不是一回事，此前后者根本没有数。
    /// </para>
    /// <para>
    /// 三条，逐级：<br/>
    ///  ① <b>当日目标已经超过全部备采</b> —— 今天就采空，Error：这不是预警，是计划本身不成立；<br/>
    ///  ② <b>剩余不足保有下限</b>（人工锚点 <see cref="ExploderConfig.MinPreparedDays"/>，0=不校核）——
    ///     Warn，采准（穿孔/爆破）该往前赶了；<br/>
    ///  ③ <b>没录备采储量</b> —— Info，如实说这个面的三量判不了，<b>不猜一个数</b>。
    /// </para>
    /// <para>
    /// ★ 只对采装面判：排土面的"备采"是库容，那是 <c>SinkNode.RemainingM3</c> 的事，
    /// 由 <see cref="SpaceTimeValidator"/> 与去向登记簿另行校核，两套口径不许混。
    /// </para>
    /// </summary>
    private static void CheckPreparedReserve(ExploderConfig cfg, FaceInput face, ExploderResult res)
    {
        if (face.Process != ProcessType.Load) return;
        if (face.DayTargetM3 <= 1e-6) return;   // 今天不采这个面，无从判起

        if (face.AvailableReserveM3 <= 1e-6)
        {
            Flag(res, ViolationSeverity.Info, ViolationCodes.PreparedReserve,
                $"{face.Zone} 未录备采储量，采准保有天数判不了（在「作业面台账」补「备采(m³)」一列）");
            return;
        }

        double days = face.PreparedDays ?? 0;

        if (face.AvailableReserveM3 < face.DayTargetM3 - 1e-6)
        {
            Flag(res, ViolationSeverity.Error, ViolationCodes.PreparedReserve,
                $"{face.Zone} 备采 {face.AvailableReserveM3:0} m³ < 当日目标 {face.DayTargetM3:0} m³ —— "
              + $"今天就会采空（可采 {days:0.0} 天）。请切面或先补采准，本面当日目标不成立");
            return;
        }

        if (cfg.MinPreparedDays > 1e-6 && days < cfg.MinPreparedDays)
            Flag(res, ViolationSeverity.Warn, ViolationCodes.PreparedReserve,
                $"{face.Zone} 备采仅够 {days:0.0} 天 < 保有下限 {cfg.MinPreparedDays:0.#} 天 —— "
              + "采准（穿孔/爆破）需前赶，否则该面将断档停采");
    }

    /// <summary>
    /// 某设备某班的有效作业时窗：扣检修（班首）+ <b>全部</b>爆破清场 + 交接班损失。
    ///
    /// <para>
    /// <b>爆破从"一炮"改成"逐炮"（2026-08-11）</b>：原实现只认 <c>cfg.BlastStart</c>（装配层取最早一炮），
    /// 于是一天三炮时后两炮在装箱里根本不存在——那几个时段计划仍在满负荷作业。现在按
    /// <see cref="ExploderConfig.BlastWindows"/> 把班切段。
    /// </para>
    /// <para>
    /// <b>为什么只取最长的一段，而不是把每一段都排上</b>：一条 (面 × 班) 只能出<b>一条</b>任务——
    /// 任务 Id 是「前缀-主设备-班次」，实绩/下达的稳定键是 (日期,班次,主设备,工序,作业区)，
    /// 同班两条任务会撞 Id、撞稳定键，还会被判「设备双占」。这是结构约束，不是偷懒。
    /// 被放弃的那些段有多少小时，由 <see cref="CheckBlastSegmentation"/> 逐班如实报出来，
    /// 不闷声吞掉（吞掉的话，计划看着"排满了"，实际少排了几小时的活）。
    /// </para>
    /// <para>
    /// 顺带修好一个老行为：爆破窗口盖住班首时，原式 <c>we = min(we, BlastStart)</c> 会把整个班
    /// 压成 0 长度（相当于整班停产）；现在那种情况是"清场解除后开工"，取得回后面那一段。
    /// </para>
    /// </summary>
    /// <summary>
    /// 有效时窗 —— 转发 <see cref="WorkWindowCalc.Of"/>。
    /// 算法只许有一处实现：逐日能力日历（月计划按天加权摊）用的是同一个，
    /// 两边各写一份的话，"日历说这天能干 6.5 小时、装箱那边排出 8 小时"这种错会从缝里漏出去。
    /// </summary>
    private static (double ws, double we) WorkWindow(ExploderConfig cfg, string equipId, ShiftWindow sh)
        => WorkWindowCalc.Of(sh, equipId, cfg.Maintenance, cfg.BlastWindows(), cfg.HandoverRampH, cfg.FromHour);

    /// <summary>
    /// 爆破把某个班切成多段时如实报账：装箱每班只取最长的一段（理由见 <see cref="WorkWindow"/>），
    /// 放弃掉的小时数必须写出来——否则计划看着排满了，实际少排了几小时的活，谁也看不出来。
    /// 一个班一条，不逐面刷屏。
    /// </summary>
    private static void CheckBlastSegmentation(ExploderConfig cfg, ExploderResult res, bool boxing = true)
    {
        var windows = cfg.BlastWindows();
        if (windows.Count == 0) return;

        foreach (var sh in cfg.Shifts)
        {
            var segs = BlastWindow.Subtract(sh.Start, sh.End, windows);
            double inShift = windows.Sum(w => Math.Max(0, Math.Min(w.End, sh.End) - Math.Max(w.Start, sh.Start)));

            if (segs.Count == 0)
            {
                if (inShift > 1e-6)
                    res.Violations.Add(new PlanViolation
                    {
                        Severity = ViolationSeverity.Warn, Code = ViolationCodes.BlastClearance,
                        Message = $"{sh.Name} 整班落在爆破清场内（{inShift:0.#} h），本班排不出作业",
                    });
                continue;
            }

            double longest = segs.Max(s => s.End - s.Start);
            double dropped = segs.Sum(s => s.End - s.Start) - longest;

            if (segs.Count > 1 && dropped > 0.25)
                res.Violations.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Info, Code = ViolationCodes.BlastClearance,
                    // ★ 后半句是**装箱那条路**的口径（一条(面×班)一条任务、取最长段）。
                    //   逐班推进分解走的是 sh.EffectiveHours（扣掉清场的**总**有效工时），
                    //   在那条路上说"取最长的一段"是错的 —— 同一句话不能两条路通用。
                    Message = $"{sh.Name} 被 {segs.Count - 1} 次爆破清场切成 {segs.Count} 段（停产 {inShift:0.#} h）；"
                            + (boxing
                                ? $"一条(面×班)只能出一条任务，装箱取最长的一段 {longest:0.#} h，"
                                  + $"另 {dropped:0.#} h 可作业时间未排——需要用满请把该班拆成两个班次"
                                : $"本班有效作业 {segs.Sum(x => x.End - x.Start):0.#} h 被切成不连续的 {segs.Count} 段；"
                                  + "逐班推进分解按有效工时总量派量，不按连续段派——"
                                  + "设备实际要在段间停两次，段数多时按连续段核一次更稳妥"),
                });
            else if (inShift > 1e-6)
                res.Violations.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Info, Code = ViolationCodes.BlastClearance,
                    Message = $"{sh.Name} 扣爆破清场 {inShift:0.#} h，有效作业 {longest:0.#} h",
                });
        }
    }

    private static ProductionTask IdleTask(ExploderConfig cfg, FaceInput face, double ws, double we, ShiftWindow sh, string label, IncompleteReason reason)
        => new()
        {
            Id = $"{cfg.IdPrefix}-{face.Group.MainEquipment}-{ShiftShort(sh.Name)}空",
            Process = ProcessType.Idle, Group = Clone(face.Group),
            WorkZone = face.Zone, UnitId = face.UnitId, Material = label, Shift = sh.Name,
            StartHour = Math.Round(ws, 2), EndHour = Math.Round(we, 2),
            Status = TaskStatus.Planned, Reasons = new() { reason },
        };

    /// <summary>约束/冲突校核：设备同班不双占、工序接续，以及采—运—排（去向/物料/库容/卸点/运距）。</summary>
    /// <summary>
    /// <b>只跑校核，不装箱</b> —— 逐班推进分解（<see cref="ShiftPlanAssembler"/>）产出的任务用它。
    ///
    /// <para><b>为什么必须有这个入口</b>：<c>Explode()</c> 把"装箱"和"校核"焊在一起，
    /// 而当日盘子有两条产出路径 —— 分解器那条**直接 return，一条判据都不跑**。
    /// 于是校核面板上那句「✓ 无异常」不是"没问题"，是"没人去校"：
    /// 实测一盘 0 个采装面、只有穿孔的计划，照样报无异常。
    /// 空过的判据比没有判据更坏 —— 它给的是"已经查过了"这个结论。</para>
    ///
    /// <para>装箱专属的那两件事不在这儿：<c>ExplodeFace</c> 的当日欠产回摊、
    /// 以及爆破切段里"取最长一段"的口径（后者已按 <c>boxing</c> 分开说）。</para>
    /// </summary>
    public static void CheckOnly(ExploderConfig cfg, ExploderResult res)
    {
        if (cfg == null || res == null) return;
        CheckBlastSegmentation(cfg, res, boxing: false);
        CheckConstraints(cfg, res);
        SpaceTimeValidator.Validate(cfg, res);
    }

    private static void CheckConstraints(ExploderConfig cfg, ExploderResult res)
    {
        // 设备同班不双占（同主设备时段重叠）
        //
        // ★ **没有主设备的笔不参与**（2026-08-20 修）：按已定口径爆破不指人
        //   （爆破队台账还没有），`MainEquipment` 恒为空 —— 而这里按它分组，
        //   于是本班所有爆破笔**归成同一台"空设备"**，两炮时窗一挨上就报「时段重叠」，
        //   Error 级，整盘不得下达。实测 2026-08-20 的真盘子就是这样：
        //   2 炮 ⇒ 1 条设备双占，消息还是「（空） 时段重叠：TK-… ∩ TK-…」，
        //   点名的那台设备没有名字，照着它根本查不下去。
        //   **没有机器的活占不住任何一台机器**。同一个面同一时刻的冲突是另一回事，
        //   由 M1（分解器的 faceBusy）与 SpaceTimeValidator 按作业面判，不在这一条里。
        foreach (var g in res.Tasks
                     .Where(t => t.Process != ProcessType.Idle && !string.IsNullOrWhiteSpace(t.Group.MainEquipment))
                     .GroupBy(t => t.Group.MainEquipment))
        {
            var ordered = g.OrderBy(t => t.StartHour).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].StartHour < ordered[i - 1].EndHour - 0.01)
                    res.Violations.Add(new PlanViolation
                    {
                        Severity = ViolationSeverity.Error, Code = "设备双占", TaskId = ordered[i].Id,
                        Message = $"{g.Key} 时段重叠：{ordered[i - 1].Id} ∩ {ordered[i].Id}",
                    });
        }

        // 工序接续（全局粗判）：有采装却全天无穿孔。降为 Info——只有硬岩/夹矸才真的需要爆破前置，
        // 精确到"哪个面的什么料缺穿孔"的判定在 SpaceTimeValidator（按物料 NeedsBlasting × 作业区匹配）。
        bool hasLoad = res.Tasks.Any(t => t.Process == ProcessType.Load);
        bool hasDrill = res.Tasks.Any(t => t.Process == ProcessType.Drill);
        if (hasLoad && !hasDrill)
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = ViolationCodes.ProcessChain, Message = "有采装但全天无穿孔任务，备采接续存疑（需爆破物料的逐面判定见「工序接续」明细）" });

        CheckFlowConstraints(cfg, res);
    }

    private static EquipmentGroup Clone(EquipmentGroup g) => g.Clone();

    private static string ShiftShort(string name) => name.StartsWith("早") ? "早" : name.StartsWith("中") ? "中" : name.StartsWith("夜") ? "夜" : name;

    /// <summary>
    /// 时刻属于哪个班 —— 与界面共用 <see cref="ShiftScope"/> 的那一条口径。
    /// 原先这里自带一句 <c>hour &gt;= s.Start &amp;&amp; hour &lt; s.End … ?? ""</c>：跨零点的夜班永远匹配不上，
    /// 穿孔/检修任务的 Shift 落成空串，之后每个按班筛选的窗口里它们都不见了。
    /// </summary>
    private static string ShiftOf(ExploderConfig cfg, double hour)
        => ShiftScope.ShiftOf(cfg.Shifts, hour);
}
