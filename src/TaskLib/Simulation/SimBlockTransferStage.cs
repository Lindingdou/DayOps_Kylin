// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimBlockTransferStage.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  块体搬运 —— 采场挖出来的每个采掘单元，沿运输线走到排土场并堆上去
//
//  这是「短期月度模拟」里唯一能把**采—运—排是一件事**演出来的那一层：
//  单元体那一层只会「消失/出现」（逐帧切可见性），看不出料去了哪儿；
//  车流那一层只有点在跑，看不出挖了多少堆了多少。本层把两头接起来。
//
//  ── R-T1 三段生命，三个体积口径（不是同一个数）─────────────────────────────
//  一个单元在期内走三段，每段用它**自己的**体积口径 —— 这正是本仓库的三口径铁律：
//     ① 采出段  源位置的块**沿推进方向收缩**（工作面后退）   口径 = 实方 V实
//     ② 搬运段  一个料块沿运输线从源走到汇                    口径 = 松方 V松 = V实×Ks
//     ③ 堆填段  汇位置的块**沿高度长高**（逐层堆填）          口径 = 占容方 V容 = V实×Kr
//  三段的体积**本来就不相等**（Ks≈1.25、Kr≈1.13），这不是"搬运途中丢了料"。
//  图例必须写明，否则一定会被当成 bug 报回来。
//  两个方向也不是随手选的：采场是工作面沿推进方向后退、排土是逐层往上堆，
//  各自缩/长的那一维就是它真实在变的那一维。
//
//  ── R-T2 开工时刻按推进序错开，**不随机**───────────────────────────────────
//  本期 N 个单元并行 = 多点采剥。各单元按台账 Seq（推进序）在期内错峰开工；
//  没有 Seq 的按 UnitId 序数补位。**绝不用随机数** —— 随机会让两次播放不一样，
//  方案对比时"哪个先开工"这种问题就永远说不清。
//
//  ── R-T3 搬运段是【展示时长】，不是真实运距/车速的比例 ─────────────────────
//  真实占比 = t_haul / 期作业小时 ≈ (3km/25kph=7min) / 600h ≈ 0.02% —— 直接用它块会瞬移。
//  所以搬运段在期内的占比是一个**显式的展示参数**，摆在界面上，图例写明
//  「搬运段是展示时长；真实运距/坡度见【路线标注】，真实车流强度见【车流】」。
//  ⇒ 这一层演的是**物料去向与时序**，不是行车时间。行车时间那一层已经有了（车流，Little 定律）。
//
//  ── R-T4 排土累积不清 ────────────────────────────────────────────────────
//  到达的块留在汇位置并逐期累积 —— 这就是「排土的整个过程」。
//  不累积的话每期只能看到一堆孤立的块，看不出排土场是怎么长起来的。
//
//  ── R-T5 一个单元一个块，不是一车一块 ────────────────────────────────────
//  一个采掘单元一个月的量是几千车次，不是一整块搬过去的。块体是**批次示意**：
//  量的口径是真的（三段各自的体积都按台账算），"一整块飞过去"是显示手法。
//  要看逐车，那是车流那一层的事。这条同样写进图例。
//
//  ── R-T8 两侧的朝向【各读各的】，一个角度不许跨侧复用 ──────────────────────
//  源块按【采场采掘单元】的走向方位摆，堆填块按【排土位置自己】的走向方位摆
//  （<see cref="SimBlockUnit.DumpAzimuthDeg"/>）。排土条带走的是排土场自己那圈台阶线，
//  与采场台阶线没有任何关系 —— 基表实测 446 笔能解出去向的流，两者
//  **中位差 45.1°、51% 超过 45°、最大 89.6°**。本层曾长期让堆填块沿用源块的朝向，
//  于是排土块与「排土条带」建出来的体整体拧着，而位置、体积、颜色、命中率全都正常。
//  读不到就轴对齐 + 明账计数（DumpAxisDefault），**不拿另一侧的角度冒充**。
//
//  ── 通道 ─────────────────────────────────────────────────────────────────
//  走批量 overlay（<see cref="SimDynamicOverlay"/>），线框，不入库、不占 Undo。
//  实体那条路走不了：IEntityCapability 没有变换矩阵，块体动不了（EquipmentStage 文件头已核过）。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>一个采掘单元在本层的输入（窄契约，不吃 MineAssLib/BlockModelLib 的类型）。</summary>
public sealed class SimBlockUnit
{
    public string UnitId = "";
    /// <summary>煤流（true）/ 岩·表土流（false）。决定物料色。</summary>
    public bool IsCoal;
    /// <summary>推进序（同一作业点内的先后）。&lt;=0 视为没填，按 UnitId 序数补位。</summary>
    public int Seq;

    /// <summary>
    /// 采剥这个单元的设备号（作业点身份）。<b>空 = 没有设备指派</b>，
    /// 此时按 <see cref="SimBlockTransferParams.WorkPoints"/> 把单元切成若干作业点，并留条说明。
    /// </summary>
    public string MachineId = "";

    // ── 源（采场）──
    public double Sx, Sy, Sz;
    /// <summary>走向长 / 推进宽 / 厚度 m。任一 &lt;=0 = 几何不全，不画。</summary>
    public double LenM, WidM, ThkM;
    /// <summary>走向方位角（度，北起顺时针）。<b>null = 不知道</b>，按轴对齐摆并计数。</summary>
    public double? AzimuthDeg;

    // ── 汇（排土位置 / 卸点）──
    public double Dx, Dy, Dz;
    /// <summary>排土位置尺寸 m。任一 &lt;=0 时按源尺寸推（并留条）。</summary>
    public double DumpLenM, DumpWidM, DumpThkM;

    /// <summary>
    /// <b>排土位置自己的</b>走向方位角（度，北起顺时针）。<b>null = 不知道</b>，按轴对齐摆并计数。
    ///
    /// <para><b>为什么必须单独有一个，不能沿用 <see cref="AzimuthDeg"/></b>：那一个是【源采场单元】的走向。
    /// 排土条带走的是排土场自己那圈台阶线，与采场台阶线没有任何关系 ——
    /// 基表实测 446 笔能解出去向的流，两者中位差 <b>45.1°</b>、51% 超过 45°、最大 89.6°。
    /// 沿用源方位的后果是排土块与「排土条带」建出来的体整体拧着，
    /// 而位置、体积、颜色全都正常，没有任何一个量看得出来。</para>
    ///
    /// <para>它就在采掘单元台账的排土行上（<c>走向方位°</c> 列，由条带自己的坡顶/坡底轨算得，
    /// 与喂给内核建条带体的是同一对轨）—— 喂数据的一侧照着排土行读即可。</para>
    /// </summary>
    public double? DumpAzimuthDeg;
    public string DestinationName = "";
    public bool IsInternalDump;

    // ── 量（三个口径，各段各用各的；&lt;=0 = 没给）──
    public double InSituM3;
    public double LooseM3;
    public double CapacityM3;

    /// <summary>源→汇折线 [x,y,z,...]（复用运输线那一份，不重新问路）。不足 2 点 = 没路径。</summary>
    public double[] Xyz = Array.Empty<double>();
    public bool HasRoute => Xyz.Length >= 6 && Xyz.Length % 3 == 0;

    /// <summary>
    /// 本条路线是**源→汇直连示意**，不是路网解出来的真路径。
    /// <para>由本舞台在没有真路径时自己合成（见 <see cref="SimBlockTransferStage"/> 的 R-T6）。
    /// 画成虚线、压暗，并在图例/状态栏里明说 —— 里程、坡度一概不由它派生。</para>
    /// </summary>
    public bool RouteIsStraight;
    /// <summary>路线折点数。</summary>
    public int PointCount => Xyz.Length / 3;

    public bool GeometryOk => LenM > 1e-6 && WidM > 1e-6 && ThkM > 1e-6;
    public bool HasSourcePos => Math.Abs(Sx) > 1e-6 || Math.Abs(Sy) > 1e-6;
    public bool HasDestPos => Math.Abs(Dx) > 1e-6 || Math.Abs(Dy) > 1e-6;
}

/// <summary>展示参数。每一个都是「替用户做的决定」，都摆到界面上。</summary>
public sealed class SimBlockTransferParams
{
    /// <summary>
    /// 没有设备指派时，把单元切成几个作业点（= 同时在采的点数）。
    /// <b>有设备指派时本参数不生效</b> —— 那时点数就是设备台数，不是猜的。
    /// </summary>
    public int WorkPoints { get; set; } = 4;

    /// <summary>一个块段的三段占**它自己那一格**的比例（三者之和会被归一化）。</summary>
    public double ExtractFrac { get; set; } = 0.45;
    public double TransferFrac { get; set; } = 0.20;
    public double DumpFrac { get; set; } = 0.35;

    /// <summary>排土块逐期累积（<see cref="SimBlockTransferStage.Rebuild"/> 之间不清）。</summary>
    public bool AccumulateDump { get; set; } = true;

    /// <summary>整体抬升 m（避 z-fight）。不改任何量。</summary>
    public double LiftM { get; set; } = 1.0;

    /// <summary>一帧最多推多少段。超了按体积从大到小截取并报出舍掉数。</summary>
    public int MaxSegments { get; set; } = 20000;

