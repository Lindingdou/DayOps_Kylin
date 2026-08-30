using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>车型与运输参数（忠实移植原 RoadLib.Routing.TruckProfile 缺省经验值）。</summary>
public sealed class TruckProfile
{
    public double PayloadT = 90.0;            // 载重 t/车
    public double FlatSpeedLoadedKph = 25.0;  // 重车平路车速
    public double FlatSpeedEmptyKph = 40.0;   // 空车平路车速
    public double UphillEquivK = 6.0;         // 等效运距上坡折算系数(重车)
    public double DownhillEquivK = 2.0;       // 下坡折减系数
    public double UnitHaulCostPerTonKm = 1.5; // 运输单价 元/(t·km)
    public static TruckProfile Default => new();
}

/// <summary>
/// 运距 / 时间公式（忠实移植原 <c>RoadLib.Routing.HaulMetrics</c>, 设计 §4 的 C；纯静态、可单测）——
/// 线性坡阻模型：等效运距(坡度折算平路里程)、行车时间(上坡降速/下坡提速封顶)、循环时间、加权平均/最大运距。
/// </summary>
public static class HaulMetrics
{
    /// <summary>等效运距：把坡度折算成等价平路里程。gradePct 为行驶方向纵坡(上坡正)。</summary>
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
            factor = Math.Max(0.5, 1.0 - t.DownhillEquivK * (-g));      // 下坡折减, 下限 0.5
        }
        return lengthM * factor;
    }

    /// <summary>行车时间 min。线性降速：上坡慢、下坡快(封顶 1.2×)。</summary>
    public static double TravelTimeMin(double lengthM, double gradePct, TruckProfile t, bool loaded)
    {
        double vFlat = loaded ? t.FlatSpeedLoadedKph : t.FlatSpeedEmptyKph;
        double slowK = loaded ? 0.12 : 0.06;
        double v = gradePct >= 0
            ? vFlat / (1.0 + slowK * gradePct)
            : Math.Min(vFlat * 1.2, vFlat / (1.0 + 0.03 * gradePct));
        v = Math.Max(5.0, v);                                           // 车速下限 5 km/h
        return (lengthM / 1000.0) / v * 60.0;
    }

    /// <summary>循环时间 min：装 + 运 + 卸 + 返 + 调车。</summary>
    public static double CycleTimeMin(double loadedHaulMin, double emptyReturnMin,
        double spotLoadMin = 3.0, double maneuverDumpMin = 1.5)
        => loadedHaulMin + emptyReturnMin + spotLoadMin + maneuverDumpMin;

    /// <summary>运量加权平均运距。samples = (运距 m, 吨量 t)。</summary>
    public static double WeightedAverageHaulM(IEnumerable<(double DistanceM, double Tons)> samples)
    {
        double num = 0, den = 0;
        foreach (var (d, w) in samples) { num += d * w; den += w; }
        return den > 1e-9 ? num / den : 0.0;
    }

    /// <summary>最大运距 m。</summary>
    public static double MaxHaulM(IEnumerable<double> distancesM)
    {
        double max = 0; bool any = false;
        foreach (var d in distancesM) { any = true; if (d > max) max = d; }
        return any ? max : 0.0;
    }
}
