// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/RoadHaulProvider.cs（逐行对应；仅命名空间适配；原 WorkLineSamples 在 Kylin 叫 Cad.WorkLineSamples）
using System;
using System.Collections.Generic;
using System.Linq;
// Cad.Road.* 全限定：Cad 里另有同名旧切片 RoadGraph/DijkstraPathSolver（RoadPathSolver.cs）
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.Data;
namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 用<b>真路网</b>算逐月 O-D 运距，喂给 <see cref="CoupledPlanInput.RoadHaul"/>。
///
/// <para><b>为什么长在 PlanLib 而不是 MineAssLib</b>：`MineAssLib` 不引用 `RoadLib`，也不该引用 ——
/// 采剥接续是几何与量的事，路网是运输的事。内核只留一个回调口子，谁有路网谁注入。
/// 这样内核照旧零依赖、脱 GUI 可测，而这一层的降级逻辑也能单独验。</para>
///
/// <para><b>三段映射</b>：
/// <list type="number">
///   <item><b>源</b>：本月工作面在推进轴上的位置 <c>u</c> → 平面点。
///     <c>WorkLineProjector</c> 只有正向（(x,y)→u）没有逆向，所以这里按
///     <b>基线质心 + u × 推进单位向量</b> 反推 —— 与 <c>DumpSlotAdapter.LateralOffsetsKm</c>
///     用的是同一套近似，<b>标出来，不冒充精确</b>。</item>
///   <item><b>汇</b>：排土位置质心 <c>(Cx,Cy,Cz)</c>。</item>
///   <item>两端各<b>就近吸附</b>到路网节点（超出 <see cref="SnapRadiusM"/> 判为落不上），
///     Dijkstra 取<b>坡度等效运距 <c>EquivM</c></b>（不是平距 —— 内排下坡、外排爬升，
///     只算平距会让两者在该分开的地方分不开）。</item>
/// </list></para>
///
/// <para><b>解不出来一律返回 null</b>（落不上节点 / 不可达 / 图是空的），由内核退回几何兜底并<b>计入命中率</b>。
/// 绝不拿一个编出来的数冒充路网运距。</para>
/// </summary>
public sealed class RoadHaulProvider
{
    /// <summary>
    /// 两端吸附到路网节点的最大距离（m）。超了就判"落不上"，退回兜底。
    /// <para><b>缺省值是从路网自己推出来的</b>，不是拍的 —— 见 <see cref="DerivedSnapRadiusM"/>。
    /// 显式赋值即覆盖。</para>
    /// </summary>
    public double SnapRadiusM { get; set; }

    /// <summary>
    /// 由路网<b>节点间距</b>推出的吸附半径：<c>2 × 中位最近邻间距</c>，夹在 [50, 1000] m。
    ///
    /// <para><b>为什么不能给个固定数</b>：300m 在节点间距 ~50m 的密路网里过松（会把不该连的点连上，
    /// 运距偏乐观），在间距 ~800m 的粗路网里又过紧（几乎全落不上 = 等于没接路网，
    /// 而且只在 <c>HaulNote</c> 的命中率上看得出来）。
    /// 判据「一个点算不算在路网上」的自然尺度就是<b>节点之间有多远</b>。</para>
    ///
    /// <para>取<b>中位数</b>不取均值：矿区路网常有几个孤立的远端节点，均值会被它们拉走。</para>
    /// </summary>
    public double DerivedSnapRadiusM { get; }

    /// <summary>吸附半径是怎么来的（要显示出来 —— 它直接决定命中率）。</summary>
    public string SnapRadiusNote { get; }

    /// <summary>寻径口径（重车、按里程；限坡由车型/路网自己带）。</summary>
    public Cad.Road.PathQuery Query { get; set; } = new() { Loaded = true, Mode = Cad.Road.WeightMode.Distance };