    /// <summary>搬运中的料块尺寸相对源块的比例（一整块太大会糊住路线）。</summary>
    public double CarryScale { get; set; } = 0.45;

    /// <summary>在采剥点上画设备符号（每个作业点一台，跟着当前块走）。</summary>
    public bool ShowShovels { get; set; } = true;
    /// <summary>设备符号大小相对块段宽度的比例。界面可调（现场反馈"图标太小"）。</summary>
    public double ShovelScale { get; set; } = 4.2;
    /// <summary>在排土场画推土机（堆填段推平）。</summary>
    public bool ShowDozers { get; set; } = true;
    /// <summary>推土机符号大小相对排土块宽度的比例。</summary>
    public double DozerScale { get; set; } = 2.4;

    /// <summary>设备铭牌字高（世界米）。</summary>
    public double TagHeightM { get; set; } = 32;
    /// <summary>在途卡车符号大小倍率。</summary>
    public double TruckScale { get; set; } = 3.4;

    /// <summary>
    /// 每条**在用线路**上常驻几辆卡车（沿线均匀分布、随相位前进）。0 = 只在搬运那一瞬间画。
    ///
    /// <para>为什么要常驻：搬运段只占每个块段时长的 20%，只在那一瞬间画的话，
    /// 任一时刻图上几乎看不到车 —— 而"这条路上有没有车"恰恰是这张图最该回答的问题之一。
    /// 常驻的车表达的是**这条线路本期在被使用**，不是"此刻正好有这几台车在这儿"
    /// （逐车口径在【车流】那一层，按 Little 定律定强度）。图例写明。</para>
    /// </summary>
    /// <para>默认 1：76 条在用线路 × 3 辆 = 228 台，图上糊成一片（现场反馈"车太多了"）。
    /// 一条线一辆已经足够表达"这条路在用"，要密就调这里 —— 密度的真口径在【车流】那一层。</para>
    public int TrucksPerRoute { get; set; } = 2;

    /// <summary>
    /// 没有路网路径时，是否用**源→汇直连**替代（虚线示意）。<b>默认关</b>。
    ///
    /// <para>现场口径（2026-08-10 拍板）：<b>线路只画路网寻径的结果</b>。
    /// 直连虽然标了虚线、也不报里程，但它在图上仍然是一条"料这样走"的线 ——
    /// 而那条线在路网上并不存在。演示里出现它，等于把「这个 O-D 走不通」
    /// 悄悄换成了「它走这条直线」，两件事的工程含义完全不同。</para>
    ///
    /// <para>开关留着而不是把代码删掉：路网还没建好时，用它能先看清"料的去向配对"，
    /// 那是另一个用途（对位关系），不是运输线路。要用就显式打开，并且知道自己在看什么。</para>
    /// </summary>
    public bool AllowStraightLink { get; set; }

    /// <summary>
    /// 路线两端与块体质心的容差 m：超过它就补一段**接入段**，把路线锚到块体上（R-T7）。
    /// 缺省 30m —— 比路网吸附半径（缺省 200m）小一个量级，所以真正贴着路的块不会被补。
    /// </summary>
    public double AnchorTolM { get; set; } = 30.0;

    /// <summary>
    /// 接入段的**上限** m：块体离最近可用路网入口超过它，就**不画接入段**。
    ///
    /// <para>为什么要封顶：就近汇入按连通分量找入口，搜索半径 1500m —— 够得着的入口
    /// 可能在一公里外，那一段接入画出来就是一条**长直线**，看着像"块体直连过去"。
    /// 而它在路网上并不存在（现场反馈"还是用直线连的，这个不对"）。</para>
    ///
    /// <para>不画不等于不说：超限的逐笔计数并报出来 —— **那是"这个块体没有路通到它"的
    /// 真实信息**，不该被一条画出来的线掩盖掉。</para>
    /// </summary>
    public double AnchorMaxM { get; set; } = 150.0;

    /// <summary>
    /// 画**所有**块体的规划线路（常驻底图）。<b>默认关</b>。
    ///
    /// <para>本期 76 个块体各有一条路线，它们本来就沿着路网走 —— 全画出来就是**一张路网的样子**，
    /// 而且永远铺在那儿，于是"哪条线对应哪个块"根本分不出来
    /// （现场反馈："线路不是逐个块体对应的"、"高亮始终是静态的"）。</para>
    ///
    /// <para>关掉之后图上只剩**正在作业的那几个块**的线路（每个采剥点一条）——
    /// 「动哪个块体，亮哪条线路」这个对应关系才成立。要看全局计划再打开。</para>
    /// </summary>
    public bool ShowPlannedRoutes { get; set; }

    /// <summary>三段之和（用于归一化，本身不是期内比例）。</summary>
    public double SpanFrac => Math.Max(1e-6, ExtractFrac + TransferFrac + DumpFrac);
}

/// <summary>一次重建的结论。</summary>
public sealed class SimBlockTransferResult
{
    public string PeriodKey { get; internal set; } = "";

    public int UnitTotal { get; internal set; }
    public int UnitNoGeometry { get; internal set; }
    public int UnitNoSourcePos { get; internal set; }
    public int UnitNoRoute { get; internal set; }
    public int UnitUsed { get; internal set; }
    /// <summary>规划线路画了多少段（常驻底图）。</summary>
    public int PlannedRouteSegments { get; internal set; }
    /// <summary>拿到**真路网路径**的块段数（状态栏要显示它 —— 这是"线路有没有用上"的唯一硬指标）。</summary>
    public int UnitRealRoute { get; internal set; }
    /// <summary>没有真路径、用**直连示意**替代的个数（R-T6）。</summary>
    public int UnitStraightLink { get; internal set; }
    /// <summary>走向不知道、按轴对齐摆的个数（形态为近似）。</summary>
    public int UnitAxisDefault { get; internal set; }
    /// <summary>排土尺寸没给、按源尺寸推的个数。</summary>
    public int DumpSizeDerived { get; internal set; }
    /// <summary>
    /// <b>排土位置</b>的走向方位没给、堆填块按轴对齐摆的个数。
    /// <para>与 <see cref="UnitAxisDefault"/> 分开数：那一个说的是采场块，两者的来源不是一回事，
    /// 合成一个数就看不出"排土这一侧到底读没读到自己的方位"。</para>
    /// </summary>
    public int DumpAxisDefault { get; internal set; }

    /// <summary>每笔都必须落进且只落进一个桶。<b>无路径的仍算 Used</b>（它只是少演一段）。</summary>
    public bool Balanced =>
        UnitTotal == UnitNoGeometry + UnitNoSourcePos + UnitUsed;

    /// <summary>本期并行在采的单元数（相位网格上的最大同时数）。</summary>
    public int ConcurrentPeak { get; internal set; }
    /// <summary>作业点数 = 同时推进的采剥点（有设备指派时 = 设备台数）。</summary>
    public int WorkPointCount { get; internal set; }
    /// <summary>最长的那个作业点这个月要啃几个块段（= 期内被切成几格）。</summary>
    public int LongestPointBlocks { get; internal set; }
    /// <summary>作业点是怎么来的（设备指派 / 界面给的点数）。</summary>
    public string PointSource { get; internal set; } = "";

    /// <summary>累计已堆到排土场的块数（含往期）。</summary>
    public int DumpedTotal { get; internal set; }

    public double InSituTotalM3 { get; internal set; }
    public double LooseTotalM3 { get; internal set; }
    public double CapacityTotalM3 { get; internal set; }

    public List<string> Notes { get; } = new();
    public string Summary { get; internal set; } = "";
    public string LegendText { get; internal set; } = "";

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine(LegendText);
        foreach (var n in Notes)
            sb.AppendLine(n.StartsWith("◆", StringComparison.Ordinal) || n.StartsWith("·", StringComparison.Ordinal) ? n : "· " + n);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 块体搬运舞台。<b>永不抛</b>：算不出来就是「不画 + 说清楚」。
/// </summary>
public sealed class SimBlockTransferStage
{
    public const string PitGroup = "simblock.pit";      // 采场侧正在被挖的块
    public const string CarryGroup = "simblock.carry";  // 在途料块
    public const string DumpGroup = "simblock.dump";    // 排土场已堆上去的块
    public const string ShovelGroup = "simblock.shovel"; // 采剥点上的设备（跟着当前块走）
    public const string ShovelTagGroup = "simblock.shoveltag";
    /// <summary>实心面（落地端支持时用；不支持就退线框）。</summary>
    public const string FaceGroup = "simblock.faces";
    /// <summary>设备的实心面 —— <b>必须与块体分组</b>，见 PushShovels 里那段注释。</summary>
    public const string EquipFaceGroup = "simblock.equipfaces";
    /// <summary>本期**每个块体的规划线路**（常驻底图，压暗）。</summary>
    public const string PlannedRouteGroup = "simblock.plan";
    /// <summary>本期**正在搬运**的那些块各自的线路（高亮，带进度）。</summary>
    public const string ActiveRouteGroup = "simblock.route";
    /// <summary>在途卡车（沿本块自己的路线跑）。</summary>
    public const string TruckGroup = "simblock.truck";

