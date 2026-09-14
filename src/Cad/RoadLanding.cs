using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>坑线落地的设计参数（移植原 <c>MineAssLib.Models.RampDesignParams</c>，缺省值同原版）。</summary>
public sealed class RampDesignParams
{
    public bool   IsDump         { get; set; } = false;
    public double RoadWidth      { get; set; } = 6.0;
    public double MaxGradePct    { get; set; } = 8.0;
    public double MinTurnRadius  { get; set; } = 15.0;
    public double CurveGradePct  { get; set; } = 4.0;
    public double SuperElevPct   { get; set; } = 6.0;
    /// <summary>弯道加宽触发半径 m；0 = 不加宽。</summary>
    public double CurveWiden     { get; set; } = 0.0;
    public double BermHeight     { get; set; } = 1.5;
    /// <summary>挖方边坡坡角 °。</summary>
    public double CutSlopeDeg    { get; set; } = 45.0;
    /// <summary>填方边坡坡角 °（≈ 1:1.5）。</summary>
    public double FillSlopeDeg   { get; set; } = 33.7;
    public int    LaneCount      { get; set; } = 2;
    public double WheelbaseM     { get; set; } = 5.5;
    public double DesignSpeedKmh { get; set; } = 25.0;
}

/// <summary>贴面剖面的记账 —— 每一条都要能在命令行看到，不许静默。</summary>
public sealed class RoadProfileResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    /// <summary>最终中线，扁平 [x,y,z,...]（XY 与输入一致，只改 Z）。</summary>
    public double[] Centerline { get; set; } = Array.Empty<double>();

    /// <summary>站点数 / 采到面的站点数 / 采不到的站点数（采不到<b>不外推</b>，只记账）。</summary>
    public int Stations, Sampled, Missed;
    /// <summary>被限坡拉离地面的最大偏差 m（正 = 路在地面之上 = 填方，负 = 挖方）。</summary>
    public double MaxDeviationM;
    /// <summary>最终逐段纵坡的最大值 %（必须 ≤ i_max）。</summary>
    public double MaxGradePct;
    /// <summary>限坡把 Z 从地面拉开的站点数。</summary>
    public int LimitedStations;
    /// <summary>首末两点的高差与水平长在 i_max 下是否可达（不可达 = 这两点本身就选错了）。</summary>
    public bool EndpointsFeasible = true;
    public double RequiredGradePct;

    public List<string> Diagnostics { get; } = new();
}

/// <summary>
/// 把中线<b>贴到现状面上</b>再按限坡整平（移植原 <c>MineAssLib.RoadLayout.RoadSurfaceProfiler</c>，逐行照移；
/// 采样器换成本仓道路域的 <see cref="IRoadZSampler"/>）。
///
/// <para>
/// <b>为什么不是直接照抄地面高程</b>：路面必须是等纵坡的，照抄地面就成了过山车，车根本跑不了。
/// 所以是"贴面 + 限坡"两步：先按地面取目标高程，再把整条剖面投影到"逐段坡度 ≤ i_max"的可行集里，
/// <b>冲突时限坡优先</b>，并如实报出被拉离地面多少。
/// </para>
/// <para>
/// <b>首末点锚死</b>：若这两点之间在 i_max 下根本到不了，那是选点本身的问题，单独报出来，
/// <b>不偷偷放宽坡度</b>。
/// </para>
/// <para>
/// <b>一处措辞纠正（登记）</b>：原版在首末不可达时写"只保证逐段不超坡" —— 可首末锚死 + Δz &gt; i·S 时
/// 至少有一段必然超坡（数学上不可能两全），实际是超坡集中在端点附近。算法照原样，只把这句改成实话。
/// </para>
/// </summary>
public static class RoadSurfaceProfiler
{
    /// <summary>沿线加密的目标站距 m —— 站距太大，贴面就贴不出地形起伏。</summary>
    public const double StationStepM = 5.0;
    /// <summary>站点数上限（防几公里长线爆点）。</summary>
    public const int MaxStations = 4000;

