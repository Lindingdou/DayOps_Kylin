// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/HaulDumpDeriver.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  运输 + 排土 的派生（HD 组，2026-08-20）—— 解析式闭环的后半截。
//
//  ══ 它补的是哪一段 ══
//  逐班分解器只排出**穿孔**与**采装**两道工序（钻机按矿调、电铲按面）。
//  运输与排土在现场不是"另外排的活"，而是**同一批量的下游**：
//      采装 v(实方) → 按物料拆 → 各自的去向 → 承运吨量 → 车次
//                              ↘ 排弃那部分 → 到场占容方 → 排土机的活
//  所以它们**不该再排一遍**，而应当由采装那一笔推出来 —— 推出来才守恒。
//
//  ══ 五条口径 ══
//   HD1 **一笔采装派生一笔运输**（混采面按物料分项派生多笔：煤去破碎站、岩去排土场）。
//       时窗与采装同一段：车是跟着铲装的，铲不装车就没得拉。
//   HD2 **定车制**：运输那一行的"设备"是**车队**（`{铲号} 车队`），不是单台车。
//       233 台卡车逐台一行会把甘特铺成上百行；而定车制下"这几台车跟着这台铲"本来就是一组。
//       车队里有哪几台车原样带过来（`Group.Trucks`），点开看得到。
//   HD3 **车次 = 承运吨量 ÷ 单车载重**，吨量是三个体积口径之间**唯一守恒**的量。
//       载重取编组解出来的那台车型；解不出来就不给车次数（**不猜一个 100t**），
//       只给吨量 —— 少一个数好过给一个编的数。
//   HD4 **排土的量 = 到场的排弃占容方**，按 (日, 班, 场) 汇总后分给该场的排土面。
//       分不到场的排弃量单列报出来 —— 那是"运出去了没人接"，不能悄悄消失。
//   HD5 **排土受推土机能力封顶**：超出的部分记成缺口，不硬塞。
//       封不住就等于说"这个场今天能接无限方"，而卸点能力校核那一条就永远不会响。
//
//  ⚠ 量口径：运输笔记的是**承运量**、排土笔记的是**排弃占容方**，
//    与采装的原位实方**不是同一本账**。三者不许并成一列求和（M5 同一条纪律）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>由采装任务派生运输与排土任务。纯计算，不碰库。永不抛。</summary>
public static class HaulDumpDeriver
{
    /// <summary>一次派生的结果。</summary>
    public sealed class Result
    {
        public List<ProductionTask> Haul = new();
        public List<ProductionTask> Dump = new();
        public List<string> Notes = new();

        /// <summary>运输总吨（承运量）。</summary>
        public double HaulTonnage;
        /// <summary>到场排弃占容方合计。</summary>
        public double DumpVolumeM3;
        /// <summary>没人接的排弃量（去向没绑到排土面）。</summary>
        public double UnservedDumpM3;
        /// <summary>推土机能力吃不下的量。</summary>
        public double OverDozerM3;

        // ── 可行性回填（FB 组）：**削掉的量按"被谁卡的"分类记账** ──
        //  三种缺口的补法完全不同：卸点卡的要加卸点/改去向，车卡的要补车，库容卡的要换场。
        //  并成一个"排不下 N 万m³"就没人知道该动哪一样。
        /// <summary>被卸点通过能力削掉的原位实方。</summary>
        public double CutByTipCapacityM3;
        /// <summary>被车池（在用卡车不够）削掉的原位实方。</summary>
        public double CutByTruckPoolM3;
        /// <summary>被排土场剩余库容削掉的原位实方。</summary>
        public double CutByStorageM3;

        public double CutTotalM3 => CutByTipCapacityM3 + CutByTruckPoolM3 + CutByStorageM3;
    }

