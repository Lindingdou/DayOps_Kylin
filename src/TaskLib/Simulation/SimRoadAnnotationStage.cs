// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimRoadAnnotationStage.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  运输线的工程图式 —— 按坡度分段 + 逐段标注坡度% / 标高
//
//  ── 这一层要回答的问题，和「车流」「运输线」都不一样 ──
//   · HaulRouteStage 回答「哪些路本期有车走」（几何 + 命中率）；
//   · SimFlowStage   回答「车有多密、往哪走」；
//   · 本层回答的是**开拓运输的工程问题**：这条路哪一段多陡、坡顶坡底在什么标高。
//     那是审图时真正会问的两个数，也是图上必须写出来的两个数。
//
//  ── R-A1 标注的粒度是「坡度段」，不是采样段 ──
//  路网中线的采样间距是十几米，一条 3 km 的路有一百多个采样段。逐采样段标注 =
//  一百多个标签糊成一条黑带，等于没标。所以先把连续同坡的采样段**并成一段**
//  （|坡度 − 段坡度| ≤ 容差 且 段长 ≥ 下限），标注打在段上。
//  ⇒ 图上标签的个数 = 坡度真正变化的次数，这正是工程上关心的那个数。
//
//  ── R-A2 只用**去重后**的路段，不用逐笔路径 ──
//  几十笔 O-D 共用同一条干线；拿逐笔路径标注会在干线上叠几十份同样的标签。
//  <see cref="SimHaulRouteResult.Segments"/> 已经是「本期真的用到的那部分路网几何」的并集，
//  一段路只出现一次 —— 标注就该长在它上面。
//
//  ── R-A3 串链的口径与去重键同源 ──
//  段是无向的、按坐标量化到 1 mm 去重的（HaulRouteStage.KeyOf）。本层按**同一套量化**
//  建端点邻接表，否则「同一个交叉口」会因为浮点尾巴被拆成两个点，链在那里断掉，
//  现象是标注莫名其妙地断成很多小段。量化口径必须与那边一致，两处写迟早漂。
//
//  ── R-A4 坡度是**有向**的，链的走向决定正负 ──
//  同一段路，上坡还是下坡取决于从哪头看。本层沿链的行进方向报坡度，
//  并在图例里写明「沿标注箭头方向」；不写方向的坡度是个没有含义的数。
//
//  ── R-A5 路宽是世界宽度，画不出来就说画不出来 ──
//  overlay 是 1px 细线。要「像一条路」只能在世界系里按半宽偏出两条边线（本层做的），
//  于是缩小视图下它会自然收细 —— 那是对的，路本来就只有几十米宽。
//  绝不用「屏幕固定粗细」冒充路宽：那样图上量出来的宽度不对应任何真实的量。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>一条按坡度并出来的路段（标注就打在它上面）。</summary>
public sealed class SimRoadRun
{
    /// <summary>沿链行进方向的起点 / 终点（世界坐标）。</summary>
    public double X0, Y0, Z0, X1, Y1, Z1;
    /// <summary>本段三维长 m（路的实际长度，报里程用它）。</summary>
    public double LengthM;
    /// <summary>本段**水平投影**长 m（坡度的分母，见 <see cref="GradePct"/>）。</summary>
    public double HorizontalM;
    /// <summary>
    /// 本段纵坡 %（沿行进方向，上坡为正）。
    /// <para>★ 分母是<b>水平距离</b>不是三维斜长 —— 工程上纵坡 i = Δh / 水平距离，
    /// 这是设计规范里那个数。拿斜长做分母会系统性偏小（12% 的坡偏 0.7 个百分点），
    /// 而两种算法都"看着对"。</para>
    /// </summary>
    public double GradePct;
    /// <summary>本段承担的吨量 t（并进来的采样段里最大的那个 —— 干线支线的粗细分档用它）。</summary>
    public double TonnageT;
    /// <summary>并进来的采样段数（判据对账用：所有 run 的 SampleCount 之和 = 链上总段数）。</summary>
    public int SampleCount;

    public double MidX => (X0 + X1) * 0.5;
    public double MidY => (Y0 + Y1) * 0.5;
    public double MidZ => (Z0 + Z1) * 0.5;

    public string GradeText => (GradePct >= 0 ? "" : "-") + Math.Abs(GradePct).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public string ElevText => Z1.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>一次标注重建的结论。</summary>
public sealed class SimRoadAnnotationResult
{
    public string PeriodKey { get; internal set; } = "";

