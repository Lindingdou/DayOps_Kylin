// 忠实移植自原 PitMine3D Modules/RoadLib/Render/RoadConditionSymbology.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>纵坡档位（R-L1）。边界 3 / 6 / 10 %，按 |坡| 分。</summary>
public enum GradeBand
{
    /// <summary>|坡| ≤ 3%：平段（平盘 / 缓接线）。</summary>
    Level,
    /// <summary>3 ~ 6%：一般坡道。</summary>
    Mild,
    /// <summary>6 ~ 10%：陡坡（与瓶颈分析「陡坡」同口径）。</summary>
    Steep,
    /// <summary>&gt; 10%：超限坡。</summary>
    Over,
}

/// <summary>坡向（R-L2）：<b>沿中线点序行进</b>（= chevron 指的方向）看，是升还是降。</summary>
public enum GradeSense
{
    /// <summary>平段（仅在 <see cref="GradeBand.Level"/> 出现）。</summary>
    Level,
    /// <summary>爬升。</summary>
    Up,
    /// <summary>下降。</summary>
    Down,
}

/// <summary>
/// 一段同档连续中线。顶点闭区间 <c>[StartIndex, EndIndex]</c>（至少两个顶点），
/// 相邻 run <b>共享</b>交界顶点，故按 run 逐条画多段线不会在交界处留缝。
/// </summary>
/// <param name="StartIndex">起顶点下标（含）。</param>
/// <param name="EndIndex">止顶点下标（含）。</param>
/// <param name="Band">档位。</param>
/// <param name="Sense">坡向（沿点序）。</param>
/// <param name="AvgGradePct">本段带符号平均纵坡 % = Σ&#916;z / Σ&#916;水平距。</param>
/// <param name="LengthM">本段三维里程 m。</param>
public readonly record struct ConditionRun(
    int StartIndex,
    int EndIndex,
    GradeBand Band,
    GradeSense Sense,
    double AvgGradePct,
    double LengthM);

/// <summary>
/// 「路况显示」的分级核心（R-L1 / R-L2 / R-L5）：把一条中心线按<b>逐段纵坡</b>切成同档连续段，
/// 供 overlay 分色着笔。纯几何 + 纯分类，零 UI / 零宿主依赖，可单测。
///
/// 为什么坡度当主色：<c>GradePct</c> 是 <see cref="RoadEdge.RecomputeGeometry"/> 从中线反算的，
/// <b>任何一条边都有真值</b>；宽度 / 车道 / 限载只有走过「坑线落地」贴过属性的边才有，
/// 图上抽的中心线全是 0。坡度是唯一零成本、永远有值的路况量。
///
/// 口径三条（现场 2026-08-10 定）：
/// <list type="bullet">
///   <item>R-L1 档位边界 3 / 6 / 10 %。3 与 6 不是新数——分别取自「平盘/坡道」分界
///     与瓶颈分析的「陡坡」起点，同一仓库里不许「陡坡」有两个口径。</item>
///   <item>R-L2 上坡暖色 / 下坡冷色，「上下」相对<b>中线点序</b>——因为 chevron 指的就是这个方向。
///     不用「重车流向」：那要先有源汇与寻径结果，路况显示就不再是能单独点的纯展示命令了。</item>
///   <item>R-L5 逐段坡度必须<b>按弧长平滑</b>再定档。中线是从台阶线 / TIN 抽的，顶点密、Z 带噪，
///     逐顶点直接算坡度会把一条路打成上百个碎档。平滑 + 滞回一起用。</item>
///   <item>R-L6 <b>过渡带不成路段</b>。平滑窗口跨过「平盘↔坡道」这种硬转折时，会把 0%→8% 的突变
///     摊成一段长约 windowM 的斜坡，于是每个交界处凭空多出两截几米长的「缓坡」。
///     矿山路每个折返都有这种交界，不收掉整张图会碎成花的。判据：短于窗口长度、
///     且档位<b>严格夹在前后两段之间</b>的段判为窗口伪影，并入较长的邻居。
///     「严格夹在之间」这个限定是关键——真实的短陡段两侧都比它缓（档位是局部极值），不满足夹逼，保留。</item>
/// </list>
/// </summary>
public static class RoadConditionSymbology
{
    /// <summary>平段/缓坡边界 %。与 <c>RoadLibPlugin.flatGradePct</c>（路网预览判「平盘 vs 坡道」）同源。</summary>
    public const double BandMildPct = 3.0;