    private readonly ISimDynamicOverlay _sink;
    private readonly EquipWireBuffer _wire = new();
    private readonly EquipWireBuffer _shovelWire = new();
    /// <summary>
    /// 设备专用的面缓冲。
    /// <para>★ 踩过的坑：设备原来往块体的 <c>_mesh</c> 里写，而那个缓冲**在调 PushShovels 之前
    /// 就已经推给落地端了** —— 推完才填，设备那批三角一帧都没到过屏幕上，下一帧 Clear 就没了。
    /// 现象是"设备不见了"，而代码看上去每一句都对。各用各的缓冲、各推各的组，这类顺序坑就不存在。</para>
    /// </summary>
    private readonly EquipMeshBuffer _equipMesh = new();
    /// <summary>实心面缓冲（落地端支持 <see cref="ISimFaceSink"/> 时才用）。</summary>
    private readonly EquipMeshBuffer _mesh = new();
    private ISimFaceSink? FaceSink => _sink as ISimFaceSink;
    /// <summary>作业点 → 该点的块段序列（设备符号按它找“此刻在采哪一个”）。</summary>
    private readonly Dictionary<string, List<Plan>> _pointHeads = new(StringComparer.Ordinal);

    public SimBlockTransferParams Params { get; set; } = new();
    public SimBlockTransferResult? Last => _last;
    public bool Available => _sink.Available;

    private SimBlockTransferResult? _last;
    private string _period = "";

    /// <summary>本期参与的单元（已算好时间窗）。</summary>
    private readonly List<Plan> _plans = new();
    /// <summary>每个单元在期内占的那一格：(起点, 格长, 采出占比, 搬运占比, 堆填占比)。</summary>
    private readonly Dictionary<SimBlockUnit, (double T0, double Slot, double FE, double FT, double FD)> _slotOf = new();
    /// <summary>已堆完的块（累积，跨期不清 —— R-T4）。键 = 期次+UnitId，防重复堆。</summary>
    private readonly Dictionary<string, Placed> _dumped = new(StringComparer.Ordinal);

    private double[] _bufXyz = Array.Empty<double>();
    private uint[] _bufArgb = Array.Empty<uint>();

    public SimBlockTransferStage(ISimDynamicOverlay? sink = null) => _sink = sink ?? SimDynamicOverlay.Current;

    private sealed class Plan
    {
        public required SimBlockUnit U;
        public double T0, T1, T2, T3;      // 开工 / 采完 / 到场 / 堆完（期内相位）
        /// <summary>源块（采场）的朝向 —— 采出段、搬运中的料块、电铲都用它。</summary>
        public double HeadingRad;
        /// <summary>
        /// 堆填块（排土位置）的朝向。<b>与 <see cref="HeadingRad"/> 各读各的</b>：
        /// 排土条带走排土场自己那圈台阶线，与采场走向无关（实测中位差 45.1°）。
        /// </summary>
        public double DumpHeadingRad;
        public uint Rgb;
        public double DumpLen, DumpWid, DumpThk;
        public string Key = "";
    }

    private readonly record struct Placed(double X, double Y, double Z,
                                          double Len, double Wid, double Thk,
                                          double Heading, uint Rgb);

    // ── 重建（换期时一次）────────────────────────────────────────────────────

    public SimBlockTransferResult Rebuild(string periodKey, IReadOnlyList<SimBlockUnit>? units)
    {
        _period = periodKey ?? "";
        _plans.Clear();
        _slotOf.Clear();
        _pointHeads.Clear();
        LongApproach = 0;
        if (!Params.AccumulateDump) _dumped.Clear();

        var res = new SimBlockTransferResult { PeriodKey = _period };
        try { Build(res, units); }
        catch (Exception ex)
        {
            res.Notes.Add($"块体搬运重建异常（{ex.GetType().Name}: {ex.Message}）→ 本期不画。");
            res.Summary = "◆ 块体搬运：重建异常，本期一个块都没画。";
        }
        _last = res;
        return res;
    }

    private void Build(SimBlockTransferResult res, IReadOnlyList<SimBlockUnit>? units)
    {
        res.UnitTotal = units?.Count ?? 0;
        if (res.UnitTotal == 0)
        {
            res.Summary = "块体搬运：本期没有采掘单元 —— 没画不是画失败。";
            BuildLegend(res);
            return;
        }

        var noRoute = new SortedSet<string>(StringComparer.Ordinal);
        var noGeom = new SortedSet<string>(StringComparer.Ordinal);

        // ── 先筛出能画的，再按推进序定错峰次序（R-T2）──
        var usable = new List<SimBlockUnit>();
        foreach (var u in units!)
        {
            if (u == null) { res.UnitNoGeometry++; continue; }
            if (!u.GeometryOk) { res.UnitNoGeometry++; noGeom.Add(u.UnitId); continue; }
            if (!u.HasSourcePos) { res.UnitNoSourcePos++; continue; }
            // ★ 没路径**不是**不能画：采出段（工作面后退）与堆填段（逐层堆高）都不需要路网，
            //   只有中间的搬运段需要。以前这里整笔丢掉 —— 路网一断，三段全没，
            //   屏幕全黑而原因写在另一层的命中率里。现在只丢搬运那一段，并逐笔计数。
            // ── R-T6 没有真路径 ⇒ 合成【直连示意】，不是整段不画 ──────────────────
            //
            //  「绝不拿直线冒充路线」这条规矩对**运输线**那一层是对的：它报里程、报坡度，
            //  假线会被当成真路径去读数。但**本层**要演的是「料从 A 到了 B」这件事本身，
            //  源点和汇点都是台账里的真坐标，只是中间那条路不知道怎么走。
            //  整段不画的后果是：路网没建好之前，搬运过程、卡车、动态路线**全部看不到** ——
            //  一个能看的过程被一条防伪规矩连坐掉了。
            //
            //  正确的做法不是"不画"，是**画成一眼就不像真路线的样子**：
            //    · 虚线（手工打断的短段），不是连续实线
            //    · 压暗、细
            //    · 图例与状态栏明说"直连示意，非真实运输路线"
            //    · 里程 / 坡度 **一概不由它派生**（那才是假数会害人的地方）
            if (!u.HasRoute && u.HasDestPos && Params.AllowStraightLink)
            {
                u.Xyz = new[] { u.Sx, u.Sy, u.Sz, u.Dx, u.Dy, u.Dz };
                u.RouteIsStraight = true;
                res.UnitStraightLink++;
                noRoute.Add(u.UnitId);
            }
            else if (!u.HasRoute || !u.HasDestPos)
            {
                u.RouteIsStraight = false;
                u.Xyz = Array.Empty<double>();     // 明确清空：绝不留半条上一次的线
                res.UnitNoRoute++;
                noRoute.Add(u.UnitId);
            }
            usable.Add(u);
        }
        res.UnitUsed = usable.Count;

        // ── R-T2′ 按【作业点】编排：点内串行、点间并行 ───────────────────────────
        //
        //  以前是「所有单元同期错峰开工」，同时在采峰值 = 单元数（实测 97）——
        //  那不是多点采剥，那是整个采场一起塌下去：既不真实，画面也糊成一片。
        //  真实的是：**一台设备就是一个采剥点**，它在自己的点上一个块段接一个块段地啃；
        //  几台设备就是几个点在同时推进。所以：
        //     · 分组 = 设备号（没有设备指派时按推进序切成 WorkPoints 个**连续**段）
        //     · 点内 = 严格串行（前一个块段堆完，这台设备才开下一个）
        //     · 点间 = 并行，同时开工
        //     · 期内格数 = 最长的那个点的块段数；短点早收工（那正是"这个点这个月活少"）
        //  ⇒ 同时在采的点数 = 组数，不是单元数。
        //
        //  连续切分而不是轮转：一个采剥点是沿走向往前推的，不会这一刀在东、下一刀在西。
        var groups = new List<List<SimBlockUnit>>();
        var byMachine = usable.Where(u => !string.IsNullOrWhiteSpace(u.MachineId))
                              .GroupBy(u => u.MachineId, StringComparer.Ordinal)
                              .OrderBy(g => g.Key, StringComparer.Ordinal)
                              .ToList();

        if (byMachine.Count > 0)
        {
            foreach (var g in byMachine)
                groups.Add(g.OrderBy(u => u.Seq > 0 ? u.Seq : int.MaxValue)
                            .ThenBy(u => u.UnitId, StringComparer.Ordinal).ToList());
            var orphan = usable.Where(u => string.IsNullOrWhiteSpace(u.MachineId))
                               .OrderBy(u => u.Seq > 0 ? u.Seq : int.MaxValue)
                               .ThenBy(u => u.UnitId, StringComparer.Ordinal).ToList();
            if (orphan.Count > 0)
            {
                groups.Add(orphan);
                res.Notes.Add($"· 有 {orphan.Count} 个块段没有设备号，单独归为一个作业点（没指派 ≠ 不采）。");
            }
            res.PointSource = $"设备指派（{byMachine.Count} 台）";
        }
        else
        {
            int k = Math.Max(1, Math.Min(Params.WorkPoints, usable.Count));
            var seqOrdered = usable.OrderBy(u => u.Seq > 0 ? u.Seq : int.MaxValue)
                                   .ThenBy(u => u.UnitId, StringComparer.Ordinal).ToList();
            for (int gi = 0; gi < k; gi++) groups.Add(new List<SimBlockUnit>());
            int per = (int)Math.Ceiling(seqOrdered.Count / (double)k);
            for (int idx = 0; idx < seqOrdered.Count; idx++)
                groups[Math.Min(k - 1, idx / Math.Max(1, per))].Add(seqOrdered[idx]);
            groups.RemoveAll(g => g.Count == 0);
            res.PointSource = $"无设备指派 → 按推进序切成 {groups.Count} 个作业点（界面可调）";
            res.Notes.Add($"◆ 没有设备指派，作业点数是**界面给的 {Params.WorkPoints}**，不是排产结果。"
                        + "要按真实台数演，先在「设备指派」里排一次产。");
        }

        // 每个块段占一格；期内格数 = 最长那个点的块段数
        int maxLen = groups.Count == 0 ? 1 : groups.Max(g => g.Count);
        double slot = 1.0 / Math.Max(1, maxLen);
        double sum = Params.SpanFrac;
        double fE = Params.ExtractFrac / sum, fT = Params.TransferFrac / sum, fD = Params.DumpFrac / sum;

        var ordered = new List<SimBlockUnit>();
        foreach (var g in groups)
            for (int gi = 0; gi < g.Count; gi++)
            {
                ordered.Add(g[gi]);
                _slotOf[g[gi]] = (gi * slot, slot, fE, fT, fD);
            }

        res.WorkPointCount = groups.Count;
        res.LongestPointBlocks = maxLen;
        res.UnitRealRoute = usable.Count(u => u.HasRoute && !u.RouteIsStraight);

        for (int i = 0; i < ordered.Count; i++)
        {
            var u = ordered[i];
            var (t0, sl, fe, ft, fd) = _slotOf[u];
            var p = new Plan
            {
                U = u,
                T0 = t0,
                T1 = t0 + sl * fe,
                T2 = t0 + sl * (fe + ft),
                T3 = t0 + sl,
                Key = _period + "|" + u.UnitId,
                Rgb = u.IsCoal ? SimMaterialColorProvider.RgbCoal : SimMaterialColorProvider.RgbRock,
            };

            if (u.AzimuthDeg.HasValue) p.HeadingRad = (90.0 - u.AzimuthDeg.Value) * Math.PI / 180.0;
            else { p.HeadingRad = 0; res.UnitAxisDefault++; }

            // 【堆填块的朝向【只】能从排土位置自己那一行读，缺了就轴对齐并计数】
            // 沿用源块的 HeadingRad 是本层长期的一个错：排土条带走的是排土场自己那圈台阶线，
            // 与采场走向无关 —— 实测中位差 45.1°、51% 超 45°。沿用之后位置/体积/颜色全对，
            // 只有形态拧着，没有任何一个量看得出来。宁可轴对齐（明账计数、图例里说），
            // 也不拿另一处的角度冒充这一处的。
            if (u.DumpAzimuthDeg.HasValue)
                p.DumpHeadingRad = (90.0 - u.DumpAzimuthDeg.Value) * Math.PI / 180.0;
            else
            {
                p.DumpHeadingRad = 0;
                // 【煤不计这一笔】煤的汇是**卸点**（破碎站/储煤场），不是排土条带 ——
                // 那里本来就没有"走向"，轴对齐是正解不是缺陷。把它算进来的话，
                // 这个数就永远清不了零，而它正是"排土侧读没读到自己方位"的唯一指标。
                if (!u.IsCoal) res.DumpAxisDefault++;
            }

            // 排土块尺寸：给了就用，没给按源尺寸 + 占容方推（长宽保持，厚度由体积定）
            if (u.DumpLenM > 1e-6 && u.DumpWidM > 1e-6 && u.DumpThkM > 1e-6)
            { p.DumpLen = u.DumpLenM; p.DumpWid = u.DumpWidM; p.DumpThk = u.DumpThkM; }
            else
            {
                res.DumpSizeDerived++;
                p.DumpLen = u.LenM; p.DumpWid = u.WidM;
                double cap = u.CapacityM3 > 1e-6 ? u.CapacityM3 : u.InSituM3;
                double area = p.DumpLen * p.DumpWid;
                p.DumpThk = area > 1e-6 && cap > 1e-6 ? cap / area : u.ThkM;
            }

            _plans.Add(p);
            res.InSituTotalM3 += Math.Max(0, u.InSituM3);
            res.LooseTotalM3 += Math.Max(0, u.LooseM3);
            res.CapacityTotalM3 += Math.Max(0, u.CapacityM3);
        }

        // 作业点 → 块段序列（设备符号靠它找"此刻在采哪一个"）
        _pointHeads.Clear();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            string key = groups[gi].Count > 0 && !string.IsNullOrWhiteSpace(groups[gi][0].MachineId)
                       ? groups[gi][0].MachineId
                       : $"作业点{gi + 1}";
            var lst = new List<Plan>();
            foreach (var u in groups[gi])
            {
                var pl = _plans.FirstOrDefault(x => ReferenceEquals(x.U, u));
                if (pl != null) lst.Add(pl);
            }
            if (lst.Count > 0) _pointHeads[key] = lst;
        }