    private readonly Cad.Road.RoadGraph _graph;
    private readonly Cad.Road.DijkstraPathSolver _solver;
    private readonly List<Cad.Road.RoadNode> _nodes;
    /// <summary>工作线基线质心 + 推进单位向量（源点反推用）。</summary>
    private readonly double _ox, _oy, _dx, _dy;
    private readonly bool _hasAxis;

    /// <summary>路网来源说明（存档名 / 规模）。<b>要显示给用户</b> —— 让人知道这个运距是哪张网算的。</summary>
    public string SourceLabel { get; }

    /// <summary>(源节点, 汇节点) → 等效公里 的缓存。逐月只有源在变，汇是固定的一小撮。</summary>
    private readonly Dictionary<(string, string), double?> _cache = new();
    /// <summary>平面点 → 最近节点 的缓存（吸附是 O(N) 扫描，逐月逐位置会调很多次）。</summary>
    private readonly Dictionary<(long, long), Cad.Road.RoadNode?> _snapCache = new();

    private RoadHaulProvider(Cad.Road.RoadGraph g, string label, IReadOnlyList<WorkLineSamples>? lines)
    {
        _graph = g;
        _solver = new Cad.Road.DijkstraPathSolver(g);
        _nodes = g.Nodes.ToList();
        SourceLabel = label;

        // 吸附半径按节点间距推 —— 固定数在密/粗两种路网上都会错（见 DerivedSnapRadiusM）
        (DerivedSnapRadiusM, SnapRadiusNote) = DeriveSnapRadius(_nodes);
        SnapRadiusM = DerivedSnapRadiusM;

        // 迂回系数也先采一遍 —— 兜底系数在排产**之前**就要定，等不到事后的 MeasuredTortuosity
        (SampledTortuosity, SampledTortuosityNote) = DeriveTortuosity();

        // 推进轴：基线质心 + 平均推进方向。Samples 的 (dx,dy) 就是逐点推进方向。
        var pts = new List<(double X, double Y)>();
        var dirs = new List<(double X, double Y)>();
        foreach (var wl in lines ?? Array.Empty<WorkLineSamples>())
        {
            foreach (var b in wl.Baseline) pts.Add((b.X, b.Y));
            foreach (var s in wl.Samples) dirs.Add((s.Dx, s.Dy));   // Samples = (Ax,Ay,Az,Dx,Dy)，Dx/Dy 是推进方向
        }
        if (pts.Count > 0)
        {
            _ox = pts.Average(p => p.X); _oy = pts.Average(p => p.Y);
            if (dirs.Count > 0)
            {
                double sx = dirs.Average(d => d.X), sy = dirs.Average(d => d.Y);
                double n = Math.Sqrt(sx * sx + sy * sy);
                if (n > 1e-9) { _dx = sx / n; _dy = sy / n; _hasAxis = true; }
            }
        }
    }

    /// <summary>
    /// 直接用一张现成的路网图建（调用方已有图、或台架要造合成路网时用）。
    /// <para>生产路径走 <see cref="TryLoadLatest"/> 从存档装；这个重载让这条桥<b>脱数据库可验收</b> ——
    /// 否则吸附半径、实测迂回系数这些只能在有存档的机器上才验得了。</para>
    /// </summary>
    public static RoadHaulProvider ForGraph(Cad.Road.RoadGraph graph, IReadOnlyList<WorkLineSamples>? lines = null,
                                            string label = "（调用方给的路网图）")
        => new(graph ?? throw new ArgumentNullException(nameof(graph)), label, lines);