    public static RoadProfileResult Fit(double[]? pickedXyz, IRoadZSampler? sampler, double maxGradePct)
    {
        var r = new RoadProfileResult();
        if (pickedXyz == null || pickedXyz.Length < 6)
        { r.Error = "中线点不足 2"; return r; }
        if (sampler == null)
        { r.Error = "没有现状面可采（未指定面）"; return r; }

        double i = Math.Max(0.1, maxGradePct) / 100.0;

        // ① 沿折线按站距加密（XY 弧长均匀），Z 先留空
        var xs = new List<double>(); var ys = new List<double>(); var ss = new List<double>();
        int np = pickedXyz.Length / 3;
        double acc = 0;
        xs.Add(pickedXyz[0]); ys.Add(pickedXyz[1]); ss.Add(0);
        for (int k = 1; k < np; k++)
        {
            double x0 = pickedXyz[(k - 1) * 3], y0 = pickedXyz[(k - 1) * 3 + 1];
            double x1 = pickedXyz[k * 3],       y1 = pickedXyz[k * 3 + 1];
            double seg = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int sub = Math.Max(1, (int)Math.Ceiling(seg / StationStepM));
            for (int m = 1; m <= sub; m++)
            {
                double t = (double)m / sub;
                xs.Add(x0 + t * (x1 - x0)); ys.Add(y0 + t * (y1 - y0));
                ss.Add(acc + seg * t);
            }
            acc += seg;
        }
        if (xs.Count > MaxStations)
        {
            int stride = (int)Math.Ceiling(xs.Count / (double)MaxStations);
            var nx2 = new List<double>(); var ny2 = new List<double>(); var ns2 = new List<double>();
            for (int k = 0; k < xs.Count; k += stride) { nx2.Add(xs[k]); ny2.Add(ys[k]); ns2.Add(ss[k]); }
            if (ns2[^1] < ss[^1]) { nx2.Add(xs[^1]); ny2.Add(ys[^1]); ns2.Add(ss[^1]); }
            xs = nx2; ys = ny2; ss = ns2;
            r.Diagnostics.Add($"中线过长，站点抽稀到 {xs.Count} 个（站距≈{ss[^1] / Math.Max(1, xs.Count - 1):0.#}m）。");
        }
        int n = xs.Count;
        double S = ss[n - 1];
        if (S < 1e-6) { r.Error = "中线水平长≈0"; return r; }

        // ② 逐站采面。采不到的**不外推**，先留 NaN，最后按两侧已采到的站线性插值补（并计数）。
        var ground = new double[n];
        var got = new bool[n];
        for (int k = 0; k < n; k++)
        {
            got[k] = sampler.TrySample(xs[k], ys[k], out double gz);
            ground[k] = got[k] ? gz : double.NaN;
            if (got[k]) r.Sampled++; else r.Missed++;
        }
        r.Stations = n;
        if (r.Sampled < 2)
        { r.Error = $"{n} 个站点里只有 {r.Sampled} 个落在面上 —— 这条线基本不在这张面的范围内"; return r; }

        double zStart = got[0]     ? ground[0]     : pickedXyz[2];
        double zEnd   = got[n - 1] ? ground[n - 1] : pickedXyz[^1];
        ground[0] = zStart; ground[n - 1] = zEnd; got[0] = got[n - 1] = true;

        for (int k = 0; k < n; k++)
        {
            if (got[k]) continue;
            int a = k; while (a >= 0 && !got[a]) a--;
            int b = k; while (b < n && !got[b]) b++;
            double t = (ss[k] - ss[a]) / Math.Max(1e-9, ss[b] - ss[a]);
            ground[k] = ground[a] + t * (ground[b] - ground[a]);
        }

        // ③ 首末点可达性：|Δz| ≤ i·S，否则这两点本身就选错了（不偷偷放宽坡度）
        r.RequiredGradePct = Math.Abs(zEnd - zStart) / S * 100.0;
        r.EndpointsFeasible = Math.Abs(zEnd - zStart) <= i * S + 1e-9;

        // ④ 投影到可行集：先夹进"两端可达锥"，再交替前/后向夹逼到逐段坡度 ≤ i
        var z = new double[n];
        for (int k = 0; k < n; k++)
        {
            double lo = Math.Max(zStart - i * ss[k], zEnd - i * (S - ss[k]));
            double hi = Math.Min(zStart + i * ss[k], zEnd + i * (S - ss[k]));
            if (!r.EndpointsFeasible) { lo = double.MinValue; hi = double.MaxValue; }
            z[k] = Math.Min(hi, Math.Max(lo, ground[k]));
        }
        z[0] = zStart; z[n - 1] = zEnd;
        for (int pass = 0; pass < 64; pass++)
        {
            double worst = 0;
            for (int k = 1; k < n - 1; k++)
            {
                double ds = ss[k] - ss[k - 1];
                double up = z[k - 1] + i * ds, dn = z[k - 1] - i * ds;
                double nz = Math.Min(up, Math.Max(dn, z[k]));
                worst = Math.Max(worst, Math.Abs(nz - z[k])); z[k] = nz;
            }
            for (int k = n - 2; k >= 1; k--)
            {
                double ds = ss[k + 1] - ss[k];
                double up = z[k + 1] + i * ds, dn = z[k + 1] - i * ds;
                double nz = Math.Min(up, Math.Max(dn, z[k]));
                worst = Math.Max(worst, Math.Abs(nz - z[k])); z[k] = nz;
            }
            if (worst < 1e-7) break;
        }

        // ⑤ 记账
        double maxG = 0, maxDev = 0;
        for (int k = 1; k < n; k++)
        {
            double ds = ss[k] - ss[k - 1];
            if (ds > 1e-9) maxG = Math.Max(maxG, Math.Abs(z[k] - z[k - 1]) / ds * 100.0);
        }
        for (int k = 0; k < n; k++)
        {
            double dev = z[k] - ground[k];
            if (Math.Abs(dev) > Math.Abs(maxDev)) maxDev = dev;
            if (Math.Abs(dev) > 0.05) r.LimitedStations++;
        }
        r.MaxGradePct = maxG;
        r.MaxDeviationM = maxDev;

        var outXyz = new double[n * 3];
        for (int k = 0; k < n; k++) { outXyz[k * 3] = xs[k]; outXyz[k * 3 + 1] = ys[k]; outXyz[k * 3 + 2] = z[k]; }
        r.Centerline = outXyz;
        r.Ok = true;

        r.Diagnostics.Add($"贴面：{n} 站（站距≈{S / Math.Max(1, n - 1):0.#}m），采到 {r.Sampled}、采不到 {r.Missed}"
                        + (r.Missed > 0 ? "（采不到的按两侧线性过渡，不外推、不当 0）" : ""));
        r.Diagnostics.Add($"限坡：i_max {maxGradePct:0.#}% → 实达 {maxG:0.##}%；"
                        + $"{r.LimitedStations}/{n} 站被拉离地面，最大偏差 {maxDev:+0.##;-0.##;0} m"
                        + (maxDev > 0 ? "（正 = 路在地面之上 = 填方）" : maxDev < 0 ? "（负 = 路在地面之下 = 挖方）" : ""));
        if (!r.EndpointsFeasible)
            r.Diagnostics.Add($"⚠ 首末两点高差 {Math.Abs(zEnd - zStart):0.#}m / 水平长 {S:0.#}m "
                            + $"= 需要 {r.RequiredGradePct:0.##}% > i_max {maxGradePct:0.#}% —— "
                            + "这两点之间**再怎么绕都不可能**满足限坡：要么把线拉长（多点绕行/折返），要么换起终点。"
                            + "本次按可达锥放开，首末仍锚在拾取标高 —— 于是**超坡必然集中在端点附近那几段**（实达见上一行），"
                            + "这不是算法错，是这两点本身就选错了。");
        return r;
    }
}

