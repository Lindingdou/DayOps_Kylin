// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/UnitVolumeBinner.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>
/// 一个采掘单元在<b>块体侧</b>的量。见 `docs/块体模型重规划_量源统一.md` 的 BM1/BM1b。
/// </summary>
public sealed class UnitVolume
{
    public string UnitId = "";
    public UnitKind Kind;

    /// <summary><b>整格</b>口径的量（m³ 原位实方）—— 与引擎 <c>(k,g,u)</c> 桶<b>同一口径</b>，守恒对账拿它。</summary>
    public double GrossM3;

    /// <summary>
    /// <b>BM1b 边界修正后</b>的量（m³）：顶/底板处的 cell 只计落在煤层里的那一部分。
    /// <para>薄煤层上整格与修正后可以差到 ±29%（Phase 0 的 E3 实测），
    /// 所以<b>报表要用它</b>，而<b>守恒对账要用 <see cref="GrossM3"/></b> —— 两个口径都留着。</para>
    /// </summary>
    public double NetM3;

    /// <summary>落在本单元里、但<b>整格都在顶底板之外</b>的量（m³）。</summary>
    /// <remarks>
    /// 这是<b>判煤规则与顶底板几何不一致</b>的直接证据：块体说这里是煤，而 TIN 说这里不在煤层里。
    /// <b>不许静默丢掉</b>（BM8）—— 它既不该进 <see cref="NetM3"/>，也不该假装不存在。
    /// </remarks>
    public double OutsideSeamM3;

    public long Cells;

    /// <summary>
    /// <b>跨界量</b>（m³，整格计）：顶/底板从这个 cell 的肚子里穿过（重叠比严格在 0 与 1 之间）。
    /// <para>它是<b>「这份块体够不够细」的直接度量</b>：跨界量占比越高，块体在这个粒度上
    /// 就越给不出准确的量 —— 边界修正只能把<b>高报</b>那一半削回去，
    /// <b>低报</b>（薄煤层整整一层 cell 没进扫描）修不了，只能靠更细的格或八叉树细化（BM1b 方案②）。</para>
    /// </summary>
    public double StraddleM3;
    public long StraddleCells;

    /// <summary>边界修正量 = 整格 − 修正后（含 <see cref="OutsideSeamM3"/>）。</summary>
    public double BoundaryAdjM3 => GrossM3 - NetM3;

    /// <summary>
    /// 几何估算量（<c>走向长 × W × 垂厚</c>，m³）—— <b>只作对账，不是账</b>（BM5）。
    /// 0 = 调用方没给。
    /// </summary>
    public double EstM3;

    /// <summary>块体量（修正后）相对几何估算的偏差。估算为 0 时给 0。</summary>
    public double DeviationPct => EstM3 > 1e-9 ? (NetM3 - EstM3) / EstM3 : 0;
}

/// <summary>一条超差记录（BM6）。</summary>
public sealed class VolumeDeviation
{
    public string UnitId = "";
    public double EstM3, NetM3, Pct;
    public override string ToString()
        => $"{UnitId}：几何估算 {EstM3:N0} → 块体 {NetM3:N0} m³（{Pct:+0.0%;-0.0%}）";
}

/// <summary>按采掘单元分箱的结果。</summary>
public sealed class UnitVolumeResult
{
    public readonly List<UnitVolume> Units = new();
    public readonly List<string> Notes = new();

    /// <summary>没落进任何单元的量（m³）。<b>显式报，不摊进任何单元</b>（BM8）。</summary>
    public double UnassignedM3;
    public long UnassignedCells;

    /// <summary>落进<b>多个</b>单元的量（m³，按重复计一次）—— 归属重叠，口径未定（待拍板 #2）。</summary>
    public double OverlapM3;
    public long OverlapCells;

    /// <summary>本次喂进来的总量（m³）—— 守恒式的左端。</summary>
    public double FedM3;
    public long FedCells;

