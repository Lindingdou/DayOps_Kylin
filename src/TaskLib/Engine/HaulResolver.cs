// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/HaulResolver.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;                // mineable_region.points_json（扁平 xyz）
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Cad.Road;                 // RoadGraph / RoadNode / Point3d / RoadGraphSerializer
using PitMine3D.Kylin.Cad.Road;                 // DijkstraPathSolver / PathQuery / HaulMetrics / TruckProfile
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  真运距解析 —— 把「运距」从拍脑袋常数升级成路网可解的工程量。
//
//  运距 L 是全部下游指标的自变量：T_c = t_装 + 60·L_eq/v_重 + t_卸 + 60·L_eq/v_空 + t_调，
//  而最优配车数 n* = T_c/τ_L。运距错 30%，配车就错 30%——这是编组求解的输入命门。
//
//  三层兜底（能自动就自动，自动不了也绝不阻塞）：
//    ① 路网：road_network 存档 → RoadGraph → Dijkstra 求重车路径，拿【坡度等效运距 EquivM】。
//       等效运距才是对的口径：重车上坡与空车下坡阻力差异巨大，几何直距会系统性低估重车耗时。
//    ② 手填：面上已填 HaulDistanceKm > 0 —— 尊重人工，等效运距按实距取（无坡度信息可折算）。
//    ③ 兜底：去向自身的 FallbackHaulKm（按去向类型给的量级缺省）。
//
//  路网侧任何一步失败（无存档 / JSON 坏了 / 定位不到节点 / 不可达）都静默降级到 ②③，不抛。
//
//  ── 源端坐标的三层来源（本轮补的另一半：路网要求源汇两端都能定位到 RoadNode）──
//    ① 面上已录：FaceInput.SourceX/Y/Z（台账录入 → working_face_routing.source_x/y/z）。
//       人工录的权威最高，程序绝不覆盖。
//    ② 区域质心：按作业面名 / 工程位置号匹配 mineable_region，取该区域环的多边形质心
//       （面积退化时退回顶点均值）。这一层让绝大多数面不必手工录坐标。
//    ③ 都没有：坐标保持 (0,0)，只能靠名称匹配路网节点 —— 匹配不上就如实落兜底运距。
//    ★ 坐标全 0 绝不走 NearestNode 吸附：(0,0,0) 会被吸到离原点最近的节点上，
//      算出一个「看着很正常」的假运距。宁可标明「兜底」，也不要编一个假数。
//    源端的来源会写进 HaulLeg.Source（"路网(录入坐标)" / "路网(区域质心)" / "路网(名称匹配)"），
//    用户看得见这个运距究竟是怎么来的。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条「源 → 汇」运输线路的解析结果。</summary>
public sealed class HaulLeg
{
    /// <summary>几何运距 km（单程实距）。</summary>
    public double Km { get; set; }
    /// <summary>坡度等效运距 km（单程）。循环时间与配车数都按它算，不按 Km 算。</summary>
    public double EquivKm { get; set; }
    /// <summary>重车运行时间 min（单程）。</summary>
    public double LoadedMin { get; set; }
    /// <summary>空车返程时间 min（单程）。</summary>
    public double EmptyMin { get; set; }
    /// <summary>
    /// 数据来自哪一层："路网(…)" / "手填" / "兜底"。
    /// 路网层的括号里是**源端怎么定位到路网节点的**：
    /// "路网(录入坐标)"（台账录的源坐标）/ "路网(区域质心)"（按可采区域自动推导）/
    /// "路网(名称匹配)"（拿作业面名或工程位置号对上了节点 RefId）。
    /// 判层一律用 <c>Contains("路网")</c> / <c>Contains("手填")</c>，不要写 == 相等比较。
    /// </summary>
    public string Source { get; set; } = "";
    /// <summary>是否解出了可用运距（Km &gt; 0）。false 表示三层全空，下游不该再算循环时间。</summary>
    public bool Feasible { get; set; }

    /// <summary>往返总行车时间 min（不含装卸调车）。</summary>
    public double RoundTripMin => LoadedMin + EmptyMin;

    public string Caption => Feasible
        ? $"{Km:0.00}km（等效 {EquivKm:0.00}km · {Source}）"
        : "运距未解出";
}

public static class HaulResolver
{
    /// <summary>三层全空时的最终缺省运距 km（连去向都没有的面）。</summary>
    private const double LastResortKm = 2.5;

    /// <summary>
    /// 按坐标吸附路网节点的最大半径 m。超过这个距离说明这个点根本不在路网覆盖范围内，
    /// 宁可降级到手填/兜底，也不要吸到八竿子打不着的节点上算出一个假运距。
    /// </summary>
    private const double SnapRadiusM = 500;

    /// <summary>数据来源文案（供 UI 显示）。ApplyTo 后为各层命中面数的汇总。</summary>
    public static string LastSourceLabel { get; private set; } = "路网未装载";

    // ── 路网缓存（一次装载全程复用；Dijkstra 求解器自带单源缓存）──
    private static RoadGraph? _graph;
    private static DijkstraPathSolver? _solver;
    private static bool _tried;

    /// <summary>
    /// 路网本身的来源文案，与 LastSourceLabel 分开存。
    /// （否则 ApplyTo 把汇总写进 LastSourceLabel 后，再调一次 ApplyTo 会把上次的汇总
    ///   当成路网文案套进新汇总里，文案逐次嵌套变长。）
    /// </summary>
    private static string _graphLabel = "路网未装载";

