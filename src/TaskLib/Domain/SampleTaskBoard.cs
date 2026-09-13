// 忠实移植自原 PitMine3D Modules/TaskLib/Domain/SampleTaskBoard.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

namespace PitMine3D.Kylin.TaskLib.Domain;

/// <summary>设备行清单项（甘特行顺序 / 显示名 / 类别 / 是否卡车子行）。</summary>
public sealed class RosterEntry
{
    public string EquipId { get; }
    public string Display { get; }
    public string Category { get; }
    public bool Sub { get; }
    public RosterEntry(string id, string display, string category, bool sub)
    { EquipId = id; Display = display; Category = category; Sub = sub; }
}

/// <summary>
/// 样例任务台账 = 引擎「输入(盘子)」+ 由 TaskExploder 算出的任务 + 实绩回灌。
///   · Config()  : 写死的输入（作业面目标 / 编组 / 工作历 / 检修 / 爆破）——真实接 ShortTerm + GeoDataBase 时只换这里。
///   · Day()     : 引擎裂解装箱产出的 ProductionTask[]（计划是算出来的，不是写死的）+ 实绩回灌。
///   · Violations: 引擎校核结果（运力 / 双占 / 欠产 / 接续 …）。
/// 甘特 / 报表 / 任务书 / 达成度 全部消费这一份。
///
/// ── 本轮升级：盘子带上完整的「采—运—排」语义 ──
///  ① 去向登记簿进盘子（<see cref="ExploderConfig.Sinks"/>），排土场/破碎站/煤仓不再是 UI 里的孤岛；
///  ② 采装面三条建模路径各留一份样例，引擎与 UI 都能被走到：
///     · **拆面**：主采面·东拆成成对的「采煤面 + 剥离面」，煤去破碎站、岩去内排场；
///     · **混采·求解分项**：辅采面·南（煤6∶岩4）不填去向，由 FlowAssigner 按运输功最小
///       逐物料定汇，解完写回 <see cref="FaceInput.Splits"/>——一条任务、多个去向；
///     · **混采·手工分项**：主采面·东（混采变体，煤5∶岩5）人工在 Splits 上逐物料写死去向，
///       求解器只按物料逐项锁定、吃容量而不改它。
///     后两条正是「同一台铲挖混采料，煤去破碎站、岩去排土场」的正解——拆成两条任务会撞「设备双占」；
///  ③ 表土单列一个剥离面 → 表土堆场，让「表土必须单独堆存供复垦」这条合规约束在样例里被真正走到；
///  ④ 排土面的日目标不再写死，改由入方物料流推导（DerivedFromInbound），采排从此守恒；
///  ⑤ 盘子装配顺序 = 月计划 → 定去向 → 定运距 → 定编组班产。顺序不可颠倒：
///     先有去向才有运距，先有运距才有循环时间 T_c，先有 T_c 才有配车数与编组班产，
///     而编组班产正是裂解装箱的 bin 大小。
/// </summary>
public static class SampleTaskBoard
{
    // ── 样例种子常量（Seed*）──────────────────────────────────────────────────
    //  只在「台账为空」时作为兜底盘子的自洽基准；真实期次一律走 IProjectContext。
    //  历史上这几个是 const 且被当成"当日"用，全仓 40 余处引用——故保留同名属性转发，
    //  调用方无需改动，拿到的却已是项目上下文里的真实期次。
    // ★ 星期几必须与日历一致：2026-06-17 是周三（原样例写的"周二"是笔误）。
    //   日期键只取空格前的片段，故这处笔误从不影响落盘与解析，但会出现在任务书抬头上，
    //   现在 ProjectPeriodFormat 按真实日历算星期，两边必须对得上。
    public const string SeedDateLabel = "2026-06-17 周三";
    public const string SeedIdPrefix = "D0617";
    public const double SeedNowHour = 10.5;
    public const double SeedBlastStart = 15.0;
    public const double SeedBlastEnd = 15.67;
    public const string SeedMineName = "示例露天矿";