/// <summary>路面对现状面的挖填方分账。三个量各有各的口径，<b>不许合并成一个"土方量"</b>。</summary>
public sealed class CutFillResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    /// <summary>挖方 m³ —— 路面低于地面的那部分体积。</summary>
    public double CutM3;
    /// <summary>填方 m³ —— 路面高于地面的那部分体积。</summary>
    public double FillM3;
    /// <summary>路面足迹的平面投影面积 m²（参与积分的那部分）。</summary>
    public double FootprintM2;
    /// <summary>足迹里落在面外/空洞、没法计量的面积 m²。<b>单列，不摊进挖填方</b>。</summary>
    public double UncoveredM2;
    public double CellM;

    public string Describe() =>
        $"挖方 {CutM3:N0} m³ / 填方 {FillM3:N0} m³ / 足迹 {FootprintM2:N0} m²"
      + (UncoveredM2 > 0.5 ? $"；另有 {UncoveredM2:N0} m² 足迹不在面上，未计量（不摊进挖填方）" : "")
      + $"（格 {CellM:0.##}m）";
}

/// <summary>
/// 路面 ∩ 现状面 的挖填方（移植原 <c>RoadCutFillCalculator</c>）。
/// 方法：把路面三角网栅格化成"每格一个路面高程"，同格采现状面高程，逐格算 (z_road − z_ground)·A。
/// <para><b>为什么不做布尔求交</b>：要的是<b>数</b>，不是新的几何体。栅格积分误差随格边线性收敛、
/// 每一格都能单独解释；布尔求交在实测面上退化成一堆碎片，还得再判水密。</para>
/// <para><b>格外的面不摊进来</b>：足迹里没有现状面的部分单列成 UncoveredM2 —— 摊进去就是拿
/// 猜出来的地面高程算方量，那个数会一路进台账。</para>
/// </summary>
public static class RoadCutFillCalculator
{
    public const double DefaultCellM = 1.0;

