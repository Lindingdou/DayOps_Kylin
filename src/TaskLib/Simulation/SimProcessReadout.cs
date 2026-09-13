// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimProcessReadout.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;   // ProcessZone
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  工序读数（PS8）—— 「每道工序这一期干了多少」。**纯算法，不碰 WPF。**
//
//  ══ 为什么抽出来 ══
//  第一版写在窗口的一个私有方法里 —— 于是这条唯一的硬口径
//  （三个方量口径绝不许并成一列）**没有任何判据钉得住**：它只能靠起应用看一眼右栏。
//  同 SimPeriodKey 那次：规则写在 UI 里，写错了也测不到。
//
//  ══ 唯一一条硬口径 ══
//  **三本方量账分列，绝不合并**（与 ProcessZone.VolumeBasis 同一条纪律）：
//    · 穿孔 = **控制方量**（这些孔控制的爆破量）
//    · 采装 = **原位实方** V实
//    · 排土 = **排弃占容** V容 = V实 × Kr
//  谁把它们 SUM 到一起，就得到一个不对应任何真实量的数，而且不会有任何东西报错。
//
//  ══ 三个「—」不是 0 ══
//  · **爆破没有自己的方量口径**（M9）：它爆的就是穿孔那一笔控制方量。写 0 会被读成"一炮没放"。
//  · **推排不另计量**：卸下来的那一方就是要推的那一方，再记一遍就是把排土算两遍。
//  · **运输没有面状作业区**（P9）：它要地的是装车点与卸载点两个端点，不是一块地。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>工序读数的一行。</summary>
public sealed class SimProcRow
{
    /// <summary>工序中文名。</summary>
    public string Name { get; init; } = "";
    /// <summary>工序码（面状工序才有；运输为空）。</summary>
    public string Code { get; init; } = "";
    public uint Rgb { get; init; }

    /// <summary>地：块数 / 面积。取不到写 <see cref="Dash"/>。</summary>
    public string Land { get; init; } = Dash;
    /// <summary>量 + 口径（口径写在同一格，防止被当成同一列相加）。</summary>
    public string Volume { get; init; } = Dash;
    /// <summary>该由谁去干。</summary>
    public string Equip { get; init; } = "";
    /// <summary>本期作业：笔数 / 工时 / 台数（月度档没有逐笔时窗 ⇒ <see cref="Dash"/>）。</summary>
    public string Work { get; init; } = Dash;

    /// <summary>本行有没有量。<b>「—」不是 0</b> —— 两者说的是完全不同的事。</summary>
    public bool HasVolume => Volume.Length > 0 && !Volume.StartsWith(Dash, StringComparison.Ordinal);

    public const string Dash = "—";
}

