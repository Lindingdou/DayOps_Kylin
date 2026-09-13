using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Transport;

namespace PitMine3D.Kylin.Cad.RoadLayout;

/// <summary>展线形式:斜坡道(直进下降) / 转弯坡道(回头·换向) / 螺旋(连续转弯)。</summary>
public enum RampForm
{
    /// <summary>斜坡道:沿一段帮直线下降(可用帮长 ≥ ΔH/i)。</summary>
    Straight,
    /// <summary>转弯坡道:帮段不够或须反向 → 回头曲线(半径≥R_min)+平台。</summary>
    Switchback,
    /// <summary>螺旋:平面受限/近圆形坑,连续转弯免回头平台。</summary>
    Spiral
}

/// <summary>
/// 台阶线(锚定几何):一个水平的坡顶/坡底 polyline + 平盘宽。
/// 坑线沿平盘走、穿坡面下降,故候选起坡位置都落在台阶线上。
/// 来源:批量台阶扩帮产出的 toe/crest,或点云提取的坡顶/坡底线。
/// </summary>
public sealed class BenchLine
{
    public double Level { get; set; }                                   // 水平标高
    public IReadOnlyList<(double X, double Y, double Z)> Crest { get; set; } // 坡顶线
        = new List<(double, double, double)>();
    public IReadOnlyList<(double X, double Y, double Z)> Toe { get; set; }   // 坡底线
        = new List<(double, double, double)>();
    public double BermWidth { get; set; }                               // 平盘宽(判回头放得下)
    public bool IsWorkingWall { get; set; }                             // 工作帮?(固定坑线只布非工作帮)
    /// <summary>
    /// 该线是否闭合成环。false = 开口线(端帮一段),末点→首点之间是**虚拟闭合段**,那里根本没有台阶线,
    /// 不能当可布走廊用(见 TODO②)。默认 true —— 与既有"隐式闭合"口径一致,老调用方行为不变。
    /// 数据源侧 BenchRecord 本就带 Closed,接上即可逐条如实给。
    /// </summary>
    public bool Closed { get; set; } = true;
}

/// <summary>
/// 候选坑线段(选线结果的一条"边"):连接相邻两水平,带展线形式与几何可行性。
/// 「哪些位置设斜坡道 / 转弯坡道」的回答 = 这批候选里每条的 Form + PortalStart。
/// 同一对台阶会产出多条(起坡点沿环弧长错开),上层求解器每级择一条 → 才有"择路"可言。
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
    /// <summary>
    /// 沿帮中心线点列(起坡点 → 落到下水平):XY 自 PortalStart 沿上环推进 RequiredLengthM,
    /// Z 按已走弧长线性从 FromLevel 降到 ToLevel。多腿折返时含腿间折点,螺旋时自然绕环多圈。
    /// 默认空表 —— 只读 PortalStart 的老调用方不受影响。
    /// </summary>
    public IReadOnlyList<(double X, double Y, double Z)> Centerline { get; set; }
        = new List<(double, double, double)>();
    /// <summary>
    /// 本级**可布走廊**总长 m(上环周长扣掉放不下路面 / 禁布 / 已占的区段后,见 TODO②)。
    /// 与 <see cref="AvailableLengthM"/> 的区别:本字段是整级的走廊总量,AvailableLengthM 是
    /// **本候选从自己的起坡点往前实际能连续走到的**那一段。默认 0 = 未评估(老调用方自造的候选)。
    /// </summary>
    public double CorridorLengthM { get; set; }
    /// <summary>
    /// 本候选沿环走过的帮段数(TODO④):1 = 就在起坡那一段帮里走完;&gt;1 = 绕过转角跨了几个帮段。
    /// 默认 1 —— 老调用方自造的候选按"不跨帮"看待,向后兼容。
    /// </summary>
    public int WallSpanCount { get; set; } = 1;
    public string Note { get; set; } = "";
}

/// <summary>
/// 选线器(Layer-1,自动选线):关联台阶线 → 算出相邻水平间用斜坡道还是转弯坡道、起坡在哪。
/// 输出候选边交给 <see cref="IRoadLayoutSolver"/>(Layer-2 网络流)选边 + 定车道扛运量。
/// </summary>
public interface IRampRouteGenerator
{
    IReadOnlyList<RampCandidate> Generate(RoadLayoutInput input);
}