    // ── 转发到当日盘子（ProductionPlanContext）──────────────────────────────
    //  本类从「唯一数据源」降级为「空库时的种子 + 兼容门面」：装配逻辑在
    //  TaskLib.Engine.ProductionPlanContext（台账优先、样例兜底、逐段标注来源）。
    public static string DateLabel => ProductionPlanContext.DateLabel;
    public static string IdPrefix => ProductionPlanContext.IdPrefix;
    public static double NowHour => ProductionPlanContext.NowHour;
    public static double BlastStart => ProductionPlanContext.BlastStart;
    public static double BlastEnd => ProductionPlanContext.BlastEnd;
    public static string MineName => ProductionPlanContext.MineName;

    /// <summary>盘子来源总文案（期次 · 班次 · 作业面 · 月计划 · 去向 · 运距 · 编组）。</summary>
    public static string SourceLabel => ProductionPlanContext.SourceLabel;

    /// <summary>月计划来源（ShortTermLink）。</summary>
    public static string PlanSourceLabel => ProductionPlanContext.PlanSourceLabel;
    /// <summary>去向来源（SinkRegistryLoader + FlowAssigner）。</summary>
    public static string SinkSourceLabel => ProductionPlanContext.SinkSourceLabel;
    /// <summary>运距来源（HaulResolver 三层兜底命中情况）。</summary>
    public static string HaulSourceLabel => ProductionPlanContext.HaulSourceLabel;
    /// <summary>编组来源（FleetMatcher 规则台账 + 求解面数）。</summary>
    public static string FleetSourceLabel => ProductionPlanContext.FleetSourceLabel;

    /// <summary>单据来源（已下达 / 已撤回 / **已排未下达** 各多少条）。</summary>
    public static string DispatchSourceLabel => ProductionPlanContext.DispatchSourceLabel;
    /// <summary>本盘单据计数。评价三窗报分母口径时用它，别各窗各数一遍。</summary>
    public static Engine.DispatchStateLink.Stat DispatchStat => ProductionPlanContext.DispatchStat;

    public static ExploderResult Result() => ProductionPlanContext.Result();
    public static List<ProductionTask> Day() => ProductionPlanContext.Day();
    public static List<PlanViolation> Violations() => ProductionPlanContext.Violations();

    /// <summary>注入外部计划（载入快照 / 落库回读）→ 后续 Day()/Violations() 即返回该计划。</summary>
    public static void SetSnapshot(ExploderResult r) => ProductionPlanContext.SetSnapshot(r);

    /// <summary>当日盘子（台账优先，样例兜底）。装配细节见 <see cref="ProductionPlanContext.Config"/>。</summary>
    public static ExploderConfig Config() => ProductionPlanContext.Config();

    /// <summary>在籍设备清单：设备台账优先，读不到回落 <see cref="SeedRoster"/>。</summary>
    public static List<RosterEntry> Roster() => ProductionPlanContext.Roster();

    // ── 设备在籍（甘特行）──────────────────────────────────────────────────
    //  一个作业面必须有一台**专属**主设备：TaskExploder 的任务 Id 是
    //  「前缀-主设备-班次」，两个面共用一台铲会撞 Id，还会被判「设备双占」。
    //  故煤岩分流后的成对面、表土面、两个排土面都各自配主设备。
    public static List<RosterEntry> SeedRoster() => new()
    {
        new RosterEntry("DR-1", "DR-1 钻机", "钻机", false),
        new RosterEntry("WK-10", "WK-10 电铲①（采煤）", "电铲", false),
        new RosterEntry("T-01", "T-01 卡车", "卡车", true),
        new RosterEntry("T-02", "T-02 卡车", "卡车", true),
        new RosterEntry("WK-12", "WK-12 电铲②（剥离）", "电铲", false),
        new RosterEntry("T-03", "T-03 卡车", "卡车", true),
        new RosterEntry("T-04", "T-04 卡车", "卡车", true),
        new RosterEntry("WK-14", "WK-14 电铲③（混采·求解分项）", "电铲", false),
        new RosterEntry("T-05", "T-05 卡车", "卡车", true),
        new RosterEntry("WK-16", "WK-16 电铲④（混采·手工分项）", "电铲", false),
        new RosterEntry("T-07", "T-07 卡车", "卡车", true),
        new RosterEntry("T-08", "T-08 卡车", "卡车", true),
        new RosterEntry("EX-1", "EX-1 液压铲（表土）", "电铲", false),
        new RosterEntry("T-06", "T-06 卡车", "卡车", true),
        new RosterEntry("BD-1", "BD-1 推土机①（内排）", "推土机", false),
        new RosterEntry("BD-2", "BD-2 推土机②（外排）", "推土机", false),
    };

