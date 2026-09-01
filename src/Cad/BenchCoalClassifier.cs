using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>台阶煤/岩判定结果。</summary>
public sealed class BenchCoalResult
{
    public enum Kind { Rock = 0, Coal = 1, Mixed = 2 }
    public Kind BenchKind;
    /// <summary>煤厚占比 [0..1] = 沿线平均煤厚 ÷ 台阶高。</summary>
    public double CoalRatio;
    /// <summary>沿线平均煤厚（m）。</summary>
    public double MeanCoalThickM;
    public double BenchHeightM;
    /// <summary>是否出煤体：平均煤厚 ≥ 最小可采厚（与占比脱钩——薄煤层不因台阶高而永远判不出）。</summary>
    public bool IsMineableCoal;
    /// <summary>采样点数 / 其中至少一层见煤的点数。</summary>
    public int SampleCount, SampleHit;
    /// <summary>涉及煤层（名, 沿线平均厚 m），按平均厚降序。</summary>
    public List<(string Name, double MeanThickM)> Seams = new();

    public string KindLabel => BenchKind switch { Kind.Coal => "煤台阶", Kind.Mixed => "混合台阶", _ => "岩台阶" };
    public string SeamSummary => Seams.Count == 0 ? "" : string.Join(" / ", Seams.Select(s => $"{s.Name}({s.MeanThickM:0.##}m)"));
}

/// <summary>
/// 台阶煤/岩判定 —— 沿台阶坡顶线等点采样各煤层柱(<see cref="VirtualBorehole"/>), 算各层与台阶区间
/// [toeZ,crestZ] 的重叠煤厚之和 → 沿线平均煤厚 → 煤厚占比=均厚/台阶高 → 煤(≥0.5)/混(>0.05)/岩 台阶。
///
/// 要点(改前先想清): ①分母用【全部采样点】而非"采到煤的点"——真尖灭处煤厚就是 0, 本就该把均值拉下来;
/// ②各煤层分别累计厚度(一级台阶可压多层煤, 后续按煤种配矿要用); ③是否出煤体看均厚≥最小可采(与占比
/// 脱钩——4 号煤 1.29m 套 15m 台阶占比才 9%, 拿占比当生成开关会让薄煤层永远判不出煤台阶)。
/// 是原 StandardLevelModel 煤/岩判定核(SeamColumnSampler.CoalThicknessIn + 分类)的自足切片。
/// 纯逻辑、可单测。（原 795 行 StandardLevelModel 的台阶线→标准水平级→相邻级配对含实测图纸调校的
/// 相对闸门启发式, 本机无真图纸不可验, 未移——见迁移进度。此处只移可验证的煤/岩判定核。）
/// </summary>
public static class BenchCoalClassifier
{
    public static BenchCoalResult Classify(
        IReadOnlyList<(double x, double y)> crestFootprint, double toeZ, double crestZ,
        IReadOnlyList<VirtualBorehole.Seam> seams,
        double coalRatioThreshold = 0.5, double mixedRatioFloor = 0.05, double minMineableCoalThickM = 0.8)
    {
        var res = new BenchCoalResult { BenchHeightM = Math.Max(0, crestZ - toeZ) };
        if (crestFootprint == null || crestFootprint.Count == 0 || res.BenchHeightM <= 1e-9 || seams == null)
            return res;

        var perSeam = new Dictionary<string, double>();
        double sumCoal = 0;
        foreach (var (x, y) in crestFootprint)
        {
            res.SampleCount++;
            var hits = VirtualBorehole.Drill(x, y, seams);
            double atPt = 0;
            foreach (var h in hits)
            {
                double lo = Math.Max(toeZ, h.FloorZ), hi = Math.Min(crestZ, h.RoofZ);   // 台阶区间 ∩ 煤层区间
                double ov = hi - lo;
                if (ov <= 0) continue;                                                   // 煤层跨出台阶的部分不算
                atPt += ov;
                perSeam[h.SeamCode] = (perSeam.TryGetValue(h.SeamCode, out double had) ? had : 0) + ov;
            }
            if (atPt > 1e-9) res.SampleHit++;
            sumCoal += atPt;
        }

        res.MeanCoalThickM = sumCoal / res.SampleCount;                                   // 分母=全部采样点
        res.CoalRatio = res.MeanCoalThickM / res.BenchHeightM;
        res.BenchKind = res.CoalRatio >= coalRatioThreshold ? BenchCoalResult.Kind.Coal
                      : res.CoalRatio > mixedRatioFloor ? BenchCoalResult.Kind.Mixed
                      : BenchCoalResult.Kind.Rock;
        res.IsMineableCoal = res.MeanCoalThickM >= minMineableCoalThickM;
        res.Seams = perSeam.Select(kv => (kv.Key, kv.Value / res.SampleCount))
                           .OrderByDescending(s => s.Item2).ToList();
        return res;
    }
}
