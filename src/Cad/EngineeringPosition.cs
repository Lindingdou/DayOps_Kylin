using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

/// <summary>示意图节点种类：模板台阶端点 / 端帮台阶断口。</summary>
public enum EpNodeKind { Template = 0, EndWall = 1 }

/// <summary>一个节点组里的真实几何点（组 = 同侧同标高的一撮端点；逐端点档一组就一个点）。</summary>
public sealed class EpMember
{
    public double X, Y, Z;
    public double U;                 // 走向轴坐标
    public ulong  Handle;            // 来源线（0=不可追溯）—— Kylin 无实体句柄，由提取方按序编号
    public string Layer = "";
    public double EdgeDist;          // 到采场范围边界的平面距离(m)

    /// <summary>
    /// **走位**：从这个点沿它所在的那条台阶线走开去的点列（trail[0] = 本点）。两侧含义不同：
    ///  · 端帮断口 —— 往**范围外**走，是"还能往外吃多远"的退路，衔接段靠它定长度；
    ///  · 模板端点 —— 往**模板里**走，只用来取这一端的**走势切向**（平面上顺着原台阶线接出去）。
    /// 见 <see cref="EpConnectorBuilder"/>。
    /// </summary>
    public IReadOnlyList<(double X, double Y, double Z)>? Trail;
}

/// <summary>喂给「断口→端帮节点」的一处断口（点 + 来源 + 退路）。</summary>
public readonly struct EpCutPoint
{
    public readonly double X, Y, Z;
    public readonly ulong  Handle;
    public readonly string Layer;
    /// <summary>退路（trail[0] = 断口本身，逐点向外）；null = 拿不到。</summary>
    public readonly IReadOnlyList<(double X, double Y, double Z)>? Trail;
    public EpCutPoint(double x, double y, double z, ulong handle, string layer, IReadOnlyList<(double X, double Y, double Z)>? trail = null)
    { X = x; Y = y; Z = z; Handle = handle; Layer = layer ?? ""; Trail = trail; }
}

/// <summary>
/// 示意图上的一个节点。逐端点档（现场口径「所有台阶线的所有端点都开放」）一个端点一个节点；
/// 聚组档 = 【同一侧 · 同一标高】的一撮端点。
/// </summary>
public sealed class EpNode
{
    public string     Id = "";
    public EpNodeKind Kind;
    public int        Side;          // 0=A 侧（走向坐标小的一端） 1=B 侧
    public double     Z;             // 组代表标高 = 组内均值
    public double     X, Y, U;       // 代表点（组内最靠外的那个成员）
    public string     Label = "";    // 层名摘要（"9煤" / "岩台阶" / "端帮"）
    public readonly List<EpMember> Members = new();
    public int Count => Members.Count;

    /// <summary>组内最小/最大标高（组内离散度，回显用）。</summary>
    public double ZMin = double.MaxValue, ZMax = double.MinValue;
    /// <summary>代表点到采场范围边界的平面距离(m)。</summary>
    public double EdgeDist;

    /// <summary>端帮节点归到同侧第几级模板台阶（−1 = 归不进任何一级）。模板节点恒为 −1。
    /// 归级只定"这一撮算一组"，**代表标高仍取组内真实均值** —— 拿模板标高顶替就把 Δz 抹平了。</summary>
    public int    MatchedLevel = -1;
    /// <summary>归到的那一级模板标高（<see cref="MatchedLevel"/> ≥ 0 时有效）。</summary>
    public double LevelZ;
    /// <summary>**接不上**：这一撮端帮台阶归不进任何一级模板台阶。</summary>
    public bool   Unmatched => Kind == EpNodeKind.EndWall && MatchedLevel < 0;
}

/// <summary>一条连线：两个节点（原版字段名保留 TemplateId/WallId；所有端点开放后两头可以是任意角色）。</summary>
public sealed class EpLink
{
    public string TemplateId = "";
    public string WallId = "";
    /// <summary>true=自动配对给出的建议（虚线）；false=用户拖出来/已确认的（实线）。</summary>
    public bool   Suggested;
}

/// <summary>「创建工程位置」一屏的全部内容：采场范围 + 模板 + 端帮 + 节点 + 连线。</summary>
public sealed class EpScene
{
    public bool   Success;
    public string Error = "";
    public string Diag  = "";

    public string   RegionName = "";
    public double[] RingXy = Array.Empty<double>();   // 采场范围环（扁平 XY，隐式闭合）；空=退化用模板两端切面

    public double AxisX = 1, AxisY = 0;               // 走向单位向量（指向 B 侧）
    public string SideAName = "A 端", SideBName = "B 端";

    public readonly List<EpNode> Nodes = new();
    public readonly List<EpLink> Links = new();

    /// <summary>端帮节点是不是从 ④ 的**裁剪断口**灌进来的（false = 按采场范围环切的那批）。</summary>
    public bool EndWallFromCuts;

    /// <summary>端帮归级用的容差(m) = 半个模板级距（回显用）。</summary>
    public double WallLevelTol;
    /// <summary>归不进任何一级模板台阶的端帮节点数 —— **接不上**的地方。</summary>
    public int WallUnmatched;

    public readonly List<List<(double X, double Y, double Z)>> TemplateLines = new();  // 模板台阶
    public readonly List<List<(double X, double Y, double Z)>> WallKeep = new();       // 范围外的端帮保留段

    public double UMinT, UMaxT;                        // 模板走向范围
    public double ZMin, ZMax;                          // 全部节点标高范围

    public IEnumerable<EpNode> Column(EpNodeKind k, int side)
    {
        foreach (var n in Nodes) if (n.Kind == k && n.Side == side) yield return n;
    }

    public EpNode? ById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var n in Nodes) if (n.Id == id) return n;
        return null;
    }
    public EpNode? Node(string id) => ById(id);
}

/// <summary>喂给提取器的一条图上多段线（与场景实体解耦，便于脱 GUI 跑）。</summary>
public readonly struct EpPolyline
{
    public readonly ulong   Handle;
    public readonly string  Layer;
    public readonly double[] Xyz;      // 扁平 [x,y,z,...]
    public readonly bool    Closed;
    public EpPolyline(ulong handle, string layer, double[] xyz, bool closed)
    { Handle = handle; Layer = layer ?? ""; Xyz = xyz ?? Array.Empty<double>(); Closed = closed; }
}

