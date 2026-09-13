using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// <b>主体案例</b> —— 全链缺省值的<b>唯一来源</b>（2026-08-09 现场定：2026 年 · 年煤 2000 万t）。
///
/// <para><b>为什么要有它</b>：此前每个窗口各写各的缺省，串起来根本不自洽 ——
/// 基础约束年目标 1000万t/6500万m³（剥采比 6.5），采掘单元清单手填却是 50万t/150万m³（剥采比 3.0）；
/// 计划年 2027 而台账期次是 2026-08，**期次永远对不上逐月配置表**，排产每次退回手填；
/// 设备缺省 4 台 × 28万m³ 只有月需求的 1/6，默认案例一跑就欠产。
/// 这些都不报错 —— 打开哪个窗口就信哪个数，谁也对不上谁。</para>
///
/// <para><b>纪律</b>：
/// ① 缺省值只在这里写，窗口一律引用；② 这里的数<b>必须互相自洽</b>（判据 D3 钉住：
/// 月缺省 = 年目标÷12 · 煤×剥采比 = 排弃量 · 设备总能力 ≥ 月总作业量 · 演示期次年 = 计划年）；
/// ③ 改盘子只改这一处，其余全部跟着走。</para>
///
/// <para><b>它不是"真计划"</b>：只是**打开软件时的起点**。人填了自己的数就以人的为准，
/// 这里一个数都不会回头覆盖（`ShortTermBase` 只在**首次创建**时取一次）。</para>
/// </summary>
public static class PlanCase
{
    // ── 时间骨架 ────────────────────────────────────────────────────────
    /// <summary>计划年度。<b>取 2026 是有原因的</b>：桌面台账里存着 2026-08 那一期，
    /// 计划年不是 2026 的话，采掘单元排产按期次去逐月配置表取数会**取不到**、退回手填。</summary>
    public const int PlanYear = 2026;
    public const int StartMonth = 1;
    public const int MonthCount = 12;
    /// <summary>演示用期次（台账里真实存在的那一期）。</summary>
    public const string DemoPeriod = "2026-08";

    // ── 年度盘子（现场给的就是这两个数）────────────────────────────────
    /// <summary>年采出目标（万t）—— 现场定。</summary>
    public const double AnnualCoalWanT = 2000;
    /// <summary>年剥离目标（万m³ 原位实方）。</summary>
    public const double AnnualStripWanM3 = 6500;

    /// <summary>年（= 生产）剥采比 m³/t —— <b>派生量，不单独填</b>。约 3.25。</summary>
    public static double StripRatio => AnnualStripWanM3 / AnnualCoalWanT;

    // ── 月均（派生：年 ÷ 月数）──────────────────────────────────────────
    /// <summary>月均采出（万t）≈ 166.7。</summary>
    public static double MonthlyCoalWanT => AnnualCoalWanT / MonthCount;
    /// <summary>月均剥离 / 排弃（万m³ 实方）≈ 541.7。</summary>
    public static double MonthlyStripWanM3 => AnnualStripWanM3 / MonthCount;

    // ── 物料（三处口径的唯一来源）──────────────────────────────────────
    /// <summary>煤视密度 t/m³。</summary>
    public const double CoalDensity = 1.35;
    /// <summary>岩视密度 t/m³。</summary>
    public const double RockDensity = 2.5;
    /// <summary>残余膨胀系数：排土占容 = 实方 × Kr。<b>不是松方 Ks</b>。</summary>
    public const double RockKr = 1.15;

    // ── 现场能力 ────────────────────────────────────────────────────────
    /// <summary>月标准作业日。</summary>
    public const double StandardWorkdays = 25;
    public const int ShiftsPerDay = 3;
    /// <summary>单台挖装设备满月作业能力（万m³，含采+剥）。</summary>
    public const double EquipMonthlyCapacityWanM3 = 28;
    /// <summary>设备完好率 %。</summary>
    public const double EquipmentAvailabilityPct = 82;

    /// <summary>
    /// 本案例每月要挖的**总方量**（万m³）= 剥离 + 采出折方。设备台数就按它反推。
    /// </summary>
    public static double MonthlyTotalWanM3 => MonthlyStripWanM3 + MonthlyCoalWanT / CoalDensity;

    /// <summary>
    /// 主采设备台数 —— <b>按盘子反推，不是拍的</b>：
    /// ⌈月总方量 ÷ (单台满月能力 × 完好率)⌉ ≈ 29 台。
    /// <para>此前缺省 4 台，只有需求的约 1/6：默认案例一跑就欠产，
    /// 而那是**参数问题伪装成算法行为**（本仓库在工作历那条轴上已经踩过一次）。
    /// 参照：库里在籍电铲 42 台，29 台是对得上的量级。</para>
    /// </summary>
    public static int EquipmentCount =>
        (int)Math.Ceiling(MonthlyTotalWanM3 / (EquipMonthlyCapacityWanM3 * EquipmentAvailabilityPct / 100.0));

    // ── 排产口径 ────────────────────────────────────────────────────────
    /// <summary>采空区回填比（内排累计占容 ≤ 采空区 × 它）。操作性损失，实测每 0.1 ≈ 内排率 10.9 个百分点。</summary>
    public const double VoidFillFactor = 0.6;
    /// <summary>一个采掘单元本月最多摆几台挖装设备（>1 才会出现同一个体上多台并行）。</summary>
    public const int MaxLoadersPerUnit = 1;
    /// <summary>转场免费距离（m）。</summary>
    public const double FreeRelocationM = 300;
    /// <summary>履带/轮式机械转场占几个工日。</summary>
    public const int RelocationDaysTracked = 1, RelocationDaysWheeled = 0;

    /// <summary>
    /// 把本案例铺进一份新的基础约束。<b>只在 <c>ShortTermSchemeStore.Base</c> 首次创建时调一次</b> ——
    /// 人改过之后绝不回头覆盖。
    /// </summary>
    public static ShortTermBase NewBase()
    {
        var b = new ShortTermBase
        {
            PlanYear = PlanYear,
            StartMonth = StartMonth,
            MonthCount = MonthCount,
            AnnualCoalTargetWanT = AnnualCoalWanT,
            AnnualStripTargetWanM3 = AnnualStripWanM3,
        };
        b.Field.StandardWorkdays = StandardWorkdays;
        b.Field.ShiftsPerDay = ShiftsPerDay;
        b.Field.EquipmentCount = EquipmentCount;
        b.Field.EquipMonthlyCapacityWanM3 = EquipMonthlyCapacityWanM3;
        b.Field.EquipmentAvailabilityPct = EquipmentAvailabilityPct;
        return b;
    }

    /// <summary>一行摘要（窗口状态栏/报告里直接用，别各处再编一遍）。</summary>
    public static string Caption =>
        $"主体案例：{PlanYear} 年 · 年采出 {AnnualCoalWanT:0} 万t · 年剥离 {AnnualStripWanM3:0} 万m³"
        + $"（剥采比 {StripRatio:0.00}）· 月均 {MonthlyCoalWanT:0.#} 万t / {MonthlyStripWanM3:0.#} 万m³"
        + $" · 主采设备 {EquipmentCount} 台 · 演示期次 {DemoPeriod}";
}
