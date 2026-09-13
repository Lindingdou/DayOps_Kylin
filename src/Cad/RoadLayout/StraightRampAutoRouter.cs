using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.RoadLayout;

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

    // ── 螺旋兜底:直线放不下【且】折返也折不进(环太小)时,在该级环内连续盘旋下降 ──
    /// <summary>折返也放不下处是否启用螺旋兜底(false = 报告并止步)。</summary>
    public bool AllowSpiralFallback { get; set; } = false;
    /// <summary>
    /// 螺旋半径下限 R_sp m。≤0 时退回 <see cref="MinTurnRadius"/>(= 卡车最小转弯半径 TruckTurnRadius)。
    /// 实际取用 max(本值, <see cref="MinCurveRadiusM"/>) —— 设计车速反算出的最小平曲线半径同样是硬下限,
    /// 否则布出来的螺旋自己就违反阶段①线形约束。三者(本值 / MinTurnRadius / MinCurveRadiusM)全 ≤0
    /// 时半径为 0 = 不启用螺旋。
    /// </summary>
    public double SpiralMinRadius { get; set; } = 0.0;
    /// <summary>螺旋中线每圈采样点数(下限 12)。点太少圈画成多边形,太多预览实体暴涨。</summary>
    public int SpiralPointsPerTurn { get; set; } = 36;

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
    public RampForm Form { get; set; } = RampForm.Straight;  // 本级展线形式:斜坡道 / 折返 / 螺旋
    public double TurnRadius { get; set; }       // 折返回头半径 R(Straight/Spiral=0)
    public int Legs { get; set; }                // 折返直线腿数(Straight/Spiral=0)
    public bool PlatformFitsOnBerm { get; set; } // 折返:回头平台是否放得下平盘(否=须外凸折返台)
    public bool Feasible { get; set; }           // 可行(直线:周长≥L;折返:回头放得下;螺旋:圈盘得进)
    public bool WidthOk { get; set; }            // 平盘宽 ≥ 路宽(信息性)
    public string Note { get; set; } = "";

    // ── 螺旋兜底几何(Form=Spiral 时填;止步行也填【算出来但放不下】的那组,供报告给数)──
    //   这五个量 + FromZ(起始标高) + 旋向(见 StraightRampRouteResult.RotationDirUsed) + 路宽/纵坡,
    //   恰好就是 IPitDesignCapability.InsertRampSpiral 的整套入参 —— 将来接螺旋落地不必重算几何。
    public double SpiralRadiusM { get; set; }      // 螺旋半径 R(非螺旋=0)
    public double SpiralTurns { get; set; }        // 所需圈数 n = L / (2πR)(可分数;非螺旋=0)
    public double SpiralCenterX { get; set; }      // 螺旋圆心 X(锚点朝形心内移 R)
    public double SpiralCenterY { get; set; }      // 螺旋圆心 Y
    public double SpiralStartAngleDeg { get; set; } // 起始角 °(圆心 → 锚点方向,螺旋起点即上一段落点)
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
    // 展线平面投影长合计 = 直腿弦长 + 螺旋弧长(圈数×周长)。【折返段未计入 —— 既有口径,折返中线
    // 本身还只是尖角折点占位、不是可行驶路线,长度没意义,见 docs/折返段落地_几何方案.md 补充三】
    public double TotalRunM { get; set; }
    public int TotalEaseSections { get; set; } // 全程插入的缓坡段总数
    public int StraightLegs { get; set; }      // 斜坡道(直腿)段数
    public int SwitchbackLegs { get; set; }    // 折返(回头)段数
    public int SpiralLegs { get; set; }        // 螺旋(连续转弯)段数
    public double SpiralTurnsTotal { get; set; } // 螺旋总圈数(各级圈数之和)
    /// <summary>
    /// 螺旋段能否并入 <see cref="FaceCutOPoints"/>(逐面切落地缓存)。
    /// 不是"待实现"的占位:见 <see cref="SpiralLandingNote"/>,判据与直线可行判据同一式,螺旋分支下必为 false。
    /// </summary>
    public bool SpiralLandable { get; set; }
    /// <summary>螺旋段不能逐面切落地的实际原因(命令行如实回显);无螺旋段时为空串。</summary>
    public string SpiralLandingNote { get; set; } = "";
    /// <summary>
    /// 承自折返/螺旋落点、已被【投影回坡顶线】的直腿 O 点段数(0 = 全程没出现过这种接缝)。
    ///
    /// 成因:自上而下时直腿的 O 点取【上一段落点】,而折返/螺旋段的落点已朝坑内偏离帮。
    /// 原先直接拿它当 O 点 —— 坑内浮点会被 NearestContour 吸到别的台阶去切(见
    /// docs/折返段落地_几何方案.md「补充二」)。现已在取 O 点时按最近弧位投影回本级环线修正。
    ///
    /// 本计数不再是缺陷计数,而是【近似度提示】:这些段的落地起坡点与预览中线在该接缝处
    /// 存在一个内偏量的横向差(中线仍按折返实际走向绘制)。数值大时值得人工核对该处落地位置。
    /// </summary>
    public int OffWallFaceCutOPoints { get; set; }
    /// <summary>逐面切落地用 O 点(仅斜坡道段的起坡点,落在坡顶线上;折返/螺旋段几何另出,不在此)。</summary>
    public IReadOnlyList<(double X, double Y, double Z)> FaceCutOPoints { get; set; }
        = new List<(double, double, double)>();
    /// <summary>
    /// 与 <see cref="FaceCutOPoints"/> 一一对齐的逐段路面宽 m(基宽 + 该处弯道加宽)。
    /// 近似:阶段④的变宽是【逐中线站】的,而 O 点落在坡顶线上、中线又已内移+圆弧化,两者无索引对应
    /// → 按平面最近中线站取宽(段长远大于内移量,同段内取值稳定)。阶段④未启用时全部退化为基宽 RoadWidth。
    /// </summary>
    public IReadOnlyList<double> FaceCutRoadWidths { get; set; } = new List<double>();

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
/// 直线坡道连通性自动布线(先不考虑运量,只满足限坡 i_max + 连通)。
///
/// 思想:开拓坑线 = 沿帮匀坡盘旋下降。若只许直线段,则必然是「一段帮一条直腿,落到下一级平盘,
/// 再接下一条」的折线螺旋。每降一个台阶高 ΔH,直腿水平投影须 L = ΔH / i。逐级把直腿首尾相接 →
/// 一条从地表到坑底的连续中线:首尾相接保证连通,平面投影长 ≥ L 保证 i ≤ i_max(略缓,安全)。
///
/// 可行性判据(逐级):该级环周长 P ≥ L = ΔH / i。越往坑底环越小,某级 P &lt; L 即纯直线放不下,
/// 此时逐级降级兜底:直线 → 折返(约束化回头,占位 2R+B) → 螺旋(环内按最小半径盘旋,圈数 n=L/2πR);
/// 三种都放不下才止步(report-and-stop),并逐级如实报出是哪一条判据没过。
///
/// 纯几何、不依赖引擎,可单测。命令侧把中线画成 Line 预览(见 MineAssLibPlugin)。
/// </summary>
public static class StraightRampAutoRouter
{
    public static StraightRampRouteResult Route(
        IReadOnlyList<BenchLine> benches, StraightRampRouteOptions opt)
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
        int straightLegs = 0, switchbackLegs = 0, spiralLegs = 0;
        double spiralTurnsTotal = 0;
        double minSpacing = Math.Max(0.0, opt.MinSpacingM);
        // 锚点是否还贴在帮上(落在环线上):起坡口取自环 → true;折返/螺旋的落点朝坑内偏 → false。
        // 自上而下时直腿 O 点取锚点,故锚点离帮时须先投影回环线再当 O 点用(见 OffWallFaceCutOPoints)。
        bool anchorOnWall = true;
        int offWallO = 0;

