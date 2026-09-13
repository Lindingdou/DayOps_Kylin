using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  采区划分 / 开采程序 —— 核心数据契约（逐字移植原 PlanLib.BoundaryOptimization.MiningProgramPlan）
//   · 定位：在已定最终境界内做采区划分 + 优化选择（自包含，不耦合下游模块）。
//   · UI = 2 个按钮：③ 采区划分（编辑 MiningProgramPlan）④ 开采程序确定（计算·比选·确定）。
//   · 手册原则落到打分：首采区(剥采比最小+煤厚+埋深+运距+内排) / 拉沟·推进(按初始拉沟位置选择约束)
//     / 均衡切采区 / 内排时机。
//   · 产能/推进耦合：Q = 工作线长 L × 推进度 v × 台阶高 H × 容重 ρ。
//  AdvanceMode / DumpMode / StripRatioField 复用 Kylin 既有 Cad 定义（同原义）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>开采策略（按产状分派；可被覆盖）。</summary>
public enum MiningStrategy
{
    Strip,        // 条带（近水平/缓倾斜：拉沟 + 内排）
    LongToCross,  // 纵采转横采（倾斜：沿倾向超前降深）
    Bench         // 台阶式（急倾斜：接近三维场，以外排为主）
}

/// <summary>采区切分取向（原序：按产能 / 按服务年限 / 固定采区数）。</summary>
public enum SplitObjective
{
    ByCapacity, // 按目标产能均衡
    ByLife,     // 按目标服务年限均衡
    FixedN      // 固定采区数
}

/// <summary>拉沟·推进方案来源模式。</summary>
public enum BoxcutMode
{
    Auto,   // 全自动推荐（按约束生成多候选并打分排序）
    Manual  // 人工指定（拾取拉沟线 + 给推进方位）
}

/// <summary>首采区打分权重（归一，和≈1）。手册：剥采比最小为主，兼顾煤厚/埋深/运距/内排。</summary>
public sealed class FirstPanelWeights
{
    public double StripRatio { get; set; } = 0.40;
    public double CoalThickness { get; set; } = 0.20;
    public double Depth { get; set; } = 0.15;
    public double HaulDistance { get; set; } = 0.15;
    public double InnerDump { get; set; } = 0.10;

    public double Sum => StripRatio + CoalThickness + Depth + HaulDistance + InnerDump;
    public static FirstPanelWeights CreateDefault() => new();
    public FirstPanelWeights Copy() => new()
    {
        StripRatio = StripRatio, CoalThickness = CoalThickness, Depth = Depth,
        HaulDistance = HaulDistance, InnerDump = InnerDump
    };
}

/// <summary>初始拉沟位置选择约束权重（归一，和≈1）。</summary>
public sealed class BoxcutWeights
{
    public double StripRatio { get; set; } = 0.30;  // 剥采比最小
    public double Shallow { get; set; } = 0.15;     // 煤层埋藏浅 / 出露
    public double Haul { get; set; } = 0.20;        // 便于开拓运输 / 运距短
    public double InnerDump { get; set; } = 0.15;   // 利于尽早内排
    public double WorkLine { get; set; } = 0.10;    // 工作线长度足够
    public double Geology { get; set; } = 0.10;     // 避开断层/构造破碎/含水/老窑 + 地形排水有利

    public double Sum => StripRatio + Shallow + Haul + InnerDump + WorkLine + Geology;
    public static BoxcutWeights CreateDefault() => new();
    public BoxcutWeights Copy() => new()
    {
        StripRatio = StripRatio, Shallow = Shallow, Haul = Haul,
        InnerDump = InnerDump, WorkLine = WorkLine, Geology = Geology
    };
}

