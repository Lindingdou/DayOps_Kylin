// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimRoadGraph.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Cad.Road;

// 离线验收台架要直接喂一张合成图跑断言（Tests/Tests.TaskLib/SimRoadGraphTests.cs）。
// 放在本文件而不是 AssemblyInfo/csproj：这条可见性只为 SimRoadGraph 的 internal 面开，
// 哪天这个类不需要台架了，删这一行和它一起走。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests.TaskLib")]

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  路网访问单例 —— 三维模拟层「问路」的唯一入口。
//
//  为什么要有这个文件：
//  路网求解原先埋在 TripAnimator 里（RealRoute / Polyline / Solver 三个 private static，
//  _graph/_solver/_graphTried 是类级 static 缓存）。外部拿不到 ⇒ 想画卡车轨迹的人只能
//  自己 new 一份 RoadGraph —— 那就是**第二份图**：两份反序列化、两份 Dijkstra 单源缓存、
//  两套吸附半径，存了新路网后还只失效其中一份。本文件把图与解算器收成进程内单例，
//  TripAnimator 降级为调用方（它那份已删）。
//
//  ── 单例语义（照搬原 _graphTried）──
//  懒加载：第一次问路时读 road_network 最新一期存档；**失败只试一次**
//  （没存档 / 空图 / 坏 JSON 都算失败），此后所有查询直接返回「未命中」，
//  不会每帧去敲一次数据库。存了新路网调 <see cref="Invalidate"/> 重置。
//  ★ 路网层永不抛异常：任何一步不成立都返回未命中（<see cref="SimRoute.Hit"/>=false）
//    并带上原因码，让调用方如实降级（画简化直线并标注），而不是让动画崩掉。
//
//  ── 吸附半径怎么定（★ 这是本文件最容易搞错的一个数）──
//  错的取法：「2×节点间距中位数」。节点间距刻画的是**路网自己的采样密度**——这张网
//  中位 19 m，只说明抽中线时每 19 m 打一个点，跟「待吸附的点离路网有多远」毫无关系。
//  按它取半径 ⇒ 50 m ⇒ 采掘单元只命中 28.3%，而这些单元其实全都挨着路。
//
//  对的取法：半径是【待吸附点集 → 路网最近节点】这个**距离分布**的高分位数。
//  实测采掘单元质心：中位 76 m、p90 157 m ⇒ 取到 200 m 时命中 97.9%。
//  所以缺省值 <see cref="DefaultSnapRadiusM"/> = 200 m（≈p95 上取到 25 m 整数倍）。
//  三条要点：
//    ① 半径由**谁来查**决定，不由**网有多密**决定。换一批查询点（排土位置、破碎站、
//       设备实时位）就要重新标定 —— 用 <see cref="Calibrate"/> 现场量，别沿用别人的数。
//    ② 不是越大越好。半径是「允许把这个点当成在路上」的容差；超过它说明该点根本没有
//       路可达，应当**老实报未命中**，而不是硬接到 800 m 外的节点上算出一条假路线。
//       所以取分位数而不是最大值，剩下那 2% 报未命中。
//    ③ 200 m 是**我按上游给的分布替你定的缺省值**，不是从这台机器的数据现测的。
//       要落到某个具体点集上，请先 <see cref="Calibrate"/> 再 <see cref="UseSnapRadius"/>，
//       界面上把 <see cref="SnapRadiusSource"/> 显示出来，别让人以为这是实测值。
//
//  ── 吸附用平面距，不用三维距 ──
//  <see cref="RoadGraph.NearestNode"/> 用的是三维距。台阶上的采掘单元与脚下那条运输路
//  高差可以有几十米，把高差算进「离路多远」会把一个明明有坡道可达的单元判成够不着。
//  本文件的坐标口 <see cref="TryGetRoute"/> 因此按**平面距**排序取最近节点，
//  高差另行报出（<see cref="SimRoute.SrcSnapDzM"/>）供调用方判断。
//  ★ 但 <see cref="RouteByKeys"/>（TripAnimator 走的那条）保留原来的三维距 + 500 m，
//    行为一字不改 —— 见该方法上的存疑记录。
//
//  ── 性能 ──
//  动画 100 ms 一帧、每帧上百个 O-D。故：图/解算器单例（Dijkstra 自带单源缓存）、
//  吸附结果按**精确坐标**记忆（不做坐标量化，量化会在 Voronoi 边界上悄悄换节点）、
//  节点对之间的折线按 (源,汇,重空,权重口径) 记忆。稳定的 O-D 从第二帧起零成本。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次问路没走成真实路网的原因（逐条可数，界面能说清「为什么没走真路」）。</summary>
public enum SimRouteMiss
{
    /// <summary>命中了，没有未命中原因。</summary>
    None = 0,
    /// <summary>没有可用路网（无存档 / 空图 / 坏 JSON）。</summary>
    NoGraph,
    /// <summary>查询点没有坐标（XY 全 0）。★ 绝不把 (0,0) 当真实位置去吸附。</summary>
    NoPosition,
    /// <summary>源端在吸附半径内找不到节点。</summary>
    SourceUnsnapped,
    /// <summary>汇端在吸附半径内找不到节点。</summary>
    SinkUnsnapped,
    /// <summary>源汇吸到同一个节点（零长路径，画不出线）。</summary>
    SameNode,
    /// <summary>路网上两点不连通（或被限坡/闭路挡住）。</summary>
    Unreachable,
    /// <summary>解出来了但折线不足两点（边中线缺失 + 端点重合）。</summary>
    Degenerate,
    /// <summary>求解过程抛了异常（已吞掉，原因见 <see cref="SimRoute.MissText"/>）。</summary>
    Error,
}

/// <summary>
/// 一次问路的结果。<see cref="Hit"/>=false 时 <see cref="Points"/> 为空、
/// <see cref="Miss"/> 给出原因 —— **不抛异常、不返回假路线**。
/// </summary>
public sealed class SimRoute
{
    /// <summary>是否走通了真实路网。false ⇒ 调用方自行降级并如实标注。</summary>
    public bool Hit { get; init; }
    public SimRouteMiss Miss { get; init; } = SimRouteMiss.None;
    /// <summary>未命中原因的中文说明（可直接进界面）。</summary>
    public string MissText { get; init; } = "";

    /// <summary>路径折线（平面，米）。与 <see cref="Points3d"/> 是同一条线的投影，点数一致。</summary>
    public IReadOnlyList<SimPoint> Points { get; init; } = Array.Empty<SimPoint>();
    /// <summary>路径折线（三维，带路面高程；三维层画车用）。</summary>
    public IReadOnlyList<Point3d> Points3d { get; init; } = Array.Empty<Point3d>();

