using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// LAS 点云质量报告的分类统计部分（忠实原 pc_quality「…高程分布/**强度分类**」）——
/// 按 ASPRS 分类码统计各类点数 + 常见码中文名。强度/高程分布走 <see cref="Statistics.Describe"/>。纯逻辑、可单测。
/// </summary>
public static class LasQualityReport
{
    public readonly record struct ClassCount(byte Code, string Name, int Count);

    /// <summary>ASPRS 标准分类码常见名。</summary>
    public static string ClassName(byte code) => code switch
    {
        0 => "从未分类", 1 => "未分类", 2 => "地面", 3 => "低植被", 4 => "中植被", 5 => "高植被",
        6 => "建筑", 7 => "低点(噪声)", 8 => "模型关键点", 9 => "水", 10 => "轨道", 11 => "路面",
        12 => "重叠", 13 => "导线护罩", 14 => "导线", 15 => "输电塔", _ => $"类{code}",
    };

    /// <summary>逐分类码统计点数, 按点数降序返回(code, 名, 数)。</summary>
    public static List<ClassCount> ClassBreakdown(IReadOnlyList<byte> classification)
    {
        var res = new List<ClassCount>();
        if (classification == null || classification.Count == 0) return res;
        var counts = new Dictionary<byte, int>();
        foreach (var c in classification) counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
        foreach (var kv in counts) res.Add(new ClassCount(kv.Key, ClassName(kv.Key), kv.Value));
        res.Sort((a, b) => b.Count.CompareTo(a.Count));
        return res;
    }

    /// <summary>分类统计 → CSV(code,name,count,pct)。</summary>
    public static string ClassBreakdownCsv(IReadOnlyList<ClassCount> breakdown)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int total = 0; foreach (var b in breakdown) total += b.Count;
        var sb = new System.Text.StringBuilder("code,name,count,pct\n");
        foreach (var b in breakdown)
        {
            double pct = total > 0 ? b.Count * 100.0 / total : 0;
            sb.Append(b.Code).Append(',').Append(b.Name).Append(',').Append(b.Count).Append(',')
              .Append(pct.ToString("0.##", inv)).Append('\n');
        }
        return sb.ToString();
    }
}
