using System;

namespace PitMine3D.Kylin.Cad.Tasks;

/// <summary>
/// 按环节（采装/运输/排土）分别降效的能力系数——忠实移植 ExploderConfig.WeatherFactorFor 核心。
/// 排土面直吃排土降效; 采装面用周期分解(τ_L/T_c/MF)把班产还原成铲装/车队两侧再分别降:
///   A ∝ 1/τ_L → A'=A·(1−d采); T_c'=τ_L/(1−d采)+(T_c−τ_L)/(1−d运), B'=B·T_c/T_c'; 系数=min(A',B')/min(A,B)。
/// 于是运输降效对采装瓶颈面自然不生效、对运力瓶颈面全额生效(物理, 非加权猜)。自足、可单测。
/// </summary>
public static class LinkDerate
{
    private static double Clamp95(double d) => Math.Clamp(d, 0, 95);

    /// <summary>
    /// 能力系数(1=不降效)。process=Dump 吃 dDump; 采装/运输面按两侧分别降(需周期分解 τ_L/T_c/MF)。
    /// 无周期分解(takt/tc/mf≤0)或两环节同降 → 退回采装侧因子(不凭空劈)。
    /// </summary>
    public static double Factor(ProcessType process, double taktMin, double cycleMin, double matchFactor,
        double dLoadPct, double dHaulPct, double dDumpPct)
    {
        if (process == ProcessType.Dump) return 1 - Clamp95(dDumpPct) / 100.0;

        double dLoad = Clamp95(dLoadPct), dHaul = Clamp95(dHaulPct);
        double fLoad = 1 - dLoad / 100.0;
        if (Math.Abs(dLoad - dHaul) < 1e-9) return fLoad;      // 两环节同降 ⇒ 全盘老口径

        bool hasBreakdown = taktMin > 1e-6 && cycleMin > taktMin + 1e-6 && matchFactor > 1e-6;
        if (!hasBreakdown) return fLoad;                       // 分不了就不分, 退采装侧

        double fHaul = 1 - dHaul / 100.0;
        double baseCap = Math.Min(1.0, matchFactor);           // 归一 A=1, B=MF, 班产=min(1,MF)
        double tcD = taktMin / fLoad + (cycleMin - taktMin) / fHaul;
        double capD = Math.Min(1.0 * fLoad, matchFactor * (cycleMin / tcD));
        return baseCap > 1e-9 ? Math.Clamp(capD / baseCap, 0.05, 1.0) : fLoad;
    }
}