    /// <summary>缓坡/陡坡边界 %。与 <c>TransportIndicators.GradeFreePct</c>（瓶颈分析判「陡坡」）同源。</summary>
    public const double BandSteepPct = 6.0;

    /// <summary>陡坡/超限边界 %（现场 2026-08-10 核准）。</summary>
    public const double BandOverPct = 10.0;

    /// <summary>坡度平滑窗口（弧长 m，R-L5）。取 20m —— 与 chevron 步长 25m 同量级，不至于抹平真实坡段。</summary>
    public const double SmoothWindowM = 20.0;

    /// <summary>档位滞回带 %。平滑后坡度仍会在边界附近来回穿越，越界不够这个量就不切档，免得切出一串碎段。</summary>
    public const double HysteresisPct = 0.3;

    /// <summary>坡度绝对值上限 %（近垂直的退化段会算出天文数字，钳住免得污染统计与配色）。</summary>
    public const double MaxAbsGradePct = 100.0;

    /// <summary>(档,坡向) 合法组合数：平段 1 + 缓/陡/超限 各上下 2 = 7。</summary>
    public const int ClassCount = 7;

    private const double HorizEpsilon = 1e-9;

    /// <summary>
    /// 把中线切成同档连续段。中线不足 2 点（或全部退化成竖直/重合）返回空表。
    /// </summary>
    /// <param name="line">有序三维中线。</param>
    /// <param name="windowM">坡度平滑窗口弧长 m；≤0 表示不平滑（逐段原值）。</param>
    /// <param name="hysteresisPct">档位滞回带 %；≤0 表示不滞回。</param>
    public static List<ConditionRun> Classify(IReadOnlyList<Point3d> line,
                                              double windowM = SmoothWindowM,
                                              double hysteresisPct = HysteresisPct)
    {
        var runs = new List<ConditionRun>();
        if (line is null || line.Count < 2) return runs;

        int segCount = line.Count - 1;
        var dz = new double[segCount];      // 每段高差
        var dh = new double[segCount];      // 每段水平距
        var d3 = new double[segCount];      // 每段三维长
        var mid = new double[segCount];     // 每段中点的累计水平里程（平滑窗口的坐标轴）

        double acc = 0;
        for (int i = 0; i < segCount; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            dz[i] = b.Z - a.Z;
            dh[i] = a.HorizontalDistanceTo(b);
            d3[i] = a.DistanceTo(b);
            mid[i] = acc + dh[i] * 0.5;
            acc += dh[i];
        }
        if (acc <= HorizEpsilon) return runs;    // 整条线水平投影退化，谈不上纵坡

        var grade = SmoothGrades(dz, dh, mid, windowM);

        // 逐段定档（带滞回），连续同 (档,坡向) 合并成一个 run。
        var cur = ClassOf(grade[0]);
        int runStartSeg = 0;
        for (int i = 1; i <= segCount; i++)
        {
            var cls = i < segCount ? ClassOf(grade[i]) : cur;
            if (i < segCount && cls != cur && hysteresisPct > 0)
                cls = HoldOnHysteresis(cur, cls, Math.Abs(grade[i]), hysteresisPct);

            if (i == segCount || cls != cur)
            {
                runs.Add(MakeRun(runStartSeg, i - 1, cur, dz, dh, d3));
                runStartSeg = i;
                cur = cls;
            }
        }

        // R-L6:阈值取窗口长度本身——过渡带的跨度正是窗口跨度，两者同源才自洽。
        // 不平滑(windowM≤0)时没有窗口伪影,阈值随之为 0,一段也不并。
        MergeTransitionRuns(runs, windowM, dz, dh, d3);
        return runs;
    }

