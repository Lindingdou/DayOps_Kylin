// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/LedgerFlowSource.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 把<b>采掘单元台账里排产写回的流</b>直接做成一期的物料流 + 卸点表（2026-08-18 加）。
///
/// <para><b>为什么必须有这条路</b>：在它之前，逐月推演的指标那条链是这么走的 ——
/// 从台账<b>按月汇总出两个总量</b>（煤 xx 万t / 岩 xx 万m³）→ 按<b>当日任务盘子的作业面份额</b>摊开
/// → 往<b>任务盘子的卸点表</b>重新做一次流向分配。而排产解出来的对位关系
/// （<c>岩1416-B2-P1 → 内排土场1-L2-1000500，3.546km</c>，台账里每一行都写着）
/// <b>只用来画运输线</b>，不进指标、不进校核。
/// ⇒ 排产排的是一套流向、指标算的是另一套，同屏显示、数对不上，而两边各自都"正常"。
/// 实测那次：台账煤 75.16 万t，指标面板显示采出 0；剥离计划 201.65 万m³，指标 143.62。</para>
///
/// <para><b>这条路只做搬运，不做分配</b>：源=台账单元、汇=台账排土位置、运距=台账那一列。
/// 唯一还算"独立"的一步是<b>库容能不能吃下</b>（拿台账的库容列逐期累扣）——
/// 采排校核判的就是这一步，而不是再去做一遍已经做过的分配。</para>
///
/// <para><b>取不到就返回 null</b>（这一期没有流 / 没有排土位置索引），调用方退回老路并说明。
/// 静默降级是本仓库明令禁止的：两条路排出来的数差着 30%，看上去却都正常。</para>
/// </summary>
public static class LedgerFlowSource
{
    /// <summary>一期的搬运结果。</summary>
    public sealed class Period
    {
        public string Label = "";
        public List<MaterialFlow> Flows = new();
        public SinkRegistry Sinks = new();

        /// <summary>本期各汇实际吃下的占容方（受台账库容约束）。</summary>
        public Dictionary<string, double> Accepted = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>按流算的剥离实方 / 应排占容（需求侧）。</summary>
        public double WasteInSituM3, ExpectedDumpM3;
        /// <summary>按流算的采出吨。</summary>
        public double OreT;

        public int UnitCount, SlotCount, FlowCount;
        /// <summary>去向码在排土索引里查不到的流（<b>不许悄悄丢</b>）。</summary>
        public int UnresolvedFlows;
        public double UnresolvedInSituM3;

        public List<string> Notes = new();
    }