    /// <summary>喂进来的去重段数。</summary>
    public int SegmentsIn { get; internal set; }
    /// <summary>串成了几条链。</summary>
    public int Chains { get; internal set; }
    /// <summary>并出来的坡度段数（= 图上标签个数）。</summary>
    public int Runs => RunList.Count;
    /// <summary>因为太短被并进邻段的段数。</summary>
    public int MergedShortRuns { get; internal set; }
    /// <summary>没能进入任何链的段数（理论上应为 0；不为 0 说明串链口径与去重键不同源）。</summary>
    public int Orphans { get; internal set; }

    /// <summary>分类账：所有 run 的采样段数之和 + 落单的 = 喂进来的段数。</summary>
    public bool Balanced => RunList.Sum(r => r.SampleCount) + Orphans == SegmentsIn;

    public List<SimRoadRun> RunList { get; } = new();

    /// <summary>最大 / 平均绝对坡度 %（界面直接显示 —— 审图先看这两个数）。</summary>
    public double MaxAbsGradePct { get; internal set; }
    public double MeanAbsGradePct { get; internal set; }
    /// <summary>标注覆盖的路网总长 m。</summary>
    public double TotalLengthM { get; internal set; }

    /// <summary>本帧真的推了多少段线 / 多少个标签。</summary>
    public int DrawnSegments { get; internal set; }
    public int DrawnLabels { get; internal set; }
    public int DroppedByCap { get; internal set; }

    public List<string> Notes { get; } = new();
    public string Summary { get; internal set; } = "";
    public string LegendText { get; internal set; } = "";

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine(LegendText);
        foreach (var n in Notes)
            sb.AppendLine(n.StartsWith("◆", StringComparison.Ordinal) || n.StartsWith("·", StringComparison.Ordinal)
                          ? n : "· " + n);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>运输线工程标注的显示口径。</summary>
public sealed class SimRoadAnnotationParams
{
    /// <summary>坡度并段容差 %（|采样段坡度 − 段坡度| ≤ 它就并进去）。</summary>
    public double GradeTolerancePct { get; set; } = 0.8;

    /// <summary>坡度段长度下限 m（短于它的并进邻段 —— 否则一个采样点的噪声就切出一段）。</summary>
    public double MinRunM { get; set; } = 60;

    /// <summary>
    /// 路的世界半宽 m（0 = 只画单根中线）。&gt;0 时按半宽在平面内偏出两条边线，
    /// 于是缩小视图下它会自然收细 —— 那是对的，见 R-A5。
    /// </summary>
    public double RoadHalfWidthM { get; set; } = 12;

    /// <summary>画中线（与边线同时开时是「路 + 中心线」，工程图常见画法）。</summary>
    public bool ShowCenterline { get; set; } = true;

    /// <summary>标坡度 %。</summary>
    public bool ShowGrade { get; set; } = true;
    /// <summary>标段末标高 m。</summary>
    public bool ShowElevation { get; set; } = true;

    /// <summary>标注世界字高 m。</summary>
    public double LabelHeightM { get; set; } = 9.0;

    /// <summary>抬升 m —— 纯显示偏移，避与地表 z-fight。不改任何标高数（标的是原始 Z）。</summary>
    public double LiftM { get; set; } = 1.5;

    /// <summary>
    /// 路面色 0xRRGGBB。<b>缺省是浅灰不是黑</b>：本应用的三维视口底色是深蓝
    /// （Sim.Canvas.Background #101A2B 那一族），黑线在上面看不见。
    /// 出图/白底演示时改成 0x101010 即可。
    /// </summary>
    public uint RoadRgb { get; set; } = 0xE6E9EE;

    /// <summary>坡度标注色（工程图惯例：坡度蓝）。</summary>
    public uint GradeRgb { get; set; } = 0x5AA9FF;
    /// <summary>标高标注色（工程图惯例：标高红）。</summary>
    public uint ElevRgb { get; set; } = 0xFF5A5A;

    /// <summary>超过这个绝对坡度就把该段路染成警示色（0 = 不判）。</summary>
    public double GradeWarnPct { get; set; } = 10.0;
    public uint WarnRgb { get; set; } = 0xFFB020;

