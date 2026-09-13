using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Views.GeoDb;
using Point3d = PitMine3D.Kylin.Cad.Road.Point3d;
using RoadGraph = PitMine3D.Kylin.Cad.Road.RoadGraph;
using RoadNode = PitMine3D.Kylin.Cad.Road.RoadNode;
using RoadEdge = PitMine3D.Kylin.Cad.Road.RoadEdge;
using RoadNodeType = PitMine3D.Kylin.Cad.Road.RoadNodeType;
using RoadEdgeStatus = PitMine3D.Kylin.Cad.Road.RoadEdgeStatus;
using RoadGraphBuilder = PitMine3D.Kylin.Cad.Road.RoadGraphBuilder;
using RoadTopology = PitMine3D.Kylin.Cad.Road.RoadTopology;
using RoadSegmentClass = PitMine3D.Kylin.Cad.Road.RoadSegmentClass;
using NodingReport = PitMine3D.Kylin.Cad.Road.NodingReport;
using ValidationReport = PitMine3D.Kylin.Cad.Road.ValidationReport;
using WeightMode = PitMine3D.Kylin.Cad.Road.WeightMode;
using DijkstraPathSolver = PitMine3D.Kylin.Cad.Road.DijkstraPathSolver;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「道路运输系统」页签 —— 忠实移植原 <c>RoadLib.RoadLibPlugin</c>（4 组 / 19 钮）的命令逻辑。
/// 本文件：会话状态（每文档一套：会话路网图 / 时段快照 / 演化结果 / 当前存档名 / 编辑会话）+
/// 所有命令共用的基础设施（读中线 / 建图 / 视口取点 / 写 overlay 图层 / 寻径高亮）。
/// 各命令分在 <c>MainWindow.RoadTransport.*.cs</c>。算法核在 <c>Cad/Road/</c>（逐文件对应原 RoadLib）。
/// </summary>
public partial class MainWindow
{
    // ── 常量（同原 RoadLibPlugin）──
    private const string RoadPathPreviewLayer = "寻径结果_预览";
    private const string RoadPathCasingLayer = "寻径结果_廊带";
    private const double RouteLiftM = 3.0;
    private const float RouteCoreLineweight = RoadSymbology.RouteCoreLineweight;
    private const double RouteBandM = 36.0;
    private const string RoadCenterlineLayer = "点云_道路中心线";
    private const double CenterlinePickRadiusM = 50.0;
    private const double RoadTerrainMarginM = 200.0;
    /// <summary>建网吸附/打断容差 m（2026-08-17 由 5m 提到 10m：5~10m 那 229 对漏路口；三维距，上下台阶 ~15m 并不到一起）。</summary>
    private const double RoadDefaultSnapToleranceM = 10.0;
    private const double RoadNodeReuseTolM = 2.0;
    private const double RoadPickZBandM = 30.0;
    private const double RoadBridgeZSepM = 4.0;
    private const double RoadMaxBridgeGapM = 300.0;
    private const double JunctionSnapPixels = 24.0;
    private const double MinJunctionSnapM = 2.0;
    private const double MaxJunctionSnapM = 40.0;
    private const string RoadMarkerPreviewLayer = "装卸点_预览";
    private const string RoadConditionLayer = "路况_预览";
    private const string RoadStructurePavementLayer = "结构路面_预览";
    private const string RoadNetworkPreviewLayer = "路网_预览";
    private const string RoadEvoExtendLayer = "道路演化_延拓";
    private const string RoadEvoShortenLayer = "道路演化_截短";
    private const string RoadEvoAbolishLayer = "道路演化_已废除";
    private const string RoadEvoShiftLayer = "道路演化_移位";
    private const string RoadEvoKeepLayer = "道路演化_保持";
    private const double DepotWarnStubM = 30.0;
    private const double DepotMaxStubM = 80.0;
    /// <summary>路网纳入的道路图层（所有道路输出层）。</summary>
    private static readonly string[] RoadLayers = { RoadCenterlineLayer, "运输坑线_预览" };

    /// <summary>会话级路网状态 —— 挂在文档上（每标签一套），跨文档不串味。</summary>
    private sealed class RoadSession
    {
        /// <summary>会话级持久路网图：建一次复用，供寻径/运距/更新各命令（「基础道路网络构建」重建）。</summary>
        public RoadGraph? Graph;
        /// <summary>时段快照（各开采期路网的命名冻结拷贝；与会话图解耦，可「转为当前路网」回灌）。</summary>
        public readonly List<RoadSnapshot> Snapshots = new();
        /// <summary>「演化对比」最近一次结果；「分色显示」据此上图。</summary>
        public PitMine3D.Kylin.Cad.Road.RoadEvolutionResult? Evolution;
        /// <summary>当前会话路网的来源存档名（「当前路网更新」同名覆盖它；载入/构建时记下）。</summary>
        public string? CurrentNetworkName;
        public PitMine3D.Kylin.Cad.Road.RoadEditSession? Edit;
        public Views.Road.RoadEditWindow? EditWin;
        public bool EditJustCommitted;
        public int BridgeSeq;
        public Views.Road.CenterlineManagerWindow? CenterlineWin;
        public Views.Road.CenterlineOnlySelectionFilter? SelectionFilter;
    }

    private readonly ConditionalWeakTable<DocState, RoadSession> _roadSessions = new();
    private RoadSession RS => _roadSessions.GetValue(_active, _ => new RoadSession());

    // ═══════════════════════ 中线读取 / 建图 ═══════════════════════

