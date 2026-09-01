using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 车铲匹配核 —— 移植自原 `GeoDataBase.FleetOptimizer` 的匹配系数 + M/M/c 排队(Erlang-C)公式。
/// 匹配系数 MF = 卡车数·装车节拍 / 循环时间（1≈均衡, &lt;1 铲待车, &gt;1 车排队）；
/// Erlang-C 给车到达需排队的概率。本类是几何/命令侧轻量匹配助手; 原**完整多规则 DP 编组优化 +
/// 日产能模型已忠实移植**在 <c>src/Data/FleetOptimizer.cs</c>(SolveMinTrucks 无界 DP + ErlangC + 物理产能
/// 子模型 + 在籍修复, 已接「编组优化」命令), 二者不重复——此处仅供无 DB 的纯几何编组产能切片(FleetCycle)复用。
/// </summary>
public static class FleetMatch
{
    /// <summary>Erlang-C：c 个服务台、每台负载 rhoPerServer∈[0,1) 时，到达需排队的概率（逐字移植）。</summary>
    public static double ErlangC(int c, double rhoPerServer)
    {
        if (c < 1) return 1.0;
        if (rhoPerServer >= 1) return 1.0;
        double a = rhoPerServer * c;                 // 话务量(Erlang)
        double sum = 0, term = 1;
        for (int k = 0; k < c; k++) { if (k > 0) term *= a / k; sum += term; }
        double last = term * a / c;                  // a^c/c!
        double top = last / (1 - rhoPerServer);
        return top / (sum + top);
    }

    /// <summary>配车匹配系数 MF = 卡车数 · 装车节拍(min/车) / 循环时间(min)。</summary>
    public static double MatchFactor(int trucks, double loadTimeMin, double cycleTimeMin)
        => cycleTimeMin <= 1e-9 ? 0 : trucks * loadTimeMin / cycleTimeMin;

    /// <summary>匹配结论：&lt;0.95 铲待车(车少) / &gt;1.05 车排队(车多) / 否则均衡。</summary>
    public static string Verdict(double mf)
        => mf < 0.95 ? "铲待车（配车偏少）" : mf > 1.05 ? "车排队（配车偏多）" : "基本均衡";
}