    /// <summary>
    /// 装最新一期路网存档。<b>装不上一律返回 null</b>，原因写进 <paramref name="why"/>
    /// —— 调用方照旧跑几何兜底，但要把原因显示出来。
    /// </summary>
    public static RoadHaulProvider? TryLoadLatest(IReadOnlyList<WorkLineSamples>? lines, out string why)
    {
        why = "";
        try
        {
            // All() 按 captured_at 倒序，第一条即最新一期（与 TaskLib.HaulResolver 同一套口径）
            var archive = EquipmentDataContext.RoadNetworks.All().FirstOrDefault();
            if (archive == null)
            { why = "没有路网存档 —— 在「开拓运输 · 保存路网」存一期后自动启用真运距"; return null; }

            var g = Cad.Road.RoadGraphSerializer.FromJson(archive.GraphJson);   // 内部容错，坏 JSON 返回空图
            if (g.NodeCount == 0 || g.EdgeCount == 0)
            { why = $"路网存档「{archive.Name}」是空图（节点 {g.NodeCount}/边 {g.EdgeCount}）"; return null; }

            string label = $"路网存档「{archive.Name}」（{archive.CapturedAt} · 节点 {g.NodeCount}"
                         + $" · 边 {g.EdgeCount} · {Cad.Road.RoadGraphSerializer.TotalKm(g):0.#}km）";
            var p = new RoadHaulProvider(g, label, lines);
            if (!p._hasAxis)
                why = "路网可用，但**没有工作线方向** —— 源点只能落在基线质心，逐月运距不会随推进变";
            return p;
        }
        catch (Exception ex) { why = "路网不可用（" + ex.GetType().Name + "：" + ex.Message + "）"; return null; }
    }

    /// <summary>
    /// 装进 <see cref="CoupledPlanInput.RoadHaul"/> 的那个回调。
    /// 签名 = (月, 排土位置, 本月源在推进轴上的 u, 本月源加权平均标高 z) → 等效公里 / null。
    /// </summary>
    public Func<int, DumpSlot, double, double, double?> AsCallback()
        => (_, slot, srcU, srcZ) => Resolve(srcU, srcZ, slot);

    /// <summary>
    /// 装进 <see cref="UnitPlanInput.Haul"/> 的那个回调（<b>单元链</b>）。
    /// 签名 = (源 xyz, 汇 xyz) → 等效公里 / null，两端都是真坐标。
    ///
    /// <para><b>此前单元链根本没接路网</b>：`UnitPlanInput.Haul` 引擎在读、界面从没填过 ⇒
    /// 每一笔运距都是"直线 × 迂回系数"。后果不只是运距不准 ——
    /// 「运输功最小」那条配对策略、比选表的运输功那一维、U4 的车队闸，
    /// <b>全都建立在一个静态数上</b>，而每一项校核仍然是 ✓。</para>
    /// </summary>
    public HaulQuery AsUnitQuery()
        => (sx, sy, sz, dx, dy, dz) => ResolveXyz(sx, sy, sz, dx, dy, dz);

    /// <summary>
    /// <b>实测迂回系数</b>：已解出的那些 O-D 上 <c>路网等效运距 ÷ 直线平距</c> 的中位数。
    /// <c>null</c> = 还没有足够样本（&lt; 3 对）。
    ///
    /// <para><b>为什么这个数该测不该拍</b>：迂回系数原本是"没有路网时的兜底口径"，
    /// 而真路网接上以后它<b>并没有作废</b> —— 落不到节点/不可达的那些 O-D 照旧走兜底。
    /// 既然同一张网上已经有一批 O-D 量出了真实的绕行程度，
    /// <b>兜底那部分就该用这张网自己的迂回系数，而不是一个通用经验值</b>。</para>
    ///
    /// <para>取中位数不取均值：矿区路网常有个别极端绕行的 O-D（绕整个坑），均值会被拉走。</para>
    /// </summary>
    public double? MeasuredTortuosity
    {
        get
        {
            if (_ratios.Count < 3) return null;
            var v = _ratios.ToList(); v.Sort();
            return v[v.Count / 2];
        }
    }

