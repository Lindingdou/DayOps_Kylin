// 忠实移植自原 PitMine3D Modules/RoadLib/Network/RoadTopology.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 节点拓扑类别（R-T1，按<b>度数</b>分，纯派生量）。装卸点的业务语义走
/// <see cref="RoadNode.Type"/>，与本枚举正交 —— 一个破碎站可以同时是「多岔」。
/// </summary>
public enum RoadNodeClass
{
    /// <summary>度 0：孤立点（无边引用）。</summary>
    Isolated,
    /// <summary>度 1：端点（悬挂头，要么是真尽头，要么是中线没接上的缺口）。</summary>
    Endpoint,
    /// <summary>度 2：接缝 —— 两条中线在此对接／一条中线被交叉打断留下的缝，<b>不是路口</b>。</summary>
    Seam,
    /// <summary>度 3：丁字路口。</summary>
    Tee,
    /// <summary>度 ≥4：多岔路口（十字及以上）。</summary>
    Multi,
}

/// <summary>路段拓扑类别（R-T3，全部由两端节点的度数推出，不新增判据）。</summary>
public enum RoadSegmentClass
{
    /// <summary>干线：两端都通（都是路口／装卸点）—— 过境交通的骨架。</summary>
    Trunk,
    /// <summary>支线：一端悬挂 —— 从骨架伸出去的尽头路。</summary>
    Spur,
    /// <summary>孤立段：两端都悬挂 —— 这条中线谁也没接上。</summary>
    Isolated,
}

/// <summary>
/// 一条路段（R-T2）：两个<b>真节点</b>之间串起来的一串边 + 拼好的整条中线。
/// 「路网预览」的着色、计数、回显一律以路段为单位，不再以打断后的碎边为单位。
/// </summary>
public sealed class RoadSegment
{
    /// <summary>路段 Id（= 首边 Id，按边 Id 序确定，同图两次分析必得同名）。</summary>
    public string Id { get; }
    /// <summary>沿路段行进方向依次经过的边 Id。</summary>
    public IReadOnlyList<string> EdgeIds { get; }
    /// <summary>起点真节点。</summary>
    public string FromId { get; }
    /// <summary>终点真节点。</summary>
    public string ToId { get; }
    /// <summary>拼接好的整条中线（已按行进方向定向、去接缝处重复点）。</summary>
    public IReadOnlyList<Point3d> Polyline { get; }
    /// <summary>三维里程 m（= 各边里程之和）。</summary>
    public double LengthM { get; }
    /// <summary>线路类别（人工改判优先，否则自动判据，R-T7）。</summary>
    public RoadSegmentClass Class { get; }
    /// <summary>本条的类别来自人工改判（而非自动判据）。</summary>
    public bool IsManual { get; }
    /// <summary>自动判据本来会判成什么（改判时用来说明「改自哪一档」；未改判时 == <see cref="Class"/>）。</summary>
    public RoadSegmentClass AutoClass { get; }
    /// <summary>本条带着改判痕迹、但占优里程不够 <see cref="RoadTopology.ManualQuorum"/>，已退回自动判据。</summary>
    public bool Stale { get; internal init; }
    /// <summary>起讫同一节点（环线）。</summary>
    public bool IsLoop => string.Equals(FromId, ToId, StringComparison.Ordinal);

    internal RoadSegment(string id, IReadOnlyList<string> edgeIds, string fromId, string toId,
                         IReadOnlyList<Point3d> polyline, double lengthM,
                         RoadSegmentClass cls, RoadSegmentClass autoCls, bool isManual)
    {
        Id = id; EdgeIds = edgeIds; FromId = fromId; ToId = toId;
        Polyline = polyline; LengthM = lengthM;
        Class = cls; AutoClass = autoCls; IsManual = isManual;
    }
}