    public static CutFillResult Compute(double[]? deckVerts, int[]? deckTris,
                                        IRoadZSampler? ground, double cellM = DefaultCellM)
    {
        var r = new CutFillResult { CellM = cellM };
        if (ground == null) { r.Error = "没有现状面可对照（未指定面）"; return r; }
        if (deckVerts == null || deckTris == null || deckVerts.Length < 9 || deckTris.Length < 3)
        { r.Error = "路面几何为空"; return r; }
        if (cellM <= 1e-3) { r.Error = "积分格边必须 > 0"; return r; }

        var deck = MeshZSampler.Build(deckVerts, deckTris);
        if (deck == null) { r.Error = "路面几何退化（顶点共线或包围盒为零）"; return r; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 2 < deckVerts.Length; i += 3)
        {
            if (deckVerts[i]     < minX) minX = deckVerts[i];     if (deckVerts[i]     > maxX) maxX = deckVerts[i];
            if (deckVerts[i + 1] < minY) minY = deckVerts[i + 1]; if (deckVerts[i + 1] > maxY) maxY = deckVerts[i + 1];
        }
        double a = cellM * cellM;
        for (double y = minY + cellM * 0.5; y <= maxY; y += cellM)
            for (double x = minX + cellM * 0.5; x <= maxX; x += cellM)
            {
                if (!deck.TrySample(x, y, out double zr)) continue;
                r.FootprintM2 += a;
                if (!ground.TrySample(x, y, out double zg)) { r.UncoveredM2 += a; continue; }
                double dz = zr - zg;
                if (dz > 0) r.FillM3 += dz * a; else r.CutM3 += -dz * a;
            }
        r.Ok = true;
        return r;
    }
}