/// <summary>
/// 选线器实现:逐对相邻非工作帮台阶,按 "可布走廊长 vs ΔH/i" 判定斜/转坡道,
/// 并把上环坡顶线按弧长均分 <see cref="CandidatesPerLevel"/> 份 → 同一级产出多个**位置不同**的候选。
///
/// 已实现:斜/转判定、回头平台可行性(平盘宽≥回头直径)、单车道年运力、起坡点锚定台阶线、
///         ①一段帮内折 2~3 直腿(腿间回头)、③沿帮真中心线点列、每级多候选(供上层择路)、
///         ②可用长度按【可布走廊】逐段筛(见 <see cref="LevelCorridor"/>)、④跨多个帮段绕行累计展线。
/// 待精化(TODO):⑤螺旋方案识别(现仅作兜底形式,未按"平面受限/近圆形坑"专门识别)。
///
/// **口径变更(②落地时)**:可用长度不再是"整条坡顶线的 3D 折线长",而是"闭合环上真正能布路的
/// 那一段弧长"——判据收紧,更多级会判成直线放不下 → 更早触发折返/螺旋,这是正确的。
/// </summary>
public sealed class RampRouteGenerator : IRampRouteGenerator
{
    /// <summary>每对相邻台阶产出的候选个数(起坡点沿上环弧长均分错开)。1 = 退回单候选。</summary>
    public const int CandidatesPerLevel = 3;

    /// <summary>同一级内允许折的最大直腿数(腿间回头);斜坡道/螺旋恒为 1 腿。</summary>
    public const int MaxLegsPerLevel = 3;

    /// <summary>帮段分界的转角阈值(度):顶点处转向超过它就算换了一段帮(端帮↔侧帮的拐点)。</summary>
    public const double WallCornerAngleDeg = 35.0;

    /// <summary>一条候选允许绕行累计的最大帮段数(TODO④)。绕太多帮才降一级不现实,到此止步如实标注。</summary>
    public const int MaxWallSpans = 4;

    /// <summary>中心线每条直腿的采样段数(点数 = 段数×腿数 + 1)。</summary>
    private const int CenterlineStepsPerLeg = 8;

    /// <summary>
    /// 既有坑线足迹(平面点列,可选注入):落在这些线的最小间距内的区段判为**已被占用**,从可布走廊里扣掉。
    /// 默认 null = 不扣 —— <see cref="RoadLayoutInput"/> 目前还没有"既有固定坑线走廊"字段(见其类内 TODO 注释),
    /// 故只能由调用方直接喂进来;没人喂时如实退化为"不考虑已布坑线冲突",不假装扣过。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>>? OccupiedRoads { get; set; }

    /// <summary>坑线最小间距 m(防路面叠加)。≤0 = 取路面宽 B,与 StraightRampRouteOptions.MinSpacingM 缺省口径一致。</summary>
    public double MinRoadSpacingM { get; set; }