    /// <summary>
    /// 喂入量按煤/岩分账（m³）。<b>这就是 E1「剥采闭合」在扫描层的形式</b>：
    /// 一个 cell 由判煤规则决定归煤还是归岩，<b>只能进其中一个</b> ——
    /// 所以"同一方土在煤账和岩账里各算一遍"（`MiningModelPlanner` 里那个要靠区间求交去扣的坑）
    /// 在这条路上<b>结构上不可能发生</b>，不需要"扣内含煤"这个动作。
    /// </summary>
    public double FedCoalM3, FedRockM3;

    public double TotalGrossM3 => Units.Sum(u => u.GrossM3);
    public double TotalNetM3 => Units.Sum(u => u.NetM3);
    public double TotalOutsideSeamM3 => Units.Sum(u => u.OutsideSeamM3);
    public double TotalStraddleM3 => Units.Sum(u => u.StraddleM3);

    /// <summary>跨界量占归属量的比例 —— <b>块体细度体检</b>。归属量为 0 时给 0。</summary>
    public double StraddleRatio => TotalGrossM3 > 1e-9 ? TotalStraddleM3 / TotalGrossM3 : 0;

    /// <summary>
    /// 块体够不够细的<b>一句话结论</b>。<paramref name="warnAt"/> 以上就明说"这个粒度给不出更准的量"。
    /// <para><b>为什么要有这一句</b>：跨界量高的时候，两条产线对不上<b>不是谁算错了</b>，
    /// 是块体在这个粒度上本来就答不了。没有这句话，用户只会看到"账差了 20%"然后去查算法。
    /// <b>而干净输入下它必须闭嘴</b>（跨界为 0 就不留条）—— 每跑必报的警告等于没有警告。</para>
    /// </summary>
    public string? CoarsenessNote(double warnAt = 0.05)
    {
        if (TotalGrossM3 <= 1e-9 || StraddleRatio < warnAt) return null;
        return $"◆ 块体细度：{StraddleRatio:P1} 的归属量（{TotalStraddleM3:N0} m³）落在顶/底板穿过的【跨界格】里。"
             + "边界修正只削得掉其中【高报】的那一半；薄煤层整层没进扫描造成的【低报】修不了 —— "
             + "要更准就得把块体在顶底板处细化（BM1b 方案②）。";
    }

    public UnitVolume? ById(string id) => Units.FirstOrDefault(u => string.Equals(u.UnitId, id, StringComparison.Ordinal));

    /// <summary>
    /// <b>BM6 · 换源对账闸</b>。把几何估算填进来，逐单元比"块体量 vs 几何估算"，
    /// 超出 <paramref name="tolPct"/> 的<b>逐条报出</b>，容差内<b>一个字都不说</b>。
    ///
    /// <para><b>这就是「量差别不太大就可以」的可执行形式</b>：容差是现场的决定，
    /// 引擎的责任是"超了必须说"，而不是替现场判断多少算大。</para>
    ///
    /// <para><b>⚠ 阈值不该拍一个全局百分比。</b>整格求和的离散残差<b>随「煤厚 ÷ cell 高」走</b>：
    /// 实测 cell 5m 下，煤厚 10m 差 0%、8m 差 +25%、7m 差 −28.6%（`BM_P1c` 那张表）。
    /// 也就是说<b>薄煤层天生超差</b>，把阈值放大到能盖住它，等于这道闸对厚煤层形同虚设。
    /// 正确做法是按该层的<b>理论离散残差 + 余量</b>定，或者干脆把块体在顶底板处细化（BM1b 方案②）。</para>
    ///
    /// <para>比<b>相对差</b>不比绝对小数位 —— 绝对位会把浮点累积噪声判成账不平（本仓踩过三次）。</para>
    /// </summary>
    /// <param name="estM3">单元 Id → 几何估算量（m³）。没给的单元<b>跳过不判</b>，并计入 <c>NoEst</c>。</param>
    /// <param name="tolPct">容差（0.10 = 10%）。</param>
    public (List<VolumeDeviation> Over, int Checked, int NoEst) Reconcile(
        IReadOnlyDictionary<string, double>? estM3, double tolPct)
    {
        var over = new List<VolumeDeviation>();
        int chk = 0, noEst = 0;
        foreach (var u in Units)
        {
            if (estM3 == null || !estM3.TryGetValue(u.UnitId, out double e) || e <= 1e-9)
            { u.EstM3 = 0; noEst++; continue; }
            u.EstM3 = e; chk++;
            double pct = (u.NetM3 - e) / e;
            if (Math.Abs(pct) > Math.Max(0, tolPct))
                over.Add(new VolumeDeviation { UnitId = u.UnitId, EstM3 = e, NetM3 = u.NetM3, Pct = pct });
        }
        over.Sort((a, b) => Math.Abs(b.Pct).CompareTo(Math.Abs(a.Pct)));
        return (over, chk, noEst);
    }

