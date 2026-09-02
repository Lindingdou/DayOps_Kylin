using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>坑线展线形式(斜坡道 / 折返 / 螺旋)。忠实原 MineAssLib.RoadLayout.RampForm(三值)。</summary>
public enum RampForm
{
    /// <summary>斜坡道:一条直腿沿帮匀坡落到下一级平盘(可用帮长 ≥ ΔH/i)。</summary>
    Straight,
    /// <summary>折返(回头):帮段不够或须反向 → 回头曲线(半径≥R_min)+平台。</summary>
    Switchback,
    /// <summary>螺旋:平面受限/近圆形坑, 连续转弯免回头平台(RampRouteGenerator 选型兜底)。</summary>
    Spiral,
}

/// <summary>
/// 自动布线的台阶输入(坡顶线 crest + 坡底线 toe + 平盘宽 + 标高)。
/// 忠实镜像原 MineAssLib.RoadLayout.BenchLine 的 Route 所需字段;命名 RampBenchLine 避与
/// <see cref="MineableAreaIdentifier"/> 内嵌 BenchLine(可采域识别用单折线)混淆。
/// </summary>
public sealed class RampBenchLine
{
    /// <summary>水平标高。</summary>
    public double Level { get; set; }
    /// <summary>坡顶线(crest);≥3 点方成环。</summary>
    public IReadOnlyList<(double X, double Y, double Z)> Crest { get; set; } = new List<(double, double, double)>();
    /// <summary>坡底线(toe);≥3 点方成环。</summary>
    public IReadOnlyList<(double X, double Y, double Z)> Toe { get; set; } = new List<(double, double, double)>();
    /// <summary>平盘宽(判回头/路宽放得下)。</summary>
    public double BermWidth { get; set; }
    /// <summary>工作帮?固定坑线只布非工作帮(RampRouteGenerator 用;StraightRampAutoRouter.Route 不读)。</summary>
    public bool IsWorkingWall { get; set; }
}

/// <summary>直线坑线自动布线的输入选项(只关限坡 + 连通,先不考虑运量)。</summary>
public sealed class StraightRampRouteOptions
{
    /// <summary>限制坡度 i_max %(= TransportConstraintSettings.MaxGradePct)。</summary>
    public double GradePct { get; set; } = 8.0;
    /// <summary>路面宽度 B m(= RoadWidthPreview);仅用于"平盘是否放得下路宽"的告警,不影响连通判定。</summary>
    public double RoadWidth { get; set; } = 0.0;
    /// <summary>起坡口平面种子(落在【起始环】上最近点);为空则取起始环上 X 最大的顶点(确定性默认)。</summary>
    public (double X, double Y)? StartSeedXY { get; set; }
    /// <summary>true=从坑底起(种子落坑底环,自下而上布到地表);false=从地表起(默认)。</summary>
    public bool StartFromBottom { get; set; } = false;
    /// <summary>盘旋旋向:≥0 = 沿顶点序前进, &lt;0 = 逆顶点序。仅影响走向,不影响逐级可行性。</summary>
    public int RotationDir { get; set; } = +1;
    /// <summary>环按标高去重的容差 m(crest/toe 同标高视为一环)。</summary>
    public double ZTolerance { get; set; } = 0.5;

    // ── 缓坡段(长大下坡每累计下降 H_seg 须插一段近平缓坡,供制动/散热) ──
    /// <summary>缓坡段触发:最大连续下降高差 H_seg m。≤0 = 不设缓坡段(退化为原匀坡)。</summary>
    public double MaxContinuousDropM { get; set; } = 0.0;
    /// <summary>缓坡段纵坡 i_ease %(≤ i_max)。</summary>
    public double EaseGradePct { get; set; } = 3.0;
    /// <summary>缓坡段最小长度 L_ease m。</summary>
    public double EaseMinLengthM { get; set; } = 50.0;

