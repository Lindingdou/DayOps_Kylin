using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  境界优化 —— 核心数据契约（逐字移植原 PlanLib.BoundaryOptimization.PitScheme）
//
//   · UI = 2 个按钮：① 境界圈定（编辑 PitScheme）  ② 确定境界（计算·方案比选·确定最终境界）
//   · PitScheme 是贯穿两个按钮的唯一数据载体；「方案比选」比较的就是多个 PitScheme。
//   · "按矿床用剥采比原则控制" 落在 Deposit→Principle 的分派 + EconParams 的 4 公式上。
//   · 能自动化的尽量自动化：Auto* 字段记录自动识别/预填的来源，全部可被用户覆盖。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>矿床类型（决定剥采比原则与圈定方法）。</summary>
public enum DepositType
{
    NearHorizontal, // 水平 / 近水平（微倾斜）
    GentleDip,      // 缓倾斜
    Inclined,       // 倾斜
    SteepDip,       // 急倾斜
    MultiSeam,      // 多煤层 / 复合
    IrregularMassive // 不规则 / 块状（金属）
}

/// <summary>剥采比控制原则。</summary>
public enum StripRatioPrinciple
{
    Average,    // 平均剥采比原则（水平/近水平）
    Contour,    // 境界剥采比原则（倾斜/急倾斜）
    Production  // 生产剥采比原则（分期/各生产时期）
}

/// <summary>圈定方法。</summary>
public enum DelineationMethod
{
    CrossSection,    // 横剖面法（层状矿床）
    FloatingCone,    // 移动圆锥法（不规则/块状）
    LerchsGrossmann, // 图论 L-G 法
    NetworkFlow      // 网络流法
}

/// <summary>经济合理剥采比 n_经 的计算方法（采矿手册 4 式）。</summary>
public enum EconRatioMethod
{
    CostComparison,   // 成本比较法（替代原则）  n=(C_D − a)/b
    Price,            // 价格法                  n=(d − a)/b
    PriceProfit,      // 价格法 + 盈利            n=[d − (a+e)]/b
    PriceProfitReclaim// 价格法 + 盈利 + 复垦     n=[d − (a+e+c)]/b
}

/// <summary>经济合理剥采比参数与结果（体积口径，与剥采比均衡 VP 曲线一致：m³/t）。</summary>
public sealed class EconParams
{
    public EconRatioMethod Method { get; set; } = EconRatioMethod.Price;

    // 缺省一律取自【生产成本口径】ProductionCostBook（规则 MU14）；这些字段仍可逐方案覆盖。
    public double UndergroundCost { get; set; } = ProductionCostBook.Current.UndergroundCostYuanT;  // C_D 矿石地下采矿成本 (元/t)
    public double MiningCost { get; set; } = ProductionCostBook.Current.MiningCostYuanT;            // a   露天纯采矿成本   (元/t)
    public double StripCost { get; set; } = ProductionCostBook.Current.StripCostYuanM3;             // b   剥离成本         (元/m³)  ← 体积口径
    public double Price { get; set; } = ProductionCostBook.Current.CoalPriceYuanT;                  // d   原煤售价         (元/t)
    public double MinProfit { get; set; } = ProductionCostBook.Current.MinProfitYuanT;              // e   单位最低盈利     (元/t)
    public double ReclaimCost { get; set; } = ProductionCostBook.Current.ReclaimCostYuanT;          // c   分摊复垦费       (元/t)
    public double CoalDensity { get; set; } = ProductionCostBook.Current.CoalDensityTM3;            // 煤密度 (t/m³)
    public double DiscountRatePct { get; set; } = ProductionCostBook.Current.DiscountRatePct;       // 折现率 (%)

    /// <summary>按所选方法计算 n_经（m³/t）；参数不全返回 null。</summary>
    public double? ComputeEconRatio()
    {
        if (StripCost <= 0) return null;
        return Method switch
        {
            EconRatioMethod.CostComparison    => (UndergroundCost - MiningCost) / StripCost,
            EconRatioMethod.Price             => (Price - MiningCost) / StripCost,
            EconRatioMethod.PriceProfit       => (Price - (MiningCost + MinProfit)) / StripCost,
            EconRatioMethod.PriceProfitReclaim=> (Price - (MiningCost + MinProfit + ReclaimCost)) / StripCost,
            _ => null
        };
    }
}

