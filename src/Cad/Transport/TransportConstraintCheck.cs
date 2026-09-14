using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Transport;

/// <summary>
/// 「约束条件设置」的校验与派生量文案（从原 <c>TransportConstraintDialog.xaml.cs</c> 里切出来的纯函数）。
///
/// <b>为什么单切一层</b>：原版这几个判据写在窗口的 code-behind 里，预览与确认共用同一份 ——
/// "预览说没问题、确认却拦下来"这种事从设计上就不可能。切成纯函数后同一份判据还能脱 GUI 验收，
/// 语义与原版一字不差。
/// </summary>
public static class TransportConstraintCheck
{
    private const double Eps = 1e-9;

    /// <summary>软警阈值：设定下限低于车速反算值的这个比例就提示"形同虚设"。</summary>
    private const double SoftCurveRadiusRatio = 0.8;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// 硬错 —— 参数之间互相打架，点确认要拦下来（空列表 = 放行）。
    /// 预览与确认共用，避免两处判据走偏。
    /// </summary>
    public static List<string> CollectHardErrors(TransportConstraintSettings s)
    {
        var list = new List<string>();
        if (s.TruckWidth <= 0) list.Add("车宽必须 > 0");
        if (s.LaneCount < 1) list.Add("车道数至少为 1");
        if (s.MaxGradePct <= 0) list.Add("限制坡度必须 > 0");
        if (s.CurveMaxGradePct > s.MaxGradePct + Eps)
            list.Add($"弯道最大纵坡 {F1(s.CurveMaxGradePct)}% 比限制坡度 {F1(s.MaxGradePct)}% 还陡（弯道只能更缓）");
        if (s.EaseGradePct > s.MaxGradePct + Eps)
            list.Add($"缓坡段纵坡 {F1(s.EaseGradePct)}% 比限制坡度 {F1(s.MaxGradePct)}% 还陡（缓坡段只能更缓）");
        if (s.MaxSuperelevationPct >= s.MaxResultantGradePct - Eps)
            list.Add($"最大超高横坡 {F1(s.MaxSuperelevationPct)}% 单独就吃满了最大合成坡度 "
                   + $"{F1(s.MaxResultantGradePct)}%，纵坡没有余量");
        if (s.EaseMinLengthM > 0 && s.EaseMinLengthM < s.MinGradeSectionLengthM - Eps)
            list.Add($"缓坡段最小长度 {F1(s.EaseMinLengthM)} m 不满足最小坡长 {F1(s.MinGradeSectionLengthM)} m");
        return list;
    }

    /// <summary>软警 —— 口径可疑但不致命，只提示、不拦确认。</summary>
    public static List<string> CollectSoftWarnings(TransportConstraintSettings s)
    {
        var list = new List<string>();
        double b = s.RoadWidthPreview();
        if (b > s.MinWorkingBenchWidth + Eps)
            list.Add($"路面宽度 B {F(b)} m 已超过最小工作平盘宽 {F(s.MinWorkingBenchWidth)} m，平盘塞不下这条路");
        double bySpeed = s.MinCurveRadiusBySpeed();
        if (bySpeed > Eps && s.MinCurveRadiusM < bySpeed * SoftCurveRadiusRatio)
            list.Add($"设定最小平曲线半径 {F(s.MinCurveRadiusM)} m 明显低于按车速反算的 {F(bySpeed)} m，"
                   + "该下限形同虚设（布线实际取反算值）");
        return list;
    }

    /// <summary>限制坡度超过设备爬坡度的告警（超了不安全，需下调）；不超返回空串。</summary>
    public static string GradeWarning(TransportConstraintSettings s)
        => s.GradeExceedsClimb()
            ? $"⚠ 限制坡度 {F1(s.MaxGradePct)}% 超过设备爬坡度 {F1(s.TruckClimbPct)}%，请下调"
            : "";

    /// <summary>
    /// 把 <see cref="TransportConstraintSettings.EffectiveMinCurveRadiusM"/> 的"三取一"写成一行，
    /// 并**点名是哪一项在控制** —— 否则用户改了"设定下限"看不到任何变化，会以为参数没生效。
    /// </summary>
    public static string DescribeEffectiveCurveRadius(TransportConstraintSettings s)
    {
        double bySpeed = s.MinCurveRadiusBySpeed();
        double eff = s.EffectiveMinCurveRadiusM();
        string who, rest;
        if (eff <= bySpeed + Eps)
        {
            who = "车速反算控制";
            rest = $"车辆转弯 {F(s.TruckTurnRadius)} / 设定下限 {F(s.MinCurveRadiusM)}";
        }
        else if (eff <= s.TruckTurnRadius + Eps)
        {
            who = "车辆转弯控制";
            rest = $"车速反算 {F(bySpeed)} / 设定下限 {F(s.MinCurveRadiusM)}";
        }
        else
        {
            who = "设定下限控制";
            rest = $"车速反算 {F(bySpeed)} / 车辆转弯 {F(s.TruckTurnRadius)}";
        }
        return $"实际最小平曲线半径 = {F(eff)} m（{who}；{rest}）";
    }

    /// <summary>⑧ 自动计算区的逐行文案（实时预览，无需填写）。</summary>
    public static List<string> DerivedLines(TransportConstraintSettings s) => new()
    {
        $"路面宽度 B = {F(s.RoadWidthPreview())} m  （{s.LaneCount} 车道 × {F1(s.TruckWidth)} + 间隙 + 安全带）",
        $"安全车挡高 = {s.BermHeightPreview().ToString("0.00", Inv)} m  （2/3 × 轮胎直径 {F1(s.TireDiameter)}）",
        $"通过能力 ≈ 单车道 {s.LaneCapacityPreview().ToString("0", Inv)} 车/h"
            + $"；全路计利用率 {s.RoadCapacityPreview().ToString("0", Inv)} 车/h",
        $"单条路年运力 ≈ {(s.AnnualCapacityTonsPreview() / 10000.0).ToString("0", Inv)} 万t/年"
            + $"  （理论上限：车/h × 年作业时间 × 载重 {F1(s.TruckPayload)}t）",
        $"展线长（每降 H = {F1(s.RefBenchHeight)} m）= {F(s.DevelopmentLengthPreview())} m  （H ÷ i）",
        DescribeEffectiveCurveRadius(s),
    };

    /// <summary>
    /// 切车型时的"未随车型更新"提示。<paramref name="preset"/> 没提供的字段仍是切换前的旧值，
    /// **必须当场提示** —— 静默沿用就是错算的来源。全都提供了则返回空串。
    /// </summary>
    public static string TruckGapNote(TruckPreset? preset)
    {
        if (preset == null) return "";
        var missing = preset.MissingFieldNames();
        if (missing.Count == 0) return "";
        return $"⚠ 「{preset.DisplayName}」未提供：{string.Join(" / ", missing)}"
             + "——以上仍是切换前的旧值，未随车型更新，请手工确认后再用（轴距直接进弯道加宽公式）。";
    }

    /// <summary>车型下拉的来源说明。</summary>
    public static string TruckSourceNote(int registryCount)
        => registryCount > 0
            ? $"型号来源：吨级经验档 {TruckPresets.Empirical.Count} 档 + 设备库在册 {registryCount} 种"
              + "（带「· 在册」后缀；在册车型只带出设备库确有的参数）"
            : "型号来源：仅吨级经验档（设备库未就绪或库内无在册卡车型号）";

    private static string F(double v) => v.ToString("0.0", Inv);
    private static string F1(double v) => v.ToString("0.#", Inv);
}