    /// <summary>路网三维里程 m（<see cref="PathResult.LengthM"/>，不是折线平面长）。</summary>
    public double LengthM { get; init; }
    /// <summary>坡度折算后的等效运距 m。</summary>
    public double EquivM { get; init; }
    /// <summary>单程行车时间 min。</summary>
    public double TimeMin { get; init; }

    public string SrcNodeId { get; init; } = "";
    public string DstNodeId { get; init; } = "";
    /// <summary>源端吸附距离 m（按名字对上的为 NaN —— 那不是吸的）。</summary>
    public double SrcSnapM { get; init; } = double.NaN;
    public double DstSnapM { get; init; } = double.NaN;
    /// <summary>源端与所吸节点的高差 m（正=点比路高）。按名字对上的为 NaN。</summary>
    public double SrcSnapDzM { get; init; } = double.NaN;
    public double DstSnapDzM { get; init; } = double.NaN;
    /// <summary>源端是按业务标识（RefId/节点 Id）对上的，不是按坐标吸的。</summary>
    public bool SrcByName { get; init; }
    public bool DstByName { get; init; }

    /// <summary>折线拷贝（交给会被改写的场景对象时用，避免共享缓存里的那份被改）。</summary>
    public List<SimPoint> ToPolyline() => new(Points);

    public override string ToString() => Hit
        ? $"命中：{Points.Count} 点 · {LengthM / 1000:0.00}km（{SrcNodeId}→{DstNodeId}）"
        : $"未命中：{MissText}";
}

/// <summary>命中率计数器（快照，读到的是当时的值，不会被后续查询改掉）。</summary>
public sealed class SimRoadGraphStats
{
    /// <summary>问路总次数（含缓存命中的重复 O-D）。</summary>
    public long Queries { get; internal set; }
    /// <summary>走通真实路网的次数。</summary>
    public long Hits { get; internal set; }
    /// <summary>其中由缓存直接答的次数（不代表命中，只说明没重算）。</summary>
    public long CacheHits { get; internal set; }

    public long MissNoGraph { get; internal set; }
    public long MissNoPosition { get; internal set; }
    public long MissSourceUnsnapped { get; internal set; }
    public long MissSinkUnsnapped { get; internal set; }
    public long MissSameNode { get; internal set; }
    public long MissUnreachable { get; internal set; }
    public long MissDegenerate { get; internal set; }
    public long MissError { get; internal set; }
    /// <summary>靠候选吸附重试才解出来的笔数（= 最近节点落在死片上、换个上路点就通了）。</summary>
    public long CandidateRescues { get; internal set; }

    /// <summary>不重复的节点对数（每帧重问同一条 O-D 不重复计）。</summary>
    public int DistinctOd { get; internal set; }
    /// <summary>其中解出了路径的节点对数。</summary>
    public int DistinctOdHits { get; internal set; }

    public long MissTotal => MissNoGraph + MissNoPosition + MissSourceUnsnapped + MissSinkUnsnapped
                           + MissSameNode + MissUnreachable + MissDegenerate + MissError;

    /// <summary>
    /// 计数自洽：总次数 = 命中 + 各类未命中。**判据用**：任何一条新分支忘了计数，这里立刻为 false。
    /// </summary>
    public bool Balanced => Queries == Hits + MissTotal;

    /// <summary>端到端命中率（按调用次数）。重复问同一条 O-D 会被计多次。</summary>
    public double HitRate => Queries > 0 ? (double)Hits / Queries : 0;
    /// <summary>按不重复节点对算的命中率 —— 报「这批点有多少能走真路」时用这个。</summary>
    public double DistinctHitRate => DistinctOd > 0 ? (double)DistinctOdHits / DistinctOd : 0;

    internal SimRoadGraphStats Snapshot() => (SimRoadGraphStats)MemberwiseClone();

    internal void CountMiss(SimRouteMiss m)
    {
        switch (m)
        {
            case SimRouteMiss.NoGraph: MissNoGraph++; break;
            case SimRouteMiss.NoPosition: MissNoPosition++; break;
            case SimRouteMiss.SourceUnsnapped: MissSourceUnsnapped++; break;
            case SimRouteMiss.SinkUnsnapped: MissSinkUnsnapped++; break;
            case SimRouteMiss.SameNode: MissSameNode++; break;
            case SimRouteMiss.Unreachable: MissUnreachable++; break;
            case SimRouteMiss.Degenerate: MissDegenerate++; break;
            default: MissError++; break;
        }
    }

    public string Text()
    {
        if (Queries == 0) return "路网问路：尚未查询。";
        var parts = new List<string>();
        void P(string name, long v) { if (v > 0) parts.Add($"{name} {v}"); }
        P("无路网", MissNoGraph);
        P("无坐标", MissNoPosition);
        P("源未吸附", MissSourceUnsnapped);
        P("汇未吸附", MissSinkUnsnapped);
        P("源汇同点", MissSameNode);
        P("不连通", MissUnreachable);
        P("路径退化", MissDegenerate);
        P("异常", MissError);
        string miss = parts.Count == 0 ? "无未命中" : string.Join(" · ", parts);
        return $"路网问路 {Queries} 次 · 命中 {Hits}（{HitRate:P1}）· "
             + $"不重复 O-D {DistinctOd} 条命中 {DistinctOdHits}（{DistinctHitRate:P1}）· "
             + $"缓存答 {CacheHits} · 未命中：{miss}";
    }
}

/// <summary>
/// 吸附半径标定结果 —— 把「这批点离路网有多远」量出来，再据此定半径。
/// <para>★ 这里的命中率只是**吸附**命中率（能不能挂到节点上），
/// 端到端还要过连通性那一关，见 <see cref="SimRoadGraphStats.DistinctHitRate"/>。</para>
/// </summary>
public sealed class SimSnapCalibration
{
    private readonly double[] _sorted;

    internal SimSnapCalibration(double[] sorted, int noPosition, string note)
    {
        _sorted = sorted;
        NoPositionCount = noPosition;
        Note = note;
    }

    /// <summary>参与统计的点数（有坐标且图非空）。</summary>
    public int Count => _sorted.Length;
    /// <summary>被剔除的无坐标点数（XY 全 0）。这些点不是「离得远」，是**没有坐标**，不许混进分布里。</summary>
    public int NoPositionCount { get; }
    public string Note { get; }

    /// <summary>分位数（最近秩法，不插值）。q∈[0,1]。</summary>
    public double Quantile(double q)
    {
        if (_sorted.Length == 0) return double.NaN;
        int i = (int)Math.Ceiling(Math.Clamp(q, 0, 1) * _sorted.Length) - 1;
        return _sorted[Math.Clamp(i, 0, _sorted.Length - 1)];
    }