    /// <summary>
    /// 从当日的采装任务派生运输 + 排土。
    /// </summary>
    /// <param name="loadTasks">当日采装任务（分解器或装箱产出的都行）。</param>
    /// <param name="cfg">盘子（要面上的去向/运距/编组，以及排土面清单）。</param>
    /// <param name="availableTrucks">
    /// 在用卡车台数（车池上限）。<b>&lt;=0 = 不判这一条</b>——不知道有几台车时，
    /// 拿 0 当"一台都没有"会把整盘削光，而那是判据环境的产物，不是现场状态。
    /// </param>
    public static Result Derive(IReadOnlyList<ProductionTask>? loadTasks, ExploderConfig? cfg,
                                int availableTrucks = 0)
    {
        var res = new Result();
        var loads = (loadTasks ?? Array.Empty<ProductionTask>())
                    .Where(t => t != null && t.Process == ProcessType.Load && t.TargetVolumeM3 > 1e-6).ToList();
        if (loads.Count == 0 || cfg == null) return res;

        // ⚠ **回填不在这里做**：回填要改采装那一笔的量，而 Derive 会被反复调用
        //   （判据、界面、体检各调一次）—— 放在这里就是"调几次削几次"，
        //   实测承运吨变成采装吨的 2.5 倍，而每一笔看着都对。
        //   回填是**装配期只跑一次**的一步，见 ApplyFeasibility（由装配层调）。

        // ⚠ **按 (面名, 工序) 索引，不能只按面名**：工序作业区派生出来的
        //   采装面与排土面**会重名**（都叫「内排土场1·L76」——同一块地的两道工序）。
        //   只按面名建字典，后进来的那个会盖掉前一个：采装那一笔于是查到排土面，
        //   拿到的是"日目标 0、去向另一个"的一份数据 —— 派生出来的运输量对不上，
        //   而每一笔看着都正常。PL2 逐笔守恒判据就是被这一条绊出来的。
        var faceOf = LoadFacesByZone(cfg);

        // (班, 去向) → 到场排弃占容方；排土那一步按它分活
        var arrivals = new Dictionary<(string Shift, string Sink), double>();
        // 这个场这一班的到货时段（第一车到 → 最后一车到），排土条按它画
        var arriveFrom = new Dictionary<(string Shift, string Sink), double>();
        var arriveTo = new Dictionary<(string Shift, string Sink), double>();
        int noDest = 0;

        // ── ① 运输：一笔采装 → 一笔（或多笔）运输 ──
        foreach (var t in loads)
        {
            faceOf.TryGetValue((t.WorkZone ?? "").Trim(), out var face);
            var mix = face?.ResolvedMix ?? MaterialMix.Parse(t.Material);
            var parts = mix.Split(t.TargetVolumeM3).Where(x => x.InSituM3 > 1e-6).ToList();
            if (parts.Count == 0) continue;

            foreach (var part in parts)
            {
                var dest = face?.DestinationFor(part.Spec.Code);
                string sinkName = (dest?.DestinationName ?? "").Trim();
                if (sinkName.Length == 0) { noDest++; continue; }

                double tonnage = part.Spec.ToTonnage(part.InSituM3);
                double payload = PayloadT(face);
                int trips = payload > 1e-6 ? (int)Math.Ceiling(tonnage / payload) : 0;

                var h = new ProductionTask
                {
                    Id = TaskKey.Compose(t.Id, t.Shift, CrewName(t.Group?.MainEquipment), "Haul", t.WorkZone + "|" + part.Spec.Code),
                    Process = ProcessType.Haul,
                    WorkZone = t.WorkZone,
                    UnitId = t.UnitId,
                    Shift = t.Shift,
                    // ★ **承接关系**：车不是班一开始就在卸点，要等第一车装完 + 拉过去（2026-08-20）。
                    //   滞后 = 装车节拍 + 单程重车行驶 = (τ_L + (T_c − τ_L − t_卸 − t_调)/2) / 60 小时。
                    //   原来直接抄采装的时窗 ⇒ 采装与运输**完全重叠**，图上看不出先后，
                    //   而现场是"铲装完这一车，车才上路"。
                    StartHour = t.StartHour + LagHours(face),
                    EndHour = t.EndHour + LagHours(face),
                    MaterialCode = part.Spec.Code,
                    Material = part.Spec.Name,
                    DestinationId = dest?.DestinationId ?? "",
                    DestinationName = sinkName,
                    DestinationKind = dest?.DestinationKind ?? SinkKind.ExternalDump,
                    HaulDistanceKm = dest?.HaulKm ?? 0,
                    EquivHaulKm = dest?.EquivHaulKm ?? 0,
                    Status = TaskLib.Domain.TaskStatus.Planned,
                    // 承运量单独一本账：**不写进 TargetVolumeM3 的原位实方口径**
                    HaulTonnageT = tonnage,
                    TripCount = trips,
                    SourceTaskId = t.Id,
                };
                h.Group.MainEquipment = CrewName(t.Group?.MainEquipment);
                if (face != null)
                {
                    h.Group.Trucks = new List<string>(face.Group.Trucks);
                    h.Group.RecommendedTrucks = face.Group.RecommendedTrucks;
                    h.Group.CycleTimeMin = face.Group.CycleTimeMin;
                }

                res.Haul.Add(h);
                res.HaulTonnage += tonnage;

                // 排弃那部分才进排土（矿石去破碎站，不入排土场）
                if (!part.Spec.IsOre)
                {
                    double dumpM3 = part.Spec.ToDumpM3(part.InSituM3);
                    var key = ((t.Shift ?? "").Trim(), sinkName);
                    arrivals[key] = (arrivals.TryGetValue(key, out double v0) ? v0 : 0) + dumpM3;
                    double hs = h.StartHour, he = h.EndHour;
                    arriveFrom[key] = arriveFrom.TryGetValue(key, out double f0) ? Math.Min(f0, hs) : hs;
                    arriveTo[key] = arriveTo.TryGetValue(key, out double e0) ? Math.Max(e0, he) : he;
                }
            }
        }

        if (noDest > 0)
            res.Notes.Add($"◆ {noDest} 笔采装**没有去向**，派生不出运输 —— "
                        + "这些量在图上不会有运输条，而采装条照样在（看着像「挖了没拉」）。"
                        + "去向是自动分配的，配不上多半是这个物料没有可用的汇（比如煤没有破碎站）。");

        // ── ② 排土：到场的量分给该场的排土面，受推土机能力封顶 ──
        var dumpFaces = (cfg.Faces ?? new List<FaceInput>())
                        .Where(f => f != null && f.Process == ProcessType.Dump).ToList();

        foreach (var kv in arrivals)
        {
            var (shift, sink) = kv.Key;
            double m3 = kv.Value;
            res.DumpVolumeM3 += m3;

            // 这个场底下有哪些排土面（面的去向名 == 场名，这是同一个源，不会差字符）
            var mine = dumpFaces.Where(f => string.Equals((f.DestinationName ?? "").Trim(), sink, StringComparison.Ordinal))
                                .ToList();
            if (mine.Count == 0)
            {
                res.UnservedDumpM3 += m3;
                continue;
            }

            // 一个场有多个排土面时按能力等分（能力都取不到就按面数等分）
            double each = m3 / mine.Count;
            foreach (var f in mine)
            {
                double cap = DozerShiftCapM3(f, cfg);
                double v = cap > 1e-6 ? Math.Min(each, cap) : each;
                if (cap > 1e-6 && each > cap) res.OverDozerM3 += each - cap;
                if (v <= 1e-6) continue;

                var d = new ProductionTask
                {
                    Id = TaskKey.Compose(sink, shift, f.Group.MainEquipment, "Dump", f.Zone),
                    Process = ProcessType.Dump,
                    WorkZone = f.Zone,
                    Shift = shift,
                    // ★ 排土跟着**到货**走：这个场这一班第一车到、最后一车到，就是推土机的活的起止。
                    //   原来写的是整班 ⇒ 排土条永远铺满，而料还没运到它就"在排"了。
                    StartHour = arriveFrom.TryGetValue(kv.Key, out double a0) ? a0 : ShiftStart(cfg, shift),
                    EndHour = arriveTo.TryGetValue(kv.Key, out double a1) ? a1 : ShiftEnd(cfg, shift),
                    MaterialCode = f.MaterialCode,
                    Material = f.Material,
                    DestinationName = sink,
                    DestinationId = f.DestinationId,
                    DestinationKind = f.DestinationKind,
                    Status = TaskLib.Domain.TaskStatus.Planned,
                    // 排土的量是**排弃占容方**，另一本账
                    DumpVolumeM3 = v,
                };
                d.Group.MainEquipment = f.Group.MainEquipment;
                res.Dump.Add(d);
            }
        }

        if (res.UnservedDumpM3 > 1e-6)
            res.Notes.Add($"◆ {res.UnservedDumpM3 / 1e4:0.##} 万m³ 排弃量**运到了却没有排土面接**（去向名下没有排土作业面）—— "
                        + "这部分在图上没有排土条。排土面的去向名要与排土场名对齐才接得上。");
        if (res.OverDozerM3 > 1e-6)
            res.Notes.Add($"◆ {res.OverDozerM3 / 1e4:0.##} 万m³ **推土机能力吃不下**（已按能力封顶）—— "
                        + "排土跟不上采装，料会堆在卸点。要么加推土机，要么削当班采装量。");

        return res;
    }

