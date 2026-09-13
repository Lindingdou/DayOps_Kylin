// 忠实移植自原 PitMine3D Modules/RoadLib/Network/RoadGraph.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace PitMine3D.Kylin.Cad.Road;

/// <summary>轻量三维点（double 精度，矿区 UTM 坐标量级大，不用 float）。RoadLib 自包含，不引几何内核。</summary>
public readonly record struct Point3d(double X, double Y, double Z)
{
    /// <summary>三维距离。</summary>
    public double DistanceTo(in Point3d o)
    {
        double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>平面（水平）距离，用于算坡度。</summary>
    public double HorizontalDistanceTo(in Point3d o)
    {
        double dx = X - o.X, dy = Y - o.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>节点类型（设计 §3，6 类）。</summary>
public enum RoadNodeType { Loading, Unloading, Junction, Portal, Entry, Waypoint }

/// <summary>边状态（设计 §3，3 态）。</summary>
public enum RoadEdgeStatus { Open, Maintenance, Closed }

/// <summary>路网节点：装载点 / 卸载点 / 交叉口 / 坡道端口 / 出入口 / 转折点。</summary>
public sealed class RoadNode
{
    public string Id { get; }
    public RoadNodeType Type { get; set; }
    public Point3d Position { get; set; }
    /// <summary>源 / 汇的吞吐能力 t/h（仅 Loading/Unloading 有意义）。</summary>
    public double ThroughputTph { get; set; }
    /// <summary>关联采场台阶 / 排土场 / 破碎站实体 handle。</summary>
    public string? RefId { get; set; }

    public RoadNode(string id, RoadNodeType type, Point3d position)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Type = type;
        Position = position;
    }
}

/// <summary>路网边（一段路）。几何走中线，拓扑 + 属性在此。</summary>
public sealed class RoadEdge
{
    public string Id { get; }
    public string FromId { get; }
    public string ToId { get; }

    /// <summary>中线（复用坑线落地输出；至少含起讫两点）。</summary>
    public IReadOnlyList<Point3d> Centerline { get; private set; }

    /// <summary>三维里程 m（构造时按中线自动算，可后续覆盖）。</summary>
    public double LengthM { get; set; }
    /// <summary>纵坡 %，带符号，沿 From→To 方向（构造时按中线起讫自动算）。整段平均，长边内的陡段会被它平掉。</summary>
    public double GradePct { get; set; }

    /// <summary>
    /// 最大分段绝对纵坡 %（派生量）：把中线按固定水平步长 <see cref="GradeSampleStepM"/> 重采样后，
    /// 逐采样段算 |纵坡| 取最大。**限坡过滤与陡坡判据一律用它，不用 <see cref="GradePct"/>** ——
    /// 一条 800m 的边里藏一段 12% 的陡坡，整段平均只有 3%，按平均判限坡等于没判。
    ///
    /// 为什么是固定步长而不是逐原始顶点：中线顶点的疏密由提取算法决定，与真实坡度变化无关。
    /// 逐顶点算的话，密集打点的缓坡会被相邻两点的微小高差抖出一个虚高的"最陡段"，
    /// 稀疏打点的真陡坡反而被平掉 —— 同一条路换个提取参数就换一个限坡结论。
    /// 固定步长把基准从"点怎么打的"换成物理长度，结论才可复现。
    ///
    /// 中线点数 &lt; 2（或水平总长不足一步）时回落为 |<see cref="GradePct"/>|。
    /// 派生量：随中线重算即可，**不序列化**（存下来只会多一个"存值与中线不一致"的故障面）。
    /// </summary>
    public double MaxAbsSegGradePct { get; set; }

    /// <summary>纵坡重采样步长 m（水平投影）。进口径摘要，改它会改限坡结论。</summary>
    public const double GradeSampleStepM = 20.0;

    public int LaneCount { get; set; } = 1;
    public bool OneWay { get; set; }
    public double MaxLoadT { get; set; }
    public double SpeedLimitKph { get; set; }
    public string? Pavement { get; set; }
    public RoadEdgeStatus Status { get; set; } = RoadEdgeStatus.Open;
    /// <summary>临时道路（随采延拓接入，采空后回收）。</summary>
    public bool IsTemporary { get; set; }

    /// <summary>
    /// 人工改判的线路类型（null = 走自动判据，R-T7）。
    ///
    /// <b>为什么挂在边上而不是路段上</b>：路段是接缝合并出来的派生对象，重算一次 Id 就可能变，
    /// 存不住改判。边有稳定 Id 且本来就随存档持久化。改判在 UI 上按<b>路段</b>下达，
    /// 落库时写进该路段的每一条边；重算时由 <see cref="RoadTopology"/> 按里程占优还原。
    ///
    /// <b>为什么必须能人工改</b>：自动判据是纯拓扑的（两端接没接上），它答不了「这条路重不重要」。
    /// 对着真实路网量过：自动判成「干线」的中位长 109m、判成「支线」的中位长 111m ——
    /// <b>支线比干线还长</b>。主运输坡道尽头停在当前工作面，拓扑上就是一端悬空。
    /// 道路等级是业务判断，机器只该给个默认值。
    /// </summary>
    public RoadSegmentClass? RoadClass { get; set; }

    /// <summary>路面宽 m。0=未知（图上抽的中线没有宽度信息，只有坑线落地带得出来）。</summary>
    public double WidthM { get; set; }
    /// <summary>来源标识：这条边打哪来。空=从图上中线抽取；「坑线落地:xx」=复用坑线落地输出（见 <c>HaulRoadImporter</c>）；
    /// 或下面三个"补出来的"常量之一。</summary>
    public string? SourceRef { get; set; }

    /// <summary>建网时按缺口自动桥接出来的连接边（<c>BR*</c> / <c>BRG*</c>）。</summary>
    public const string SourceAutoBridge = "自动桥接";
    /// <summary>寻径判不连通后由用户点「一键补缺口」补出来的连接边（<c>BRX*</c>）。</summary>
    public const string SourceManualGapFix = "手动补缺口";
    /// <summary>装卸点接进路网的那条接入支线（<c>*_link</c>）。</summary>
    public const string SourceDepotLink = "装卸点接入";

    /// <summary>
    /// 这条边是**补出来的**，图上没有对应的真实中线。
    ///
    /// 为什么必须能问出来：对现场那张网量过，桥接距离设 50m 时 <b>79.5% 的可行路径至少踩一条这种边</b>。
    /// 它们很短（≤48m，占路径里程中位 0.6%），所以不会把路拉长 —— 但"这一段其实没有路"这件事
    /// 如果逐段明细里一个字都不说，用户拿到的就是一条<b>看着能走、实际得先修路</b>的线路。
    /// </summary>
    public bool IsSynthetic => SourceRef is SourceAutoBridge or SourceManualGapFix or SourceDepotLink;

    public RoadEdge(string id, string fromId, string toId, IReadOnlyList<Point3d>? centerline = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        FromId = fromId ?? throw new ArgumentNullException(nameof(fromId));
        ToId = toId ?? throw new ArgumentNullException(nameof(toId));
        Centerline = centerline ?? Array.Empty<Point3d>();
        RecomputeGeometry();
    }

    /// <summary>替换中线并重算里程 / 纵坡。</summary>
    public void SetCenterline(IReadOnlyList<Point3d> centerline)
    {
        Centerline = centerline ?? Array.Empty<Point3d>();
        RecomputeGeometry();
    }

    /// <summary>由中线算三维里程、平均纵坡与最大分段纵坡（中线点数 &lt; 2 则保持现值，留给调用方显式填）。</summary>
    public void RecomputeGeometry()
    {
        var c = Centerline;
        if (c.Count < 2) return;
        double len = 0, horiz = 0;
        for (int i = 1; i < c.Count; i++)
        {
            len += c[i].DistanceTo(c[i - 1]);
            horiz += c[i].HorizontalDistanceTo(c[i - 1]);
        }
        LengthM = len;
        double rise = c[^1].Z - c[0].Z;
        GradePct = horiz > 1e-9 ? rise / horiz * 100.0 : 0.0;
        MaxAbsSegGradePct = ComputeMaxAbsSegGradePct(c, GradeSampleStepM, Math.Abs(GradePct));
    }

    /// <summary>
    /// 按固定水平步长重采样中线 → 逐采样段 |Δz / Δ水平| 取最大（%）。
    /// 水平总长 &lt; 一步时只有起讫两个采样点，结果自然回落成整段平均，与 <paramref name="fallbackAbsPct"/> 同值。
    /// 纯函数、无状态，可单测。
    /// </summary>
    internal static double ComputeMaxAbsSegGradePct(IReadOnlyList<Point3d> c, double stepM, double fallbackAbsPct)
    {
        if (c.Count < 2 || stepM <= 1e-6) return fallbackAbsPct;

        // 逐顶点走链，每累计满 stepM 水平距就落一个采样点（Z 在段内线性插值）。
        double lastZ = c[0].Z;
        double acc = 0;          // 自上一个采样点起累计的水平距
        double maxPct = 0;
        bool any = false;
        for (int i = 1; i < c.Count; i++)
        {
            double segH = c[i].HorizontalDistanceTo(c[i - 1]);
            double segDz = c[i].Z - c[i - 1].Z;
            if (segH <= 1e-9)
            {
                // 纯竖直段（水平投影为 0）：坡度无定义，按里程算它也走不动，跳过不参与判据。
                continue;
            }
            double walked = 0;
            while (acc + (segH - walked) >= stepM)
            {
                double need = stepM - acc;                 // 本段还要走 need 才凑满一步
                walked += need;
                double z = c[i - 1].Z + segDz * (walked / segH);
                double pct = Math.Abs((z - lastZ) / stepM * 100.0);
                if (pct > maxPct) maxPct = pct;
                any = true;
                lastZ = z;
                acc = 0;
            }
            acc += segH - walked;
        }
        // 收尾不足一步的余段：仍按其自身长度算坡度（末段常是最陡的下坡口，丢了就漏判）。
        if (acc > 1e-6)
        {
            double pct = Math.Abs((c[^1].Z - lastZ) / acc * 100.0);
            if (pct > maxPct) maxPct = pct;
            any = true;
        }
        return any ? maxPct : fallbackAbsPct;
    }
}

/// <summary>有向可达连接：从某节点出发经 <see cref="Edge"/> 抵达 <see cref="ToId"/>；<see cref="Reversed"/>=逆 From→To 方向行驶（坡度取反）。</summary>
public readonly record struct RoadLink(RoadEdge Edge, string ToId, bool Reversed);

/// <summary>路网图：节点 + 边 + 邻接。第七条所有寻径 / 运距 / 更新 / 优化的地基。</summary>
public sealed class RoadGraph
{
    private readonly Dictionary<string, RoadNode> _nodes = new();
    private readonly Dictionary<string, RoadEdge> _edges = new();
    private readonly Dictionary<string, List<RoadLink>> _adj = new();   // nodeId -> 出向连接（尊重 OneWay）
    private readonly Dictionary<string, List<RoadLink>> _adjBothWays = new(); // 同上但把单向边也当双向（仅"不可达诊断"逐级放宽用）

    public IReadOnlyCollection<RoadNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<RoadEdge> Edges => _edges.Values;
    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;

    public RoadNode? GetNode(string id) => _nodes.GetValueOrDefault(id);
    public RoadEdge? GetEdge(string id) => _edges.GetValueOrDefault(id);

    public RoadNode AddNode(RoadNode node)
    {
        _nodes[node.Id] = node;
        return node;
    }

    public RoadNode AddNode(string id, RoadNodeType type, Point3d position)
        => AddNode(new RoadNode(id, type, position));

    /// <summary>加边（D1）。要求两端节点已存在。若边里程为 0，按两端节点直线兜底。</summary>
    public RoadEdge AddEdge(RoadEdge edge)
    {
        if (!_nodes.ContainsKey(edge.FromId))
            throw new InvalidOperationException($"边 {edge.Id} 的起点 {edge.FromId} 不在图中。");
        if (!_nodes.ContainsKey(edge.ToId))
            throw new InvalidOperationException($"边 {edge.Id} 的终点 {edge.ToId} 不在图中。");
        if (edge.LengthM <= 0)
        {
            var a = _nodes[edge.FromId].Position;
            var b = _nodes[edge.ToId].Position;
            edge.LengthM = a.DistanceTo(b);
            double h = a.HorizontalDistanceTo(b);
            edge.GradePct = h > 1e-9 ? (b.Z - a.Z) / h * 100.0 : 0.0;
        }
        // 无中线的边（直线兜底 / 反序列化只带 Len+Grade）拿不到分段坡，回落整段平均，
        // 否则派生量停在 0，限坡过滤会把这些边当"零坡"一路放行。
        if (edge.MaxAbsSegGradePct <= 0) edge.MaxAbsSegGradePct = Math.Abs(edge.GradePct);
        _edges[edge.Id] = edge;
        Link(edge);
        return edge;
    }

    /// <summary>删边（D1）+ 重建邻接。</summary>
    public bool RemoveEdge(string edgeId)
    {
        if (!_edges.Remove(edgeId)) return false;
        RebuildAdjacency();
        return true;
    }

    /// <summary>删节点 + 连带删除其关联的所有边（装卸点重录入用）。返回是否删掉。</summary>
    public bool RemoveNode(string nodeId)
    {
        if (!_nodes.Remove(nodeId)) return false;
        var incident = _edges.Values
            .Where(e => e.FromId == nodeId || e.ToId == nodeId)
            .Select(e => e.Id).ToList();
        foreach (var id in incident) _edges.Remove(id);
        RebuildAdjacency();
        return true;
    }

    /// <summary>改边属性（D2）：替换同 Id 边并重建邻接（坡度 / 车道 / 状态变更走这里）。</summary>
    public void UpdateEdge(RoadEdge edge)
    {
        _edges[edge.Id] = edge;
        RebuildAdjacency();
    }

    /// <summary>
    /// 在某边中线上离 <paramref name="at"/> 最近处打断，插一个节点（默认 Junction），
    /// 原边删除、生成两半（<c>{edgeId}_a</c> / <c>{edgeId}_b</c>，继承属性），返回新节点。
    /// 装卸点接入 / 交叉补点用。中线缺失时按两端节点直线兜底。
    /// </summary>
    public RoadNode SplitEdgeAtNearest(string edgeId, in Point3d at, string newNodeId,
                                       RoadNodeType type = RoadNodeType.Junction)
    {
        var e = GetEdge(edgeId) ?? throw new InvalidOperationException($"边 {edgeId} 不存在，无法打断。");
        var c = e.Centerline.Count >= 2
            ? e.Centerline
            : new[] { GetNode(e.FromId)!.Position, GetNode(e.ToId)!.Position };
        SplitCenterline(c, at, out var c1, out var c2, out var cut);
        var node = AddNode(new RoadNode(newNodeId, type, cut));
        RemoveEdge(edgeId);
        AddEdge(CopyAttrs(e, $"{edgeId}_a", e.FromId, node.Id, c1));
        AddEdge(CopyAttrs(e, $"{edgeId}_b", node.Id, e.ToId, c2));
        return node;
    }

    /// <summary>把中线在离 <paramref name="at"/> 最近的投影脚切两半（投影脚作共享切点，去近重合，保证各 ≥2 点）。</summary>
    private static void SplitCenterline(IReadOnlyList<Point3d> c, in Point3d at,
        out IReadOnlyList<Point3d> a, out IReadOnlyList<Point3d> b, out Point3d cut)
    {
        int bestSeg = 0;
        double bestD = double.MaxValue;
        Point3d bestP = c[0];
        for (int i = 0; i + 1 < c.Count; i++)
        {
            var p0 = c[i];
            var p1 = c[i + 1];
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : ((at.X - p0.X) * dx + (at.Y - p0.Y) * dy) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            var q = new Point3d(p0.X + t * dx, p0.Y + t * dy, p0.Z + t * (p1.Z - p0.Z));
            double d = q.HorizontalDistanceTo(at);
            if (d < bestD) { bestD = d; bestSeg = i; bestP = q; }
        }
        cut = bestP;

        var la = new List<Point3d>();
        for (int i = 0; i <= bestSeg; i++) la.Add(c[i]);
        if (la[^1].DistanceTo(bestP) > 1e-6) la.Add(bestP);
        var lb = new List<Point3d> { bestP };
        for (int i = bestSeg + 1; i < c.Count; i++)
            if (lb[^1].DistanceTo(c[i]) > 1e-6) lb.Add(c[i]);

        if (la.Count < 2) la.Add(bestP);          // 退化保护：切点贴端点时凑足 2 点
        if (lb.Count < 2) lb.Add(c[^1]);
        a = la; b = lb;
    }

    /// <summary>克隆一条边的属性到新 Id / 端点 / 中线（打断两半继承原边属性）。</summary>
    private static RoadEdge CopyAttrs(RoadEdge src, string id, string from, string to, IReadOnlyList<Point3d> centerline)
        => new(id, from, to, centerline)
        {
            LaneCount = src.LaneCount,
            OneWay = src.OneWay,
            MaxLoadT = src.MaxLoadT,
            SpeedLimitKph = src.SpeedLimitKph,
            Pavement = src.Pavement,
            Status = src.Status,
            IsTemporary = src.IsTemporary,
            WidthM = src.WidthM,
            SourceRef = src.SourceRef,
            RoadClass = src.RoadClass,   // 打断出的两半继承人工改判，否则插一次交叉口改判就丢一半
        };

    /// <summary>从某节点可出发的连接（含双向边的逆向；状态过滤交给求解器）。</summary>
    public IReadOnlyList<RoadLink> EdgesFrom(string nodeId)
        => _adj.TryGetValue(nodeId, out var list) ? list : (IReadOnlyList<RoadLink>)Array.Empty<RoadLink>();

    /// <summary>
    /// 同 <see cref="EdgesFrom"/>，但把 <see cref="RoadEdge.OneWay"/> 边也挂上反向连接。
    /// **只给"不可达诊断"的第 4 级放宽用**，正常寻径绝不能走这条 —— 它会让车逆行。
    /// 单独存在的理由：单向路造成的不可达与物理不连通在求解器眼里长得一模一样（都返回 Unreachable），
    /// 但一个改个属性就能解决、另一个要去补一条真路，处置动作差了十万八千里，必须分得开。
    /// </summary>
    public IReadOnlyList<RoadLink> EdgesFromIgnoringOneWay(string nodeId)
        => _adjBothWays.TryGetValue(nodeId, out var list) ? list : (IReadOnlyList<RoadLink>)Array.Empty<RoadLink>();

    /// <summary>
    /// 连通分量编号表（无向、忽略单双向；编号从 1 起，按节点 Id <c>Ordinal</c> 序稳定分配）。
    /// 诊断第 5 级"物理不连通"要报"源在第几片、汇在第几片"，靠它。
    /// </summary>
    public Dictionary<string, int> BuildComponentMap()
    {
        var parent = new Dictionary<string, string>();
        string Find(string x)
        {
            string r = x;
            while (parent[r] != r) r = parent[r];
            while (parent[x] != r) { var n = parent[x]; parent[x] = r; x = n; }
            return r;
        }
        foreach (var id in _nodes.Keys) parent[id] = id;
        foreach (var e in _edges.Values)
        {
            var ra = Find(e.FromId); var rb = Find(e.ToId);
            if (ra != rb) parent[ra] = rb;
        }
        // 编号按"该片中 Id 最小的节点"排序分配：同一张图两次调用必得同一套编号（R3）。
        var byRoot = new Dictionary<string, List<string>>();
        foreach (var id in _nodes.Keys)
        {
            var r = Find(id);
            if (!byRoot.TryGetValue(r, out var bucket)) { bucket = new(); byRoot[r] = bucket; }
            bucket.Add(id);
        }
        var map = new Dictionary<string, int>();
        int no = 1;
        foreach (var kv in byRoot.OrderBy(kv => kv.Value.Min(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            foreach (var id in kv.Value) map[id] = no;
            no++;
        }
        return map;
    }

    /// <summary>离给定点最近的节点（线性扫描；视口取点 / 抽图吸附用）。超 <paramref name="maxDistM"/> 返回 null。</summary>
    public RoadNode? NearestNode(Point3d p, double maxDistM = double.MaxValue)
    {
        RoadNode? best = null;
        double bestD = maxDistM;
        foreach (var n in _nodes.Values)
        {
            double d = n.Position.DistanceTo(p);
            if (d <= bestD) { bestD = d; best = n; }
        }
        return best;
    }

    /// <summary>
    /// 离给定点最近的边 **+ 中线上的投影脚**，按<b>三维</b>距（与 <see cref="NearestNode"/> 同口径）。超 <paramref name="maxDistM"/> 返回 null。
    ///
    /// 为什么不复用 <see cref="NearestEdge"/>：那个按<b>水平</b>距判，露天矿上下台阶的路在平面上是叠着的，
    /// 水平判据会把点吸到脚下另一个台阶的路上 —— 用户点的是这条，算出来的是那条，而且看不出来。
    ///
    /// 为什么要投影脚：路网的节点只长在中线端点上（<see cref="RoadGraphBuilder"/> 只对首末点建节点），
    /// 现场量过中位 67m 才一个节点。"吸到最近节点"等于要求用户点在端点上，这是 <b>点对点寻径点了没反应</b>的根因。
    /// 拿到投影脚后由调用方 <see cref="SplitEdgeAtNearest"/> 就地插一个临时节点，任意路面位置都能当起终点。
    /// </summary>
    public RoadEdge? NearestEdgeWithFoot(in Point3d p, double maxDistM, out Point3d foot, out double distM)
    {
        RoadEdge? best = null;
        foot = default;
        distM = maxDistM;
        foreach (var e in _edges.Values)
        {
            var c = e.Centerline.Count >= 2
                ? e.Centerline
                : (GetNode(e.FromId), GetNode(e.ToId)) is ({ } a, { } b)
                    ? new[] { a.Position, b.Position }
                    : Array.Empty<Point3d>();
            for (int i = 1; i < c.Count; i++)
            {
                double d = PointSegDist3D(p, c[i - 1], c[i], out var q);
                if (d > distM) continue;
                distM = d; foot = q; best = e;
            }
        }
        if (best is null) distM = double.PositiveInfinity;
        return best;
    }

    /// <summary>
    /// **视口取点专用**的吸附：用<b>平面距</b>选路，Z 只在可信时用来分辨上下台阶。
    ///
    /// 为什么不能直接用 <see cref="NearestEdgeWithFoot"/> 的三维距：视口取点的 Z 来自对三角网的射线求交
    /// （<c>PickWorldOnGeometry</c>）。图上没有可命中的地形面时它给不出真实标高，现场实测回来的是 <b>Z=0</b>，
    /// 而矿区路面在 1128~1515m —— 于是"离最近路面 1239m"，那 1239 根本不是平面距离，是高差本身。
    /// 用户在平面视图里点的是他<b>看得见</b>的那条路，Z 是光标下正好有什么面的副产物，不该拿它当判据。
    ///
    /// 口径：
    ///   ① 先取平面距 ≤ <paramref name="maxHorizM"/> 的候选；一个都没有 → 真的点歪了，返回 null。
    ///   ② 候选里有 |Δz| ≤ <paramref name="zBandM"/> 的（说明取点 Z 落在路网标高上、可信）→ 在这些里按<b>三维距</b>取最近，
    ///      上下叠置的台阶照样分得开（<paramref name="zUsed"/>=true）。
    ///   ③ 一个都不在标高带里（取点 Z 不可信）→ 退回平面最近的那条，并由 <paramref name="dzM"/> 如实报出差了多少
    ///      （<paramref name="zUsed"/>=false）。**退回不是静默的**，调用方要把这句话说给用户听。
    /// </summary>
    public RoadEdge? NearestEdgeForPick(in Point3d p, double maxHorizM, double zBandM,
        out Point3d foot, out double horizDistM, out double dzM, out bool zUsed)
    {
        RoadEdge? flatBest = null, bandBest = null;
        Point3d flatFoot = default, bandFoot = default;
        double flatD = maxHorizM, bandD3 = double.PositiveInfinity;

        foreach (var e in _edges.Values)
        {
            var c = e.Centerline.Count >= 2
                ? e.Centerline
                : (GetNode(e.FromId), GetNode(e.ToId)) is ({ } a, { } b)
                    ? new[] { a.Position, b.Position }
                    : Array.Empty<Point3d>();
            for (int i = 1; i < c.Count; i++)
            {
                double dh = PointSegDistH(p, c[i - 1], c[i], out var q);
                if (dh > maxHorizM) continue;
                if (dh <= flatD) { flatD = dh; flatFoot = q; flatBest = e; }
                if (Math.Abs(q.Z - p.Z) > zBandM) continue;
                double d3 = q.DistanceTo(p);
                if (d3 < bandD3) { bandD3 = d3; bandFoot = q; bandBest = e; }
            }
        }

        if (bandBest is not null)
        {
            foot = bandFoot; horizDistM = bandFoot.HorizontalDistanceTo(p); dzM = bandFoot.Z - p.Z; zUsed = true;
            return bandBest;
        }
        if (flatBest is not null)
        {
            foot = flatFoot; horizDistM = flatD; dzM = flatFoot.Z - p.Z; zUsed = false;
            return flatBest;
        }
        foot = default; horizDistM = double.PositiveInfinity; dzM = double.NaN; zUsed = false;
        return null;
    }

    /// <summary>点到线段的<b>平面</b>距 + 最近点（Z 沿段线性插值）。</summary>
    private static double PointSegDistH(in Point3d p, in Point3d a, in Point3d b, out Point3d q)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0.0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        q = new Point3d(a.X + t * dx, a.Y + t * dy, a.Z + t * (b.Z - a.Z));
        return q.HorizontalDistanceTo(p);
    }

    /// <summary>点到线段的三维距 + 最近点（<see cref="NearestEdgeWithFoot"/> 用）。</summary>
    private static double PointSegDist3D(in Point3d p, in Point3d a, in Point3d b, out Point3d q)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double len2 = dx * dx + dy * dy + dz * dz;
        double t = len2 < 1e-12 ? 0.0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / len2;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        q = new Point3d(a.X + t * dx, a.Y + t * dy, a.Z + t * dz);
        return q.DistanceTo(p);
    }

    /// <summary>离给定点最近的边（按中线/端点的平面距；改边状态 / 删边取点用）。</summary>
    public RoadEdge? NearestEdge(Point3d p, double maxDistM = double.MaxValue)
    {
        RoadEdge? best = null;
        double bestD = maxDistM;
        foreach (var e in _edges.Values)
        {
            double d = DistanceToEdge(p, e);
            if (d <= bestD) { bestD = d; best = e; }
        }
        return best;
    }

    private double DistanceToEdge(in Point3d p, RoadEdge e)
    {
        var c = e.Centerline;
        if (c.Count >= 2)
        {
            double best = double.MaxValue;
            for (int i = 1; i < c.Count; i++)
                best = Math.Min(best, PointSegDist2D(p, c[i - 1], c[i]));
            return best;
        }
        var a = GetNode(e.FromId); var b = GetNode(e.ToId);
        return (a is not null && b is not null) ? PointSegDist2D(p, a.Position, b.Position) : double.MaxValue;
    }

    private static double PointSegDist2D(in Point3d p, in Point3d a, in Point3d b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0.0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = t < 0 ? 0 : (t > 1 ? 1 : t);
        double qx = a.X + t * dx, qy = a.Y + t * dy;
        double ex = p.X - qx, ey = p.Y - qy;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>深拷贝（时段快照用）。节点/边复制，中线引用共享（不可变）。</summary>
    public RoadGraph Clone()
    {
        var g = new RoadGraph();
        foreach (var n in _nodes.Values)
            g.AddNode(new RoadNode(n.Id, n.Type, n.Position) { ThroughputTph = n.ThroughputTph, RefId = n.RefId });
        foreach (var e in _edges.Values)
            g.AddEdge(new RoadEdge(e.Id, e.FromId, e.ToId, e.Centerline)
            {
                LengthM = e.LengthM,
                GradePct = e.GradePct,
                MaxAbsSegGradePct = e.MaxAbsSegGradePct,
                LaneCount = e.LaneCount,
                OneWay = e.OneWay,
                MaxLoadT = e.MaxLoadT,
                SpeedLimitKph = e.SpeedLimitKph,
                Pavement = e.Pavement,
                Status = e.Status,
                IsTemporary = e.IsTemporary,
                WidthM = e.WidthM,
                SourceRef = e.SourceRef,
                RoadClass = e.RoadClass,
            });
        return g;
    }

    private void Link(RoadEdge e)
    {
        if (!_adj.TryGetValue(e.FromId, out var fwd)) { fwd = new(); _adj[e.FromId] = fwd; }
        fwd.Add(new RoadLink(e, e.ToId, Reversed: false));
        if (!e.OneWay)
        {
            if (!_adj.TryGetValue(e.ToId, out var rev)) { rev = new(); _adj[e.ToId] = rev; }
            rev.Add(new RoadLink(e, e.FromId, Reversed: true));
        }

        // 诊断专用邻接：无条件双向。
        if (!_adjBothWays.TryGetValue(e.FromId, out var f2)) { f2 = new(); _adjBothWays[e.FromId] = f2; }
        f2.Add(new RoadLink(e, e.ToId, Reversed: false));
        if (!_adjBothWays.TryGetValue(e.ToId, out var r2)) { r2 = new(); _adjBothWays[e.ToId] = r2; }
        r2.Add(new RoadLink(e, e.FromId, Reversed: true));
    }

    private void RebuildAdjacency()
    {
        _adj.Clear();
        _adjBothWays.Clear();
        foreach (var e in _edges.Values) Link(e);
    }

    /// <summary>校验（A6）：连通性 / 孤立节点 / 坡度·车道合规。</summary>
    public ValidationReport Validate(double maxGradePct = 10.0)
    {
        var issues = new List<string>();

        // 物理连通性（无向，忽略单双向）：并查集数连通分量。
        var parent = new Dictionary<string, string>();
        string Find(string x)
        {
            string r = x;
            while (parent[r] != r) r = parent[r];
            while (parent[x] != r) { var n = parent[x]; parent[x] = r; x = n; }
            return r;
        }
        foreach (var id in _nodes.Keys) parent[id] = id;
        foreach (var e in _edges.Values)
        {
            var ra = Find(e.FromId); var rb = Find(e.ToId);
            if (ra != rb) parent[ra] = rb;
        }
        int components = _nodes.Count == 0 ? 0 : _nodes.Keys.Select(Find).Distinct().Count();

        // 孤立节点：没有任何边引用。
        var referenced = new HashSet<string>();
        foreach (var e in _edges.Values) { referenced.Add(e.FromId); referenced.Add(e.ToId); }
        var isolated = _nodes.Keys.Where(id => !referenced.Contains(id)).ToList();
        foreach (var id in isolated) issues.Add($"孤立节点：{id}（无边连接）");

        // 逐边合规。
        foreach (var e in _edges.Values)
        {
            // 判超限用分段最大坡，与寻径的限坡过滤同口径；否则校验说合规、寻径说走不通。
            if (e.MaxAbsSegGradePct > maxGradePct)
                issues.Add($"边 {e.Id} 最陡段纵坡 {e.MaxAbsSegGradePct:F1}% 超限（>{maxGradePct:F1}%，整段平均 {e.GradePct:+0.0;-0.0}%）");
            if (e.LaneCount < 1)
                issues.Add($"边 {e.Id} 车道数 {e.LaneCount} 非法（<1）");
            if (e.LengthM <= 0)
                issues.Add($"边 {e.Id} 里程为 0（中线缺失且两端重合？）");
        }

        return new ValidationReport
        {
            IsFullyConnected = components <= 1,
            ComponentCount = components,
            IsolatedNodeIds = isolated,
            Issues = issues,
        };
    }
}

/// <summary>校验报告。</summary>
public sealed class ValidationReport
{
    public bool IsFullyConnected { get; init; }
    public int ComponentCount { get; init; }
    public IReadOnlyList<string> IsolatedNodeIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
    /// <summary>全连通且无任何问题。</summary>
    public bool Ok => IsFullyConnected && Issues.Count == 0;
}