        // 并发峰值：取相位网格上同时处于 [T0,T3) 的最大个数
        int peak = 0;
        for (int k = 0; k <= 100; k++)
        {
            double t = k / 100.0;
            int c = _plans.Count(p => t >= p.T0 && t < p.T3);
            if (c > peak) peak = c;
        }
        res.ConcurrentPeak = peak;
        res.DumpedTotal = _dumped.Count;

        // ── 留条 ──
        if (res.UnitNoGeometry > 0)
            res.Notes.Add($"◆ 有 {res.UnitNoGeometry} 个单元几何不全（长/宽/厚有一项 ≤0）→ 不画：{Join(noGeom)}。");
        if (res.UnitNoSourcePos > 0)
            res.Notes.Add($"◆ 有 {res.UnitNoSourcePos} 个单元没有质心坐标（XY 全 0 = 没录，不是在原点）→ 不画。");
        if (res.UnitStraightLink > 0)
            res.Notes.Add($"◆ 有 {res.UnitStraightLink} 个块段没有路网路径，中间那段按**源→汇直连示意**演"
                        + "（画成虚线、压暗，一眼可辨）—— **它不是真实运输路线**，"
                        + "里程/坡度一概不由它派生。要看真路线，先建路网（道路运输系统）。");
        if (res.UnitNoRoute > 0 && !Params.AllowStraightLink)
            res.Notes.Add($"◆ 有 {res.UnitNoRoute} 个块段**在路网上解不出路径** → 不画搬运段、不画卡车"
                        + "（口径：线路只画路网寻径的结果，不用直连冒充）。"
                        + "采出段与堆填段照常演 —— 那两段不需要路网。"
                        + "要让它们出现，得把路网修通（看【运输线】那一层的未命中分类：源未吸附/汇未吸附/不连通）。");
        if (res.UnitNoRoute > 0 && Params.AllowStraightLink)
            res.Notes.Add($"◆ 有 {res.UnitNoRoute} 个单元连汇点坐标都没有 → **只演采出，搬运与堆填都不画**"
                        + "（不拿直线冒充路线）。采出/堆填不需要路网，所以那两段照常。"
                        + $"涉及：{Join(noRoute)}。要看搬运，先看【运输线】那一层的命中率 —— 那边是同一批 O-D。");
        if (res.UnitAxisDefault > 0)
            res.Notes.Add($"· 有 {res.UnitAxisDefault} 个单元不知道走向方位，按轴对齐摆（形态为近似，位置是真的）。");
        if (res.DumpSizeDerived > 0)
            res.Notes.Add($"· 有 {res.DumpSizeDerived} 个排土位置没给尺寸，按【源块长宽 + 占容方定厚度】推 —— 体积口径是真的，长宽是借来的。");
        if (res.DumpAxisDefault > 0)
            res.Notes.Add($"◆ 有 {res.DumpAxisDefault} 个排土位置**没读到自己的走向方位**，堆填块按轴对齐摆"
                        + "（长轴朝东，形态为近似，位置/体积是真的）—— **绝不拿源采场单元的走向冒充**："
                        + "两者中位差 45°，冒充之后图上与「排土条带」建出来的体整体拧着，而没有一个量看得出来。"
                        + "要让它摆正，台账排土行得有【走向方位°】列（从「排土条带」重新取一次台账就会带上）。");

        res.Summary = res.UnitUsed == 0
            ? "◆ 块体搬运：本期一个块都画不出来（原因见上）。"
            : $"块体搬运：{res.UnitUsed}/{res.UnitTotal} 个块段　·　**{res.WorkPointCount} 个采剥点同时推进**"
            + $"（{res.PointSource}；最长的点要啃 {res.LongestPointBlocks} 个块段）"
            + $"　·　挖 {res.InSituTotalM3 / 1e4:0.##}万m³实方 → 拉 {res.LooseTotalM3 / 1e4:0.##}万m³松方 → 堆 {res.CapacityTotalM3 / 1e4:0.##}万m³占容"
            + $"　·　**真路网路径 {res.UnitRealRoute}/{res.UnitUsed}**"
            + (res.UnitStraightLink > 0 ? $"（直连 {res.UnitStraightLink}）" : "")
            + (res.PlannedRouteSegments > 0 ? $"　·　规划线路 {res.PlannedRouteSegments} 段常驻" : "　·　**规划线路 0 段**")
            + (res.DumpedTotal > 0 ? $"　·　排土场已累计 {res.DumpedTotal} 块" : "");

