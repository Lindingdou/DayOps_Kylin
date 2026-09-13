// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimModel.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  采—排三维时序模拟 · 数据模型
//
//  一句话：模拟不是动画，动画只是模拟的显示层。真正的模拟对象是**物料流六元组**
//  （期, 源, 物料, 实方量, 汇, 等效运距）在时间轴上的逐期推演；三维/二维画面只是
//  把「本期挖了多少 → 采场推进多少米」「本期堆了多少 → 排土场推进多少米」画出来。
//
//  ── 露天矿的几何反算（本包算法核心，采与排是对偶的）──
//    采场推进   v = V实 / (L_工作线 × H_台阶)      → 坡底线沿推进方位偏移 v
//    排土推进   d = V容 / (L_排土线 × h_排土台阶)   → 排土坡顶线沿推进方向偏移 d
//  两式形式相同，差别只在**口径**：
//    · 采场挖的是【实方 V实】（块体/几何量算口径）；
//    · 排土场堆的是【占容方 V容 = V实 × Kr】（沉降稳定后的库容口径）。
//  同一期，挖 V实 必须对应堆 V实×Kr —— 这就是 <see cref="SimPairCheck"/> 校核的等式，
//  也是判断一份推演「算得对不对」的唯一硬判据。
//
//  ── 三个体积口径与唯一守恒量 ──
//    V实 ──×Ks──▶ V松（卡车载重口径） ；V实 ──×Kr──▶ V容（排土库容口径）
//    吨量 T = V实 × ρ实 是三者之间唯一守恒的中间量。
//  本文件全程经 <see cref="MaterialSpec"/> 取 ρ/Ks/Kr，**不写死任何密度或膨胀系数**。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>推演粒度。四级共用同一套 <see cref="SimFrame"/>，只换期长与数据来源。</summary>
public enum SimGranularity
{
    /// <summary>L1 年（中长远进度计划逐年）。</summary>
    Year,
    /// <summary>L2 月（短期月度计划逐月）。</summary>
    Month,
    /// <summary>L3 日班（当日盘子按班次）。</summary>
    Shift,
    /// <summary>L4 车次（单车装—运—卸—返循环）。</summary>
    Trip,
}

/// <summary>推演轨：计划轨 / 实绩轨。双轨并列即「调度复盘」。</summary>
public enum SimTrack
{
    Plan,
    Actual,
}

/// <summary>校核结论等级。</summary>
public enum SimCheckLevel
{
    /// <summary>数据不足，无法校核（不是「通过」，不许当成通过用）。</summary>
    NotAvailable,
    Ok,
    Warn,
    Error,
}

public static class SimEnumLabels
{
    public static string Label(this SimGranularity g) => g switch
    {
        SimGranularity.Year => "年",
        SimGranularity.Month => "月",
        SimGranularity.Shift => "日班",
        SimGranularity.Trip => "车次",
        _ => "",
    };

    public static string Label(this SimTrack t) => t == SimTrack.Actual ? "实绩" : "计划";

    public static string Label(this SimCheckLevel l) => l switch
    {
        SimCheckLevel.Ok => "配对通过",
        SimCheckLevel.Warn => "配对偏差",
        SimCheckLevel.Error => "采排不守恒",
        _ => "无法校核",
    };
}

// ─────────────────────────────────────────────────────────────────────────────
//  采场几何反算参数
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 采场侧的推进反算参数（排土侧的同名参数长在 <see cref="SinkNode"/> 上，不在这里）。
/// <para>
/// 三层来源：① 软读 PlanLib 已确定的短期月度方案（BenchHeightM / WorkLineLenM）；
/// ② 用户在推演窗口手改；③ 都没有 → <see cref="Resolved"/>=false，推进距离一律显示
/// 「—」并说明原因，**绝不拿缺省值冒充解出来的工程量**。
/// </para>
/// </summary>
public sealed class SimMiningParams
{
    /// <summary>采场台阶高 H（m）。</summary>
    public double BenchHeightM { get; set; } = 12;

    /// <summary>采场工作线长 L（m）。</summary>
    public double WorkLineLengthM { get; set; } = 1100;

    /// <summary>参数是否真的从计划/台账解出来了。false = 界面缺省值，推进距离只作参考。</summary>
    public bool Resolved { get; set; }

    /// <summary>参数来源文案（界面直接显示这一句）。</summary>
    public string SourceLabel { get; set; } = "采场台阶高/工作线长未接计划，用界面缺省值（推进距离仅示意）";

    public bool Usable => BenchHeightM > 1e-6 && WorkLineLengthM > 1e-6;

