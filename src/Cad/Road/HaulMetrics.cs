// 忠实移植自原 PitMine3D Modules/RoadLib/Routing/HaulMetrics.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace PitMine3D.Kylin.Cad.Road;

/// <summary>车型与运输参数（v1：缺省值取经验，后续从 MineAssLib 的 TransportConstraintSettings 喂入）。</summary>
public sealed class TruckProfile
{
    public double PayloadT { get; set; } = 90.0;            // 载重 t/车
    public double FlatSpeedLoadedKph { get; set; } = 25.0;  // 重车平路车速
    public double FlatSpeedEmptyKph { get; set; } = 40.0;   // 空车平路车速

    /// <summary>等效运距上坡折算系数（重车）：等效 = 实距 ×(1 + k·坡度).</summary>
    public double UphillEquivK { get; set; } = 6.0;
    /// <summary>等效运距下坡折减系数。</summary>
    public double DownhillEquivK { get; set; } = 2.0;

    /// <summary>运输单价 元/(t·km)，用于成本/Fuel 代理权重。</summary>
    public double UnitHaulCostPerTonKm { get; set; } = 1.5;

    public static TruckProfile Default => new();
}

/// <summary>运距 / 时间公式（设计 §4 的 C；纯静态、可单测）。v1 用线性坡阻模型，后续可换 rimpull/retarder 曲线。</summary>
public static class HaulMetrics
{
    /// <summary>等效运距（C2）：把坡度折算成等价平路里程。<paramref name="gradePct"/> 为行驶方向纵坡（上坡正）。</summary>
    public static double EquivalentLengthM(double lengthM, double gradePct, TruckProfile t, bool loaded)
    {
        double g = gradePct / 100.0;
        double factor;
        if (gradePct >= 0)
        {
            double k = loaded ? t.UphillEquivK : t.UphillEquivK * 0.4;  // 空车上坡惩罚小
            factor = 1.0 + k * g;
        }
        else
        {
            factor = Math.Max(0.5, 1.0 - t.DownhillEquivK * (-g));      // 下坡折减，下限 0.5
        }
        return lengthM * factor;
    }

    /// <summary>行车时间 min（C4 的运 / 返分量）。线性降速：上坡慢、下坡快（封顶）。</summary>
    public static double TravelTimeMin(double lengthM, double gradePct, TruckProfile t, bool loaded)
    {
        double vFlat = loaded ? t.FlatSpeedLoadedKph : t.FlatSpeedEmptyKph;
        double slowK = loaded ? 0.12 : 0.06;
        double v = gradePct >= 0
            ? vFlat / (1.0 + slowK * gradePct)               // 上坡降速
            : Math.Min(vFlat * 1.2, vFlat / (1.0 + 0.03 * gradePct)); // 下坡提速，封顶 1.2×
        v = Math.Max(5.0, v);                                 // 车速下限 5 km/h
        return (lengthM / 1000.0) / v * 60.0;
    }

    /// <summary>循环时间 min（C4）：装 + 运 + 卸 + 返 + 调车。</summary>
    public static double CycleTimeMin(double loadedHaulMin, double emptyReturnMin,
        double spotLoadMin = 3.0, double maneuverDumpMin = 1.5)
        => loadedHaulMin + emptyReturnMin + spotLoadMin + maneuverDumpMin;

    /// <summary>运量加权平均运距（C6）。samples = (运距 m, 吨量 t)。</summary>
    public static double WeightedAverageHaulM(IEnumerable<(double DistanceM, double Tons)> samples)
    {
        double num = 0, den = 0;
        foreach (var (d, w) in samples) { num += d * w; den += w; }
        return den > 1e-9 ? num / den : 0.0;
    }

    /// <summary>最大运距 m（C6）。</summary>
    public static double MaxHaulM(IEnumerable<double> distancesM)
    {
        double max = 0; bool any = false;
        foreach (var d in distancesM) { any = true; if (d > max) max = d; }
        return any ? max : 0.0;
    }
}
