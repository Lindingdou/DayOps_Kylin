using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Transport;

/// <summary>
/// 开拓运输系统设计的约束条件与参数（user 全局配置，持久化键 "transport.constraints"）。
///
/// 设计原则（见《露天矿采矿手册》道路运输系统 + 项目内讨论）：
///   能从【设备库 / 几何 / 规范】推出来的全部自动算，只把"设备选型"与"价/成本"留给人填。
///
/// 本类同时承载：
///   - 数据（公共自动属性，可被 System.Text.Json 持久化）
///   - 派生量公式（RoadWidthPreview 等方法），供对话框实时预览与命令行回显复用（DRY）。
///
/// 字段分三类（与对话框分区一致）：
///   A. 设备选型——人选，驱动其余几何/能力
///   B. 硬约束默认——规范/经验默认，可改、标来源
///   C. 价/成本——人给，给默认
/// 其余（路面宽度 B、安全车挡高、通过能力、展线长…）一律由下方公式自动算。
/// </summary>
public sealed class TransportConstraintSettings
{
    // ── A. 设备选型（人选，驱动其余几何/能力） ──
    /// <summary>卡车型号档（预设名或"自定义"）。</summary>
    public string TruckClass      { get; set; } = "100t级";
    /// <summary>车宽 m（设备）。→ 路面宽度 B、弯道加宽。</summary>
    public double TruckWidth       { get; set; } = 6.0;
    /// <summary>额定爬坡度 %（设备）。= 限制坡度 i_max 的物理上限。</summary>
    public double TruckClimbPct    { get; set; } = 10.0;
    /// <summary>最小转弯半径 m（设备）。→ 回头/螺旋最小半径下限。</summary>
    public double TruckTurnRadius  { get; set; } = 10.0;
    /// <summary>最大轮胎直径 m（设备）。→ 安全车挡（挡墙）高度。</summary>
    public double TireDiameter     { get; set; } = 2.7;
    /// <summary>车辆轴距 m（设备，前保险杠到后轴近似）。→ 弯道加宽 ε=车道·L²/(2R)。</summary>
    public double VehicleWheelbase { get; set; } = 6.0;
    /// <summary>额定载重 t/车（设备）。把通过能力(车/h)换算成运量(t)，是「运量驱动布线」的关键量。</summary>
    public double TruckPayload      { get; set; } = 90.0;

    // ── B. 硬约束默认（规范/经验，可改、标来源） ──
    /// <summary>限制坡度 i_max %（设备爬坡度 → 规范封顶，默认 8）。</summary>
    public double MaxGradePct      { get; set; } = 8.0;
    /// <summary>车道数。需求驱动，运行时可由产量自动定（见 AutoLaneByCapacity）。</summary>
    public int    LaneCount        { get; set; } = 2;
    /// <summary>车道数是否按各期产量自动确定（true=运行时自动，LaneCount 仅作回退默认）。</summary>
    public bool   AutoLaneByCapacity { get; set; } = true;
    /// <summary>车间安全间隙 c m（规范）。</summary>
    public double LaneClearance    { get; set; } = 1.0;
    /// <summary>安全带 / 路肩 d m（规范）。</summary>
    public double SafetyStrip      { get; set; } = 1.0;
    /// <summary>最小工作平盘宽 W_min m（铲+路+作业空间，经验默认）。限定最小分期宽度。</summary>
    public double MinWorkingBenchWidth { get; set; } = 40.0;

    // ── 缓坡段（长大下坡每隔一段须设缓坡，供制动/散热；规范/经验，可改、标来源） ──
    /// <summary>缓坡段触发：最大连续下降高差 H_seg m。累计陡坡下降到此值须插一段缓坡。≤0=不设缓坡段。经验默认 30。</summary>
    public double MaxContinuousDropM { get; set; } = 30.0;
    /// <summary>缓坡段纵坡 i_ease %（≤ 限制坡度，规范常取 ≤3）。</summary>
    public double EaseGradePct       { get; set; } = 3.0;
    /// <summary>缓坡段最小长度 L_ease m（规范/经验，常取 ≥50）。</summary>
    public double EaseMinLengthM     { get; set; } = 50.0;

    // ── 弯道 / 竖曲线 线形约束（GBJ22-87《厂矿道路设计规范》；中心线布线须满足，可改、标来源） ──
    /// <summary>最小平曲线（圆曲线）半径 R_min m。铰接式专用道可低至 10m；一般按设计车速反算（见 MinCurveRadiusBySpeed）。默认 15。</summary>
    public double MinCurveRadiusM             { get; set; } = 15.0;
    /// <summary>弯道加宽阈值 m：圆曲线半径 ≤ 此值须在内侧加宽路面（GBJ22-87：R≤200m）。</summary>
    public double CurveWidenThresholdM        { get; set; } = 200.0;
    /// <summary>弯道（含回头）最大纵坡 %：急弯/回头处纵坡须折减（经验 ≤4，回头更严）。</summary>
    public double CurveMaxGradePct            { get; set; } = 4.0;
    /// <summary>最大合成坡度 %：纵坡 ⊕ 超高横坡的矢量合成上限（GBJ22-87；寒冷冰冻积雪区 ≤8）。</summary>
    public double MaxResultantGradePct        { get; set; } = 8.0;
    /// <summary>最大超高横坡 %：弯道外侧抬高上限（规范/经验，常 6~8）。</summary>
    public double MaxSuperelevationPct        { get; set; } = 6.0;
    /// <summary>设竖曲线的变坡阈值 %：相邻纵坡代数差 &gt; 此值须设竖曲线（GBJ22-87：2%）。</summary>
    public double VerticalCurveTriggerDiffPct { get; set; } = 2.0;
    /// <summary>最小竖曲线半径 m（圆曲线，凸/凹取严者；按设计车速，规范/经验默认 300）。</summary>
    public double MinVerticalCurveRadiusM     { get; set; } = 300.0;
    /// <summary>最小坡长 m：单一纵坡段长度下限（GBJ22-87：≥50）。</summary>
    public double MinGradeSectionLengthM      { get; set; } = 50.0;

