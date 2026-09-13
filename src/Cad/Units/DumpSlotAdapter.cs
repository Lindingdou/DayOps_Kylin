// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/DumpSlotAdapter.cs（逐行对应；仅命名空间适配；原 MineAssLib.Models.WorkLineGeometry 在 Kylin 叫 WorkLineSamples）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>
/// 把「排土条带·潜在排土位置」（<see cref="DumpStripPlanner"/>，口径 D1–D6）的位置清单
/// 适配成采排配对吃的 <see cref="DumpSlot"/>。
///
/// <para><b>不重造几何</b>：库容（占容方 D6）、质心、走向长、条带宽那边全算好了，这里只做口径对齐。</para>
///
/// <para><b>⚠ 极性必须翻</b>：<c>Cell.LevelIndex</c> 的 <b>1 = 最上一级</b>（图上从上往下数），
/// 而排土台阶是<b>自下而上</b>承接的（时空约束第 2 条：同一时刻只有最低的未满级在收）。
/// <see cref="DumpSlot.Level"/> 小的先填，所以必须<b>倒过来</b>。
/// 不翻的话计划会从山顶往下排 —— 图上看着有台阶、现场没法卸。这类极性错在本仓库有前科
/// （内核 <c>CarveStripInput::isDump</c> 的三处镜像做了却长期没人调用、没测试走过）。</para>
/// </summary>
public static class DumpSlotAdapter
{
    /// <summary>
    /// 转换。<paramref name="dumpName"/> 空则从 <c>Cell.Code</c> 前缀（形如 "外排1-L3-P02-S05"）取。
    /// </summary>
    /// <param name="isInternal">是否内排（有启用时机 + 受采空区约束）。界面上的排土场类型说了算。</param>
    /// <param name="availableFromMonth">用户指定的最早启用月（内排的硬下限；外循环只会把它往后推）。</param>
    /// <param name="haulKm">静态兜底运距。外循环接管后会按逐月几何覆盖它。</param>
    public static List<DumpSlot> ToSlots(IReadOnlyList<DumpStripPlanner.Cell>? cells,
                                         string dumpName = "", bool isInternal = false,
                                         int availableFromMonth = 1, double haulKm = 0)
    {
        var list = new List<DumpSlot>();
        if (cells == null || cells.Count == 0) return list;

        // 极性翻转的基准：最大 LevelIndex（图上最下一级）映射成 Level 0（最先接收）
        int maxLv = cells.Max(c => c.LevelIndex);

        foreach (var c in cells)
        {
            if (c == null || c.CapacityM3 <= 0) continue;      // 退化位置不进清单（DumpStripPlanner 那边已记账）
            string name = string.IsNullOrWhiteSpace(dumpName) ? DumpNameFromCode(c.Code) : dumpName;
            list.Add(new DumpSlot
            {
                DumpName = string.IsNullOrEmpty(name) ? "排土场" : name,
                Level = maxLv - c.LevelIndex,                   // ★ 自下而上：最下一级 = 0
                // 同一级内：先把当前排土线那一带（StepIndex=1）的各幅填满，再往前推一带。
                //
                // 【子号必须进排序键】外凸拐角把带撑长之后，一带会切成几个位置，
                // 它们的 (级,幅,带) 完全相同、只差子号。不带子号的话这几个位置排序键相撞，
                // 分配器 `s.Order < head.Order` 挑不出先后，同一份输入排出来的顺序就不定了。
                // 位次 = 幅号×100 + 子号：幅之间的先后不变，只是把幅内的并列拆开。
                // ★ 走共享件：此前这里是 (StepIndex−1)×1e6，与台账那条（Band×1e6）差整整 1e6，
                //   同一个排土位置在两条路上得到两个码 —— 会话内排出来的去向码，三维解码时一笔都对不上。
                //   已按现场决定【统一到台账口径、老码作废】。
                Order = DumpSlotCode.OrderOf(c.StepIndex, c.PanelIndex, c.SubIndex),
                CapacityM3 = c.CapacityM3,                      // 已是占容方（D6），不再换算
                Cx = c.Cx, Cy = c.Cy, Cz = c.Cz,
                IsInternal = isInternal,
                AvailableFromMonth = Math.Max(1, availableFromMonth),
                HaulKm = haulKm,
            });
        }
        return list;
    }