/// <summary>分帮最终边坡角（来自 SlopeDesign，可被覆盖）。</summary>
public sealed class WallAngle
{
    public string SideName { get; set; } = "";  // 东/西/南/北/工作帮/端帮
    public string SideType { get; set; } = "final"; // working / final
    public double BetaDeg { get; set; }           // 最终帮坡角 β (°)
    public double? SafetyF { get; set; }          // 安全系数 F = tanφ/tanβ
    public string Source { get; set; } = "SlopeDesign";
    public bool Ok { get; set; } = true;          // 稳定性校核
    public string OkText => Ok ? "✓" : "⚠";
    /// <summary>DataGrid 显示用（可空 F）。</summary>
    public string SafetyFText => SafetyF is { } f ? f.ToString("F2") : "";

    public static ObservableCollection<WallAngle> CreateDefault() => new()
    {
        new WallAngle { SideName = "工作帮", SideType = "working", BetaDeg = 18, SafetyF = 1.30 },
        new WallAngle { SideName = "端帮(东)", SideType = "final", BetaDeg = 38, SafetyF = 1.22 },
        new WallAngle { SideName = "端帮(西)", SideType = "final", BetaDeg = 38, SafetyF = 1.22 },
        new WallAngle { SideName = "非工作帮", SideType = "final", BetaDeg = 35, SafetyF = 1.18 },
    };
}

/// <summary>
/// 境界优化引用的面与界线。几何（地表/煤层顶底板网格、底周界线）存于 CAD 文档，
/// 这里只持有其实体 handle（0=未指定；Kylin 侧 handle 由 <see cref="Draw.EntityHandles"/> 会话内分配）。
/// </summary>
public sealed class PitGeometryRefs
{
    public long TerrainHandle { get; set; }                              // 地表面
    public ObservableCollection<SeamSurfaceRef> Seams { get; set; } = new(); // 煤层顶底板（可多层）
    public long BottomSeedHandle { get; set; }                           // 底周界种子（0=按矿体投影自动）
    public long SurfaceLimitHandle { get; set; }                         // 地表界/顶口限制线（矿权/征地范围；0=用块体足迹兜底）
    public ObservableCollection<SegmentBeta> WallSegments { get; set; } = new(); // 按地表界多段线各直线段绑定的最终帮坡角（空=按方位兜底）
}

/// <summary>境界线某一直线段（帮）的最终帮坡角绑定。Index 与地表界多段线的边对齐（顶点 i→i+1）。</summary>
public sealed class SegmentBeta
{
    public int Index { get; set; }
    public string SideName { get; set; } = "";   // 帮别（描述，可空）
    public double BetaDeg { get; set; } = 40;     // 该段最终帮坡角
    public double InsetExtraM { get; set; }       // 该段额外向内收缩量 (m)，0=不收缩
}

/// <summary>单个煤层的顶/底板面引用（handle，几何在文档）。</summary>
public sealed class SeamSurfaceRef
{
    public string Name { get; set; } = "煤层1";
    public long RoofHandle { get; set; }   // 顶板
    public long FloorHandle { get; set; }  // 底板
}

/// <summary>求解结果（计算后回填到 PitScheme.Result）。</summary>
public sealed class PitResult
{
    public double EconRatio { get; set; }    // n_经 (m³/t)
    public double DepthM { get; set; }       // 开采深度 (m)
    public double CoalWanT { get; set; }     // 煤量 (万t)
    public double WasteWanM3 { get; set; }   // 岩量 (万m³)
    public double AvgRatio { get; set; }     // 平均剥采比 (m³/t)
    public double ContourRatio { get; set; } // 境界剥采比 (m³/t)
    public double NetValue { get; set; }     // 净值 (万元)
    public bool Ok { get; set; }             // 校核：平均剥采比 ≤ n_经
    public string OkText => Ok ? "通过" : "超经济比";

    // ── 系统全面对比指标（求解后回填；用于方案比选矩阵 + 综合评分）──
    public double ProductionRatioPeak { get; set; } // 生产剥采比峰值 (m³/t)
    public double RecoveryPct { get; set; }         // 资源回收率 (%)
    public double AvgAshPct { get; set; }           // 平均灰分 (%)
    public double TopAreaHa { get; set; }           // 占地/地表界面积 (ha)
    public double BottomWidthM { get; set; }        // 实际底宽 (m)
    public int BenchCount { get; set; }             // 台阶数
    public double Npv { get; set; }                 // 折现净现值 (万元)
    public double UnitCostYuanPerT { get; set; }    // 单位成本 (元/t)
    public double ServiceLifeYears { get; set; }    // 服务年限 (a)
    public double AnnualCoalWanT { get; set; }      // 年均采出 (万t)
    public double MinSafetyF { get; set; }          // 最小边坡安全系数

    // 输出几何（落地为文档实体后回填其 handle；0=未落地）
    public long BottomPerimeterHandle { get; set; }   // 底周界（最终）
    public long SurfaceBoundaryHandle { get; set; }   // 地表界（坡顶落地表）
    public List<long> BenchLineHandles { get; set; } = new(); // 坡顶/坡底线