        PushPlannedRoutes(res);
        if (LongApproach > 0)
            res.Notes.Add($"· 有 {LongApproach} 个块段离最近的可用路网入口超过 {Params.AnchorMaxM:0} m，"
                        + "**接入段不画**（那一段路网上并不存在，画出来就是一条长直线）。"
                        + "线路仍从路网入口画起 —— 块体与线路之间的空档，就是「这里没有路通到它」。");
        BuildLegend(res);
    }

    /// <summary>
    /// 本期**每个块体的规划线路**：源块 → 汇块，全部常驻画出来（压暗、细）。
    ///
    /// <para>为什么要常驻：搬运段只占每个块段时长的 20%，只在搬运时才亮的话，
    /// 任一时刻图上只有零星几条 —— 看不到「这个月的料整体往哪走」这件事。
    /// 规划线路回答的是**计划**（每个块去哪），高亮那一层回答的是**此刻**（谁在路上）。
    /// 两个问题，两层，叠放次序也不同。</para>
    ///
    /// <para>不随相位变 ⇒ 只在换期重建时推一次，不进逐帧钩子。</para>
    /// </summary>
    private void PushPlannedRoutes(SimBlockTransferResult res)
    {
        if (!Params.ShowPlannedRoutes)
        {
            res.PlannedRouteSegments = 0;
            _sink.SetLines(PlannedRouteGroup, null, null, 0);
            return;
        }
        var xyz = new List<double>();
        var argb = new List<uint>();
        double lift = double.IsNaN(Params.LiftM) ? 0 : Params.LiftM;

        foreach (var p in _plans)
        {
            var u = p.U;
            if (!u.HasRoute) continue;
            var poly = EffectiveRoute(u, out _, out _);
            int np = poly.Length / 3;
            // 直连示意的规划线同样打断成虚线 —— 与真路线在底图上也要分得出
            int stride = u.RouteIsStraight ? 2 : 1;
            for (int k = 0; k + 1 < np; k += stride)
            {
                int a0 = k * 3, b0 = (k + 1) * 3;
                xyz.Add(poly[a0]); xyz.Add(poly[a0 + 1]); xyz.Add(poly[a0 + 2] + lift * 0.5);
                xyz.Add(poly[b0]); xyz.Add(poly[b0 + 1]); xyz.Add(poly[b0 + 2] + lift * 0.5);
                // 0x33（20% 不透明）实测在深底上几乎看不见 —— 每个块体的规划线路是
                // "这块料往哪走"的主要信息，不该淡到要凑近看。提到 0x88 并保留物料色相。
                argb.Add(0x44000000u | TowardGrey(p.Rgb));   // 常驻的压暗：高亮那条才是「此刻在动的」
            }
        }

        res.PlannedRouteSegments = argb.Count;
        if (argb.Count > 0) _sink.SetLines(PlannedRouteGroup, xyz.ToArray(), argb.ToArray(), argb.Count);
        else _sink.SetLines(PlannedRouteGroup, null, null, 0);
    }

    private void BuildLegend(SimBlockTransferResult res)
    {
        res.LegendText =
            "图例：**采场块沿推进方向收缩**（工作面后退，实方）→ **料块沿运输线在途**（松方 ×Ks）→ "
          + "**排土块沿高度长高**（占容方 ×Kr）。三段体积本来就不相等（Ks≈1.25 / Kr≈1.13），"
          + "**不是搬运途中丢了料**。"
          + $"　**三色**：煤 #{SimMaterialColorProvider.RgbCoal:X6} · 岩 #{SimMaterialColorProvider.RgbRock:X6} · "
          + $"**排弃土 #{SimMaterialColorProvider.RgbDumped:X6}**（堆下去之后不再分煤岩 —— "
          + "库容按占容方扣、边坡按排弃土的稳定角算，都与原来是哪一层无关）。"
          + $"　⚠ 一个块 = **一个采掘单元一个月的量**（几千车次的批次示意），不是一车；逐车看【车流】。"
          + "　**每个采剥点一台电铲**（整个块段周期都在，不因为转入运输而消失）；"
          + "**排土场堆填时有推土机在推平**（卸下来的是松散料堆，推平分层才算占容方）。"
          + "　**常驻的线 = 每个块体的规划线路**（计划：这块料往哪走）；**亮起来的那条 = 此刻在搬的**；"
          + $"　**每条在用线路上常驻 {Params.TrucksPerRoute} 辆车** —— 表达「这条路本期在用」，"
          + "不是「此刻正好有这几台车」（逐车口径见【车流】，按 Little 定律定强度）。"
          + "　**两端压暗的那一小段 = 接入段**（块体↔路网节点，不是路网上的路段）。"
          + (Params.AllowStraightLink
                ? "　**虚线 = 直连示意**（该块没有路网路径，中间按源→汇直线演；**不是真实运输路线**，不报里程/坡度）。"
                : "　**线路一律是路网寻径（Dijkstra 最短路）的结果**；解不出的块段不画搬运段，绝不用直连冒充。")
          + $"　⚠ 搬运段是**展示时长**（{Params.TransferFrac:0.##} 期）不是真实行车时间"
          + "（真实运距/坡度见【路线标注】，真实车流强度见【车流】）。";
    }

    // ── 逐帧（只算位置、发一次）─────────────────────────────────────────────

