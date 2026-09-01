using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 熵权法客观赋权 —— 忠实原 EquipmentShiftForecastWindow §2.5「客观赋权」：
/// 指标离散度越大权重越高(替固定专家权重)。kEnt=1/ln(m); pᵢⱼ=xᵢⱼ/Σᵢxᵢⱼ; eⱼ=−kEnt·Σpᵢⱼ·ln pᵢⱼ; wⱼ=(1−eⱼ)/Σ(1−e)。
/// 纯逻辑、可单测。用于设备五维综合评分等多准则加权。
/// </summary>
public static class EntropyWeighting
{
    /// <summary>由 m 行(样本)×p 列(指标)矩阵算各指标熵权。m&lt;3 或退化 → 等权 1/p。忠实原 EntropyWeights。</summary>
    public static double[] Weights(IReadOnlyList<double[]> rows)
    {
        int m = rows?.Count ?? 0;
        int p = m > 0 ? rows![0].Length : 0;
        var w = new double[p];
        if (m < 3 || p == 0) { for (int j = 0; j < p; j++) w[j] = p > 0 ? 1.0 / p : 0; return w; }
        double kEnt = 1.0 / Math.Log(m);
        var oneMinusE = new double[p]; double sum = 0;
        for (int j = 0; j < p; j++)
        {
            double col = 1e-9;
            for (int i = 0; i < m; i++) col += Math.Max(0, rows[i][j]);
            double e = 0;
            for (int i = 0; i < m; i++)
            {
                double pij = Math.Max(0, rows[i][j]) / col;
                if (pij > 1e-12) e += pij * Math.Log(pij);
            }
            oneMinusE[j] = Math.Max(0, 1 - (-kEnt * e));
            sum += oneMinusE[j];
        }
        for (int j = 0; j < p; j++) w[j] = sum > 1e-9 ? oneMinusE[j] / sum : 1.0 / p;
        return w;
    }

    /// <summary>加权综合得分 = Σⱼ wⱼ·dimⱼ。</summary>
    public static double Composite(double[] dims, double[] weights)
    {
        double s = 0; int n = Math.Min(dims.Length, weights.Length);
        for (int j = 0; j < n; j++) s += dims[j] * weights[j];
        return s;
    }
}