    // ── 折返(回头)兜底:直线放不下(周长 < L)时,改约束化回头继续下降到坑底 ──
    /// <summary>直线放不下处是否启用约束化折返兜底(false = 报告并止步)。</summary>
    public bool AllowSwitchbackFallback { get; set; } = false;
    /// <summary>回头最小半径 R m(= 卡车最小转弯半径 TruckTurnRadius);折半径下限。≤0 = 不启用折返。</summary>
    public double MinTurnRadius { get; set; } = 0.0;
    /// <summary>回头弧减坡 i_curve %(直线腿仍按 i_max,回头弧减坡限速)。</summary>
    public double CurveGradePct { get; set; } = 4.0;

    /// <summary>坑线最小间距 m(相邻段起坡点平面距须 ≥ 此值,防路面叠加;缺省=路宽)。≤0 = 不校间距。</summary>
    public double MinSpacingM { get; set; } = 0.0;

    // ── 阶段①线形约束(GBJ22-87;对中线做内移偏置 + 转角圆弧化,见 CenterlineLineForm) ──
    /// <summary>中线内移偏置 m:朝形心移,让路落到平盘内(中线≠台阶边缘)。一般取 路宽/2+安全带。0=不偏置。</summary>
    public double CenterlineOffsetM { get; set; } = 0.0;
    /// <summary>最小平曲线半径 m:转角圆弧化下限(= EffectiveMinCurveRadiusM)。0=不圆弧化。</summary>
    public double MinCurveRadiusM { get; set; } = 0.0;
    /// <summary>是否对中线做线形后处理(内移+圆弧化)。默认开;偏置=0 且半径=0 时自然 no-op。</summary>
    public bool ApplyLineForm { get; set; } = true;

    // ── 阶段②纵断面(弯道折减 + 合成坡度);0 = 不做(退化为阶段①匀坡) ──
    /// <summary>弯道(圆曲线段)最大纵坡 %(= CurveMaxGradePct,急弯折减)。</summary>
    public double CurveMaxGradePct { get; set; } = 0.0;
    /// <summary>最大合成坡度 %(纵⊕超高横坡,= MaxResultantGradePct)。</summary>
    public double MaxResultantGradePct { get; set; } = 0.0;
    /// <summary>弯道超高横坡 %(= MaxSuperelevationPct),进合成坡度。</summary>
    public double SuperElevationPct { get; set; } = 0.0;

    // ── 阶段③竖曲线 + 最小坡长;0 = 不做 ──
    /// <summary>设竖曲线的变坡阈值 %(相邻纵坡差 > 此值插竖曲线,= VerticalCurveTriggerDiffPct)。</summary>
    public double VerticalCurveTriggerPct { get; set; } = 0.0;
    /// <summary>最小竖曲线半径 m(= MinVerticalCurveRadiusM)。</summary>
    public double VerticalCurveRadiusM { get; set; } = 0.0;
    /// <summary>最小坡长 m(直线段连续坡长下限,= MinGradeSectionLengthM)。</summary>
    public double MinGradeSectionLengthM { get; set; } = 0.0;

    // ── 阶段④横断面(弯道加宽 + 超高);CurveWidenThresholdM=0 则不算 ──
    /// <summary>弯道加宽阈值 m(R ≤ 此值加宽,= CurveWidenThresholdM)。</summary>
    public double CurveWidenThresholdM { get; set; } = 0.0;
    /// <summary>车辆轴距 m(加宽公式 L)。</summary>
    public double VehicleWheelbaseM { get; set; } = 6.0;
    /// <summary>设计车速 km/h(超高公式 V)。</summary>
    public double DesignSpeedKmh { get; set; } = 25.0;
    /// <summary>车道数(加宽公式)。</summary>
    public int LaneCount { get; set; } = 2;
}

/// <summary>逐级判据明细(一相邻环对一行)。</summary>
public sealed class LevelReport
{
    public int Index { get; set; }
    public double FromZ { get; set; }
    public double ToZ { get; set; }
    public double DeltaH { get; set; }
    public double RequiredRunM { get; set; }     // L = ΔH/i + 缓坡段附加展线
    public double LowerPerimeterM { get; set; }  // 下级环周长
    public double BermWidthM { get; set; }
    public int EaseSections { get; set; }        // 本级内须插入的缓坡段数(按最大连续高差触发)
    public RampForm Form { get; set; } = RampForm.Straight;  // 本级展线形式:斜坡道 / 折返
    public double TurnRadius { get; set; }       // 折返回头半径 R(Straight=0)
    public int Legs { get; set; }                // 折返直线腿数(Straight=0)
    public bool PlatformFitsOnBerm { get; set; } // 折返:回头平台是否放得下平盘(否=须外凸折返台)
    public bool Feasible { get; set; }           // 可行(直线:周长≥L;折返:回头放得下)
    public bool WidthOk { get; set; }            // 平盘宽 ≥ 路宽(信息性)
    public string Note { get; set; } = "";
}