    /// <summary>
    /// 自洽校核。<b>守恒式：<c>Σ 单元整格量 + 未归属量 = 喂进来的总量</c></b>。
    /// <para>重叠的那部分在 <see cref="OverlapM3"/> 里另记 —— 它已经被<b>算进第一个命中的单元</b>，
    /// 所以不进守恒式；把它也加进去会重复计量。</para>
    /// </summary>
    public bool Validate(out List<string> issues)
    {
        issues = new List<string>();
        double lhs = TotalGrossM3 + UnassignedM3;
        double rel = FedM3 > 1e-9 ? Math.Abs(lhs - FedM3) / FedM3 : (lhs > 1e-9 ? 1 : 0);
        // 比相对差不比绝对小数位 —— 绝对位会把浮点累积噪声判成账不平（本仓踩过三次）
        if (rel > 1e-9)
            issues.Add($"守恒不成立：Σ单元 {TotalGrossM3:0.###} + 未归属 {UnassignedM3:0.###} = {lhs:0.###}"
                     + $" ≠ 喂入 {FedM3:0.###}（相对差 {rel:P6}）");
        long cl = Units.Sum(u => u.Cells) + UnassignedCells;
        if (cl != FedCells) issues.Add($"单元数不守恒：{cl} ≠ 喂入 {FedCells}");
        foreach (var u in Units)
        {
            if (u.GrossM3 < -1e-9) issues.Add($"{u.UnitId} 整格量为负（{u.GrossM3:0.###}）");
            if (u.NetM3 < -1e-9) issues.Add($"{u.UnitId} 修正后量为负（{u.NetM3:0.###}）");
            if (u.NetM3 > u.GrossM3 + 1e-6)
                issues.Add($"{u.UnitId} 修正后 {u.NetM3:0.###} > 整格 {u.GrossM3:0.###} —— 边界修正只能减不能加");
            if (Bad(u.GrossM3) || Bad(u.NetM3) || Bad(u.OutsideSeamM3))
                issues.Add($"{u.UnitId} 有非有限值");
        }
        if (Bad(UnassignedM3) || Bad(OverlapM3) || Bad(FedM3)) issues.Add("汇总量里有非有限值");
        // E1 在扫描层：煤 + 岩 = 全部，不重不漏
        double split = FedCoalM3 + FedRockM3;
        if (FedM3 > 1e-9 && Math.Abs(split - FedM3) / FedM3 > 1e-9)
            issues.Add($"煤岩分账不闭合：煤 {FedCoalM3:0.###} + 岩 {FedRockM3:0.###} = {split:0.###} ≠ 喂入 {FedM3:0.###}");
        // 煤单元里不许出现岩、反之亦然（Add 是按 Kind 配的，破了说明配对逻辑被改坏了）
        foreach (var u in Units)
            if (u.Kind == UnitKind.Coal && u.GrossM3 > FedCoalM3 + 1e-6)
                issues.Add($"{u.UnitId} 是煤单元，量 {u.GrossM3:0.###} 却超过了全部煤量 {FedCoalM3:0.###}");
        return issues.Count == 0;

        static bool Bad(double v) => double.IsNaN(v) || double.IsInfinity(v);
    }

