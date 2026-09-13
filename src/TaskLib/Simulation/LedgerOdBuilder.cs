// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/LedgerOdBuilder.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 台账行 → 运输 O-D（<see cref="SimHaulOd"/>）。
///
/// <para><b>纯函数</b>：不读盘、不碰内核、不要任何能力。读盘（基表 / 本期台账 / 煤卸点）
/// 留给窗口那几行接线 —— 与 <c>HaulRouteStage</c> 文件头「映射由接线方写，一处一份」同一条纪律。</para>
///
/// <para><b>三条不许</b>：
/// ① 解不出汇点的流<b>不画</b>（<c>DestTrust</c> 留 <c>Unknown</c>），并计进分类账 —— 少画比画错好；
/// ② 煤没指过卸点时<b>不拿采场质心冒充</b>，标 <c>Approximate</c> 且坐标留 0 ——
///    冒充出来的线和真运输线长得一模一样；
/// ③ 每一笔流都要落进<b>恰好一个</b>桶（画出来 或 某一种跳过），分类账自洽。
///    少记一笔就意味着"悄悄丢了一笔"，而命中率看上去照样正常。</para>
///
/// <para><b>⚠ <see cref="SimHaulOd.TrustNote"/> 是死字段</b>：<c>HaulRouteStage</c> 全文只有声明处
/// 出现过一次，渲染路径一次都没读它。填了它界面上不会显示任何东西 ——
/// 所以降级说明必须同时进 <see cref="LedgerOdResult.Notes"/>。</para>
/// </summary>
public static class LedgerOdBuilder
{
    /// <summary>造 O-D 的结果 + 分类账。<b>每一笔流的去处都要记账</b>。</summary>
    public sealed class LedgerOdResult
    {
        public List<SimHaulOd> Ods { get; } = new();
        public List<string> Notes { get; } = new();

        /// <summary>输入里一共几笔流。</summary>
        public int FlowsTotal { get; set; }
        /// <summary>画得出来的。</summary>
        public int Emitted => Ods.Count;

        public int SkipNoDestCode { get; set; }      // 去向列是空的
        public int SkipDestUnresolved { get; set; }  // 有码，但索引里查不到（撞码剔除 / 码不属于本批）
        public int SkipDestNoXy { get; set; }        // 查到了行，但那一行没有坐标
        public int SkipSrcNoXy { get; set; }         // 源单元没有坐标

        public int EmittedCoalReal { get; set; }     // 煤：指过卸点，真点
        public int EmittedCoalApprox { get; set; }   // 煤：没指过卸点，降级、不画
        public int EmittedRock { get; set; }

        /// <summary>排土索引里有几个位置、最大级是多少 —— 解不出时第一件要看的事。</summary>
        public int SlotIndexed { get; set; }
        public int MaxDumpLevel { get; set; }
        public int CollidedCodes { get; set; }

        /// <summary>分类账自洽：画出来的 + 四种跳过 == 总数。<b>不自洽说明有一笔被悄悄丢了</b>。</summary>
        public bool Balanced =>
            Emitted + SkipNoDestCode + SkipDestUnresolved + SkipDestNoXy + SkipSrcNoXy == FlowsTotal;

        public string Summary
        {
            get
            {
                if (FlowsTotal == 0) return "◆ 这一批里一笔带去向的流都没有 —— 先排一次产（「按目标排产」会写回去向列）。";
                int skipped = FlowsTotal - Emitted;
                var s = $"运输线：{Emitted}/{FlowsTotal} 笔流画得出来";
                if (skipped > 0) s = "◆ " + s + $"，跳过 {skipped} 笔";
                if (!Balanced) s = "◆◆ " + s + "（分类账不自洽 —— 有流没记账，这是个 bug）";
                return s;
            }
        }
    }

