// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/EquipStageBridge.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.TaskLib.Simulation;
namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 设备指派结果 → 三维动态模拟的<b>设备符号行</b>的转换器（`MachineAssignment` → <c>EquipPlacementRow</c>）。
///
/// <para><b>为什么必须有它</b>：动态模拟窗口<b>逐帧驱动</b>设备符号阶段
/// （<c>EquipmentStage.Current.ApplyFrame(frame)</c>），但全仓<b>没有任何人调过</b>
/// <c>EquipmentStage.Stage(rows, periodKey)</c> —— 那个阶段一直在跑，
/// <b>而数据从来没喂进去</b>：屏幕上一个设备符号都没有，只有一句"本期没有任何设备指派"。
/// 这是本仓库反复出现的"零调用方"形态的又一例。</para>
///
/// <para><b>它只做映射，不做决策</b>：谁在哪个单元、干几天、绑哪台铲，全部由
/// <see cref="EquipmentAssigner"/> 解出来。这里换的只是<b>类型与枚举</b>。
/// 一旦在这儿"补"一台设备或改一个工日，就成了第二套指派。</para>
/// </summary>
public static class EquipStageBridge
{
    /// <summary>
    /// 转换。<paramref name="sourceLabel"/> 会写进每一行的来源（<b>不许留空</b> ——
    /// 图上那台铲是"这次指派的"还是"在籍清单兜底的"，看的就是它）。
    /// </summary>
    public static List<EquipPlacementRow> ToPlacements(EquipmentAssignResult? res, string sourceLabel)
    {
        var rows = new List<EquipPlacementRow>();
        if (res == null || !res.Success) return rows;

        foreach (var a in res.Assignments)
        {
            if (a == null || a.MachineId.Length == 0) continue;   // 空机号会被阶段拒（无法去重、无法追溯）
            rows.Add(new EquipPlacementRow
            {
                MachineId = a.MachineId,
                Kind = MapKind(a.MachineKind),
                Model = a.Model,
                UnitId = a.UnitId,
                State = MapState(a.Role),
                ServesMachineId = a.ServesMachineId,
                Seq = a.Seq,
                StartDay = a.StartDay,
                EndDay = a.EndDay,
                SourceNote = sourceLabel,
            });
        }
        return rows;
    }

    /// <summary>
    /// 把一次指派摆到三维舞台上。<b>期次键走阶段自己的写法</b>（`EquipmentStage.PeriodKey`），
    /// 不在这儿另拼一个 —— 两处各拼一套期次键就永远对不上帧，而且不报错。
    /// </summary>
    /// <returns>人读的一句话（摆了几台 / 为什么没摆）。</returns>
    public static string Stage(EquipmentAssignResult? res, int year, int month)
    {
        if (res == null || !res.Success) return "· 三维设备符号：这次没有可用的指派结果，未摆。";

        string period = EquipmentStage.PeriodKey(year, month);
        var rows = ToPlacements(res, $"EquipmentAssigner {period}");
        if (rows.Count == 0) return "· 三维设备符号：指派结果里一台设备都没有，未摆。";

        var stage = EquipmentStage.Current;
        if (!stage.Available)
            // 宿主实体能力没就绪（面板可能在主图之外打开）——**如实说**，别让人以为摆过了
            return $"· 三维设备符号：已备好 {rows.Count} 台（{period}），"
                 + "但当前没有可用的绘图能力 —— 打开「生产进度计划过程模拟」时会重新摆。";

        try
        {
            var r = stage.Stage(rows, period);
            // ⚠ 喂了几行 ≠ 摆上几台：没坐标的、被拒的、建体失败的都会掉队 —— 逐项报，别只报"成功"
            int lost = r == null ? 0 : r.SkippedNoPosition + r.Rejected + r.DroppedOnBuildFailure;
            return $"· 三维设备符号：{period} 喂 {rows.Count} 台 → 摆上 {r?.Placed ?? 0} 台"
                 + $"（挖装 {rows.Count(x => x.State == EquipState.Working)}"
                 + $" · 运输 {rows.Count(x => x.State == EquipState.Hauling)}"
                 + $" · 转场 {rows.Count(x => x.State == EquipState.Relocating)}）"
                 + (lost > 0
                    ? $"　◆ {lost} 台没摆上（无位置 {r!.SkippedNoPosition} · 被拒 {r.Rejected} · 建体失败 {r.DroppedOnBuildFailure}）"
                    : "");
        }
        catch (Exception ex)
        {
            return $"◆ 三维设备符号摆位失败（{ex.GetType().Name}：{ex.Message}）—— 模拟里会没有设备。";
        }
    }

    // ── 枚举映射：两侧各自独立定义，这里是**唯一**的对照表 ────────────────
    //    ★ 开放给外部（设备工艺树的图标要按类别取符号）—— 别在别处再写一遍：
    //      多一份对照表，加一个新类别时就会有一处忘了改，而那一处会静默落到 Other。
    public static EquipKind MapKind(MachineKind k) => k switch
    {
        MachineKind.Shovel => EquipKind.Shovel,
        MachineKind.Truck => EquipKind.Truck,
        MachineKind.Drill => EquipKind.Drill,
        MachineKind.Loader => EquipKind.Loader,
        MachineKind.Dozer => EquipKind.Dozer,
        MachineKind.Grader => EquipKind.Grader,
        MachineKind.WaterTruck => EquipKind.WaterTruck,
        _ => EquipKind.Other,
    };

    /// <summary>
    /// 角色 → 状态。<b>只有指派器真出的那三种</b>；
    /// 待命 / 检修是"在籍但本月没派到活"，由 <c>EquipmentStage.FeedIdleFleet</c> 另喂，不在这里编。
    /// </summary>
    private static EquipState MapState(MachineRole r) => r switch
    {
        MachineRole.Excavate => EquipState.Working,
        MachineRole.Haul => EquipState.Hauling,
        MachineRole.Relocate => EquipState.Relocating,
        _ => EquipState.Working,
    };
}