    // ── 台阶放坡参数（真台阶几何用；H/L 管的是「挖多少」，这两个管的是「挖成什么形状」）──
    /// <summary>
    /// 采场台阶坡面角 α（°）。坡面的**水平投影宽** S = H / tan(α)：
    /// α=90° 退化成垂直壁（S=0），α 越缓 S 越大。工程常见 60~75°。
    /// </summary>
    public double FaceAngleDeg { get; set; } = 70;

    /// <summary>
    /// 采场平盘（安全平台）宽 W（m）：同一标高上的水平面，接在坡底之后。
    /// 上一级的坡底 + 平盘 = 下一级的坡顶起点 —— 决定台阶之间的水平错距。
    /// </summary>
    public double BermWidthM { get; set; } = 8;

    /// <summary>
    /// 放坡参数是否真的解出来了。**false 时层体一律按垂直壁建**，绝不拿 70°/8m 冒充工程参数：
    /// 坡面角与平盘宽直接决定边坡形态与安全平台，猜错比不画更糟。
    /// </summary>
    public bool SlopeResolved { get; set; }

    /// <summary>放坡参数来源文案（界面直接显示这一句）。</summary>
    public string SlopeSourceLabel { get; set; } =
        "采场坡面角 α / 平盘宽 W 未接计划，层体按**垂直壁**建（不拿缺省角度冒充放坡）；在上方填入并点「应用参数」即启用真台阶";

    /// <summary>放坡参数可用（已解析且数值成立）。α 必须在 (0,90]，W ≥ 0。</summary>
    public bool SlopeUsable => SlopeResolved
        && FaceAngleDeg > 1e-6 && FaceAngleDeg <= 90 + 1e-9 && !double.IsNaN(FaceAngleDeg)
        && BermWidthM >= 0 && !double.IsNaN(BermWidthM);

    /// <summary>坡面水平投影宽 S = H/tan(α) m；未解析或垂直壁返回 0。</summary>
    public double SlopeRunM => !SlopeUsable ? 0 : SimSlope.RunFor(BenchHeightM, FaceAngleDeg);

    public string SlopeCaption => SlopeUsable
        ? $"α={FaceAngleDeg:0.#}° · W={BermWidthM:0.##}m · 坡面投影 S={SlopeRunM:0.##}m"
        : "α/W 未解析（垂直壁）";

    /// <summary>采场推进距离 v = V实 /(L × H)。参数不可用返回 NaN（调用方显示「—」）。</summary>
    public double AdvanceMetersFor(double inSituM3)
        => Usable ? inSituM3 / (WorkLineLengthM * BenchHeightM) : double.NaN;

    public string Caption => $"H={BenchHeightM:0.##}m · L={WorkLineLengthM:0}m";

    public SimMiningParams Clone() => (SimMiningParams)MemberwiseClone();

    /// <summary>人工在推演窗口填入 α/W（界面「应用参数」调）。来源如实标注为人工，不冒充计划台账值。</summary>
    public void ApplySlopeByHand(double faceAngleDeg, double bermWidthM)
    {
        FaceAngleDeg = faceAngleDeg;
        BermWidthM = bermWidthM;
        SlopeResolved = true;
        SlopeSourceLabel = $"采场 α/W 由人工在推演窗口填入（α={faceAngleDeg:0.#}° · W={bermWidthM:0.##}m），非计划台账值";
    }