        // 折返兜底参数
        bool allowSb = opt.AllowSwitchbackFallback && opt.MinTurnRadius > 1e-6;
        double R = opt.MinTurnRadius;
        double sbFootprint = 2.0 * R + Math.Max(0.0, opt.RoadWidth);   // 一次回头折叠的沿帮最小占位

        // 螺旋兜底参数:半径取【设备最小转弯半径】与【设计最小平曲线半径】双下限的大者,
        //   免得为了"盘得下"布出一条自己就违反阶段①线形约束的螺旋。
        //
        // 【可达性,如实交代,别当 bug 查】折返的门槛是 2R+B ≤ 环周长,螺旋是 2πR ≤ 环周长;
        //   R > B/(2π−2) 时 2πR > 2R+B —— 也就是说只要折返兜底开着,环但凡够盘一圈螺旋,
        //   就一定先够折一次回头,螺旋这一支轮不到。默认参数(B=17m、R=10m、R_设计=23.4m)正是如此。
        //   螺旋真正接得住的场合:① 参数框「回头半径」填 0 = 关掉折返时的唯一兜底;② R 很小/路很宽。
        //   而环小到连回头都折不进(等效内径 < 6m)时,一个 R 的圆更放不下 —— 那种深度是几何上真放不下,
        //   如实止步,不为"看着贯通"而放宽任何一条判据。
        double spRadiusBase = opt.SpiralMinRadius > 1e-6 ? opt.SpiralMinRadius : opt.MinTurnRadius;
        double spR = Math.Max(spRadiusBase, Math.Max(0.0, opt.MinCurveRadiusM));
        int spPerTurn = Math.Max(12, opt.SpiralPointsPerTurn);
        // 中线是圆的【内接多边形】,阶段①还要按 R_min 把各拐角圆弧化 —— 多边形的内切圆(=R·cos(π/N))
        //   必须仍 ≥ R_min,否则每个采样点都会被判"半径不足"。故把 R 抬到 R_min/cos(π/N)(N=36 时抬 0.4%)。
        if (opt.MinCurveRadiusM > 1e-9)
            spR = Math.Max(spR, opt.MinCurveRadiusM / Math.Cos(Math.PI / spPerTurn));
        bool allowSp = opt.AllowSpiralFallback && spR > 1e-6;
        double spCircum = 2.0 * Math.PI * spR;                          // 一圈周长
        // 该半径下的弯道加宽(阶段④同一公式);CurveWidenThresholdM=0(未配)时恒为 0 → 判据退化成"平盘 ≥ 路宽"。
        double spWiden = RoadCrossSection.WideningM(spR, opt.CurveWidenThresholdM,
                                                    opt.LaneCount, opt.VehicleWheelbaseM);

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
            // 自上而下取锚点、自下而上取本环落点(PointAtArc 给的点恒在环上)。
            // 锚点承自折返/螺旋时已朝坑内偏,直接当 O 点会被 NearestContour 吸到别的台阶去切 ——
            // 故按最近弧位投影回【本级环线 up】,还原成真正落在坡顶线上的起坡点。
            //   只矫正落地用的 O 点,不动 anchor(中线仍按折返/螺旋实际走向绘制),
            //   所以该接缝处「预览中线」与「落地起坡点」有一个内偏量的横向差 —— 计入 offWallO 如实上报。
            bool oNeedsSnap = (up.AvgZ >= dn.AvgZ) && !anchorOnWall;
            var sO = (up.AvgZ >= dn.AvgZ)
                ? (oNeedsSnap ? up.PointAtArc(up.NearestArc(anchor.X, anchor.Y, out _, out _)) : anchor)
                : sLand;
            bool spaceOk = minSpacing <= 1e-6 || MinPlanDist(placedO, sO) + 1e-6 >= minSpacing;
            bool straightFits = (dn.Perimeter + 1e-6 >= L) && spaceOk;