/// <summary>
/// 一个「拉沟 · 推进」候选方案（= 初始拉沟位置 + 推进方向）。逐约束分 0..1，总分 0..100；
/// 硬约束（工作线长≥最小、避开禁区）不满足 → Feasible=false。
/// </summary>
public sealed class BoxcutAdvanceOption
{
    public string Name { get; set; } = "拉沟方案1";
    public bool IsAuto { get; set; } = true;
    public string BoxcutDesc { get; set; } = "";
    public long BoxcutLineHandle { get; set; }
    public double AdvanceAzimuthDeg { get; set; }
    public double WorkingLineLengthM { get; set; }
    public AdvanceMode AdvanceMode { get; set; } = AdvanceMode.Parallel;
    public double PivotX { get; set; }
    public double PivotY { get; set; }

    public double SrScore { get; set; }
    public double ShallowScore { get; set; }
    public double HaulScore { get; set; }
    public double InnerDumpScore { get; set; }
    public double WorkLineScore { get; set; }
    public double GeoScore { get; set; }

    public double TotalScore { get; set; }
    public bool Feasible { get; set; } = true;
    public bool Recommended { get; set; }
    public string Rationale { get; set; } = "";

    public string SourceText => IsAuto ? "全自动" : "人工指定";
    public string RecoText => Recommended ? "★推荐" : (Feasible ? "" : "✗不可行");
    public string AdvanceModeText => AdvanceMode switch
    {
        AdvanceMode.Parallel => "平行推进",
        AdvanceMode.FixedPivot => "定点回转",
        AdvanceMode.MovingPivot => "动点回转",
        _ => "—"
    };
    // DataGrid 显示格式列
    public string AzText => AdvanceAzimuthDeg.ToString("F0");
    public string WlText => WorkingLineLengthM.ToString("F0");
    public string SrText => SrScore.ToString("F2");
    public string HaulText => HaulScore.ToString("F2");
    public string InnerText => InnerDumpScore.ToString("F2");
    public string WlScoreText => WorkLineScore.ToString("F2");
    public string TotalText => TotalScore.ToString("F0");

    public BoxcutAdvanceOption Copy() => new()
    {
        Name = Name, IsAuto = IsAuto, BoxcutDesc = BoxcutDesc, BoxcutLineHandle = BoxcutLineHandle,
        AdvanceAzimuthDeg = AdvanceAzimuthDeg, WorkingLineLengthM = WorkingLineLengthM,
        AdvanceMode = AdvanceMode, PivotX = PivotX, PivotY = PivotY,
        SrScore = SrScore, ShallowScore = ShallowScore, HaulScore = HaulScore,
        InnerDumpScore = InnerDumpScore, WorkLineScore = WorkLineScore, GeoScore = GeoScore,
        TotalScore = TotalScore, Feasible = Feasible, Recommended = Recommended, Rationale = Rationale
    };

    /// <summary>按权重把逐约束分汇成 0..100 总分。</summary>
    public void Recompute(BoxcutWeights w)
    {
        double s = SrScore * w.StripRatio + ShallowScore * w.Shallow + HaulScore * w.Haul
                 + InnerDumpScore * w.InnerDump + WorkLineScore * w.WorkLine + GeoScore * w.Geology;
        double sum = w.Sum <= 0 ? 1 : w.Sum;
        TotalScore = s / sum * 100.0;
    }
}

/// <summary>单个采区（求解产物）。几何（边界/拉沟线）存文档，这里只持 handle。</summary>
public sealed class MiningPanel
{
    public int Index { get; set; }
    public string Name { get; set; } = "采区1";
    public long BoundaryHandle { get; set; }
    public double CoalWanT { get; set; }
    public double WasteWanM3 { get; set; }
    public double StripRatio { get; set; }
    public int Order { get; set; }
    public long BoxcutLineHandle { get; set; }
    public double AdvanceAzimuthDeg { get; set; }
    public double WorkingLineLengthM { get; set; }
    public double AdvanceRateMpa { get; set; }
    public DumpMode Dump { get; set; } = DumpMode.External;
    public double DumpSwitchYear { get; set; }
    public double ServiceLifeYears { get; set; }
    public double MinX, MinY, MaxX, MaxY;