/// <summary>布线结果:中线(地表→最后可行台阶)+ 逐级报告 + 贯通情况。</summary>
public sealed class StraightRampRouteResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<(double X, double Y, double Z)> Centerline { get; set; }
        = new List<(double, double, double)>();
    public IReadOnlyList<LevelReport> Levels { get; set; } = new List<LevelReport>();
    public int LevelsTotal { get; set; }       // 相邻环对总数(= 环数 - 1)
    public int LevelsConnected { get; set; }   // 实际铺下的直腿数
    public double TopZ { get; set; }
    public double BottomZ { get; set; }        // 最深环标高(目标坑底)
    public double ReachedZ { get; set; }       // 实际到达的最低标高
    public bool ReachedBottom { get; set; }
    public int RotationDirUsed { get; set; }
    public double TotalRunM { get; set; }      // 直腿平面投影长合计
    public int TotalEaseSections { get; set; } // 全程插入的缓坡段总数
    public int StraightLegs { get; set; }      // 斜坡道(直腿)段数
    public int SwitchbackLegs { get; set; }    // 折返(回头)段数
    /// <summary>逐面切落地用 O 点(仅斜坡道段的起坡点,落在坡顶线上;折返段几何另出,不在此)。</summary>
    public IReadOnlyList<(double X, double Y, double Z)> FaceCutOPoints { get; set; }
        = new List<(double, double, double)>();

    // ── 阶段①线形后处理结果 ──
    /// <summary>实际达到的最小平曲线半径 m(0=未做圆弧化)。</summary>
    public double MinCurveRadiusAchievedM { get; set; }
    /// <summary>半径不达标(段长放不下 R_min)的转角数。</summary>
    public int CurveRadiusViolations { get; set; }
    /// <summary>实际应用的中线内移偏置 m。</summary>
    public double CenterlineOffsetAppliedM { get; set; }

    // ── 阶段②纵断面结果 ──
    /// <summary>直线段实际最大纵坡 %。</summary>
    public double MaxGradeUsedPct { get; set; }
    /// <summary>圆曲线段实际最大纵坡 %(应 ≤ 弯道折减限)。</summary>
    public double CurveGradeUsedPct { get; set; }
    /// <summary>弯道最大合成坡度 %。</summary>
    public double ResultantGradePct { get; set; }
    /// <summary>限坡内 descend 不满总高差(路太短,须增长展线)。</summary>
    public bool GradeExceedsLimit { get; set; }

    // ── 阶段③竖曲线 + 最小坡长结果 ──
    /// <summary>插入的竖曲线条数。</summary>
    public int VerticalCurveCount { get; set; }
    /// <summary>实际最小竖曲线半径 m。</summary>
    public double MinVerticalRadiusAchievedM { get; set; }
    /// <summary>竖曲线半径不达标数。</summary>
    public int VerticalCurveViolations { get; set; }
    /// <summary>直线段坡长 &lt; 最小坡长 的段数。</summary>
    public int MinGradeSectionViolations { get; set; }

    // ── 阶段④横断面结果(与 Centerline 对齐) ──
    /// <summary>每站路面宽 m(基宽 + 弯道加宽);与 Centerline 等长。</summary>
    public IReadOnlyList<double> RoadWidths { get; set; } = new List<double>();
    /// <summary>每站超高横坡 %;与 Centerline 等长。</summary>
    public IReadOnlyList<double> Superelevations { get; set; } = new List<double>();
    public double MaxWideningM { get; set; }
    public double MaxSuperelevationUsedPct { get; set; }
    public double WidenedLengthM { get; set; }
}