    public double Min => Count > 0 ? _sorted[0] : double.NaN;
    public double P50 => Quantile(0.50);
    public double P90 => Quantile(0.90);
    public double P95 => Quantile(0.95);
    public double P99 => Quantile(0.99);
    public double Max => Count > 0 ? _sorted[^1] : double.NaN;

    /// <summary>半径取 r 时能吸上的比例（只算有坐标的点）。</summary>
    public double SnapRateAt(double radiusM)
    {
        if (_sorted.Length == 0) return 0;
        int n = 0;
        foreach (double d in _sorted) { if (d <= radiusM) n++; else break; }   // 已排序，遇到超的即可停
        return (double)n / _sorted.Length;
    }

    /// <summary>建议半径 m：取 <paramref name="quantile"/> 分位上取到 25 m 的整数倍，夹在 [50, 1000]。</summary>
    public double Recommend(double quantile = 0.95)
    {
        double q = Quantile(quantile);
        if (double.IsNaN(q)) return double.NaN;
        return Math.Clamp(Math.Ceiling(q / 25.0) * 25.0, 50, 1000);
    }

    public string Text(double quantile = 0.95)
    {
        if (Count == 0)
            return $"吸附标定：无可用样本（无坐标点 {NoPositionCount} 个）。{Note}";
        double r = Recommend(quantile);
        string skip = NoPositionCount > 0 ? $"（另有 {NoPositionCount} 个点无坐标，未计入）" : "";
        return $"吸附标定 n={Count}{skip} · 到路网最近节点（平面距）："
             + $"min {Min:0} / p50 {P50:0} / p90 {P90:0} / p95 {P95:0} / p99 {P99:0} / max {Max:0} m "
             + $"⇒ 建议吸附半径 {r:0} m（可吸上 {SnapRateAt(r):P1}，其余老实报未命中）。{Note}";
    }
}

/// <summary>
/// 路网访问单例：图 + 解算器进程内只有一份，所有三维模拟的问路都从这里走。
/// <para>线程安全：全部公开入口在同一把锁内（无竞争时开销可忽略），
/// 免得动画线程与后台预热同时触发反序列化，造出两份图。</para>
/// </summary>
public static class SimRoadGraph
{
    // ── 吸附半径 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 坐标口的缺省吸附半径 m。取值依据见文件头「吸附半径怎么定」：
    /// 采掘单元→路网最近节点 中位 76 m / p90 157 m，200 m 时命中 97.9%。
    /// </summary>
    public const double DefaultSnapRadiusM = 200;

    /// <summary>
    /// 按业务标识问路（<see cref="RouteByKeys"/>）时的坐标兜底半径 m = 500。
    /// 这是 TripAnimator 改造前就在用的数，原样保留以保证行为不变；它**没有**按分布标定过。
    /// 同一个 500 也写在 <c>HaulResolver.SnapRadiusM</c>，两处口径一致。
    /// </summary>
    public const double LegacyKeySnapRadiusM = 500;

    private static double _snapRadiusM = DefaultSnapRadiusM;

    /// <summary>坐标口当前使用的吸附半径 m。改它请走 <see cref="UseSnapRadius"/>（要带来源说明）。</summary>
    public static double SnapRadiusM { get { lock (_lock) return _snapRadiusM; } }

    /// <summary>
    /// 半径这个数打哪来 —— 界面必须显示它，免得把缺省值当实测值。
    /// </summary>
    public static string SnapRadiusSource { get; private set; }
        = $"缺省 {DefaultSnapRadiusM:0} m（按上游实测分布 p90=157 m 上取，**未在本机现场标定**）";

    /// <summary>
    /// 用标定出来的半径覆盖缺省值。<paramref name="source"/> 必须写清这个数是怎么来的
    /// （例：「2026-08 采掘单元 193 个点 p95=188 m ⇒ 200 m」），它会原样进界面。
    /// </summary>
    public static void UseSnapRadius(double radiusM, string source)
    {
        if (!(radiusM > 0) || double.IsInfinity(radiusM))
            throw new ArgumentOutOfRangeException(nameof(radiusM), radiusM, "吸附半径必须是正的有限值。");
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("必须说明半径的来源（会显示在界面上）。", nameof(source));
        lock (_lock)
        {
            _snapRadiusM = radiusM;
            SnapRadiusSource = source;
            _snapCache.Clear();          // 半径变了，吸附记忆全作废
        }
    }

    // ── 单例状态 ─────────────────────────────────────────────────────────────

    private static readonly object _lock = new();
    private static RoadGraph? _graph;
    private static DijkstraPathSolver? _solver;
    private static bool _tried;
    private static string _label = "路网未装载";

    private static int _loadAttempts;
    private static int _loadCount;

    private static readonly SimRoadGraphStats _stats = new();

    // (x, y, radius) → 吸附结果。★ 精确坐标做键，不量化：量化会在两节点等距处悄悄换节点。
    private static readonly Dictionary<(double X, double Y, double R), SnapHit> _snapCache = new();
    // (源节点, 汇节点, 重车, 权重口径) → 折线。Dijkstra 自带单源缓存，这里省的是折线重建。
    private static readonly Dictionary<(string Src, string Dst, bool Loaded, WeightMode Mode), PathLeg> _legCache = new();

    /// <summary>缓存条目上限（防长时间运行无限涨）。超了整体清空，重算即可。</summary>
    private const int MaxCacheEntries = 20000;

    // ── 对外只读状态 ─────────────────────────────────────────────────────────

    /// <summary>路网可用（已成功装载一张非空图）。</summary>
    public static bool Available { get { lock (_lock) return Solver() != null; } }

    /// <summary>路网来源文案（存档名 + 节点/边数，或不可用的原因）。</summary>
    public static string Label { get { lock (_lock) { Solver(); return _label; } } }

    /// <summary>
    /// 单例图本体（只读用途：自己找节点、量距离）。**不要**拿它去 new 第二个解算器 ——
    /// 要解路径请调 <see cref="TryGetRoute"/>，那条路上有缓存和计数。
    /// </summary>
    public static RoadGraph? Graph { get { lock (_lock) { Solver(); return _graph; } } }

    public static int NodeCount { get { lock (_lock) { Solver(); return _graph?.NodeCount ?? 0; } } }
    public static int EdgeCount { get { lock (_lock) { Solver(); return _graph?.EdgeCount ?? 0; } } }