    // ── 通过能力参数（部分默认，用于算每条路最大运输能力） ──
    /// <summary>设计车速 km/h（按道路等级，规范/经验）。</summary>
    public double DesignSpeedKmh   { get; set; } = 25.0;
    /// <summary>车头时距 s（规范/经验）。→ 单车道通过能力。</summary>
    public double HeadwaySec       { get; set; } = 30.0;
    /// <summary>设备利用率 %（经验）。</summary>
    public double UtilizationPct   { get; set; } = 80.0;
    /// <summary>年有效作业时间 h（经验）。</summary>
    public double WorkHoursPerYear { get; set; } = 5000.0;

    // ── C. 价/成本（人给，给默认） ──
    /// <summary>矿石价值 元/t。</summary>
    public double OreValue         { get; set; } = 0.0;
    /// <summary>运输单价 元/(t·km)。</summary>
    public double HaulUnitCost     { get; set; } = 0.0;
    /// <summary>剥离单价 元/m³。</summary>
    public double StripUnitCost    { get; set; } = 0.0;
    /// <summary>油价 元/L。</summary>
    public double FuelPrice        { get; set; } = 0.0;

    // ── D. 优化目标 ──
    /// <summary>优化目标（默认"最小总成本"）。</summary>
    public string Objective        { get; set; } = "最小总成本";

    /// <summary>参考台阶高 m——仅用于"展线长"预览（每降一阶需要的水平展线长），不参与业务。</summary>
    public double RefBenchHeight   { get; set; } = 15.0;

    // ───────── 派生量（自动计算，不持久化业务含义；对话框/命令行复用） ─────────

    /// <summary>路面宽度 B = 车道×车宽 + (车道+1)×车间间隙 + 2×安全带。</summary>
    public double RoadWidthPreview()
        => LaneCount * TruckWidth + (LaneCount + 1) * LaneClearance + 2.0 * SafetyStrip;

    /// <summary>安全车挡（挡墙）高 = 2/3 × 最大轮胎直径。</summary>
    public double BermHeightPreview() => 2.0 / 3.0 * TireDiameter;

    /// <summary>单车道通过能力（车/h）= 3600 ÷ 车头时距。</summary>
    public double LaneCapacityPreview() => HeadwaySec > 1e-6 ? 3600.0 / HeadwaySec : 0.0;

    /// <summary>全路通过能力（车/h，计利用率）= 单车道 × 车道数 × 利用率。</summary>
    public double RoadCapacityPreview()
        => LaneCapacityPreview() * LaneCount * (UtilizationPct / 100.0);

    /// <summary>该路年运输能力（t/年）= 全路通过能力(车/h) × 年作业时间 × 额定载重。供「运量驱动布线」做运量约束。</summary>
    public double AnnualCapacityTonsPreview()
        => RoadCapacityPreview() * WorkHoursPerYear * TruckPayload;

    /// <summary>展线长（每降 RefBenchHeight）= H ÷ (i/100)。</summary>
    public double DevelopmentLengthPreview()
        => MaxGradePct > 1e-6 ? RefBenchHeight / (MaxGradePct / 100.0) : 0.0;

    /// <summary>
    /// 按设计车速反算最小平曲线半径 m：R = v² / (127·(μ+e))，μ=横向力系数(默认 0.15)，e=超高。
    /// 布线取 max(此值, 车辆最小转弯半径 TruckTurnRadius) 作 R_min（物理 + 设备双下限）。
    /// <para><b>忠实逐字移植</b> <c>MineAssLib.Models.TransportConstraintSettings</c>（仅改命名空间）。</para>
/// </summary>
    public double MinCurveRadiusBySpeed(double frictionMu = 0.15)
    {
        double e = MaxSuperelevationPct / 100.0;
        double denom = 127.0 * (frictionMu + e);
        return denom > 1e-6 ? DesignSpeedKmh * DesignSpeedKmh / denom : MinCurveRadiusM;
    }

    /// <summary>布线实际采用的最小平曲线半径 = max(车速反算值, 车辆最小转弯半径, 设定下限)。</summary>
    public double EffectiveMinCurveRadiusM()
        => Math.Max(Math.Max(MinCurveRadiusBySpeed(), TruckTurnRadius), MinCurveRadiusM);

    /// <summary>限制坡度是否超过设备爬坡度（超了不安全，需下调）。</summary>
    public bool GradeExceedsClimb() => MaxGradePct > TruckClimbPct + 1e-9;
}