    // ── 可行性回填（FB1–FB4）──────────────────────────────────────────
    //
    //  ★ 在它之前，卸点能力/车池/库容这三条**只报警不生效**：
    //    界面上写着「内排场 通过能力不足：接收 11.86 万t > 能力 8.4 万t，超 3.46 万t」，
    //    而计划量一点没削 —— 这一班照排、车照派，到了现场卡车在卸点排长队。
    //    「算出来」和「报个告警」的分水岭就在这里：**超了就把采装量削到可行为止**。
    //
    //   FB1 **按班判、按班削**：能力是"每班能接多少"，按天判会把早班的富余借给夜班。
    //   FB2 **等比例削同一班同一去向的各面**，不挑面：挑面就得有一套"谁让谁"的规则，
    //       而那是调度决策；等比例至少是可解释、可复算的。
    //   FB3 **削掉的量按"被谁卡的"分类记账**（卸点/车池/库容）——
    //       三种缺口的补法完全不同：加卸点、补车、换场。并成一个总数就没人知道该动哪一样。
    //   FB4 **判不了就不削**：通过能力没录（AcceptTph<=0）、车池不知道（<=0）、
    //       库容没录（DesignCapacityM3<=0）时这一条不参与 —— 拿 0 当"能力为 0"会把整盘削光。
    /// <summary>
    /// 可行性回填：卸点通过能力 / 库容 / 车池超了就**真削采装量**（FB1–FB4）。
    /// <para><b>装配期只调一次</b>——它会就地改 <paramref name="loadTasks"/> 的量，调两次就削两次。</para>
    /// </summary>
    public static Result ApplyFeasibility(IReadOnlyList<ProductionTask>? loadTasks, ExploderConfig? cfg,
                                          int availableTrucks = 0)
    {
        var res = new Result();
        var loads = (loadTasks ?? Array.Empty<ProductionTask>())
                    .Where(t => t != null && t.Process == ProcessType.Load && t.TargetVolumeM3 > 1e-6).ToList();
        if (loads.Count == 0 || cfg == null) return res;
        Backfill(loads, cfg, availableTrucks, res);
        return res;
    }