/// <summary>
/// 「创建工程位置」的几何层（忠实移植原 <c>MineAssLib.Driving.EngineeringPositionBuilder</c>）。
///
/// 口径（原版 2026-08-06/07 现场定，照搬）：
///  ① <b>采场范围 = 可采范围环</b>。开采模板放在范围内，范围<b>外</b>的现状采场台阶就是端帮。
///  ② <b>端帮节点 = 采场台阶被范围环分割出的断口</b>，断口点自带真实 XYZ + 退路（范围外那段保留线）。
///     只取环上<b>横切走向</b>的边（边界局部走势按 ±30m 弧长平滑后与走向轴夹角 &gt; 60°）产生的断口。
///  ③ <b>模板节点 = 模板台阶线两端端点</b>。侧别按走向坐标 u 与中位比较。
///  ④ 逐端点档（<c>allEndpointNodes</c>，窗口默认）：<b>所有台阶线的所有端点各成一个节点、一律开放</b> ——
///     就近接哪两段，判据长在平面上；按侧别+标高聚组会把"谁挨着谁"整个抹掉。
///     聚组档：模板按 ±zGroupTol 自聚；端帮归到同侧最近模板级（容差 = 半个级距），代表标高仍取真实均值。
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item>Kylin 多段线无实体句柄：<c>Handle</c> 由提取方按序编号（≥1），只用于"同一条线的两头"判定与回溯。</item>
///   <item>二维多段线的 Z 取 <c>Elevation</c>（有 Zs 的三维线逐点取）。</item>
/// </list>
/// </summary>
public static class EngineeringPositionBuilder
{
    /// <summary>横切判据：环边方向与走向轴夹角 &gt; 60° 才算"端"（cos60°=0.5）。</summary>
    private const double EndEdgeCos = 0.5;