    public bool IsFirst => Order == 1;
    public string OrderText => Order == 1 ? "① 首采区" : Order.ToString();
    public string DumpText => Dump == DumpMode.Internal
        ? (DumpSwitchYear > 0 ? $"内排(第{DumpSwitchYear:0.#}年)" : "内排")
        : "外排";
    public string CoalText => CoalWanT.ToString("N0");
    public string WasteText => WasteWanM3.ToString("N0");
    public string SrText => StripRatio.ToString("F1");
    public string AzText => AdvanceAzimuthDeg.ToString("F0");
    public string WlText => WorkingLineLengthM.ToString("F0");
    public string RateText => AdvanceRateMpa.ToString("F0");
    public string LifeText => ServiceLifeYears.ToString("F1");
}

/// <summary>开采程序求解结果（本功能自算的系统全面指标）。对标 PitResult。</summary>
public sealed class ProgramResult
{
    public double ProductionRatioPeak { get; set; }
    public double InnerDumpPct { get; set; }
    public double TimeToCapacityYears { get; set; }
    public double ServiceLifeYears { get; set; }
    public double ReserveBalanceCoef { get; set; }
    public double BasicStrippingYiM3 { get; set; }
    public double AvgHaulKm { get; set; }
    public double Npv { get; set; }
    public double CompositeScore { get; set; }
    public int PanelCount { get; set; }
    public double DrawZ;
    public List<long> EntityHandles = new();
    public bool Ok { get; set; }
    public string OkText => Ok ? "通过" : "待校核";
}

/// <summary>
/// 「开采程序方案」—— 一组完整的采区划分输入（产状/策略 + 境界来源 + 首采区权重 +
/// 拉沟·推进 + 采区数取向 + 内排 + 产能/推进约束）。③ 配置窗口编辑它，④ 计算窗口求解、比选、确定。
/// </summary>
public sealed class MiningProgramPlan
{
    public const double DefaultCoalDensity = 1.35; // t/m³

    public string Name { get; set; } = "程序1";
    public string Note { get; set; } = "";
    public bool Participate { get; set; } = true;

    // ① 产状与策略
    public DepositType Attitude { get; set; } = DepositType.NearHorizontal;
    public MiningStrategy Strategy { get; set; } = MiningStrategy.Strip;
    public double? AutoDipDeg { get; set; }
    public double? AutoStrikeDeg { get; set; }

    // ② 境界来源
    public string SourceSchemeName { get; set; } = "";
    public PitResult? SourcePitResult { get; set; }

    // ③ 首采区打分权重
    public FirstPanelWeights FirstWeights { get; set; } = FirstPanelWeights.CreateDefault();

    // ④ 拉沟 · 推进方案
    public BoxcutMode BoxcutMode { get; set; } = BoxcutMode.Auto;
    public double ManualAdvanceAzimuthDeg { get; set; }
    public long ManualBoxcutLineHandle { get; set; }
    public BoxcutWeights BoxcutWeights { get; set; } = BoxcutWeights.CreateDefault();
    public ObservableCollection<BoxcutAdvanceOption> BoxcutOptions { get; set; } = new();
    public BoxcutAdvanceOption? SelectedBoxcut { get; set; }

    // ⑤ 采区数与均衡
    public SplitObjective Split { get; set; } = SplitObjective.ByLife;
    public int PanelCount { get; set; } = 4;
    public double TargetCapacityWanTa { get; set; } = 400;
    public double TargetServiceLifeYears { get; set; } = 30;

    // ⑥ 内排
    public bool InnerDumpEnabled { get; set; } = true;
    public double InnerDumpStartWidthM { get; set; } = 300;

    // ⑦ 产能 / 推进约束
    public double MinWorkingLineM { get; set; } = 800;
    public double MiningWidthM { get; set; } = 50;
    public double BenchHeightM { get; set; } = 12;
    public double TargetAdvanceRateMpa { get; set; }
    public double MaxAdvanceRateMpa { get; set; }
    public string EquipmentRef { get; set; } = "WK-10 电铲 + 矿用卡车";

