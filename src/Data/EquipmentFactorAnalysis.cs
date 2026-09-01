using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  设备主控因素分析 —— 忠实原 EquipmentAnalysisWindow「因素分析(主控因素 + 相关性)」的纯核:
//  把设备各月「因素(可用率/作业率/利用率/内外故障率)」与「产能(output)」做 Pearson 相关,
//  按 |r| 排名找主控因素, 标正/负向 + 强(≥0.7)/中(≥0.4)/弱。纯逻辑、可单测(合成向量)。
//  数据来自 equipment_kpi_monthly ⋈ capacity_monthly(equipment_id,year,month) 对齐行。
// ─────────────────────────────────────────────────────────────────────────────

public static class EquipmentFactorAnalysis
{
    /// <summary>一台设备一月的因素 + 产能对齐行。</summary>
    public sealed record FactorRow(double Availability, double RunRate, double Utilization,
        double InternalFaultPct, double ExternalFaultPct, double Output);

    public sealed record FactorCorr(string Factor, double R, string Strength, string Direction);

    private static readonly (string name, Func<FactorRow, double> sel)[] Factors =
    {
        ("可用率", r => r.Availability), ("作业率", r => r.RunRate), ("利用率", r => r.Utilization),
        ("内部故障率", r => r.InternalFaultPct), ("外部故障率", r => r.ExternalFaultPct),
    };

    /// <summary>各因素对产能的 Pearson 相关, 按 |r| 降序。无方差/样本&lt;2 的因素略去。</summary>
    public static List<FactorCorr> Correlate(IReadOnlyList<FactorRow> rows)
    {
        var outp = new List<FactorCorr>();
        if (rows == null || rows.Count < 2) return outp;
        var ys = rows.Select(r => r.Output).ToList();
        foreach (var (name, sel) in Factors)
        {
            double? r = Pearson(rows.Select(sel).ToList(), ys);
            if (r is not double rv) continue;
            string dir = rv >= 0 ? "正" : "负";
            double a = Math.Abs(rv);
            string strength = a >= 0.7 ? "强" : a >= 0.4 ? "中" : "弱";
            outp.Add(new FactorCorr(name, rv, strength, dir));
        }
        return outp.OrderByDescending(c => Math.Abs(c.R)).ToList();
    }

    /// <summary>Pearson r∈[-1,1]。长度不等/&lt;2/任一方差为 0 → null。</summary>
    public static double? Pearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        int n = xs.Count;
        if (n < 2 || ys.Count != n) return null;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sx += dx * dx; sy += dy * dy; }
        double d = Math.Sqrt(sx * sy);
        return d < 1e-12 ? null : Math.Max(-1, Math.Min(1, sxy / d));
    }
}