    /// <param name="phase">期内相位 0..1。</param>
    /// <returns>本帧画了多少段。</returns>
    public int Tick(double phase)
    {
        if (_plans.Count == 0 && _dumped.Count == 0) { PushAll(0, 0, 0); ClearExtras(); return 0; }
        double t = double.IsNaN(phase) ? 0 : Math.Clamp(phase, 0, 1);
        double lift = double.IsNaN(Params.LiftM) ? 0 : Params.LiftM;
        int cap = Math.Max(64, Params.MaxSegments);
        EnsureBuf(cap);

        var fs = FaceSink;
        _mesh.Clear();
        _wire.Clear();
        _activeIds.Clear();
        foreach (var pl in _plans)
            if (t >= pl.T0 && t < pl.T3) _activeIds.Add(pl.U.UnitId);

        // -- (1) pit side --
        foreach (var p in _plans)
        {
            var u = p.U;
            double f;
            if (t < p.T0) f = 1.0;
            else if (t >= p.T1) f = 0.0;
            else f = 1.0 - (t - p.T0) / Math.Max(1e-9, p.T1 - p.T0);
            if (f <= 1e-6) continue;

            double w = u.WidM * f;
            EmitBox(fs, u.Sx, u.Sy, u.Sz - u.ThkM * 0.5 + lift, p.HeadingRad,
                    -u.LenM * 0.5, u.LenM * 0.5, -u.WidM * 0.5, -u.WidM * 0.5 + w, 0, u.ThkM, p.Rgb);
        }
        int pitEnd = _wire.SegmentCount;

        // -- (2) carry block + (3) that block's own route, lit only while it is moving --
        var routeXyz = new List<double>();
        var routeArgb = new List<uint>();
        int trucks = 0;

        foreach (var p in _plans)
        {
            // ★ 高亮口径：**这个块段只要在作业（T0..T3），它那条线路就亮着**。
            //   以前只在搬运段（T1..T2，占一格的 20%）亮 —— 于是任一时刻只有零星几条亮线，
            //   而正在被挖的那个块的线路反倒是暗的，看着就"跟块体对不上"。
            //   现在图上一眼能答的是同一个问题：**动的是哪个块，亮的就是它的线路**。
            if (t < p.T0 || t >= p.T3) continue;
            var u = p.U;
            if (!u.HasRoute) continue;

            // 进度：采出段还没上路（0）· 搬运段按比例走 · 堆填段已到达（1）
            double s = t < p.T1 ? 0.0
                     : t >= p.T2 ? 1.0
                     : (t - p.T1) / Math.Max(1e-9, p.T2 - p.T1);
            bool onRoad = t >= p.T1 && t < p.T2;

            // ── R-T7 路线必须**锚在块体上** ────────────────────────────────────
            //
            //  路网解出来的折线，起点是**源点被吸附到的那个路网节点**、终点是汇点被吸附到的节点，
            //  两头都可能离真正的块体几百米（吸附半径缺省 200m）。直接画的话，
            //  线和块是**脱开的** —— 看不出"这条路是这个块的"，尤其多个块共用干线时更分不清。
            //
            //  ⇒ 把有效路线补成  [块体质心] → 路网折线 → [排土块质心]。
            //    补出来的那两段是**接入段**，不是路：与直连示意同样处理（虚线、压暗），
            //    因为它确实不是路网上的路段。里程/坡度同样不由它派生。
            var poly = EffectiveRoute(u, out int approachHead, out int approachTail);

            if (u.RouteIsStraight)
            {
                // 直连示意：手工打断成虚线（本通道没有线型，虚线只能靠分段做出来）。
                // 段数固定 24，与真路线的连续实线在任何缩放下都不会混淆。
                const int Dashes = 24;
                for (int k = 0; k < Dashes; k++)
                {
                    double f0 = k / (double)Dashes, f1 = f0 + 0.55 / Dashes;   // 55% 实、45% 空
                    double ax2 = u.Sx + (u.Dx - u.Sx) * f0, ay2 = u.Sy + (u.Dy - u.Sy) * f0, az2 = u.Sz + (u.Dz - u.Sz) * f0;
                    double bx2 = u.Sx + (u.Dx - u.Sx) * f1, by2 = u.Sy + (u.Dy - u.Sy) * f1, bz2 = u.Sz + (u.Dz - u.Sz) * f1;
                    routeXyz.Add(ax2); routeXyz.Add(ay2); routeXyz.Add(az2 + lift);
                    routeXyz.Add(bx2); routeXyz.Add(by2); routeXyz.Add(bz2 + lift);
                    routeArgb.Add(f0 < s ? (0xCC000000u | TowardGrey(p.Rgb)) : (0x44000000u | TowardGrey(p.Rgb)));
                }
            }
            else
            {
                int np = poly.Length / 3;
                double done = s * Math.Max(0, np - 1);
                for (int k = 0; k + 1 < np; k++)
                {
                    int a0 = k * 3, b0 = (k + 1) * 3;
                    routeXyz.Add(poly[a0]); routeXyz.Add(poly[a0 + 1]); routeXyz.Add(poly[a0 + 2] + lift);
                    routeXyz.Add(poly[b0]); routeXyz.Add(poly[b0 + 1]); routeXyz.Add(poly[b0 + 2] + lift);

                    bool isApproach = k < approachHead || k >= np - 1 - approachTail;
                    bool passed = k < done;
                    // ★ 高亮 = "这条线属于**正在动的那个块**"，**不等走过了才亮**。
                    //   以前未走过的部分给 0x55（33% 不透明），而块段刚开工时进度为 0
                    //   ⇒ 整条线都算"未走过" ⇒ 跟常驻规划线（0x44）一样暗，
                    //   现象就是"开始时高亮不显示"。
                    //   现在：整条都亮（0xCC 本色），**走过的那截再提到全不透明 + 提亮**，
                    //   于是"哪条在动"和"走到哪了"是两个独立可读的信息。
                    // ★ 配色与离线出图台架（plot_block_routes.py 的图③）对齐：
                    //   当前在搬的那条一律**琥珀色**，不用物料色 —— 物料色（岩偏暗）在深底上
                    //   与规划线、路网底图分不开，而"此刻在动的是哪条"必须一眼看得出来。
                    //   物料仍由块体本身的颜色表达，不靠线。
                    // 红：本层唯一用红的地方 —— 一屏里同时有路网底图、规划线路、块体、设备，
                    // 红既不与它们中的任何一个撞色，又是最跳的。走过的那截更亮。
                    const uint Amber = 0xFF2D2Du;      // 粗红：此刻在搬的那条
                    const uint AmberLit = 0xFF7A6Bu;   // 走过的那截提亮
                    routeArgb.Add(isApproach
                        ? (passed ? 0xAA000000u : 0x66000000u) | TowardGrey(p.Rgb)
                        : (passed ? 0xFF000000u | AmberLit : 0xEE000000u | Amber));
                }
            }

            if (!onRoad) continue;                     // 线亮着，但料块/卡车只在真正在途时才有
            if (!TryPointOnPath(poly, s, out double cx, out double cy, out double cz, out double hd)) continue;

            double k2 = Math.Clamp(Params.CarryScale, 0.05, 1.0);
            EmitBox(fs, cx, cy, cz + lift, hd,
                    -u.LenM * 0.5 * k2, u.LenM * 0.5 * k2, -u.WidM * 0.5 * k2, u.WidM * 0.5 * k2,
                    0, u.ThkM * k2, p.Rgb);

            double tl = Math.Max(12, u.LenM * 0.10) * Params.TruckScale;
            for (int q = -1; q <= 1; q += 2)
            {
                double off = q * tl * 1.9;
                EmitTruck(fs, cx + Math.Cos(hd) * off, cy + Math.Sin(hd) * off, cz + lift, hd, tl,
                          EquipPalette.Rgb(EquipKind.Truck));
                trucks++;
            }
        }
        int carryEnd = _wire.SegmentCount;

        // -- (4) dump side --
        foreach (var d in _dumped.Values)
            EmitBox(fs, d.X, d.Y, d.Z + lift, d.Heading,
                    -d.Len * 0.5, d.Len * 0.5, -d.Wid * 0.5, d.Wid * 0.5, 0, d.Thk, d.Rgb);

        foreach (var p in _plans)
        {
            if (t < p.T2) continue;
            var u = p.U;
            double g = t >= p.T3 ? 1.0 : (t - p.T2) / Math.Max(1e-9, p.T3 - p.T2);
            if (g <= 1e-6) continue;
            if (!u.HasDestPos) continue;

            if (g >= 1.0 - 1e-9)
            {
                // 已堆下去的是**排弃土**，不再按源物料分色（见 SimMaterialColorProvider.RgbDumped）
                _dumped[p.Key] = new Placed(u.Dx, u.Dy, u.Dz, p.DumpLen, p.DumpWid, p.DumpThk,
                                            p.DumpHeadingRad, SimMaterialColorProvider.RgbDumped);
                continue;
            }
            EmitBox(fs, u.Dx, u.Dy, u.Dz + lift, p.DumpHeadingRad,
                    -p.DumpLen * 0.5, p.DumpLen * 0.5, -p.DumpWid * 0.5, p.DumpWid * 0.5, 0,
                    p.DumpThk * g, SimMaterialColorProvider.RgbDumped);
        }

        // ── ⑤ 在用线路上常驻卡车（沿线均匀分布，随相位前进）────────────────────
        //   与上面"搬运那一瞬间跟着料块的两台车"不是一回事：那两台演的是**这一批料在路上**，
        //   这些演的是**这条线路本期在被使用**。两者叠加才既看得出路在用、又看得出料在动。
        if (Params.TrucksPerRoute > 0)
        {
            foreach (var p in _plans)
            {
                var u = p.U;
                if (!u.HasRoute || u.RouteIsStraight) continue;
                // ★ 只撒在**此刻在作业**的那几条线路上（= 高亮的那几条），不是全部 76 条。
                //   76 条各一辆就是 76 台车铺满全图（现场反馈"卡车还是有点多"），
                //   而它要表达的本来就是"这条路此刻在被使用" —— 那正是高亮那几条。
                if (t < p.T0 || t >= p.T3) continue;
                var poly = EffectiveRoute(u, out _, out _);
                double tl = Math.Max(14, u.LenM * 0.10) * Params.TruckScale;
                for (int q = 0; q < Params.TrucksPerRoute; q++)
                {
                    // 相位驱动 + 每条线错开一点：不用随机数（随机会让两次播放不一样）
                    double ph = (t * 1.7 + q / (double)Params.TrucksPerRoute
                                 + (Math.Abs(u.UnitId.GetHashCode()) % 997) / 997.0) % 1.0;
                    if (!TryPointOnPath(poly, ph, out double tx, out double ty, out double tz, out double th)) continue;
                    EmitTruck(fs, tx, ty, tz + lift, th, tl, EquipPalette.Rgb(EquipKind.Truck));
                    trucks++;
                }
            }
        }

        if (routeArgb.Count > 0) _sink.SetLines(ActiveRouteGroup, routeXyz.ToArray(), routeArgb.ToArray(), routeArgb.Count);
        else _sink.SetLines(ActiveRouteGroup, null, null, 0);
        ActiveTrucks = trucks;

        if (fs != null && !_mesh.IsEmpty) PushFaces(fs);
        else fs?.SetFaces(FaceGroup, null, null, 0);

        PushShovels(t, lift);
        return PushSplit(pitEnd, carryEnd, cap);
    }