    // ── 逐层曲线（Kylin 补：供②窗「境界剥采比–深度曲线」用，原版此处为占位）──
    public double[] CurveDepthM { get; set; } = System.Array.Empty<double>();
    public double[] CurveContourSR { get; set; } = System.Array.Empty<double>();
    public double[] CurveNetWan { get; set; } = System.Array.Empty<double>();
}

/// <summary>
/// 「境界优化方案」—— 一组完整的圈定输入（矿床/原则 + 经济合理剥采比 + 分帮角 + 底部 + 剖面）。
/// 配置窗口编辑它，计算窗口对一组方案求解、比选、确定最终境界。
/// </summary>
public sealed class PitScheme
{
    public string Name { get; set; } = "方案1";
    public string Note { get; set; } = "";
    public bool Participate { get; set; } = true; // 是否参与本次方案比选
    public bool IsConfirmed { get; set; }         // 已「确定最终境界」（落地确认）；采区划分的境界来源必选且仅限已确定的方案
    public string ConfirmedText => IsConfirmed ? "✔已确定" : "未确定";

    // ① 矿床与剥采比原则
    public DepositType Deposit { get; set; } = DepositType.Inclined;
    public StripRatioPrinciple Principle { get; set; } = StripRatioPrinciple.Contour;
    public DelineationMethod Method { get; set; } = DelineationMethod.CrossSection;
    public double? AutoDipDeg { get; set; }     // 自动识别：平均倾角
    public double? AutoStrikeDeg { get; set; }  // 自动识别：走向方位
    public int? AutoSeamCount { get; set; }     // 自动识别：煤层数

    // ② 经济合理剥采比
    public EconParams Econ { get; set; } = new();

    // ③ 分帮最终边坡角
    public ObservableCollection<WallAngle> Walls { get; set; } = WallAngle.CreateDefault();

    // ④ 底部周界与底宽
    public double BottomWidthM { get; set; } = 60;    // 最小底宽（按采装设备规格）
    public double ContractionM { get; set; }          // 整体向内收缩量 (m)；0=满采到算出境界
    public double MinWorkingWidthM { get; set; } = 40; // 最小工作面宽
    public string BottomSource { get; set; } = "矿体投影(自动)";
    public string EquipmentRef { get; set; } = "WK-10 电铲";

    // ④' 台阶（境界级代表值；精细台阶模板属「剥采排工程」）
    public double BenchHeightM { get; set; } = 12;       // 台阶高 H
    public double BenchFaceAngleDeg { get; set; } = 70;  // 台阶坡面角 α

    // 时序 / 质量（0 = 自动：年产按泰勒规则、灰分按块体采样/缺省）
    public double TargetAnnualWanT { get; set; } = 0;   // 设计年产 (万t)
    public double AshPct { get; set; } = 0;             // 平均灰分 (%)

    // ⑤ 横剖面布置
    public double StrikeAzimuthDeg { get; set; } = 0;
    public double SectionSpacingM { get; set; } = 50;
    public bool AutoStrike { get; set; } = true;
    public bool AutoSpacing { get; set; } = true;

    // ⑥ 面与界线（地表 / 煤层顶底板 / 底周界）——几何在文档，这里只引用 handle
    public PitGeometryRefs Geometry { get; set; } = new();

    // 求解结果（计算后回填）
    public PitResult? Result { get; set; }

    // ── 展示用（供 DataGrid 直接绑定）──
    public string DepositText => Deposit switch
    {
        DepositType.NearHorizontal => "水平/近水平",
        DepositType.GentleDip => "缓倾斜",
        DepositType.Inclined => "倾斜",
        DepositType.SteepDip => "急倾斜",
        DepositType.MultiSeam => "多煤层",
        DepositType.IrregularMassive => "不规则/块状",
        _ => "—"
    };
    public string PrincipleText => Principle switch
    {
        StripRatioPrinciple.Average => "平均剥采比",
        StripRatioPrinciple.Contour => "境界剥采比",
        StripRatioPrinciple.Production => "生产剥采比",
        _ => "—"
    };
    public string SolvedText => Result == null ? "未求解" : "已求解";