    /// <summary>
    /// 把 <see cref="TruckProfile"/> 的坡阻系数换算成 <c>HaulModel</c> 那套「每米高差折多少米平距」的口径，
    /// 并<b>指出两边对不对得上</b>。
    ///
    /// <para><b>换算</b>：RoadLib 的 <c>equiv = length × (1 + k·g)</c>，其中 <c>g = gradePct/100</c>。
    /// 爬升 <c>lift</c>、水平 <c>h</c> 时 <c>g = lift/h</c>，于是
    /// <c>equiv = h(1 + k·lift/h) = h + k·lift</c> —— <b>k 本身就是"每米高差折多少米平距"</b>。
    /// 下坡那支是 <c>factor = max(0.5, 1 − k↓·|g|)</c>，同理得 <c>equiv = h − k↓·drop</c>：
    /// <b>下坡在 RoadLib 里是【减免】不是【代价】。</b></para>
    ///
    /// <para><b>这就是要报出来的那件事</b>：`HaulModel` 现在用上坡 12 / 下坡 <b>+3（代价）</b>，
    /// 而 RoadLib 的车型缺省值是上坡 6 / 下坡 <b>−2（减免）</b> ——
    /// 上坡差一倍，<b>下坡连正负号都相反</b>。两者都在算同一件事，
    /// 而内排通常下坡、外排常要爬升 ⇒ <b>这个分歧直接摆动内排率</b>。</para>
    /// </summary>
    public (double UphillPerM, double DownhillPerM, string Note) LiftEquivalentFromTruck()
    {
        var t = Query.Truck ?? Cad.Road.TruckProfile.Default;
        double up = t.UphillEquivK;          // 重车上坡：每米高差折 up 米平距
        double down = -t.DownhillEquivK;     // 下坡是减免 ⇒ 负号
        string note = $"RoadLib 车型缺省值折算：上坡 {up:0.#} m平距/m高差 · "
                    + $"下坡 {down:0.#}（**负 = 减免**，下限 0.5×实距）";
        return (up, down, note);
    }

    /// <summary>实测迂回系数的说明（样本数、区间）—— 要显示出来，别让人以为是拍的。</summary>
    public string TortuosityNote
        => _ratios.Count < 3
         ? $"路网上只解出 {_ratios.Count} 对 O-D，样本不够（需 ≥3），迂回系数仍用输入值"
         : $"实测迂回系数 {MeasuredTortuosity:0.00}（{_ratios.Count} 对 O-D 的中位数，"
         + $"区间 {_ratios.Min():0.00}~{_ratios.Max():0.00}）";

    private readonly List<double> _ratios = new();

    /// <summary>
    /// <b>开跑前</b>就从路网本身采出来的迂回系数（网上距离 ÷ 直线距离的中位数）；采不到返回 null。
    ///
    /// <para><b>为什么不能用 <see cref="MeasuredTortuosity"/> 代替</b>：那个是<b>事后</b>的 ——
    /// <c>_ratios</c> 要等排产真去查过 O-D 才有值，而兜底系数<b>在排产之前就要定</b>。
    /// 拿它做"自动推"会永远取不到值，静静退回常数（看起来像自动、其实一次也没生效）。
    /// 所以这里直接<b>采图</b>，与 <see cref="DerivedSnapRadiusM"/> 同一个路子：路网自己就答得出。</para>
    ///
    /// <para><b>采样是确定性的</b>（按节点顺序取固定步长的配对，不用随机数）——
    /// 否则同一张网两次跑出不同的兜底运距，判据也没法两遍连跑逐位相同。</para>
    ///
    /// <para>取<b>中位数</b>不取均值：矿区路网常有绕远的孤立支线，均值会被它们拉走。</para>
    /// </summary>
    public double? SampledTortuosity { get; }

    /// <summary>采样迂回系数的说明（样本数、区间）。</summary>
    public string SampledTortuosityNote { get; } = "";