    // ⑧ 经济（缺省取自生产成本口径 MU14；剥离成本按册走 30）
    public double CoalPriceYuanT { get; set; } = ProductionCostBook.Current.CoalPriceYuanT;
    public double MiningCostYuanT { get; set; } = ProductionCostBook.Current.MiningCostYuanT;
    public double StripCostYuanM3 { get; set; } = ProductionCostBook.Current.StripCostYuanM3;
    public double DiscountRatePct { get; set; } = ProductionCostBook.Current.DiscountRatePct;

    // 求解产物
    public ObservableCollection<MiningPanel> Panels { get; set; } = new();
    public ProgramResult? Result { get; set; }

    public static double CapacityWanTaFrom(double lengthM, double rateMpa, double benchM, double rho = DefaultCoalDensity)
        => lengthM * rateMpa * benchM * rho / 1e4;
    public static double AdvanceRateFrom(double capacityWanTa, double lengthM, double benchM, double rho = DefaultCoalDensity)
        => (lengthM <= 0 || benchM <= 0) ? 0 : capacityWanTa * 1e4 / (lengthM * benchM * rho);

    public string AttitudeText => Attitude switch
    {
        DepositType.NearHorizontal => "水平/近水平", DepositType.GentleDip => "缓倾斜", DepositType.Inclined => "倾斜",
        DepositType.SteepDip => "急倾斜", DepositType.MultiSeam => "多煤层", DepositType.IrregularMassive => "不规则/块状", _ => "—"
    };
    public string StrategyText => Strategy switch
    {
        MiningStrategy.Strip => "条带", MiningStrategy.LongToCross => "纵采转横采", MiningStrategy.Bench => "台阶式", _ => "—"
    };
    public string SplitText => Split switch
    {
        SplitObjective.ByCapacity => "按产能均衡", SplitObjective.ByLife => "按服务年限均衡", SplitObjective.FixedN => $"固定 {PanelCount} 采区", _ => "—"
    };
    public string BoxcutModeText => BoxcutMode == BoxcutMode.Auto ? "全自动推荐" : "人工指定";
    public string SolvedText => Result == null ? "未求解" : "已求解";
    // 对比表显示列（原 DataGrid 绑 Result.X + StringFormat）
    public string RPanelCount => Result?.PanelCount.ToString() ?? "";
    public string RPeak => Result?.ProductionRatioPeak.ToString("F1") ?? "";
    public string RInner => Result?.InnerDumpPct.ToString("F0") ?? "";
    public string RTtc => Result?.TimeToCapacityYears.ToString("F0") ?? "";
    public string RLife => Result?.ServiceLifeYears.ToString("F0") ?? "";
    public string RBalance => Result?.ReserveBalanceCoef.ToString("F2") ?? "";
    public string RBasic => Result?.BasicStrippingYiM3.ToString("F1") ?? "";
    public string RScore => Result?.CompositeScore.ToString("F0") ?? "";

    public MiningProgramPlan Clone()
    {
        var c = (MiningProgramPlan)MemberwiseClone();
        c.Name = Name + "·副本";
        c.FirstWeights = FirstWeights.Copy();
        c.BoxcutWeights = BoxcutWeights.Copy();
        c.SourcePitResult = null;
        c.BoxcutOptions = new ObservableCollection<BoxcutAdvanceOption>();
        c.SelectedBoxcut = null;
        c.Panels = new ObservableCollection<MiningPanel>();
        c.Result = null;
        return c;
    }

    private static MiningPanel MakePanel(int order, string name, double coal, double waste,
        double ratio, double azimuth, double workLine, double advRate,
        DumpMode dump, double switchYear, double life) => new()
    {
        Index = order, Name = name, Order = order, CoalWanT = coal, WasteWanM3 = waste,
        StripRatio = ratio, AdvanceAzimuthDeg = azimuth, WorkingLineLengthM = workLine,
        AdvanceRateMpa = advRate, Dump = dump, DumpSwitchYear = switchYear, ServiceLifeYears = life
    };

