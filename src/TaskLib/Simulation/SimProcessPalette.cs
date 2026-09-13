// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimProcessPalette.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.Data.Entities;   // ProcessZone

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  工序配色 —— **一处定义**（PS3）。
//
//  在这之前，工序色写死在 `DynamicSimWindow.PushActivities` 的一个 switch 里，
//  甘特那边另写一份，工序作业区窗口再写一份。三份各自都对，改一处就悄悄分叉：
//  现象是「同一道工序在两个窗里是两个颜色」，而没有任何东西会报错。
//
//  ── 两套键，同一张表 ──
//  · <see cref="ProcessType"/>：任务/活动那一侧的枚举（穿孔/爆破/采装/运输/排土/检修）。
//  · 工序码字符串：工序作业区那一侧（`process_zone.process`，含 <c>blast_guard</c> /
//    <c>dump_tip</c> / <c>dump_doze</c> 三个枚举里没有的细分）。
//  两侧不是一一对应 —— 排土在任务侧是一道工序，在地的那一侧是**卸载带 + 推排带两块地**
//  （PS5：两块地两台设备，卡车与推土机，不许合并成一块）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>工序在图上的表现方式。</summary>
public enum SimProcessDraw : byte
{
    /// <summary>作业区：有人有设备在这儿干活 —— 实线闭合环 + 淡填充。</summary>
    WorkArea = 0,
    /// <summary>禁入区：这段时间谁都不许进（爆破警戒）—— 只画虚线边界，**不填充**（PS4）。</summary>
    KeepOut = 1,
}

/// <summary>工序配色与图面表现（<b>唯一定义处</b>）。</summary>
public static class SimProcessPalette
{
    // ── 工序码 → 本色（0xRRGGBB）────────────────────────────────────────────
    //  色相按工序链走，不是随手挑的：
    //  紫（穿孔，最超前）→ 红（爆破，禁入）→ 蓝（采装，主工序）→ 青（运输，线状）
    //  → 琥珀（排土·卸载）→ 深琥珀（排土·推排，同族异调 —— 它俩是同一个排土场的两条带）。
    public const uint RgbDrill = 0x8B5CF6u;   // 穿孔 紫
    public const uint RgbBlast = 0xEF4444u;   // 爆破 红
    public const uint RgbLoad = 0x3B82F6u;    // 采装 蓝
    public const uint RgbHaul = 0x10B981u;    // 运输 青
    public const uint RgbDumpTip = 0xF59E0Bu; // 排土·卸载 琥珀
    public const uint RgbDumpDoze = 0xB45309u;// 排土·推排 深琥珀
    public const uint RgbIdle = 0x9CA3AFu;    // 检修/空闲 灰

    /// <summary>工序码（<c>process_zone.process</c>）→ 本色。认不出的给灰，不冒充成某一类。</summary>
    public static uint RgbOfCode(string? code) => (code ?? "").Trim() switch
    {
        ProcessZone.ProcDrill => RgbDrill,
        ProcessZone.ProcBlastGuard => RgbBlast,
        ProcessZone.ProcLoad => RgbLoad,
        ProcessZone.ProcDumpTip => RgbDumpTip,
        ProcessZone.ProcDumpDoze => RgbDumpDoze,
        _ => RgbIdle,
    };

    /// <summary>任务侧工序 → 本色。</summary>
    public static uint RgbOf(ProcessType p) => p switch
    {
        ProcessType.Drill => RgbDrill,
        ProcessType.Blast => RgbBlast,
        ProcessType.Load => RgbLoad,
        ProcessType.Haul => RgbHaul,
        ProcessType.Dump => RgbDumpTip,
        _ => RgbIdle,
    };

    /// <summary>
    /// 这块地是作业区还是禁入区（PS4）。
    /// <para>爆破警戒区的语义与其余四类**相反** —— 其余是「谁去这儿干活」，它是「这段时间谁都不许进」，
    /// 而且它不派设备（派的是清场，人不是机）。所以它绝不能与作业区同笔画：
    /// 实测本矿 2026-08 的警戒区一块就 447 万 m²，是全部穿孔区面积的 32 倍，
    /// 一填充就把整张图盖住，"工序分不出来"反而更严重。</para>
    /// </summary>
    public static SimProcessDraw DrawOf(string? code)
        => (code ?? "").Trim() == ProcessZone.ProcBlastGuard ? SimProcessDraw.KeepOut : SimProcessDraw.WorkArea;

    /// <summary>工序码 → 中文名（转发到 <see cref="ProcessZone.DisplayName"/>，不另写一份）。</summary>
    public static string NameOfCode(string? code) => ProcessZone.DisplayName(code);

    /// <summary>
    /// 一道工序在图上的**排序号**（按现场先后：穿孔 → 爆破 → 采装 → 运输 → 排土·卸载 → 排土·推排）。
    /// <para>图例、工序读数表、工序链带三处共用同一个序 —— 各排各的就会出现
    /// 「图例里爆破在采装后面、链带里在前面」这种自相矛盾。</para>
    /// </summary>
    public static int OrderOfCode(string? code) => (code ?? "").Trim() switch
    {
        ProcessZone.ProcDrill => 0,
        ProcessZone.ProcBlastGuard => 1,
        ProcessZone.ProcLoad => 2,
        ProcessZone.ProcDumpTip => 4,
        ProcessZone.ProcDumpDoze => 5,
        _ => 9,
    };

    /// <summary>任务侧工序的排序号（与 <see cref="OrderOfCode"/> 同一把尺）。运输落在采装与排土之间。</summary>
    public static int OrderOf(ProcessType p) => p switch
    {
        ProcessType.Drill => 0,
        ProcessType.Blast => 1,
        ProcessType.Load => 2,
        ProcessType.Haul => 3,
        ProcessType.Dump => 4,
        _ => 9,
    };

    /// <summary>加 alpha。</summary>
    public static uint With(uint rgb, byte alpha) => ((uint)alpha << 24) | (rgb & 0x00FFFFFFu);
}