    private static void Backfill(List<ProductionTask> loads, ExploderConfig cfg,
                                 int availableTrucks, Result res)
    {
        // ⚠ **按 (面名, 工序) 索引，不能只按面名**：工序作业区派生出来的
        //   采装面与排土面**会重名**（都叫「内排土场1·L76」——同一块地的两道工序）。
        //   只按面名建字典，后进来的那个会盖掉前一个：采装那一笔于是查到排土面，
        //   拿到的是"日目标 0、去向另一个"的一份数据 —— 派生出来的运输量对不上，
        //   而每一笔看着都正常。PL2 逐笔守恒判据就是被这一条绊出来的。
        var faceOf = LoadFacesByZone(cfg);

        var sinkOf = new Dictionary<string, SinkNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var sk in cfg.Sinks?.All ?? (IReadOnlyCollection<SinkNode>)Array.Empty<SinkNode>())
            if (sk != null && !string.IsNullOrWhiteSpace(sk.Name)) sinkOf[sk.Name.Trim()] = sk;

        double shiftHours = cfg.Shifts is { Count: > 0 } ? 24.0 / cfg.Shifts.Count : 8.0;

        // ── FB1/FB2：卸点通过能力（按 班 × 去向）──
        foreach (var g in loads.GroupBy(t => ((t.Shift ?? "").Trim(), SinkNameOf(t, faceOf))))
        {
            string sinkName = g.Key.Item2;
            if (sinkName.Length == 0) continue;
            if (!sinkOf.TryGetValue(sinkName, out var sink) || sink.AcceptTph <= 1e-6) continue;   // FB4

            double capT = sink.AcceptTph * shiftHours;
            double gotT = g.Sum(t => TonnageOf(t, faceOf));
            if (gotT <= capT + 1e-6) continue;

            double scale = capT / gotT;
            foreach (var t in g)
            {
                double before = t.TargetVolumeM3;
                t.TargetVolumeM3 = Math.Round(before * scale);
                res.CutByTipCapacityM3 += before - t.TargetVolumeM3;
            }
            res.Notes.Add($"◆ **{sinkName}·{g.Key.Item1} 卸点通过能力不足**：本班到达 {gotT / 1e4:0.##} 万t "
                        + $"> 能力 {capT / 1e4:0.##} 万t（{sink.AcceptTph:0} t/h × {shiftHours:0.#}h）—— "
                        + $"已把这一班送这个场的采装量**按比例削到 {scale * 100:0.#}%**。"
                        + "不削的话卡车会在卸点排长队，而计划上一切正常。补法：加卸点/延长开放时窗/分流到别的场。");
        }