    public string Text()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"单元 {Units.Count} 个 · 喂入 {FedM3:N0} m³ / {FedCells:N0} 格");
        sb.AppendLine($"整格合计 {TotalGrossM3:N0} m³ · 边界修正后 {TotalNetM3:N0} m³"
                    + $"（修正 {TotalGrossM3 - TotalNetM3:N0} m³，其中整格在层外的 {TotalOutsideSeamM3:N0} m³）");
        sb.AppendLine($"未归属 {UnassignedM3:N0} m³ / {UnassignedCells:N0} 格"
                    + (OverlapM3 > 1e-9 ? $" · 归属重叠 {OverlapM3:N0} m³ / {OverlapCells:N0} 格" : ""));
        var cn = CoarsenessNote();
        if (cn != null) sb.AppendLine(cn);
        foreach (var n in Notes) sb.AppendLine("· " + n);
        return sb.ToString();
    }
}

/// <summary>
/// <b>把扫描到的 cell 按采掘单元分箱</b>（规划 `docs/块体模型重规划_量源统一.md` 的 Phase 1）。
///
/// <para><b>它不自己扫块体</b>。挂在 <see cref="InclineVolumeEngine.BuildProfile"/> 的
/// <see cref="InclineVolumeEngine.CellSink"/> 上，吃的是<b>同一趟扫描</b>的 cell ——
/// 这正是 BM2「一趟扫描、多种分箱」的落地方式。另起一趟扫描迟早会与 <c>(k,g,u)</c> 桶漂开，
/// 而漂开之后没人看得出来（本仓库在"两套量各算各的"上已经付过一次代价）。</para>
///
/// <para><b>归属判据</b>与 <see cref="UnitGraphBuilder"/> 的占地测试同构：
/// 点到单元前脸轨的距离 ≤ 该单元的推进宽 <c>W</c>，且（给了推进方向时）在推进那一侧。
/// <b>⚠ 这里判的是"这个 cell 属于哪一幅"，与压覆判据判的"哪一幅压着哪一幅"是两件事</b> ——
/// 后者有已知缺陷（采前脸轨而不是占地，见 `UnitGraphOverburdenGeometryTests`），不要混为一谈。</para>
///
/// <para><b>BM1b</b>：煤单元在顶/底板处按 cell 与煤层的<b>重叠比</b>加权（<see cref="UnitVolume.NetM3"/>），
/// 岩单元按台阶的 <c>[ZLo, ZHi]</c> 加权。整格量 <see cref="UnitVolume.GrossM3"/> 同时保留，
/// 守恒对账用它 —— 否则守恒式左右两端口径不同，永远对不平。</para>
/// </summary>
public sealed class UnitVolumeBinner
{
    private readonly MineUnit[] _units;
    private readonly SeamSurfaces[] _seams;
    private readonly double _adx, _ady;
    private readonly bool _hasDir;
    private readonly UnitVolumeResult _res = new();
    private readonly UnitVolume[] _bins;

    public UnitVolumeResult Result => _res;

    /// <param name="units">采掘单元（煤 + 岩混在一起，与 <c>UnitPlanEngine</c> 同一份）。</param>
    /// <param name="seams">判煤用的顶/底板，索引与扫描侧的 <c>seam</c> 下标一致。BM1b 的煤侧修正要用。</param>
    /// <param name="advanceDx">推进方向（水平）。不给（0,0）则占地按<b>双侧走廊</b>判，偏保守 —— 与建图侧同一约定。</param>
    public UnitVolumeBinner(IReadOnlyList<MineUnit>? units, IReadOnlyList<SeamSurfaces>? seams,
                            double advanceDx = 0, double advanceDy = 0)
    {
        _units = (units ?? Array.Empty<MineUnit>()).Where(u => u != null && u.RailXy.Length >= 4).ToArray();
        _seams = (seams ?? Array.Empty<SeamSurfaces>()).ToArray();
        _hasDir = Math.Abs(advanceDx) + Math.Abs(advanceDy) > 1e-9;
        if (_hasDir)
        {
            double L = Math.Sqrt(advanceDx * advanceDx + advanceDy * advanceDy);
            _adx = advanceDx / L; _ady = advanceDy / L;
        }
        else
        {
            _res.Notes.Add("没给推进方向，占地按【双侧走廊】判（偏保守：可能多归属，不会漏）。");
        }
        _bins = new UnitVolume[_units.Length];
        for (int i = 0; i < _units.Length; i++)
        {
            _bins[i] = new UnitVolume { UnitId = _units[i].UnitId, Kind = _units[i].Kind };
            _res.Units.Add(_bins[i]);
        }
        if (_units.Length == 0) _res.Notes.Add("◆ 一个采掘单元都没有 —— 所有量都会记进【未归属】。");
    }