/// <summary>路网拓扑分析结果（<see cref="RoadTopology.Analyze"/> 的产物，只算不存）。</summary>
public sealed class RoadTopologyReport
{
    /// <summary>逐节点度数（自环记 2）。</summary>
    public IReadOnlyDictionary<string, int> Degree { get; init; } = new Dictionary<string, int>();
    /// <summary>逐节点拓扑类别。</summary>
    public IReadOnlyDictionary<string, RoadNodeClass> NodeClass { get; init; } = new Dictionary<string, RoadNodeClass>();
    /// <summary>路段表（按 Id Ordinal 序稳定）。</summary>
    public IReadOnlyList<RoadSegment> Segments { get; init; } = Array.Empty<RoadSegment>();
    /// <summary>边 Id → 它所属的路段（视口点选落到边上，改判要作用到整条路段）。</summary>
    public IReadOnlyDictionary<string, RoadSegment> SegmentByEdge { get; init; }
        = new Dictionary<string, RoadSegment>();
    /// <summary>连通分量数（无向，忽略单双向）。</summary>
    public int ComponentCount { get; init; }

    /// <summary>各节点类别的个数，下标 = <see cref="RoadNodeClass"/>。</summary>
    public IReadOnlyList<int> NodeCountByClass { get; init; } = Array.Empty<int>();
    /// <summary>各路段类别的条数，下标 = <see cref="RoadSegmentClass"/>。</summary>
    public IReadOnlyList<int> SegmentCountByClass { get; init; } = Array.Empty<int>();
    /// <summary>各路段类别的里程 m，下标 = <see cref="RoadSegmentClass"/>。</summary>
    public IReadOnlyList<double> SegmentLengthByClass { get; init; } = Array.Empty<double>();

    /// <summary>真节点数（度≠2 或装卸点）—— 图上真正称得上「点」的那些。</summary>
    public int RealNodeCount { get; init; }
    /// <summary>接缝数（度=2 且非装卸点）—— 计入「节点数」会把路口数虚报好几倍。</summary>
    public int SeamCount { get; init; }
    /// <summary>路口数（度≥3）。</summary>
    public int JunctionCount => NodeCountByClass[(int)RoadNodeClass.Tee] + NodeCountByClass[(int)RoadNodeClass.Multi];
    /// <summary>悬挂端点数（度≤1，含孤立点）。</summary>
    public int DangleCount => NodeCountByClass[(int)RoadNodeClass.Endpoint] + NodeCountByClass[(int)RoadNodeClass.Isolated];
    /// <summary>路段总里程 m。</summary>
    public double TotalLengthM { get; init; }

    /// <summary>本次分析是否只算了可通行边（检修/封闭视为不存在）。</summary>
    public bool PassableOnly { get; init; }

    /// <summary>类别来自人工改判的路段数（R-T7）。</summary>
    public int ManualCount { get; init; }
    /// <summary>改判已失效、被退回自动判据的路段数 —— 图变了，改判跟不上（R-T7）。</summary>
    public int StaleManualCount { get; init; }

    /// <summary>一行图例式摘要（命令行回显即图例）。</summary>
    public string Summary =>
        $"路段 {Segments.Count}（" + string.Join(" / ", Enum.GetValues<RoadSegmentClass>()
            .Select(c => $"{RoadTopology.TextOf(c)} {SegmentCountByClass[(int)c]}")) + "）"
        + (ManualCount > 0 ? $" · 其中人工改判 {ManualCount}" : "")
        + (StaleManualCount > 0 ? $" · 改判失效退回自动 {StaleManualCount}" : "")
        + $" · 路口 {JunctionCount} · 悬挂端点 {DangleCount} · 接缝 {SeamCount} · 连通片 {ComponentCount}";
}