    /// <summary>
    /// 造这一期。<paramref name="period"/> = <c>yyyy-MM</c>。
    /// <para><b>排土索引优先用本期文件里的位置，取不到才回基表</b> ——
    /// 老的期次文件里一个位置都没有（存期次时漏了），那时只能靠基表补索引，
    /// 但要在 <see cref="Period.Notes"/> 里说出来：它意味着<b>库容用的是全场的账</b>，
    /// 不是这一期真正划给它的那部分。</para>
    /// </summary>
    public static Period? TryBuild(string period)
    {
        if (string.IsNullOrWhiteSpace(period)) return null;
        var res = new Period { Label = period };
        try
        {
            var store = new MonthlyUnitLedgerStore();
            if (!store.TryLoad(period, out var rows, out _) || rows.Count == 0) return null;

            // 排土索引 = 本期文件里的位置 ∪ 基表里的位置。
            //
            // ⚠ 早先写成"本期一个位置都没有才回基表"，那是**错的**：本期文件里往往只存了
            //   一小撮位置（存期次时按流的去向码捞的那些），而流指向的位置可以更多。
            //   于是"有 17 个位置"就不回基表了 ⇒ 1174 笔岩流的去向码查不到、
            //   1008.7 万m³ 实方**全被判成没有落点**，剥离量直接掉到 0、内排率 0%。
            //   两边合起来取才对：本期那份优先（它带着本期的库容口径），缺的从基表补。
            var slotRows = rows.Where(r => r.Kind == LedgerKind.Dump).ToList();
            int fromPeriod = slotRows.Count;
            int fromBase = 0;
            if (store.TryLoadBase(out var baseRows, out _))
            {
                var have = new HashSet<string>(slotRows.Select(r => r.UnitId), StringComparer.Ordinal);
                foreach (var b in baseRows)
                    if (b.Kind == LedgerKind.Dump && have.Add(b.UnitId)) { slotRows.Add(b); fromBase++; }
            }
            var index = DumpSlotCode.BuildIndex(slotRows, out _, out var collided);
            res.SlotCount = index.Count;

            var mineRows = rows.Where(r => r.Kind != LedgerKind.Dump).ToList();
            res.UnitCount = mineRows.Count;

            foreach (var u in mineRows)
            {
                foreach (var f in u.Flows)
                {
                    if (f == null || f.InSituM3 <= 1e-6) continue;
                    res.FlowCount++;

                    bool isCoal = u.Kind == LedgerKind.Coal;
                    string code = (f.Destination ?? "").Trim();
                    SinkNode? node = null;

                    if (code.Length > 0 && index.TryGetValue(code, out var slot))
                    {
                        node = res.Sinks.Find(code) ?? PutSlot(res, code, slot);
                    }
                    else if (isCoal)
                    {
                        // 煤的去向是出矿点（破碎站/煤仓），不在排土索引里 —— 按通过型汇建，库容不限
                        string id = code.Length > 0 ? code : "CR-DEFAULT";
                        node = res.Sinks.Find(id) ?? PutOreSink(res, id);
                    }
                    else
                    {
                        // 岩流的去向码解不出：**记账不吞**。悄悄丢掉的话，剥离量会凭空少一块，
                        // 而每个数看上去都正常（本仓库这类静默是主要的坑源）。
                        res.UnresolvedFlows++; res.UnresolvedInSituM3 += f.InSituM3;
                        continue;
                    }

                    string mat = !string.IsNullOrWhiteSpace(f.MaterialCode)
                        ? f.MaterialCode
                        : isCoal ? MaterialCatalog.Coal : MaterialCatalog.Rock;

                    res.Flows.Add(new MaterialFlow
                    {
                        Period = period,
                        SourceId = u.UnitId,
                        SourceName = u.UnitId,
                        SourceBenchElevationM = u.Cz,
                        MaterialCode = mat,
                        InSituM3 = f.InSituM3,
                        SinkId = node!.Id,
                        SinkName = node.Name,
                        SinkKind = node.Kind,
                        // 运距是台账里那一列（排产按路网算过的）。没算过时留 0，
                        // 不拿直线距离顶上 —— 顶上就分不清"算过是 0"和"根本没算"。
                        HaulKm = f.HaulKm ?? 0,
                    });
                }
            }

            if (res.Flows.Count == 0) return null;

            res.OreT = res.Flows.Where(f => f.IsOre).Sum(f => f.TonnageT);
            res.WasteInSituM3 = res.Flows.Where(f => !f.IsOre).Sum(f => f.InSituM3);
            res.ExpectedDumpM3 = res.Flows.Where(f => !f.IsOre).Sum(f => f.DumpM3);

            // ── 供给侧：台账库容能吃下多少（逐汇上限，超出的就是本期排不下的量）──
            foreach (var grp in res.Flows.Where(f => f.SinkKind.IsDumping()).GroupBy(f => f.SinkId))
            {
                double want = grp.Sum(f => f.DumpM3);
                var node = res.Sinks.Find(grp.Key);
                double room = node?.RemainingM3 ?? double.PositiveInfinity;
                res.Accepted[grp.Key] = double.IsPositiveInfinity(room) ? want : Math.Min(want, room);
            }

            res.Notes.Add($"本期取自【采掘单元台账 {period}】排产写回的流："
                        + $"{res.UnitCount} 个单元 · {res.FlowCount} 笔流 · {res.SlotCount} 个排土位置"
                        + $" —— 源、汇、运距全部按排产的结果搬过来，**不再重新做流向分配**。");
            if (fromBase > 0)
                res.Notes.Add($"· 排土索引：本期文件 {fromPeriod} 个位置 + 从基表补 {fromBase} 个 —— "
                            + "补进来的那些用的是**全场的库容账**，不是这一期划给它的那部分。"
                            + "重新「存为期次」一次会把本期用到的位置都写进期次文件。");
            if (collided.Count > 0)
                res.Notes.Add($"◆ 有 {collided.Count} 个排土位置因去向码相撞被整条剔除（同一 场-级-带-幅），"
                            + "指向它们的流解不出来。");
            if (res.UnresolvedFlows > 0)
                res.Notes.Add($"◆ {res.UnresolvedFlows} 笔岩流的去向码在排土索引里查不到，"
                            + $"共 {res.UnresolvedInSituM3 / 1e4:0.##}万m³实方**没有落点**（已从本期剥离量里剔除，不是悄悄丢）。");
            return res;
        }
        catch (Exception ex)
        {
            res.Notes.Add("◆ 台账流搬运失败：" + ex.GetType().Name + "：" + ex.Message);
            return res.Flows.Count > 0 ? res : null;
        }
    }

    private static SinkNode PutSlot(Period res, string code, MiningUnitLedger.Row slot)
    {
        // 内排/外排按排土场名判 —— 台账里没有独立的类型列，名字是唯一的依据。
        bool inner = slot.Region.Contains("内排", StringComparison.Ordinal);
        var node = new SinkNode
        {
            Id = code,
            Name = code,
            Kind = inner ? SinkKind.InternalDump : SinkKind.ExternalDump,
            DesignCapacityM3 = Math.Max(0, slot.DumpCapM3 ?? 0),
            FilledM3 = 0,
            X = slot.Cx, Y = slot.Cy, Z = slot.Cz,
            BenchHeightM = slot.ThickM > 0.1 ? slot.ThickM : 20,
            WorkLineLengthM = slot.LengthM,
        };
        res.Sinks.Put(node);
        return node;
    }

    private static SinkNode PutOreSink(Period res, string id)
    {
        var node = new SinkNode
        {
            Id = id,
            Name = id == "CR-DEFAULT" ? "出矿点（未指定，排产按缺省位置算的）" : id,
            Kind = SinkKind.Crusher,
            DesignCapacityM3 = 0,           // 通过型：不限
        };
        res.Sinks.Put(node);
        return node;
    }
}