/// <summary>工序读数表。永不抛。</summary>
public static class SimProcessReadout
{
    /// <summary>
    /// 按工序汇总「地 / 量 / 人机」。
    /// </summary>
    /// <param name="set">本期工序作业区（地与控制方量的出处）。null/空 ⇒ 地列全是「—」。</param>
    /// <param name="frame">当前帧（采装/运输/排土的量与逐笔时窗的出处）。null ⇒ 量与作业列退回台账。</param>
    public static IReadOnlyList<SimProcRow> Build(SimProcessRegionSet? set, SimFrame? frame)
    {
        var patches = set?.Patches ?? (IReadOnlyList<SimProcessPatch>)Array.Empty<SimProcessPatch>();
        var acts = frame?.Activities ?? new List<SimActivity>();

        var landOf = patches.GroupBy(p => p.Process ?? "")
            .ToDictionary(g => g.Key,
                          g => (Count: g.Count(),
                                AreaM2: g.Sum(p => p.AreaM2),
                                VolM3: g.Where(p => p.VolumeM3.HasValue).Sum(p => p.VolumeM3!.Value),
                                HasVol: g.Any(p => p.VolumeM3.HasValue && p.VolumeM3.Value > 1e-6)),
                          StringComparer.Ordinal);

        var workOf = acts.GroupBy(a => a.Process)
            .ToDictionary(g => g.Key,
                          g => (N: g.Count(),
                                H: g.Sum(a => a.DurationH),
                                Eq: g.Select(a => a.Equipment).Where(s => s.Length > 0)
                                     .Distinct(StringComparer.Ordinal).Count()));

        string Land(string code) => landOf.TryGetValue(code, out var v)
            ? $"{v.Count} 块 · {v.AreaM2 / 1e4:0.##} 万m²" : SimProcRow.Dash;

        string Work(ProcessType p) => workOf.TryGetValue(p, out var v)
            ? $"{v.N} 笔 · {v.H:0.#} h" + (v.Eq > 0 ? $" · {v.Eq} 台" : "") : SimProcRow.Dash;

        string LedgerVol(string code, string basis) =>
            landOf.TryGetValue(code, out var v) && v.HasVol
                ? $"{v.VolM3 / 1e4:0.##} 万m³（{basis}）" : SimProcRow.Dash;

        // 采装量：帧里有源就用帧（那是本期真正排下去的），否则退台账的控制方量口径。
        double loadInSitu = frame?.Sources.Sum(s => s.InSituM3) ?? 0;
        string loadVol = loadInSitu > 1e-6
            ? $"{loadInSitu / 1e4:0.##} 万m³（原位实方 V实）"
            : LedgerVol(ProcessZone.ProcLoad, "原位实方 V实");

        string haulVol = frame != null && frame.TransportWorkWanTKm > 1e-9
            ? $"{frame.TransportWorkWanTKm:0.##} 万t·km（运输功）"
              + (frame.WeightedAvgHaulKm > 1e-9 ? $"　加权运距 {frame.WeightedAvgHaulKm:0.##} km" : "")
            : SimProcRow.Dash;

        string dumpVol = frame != null && frame.DumpedWanM3 > 1e-9
            ? $"{frame.DumpedWanM3:0.##} 万m³（排弃占容 V容＝V实×Kr）"
            : LedgerVol(ProcessZone.ProcDumpTip, "排弃占容 V容");

        return new List<SimProcRow>
        {
            new()
            {
                Name = "穿孔", Code = ProcessZone.ProcDrill, Rgb = SimProcessPalette.RgbDrill,
                Land = Land(ProcessZone.ProcDrill),
                Volume = LedgerVol(ProcessZone.ProcDrill, "控制方量"),
                Equip = "钻机", Work = Work(ProcessType.Drill),
            },
            new()
            {
                Name = "爆破", Code = ProcessZone.ProcBlastGuard, Rgb = SimProcessPalette.RgbBlast,
                Land = Land(ProcessZone.ProcBlastGuard) is var bl && bl != SimProcRow.Dash
                       ? bl + "（警戒范围·禁入区）" : SimProcRow.Dash,
                // M9：写 0 会被读成"这个月一炮没放"
                Volume = SimProcRow.Dash + "（没有自己的方量口径：它爆的就是穿孔那一笔控制方量）",
                Equip = "不派设备（清场）", Work = Work(ProcessType.Blast),
            },
            new()
            {
                Name = "采装", Code = ProcessZone.ProcLoad, Rgb = SimProcessPalette.RgbLoad,
                Land = Land(ProcessZone.ProcLoad), Volume = loadVol,
                Equip = "电铲", Work = Work(ProcessType.Load),
            },
            new()
            {
                Name = "运输", Code = "", Rgb = SimProcessPalette.RgbHaul,
                Land = SimProcRow.Dash + "（线状：要地的是装车点与卸载点两个端点，不是一块地）",
                Volume = haulVol, Equip = "卡车", Work = Work(ProcessType.Haul),
            },
            new()
            {
                Name = "排土·卸载", Code = ProcessZone.ProcDumpTip, Rgb = SimProcessPalette.RgbDumpTip,
                Land = Land(ProcessZone.ProcDumpTip), Volume = dumpVol,
                Equip = "卡车", Work = Work(ProcessType.Dump),
            },
            new()
            {
                Name = "排土·推排", Code = ProcessZone.ProcDumpDoze, Rgb = SimProcessPalette.RgbDumpDoze,
                Land = Land(ProcessZone.ProcDumpDoze),
                Volume = SimProcRow.Dash + "（推排不另计量：卸下来的那一方就是要推的那一方）",
                Equip = "推土机", Work = SimProcRow.Dash,
            },
        };
    }

    /// <summary>口径提示（**永远显示**，不是出了问题才显示）。</summary>
    public const string BasisNote =
        "★ 三个方量口径**不许并成一列**：穿孔＝控制方量、采装＝原位实方 V实、排土＝排弃占容 V容＝V实×Kr。"
      + "谁把它们加到一起，得到的是一个不对应任何真实量的数，而且没有任何地方会报错。";

    /// <summary>台账缺失时的指路（要说到「所以图上会看到什么」）。</summary>
    public const string NoLedgerNote =
        "ⓘ 「地」这一列全是「—」：本期没有工序作业区台账（process_zone）。"
      + "到「作业区划分」生成并入库后，这里才会有各工序的占地与控制方量，"
      + "三维上五道工序也才各归各的地 —— 在那之前它们只能共用作业面的那一个点。";
}