    /// <summary>
    /// 造 O-D。
    /// </summary>
    /// <param name="baseRows">
    /// <b>基表全量</b> —— 排土位置的坐标只在基表里，月度台账通常只有本期涉及的单元。
    /// 传本期行进来会导致排土索引为空、所有岩流解不出，而界面上只表现为"命中率 0%"，看起来像路网问题。
    /// </param>
    /// <param name="flowRows">要画的那一批行（本期台账，或基表里筛出来的）。</param>
    /// <param name="coalSink">煤的卸点。null = 没指过，煤流一律降级不画。</param>
    public static LedgerOdResult Build(
        IReadOnlyList<MiningUnitLedger.Row>? baseRows,
        IReadOnlyList<MiningUnitLedger.Row>? flowRows,
        CoalSinkPoint? coalSink)
    {
        var res = new LedgerOdResult();

        var slots = DumpSlotCode.BuildIndex(baseRows, out int maxLv, out var collided);
        res.SlotIndexed = slots.Count;
        res.MaxDumpLevel = maxLv;
        res.CollidedCodes = collided.Count;

        if (slots.Count == 0)
            res.Notes.Add("◆ 一个排土位置都没索引到 —— 排土行的坐标只存在【基表】里，"
                        + "只读了月度台账的话这里就是空的，所有岩流都会解不出。");
        if (collided.Count > 0)
            res.Notes.Add($"◆ 有 {collided.Count} 个去向码被两个以上排土位置**同时占用**（台账没有子号列），"
                        + "已整条剔除 —— 指向它们的流会解不出。"
                        + $"第一个是 {collided[0]}。");

        if (coalSink == null || !coalSink.IsPicked)
            res.Notes.Add("◆ 还没在图上指过煤的卸点 —— 煤流**没有坐标**，一条都不画。"
                        + "在「采掘单元清单 → 指煤卸点…」指一次。");
        else
            res.Notes.Add($"· 煤运到【{coalSink.Name}】({coalSink.X:0}, {coalSink.Y:0})。"
                        + "⚠ 台账那一列的运距是排产时按默认位置算的，**排产时算运距用的不是这个点** —— "
                        + "指完卸点重排一次，数才和线对得上。");

        var unknownMats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in flowRows ?? Array.Empty<MiningUnitLedger.Row>())
        {
            if (r == null || r.Kind == LedgerKind.Dump) continue;   // 排土行是汇，不是源
            foreach (var f in r.Flows)
            {
                if (f == null) continue;
                res.FlowsTotal++;

                string code = (f.Destination ?? "").Trim();
                if (code.Length == 0) { res.SkipNoDestCode++; continue; }

                // 源坐标
                if (Math.Abs(r.Cx) < 1e-9 && Math.Abs(r.Cy) < 1e-9) { res.SkipSrcNoXy++; continue; }

                bool isCoal = r.Kind == LedgerKind.Coal;
                double dx = 0, dy = 0, dz = 0;
                string destName;
                SimHaulTrust trust;

                if (isCoal)
                {
                    // 煤 → 卸点。没指过就降级、坐标留 0，【绝不拿采场质心冒充】
                    if (coalSink != null && coalSink.IsPicked)
                    {
                        dx = coalSink.X; dy = coalSink.Y; dz = coalSink.Z;
                        destName = coalSink.Name;
                        trust = SimHaulTrust.Real;
                        res.EmittedCoalReal++;
                    }
                    else
                    {
                        destName = "（未指定的煤卸点）";
                        trust = SimHaulTrust.Approximate;
                        res.EmittedCoalApprox++;
                    }
                }
                else
                {
                    if (!slots.TryGetValue(code, out var slot)) { res.SkipDestUnresolved++; continue; }
                    if (Math.Abs(slot.Cx) < 1e-9 && Math.Abs(slot.Cy) < 1e-9) { res.SkipDestNoXy++; continue; }
                    dx = slot.Cx; dy = slot.Cy; dz = slot.Cz;
                    destName = slot.UnitId;
                    trust = SimHaulTrust.Real;
                    res.EmittedRock++;
                }

                // 吨量走物料目录。未知码回落硬岩，但【必须说出来】——
                // 静默回落会让载荷分档偏，而每个数看上去都正常。
                string mat = (f.MaterialCode ?? "").Trim();
                if (mat.Length == 0) mat = isCoal ? MaterialCatalog.Coal : MaterialCatalog.Rock;
                if (!MaterialCatalog.Exists(mat)) unknownMats.Add(mat);
                var spec = MaterialCatalog.Resolve(mat);

                res.Ods.Add(new SimHaulOd
                {
                    UnitId = r.UnitId,
                    IsCoal = isCoal,
                    Sx = r.Cx, Sy = r.Cy, Sz = r.Cz,
                    DestinationCode = code,
                    DestinationName = destName,
                    Dx = dx, Dy = dy, Dz = dz,
                    DestTrust = trust,
                    InSituM3 = f.InSituM3,
                    TonnageT = spec.ToTonnage(f.InSituM3),
                    HaulKm = f.HaulKm ?? 0,     // 台账没填运距 ⇒ 0（本阶段不拿它画线，只在说明里对照）
                    MaterialCode = mat,
                    MaterialName = spec.Name,
                    IsInternalDump = !isCoal && destName.Contains("内排"),
                    IsCoalSink = isCoal,
                });
            }
        }

        foreach (var m in unknownMats)
            res.Notes.Add($"◆ 物料码「{m}」不在物料目录里，吨量按**硬岩**回落算的 —— 载荷分档会偏。");

        if (res.SkipDestUnresolved > 0)
            res.Notes.Add($"◆ 有 {res.SkipDestUnresolved} 笔流的去向码在排土索引里查不到 ——"
                        + $"索引里有 {res.SlotIndexed} 个位置（最大级 L{res.MaxDumpLevel}）。"
                        + "码对不上号最常见的原因是排产与解码用了两套编码。");
        if (res.SkipSrcNoXy > 0)
            res.Notes.Add($"◆ 有 {res.SkipSrcNoXy} 笔流的**源单元没有坐标**（质心是 0,0），画不出起点。");
        if (res.SkipDestNoXy > 0)
            res.Notes.Add($"◆ 有 {res.SkipDestNoXy} 笔流查到了排土位置，但**那一行没有坐标**。");
        if (!res.Balanced)
            res.Notes.Add("◆◆ 分类账不自洽：画出来的 + 跳过的 ≠ 总数，说明有流没记账。这是代码的 bug，不是数据问题。");

        return res;
    }
}