/// <summary>走廊放样的产物：路面 + 挖方边坡 + 填方边坡（各自一张网，各自可撤）。</summary>
public sealed class RoadCorridorResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    public List<(double x, double y, double z)> DeckVerts { get; } = new();
    public List<(int a, int b, int c)> DeckTris { get; } = new();
    public List<(double x, double y, double z)> CutVerts { get; } = new();
    public List<(int a, int b, int c)> CutTris { get; } = new();
    public List<(double x, double y, double z)> FillVerts { get; } = new();
    public List<(int a, int b, int c)> FillTris { get; } = new();

    public List<(double x, double y, double z)> LeftEdge { get; } = new();
    public List<(double x, double y, double z)> RightEdge { get; } = new();

    /// <summary>边坡找不到落地点（坡脚/坡顶超出采样面或超过搜索上限）的站数。<b>不外推</b>，那些站不出边坡。</summary>
    public int SlopeMissed;
    public int Stations;
    public double MaxCutHeightM, MaxFillHeightM;

    /// <summary>扁平化路面顶点（挖填方计算用）。</summary>
    public double[] DeckFlat() { var v = new double[DeckVerts.Count * 3]; for (int i = 0; i < DeckVerts.Count; i++) { v[i * 3] = DeckVerts[i].x; v[i * 3 + 1] = DeckVerts[i].y; v[i * 3 + 2] = DeckVerts[i].z; } return v; }
    public int[] DeckTrisFlat() { var t = new int[DeckTris.Count * 3]; for (int i = 0; i < DeckTris.Count; i++) { t[i * 3] = DeckTris[i].a; t[i * 3 + 1] = DeckTris[i].b; t[i * 3 + 2] = DeckTris[i].c; } return t; }
}

/// <summary>
/// 走廊放样（托管实现，替代原版内核 <c>IPitDesignCapability</c> 的"逐 O 点贴帮切进单面坡道 + 挖填方边坡"）。
///
/// <para>
/// 中线逐站 + 该站路宽 → 左右路肩点 → 路面网；路肩之外按挖/填坡角向外放坡，
/// 直到与采样面相交（坡脚 / 坡顶落地点，二分求交）→ 边坡网。
/// </para>
///
/// ── 与原版内核的差异（登记）──
/// <list type="bullet">
///   <item><b>不切原坡面网</b>：原版内核会把被切面（bench face mesh）沿路面足迹切开；这里生成
///     <b>独立叠加实体</b>（路面 / 挖方边坡 / 填方边坡各一张），原网不动 —— 与 §三四二 煤层露头着色
///     的处置相同。好处是逐段可撤、不会切坏别的面；代价是路面与原坡面在视觉上重叠，
///     以及"被切面的挖方体"不会从原网上消失。</item>
///   <item>边坡落地点找不到（超出采样面范围、或放坡到搜索上限仍不相交）的站<b>不出边坡</b>并计数，
///     不按"最后一站的高度"外推。</item>
///   <item>超高（横坡）只体现在左右路肩的高差上；不做路拱。</item>
/// </list>
/// </summary>
public static class RoadCorridorLofter
{
    /// <summary>边坡向外搜索的最大水平距离 m（防止在悬空处无限放坡）。</summary>
    public const double MaxSlopeReachM = 120.0;

