using System;

namespace PitMine3D.Kylin.Cad.Tasks;

/// <summary>
/// 车铲循环产能求解——忠实移植 TaskLib.Engine.FleetMatcher 的核心 §3-5 公式:
/// 斗数 m=W_t/(V_b·ρ松·η_b) → 装车节拍 τ_L=m·t_s → 循环时间 T_c(复用 HaulMetrics)
/// → 匹配系数 MF=n·τ_L/T_c → 铲装能力 P_sh=60W/τ_L / 车队运力 P_fl=n·60W/T_c
/// → 编组产能 q(m³实方/h)=min(P_sh,P_fl)/ρ实·η。自足(复用已移 HaulMetrics + FleetMatch)，可单测。
/// </summary>
public static class FleetCycle
{
    private const double BucketFillFactor = 0.85;   // η_b
    private const double SwingMin = 0.53;           // t_s 单斗摆动
    private const double SpotMin = 1.0;             // 调车
    private const double DumpMin = 1.5;             // 卸车

    public sealed record Result(
        double BucketsPerTruck, double LoadTaktMin, double CycleTimeMin,
        int OptimalTrucks, int Trucks, double MatchFactor,
        double ShovelCapTph, double FleetCapTph, double GroupCapM3PerH, string Bottleneck);

    /// <summary>求解。inSituDensity=ρ实(t/m³), ks=松散系数; haulKm 单程等效运距; trucks≤0 用最优 n*。</summary>
    public static Result Solve(double bucketM3, double payloadT, double inSituDensity, double ks,
        double haulKm, int trucks = 0, double efficiency = 0.80)
    {
        double rhoLoose = inSituDensity / Math.Max(1.0, ks);          // ρ松 (t/m³松方) 铲斗口径
        double m = Math.Clamp(payloadT / Math.Max(0.1, bucketM3 * rhoLoose * BucketFillFactor), 1.0, 20.0);
        double takt = Math.Max(0.1, m * SwingMin);                    // τ_L

        double loaded = HaulMetrics.TravelTimeMin(Math.Max(0, haulKm) * 1000.0, 0, TruckProfile.Default, loaded: true);
        double empty = HaulMetrics.TravelTimeMin(Math.Max(0, haulKm) * 1000.0, 0, TruckProfile.Default, loaded: false);
        double tc = Math.Max(takt + DumpMin, HaulMetrics.CycleTimeMin(loaded, empty, spotLoadMin: takt + SpotMin, maneuverDumpMin: DumpMin));

        int nStar = Math.Max(1, (int)Math.Round(tc / takt));          // n* = T_c/τ_L
        int n = trucks > 0 ? trucks : nStar;
        n = Math.Max(1, n);

        double mf = n * takt / tc;
        double pSh = 60.0 * payloadT / takt;                          // P_sh 铲装能力 t/h
        double pFl = n * 60.0 * payloadT / tc;                        // P_fl 车队运力 t/h
        double p = Math.Min(pSh, pFl);
        double qm3h = p / Math.Max(0.1, inSituDensity) * Math.Clamp(efficiency, 0.1, 1.0);
        string bottleneck = mf < 0.9 ? "运力不足·铲待车" : mf <= 1.1 ? "配置均衡" : "铲能力瓶颈·车排队";

        return new Result(Math.Round(m, 2), Math.Round(takt, 2), Math.Round(tc, 2),
            nStar, n, Math.Round(mf, 3), Math.Round(pSh, 1), Math.Round(pFl, 1), Math.Round(qm3h, 2), bottleneck);
    }
}