    public static EpScene Build(
        IReadOnlyList<EpPolyline> templateLines,
        IReadOnlyList<EpPolyline> pitLines,
        double[]? ringXy,
        string regionName,
        double zGroupTol = 0.5,
        bool endEdgesOnly = true,
        bool pitEndpointsAreCuts = false,
        bool trustTemplateList = false,
        bool allEndpointNodes = false)
    {
        var s = new EpScene { RegionName = regionName ?? "" };
        try
        {
            templateLines ??= Array.Empty<EpPolyline>();
            pitLines ??= Array.Empty<EpPolyline>();
            if (zGroupTol <= 0) zGroupTol = 0.5;

            // 【模板清单已经定死时不许再筛一遍】trustTemplateList=true 意味着调用方已按白名单挑好了
            bool IsTpl(string lay) => trustTemplateList || IsTemplateSide(lay, pitEndpointsAreCuts);

            // ── ① 走向轴
            if (!TryStrikeAxis(templateLines, trustTemplateList, out double ax, out double ay) &&
                !TryRingAxis(ringXy, out ax, out ay))
            { ax = 1; ay = 0; }
            s.AxisX = ax; s.AxisY = ay;
            s.SideBName = Compass(ax, ay);
            s.SideAName = Compass(-ax, -ay);

            double U(double x, double y) => x * ax + y * ay;

            // ── ② 模板走向范围
            double uMinT = double.MaxValue, uMaxT = double.MinValue;
            foreach (var pl in templateLines)
            {
                if (!IsTpl(pl.Layer) || pl.Xyz.Length < 6) continue;
                for (int i = 0; i + 2 < pl.Xyz.Length; i += 3)
                { double u = U(pl.Xyz[i], pl.Xyz[i + 1]); if (u < uMinT) uMinT = u; if (u > uMaxT) uMaxT = u; }
            }
            bool hasTemplate = uMinT <= uMaxT;
            s.UMinT = hasTemplate ? uMinT : 0;
            s.UMaxT = hasTemplate ? uMaxT : 0;

            var ring = NormalizeRing(ringXy);
            bool hasRing = ring.Length >= 6;
            if (!hasRing && !hasTemplate)
            {
                s.Error = $"既没有采场范围环，也没有模板台阶线——无从定位两侧端帮。"
                        + $"（传进来的模板侧 {templateLines.Count} 条"
                        + (trustTemplateList
                            ? "，已按你指定的模板图层白名单收下，说明这几条要么点数<2、要么全是搭接线。"
                            : $"，其中过「台阶_*」判据的 0 条 —— 若模板不是「驱动量」做的，请在①提取旁的「模板图层 ▾」里指定它所在的图层。")
                        + $" 采场侧 {pitLines.Count} 条。）";
                return s;
            }
            s.RingXy = ring;

            double uMid = hasRing ? RingUMid(ring, ax, ay) : 0.5 * (s.UMinT + s.UMaxT);
            if (hasTemplate && hasRing) uMid = 0.5 * (0.5 * (s.UMinT + s.UMaxT) + uMid);

            // ── ③ 模板节点：每条台阶线的两个端点
            var tMembers = new List<(EpMember M, int Side, string Tag)>();
            int tplLineCount = 0, skipLap = 0;
            foreach (var pl in templateLines)
            {
                if (pl.Xyz.Length < 6) continue;
                if (IsLapLine(pl.Layer)) { skipLap++; continue; }
                if (!IsTpl(pl.Layer)) continue;
                tplLineCount++;
                s.TemplateLines.Add(ToPts(pl.Xyz));
                int n = pl.Xyz.Length / 3;
                foreach (int i in new[] { 0, n - 1 })
                {
                    var m = new EpMember
                    {
                        X = pl.Xyz[i * 3], Y = pl.Xyz[i * 3 + 1], Z = pl.Xyz[i * 3 + 2],
                        Handle = pl.Handle, Layer = pl.Layer,
                        Trail = InwardTrail(pl.Xyz, fromHead: i == 0),
                    };
                    m.U = U(m.X, m.Y);
                    m.EdgeDist = hasRing ? DistToRing(ring, m.X, m.Y)
                                         : Math.Min(Math.Abs(m.U - s.UMinT), Math.Abs(m.U - s.UMaxT));
                    tMembers.Add((m, m.U < uMid ? 0 : 1, LayerTag(pl.Layer)));
                }
            }

            // ── ④ 采场（端帮）侧节点
            var rg = hasRing ? new RingGeom(ring) : null;
            int pitCount = 0, crossCount = 0, endCross = 0;
            var wMembers = new List<(EpMember M, int Side, string Tag)>();

            if (allEndpointNodes)
            {
                foreach (var pl in pitLines)
                {
                    if (pl.Xyz.Length < 6) continue;
                    pitCount++;
                    int np = pl.Xyz.Length / 3;
                    s.WallKeep.Add(ToPts(pl.Xyz));
                    for (int e = 0; e < 2; e++)
                    {
                        int vi = e == 0 ? 0 : np - 1;
                        var me = new EpMember
                        {
                            X = pl.Xyz[vi * 3], Y = pl.Xyz[vi * 3 + 1], Z = pl.Xyz[vi * 3 + 2],
                            Handle = pl.Handle, Layer = pl.Layer, EdgeDist = 0,
                            Trail = InwardTrail(pl.Xyz, fromHead: e == 0),
                        };
                        me.U = U(me.X, me.Y);
                        crossCount++; endCross++;
                        wMembers.Add((me, me.U < uMid ? 0 : 1, LayerTag(pl.Layer)));
                    }
                }
                goto grouped;
            }

            foreach (var pl in pitLines)
            {
                if (pl.Xyz.Length < 6) continue;
                pitCount++;

                if (pitEndpointsAreCuts)
                {
                    int np = pl.Xyz.Length / 3;
                    s.WallKeep.Add(ToPts(pl.Xyz));
                    for (int e = 0; e < 2; e++)
                    {
                        int vi = e == 0 ? 0 : np - 1;
                        var trail = new List<(double X, double Y, double Z)>(np);
                        if (e == 0) for (int k = 0; k < np; k++) trail.Add((pl.Xyz[k * 3], pl.Xyz[k * 3 + 1], pl.Xyz[k * 3 + 2]));
                        else for (int k = np - 1; k >= 0; k--) trail.Add((pl.Xyz[k * 3], pl.Xyz[k * 3 + 1], pl.Xyz[k * 3 + 2]));
                        crossCount++; endCross++;
                        var me = new EpMember
                        {
                            X = pl.Xyz[vi * 3], Y = pl.Xyz[vi * 3 + 1], Z = pl.Xyz[vi * 3 + 2],
                            Handle = pl.Handle, Layer = pl.Layer, EdgeDist = 0, Trail = trail,
                        };
                        me.U = U(me.X, me.Y);
                        wMembers.Add((me, me.U < uMid ? 0 : 1, LayerTag(pl.Layer)));
                    }
                    continue;
                }

                SplitByBoundary(pl, rg, ax, ay, s.UMinT, s.UMaxT, out var outside, out var crossings);
                foreach (var seg in outside) if (seg.Count >= 2) s.WallKeep.Add(seg);
                foreach (var (c, trail) in crossings)
                {
                    crossCount++;
                    if (endEdgesOnly && !c.IsEnd) continue;
                    endCross++;
                    var m = new EpMember
                    {
                        X = c.X, Y = c.Y, Z = c.Z, U = U(c.X, c.Y),
                        Handle = pl.Handle, Layer = pl.Layer, EdgeDist = 0,
                        Trail = trail,
                    };
                    wMembers.Add((m, m.U < uMid ? 0 : 1, LayerTag(pl.Layer)));
                }
            }

        grouped:
            if (allEndpointNodes)
            {
                foreach (var (m, side, tag) in tMembers)
                    EmitNode(s, new List<(EpMember, string)> { (m, tag) }, EpNodeKind.Template, side, -1, m.Z);
                foreach (var (m, side, tag) in wMembers)
                    EmitNode(s, new List<(EpMember, string)> { (m, tag) }, EpNodeKind.EndWall, side, -1, m.Z);
            }
            else
            {
                GroupInto(s, tMembers, EpNodeKind.Template, zGroupTol);
                GroupWallByTemplateLevels(s, wMembers, zGroupTol);
            }

            s.ZMin = double.MaxValue; s.ZMax = double.MinValue;
            foreach (var n in s.Nodes) { if (n.Z < s.ZMin) s.ZMin = n.Z; if (n.Z > s.ZMax) s.ZMax = n.Z; }
            if (s.Nodes.Count == 0) { s.ZMin = 0; s.ZMax = 1; }
            else if (s.ZMax - s.ZMin < 1e-6) { s.ZMin -= 1; s.ZMax += 1; }

            int tA = 0, tB = 0, wA = 0, wB = 0;
            foreach (var n in s.Nodes)
            {
                if (n.Kind == EpNodeKind.Template) { if (n.Side == 0) tA++; else tB++; }
                else { if (n.Side == 0) wA++; else wB++; }
            }
            s.Diag = $"走向 {s.SideAName}↔{s.SideBName}（方位 {Bearing(ax, ay):0.#}°）· " +
                     $"模板台阶线 {tplLineCount} 条{(skipLap > 0 ? $"（另跳过搭接线 {skipLap} 条）" : "")} · " +
                     $"采场台阶线 {pitCount} 条 → 断口 {crossCount} 处，取端帮 {endCross} 处 · " +
                     $"节点：模板 {tA}/{tB}，端帮 {wA}/{wB}（{s.SideAName}/{s.SideBName}）· " +
                     $"标高 {s.ZMin:0.#}~{s.ZMax:0.#}m · " +
                     (allEndpointNodes ? "逐端点成节点（所有端点开放）" :
                        $"模板分组容差 ±{zGroupTol:0.##}m · 端帮归到最近模板级（容差 ±{s.WallLevelTol:0.##}m = 半个级距）" +
                        (s.WallUnmatched > 0 ? $"，其中 {s.WallUnmatched} 处归不进任何一级（接不上）" : "")) + " · " +
                     (hasRing ? $"采场范围「{s.RegionName}」{ring.Length / 2} 点环" : "无范围环（退化用模板两端横切面）");
            s.Success = true;
            return s;
        }
        catch (Exception ex)
        {
            s.Success = false;
            s.Error = $"{ex.GetType().Name}: {ex.Message}";
            return s;
        }
    }