    /// <summary>
    /// 软读 PlanLib 已确定的短期月度方案里的台阶高/工作线长（无编译期依赖，读不到静默降级）。
    /// 与 <see cref="TaskLib.Engine.ShortTermLink"/> 同一套反射约定。
    /// </summary>
    public static SimMiningParams Load()
    {
        var p = new SimMiningParams();
        try
        {
            var t = Type.GetType("PlanLib.ShortTerm.ShortTermSchemeStore, PlanLib");
            object? plan = t?.GetProperty("Confirmed", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (plan == null)
            {
                p.SourceLabel = "未确定短期月度方案，采场 H/L 用界面缺省值（推进距离仅示意；在「短期生产计划 · 确定月度计划」后自动接入）";
                return p;
            }

            double h = ReadD(plan, "BenchHeightM");
            double l = ReadD(plan, "WorkLineLenM");
            string name = plan.GetType().GetProperty("Name")?.GetValue(plan)?.ToString() ?? "";

            if (h > 1e-6 && l > 1e-6)
            {
                p.BenchHeightM = h;
                p.WorkLineLengthM = l;
                p.Resolved = true;
                p.SourceLabel = $"采场 H/L 取自短期方案「{name}」（H={h:0.##}m · L={l:0}m）";
            }
            else
            {
                p.SourceLabel = $"短期方案「{name}」未录台阶高/工作线长，采场 H/L 用界面缺省值（推进距离仅示意）";
            }

            LoadSlope(p, plan, name);
        }
        catch
        {
            p.SourceLabel = "PlanLib 未加载，采场 H/L 用界面缺省值（推进距离仅示意）";
        }
        return p;
    }

    /// <summary>
    /// 软读放坡参数（坡面角 α / 平盘宽 W）。
    /// <para>
    /// **2026-08 已接通**：`ShortTermPlan` 上补了 <c>BenchFaceAngleDeg</c> / <c>BermWidthM</c> /
    /// <c>OverallSlopeAngleDeg</c>（正是下面这组候选名的第一个），由
    /// <c>MinePlanImporter.ApplyGeometry</c> 从月度采剥契约的 <c>ExportGeometry</c> 搬进去。
    /// **这三个名字是反射契约，PlanLib 侧判据 `I4` 按名字钉住了，改名即红。**
    /// 早先的说法「短期月度方案没有 α/W 的字段」已作废。
    /// <para>
    /// 仍然读不到的情形：手工建的计划、契约里本来就没带放坡参数。那时**明确降级** ——
    /// <see cref="SlopeResolved"/> 保持 false，层体按垂直壁建，界面写清原因并让人手填，
    /// 绝不塞个缺省角度冒充工程量。（境界优化的 PitScheme 里也有 <c>BenchFaceAngleDeg</c>，
    /// 但那是**窗口内的方案对象**、没有静态确定簿，拿不到。）
    /// </para>
    /// <para>
    /// 若同时读到台阶坡面角 α 与并段/整体边坡角 β（β≤α），平盘宽按几何关系
    /// <c>W = H/tanβ − H/tanα</c> 反算 —— 这是 PitScheme 里写着的那条关系，不是拍脑袋。
    /// </para>
    /// </summary>
    private static void LoadSlope(SimMiningParams p, object plan, string planName)
    {
        double alpha = FirstD(plan, "BenchFaceAngleDeg", "FaceAngleDeg", "BenchSlopeAngleDeg", "SlopeAngleDeg");
        double berm = FirstD(plan, "BermWidthM", "BermM", "SafetyBermWidthM", "PlatformWidthM");
        double beta = FirstD(plan, "OverallSlopeAngleDeg", "WorkingSlopeAngleDeg", "FinalSlopeAngleDeg");

        if (!(alpha > 1e-6) || alpha > 90)
        {
            p.SlopeSourceLabel = $"短期方案「{planName}」未录台阶坡面角 α，层体按**垂直壁**建；"
                               + "在此填入 α/W 并点「应用参数」，或让月度采剥契约带上放坡参数（导入时自动写入），即启用真台阶放坡。";
            return;
        }

        // 平盘宽：优先直接读；没有就用 W = H/tanβ − H/tanα 反算（要求 0<β≤α）
        string how = "计划直接给出";
        if (!(berm >= 0) || double.IsNaN(berm))
        {
            if (beta > 1e-6 && beta <= alpha && p.BenchHeightM > 1e-6)
            {
                berm = SimSlope.RunFor(p.BenchHeightM, beta) - SimSlope.RunFor(p.BenchHeightM, alpha);
                how = $"由整体边坡角 β={beta:0.#}° 按 W=H/tanβ−H/tanα 反算";
            }
            else
            {
                p.SlopeSourceLabel = $"短期方案「{planName}」给了坡面角 α={alpha:0.#}° 但没有平盘宽 W，"
                                   + "也没有可反算 W 的整体边坡角 β → 层体仍按**垂直壁**建（缺一个就不放坡，不半猜）。";
                return;
            }
        }
        if (berm < 0) berm = 0;

        p.FaceAngleDeg = alpha;
        p.BermWidthM = berm;
        p.SlopeResolved = true;
        p.SlopeSourceLabel = $"采场 α/W 取自短期方案「{planName}」（α={alpha:0.#}° · W={berm:0.##}m，{how}）";
    }

    /// <summary>按一组候选属性名依次读，取第一个 &gt;0 的；都读不到返回 NaN。</summary>
    private static double FirstD(object o, params string[] props)
    {
        foreach (var name in props)
        {
            double v = ReadD(o, name);
            if (v > 1e-9 && !double.IsNaN(v) && !double.IsInfinity(v)) return v;
        }
        return double.NaN;
    }

    private static double ReadD(object o, string prop)
    {
        try
        {
            var v = o.GetType().GetProperty(prop)?.GetValue(o);
            return v is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : 0;
        }
        catch { return 0; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  一期里的「源」与「汇」
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个源（采场作业面/可采区域）在本期的挖除账 + 反算出的推进距离。</summary>
public sealed class SimSourceStep
{
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public double BenchElevationM { get; set; }
    /// <summary>物料构成文案（"煤6∶岩4"）。</summary>
    public string MaterialCaption { get; set; } = "";

    // ── 本期挖除（实方为准）──
    /// <summary>本期挖除实方 m³。</summary>
    public double InSituM3 { get; set; }
    /// <summary>其中计入采出量（矿/煤）的实方 m³。</summary>
    public double OreInSituM3 { get; set; }
    /// <summary>其中计入剥离量的实方 m³。</summary>
    public double WasteInSituM3 => Math.Max(0, InSituM3 - OreInSituM3);
    /// <summary>吨量 t（三个体积口径间唯一守恒量）。</summary>
    public double TonnageT { get; set; }
    /// <summary>运输松方 m³（×Ks，卡车口径）。</summary>
    public double LooseM3 { get; set; }
    /// <summary>剥离部分应产生的排弃占容方 m³（×Kr）—— 采排配对校核的**采侧**。</summary>
    public double WasteDumpM3 { get; set; }

    // ── 几何反算 ──
    public double BenchHeightM { get; set; }
    public double WorkLineLengthM { get; set; }
    /// <summary>本期采场推进距离 m = V实 /(L×H)。参数缺失时为 NaN。</summary>
    public double AdvanceM { get; set; } = double.NaN;
    /// <summary>计划自带的推进距离 m（月/年计划行上的 AdvanceM），用于与反算值互校；0=计划没给。</summary>
    public double RefAdvanceM { get; set; }

    public bool AdvanceResolved => !double.IsNaN(AdvanceM) && !double.IsInfinity(AdvanceM);

    public string AdvanceText => AdvanceResolved ? $"{AdvanceM:0.##} m" : "—";

    public string Caption =>
        $"{SourceName}：挖 {InSituM3 / 1e4:0.##}万m³实方" +
        (OreInSituM3 > 1e-6 ? $"（矿 {OreInSituM3 / 1e4:0.##} / 剥 {WasteInSituM3 / 1e4:0.##}）" : "") +
        $" · 推进 {AdvanceText}";
}

/// <summary>一个汇（排土场/破碎站/煤仓）在本期的堆填账 + 库容与推进距离。</summary>
public sealed class SimSinkStep
{
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind Kind { get; set; }
    public bool IsDumping { get; set; }

    // ── 入方（采装侧投过来的）──
    public double InboundInSituM3 { get; set; }
    /// <summary>入方折成的占容方 m³（×Kr）。</summary>
    public double InboundDumpM3 { get; set; }
    public double InboundTonnageT { get; set; }

    // ── 受排侧实际承接（排土作业面的账）──
    /// <summary>本期本汇实际堆填的占容方 m³ —— 采排配对校核的**排侧**。</summary>
    public double AcceptedDumpM3 { get; set; }
    /// <summary>
    /// true = 本汇没有独立的排土作业台账，<see cref="AcceptedDumpM3"/> 是按入方推导的。
    /// 此时采排两侧同源，配对校核对本汇恒成立（不构成「校核通过」的证据）。
    /// </summary>
    public bool AcceptedFromInbound { get; set; }

    // ── 几何反算（排土侧参数长在 SinkNode 上）──
    public double BenchHeightM { get; set; }
    public double WorkLineLengthM { get; set; }
    /// <summary>本期排土推进距离 m = V容 /(L排×h排)。参数缺失时为 NaN。</summary>
    public double AdvanceM { get; set; } = double.NaN;

    public bool AdvanceResolved => !double.IsNaN(AdvanceM) && !double.IsInfinity(AdvanceM);
    public string AdvanceText => AdvanceResolved ? $"{AdvanceM:0.##} m" : "—";

    // ── 库容 ──
    public double DesignCapacityM3 { get; set; }
    public double RemainingBeforeM3 { get; set; } = double.PositiveInfinity;
    public double RemainingAfterM3 { get; set; } = double.PositiveInfinity;
    public bool CapacityLimited { get; set; }
    /// <summary>期末充填率 0..1（不限容量恒 0）。</summary>
    public double FillRateAfter { get; set; }
    /// <summary>本期把库容排穿了（期末剩余 ≤ 0）。</summary>
    public bool Overflow { get; set; }
    /// <summary>快满（期末充填率 ≥ 90%）。</summary>
    public bool NearFull => CapacityLimited && FillRateAfter >= 0.90;

    public string CapacityText => !CapacityLimited
        ? "容量不限"
        : $"余 {RemainingAfterM3 / 1e4:0.##}万m³（填{FillRateAfter * 100:0.#}%）";

    public string Caption =>
        $"{SinkName}（{Kind.Label()}）：进 {InboundInSituM3 / 1e4:0.##}万m³实方 / 占容 {AcceptedDumpM3 / 1e4:0.##}万m³" +
        (IsDumping ? $" · 推进 {AdvanceText} · {CapacityText}" : " · 通过型去向不占库容");
}

// ─────────────────────────────────────────────────────────────────────────────
//  采排配对校核 —— 模拟正确性的判据
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 采排配对校核：同一期，采装侧剥离的 <c>Σ V实×Kr</c> 必须等于排弃侧承接的 <c>Σ V容</c>。
/// <para>
/// 两侧必须来自**两套独立台账**才有校核意义：
///   · 采侧 = 各采装作业面按物料 Kr 折出的应排占容（<see cref="SimSourceStep.WasteDumpM3"/>）；
///   · 排侧 = 各排土作业面/受排点承接的占容（<see cref="SimSinkStep.AcceptedDumpM3"/>）。
/// 若排侧本身就是按入方推导出来的（<see cref="SimSinkStep.AcceptedFromInbound"/>），
/// 等式恒成立，<see cref="Level"/> 记 <see cref="SimCheckLevel.NotAvailable"/> ——
/// 恒等式不是证据，不许当成「通过」。
/// </para>
/// </summary>
public sealed class SimPairCheck
{
    /// <summary>采装侧本期剥离实方 m³。</summary>
    public double MinedWasteInSituM3 { get; set; }
    /// <summary>采装侧应排占容方 m³ = Σ 各物料 V实×Kr。</summary>
    public double ExpectedDumpM3 { get; set; }
    /// <summary>排弃侧实际承接占容方 m³。</summary>
    public double DumpedM3 { get; set; }

    /// <summary>加权残余膨胀系数 Kr = 应排占容 / 剥离实方（显示用，非常量）。</summary>
    public double EquivalentKr => MinedWasteInSituM3 > 1e-6 ? ExpectedDumpM3 / MinedWasteInSituM3 : 0;

    public double DeltaM3 => DumpedM3 - ExpectedDumpM3;
    public double DeltaPct => ExpectedDumpM3 > 1e-6 ? DeltaM3 / ExpectedDumpM3 * 100 : 0;

    public SimCheckLevel Level { get; set; } = SimCheckLevel.NotAvailable;
    /// <summary>两侧口径的出处说明（界面必须显示，否则看客不知道在比什么）。</summary>
    public string Basis { get; set; } = "";

    /// <summary>相对偏差告警阈（%）。</summary>
    public const double WarnPct = 0.5;
    /// <summary>相对偏差错误阈（%）。</summary>
    public const double ErrorPct = 2.0;
    /// <summary>绝对偏差地板（m³）：小于它的差额是取整噪声，不报警。</summary>
    public const double NoiseFloorM3 = 50;

    /// <summary>按阈值定级（两侧同源时由调用方直接置 NotAvailable，不走这里）。</summary>
    public void Grade()
    {
        double abs = Math.Abs(DeltaM3);
        if (ExpectedDumpM3 <= 1e-6 && DumpedM3 <= 1e-6) { Level = SimCheckLevel.NotAvailable; return; }
        if (abs <= NoiseFloorM3) { Level = SimCheckLevel.Ok; return; }
        double pct = Math.Abs(DeltaPct);
        Level = pct >= ErrorPct ? SimCheckLevel.Error
              : pct >= WarnPct ? SimCheckLevel.Warn
              : SimCheckLevel.Ok;
    }

    public string Caption
    {
        get
        {
            if (Level == SimCheckLevel.NotAvailable && ExpectedDumpM3 <= 1e-6 && DumpedM3 <= 1e-6)
                return "本期无剥离，无需配对校核";
            string core = $"应排 {ExpectedDumpM3 / 1e4:0.##}万m³（剥离 {MinedWasteInSituM3 / 1e4:0.##}万m³实方 × Kr{EquivalentKr:0.000}）"
                        + $" ↔ 实排 {DumpedM3 / 1e4:0.##}万m³";
            if (Level == SimCheckLevel.NotAvailable) return core + " · 两侧同源，恒等（不构成校核证据）";
            string d = $"，差 {DeltaM3 / 1e4:+0.####;-0.####;0}万m³（{DeltaPct:+0.##;-0.##;0}%）";
            return core + d + " · " + Level.Label();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  一期 = 一帧
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 一笔非量型作业（穿孔 / 爆破 / 检修·空闲）。**不带物料量** —— 它们不搬料，
/// 编一个量出来会一路污染剥采比、运输功与库容占用（见 <see cref="SimFrame.Activities"/>）。
/// </summary>
public sealed class SimActivity
{
    public ProcessType Process { get; set; }
    /// <summary>作业面 / 位置名（与 <c>SimRegion.Name</c> 对名，三维据此定位）。</summary>
    public string Zone { get; set; } = "";
    public string Equipment { get; set; } = "";
    /// <summary>当日起止 (0..24)。</summary>
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    /// <summary>物料/内容文本（穿孔写岩性，检修写事由）。</summary>
    public string Material { get; set; } = "";
    /// <summary>台阶标高 m（0 = 未知）。</summary>
    public double BenchElevationM { get; set; }
    /// <summary>关联的任务 Id（回查用）。</summary>
    public string TaskId { get; set; } = "";
    /// <summary>工程位置号（业务事实 ↔ 三维几何的连接键）。</summary>
    public string EngineeringPositionId { get; set; } = "";

    // ── 作业面自己的坐标（来自 FaceInput.SourceX/Y/Z，即作业面档案的 source_x/y/z）──
    /// <summary>
    /// 源坐标 X/Y/Z（世界米）。<see cref="HasPosition"/> 为 true 时才有意义。
    ///
    /// <para><b>为什么要带它</b>：三维定位原本只能拿作业面名去套区域轮廓，
    /// 而作业面名（<c>主采面·东（采煤）</c>）、可采区域名、采掘单元的区域名是三套互不相干的命名，
    /// 对不上就画不出来。作业面档案里本来就存着这个面的坐标（<c>source_x/y/z</c>，
    /// 路网定源节点用的就是它）—— 有坐标就直接摆，根本不需要名字对得上。</para>
    /// </summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    /// <summary>坐标可用（作业面档案里填了 source_x/y）。</summary>
    public bool HasPosition { get; set; }

    /// <summary>
    /// 量型工序（采装 / 排土）。
    ///
    /// <para><b>为什么量型的也进这张表</b>：这张表最初只收不搬料的那几道工序，
    /// 因为它们在块体那一层完全没有表达。但块体表达的是「料从哪挖到哪」，
    /// <b>不是「谁在哪个面上干」</b> —— 而且日班档下块体常常筛不出本班的单元（退回全量）。
    /// 于是「早班 WK-10 在主采面·东（采煤）」这件事在三维上没有任何地方看得见。
    /// 现在一张表收齐本班所有任务：<b>信息栏仍只列非量型</b>（量型有自己的量表，
    /// 重复列一遍反而乱），<b>三维层画全部</b>——它回答的正是「谁在哪」。</para>
    ///
    /// <para>⚠ 量型条目在这里<b>依然不带量</b>：量的唯一出处是 Sources/Sinks。
    /// 两处都放一份量，迟早会有人加错地方。</para>
    /// </summary>
    public bool IsVolumetric { get; set; }

    /// <summary>随行设备数（采装任务的配属卡车；主设备不计在内）。</summary>
    public int TruckCount { get; set; }

    /// <summary>
    /// 停工/空闲的原因（来自任务上的 <c>Reasons</c>）。
    ///
    /// <para><b>为什么必须带过来</b>：日班档下中班、夜班整班都是「检修/空闲」，
    /// 而窗口只写「无采排」—— 人看不出是**排产就没排**、**数据没读到**、还是**没料可采**。
    /// 三种的处置完全不同。排产器给的原因就在任务上（<c>TaskExploder.IdleTask</c> 写的
    /// <c>IncompleteReason.OreShortage</c> = 备采用尽），接过来直接答掉这个问题。</para>
    /// </summary>
    public List<IncompleteReason> IdleReasons { get; set; } = new();

    /// <summary>停工原因的中文串（无原因返回空串）。</summary>
    public string IdleReasonText => IdleReasons.Count == 0 ? ""
        : string.Join("、", IdleReasons.Select(r => r.Label()).Distinct());

    public double DurationH => Math.Max(0, EndHour - StartHour);
    public string Caption => $"{Process.Label()}　{Zone}　{Hm(StartHour)}–{Hm(EndHour)}"
                           + (Equipment.Length > 0 ? $"　{Equipment}" : "")
                           + (Material.Length > 0 ? $"　{Material}" : "")
                           + (IdleReasonText.Length > 0 ? $"　（{IdleReasonText}）" : "");

    private static string Hm(double h)
    {
        int hh = (int)h, mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }
}

/// <summary>一期（年/月/班）的完整推演状态。UI 逐帧渲染的就是它。</summary>
public sealed class SimFrame
{
    public int Index { get; set; }
    /// <summary>期标签（2027 / 2027-06 / 中班）。</summary>
    public string Period { get; set; } = "";
    /// <summary>界面显示用的长标签。</summary>
    public string Label { get; set; } = "";
    public SimGranularity Granularity { get; set; }
    public SimTrack Track { get; set; }
    /// <summary>本期是外推估算（不是计划实数）——界面必须标出来。</summary>
    public bool Estimated { get; set; }

    /// <summary>本期物料流平衡（剥采比/内排率/运输功/加权运距都从它取）。</summary>
    public PeriodBalance Balance { get; set; } = new();

    public List<SimSourceStep> Sources { get; set; } = new();
    public List<SimSinkStep> Sinks { get; set; } = new();
    public SimPairCheck Pair { get; set; } = new();

    /// <summary>
    /// 本期的**非量型作业**（穿孔 / 爆破 / 检修·空闲）。
    ///
    /// <para><b>为什么单列一条而不是塞进 Sources</b>：源/汇是物料流的两端，
    /// 它们的每一个字段（实方量、占容方、运距、推进距离）对穿孔和检修都没有意义。
    /// 硬塞进去就得给它们编一个量，而那个量会一路流进剥采比、运输功、库容占用。</para>
    ///
    /// <para><b>为什么必须有</b>：日班粒度下要看的是「这个班在干什么」。
    /// 一个只排了穿孔和检修的夜班，物料流是空的、推进是零 —— 但它不是没在干活。
    /// 只画采排的话，画面上那个班就是一片静止，看图的人无从分辨
    /// 「没排班」「排了但没量」「排了穿孔」这三件完全不同的事。</para>
    /// </summary>
    public List<SimActivity> Activities { get; set; } = new();

    /// <summary>
    /// 本期涉及的**采掘单元号**（<c>MiningUnitLedger.Row.UnitId</c> 口径，来自任务的
    /// <c>ProductionTask.UnitId</c>，再上溯到作业面档案的 <c>unit_id</c>）。
    ///
    /// <para><b>干什么用</b>：三维块体的单元来自采掘单元台账（按月存），
    /// 而日班帧的活来自当日任务盘子（按班）。两条线各自独立，靠名字对不上
    /// （作业面名 / 可采区域名 / 单元区域名是三套互不相干的命名）。
    /// UnitId 是仓库指定的那把钥匙 —— 有它就能把「这个班在动哪几个单元」筛出来。</para>
    ///
    /// <para>空 = 任务上没有单元号（作业面档案没填 <c>unit_id</c>）⇒ 筛不出来，
    /// 调用方须如实说明并退回全量，<b>不要静默当成「本期没有单元」</b>。</para>
    /// </summary>
    public List<string> UnitIds { get; set; } = new();

    // ── 累计（含本期）──
    public double CumOreWanT { get; set; }
    public double CumStripWanM3 { get; set; }
    public double CumDumpedWanM3 { get; set; }
    public double CumTransportWorkWanTKm { get; set; }

    /// <summary>本期口径说明 / 降级原因（界面逐条显示）。</summary>
    public List<string> Notes { get; set; } = new();

    // ── 便捷派生（全部转发到 Balance，避免两套口径）──
    public double OreWanT => Balance.OreWanT;
    public double StripWanM3 => Balance.StripWanM3;
    public double StripRatio => Balance.StripRatio;
    public double DumpedWanM3 => Balance.DumpedWanM3;
    public double InternalDumpPct => Balance.InternalDumpPct;
    public double TransportWorkWanTKm => Balance.TransportWorkWanTKm;
    public double WeightedAvgHaulKm => Balance.WeightedAvgHaulKm;

    /// <summary>本期采场总推进（各源推进的量加权平均；参数缺失返回 NaN）。</summary>
    public double MineAdvanceM
    {
        get
        {
            var ok = Sources.Where(s => s.AdvanceResolved && s.InSituM3 > 1e-6).ToList();
            if (ok.Count == 0) return double.NaN;
            double v = ok.Sum(s => s.InSituM3);
            return v <= 1e-6 ? double.NaN : ok.Sum(s => s.AdvanceM * s.InSituM3) / v;
        }
    }

    /// <summary>本期排土总推进（各排弃汇推进的占容加权平均；参数缺失返回 NaN）。</summary>
    public double DumpAdvanceM
    {
        get
        {
            var ok = Sinks.Where(s => s.IsDumping && s.AdvanceResolved && s.AcceptedDumpM3 > 1e-6).ToList();
            if (ok.Count == 0) return double.NaN;
            double v = ok.Sum(s => s.AcceptedDumpM3);
            return v <= 1e-6 ? double.NaN : ok.Sum(s => s.AdvanceM * s.AcceptedDumpM3) / v;
        }
    }

    public string HeadCaption =>
        $"{Label}　采出 {OreWanT:0.##}万t · 剥离 {StripWanM3:0.##}万m³ · 剥采比 {StripRatio:0.##} · 排弃占容 {DumpedWanM3:0.##}万m³";
}

// ─────────────────────────────────────────────────────────────────────────────
//  时间轴
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条推演时间轴：某粒度 × 某轨 的全部期次。</summary>
public sealed class SimTimeline
{
    public SimGranularity Granularity { get; set; }
    public SimTrack Track { get; set; }
    public List<SimFrame> Frames { get; set; } = new();

    /// <summary>数据来源一句话（界面顶栏显示）。</summary>
    public string SourceLabel { get; set; } = "";
    /// <summary>期次为外推估算（未接计划）。界面必须挂横幅。</summary>
    public bool Estimated { get; set; }
    /// <summary>完全没数据（Frames 为空）。</summary>
    public bool Degraded => Frames.Count == 0;
    /// <summary>口径/降级说明。</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>采场几何反算参数（推进距离的分母）。</summary>
    public SimMiningParams MiningParams { get; set; } = new();

    // ── 年粒度专用：方案自带的口径（不另取缺省，读不到就是 0 ⇒ 界面不画那条线）──
    /// <summary>在演的中长远方案名。空 = 不是年粒度，或没挑到方案。</summary>
    public string SchemeName { get; set; } = "";
    /// <summary>设计生产能力 A_p（万t/a）—— 逐年图上那条达产线。0 = 方案没给，不画。</summary>
    public double DesignCapacityWanTa { get; set; }
    /// <summary>经济合理剥采比 n经（m³/t）—— 剥采比折线上的报警线。0 = 方案没给，不画。</summary>
    public double EconomicStripRatioMax { get; set; }

    public int Count => Frames.Count;

    public SimFrame? FrameAt(int i) => i >= 0 && i < Frames.Count ? Frames[i] : null;

    /// <summary>全期合计（对比方案用）。</summary>
    public double TotalOreWanT => Frames.Sum(f => f.OreWanT);
    public double TotalStripWanM3 => Frames.Sum(f => f.StripWanM3);
    public double TotalDumpedWanM3 => Frames.Sum(f => f.DumpedWanM3);
    public double TotalTransportWorkWanTKm => Frames.Sum(f => f.TransportWorkWanTKm);

    /// <summary>
    /// 全期合计的采排配对。
    /// <para>
    /// 单期对不上未必是错：班内采装与排土本来就可能不同步（排土工序滞后于采装、
    /// 推土机班产与电铲班产不等），但**全期合计必须守恒**——采出来的每一方岩，
    /// 迟早都得有地方放。所以单期看趋势、全期看守恒，两个都要显示。
    /// </para>
    /// </summary>
    public SimPairCheck TotalPair
    {
        get
        {
            var p = new SimPairCheck
            {
                MinedWasteInSituM3 = Frames.Sum(f => f.Pair.MinedWasteInSituM3),
                ExpectedDumpM3 = Frames.Sum(f => f.Pair.ExpectedDumpM3),
                DumpedM3 = Frames.Sum(f => f.Pair.DumpedM3),
                Basis = "全期合计：单期可以不同步（排土滞后于采装），但全期必须守恒。",
            };
            if (Frames.Any(f => f.Pair.Level != SimCheckLevel.NotAvailable)) p.Grade();
            return p;
        }
    }

    /// <summary>全期最严重的配对结论（错 &gt; 警 &gt; 通过 &gt; 无法校核）。</summary>
    public SimCheckLevel WorstPair
    {
        get
        {
            if (Frames.Any(f => f.Pair.Level == SimCheckLevel.Error)) return SimCheckLevel.Error;
            if (Frames.Any(f => f.Pair.Level == SimCheckLevel.Warn)) return SimCheckLevel.Warn;
            if (Frames.Any(f => f.Pair.Level == SimCheckLevel.Ok)) return SimCheckLevel.Ok;
            return SimCheckLevel.NotAvailable;
        }
    }
}
