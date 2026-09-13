using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  作业面的【工艺流程】—— 穿 → 爆 → 采 → 运 → 排
//
//  与「设备类型」的分工（两者都挂在作业面上，别混）：
//    · 设备类型（WorkingFace 的四个 *Model 列）回答"用哪个型号的机器"；
//    · 工艺流程（本类）回答"这个面走不走这道工序、这道工序按什么参数干"。
//  同一台 WK-10 在表土面上不穿爆、在硬岩面上要穿爆 —— 型号说不了这件事，工艺流程才说得了。
//
//  ★ 本类是【月度粒度】的：它定的是"这个面这个月按什么工艺干、需要多少工序量"，
//    不是某一炮的孔位、不是某一班的车次。更细的粒度在下游（TaskLib 的日作业计划）。
//
//  ★ 参数的唯一来源纪律：密度/Ks/Kr 一律走 PlanMaterialCatalog，本类【不重复定义】。
//    本类只放"穿爆采运排各自的工艺参数"，且每一个都能推出一个【月度工序量】——
//    推不出量的参数不放进来（放了也没人用，就是死输入）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>五道工序。顺序就是现场的顺序，<b>枚举值别重排</b>（下游按序号排甘特行）。</summary>
public enum FaceProcess
{
    /// <summary>穿孔。</summary>
    Drill = 0,
    /// <summary>爆破。<b>不排设备，排的是窗口与量</b>。</summary>
    Blast = 1,
    /// <summary>采装。</summary>
    Load = 2,
    /// <summary>运输。</summary>
    Haul = 3,
    /// <summary>排土 / 卸料。</summary>
    Dump = 4,
}

/// <summary>采装方式 —— 决定台阶几何能不能站得住，也决定配什么设备。</summary>
public enum LoadMethod
{
    /// <summary>正铲（站在台阶底、向上挖）—— 露天煤矿剥离主力。</summary>
    FaceShovel = 0,
    /// <summary>反铲（站在台阶顶、向下挖）—— 采煤面清底、薄煤层常用。</summary>
    Backhoe = 1,
    /// <summary>前装机 —— 小面 / 倒堆 / 配合作业。</summary>
    Loader = 2,
}

/// <summary>排土方式 —— 决定排土侧配什么设备、以及推排量怎么算。</summary>
public enum DumpMethod
{
    /// <summary>卡车排卸 + 推土机推排（本系统当前排产支持的就是这一种）。</summary>
    TruckDozer = 0,
    /// <summary>前装机倒堆。</summary>
    LoaderRehandle = 1,
    /// <summary>排土机 / 排土犁（带式输送机末端）。</summary>
    Spreader = 2,
}

/// <summary>
/// 一个作业面的工艺流程与工序参数。
///
/// <para><b>缺省值是"露天煤矿单斗-卡车工艺"的常规值</b>，且每一个都写明了出处或量级依据。
/// 不填也能跑，但报表里会标出"这一项走的是缺省"——
/// 「填了现场值」和「一个都没填、全走缺省」算出来的工序量差别很大，而两者在界面上长得一模一样。</para>
/// </summary>
public sealed class FaceProcessChain
{
    // ── 穿孔 ──────────────────────────────────────────────────────────
    /// <summary>本面走不走穿孔。<b>缺省跟着物料走</b>（硬岩/夹矸要，表土/煤不要），
    /// 这里是显式覆盖：null = 按物料判，true/false = 人说了算。</summary>
    public bool? DrillEnabled { get; set; }

    /// <summary>孔网参数 a×b（m）—— 孔距 × 排距。现场工序定额给的是 7×8。</summary>
    public double HoleSpacingM { get; set; } = 7.0;
    public double HoleBurdenM { get; set; } = 8.0;

    /// <summary>台阶高（m）。0 = 用库表 <c>working_face.bench_height_m</c>（按 FaceCode 取）。</summary>
    public double BenchHeightM { get; set; } = 0;

