using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>对比矩阵的一行（一个指标，跨方案）。</summary>
public sealed class IndicatorRow
{
    public string Group { get; set; } = "";
    public string Label { get; set; } = "";     // 指标名 + 单位
    public string[] Cells { get; set; } = Array.Empty<string>(); // 各方案显示值（最优带 ▲）
    public string Best { get; set; } = "—";      // 最优方案名
    /// <summary>true = 分组标题行（Avalonia DataGrid 无分组，用行代替原 WPF GroupStyle）。</summary>
    public bool IsGroupHeader { get; set; }
    public string Cell(int i) => i >= 0 && i < Cells.Length ? Cells[i] : "";
    // DataGrid 绑定用固定列（最多 8 个方案；原版列在代码动态生成）
    public string C0 => Cell(0); public string C1 => Cell(1); public string C2 => Cell(2); public string C3 => Cell(3);
    public string C4 => Cell(4); public string C5 => Cell(5); public string C6 => Cell(6); public string C7 => Cell(7);
}

public sealed class ComparisonResult
{
    public string[] Names { get; set; } = Array.Empty<string>();
    public List<IndicatorRow> Rows { get; set; } = new();
    public int RecommendIndex { get; set; } = -1;
}

/// <summary>
/// 方案系统全面对比（采矿手册"方案比较法"，原 <c>ComparisonBuilder</c>）：指标 × 方案矩阵 + 多准则加权综合评分/排名/推荐。
/// 指标按组分类（资源储量 / 剥采比 / 境界几何 / 经济 / 生产时序 / 安全技术 / 综合评价）；
/// 方向感知归一（高优 +1 / 低优 −1 / 仅展示 0），缺值跳过。纯函数、可复算。
/// </summary>
public static class ComparisonBuilder
{
    private sealed record Ind(string Group, string Name, string Unit, Func<PitResult, double?> Get, int Dir, double W, string Fmt);

    private static readonly Ind[] Specs =
    {
        new("资源储量", "煤量", "万t", r => r.CoalWanT, +1, 2.0, "N0"),
        new("资源储量", "岩量(剥离)", "万m³", r => r.WasteWanM3, -1, 1.0, "N0"),
        new("资源储量", "资源回收率", "%", r => r.RecoveryPct, +1, 1.5, "F1"),
        new("资源储量", "平均灰分", "%", r => r.AvgAshPct, -1, 1.0, "F1"),

        new("剥采比", "平均剥采比", "m³/t", r => r.AvgRatio, -1, 2.0, "F2"),
        new("剥采比", "境界剥采比", "m³/t", r => r.ContourRatio, 0, 0, "F2"),
        new("剥采比", "生产剥采比峰值", "m³/t", r => r.ProductionRatioPeak, -1, 1.0, "F2"),
        new("剥采比", "经济合理剥采比 n经", "m³/t", r => r.EconRatio, 0, 0, "F2"),

        new("境界几何", "开采深度", "m", r => r.DepthM, 0, 0, "N0"),
        new("境界几何", "底宽", "m", r => r.BottomWidthM, 0, 0, "N0"),
        new("境界几何", "占地面积", "ha", r => r.TopAreaHa, -1, 0.5, "N0"),
        new("境界几何", "台阶数", "个", r => r.BenchCount, 0, 0, "N0"),

        new("经济", "未折现净值", "万元", r => r.NetValue, +1, 3.0, "N0"),
        new("经济", "折现 NPV", "万元", r => r.Npv, +1, 2.5, "N0"),
        new("经济", "单位成本", "元/t", r => r.UnitCostYuanPerT, -1, 1.5, "N0"),

        new("生产时序", "服务年限", "a", r => r.ServiceLifeYears, +1, 1.0, "F0"),
        new("生产时序", "年均采出", "万t", r => r.AnnualCoalWanT, 0, 0, "N0"),

        new("安全技术", "最小安全系数 F", "", r => r.MinSafetyF, +1, 1.5, "F2"),
        new("安全技术", "校核(平均≤n经)", "", r => r.Ok ? 1.0 : 0.0, +1, 1.0, "bool"),
    };