    /// <summary>
    /// R-L6：把「短于 <paramref name="minLenM"/> 且档位严格夹在前后两段之间」的过渡带并入较长的邻居。
    /// 并入后该段整体按<b>宿主的档位</b>着色（这是一次合并裁决，不是重新分类），
    /// 故 <see cref="ConditionRun.AvgGradePct"/> 会按合并后的真实里程重算、可能与档位边界不再严格对应。
    /// </summary>
    private static void MergeTransitionRuns(List<ConditionRun> runs, double minLenM,
                                            double[] dz, double[] dh, double[] d3)
    {
        if (minLenM <= 0) return;
        bool changed = true;
        while (changed && runs.Count >= 3)
        {
            changed = false;
            for (int i = 1; i + 1 < runs.Count; i++)
            {
                if (runs[i].LengthM >= minLenM) continue;
                int prev = (int)runs[i - 1].Band, mid = (int)runs[i].Band, next = (int)runs[i + 1].Band;
                bool between = (prev < mid && mid < next) || (next < mid && mid < prev);
                if (!between) continue;   // 局部极值(真实短陡段)——保留

                int host = runs[i - 1].LengthM >= runs[i + 1].LengthM ? i - 1 : i + 1;
                int lo = Math.Min(host, i), hi = Math.Max(host, i);
                runs[lo] = MakeRun(runs[lo].StartIndex, runs[hi].EndIndex - 1,
                                   (runs[host].Band, runs[host].Sense), dz, dh, d3);
                runs.RemoveAt(hi);
                changed = true;
                break;
            }
        }
    }

    /// <summary>
    /// 按弧长窗口平滑逐段坡度：窗口内 <c>&#931;&#916;z / &#931;&#916;水平距</c>
    /// —— 即窗口两端之间的平均坡度，不是段坡的算术平均（后者会被短段放大）。
    /// 窗口按<b>段中点落在 ±windowM/2 内</b>取段，本段永远在内。
    /// </summary>
    private static double[] SmoothGrades(double[] dz, double[] dh, double[] mid, double windowM)
    {
        int n = dz.Length;
        var g = new double[n];
        double half = windowM * 0.5;

        // mid 单调不减，用双指针滑窗，O(n)。
        int lo = 0, hi = 0;
        double sumZ = 0, sumH = 0;
        for (int i = 0; i < n; i++)
        {
            if (windowM <= 0)
            {
                g[i] = Clamp(dh[i] > HorizEpsilon ? dz[i] / dh[i] * 100.0 : 0.0);
                continue;
            }
            double left = mid[i] - half, right = mid[i] + half;
            while (hi < n && mid[hi] <= right) { sumZ += dz[hi]; sumH += dh[hi]; hi++; }
            while (lo < n && mid[lo] < left) { sumZ -= dz[lo]; sumH -= dh[lo]; lo++; }
            // 窗口内水平距可能全退化（一串竖直/重合段）——退回本段原值，仍退化则记 0。
            g[i] = sumH > HorizEpsilon ? Clamp(sumZ / sumH * 100.0)
                 : (dh[i] > HorizEpsilon ? Clamp(dz[i] / dh[i] * 100.0) : 0.0);
        }
        return g;
    }

    private static double Clamp(double gradePct) =>
        gradePct > MaxAbsGradePct ? MaxAbsGradePct
        : (gradePct < -MaxAbsGradePct ? -MaxAbsGradePct : gradePct);

    private static ConditionRun MakeRun(int seg0, int seg1, (GradeBand Band, GradeSense Sense) cls,
                                        double[] dz, double[] dh, double[] d3)
    {
        double sz = 0, sh = 0, s3 = 0;
        for (int i = seg0; i <= seg1; i++) { sz += dz[i]; sh += dh[i]; s3 += d3[i]; }
        double avg = sh > HorizEpsilon ? Clamp(sz / sh * 100.0) : 0.0;
        return new ConditionRun(seg0, seg1 + 1, cls.Band, cls.Sense, avg, s3);
    }