    /// <summary>丢弃路网缓存，下次重新读 road_network（存了新路网后调用）。</summary>
    public static void Invalidate()
    {
        _graph = null;
        _solver = null;
        _tried = false;
        _graphLabel = "路网未装载";
        LastSourceLabel = "路网未装载";

        // 可采区域也一并丢：区域边界改了，质心跟着改（源坐标是从质心推的）。
        _regions = null;
        _regionsTried = false;
        _regionLabel = "";
        _regionMemo.Clear();
        _autoSourced.Clear();
        _srcByFace.Clear();
    }

    /// <summary>
    /// 装载最新一期路网存档。失败一律返回 null（调用方自行降级），并把原因写进 LastSourceLabel。
    /// </summary>
    private static DijkstraPathSolver? Solver()
    {
        if (_tried) return _solver;
        _tried = true;
        try
        {
            // All() 按 captured_at 倒序，第一条即最新一期路网。
            var archive = EquipmentDataContext.RoadNetworks.All().FirstOrDefault();
            if (archive == null)
            {
                _graphLabel = "无路网存档（在「开拓运输 · 保存路网」存一期后自动启用真运距）";
                return null;
            }

            var g = RoadGraphSerializer.FromJson(archive.GraphJson);   // 内部已容错，坏 JSON 返回空图
            if (g.NodeCount == 0 || g.EdgeCount == 0)
            {
                _graphLabel = $"路网存档「{archive.Name}」为空图（节点 {g.NodeCount}/边 {g.EdgeCount}）";
                return null;
            }

            _graph = g;
            _solver = new DijkstraPathSolver(g);
            _graphLabel = $"路网存档「{archive.Name}」（{archive.CapturedAt} · 节点 {g.NodeCount} · 边 {g.EdgeCount} · {RoadGraphSerializer.TotalKm(g):0.#}km）";
        }
        catch (Exception ex)
        {
            _graphLabel = $"路网不可用（{Short(ex)}）";
            _graph = null;
            _solver = null;
        }
        LastSourceLabel = _graphLabel;
        return _solver;
    }

    // ── 源端坐标：三层来源（面上已录 → 区域质心 → 无）─────────────────────────

    /// <summary>
    /// 面上那个坐标的来源。<b>面自己说了算</b>（<c>FaceInput.SourceOrigin</c>），
    /// 没说才按历史行为当成人工录入。
    /// <para>不认这一列的话，从工序作业区派生出来的面会被统统记成「录入坐标」——
    /// 而那些坐标一个都不是人录的。</para>
    /// </summary>
    private static string OriginOf(FaceInput face)
        => string.IsNullOrWhiteSpace(face.SourceOrigin) ? OriginEntered : face.SourceOrigin.Trim();

    /// <summary>源端坐标来源文案：面上人工录入。</summary>
    public const string OriginEntered = "录入坐标";
    /// <summary>源端坐标来源文案：按可采区域自动推导（多边形质心）。</summary>
    public const string OriginRegion = "区域质心";
    /// <summary>
    /// 源端坐标来源文案：<b>从工序作业区派生</b>（不是人录的，也不是这里按 mineable_region 推的）。
    /// <para>三者必须分开：报表上写「录入 N」而其实没人录过，是在给一个数字冒充精度。</para>
    /// </summary>
    public const string OriginDerived = "工序区质心";

    /// <summary>
    /// 平面环 → <b>面积质心</b>（Shoelace）。面积退化（共线/自交/重复点）时退回顶点均值并置
    /// <paramref name="degenerate"/>。
    ///
    /// <para><b>为什么不能用顶点均值</b>：顶点在某一侧加密时均值会被拖过去，
    /// 而作业面的装车点更接近区域的几何中心。栅格描边 + DP 抽稀出来的环尤其如此 ——
    /// 弯的那一侧留的点更多，均值会系统性地偏向弯的那一头。</para>
    ///
    /// <para><b>开成公用件</b>是因为「工序作业区派生作业面」也要求同一个点。
    /// 两处各写一份的话，同一块地在运距里是一个点、在派生的面上是另一个点，而两边各自都"看着对"。</para>
    /// </summary>
    public static (double X, double Y) AreaCentroid(IReadOnlyList<(double X, double Y)> ring, out bool degenerate)
    {
        degenerate = true;
        int n = ring?.Count ?? 0;
        if (n == 0) return (0, 0);
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += ring![i].X; my += ring[i].Y; }
        mx /= n; my /= n;
        if (n < 3) return (mx, my);

        double a2 = 0, cx = 0, cy = 0, minX = double.MaxValue, maxX = double.MinValue,
               minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double xi = ring![i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
            double cross = xi * yj - xj * yi;
            a2 += cross; cx += (xi + xj) * cross; cy += (yi + yj) * cross;
            if (xi < minX) minX = xi; if (xi > maxX) maxX = xi;
            if (yi < minY) minY = yi; if (yi > maxY) maxY = yi;
        }
        // |A| 用相对判据：矿区坐标量级 1e5~1e6，绝对面积阈值在这个量级上没有意义。
        double area = Math.Abs(a2) * 0.5, span = Math.Max(maxX - minX, maxY - minY);
        if (!(area > 1e-6) || !(area > span * span * 1e-9)) return (mx, my);

