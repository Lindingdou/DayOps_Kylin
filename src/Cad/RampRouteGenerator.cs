using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 运输约束(布线/运力所需标量)。忠实镜像原 MineAssLib.Models.TransportConstraintSettings 中
/// RampRouteGenerator 实际读取的 6 字段(最小参数, 默认值照搬原版), 不移全 34 属性设置域。
/// </summary>
public sealed class RampRouteConstraints
{
    /// <summary>限制坡度 i_max %。</summary>
    public double MaxGradePct { get; set; } = 8.0;
    /// <summary>卡车最小转弯半径 m(回头平台粗估 = 2×此值)。</summary>
    public double TruckTurnRadius { get; set; } = 10.0;
    /// <summary>卡车载重 t(单车道年运力公式)。</summary>
    public double TruckPayload { get; set; } = 90.0;
    /// <summary>车头时距 s(单车道通过能力 = 3600/此值 车/h)。</summary>
    public double HeadwaySec { get; set; } = 30.0;
    /// <summary>利用率 %(单车道年运力公式)。</summary>
    public double UtilizationPct { get; set; } = 80.0;
    /// <summary>年作业时间 h(单车道年运力公式)。</summary>
    public double WorkHoursPerYear { get; set; } = 5000.0;

    /// <summary>单车道通过能力 车/h = 3600 / 车头时距。</summary>
    public double LaneCapacityPreview() => HeadwaySec > 1e-6 ? 3600.0 / HeadwaySec : 0.0;
    /// <summary>单车道年运力 t = 通过能力(车/h) × 利用率 × 年作业时间 × 载重。</summary>
    public double PerLaneCapacityTons()
        => LaneCapacityPreview() * (UtilizationPct / 100.0) * WorkHoursPerYear * TruckPayload;
}

/// <summary>
/// 候选坑线段(选线结果的一条"边"):连接相邻两水平, 带展线形式与几何可行性。
/// 忠实镜像原 MineAssLib.RoadLayout.RampCandidate。
/// </summary>
public sealed class RampCandidate
{
    public double FromLevel { get; set; }                 // 上水平标高
    public double ToLevel { get; set; }                   // 下水平标高
    public RampForm Form { get; set; }                    // 斜坡道 / 转弯坡道 / 螺旋
    public double GradePct { get; set; }                  // 纵坡(=i_max)
    public double RequiredLengthM { get; set; }           // 需要的展线长 = ΔH / i
    public double AvailableLengthM { get; set; }          // 沿台阶线可用长度
    public double TurnRadius { get; set; }                // 转弯/回头半径(Straight=0)
    public double PerLaneCapacityTons { get; set; }       // 单车道年运力 t(供网络流定容量)
    public bool GeomFeasible { get; set; }                // 几何可行(含回头平台放得下)
    public (double X, double Y, double Z) PortalStart { get; set; } // 起坡位置(锚定台阶线)
    public string Note { get; set; } = "";
}