    /// <summary>
    /// 采剥点上的设备符号：**每个作业点一台**，摆在它当前正在采的那个块上。
    ///
    /// <para>这样设备**一定看得见**，且位置有依据 —— 它就在那个块上作业。
    /// 以前设备是另一层（<c>SimEquipMotionStage</c>）画的，要有设备指派才有东西；
    /// 没指派就一台都没有，而块体明明在动。两层说的是同一件事，却一个有一个没有。</para>
    ///
    /// <para>形状复用 <see cref="EquipSymbolLibrary"/>（与实体符号同一份定义）。
    /// 尺寸按块段宽度取，免得在 3km 的图上小到看不见。</para>
    /// </summary>
    private void PushShovels(double t, double lift)
    {
        var fs = FaceSink;
        _equipMesh.Clear();
        if (!Params.ShowShovels) { _sink.SetLines(ShovelGroup, null, null, 0); _sink.SetLabels(ShovelTagGroup, null, null, null, null, null, null, 0); return; }

        _shovelWire.Clear();
        var tagXyz = new List<double>();
        var tagTxt = new List<string>();

        foreach (var g in _pointHeads)
        {
            // ★ 该作业点此刻负责哪一个块段 —— 判据是**整格 T0..T3**，不是只有采出段。
            //
            //   以前只在 T0..T1（采出段）画，于是块段一进入搬运/堆填，
            //   这个点上的电铲就凭空消失了 —— 而现实里铲一直在那儿装车，
            //   料在路上、在排土场堆，跟铲在不在没有关系（现场反馈"采完转接运输时电铲不消失"）。
            //
            //   收工之后（最后一格也过完）才没有 —— 那时这个点这个月的活确实干完了。
            Plan? cur = null;
            foreach (var p in g.Value) if (t >= p.T0 && t < p.T3) { cur = p; break; }
            if (cur == null)
            {
                // 已收工：停在最后一个块段的位置上（不凭空消失，也不再前进）
                Plan? last = null;
                foreach (var p in g.Value) if (t >= p.T3) last = p;
                if (last == null) continue;
                cur = last;
            }

            var u = cur.U;
            // 采出段按进度后退；采完之后停在采完的那一端（工作面已经推到底了）
            double f = t >= cur.T1 ? 0.0
                     : 1.0 - (t - cur.T0) / Math.Max(1e-9, cur.T1 - cur.T0);
            // 设备站在“已挖到的那个面”上：沿宽度方向从一端推向另一端
            double y = -u.WidM * 0.5 + u.WidM * (1 - f);
            double sz = Math.Max(u.WidM, u.LenM * 0.25) * Params.ShovelScale;

            double wx = u.Sx + (y * -Math.Sin(cur.HeadingRad));
            double wy = u.Sy + (y * Math.Cos(cur.HeadingRad));
            double wz = u.Sz + u.ThkM * 0.5 + lift;

            // ★ 有面通道就走**实体面**，线框只是没有面通道时的退路。
            //   两者都发的话，边线会盖在自己的面上，缩小时糊成一团。
            if (fs != null)
                EquipSymbolLibrary.Emit(_equipMesh, EquipKind.Shovel, EquipState.Working,
                                        wx, wy, wz, cur.HeadingRad, sz);
            else
                EquipSymbolLibrary.Emit(_shovelWire, EquipKind.Shovel, EquipState.Working,
                                        wx, wy, wz, cur.HeadingRad, sz);

            tagXyz.Add(wx); tagXyz.Add(wy); tagXyz.Add(wz + sz * 0.8);
            tagTxt.Add(g.Key + "  " + u.UnitId);
        }

        // ── 推土机：本期正在堆填的那些排土位置，各摆一台在堆顶推平 ──────────────
        //
        //   排土不是"倒下去就完了"：卸下来的是松散料堆，要推平、分层碾压才算占容方。
        //   所以堆填段（T2..T3）排土场上必有推土机 —— 少了它，排土场那半边看着像
        //   料自己长出来的。位置放在**正在长的那个堆的顶面**，随高度一起抬。
        if (Params.ShowDozers)
        {
            foreach (var p in _plans)
            {
                if (t < p.T2 || t >= p.T3) continue;
                var u = p.U;
                if (!u.HasDestPos) continue;
                double g2 = (t - p.T2) / Math.Max(1e-9, p.T3 - p.T2);
                double sz = Math.Max(u.WidM, p.DumpWid) * Params.DozerScale;
                // 在堆顶来回推：沿排土块的长边往复（相位驱动，不用随机）
                double sweep = Math.Sin(g2 * Math.PI * 4) * p.DumpLen * 0.3;
                // 推土机在【排土块】上推平，所以往复方向跟排土块走，不跟采场块走
                double hd = p.DumpHeadingRad;
                double dx2 = Math.Cos(hd) * sweep, dy2 = Math.Sin(hd) * sweep;
                double topZ = u.Dz + p.DumpThk * g2 + lift;
                if (fs != null)
                    EquipSymbolLibrary.Emit(_equipMesh, EquipKind.Dozer, EquipState.Working,
                                            u.Dx + dx2, u.Dy + dy2, topZ, hd, sz);
                else
                    EquipSymbolLibrary.Emit(_shovelWire, EquipKind.Dozer, EquipState.Working,
                                            u.Dx + dx2, u.Dy + dy2, topZ, hd, sz);

                tagXyz.Add(u.Dx + dx2); tagXyz.Add(u.Dy + dy2); tagXyz.Add(topZ + sz * 0.8);
                tagTxt.Add("推土机  " + (u.DestinationName.Length > 0 ? u.DestinationName : u.UnitId));
            }
        }

        // 设备的面自己推自己那一组（块体那组早就推过了）
        if (fs != null)
        {
            if (_equipMesh.IsEmpty) fs.SetFaces(EquipFaceGroup, null, null, 0);
            else
            {
                var v = _equipMesh.WorldXyz; var tri = _equipMesh.Triangles; var vc = _equipMesh.VertexRgb;
                int nt = tri.Length / 3;
                var xyz = new double[nt * 9];
                var argb = new uint[nt];
                for (int i = 0; i < nt; i++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        uint vi = tri[i * 3 + k];
                        xyz[i * 9 + k * 3] = v[vi * 3];
                        xyz[i * 9 + k * 3 + 1] = v[vi * 3 + 1];
                        xyz[i * 9 + k * 3 + 2] = v[vi * 3 + 2];
                    }
                    argb[i] = 0xFF000000u | (tri[i * 3] < vc.Length ? vc[tri[i * 3]] : 0xFFFFFFu);
                }
                fs.SetFaces(EquipFaceGroup, xyz, argb, nt);
            }
        }

        var segs = _shovelWire.WorldSegments;
        var cols = _shovelWire.SegmentRgb;
        if (cols.Length == 0) { _sink.SetLines(ShovelGroup, null, null, 0); }
        else
        {
            var argb = new uint[cols.Length];
            for (int i = 0; i < cols.Length; i++) argb[i] = 0xFF000000u | cols[i];
            _sink.SetLines(ShovelGroup, segs, argb, cols.Length);
        }