    public int MaxSegments { get; set; } = 6000;
    public int MaxLabels { get; set; } = 400;
}

/// <summary>
/// 运输线工程标注舞台。<b>永不抛</b>。
/// <para>换期时 <see cref="Rebuild"/> 一次即可 —— 标注不随期内相位变（路不会在一个月里挪），
/// 所以本舞台**没有 Tick**。</para>
/// </summary>
public sealed class SimRoadAnnotationStage
{
    public const string RoadGroup = "simroad.line";
    public const string GradeGroup = "simroad.grade";
    public const string ElevGroup = "simroad.elev";

    private readonly ISimDynamicOverlay _sink;
    public SimRoadAnnotationStage(ISimDynamicOverlay? sink = null) => _sink = sink ?? SimDynamicOverlay.Current;

    public SimRoadAnnotationParams Params { get; set; } = new();
    public SimRoadAnnotationResult? Last { get; private set; }
    public bool Available => _sink.Available;

    private bool _pushedAny;

    /// <summary>
    /// 按本期去重段重建标注。<paramref name="segments"/> 来自
    /// <see cref="SimHaulRouteResult.Segments"/> —— 见 R-A2，别喂逐笔路径。
    /// </summary>
    public SimRoadAnnotationResult Rebuild(string periodKey, IReadOnlyList<SimHaulSegment>? segments)
    {
        var res = new SimRoadAnnotationResult { PeriodKey = periodKey ?? "" };
        try { RebuildCore(res, segments); }
        catch (Exception ex)
        {
            res.Notes.Add($"运输线标注重建异常（{ex.GetType().Name}: {ex.Message}）→ 本期不标。");
            res.Summary = "◆ 运输线标注：重建异常，本期一段都没标。";
            res.RunList.Clear();
        }
        Last = res;
        Push(res);
        return res;
    }

    /// <summary>撤掉本舞台的三个组。幂等。</summary>
    public void Clear()
    {
        try { _sink.Clear(RoadGroup); _sink.Clear(GradeGroup); _sink.Clear(ElevGroup); } catch { }
        _pushedAny = false;
    }

    public void RequestRender() => _sink.RequestRender();

    // ── 串链 → 并坡 ─────────────────────────────────────────────────────────

    private void RebuildCore(SimRoadAnnotationResult res, IReadOnlyList<SimHaulSegment>? segments)
    {
        var segs = (segments ?? Array.Empty<SimHaulSegment>()).Where(s => s != null && s.LengthM > 1e-6).ToList();
        res.SegmentsIn = segs.Count;
        if (segs.Count == 0)
        {
            res.Summary = "运输线标注：本期没有路段可标（先看运输线那一侧的命中率）。";
            BuildLegend(res);
            return;
        }

        // ── 端点邻接（量化口径与 HaulRouteStage 的去重键同源，见 R-A3）──
        var adj = new Dictionary<Node, List<int>>();
        var ends = new Node[segs.Count * 2];
        for (int i = 0; i < segs.Count; i++)
        {
            var a = NodeOf(segs[i].X0, segs[i].Y0, segs[i].Z0);
            var b = NodeOf(segs[i].X1, segs[i].Y1, segs[i].Z1);
            ends[2 * i] = a; ends[2 * i + 1] = b;
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>();
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>();
            la.Add(i); lb.Add(i);
        }

        var used = new bool[segs.Count];
        var chains = new List<List<int>>();     // 每条链 = 有序的段下标
        var chainDir = new List<List<bool>>();  // 该段是否按 (X0→X1) 方向走

        // 先从「非度 2」的节点起链（端点/交叉口），剩下的是纯环，再各起一条
        foreach (var start in adj.Where(kv => kv.Value.Count != 2).Select(kv => kv.Key).ToList())
            foreach (int s0 in adj[start].ToList())
            {
                if (used[s0]) continue;
                Walk(segs, adj, ends, used, start, s0, chains, chainDir);
            }
        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            Walk(segs, adj, ends, used, ends[2 * i], i, chains, chainDir);
        }
        res.Chains = chains.Count;
        res.Orphans = used.Count(u => !u);

        // ── 每条链：按坡度并段 ──
        foreach (var (chain, dirs) in chains.Zip(chainDir, (c, d) => (c, d)))
            MergeChain(res, segs, chain, dirs);

        // 太短的并进邻段（噪声段）
        MergeShortRuns(res);

        res.TotalLengthM = res.RunList.Sum(r => r.LengthM);
        res.MaxAbsGradePct = res.RunList.Count > 0 ? res.RunList.Max(r => Math.Abs(r.GradePct)) : 0;
        res.MeanAbsGradePct = res.TotalLengthM > 1e-6
            ? res.RunList.Sum(r => Math.Abs(r.GradePct) * r.LengthM) / res.TotalLengthM
            : 0;