        double px = cx / (3 * a2), py = cy / (3 * a2);
        if (double.IsNaN(px) || double.IsInfinity(px) || double.IsNaN(py) || double.IsInfinity(py))
            return (mx, my);
        degenerate = false;
        return (px, py);
    }

    /// <summary>
    /// 一个作业面的源代表点（铲位/装载点）及其来源。
    /// <see cref="Origin"/> 为空串表示三层全空 —— 该面没有可用坐标，
    /// 路网只能靠名称匹配节点，匹配不上就落兜底运距。
    /// </summary>
    public readonly record struct FaceSource(double X, double Y, double Z, string Origin, string RegionName)
    {
        /// <summary>坐标是否可用。★ 只看 XY，Z=0 是合法标高——与 <c>FaceInput.HasSourcePosition</c> 同一判据。</summary>
        public bool HasPosition => Math.Abs(X) > 1e-9 || Math.Abs(Y) > 1e-9;
    }

    /// <summary>一块可采区域的代表点（质心 + 环上 Z 均值），从 mineable_region.points_json 解出来。</summary>
    private sealed class RegionShape
    {
        public string Name = "";
        public double Cx, Cy;
        /// <summary>环上顶点 Z 均值；NaN = 点串里没有可用 Z。</summary>
        public double Cz = double.NaN;
        /// <summary>面积退化（顶点共线/自交等）⇒ 质心退回顶点均值，如实标出来。</summary>
        public bool Degenerate;
    }

    private static List<RegionShape>? _regions;
    private static bool _regionsTried;
    private static string _regionLabel = "";

    /// <summary>作业面名/工程位置号 → 区域 的匹配记忆（区域是台账里的静态数据，匹配一次即可）。</summary>
    private static readonly Dictionary<string, RegionShape?> _regionMemo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次批量填充里，坐标是【系统自动推导】的面（键 = 面名/工程位置号 → 区域名）。</summary>
    private static readonly Dictionary<string, string> _autoSourced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本班盘子里各作业面已定的源坐标（键 = 面名/工程位置号）。
    /// <para>
    /// 为什么要单存一份：<c>ProductionTask</c> 上没有源坐标字段（契约冻结），甘特车次 /
    /// 三维模拟 / 派单那边是**从任务反构 FaceInput** 的，反构出来的面天然没有坐标。
    /// 没有这份记忆，同一个作业面会在台账上按【录入坐标】解运距、在甘特上按【区域质心】解，
    /// 两处的运距对不上——而运距一变，循环时间与车次数跟着变，用户看到的是两套数。
    /// </para>
    /// 每次 <see cref="FillSourcePositions"/> 整体重建，不留上一盘的陈货。
    /// </summary>
    private static readonly Dictionary<string, FaceSource> _srcByFace = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 可采区域台账（mineable_region）。读失败/无表一律返回空表并把原因记进 <see cref="_regionLabel"/>，
    /// 跨模块读取永不抛——源坐标推不出来只是少一层自动化，不该拖垮整条运距链。
    /// </summary>
    private static List<RegionShape> Regions()
    {
        if (_regionsTried) return _regions ?? new List<RegionShape>();
        _regionsTried = true;

        var list = new List<RegionShape>();
        try
        {
            foreach (var r in EquipmentDataContext.MineableRegion.All())
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;   // 无名区域没法与作业面对上，跳过
                var shape = FromRing(r.PointsJson);
                if (shape == null) continue;
                shape.Name = r.Name.Trim();
                list.Add(shape);
            }
            _regionLabel = list.Count > 0
                ? $"可采区域台账 {list.Count} 块"
                : "可采区域台账为空（圈画区域后源坐标可自动推导）";
        }
        catch (Exception ex)
        {
            _regionLabel = $"可采区域台账不可用（{Short(ex)}）";
            list.Clear();
        }

        _regions = list;
        return list;
    }

    /// <summary>
    /// 扁平 xyz 点串 → 区域代表点。
    /// 质心用多边形【面积质心】（比顶点均值稳：顶点在某一侧加密时，均值会被拖过去，
    /// 而作业面的装车点更接近区域的几何中心）；面积退化（|A|≈0：共线/只有两三个重复点）
    /// 时退回顶点均值，并标 <see cref="RegionShape.Degenerate"/>。
    /// </summary>
    private static RegionShape? FromRing(string? json)
    {
        double[]? flat;
        try { flat = JsonSerializer.Deserialize<double[]>(json ?? "[]"); }
        catch { return null; }
        if (flat == null || flat.Length < 9) return null;      // 至少 3 个点才成环

        int n = flat.Length / 3;
        var xs = new double[n];
        var ys = new double[n];
        double zs = 0; int zn = 0;
        for (int i = 0; i < n; i++)
        {
            double x = flat[3 * i], y = flat[3 * i + 1], z = flat[3 * i + 2];
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) return null;
            xs[i] = x; ys[i] = y;
            if (!double.IsNaN(z) && !double.IsInfinity(z)) { zs += z; zn++; }
        }

        // 面积质心走公用件 —— 这里曾经另写过一份，而「工序作业区派生作业面」也要求同一个点。
        // 两份实现会让同一块地在运距里是一个点、在派生的面上是另一个点，两边各自都"看着对"。
        var ring = new (double X, double Y)[n];
        for (int i = 0; i < n; i++) ring[i] = (xs[i], ys[i]);
        var (px, py) = AreaCentroid(ring, out bool degenerate);

        var shape = new RegionShape
        {
            Cz = zn > 0 ? zs / zn : double.NaN,
            Cx = px, Cy = py, Degenerate = degenerate,
        };
        if (double.IsNaN(shape.Cx) || double.IsInfinity(shape.Cx)
            || double.IsNaN(shape.Cy) || double.IsInfinity(shape.Cy)) return null;
        return shape;
    }

    /// <summary>
    /// 解析一个作业面的源代表点，三层来源（见文件头注释）。永不抛。
    /// <para>
    /// ★ 第②层是**自动推导**：面上没录坐标时按区域质心给一个。它不入库
    /// （<c>working_face_routing.source_x/y/z</c> 只存人工录的），这样区域边界一改，
    /// 推导值跟着改，而不会被一份陈旧的猜测钉死；界面上也才分得清"哪些是自己填的、
    /// 哪些是系统猜的"。
    /// </para>
    /// </summary>
    public static FaceSource SourceOf(FaceInput? face)
    {
        if (face == null) return default;

        // ① 面上已录（含台账带回来的 source_x/y/z）——人工录入权威最高
        if (face.HasSourcePosition)
            return new FaceSource(face.SourceX, face.SourceY, face.SourceZ, OriginOf(face), "");

        // ①b 本班盘子里同名作业面已定的源坐标 —— 甘特/车次/三维是从任务反构 FaceInput 的，
        //     反构的面没有坐标字段（ProductionTask 契约冻结）。不认这一份，同一个面
        //     在台账与甘特上会解出两个运距。
        string faceKey = KeyOf(face);
        if (faceKey.Length > 0 && _srcByFace.TryGetValue(faceKey, out var known) && known.HasPosition)
            return known;

        // ② 可采区域质心
        try
        {
            var region = MatchRegion(face);
            if (region != null)
            {
                // Z 取台阶标高优先：作业面就在某个台阶标高上，比"整块区域顶点 Z 均值"准得多。
                double z = Math.Abs(face.BenchElevationM) > 1e-6
                    ? face.BenchElevationM
                    : (double.IsNaN(region.Cz) ? 0 : region.Cz);

                // 环退化的如实说出来：那块区域的代表点不是面积质心而是顶点均值，
                // 界面上要让人看得见"这个点是怎么取的"。
                string name = region.Degenerate ? $"{region.Name}（环退化：代表点按顶点均值取）" : region.Name;
                return new FaceSource(region.Cx, region.Cy, z, OriginRegion, name);
            }
        }
        catch { /* 区域推导失败 → 落第③层，绝不拖垮运距解析 */ }

        // ③ 都没有：坐标留空（0,0），路网只能靠名称匹配 —— 匹配不上就如实落兜底
        return default;
    }

    /// <summary>
    /// 作业面 → 可采区域的匹配：先精确同名（面名 / 工程位置号），再按包含匹配，
    /// 取"匹配上的那串最长"的区域（最具体的一块）。
    /// <para>
    /// ★ 同分并列（两块区域都能对上、且质心不同）时**返回 null**：宁可这条腿落兜底，
    /// 也不要在两块区域之间掷骰子——猜错的坐标会算出一个看着正常的假运距。
    /// </para>
    /// </summary>
    private static RegionShape? MatchRegion(FaceInput face)
    {
        string key = KeyOf(face);
        if (key.Length == 0) return null;
        if (_regionMemo.TryGetValue(key, out var memo)) return memo;

        RegionShape? hit = null;
        var regions = Regions();
        if (regions.Count > 0)
        {
            var keys = new[] { face.Zone, face.EngineeringPositionId }
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .ToList();

            // ① 精确同名
            foreach (var k in keys)
            {
                hit = regions.FirstOrDefault(r => string.Equals(r.Name, k, StringComparison.OrdinalIgnoreCase));
                if (hit != null) break;
            }

            // ② 包含匹配（"主采面" ⊂ "主采面·东（剥离）"，或反向）。
            //    要求被包含的那串 ≥ 2 字：一个字（"东"）满天下都能命中，那不是匹配是碰运气。
            if (hit == null)
            {
                var scored = new List<(RegionShape R, int Len)>();
                foreach (var r in regions)
                {
                    if (r.Name.Length < 2) continue;
                    foreach (var k in keys)
                    {
                        if (k.Length < 2) continue;
                        bool ok = k.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0
                               || r.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!ok) continue;
                        scored.Add((r, Math.Min(k.Length, r.Name.Length)));
                        break;
                    }
                }

                if (scored.Count > 0)
                {
                    int best = scored.Max(s => s.Len);
                    var tops = scored.Where(s => s.Len == best).ToList();
                    // 并列但质心其实是同一个点（同名重复录了两块）不算歧义
                    bool ambiguous = tops.Count > 1
                        && tops.Any(t => Math.Abs(t.R.Cx - tops[0].R.Cx) > 1e-6 || Math.Abs(t.R.Cy - tops[0].R.Cy) > 1e-6);
                    hit = ambiguous ? null : tops[0].R;
                }
            }
        }

        _regionMemo[key] = hit;
        return hit;
    }

    /// <summary>面的记忆键：面名优先，没有就用工程位置号。</summary>
    private static string KeyOf(FaceInput face)
    {
        string z = (face.Zone ?? "").Trim();
        if (z.Length > 0) return z;
        return (face.EngineeringPositionId ?? "").Trim();
    }

    /// <summary>
    /// 该面的源坐标是不是**系统自动推导**（区域质心）来的，以及推自哪块区域。
    /// 供台账界面把「自己填的」与「系统猜的」分开显示——用户有权知道哪个数是猜的。
    /// 只反映最近一次 <see cref="FillSourcePositions"/> 的结果。
    /// </summary>
    public static bool IsAutoSourced(FaceInput? face, out string regionName)
    {
        regionName = "";
        if (face == null) return false;
        string key = KeyOf(face);
        if (key.Length == 0) return false;
        if (!_autoSourced.TryGetValue(key, out var name)) return false;
        regionName = name;
        return true;
    }

    /// <summary>
    /// 把某个面的源坐标标记为【人工录入】（用户在台账里改了坐标就调它）。
    /// 改过之后 <see cref="IsAutoSourced"/> 不再认它是"系统猜的"——界面不再标灰，
    /// 保存时也才会真的落库（自动推导值故意不入库，见 <see cref="SourceOf"/>）。
    /// </summary>
    public static void ClearAutoSource(FaceInput? face)
    {
        if (face == null) return;
        string key = KeyOf(face);
        if (key.Length == 0) return;

        _autoSourced.Remove(key);

        // 「本班已定」的那一份也跟着改：否则甘特/车次那边还在用改之前的旧坐标解运距，
        // 台账上改完了、甘特上没变，两处的数就此对不上。
        if (face.HasSourcePosition)
            _srcByFace[key] = new FaceSource(face.SourceX, face.SourceY, face.SourceZ, OriginOf(face), "");
        else
            _srcByFace.Remove(key);
    }

    /// <summary>
    /// 批量给作业面填源坐标（解运距**之前**跑）。
    /// 【不覆盖已有非零值】——手填的坐标是人工判断，程序不许擅自改写；只填空着的那些。
    /// 返回一句来源汇总（各层命中面数）。
    /// </summary>
    public static string FillSourcePositions(ExploderConfig? cfg)
    {
        _autoSourced.Clear();
        _srcByFace.Clear();
        if (cfg == null || cfg.Faces == null) return "";

        // ★ 三档分开数。此前 entered 把**一切带坐标的面**都算进去了 ——
        //   从工序作业区派生出来的面于是被记成「录入」，而那些坐标一个都不是人录的。
        int entered = 0, fromZone = 0, derived = 0, none = 0;
        foreach (var face in cfg.Faces)
        {
            if (face == null) continue;
            string key = KeyOf(face);

            if (face.HasSourcePosition)
            {
                // 已有坐标 ⇒ 一律不动；只把它记进"本班已定"，供从任务反构的面复用同一个坐标
                string origin = OriginOf(face);
                if (string.Equals(origin, OriginDerived, StringComparison.Ordinal)) fromZone++;
                else entered++;
                if (key.Length > 0)
                    _srcByFace[key] = new FaceSource(face.SourceX, face.SourceY, face.SourceZ, origin, "");
                continue;
            }

            var src = SourceOf(face);
            if (!src.HasPosition) { none++; continue; }

            face.SourceX = src.X;
            face.SourceY = src.Y;
            face.SourceZ = src.Z;

            if (key.Length > 0)
            {
                _autoSourced[key] = src.RegionName;
                _srcByFace[key] = src;
            }
            derived++;
        }

        return $"源坐标：录入 {entered} / 工序区质心 {fromZone} / 区域质心 {derived} / 无 {none}"
             + (derived > 0 || none > 0 ? $"（{(_regionLabel.Length > 0 ? _regionLabel : "可采区域台账未读")}）" : "");
    }

    // ── 主入口 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析一个作业面到其去向的运距。三层兜底，永不抛异常。
    /// </summary>
    public static HaulLeg Resolve(FaceInput face, SinkNode? sink)
    {
        // ① 路网：源先按 工程位置/作业区 名匹配节点，对不上再按【源坐标】吸附
        //    （坐标三层来源：面上已录 → 区域质心 → 无，见 SourceOf）；汇用 RefEntityId + 坐标。
        var src = SourceOf(face);
        var byNetwork = TryNetwork(
            srcKeys: new[] { face.EngineeringPositionId, face.Zone },
            srcX: src.X, srcY: src.Y, srcZ: src.Z,
            sink: sink, srcOrigin: src.Origin);
        if (byNetwork != null) return byNetwork;

        // ② 手填：尊重人工填的运距。无坡度信息可折算，等效运距按实距取。
        if (face.HaulDistanceKm > 1e-6)
            return Flat(face.HaulDistanceKm,
                        equivKm: face.EquivHaulKm > 1e-6 ? face.EquivHaulKm : face.HaulDistanceKm,
                        source: "手填");

        // ③ 兜底：去向自身的缺省运距。
        return Flat(sink?.FallbackHaulKm > 1e-6 ? sink!.FallbackHaulKm : LastResortKm, equivKm: 0, source: "兜底");
    }

    /// <summary>
    /// 按【物料分项】解析运距 —— 混采面的两条腿。
    ///
    /// <para>
    /// 一台电铲同一时窗挖「煤7∶岩3」，煤去破碎站 2.6km、岩去内排场 1.26km：这是**两条运距不同的线**，
    /// 面级的一个运距无论如何也代表不了它们。配车数 n* = T_c/τ_L 里的 T_c 直接由运距决定，
    /// 拿主去向的运距一刀切，次要物料那条腿就没进最优配车数。
    /// </para>
    /// <para>
    /// 三层兜底与 <see cref="Resolve(FaceInput,SinkNode?)"/> 同构，但第②层「手填」多一道闸：
    /// <b>只有候选汇正是该物料自己的去向时才采信手填值</b>。手填的那个数是给它自家去向填的，
    /// 拿去评估别的汇会让所有汇同价（流向分配就此变瞎）；在混采面上更会把煤的 2.6km
    /// 原样安到岩那条 1.26km 的腿上，两条线的差别就此抹平。
    /// </para>
    /// </summary>
    /// <param name="materialCode">物料码（<see cref="MaterialCatalog"/>）。</param>
    /// <param name="sink">该物料的候选/实际去向；null 表示未定汇，只走 ②③ 层。</param>
    public static HaulLeg Resolve(FaceInput face, string materialCode, SinkNode? sink)
    {
        // 该物料在本面上的去向分项（无分项时回落主去向的五字段，单一物料面口径完全不变）
        var d = face.DestinationFor(materialCode);

        // ① 路网：源先按名匹配节点、对不上再按源坐标吸附；汇用 RefEntityId + 坐标。
        var src = SourceOf(face);
        var byNetwork = TryNetwork(
            srcKeys: new[] { face.EngineeringPositionId, face.Zone },
            srcX: src.X, srcY: src.Y, srcZ: src.Z,
            sink: sink, srcOrigin: src.Origin);
        if (byNetwork != null) return byNetwork;

        // ② 手填：只在「这条腿的手填值真是给这个汇填的」时采信（见方法注释第二段）。
        if (IsOwnSink(d, sink) && d.HaulKm > 1e-6)
            return Flat(d.HaulKm,
                        equivKm: d.EquivHaulKm > 1e-6 ? d.EquivHaulKm : d.HaulKm,
                        source: "手填");

        // ③ 兜底：去向自身的缺省运距。
        return Flat(sink?.FallbackHaulKm > 1e-6 ? sink!.FallbackHaulKm : LastResortKm, equivKm: 0, source: "兜底");
    }

    /// <summary>
    /// 一次解出本面【全部物料分项】的运距：混采面 N 种物料 ⇒ N 条腿，各挂各自的汇与运距。
    /// 逐物料走 ResolvedMix 的份额（而不是走 Splits 列表）——
    /// 某物料没写回分项时也要有一条腿，否则加权循环时间会漏掉它。
    /// 返回的 <see cref="MaterialDestination"/> 是**副本**，调用方改它不会污染作业面台账。
    /// </summary>
    public static IReadOnlyList<(MaterialDestination Dest, HaulLeg Leg)> ResolveAll(FaceInput face, SinkRegistry? sinks)
    {
        var list = new List<(MaterialDestination, HaulLeg)>();
        if (face == null) return list;

        foreach (var sh in face.ResolvedMix.Normalized().Shares)
        {
            if (sh.Fraction <= 1e-6) continue;

            var d = face.DestinationFor(sh.MaterialCode).Clone();
            d.Fraction = sh.Fraction;                       // 实方份额以归一化后的 Mix 为准

            var sink = FindSink(sinks, d.DestinationId, d.DestinationName);
            var leg = Resolve(face, sh.MaterialCode, sink);

            // 解出来的运距顺手补进副本，调用方（编组求解/车次展开）直接拿它当这条腿的口径。
            if (d.HaulKm <= 1e-6) d.HaulKm = Math.Round(leg.Km, 3);
            if (d.EquivHaulKm <= 1e-6) d.EquivHaulKm = Math.Round(leg.EquivKm, 3);

            list.Add((d, leg));
        }
        return list;
    }

    /// <summary>
    /// 候选汇是不是这条分项**自己的**去向。sink 为 null 视为「没给候选汇，就按它自家的去向解」，
    /// 此时手填值本就是给这条腿填的，可以采信。
    /// </summary>
    private static bool IsOwnSink(MaterialDestination d, SinkNode? sink)
    {
        if (sink == null) return true;
        if (!string.IsNullOrWhiteSpace(d.DestinationId)
            && string.Equals(d.DestinationId, sink.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(d.DestinationName)
            && string.Equals(d.DestinationName, sink.Name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 按坐标解析运距（源是三维点而非作业面：三维模拟 / 临时取点用）。
    /// 无「手填」层可用，故只有 ①路网 → ③兜底 两层。
    /// </summary>
    public static HaulLeg Resolve(double srcX, double srcY, double srcZ, SinkNode? sink, string? srcRefId = null)
    {
        var byNetwork = TryNetwork(
            srcKeys: srcRefId == null ? Array.Empty<string>() : new[] { srcRefId },
            srcX: srcX, srcY: srcY, srcZ: srcZ,
            sink: sink, srcOrigin: "取点坐标");
        if (byNetwork != null) return byNetwork;

        return Flat(sink?.FallbackHaulKm > 1e-6 ? sink!.FallbackHaulKm : LastResortKm, equivKm: 0, source: "兜底");
    }

    // ── ① 路网层 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 路网求解；任何一环缺失都返回 null 表示「这一层解不出，请降级」。
    /// <paramref name="srcOrigin"/> = 源坐标的来源文案（"录入坐标"/"区域质心"/"取点坐标"），
    /// 只在源端确实是**按坐标吸附**到节点时才写进 <see cref="HaulLeg.Source"/>；
    /// 按名字对上节点的写"名称匹配"——这个数是怎么来的，用户有权看见。
    /// </summary>
    private static HaulLeg? TryNetwork(
        IReadOnlyList<string> srcKeys, double srcX, double srcY, double srcZ, SinkNode? sink, string srcOrigin)
    {
        try
        {
            var solver = Solver();
            var g = _graph;
            if (solver == null || g == null || sink == null) return null;

            var (srcNode, srcByName) = FindNode(g, srcKeys, srcX, srcY, srcZ);
            var (dstNode, _) = FindNode(g, new[] { sink.RefEntityId, sink.Id }, sink.X, sink.Y, sink.Z);
            if (srcNode == null || dstNode == null || srcNode.Id == dstNode.Id) return null;

            // 重车去程：Mode=Distance 走最短里程，等效运距/时间是寻径副产物（PathResult 一次给全）。
            var loaded = solver.FindPath(srcNode.Id, dstNode.Id, new PathQuery { Loaded = true, Mode = WeightMode.Distance });
            if (!loaded.Feasible || loaded.LengthM <= 0) return null;

            // 空车返程：单独求一次——空车限载/坡阻口径与重车不同，回程未必是去程的镜像。
            var empty = solver.FindPath(dstNode.Id, srcNode.Id, new PathQuery { Loaded = false, Mode = WeightMode.Distance });

            return new HaulLeg
            {
                Km = loaded.LengthM / 1000.0,
                EquivKm = loaded.EquivM / 1000.0,
                LoadedMin = loaded.TimeMin,
                // 回程不可达（单行道成环等）时按去程时间的经验比例估：空车快于重车。
                EmptyMin = empty.Feasible && empty.TimeMin > 0 ? empty.TimeMin : loaded.TimeMin * 0.65,
                Source = NetworkLabel(srcByName, srcOrigin),
                Feasible = true,
            };
        }
        catch
        {
            return null;   // 路网层永不把异常抛给调用方
        }
    }

    /// <summary>路网层的来源文案：括号里说清源端是怎么定位到节点的。</summary>
    private static string NetworkLabel(bool byName, string srcOrigin)
    {
        if (byName) return "路网(名称匹配)";
        return string.IsNullOrWhiteSpace(srcOrigin) ? "路网" : $"路网({srcOrigin})";
    }

    /// <summary>
    /// 路网节点定位：先按业务标识（RefId / 节点 Id）精确匹配，匹配不上再按坐标吸附。
    /// 返回值里的 ByName 表示「是按名字对上的」（而不是按坐标吸的）——上层据此如实标注来源。
    ///
    /// ★ 坐标全 0 视为「无坐标」，绝不吸附：作业面（V037 之前）与排土场（V036 之前）都可能
    ///   一个坐标都没有，把 (0,0,0) 当真实位置去吸，会吸到离原点最近的那个节点上，
    ///   算出一个看着很正常的假运距 —— 宁可返回 null 让上层如实落到兜底层。
    ///   判据只看 XY（Z=0 是合法标高），与 FaceInput.HasSourcePosition 严格一致。
    /// </summary>
    private static (RoadNode? Node, bool ByName) FindNode(RoadGraph g, IReadOnlyList<string> keys, double x, double y, double z)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            foreach (var n in g.Nodes)
            {
                if (string.Equals(n.RefId, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Id, key, StringComparison.OrdinalIgnoreCase))
                    return (n, true);
            }
        }

        if (HasPosition(x, y))
            return (g.NearestNode(new Point3d(x, y, z), SnapRadiusM), false);

        return (null, false);
    }

    /// <summary>坐标是否有效（X/Y 任一非零即认为录了位置；Z 可以合法为 0）。</summary>
    private static bool HasPosition(double x, double y)
        => Math.Abs(x) > 1e-6 || Math.Abs(y) > 1e-6;

    // ── ②③ 平路层：无坡度信息，按平路车速估行车时间 ─────────────────────────

    /// <summary>
    /// 由一个纯里程构造 HaulLeg：坡度未知按平路（grade=0）算，
    /// 车速用 RoadLib 的 TruckProfile 缺省值，保证与路网层同一套速度口径。
    /// </summary>
    private static HaulLeg Flat(double km, double equivKm, string source)
    {
        double m = Math.Max(0, km) * 1000.0;
        var t = TruckProfile.Default;
        return new HaulLeg
        {
            Km = km,
            EquivKm = equivKm > 1e-6 ? equivKm : km,   // 无坡度可折算 ⇒ 等效运距即实距
            LoadedMin = HaulMetrics.TravelTimeMin(m, gradePct: 0, t, loaded: true),
            EmptyMin = HaulMetrics.TravelTimeMin(m, gradePct: 0, t, loaded: false),
            Source = source,
            Feasible = km > 1e-6,
        };
    }

    // ── 批量接线 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 给 cfg 里所有作业面批量填运距。
    /// 【不覆盖已有非零值】——手填的运距是人工判断，程序不许擅自改写；只填空着的那些。
    ///
    /// <para>
    /// 混采面（有 <c>Splits</c> 的面）走**分项**口径：逐物料解各自那条腿的运距并回填到各分项上，
    /// 面上的主去向五字段继续记【主分项】的值（份额最大 / 与主去向同汇的那一条），保持单据兼容。
    /// 不这么做的话，煤的 2.6km 与岩的 1.26km 会被压成面上唯一的一个数，两条线的差别在配车环节就没了。
    /// </para>
    /// 返回一句汇总文案（各层命中面数），同时写进 LastSourceLabel。
    /// </summary>

    /// <summary>
    /// 上一次装配用的是不是真数据（false = 走了兜底）。
    /// <para><b>链路体检读它，不读来源文案</b> —— 文案一改就静默抠空，
    /// 而抠空之后「兜底」会被当成「真实」，体检朝着让人放心的方向失效。</para>
    /// </summary>
    public static bool LastFromLedger { get; private set; }

    public static string ApplyTo(ExploderConfig cfg)
    {
        int net = 0, manual = 0, fall = 0, noSink = 0, splitFaces = 0, splitLegs = 0;

        // ★ 先把源坐标填上再解运距：路网寻径要求源汇两端都能定位到节点，
        //   源端没坐标只能靠名字碰运气，碰不上整条链就落兜底值。顺序不能颠倒。
        string srcLabel = "";
        try { srcLabel = FillSourcePositions(cfg); } catch { }

        foreach (var face in cfg.Faces)
        {
            // ── 分项面：逐物料一条腿 ──
            if (face.HasSplits)
            {
                splitFaces++;
                foreach (var d in face.Splits)
                {
                    var s = FindSink(cfg.Sinks, d.DestinationId, d.DestinationName);
                    var lg = Resolve(face, d.MaterialCode, s);

                    if (d.HaulKm <= 1e-6) d.HaulKm = Math.Round(lg.Km, 3);
                    if (d.EquivHaulKm <= 1e-6) d.EquivHaulKm = Math.Round(lg.EquivKm, 3);

                    splitLegs++;
                    Tally(lg.Source, ref net, ref manual, ref fall);
                }

                // 主去向五字段 ← 主分项（单据/历史口径不变；只补空，不覆盖人工值）
                var lead = PrimarySplit(face);
                if (lead != null)
                {
                    if (face.HaulDistanceKm <= 1e-6) face.HaulDistanceKm = lead.HaulKm;
                    if (face.EquivHaulKm <= 1e-6) face.EquivHaulKm = lead.EquivHaulKm;
                }
                if (!face.HasDestination) noSink++;
                continue;
            }

            // ── 单去向面：口径与改造前完全一致 ──
            var sink = FindSink(cfg.Sinks, face.DestinationId, face.DestinationName);
            if (sink == null) noSink++;

            var leg = Resolve(face, sink);

            if (face.HaulDistanceKm <= 1e-6) face.HaulDistanceKm = Math.Round(leg.Km, 3);
            if (face.EquivHaulKm <= 1e-6) face.EquivHaulKm = Math.Round(leg.EquivKm, 3);

            Tally(leg.Source, ref net, ref manual, ref fall);
        }

        string netInfo = Solver() != null ? _graphLabel : $"路网未用（{_graphLabel}）";
        string label = $"运距：路网 {net} / 手填 {manual} / 兜底 {fall} 条腿"
                     + (splitFaces > 0 ? $"（含 {splitFaces} 个混采面共 {splitLegs} 条分项腿）" : "")
                     + (noSink > 0 ? $"（{noSink} 面去向未定）" : "")
                     + (srcLabel.Length > 0 ? $" · {srcLabel}" : "")
                     + $" · {netInfo}";
        // 一条腿都没走兜底才算真数据 —— 兜底运距会一路把循环时间、配车数、编组班产带偏，
        // 而每一步都不报错。
        LastFromLedger = fall == 0 && noSink == 0;
        LastSourceLabel = label;
        return label;
    }

    /// <summary>
    /// 分层计数。★ 用前缀判而不是相等判：路网层的取值现在带括号后缀
    /// （"路网(区域质心)" / "路网(名称匹配)" …），写 == "路网" 会把整条路网腿算成兜底腿，
    /// 汇总文案就成了"全是兜底"的假警报。
    /// </summary>
    private static void Tally(string source, ref int net, ref int manual, ref int fall)
    {
        string s = source ?? "";
        if (s.StartsWith("路网", StringComparison.Ordinal)) net++;
        else if (s.StartsWith("手填", StringComparison.Ordinal)) manual++;
        else fall++;
    }

    /// <summary>
    /// 主分项 = 与面上主去向同汇的那一条；对不上就取份额最大者。
    /// 主去向五字段要跟它走，单据上「这个面拉多远」才和主去向自洽。
    /// </summary>
    private static MaterialDestination? PrimarySplit(FaceInput face)
    {
        if (face.Splits.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(face.DestinationId))
        {
            var hit = face.Splits.FirstOrDefault(
                s => string.Equals(s.DestinationId, face.DestinationId, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return face.Splits.OrderByDescending(s => s.Fraction).First();
    }

    /// <summary>按 Id 找去向，找不到再按名字兜一次。登记簿为空/未接通时返回 null。</summary>
    private static SinkNode? FindSink(SinkRegistry? sinks, string? id, string? name)
    {
        if (sinks == null) return null;
        var s = sinks.Find(id);
        if (s != null) return s;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return sinks.All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