    private static CoalQuality Q(double ash, double cv, double s, double m)
        => new() { AshPct = ash, CalorificMJkg = cv, SulfurPct = s, MoisturePct = m };

    // ── 样例矿区平面坐标（源端接线用）────────────────────────────────────────
    //  为什么样例也要给坐标：路网求运距要求【源汇两端都能定位到 RoadNode】。汇端 V036 补上了，
    //  源端（作业面）过去恒为 (0,0,0)，于是样例里每条腿都只能落兜底运距——而运距是
    //  循环时间 T_c 的输入，T_c 定配车数 n*、n* 定编组班产、编组班产是装箱的 bin。
    //
    //  量级对齐工程台账的矿区平面坐标系：V014 去掉 37 带号后经距 X≈6.2e5、纬距 Y≈4.38e6（m）。
    //  样例若用「300 / 60」这种局部小数，会教出「源坐标就填几百」的错觉，用户照着填进真台账
    //  就会离路网节点十万八千里（吸附半径 500m），运距照样落兜底。
    //  Z 一律用各自的台阶标高 / 排弃平台标高（与样例的 BenchElevationM 自洽）。
    private const double OX = 620000;    // 采场中心 X（样例基准点）
    private const double OY = 4376000;   // 采场中心 Y

    /// <summary>
    /// 样例去向的坐标。<see cref="SinkRegistry.Sample()"/> 是冻结契约（那份样例一个坐标都没有），
    /// 故坐标只能补在这里。摆位按「内排近、表土/破碎站中、外排与煤仓远」，
    /// 直线距离与各自的 <c>FallbackHaulKm</c> 相称（路网绕行系数约 1.2~1.3）——
    /// 这样样例的「源→汇」是一盘自洽的数，而不是随手摆的点。
    /// </summary>
    private static readonly Dictionary<string, (string Name, double X, double Y, double Z)> SampleSinkXyz =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["D-IN1"] = ("内排场", OX - 800, OY + 120, 68),      // 内排：采空区里，离采场最近（兜底 1.4km）
            ["TS-1"] = ("表土堆场", OX - 500, OY + 2000, 104),   // 表土堆场：北侧，紧邻表土剥离面（兜底 2.0km）
            ["CR-1"] = ("1号破碎站", OX + 2150, OY + 650, 92),   // 破碎站：东侧地表（兜底 2.6km）
            ["D-N1"] = ("北排土场", OX + 300, OY + 2700, 110),   // 外排：北端，最远的排弃场（兜底 3.2km）
            ["SL-1"] = ("原煤仓", OX + 3200, OY - 500, 88),      // 原煤仓：破碎站以东，工业场地（兜底 3.8km）
        };

    // 编组的 RecommendedTrucks / GroupCapacityM3PerH 只是「求解器不可用时」的兜底初值，
    // 正常路径下会被 FleetMatcher 按【物料密度 × 真运距 × 设备参数】解出的值覆盖。
    private static EquipmentGroup G1() => new()
    { MainEquipment = "WK-10", Trucks = new() { "T-01", "T-02" }, Aux = new() { "W-3 洒水车" }, RecommendedTrucks = 2, GroupCapacityM3PerH = 200 };
    private static EquipmentGroup G2() => new()
    { MainEquipment = "WK-12", Trucks = new() { "T-03", "T-04" }, RecommendedTrucks = 2, GroupCapacityM3PerH = 220 };
    private static EquipmentGroup G3() => new()
    { MainEquipment = "WK-14", Trucks = new() { "T-05" }, RecommendedTrucks = 2, GroupCapacityM3PerH = 200 };  // 配1荐2 → 运力不足
    private static EquipmentGroup G4() => new()
    { MainEquipment = "EX-1", Trucks = new() { "T-06" }, RecommendedTrucks = 1, GroupCapacityM3PerH = 160 };
    private static EquipmentGroup G5() => new()
    { MainEquipment = "WK-16", Trucks = new() { "T-07", "T-08" }, RecommendedTrucks = 2, GroupCapacityM3PerH = 190 };
    private static EquipmentGroup GDoz1() => new()
    { MainEquipment = "BD-1", RecommendedTrucks = 0, GroupCapacityM3PerH = 250 };
    private static EquipmentGroup GDoz2() => new()
    { MainEquipment = "BD-2", RecommendedTrucks = 0, GroupCapacityM3PerH = 220 };

    /// <summary>
    /// 样例种子盘子：作业面（源·物料·汇）+ 编组 + 三班工作历 + 检修 + 爆破 + 配煤标准。
    ///
    /// <para>
    /// 它**只是空库时的兜底**，不再是"当日盘子"本身——期次、班次、爆破窗口、作业面
    /// 一旦台账里有，装配器（<see cref="ProductionPlanContext.Config"/>）就会逐段用台账替换它。
    /// 四步接线（月计划 → 去向 → 运距 → 编组）也已移到装配器，本方法只返回纯数据，不做求解。
    /// </para>
    /// </summary>
    internal static ExploderConfig SeedConfig()
    {
        var cfg = new ExploderConfig
        {
            DateLabel = SeedDateLabel, IdPrefix = SeedIdPrefix, NowHour = SeedNowHour,
            BlastStart = SeedBlastStart, BlastEnd = SeedBlastEnd,
            Shifts = { new ShiftWindow("早班", 0, 8), new ShiftWindow("中班", 8, 16), new ShiftWindow("夜班", 16, 24) },

            Faces =
            {
                // ── 主采面·东：煤岩分流的一对面 ───────────────────────────────
                //  露天煤矿的常态是「上台阶剥离、下台阶采煤」，两者物料不同、去向不同、
                //  运距不同、配车数也不同。FaceInput 是单去向结构，把它压成一个
                //  「煤7∶岩3」的面就只能填一个去向，剥离量的去处会凭空消失。
                new FaceInput
                {
                    Zone = "主采面·东（剥离）", BenchElevationM = 72, EngineeringPositionId = "EP-12R",
                    // 源坐标 = 铲位代表点；Z 取本面台阶标高。有它路网才定得到源节点（见 HaulResolver）
                    SourceX = OX + 320, SourceY = OY + 140, SourceZ = 72,
                    Material = "硬岩", MaterialCode = MaterialCatalog.Rock,
                    DestinationId = "D-IN1", DestinationName = "内排场", DestinationKind = SinkKind.InternalDump,
                    DayTargetM3 = 1050, Group = G2(), Process = ProcessType.Load,
                },
                new FaceInput
                {
                    Zone = "主采面·东（采煤）", BenchElevationM = 60, EngineeringPositionId = "EP-12C",
                    SourceX = OX + 300, SourceY = OY + 40, SourceZ = 60,
                    Material = "煤", MaterialCode = MaterialCatalog.Coal,
                    DestinationId = "CR-1", DestinationName = "1号破碎站", DestinationKind = SinkKind.Crusher,
                    DayTargetM3 = 2450, Quality = Q(12, 22, 0.6, 8), Group = G1(), Process = ProcessType.Load,
                },

                // ── 辅采面·南：真·混采面（求解分项这条路径）────────────────────
                //  一个面同时出煤和岩，**不填去向**，交 FlowAssigner 按 MaterialMix 拆成
                //  多份供给再各自定汇（煤→破碎站/煤仓、岩→排土场），按运输功最小求解，
                //  解完把两条 MaterialDestination 写回 face.Splits——一条任务、多个去向。
                new FaceInput
                {
                    Zone = "辅采面·南（混采）", BenchElevationM = 48, EngineeringPositionId = "EP-08",
                    SourceX = OX - 80, SourceY = OY - 430, SourceZ = 48,
                    Material = "煤6∶岩4", Mix = MaterialMix.Parse("煤6∶岩4"),
                    DayTargetM3 = 2800, Quality = Q(14, 20.5, 0.8, 9), Group = G3(), Process = ProcessType.Load,
                },

                // ── 主采面·东（混采变体）：手工指定分项这条路径 ────────────────
                //  与上一个面互补：去向**不交求解器**，人工在 Splits 上逐物料写死
                //  （煤→CR-1 破碎站、岩→D-IN1 内排场）。FlowAssigner 按物料逐项锁定、
                //  只吃容量不参与优化；运距人工给了就采信，没给才回落三层兜底。
                //  这条路径覆盖的是现场最常见的做法：调度心里早有定数，系统只需别把它算丢。
                new FaceInput
                {
                    Zone = "主采面·东（混采·手工分项）", BenchElevationM = 66, EngineeringPositionId = "EP-12M",
                    SourceX = OX + 350, SourceY = OY - 60, SourceZ = 66,
                    Material = "煤5∶岩5", Mix = MaterialMix.Parse("煤5∶岩5"),
                    DayTargetM3 = 1600, Quality = Q(12.5, 21.5, 0.65, 8.5), Group = G5(), Process = ProcessType.Load,
                    Splits =
                    {
                        new MaterialDestination
                        {
                            MaterialCode = MaterialCatalog.Coal, Fraction = 0.5,
                            DestinationId = "CR-1", DestinationName = "1号破碎站", DestinationKind = SinkKind.Crusher,
                            HaulKm = 2.6,
                        },
                        new MaterialDestination
                        {
                            MaterialCode = MaterialCatalog.Rock, Fraction = 0.5,
                            DestinationId = "D-IN1", DestinationName = "内排场", DestinationKind = SinkKind.InternalDump,
                            HaulKm = 1.4,
                        },
                    },
                    // 单去向五字段 = 主去向（份额并列时取物料构成里的头一项），供单据/甘特兼容显示
                    DestinationId = "CR-1", DestinationName = "1号破碎站", DestinationKind = SinkKind.Crusher,
                    HaulDistanceKm = 2.6,
                },

                // ── 表土剥离面·北：合规约束的样例载体 ─────────────────────────
                //  表土是复垦资源，MaterialSpec 只允许 TopsoilYard；这一面让
                //  「表土必须单独堆存」在流向分配、时空校核、台账下拉框里都被真正走到。
                new FaceInput
                {
                    Zone = "表土剥离面·北", BenchElevationM = 96, EngineeringPositionId = "EP-05",
                    SourceX = OX - 40, SourceY = OY + 520, SourceZ = 96,
                    Material = "表土", MaterialCode = MaterialCatalog.Topsoil,
                    DestinationId = "TS-1", DestinationName = "表土堆场", DestinationKind = SinkKind.TopsoilYard,
                    DayTargetM3 = 1200, Group = G4(), Process = ProcessType.Load,
                },

                // ── 排土面：日目标由入方推导，不写死 ──────────────────────────
                //  DerivedFromInbound=true ⇒ 引擎按「本期投向该汇的占容方」覆盖 DayTargetM3。
                //  写死 4000 的老做法会让采排两侧各说各话，库容预警与内排率全都失真。
                //  北排土场今日可能推导为 0（内排优先接完全部剥离）——那正是「内排率 100%」
                //  这一事实的诚实呈现，而不是给它硬塞一个数。
                //  排土面的源就是排土场自己（推土机在场内推排），故源坐标与同名去向重合。
                //  重合 ⇒ 路网的源汇是同一个节点，运距解不出来 —— 那是对的：场内推排没有"运距"
                //  可言，如实落到兜底层，而不是给它编一个几公里的数。
                new FaceInput
                {
                    Zone = "内排场", Material = "硬岩", MaterialCode = MaterialCatalog.Rock,
                    SourceX = OX - 800, SourceY = OY + 120, SourceZ = 68,
                    DestinationId = "D-IN1", DestinationName = "内排场", DestinationKind = SinkKind.InternalDump,
                    DerivedFromInbound = true, Group = GDoz1(), Process = ProcessType.Dump,
                },
                new FaceInput
                {
                    Zone = "北排土场", Material = "硬岩", MaterialCode = MaterialCatalog.Rock,
                    SourceX = OX + 300, SourceY = OY + 2700, SourceZ = 110,
                    DestinationId = "D-N1", DestinationName = "北排土场", DestinationKind = SinkKind.ExternalDump,
                    DerivedFromInbound = true, Group = GDoz2(), Process = ProcessType.Dump,
                },
            },
            Drills = { new DrillInput { EquipId = "DR-1", Zone = "待爆区B", BenchElevationM = 72, Start = 1, End = 8 } },
            Maintenance = { new MaintenanceWindow { EquipId = "WK-10", Start = 0, End = 2, Label = "检修" } },
            Blend = new BlendStandard { MaxAshPct = 12.8, MinCalorificMJkg = 21.5, MaxSulfurPct = 0.7 },
        };

        return cfg;
    }

    /// <summary>
    /// 去向登记簿：走 <see cref="SinkRegistryLoader"/>（内部已是「DB → 样例」两层兜底）。
    /// 用 Current 而非每次 Load()：Current 首次访问即调 Load()，之后复用同一份登记簿，
    /// 这样实绩回灌累加的 FilledM3 不会因为别的窗口再取一次盘子就被清掉。
    /// 台账真改了由 UI 的「刷新」调 Invalidate() 强制重读。
    /// </summary>
    internal static SinkRegistry LoadSinks()
    {
        try
        {
            var reg = SinkRegistryLoader.Current;
            if (reg != null && reg.All.Count > 0)
            {
                // 装载器读不到台账时它自己也会回落 SinkRegistry.Sample()，那一份同样需要补坐标
                if (IsSampleRegistry()) FillSampleSinkXyz(reg);
                return reg;
            }
        }
        catch { /* DB/服务未就绪 → 落样例 */ }

        var sample = SinkRegistry.Sample();
        FillSampleSinkXyz(sample);
        return sample;
    }

    /// <summary>登记簿是不是「样例去向」那一份（装载器读不到台账时的回落）。</summary>
    private static bool IsSampleRegistry()
    {
        try { return SinkRegistryLoader.LastSourceLabel.StartsWith("样例去向", StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>
    /// 给【样例】去向补坐标（真台账的坐标是权威，程序绝不覆盖）。
    /// 三道闸一起把关，免得把样例坐标糊到真数据头上——那会算出一个看着正常的假运距：
    ///  ① 只在登记簿确实是样例那一份时才跑；
    ///  ② 只补 X、Y 均为 0（= 未录坐标）的去向；
    ///  ③ 编号与名称都要对上样例那五个（真台账万一撞了编号，名字也撞的概率可忽略）。
    /// </summary>
    private static void FillSampleSinkXyz(SinkRegistry? reg)
    {
        if (reg == null) return;
        foreach (var s in reg.All)
        {
            if (s == null) continue;
            if (Math.Abs(s.X) > 1e-9 || Math.Abs(s.Y) > 1e-9) continue;              // ② 已有坐标，不动
            if (!SampleSinkXyz.TryGetValue(s.Id ?? "", out var p)) continue;         // ③ 编号
            if (!string.Equals((s.Name ?? "").Trim(), p.Name, StringComparison.Ordinal)) continue;   // ③ 名称
            s.X = p.X; s.Y = p.Y; s.Z = p.Z;
        }
    }

    /// <summary>
    /// 实绩回灌（**样例合成，仅用于种子盘子**）：现在之前完成、跨现在执行中、之后计划；
    /// WK-10 中班故障 + 运力不足致欠产。返回一句来源文案。
    ///
    /// <para>
    /// ★ 只有当作业面来自样例种子时才允许调用（由 <see cref="ProductionPlanContext"/> 把关）。
    /// 盘子来自真实台账时一律改读 <c>actuals/</c> 里录入的实绩，没录就保持"计划"状态——
    /// 在真台账上合成实绩就是编数据。
    /// </para>
    /// </summary>
    internal static string ApplySampleActuals(List<ProductionTask> tasks)
    {
        double now = ProductionPlanContext.NowHour;
        foreach (var t in tasks)
        {
            if (t.Process == ProcessType.Idle) continue;
            bool isLoad = t.Process == ProcessType.Load;
            bool wk10Running = t.Group.MainEquipment == "WK-10" && isLoad && t.StartHour < now && t.EndHour > now;

            if (t.EndHour <= now)        // 已完成
            {
                t.Status = TaskStatus.Done;
                t.ActualVolumeM3 = Math.Round(t.TargetVolumeM3 * 0.98);
                t.ActualHours = Math.Round(t.PlannedHours * 0.98, 1);
                t.TrucksOnSite = t.Group.Trucks.Count;
                if (isLoad && t.QualityTarget != null) t.QualityActual = Near(t.QualityTarget);
            }
            else if (t.StartHour < now)  // 执行中
            {
                t.Status = TaskStatus.Running;
                double frac = (now - t.StartHour) / Math.Max(1e-6, t.EndHour - t.StartHour);
                double eff = wk10Running ? 0.85 : 0.97;
                t.ActualVolumeM3 = Math.Round(t.TargetVolumeM3 * frac * eff);
                t.ActualHours = Math.Round((now - t.StartHour) * 0.92, 1);
                t.TrucksOnSite = wk10Running ? 1 : t.Group.Trucks.Count;
                if (isLoad && t.QualityTarget != null) t.QualityActual = Near(t.QualityTarget);
                if (wk10Running) t.Reasons = new() { IncompleteReason.Fault, IncompleteReason.TruckShortage };
            }
            // 之后：计划，无实绩

            FillSink(t);
        }
        return $"实绩：样例合成（截至 {now:0.#} 时；台账模式下改读真实录入）";
    }

    /// <summary>
    /// 实绩顺带扣去向库容——传的是【排弃占容方 V容 = V实×Kr】，不是实方也不是松方。
    /// <para>
    /// 只按**采装侧**（Process==Load）计：排土面的日目标本身就是由入方推导来的，
    /// 两侧都扣会把同一批料记两遍；且排土面的 TargetVolumeM3 已是占容口径，
    /// 再乘一次 Kr 更是错上加错。
    /// </para>
    /// <para>
    /// 只对**排弃类**去向计：破碎站/煤仓是通过型去向，卸多少走多少、不占库容，
    /// 给它累加「已填」会让去向台账的充填率变成一个没有意义的数。
    /// </para>
    /// <para>
    /// 逐**物料分项**扣：混采任务煤进破碎站、岩进排土场，只按主去向扣的话，
    /// 主去向恰是破碎站的混采面（"煤5∶岩5 主去向 CR-1"）会把岩的整份占容漏掉，
    /// 排土场充填率就此长期偏低。走 ToFlows 即每条流各自认自己的汇。
    /// </para>
    /// </summary>
    private static void FillSink(ProductionTask t)
    {
        if (t.Process != ProcessType.Load) return;
        if (t.ActualVolumeM3 <= 1e-6) return;

        foreach (var fl in t.ToFlows(DateLabel, t.ActualVolumeM3))
        {
            if (!fl.SinkKind.IsDumping() || string.IsNullOrWhiteSpace(fl.SinkId)) continue;
            if (fl.DumpM3 <= 1e-6) continue;
            try { SinkRegistryLoader.AddFilled(fl.SinkId, fl.DumpM3); }
            catch { /* 写库/登记簿失败不影响派工 */ }
        }
    }

    private static CoalQuality Near(CoalQuality q)
        => new() { AshPct = q.AshPct + 0.3, CalorificMJkg = q.CalorificMJkg - 0.2, SulfurPct = q.SulfurPct + 0.02, MoisturePct = q.MoisturePct + 0.1 };
}