        // ── 留条 ──
        if (res.Orphans > 0)
            res.Notes.Add($"◆ 有 {res.Orphans} 段没能进入任何链 —— 串链的坐标量化口径与去重键**不同源**了"
                        + "（本该都是 1 mm）。现象会是标注莫名其妙地断成很多小段。");
        if (res.MergedShortRuns > 0)
            res.Notes.Add($"· 有 {res.MergedShortRuns} 个不足 {Params.MinRunM:0} m 的坡度段并进了邻段 ——"
                        + "一个采样点的噪声不该切出一段。要看更细的分段把「最小段长」调小。");
        if (Params.GradeWarnPct > 0)
        {
            int warn = res.RunList.Count(r => Math.Abs(r.GradePct) > Params.GradeWarnPct);
            if (warn > 0)
                res.Notes.Add($"◆ 有 {warn} 段坡度超过 {Params.GradeWarnPct:0.#}%（已染警示色），"
                            + $"最陡 {res.MaxAbsGradePct:0.0}%。这是**路网中线自身的几何**，"
                            + "不是本层算出来的设计值 —— 要改坡度请去开拓运输系统改中线。");
        }
        res.Notes.Add("· 坡度沿**链的行进方向**报（同一段路反过来走就是反号）；标高标的是段末点的原始 Z，"
                    + $"抬升 {Params.LiftM:0.##} m 只作用于画线，不进标注数字。");

        res.Summary = res.RunList.Count == 0
            ? "◆ 运输线标注：串出 0 段（喂进来 " + res.SegmentsIn + " 段）。"
            : $"运输线标注：{res.Chains} 条链 · {res.Runs} 个坡度段 · 全长 {res.TotalLengthM / 1000:0.00} km"
            + $"　·　最陡 {res.MaxAbsGradePct:0.0}%　平均 {res.MeanAbsGradePct:0.0}%"
            + $"　[分类账自洽：{(res.Balanced ? "是" : "**否 —— 有段没落进任何桶**")}]";

        BuildLegend(res);
    }

    private static void Walk(List<SimHaulSegment> segs, Dictionary<Node, List<int>> adj, Node[] ends,
                             bool[] used, Node from, int seg,
                             List<List<int>> chains, List<List<bool>> dirs)
    {
        var chain = new List<int>();
        var dir = new List<bool>();
        Node cur = from;
        int s = seg;
        while (true)
        {
            if (used[s]) break;
            used[s] = true;
            var a = ends[2 * s];
            var b = ends[2 * s + 1];
            bool forward = a.Equals(cur);
            chain.Add(s); dir.Add(forward);
            cur = forward ? b : a;

            if (!adj.TryGetValue(cur, out var nb) || nb.Count != 2) break;   // 到端点/交叉口停
            int next = nb[0] == s ? nb[1] : nb[0];
            if (used[next]) break;
            s = next;
        }
        if (chain.Count > 0) { chains.Add(chain); dirs.Add(dir); }
    }

    private void MergeChain(SimRoadAnnotationResult res, List<SimHaulSegment> segs,
                            List<int> chain, List<bool> dirs)
    {
        SimRoadRun? run = null;
        double runRise = 0, runLen = 0, runHoriz = 0;

        for (int k = 0; k < chain.Count; k++)
        {
            var s = segs[chain[k]];
            bool fwd = dirs[k];
            double ax = fwd ? s.X0 : s.X1, ay = fwd ? s.Y0 : s.Y1, az = fwd ? s.Z0 : s.Z1;
            double bx = fwd ? s.X1 : s.X0, by = fwd ? s.Y1 : s.Y0, bz = fwd ? s.Z1 : s.Z0;
            double len = s.LengthM;
            double hor = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));   // ★ 坡度的分母
            double g = hor > 1e-9 ? (bz - az) / hor * 100.0 : 0;

            if (run != null)
            {
                double cur = runHoriz > 1e-9 ? runRise / runHoriz * 100.0 : 0;
                if (Math.Abs(g - cur) <= Params.GradeTolerancePct)
                {
                    run.X1 = bx; run.Y1 = by; run.Z1 = bz;
                    runRise += bz - az; runLen += len; runHoriz += hor;
                    run.LengthM = runLen;
                    run.HorizontalM = runHoriz;
                    run.GradePct = runHoriz > 1e-9 ? runRise / runHoriz * 100.0 : 0;
                    run.TonnageT = Math.Max(run.TonnageT, s.TonnageT);
                    run.SampleCount++;
                    continue;
                }
                res.RunList.Add(run);
            }