    private (double?, string) DeriveTortuosity()
    {
        int n = _nodes.Count;
        if (n < 3) return (null, $"路网只有 {n} 个节点，采不出迂回系数");

        // ★ 只采**运距尺度**的 O-D。相邻节点之间往往有直达边，比值恒等于 1 ——
        //   把它们计进去，中位数会被压到 1.00，等于报"这张网不绕"，
        //   而兜底系数服务的恰恰是**源→排土场**这种长距离对。
        //   门槛取路网包围盒对角线的一个比例，跟着网自己的尺度走，不写死米数。
        double xMin = _nodes.Min(v => v.Position.X), xMax = _nodes.Max(v => v.Position.X);
        double yMin = _nodes.Min(v => v.Position.Y), yMax = _nodes.Max(v => v.Position.Y);
        double diag = Math.Sqrt((xMax - xMin) * (xMax - xMin) + (yMax - yMin) * (yMax - yMin));
        double minSep = Math.Max(1.0, diag * 0.15);

        var r = new List<double>();
        // 固定步长配对，不用随机数 —— 同一张网两次必须采出同一个值。
        foreach (int stride in new[] { 2, 3, 5, 8, 13, 21, 34 })
        {
            if (stride >= n) break;
            for (int i = 0; i + stride < n && r.Count < 64; i += Math.Max(1, n / 12))
            {
                var a = _nodes[i]; var b = _nodes[i + stride];
                double dx = b.Position.X - a.Position.X, dy = b.Position.Y - a.Position.Y;
                double straight = Math.Sqrt(dx * dx + dy * dy);
                if (straight < minSep) continue;                  // 太近，不代表运距尺度
                try
                {
                    var p = _solver.FindPath(a.Id, b.Id, Query);
                    if (p.Feasible && p.EquivM > 0) r.Add(p.EquivM / straight);
                }
                catch { /* 这一对解不出就算了，采样本来就是尽力而为 */ }
            }
        }
        if (r.Count < 3)
            return (null, $"路网上只采到 {r.Count} 对够远的可达 O-D（需 ≥3，间距 ≥{minSep:0}m），迂回系数采不出");
        r.Sort();
        double med = r[r.Count / 2];
        return (med, $"迂回系数按路网采样 {med:0.00}（{r.Count} 对 O-D 的中位数，"
                   + $"间距 ≥{minSep:0}m，区间 {r[0]:0.00}~{r[^1]:0.00}）");
    }

    /// <summary>
    /// 一对 O-D 的等效运距（km）。解不出来返回 null。
    /// <para><b>源点靠推进坐标反推</b>（基线质心沿推进方向推 <paramref name="srcU"/> 米）——
    /// 量驱动链只有 u 轴，没有源的真坐标。<b>单元链有两端真坐标，走
    /// <see cref="ResolveXyz"/>，别绕这一圈近似。</b></para>
    /// </summary>
    public double? Resolve(double srcU, double srcZ, DumpSlot slot)
    {
        if (slot == null) return null;
        // 源点：基线质心沿推进方向推 srcU 米
        double sx = _ox + (_hasAxis ? _dx * srcU : 0);
        double sy = _oy + (_hasAxis ? _dy * srcU : 0);
        return ResolveXyz(sx, sy, srcZ, slot.Cx, slot.Cy, slot.Cz);
    }