    /// <summary>超深（m）—— 孔底钻过台阶底板的那一截，保证根底不留台。</summary>
    public double SubDrillM { get; set; } = 1.5;

    /// <summary>孔径（mm）—— 只带着走，供下游钻爆设计用，不进月度量的算式。</summary>
    public double HoleDiameterMm { get; set; } = 250;

    // ── 爆破 ──────────────────────────────────────────────────────────
    /// <summary>炸药单耗（kg/m³ 原位实方）。现场工序定额给的是 0.32。</summary>
    public double PowderFactorKgPerM3 { get; set; } = 0.32;

    /// <summary>
    /// 穿爆超前期（工日）—— 穿完到能采装之间隔几天（起爆 + 清场 + 撤警戒）。
    /// <para>0 = 用全局缺省。<b>面级可覆盖</b>：硬岩大区爆破和煤层控制爆破的超前期本来就不一样。</para>
    /// </summary>
    public int BlastLeadDays { get; set; } = 0;

    /// <summary>单次爆破规模（万m³ 原位实方）。0 = 不限（本月的量一次爆完，不现实但不挡人）。</summary>
    public double BlastBatchWanM3 { get; set; } = 0;

    // ── 采装 ──────────────────────────────────────────────────────────
    public LoadMethod LoadMethod { get; set; } = LoadMethod.FaceShovel;

    /// <summary>采宽（m）—— 一次推进的条带宽。0 = 用库表 <c>working_face.mining_width_m</c>。</summary>
    public double MiningWidthM { get; set; } = 0;

    // ── 运输 ──────────────────────────────────────────────────────────
    /// <summary>是否经破碎站转运（半连续工艺）。<b>true 时运距口径变</b>：面→破碎站 + 胶带。</summary>
    public bool ViaCrusher { get; set; }

    // ── 排土 ──────────────────────────────────────────────────────────
    public DumpMethod DumpMethod { get; set; } = DumpMethod.TruckDozer;

    // ══════════════════════════════════════════════════════════════════
    //  派生：月度工序量。每一个都能从上面的参数推出来 —— 推不出的参数不该放进本类。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>孔深（m）= 台阶高 + 超深。台阶高取不到时返回 0（<b>不拿一个假高度顶上</b>）。</summary>
    public double HoleDepthM(double benchHeightFallbackM = 0)
    {
        double h = BenchHeightM > 0 ? BenchHeightM : benchHeightFallbackM;
        return h > 0 ? h + Math.Max(0, SubDrillM) : 0;
    }

    /// <summary>
    /// 单孔控制方量（m³ 原位实方）= 孔距 × 排距 × 台阶高。
    /// <para>台阶高取不到就返回 0 —— 调用方据此判"这个面的穿孔量算不出来"，
    /// 而不是拿一个编出来的数继续往下算。</para>
    /// </summary>
    public double VolumePerHoleM3(double benchHeightFallbackM = 0)
    {
        double h = BenchHeightM > 0 ? BenchHeightM : benchHeightFallbackM;
        if (!(h > 0) || !(HoleSpacingM > 0) || !(HoleBurdenM > 0)) return 0;
        return HoleSpacingM * HoleBurdenM * h;
    }

    /// <summary>
    /// 单位耗孔率（延米/m³）= 孔深 ÷ 单孔控制方量。
    /// <b>这是把"月剥离量"换成"月穿孔延米"的唯一系数</b>。
    /// </summary>
    public double MetersPerM3(double benchHeightFallbackM = 0)
    {
        double v = VolumePerHoleM3(benchHeightFallbackM);
        double d = HoleDepthM(benchHeightFallbackM);
        return v > 1e-9 && d > 0 ? d / v : 0;
    }

    /// <summary>月穿孔延米（m）—— 给定本月该面的原位实方。0 = 台阶高/孔网缺参数，算不出来。</summary>
    public double MonthlyDrillMeters(double inSituM3, double benchHeightFallbackM = 0)
        => Math.Max(0, inSituM3) * MetersPerM3(benchHeightFallbackM);