        // ── FB4：库容（按 去向，跨班累计）──
        foreach (var g in loads.GroupBy(t => SinkNameOf(t, faceOf)))
        {
            if (g.Key.Length == 0) continue;
            if (!sinkOf.TryGetValue(g.Key, out var sink) || !sink.IsCapacityLimited) continue;
            double room = sink.RemainingM3;
            if (double.IsPositiveInfinity(room) || room <= 0) continue;

            double need = g.Sum(t => DumpM3Of(t, faceOf));
            if (need <= room + 1e-6) continue;

            double scale = room / need;
            foreach (var t in g)
            {
                double before = t.TargetVolumeM3;
                t.TargetVolumeM3 = Math.Round(before * scale);
                res.CutByStorageM3 += before - t.TargetVolumeM3;
            }
            res.Notes.Add($"◆ **{g.Key} 剩余库容不够**：今日需 {need / 1e4:0.##} 万m³ 占容 "
                        + $"> 剩余 {room / 1e4:0.##} 万m³ —— 已按比例削到 {scale * 100:0.#}%。"
                        + "补法：换场或扩容；库容是**盘点出来的**，剩余不对就去「盘点修正」。");
        }

        // ── FB3：车池（按班；定车制下一台车一班只跟一台铲）──
        if (availableTrucks > 0)
        {
            foreach (var g in loads.GroupBy(t => (t.Shift ?? "").Trim()))
            {
                // 同一班里每台铲各要几台车（同一台铲多笔任务只算一次）
                var perShovel = g.GroupBy(t => (t.Group?.MainEquipment ?? "").Trim())
                                 .ToDictionary(x => x.Key, x => x.Max(t => TrucksNeeded(t, faceOf)));
                int need = perShovel.Values.Sum();
                if (need <= availableTrucks) continue;

                double scale = (double)availableTrucks / need;
                foreach (var t in g)
                {
                    double before = t.TargetVolumeM3;
                    t.TargetVolumeM3 = Math.Round(before * scale);
                    res.CutByTruckPoolM3 += before - t.TargetVolumeM3;
                }
                res.Notes.Add($"◆ **{g.Key} 车不够**：本班各铲共需 {need} 台，在用只有 {availableTrucks} 台 —— "
                            + $"已按比例削到 {scale * 100:0.#}%。定车制下一台车一班只跟一台铲，"
                            + "不削就是同一台车被派给两个铲，而派车单上看不出来。");
            }
        }