    /// <summary>带符号坡度 → (档, 坡向)。平段不分上下（<see cref="GradeSense.Level"/>）。</summary>
    public static (GradeBand Band, GradeSense Sense) ClassOf(double gradePct)
    {
        double ag = Math.Abs(gradePct);
        if (ag <= BandMildPct) return (GradeBand.Level, GradeSense.Level);
        var sense = gradePct > 0 ? GradeSense.Up : GradeSense.Down;
        if (ag <= BandSteepPct) return (GradeBand.Mild, sense);
        if (ag <= BandOverPct) return (GradeBand.Steep, sense);
        return (GradeBand.Over, sense);
    }

    /// <summary>
    /// 滞回：候选档与当前档之间那条边界，没被越过 <paramref name="hyst"/> 就按住不切。
    /// 坡向翻转天然被同一条闸挡住 —— 从 +3.5% 走到 −3.5% 必须先穿过 |坡|≤3% 的平段档。
    /// </summary>
    private static (GradeBand Band, GradeSense Sense) HoldOnHysteresis(
        (GradeBand Band, GradeSense Sense) cur, (GradeBand Band, GradeSense Sense) cand,
        double absGrade, double hyst)
    {
        int a = (int)cur.Band, b = (int)cand.Band;
        if (a == b) return cand;                       // 同档换向：已由平段档隔开，直接放行
        double boundary = Math.Max(a, b) switch
        {
            1 => BandMildPct,
            2 => BandSteepPct,
            _ => BandOverPct,
        };
        return Math.Abs(absGrade - boundary) < hyst ? cur : cand;
    }

    /// <summary>(档,坡向) → 分色（取自 <see cref="RoadSymbology"/> 统一符号字典）。</summary>
    public static Rgb ColorOf(GradeBand band, GradeSense sense) => band switch
    {
        GradeBand.Mild => sense == GradeSense.Up ? RoadSymbology.GradeMildUp : RoadSymbology.GradeMildDown,
        GradeBand.Steep => sense == GradeSense.Up ? RoadSymbology.GradeSteepUp : RoadSymbology.GradeSteepDown,
        GradeBand.Over => sense == GradeSense.Up ? RoadSymbology.GradeOverUp : RoadSymbology.GradeOverDown,
        _ => RoadSymbology.GradeLevel,
    };

    /// <summary>(档,坡向) → 回显用的中文标签（命令行回显即图例）。</summary>
    public static string TextOf(GradeBand band, GradeSense sense)
    {
        string arrow = sense == GradeSense.Up ? "↑" : (sense == GradeSense.Down ? "↓" : "");
        return band switch
        {
            GradeBand.Mild => $"缓坡{arrow}",
            GradeBand.Steep => $"陡坡{arrow}",
            GradeBand.Over => $"超限{arrow}",
            _ => "平段",
        };
    }

    /// <summary>(档,坡向) → 0..<see cref="ClassCount"/>-1 的稠密下标，方便按类累计长度。</summary>
    public static int ClassIndex(GradeBand band, GradeSense sense) => band switch
    {
        GradeBand.Mild => sense == GradeSense.Up ? 1 : 2,
        GradeBand.Steep => sense == GradeSense.Up ? 3 : 4,
        GradeBand.Over => sense == GradeSense.Up ? 5 : 6,
        _ => 0,
    };

    /// <summary><see cref="ClassIndex"/> 的逆。下标越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public static (GradeBand Band, GradeSense Sense) ClassAt(int index) => index switch
    {
        0 => (GradeBand.Level, GradeSense.Level),
        1 => (GradeBand.Mild, GradeSense.Up),
        2 => (GradeBand.Mild, GradeSense.Down),
        3 => (GradeBand.Steep, GradeSense.Up),
        4 => (GradeBand.Steep, GradeSense.Down),
        5 => (GradeBand.Over, GradeSense.Up),
        6 => (GradeBand.Over, GradeSense.Down),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}