/// <summary>
/// 选线器(Layer-1, 自动选线):关联台阶线 → 算出相邻水平间用斜坡道还是转弯坡道、起坡在哪。
/// 忠实移植原 MineAssLib.RoadLayout.RampRouteGenerator(首版实现, 纯几何、不依赖引擎、可单测)。
/// 输出候选边交给 <see cref="RoadLayoutSolver"/>(Layer-2 网络流)选边 + 定车道扛运量。
///
/// 逐对相邻非工作帮台阶, 按 "可用帮长 vs ΔH/i" 判定: 直腿够 → 斜坡道; 否则平盘够宽 → 转弯坡道(回头);
/// 都不够 → 螺旋(连续转弯兜底)。待精化(忠实原 TODO, 均未实现): 一段帮多腿 / 可布走廊 / 中线真偏置 /
/// 跨帮绕行 / 螺旋方案识别。
/// </summary>
public static class RampRouteGenerator
{
    public static IReadOnlyList<RampCandidate> Generate(
        IReadOnlyList<RampBenchLine> benches, RampRouteConstraints? constraints = null)
    {
        var list = new List<RampCandidate>();
        if (benches == null || benches.Count == 0) return list;

        var cons = constraints ?? new RampRouteConstraints();
        double iMax = cons.MaxGradePct;
        double perLaneTons = cons.PerLaneCapacityTons();
        double switchbackBermNeed = cons.TruckTurnRadius * 2.0; // 回头平台粗估:转弯直径

        // 只取非工作帮(固定坑线布在端帮/非工作帮),按标高从高到低排
        var walls = new List<RampBenchLine>();
        foreach (var b in benches)
            if (b != null && !b.IsWorkingWall) walls.Add(b);
        walls.Sort((a, b) => b.Level.CompareTo(a.Level));

        for (int k = 0; k + 1 < walls.Count; k++)
        {
            var up = walls[k];
            var dn = walls[k + 1];
            double dH = up.Level - dn.Level;
            if (dH <= 1e-6) continue;

            double needLen = iMax > 1e-6 ? dH / (iMax / 100.0) : double.PositiveInfinity;
            var line = (up.Crest != null && up.Crest.Count > 0) ? up.Crest : up.Toe;
            double avail = PolylineLength(line);

            // 选型(选出可行的):直腿够长 → 斜坡道;否则平盘够宽 → 转弯坡道(回头);
            //                  都不够 → 螺旋(连续转弯兜底,使该段仍可行)。
            RampForm form;
            string note;
            if (avail + 1e-6 >= needLen)
            {
                form = RampForm.Straight;
                note = $"斜坡道:可用{avail:0}m ≥ 需{needLen:0}m";
            }
            else if (up.BermWidth + 1e-6 >= switchbackBermNeed)
            {
                form = RampForm.Switchback;
                note = $"转弯坡道(回头):直腿不够({avail:0}＜{needLen:0}m),平盘{up.BermWidth:0}m可容回头";
            }
            else
            {
                form = RampForm.Spiral;
                note = $"螺旋:直腿/回头都不够,改连续螺旋下降(r≥{cons.TruckTurnRadius:0}m)";
            }

            list.Add(new RampCandidate
            {
                FromLevel = up.Level,
                ToLevel = dn.Level,
                Form = form,
                GradePct = iMax,
                RequiredLengthM = needLen,
                AvailableLengthM = avail,
                TurnRadius = form == RampForm.Straight ? 0.0 : cons.TruckTurnRadius,
                PerLaneCapacityTons = perLaneTons,
                // 形式已兜底为可行;仅在无坡度/无台阶等退化情形不可行。
                GeomFeasible = needLen < double.PositiveInfinity && iMax > 1e-6,
                PortalStart = FirstPoint(line),
                Note = note
            });
        }
        return list;
    }

    /// <summary>统计:斜坡道 / 转弯坡道 / 螺旋 / 不可行 各几条(供命令行汇报)。忠实原 Tally。</summary>
    public static (int straight, int switchback, int spiral, int infeasible) Tally(IReadOnlyList<RampCandidate> c)
    {
        int s = 0, sb = 0, sp = 0, bad = 0;
        foreach (var x in c)
        {
            if (!x.GeomFeasible) bad++;
            switch (x.Form)
            {
                case RampForm.Straight: s++; break;
                case RampForm.Switchback: sb++; break;
                case RampForm.Spiral: sp++; break;
            }
        }
        return (s, sb, sp, bad);
    }

    /// <summary>展线形式的显示标签(可视化标注 / 命令行用)。忠实原 FormLabel。</summary>
    public static string FormLabel(RampForm f) => f switch
    {
        RampForm.Straight => "斜坡道",
        RampForm.Switchback => "转弯坡道",
        RampForm.Spiral => "螺旋",
        _ => "?"
    };

    private static double PolylineLength(IReadOnlyList<(double X, double Y, double Z)>? pts)
    {
        if (pts == null || pts.Count < 2) return 0.0;
        double L = 0.0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X;
            double dy = pts[i].Y - pts[i - 1].Y;
            double dz = pts[i].Z - pts[i - 1].Z;
            L += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return L;
    }

    private static (double X, double Y, double Z) FirstPoint(IReadOnlyList<(double X, double Y, double Z)>? pts)
        => (pts != null && pts.Count > 0) ? pts[0] : (0.0, 0.0, 0.0);
}