        if (tagTxt.Count == 0) { _sink.SetLabels(ShovelTagGroup, null, null, null, null, null, null, 0); }
        else
        {
            var ta = new uint[tagTxt.Count];
            var th = new float[tagTxt.Count];
            for (int i = 0; i < ta.Length; i++) { ta[i] = 0xFFFFFFFFu; th[i] = (float)Params.TagHeightM; }
            _sink.SetLabels(ShovelTagGroup, tagXyz.ToArray(), tagTxt, ta, th, null, null, tagTxt.Count);
        }
    }

    /// <summary>
    /// 有效路线 = <b>[块体质心] → 路网折线 → [排土块质心]</b>。
    ///
    /// <para>两头补出来的那一段叫**接入段**（<paramref name="approachHead"/>/<paramref name="approachTail"/>
    /// 给出各补了几段）。补它的理由见 R-T7：路网折线的两端是**吸附到的节点**，
    /// 离真正的块体可能几百米，不补的话线和块是脱开的，看不出这条路属于哪个块。</para>
    ///
    /// <para>两端已经足够近（&lt; <see cref="SimBlockTransferParams.AnchorTolM"/>）就不补 ——
    /// 补一段零长的只会在图上叠出一个点。</para>
    /// </summary>
    private double[] EffectiveRoute(SimBlockUnit u, out int approachHead, out int approachTail)
    {
        approachHead = approachTail = 0;
        if (!u.HasRoute) return u.Xyz;

        double tol = Math.Max(1e-6, Params.AnchorTolM);
        int n = u.PointCount;
        double hx = u.Xyz[0], hy = u.Xyz[1], hz = u.Xyz[2];
        double tx = u.Xyz[(n - 1) * 3], ty = u.Xyz[(n - 1) * 3 + 1], tz = u.Xyz[(n - 1) * 3 + 2];

        double headGap = Hypot(hx - u.Sx, hy - u.Sy);
        double tailGap = u.HasDestPos ? Hypot(tx - u.Dx, ty - u.Dy) : 0;
        double cap = Math.Max(tol, Params.AnchorMaxM);

        // 太近不用补；太远不该补（那一段路网上不存在，画出来就是一条长直线）
        bool needHead = headGap > tol && headGap <= cap;
        bool needTail = u.HasDestPos && tailGap > tol && tailGap <= cap;
        if (headGap > cap || tailGap > cap) LongApproach++;
        if (!needHead && !needTail) return u.Xyz;

        var list = new List<double>((n + 2) * 3);
        if (needHead) { list.Add(u.Sx); list.Add(u.Sy); list.Add(u.Sz); approachHead = 1; }
        list.AddRange(u.Xyz);
        if (needTail) { list.Add(u.Dx); list.Add(u.Dy); list.Add(u.Dz); approachTail = 1; }
        return list.ToArray();
    }

    /// <summary>本期有几个块段的接入距超限（接入段没画）。重建时清零。</summary>
    public int LongApproach { get; private set; }

    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>本帧在途的卡车台数（示意）。</summary>
    public int ActiveTrucks { get; private set; }

    /// <summary>
    /// 此刻在作业的块段（每个采剥点一个）。<b>界面每帧显示它</b> ——
    /// 「高亮是不是静态的」只靠看图分不清（线细、色差小），把块段号打出来就能一眼确认相位在走。
    /// </summary>
    public IReadOnlyList<string> ActiveUnitIds => _activeIds;
    private readonly List<string> _activeIds = new();

    /// <summary>
    /// 画一个盒子：落地端支持实心面就同时落**面 + 边**，不支持只落边。
    /// <para>面 + 边一起给的理由：只有面时相邻同色块之间没有分界、糊成一坨；
    /// 只有边时是线框，看不出体量。两者叠加才既有实体感又有形。</para>
    /// </summary>
    private void EmitBox(ISimFaceSink? fs, double ox, double oy, double oz, double heading,
                         double x0, double x1, double y0, double y1, double z0, double z1, uint rgb)
    {
        if (fs != null)
        {
            _mesh.BeginPose(ox, oy, oz, heading, 1.0);
            _mesh.Box(x0, x1, y0, y1, z0, z1, rgb);
            _mesh.EndPose();
        }
        _wire.BeginPose(ox, oy, oz, heading, 1.0);
        _wire.Box(x0, x1, y0, y1, z0, z1, fs != null ? Darken(rgb) : rgb);
        _wire.EndPose();
    }

    /// <summary>矿卡：复用设备符号库的卡车形态（与实体符号同一份定义）。</summary>
    /// <summary>
    /// 矿卡：形态复用设备符号库（与实体符号同一份定义），<b>配色走 EquipPalette 的设备类别色</b>。
    /// <para>以前跟着物料色走 —— 于是"拉煤的车"和"煤块"同色、"拉岩的车"和"岩块"同色，
    /// 图上分不出哪个是料哪个是车。设备就该按设备着色，物料由块体表达。</para>
    /// </summary>
    private void EmitTruck(ISimFaceSink? fs, double ox, double oy, double oz, double heading, double size, uint rgb)
    {
        if (fs != null) EquipSymbolLibrary.Emit(_mesh, EquipKind.Truck, EquipState.Hauling, ox, oy, oz, heading, size);
        else EquipSymbolLibrary.Emit(_wire, EquipKind.Truck, EquipState.Hauling, ox, oy, oz, heading, size);
    }

    /// <summary>网格缓冲 → 「每三角 9 个 double + 逐三角色」推给面通道。</summary>
    private void PushFaces(ISimFaceSink fs)
    {
        var v = _mesh.WorldXyz; var idx = _mesh.Triangles; var vc = _mesh.VertexRgb;
        int nt = idx.Length / 3;
        var xyz = new double[nt * 9];
        var argb = new uint[nt];
        for (int i = 0; i < nt; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                uint vi = idx[i * 3 + k];
                xyz[i * 9 + k * 3] = v[vi * 3];
                xyz[i * 9 + k * 3 + 1] = v[vi * 3 + 1];
                xyz[i * 9 + k * 3 + 2] = v[vi * 3 + 2];
            }
            argb[i] = 0xFF000000u | vc[idx[i * 3]];
        }
        fs.SetFaces(FaceGroup, xyz, argb, nt);
    }

    private void ClearExtras()
    {
        try
        {
            _sink.SetLines(ActiveRouteGroup, null, null, 0);
            _sink.SetLines(PlannedRouteGroup, null, null, 0);
            FaceSink?.SetFaces(FaceGroup, null, null, 0);
            FaceSink?.SetFaces(EquipFaceGroup, null, null, 0);
        }
        catch { }
    }

    private static uint Darken(uint rgb)
    {
        int r = (int)((rgb >> 16 & 0xFF) * 0.45), g = (int)((rgb >> 8 & 0xFF) * 0.45), b = (int)((rgb & 0xFF) * 0.45);
        return (uint)(r << 16 | g << 8 | b);
    }

    /// <summary>向白提亮（已走过的那截：同色系但更跳，不换色相）。</summary>
    private static uint Brighten(uint rgb)
    {
        int r = (int)((rgb >> 16 & 0xFF) * 0.45 + 255 * 0.55);
        int g = (int)((rgb >> 8 & 0xFF) * 0.45 + 255 * 0.55);
        int b = (int)((rgb & 0xFF) * 0.45 + 255 * 0.55);
        return (uint)(r << 16 | g << 8 | b);
    }

    private static uint TowardGrey(uint rgb)
    {
        int r = (int)((rgb >> 16 & 0xFF) * 0.4 + 0x80 * 0.6);
        int g = (int)((rgb >> 8 & 0xFF) * 0.4 + 0x80 * 0.6);
        int b = (int)((rgb & 0xFF) * 0.4 + 0x80 * 0.6);
        return (uint)(r << 16 | g << 8 | b);
    }

    /// <summary>把线框缓冲按三段切成三组各推一次（每组一次 P/Invoke，与块数无关）。</summary>
    private int PushSplit(int pitEnd, int carryEnd, int cap)
    {
        var segs = _wire.WorldSegments;
        var cols = _wire.SegmentRgb;
        int total = Math.Min(cols.Length, cap);

        int n1 = Math.Min(pitEnd, total);
        int n2 = Math.Min(carryEnd, total) - n1;
        int n3 = total - n1 - Math.Max(0, n2);
        if (n2 < 0) n2 = 0;
        if (n3 < 0) n3 = 0;

        Fill(segs, cols, 0, total);
        _sink.SetLines(PitGroup, _bufXyz, _bufArgb, n1);
        _sink.SetLines(CarryGroup, Slice(segs, cols, n1, n2, out var a2), a2, n2);
        _sink.SetLines(DumpGroup, Slice(segs, cols, n1 + n2, n3, out var a3), a3, n3);
        return total;
    }

    private void Fill(double[] segs, uint[] cols, int from, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 6, q = (from + i) * 6;
            _bufXyz[o] = segs[q]; _bufXyz[o + 1] = segs[q + 1]; _bufXyz[o + 2] = segs[q + 2];
            _bufXyz[o + 3] = segs[q + 3]; _bufXyz[o + 4] = segs[q + 4]; _bufXyz[o + 5] = segs[q + 5];
            _bufArgb[i] = 0xFF000000u | cols[from + i];
        }
    }

    private static double[] Slice(double[] segs, uint[] cols, int from, int count, out uint[] argb)
    {
        var xyz = new double[Math.Max(6, count * 6)];
        argb = new uint[Math.Max(1, count)];
        for (int i = 0; i < count; i++)
        {
            int o = i * 6, q = (from + i) * 6;
            xyz[o] = segs[q]; xyz[o + 1] = segs[q + 1]; xyz[o + 2] = segs[q + 2];
            xyz[o + 3] = segs[q + 3]; xyz[o + 4] = segs[q + 4]; xyz[o + 5] = segs[q + 5];
            argb[i] = 0xFF000000u | cols[from + i];
        }
        return xyz;
    }

    private void PushAll(int a, int b, int c)
    {
        _sink.SetLines(PitGroup, null, null, a);
        _sink.SetLines(CarryGroup, null, null, b);
        _sink.SetLines(DumpGroup, null, null, c);
    }

    /// <summary>撤掉本层三个组。幂等。<paramref name="keepDumped"/>=false 时连累积的排土块一起清。</summary>
    public void Clear(bool keepDumped = false)
    {
        _plans.Clear();
        if (!keepDumped) _dumped.Clear();
        try { PushAll(0, 0, 0); ClearExtras(); } catch { }
    }

    private void EnsureBuf(int cap)
    {
        if (_bufXyz.Length < cap * 6) _bufXyz = new double[cap * 6];
        if (_bufArgb.Length < cap) _bufArgb = new uint[cap];
    }

    /// <summary>折线上按弧长比例取点 + 该点的走向（弧度，atan2）。</summary>
    internal static bool TryPointOnPath(double[] xyz, double s,
                                        out double x, out double y, out double z, out double headingRad)
    {
        x = y = z = 0; headingRad = 0;
        if (xyz == null || xyz.Length < 6 || xyz.Length % 3 != 0) return false;
        int n = xyz.Length / 3;

        double total = 0;
        for (int i = 0; i + 1 < n; i++) total += Dist(xyz, i, i + 1);
        if (total <= 1e-9) return false;

        double want = Math.Clamp(s, 0, 1) * total, acc = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            double d = Dist(xyz, i, i + 1);
            if (d <= 1e-12) continue;
            if (acc + d >= want || i + 2 == n)
            {
                double f = Math.Clamp((want - acc) / d, 0, 1);
                int a = i * 3, b = (i + 1) * 3;
                x = xyz[a] + (xyz[b] - xyz[a]) * f;
                y = xyz[a + 1] + (xyz[b + 1] - xyz[a + 1]) * f;
                z = xyz[a + 2] + (xyz[b + 2] - xyz[a + 2]) * f;
                headingRad = Math.Atan2(xyz[b + 1] - xyz[a + 1], xyz[b] - xyz[a]);
                return true;
            }
            acc += d;
        }
        return false;
    }

    private static double Dist(double[] p, int i, int j)
    {
        int a = i * 3, b = j * 3;
        double dx = p[b] - p[a], dy = p[b + 1] - p[a + 1], dz = p[b + 2] - p[a + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string Join(IEnumerable<string> xs, int cap = 6)
    {
        var l = xs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (l.Count == 0) return "(无单元号)";
        return l.Count <= cap ? string.Join("、", l) : string.Join("、", l.Take(cap)) + $"…（共 {l.Count} 个）";
    }
}