    /// <summary>"外排1-L3-P02-S05" → "外排1"。取不出来返回空串（调用方兜底）。</summary>
    internal static string DumpNameFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        int i = code!.IndexOf('-');
        return i > 0 ? code[..i] : code;
    }

    /// <summary>
    /// 把各排土位置的质心投影到<b>推进轴 u</b>（与 <see cref="RockProfile"/> 同轴），
    /// 供外循环反算内排启用时机与逐月运距。
    /// <para>投不上的位置（落在工作线扫掠域之外）给 <see cref="double.NegativeInfinity"/> ——
    /// 那意味着"它一直在采场后方"，采空区一形成就可用。**不静默给 0**：0 是轴上的一个真实位置，
    /// 拿它冒充"投不上"会让那些位置在错误的月份启用。</para>
    /// </summary>
    public static double[] ProjectToAdvanceAxis(IReadOnlyList<DumpSlot> slots,
                                                IReadOnlyList<WorkLineSamples> workLines,
                                                double latTol = 20.0)
    {
        var u = new double[slots?.Count ?? 0];
        if (slots == null || slots.Count == 0) return u;
        var proj = WorkLineProjector.Build(workLines ?? Array.Empty<WorkLineSamples>(), latTol);
        for (int i = 0; i < slots.Count; i++)
            u[i] = proj.HasAny && proj.TryProject(slots[i].Cx, slots[i].Cy, out double a0, out _)
                 ? a0 : double.NegativeInfinity;
        return u;
    }

    /// <summary>
    /// 各位置到推进轴的<b>横向偏距</b>（km）—— 运距的固定分量。
    /// 用质心到最近工作线段的垂距近似；投不上的用到工作线质心的平面距离兜底。
    /// </summary>
    public static double[] LateralOffsetsKm(IReadOnlyList<DumpSlot> slots,
                                            IReadOnlyList<WorkLineSamples> workLines)
    {
        int n = slots?.Count ?? 0;
        var off = new double[n];
        if (n == 0) return off;
        // 工作线所有基线点的平面质心 —— 只用来兜底，够粗但标得出来
        double sx = 0, sy = 0; int cnt = 0;
        foreach (var wl in workLines ?? Array.Empty<WorkLineSamples>())
            foreach (var b in wl.Baseline) { sx += b.X; sy += b.Y; cnt++; }
        double gx = cnt > 0 ? sx / cnt : 0, gy = cnt > 0 ? sy / cnt : 0;
        for (int i = 0; i < n; i++)
        {
            double dx = slots![i].Cx - gx, dy = slots[i].Cy - gy;
            off[i] = Math.Sqrt(dx * dx + dy * dy) / 1000.0 * 0.25;   // 只取一小部分作为固定分量，主项走 u 轴距离
        }
        return off;
    }

    /// <summary>一行诊断：转出来多少位置、分几级、总库容、极性翻转确认。</summary>
    public static string Summary(IReadOnlyList<DumpSlot> slots)
    {
        if (slots == null || slots.Count == 0) return "排土位置：0 个";
        var lv = slots.Select(s => s.Level).Distinct().OrderBy(x => x).ToList();
        double cap = slots.Sum(s => s.CapacityM3);
        var bottom = slots.Where(s => s.Level == lv[0]).ToList();
        return $"排土位置 {slots.Count} 个 · {lv.Count} 级（Level {lv[0]}~{lv[^1]}，"
             + $"Level {lv[0]} = 最下一级、最先承接，标高 {bottom.Average(s => s.Cz):0.#}m）"
             + $" · 总库容 {cap / 1e4:0.0}万m³占容";
    }
}