            run = new SimRoadRun
            {
                X0 = ax, Y0 = ay, Z0 = az, X1 = bx, Y1 = by, Z1 = bz,
                LengthM = len, HorizontalM = hor, GradePct = g, TonnageT = s.TonnageT, SampleCount = 1,
            };
            runRise = bz - az; runLen = len; runHoriz = hor;
        }
        if (run != null) res.RunList.Add(run);
    }

    /// <summary>把短于下限的段并进**相邻**段（优先并向更长的那一侧；链首尾各只有一侧）。</summary>
    private void MergeShortRuns(SimRoadAnnotationResult res)
    {
        double min = Params.MinRunM;
        if (min <= 0 || res.RunList.Count < 2) return;

        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 64)
        {
            changed = false;
            for (int i = 0; i < res.RunList.Count; i++)
            {
                var r = res.RunList[i];
                if (r.LengthM >= min) continue;
                // 只与**几何上真的首尾相接**的邻段合并 —— 不同链的两段挨在列表里不代表挨在图上
                int j = -1;
                if (i + 1 < res.RunList.Count && Touch(r, res.RunList[i + 1])) j = i + 1;
                else if (i - 1 >= 0 && Touch(res.RunList[i - 1], r)) j = i - 1;
                if (j < 0) continue;

                var o = res.RunList[j];
                double rise = (r.Z1 - r.Z0) + (o.Z1 - o.Z0);
                double len = r.LengthM + o.LengthM;
                double hor = r.HorizontalM + o.HorizontalM;
                if (j > i) { o.X0 = r.X0; o.Y0 = r.Y0; o.Z0 = r.Z0; }
                else { o.X1 = r.X1; o.Y1 = r.Y1; o.Z1 = r.Z1; }
                o.LengthM = len;
                o.HorizontalM = hor;
                o.GradePct = hor > 1e-9 ? rise / hor * 100.0 : 0;   // ★ 分母是水平距离
                o.TonnageT = Math.Max(o.TonnageT, r.TonnageT);
                o.SampleCount += r.SampleCount;
                res.RunList.RemoveAt(i);
                res.MergedShortRuns++;
                changed = true;
                break;
            }
        }
    }

    private static bool Touch(SimRoadRun a, SimRoadRun b)
        => Q(a.X1) == Q(b.X0) && Q(a.Y1) == Q(b.Y0) && Q(a.Z1) == Q(b.Z0);

    // ── 推送 ────────────────────────────────────────────────────────────────

    private void Push(SimRoadAnnotationResult res)
    {
        try
        {
            var p = Params;
            double lift = double.IsNaN(p.LiftM) || double.IsInfinity(p.LiftM) ? 0 : p.LiftM;
            double hw = Math.Max(0, p.RoadHalfWidthM);

            var xyz = new List<double>();
            var col = new List<uint>();
            int cap = Math.Max(0, p.MaxSegments);
            int dropped = 0;

            foreach (var r in res.RunList)
            {
                uint rgb = p.GradeWarnPct > 0 && Math.Abs(r.GradePct) > p.GradeWarnPct ? p.WarnRgb : p.RoadRgb;
                uint argb = 0xFF000000u | rgb;

                double dx = r.X1 - r.X0, dy = r.Y1 - r.Y0;
                double h = Math.Sqrt(dx * dx + dy * dy);
                double nx = 0, ny = 0;
                if (h > 1e-9) { nx = -dy / h * hw; ny = dx / h * hw; }

                void Seg(double x0, double y0, double z0, double x1, double y1, double z1)
                {
                    if (xyz.Count / 6 >= cap) { dropped++; return; }
                    xyz.Add(x0); xyz.Add(y0); xyz.Add(z0 + lift);
                    xyz.Add(x1); xyz.Add(y1); xyz.Add(z1 + lift);
                    col.Add(argb);
                }

                if (hw > 1e-9)
                {
                    Seg(r.X0 + nx, r.Y0 + ny, r.Z0, r.X1 + nx, r.Y1 + ny, r.Z1);   // 左边线
                    Seg(r.X0 - nx, r.Y0 - ny, r.Z0, r.X1 - nx, r.Y1 - ny, r.Z1);   // 右边线
                    // 段末封口：坡度变化点在图上看得见（工程图里那是个桩号）
                    Seg(r.X1 + nx, r.Y1 + ny, r.Z1, r.X1 - nx, r.Y1 - ny, r.Z1);
                }
                if (hw <= 1e-9 || p.ShowCenterline)
                    Seg(r.X0, r.Y0, r.Z0, r.X1, r.Y1, r.Z1);
            }

            res.DrawnSegments = col.Count;
            res.DroppedByCap = dropped;
            if (dropped > 0)
                res.Notes.Add($"◆ 路面线段超过一帧上限 {cap}，**舍掉 {dropped} 段**（不是没有路，是没画）。");
            _sink.SetLines(RoadGroup, xyz.ToArray(), col.ToArray(), col.Count);

            // ── 标注：坡度打在段中点、标高打在段末点 ──
            int labCap = Math.Max(0, p.MaxLabels);
            PushLabels(res, GradeGroup, p.ShowGrade, labCap, p.GradeRgb,
                       r => (r.MidX, r.MidY, r.MidZ + lift + p.LabelHeightM * 0.4), r => r.GradeText);
            PushLabels(res, ElevGroup, p.ShowElevation, labCap, p.ElevRgb,
                       r => (r.X1, r.Y1, r.Z1 + lift), r => r.ElevText);

            _pushedAny = true;
        }
        catch (Exception ex)
        {
            res.Notes.Add($"◆ 运输线标注推送失败（{ex.GetType().Name}），本期图上没有标注。");
        }
    }

    private void PushLabels(SimRoadAnnotationResult res, string group, bool want, int cap, uint rgb,
                            Func<SimRoadRun, (double X, double Y, double Z)> anchor,
                            Func<SimRoadRun, string> text)
    {
        if (!want || res.RunList.Count == 0)
        {
            if (_pushedAny) _sink.SetLabels(group, null, null, null, null, null, null, 0);
            return;
        }
        // 段数超上限时按**段长**从大到小取：短段的标签本来就最容易互相压盖，先丢它们。
        var take = res.RunList.Count <= cap
                 ? res.RunList
                 : res.RunList.OrderByDescending(r => r.LengthM).Take(cap).ToList();

        int n = take.Count;
        var xyz = new double[n * 3];
        var argb = new uint[n];
        var hM = new float[n];
        var ha = new byte[n];
        var va = new byte[n];
        var txt = new string[n];
        for (int i = 0; i < n; i++)
        {
            var (x, y, z) = anchor(take[i]);
            xyz[3 * i] = x; xyz[3 * i + 1] = y; xyz[3 * i + 2] = z;
            argb[i] = 0xFF000000u | rgb;
            hM[i] = (float)Math.Max(0.5, Params.LabelHeightM);
            ha[i] = 1;   // 水平居中
            va[i] = 2;   // 垂直居中
            txt[i] = text(take[i]);
        }
        _sink.SetLabels(group, xyz, txt, argb, hM, ha, va, n);
        res.DrawnLabels += n;
        if (take.Count < res.RunList.Count)
            res.Notes.Add($"◆ 标注个数超过上限 {cap}，按段长从大到小只标了 {take.Count} 个"
                        + $"（**少标 {res.RunList.Count - take.Count} 个**，不是没有）。");
    }

    private void BuildLegend(SimRoadAnnotationResult res)
    {
        var p = Params;
        res.LegendText =
            $"图例：路按**世界半宽 {p.RoadHalfWidthM:0.#} m** 画双边线"
          + (p.ShowCenterline ? " + 中线" : "")
          + "（缩小视图下会自然收细 —— 路本来就只有几十米宽，不用屏幕固定粗细冒充）；"
          + $"**蓝字 = 纵坡 %**（沿链行进方向，上坡为正）、**红字 = 段末标高 m**；"
          + $"并段口径：|坡差| ≤ {p.GradeTolerancePct:0.#}% 且段长 ≥ {p.MinRunM:0} m；"
          + (p.GradeWarnPct > 0 ? $"绝对坡度 > {p.GradeWarnPct:0.#}% 染警示色。" : "");
    }

    // ── 与 HaulRouteStage 同源的坐标量化（R-A3）──
    private readonly record struct Node(long X, long Y, long Z);
    private static Node NodeOf(double x, double y, double z) => new(Q(x), Q(y), Q(z));
    private static long Q(double v) => (long)Math.Round(v * 1000.0);
}