    /// <summary>扁平 [x,y,z,...] → Point3d 列表。</summary>
    private static IReadOnlyList<Point3d> RoadToPoints(double[] xyz)
    {
        int n = xyz.Length / 3;
        var pts = new Point3d[n];
        for (int i = 0; i < n; i++) pts[i] = new Point3d(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
        return pts;
    }

    private static double[] RoadToFlat(IReadOnlyList<Point3d> pts)
    {
        var f = new double[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++) { f[3 * i] = pts[i].X; f[3 * i + 1] = pts[i].Y; f[3 * i + 2] = pts[i].Z; }
        return f;
    }

    /// <summary>读图上「点云_道路中心线」层的全部中线快照（实体 + 扁平坐标；退化条目跳过）。</summary>
    private List<(PolylineEntity H, double[] Xyz)> ReadCenterlinePool()
    {
        var pool = new List<(PolylineEntity, double[])>();
        foreach (var e in _scene.Entities)
            if (e is PolylineEntity pl && pl.LayerName == RoadCenterlineLayer && pl.Points.Count >= 2)
                pool.Add((pl, PolylineToFlatXyz(pl)));
        return pool;
    }

    /// <summary>读道路中线（所有线路都转化）：所有道路图层并集 ∪ 当前选集，去重；只取多段线顶点。</summary>
    private List<IReadOnlyList<Point3d>> ReadRoadCenterlines(bool echo = true)
    {
        var seen = new HashSet<SceneEntity>();
        var lines = new List<IReadOnlyList<Point3d>>();
        int layerCount = 0, selCount = 0;
        foreach (var e in _scene.Entities)
        {
            if (e is not PolylineEntity pl || pl.Points.Count < 2) continue;
            if (Array.IndexOf(RoadLayers, pl.LayerName) < 0) continue;
            layerCount++;
            if (seen.Add(pl)) lines.Add(RoadToPoints(PolylineToFlatXyz(pl)));
        }
        foreach (var e in _selected)
        {
            if (e is not PolylineEntity pl || pl.Points.Count < 2) continue;
            selCount++;
            if (seen.Add(pl)) lines.Add(RoadToPoints(PolylineToFlatXyz(pl)));
        }
        if (echo && lines.Count > 0)
            EditEcho($"读取道路中线：{lines.Count} 条（道路图层 {layerCount}" + (selCount > 0 ? $" + 选中 {selCount}" : "") + "，去重后全量转化）");
        return lines;
    }

    /// <summary>读「坑线落地」共享中线（<see cref="HaulRoadCenterlineSet"/>，key transport.haulroads）。写入侧未接时返回空表，行为退回纯图上抽线。</summary>
    private List<HaulRoadCenterline> ReadLandedHaulRoads()
    {
        HaulRoadCenterlineSet? set = null;
        try { set = UserSettings.Current.Get<HaulRoadCenterlineSet>(HaulRoadCenterlineSet.SettingsKey); } catch { }
        var roads = HaulRoadImporter.Read(set);
        if (roads.Count > 0)
            EditEcho($"读取已落地坑线：{HaulRoadImporter.Describe(roads)}"
                     + (string.IsNullOrEmpty(set?.SavedAt) ? "" : $"（{set!.SavedAt} 落地）")
                     + " —— 与设计侧同一份几何，寻径/运距即算这条路。");
        return roads;
    }

    /// <summary>建图后把坑线的路宽/车道/来源回贴到匹配上的边。</summary>
    private void StampHaulRoads(RoadGraph g, IReadOnlyList<HaulRoadCenterline> roads)
    {
        if (roads.Count == 0) return;
        int n = HaulRoadImporter.StampEdges(g, roads);
        EditEcho(n > 0
                ? $"  已落地坑线并入路网：{n} 条边带上路宽/车道/来源标识"
                : "  ⚠ 已落地坑线未匹配到任何边：中线与建图结果对不上（坐标系不一致？中线退化？），几何仍已并入但属性未贴。",
            n > 0 ? EchoLevel.Info : EchoLevel.Warn);
    }

    /// <summary>建网连通性回显：两遍桥接分开报（悬挂端点那遍接碎片跟碎片，片间那遍才把碎片接回主网）。</summary>
    private static string RoadBridgeText(NodingReport nrep, ValidationReport rep)
    {
        if (nrep.TotalBridges == 0) return $"连通片 {rep.ComponentCount}";
        var parts = new List<string>();
        if (nrep.BridgesAdded > 0) parts.Add($"悬挂端点 {nrep.BridgesAdded}");
        if (nrep.GapBridgesAdded > 0) parts.Add($"片间缺口 {nrep.GapBridgesAdded}");
        return $"连通片 {nrep.ComponentsBeforeBridge}→{rep.ComponentCount}（桥接 {string.Join(" + ", parts)} 共 {nrep.TotalBridges} 处）";
    }

    /// <summary>读图 + 抽图（道路图层 + 选集 + 「坑线落地」共享中线 → RoadGraph）+ 报连通。无中线返回 null。各寻径/运距命令共用。</summary>
    private RoadGraph? TryBuildRoadGraph(bool echo = true)
    {
        var rs = RS;
        if (rs.Graph != null && rs.Graph.NodeCount >= 2)
        {
            if (echo) EditEcho($"用已建路网图：{rs.Graph.NodeCount} 节点 / {rs.Graph.EdgeCount} 边（「基础道路网络构建」可重建）");
            return rs.Graph;
        }
        var lines = ReadRoadCenterlines(echo);
        var haulRoads = ReadLandedHaulRoads();
        lines.AddRange(HaulRoadImporter.ToPolylines(haulRoads));   // 已落地坑线与图上中线一起 noding，才真连通
        if (lines.Count == 0)
        {
            if (echo) EditEcho("未找到道路中线。请先选中坑线中线，或用「直线坑线」+「坑线落地」生成运输通道。", EchoLevel.Warn);
            return null;
        }
        var graph = RoadGraphBuilder.FromPolylines(lines, out var nrep, snapToleranceM: RoadDefaultSnapToleranceM);
        StampHaulRoads(graph, haulRoads);
        var rep = graph.Validate();
        if (echo)
        {
            EditEcho($"路网图：中线 {nrep.InputLines}→{nrep.OutputSegments} 段（交叉打断 X{nrep.CrossSplits}/T{nrep.TeeSplits}"
                     + (nrep.DuplicateEdgesRemoved > 0 ? $"，去重叠 {nrep.DuplicateEdgesRemoved}" : "") + "）；"
                     + $"{graph.NodeCount} 节点 / {graph.EdgeCount} 边，{RoadBridgeText(nrep, rep)}",
                rep.IsFullyConnected ? EchoLevel.Success : EchoLevel.Warn);
            if (!rep.IsFullyConnected)
                EditEcho($"⚠ 路网仍未全连通（{rep.ComponentCount} 个独立片）：剩余缺口超 25m 桥接距离或跨标高（立交需走坡道）。"
                         + "可「手动标定线路」补连接，或检查断点。", EchoLevel.Warn);
        }
        rs.Graph = graph;   // 缓存为会话图，后续命令复用
        return graph;
    }

    /// <summary>取本次计算的口径：从共享运输约束（transport.constraints，「约束条件设置」窗写）读一份。三颗钮全部走这里。</summary>
    private static HaulCaliper RoadCaliper(WeightMode mode = WeightMode.Time)
        => HaulCaliper.FromUserSettings(UserSettings.Current, mode);

    // ═══════════════════════ 视口取点 ═══════════════════════

    /// <summary>光标 XY 落点处的地形标高：取所有三角网里含该点的、Z 最高的那张（同「实时曲面坐标」）。没有网面 → 0（原版无地形面时也给 0）。</summary>
    private double RoadSurfaceZAt(double x, double y)
    {
        double bestZ = double.NegativeInfinity;
        foreach (var m in AllMeshes())
        {
            var b = m.Bounds;
            if (x < b.minX || x > b.maxX || y < b.minY || y > b.maxY) continue;
            var z = MeshZ(m, x, y);
            if (z.HasValue && z.Value > bestZ) bestZ = z.Value;
        }
        return double.IsNegativeInfinity(bestZ) ? 0.0 : bestZ;
    }

    /// <summary>视口取一个点（左键一次即完成；Esc 取消 → null）。Z 取地形面（没命中地形面则 0，由 SnapToRoad 判可信度）。</summary>
    private Task<Point3d?> RoadPickOnePointAsync(string prompt)
    {
        var tcs = new TaskCompletionSource<Point3d?>();
        _oneShotPick = (x, y) =>
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) { tcs.TrySetResult(null); return; }
            tcs.TrySetResult(new Point3d(x, y, RoadSurfaceZAt(x, y)));
        };
        _pickConfirmable = false;
        _pickCursor = Controls.CadGlViewport.CursorMode.CrosshairOnly;
        _pickPrompt = prompt;
        StatusMsg.Text = prompt;
        Activate();
        return tcs.Task;
    }

    /// <summary>视口取两点（左键×2 自动完成；Esc 取消）。</summary>
    private async Task<(Point3d a, Point3d b)?> RoadPickTwoPointsAsync(string what)
    {
        EditEcho($"{what}：在视口左键点【起点】和【终点】（Esc 取消）");
        var a = await RoadPickOnePointAsync($"{what}：点起点（Esc 取消）");
        if (a == null) { EditEcho("已取消（不足 2 点）"); return null; }
        EditEcho($"取点 1：({a.Value.X:F1}, {a.Value.Y:F1}, {a.Value.Z:F1})");
        var b = await RoadPickOnePointAsync($"{what}：点终点（Esc 取消）");
        if (b == null) { EditEcho("已取消（不足 2 点）"); return null; }
        EditEcho($"取点 2：({b.Value.X:F1}, {b.Value.Y:F1}, {b.Value.Z:F1})");
        return (a.Value, b.Value);
    }

    // ═══════════════════════ overlay 图层写入（替代原 PmbiWriter → 引擎） ═══════════════════════

    /// <summary>确保 overlay 图层存在并设色/线宽（mm）。原引擎线宽 lineweight×4 px 封顶 16px；这里存 DXF 0.01mm 单位，封顶 211。</summary>
    private Layer RoadEnsureLayer(string name, Rgb c, float lineweightMm)
    {
        var l = _layers.Get(name) ?? _layers.EnsureImported(name, c.R / 255f, c.G / 255f, c.B / 255f);
        l.LineWeight = (short)Math.Min(211, Math.Max(0, (int)Math.Round(lineweightMm * 100)));
        return l;
    }

    /// <summary>清掉某 overlay 图层上的全部实体（不吭声）。返回删掉的实体数。</summary>
    private int RoadClearLayer(string name)
    {
        int n = _scene.Entities.RemoveAll(e => e.LayerName == name);
        if (n > 0) _selected.RemoveAll(e => e.LayerName == name);
        return n;
    }

    private int RoadLayerCount(string name) => _scene.Entities.Count(e => e.LayerName == name);

    /// <summary>写一条三维折线（带 Zs）到图层。</summary>
    private PolylineEntity RoadAddPoly(string layer, IReadOnlyList<Point3d> pts, Rgb c, bool closed = false)
    {
        var pl = new PolylineEntity { LayerName = layer, Closed = closed, Cr = c.R / 255f, Cg = c.G / 255f, Cb = c.B / 255f, Elevation = 0, Zs = new List<double>(pts.Count) };
        foreach (var p in pts) { pl.Points.Add((p.X, p.Y)); pl.Zs.Add(p.Z); }
        _scene.Add(pl);
        return pl;
    }

    private PolylineEntity RoadAddFlat(string layer, double[] flat, Rgb c, bool closed = false)
        => RoadAddPoly(layer, RoadToPoints(flat), c, closed);

    private void RoadAddLine(string layer, double x0, double y0, double z0, double x1, double y1, double z1, Rgb c)
        => RoadAddPoly(layer, new[] { new Point3d(x0, y0, z0), new Point3d(x1, y1, z1) }, c);

    private void RoadAddPoint(string layer, double x, double y, double z, Rgb c)
        => _scene.Add(new PointEntity { LayerName = layer, X = x, Y = y, Elevation = z, Size = 1.0, Cr = c.R / 255f, Cg = c.G / 255f, Cb = c.B / 255f });

    /// <summary>写单行文字（hAlign 0=左 1=中 2=右；vAlign 原版 0=Baseline 1=Bottom 2=Middle 3=Top → Kylin 0=底 1=中 2=顶）。</summary>
    private void RoadAddText(string layer, double x, double y, double z, double height, string text, Rgb c, int hAlign = 0, int vAlign = 2)
    {
        int va = vAlign switch { 3 => 2, 2 => 1, _ => 0 };
        _scene.Add(new TextEntity { LayerName = layer, X = x, Y = y, Elevation = z, Height = height, Text = text, HAlign = hAlign, VAlign = va, Cr = c.R / 255f, Cg = c.G / 255f, Cb = c.B / 255f });
    }

    /// <summary>XY 平面圆的闭合折线顶点，n 段近似。</summary>
    private static Point3d[] RoadRingFlat(double x, double y, double z, double r, int n)
    {
        var a = new Point3d[n];
        for (int i = 0; i < n; i++)
        {
            double t = 2.0 * Math.PI * i / n;
            a[i] = new Point3d(x + r * Math.Cos(t), y + r * Math.Sin(t), z);
        }
        return a;
    }

    /// <summary>在 (x,y) 处画一个标志性装卸点图元：source 放射环 / crusher 料斗方+内菱 / stockpile 同心双环 / dump 料堆三角+基线+倾倒箭头。</summary>
    private void RoadWriteMarkerGlyph(string layer, string kind, double x, double y, double z, double s, Rgb c)
    {
        switch (kind)
        {
            case "source":
                RoadAddPoly(layer, RoadRingFlat(x, y, z, s, 16), c, true);
                RoadAddPoint(layer, x, y, z, c);
                for (int k = 0; k < 4; k++)
                {
                    double t = Math.PI / 2 * k + Math.PI / 4;
                    double dx = Math.Cos(t), dy = Math.Sin(t);
                    RoadAddLine(layer, x + dx * s * 1.05, y + dy * s * 1.05, z, x + dx * s * 1.5, y + dy * s * 1.5, z, c);
                }
                break;
            case "crusher":
            {
                double q = s * 0.78;
                RoadAddPoly(layer, new[] { new Point3d(x - q, y - q, z), new Point3d(x + q, y - q, z), new Point3d(x + q, y + q, z), new Point3d(x - q, y + q, z) }, c, true);
                RoadAddPoly(layer, new[] { new Point3d(x, y + q, z), new Point3d(x + q, y, z), new Point3d(x, y - q, z), new Point3d(x - q, y, z) }, c, true);
                break;
            }
            case "stockpile":
                RoadAddPoly(layer, RoadRingFlat(x, y, z, s, 16), c, true);
                RoadAddPoly(layer, RoadRingFlat(x, y, z, s * 0.55, 12), c, true);
                RoadAddPoint(layer, x, y, z, c);
                break;
            default:
            {
                double h = s * 0.92;
                RoadAddPoly(layer, new[] { new Point3d(x, y + s, z), new Point3d(x - h, y - s * 0.6, z), new Point3d(x + h, y - s * 0.6, z) }, c, true);
                RoadAddLine(layer, x - s * 1.1, y - s * 0.6, z, x + s * 1.1, y - s * 0.6, z, c);
                RoadAddLine(layer, x - s * 0.4, y + s * 1.5, z, x, y + s * 1.12, z, c);
                RoadAddLine(layer, x + s * 0.4, y + s * 1.5, z, x, y + s * 1.12, z, c);
                break;
            }
        }
    }

    /// <summary>在 (x,y,z) 处画一面旗标（半径/旗高 s）：竖旗杆 + 顶部三角旗面 + 杆脚环 + 落点。</summary>
    private void RoadWriteFlagGlyph(string layer, double x, double y, double z, double s, Rgb c)
    {
        double poleTop = z + s;
        RoadAddLine(layer, x, y, z, x, y, poleTop, c);
        double fw = s * 0.62, fh = s * 0.40;
        RoadAddPoly(layer, new[] { new Point3d(x, y, poleTop), new Point3d(x + fw, y, poleTop - fh * 0.5), new Point3d(x, y, poleTop - fh) }, c, true);
        RoadAddPoly(layer, RoadRingFlat(x, y, z, s * 0.16, 12), c, true);
        RoadAddPoint(layer, x, y, z, c);
    }

    // ═══════════════════════ 装卸点 → 路网 ═══════════════════════

    /// <summary>
    /// 把一个装卸点接入会话图：投影到 maxStubM 内的最近边，在投影脚打断该边插 Junction（投影脚已有节点则复用），
    /// 再补一条接入支线（IsTemporary）。装卸点保留用户拾取的真实坐标，不挪到路上。
    /// 返回接入支线长度 m；附近无边可接 → 落孤立节点，返回 -1。
    /// </summary>
    private static double RoadAttachLoadUnload(RoadGraph g, Point3d p, string id, RoadNodeType type,
                                               double throughputTph, double snapTolM, double maxStubM)
    {
        var e = g.NearestEdge(p, maxStubM);
        if (e is null)
        {
            g.AddNode(new RoadNode(id, type, p) { ThroughputTph = throughputTph });
            return -1.0;
        }
        RoadProjectToEdge(g, e, p, out Point3d proj, out double d);
        var hub = g.NearestNode(proj, snapTolM);
        string hubId = hub?.Id ?? g.SplitEdgeAtNearest(e.Id, proj, $"{id}_hub").Id;
        g.AddNode(new RoadNode(id, type, p) { ThroughputTph = throughputTph });
        g.AddEdge(new RoadEdge($"{id}_link", id, hubId, new[] { p, proj }) { IsTemporary = true, SourceRef = RoadEdge.SourceDepotLink });
        return d;
    }

    /// <summary>装卸点到一条边中线的最近投影（水平距，Z 按段插值）。</summary>
    private static void RoadProjectToEdge(RoadGraph g, RoadEdge e, in Point3d p, out Point3d proj, out double dist)
    {
        IReadOnlyList<Point3d> line = e.Centerline.Count >= 2
            ? e.Centerline
            : new[] { g.GetNode(e.FromId)!.Position, g.GetNode(e.ToId)!.Position };
        proj = line[0];
        dist = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i]; var b = line[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            var q = new Point3d(a.X + t * dx, a.Y + t * dy, a.Z + t * (b.Z - a.Z));
            double d = Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
            if (d < dist) { dist = d; proj = q; }
        }
    }

    /// <summary>把持久化的装卸点(DB 优先→user 设置)源/汇节点注入 graph（路网重建后保活；名称已存在则跳过）。必须走 AttachLoadUnload 而不是裸 AddNode。</summary>
    private string RoadInjectLoadUnloadIntoGraph(RoadGraph graph)
    {
        int attached = 0, isolated = 0;
        var longStubs = new List<string>();
        void Attach(string id, RoadNodeType type, Point3d p, double tph)
        {
            if (graph.GetNode(id) is not null) return;
            double stub = RoadAttachLoadUnload(graph, p, id, type, tph, snapTolM: 5.0, maxStubM: DepotMaxStubM);
            if (stub < 0) { isolated++; return; }
            attached++;
            if (stub > DepotWarnStubM) longStubs.Add($"{id}({stub:F0}m)");
        }
        var conn = _geoDb?.Connection;
        var all = Data.LoadUnloadPointStore.LoadAll(conn);
        if (all.Count > 0)
        {
            foreach (var p in all)
                Attach(p.Name, p.IsLoading ? RoadNodeType.Loading : RoadNodeType.Unloading, new Point3d(p.X, p.Y, p.Z), p.ThroughputTph);
        }
        else
        {
            LoadUnloadPointSet? stored = null;
            try { stored = UserSettings.Current.Get<LoadUnloadPointSet>(LoadUnloadPointSet.SettingsKey); } catch { }
            if (stored != null)
                foreach (var p in stored.Points)
                    Attach(p.Id, p.IsLoading ? RoadNodeType.Loading : RoadNodeType.Unloading, new Point3d(p.X, p.Y, p.Z), p.ThroughputTph);
        }
        if (attached == 0 && isolated == 0) return "";
        return $"装卸点接入：{attached} 个已接进路网"
               + (isolated > 0 ? $"，{isolated} 个附近 {DepotMaxStubM:F0}m 内没有路 → 孤立（寻径过不去）" : "")
               + (longStubs.Count > 0 ? $"；接入支线偏长（>{DepotWarnStubM:F0}m）请核实：{string.Join("、", longStubs)}" : "");
    }

    /// <summary>按类型给源汇点画旗标(采剥点红/破碎站紫/排土场蓝/储矿场青)+名称标签，落「装卸点_预览」层（先清旧）。吃的是库里全表。</summary>
    private void RoadHighlightLoadUnloadPoints(IReadOnlyList<Data.LoadUnloadRecord> points)
    {
        RoadClearLayer(RoadMarkerPreviewLayer);
        RoadEnsureLayer(RoadMarkerPreviewLayer, new Rgb(255, 60, 30), 2.4f);
        double s = RoadMarkerSizeForRows(points);
        int crusher = 0;
        foreach (var p in points)
        {
            Rgb c;
            if (p.IsLoading) c = new Rgb(255, 60, 30);
            else switch ((p.UnloadSub ?? "").ToLowerInvariant())
            {
                case "crusher": c = new Rgb(200, 50, 255); crusher++; break;
                case "stockpile": c = new Rgb(0, 225, 210); break;
                default: c = new Rgb(30, 150, 255); break;
            }
            RoadWriteFlagGlyph(RoadMarkerPreviewLayer, p.X, p.Y, p.Z, s, c);
            if (!string.IsNullOrWhiteSpace(p.Name))
                RoadAddText(RoadMarkerPreviewLayer, p.X + s * 0.66, p.Y, p.Z + s * 1.12, s * 0.45, p.Name, c, 0, 2);
        }
        RefreshScene();
        if (points.Count > 0)
            EditEcho($"已在「{RoadMarkerPreviewLayer}」用旗标标出 {points.Count} 个源汇点（破碎站紫旗 {crusher} 面"
                     + $"，采剥点红旗 / 排土场蓝旗 / 储矿场青旗照旧，旗高≈{s:F0}m）");
    }

    /// <summary>源汇旗标尺寸：按各点 XY 跨度的 3% 自适应（下限 30m·上限 400m）。</summary>
    private static double RoadMarkerSizeForRows(IReadOnlyList<Data.LoadUnloadRecord> points)
    {
        if (points.Count == 0) return 30.0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var r in points)
        {
            if (r.X < minX) minX = r.X; if (r.X > maxX) maxX = r.X;
            if (r.Y < minY) minY = r.Y; if (r.Y > maxY) maxY = r.Y;
        }
        double dx = maxX - minX, dy = maxY - minY;
        double diag = Math.Sqrt(dx * dx + dy * dy);
        if (diag < 1.0) return 30.0;
        return Math.Clamp(diag * 0.03, 30.0, 400.0);
    }

    // ═══════════════════════ 寻径高亮（描边 + 芯线 + 箭头 + 起终点） ═══════════════════════

    /// <summary>清寻径高亮的两层（芯线层 + 描边层）。返回删掉的实体数。</summary>
    private int RoadClearPathPreviewLayer()
    {
        int n = RoadClearLayer(RoadPathPreviewLayer) + RoadClearLayer(RoadPathCasingLayer);
        if (n > 0) RefreshScene();
        return n;
    }

    private void RoadClearPathHighlight()
    {
        if (RoadClearPathPreviewLayer() > 0) EditEcho("已清除寻径高亮");
    }

    /// <summary>往返双色高亮：品红 = 去程（含往返共用段）、亮紫 = 仅回程独有段。全品红即代表原路返回。</summary>
    private void RoadHighlightHaulPair(RoadGraph graph, HaulPairResult pair)
    {
        var outLine = pair.Outbound.Feasible
            ? RoadBuildDirectedRoute(graph, pair.Outbound.Path.NodeIds, pair.Outbound.Path.EdgeIds)
            : new List<Point3d>();
        var outbound = pair.Outbound.Feasible ? pair.Outbound.Path.EdgeIds : Array.Empty<string>();
        var retOnly = pair.Return.Feasible
            ? pair.Return.Path.EdgeIds.Where(id => !outbound.Contains(id)).ToList()
            : new List<string>();
        RoadDrawRoute(graph, outLine, retOnly, outbound.Count);
    }

    /// <summary>按行进顺序把一条路径的各边中线首尾相接成一条连续折线（边存储方向与行进方向相反就倒过来拼）。</summary>
    private static List<Point3d> RoadBuildDirectedRoute(RoadGraph g, IReadOnlyList<string> nodeIds, IReadOnlyList<string> edgeIds)
    {
        var line = new List<Point3d>();
        for (int i = 0; i < edgeIds.Count && i + 1 < nodeIds.Count; i++)
        {
            var e = g.GetEdge(edgeIds[i]);
            if (e is null) continue;
            var c = e.Centerline.Count >= 2
                ? e.Centerline.ToList()
                : (g.GetNode(e.FromId), g.GetNode(e.ToId)) is ({ } a, { } b)
                    ? new List<Point3d> { a.Position, b.Position }
                    : new List<Point3d>();
            if (c.Count < 2) continue;
            if (e.ToId == nodeIds[i]) c.Reverse();
            int start = line.Count > 0 && line[^1].DistanceTo(c[0]) < 1e-6 ? 1 : 0;
            for (int k = start; k < c.Count; k++) line.Add(c[k]);
        }
        return line;
    }

    /// <summary>取一组边的中线（无中线的边退回两端节点直线）。</summary>
    private static List<IReadOnlyList<Point3d>> RoadCollectEdgeLines(RoadGraph graph, IReadOnlyList<string> edgeIds)
    {
        var list = new List<IReadOnlyList<Point3d>>();
        foreach (var eid in edgeIds)
        {
            var e = graph.GetEdge(eid);
            if (e is null) continue;
            if (e.Centerline.Count >= 2) { list.Add(e.Centerline); continue; }
            if (graph.GetNode(e.FromId) is { } a && graph.GetNode(e.ToId) is { } b) list.Add(new[] { a.Position, b.Position });
        }
        return list;
    }

    private static List<Point3d> RoadLift(IReadOnlyList<Point3d> line, double lift)
    {
        var o = new List<Point3d>(line.Count);
        foreach (var p in line) o.Add(new Point3d(p.X, p.Y, p.Z + lift));
        return o;
    }

    /// <summary>
    /// 画寻径结果：描边 + 芯线 + 方向箭头 + 起终点，落「寻径结果_廊带 / _预览」两层（先清旧）。
    /// 结果线几乎总叠在「路网预览」上且与中线几何重合：换色相（品红/亮紫）+ 最粗芯线 + 世界尺度廊带 + 抬高 3m + 箭头/起终点，四道各治一处。
    /// </summary>
    private void RoadDrawRoute(RoadGraph graph, IReadOnlyList<Point3d> outLine, IReadOnlyList<string> returnOnlyEdges, int outboundEdgeCount)
    {
        RoadClearPathPreviewLayer();
        var retLines = RoadCollectEdgeLines(graph, returnOnlyEdges);
        if (outLine.Count < 2 && retLines.Count == 0) return;

        var casing = RoadSymbology.RouteCasing;
        var outC = RoadSymbology.RouteOutbound;
        var retC = RoadSymbology.RouteReturn;
        RoadEnsureLayer(RoadPathCasingLayer, casing, 1.5f);
        RoadEnsureLayer(RoadPathPreviewLayer, outC, RouteCoreLineweight);

        // ① 廊带：沿路线左右外扩 RouteBandM/2 的闭合边界（世界尺度，越放大越清楚）。
        var band = new List<IReadOnlyList<Point3d>>();
        if (outLine.Count >= 2) band.Add(RoadLift(outLine, RouteLiftM - 0.5));
        foreach (var l in retLines) band.Add(RoadLift(l, RouteLiftM - 0.5));
        foreach (var rb in PitMine3D.Kylin.Cad.Road.StructurePavement.BuildRibbons(band, RouteBandM))
            RoadAddFlat(RoadPathCasingLayer, rb, casing, true);
        // ② 芯线
        if (outLine.Count >= 2) RoadAddPoly(RoadPathPreviewLayer, RoadLift(outLine, RouteLiftM), outC);
        foreach (var l in retLines) RoadAddPoly(RoadPathPreviewLayer, RoadLift(l, RouteLiftM), retC);
        // ③ 方向箭头（只画去程）+ ④ 起终点
        double s = RoadGlyphSizeFor(outLine, retLines);
        if (outLine.Count >= 2)
        {
            RoadDrawChevrons(outLine, s, outC);
            RoadDrawEndMarker(outLine[0], s, outC, "起点");
            RoadDrawEndMarker(outLine[^1], s, retLines.Count > 0 ? retC : outC, "终点");
        }
        RefreshScene();
        EditEcho($"已高亮线路（玫红 = 去程 {outboundEdgeCount} 段，箭头指行进方向；芯线 {RouteCoreLineweight * 4:F0}px = 引擎上限、路网干线是 8px；"
                 + $"外加 {RouteBandM:F0}m 廊带、抬高 {RouteLiftM:F0}m 画在路网之上）"
                 + (retLines.Count > 0 ? $"；亮紫 = 仅回程 {retLines.Count} 段（回程走了别的路）" : "；回程原路返回"));
    }

    /// <summary>标记尺寸：按路线跨度的 2.5% 自适应（下限 15m·上限 200m）。</summary>
    private static double RoadGlyphSizeFor(IReadOnlyList<Point3d> outLine, List<IReadOnlyList<Point3d>> others)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Feed(IReadOnlyList<Point3d> l)
        {
            foreach (var p in l)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
        }
        Feed(outLine);
        foreach (var l in others) Feed(l);
        if (minX > maxX) return 20.0;
        return Math.Clamp(Math.Max(maxX - minX, maxY - minY) * 0.025, 15.0, 200.0);
    }

    /// <summary>沿路线每隔一段画一个「&gt;」形箭头指向行进方向（间距按标记尺寸的 6 倍，至少 3 个）。</summary>
    private void RoadDrawChevrons(IReadOnlyList<Point3d> line, double s, Rgb color)
    {
        double total = 0;
        for (int i = 1; i < line.Count; i++) total += line[i].HorizontalDistanceTo(line[i - 1]);
        if (total < 1e-6) return;
        double step = Math.Min(s * 6.0, total / 3.0);
        if (step < 1e-6) return;
        double next = step * 0.5, run = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double seg = line[i].HorizontalDistanceTo(line[i - 1]);
            if (seg < 1e-9) continue;
            double ux = (line[i].X - line[i - 1].X) / seg, uy = (line[i].Y - line[i - 1].Y) / seg;
            while (next <= run + seg)
            {
                double t = (next - run) / seg;
                double cx = line[i - 1].X + (line[i].X - line[i - 1].X) * t;
                double cy = line[i - 1].Y + (line[i].Y - line[i - 1].Y) * t;
                double cz = line[i - 1].Z + (line[i].Z - line[i - 1].Z) * t + RouteLiftM + 0.2;
                double px = -uy, py = ux;
                double tipX = cx + ux * s * 0.5, tipY = cy + uy * s * 0.5;
                double bx = cx - ux * s * 0.5, by = cy - uy * s * 0.5;
                RoadAddLine(RoadPathPreviewLayer, tipX, tipY, cz, bx + px * s * 0.55, by + py * s * 0.55, cz, color);
                RoadAddLine(RoadPathPreviewLayer, tipX, tipY, cz, bx - px * s * 0.55, by - py * s * 0.55, cz, color);
                next += step;
            }
            run += seg;
        }
    }

    /// <summary>起/终点标记：一个方框 + 叉 + 文字。</summary>
    private void RoadDrawEndMarker(in Point3d p, double s, Rgb color, string text)
    {
        double r = s * 0.9, z = p.Z + RouteLiftM + 0.2;
        RoadAddPoly(RoadPathPreviewLayer, new[] { new Point3d(p.X - r, p.Y - r, z), new Point3d(p.X + r, p.Y - r, z), new Point3d(p.X + r, p.Y + r, z), new Point3d(p.X - r, p.Y + r, z) }, color, true);
        RoadAddLine(RoadPathPreviewLayer, p.X - r, p.Y - r, z, p.X + r, p.Y + r, z, color);
        RoadAddLine(RoadPathPreviewLayer, p.X - r, p.Y + r, z, p.X + r, p.Y - r, z, color);
        RoadAddText(RoadPathPreviewLayer, p.X + r * 1.3, p.Y, z + r, s * 0.9, text, color, 0, 2);
    }

    /// <summary>取点吸不上时的图上交代：黄叉标"你点的位置"、连一条线到最近的路面、写上实距。标记画在取点 XY + 最近路面的 Z 上。</summary>
    private void RoadMarkPickMiss(in Point3d pick, in Point3d nearestOnRoad, double distM, string what)
    {
        var amber = new Rgb(255, 190, 0);
        RoadEnsureLayer(RoadPathPreviewLayer, amber, 1.2f);
        double z = nearestOnRoad.Z;
        double s = Math.Max(12.0, distM * 0.12);
        RoadAddLine(RoadPathPreviewLayer, pick.X - s, pick.Y - s, z, pick.X + s, pick.Y + s, z, amber);
        RoadAddLine(RoadPathPreviewLayer, pick.X - s, pick.Y + s, z, pick.X + s, pick.Y - s, z, amber);
        RoadAddLine(RoadPathPreviewLayer, pick.X, pick.Y, z, nearestOnRoad.X, nearestOnRoad.Y, nearestOnRoad.Z, amber);
        RoadAddPoint(RoadPathPreviewLayer, nearestOnRoad.X, nearestOnRoad.Y, nearestOnRoad.Z, amber);
        RoadAddText(RoadPathPreviewLayer, pick.X + s, pick.Y, z + s, s * 0.9, $"{what} 平面上离最近路面 {distM:F0}m", amber, 0, 2);
        RefreshScene();
        EditEcho($"已在「{RoadPathPreviewLayer}」用黄叉标出{what}位置并连到最近的路面。");
    }

    /// <summary>不可达时的图上交代：起点所在连通片画品红、终点所在片画亮紫，缺口链画成缺口红连线 + 标注宽度/高差。整体抬高 RouteLiftM。</summary>
    private void RoadHighlightDisconnected(RoadGraph g, string fromId, string toId, RoadBridgePlan plan)
    {
        RoadClearPathPreviewLayer();
        var comp = g.BuildComponentMap();
        int cs = comp.GetValueOrDefault(fromId, 0), cd = comp.GetValueOrDefault(toId, 0);
        var srcC = RoadSymbology.RouteOutbound;
        var dstC = RoadSymbology.RouteReturn;
        var gapC = RoadSymbology.Gap;
        RoadEnsureLayer(RoadPathCasingLayer, srcC, 1.5f);
        RoadEnsureLayer(RoadPathPreviewLayer, gapC, RouteCoreLineweight);
        foreach (var e in g.Edges)
        {
            int c = comp.GetValueOrDefault(e.FromId, 0);
            if (c != cs && c != cd) continue;
            IReadOnlyList<Point3d> line = e.Centerline.Count >= 2
                ? e.Centerline
                : (g.GetNode(e.FromId), g.GetNode(e.ToId)) is ({ } a, { } b) ? new[] { a.Position, b.Position } : Array.Empty<Point3d>();
            if (line.Count >= 2) RoadAddPoly(RoadPathCasingLayer, RoadLift(line, RouteLiftM), c == cs ? srcC : dstC);
        }
        int k = 1;
        foreach (var gap in plan.Gaps)
        {
            RoadAddPoly(RoadPathPreviewLayer, RoadLift(new[] { gap.From, gap.To }, RouteLiftM), gapC);
            double s = Math.Max(10.0, gap.GapM * 0.25);
            foreach (var p0 in new[] { gap.From, gap.To })
            {
                var p = new Point3d(p0.X, p0.Y, p0.Z + RouteLiftM);
                RoadAddLine(RoadPathPreviewLayer, p.X - s, p.Y - s, p.Z, p.X + s, p.Y + s, p.Z, gapC);
                RoadAddLine(RoadPathPreviewLayer, p.X - s, p.Y + s, p.Z, p.X + s, p.Y - s, p.Z, gapC);
            }
            var mid = new Point3d((gap.From.X + gap.To.X) / 2, (gap.From.Y + gap.To.Y) / 2, (gap.From.Z + gap.To.Z) / 2 + RouteLiftM);
            RoadAddText(RoadPathPreviewLayer, mid.X, mid.Y, mid.Z + s, s,
                $"缺口{k++} {gap.GapM:F0}m" + (gap.IsFlat(RoadBridgeZSepM) ? "" : $" Δz{gap.DzM:+0;-0}m 需坡道"), gapC, 1, 1);
        }
        RefreshScene();
        EditEcho($"已在「{RoadPathPreviewLayer}」标出断口：品红 = 起点所在片 #{cs}、亮紫 = 终点所在片 #{cd}、红 = 要补的 {plan.Gaps.Count} 处缺口。");
    }

    /// <summary>边状态中文。</summary>
    private static string RoadStatusText(RoadEdgeStatus s) => s switch
    {
        RoadEdgeStatus.Open => "开放",
        RoadEdgeStatus.Maintenance => "检修",
        _ => "封闭",
    };

    /// <summary>线路类型中文（null=未改判，走自动判据）。</summary>
    private static string RoadClassTextR(RoadSegmentClass? c) => c is null ? "自动" : RoadTopology.TextOf(c.Value);

    private Task RoadInfoAsync(string title, string text) => CoalMsgBox.ShowAsync(this, title, text);
    private Task<bool> RoadConfirmAsync(string title, string text, string ok = "确定", string cancel = "取消") => CoalMsgBox.ConfirmAsync(this, title, text, ok, cancel);
}