    /// <summary>
    /// 把 ④替换算出的**裁剪断口**灌成端帮节点（归组口径与 <see cref="Build"/> 聚组档一致：归到同侧最近模板级）。
    /// 【为什么要这条路】<see cref="Build"/> 的端帮断口是采场范围环切出来的，而 ④替换真正动刀的边界是
    /// 工作线 × 模板覆盖范围那条带 —— 裁剪与连线必须共用同一条边界，否则连的是图上不存在的断口。
    /// </summary>
    /// <returns>新增的端帮节点数。</returns>
    public static int AddEndWallNodesFromCuts(EpScene s, IReadOnlyList<EpCutPoint> cuts, double zGroupTol = 0.5, bool replaceExisting = true)
    {
        if (s == null || !s.Success || cuts == null || cuts.Count == 0) return 0;
        if (zGroupTol <= 0) zGroupTol = 0.5;
        if (replaceExisting)
        {
            var doomed = new HashSet<string>();
            foreach (var n in s.Nodes) if (n.Kind == EpNodeKind.EndWall) doomed.Add(n.Id);
            s.Nodes.RemoveAll(n => n.Kind == EpNodeKind.EndWall);
            s.Links.RemoveAll(l => doomed.Contains(l.WallId) || doomed.Contains(l.TemplateId));
        }
        double ax = s.AxisX, ay = s.AxisY;
        double uMid = 0.5 * (s.UMinT + s.UMaxT);
        var src = new List<(EpMember M, int Side, string Tag)>(cuts.Count);
        foreach (var c in cuts)
        {
            var m = new EpMember { X = c.X, Y = c.Y, Z = c.Z, U = c.X * ax + c.Y * ay, Handle = c.Handle, Layer = c.Layer ?? "", EdgeDist = 0, Trail = c.Trail };
            src.Add((m, m.U < uMid ? 0 : 1, LayerTag(c.Layer ?? "")));
        }
        int before = s.Nodes.Count;
        GroupWallByTemplateLevels(s, src, zGroupTol);
        s.EndWallFromCuts = true;
        s.ZMin = double.MaxValue; s.ZMax = double.MinValue;
        foreach (var n in s.Nodes) { if (n.Z < s.ZMin) s.ZMin = n.Z; if (n.Z > s.ZMax) s.ZMax = n.Z; }
        if (s.Nodes.Count == 0) { s.ZMin = 0; s.ZMax = 1; }
        else if (s.ZMax - s.ZMin < 1e-6) { s.ZMin -= 1; s.ZMax += 1; }
        return s.Nodes.Count - before;
    }

    /// <summary>两个节点是不是**同一条原线**的两头。接起来是条绕回自身的废线。</summary>
    public static bool SharesSourceLine(EpNode a, EpNode b)
    {
        if (a == null || b == null) return false;
        foreach (var ma in a.Members)
            foreach (var mb in b.Members)
                if (ma.Handle != 0 && ma.Handle == mb.Handle) return true;
        return false;
    }

    /// <summary>在现有连线之上再连 a–b 会不会**成环**（并查集）。</summary>
    public static bool WouldFormCycle(EpScene? s, string aId, string bId)
    {
        if (s == null || string.IsNullOrEmpty(aId) || string.IsNullOrEmpty(bId)) return false;
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            if (p == x) return x;
            string r = Find(p); parent[x] = r; return r;
        }
        void Union(string x, string y) { string rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
        foreach (var l in s.Links) Union(l.TemplateId, l.WallId);
        return Find(aId) == Find(bId);
    }

    /// <summary>
    /// 能不能连成一处衔接（原版窗口 <c>IsValidPair</c>）。**所有端点一律开放**，只有四条闸：
    /// 不是同一个节点；不是同一条线的两头；同角色（坡顶只接坡顶、坡底只接坡底，判不出的不拦）；不成环。
    /// </summary>
    public static bool IsValidPair(EpScene? s, EpNode? a, EpNode? b)
        => a != null && b != null && !ReferenceEquals(a, b) && a.Id != b.Id
        && !SharesSourceLine(a, b) && SameRole(a, b) && !WouldFormCycle(s, a.Id, b.Id);

    /// <summary>同角色？两边都判得出角色时才拦；有一边未知就放行（不猜）。</summary>
    public static bool SameRole(EpNode a, EpNode b)
    {
        var ra = RoleOf(a); var rb = RoleOf(b);
        if (ra == BenchRole.Unknown || rb == BenchRole.Unknown) return true;
        return ra == rb;
    }

    /// <summary>接不上时说清楚是被哪一条闸挡的 —— 只说"接不上"等于没说。</summary>
    public static string PairRefusal(EpScene? s, EpNode a, EpNode b)
    {
        if (a.Id == b.Id) return "同一个节点，没建连线。";
        if (SharesSourceLine(a, b)) return "接不上：这两个端点是【同一条台阶线】的两头 —— 接起来是条绕回自身的废线。";
        if (!SameRole(a, b))
            return $"接不上：角色不同 —— {RoleName(RoleOf(a))} ⇄ {RoleName(RoleOf(b))}。"
                 + "内部（坡底线）只接内部、外部（坡顶线）只接外部；坡顶接到坡底上，那条线在平面上会横穿整幅坡面。";
        if (WouldFormCycle(s, a.Id, b.Id))
            return "接不上：这两个端点已经通过现有连线连通了，再连就成环。同一标高只接成开链，远端不相连（要改就先删掉中间某一段连线）。";
        return "接不上。";
    }

    /// <summary>建一条连线（原版 <c>AddLink</c>）：1-1，一个端点最多挂一条，旧的自动让位。</summary>
    public static EpLink AddLink(EpScene s, EpNode a, EpNode b)
    {
        var t = a.Kind == EpNodeKind.Template ? a : b;
        var w = a.Kind == EpNodeKind.Template ? b : a;
        s.Links.RemoveAll(l => l.TemplateId == t.Id || l.WallId == t.Id || l.TemplateId == w.Id || l.WallId == w.Id);
        var link = new EpLink { TemplateId = t.Id, WallId = w.Id, Suggested = false };
        s.Links.Add(link);
        return link;
    }