    /// <summary>
    /// 一对 O-D 的等效运距（km），<b>两端都给真坐标</b>。解不出来返回 null（落不上/不可达/同节点/抛异常）。
    ///
    /// <para><b>单元链用的就是它</b>：采掘单元有真质心、排土位置也有真质心，
    /// 不必像量驱动链那样从推进坐标 u 反推源点（那是近似，`WorkLineProjector` 没有逆向）。</para>
    ///
    /// <para><b>与 <see cref="Resolve"/> 共用同一份实现</b> —— 吸附、缓存、Dijkstra、迂回系数采样
    /// 全在这里，上面那个只负责把 u 折成 xy。两个入口两份实现迟早漂，漂了没人看得出来。</para>
    /// </summary>
    public double? ResolveXyz(double sx, double sy, double sz, double dx, double dy, double dz)
    {
        var from = Snap(sx, sy, sz);
        var to = Snap(dx, dy, dz);
        if (from == null || to == null) return null;
        if (string.Equals(from.Id, to.Id, StringComparison.Ordinal)) return null;   // 同一个节点 = 吸附太粗，别当 0 公里

        var key = (from.Id, to.Id);
        if (_cache.TryGetValue(key, out double? hit)) return hit;

        double? km = null;
        try
        {
            var path = _solver.FindPath(from.Id, to.Id, Query);
            if (path.Feasible && path.EquivM > 0)
            {
                km = path.EquivM / 1000.0;
                // 顺带量这张网的迂回程度：等效运距 ÷ 两端节点的直线平距。
                // 只在**新解出**的 O-D 上记一次（缓存命中不重复计入，否则中位数会被高频对压偏）。
                double sdx = to.Position.X - from.Position.X, sdy = to.Position.Y - from.Position.Y;
                double straight = Math.Sqrt(sdx * sdx + sdy * sdy);
                if (straight > 1.0) _ratios.Add(path.EquivM / straight);
            }
        }
        catch { km = null; }
        _cache[key] = km;
        return km;
    }

    /// <summary>
    /// 从节点间距推吸附半径。节点少于 2 个时给一个保守值并说清。
    /// <para>算法：每个节点到<b>最近邻</b>的平面距离 → 取中位数 → ×2 → 夹在 [50, 1000]。
    /// 节点多时抽样 400 个，避免 O(N²) 在大路网上卡住（中位数对抽样稳健）。</para>
    /// </summary>
    private static (double R, string Note) DeriveSnapRadius(List<Cad.Road.RoadNode> nodes)
    {
        if (nodes.Count < 2) return (300, "路网节点少于 2 个，吸附半径用保守缺省 300m");

        // 抽样：节点很多时不必全算，中位数对抽样稳健
        int step = Math.Max(1, nodes.Count / 400);
        var nn = new List<double>();
        for (int i = 0; i < nodes.Count; i += step)
        {
            double best = double.MaxValue;
            for (int j = 0; j < nodes.Count; j++)
            {
                if (j == i) continue;
                double dx = nodes[i].Position.X - nodes[j].Position.X;
                double dy = nodes[i].Position.Y - nodes[j].Position.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < best) best = d2;
            }
            if (best < double.MaxValue) nn.Add(Math.Sqrt(best));
        }
        if (nn.Count == 0) return (300, "算不出节点间距，吸附半径用保守缺省 300m");

        nn.Sort();
        double med = nn[nn.Count / 2];
        double r = Math.Clamp(med * 2, 50, 1000);
        string how = $"吸附半径 {r:0}m = 2 × 中位节点间距 {med:0.#}m"
                   + (Math.Abs(r - med * 2) > 1e-6 ? $"（已夹到 [50,1000] 区间）" : "")
                   + $"（{nn.Count} 个节点抽样）";
        return (r, how);
    }

    /// <summary>就近吸附到路网节点；超出 <see cref="SnapRadiusM"/> 返回 null（= 落不上，退回兜底）。</summary>
    private Cad.Road.RoadNode? Snap(double x, double y, double z)
    {
        // 10m 量化做缓存键 —— 吸附半径几百米，这个粒度不会改变结果
        var key = ((long)Math.Round(x / 10.0), (long)Math.Round(y / 10.0));
        if (_snapCache.TryGetValue(key, out var cached)) return cached;

        Cad.Road.RoadNode? best = null;
        double bestD2 = SnapRadiusM * SnapRadiusM;
        foreach (var n in _nodes)
        {
            double dx = n.Position.X - x, dy = n.Position.Y - y;
            double d2 = dx * dx + dy * dy;                  // 平距吸附：标高由路网自己的边坡度承担
            if (d2 <= bestD2) { bestD2 = d2; best = n; }
        }
        _snapCache[key] = best;
        return best;
    }
}