    /// <summary>月炸药量（kg）= 本月实方 × 单耗。</summary>
    public double MonthlyPowderKg(double inSituM3)
        => Math.Max(0, inSituM3) * Math.Max(0, PowderFactorKgPerM3);

    /// <summary>月爆破次数 —— 按单次规模切。单次规模为 0（不限）时返回 1。</summary>
    public int MonthlyBlastCount(double inSituM3)
    {
        if (!(BlastBatchWanM3 > 0)) return inSituM3 > 0 ? 1 : 0;
        return (int)Math.Ceiling(Math.Max(0, inSituM3) / 1e4 / BlastBatchWanM3);
    }

    /// <summary>月孔数 = 本月实方 ÷ 单孔控制方量。</summary>
    public double MonthlyHoleCount(double inSituM3, double benchHeightFallbackM = 0)
    {
        double v = VolumePerHoleM3(benchHeightFallbackM);
        return v > 1e-9 ? Math.Max(0, inSituM3) / v : 0;
    }

    /// <summary>这个面这个月到底走不走穿爆（显式覆盖优先，否则按物料）。</summary>
    public bool ResolveDrilling(string materialCode)
        => DrillEnabled ?? PlanMaterialCatalog.Resolve(materialCode).NeedsBlasting;

    /// <summary>
    /// 参数完备性自检 —— 返回<b>算不出量</b>的那几条（空 = 能算）。
    /// <para>只报"算不出来"，不报"没填"：缺省值是合法的，缺省值算不出量才是问题。</para>
    /// </summary>
    public List<string> CheckComputable(string faceName, string materialCode, double benchHeightFallbackM = 0)
    {
        var bad = new List<string>();
        if (!ResolveDrilling(materialCode)) return bad;      // 不穿爆的面不必有穿爆参数

        double h = BenchHeightM > 0 ? BenchHeightM : benchHeightFallbackM;
        if (!(h > 0))
            bad.Add($"「{faceName}」要穿爆，但台阶高既没在工艺里填、也没在 working_face 台账里查到 "
                  + "—— 单孔控制方量算不出来，这个面的月穿孔延米会是 0。"
                  + "◆ 这个 0 的意思是「算不出」，不是「不用穿」，两者在报表上长得一模一样。");
        if (!(HoleSpacingM > 0) || !(HoleBurdenM > 0))
            bad.Add($"「{faceName}」孔网参数是 {HoleSpacingM:0.##}×{HoleBurdenM:0.##}m（须为正）—— 穿孔量算不出来。");
        if (!(PowderFactorKgPerM3 > 0))
            bad.Add($"「{faceName}」炸药单耗是 {PowderFactorKgPerM3:0.###}kg/m³（须为正）—— 月炸药量算不出来。");
        return bad;
    }

    /// <summary>一行摘要（表格里显示用）。</summary>
    public string Caption(string materialCode)
    {
        bool dr = ResolveDrilling(materialCode);
        return (dr ? $"穿爆 {HoleSpacingM:0.#}×{HoleBurdenM:0.#}m·{PowderFactorKgPerM3:0.##}kg/m³" : "免爆")
             + $" · {LoadMethodText(LoadMethod)}"
             + (ViaCrusher ? " · 经破碎站" : "")
             + $" · {DumpMethodText(DumpMethod)}";
    }

    public FaceProcessChain Copy() => (FaceProcessChain)MemberwiseClone();

    public static string LoadMethodText(LoadMethod m) => m switch
    {
        LoadMethod.Backhoe => "反铲",
        LoadMethod.Loader => "前装机",
        _ => "正铲",
    };

    public static string DumpMethodText(DumpMethod m) => m switch
    {
        DumpMethod.LoaderRehandle => "前装机倒堆",
        DumpMethod.Spreader => "排土机",
        _ => "卡车+推土机",
    };
}