    /// <summary>
    /// 按标高就近自动配对（Kylin 补的辅助，登记：原版最终档只有拖拽 + 存档回填）：
    /// 逐端点档下，每个模板端点找**同侧、|Δz| ≤ tol、平距最近、过四条闸**的端帮端点连一条建议线（虚线）。
    /// </summary>
    public static int AutoPairNearest(EpScene s, double zTol, double maxDist)
    {
        int added = 0;
        var tpls = new List<EpNode>();
        foreach (var n in s.Nodes) if (n.Kind == EpNodeKind.Template) tpls.Add(n);
        tpls.Sort((p, q) => p.Z.CompareTo(q.Z));
        foreach (var t in tpls)
        {
            if (s.Links.Exists(l => l.TemplateId == t.Id || l.WallId == t.Id)) continue;
            EpNode? best = null; double bd = maxDist;
            foreach (var w in s.Nodes)
            {
                if (w.Kind != EpNodeKind.EndWall || w.Side != t.Side || Math.Abs(w.Z - t.Z) > zTol) continue;
                if (s.Links.Exists(l => l.TemplateId == w.Id || l.WallId == w.Id)) continue;
                if (!IsValidPair(s, t, w)) continue;
                double d = Dist(t.X, t.Y, w.X, w.Y);
                if (d < bd) { bd = d; best = w; }
            }
            if (best == null) continue;
            s.Links.Add(new EpLink { TemplateId = t.Id, WallId = best.Id, Suggested = true });
            added++;
        }
        return added;
    }

    // ── 分组 ───────────────────────────────────────────────────────────────

    private static void GroupInto(EpScene s, List<(EpMember M, int Side, string Tag)> src, EpNodeKind kind, double zTol)
    {
        for (int side = 0; side <= 1; side++) EmitClusters(s, Bucket(src, side), kind, side, zTol);
    }

    private static void GroupWallByTemplateLevels(EpScene s, List<(EpMember M, int Side, string Tag)> src, double fallbackTol)
    {
        s.WallLevelTol = 0; s.WallUnmatched = 0;
        for (int side = 0; side <= 1; side++)
        {
            var bucket = Bucket(src, side);
            if (bucket.Count == 0) continue;
            var levels = new List<double>();
            foreach (var n in s.Nodes) if (n.Kind == EpNodeKind.Template && n.Side == side) levels.Add(n.Z);
            levels.Sort();
            double tol = LevelTol(levels, fallbackTol);
            s.WallLevelTol = Math.Max(s.WallLevelTol, tol);
            if (levels.Count == 0) { EmitClusters(s, bucket, EpNodeKind.EndWall, side, tol); continue; }

            var byLevel = new Dictionary<int, List<(EpMember M, string Tag)>>();
            var orphan = new List<(EpMember M, string Tag)>();
            foreach (var e in bucket)
            {
                int li = NearestLevel(levels, e.M.Z, tol);
                if (li < 0) { orphan.Add(e); continue; }
                if (!byLevel.TryGetValue(li, out var lst)) { lst = new List<(EpMember, string)>(); byLevel[li] = lst; }
                lst.Add(e);
            }
            var keys = new List<int>(byLevel.Keys); keys.Sort();
            foreach (var li in keys) EmitNode(s, byLevel[li], EpNodeKind.EndWall, side, li, levels[li]);
            s.WallUnmatched += EmitClusters(s, orphan, EpNodeKind.EndWall, side, tol);
        }
    }

    /// <summary>归级容差 = 半个模板级距（相邻级距的中位数）。级数 &lt; 2 就退回兜底值。</summary>
    public static double LevelTol(List<double> levels, double fallback)
    {
        if (levels.Count < 2) return fallback > 0 ? fallback : 0.5;
        var gaps = new List<double>(levels.Count - 1);
        for (int i = 1; i < levels.Count; i++) gaps.Add(levels[i] - levels[i - 1]);
        gaps.Sort();
        double step = gaps[gaps.Count / 2];
        return step > 1e-6 ? step * 0.5 : (fallback > 0 ? fallback : 0.5);
    }

    private static int NearestLevel(List<double> levels, double z, double tol)
    {
        int best = -1; double bd = double.MaxValue;
        for (int i = 0; i < levels.Count; i++) { double d = Math.Abs(levels[i] - z); if (d < bd) { bd = d; best = i; } }
        return bd <= tol ? best : -1;
    }

    private static List<(EpMember M, string Tag)> Bucket(List<(EpMember M, int Side, string Tag)> src, int side)
    {
        var bucket = new List<(EpMember M, string Tag)>();
        foreach (var e in src) if (e.Side == side) bucket.Add((e.M, e.Tag));
        return bucket;
    }

    private static int EmitClusters(EpScene s, List<(EpMember M, string Tag)> bucket, EpNodeKind kind, int side, double zTol)
    {
        if (bucket.Count == 0) return 0;
        bucket.Sort((a, b) => a.M.Z.CompareTo(b.M.Z));
        int i = 0, n = 0;
        while (i < bucket.Count)
        {
            double z0 = bucket[i].M.Z;
            int j = i;
            while (j < bucket.Count && bucket[j].M.Z - z0 <= zTol) j++;
            EmitNode(s, bucket.GetRange(i, j - i), kind, side, -1, 0);
            n++; i = j;
        }
        return n;
    }

    private static void EmitNode(EpScene s, List<(EpMember M, string Tag)> members, EpNodeKind kind, int side, int level, double levelZ)
    {
        if (members.Count == 0) return;
        var node = new EpNode
        {
            Kind = kind, Side = side,
            Id = $"{(kind == EpNodeKind.Template ? "T" : "W")}{side}#{s.Nodes.Count:00}",
            MatchedLevel = level, LevelZ = levelZ,
        };
        double zsum = 0;
        var tags = new List<string>();
        foreach (var (m, tg) in members)
        {
            node.Members.Add(m);
            zsum += m.Z;
            if (m.Z < node.ZMin) node.ZMin = m.Z;
            if (m.Z > node.ZMax) node.ZMax = m.Z;
            if (!string.IsNullOrEmpty(tg) && !tags.Contains(tg)) tags.Add(tg);
        }
        node.Z = zsum / node.Members.Count;
        EpMember rep = node.Members[0];
        foreach (var m in node.Members) if (side == 0 ? m.U < rep.U : m.U > rep.U) rep = m;
        node.X = rep.X; node.Y = rep.Y; node.U = rep.U; node.EdgeDist = rep.EdgeDist;
        node.Label = tags.Count == 0 ? "" : (tags.Count <= 2 ? string.Join("·", tags) : tags[0] + "…");
        s.Nodes.Add(node);
    }