/// <summary>
/// 路网拓扑分类（「路网预览」的分级核心）：把<b>离散中心线打断出来的碎边</b>还原成
/// 「路口—路口」的路段，并按两端节点的类型给路段定类。纯图论 + 纯分类，零 UI / 零宿主依赖，可单测。
///
/// ── 为什么要有这一层（2026-08-11 现场：「转换之后颜色太多」） ──
/// 旧「路网预览」按<b>连通片</b>着色（最大片绿 + 其余 10 色循环）。对着库里的真实路网量：
/// 127 个连通片 / 10 色调色板 ⇒ 同色不同片，颜色既多又不表意；且「最大片=主干绿」名不副实 ——
/// 最大片只占 30.5% 里程、9% 节点，<b>69.5% 的里程根本不在它上面</b>。
/// 同一次量还暴露两件事：存档里 1900 个节点<b>全是 Junction</b>（节点类型一个都没识别）；
/// 而度数分布极分得开 —— 悬挂 542 / <b>接缝 963（50.7%）</b> / 丁字 394 / 十字 1，
/// 也就是说图上一半以上的「节点」是中线被打断留下的缝，根本不是路口。
///
/// 口径（现场 2026-08-11 定）：
/// <list type="bullet">
///   <item><b>R-T1 节点按度数分 5 类</b>：孤立/端点/接缝/丁字/多岔。只有<b>度≠2 的才是真节点</b>；
///     接缝不画、不计入路口数。装卸点（<see cref="RoadNode.Type"/>）无论度数一律算真节点 ——
///     源汇是路段的天然端点，被路段吞进去就再也指不出「车从哪装到哪卸」。</item>
///   <item><b>R-T2 路段 = 两个真节点之间的一串边</b>。在接缝处把边串起来，分类/着色/统计一律以
///     路段为单位（实测 1827 边 → 865 路段，压缩 53%；边中位长 38m → 路段中位长 108m）。</item>
///   <item><b>R-T3 路段分 3 类，全由节点度数推出</b>：干线（两端都通）/ 支线（一端悬挂）/
///     孤立段（两端悬挂）。<b>「悬挂」= 度≤1 且不是装卸点</b> —— 路修到破碎站为止是正常终止，
///     不是断头。环线（起讫同一节点）：挂在真路口上的算干线，全接缝的孤立环算孤立段。
///     实测占比 43.0% / 52.3% / 4.1%，三类都有实质里程，换色分得开。</item>
///   <item><b>R-T6 节点类型是派生量，只算不存</b>。加一条边、删一条边，路口/端点立刻跟着变；
///     存进 <see cref="RoadNode.Type"/> 只会多一个「存值与图不一致」的故障面 ——
///     同 <see cref="RoadEdge.MaxAbsSegGradePct"/> 刻意不序列化的理由。</item>
/// </list>
///
/// <b>确定性</b>：节点、边一律按 Id <c>Ordinal</c> 序遍历（<c>Dictionary.Values</c> 的顺序随插删历史漂移），
/// 故同一张图两次分析必得同一套路段 Id 与同一个顺序。
/// </summary>
public static class RoadTopology
{
    /// <summary>接缝处拼中线时的去重合阈值 m（两条边共享的那个顶点会出现两次）。</summary>
    private const double JoinEpsM = 1e-6;

    /// <summary>
    /// 分析一张图的拓扑：度数 → 节点类别 → 路段 → 路段类别。
    ///
    /// <paramref name="passableOnly"/>=true 时<b>把非 <see cref="RoadEdgeStatus.Open"/> 的边当作不存在</b>，
    /// 得到的是「车实际能走的那张网」。这是「边状态」这颗钮唯一说得清的口径 ——
    /// 封一条边在结构上什么都没变（边还在、度数照旧），要回答「封了这条谁就断了」，
    /// 只能拿可通行拓扑跟结构拓扑对比：干线掉成支线、支线掉成孤立段、连通片多出来几片。
    /// 与 <c>PathSolver</c> 禁行这些边的口径同源，否则会出现「拓扑说通、寻径说不通」。
    /// </summary>
    public static RoadTopologyReport Analyze(RoadGraph g, bool passableOnly = false)
    {
        var nodes = g.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var edges = g.Edges
            .Where(e => !passableOnly || e.Status == RoadEdgeStatus.Open)
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

        // ── 度数（自环给同一节点记 2） ──
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in nodes) degree[n.Id] = 0;
        foreach (var e in edges)
        {
            degree[e.FromId] = degree.GetValueOrDefault(e.FromId) + 1;
            degree[e.ToId] = degree.GetValueOrDefault(e.ToId) + 1;
        }