    /// <param name="centerXyz">贴面后的中线 [x,y,z,...]。</param>
    /// <param name="widthPerStation">逐站路宽；null 或长度不符时用 <paramref name="baseWidth"/>。</param>
    /// <param name="superPctPerStation">逐站超高 %（外侧抬高）；null = 0。</param>
    /// <param name="ground">采样面；null = 只出路面，不出边坡。</param>
    public static RoadCorridorResult Loft(double[]? centerXyz, double baseWidth,
                                          double[]? widthPerStation, double[]? superPctPerStation,
                                          IRoadZSampler? ground, double cutSlopeDeg, double fillSlopeDeg)
    {
        var res = new RoadCorridorResult();
        if (centerXyz == null || centerXyz.Length < 6) { res.Error = "中线点不足 2"; return res; }
        if (baseWidth <= 1e-6) { res.Error = "路宽必须 > 0"; return res; }

        int n = centerXyz.Length / 3;
        res.Stations = n;
        var cx = new double[n]; var cy = new double[n]; var cz = new double[n];
        for (int k = 0; k < n; k++) { cx[k] = centerXyz[k * 3]; cy[k] = centerXyz[k * 3 + 1]; cz[k] = centerXyz[k * 3 + 2]; }

        // ── 逐站法向（左侧为正）──
        var nx = new double[n]; var ny = new double[n];
        for (int k = 0; k < n; k++)
        {
            int a = Math.Max(0, k - 1), b = Math.Min(n - 1, k + 1);
            double dx = cx[b] - cx[a], dy = cy[b] - cy[a];
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { nx[k] = k > 0 ? nx[k - 1] : 0; ny[k] = k > 0 ? ny[k - 1] : 1; continue; }
            nx[k] = -dy / len; ny[k] = dx / len;
        }

        // ── 路面：左右路肩点 ──
        for (int k = 0; k < n; k++)
        {
            double w = widthPerStation != null && widthPerStation.Length == n && widthPerStation[k] > 1e-6 ? widthPerStation[k] : baseWidth;
            double half = w * 0.5;
            double e = superPctPerStation != null && superPctPerStation.Length == n ? superPctPerStation[k] / 100.0 : 0;
            // 超高：外侧抬高 e·w/2，内侧压低 —— 哪侧是外侧由转向定（左转 ⇒ 右侧为外）
            double turn = 0;
            if (k > 0 && k < n - 1)
            {
                double ax = cx[k] - cx[k - 1], ay = cy[k] - cy[k - 1];
                double bx = cx[k + 1] - cx[k], by = cy[k + 1] - cy[k];
                turn = ax * by - ay * bx;      // >0 左转
            }
            double dzL = turn > 0 ? -e * half : e * half;
            double dzR = -dzL;
            res.LeftEdge.Add((cx[k] + nx[k] * half, cy[k] + ny[k] * half, cz[k] + dzL));
            res.RightEdge.Add((cx[k] - nx[k] * half, cy[k] - ny[k] * half, cz[k] + dzR));
        }
        for (int k = 0; k < n; k++)
        {
            res.DeckVerts.Add(res.LeftEdge[k]);
            res.DeckVerts.Add(res.RightEdge[k]);
        }
        for (int k = 0; k + 1 < n; k++)
        {
            int l0 = k * 2, r0 = k * 2 + 1, l1 = (k + 1) * 2, r1 = (k + 1) * 2 + 1;
            res.DeckTris.Add((l0, r0, l1));
            res.DeckTris.Add((r0, r1, l1));
        }

        if (ground == null) { res.Ok = true; return res; }

        // ── 边坡：路肩之外按坡角放坡到与采样面相交 ──
        double tanCut = Math.Tan(Math.Clamp(cutSlopeDeg, 5, 89) * Math.PI / 180.0);
        double tanFill = Math.Tan(Math.Clamp(fillSlopeDeg, 5, 89) * Math.PI / 180.0);

        var leftFoot = new (double x, double y, double z)?[n];
        var rightFoot = new (double x, double y, double z)?[n];
        var leftCut = new bool[n]; var rightCut = new bool[n];
        for (int k = 0; k < n; k++)
        {
            leftFoot[k] = Daylight(res.LeftEdge[k], nx[k], ny[k], ground, tanCut, tanFill, out leftCut[k]);
            rightFoot[k] = Daylight(res.RightEdge[k], -nx[k], -ny[k], ground, tanCut, tanFill, out rightCut[k]);
            if (leftFoot[k] == null) res.SlopeMissed++;
            if (rightFoot[k] == null) res.SlopeMissed++;
            if (leftFoot[k] is { } lf) Track(res, lf.z - res.LeftEdge[k].z);
            if (rightFoot[k] is { } rf) Track(res, rf.z - res.RightEdge[k].z);
        }

        // 相邻两站都有落地点且同为挖/同为填 ⇒ 出一片四边形（两三角）
        for (int k = 0; k + 1 < n; k++)
        {
            AddSlopeQuad(res, res.LeftEdge[k], res.LeftEdge[k + 1], leftFoot[k], leftFoot[k + 1], leftCut[k], leftCut[k + 1]);
            AddSlopeQuad(res, res.RightEdge[k], res.RightEdge[k + 1], rightFoot[k], rightFoot[k + 1], rightCut[k], rightCut[k + 1]);
        }

        res.Ok = true;
        return res;
    }