    // ── 边界分割 ───────────────────────────────────────────────────────────

    private readonly struct Crossing
    {
        public readonly double X, Y, Z; public readonly bool IsEnd;
        public Crossing(double x, double y, double z, bool isEnd) { X = x; Y = y; Z = z; IsEnd = isEnd; }
    }

    private static void SplitByBoundary(
        EpPolyline pl, RingGeom? rg, double ax, double ay, double uMin, double uMax,
        out List<List<(double X, double Y, double Z)>> outside,
        out List<(Crossing C, List<(double X, double Y, double Z)>? Trail)> crossings)
    {
        var outs  = new List<List<(double X, double Y, double Z)>>();
        var cross = new List<(Crossing C, List<(double X, double Y, double Z)>? Trail)>();
        outside = outs; crossings = cross;
        int n = pl.Xyz.Length / 3;
        if (n < 2) return;

        bool Inside(double x, double y)
            => rg != null ? rg.Contains(x, y) : (x * ax + y * ay) >= uMin && (x * ax + y * ay) <= uMax;

        var run = new List<(double X, double Y, double Z)>();
        int headIdx = -1;
        int limit = pl.Closed ? n : n - 1;
        bool prevIn = Inside(pl.Xyz[0], pl.Xyz[1]);
        if (!prevIn) run.Add((pl.Xyz[0], pl.Xyz[1], pl.Xyz[2]));

        void Close(int tailIdx)
        {
            if (run.Count < 2) { run.Clear(); headIdx = -1; return; }
            var seg = new List<(double X, double Y, double Z)>(run);
            outs.Add(seg);
            if (headIdx >= 0) cross[headIdx] = (cross[headIdx].C, new List<(double, double, double)>(seg));
            if (tailIdx >= 0)
            {
                var back = new List<(double X, double Y, double Z)>(seg);
                back.Reverse();
                cross[tailIdx] = (cross[tailIdx].C, back);
            }
            run.Clear(); headIdx = -1;
        }

        for (int i = 0; i < limit; i++)
        {
            int a = i, b = (i + 1) % n;
            double x0 = pl.Xyz[a * 3], y0 = pl.Xyz[a * 3 + 1], z0 = pl.Xyz[a * 3 + 2];
            double x1 = pl.Xyz[b * 3], y1 = pl.Xyz[b * 3 + 1], z1 = pl.Xyz[b * 3 + 2];
            var hits = rg != null ? rg.Hits(x0, y0, x1, y1, ax, ay) : PlaneHits(x0, y0, x1, y1, ax, ay, uMin, uMax);
            hits.Sort((p, q) => p.T.CompareTo(q.T));
            double tPrev = 0;
            bool cur = prevIn;
            foreach (var h in hits)
            {
                if (h.T <= tPrev + 1e-12 || h.T >= 1 - 1e-12) continue;
                double hx = x0 + (x1 - x0) * h.T, hy = y0 + (y1 - y0) * h.T, hz = z0 + (z1 - z0) * h.T;
                cross.Add((new Crossing(hx, hy, hz, h.IsEnd), null));
                int idx = cross.Count - 1;
                if (cur) { run.Clear(); run.Add((hx, hy, hz)); headIdx = idx; }
                else { run.Add((hx, hy, hz)); Close(idx); }
                cur = !cur;
                tPrev = h.T;
            }
            bool endIn = Inside(x1, y1);
            if (!endIn) run.Add((x1, y1, z1));
            prevIn = endIn;
        }
        Close(-1);
    }

    private readonly struct Hit
    {
        public readonly double T; public readonly bool IsEnd;
        public Hit(double t, bool isEnd) { T = t; IsEnd = isEnd; }
    }

    /// <summary>采场范围环：内外判定 + 与线段求交 + 交点处边界的【局部走势】（±30m 弧长平滑）。</summary>
    private sealed class RingGeom
    {
        private const double SmoothRadius = 30.0;
        private readonly double[] _r; private readonly int _m; private readonly double[] _cum; private readonly double _total;

        public RingGeom(double[] ring)
        {
            _r = ring; _m = ring.Length / 2;
            _cum = new double[_m + 1];
            for (int i = 0; i < _m; i++)
            {
                int j = (i + 1) % _m;
                double dx = _r[j * 2] - _r[i * 2], dy = _r[j * 2 + 1] - _r[i * 2 + 1];
                _cum[i + 1] = _cum[i] + Math.Sqrt(dx * dx + dy * dy);
            }
            _total = _cum[_m];
        }

        public bool Contains(double x, double y) => PointInRing(_r, x, y);

        public List<Hit> Hits(double x0, double y0, double x1, double y1, double ax, double ay)
        {
            var hits = new List<Hit>();
            double dx = x1 - x0, dy = y1 - y0;
            for (int i = 0; i < _m; i++)
            {
                int j = (i + 1) % _m;
                double rx0 = _r[i * 2], ry0 = _r[i * 2 + 1], rx1 = _r[j * 2], ry1 = _r[j * 2 + 1];
                double ex = rx1 - rx0, ey = ry1 - ry0;
                double den = dx * ey - dy * ex;
                if (Math.Abs(den) < 1e-12) continue;
                double t = ((rx0 - x0) * ey - (ry0 - y0) * ex) / den;
                double u = ((rx0 - x0) * dy - (ry0 - y0) * dx) / den;
                if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                double s = _cum[i] + u * (_cum[i + 1] - _cum[i]);
                TrendAt(s, out double tx, out double ty);
                bool isEnd = Math.Abs(tx * ax + ty * ay) < EndEdgeCos;
                hits.Add(new Hit(t, isEnd));
            }
            return hits;
        }

        private void TrendAt(double s, out double tx, out double ty)
        {
            double rad = Math.Min(SmoothRadius, _total * 0.25);
            PointAt(s - rad, out double bx, out double by);
            PointAt(s + rad, out double fx, out double fy);
            double dx = fx - bx, dy = fy - by;
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) { tx = 1; ty = 0; return; }
            tx = dx / l; ty = dy / l;
        }

