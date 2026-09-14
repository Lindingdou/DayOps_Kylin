using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Cad.Transport;

/// <summary>
/// 「延拓参数中心」的持久化设置（忠实移植 <c>RoadLib.Evolution.ExtendTriggerSettings</c>）。
/// 把延拓相关全部参数收编为一处统一默认 —— 触发条件（推进步距/时间步 + 临时道路回收）
/// + 演化判定阈值（= <see cref="RoadEvolutionOptions"/>）+ 推进方向先验。
///
/// 默认值与 <see cref="RoadEvolutionOptions"/> 现默认一致 ⇒ **不动设置时行为不变（零回归）**。
///
/// 消费方：演化对比读它作阈值（在此之前 Kylin 侧那里写死的是 <c>new RoadEvolutionOptions()</c>，
/// 参数中心改了也不生效 —— 本轮一并接上，否则这就是个存了没人读的设置）。
/// </summary>
public sealed class ExtendTriggerSettings
{
    /// <summary>持久化键（与原版一致）。</summary>
    public const string SettingsKey = "road.extend.trigger";

    /// <summary>触发模式：推进步距 / 时间步。</summary>
    public enum Mode
    {
        /// <summary>推进步距达到阈值时提示/触发延拓。</summary>
        AdvanceStep,
        /// <summary>每经过 N 个开采期提示/触发延拓。</summary>
        TimeStep,
    }

    // ── A 触发条件 ──
    /// <summary>触发模式。默认按推进步距。</summary>
    public Mode TriggerMode { get; set; } = Mode.AdvanceStep;
    /// <summary>推进步距阈值 m（达到→提示/触发延拓）。默认 50 m。</summary>
    public double AdvanceStepM { get; set; } = 50.0;
    /// <summary>时间步阈值：每 N 期触发一次。默认 1 期。</summary>
    public int TimeStepPeriods { get; set; } = 1;
    /// <summary>采空后自动回收临时道路。默认关。</summary>
    public bool AutoReclaimTemporary { get; set; } = false;

    // ── B 演化判定阈值（= RoadEvolutionOptions，收编为持久默认）──
    /// <summary>重采样步长 m。默认 2 m。</summary>
    public double SampleStepM { get; set; } = 2.0;
    /// <summary>匹配横向容差 m。默认 8 m。</summary>
    public double MatchToleranceM { get; set; } = 8.0;
    /// <summary>走向夹角阈值 °。默认 35°。</summary>
    public double MatchAngleDeg { get; set; } = 35.0;
    /// <summary>"新建"判据覆盖比。默认 0.35。</summary>
    public double NewCoverFrac { get; set; } = 0.35;
    /// <summary>"废除"判据覆盖比。默认 0.40。</summary>
    public double GoneCoverFrac { get; set; } = 0.40;
    /// <summary>最小段长 m（短于它的游程并入相邻段）。默认 15 m。</summary>
    public double GrowMinLenM { get; set; } = 15.0;
    /// <summary>移位阈值 m（共有段横移中位数 ≥ 此值 → 判「移位」）。默认 3 m。</summary>
    public double ShiftThresholdM { get; set; } = 3.0;

    // ── C 推进方向先验（可选）──
    /// <summary>是否启用推进方向先验。默认关。</summary>
    public bool UseAdvanceDir { get; set; } = false;
    /// <summary>推进方位角（北=0，顺时针），勾选后生效。默认 0°。</summary>
    public double AdvanceAzimuthDeg { get; set; } = 0.0;

    /// <summary>
    /// 映射：把 B 段阈值 + C 段方向先验填充成演化引擎选项。
    /// B 段直填同名属性；C 段方位角→单位向量 XY（**北=+Y、顺时针**），
    /// <c>AdvanceDirXY</c> = 启用时给 (sinθ, cosθ)，否则 null。
    /// </summary>
    public RoadEvolutionOptions ToEvolutionOptions()
    {
        double rad = AdvanceAzimuthDeg * Math.PI / 180.0;   // 方位角弧度(北=+Y、顺时针)
        return new RoadEvolutionOptions
        {
            SampleStepM = SampleStepM,
            MatchToleranceM = MatchToleranceM,
            MatchAngleDeg = MatchAngleDeg,
            NewCoverFrac = NewCoverFrac,
            GoneCoverFrac = GoneCoverFrac,
            GrowMinLenM = GrowMinLenM,
            ShiftThresholdM = ShiftThresholdM,
            AdvanceDirXY = UseAdvanceDir ? (Math.Sin(rad), Math.Cos(rad)) : null,
        };
    }

    /// <summary>一句话摘要（存盘后回显用）。</summary>
    public string Caption
        => "触发=" + (TriggerMode == Mode.AdvanceStep
                ? $"推进步距 {AdvanceStepM.ToString("0.##", CultureInfo.InvariantCulture)}m"
                : $"时间步 每{TimeStepPeriods}期")
         + $" / 演化阈值 6 项 / 推进方向 {(UseAdvanceDir ? "开" : "关")}";

    /// <summary>
    /// 校验（对话框与命令行共用，避免两处判据走偏）。返回空列表 = 放行。
    /// 逐条口径照搬原版 <c>ExtendParamsDialog.OnOk</c>。
    /// </summary>
    public List<string> Validate()
    {
        var e = new List<string>();
        if (!(AdvanceStepM > 0)) e.Add("推进步距阈值需大于 0。");
        if (!(TimeStepPeriods >= 1)) e.Add("时间步阈值需为不小于 1 的整数(期)。");
        if (!(SampleStepM > 0)) e.Add("采样步长需大于 0。");
        if (!(MatchToleranceM > 0)) e.Add("匹配容差需大于 0。");
        if (!(MatchAngleDeg >= 0 && MatchAngleDeg <= 90)) e.Add("走向夹角需在 [0, 90] 范围内。");
        if (!(NewCoverFrac > 0 && NewCoverFrac < 1)) e.Add("新建覆盖比需在 (0, 1) 范围内。");
        if (!(GoneCoverFrac > 0 && GoneCoverFrac < 1)) e.Add("废除覆盖比需在 (0, 1) 范围内。");
        if (!(GrowMinLenM > 0)) e.Add("最小段长需大于 0。");
        if (!(ShiftThresholdM > 0)) e.Add("移位阈值需大于 0。");
        // 移位阈值必须比匹配容差窄：容差答"是不是同一条路"，移位阈值答"变没变"，
        // 前者宽后者窄；填反了等于容差内的推进永远不报。
        else if (ShiftThresholdM >= MatchToleranceM)
            e.Add("移位阈值需小于匹配容差 —— 否则同一条路的横移会被当成两条路，永远判不出「移位」。");
        if (UseAdvanceDir && !(AdvanceAzimuthDeg >= 0 && AdvanceAzimuthDeg < 360))
            e.Add("方位角需在 [0, 360) 范围内。");
        return e;
    }

    /// <summary>读取当前设置（没存过 / 读坏了都回默认值，绝不抛）。</summary>
    public static ExtendTriggerSettings Load(UserSettings? settings = null)
    {
        try { return (settings ?? UserSettings.Current).Get<ExtendTriggerSettings>(SettingsKey) ?? new ExtendTriggerSettings(); }
        catch { return new ExtendTriggerSettings(); }
    }

    /// <summary>存回配置。失败只回 false + 原因，不抛。</summary>
    public bool TrySave(UserSettings? settings, out string? error)
    {
        error = null;
        try
        {
            var st = settings ?? UserSettings.Current;
            st.Set(SettingsKey, this);
            st.Flush();
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