/// <summary>
/// 直线坡道连通性自动布线(先不考虑运量,只满足限坡 i_max + 连通)。忠实移植原
/// <c>MineAssLib.RoadLayout.StraightRampAutoRouter</c>(纯几何、不依赖引擎、可单测)。
///
/// 思想:开拓坑线 = 沿帮匀坡盘旋下降。若只许直线段,则必然是「一段帮一条直腿,落到下一级平盘,
/// 再接下一条」的折线螺旋。每降一个台阶高 ΔH,直腿水平投影须 L = ΔH / i。逐级把直腿首尾相接 →
/// 一条从地表到坑底的连续中线:首尾相接保证连通,平面投影长 ≥ L 保证 i ≤ i_max(略缓,安全)。
///
/// 可行性判据(逐级):该级环周长 P ≥ L = ΔH / i。越往坑底环越小,某级 P &lt; L 即纯直线放不下;
/// 本版策略 = 报告并止于最后可行台阶(report-and-stop),不混回头/螺旋。
///
/// 阶段①(线形:内移+圆弧化)与阶段④(横断面:加宽+超高)接 Kylin 已移植的
/// <see cref="CenterlineLineForm"/> / <see cref="RoadCrossSection"/>;由 opt 标志门控,默认 no-op。
/// </summary>
public static class StraightRampAutoRouter
{
    public static StraightRampRouteResult Route(
        IReadOnlyList<RampBenchLine> benches, StraightRampRouteOptions opt)
    {
        var res = new StraightRampRouteResult();
        var levels = new List<LevelReport>();
        res.Levels = levels;
        opt ??= new StraightRampRouteOptions();

        if (benches == null || benches.Count < 2) { res.Error = "台阶不足(需 ≥ 2 级)"; return res; }
        double i = opt.GradePct;
        if (i <= 1e-6) { res.Error = "限坡 i_max 无效(≤ 0)"; return res; }

        var rings = BuildRings(benches, opt.ZTolerance);
        if (rings.Count < 2) { res.Error = "有效台阶环不足(需 ≥ 2,检查 toe/crest 点数)"; return res; }

        // BuildRings 给的是高→低。从坑底起 → 反转成 低→高(rings[0]=最深环),自下而上布到地表。
        if (opt.StartFromBottom) rings.Reverse();

        res.LevelsTotal = rings.Count - 1;
        res.TopZ = Math.Max(rings[0].AvgZ, rings[rings.Count - 1].AvgZ);     // 地表(最高环)
        res.BottomZ = Math.Min(rings[0].AvgZ, rings[rings.Count - 1].AvgZ);  // 坑底(最深环)
        res.RotationDirUsed = opt.RotationDir >= 0 ? +1 : -1;

        // 起坡口:种子最近点 / 默认最高环 X 最大顶点
        var top = rings[0];
        (double X, double Y, double Z) anchor;
        if (opt.StartSeedXY.HasValue)
        {
            double s = top.NearestArc(opt.StartSeedXY.Value.X, opt.StartSeedXY.Value.Y, out _, out _);
            anchor = top.PointAtArc(s);
        }
        else
        {
            anchor = PickDefaultPortal(top);
        }

        var center = new List<(double X, double Y, double Z)> { anchor };
        var centroids = new List<(double Cx, double Cy)> { (top.Cx, top.Cy) };  // 与 center 对齐,供线形内移取内法向
        var faceOPts = new List<(double X, double Y, double Z)>();   // 斜坡道段逐面切 O 点(起坡点,坡顶线上)
        var placedO = new List<(double X, double Y, double Z)>();    // 已布段代表点(平面),供按最小间距防叠加
        int dir = res.RotationDirUsed;
        double totalRun = 0;
        int connected = 0;
        int straightLegs = 0, switchbackLegs = 0;
        double minSpacing = Math.Max(0.0, opt.MinSpacingM);

        // 折返兜底参数
        bool allowSb = opt.AllowSwitchbackFallback && opt.MinTurnRadius > 1e-6;
        double R = opt.MinTurnRadius;
        double sbFootprint = 2.0 * R + Math.Max(0.0, opt.RoadWidth);   // 一次回头折叠的沿帮最小占位

        // 缓坡段状态:g=陡坡坡度, ge=缓坡坡度, hSeg=最大连续高差触发, lEase=缓坡最小长。
        double g = i / 100.0;
        double ge = Math.Max(0.0, Math.Min(opt.EaseGradePct, i)) / 100.0;
        double hSeg = opt.MaxContinuousDropM;
        double lEase = Math.Max(0.0, opt.EaseMinLengthM);
        double cumDrop = 0;   // 距上一缓坡段的累计(陡坡)下降
        int totalEase = 0;

        for (int k = 0; k + 1 < rings.Count; k++)
        {
            var up = rings[k];                    // 当前环(锚点所在)
            var dn = rings[k + 1];                // 目标环(本段落到的环;沿帮走 L 在它上面)
            double absdH = Math.Abs(up.AvgZ - dn.AvgZ);   // 段高差(自上而下/自下而上都取正)

            var rep = new LevelReport { Index = k, FromZ = up.AvgZ, ToZ = dn.AvgZ, DeltaH = absdH };
            if (absdH <= 1e-6)
            {
                rep.Feasible = false;
                rep.Note = "相邻环同标高(ΔH ≈ 0),止步";
                levels.Add(rep);
                break;
            }

            // 缓坡段:累计高差到 hSeg 即须插一段缓坡。每段附加展线 = lEase·(1 − ge/g)
            //   (缓坡自身以 ge 行 lEase·ge,替代了等量陡坡高差,余下仍按 g)。
            int easeThis = 0;
            if (hSeg > 1e-6 && lEase > 1e-6)
            {
                cumDrop += absdH;
                while (cumDrop >= hSeg - 1e-6) { easeThis++; cumDrop -= hSeg; }
                if (ge > 1e-9 && easeThis * lEase * ge > absdH)     // 缓坡自身别把本级高差吃穿
                    easeThis = (int)Math.Floor(absdH / (lEase * ge));
            }
            double L = absdH / g + easeThis * lEase * (1.0 - (g > 1e-9 ? ge / g : 0.0));

            rep.RequiredRunM = L;
            rep.LowerPerimeterM = dn.Perimeter;
            rep.BermWidthM = dn.BermWidth;
            rep.EaseSections = easeThis;
            rep.WidthOk = opt.RoadWidth <= 1e-6 || dn.BermWidth <= 1e-6
                          || dn.BermWidth + 1e-6 >= opt.RoadWidth;

            // 直腿候选:落点 + 该面坡顶线上的起坡 O 点;再按最小间距判是否与已布坑线叠加。
            double sFootS = dn.NearestArc(anchor.X, anchor.Y, out _, out _);
            var sLand = dn.PointAtArc(sFootS + dir * L);
            var sO = (up.AvgZ >= dn.AvgZ) ? anchor : sLand;
            bool spaceOk = minSpacing <= 1e-6 || MinPlanDist(placedO, sO) + 1e-6 >= minSpacing;
            bool straightFits = (dn.Perimeter + 1e-6 >= L) && spaceOk;

            if (straightFits)
            {
                // ── 斜坡道:周长够 且 不与已布坑线叠加 → 直腿 ──
                faceOPts.Add(sO);
                placedO.Add(sO);

                rep.Form = RampForm.Straight;
                rep.Feasible = true;
                rep.Note = $"斜坡道 L={L:0}m → 落 Z={dn.AvgZ:0.#}m"
                           + (easeThis > 0 ? $"(含缓坡×{easeThis})" : "")
                           + (rep.WidthOk ? "" : $"(注:平盘 {dn.BermWidth:0}m < 路宽 {opt.RoadWidth:0.#}m)");
                levels.Add(rep);

                totalRun += Math.Sqrt(Dist2(anchor.X, anchor.Y, sLand.X, sLand.Y));
                totalEase += easeThis;
                center.Add(sLand);
                centroids.Add((dn.Cx, dn.Cy));
                anchor = sLand;
                connected++;
                straightLegs++;
            }
            else if (allowSb && sbFootprint + 1e-6 <= dn.Perimeter)
            {
                // ── 折返兜底:直线放不下(没空间继续直进)→ 约束化回头,把 L 折成 N 条【直腿】下降 ΔH ──
                //   关键:每条腿是直线、且在【不同位置】(逐折朝坑内步进 = 外凸折返台),腿间用回头连接;
                //   绝不在同一条线上原地反折(那会"方向相悖"、不连续不可行驶)。半径 R=最小转弯半径下限。
                //   沿帮折叠跨度 s;腿数 N=⌈L/s⌉;朝坑内步进总量受内径预算约束,逐腿错开。
                double footS = dn.NearestArc(anchor.X, anchor.Y, out _, out _);
                double s = Math.Min(dn.Perimeter * 0.45, L);           // 每腿沿帮跨度
                int legs = Math.Max(2, (int)Math.Ceiling(L / s));
                double budget = Math.Max(sbFootprint, dn.InRadius * 0.7); // 朝坑内可步进的总深度
                double step = budget / legs;                            // 逐折步进(让相邻腿错开、可连续行驶)

                // 折叠中线:腿端在 footS / footS+dir·s 两弧位交替,逐折朝坑内步进 step、降 ΔH/legs。
                //   连续点之间是一条直腿(点到点直线);相邻腿因步进而错开,经端点回头连接,方向不相悖。
                (double X, double Y, double Z) last = anchor;
                for (int j = 1; j <= legs; j++)
                {
                    double arc = footS + dir * (((j & 1) == 1) ? s : 0.0);
                    var b = dn.PointAtArc(arc);
                    double inx = dn.Cx - b.X, iny = dn.Cy - b.Y;       // 朝坑内(形心)方向
                    double il = Math.Sqrt(inx * inx + iny * iny);
                    if (il > 1e-9) { inx /= il; iny /= il; }
                    double off = j * step;                              // 逐折累进朝坑内
                    double z = up.AvgZ - (up.AvgZ - dn.AvgZ) * ((double)j / legs);
                    last = (b.X + inx * off, b.Y + iny * off, z);
                    center.Add(last);
                    centroids.Add((dn.Cx, dn.Cy));
                }
                last = (last.X, last.Y, dn.AvgZ);   // 末腿落到下环标高
                placedO.Add(last);

                string sbWhy = (dn.Perimeter + 1e-6 < L) ? $"周长{dn.Perimeter:0}<需{L:0}m" : "直线与已布坑线叠加";
                rep.Form = RampForm.Switchback;
                rep.TurnRadius = R;
                rep.Legs = legs;
                rep.PlatformFitsOnBerm = dn.BermWidth + 1e-6 >= sbFootprint;
                rep.Feasible = true;
                rep.Note = $"折返 R={R:0.#}m {legs}直腿(没空间直进:{sbWhy})"
                           + (rep.PlatformFitsOnBerm ? ",回头在平盘内" : ",平盘窄→外凸折返台")
                           + (easeThis > 0 ? $",含缓坡×{easeThis}" : "");
                levels.Add(rep);

                totalEase += easeThis;
                anchor = last;
                connected++;
                switchbackLegs++;
            }
            else
            {
                // 直线放不下,且折返也放不下(环太小,回头都折不进)→ 止步(更深需螺旋/并段)
                rep.Feasible = false;
                rep.Note = allowSb
                    ? $"周长 {dn.Perimeter:0}m 连回头({sbFootprint:0}m)都放不下 → 止步(需螺旋/并段)"
                    : $"周长 {dn.Perimeter:0}m < 需 {L:0}m,直线放不下,止步(未启用折返兜底)";
                levels.Add(rep);
                break;
            }
        }

        // 阶段①线形后处理:中线内移偏置 + 转角圆弧化(满足 R_min)。仅作用于预览/行驶中线,
        // FaceCutOPoints(切面 O 点,落坡顶线)不动 —— 切面与偏置的对齐留阶段④。
        res.Centerline = center;
        if (opt.ApplyLineForm && (opt.CenterlineOffsetM > 1e-9 || opt.MinCurveRadiusM > 1e-9) && center.Count >= 2)
        {
            var lf = CenterlineLineForm.Apply(center, centroids, opt.CenterlineOffsetM, opt.MinCurveRadiusM,
                maxGradePct: opt.GradePct, curveMaxGradePct: opt.CurveMaxGradePct,
                maxResultantPct: opt.MaxResultantGradePct, superElevPct: opt.SuperElevationPct,
                vcTriggerPct: opt.VerticalCurveTriggerPct, vcRadiusM: opt.VerticalCurveRadiusM,
                minSecLenM: opt.MinGradeSectionLengthM);
            res.Centerline = lf.Line;
            res.MinCurveRadiusAchievedM = lf.MinRadiusM;
            res.CurveRadiusViolations = lf.Violations;
            res.CenterlineOffsetAppliedM = opt.CenterlineOffsetM;
            res.MaxGradeUsedPct = lf.MaxGradeUsedPct;
            res.CurveGradeUsedPct = lf.CurveGradeUsedPct;
            res.ResultantGradePct = lf.ResultantGradePct;
            res.GradeExceedsLimit = lf.GradeExceedsLimit;
            res.VerticalCurveCount = lf.VerticalCurveCount;
            res.MinVerticalRadiusAchievedM = lf.MinVerticalRadiusAchievedM;
            res.VerticalCurveViolations = lf.VerticalCurveViolations;
            res.MinGradeSectionViolations = lf.MinGradeSectionViolations;
        }

        // 阶段④横断面:按中线曲率算弯道加宽 + 超高(数据;落地切面在 C++ 侧消费)
        if (opt.CurveWidenThresholdM > 1e-9 && opt.RoadWidth > 1e-9 && res.Centerline.Count >= 2)
        {
            var cs = RoadCrossSection.ComputeAlong(res.Centerline, opt.RoadWidth, opt.CurveWidenThresholdM,
                opt.LaneCount, opt.VehicleWheelbaseM, opt.DesignSpeedKmh, opt.SuperElevationPct);
            res.RoadWidths = cs.WidthM;
            res.Superelevations = cs.SuperelevationPct;
            res.MaxWideningM = cs.MaxWideningM;
            res.MaxSuperelevationUsedPct = cs.MaxSuperelevationPct;
            res.WidenedLengthM = cs.WidenedLengthM;
        }

        res.FaceCutOPoints = faceOPts;
        res.LevelsConnected = connected;
        res.StraightLegs = straightLegs;
        res.SwitchbackLegs = switchbackLegs;
        res.ReachedZ = anchor.Z;
        res.ReachedBottom = connected == rings.Count - 1;
        res.TotalRunM = totalRun;
        res.TotalEaseSections = totalEase;
        res.Success = connected >= 1;
        if (!res.Success && res.Error == null) res.Error = "首级即不可行(最外环周长不足或无下降)";
        return res;
    }