    /// <summary>
    /// 挂到 <c>BuildProfile</c> 的 cell 出口上。
    /// </summary>
    /// <param name="cz">单元中心标高。</param>
    /// <param name="czSize">该单元的 z 向尺寸（稠密 = 层高；八叉树叶块 = 边长）。BM1b 的重叠比要用。</param>
    /// <param name="vol">原位实方 m³（叶块已按细格数加权）。</param>
    /// <param name="seam">判为第几层煤；<b>−1 = 岩</b>。</param>
    /// <summary>
    /// 这个 cell 归哪一个单元 —— 返回下标，−1 = 没归属上。<paramref name="hits"/> 是命中了几个单元。
    ///
    /// <para><b>公开是为了让"按单元删格 / 按单元着色"这类下游用【同一套】判据</b>，
    /// 而不是各自再写一遍占地测试。归属口径只能有一处（BM2 的同一条理由）。</para>
    /// </summary>
    public int Resolve(double cx, double cy, double cz, double czSize, int seam, out int hits)
    {
        hits = 0;
        int hit = -1;
        for (int i = 0; i < _units.Length; i++)
        {
            var u = _units[i];
            // 煤 cell 只找煤单元、岩 cell 只找岩单元 —— 混着找会让同一方土在两本账里各记一遍
            if (seam >= 0 ? u.Kind != UnitKind.Coal : u.Kind != UnitKind.Rock) continue;
            if (!InFootprint(cx, cy, u)) continue;
            if (!InZ(cz, czSize, u, seam)) continue;
            hits++;
            if (hit < 0) hit = i;
        }
        return hit;
    }

    /// <summary>归属到的单元 Id（没归属上给 null）。</summary>
    public string? ResolveId(double cx, double cy, double cz, double czSize, int seam)
    {
        int i = Resolve(cx, cy, cz, czSize, seam, out _);
        return i >= 0 ? _units[i].UnitId : null;
    }

    public void Add(double cx, double cy, double cz, double czSize, double vol, int seam)
    {
        _res.FedM3 += vol; _res.FedCells++;
        if (seam >= 0) _res.FedCoalM3 += vol; else _res.FedRockM3 += vol;

        int hit = Resolve(cx, cy, cz, czSize, seam, out int hits);

        if (hit < 0) { _res.UnassignedM3 += vol; _res.UnassignedCells++; return; }
        if (hits > 1) { _res.OverlapM3 += vol; _res.OverlapCells++; }

        var b = _bins[hit];
        b.GrossM3 += vol; b.Cells++;

        double frac = Fraction(cx, cy, cz, czSize, _units[hit], seam);
        b.NetM3 += vol * frac;
        if (frac <= 1e-12) b.OutsideSeamM3 += vol;
        else if (frac < 1 - 1e-12) { b.StraddleM3 += vol; b.StraddleCells++; }
    }