    public IReadOnlyList<RampCandidate> Generate(RoadLayoutInput input)
    {
        var list = new List<RampCandidate>();
        if (input?.Benches == null || input.Benches.Count == 0) return list;

        var cons = input.Constraints ?? new TransportConstraintSettings();
        double iMax = cons.MaxGradePct;
        // 单车道年运力(t) = 单车道通过能力(车/h) × 利用率 × 年作业时间 × 载重
        double perLaneTons = cons.LaneCapacityPreview() * (cons.UtilizationPct / 100.0)
                           * cons.WorkHoursPerYear * cons.TruckPayload;
        double switchbackBermNeed = cons.TruckTurnRadius * 2.0; // 回头平台粗估:转弯直径
        double roadWidth = cons.RoadWidthPreview();             // 路面宽 B:可布走廊判据的核心尺寸
        double minSpacing = MinRoadSpacingM > 1e-6 ? MinRoadSpacingM : roadWidth;

        // 只取非工作帮(固定坑线布在端帮/非工作帮),按标高从高到低排;
        // 工作帮单收一份 —— 它不产候选,但**是禁布区**,要从可布走廊里扣掉(TODO②)。
        var walls = new List<BenchLine>();
        var workingWalls = new List<BenchLine>();
        foreach (var b in input.Benches)
        {
            if (b == null) continue;
            if (b.IsWorkingWall) workingWalls.Add(b); else walls.Add(b);
        }
        walls.Sort((a, b) => b.Level.CompareTo(a.Level));

        for (int k = 0; k + 1 < walls.Count; k++)
        {
            var up = walls[k];
            var dn = walls[k + 1];
            double dH = up.Level - dn.Level;
            if (dH <= 1e-6) continue;

            double needLen = iMax > 1e-6 ? dH / (iMax / 100.0) : double.PositiveInfinity;
            var line = (up.Crest != null && up.Crest.Count > 0) ? up.Crest : up.Toe;
            var lower = (dn.Crest != null && dn.Crest.Count > 0) ? dn.Crest : dn.Toe;
            var ring = new ArcRing(line);

            // TODO② 可布走廊:整条坡顶线不是处处能布路 —— 逐段筛掉"放不下路面 / 禁布 / 已被占"的区间,
            //   剩下的连续可布区间才是能算展线的长度。环退化时退回首版口径(整条折线长),不编造。
            var corridor = LevelCorridor.Build(ring, up, lower, dn.Closed, workingWalls, OccupiedRoads,
                                               roadWidth, minSpacing, cons.SafetyStrip, up.Level, dH * 0.5);

            // 多候选:把上环按弧长均分 nCand 份,各取一个起坡锚点 → 同一级多个位置供求解器择路。
            // 环退化(点不足/周长≈0)时退回单候选,起坡点仍取首点,与首版一致。
            int nCand = ring.IsValid ? Math.Max(1, CandidatesPerLevel) : 1;
            double prevArc = double.NegativeInfinity;
            for (int j = 0; j < nCand; j++)
            {
                double nominalArc = ring.Perimeter * j / nCand;   // j=0 落在首点,与首版起坡点一致
                double startArc = corridor.SnapForward(nominalArc);   // 名义弧位不可布 → 前移到最近可布处
                // 走廊很碎时多个名义弧位会吸附到同一处 → 往后另找,保各候选位置互异(否则谈不上"择路")。
                if (j > 0 && Math.Abs(startArc - prevArc) < 1e-9)
                {
                    double alt = corridor.SnapForward(prevArc + corridor.CellM);
                    startArc = Math.Abs(alt - prevArc) < 1e-9 ? nominalArc : alt;
                }
                prevArc = startArc;
                var portal = ring.IsValid ? ring.PointAtArc(startArc) : FirstPoint(line);

                // 从起坡点沿走廊往前走:①本帮段内能连续走多长 ②允许绕过转角跨帮时累计能走多长(TODO④)。
                // 环退化时无走廊可切,退回首版口径(整条坡顶线折线长),按"不跨帮"记。
                var run = ring.IsValid
                    ? corridor.RunFrom(startArc, needLen)
                    : new CorridorRun(PolylineLength(line), PolylineLength(line), 1);

                // 选型(选出可行的):本帮段直腿够长 → 斜坡道;绕几个帮段够长 → 仍是斜坡道(跨帮绕行);
                //   都不够但平盘够宽 → 转弯坡道(回头);再不够 → 螺旋兜底;连走廊都没有 → 如实判不可行。
                RampForm form;
                string note;
                int legs = 1;                   // 本候选直腿数:斜坡道/螺旋恒 1,折返 2~MaxLegsPerLevel
                double avail;                   // 本候选真正能用上的展线长
                bool feasible = needLen < double.PositiveInfinity && iMax > 1e-6;

                if (run.SingleWallM + 1e-6 >= needLen)
                {
                    form = RampForm.Straight;
                    avail = run.SingleWallM;
                    note = $"斜坡道:本帮段可布{avail:0}m ≥ 需{needLen:0}m";
                }
                else if (run.MultiWallM + 1e-6 >= needLen)
                {
                    // TODO④ 跨帮绕行:一段帮放不下 → 绕过转角接着走,把沿途各帮段的可布长度累加。
                    form = RampForm.Straight;
                    avail = run.MultiWallM;
                    note = $"斜坡道(绕{run.WallSpans}个帮段):累计可布{avail:0}m ≥ 需{needLen:0}m"
                         + $"(本帮段只有{run.SingleWallM:0}m)";
                }
                else if (run.MultiWallM > 1e-6 && up.BermWidth + 1e-6 >= switchbackBermNeed)
                {
                    form = RampForm.Switchback;
                    // TODO① 多腿链接:整条直腿放不下 → 在可布走廊上折 2~3 腿(腿间回头),
                    //   每折一腿多得一个 avail 的展线;仍不够则如实标注差额(须外凸折返台/加宽平盘)。
                    avail = run.MultiWallM;
                    legs = LegsNeeded(needLen, avail);
                    note = $"转弯坡道(回头):绕{run.WallSpans}帮段仅可布{avail:0}m＜需{needLen:0}m,"
                         + $"折{legs}腿,平盘{up.BermWidth:0}m可容回头";
                    double got = legs * avail;
                    if (got + 1e-6 < needLen) note += $";{legs}腿仍差{needLen - got:0}m(须外凸折返台)";
                }
                else if (run.MultiWallM > 1e-6)
                {
                    form = RampForm.Spiral;
                    avail = run.MultiWallM;
                    note = $"螺旋:直腿/回头都不够,改连续螺旋下降(r≥{cons.TruckTurnRadius:0}m)";
                    // 螺旋要连续绕环才降得下去 —— 走廊被阻断就绕不过去,不硬判可行。
                    if (!corridor.IsFullLoop)
                    {
                        feasible = false;
                        note += $";但走廊被阻断({corridor.BlockedM:0}m 不可布),连续绕环布不成,须先解阻断/改并段";
                    }
                }
                else
                {
                    form = RampForm.Spiral;
                    avail = 0.0;
                    feasible = false;
                    note = $"本级无可布走廊(路面宽{roadWidth:0.#}m 处处放不下),坑线布不下去";
                }
                note += $";{corridor.Summary}";
                if (nCand > 1) note += $";位置{j + 1}/{nCand}(弧长{startArc:0}m)";

                // 折返腿逐腿朝坑内让开的步距:一次回头占位(回头直径),且总让量不超环内径的一半。
                double inwardStep = legs > 1 && ring.InRadius > 1e-6
                    ? Math.Min(switchbackBermNeed, ring.InRadius * 0.5 / legs)
                    : 0.0;

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
                    // 形式仍以螺旋兜底;只在**真的没有走廊**(或无坡度/无台阶等退化)时才判不可行 —— 收紧但不虚判。
                    GeomFeasible = feasible,
                    PortalStart = portal,           // 口径同首版:锚在台阶线上(取该线自身 Z)
                    CorridorLengthM = corridor.DeployableM,
                    WallSpanCount = Math.Max(1, run.WallSpans),
                    Centerline = BuildCenterline(ring, startArc, needLen, legs, up.Level, dn.Level, inwardStep),
                    Note = note
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 按(上→下水平)分组:同一级的多个候选(起坡点错开)归一组,供上层求解器每组择一条。
    /// 用途:免把整张候选表当"一条链"直接串起来 —— 那样展线长会翻 <see cref="CandidatesPerLevel"/> 倍。
    /// 组序 = 首次出现序(Generate 产出即标高从高到低)。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<RampCandidate>> GroupByLevelPair(IReadOnlyList<RampCandidate> cands)
    {
        var groups = new List<IReadOnlyList<RampCandidate>>();
        if (cands == null) return groups;
        var buckets = new Dictionary<(double, double), List<RampCandidate>>();
        foreach (var c in cands)
        {
            if (c == null) continue;
            var key = (c.FromLevel, c.ToLevel);
            if (!buckets.TryGetValue(key, out var g))
            {
                g = new List<RampCandidate>();
                buckets[key] = g;
                groups.Add(g);          // 组序 = 首次出现序
            }
            g.Add(c);
        }
        return groups;
    }

    /// <summary>统计:斜坡道 / 转弯坡道 / 螺旋 / 不可行 各几条(供命令行汇报)。按候选条数计,非按级数。</summary>
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

    /// <summary>展线形式的显示标签(可视化标注 / 命令行用)。</summary>
    public static string FormLabel(RampForm f) => f switch
    {
        RampForm.Straight   => "斜坡道",
        RampForm.Switchback => "转弯坡道",
        RampForm.Spiral     => "螺旋",
        _ => "?"
    };

    /// <summary>展线形式的标注颜色 RGBA(0-1):斜=绿 / 转=橙 / 螺=蓝。不可行由调用方覆盖为红。</summary>
    public static (float R, float G, float B, float A) FormColor(RampForm f) => f switch
    {
        RampForm.Straight   => (0.30f, 0.80f, 0.40f, 1f), // 绿
        RampForm.Switchback => (1.00f, 0.60f, 0.10f, 1f), // 橙
        RampForm.Spiral     => (0.25f, 0.55f, 1.00f, 1f), // 蓝
        _ => (1f, 1f, 1f, 1f)
    };

    /// <summary>
    /// 一段帮放不下整条直腿时须折的腿数(TODO①):每折一腿多得一个可用帮长,封顶 <see cref="MaxLegsPerLevel"/>。
    /// 折返按定义至少 2 腿;封顶后仍不够由调用方在 Note 里如实标注差额(不改可行性判据)。
    /// </summary>
    private static int LegsNeeded(double needLen, double availPerLeg)
    {
        if (!(availPerLeg > 1e-6) || double.IsInfinity(needLen)) return Math.Min(2, MaxLegsPerLevel);
        int n = (int)Math.Ceiling(needLen / availPerLeg - 1e-9);
        return Math.Max(2, Math.Min(MaxLegsPerLevel, n));
    }

    /// <summary>
    /// 从某个起坡点沿走廊往前走,能拿到的展线长(TODO②③④共用的度量)。
    /// </summary>
    private readonly struct CorridorRun
    {
        /// <summary>起点所在**那一段帮**里连续可布的长度 m(不跨转角)。</summary>
        public readonly double SingleWallM;
        /// <summary>允许绕过转角跨帮时累计可布的长度 m(遇不可布 / 满一圈 / 超帮段上限即止)。</summary>
        public readonly double MultiWallM;
        /// <summary>累计到需要的展线长时实际走过的帮段数(1 = 没跨帮);走不到则记累计到的总帮段数。</summary>
        public readonly int WallSpans;

        public CorridorRun(double single, double multi, int spans)
        {
            SingleWallM = single;
            MultiWallM = multi;
            WallSpans = spans;
        }
    }

    /// <summary>
    /// 一级台阶的「可布走廊」(TODO②):把上环按弧长切成小格,逐格判"这儿放不放得下路面",
    /// 可布格连起来才是能算展线的长度 —— 取代首版"整条坡顶线周长当可用长"的乐观口径。
    ///
    /// 逐格判据(每条都是**必要条件**,任一不满足即判不可布,不为了结果好看放宽):
    ///   ① 设定平盘宽 &lt; 路面宽 B → 全环放不下(平盘宽是全环同值的设计参数;≤0 = 未给,不据此判,
    ///      与 <see cref="StraightRampAutoRouter"/> 的 WidthOk 口径一致)。
    ///   ② 帮台进深(该点到**下一级**台阶线的平面净距)&lt; B → 该处放不下。
    ///      净距 = 台阶坡面水平投影 + 平盘宽,是平盘宽的**上界**;上界都不够 → 一定放不下。
    ///      上下两级线若平面重合(退化输入,没给有效偏移几何),此判据无从实测 → 整级跳过并如实标注,
    ///      既不假装量到了、也不拿 0 当真值把整环判死。
    ///   ③ 落在同标高**工作帮**台阶线附近(&lt; B/2 + 安全带)→ 固定坑线不布工作帮。
    ///   ④ 落在 <see cref="OccupiedRoads"/> 既有坑线的最小间距内 → 与已布坑线冲突。
    ///   ⑤ 开口线(<see cref="BenchLine.Closed"/>=false)的虚拟闭合段 → 那里根本没有台阶线。
    ///
    /// 格长取 max(2m, B/2);走廊边界与转角的定位精度即此格长(如实标注,不做亚格插值)。
    /// 度量在 XY 平面上做(与 <see cref="ArcRing"/> 一致);台阶线本就水平,与首版 3D 折线长无实质差别。
    /// </summary>
    private sealed class LevelCorridor
    {
        private readonly bool[] _ok;        // 逐格:放得下路面吗
        private readonly bool[] _corner;    // 逐格:格内含帮段分界(转角)吗
        private readonly int _n;            // 格数(0 = 环退化,未切走廊)
        private readonly double _cell;      // 格长 m = 周长 / 格数
        private readonly bool _closedLine;  // 本级台阶线是否闭合成环

        /// <summary>格长 m(0 = 未切走廊);供调用方错开起坡锚点时步进。</summary>
        public double CellM => _cell;
        /// <summary>可布走廊总长 m。</summary>
        public double DeployableM { get; }
        /// <summary>被判不可布而扣掉的长度 m。</summary>
        public double BlockedM { get; }
        /// <summary>走廊是否连成整圈(螺旋要连续绕环下降,断一处 / 线本身开口就绕不过去)。未切走廊时不下断言,给 true。</summary>
        public bool IsFullLoop => _n == 0 || (_closedLine && BlockedM <= 1e-9);
        /// <summary>走廊口径的一句话交代(扣了多少、按什么扣的),进候选 Note 供人核对。</summary>
        public string Summary { get; }

        private LevelCorridor(bool[] ok, bool[] corner, double cell, bool closedLine,
            double deployable, double blocked, string summary)
        {
            _ok = ok;
            _corner = corner;
            _n = ok.Length;
            _cell = cell;
            _closedLine = closedLine;
            DeployableM = deployable;
            BlockedM = blocked;
            Summary = summary;
        }

        /// <summary>环退化(点不足 / 周长≈0):切不出走廊,调用方退回首版口径。</summary>
        private static LevelCorridor Degenerate()
            => new(Array.Empty<bool>(), Array.Empty<bool>(), 0.0, true, 0.0, 0.0, "走廊未评估(环退化:点不足或周长≈0)");

        public static LevelCorridor Build(
            ArcRing ring, BenchLine up,
            IReadOnlyList<(double X, double Y, double Z)>? lower, bool lowerClosed,
            IReadOnlyList<BenchLine> workingWalls,
            IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>>? occupied,
            double roadWidth, double minSpacing, double safetyStrip,
            double upLevel, double levelTol)
        {
            if (!ring.IsValid) return Degenerate();

            int n = (int)Math.Ceiling(ring.Perimeter / Math.Max(2.0, roadWidth * 0.5));
            n = Math.Max(32, Math.Min(512, n));
            double cell = ring.Perimeter / n;
            var ok = new bool[n];

            // ① 设定平盘宽:全环同值,够不够一次判完。
            bool bermTooNarrow = roadWidth > 1e-6 && up.BermWidth > 1e-6 && up.BermWidth + 1e-6 < roadWidth;
            // ⑤ 开口线的虚拟闭合段:末顶点之后到环首那一段没有台阶线。
            double lastArc = ring.LastVertexArc;

            // ② 帮台进深:先逐格量一遍,中位数≈0 说明上下两级线平面重合(退化输入)→ 此判据无从实测。
            //    开销 O(格数 × 下级线点数);格数已封顶 512,量级可控(Generate 是一次性命令,不在渲染热路径)。
            var depth = new double[n];
            bool depthUsable = lower != null && lower.Count >= 2 && roadWidth > 1e-6;
            if (depthUsable)
            {
                for (int c = 0; c < n; c++)
                {
                    var q = ring.PointAtArc((c + 0.5) * cell);
                    depth[c] = NearestPlanDist(q.X, q.Y, lower!, lowerClosed);
                }
                depthUsable = Median(depth) > 1e-3;
            }

            double clearance = roadWidth * 0.5 + Math.Max(0.0, safetyStrip);
            double blkBerm = 0, blkDepth = 0, blkWork = 0, blkRoad = 0, blkOpen = 0;
            int okCells = 0;
            for (int c = 0; c < n; c++)
            {
                double a = (c + 0.5) * cell;                    // 取格中点代表该格
                if (bermTooNarrow) { blkBerm += cell; continue; }
                if (!up.Closed && a > lastArc + 1e-9) { blkOpen += cell; continue; }
                if (depthUsable && depth[c] + 1e-6 < roadWidth) { blkDepth += cell; continue; }
                var p = ring.PointAtArc(a);
                if (NearWorkingWall(p, workingWalls, upLevel, levelTol, clearance)) { blkWork += cell; continue; }
                if (NearOccupied(p, occupied, minSpacing)) { blkRoad += cell; continue; }
                ok[c] = true;
                okCells++;
            }

            // 帮段分界落到格上(转角 = 端帮↔侧帮的拐点),供 TODO④ 判"跨了几个帮段"。
            var corner = new bool[n];
            foreach (double a in ring.CornerArcs(WallCornerAngleDeg, up.Closed))
                corner[Math.Min(n - 1, Math.Max(0, (int)(a / cell)))] = true;

            double deployable = okCells * cell;
            double blocked = (n - okCells) * cell;
            string sum = $"走廊{deployable:0}/{ring.Perimeter:0}m";
            string cut = "";
            if (blkBerm > 0.5) cut += $"平盘窄{blkBerm:0}m ";
            if (blkDepth > 0.5) cut += $"帮台进深不足{blkDepth:0}m ";
            if (blkWork > 0.5) cut += $"工作帮{blkWork:0}m ";
            if (blkRoad > 0.5) cut += $"已布坑线{blkRoad:0}m ";
            if (blkOpen > 0.5) cut += $"开口段{blkOpen:0}m ";
            if (cut.Length > 0) sum += "(扣:" + cut.TrimEnd() + ")";
            if (!depthUsable && lower != null && lower.Count >= 2)
                sum += "(下级台阶线与本级平面重合,平盘宽只能按设定值判,未逐点实测)";
            return new LevelCorridor(ok, corner, cell, up.Closed, deployable, blocked, sum);
        }

        /// <summary>
        /// 名义起坡弧位若落在不可布格上,前移到最近一个可布格的格首;本就可布则原位不动
        /// (保口径:全环可布时起坡点仍是均分的名义弧位)。全环不可布时原位返回,由调用方如实标注。
        /// </summary>
        public double SnapForward(double arc)
        {
            if (_n == 0 || DeployableM <= 1e-9) return arc;
            double a = Wrap(arc);
            int c0 = CellOf(a);
            if (_ok[c0]) return a;
            for (int s = 1; s <= _n; s++)
            {
                int c = (c0 + s) % _n;
                if (_ok[c]) return c * _cell;
            }
            return a;
        }

        /// <summary>
        /// 从起坡弧位沿走廊往前走:遇到不可布格即止(路过不去),遇到转角记一次跨帮。
        /// 单帮段口径 = 到第一个转角为止;跨帮口径 = 一直累加,满一圈或超 <see cref="MaxWallSpans"/> 帮段即止。
        /// 起格是半格,故走满一圈时最多少算一格(≈ 路宽/2)—— 偏保守,不虚增可用长。
        /// </summary>
        public CorridorRun RunFrom(double startArc, double needLen)
        {
            if (_n == 0) return new CorridorRun(0.0, 0.0, 1);
            double a = Wrap(startArc);
            int c0 = CellOf(a);
            if (!_ok[c0]) return new CorridorRun(0.0, 0.0, 1);

            double single = 0.0, multi = 0.0;
            int spans = 1, spansAtNeed = 0;
            bool inFirstWall = true;
            for (int s = 0; s < _n; s++)
            {
                int c = (c0 + s) % _n;
                if (!_ok[c]) break;
                if (s > 0 && _corner[c])
                {
                    inFirstWall = false;
                    spans++;
                    if (spans > MaxWallSpans) { spans = MaxWallSpans; break; }
                }
                double adv = (s == 0) ? ((c + 1) * _cell - a) : _cell;
                if (adv <= 1e-9) continue;
                double lap = _n * _cell;                       // 一圈:再走就是重复占同一段帮
                if (multi + adv > lap) adv = lap - multi;
                multi += adv;
                if (inFirstWall) single += adv;
                if (spansAtNeed == 0 && multi + 1e-6 >= needLen) spansAtNeed = spans;
                if (multi >= lap - 1e-9) break;
            }
            return new CorridorRun(single, multi, spansAtNeed > 0 ? spansAtNeed : spans);
        }

        private double Wrap(double s)
        {
            double lap = _n * _cell;
            if (lap <= 1e-12) return 0.0;
            s %= lap;
            return s < 0 ? s + lap : s;
        }

        private int CellOf(double a) => Math.Min(_n - 1, Math.Max(0, (int)(a / _cell)));

        /// <summary>该点是否贴着同标高的工作帮线(固定坑线不布工作帮)。标高相差超 <paramref name="levelTol"/> 的不算同一级。</summary>
        private static bool NearWorkingWall((double X, double Y, double Z) p, IReadOnlyList<BenchLine> ww,
            double level, double levelTol, double clearance)
        {
            if (ww == null || ww.Count == 0 || clearance <= 1e-9) return false;
            foreach (var w in ww)
            {
                if (w == null || Math.Abs(w.Level - level) > levelTol) continue;
                var l = (w.Crest != null && w.Crest.Count > 0) ? w.Crest : w.Toe;
                if (l == null || l.Count < 2) continue;
                if (NearestPlanDist(p.X, p.Y, l, w.Closed) + 1e-6 < clearance) return true;
            }
            return false;
        }

        /// <summary>该点是否落进既有坑线的最小间距内(路面会叠加)。</summary>
        private static bool NearOccupied((double X, double Y, double Z) p,
            IReadOnlyList<IReadOnlyList<(double X, double Y, double Z)>>? roads, double minSpacing)
        {
            if (roads == null || roads.Count == 0 || minSpacing <= 1e-9) return false;
            foreach (var r in roads)
            {
                if (r == null || r.Count == 0) continue;
                if (NearestPlanDist(p.X, p.Y, r, closed: false) + 1e-6 < minSpacing) return true;
            }
            return false;
        }

        /// <summary>点到折线的平面最近距(closed=true 时含首末闭合段)。</summary>
        private static double NearestPlanDist(double px, double py,
            IReadOnlyList<(double X, double Y, double Z)> pts, bool closed)
        {
            int n = pts.Count;
            if (n == 0) return double.MaxValue;
            if (n == 1) return Math.Sqrt((px - pts[0].X) * (px - pts[0].X) + (py - pts[0].Y) * (py - pts[0].Y));
            double best = double.MaxValue;
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                double t = len2 > 1e-18 ? ((px - a.X) * dx + (py - a.Y) * dy) / len2 : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double qx = a.X + t * dx, qy = a.Y + t * dy;
                double d2 = (px - qx) * (px - qx) + (py - qy) * (py - qy);
                if (d2 < best) best = d2;
            }
            return Math.Sqrt(best);
        }

        private static double Median(double[] v)
        {
            if (v.Length == 0) return 0.0;
            var t = (double[])v.Clone();
            Array.Sort(t);
            return t[t.Length / 2];
        }
    }

    /// <summary>
    /// 沿帮真中心线(TODO③):XY 自起坡弧位沿上环推进 runLen,Z 按已走弧长线性由 zTop 降到 zBot。
    ///   · legs = 1:一条直腿沿帮走(斜坡道);runLen &gt; 周长时自然绕环多圈 = 螺旋连续下降。
    ///   · legs ≥ 2:把 runLen 折成等长直腿,奇数腿反向(腿间回头),并逐腿朝坑内(形心)让开
    ///     inwardStep,免多腿在同一条线上叠压。
    /// 环退化(点不足/周长≈0)时只给起坡一点,不编造几何。
    /// </summary>
    private static List<(double X, double Y, double Z)> BuildCenterline(
        ArcRing ring, double startArc, double runLen, int legs,
        double zTop, double zBot, double inwardStep)
    {
        var pts = new List<(double X, double Y, double Z)>();
        var anchor = ring.PointAtArc(startArc);
        if (!ring.IsValid || double.IsInfinity(runLen) || double.IsNaN(runLen) || runLen <= 1e-6)
        {
            pts.Add((anchor.X, anchor.Y, zTop));
            return pts;
        }

        int nLeg = Math.Max(1, legs);
        double legRun = runLen / nLeg;                 // 每腿展线长
        int steps = CenterlineStepsPerLeg * nLeg;      // 采样段数(腿界正好落在采样点上)
        for (int s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;              // 已走展线比例
            double u = t * runLen;                     // 已走展线长
            int leg = Math.Min(nLeg - 1, (int)(u / legRun));
            double r = u - leg * legRun;               // 本腿内已走
            double frac = legRun > 1e-9 ? r / legRun : 0.0;
            // 偶数腿正向、奇数腿反向 —— 腿端即回头点。
            double arc = startArc + (((leg & 1) == 0) ? r : legRun - r);
            var p = ring.PointAtArc(arc);
            // 逐腿朝坑内累进让开(legs=1 时 inwardStep=0,即纯沿环)。
            double off = (leg + frac) * inwardStep;
            double nx = ring.Cx - p.X, ny = ring.Cy - p.Y;
            double nl = Math.Sqrt(nx * nx + ny * ny);
            if (off > 1e-9 && nl > 1e-9) { p.X += nx / nl * off; p.Y += ny / nl * off; }
            pts.Add((p.X, p.Y, zTop + (zBot - zTop) * t));
        }
        return pts;
    }

    private static double PolylineLength(IReadOnlyList<(double X, double Y, double Z)> pts)
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

    private static (double X, double Y, double Z) FirstPoint(IReadOnlyList<(double X, double Y, double Z)> pts)
        => (pts != null && pts.Count > 0) ? pts[0] : (0.0, 0.0, 0.0);

    /// <summary>
    /// 闭合环(隐式闭合首尾)+ 累计弧长,供"按弧长取点"(起坡点均分 / 中心线推进 / 走廊切格)。
    /// XY 平面度量,与 <see cref="StraightRampAutoRouter"/> 的环口径一致;此处只需取点,故从简。
    /// 注意:可用长度判据已改走本环上的**可布走廊**(见 <see cref="LevelCorridor"/>),不再是开口折线 3D 长;
    /// <see cref="RampRouteGenerator.PolylineLength"/> 只留作环退化(切不出走廊)时的兜底口径。
    /// 线本身是否真闭合由 <see cref="BenchLine.Closed"/> 说了算 —— 开口线的虚拟闭合段会被走廊判为不可布。
    /// </summary>
    private sealed class ArcRing
    {
        private readonly IReadOnlyList<(double X, double Y, double Z)> _pts;
        private readonly double[] _cum;     // _cum[i] = 前 i 段累计弧长;_cum[n] = 周长

        public readonly double Perimeter;
        public readonly double Cx, Cy;      // 形心(顶点均值),供折返腿朝坑内让开取内法向
        public readonly double InRadius;    // 近似内径 = 周长 / 2π,供让开步距预算

        public ArcRing(IReadOnlyList<(double X, double Y, double Z)>? pts)
        {
            _pts = pts ?? new List<(double, double, double)>();
            int n = _pts.Count;
            _cum = new double[n + 1];
            double xs = 0, ys = 0;
            for (int i = 0; i < n; i++)
            {
                var a = _pts[i];
                var b = _pts[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                _cum[i + 1] = _cum[i] + Math.Sqrt(dx * dx + dy * dy);
                xs += a.X; ys += a.Y;
            }
            Perimeter = _cum[n];
            Cx = n > 0 ? xs / n : 0.0;
            Cy = n > 0 ? ys / n : 0.0;
            InRadius = Perimeter / (2.0 * Math.PI);
        }

        /// <summary>能按弧长取点(点数够 + 周长非零);否则调用方退回单候选 / 单点中心线。</summary>
        public bool IsValid => _pts.Count >= 2 && Perimeter > 1e-6;

        /// <summary>
        /// 末顶点的累计弧长 = 虚拟闭合段的起点。开口线上 [LastVertexArc, Perimeter] 那一段没有台阶线,
        /// 不能当可布走廊用(见 <see cref="LevelCorridor"/> 判据⑤)。
        /// </summary>
        public double LastVertexArc => _pts.Count >= 2 ? _cum[_pts.Count - 1] : 0.0;

        /// <summary>
        /// 帮段分界(转角)的弧位:顶点处转向超过 <paramref name="thresholdDeg"/> 即认为换了一段帮
        /// (端帮↔侧帮的拐点)。开口线额外把首末点计为分界(线到头了)。供 TODO④ 判候选跨了几个帮段。
        /// </summary>
        public List<double> CornerArcs(double thresholdDeg, bool closed)
        {
            var res = new List<double>();
            int n = _pts.Count;
            if (n < 2) return res;
            double cosLim = Math.Cos(thresholdDeg * Math.PI / 180.0);
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) { res.Add(_cum[i]); continue; }
                var a = _pts[(i - 1 + n) % n];
                var b = _pts[i];
                var c = _pts[(i + 1) % n];
                double ax = b.X - a.X, ay = b.Y - a.Y;
                double bx = c.X - b.X, by = c.Y - b.Y;
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                if (la < 1e-9 || lb < 1e-9) continue;
                if ((ax * bx + ay * by) / (la * lb) < cosLim) res.Add(_cum[i]);
            }
            return res;
        }

        public (double X, double Y, double Z) PointAtArc(double s)
        {
            int n = _pts.Count;
            if (n == 0) return (0.0, 0.0, 0.0);
            if (Perimeter < 1e-9) return _pts[0];
            s %= Perimeter;
            if (s < 0) s += Perimeter;

            int seg = n - 1;
            for (int i = 0; i < n; i++)
                if (s <= _cum[i + 1] + 1e-9) { seg = i; break; }

            double segLen = _cum[seg + 1] - _cum[seg];
            double t = segLen > 1e-12 ? (s - _cum[seg]) / segLen : 0.0;
            var a = _pts[seg];
            var b = _pts[(seg + 1) % n];
            return (a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z));
        }
    }
}