        // ── R-T1 节点类别 ──
        var nodeClass = new Dictionary<string, RoadNodeClass>(StringComparer.Ordinal);
        var nodeCount = new int[Enum.GetValues<RoadNodeClass>().Length];
        foreach (var n in nodes)
        {
            var c = ClassOfDegree(degree.GetValueOrDefault(n.Id));
            nodeClass[n.Id] = c;
            nodeCount[(int)c]++;
        }

        bool IsDepot(string id) => g.GetNode(id)?.Type is RoadNodeType.Loading or RoadNodeType.Unloading;
        // 真节点 = 度≠2 或装卸点（R-T1）。路段就在这些点上断开。
        bool IsBreak(string id) => degree.GetValueOrDefault(id) != 2 || IsDepot(id);
        // 悬挂 = 度≤1 且不是装卸点（R-T3）。修到破碎站为止不算断头。
        bool IsOpen(string id) => degree.GetValueOrDefault(id) <= 1 && !IsDepot(id);

        // 逐节点关联边（按 Id Ordinal 序，确定性）。自环在同一节点出现两次，与度数记 2 自洽。
        var incident = new Dictionary<string, List<RoadEdge>>(StringComparer.Ordinal);
        foreach (var n in nodes) incident[n.Id] = new List<RoadEdge>();
        foreach (var e in edges)
        {
            if (incident.TryGetValue(e.FromId, out var a)) a.Add(e);
            if (incident.TryGetValue(e.ToId, out var b)) b.Add(e);
        }

        // ── R-T2 串成路段 ──
        var used = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<RoadSegment>();

        // ① 自环边单独成段（builder 会跳过自环，这里只是不让退化输入把走链走乱）。
        foreach (var e in edges)
        {
            if (!string.Equals(e.FromId, e.ToId, StringComparison.Ordinal)) continue;
            used.Add(e.Id);
            segments.Add(BuildSegment(g, new List<RoadEdge> { e }, e.FromId, IsOpen, degree, IsDepot));
        }

        // ② 从每个真节点出发，顺着接缝一路走到下一个真节点。
        foreach (var start in nodes)
        {
            if (!IsBreak(start.Id)) continue;
            foreach (var e0 in incident[start.Id])
            {
                if (!used.Add(e0.Id)) continue;
                var chain = new List<RoadEdge> { e0 };
                string cur = Other(e0, start.Id);
                var last = e0;
                while (!IsBreak(cur))
                {
                    var next = incident.GetValueOrDefault(cur)?
                        .FirstOrDefault(x => !string.Equals(x.Id, last.Id, StringComparison.Ordinal) && !used.Contains(x.Id));
                    if (next is null) break;
                    used.Add(next.Id);
                    chain.Add(next);
                    cur = Other(next, cur);
                    last = next;
                }
                segments.Add(BuildSegment(g, chain, start.Id, IsOpen, degree, IsDepot));
            }
        }

        // ③ 剩下的只可能是「整环都是接缝」的孤立环（没有任何真节点可作起点）。
        foreach (var e0 in edges)
        {
            if (!used.Add(e0.Id)) continue;
            var chain = new List<RoadEdge> { e0 };
            string start = e0.FromId;
            string cur = e0.ToId;
            var last = e0;
            while (!string.Equals(cur, start, StringComparison.Ordinal))
            {
                var next = incident.GetValueOrDefault(cur)?
                    .FirstOrDefault(x => !string.Equals(x.Id, last.Id, StringComparison.Ordinal) && !used.Contains(x.Id));
                if (next is null) break;
                used.Add(next.Id);
                chain.Add(next);
                cur = Other(next, cur);
                last = next;
            }
            segments.Add(BuildSegment(g, chain, start, IsOpen, degree, IsDepot));
        }

        segments.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        var segCount = new int[Enum.GetValues<RoadSegmentClass>().Length];
        var segLen = new double[segCount.Length];
        double total = 0;
        foreach (var s in segments)
        {
            segCount[(int)s.Class]++;
            segLen[(int)s.Class] += s.LengthM;
            total += s.LengthM;
        }