    public static ComparisonResult Build(IReadOnlyList<PitScheme> schemes, bool withGroupHeaders = false)
    {
        var solved = schemes.Where(s => s.Result != null).ToList();
        var res = new ComparisonResult { Names = solved.Select(s => s.Name).ToArray() };
        int n = solved.Count;
        if (n == 0) return res;

        var results = solved.Select(s => s.Result!).ToList();
        var scores = new double[n];
        double totalW = 0;
        string lastGroup = "";

        foreach (var sp in Specs)
        {
            if (withGroupHeaders && sp.Group != lastGroup)
            {
                res.Rows.Add(new IndicatorRow { Group = sp.Group, Label = sp.Group, IsGroupHeader = true, Cells = new string[n], Best = "" });
                lastGroup = sp.Group;
            }
            var vals = results.Select(sp.Get).ToArray();
            int best = BestIndex(vals, sp.Dir);
            var cells = new string[n];
            for (int i = 0; i < n; i++)
            {
                cells[i] = Fmt(vals[i], sp.Fmt);
                if (i == best && sp.Dir != 0) cells[i] = "▲ " + cells[i];
            }
            res.Rows.Add(new IndicatorRow
            {
                Group = sp.Group,
                Label = string.IsNullOrEmpty(sp.Unit) ? sp.Name : $"{sp.Name} ({sp.Unit})",
                Cells = cells,
                Best = (sp.Dir != 0 && best >= 0) ? res.Names[best] : "—",
            });

            if (sp.Dir != 0 && sp.W > 0)
            {
                var norm = Normalize(vals, sp.Dir);
                for (int i = 0; i < n; i++) scores[i] += sp.W * norm[i];
                totalW += sp.W;
            }
        }

        var score100 = scores.Select(s => totalW > 0 ? Math.Round(s / totalW * 100.0, 1) : 0).ToArray();
        int reco = -1; double bestScore = double.NegativeInfinity;
        for (int i = 0; i < n; i++) if (score100[i] > bestScore) { bestScore = score100[i]; reco = i; }
        var ranks = Ranks(score100);

        if (withGroupHeaders)
            res.Rows.Add(new IndicatorRow { Group = "综合评价", Label = "综合评价", IsGroupHeader = true, Cells = new string[n], Best = "" });
        res.Rows.Add(new IndicatorRow
        {
            Group = "综合评价", Label = "综合评分 (0–100)",
            Cells = Enumerable.Range(0, n).Select(i => i == reco ? $"▲ {score100[i]:F1}" : $"{score100[i]:F1}").ToArray(),
            Best = reco >= 0 ? res.Names[reco] : "—",
        });
        res.Rows.Add(new IndicatorRow
        {
            Group = "综合评价", Label = "排名",
            Cells = ranks.Select(r => $"第 {r} 名").ToArray(),
            Best = "—",
        });
        res.Rows.Add(new IndicatorRow
        {
            Group = "综合评价", Label = "推荐",
            Cells = Enumerable.Range(0, n).Select(i => i == reco ? "✔ 推荐" : "").ToArray(),
            Best = reco >= 0 ? res.Names[reco] : "—",
        });
        res.RecommendIndex = reco;
        return res;
    }

    private static int BestIndex(double?[] vals, int dir)
    {
        if (dir == 0) return -1;
        int best = -1;
        double bv = dir > 0 ? double.NegativeInfinity : double.PositiveInfinity;
        for (int i = 0; i < vals.Length; i++)
        {
            if (vals[i] is not { } v) continue;
            if (dir > 0 ? v > bv : v < bv) { bv = v; best = i; }
        }
        return best;
    }

    private static double[] Normalize(double?[] vals, int dir)
    {
        var norm = new double[vals.Length];
        var present = vals.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return norm;
        double mn = present.Min(), mx = present.Max();
        for (int i = 0; i < vals.Length; i++)
        {
            if (vals[i] is not { } v) { norm[i] = 0; continue; }
            if (mx - mn < 1e-12) { norm[i] = 0.5; continue; }
            double t = (v - mn) / (mx - mn);
            norm[i] = dir > 0 ? t : 1 - t;
        }
        return norm;
    }

    private static int[] Ranks(double[] score)
    {
        var idx = Enumerable.Range(0, score.Length).OrderByDescending(i => score[i]).ToArray();
        var rank = new int[score.Length];
        for (int r = 0; r < idx.Length; r++) rank[idx[r]] = r + 1;
        return rank;
    }

    private static string Fmt(double? v, string fmt)
    {
        if (v is not { } x) return "—";
        return fmt switch
        {
            "bool" => x >= 0.5 ? "通过" : "超限",
            "N0" => x.ToString("N0", CultureInfo.CurrentCulture),
            "F0" => x.ToString("F0", CultureInfo.CurrentCulture),
            "F1" => x.ToString("F1", CultureInfo.CurrentCulture),
            "F2" => x.ToString("F2", CultureInfo.CurrentCulture),
            _ => x.ToString(CultureInfo.CurrentCulture),
        };
    }

    /// <summary>导出报表文本（原窗口「导出报表」按钮：储量与剥采比评价报表；CSV 逐行 指标,方案…,最优）。</summary>
    public static string ToCsv(ComparisonResult cmp)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("指标");
        foreach (var nme in cmp.Names) sb.Append(',').Append(nme);
        sb.AppendLine(",最优");
        foreach (var r in cmp.Rows)
        {
            if (r.IsGroupHeader) { sb.AppendLine($"[{r.Label}]"); continue; }
            sb.Append(r.Label);
            foreach (var c in r.Cells) sb.Append(',').Append(c);
            sb.Append(',').AppendLine(r.Best);
        }
        return sb.ToString();
    }
}