    private static void Track(RoadCorridorResult res, double dz)
    {
        if (dz > 0) res.MaxCutHeightM = Math.Max(res.MaxCutHeightM, dz);     // 地面在路肩之上 ⇒ 挖
        else res.MaxFillHeightM = Math.Max(res.MaxFillHeightM, -dz);        // 地面在路肩之下 ⇒ 填
    }

    /// <summary>
    /// 从路肩沿外法向放坡，找与采样面的交点。地面高于路肩 ⇒ 挖方坡（向上放），反之填方坡（向下放）。
    /// 搜索到 <see cref="MaxSlopeReachM"/> 仍不相交 ⇒ 返回 null（<b>不外推</b>）。
    /// </summary>
    private static (double x, double y, double z)? Daylight((double x, double y, double z) edge, double dx, double dy,
                                                            IRoadZSampler ground, double tanCut, double tanFill, out bool isCut)
    {
        isCut = false;
        if (!ground.TrySample(edge.x, edge.y, out double g0)) return null;
        isCut = g0 > edge.z;
        double tan = isCut ? tanCut : tanFill;
        double sign = isCut ? +1 : -1;

        // f(d) = (edge.z + sign·tan·d) − ground(d)：路肩处 f 与 sign 反号，向外走到 f 变号处即交点
        double step = 0.5;
        double dPrev = 0, fPrev = (edge.z) - g0;
        for (double d = step; d <= MaxSlopeReachM; d += step)
        {
            if (!ground.TrySample(edge.x + dx * d, edge.y + dy * d, out double g)) return null;
            double f = (edge.z + sign * tan * d) - g;
            if (Math.Sign(f) != Math.Sign(fPrev) || Math.Abs(f) < 1e-9)
            {
                // 二分细化
                double lo = dPrev, hi = d;
                for (int it = 0; it < 24; it++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (!ground.TrySample(edge.x + dx * mid, edge.y + dy * mid, out double gm)) break;
                    double fm = (edge.z + sign * tan * mid) - gm;
                    if (Math.Sign(fm) == Math.Sign(fPrev)) lo = mid; else hi = mid;
                }
                double dd = (lo + hi) * 0.5;
                return (edge.x + dx * dd, edge.y + dy * dd, edge.z + sign * tan * dd);
            }
            dPrev = d; fPrev = f;
        }
        return null;
    }

    private static void AddSlopeQuad(RoadCorridorResult res,
                                     (double x, double y, double z) e0, (double x, double y, double z) e1,
                                     (double x, double y, double z)? f0, (double x, double y, double z)? f1,
                                     bool cut0, bool cut1)
    {
        if (f0 == null || f1 == null || cut0 != cut1) return;
        var verts = cut0 ? res.CutVerts : res.FillVerts;
        var tris = cut0 ? res.CutTris : res.FillTris;
        int b = verts.Count;
        verts.Add(e0); verts.Add(e1); verts.Add(f0.Value); verts.Add(f1.Value);
        tris.Add((b, b + 1, b + 2));
        tris.Add((b + 1, b + 3, b + 2));
    }
}