    // ── 环抽取:每级用 crest(退化用 toe)+ 最深 toe 兜底为坑底环;按标高去重、从高到低排 ──
    private static List<Ring> BuildRings(IReadOnlyList<RampBenchLine> benches, double zTol)
    {
        var raw = new List<Ring>();
        Ring? lowestToe = null;

        foreach (var b in benches)
        {
            if (b == null) continue;
            var crest = (b.Crest != null && b.Crest.Count >= 3) ? b.Crest : null;
            var toe = (b.Toe != null && b.Toe.Count >= 3) ? b.Toe : null;
            var line = crest ?? toe;
            if (line != null)
            {
                var ring = new Ring(line, b.BermWidth, b.Level);
                if (ring.Perimeter > 1e-6) raw.Add(ring);
            }
            if (toe != null)
            {
                var tring = new Ring(toe, b.BermWidth, b.Level);
                if (tring.Perimeter > 1e-6 && (lowestToe == null || tring.AvgZ < lowestToe.AvgZ))
                    lowestToe = tring;
            }
        }

        raw.Sort((a, c) => c.AvgZ.CompareTo(a.AvgZ)); // 高 → 低

        var outp = new List<Ring>();
        foreach (var r in raw)
            if (outp.Count == 0 || Math.Abs(outp[outp.Count - 1].AvgZ - r.AvgZ) > zTol)
                outp.Add(r);

        // 坑底兜底:最深 toe 比最低 crest 环还低 → 追加为终止环(到真正坑底沿)
        if (lowestToe != null && (outp.Count == 0 ||
            lowestToe.AvgZ < outp[outp.Count - 1].AvgZ - zTol))
            outp.Add(lowestToe);

        return outp;
    }