            // 螺旋候选(真算,不是"标个可行"):本级要盘 n = L / (2πR) 圈才降得完 ΔH
            //   —— 一圈爬升 = i × 2πR,故 n = ΔH/(i·2πR),缓坡段附加展线已含在 L 里。
            // 环内放得下 = ① 环周长 ≥ 一圈周长(等价于等效内径 R_eq = P/2π ≥ R,圆内切进这一圈)
            //             ② 平盘宽 ≥ 路宽 + 该半径的弯道加宽(平盘/路宽任一未知则此条不判,同 WidthOk 口径)。
            double spTurns = (allowSp && spCircum > 1e-9) ? L / spCircum : 0.0;
            bool spPerimOk = allowSp && dn.Perimeter + 1e-6 >= spCircum;
            bool spBermOk = opt.RoadWidth <= 1e-6 || dn.BermWidth <= 1e-6
                            || dn.BermWidth + 1e-6 >= opt.RoadWidth + spWiden;
            bool spiralFits = allowSp && spPerimOk && spBermOk && spTurns > 1e-9;

            if (straightFits)
            {
                // ── 斜坡道:周长够 且 不与已布坑线叠加 → 直腿 ──
                faceOPts.Add(sO);
                placedO.Add(sO);

                // O 点现在恒落在环线上(自下而上由 PointAtArc 给;自上而下若锚点离帮已投影回 up 环)。
                if (oNeedsSnap) offWallO++;

                rep.Form = RampForm.Straight;
                rep.Feasible = true;
                rep.Note = $"斜坡道 L={L:0}m → 落 Z={dn.AvgZ:0.#}m"
                           + (easeThis > 0 ? $"(含缓坡×{easeThis})" : "")
                           + (rep.WidthOk ? "" : $"(注:平盘 {dn.BermWidth:0}m < 路宽 {opt.RoadWidth:0.#}m)")
                           + (oNeedsSnap ? "(注:O 点承自上一段折返/螺旋落点,已投影回坡顶线;该接缝处中线与落地起坡点有横向差)" : "");
                levels.Add(rep);

                // ── 中线:沿台阶弯逐站重采样 + 横向从上环插到下环(不再是 anchor→sLand 一条直弦) ──
                //
                // 【2026-08-18 四轮:直弦的三宗罪】原来这里只 center.Add(sLand),
                //   于是 anchor 到落点之间是一条**平面直弦**:
                //   ① 展线是假的 —— 弧长 L 是按限坡算的(L=ΔH/i),弦长却 ≪ L(环越弯差越多),
                //      实际纵坡因此远超限坡,而报告里还写着"需展线 L / ✓斜坡道";
                //   ② 不走平盘、飞在平盘上方 —— 弦横穿坑内,中间几级平盘全在它下面,
                //      落地只能靠**填方**把它堆起来(现场看到的就是这个);
                //   ③ 脱离帮面 ⇒ 走廊切帮无处可切,挖填方全成了填。
                //
                // 正确模型是被退役的老几何核 BuildBenchFaceRamp 一直在做的那件事:
                //   「沿坡顶/坡脚线按弧长重采样配对,逐站横向插值 → 中线随台阶弯」。
                //   它的毛病是表达不了回头弧/盘旋(所以线形那一层换掉了),但这个模型是对的。
                //
                // 逐站 t∈(0,1]:沿【下环】走弧长 L·t 得 P_dn,再取【上环】上离它最近的点 P_up,
                //   位置 = lerp(P_up, P_dn, t)、Z = lerp(z_up, z_dn, t)
                //   ⇒ 一条【从上平盘起坡、斜穿坡面、落到下平盘】的路,平面长真的等于 L。
                int nRe = Math.Max(2, (int)Math.Ceiling(L / RingResampleStepM));
                if (nRe > MaxStationsPerLevel) nRe = MaxStationsPerLevel;   // 极长展线时限站数(只影响画得多密)

                // 先只生成 XY(横向插值会让实际平面长 ≠ 下环弧长),再按【实际平面长】校正弧段跨度,
                //   最后才按累计平面长定 Z —— 顺序不能反:
                //   Z 若按参数 t 线性给,每段 Δz 相等而平面步长不等 ⇒ 局部纵坡会窜上去(实测顶到 11.1%)。
                double arcSpan = L;
                List<(double X, double Y)> xy = new();
                double planLen = 0;
                for (int iter = 0; iter < 4; iter++)
                {
                    xy.Clear();
                    planLen = 0;
                    double px = anchor.X, py = anchor.Y;
                    for (int q = 1; q <= nRe; q++)
                    {
                        double t = (double)q / nRe;
                        var pdn = dn.PointAtArc(sFootS + dir * arcSpan * t);
                        // 横向:t→0 贴上环(上平盘)、t→1 贴下环(下平盘),中间斜穿坡面。
                        var pup = up.PointAtArc(up.NearestArc(pdn.X, pdn.Y, out _, out _));
                        double qx = pup.X + (pdn.X - pup.X) * t, qy = pup.Y + (pdn.Y - pup.Y) * t;
                        planLen += Math.Sqrt(Dist2(px, py, qx, qy));
                        xy.Add((qx, qy));
                        px = qx; py = qy;
                    }
                    if (planLen >= L - 1e-6) break;          // 平面长够了 ⇒ 纵坡不会超限
                    arcSpan *= L / Math.Max(1e-6, planLen);  // 不够就把弧段跨度按比例拉长,再来一轮
                }

                // Z 按【累计平面长】线性给 ⇒ 逐段纵坡恒等于 ΔH/planLen ≤ i_max(planLen ≥ L 时)。
                double zUp = up.AvgZ, zDn = dn.AvgZ, cum = 0, pxz = anchor.X, pyz = anchor.Y;
                foreach (var (qx, qy) in xy)
                {
                    cum += Math.Sqrt(Dist2(pxz, pyz, qx, qy));
                    center.Add((qx, qy, zUp + (zDn - zUp) * (planLen > 1e-9 ? cum / planLen : 1.0)));
                    centroids.Add((dn.Cx, dn.Cy));
                    pxz = qx; pyz = qy;
                }
                sLand = dn.PointAtArc(sFootS + dir * arcSpan);   // 落点随校正后的跨度走,别和中线末点对不上

                totalRun += planLen;     // 真展线 = 中线实际平面长,不是弦长(弦长会把展线报小、把纵坡报低)
                totalEase += easeThis;
                anchor = sLand;
                anchorOnWall = true;     // 落点由 PointAtArc 给 → 就在下级环线上
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
                anchorOnWall = false;    // 折返落点已朝坑内步进,不在环线上
                connected++;
                switchbackLegs++;
            }
            else if (spiralFits)
            {
                // ── 螺旋兜底:直线放不下、回头也折不进 → 在本级环内按最小半径【连续盘旋】下降 ──
                //   圆心 = 锚点朝形心内移 R:于是螺旋起点恰好落在锚点上,中线不断开;绕 n 圈匀降 ΔH。
                //   n 是真算出来的(L/2πR),放不下已在上面被 spiralFits 挡掉,这里不再"凑一个可行"。
                double inx = dn.Cx - anchor.X, iny = dn.Cy - anchor.Y;   // 朝坑内(形心)方向
                double il = Math.Sqrt(inx * inx + iny * iny);
                if (il > 1e-9) { inx /= il; iny /= il; }
                double ccx = anchor.X + inx * spR, ccy = anchor.Y + iny * spR;
                double a0 = Math.Atan2(anchor.Y - ccy, anchor.X - ccx);  // 起始角:圆心 → 锚点
                double sweep = dir * 2.0 * Math.PI * spTurns;
                // 采样点数封顶 4000:圈数极多时中线画得比设定密度粗,只影响预览观感 ——
                //   展线长按解析值(圈数×周长)计,不受采样密度影响,不会因此虚报。
                int steps = Math.Max(8, (int)Math.Min(4000.0, Math.Ceiling(spTurns * spPerTurn)));

                (double X, double Y, double Z) sp = anchor;
                for (int j = 1; j <= steps; j++)
                {
                    double t = (double)j / steps;
                    double a = a0 + sweep * t;
                    double z = up.AvgZ + (dn.AvgZ - up.AvgZ) * t;        // 沿圈匀降(自上而下/自下而上通用)
                    (double X, double Y, double Z) q = (ccx + spR * Math.Cos(a), ccy + spR * Math.Sin(a), z);
                    center.Add(q);
                    // 形心传【点自身】→ 阶段①的"朝形心内移"在螺旋点上自然 no-op(dx=dy=0):
                    //   螺旋中线本就是设计半径 R 的圆、已在坑内不贴帮,再内移只会把半径削到 R_min 以下。
                    centroids.Add((q.X, q.Y));
                    sp = q;
                }
                (double X, double Y, double Z) spEnd = (sp.X, sp.Y, dn.AvgZ);   // 末圈落到下环标高
                placedO.Add(spEnd);

                string spWhy = (dn.Perimeter + 1e-6 < L) ? $"周长{dn.Perimeter:0}<需{L:0}m" : "直线与已布坑线叠加";
                rep.Form = RampForm.Spiral;
                rep.SpiralRadiusM = spR;
                rep.SpiralTurns = spTurns;
                rep.SpiralCenterX = ccx;
                rep.SpiralCenterY = ccy;
                rep.SpiralStartAngleDeg = a0 * 180.0 / Math.PI;
                rep.Feasible = true;
                rep.Note = $"螺旋 R={spR:0.#}m × {spTurns:0.##}圈(一圈周长 {spCircum:0}m/爬升 {spCircum * g:0.#}m;"
                           + $"直线放不下:{spWhy}"
                           + (allowSb ? $";回头占位 {sbFootprint:0}m 也折不进)" : ";未启用折返兜底)")
                           + (spWiden > 1e-6 ? $",弯道加宽 {spWiden:0.#}m" : "")
                           + (easeThis > 0 ? $",含缓坡×{easeThis}" : "")
                           + ";螺旋段不逐面切落地(原因见汇总)";
                levels.Add(rep);

                totalRun += spTurns * spCircum;   // 螺旋展线 = 圈数 × 一圈周长(圆弧长解析值,与采样密度无关)
                totalEase += easeThis;
                anchor = spEnd;
                anchorOnWall = false;    // 螺旋末点在坑内那个圆上(离帮最远 2R),不在环线上
                connected++;
                spiralLegs++;
                spiralTurnsTotal += spTurns;
            }
            else
            {
                // 直线放不下、回头折不进、螺旋也盘不下 → 止步。逐条报出是哪一款判据没过(带数),
                // 不为了"看着贯通"而放宽任何一条 —— 放不下就是放不下。
                string sbTxt = allowSb ? $"回头占位 {sbFootprint:0}m 折不进" : "未启用折返兜底";
                string spTxt = !allowSp ? "未启用螺旋兜底"
                    : !spPerimOk ? $"螺旋一圈周长 {spCircum:0}m > 环周长 {dn.Perimeter:0}m(R={spR:0.#}m 盘不进)"
                    : !spBermOk ? $"平盘 {dn.BermWidth:0.#}m < 路宽 {opt.RoadWidth:0.#}m+加宽 {spWiden:0.#}m(R={spR:0.#}m)"
                    : $"螺旋圈数算不出(L={L:0}m/一圈{spCircum:0}m)";
                rep.SpiralRadiusM = allowSp ? spR : 0.0;
                rep.SpiralTurns = allowSp ? spTurns : 0.0;
                rep.Feasible = false;
                rep.Note = $"环周长 {dn.Perimeter:0}m 放不下直线(需 {L:0}m);{sbTxt};{spTxt} "
                           + "→ 止步(须并段/降台阶高/另辟运输方式)";
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

        // 逐段路面宽(与 faceOPts 对齐),供【坑线落地】按段取宽而非回读全局约束。
        //   阶段④的 RoadWidths 是逐中线站的,O 点在坡顶线上、中线已内移+圆弧化 → 无索引对应,
        //   取平面最近中线站的宽度作近似(见 FaceCutRoadWidths 注释);阶段④未算则退化为基宽。
        {
            var line = res.Centerline;
            var wid = res.RoadWidths;
            bool hasWiden = wid.Count > 0 && wid.Count == line.Count;
            var oW = new double[faceOPts.Count];
            for (int n = 0; n < faceOPts.Count; n++)
            {
                double w = opt.RoadWidth;
                if (hasWiden)
                {
                    double best = double.MaxValue;
                    for (int t = 0; t < line.Count; t++)
                    {
                        double d2 = Dist2(faceOPts[n].X, faceOPts[n].Y, line[t].X, line[t].Y);
                        if (d2 < best) { best = d2; w = wid[t]; }
                    }
                }
                oW[n] = w;
            }
            res.FaceCutRoadWidths = oW;
        }

        res.FaceCutOPoints = faceOPts;
        res.LevelsConnected = connected;
        res.StraightLegs = straightLegs;
        res.SwitchbackLegs = switchbackLegs;
        res.SpiralLegs = spiralLegs;
        res.SpiralTurnsTotal = spiralTurnsTotal;
        res.OffWallFaceCutOPoints = offWallO;

        // ── 螺旋段能不能落地:按【逐面切 O 点合法性】实算复核,不写死 ──
        //   逐面切几何核(BuildBenchFaceRamp)拿一个 O 点只解得出【单片坡面·单向下降】的一条带:
        //   平面长 L=ΔH/i,两端还要吸到该级真实坡顶/坡脚线上 ⟹ O 点合法 ⟺ 该环放得下这条 L,
        //   与直线可行判据同一式。螺旋恰恰是这一式为假才降级来的,故逐级复核结果应为 0 段可取点。
        if (spiralLegs > 0)
        {
            int oLegal = 0;
            foreach (var lv in levels)
                if (lv.Form == RampForm.Spiral && lv.LowerPerimeterM + 1e-6 >= lv.RequiredRunM) oLegal++;
            res.SpiralLandable = false;   // 没算出 O 点就是没有:哪怕复核出反例也不造点
            res.SpiralLandingNote =
                "螺旋段不并入逐面切落地缓存:逐面切几何核(BuildBenchFaceRamp)从一个 O 点只解得出" +
                "【单片坡面·单向下降】的一条带(平面长 L=ΔH/i,两端须吸到该级真实坡顶/坡脚线上)," +
                "而本级正是因为环周长放不下这条 L 才降级到螺旋 → 同一环上取不到合法 O 点(已逐级复核);" +
                "加之螺旋中线离帮最远达 2R、绕圈自交,坡顶线上没有对应点。螺旋落地须改走 InsertRampSpiral" +
                "(圆心/半径/起始角/圈数/旋向 已在逐级报告里给全),该通路尚未接。" +
                (oLegal > 0
                    ? $" 另:其中 {oLegal} 段所在环其实放得下 L —— 那是因【与已布段间距冲突】才降级的," +
                      "在那儿取 O 点虽合法却会与已切坡面重叠,故同样不入缓存。"
                    : "");
        }
        res.ReachedZ = anchor.Z;
        res.ReachedBottom = connected == rings.Count - 1;
        res.TotalRunM = totalRun;
        res.TotalEaseSections = totalEase;
        res.Success = connected >= 1;
        if (!res.Success && res.Error == null) res.Error = "首级即不可行(最外环周长不足或无下降)";
        return res;
    }

    // ── 环抽取:每级用 crest(退化用 toe)+ 最深 toe 兜底为坑底环;按标高去重、从高到低排 ──
    /// <summary>沿环重采样的目标站距 m —— 站距太大,中线就跟不住台阶的弯。</summary>
    private const double RingResampleStepM = 10.0;
    /// <summary>单级站点数上限(极长展线时只影响画得多密,不影响展线长的解析值)。</summary>
    private const int MaxStationsPerLevel = 400;

    private static List<Ring> BuildRings(IReadOnlyList<BenchLine> benches, double zTol)
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