    /// <summary>
    /// 全网中线（每条边一串三维点）。<b>给"哪些路本期在走、哪些不走"那张底图用</b>。
    /// <para>没有路网时返回空表。返回的是**只读快照**，调用方随便持有。</para>
    /// </summary>
    public static IReadOnlyList<double[]> AllCenterlines()
    {
        lock (_lock)
        {
            var g = Solver() != null ? _graph : null;
            if (g == null) return Array.Empty<double[]>();
            var outp = new List<double[]>(g.EdgeCount);
            foreach (var e in g.Edges)
            {
                var cl = e.Centerline;
                if (cl == null || cl.Count < 2) continue;
                var flat = new double[cl.Count * 3];
                for (int i = 0; i < cl.Count; i++)
                {
                    flat[3 * i] = cl[i].X; flat[3 * i + 1] = cl[i].Y; flat[3 * i + 2] = cl[i].Z;
                }
                outp.Add(flat);
            }
            return outp;
        }
    }

    /// <summary>
    /// 实际反序列化出图的次数。**判据用**：单例成立 ⇒ 无论问多少次路，这个数 ≤ 1
    /// （<see cref="Invalidate"/> 之后才会再涨）。
    /// </summary>
    public static int GraphLoadCount { get { lock (_lock) return _loadCount; } }

    /// <summary>
    /// 尝试装载的次数（含失败）。**判据用**：「失败只试一次」⇒ 装不上时反复问路，这个数恒为 1。
    /// </summary>
    public static int GraphLoadAttempts { get { lock (_lock) return _loadAttempts; } }

    /// <summary>命中率计数器快照。</summary>
    public static SimRoadGraphStats Stats { get { lock (_lock) return _stats.Snapshot(); } }

    /// <summary>预热（把首次读库的开销挪出动画帧）。返回是否装上了。</summary>
    public static bool Warmup() { lock (_lock) return Solver() != null; }