    private static (double X, double Y, double Z) PickDefaultPortal(Ring r)
    {
        var best = r.Pts[0];
        for (int k = 1; k < r.Pts.Count; k++)
            if (r.Pts[k].X > best.X) best = r.Pts[k];
        return best;
    }

    private static double Dist2(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        return dx * dx + dy * dy;
    }

    /// <summary>q 到已布段代表点集的最小平面距(空集 = +∞,首段总通过间距校核)。</summary>
    private static double MinPlanDist(List<(double X, double Y, double Z)> pts, (double X, double Y, double Z) q)
    {
        double best = double.MaxValue;
        foreach (var p in pts)
        {
            double d2 = Dist2(p.X, p.Y, q.X, q.Y);
            if (d2 < best) best = d2;
        }
        return best >= double.MaxValue ? double.MaxValue : Math.Sqrt(best);
    }

    private static void ProjectOnSeg(double px, double py, double ax, double ay, double bx, double by,
        out double qx, out double qy, out double t)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) { qx = ax; qy = ay; t = 0; return; }
        t = ((px - ax) * dx + (py - ay) * dy) / len2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        qx = ax + t * dx; qy = ay + t * dy;
    }

    /// <summary>闭合环(隐式闭合首尾)+ 预算累计弧长,供最近点 / 按弧长取点。XY 平面度量。</summary>
    private sealed class Ring
    {
        public readonly IReadOnlyList<(double X, double Y, double Z)> Pts;
        public readonly double[] Cum;     // Cum[i] = 前 i 段累计弧长;Cum[n] = 周长
        public readonly double Perimeter;
        public readonly double AvgZ;
        public readonly double BermWidth;
        public readonly double Level;
        public readonly double Cx, Cy;    // 形心(顶点均值),供折返"朝坑内步进"取内法向
        public readonly double InRadius;  // 近似内径 = 周长 / 2π,供折返步进预算

        public Ring(IReadOnlyList<(double X, double Y, double Z)> pts, double bermWidth, double level)
        {
            Pts = pts;
            int n = pts.Count;
            Cum = new double[n + 1];
            double zsum = 0, xsum = 0, ysum = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var a = pts[idx];
                var b = pts[(idx + 1) % n];
                Cum[idx + 1] = Cum[idx] + Math.Sqrt(Dist2(a.X, a.Y, b.X, b.Y));
                zsum += a.Z; xsum += a.X; ysum += a.Y;
            }
            Perimeter = Cum[n];
            AvgZ = n > 0 ? zsum / n : 0.0;
            Cx = n > 0 ? xsum / n : 0.0;
            Cy = n > 0 ? ysum / n : 0.0;
            InRadius = Perimeter / (2.0 * Math.PI);
            BermWidth = bermWidth;
            Level = level;
        }

        public double NearestArc(double px, double py, out double nx, out double ny)
        {
            int n = Pts.Count;
            double best = double.MaxValue, bestS = 0;
            nx = px; ny = py;
            for (int idx = 0; idx < n; idx++)
            {
                var a = Pts[idx];
                var b = Pts[(idx + 1) % n];
                ProjectOnSeg(px, py, a.X, a.Y, b.X, b.Y, out double qx, out double qy, out double t);
                double d = Dist2(px, py, qx, qy);
                if (d < best)
                {
                    best = d; nx = qx; ny = qy;
                    bestS = Cum[idx] + t * (Cum[idx + 1] - Cum[idx]);
                }
            }
            return bestS;
        }

        public (double X, double Y, double Z) PointAtArc(double s)
        {
            int n = Pts.Count;
            if (n == 0) return (0, 0, 0);
            if (Perimeter < 1e-9) return Pts[0];
            s %= Perimeter;
            if (s < 0) s += Perimeter;

            int seg = n - 1;
            for (int idx = 0; idx < n; idx++)
                if (s <= Cum[idx + 1] + 1e-9) { seg = idx; break; }

            double segLen = Cum[seg + 1] - Cum[seg];
            double t = segLen > 1e-12 ? (s - Cum[seg]) / segLen : 0.0;
            var a = Pts[seg];
            var b = Pts[(seg + 1) % n];
            return (a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z));
        }
    }
}