    private static BoxcutAdvanceOption MakeBoxcut(string name, bool auto, string desc, double az,
        double workLine, double sr, double shallow, double haul, double inner, double wl, double geo,
        BoxcutWeights w, string rationale, AdvanceMode adv = AdvanceMode.Parallel)
    {
        var o = new BoxcutAdvanceOption
        {
            Name = name, IsAuto = auto, BoxcutDesc = desc, AdvanceAzimuthDeg = az, WorkingLineLengthM = workLine,
            SrScore = sr, ShallowScore = shallow, HaulScore = haul, InnerDumpScore = inner,
            WorkLineScore = wl, GeoScore = geo, Feasible = true, Rationale = rationale, AdvanceMode = adv
        };
        o.Recompute(w);
        return o;
    }

    /// <summary>骨架演示用样例方案（含求解结果 + 采区 + 拉沟·推进候选），与原版一致。</summary>
    public static ObservableCollection<MiningProgramPlan> CreateSamples()
    {
        var a = new MiningProgramPlan
        {
            Name = "方案A·3采区北推", Participate = true,
            Attitude = DepositType.NearHorizontal, Strategy = MiningStrategy.Strip,
            AutoDipDeg = 6, AutoStrikeDeg = 75,
            Split = SplitObjective.FixedN, PanelCount = 3,
            BenchHeightM = 12, TargetAdvanceRateMpa = 250, MaxAdvanceRateMpa = 320,
            Result = new ProgramResult
            {
                ProductionRatioPeak = 10.5, InnerDumpPct = 64, TimeToCapacityYears = 4,
                ServiceLifeYears = 28, ReserveBalanceCoef = 0.78, BasicStrippingYiM3 = 1.7,
                AvgHaulKm = 2.6, Npv = 123000, CompositeScore = 76, PanelCount = 3, Ok = true,
            }
        };
        a.Panels.Add(MakePanel(1, "采区1", 3600, 21600, 6.0, 0, 1100, 250, DumpMode.External, 0, 10));
        a.Panels.Add(MakePanel(2, "采区2", 3200, 25600, 8.0, 0, 1100, 250, DumpMode.Internal, 5, 10));
        a.Panels.Add(MakePanel(3, "采区3", 3050, 27450, 9.0, 0, 1100, 250, DumpMode.Internal, 11, 8));
        a.BoxcutOptions.Add(MakeBoxcut("南端拉沟·向北推", true, "南端最小剥采比脊线", 0, 1100, 0.85, 0.80, 0.70, 0.82, 0.95, 0.80, a.BoxcutWeights, "南端煤层浅、剥采比低；向北推背对采空区利内排"));
        a.BoxcutOptions.Add(MakeBoxcut("中部拉沟·双向推", true, "中部煤层露头", 15, 900, 0.70, 0.75, 0.78, 0.55, 0.85, 0.75, a.BoxcutWeights, "中部便于运输，但双向推内排空间受限"));
        MarkBest(a);

        var b = new MiningProgramPlan
        {
            Name = "方案B·4采区东推", Note = "推荐", Participate = true,
            Attitude = DepositType.NearHorizontal, Strategy = MiningStrategy.Strip,
            AutoDipDeg = 6, AutoStrikeDeg = 75,
            Split = SplitObjective.ByLife, PanelCount = 4, TargetServiceLifeYears = 30,
            BenchHeightM = 12, TargetAdvanceRateMpa = 300, MaxAdvanceRateMpa = 360,
            Result = new ProgramResult
            {
                ProductionRatioPeak = 8.2, InnerDumpPct = 72, TimeToCapacityYears = 3,
                ServiceLifeYears = 30, ReserveBalanceCoef = 0.91, BasicStrippingYiM3 = 1.4,
                AvgHaulKm = 2.1, Npv = 146000, CompositeScore = 87, PanelCount = 4, Ok = true,
            }
        };
        b.Panels.Add(MakePanel(1, "采区1", 2800, 16800, 6.5, 90, 900, 300, DumpMode.External, 0, 7));
        b.Panels.Add(MakePanel(2, "采区2", 2750, 20600, 7.5, 90, 900, 300, DumpMode.Internal, 3, 8));
        b.Panels.Add(MakePanel(3, "采区3", 2700, 21600, 8.0, 90, 900, 300, DumpMode.Internal, 11, 8));
        b.Panels.Add(MakePanel(4, "采区4", 2650, 20650, 7.8, 90, 900, 300, DumpMode.Internal, 19, 7));
        b.BoxcutOptions.Add(MakeBoxcut("东端拉沟·向西推", true, "东端最小剥采比角", 90, 900, 0.92, 0.85, 0.80, 0.88, 0.90, 0.82, b.BoxcutWeights, "东端剥采比最小+煤层浅，向西推背对采空区利尽早内排"));
        b.BoxcutOptions.Add(MakeBoxcut("南端拉沟·向北推", true, "南端煤层露头", 0, 820, 0.80, 0.78, 0.85, 0.70, 0.82, 0.78, b.BoxcutWeights, "南端靠工业场地运距短，但内排腾空较慢"));
        b.BoxcutOptions.Add(MakeBoxcut("低剥采比脊·扇形回转", true, "西南低剥采比脊", 45, 760, 0.88, 0.72, 0.62, 0.75, 0.70, 0.68, b.BoxcutWeights, "顺剥采比脊扇形回转削峰，但工作线长偏短、运输接口复杂", AdvanceMode.FixedPivot));
        MarkBest(b);

        var c = new MiningProgramPlan
        {
            Name = "方案C·2采区按年限", Participate = true,
            Attitude = DepositType.NearHorizontal, Strategy = MiningStrategy.Strip,
            AutoDipDeg = 6, AutoStrikeDeg = 75,
            Split = SplitObjective.ByLife, PanelCount = 2,
            BenchHeightM = 15, TargetAdvanceRateMpa = 200, MaxAdvanceRateMpa = 300,
            Result = new ProgramResult
            {
                ProductionRatioPeak = 10.8, InnerDumpPct = 48, TimeToCapacityYears = 5,
                ServiceLifeYears = 26, ReserveBalanceCoef = 0.70, BasicStrippingYiM3 = 2.1,
                AvgHaulKm = 3.1, Npv = 112000, CompositeScore = 61, PanelCount = 2, Ok = false,
            }
        };
        c.Panels.Add(MakePanel(1, "采区1", 5600, 39200, 7.0, 45, 1400, 200, DumpMode.External, 0, 14));
        c.Panels.Add(MakePanel(2, "采区2", 5200, 49400, 9.5, 45, 1400, 200, DumpMode.Internal, 14, 12));
        c.BoxcutMode = BoxcutMode.Manual; c.ManualAdvanceAzimuthDeg = 45;
        c.BoxcutOptions.Add(MakeBoxcut("人工·西南拉沟·向东北推", false, "人工指定(西南角)", 45, 1400, 0.70, 0.65, 0.60, 0.50, 0.95, 0.60, c.BoxcutWeights, "人工指定：采区少、工作线长，但内排率低、运距偏大"));
        MarkBest(c);

        return new ObservableCollection<MiningProgramPlan> { a, b, c };
    }

    /// <summary>把候选里总分最高且可行的标为推荐，并设为 SelectedBoxcut。</summary>
    public static void MarkBest(MiningProgramPlan p)
    {
        BoxcutAdvanceOption? best = null;
        foreach (var o in p.BoxcutOptions)
        {
            o.Recommended = false;
            if (o.Feasible && (best == null || o.TotalScore > best.TotalScore)) best = o;
        }
        if (best != null) { best.Recommended = true; p.SelectedBoxcut = best; }
    }
}

/// <summary>会话级共享：开采程序方案集（③ 配置窗建，④ 求解窗与中长远「来源开采程序」复用）。首次访问以样例初始化。</summary>
public static class MiningProgramStore
{
    private static ObservableCollection<MiningProgramPlan>? _schemes;
    public static ObservableCollection<MiningProgramPlan> Schemes => _schemes ??= MiningProgramPlan.CreateSamples();
    public static void Reset() => _schemes = null;
}