/// <summary>平盘联络道的解：起坡点处的坡面走向、台阶高差、展线长与中线。</summary>
public sealed class BenchConnectorResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    /// <summary>该处坡面的台阶高差 m（起坡点到下平盘）。</summary>
    public double BenchHeightM { get; set; }
    /// <summary>按纵坡解出的展线长 m = H ÷ i。</summary>
    public double LengthM { get; set; }
    /// <summary>最陡下降方向（单位向量）。</summary>
    public double DownX { get; set; }
    public double DownY { get; set; }
    /// <summary>斜矩形路面的中线 [x,y,z,...]（从起坡点沿平盘走向、一边走一边下到下平盘）。</summary>
    public double[] Centerline { get; set; } = Array.Empty<double>();
}

/// <summary>
/// 平盘联络道求解（原版内核"按该处台阶段 + 纵坡解出斜矩形路面"的托管等价）。
/// <para>
/// 在坡面上点一下 → 探该处最陡下降方向 → 沿它走到坡度变缓即下平盘，得台阶高差 H →
/// 展线长 L = H ÷ i → 中线沿平盘走向铺 L、同时斜着切过坡面下到下平盘（镜像取另一侧）。
/// 点在平盘上（近乎水平）或沿最陡方向解不出高差时<b>如实报错</b>，不编一个台阶高。
/// </para>
/// </summary>
public static class BenchConnectorSolver
{
    /// <summary>最陡方向探测步长 m。</summary>
    public const double ProbeM = 2.0;
    /// <summary>向下探台阶高差的最远距离 m。</summary>
    public const double ReachMaxM = 200.0;

    public static BenchConnectorResult Solve(IRoadZSampler? ground, double px, double py, double z0,
                                             double gradePct, bool mirror)
    {
        var r = new BenchConnectorResult();
        if (ground == null) { r.Error = "没有坡面可采（未指定面）。"; return r; }
        if (gradePct <= 1e-6) { r.Error = "纵坡必须 > 0。"; return r; }

        // ① 最陡下降方向：16 个方向探 ProbeM
        double best = 0, bdx = 0, bdy = 1;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8, dx = Math.Cos(a), dy = Math.Sin(a);
            if (!ground.TrySample(px + dx * ProbeM, py + dy * ProbeM, out double z1)) continue;
            double s = (z0 - z1) / ProbeM;
            if (s > best) { best = s; bdx = dx; bdy = dy; }
        }
        if (best < 1e-3) { r.Error = "该处坡面近乎水平，解不出台阶段（请点在坡面上，不是平盘上）。"; return r; }
        r.DownX = bdx; r.DownY = bdy;

        // ② 沿最陡方向往下走到坡度变缓（下平盘）：取高差最大处
        double H = 0, reach = 0;
        for (double d = 1; d <= ReachMaxM; d += 1)
        {
            if (!ground.TrySample(px + bdx * d, py + bdy * d, out double zd)) break;
            double drop = z0 - zd;
            if (drop > H + 1e-6) { H = drop; reach = d; }
            else if (d > 3) break;
        }
        if (H < 0.5) { r.Error = "沿最陡方向解不出台阶高差。"; return r; }
        r.BenchHeightM = H;

        // ③ 展线长 + 中线：沿平盘走向（垂直于最陡方向）铺 L，同时斜着往下平盘偏
        double L = H / (gradePct / 100.0);
        r.LengthM = L;
        double tx = -bdy, ty = bdx;
        if (mirror) { tx = -tx; ty = -ty; }
        int n = Math.Max(2, (int)Math.Ceiling(L / 5.0) + 1);
        var c = new double[n * 3];
        for (int k = 0; k < n; k++)
        {
            double t = (double)k / (n - 1);
            c[k * 3] = px + tx * L * t + bdx * reach * t;
            c[k * 3 + 1] = py + ty * L * t + bdy * reach * t;
            c[k * 3 + 2] = z0 - H * t;
        }
        r.Centerline = c;
        r.Ok = true;
        return r;
    }
}