    // ── 归属判据 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 点在不在单元的占地里。
    ///
    /// <para><b>⚠ 与 <c>UnitGraphBuilder.InFootprint</c> 的关键差别：这里<b>沿走向设界</b>。</b>
    /// 建图侧只判「到前脸轨的距离 ≤ W」，而相邻分幅的轨是<b>共线首尾相接</b>的 ——
    /// 于是接缝附近 ±W 的点<b>同时</b>落进两幅。分幅 100m、W=40m 时，一幅有八成长度
    /// 处在某个接缝的 40m 内：实测<b>归属重叠占到 41%</b>（208,000 / 502,500 m³）。
    /// 归属一旦有 41% 是模棱两可的，"哪一幅有多少量"就变成了看谁先命中，
    /// <b>而守恒式照样成立</b>（每个 cell 仍只记一次）。</para>
    ///
    /// <para>做法：投影参数被<b>钳到端点之外</b>的段不算命中（沿走向的越界量 &gt; 0 就否）。
    /// 于是占地成为一个<b>沿走向封口</b>的棱柱，相邻幅只在接缝那条零测度线上重叠。</para>
    /// </summary>
    private bool InFootprint(double px, double py, MineUnit u)
    {
        double w = u.WidthM > 1e-6 ? u.WidthM : 40.0;
        // 包围盒粗筛（与建图侧同一约定：轨的范围各向外胀一个 W）
        if (px < u.MinX || px > u.MaxX || py < u.MinY || py > u.MaxY) return false;
        double bestD2 = double.MaxValue, bvx = 0, bvy = 0;
        bool any = false;
        int segs = u.RailXy.Length / 2 - 1;
        for (int s = 0; s < segs; s++)
        {
            double x0 = u.RailXy[s * 2], y0 = u.RailXy[s * 2 + 1];
            double x1 = u.RailXy[s * 2 + 2], y1 = u.RailXy[s * 2 + 3];
            double dx = x1 - x0, dy = y1 - y0, L2 = dx * dx + dy * dy;
            if (L2 <= 1e-12) continue;
            double t = ((px - x0) * dx + (py - y0) * dy) / L2;
            if (t < -1e-12 || t > 1 + 1e-12) continue;          // ★ 沿走向越界 = 不是这一幅的地
            double qx = x0 + dx * t, qy = y0 + dy * t;
            double vx = px - qx, vy = py - qy, d2 = vx * vx + vy * vy;
            if (d2 < bestD2) { bestD2 = d2; bvx = vx; bvy = vy; any = true; }
        }
        if (!any || bestD2 > w * w) return false;
        if (!_hasDir) return true;
        return bvx * _adx + bvy * _ady >= -1e-6;
    }

    /// <summary>
    /// z 向是否够得着：要求与单元的 <c>[ZLo, ZHi]</c> 有<b>严格为正</b>的重叠，
    /// 量的多少交给 <see cref="Fraction"/>。
    ///
    /// <para><b>⚠「碰到边界」不算落在这个单元里。</b>第一版写成
    /// <c>cz+h &gt; ZLo−1e-9 &amp;&amp; cz−h &lt; ZHi+1e-9</c>，于是每一个<b>正好贴着台阶底/顶</b>的 cell
    /// 都算命中相邻那一级 —— 而 cell 边界（5 的倍数）与台阶边界（同样是 5 的倍数）<b>天天相切</b>。
    /// 后果：整格量虚高、<c>OutsideSeamM3</c> 里堆了 217,500 m³ 零贡献的格、归属重叠 2,356 格，
    /// <b>而守恒式照样成立</b>（那些格的量确实被记了一次）—— 典型的"每一项校核都是 ✓"。</para>
    /// </summary>
    private bool InZ(double cz, double czSize, MineUnit u, int seam)
    {
        double h = Math.Max(0, czSize) * 0.5;
        double ov = Math.Min(cz + h, u.ZHi) - Math.Max(cz - h, u.ZLo);
        return ov > 1e-9;
    }

    /// <summary>
    /// BM1b 的重叠比：cell 的 z 区间与「煤层（煤单元，按顶底板逐点采）/ 台阶（岩单元）」的重叠占比。
    /// </summary>
    private double Fraction(double cx, double cy, double cz, double czSize, MineUnit u, int seam)
    {
        double h = Math.Max(1e-9, czSize) * 0.5;
        double lo = cz - h, hi = cz + h;
        double bLo = u.ZLo, bHi = u.ZHi;
        if (seam >= 0 && seam < _seams.Length)
        {
            var s = _seams[seam];
            // 倾斜煤层下单元的 ZLo/ZHi 只是坡顶线处的值，逐点采才是这一 cell 真正压着的那段煤
            if (s?.Floor != null && s.Roof != null &&
                s.Floor.TrySampleZ(cx, cy, out double f) && s.Roof.TrySampleZ(cx, cy, out double r) && r > f)
            { bLo = f; bHi = r; }
        }
        double ov = Math.Min(hi, bHi) - Math.Max(lo, bLo);
        if (ov <= 0) return 0;
        return Math.Min(1.0, ov / (2 * h));
    }
}