        private void PointAt(double s, out double x, out double y)
        {
            if (_total < 1e-9) { x = _r[0]; y = _r[1]; return; }
            s -= Math.Floor(s / _total) * _total;
            int lo = 0, hi = _m;
            while (lo + 1 < hi) { int mid = (lo + hi) / 2; if (_cum[mid] <= s) lo = mid; else hi = mid; }
            double seg = _cum[lo + 1] - _cum[lo];
            double f = seg < 1e-9 ? 0 : (s - _cum[lo]) / seg;
            int j = (lo + 1) % _m;
            x = _r[lo * 2] + (_r[j * 2] - _r[lo * 2]) * f;
            y = _r[lo * 2 + 1] + (_r[j * 2 + 1] - _r[lo * 2 + 1]) * f;
        }
    }

    private static List<Hit> PlaneHits(double x0, double y0, double x1, double y1, double ax, double ay, double uMin, double uMax)
    {
        var hits = new List<Hit>();
        double ua = x0 * ax + y0 * ay, ub = x1 * ax + y1 * ay;
        double d = ub - ua;
        if (Math.Abs(d) < 1e-12) return hits;
        foreach (double up in new[] { uMin, uMax })
        {
            double t = (up - ua) / d;
            if (t > 0 && t < 1) hits.Add(new Hit(t, true));
        }
        return hits;
    }

    // ── 图层判据 ─────────────────────────────────────────────────────────

    public const string NewPositionLayerPrefix = "新建工程位置";

    public static bool IsDumpBenchLine(string layer)
        => !string.IsNullOrEmpty(layer) && layer.StartsWith("排土场", StringComparison.Ordinal)
           && (layer.EndsWith("坡顶线", StringComparison.Ordinal) || layer.EndsWith("坡脚线", StringComparison.Ordinal) || layer.EndsWith("坡底线", StringComparison.Ordinal));

    private static bool IsTemplateSide(string layer, bool dumpMode) => IsBenchLine(layer) || (dumpMode && IsDumpBenchLine(layer));

    /// <summary>模板台阶线图层？（「生成采区台阶面」写的是 台阶_岩台阶 / 台阶_9煤 / 创建工程位置_台阶）</summary>
    public static bool IsBenchLine(string layer)
        => !string.IsNullOrEmpty(layer) && (layer.StartsWith("台阶_", StringComparison.Ordinal) || layer == "创建工程位置_台阶");

    /// <summary>与 <see cref="IsBenchLine"/> 同义的别名（Kylin 早期接口名，保留给调用方）。</summary>
    public static bool IsTemplateLayer(string? layer) => IsBenchLine(layer ?? "");

    /// <summary>**模板台阶本体**（比 <see cref="IsBenchLine"/> 严）：只认 台阶_*。</summary>
    public static bool IsTemplateBenchLine(string layer) => !string.IsNullOrEmpty(layer) && layer.StartsWith("台阶_", StringComparison.Ordinal);

    /// <summary>台阶线在一幅坡面里的角色：内部 = 坡底线（坡底/坡脚）、外部 = 坡顶线。Unknown = 层名里看不出来（不许猜）。</summary>
    public enum BenchRole { Unknown = 0, Toe = 1, Crest = 2 }

    public static BenchRole RoleOf(string? layer)
    {
        if (string.IsNullOrEmpty(layer)) return BenchRole.Unknown;
        if (layer.Contains("坡底线", StringComparison.Ordinal) || layer.Contains("坡脚线", StringComparison.Ordinal)) return BenchRole.Toe;
        if (layer.Contains("坡顶线", StringComparison.Ordinal)) return BenchRole.Crest;
        return BenchRole.Unknown;
    }

    public static BenchRole RoleOf(EpNode? n)
    {
        if (n == null) return BenchRole.Unknown;
        foreach (var m in n.Members) { var r = RoleOf(m.Layer); if (r != BenchRole.Unknown) return r; }
        return BenchRole.Unknown;
    }

    public static string RoleName(BenchRole r) => r switch { BenchRole.Toe => "内部·坡底线", BenchRole.Crest => "外部·坡顶线", _ => "角色未知" };

    /// <summary>搭接线（斜的，本身就是衔接段，不是台阶端）。</summary>
    public static bool IsLapLine(string? layer) => layer == "台阶_搭接线";

    private static string LayerTag(string layer)
    {
        if (string.IsNullOrEmpty(layer)) return "";
        return layer.StartsWith("台阶_", StringComparison.Ordinal) ? layer.Substring(3) : layer;
    }

    /// <summary>自某一端往线内走的点列（trail[0] = 该端点），最多 64 点：走势拟合（30m 回望）+ 交叉检测都够。</summary>
    private static List<(double X, double Y, double Z)> InwardTrail(double[] xyz, bool fromHead, int maxPts = 64)
    {
        int n = xyz.Length / 3;
        var l = new List<(double X, double Y, double Z)>(Math.Min(n, maxPts));
        for (int k = 0; k < n && k < maxPts; k++)
        {
            int i = fromHead ? k : n - 1 - k;
            l.Add((xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]));
        }
        return l;
    }

    private static List<(double X, double Y, double Z)> ToPts(double[] xyz)
    {
        var l = new List<(double, double, double)>(xyz.Length / 3);
        for (int i = 0; i + 2 < xyz.Length; i += 3) l.Add((xyz[i], xyz[i + 1], xyz[i + 2]));
        return l;
    }

    private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    private static bool TryStrikeAxis(IReadOnlyList<EpPolyline> lines, bool trustAll, out double ax, out double ay)
    {
        ax = 1; ay = 0;
        double rx = 0, ry = 0; bool haveRef = false;
        double sx = 0, sy = 0;
        foreach (var pl in lines)
        {
            if ((!trustAll && !IsBenchLine(pl.Layer)) || IsLapLine(pl.Layer) || pl.Xyz.Length < 6) continue;
            int n = pl.Xyz.Length / 3;
            double dx = pl.Xyz[(n - 1) * 3] - pl.Xyz[0], dy = pl.Xyz[(n - 1) * 3 + 1] - pl.Xyz[1];
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-6) continue;
            if (!haveRef) { rx = dx / l; ry = dy / l; haveRef = true; }
            double sgn = (dx * rx + dy * ry) >= 0 ? 1 : -1;
            sx += dx * sgn; sy += dy * sgn;
        }
        double sl = Math.Sqrt(sx * sx + sy * sy);
        if (!haveRef || sl < 1e-9) return false;
        ax = sx / sl; ay = sy / sl;
        return true;
    }

    private static bool TryRingAxis(double[]? ring, out double ax, out double ay)
    {
        ax = 1; ay = 0;
        if (ring == null || ring.Length < 6) return false;
        int m = ring.Length / 2;
        double cx = 0, cy = 0;
        for (int i = 0; i < m; i++) { cx += ring[i * 2]; cy += ring[i * 2 + 1]; }
        cx /= m; cy /= m;
        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < m; i++) { double dx = ring[i * 2] - cx, dy = ring[i * 2 + 1] - cy; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        ax = Math.Cos(theta); ay = Math.Sin(theta);
        return true;
    }

    private static double RingUMid(double[] ring, double ax, double ay)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i + 1 < ring.Length; i += 2) { double u = ring[i] * ax + ring[i + 1] * ay; if (u < lo) lo = u; if (u > hi) hi = u; }
        return 0.5 * (lo + hi);
    }

    private static double[] NormalizeRing(double[]? ringXy)
    {
        if (ringXy == null || ringXy.Length < 6) return Array.Empty<double>();
        int m = ringXy.Length / 2;
        if (m >= 4 && Math.Abs(ringXy[(m - 1) * 2] - ringXy[0]) < 1e-9 && Math.Abs(ringXy[(m - 1) * 2 + 1] - ringXy[1]) < 1e-9)
        {
            var cut = new double[(m - 1) * 2];
            Array.Copy(ringXy, cut, cut.Length);
            return cut;
        }
        return ringXy;
    }

    private static bool PointInRing(double[] ring, double x, double y)
    {
        bool inside = false;
        int m = ring.Length / 2;
        for (int i = 0, j = m - 1; i < m; j = i++)
        {
            double xi = ring[i * 2], yi = ring[i * 2 + 1], xj = ring[j * 2], yj = ring[j * 2 + 1];
            if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi + 1e-300) + xi)) inside = !inside;
        }
        return inside;
    }

    private static double DistToRing(double[] ring, double x, double y)
    {
        double best = double.MaxValue;
        int m = ring.Length / 2;
        for (int i = 0; i < m; i++)
        {
            int j = (i + 1) % m;
            double ax = ring[i * 2], ay = ring[i * 2 + 1], bx = ring[j * 2], by = ring[j * 2 + 1];
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : ((x - ax) * dx + (y - ay) * dy) / l2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double d = Dist(x, y, ax + t * dx, ay + t * dy);
            if (d < best) best = d;
        }
        return best == double.MaxValue ? 0 : best;
    }

    private static double Bearing(double ax, double ay)
    {
        double b = Math.Atan2(ax, ay) * 180.0 / Math.PI;
        return b < 0 ? b + 360 : b;
    }

    private static readonly string[] Compass16 = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

    /// <summary>方向 → 八向汉字端名（"东北端"）。工程师看帮先问"哪一端"，A/B 说不清。</summary>
    private static string Compass(double ax, double ay)
    {
        double b = Bearing(ax, ay);
        int k = (int)Math.Round(b / 45.0) % 8;
        return Compass16[k] + "端";
    }

    /// <summary>连线摘要（回显 / 存档）。</summary>
    public static string Describe(EpScene s, EpLink l)
    {
        var t = s.ById(l.TemplateId); var w = s.ById(l.WallId);
        if (t == null || w == null) return "(失效)";
        string side = t.Side == 0 ? s.SideAName : s.SideBName;
        if (t.Kind == EpNodeKind.Template && w.Kind == EpNodeKind.Template)
            return string.Format(CultureInfo.InvariantCulture,
                "模板互连 · {0} {1:0.##}m{2} ⇄ {3} {4:0.##}m ｜ Δz={5:0.##}m 平距={6:0.#}m",
                side, t.Z, string.IsNullOrEmpty(t.Label) ? "" : $"({t.Label})",
                w.Side == 0 ? s.SideAName : s.SideBName, w.Z, w.Z - t.Z, Dist(t.X, t.Y, w.X, w.Y));
        if (t.Kind == EpNodeKind.EndWall && w.Kind == EpNodeKind.EndWall)
            return string.Format(CultureInfo.InvariantCulture,
                "端帮互连 · {0} {1:0.##}m ⇄ {2} {3:0.##}m ｜ Δz={4:0.##}m 平距={5:0.#}m",
                side, t.Z, w.Side == 0 ? s.SideAName : s.SideBName, w.Z, w.Z - t.Z, Dist(t.X, t.Y, w.X, w.Y));
        return string.Format(CultureInfo.InvariantCulture,
            "{0} · 模板 {1:0.##}m{2} ⇄ 端帮 {3:0.##}m ｜ Δz={4:0.##}m 平距={5:0.#}m",
            side, t.Z, string.IsNullOrEmpty(t.Label) ? "" : $"({t.Label})", w.Z, w.Z - t.Z, Dist(t.X, t.Y, w.X, w.Y));
    }
}

/// <summary>配对关系的落盘 DTO（UserSettings，按采场范围名分档；跨会话记住"哪一级接哪一级"）。</summary>
public sealed class EpLinkStoreDto
{
    public string RegionName { get; set; } = "";
    public string SavedAt { get; set; } = "";
    public bool TplPairMode { get; set; }
    public List<EpLinkDto> Links { get; set; } = new();
}

/// <summary>一条配对：按【侧别 + 标高 + 平面位置】记，不记节点 Id——重新提取后 Id 会变，标高与位置不会。</summary>
public sealed class EpLinkDto
{
    public int    Side { get; set; }
    public double TemplateZ { get; set; }
    public double WallZ { get; set; }
    public string Note { get; set; } = "";
    public bool   WallIsTemplate { get; set; }
    public int    WallSide { get; set; } = -1;
    /// <summary>两头的平面位置（Kylin 补：逐端点档同侧同标高常有多个端点，只按标高回填会接错头）。</summary>
    public double TX { get; set; } = double.NaN;
    public double TY { get; set; } = double.NaN;
    public double WX { get; set; } = double.NaN;
    public double WY { get; set; } = double.NaN;
}