        int seams = nodes.Count(n => !IsBreak(n.Id));
        var byEdge = new Dictionary<string, RoadSegment>(StringComparer.Ordinal);
        foreach (var s in segments)
            foreach (var id in s.EdgeIds) byEdge[id] = s;

        return new RoadTopologyReport
        {
            Degree = degree,
            NodeClass = nodeClass,
            Segments = segments,
            SegmentByEdge = byEdge,
            // 连通片自己数，不借 RoadGraph.BuildComponentMap / Validate：那两个一律吃全部边，
            // passableOnly 下就会报出「结构上连通、实际过不去」的假数 —— 这正是本参数要区分的事。
            ComponentCount = CountComponents(nodes, edges),
            PassableOnly = passableOnly,
            NodeCountByClass = nodeCount,
            SegmentCountByClass = segCount,
            SegmentLengthByClass = segLen,
            RealNodeCount = nodes.Count - seams,
            SeamCount = seams,
            TotalLengthM = total,
            ManualCount = segments.Count(s => s.IsManual),
            StaleManualCount = segments.Count(s => s.Stale),
        };
    }

    /// <summary>连通分量数（无向；只吃传入的边集，故 passableOnly 下数的是「车走得通的片」）。</summary>
    private static int CountComponents(List<RoadNode> nodes, List<RoadEdge> edges)
    {
        if (nodes.Count == 0) return 0;
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in nodes) parent[n.Id] = n.Id;
        string Find(string x)
        {
            string r = x;
            while (parent[r] != r) r = parent[r];
            while (parent[x] != r) { var nx = parent[x]; parent[x] = r; x = nx; }
            return r;
        }
        foreach (var e in edges)
        {
            if (!parent.ContainsKey(e.FromId) || !parent.ContainsKey(e.ToId)) continue;
            var ra = Find(e.FromId);
            var rb = Find(e.ToId);
            if (ra != rb) parent[ra] = rb;
        }
        return nodes.Select(n => Find(n.Id)).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>
    /// 两次分析之间的<b>变化</b>，只列真的动了的项（没动就返回空串）。
    /// 「增量增删边」「边状态」每动一下都拿它回显 —— 加一条边到底把两片接上了没有、
    /// 封一条边有没有把干线打成孤立段，这是这两颗钮唯一能当场验证自己有没有生效的东西。
    /// </summary>
    public static string DescribeDelta(RoadTopologyReport before, RoadTopologyReport after)
    {
        var parts = new List<string>();
        foreach (var c in Enum.GetValues<RoadSegmentClass>())
        {
            int a = before.SegmentCountByClass[(int)c], b = after.SegmentCountByClass[(int)c];
            if (a != b) parts.Add($"{TextOf(c)} {a}→{b}");
        }
        if (before.ManualCount != after.ManualCount) parts.Add($"人工改判 {before.ManualCount}→{after.ManualCount}");
        if (before.StaleManualCount != after.StaleManualCount) parts.Add($"改判失效 {before.StaleManualCount}→{after.StaleManualCount}");
        if (before.JunctionCount != after.JunctionCount) parts.Add($"路口 {before.JunctionCount}→{after.JunctionCount}");
        if (before.DangleCount != after.DangleCount) parts.Add($"悬挂端点 {before.DangleCount}→{after.DangleCount}");
        if (before.ComponentCount != after.ComponentCount) parts.Add($"连通片 {before.ComponentCount}→{after.ComponentCount}");
        return string.Join(" · ", parts);
    }

    /// <summary>度数 → 节点类别（R-T1）。</summary>
    public static RoadNodeClass ClassOfDegree(int degree) => degree switch
    {
        <= 0 => RoadNodeClass.Isolated,
        1 => RoadNodeClass.Endpoint,
        2 => RoadNodeClass.Seam,
        3 => RoadNodeClass.Tee,
        _ => RoadNodeClass.Multi,
    };

    /// <summary>节点类别 → 回显用中文标签（命令行回显即图例）。</summary>
    public static string TextOf(RoadNodeClass c) => c switch
    {
        RoadNodeClass.Isolated => "孤立点",
        RoadNodeClass.Endpoint => "端点",
        RoadNodeClass.Seam => "接缝",
        RoadNodeClass.Tee => "丁字",
        _ => "多岔",
    };

    /// <summary>路段类别 → 回显用中文标签。</summary>
    public static string TextOf(RoadSegmentClass c) => c switch
    {
        RoadSegmentClass.Trunk => "干线",
        RoadSegmentClass.Spur => "支线",
        _ => "孤立段",
    };

    private static string Other(RoadEdge e, string at)
        => string.Equals(e.FromId, at, StringComparison.Ordinal) ? e.ToId : e.FromId;

    /// <summary>
    /// R-T7 人工改判的还原口径：改判按<b>边</b>存，一次改判写整条路段的每一条边；
    /// 重算时取该路段各边改判中<b>里程占优</b>的那个，且占优里程要够 <see cref="ManualQuorum"/>
    /// 才采信。够不着说明图已经变了（路段被重新分组、只剩零星几条边还带着旧改判），
    /// 这时退回自动判据并计入 <see cref="RoadTopologyReport.StaleManualCount"/> ——
    /// <b>宁可退回也不能拿一小截边的旧改判去定整条新路段的性质</b>。
    /// </summary>
    public const double ManualQuorum = 0.5;

    /// <summary>把一串边按行进方向拼成一条路段（定向中线 + 累计里程 + 定类）。</summary>
    private static RoadSegment BuildSegment(RoadGraph g, List<RoadEdge> chain, string startId,
                                            Func<string, bool> isOpen, Dictionary<string, int> degree,
                                            Func<string, bool> isDepot)
    {
        var pts = new List<Point3d>();
        double len = 0;
        string cur = startId;
        foreach (var e in chain)
        {
            var line = e.Centerline.Count >= 2
                ? e.Centerline
                : new[] { g.GetNode(e.FromId)!.Position, g.GetNode(e.ToId)!.Position };
            // 边的中线按 From→To 存；走链方向若是 To→From 就得倒过来，否则拼出来是锯齿。
            bool forward = string.Equals(e.FromId, cur, StringComparison.Ordinal);
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[forward ? i : line.Count - 1 - i];
                if (pts.Count > 0 && pts[^1].DistanceTo(p) <= JoinEpsM) continue;   // 接缝处共享顶点，去一次重
                pts.Add(p);
            }
            len += e.LengthM;
            cur = Other(e, cur);
        }

        // R-T3 自动定类。
        bool loop = string.Equals(startId, cur, StringComparison.Ordinal);
        RoadSegmentClass auto;
        if (loop)
            // 挂在真路口/装卸点上的环 = 干线；全接缝的孤立环谁也没接上 = 孤立段。
            auto = degree.GetValueOrDefault(startId) >= 3 || isDepot(startId)
                ? RoadSegmentClass.Trunk : RoadSegmentClass.Isolated;
        else
        {
            int open = (isOpen(startId) ? 1 : 0) + (isOpen(cur) ? 1 : 0);
            auto = open switch
            {
                0 => RoadSegmentClass.Trunk,
                1 => RoadSegmentClass.Spur,
                _ => RoadSegmentClass.Isolated,
            };
        }

        // R-T7 人工改判优先：按里程占优取胜，且要够法定比例才采信。
        var votes = new Dictionary<RoadSegmentClass, double>();
        foreach (var e in chain)
            if (e.RoadClass is { } rc)
                votes[rc] = votes.GetValueOrDefault(rc) + e.LengthM;

        var cls = auto;
        bool manual = false, stale = false;
        if (votes.Count > 0)
        {
            // 并列时按枚举序定胜负，保证同一张图两次分析同一个结果（不吃 Dictionary 顺序）。
            var win = votes.OrderByDescending(kv => kv.Value).ThenBy(kv => (int)kv.Key).First();
            if (len > 1e-9 && win.Value / len >= ManualQuorum) { cls = win.Key; manual = true; }
            else stale = true;
        }

        var ids = chain.Select(e => e.Id).ToList();
        return new RoadSegment(chain[0].Id, ids, startId, cur, pts, len, cls, auto, manual) { Stale = stale };
    }
}