        if (res.CutTotalM3 > 1e-6)
            res.Notes.Add($"· 可行性回填共削掉 {res.CutTotalM3 / 1e4:0.##} 万m³ —— "
                        + $"卸点 {res.CutByTipCapacityM3 / 1e4:0.##} · 车池 {res.CutByTruckPoolM3 / 1e4:0.##} · "
                        + $"库容 {res.CutByStorageM3 / 1e4:0.##} 万m³。**削掉不等于不用干**，"
                        + "这些量要么补能力、要么挪到后面的班，月底会对账。");
    }

    /// <summary>采装面按面名索引（排土面另走 <c>Process == Dump</c> 那一支，见 Derive）。</summary>
    private static Dictionary<string, FaceInput> LoadFacesByZone(ExploderConfig cfg)
    {
        var map = new Dictionary<string, FaceInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cfg.Faces ?? new List<FaceInput>())
            if (f != null && f.Process == ProcessType.Load && !string.IsNullOrWhiteSpace(f.Zone))
                map[f.Zone.Trim()] = f;
        return map;
    }

    private static string SinkNameOf(ProductionTask t, Dictionary<string, FaceInput> faceOf)
        => faceOf.TryGetValue((t.WorkZone ?? "").Trim(), out var f) ? (f.DestinationName ?? "").Trim() : "";

    /// <summary>这一笔的吨量（三个体积口径之间唯一守恒的量）。</summary>
    private static double TonnageOf(ProductionTask t, Dictionary<string, FaceInput> faceOf)
    {
        var mix = faceOf.TryGetValue((t.WorkZone ?? "").Trim(), out var f) ? f.ResolvedMix : MaterialMix.Parse(t.Material);
        return mix.ToTonnage(t.TargetVolumeM3);
    }

    /// <summary>这一笔运到场里占多少库容（只算排弃那部分，矿石去破碎站不占排土场）。</summary>
    private static double DumpM3Of(ProductionTask t, Dictionary<string, FaceInput> faceOf)
    {
        var mix = faceOf.TryGetValue((t.WorkZone ?? "").Trim(), out var f) ? f.ResolvedMix : MaterialMix.Parse(t.Material);
        return mix.Split(t.TargetVolumeM3).Where(x => !x.Spec.IsOre).Sum(x => x.Spec.ToDumpM3(x.InSituM3));
    }

    /// <summary>这台铲这一班要几台车（编组解出来的实配优先，其次推荐数）。</summary>
    private static int TrucksNeeded(ProductionTask t, Dictionary<string, FaceInput> faceOf)
    {
        if (faceOf.TryGetValue((t.WorkZone ?? "").Trim(), out var f))
        {
            if (f.Group.Trucks.Count > 0) return f.Group.Trucks.Count;
            if (f.Group.RecommendedTrucks > 0) return f.Group.RecommendedTrucks;
        }
        return Math.Max(t.Group?.Trucks?.Count ?? 0, t.Group?.RecommendedTrucks ?? 0);
    }

    // ── 内部 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 装车 + 单程重车行驶的滞后（h）—— 运输条相对采装条往后错这么多。
    /// <para>解不出编组（没有 τ_L / T_c）就返回 0：**不猜一个滞后**，宁可两条重叠也不给个编的先后。</para>
    /// </summary>
    private static double LagHours(FaceInput? face)
    {
        double takt = face?.Group.LoadTaktMin ?? 0;
        double tc = face?.Group.CycleTimeMin ?? 0;
        if (takt <= 1e-6 || tc <= takt) return 0;
        double travel = Math.Max(0, tc - takt - 1.5 - 1.0) / 2.0;   // t_卸 1.5 / t_调 1.0，与 FleetMatcher 同数
        return (takt + travel) / 60.0;
    }

    /// <summary>车队名：定车制下"跟着这台铲的那几台车"就是一组，甘特上一行。</summary>
    private static string CrewName(string? shovelId)
    {
        string s = (shovelId ?? "").Trim();
        return s.Length == 0 ? "车队" : s + " 车队";
    }

    /// <summary>
    /// 单车载重 t。取编组解出来的车型载重；<b>取不到返回 0 = 不给车次数</b>。
    /// <para>猜一个 100t 会让派车单上的车次看着完全正常而与现场差一截。</para>
    /// </summary>
    private static double PayloadT(FaceInput? face)
    {
        if (face == null) return 0;
        double p = face.Group.TruckPayloadT;
        return p > 1e-6 ? p : 0;
    }

    /// <summary>这一班这个排土面的推土能力（占容方 m³）。取不到返回 0 = 不封顶（由缺口那条报出来）。</summary>
    private static double DozerShiftCapM3(FaceInput f, ExploderConfig cfg)
    {
        double perH = f.Group.GroupCapacityM3PerH;
        if (perH <= 1e-6) return 0;
        double hours = cfg.Shifts is { Count: > 0 } ? 24.0 / cfg.Shifts.Count : 8.0;
        return perH * hours;
    }

    private static double ShiftStart(ExploderConfig cfg, string shift)
        => cfg.Shifts?.FirstOrDefault(w => string.Equals(w.Name, shift, StringComparison.Ordinal))?.Start ?? 0;

    private static double ShiftEnd(ExploderConfig cfg, string shift)
        => cfg.Shifts?.FirstOrDefault(w => string.Equals(w.Name, shift, StringComparison.Ordinal))?.End ?? 24;
}