    public PitScheme Clone()
    {
        var c = (PitScheme)MemberwiseClone();
        c.Name = Name + "·副本";
        c.Econ = new EconParams
        {
            Method = Econ.Method, UndergroundCost = Econ.UndergroundCost, MiningCost = Econ.MiningCost,
            StripCost = Econ.StripCost, Price = Econ.Price, MinProfit = Econ.MinProfit,
            ReclaimCost = Econ.ReclaimCost, CoalDensity = Econ.CoalDensity, DiscountRatePct = Econ.DiscountRatePct
        };
        c.Walls = new ObservableCollection<WallAngle>();
        foreach (var w in Walls)
            c.Walls.Add(new WallAngle { SideName = w.SideName, SideType = w.SideType, BetaDeg = w.BetaDeg, SafetyF = w.SafetyF, Source = w.Source, Ok = w.Ok });
        c.Geometry = new PitGeometryRefs { TerrainHandle = Geometry.TerrainHandle, BottomSeedHandle = Geometry.BottomSeedHandle, SurfaceLimitHandle = Geometry.SurfaceLimitHandle };
        foreach (var s in Geometry.Seams)
            c.Geometry.Seams.Add(new SeamSurfaceRef { Name = s.Name, RoofHandle = s.RoofHandle, FloorHandle = s.FloorHandle });
        foreach (var seg in Geometry.WallSegments)
            c.Geometry.WallSegments.Add(new SegmentBeta { Index = seg.Index, SideName = seg.SideName, BetaDeg = seg.BetaDeg, InsetExtraM = seg.InsetExtraM });
        c.Result = null;
        c.IsConfirmed = false;
        return c;
    }

    /// <summary>骨架演示用样例方案（含求解结果），供两个窗口先看到布局（原版同）。</summary>
    public static ObservableCollection<PitScheme> CreateSamples()
    {
        var a = new PitScheme
        {
            Name = "方案A·基准",
            Deposit = DepositType.Inclined, Principle = StripRatioPrinciple.Contour,
            AutoDipDeg = 22, AutoStrikeDeg = 75, AutoSeamCount = 1,
            Econ = new EconParams { Method = EconRatioMethod.Price, Price = 320, MiningCost = 95, StripCost = 28 },
            Result = new PitResult
            {
                EconRatio = 8.04, DepthM = 210, CoalWanT = 9850, WasteWanM3 = 71200,
                AvgRatio = 7.23, ContourRatio = 8.01, NetValue = 186500, Ok = true,
                ProductionRatioPeak = 9.5, RecoveryPct = 88, AvgAshPct = 14.2, TopAreaHa = 420,
                BottomWidthM = 60, BenchCount = 14, Npv = 96500, UnitCostYuanPerT = 168,
                ServiceLifeYears = 24, AnnualCoalWanT = 410, MinSafetyF = 1.18,
            }
        };
        a.Geometry.Seams.Add(new SeamSurfaceRef { Name = "主煤层" });
        var b = a.Clone();
        b.Name = "方案B·陡帮"; b.Walls[1].BetaDeg = 42; b.Walls[2].BetaDeg = 42;
        b.Result = new PitResult
        {
            EconRatio = 8.04, DepthM = 235, CoalWanT = 10980, WasteWanM3 = 84600,
            AvgRatio = 7.70, ContourRatio = 8.03, NetValue = 201300, Ok = true,
            ProductionRatioPeak = 9.9, RecoveryPct = 90, AvgAshPct = 14.5, TopAreaHa = 405,
            BottomWidthM = 60, BenchCount = 16, Npv = 102300, UnitCostYuanPerT = 172,
            ServiceLifeYears = 27, AnnualCoalWanT = 410, MinSafetyF = 1.12,
        };
        var c = a.Clone();
        c.Name = "方案C·高煤价"; c.Econ.Price = 360;
        c.Result = new PitResult
        {
            EconRatio = 9.46, DepthM = 268, CoalWanT = 12240, WasteWanM3 = 101800,
            AvgRatio = 8.32, ContourRatio = 9.40, NetValue = 233900, Ok = true,
            ProductionRatioPeak = 11.1, RecoveryPct = 92, AvgAshPct = 14.8, TopAreaHa = 460,
            BottomWidthM = 60, BenchCount = 18, Npv = 118700, UnitCostYuanPerT = 165,
            ServiceLifeYears = 30, AnnualCoalWanT = 410, MinSafetyF = 1.08,
        };
        return new ObservableCollection<PitScheme> { a, b, c };
    }
}

/// <summary>
/// 会话级共享：境界方案集在「境界圈定①」「确定境界②」「采区划分③」之间共享（原 <c>BoundarySchemeStore</c>）。
/// 首次访问以样例方案初始化（演示态）；后续所有窗口复用同一实例。
/// </summary>
public static class BoundarySchemeStore
{
    private static ObservableCollection<PitScheme>? _schemes;
    public static ObservableCollection<PitScheme> Schemes => _schemes ??= PitScheme.CreateSamples();
    /// <summary>测试/新文档用：清空回样例。</summary>
    public static void Reset() => _schemes = null;
}