    /// <summary>
    /// 丢弃路网缓存（存了新路网后调用）。图、解算器、吸附/折线记忆、计数器**全部**重置 ——
    /// 换了一张网，之前那些命中率不再代表当前这张网。吸附半径与其来源说明保留。
    /// <para><see cref="GraphLoadCount"/>/<see cref="GraphLoadAttempts"/> 一并归零：
    /// 它们数的是「当前这一代图装了几次」，不是进程累计。</para>
    /// </summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _graph = null;
            _solver = null;
            _tried = false;
            _label = "路网未装载";
            _loadAttempts = 0;
            _loadCount = 0;
            _snapCache.Clear();
            _legCache.Clear();
            ResetStatsCore();
        }
    }

    /// <summary>只清计数器（图与缓存保留）—— 想量「这一批点的命中率」时先清一次。</summary>
    public static void ResetStats() { lock (_lock) ResetStatsCore(); }

    /// <summary>
    /// 【判据台架专用】直接挂一张现成的图，跳过读库。用来在没有工程库的环境里跑离线验收。
    /// <para>★ 挂上之后 <see cref="Label"/> 会带「外挂图」前缀 —— 万一漏到界面上能一眼看出来，
    /// 不会被当成真的路网存档。生产代码不要调它：图只该有一个来源（road_network 最新一期）。</para>
    /// </summary>
    internal static void AttachForTest(RoadGraph? graph, string label = "台架外挂图")
    {
        lock (_lock)
        {
            _snapCache.Clear();
            _legCache.Clear();
            ResetStatsCore();
            _loadAttempts = 0;
            _loadCount = 0;
            _tried = true;                       // 不再去读库
            _graph = graph;
            _solver = graph == null ? null : new DijkstraPathSolver(graph);
            if (graph != null) _loadCount++;
            _label = graph == null ? $"【外挂图】{label}：空" : $"【外挂图】{label}（节点 {graph.NodeCount} · 边 {graph.EdgeCount}）";
        }
    }

    private static void ResetStatsCore()
    {
        _stats.Queries = 0; _stats.Hits = 0; _stats.CacheHits = 0;
        _stats.MissNoGraph = 0; _stats.MissNoPosition = 0;
        _stats.MissSourceUnsnapped = 0; _stats.MissSinkUnsnapped = 0;
        _stats.MissSameNode = 0; _stats.MissUnreachable = 0;
        _stats.MissDegenerate = 0; _stats.MissError = 0;
        // 不重复 O-D 数跟着折线缓存走：缓存没清就还是那些条，清了才归零。
        _stats.DistinctOd = _legCache.Count;
        _stats.DistinctOdHits = _legCache.Values.Count(v => v.Ok);
    }

    /// <summary>一段可读的现状报告（路网 + 半径 + 命中率），直接进界面/日志。</summary>
    public static string Report()
    {
        lock (_lock)
        {
            Solver();
            return $"{_label}\n吸附半径：{_snapRadiusM:0} m —— {SnapRadiusSource}\n{_stats.Snapshot().Text()}";
        }
    }

    // ── 问路：坐标口（新调用方用这个）───────────────────────────────────────

    /// <summary>
    /// 源点 → 汇点求一条真实路网折线。**解不出来返回未命中，不抛异常、不给假线**。
    /// <para>吸附按**平面距**（高差另报，见 <see cref="SimRoute.SrcSnapDzM"/>），
    /// 半径缺省取 <see cref="SnapRadiusM"/>。</para>
    /// <para>★ XY 全 0 的点视为「没有坐标」，直接判 <see cref="SimRouteMiss.NoPosition"/> ——
    /// 绝不把 (0,0) 当真实位置吸到离原点最近的节点上，那会算出一条看着很正常的假路线。</para>
    /// </summary>
    /// <param name="snapRadiusM">吸附半径 m；NaN/&lt;=0 表示用 <see cref="SnapRadiusM"/>。</param>
    public static SimRoute TryGetRoute(Point3d src, Point3d dst,
                                       double snapRadiusM = double.NaN,
                                       bool loaded = true,
                                       WeightMode mode = WeightMode.Distance)
    {
        lock (_lock)
        {
            _stats.Queries++;
            try
            {
                var solver = Solver();
                var g = _graph;
                if (solver == null || g == null) return Missed(SimRouteMiss.NoGraph, _label);

                double r = snapRadiusM > 0 && !double.IsNaN(snapRadiusM) ? snapRadiusM : _snapRadiusM;

                if (!HasPosition(src)) return Missed(SimRouteMiss.NoPosition, "源点没有坐标（XY 全 0）");
                if (!HasPosition(dst)) return Missed(SimRouteMiss.NoPosition, "汇点没有坐标（XY 全 0）");

                var s = SnapCached(g, src, r);
                if (s.Node == null) return Missed(SimRouteMiss.SourceUnsnapped, $"源点 {r:0} m 内没有路网节点");
                var d = SnapCached(g, dst, r);
                if (d.Node == null) return Missed(SimRouteMiss.SinkUnsnapped, $"汇点 {r:0} m 内没有路网节点");

                var first = Between(g, solver, s.Node, d.Node, loaded, mode,
                                    s.DistM, s.DzM, false, d.DistM, d.DzM, false);
                if (first.Hit || first.Miss != SimRouteMiss.Unreachable || CandidateFanout <= 1)
                    return first;

                // ── 候选吸附重试（只在"不连通"时才做）──────────────────────────────
                //
                //  吸附原本只取**最近的那一个**节点。而现场路网常常是碎的
                //  （实测 1900 节点 / 1827 边 ⇒ 127 个连通分量，最大一块只占 9.2%），
                //  于是块体旁边最近的节点很可能落在一个几十节点的**死片**里 —— 必然"不连通"，
                //  而同一个吸附半径内往往就有主网上的节点。
                //
                //  ⇒ 半径内取若干候选（按距离升序），挑**第一对真的解得出路**的。
                //    这不是造假连通：路径全程仍然只走路网的边，Dijkstra 一步没变，
                //    变的只是"从哪个节点上路"这个选择 —— 那本来就该选个上得去的。
                //
                //  代价：只有第一次尝试失败（Unreachable）才进这条路；候选数封顶
                //  <see cref="CandidateFanout"/>，最坏 k² 次求解，k 默认 6。
                // ── 先按【连通分量】配对：源汇各取每块的最近入口，挑同属一块且总接入距最短的 ──
                //   这才是"就近汇入路网"：入口不是全局最近的那个点，而是**够得着对面的那块**里最近的点。
                double rr = Math.Max(r, ComponentSearchRadiusM);
                var sPer = NearestPerComponent(g, src, rr);
                var dPer = NearestPerComponent(g, dst, rr);
                var shared = sPer.Keys.Where(dPer.ContainsKey)
                                      .OrderBy(c => sPer[c].DistM + dPer[c].DistM)
                                      .ToList();

                _quiet = true;                                           // 重试期间一律不记账
                try
                {
                    // ★ 按**接入 + 路网里程的总和**选，不是只看接入距。
                    //
                    //   只看接入距会挑出"入口很近、但进去之后被迫绕一大圈"的那个分量 ——
                    //   实测出过 8.78 km / 261 折点、在采场里兜两圈的路径（现场反馈"线路有点绕"）。
                    //   接入近 ≠ 路近。要的是**门到门**最短，所以候选分量逐个解一次再比总和。
                    //
                    //   代价：shared 通常只有 1~2 个；封顶 ComponentTryMax 个，避免碎网上退化成全解。
                    SimRoute? best = null;
                    double bestCost = double.MaxValue;
                    int tried = 0;
                    foreach (int c in shared)
                    {
                        if (tried++ >= ComponentTryMax) break;
                        var sh = sPer[c]; var dh = dPer[c];
                        var cand = Between(g, solver, sh.Node!, dh.Node!, loaded, mode,
                                           sh.DistM, sh.DzM, false, dh.DistM, dh.DzM, false);
                        if (!cand.Hit) continue;
                        // ★ 接入段**不是路**：卡车不能越野跑一公里，它必须先上到路上。
                        //   把接入距与路网里程等价相加（第一版就是这么写的）会挑出
                        //   「路网里程 0.2 km、接入 1119 m」这种候选 —— 总和确实最小，物理上却是假的。
                        //   ⇒ 分层：先要求接入够短（AcceptableApproachM），在合格的里面挑路最短的；
                        //     一个合格的都没有时，才退回按加权总和比（接入按 ApproachPenalty 倍计价）。
                        double appr = Math.Max(sh.DistM, dh.DistM);
                        bool ok = appr <= AcceptableApproachM;
                        double cost = ok
                            ? cand.LengthM                                   // 合格档：只比路
                            : 1e9 + cand.LengthM + ApproachPenalty * (sh.DistM + dh.DistM);
                        if (cost < bestCost) { bestCost = cost; best = cand; }
                    }
                    if (best != null)
                    {
                        _stats.MissUnreachable--;
                        _stats.Hits++;
                        _stats.CandidateRescues++;
                        return best;
                    }

                    var sc = NearestMany(g, src, r, CandidateFanout);
                    var dc = NearestMany(g, dst, r, CandidateFanout);
                    for (int i = 0; i < sc.Count; i++)
                        for (int j = 0; j < dc.Count; j++)
                        {
                            if (i == 0 && j == 0) continue;              // 已经试过
                            var cand = Between(g, solver, sc[i].Node!, dc[j].Node!, loaded, mode,
                                               sc[i].DistM, sc[i].DzM, false, dc[j].DistM, dc[j].DzM, false);
                            if (!cand.Hit) continue;

                            // 成功：把刚才那条「不连通」改记成命中 —— 一次问路仍然只有一条账
                            _stats.MissUnreachable--;
                            _stats.Hits++;
                            _stats.CandidateRescues++;
                            return cand;
                        }
                }
                finally { _quiet = false; }
                return first;
            }
            catch (Exception ex) { return Missed(SimRouteMiss.Error, Short(ex)); }
        }
    }

    /// <summary>同 <see cref="TryGetRoute"/> 的平面重载（Z 未知时用；吸附本就按平面距，结果一致）。</summary>
    public static SimRoute TryGetRoute(SimPoint src, SimPoint dst,
                                       double snapRadiusM = double.NaN,
                                       bool loaded = true,
                                       WeightMode mode = WeightMode.Distance)
        => TryGetRoute(new Point3d(src.X, src.Y, 0), new Point3d(dst.X, dst.Y, 0), snapRadiusM, loaded, mode);

    /// <summary>
    /// 把一个点吸到路网节点上（诊断/界面用：告诉用户「这个单元离路 137 m」）。
    /// 未吸上返回 Node=null，距离仍如实给出（用无限半径量的真实距离）。
    /// </summary>
    public static (RoadNode? Node, double DistM, double DzM) TrySnap(Point3d p, double snapRadiusM = double.NaN)
    {
        lock (_lock)
        {
            var g = Solver() != null ? _graph : null;
            if (g == null || !HasPosition(p)) return (null, double.NaN, double.NaN);
            double r = snapRadiusM > 0 && !double.IsNaN(snapRadiusM) ? snapRadiusM : _snapRadiusM;
            var near = Nearest2d(g, p);
            if (near.Node == null) return (null, double.NaN, double.NaN);
            RoadNode? within = near.DistM <= r ? near.Node : null;   // 超半径：节点不给，距离照给（诊断要看这个数）
            return (within, near.DistM, near.DzM);
        }
    }

    // ── 问路：业务标识口（TripAnimator 走这条）───────────────────────────────

    /// <summary>
    /// 先按业务标识（RoadNode.RefId / RoadNode.Id）精确匹配节点，匹配不上再按坐标兜底吸附。
    /// <para>
    /// ★ 这条是从 TripAnimator 原样搬过来的，**行为一字不改**，包括两处存疑之处：
    /// ① 兜底吸附走 <see cref="RoadGraph.NearestNode"/>，那是**三维距**；
    /// ② TripAnimator 调用时汇点 Z 传的是 0（<c>SinkNode.Z</c> 明明有值）。
    /// 路网节点 Z 是真实标高，于是「三维距 = 标高本身」通常就已经超过 500 m ⇒
    /// **这条坐标兜底实际上大概率永远吸不上**，只有名称匹配那一支在起作用。
    /// 这是改造前就存在的问题，本次不动（任务要求行为不变）；要修请单独立一条，
    /// 修法是把汇点 Z 填真值并改用平面距（新调用方请直接用 <see cref="TryGetRoute"/>）。
    /// </para>
    /// </summary>
    internal static SimRoute RouteByKeys(IReadOnlyList<string> srcKeys, Point3d srcPos,
                                         IReadOnlyList<string> dstKeys, Point3d dstPos,
                                         double snapRadiusM = LegacyKeySnapRadiusM,
                                         bool loaded = true,
                                         WeightMode mode = WeightMode.Distance)
    {
        lock (_lock)
        {
            _stats.Queries++;
            try
            {
                var solver = Solver();
                var g = _graph;
                if (solver == null || g == null) return Missed(SimRouteMiss.NoGraph, _label);

                var s = FindNodeLegacy(g, srcKeys, srcPos, snapRadiusM);
                if (s.Node == null) return Missed(SimRouteMiss.SourceUnsnapped, "源端既没匹配到节点也没吸附上");
                var d = FindNodeLegacy(g, dstKeys, dstPos, snapRadiusM);
                if (d.Node == null) return Missed(SimRouteMiss.SinkUnsnapped, "汇端既没匹配到节点也没吸附上");

                return Between(g, solver, s.Node, d.Node, loaded, mode,
                               s.DistM, s.DzM, s.ByName, d.DistM, d.DzM, d.ByName);
            }
            catch (Exception ex) { return Missed(SimRouteMiss.Error, Short(ex)); }
        }
    }

    // ── 内部：装载 / 求解 / 折线 ─────────────────────────────────────────────

    /// <summary>
    /// 装载最新一期路网存档并建解算器。**懒加载 + 失败只试一次**（原 TripAnimator._graphTried 语义）。
    /// 调用方必须已持锁。
    /// </summary>
    internal static DijkstraPathSolver? Solver()
    {
        if (_tried) return _solver;
        _tried = true;
        _loadAttempts++;
        try
        {
            // All() 按 captured_at 倒序，第一条即最新一期路网。
            var archive = EquipmentDataContext.RoadNetworks.All().FirstOrDefault();
            if (archive == null) { _label = "无路网存档"; return null; }
            var g = RoadGraphSerializer.FromJson(archive.GraphJson);
            if (g.NodeCount == 0 || g.EdgeCount == 0) { _label = $"路网存档「{archive.Name}」为空图"; return null; }
            _graph = g;
            _compOf = null;                     // 换图 ⇒ 分量索引作废
            _solver = new DijkstraPathSolver(g);
            _loadCount++;
            _label = $"路网存档「{archive.Name}」（节点 {g.NodeCount} · 边 {g.EdgeCount}）";
        }
        catch (Exception ex) { _label = $"路网不可用（{Short(ex)}）"; _graph = null; _solver = null; }
        return _solver;
    }

    private static SimRoute Between(RoadGraph g, DijkstraPathSolver solver, RoadNode src, RoadNode dst,
                                    bool loaded, WeightMode mode,
                                    double srcSnap, double srcDz, bool srcByName,
                                    double dstSnap, double dstDz, bool dstByName)
    {
        // ★ 与改造前一致：源汇同一节点判未命中（零长路径画不出线）。用序数比较，不忽略大小写。
        if (string.Equals(src.Id, dst.Id, StringComparison.Ordinal))
            return Missed(SimRouteMiss.SameNode, $"源汇吸到同一节点 {src.Id}");

        var leg = Leg(g, solver, src.Id, dst.Id, loaded, mode);
        if (!leg.Ok) return Missed(leg.Miss, leg.MissText);

        if (!_quiet) _stats.Hits++;
        return new SimRoute
        {
            Hit = true,
            Points = leg.P2,
            Points3d = leg.P3,
            LengthM = leg.LengthM,
            EquivM = leg.EquivM,
            TimeMin = leg.TimeMin,
            SrcNodeId = src.Id,
            DstNodeId = dst.Id,
            SrcSnapM = srcByName ? double.NaN : srcSnap,
            DstSnapM = dstByName ? double.NaN : dstSnap,
            SrcSnapDzM = srcByName ? double.NaN : srcDz,
            DstSnapDzM = dstByName ? double.NaN : dstDz,
            SrcByName = srcByName,
            DstByName = dstByName,
        };
    }

    /// <summary>节点对之间的折线（带缓存）。缓存里连「不可达」也记 —— 免得每帧重跑一次注定失败的 Dijkstra。</summary>
    private static PathLeg Leg(RoadGraph g, DijkstraPathSolver solver, string srcId, string dstId,
                               bool loaded, WeightMode mode)
    {
        var key = (srcId, dstId, loaded, mode);
        if (_legCache.TryGetValue(key, out var cached)) { _stats.CacheHits++; return cached; }

        PathLeg leg;
        var pr = solver.FindPath(srcId, dstId, new PathQuery { Loaded = loaded, Mode = mode });
        if (!pr.Feasible || pr.EdgeIds.Count == 0)
            leg = PathLeg.Failed(SimRouteMiss.Unreachable, $"路网上 {srcId}→{dstId} 不连通");
        else
        {
            var p3 = Polyline3d(g, pr, srcId);
            leg = p3.Count >= 2
                ? PathLeg.Made(p3, pr)
                : PathLeg.Failed(SimRouteMiss.Degenerate, $"{srcId}→{dstId} 折线不足两点");
        }

        // 缓存满了整体清空；不重复 O-D 的计数必须跟着归零，否则 DistinctHitRate 会 > 1。
        if (_legCache.Count >= MaxCacheEntries) { _legCache.Clear(); _stats.DistinctOdHits = 0; }
        _legCache[key] = leg;                       // 缓存照存（重试解出来的路下次能直接用）

        // ★ 但**不记账**：候选重试里的中间尝试不是调用方问的那条 O-D。
        //   不静默的话，一次问路会把 k² 个节点对全记成"不重复 O-D"，
        //   DistinctHitRate 立刻失真（判据 J3 抓到过）。
        if (!_quiet)
        {
            _stats.DistinctOd = _legCache.Count;
            if (leg.Ok) _stats.DistinctOdHits++;
        }
        return leg;
    }

    /// <summary>
    /// 由寻径结果拼三维折线（沿行进方向；逆行边把中线倒过来）。
    /// ★ 去重判据是**平面**距 &lt; 1e-6 —— 与改造前逐字一致，
    ///   这样 <see cref="Project"/> 出来的平面折线与旧实现一模一样。
    /// </summary>
    internal static List<Point3d> Polyline3d(RoadGraph g, PathResult pr, string startNodeId)
    {
        var pts = new List<Point3d>();
        string cur = startNodeId;
        foreach (var eid in pr.EdgeIds)
        {
            var e = g.GetEdge(eid);
            if (e == null) continue;
            bool reversed = string.Equals(e.ToId, cur, StringComparison.OrdinalIgnoreCase);
            var cl = e.Centerline;
            if (cl == null || cl.Count < 2)
            {
                var na = g.GetNode(reversed ? e.ToId : e.FromId);
                var nb = g.GetNode(reversed ? e.FromId : e.ToId);
                if (na != null) Push(pts, na.Position);
                if (nb != null) Push(pts, nb.Position);
            }
            else
            {
                var seq = reversed ? cl.Reverse().ToList() : cl.ToList();
                foreach (var p in seq) Push(pts, p);
            }
            cur = reversed ? e.FromId : e.ToId;
        }
        return pts;
    }

    /// <summary>三维折线 → 平面折线（同一条线的投影，点数一致；口径只有一份）。</summary>
    internal static List<SimPoint> Project(IReadOnlyList<Point3d> pts)
    {
        var o = new List<SimPoint>(pts.Count);
        foreach (var p in pts) o.Add(new SimPoint(p.X, p.Y));
        return o;
    }

    private static void Push(List<Point3d> pts, Point3d p)
    {
        // 平面重合即视为同一点（与旧实现相同）：中线上下重叠但 XY 相同的点不重复入列。
        if (pts.Count > 0 && Dist2d(pts[^1], p) < 1e-6) return;
        pts.Add(p);
    }

    // ── 内部：吸附 ───────────────────────────────────────────────────────────

    private readonly record struct SnapHit(RoadNode? Node, double DistM, double DzM);

    private static SnapHit SnapCached(RoadGraph g, Point3d p, double radiusM)
    {
        var key = (p.X, p.Y, radiusM);
        if (_snapCache.TryGetValue(key, out var hit)) return hit;

        var near = Nearest2d(g, p);
        hit = near.Node != null && near.DistM <= radiusM ? near : new SnapHit(null, near.DistM, near.DzM);

        if (_snapCache.Count >= MaxCacheEntries) _snapCache.Clear();
        _snapCache[key] = hit;
        return hit;
    }

    /// <summary>
    /// 平面距最近的节点（无半径限制，距离如实给）。
    /// 用平面距而不是 <see cref="RoadGraph.NearestNode"/> 的三维距：
    /// 台阶上的作业点与脚下运输路的高差不该被算成「离路远」。
    /// </summary>
    /// <summary>
    /// 候选吸附的扇出 k：只在最近节点解不出（不连通）时，才在半径内多试 k 个候选。
    /// 1 = 关闭（回到"只取最近"）。默认 6 —— 再大收益递减而最坏 k² 次求解。
    /// </summary>
    /// <para>实测响应（真路网 1900 节点 / 1827 边、2026-08 的 172 笔问路）：
    /// 半径 400m 下扇出 1→6→12 的命中率是 4.1% → 19.2% → 23.3%，
    /// 而半径从 400m 放到 1500m **一点用都没有**（碎片之间真的没路）。
    /// ⇒ 扇出是主要杠杆，取 12；再大收益递减而最坏 k² 次求解。</para>
    public static int CandidateFanout { get; set; } = 12;

    /// <summary>
    /// 按分量找入口时的搜索半径 m。<b>比普通吸附半径大得多是刻意的</b>：
    /// 普通吸附问的是"这个点贴着哪条路"，而这里问的是"要汇入能到对面的那块网，最近的入口在哪"——
    /// 后者天然更远（碎片之间的缺口中位数就有 96m）。取 1500m：再大也没有新分量进来（实测饱和）。
    /// </summary>
    public static double ComponentSearchRadiusM { get; set; } = 1500.0;

    /// <summary>
    /// 按分量选入口时最多解几个候选分量（每个一次 Dijkstra）。
    /// 4 已经覆盖绝大多数情形；碎网上分量多，不封顶会退化成"全网逐块解一遍"。
    /// </summary>
    public static int ComponentTryMax { get; set; } = 4;

    /// <summary>
    /// 可接受的接入距 m：块体到路网入口这一段**不是路**，卡车得越野过去，所以越短越好。
    /// 候选分量里接入 ≤ 本值的算"合格"，合格的之间只比路网里程；一个合格的都没有时才放宽。
    /// 300m ≈ 一个采掘单元的走向长量级，现场可接受的"开到路上"的距离。
    /// </summary>
    public static double AcceptableApproachM { get; set; } = 300.0;

    /// <summary>没有合格候选时，接入距相对路网里程的计价倍数（越野比走路贵）。</summary>
    public static double ApproachPenalty { get; set; } = 6.0;

    // ═══ 连通分量索引 ═══════════════════════════════════════════════════════
    //
    //  现场路网常常是碎的（实测 1900 节点 / 1827 边 ⇒ 127 块，最大一块只占 9.2%）。
    //  只按"最近节点"吸附，块体旁边那个节点很可能落在一个几十节点的**死片**上 ——
    //  于是无论源汇多近都判"不连通"，而同一个方向上往往就有主网的入口。
    //
    //  ⇒ 按分量建索引，查询时**每个分量各取一个最近入口**，再挑一对源汇同属一块的。
    //    这不是造假连通：路径仍然全程走路网的边，只是"从哪儿汇入路网"这个选择做对了。
    //    分量在图不变时是常量，挂图时算一次即可（BFS，O(V+E)）。

    private static Dictionary<string, int>? _compOf;

    private static Dictionary<string, int> Components(RoadGraph g)
    {
        if (_compOf != null) return _compOf;
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in g.Nodes) adj[n.Id] = new List<string>();
        foreach (var e in g.Edges)
        {
            if (adj.TryGetValue(e.FromId, out var la)) la.Add(e.ToId);
            if (adj.TryGetValue(e.ToId, out var lb)) lb.Add(e.FromId);
        }
        var comp = new Dictionary<string, int>(StringComparer.Ordinal);
        int k = 0;
        foreach (var n in g.Nodes)
        {
            if (comp.ContainsKey(n.Id)) continue;
            var q = new Queue<string>();
            q.Enqueue(n.Id); comp[n.Id] = k;
            while (q.Count > 0)
            {
                var u = q.Dequeue();
                foreach (var v in adj[u])
                    if (!comp.ContainsKey(v)) { comp[v] = k; q.Enqueue(v); }
            }
            k++;
        }
        _compOf = comp;
        return comp;
    }

    /// <summary>各连通分量里离 <paramref name="p"/> 最近的那个节点（分量号 → 入口）。</summary>
    private static Dictionary<int, SnapHit> NearestPerComponent(RoadGraph g, Point3d p, double radiusM)
    {
        var comp = Components(g);
        var best = new Dictionary<int, SnapHit>();
        foreach (var n in g.Nodes)
        {
            double dd = Dist2d(n.Position, p);
            if (dd > radiusM) continue;
            if (!comp.TryGetValue(n.Id, out int c)) continue;
            if (best.TryGetValue(c, out var cur) && cur.DistM <= dd) continue;
            best[c] = new SnapHit(n, dd, p.Z - n.Position.Z);
        }
        return best;
    }

    /// <summary>半径内最近的 k 个节点（按平面距升序）。</summary>
    private static List<SnapHit> NearestMany(RoadGraph g, Point3d p, double radiusM, int k)
    {
        var all = new List<SnapHit>();
        foreach (var n in g.Nodes)
        {
            double dd = Dist2d(n.Position, p);
            if (dd <= radiusM) all.Add(new SnapHit(n, dd, p.Z - n.Position.Z));
        }
        all.Sort((x, y) => x.DistM.CompareTo(y.DistM));
        if (all.Count > k) all.RemoveRange(k, all.Count - k);
        return all;
    }

    private static SnapHit Nearest2d(RoadGraph g, Point3d p)
    {
        RoadNode? best = null;
        double bestD = double.MaxValue;
        foreach (var n in g.Nodes)
        {
            double d = Dist2d(n.Position, p);
            if (d < bestD) { bestD = d; best = n; }
        }
        return best == null
            ? new SnapHit(null, double.NaN, double.NaN)
            : new SnapHit(best, bestD, p.Z - best.Position.Z);
    }

    /// <summary>业务标识优先、坐标兜底的节点定位（改造前 TripAnimator.FindNode 的原样搬迁）。</summary>
    private static (RoadNode? Node, double DistM, double DzM, bool ByName) FindNodeLegacy(
        RoadGraph g, IReadOnlyList<string> keys, Point3d pos, double radiusM)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            foreach (var n in g.Nodes)
                if (string.Equals(n.RefId, key, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(n.Id, key, StringComparison.OrdinalIgnoreCase))
                    return (n, double.NaN, double.NaN, true);
        }
        // ★ 原样保留：三维距 + 500 m（见 RouteByKeys 注释里的存疑记录）。
        if (HasPosition(pos))
        {
            var n = g.NearestNode(pos, radiusM);
            return n == null ? (null, double.NaN, double.NaN, false)
                             : (n, n.Position.DistanceTo(pos), pos.Z - n.Position.Z, false);
        }
        return (null, double.NaN, double.NaN, false);
    }

    // ── 标定 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 量一批点到路网的距离分布，据此给出建议吸附半径。
    /// **这是定半径的唯一正确方法** —— 别用节点间距，那是路网自己的采样密度，不是这批点离路多远。
    /// <para>XY 全 0 的点不计入分布（它们不是「离得远」，是没有坐标），单独计数。</para>
    /// </summary>
    public static SimSnapCalibration Calibrate(IEnumerable<Point3d> points)
    {
        lock (_lock)
        {
            var solver = Solver();
            var g = _graph;
            if (solver == null || g == null)
                return new SimSnapCalibration(Array.Empty<double>(), 0, _label);

            var ds = new List<double>();
            int noPos = 0;
            foreach (var p in points)
            {
                if (!HasPosition(p)) { noPos++; continue; }
                var near = Nearest2d(g, p);
                if (near.Node != null) ds.Add(near.DistM);
            }
            ds.Sort();
            return new SimSnapCalibration(ds.ToArray(), noPos, $"路网：{_label}");
        }
    }

    /// <summary>平面点的标定重载。</summary>
    public static SimSnapCalibration Calibrate(IEnumerable<SimPoint> points)
        => Calibrate(points.Select(p => new Point3d(p.X, p.Y, 0)));

    // ── 小工具 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 候选吸附重试期间**不记账**。
    /// <para>★ 一次问路只能落一条账：不静默的话，重试里每次失败的 <see cref="Between"/>
    /// 都会记一条未命中，于是 <c>Queries == Hits + MissTotal</c> 这条自洽当场破掉
    /// （判据 J3 立刻红——它就是这么被抓出来的）。</para>
    /// </summary>
    private static bool _quiet;

    private static SimRoute Missed(SimRouteMiss m, string text)
    {
        if (!_quiet) _stats.CountMiss(m);
        return new SimRoute { Hit = false, Miss = m, MissText = text };
    }

    /// <summary>坐标是否有效（XY 任一非零即认为有位置；Z=0 是合法标高）。与 HaulResolver 同一判据。</summary>
    private static bool HasPosition(Point3d p) => Math.Abs(p.X) > 1e-6 || Math.Abs(p.Y) > 1e-6;

    private static double Dist2d(in Point3d a, in Point3d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }

    /// <summary>缓存里的一段路（含「不可达」这种失败态 —— 失败也要记，免得每帧重跑）。</summary>
    private sealed class PathLeg
    {
        public bool Ok { get; private init; }
        public SimRouteMiss Miss { get; private init; }
        public string MissText { get; private init; } = "";
        public List<Point3d> P3 { get; private init; } = new();
        public List<SimPoint> P2 { get; private init; } = new();
        public double LengthM { get; private init; }
        public double EquivM { get; private init; }
        public double TimeMin { get; private init; }

        public static PathLeg Made(List<Point3d> p3, PathResult pr) => new()
        {
            Ok = true,
            P3 = p3,
            P2 = Project(p3),
            LengthM = pr.LengthM,
            EquivM = pr.EquivM,
            TimeMin = pr.TimeMin,
        };

        public static PathLeg Failed(SimRouteMiss m, string text) => new() { Ok = false, Miss = m, MissText = text };
    }
}
