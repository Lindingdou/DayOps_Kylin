// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimFlowStage.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  车流示意 —— 让运输线上真的有车在跑
//
//  ── 这是「示意」，不是「逐车仿真」。差别写在最前面，因为它决定了每一条公式 ──
//  逐车仿真（<see cref="TripAnimator"/> 干的事）：每台车有编号，装—运—卸—返四段带排队，
//  能演出「MF<1 铲等车 / MF>1 车排队」。它只在**车次层**成立，因为那一层才有逐趟数据。
//  本舞台干的是另一件事：把**这一期的平均车流**画出来 —— 哪条路上车多、料往哪走、多密。
//  月粒度上没有逐趟数据，编出来的「第 37 趟」是假的；但**平均强度**是真的，它就是台账里的吨量。
//
//  ── R-F1 密度只有一个自由度，且它有精确解（Little 定律）──
//  一条线上「同时在途的重车数」不是可以随便定的显示参数，它被吨量和车速**唯一确定**：
//      车次率   λ = T吨 / (W_t · H_期)                 [车/h]
//      单程时间 t_haul = L / v重                        [h]
//      在途车数 n = λ · t_haul                           （Little 定律）
//  等价的距离形式是**车头间距** Δs = v重 / λ，于是 n = L / Δs —— 同一个数的两种写法。
//  ⇒ 「点画得密一点好看」这种调整没有位置：调密度 = 改吨量或改车速，两者都是有出处的数。
//     唯一允许的显示自由度是 <see cref="SimFlowParams.DisplayScale"/>（见 R-F3）。
//
//  ── R-F2 n 可以小于 1，此时线上**大部分时刻没有车**，不许补一个 ──
//  真实数字：单月 20 万 t、载重 100 t、月作业 600 h ⇒ λ≈3.3 车/h；运距 3 km、重车 25 km/h
//  ⇒ t_haul≈0.12 h ⇒ n≈0.4。也就是这条路上**四成时间有一台车，六成时间空着**。
//  所以本舞台按**发车时刻**布点，不按「至少一个点」布：
//      第 j 台车在 t_j = j/λ 发车，矿山时刻 t 时它在 s = v·(t − t_j)，
//      只有 0 ≤ s ≤ L 才在路上（⇒ 在途台数自然等于 λL/v，n<1 时会真的时有时无）。
//  取 mod 会把「一台车绕圈跑」当成常驻，把 n=0.4 画成 n=1 —— 那是**把强度夸大 2.5 倍**，
//  而画面看不出任何异常。这条是本文件最容易写错的一行。
//
//  ── R-F3 显示倍率 k 是唯一的显示旋钮，且必须写进图例 ──
//  k>1 时把发车率按 k 倍加密，**一个点代表 1/k 台车**。默认 1（点=车）。
//  绝不自动选 k（「太稀了自动加密」= 图上的车数再也没有含义）。
//
//  ── R-F4 车流钟与期次钟是两个钟 ──
//  见 <see cref="SimMineClock"/> 的说明。本舞台吃的是**矿山时间**，1× = 实时。
//  拿期次钟（月压成 10 秒）驱动的话，25 km/h 的车会以十万倍速掠过。
//
//  ── R-F5 三个输入（载重 / 期作业小时 / 车速）逐项报来源，任一是缺省 ⇒ 整体标「示意」──
//  λ 与这三个数成简单比例，错一个就整体缩放。所以不报来源的密度等于没有含义。
//
//  ── R-F6 空驶用**自己的**车速与在途数 ──
//  n空 = λ · t空，而 t空 < t重（空车快）⇒ **n空 < n重**。两边取同一个数是错的。
//
//  ── R-F7 一帧的代价与车数无关 ──
//  重载点、空驶点各推一组（<see cref="ISimDynamicOverlay.SetMarkers"/>），流向箭头只在
//  换期时推一次。所以一帧 2 次 P/Invoke + 帧末 1 次 RequestRender。
//
//  ── 一条假线都不许画（承接 HaulRouteStage 的同名纪律）──
//  本舞台**只吃 <see cref="SimHaulRouteResult.Paths"/>** —— 那是运输线阶段真正解出来、
//  真正画在图上的那条路。绝不自己再问一次路：再问会得到另一条路（权重/吸附任一处不同就分岔），
//  于是车飘在路外，而两边各自都自洽。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 车流强度的三个输入。<b>每一项都带来源</b> —— 没有来源的密度没有含义（R-F5）。
/// </summary>
public sealed class SimFlowParams
{
    /// <summary>单车载重 W_t（t）。</summary>
    public double TruckPayloadT { get; set; } = 100;
    /// <summary>载重来源文案。<b>空 = 缺省值</b>（<see cref="Resolved"/> 会因此为 false）。</summary>
    public string PayloadSource { get; set; } = "";

    /// <summary>本期作业小时 H_期（h）。年/月/班的期长 × 日作业小时 × 作业率，由调用方给。</summary>
    public double WorkHoursPerPeriod { get; set; } = 600;
    /// <summary>作业小时来源文案。空 = 缺省值。</summary>
    public string WorkHoursSource { get; set; } = "";

    /// <summary>车速/坡阻模型（<see cref="HaulMetrics"/> 用它算重车与空车的行车时间）。</summary>
    public TruckProfile Truck { get; set; } = TruckProfile.Default;
    /// <summary>车速来源文案。空 = <see cref="TruckProfile"/> 的缺省经验值。</summary>
    public string SpeedSource { get; set; } = "";

    /// <summary>
    /// 显示倍率 k（R-F3）：k 倍加密发车，一个点代表 1/k 台车。默认 1。
    /// &lt;=0 或 NaN 视为 1。上限 20（再大就只是把线涂实，读不出任何东西）。
    /// </summary>
    public double DisplayScale { get; set; } = 1.0;

    /// <summary>画空驶回程（默认关：绝大多数回程与重载同路，画上去只是把图压暗一层）。</summary>
    public bool ShowEmptyRun { get; set; }

    /// <summary>动点抬升 m —— 纯显示偏移，避与地表/运输线 z-fight。不改任何里程。</summary>
    public double LiftM { get; set; } = 3.0;

    /// <summary>动点屏幕像素半径。</summary>
    public float PixelSize { get; set; } = 4.0f;

    /// <summary>一帧最多画多少个动点（超了按吨量从大到小截，并把舍掉的报出来）。</summary>
    public int MaxParticles { get; set; } = 4000;

    /// <summary>流向箭头的弧长间距 m（&lt;=0 = 不画箭头）。只在换期时推一次，不逐帧。</summary>
    public double ChevronSpacingM { get; set; } = 300;

    /// <summary>三个输入是不是都有出处（任一缺省 ⇒ false ⇒ 界面/图例整体标「示意」）。</summary>
    public bool Resolved => PayloadSource.Length > 0 && WorkHoursSource.Length > 0 && SpeedSource.Length > 0;

    /// <summary>有效倍率（夹过的）。</summary>
    public double ScaleK
    {
        get
        {
            double k = DisplayScale;
            if (double.IsNaN(k) || double.IsInfinity(k) || k <= 0) return 1.0;
            return Math.Min(20.0, k);
        }
    }

    public string SourceLine =>
        $"载重 {TruckPayloadT:0.#} t（{(PayloadSource.Length > 0 ? PayloadSource : "**缺省值**")}）"
      + $"　·　期作业 {WorkHoursPerPeriod:0.#} h（{(WorkHoursSource.Length > 0 ? WorkHoursSource : "**缺省值**")}）"
      + $"　·　车速 重{Truck.FlatSpeedLoadedKph:0.#}/空{Truck.FlatSpeedEmptyKph:0.#} km/h"
      + $"（{(SpeedSource.Length > 0 ? SpeedSource : "**缺省经验值**")}）";
}

/// <summary>一条线的车流强度结论（每一项都能对着输入手算复核）。</summary>
public sealed class SimFlowLine
{
    public string UnitId = "";
    public string DestinationName = "";
    public bool IsCoal;
    public double TonnageT;

    /// <summary>折线自身的三维长 m（<b>布点用这个</b>，不是路网里程 —— 车走在画出来的这条线上）。</summary>
    public double PolyLengthM;
    /// <summary>路网里程 m（口径对照用）。</summary>
    public double NetworkLengthM;

    /// <summary>净纵坡 %（重载方向）。</summary>
    public double GradePct;

    /// <summary>车次率 λ [车/h]（已含显示倍率 k）。</summary>
    public double TripsPerHour;
    /// <summary>不含倍率的真实车次率 [车/h]。</summary>
    public double RealTripsPerHour;

    /// <summary>重车单程 min / 空车单程 min。</summary>
    public double LoadedMin, EmptyMin;
    /// <summary>重车速度 / 空车速度 m/s（= 折线长 ÷ 行车时间，与上面那两个数严格互推）。</summary>
    public double LoadedMps, EmptyMps;

    /// <summary>在途重车数 n重 = λ·t重（可以 &lt;1，见 R-F2）。含倍率。</summary>
    public double OnRoadLoaded;
    /// <summary>在途空车数 n空 = λ·t空。含倍率。</summary>
    public double OnRoadEmpty;

    /// <summary>车头间距 Δs = v重/λ m。</summary>
    public double HeadwayM;

    /// <summary>逐点累计弧长（长度 = 点数；[0]=0）。布点二分用。</summary>
    internal double[] Cum = Array.Empty<double>();
    internal double[] Xyz = Array.Empty<double>();

    public string Caption =>
        $"{UnitId} → {DestinationName}　{(IsCoal ? "煤" : "岩")} {TonnageT / 1e4:0.##}万t　"
      + $"{PolyLengthM / 1000:0.00}km　λ={RealTripsPerHour:0.##}车/h　在途 {OnRoadLoaded:0.##}台";
}

/// <summary>一次车流重建的全部结论。</summary>
public sealed class SimFlowResult
{
    public string PeriodKey { get; internal set; } = "";

    // ── 分类账（每条路径落进且只落进一个桶）──
    public int PathTotal { get; internal set; }
    /// <summary>折线点数 &lt; 2 或长度 &lt;= 0 → 布不了点。</summary>
    public int PathNoGeometry { get; internal set; }
    /// <summary>吨量 &lt;= 0 → 没有车流（不是错，是真的没车）。</summary>
    public int PathNoTonnage { get; internal set; }
    /// <summary>真的参与布点的线数。</summary>
    public int PathUsed { get; internal set; }

    public bool Balanced => PathTotal == PathNoGeometry + PathNoTonnage + PathUsed;

    /// <summary>全矿车次率合计 [车/h]（**不含**显示倍率 —— 这是要报给人的真数）。</summary>
    public double TotalTripsPerHour { get; internal set; }
    /// <summary>全矿在途重车 / 空车合计（不含倍率）。</summary>
    public double TotalOnRoadLoaded { get; internal set; }
    public double TotalOnRoadEmpty { get; internal set; }

    /// <summary>逐线结论（界面列表 / 判据都看它）。</summary>
    public List<SimFlowLine> Lines { get; } = new();

    // ── 本帧真的画了多少 ──
    public int DrawnLoaded { get; internal set; }
    public int DrawnEmpty { get; internal set; }
    public int DroppedByCap { get; internal set; }
    /// <summary>流向箭头段数（换期时推一次）。</summary>
    public int ChevronSegments { get; internal set; }

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

/// <summary>
/// 车流示意舞台。<b>永不抛</b>：算不出来只是「没画 + 说清楚」。
/// <para>用法：换期时 <see cref="Rebuild"/> 一次（重算强度、推流向箭头），
/// 每帧 <see cref="Tick"/> 一次（只算位置、推两组点）。</para>
/// </summary>
public sealed class SimFlowStage
{
    /// <summary>重载动点的组名。</summary>
    public const string LoadedGroup = "simflow.loaded";
    /// <summary>空驶动点的组名。</summary>
    public const string EmptyGroup = "simflow.empty";
    /// <summary>流向箭头的组名（换期推一次，不逐帧）。</summary>
    public const string ChevronGroup = "simflow.dir";

    private readonly ISimDynamicOverlay _sink;

    public SimFlowStage(ISimDynamicOverlay? sink = null) => _sink = sink ?? SimDynamicOverlay.Current;

    public SimFlowParams Params { get; set; } = new();

    /// <summary>上一次重建的结论（还没跑过 = null）。</summary>
    public SimFlowResult? Last { get; private set; }

    /// <summary>接得上真通道吗（false ⇒ 所有推送静默无效，界面要照实说）。</summary>
    public bool Available => _sink.Available;
    public string StatusLabel => _sink.StatusLabel;

    // 逐帧复用的缓冲（刻意不每帧 new —— 一分钟 600 帧就是 600 次几万元素的分配）
    private double[] _bufXyz = Array.Empty<double>();
    private uint[] _bufArgb = Array.Empty<uint>();
    private float[] _bufSize = Array.Empty<float>();
    private byte[] _bufStyle = Array.Empty<byte>();
    private bool _pushedAny;

    // ── 重建（换期时一次）────────────────────────────────────────────────────

    /// <summary>
    /// 按本期路径重算车流强度，并推一次流向箭头。
    /// <paramref name="paths"/> 来自 <see cref="SimHaulRouteResult.Paths"/> —— 别自己再问路。
    /// </summary>
    public SimFlowResult Rebuild(string periodKey, IReadOnlyList<SimHaulPath>? paths)
    {
        var res = new SimFlowResult { PeriodKey = periodKey ?? "" };
        try { RebuildCore(res, paths); }
        catch (Exception ex)
        {
            res.Notes.Add($"车流重建异常（{ex.GetType().Name}: {ex.Message}）→ 本期不画车。");
            res.Summary = "◆ 车流：重建异常，本期一个动点都没画。";
            res.Lines.Clear();
        }
        Last = res;
        return res;
    }

    private void RebuildCore(SimFlowResult res, IReadOnlyList<SimHaulPath>? paths)
    {
        var p = Params;
        double k = p.ScaleK;
        double wt = p.TruckPayloadT > 1e-9 ? p.TruckPayloadT : 0;
        double hours = p.WorkHoursPerPeriod > 1e-9 ? p.WorkHoursPerPeriod : 0;

        res.PathTotal = paths?.Count ?? 0;

        if (wt <= 0 || hours <= 0)
        {
            res.Notes.Add($"◆ 载重（{p.TruckPayloadT:0.##} t）或期作业小时（{p.WorkHoursPerPeriod:0.##} h）不是正数，"
                        + "车次率 λ = T/(W·H) 分母为零 —— 本期不画车。这不是几何问题，是参数没接上。");
            res.Summary = "◆ 车流：载重或期作业小时无效，本期一个动点都没画。";
            BuildLegend(res);
            return;
        }

        for (int i = 0; i < res.PathTotal; i++)
        {
            var path = paths![i];
            if (path == null || path.PointCount < 2) { res.PathNoGeometry++; continue; }

            var (cum, polyLen) = Cumulative(path.Xyz);
            if (polyLen <= 1e-6) { res.PathNoGeometry++; continue; }
            if (path.TonnageT <= 1e-9) { res.PathNoTonnage++; continue; }

            double grade = path.NetGradePct;
            // 行车时间用**折线自身长**：车走在画出来的这条线上，用路网里程算速度会与位移对不上。
            double tLoadMin = HaulMetrics.TravelTimeMin(polyLen, grade, p.Truck, loaded: true);
            double tEmptyMin = HaulMetrics.TravelTimeMin(polyLen, -grade, p.Truck, loaded: false);
            if (tLoadMin <= 1e-9 || tEmptyMin <= 1e-9) { res.PathNoGeometry++; continue; }

            double lamReal = path.TonnageT / (wt * hours);      // [车/h]，真数
            double lam = lamReal * k;                            // 含显示倍率

            var line = new SimFlowLine
            {
                UnitId = path.UnitId,
                DestinationName = path.DestinationName.Length > 0 ? path.DestinationName : path.DestinationCode,
                IsCoal = path.IsCoal,
                TonnageT = path.TonnageT,
                PolyLengthM = polyLen,
                NetworkLengthM = path.LengthM,
                GradePct = grade,
                RealTripsPerHour = lamReal,
                TripsPerHour = lam,
                LoadedMin = tLoadMin,
                EmptyMin = tEmptyMin,
                LoadedMps = polyLen / (tLoadMin * 60.0),
                EmptyMps = polyLen / (tEmptyMin * 60.0),
                OnRoadLoaded = lam * (tLoadMin / 60.0),
                OnRoadEmpty = lam * (tEmptyMin / 60.0),
                HeadwayM = lam > 1e-12 ? polyLen / (lam * (tLoadMin / 60.0)) : double.PositiveInfinity,
                Cum = cum,
                Xyz = path.Xyz,
            };
            res.Lines.Add(line);
            res.PathUsed++;
            res.TotalTripsPerHour += lamReal;
            res.TotalOnRoadLoaded += lamReal * (tLoadMin / 60.0);
            res.TotalOnRoadEmpty += lamReal * (tEmptyMin / 60.0);
        }

        // 超上限时按吨量从大到小保留（与 HaulRouteStage 的截断口径一致）
        res.Lines.Sort((a, b) => b.TonnageT.CompareTo(a.TonnageT));

        // 里程口径对照：折线长与路网里程差得多，说明吸附把端点挪了一大截，值得说一句
        int mismatch = res.Lines.Count(l => l.NetworkLengthM > 1 &&
                                            Math.Abs(l.PolyLengthM - l.NetworkLengthM) / l.NetworkLengthM > 0.05);
        if (mismatch > 0)
            res.Notes.Add($"· 有 {mismatch} 条线的折线长与路网里程差 >5%（端点吸附挪动所致）。"
                        + "车速与位移都按**折线长**算 —— 车必须走在画出来的那条线上；"
                        + "台账里的运距仍是路网里程，两个数不是一回事。");

        if (res.PathNoGeometry > 0)
            res.Notes.Add($"· 有 {res.PathNoGeometry} 条路径点数不足/长度为零/行车时间算不出来，未布点。");
        if (res.PathNoTonnage > 0)
            res.Notes.Add($"· 有 {res.PathNoTonnage} 条路径吨量为 0 —— **没有车流不是画不出来**，是这一期这条路上真的没料。");

        double nLo = res.TotalOnRoadLoaded;
        var sparse = res.Lines.Count(l => l.OnRoadLoaded < 1.0);
        if (sparse > 0)
            res.Notes.Add($"· 有 {sparse} 条线的在途车数 <1 台（λ·t_重 <1）—— 图上会**时有时无**，这是真实的："
                        + "那条路本来就是隔一阵过一辆车。要让它常驻只能调「显示倍率」，"
                        + "调了之后一个点就不再代表一台车（图例会写明）。");
        if (Math.Abs(Params.ScaleK - 1.0) > 1e-9)
            res.Notes.Add($"◆ 显示倍率 ×{Params.ScaleK:0.##} 已开：**一个点代表 {1.0 / Params.ScaleK:0.###} 台车**，"
                        + "图上数点得出的车数要除以倍率才是真数。");
        if (!Params.Resolved)
            res.Notes.Add("◆ 强度输入里有缺省值（" + Params.SourceLine + "）—— "
                        + "λ 与这三个数成简单比例，缺一个整体就是等比例缩放的**示意值**，不能当排产结论用。");

        res.Summary = res.PathUsed == 0
            ? "◆ 车流：本期一条线都没有车（" +
              (res.PathTotal == 0 ? "运输线阶段一条路径都没解出来 —— 先看那一侧的命中率"
                                  : $"{res.PathNoGeometry} 条无几何 · {res.PathNoTonnage} 条无吨量") + "）。"
            : $"车流：{res.PathUsed} 条线　·　全矿 {res.TotalTripsPerHour:0.#} 车/h　·　"
            + $"同时在途 重车 {nLo:0.#} 台" + (Params.ShowEmptyRun ? $" / 空车 {res.TotalOnRoadEmpty:0.#} 台" : "")
            + (Params.Resolved ? "" : "　（**示意值**，见下）");

        BuildLegend(res);
        PushChevrons(res);
    }

    private void BuildLegend(SimFlowResult res)
    {
        double k = Params.ScaleK;
        res.LegendText =
            "图例：动点 = 在途卡车，"
          + (Math.Abs(k - 1.0) < 1e-9 ? "**一点 = 一台车**" : $"**一点 = {1.0 / k:0.###} 台车**（显示倍率 ×{k:0.##}）")
          + "；色相同运输线（煤 / 岩），"
          + (Params.ShowEmptyRun ? "**灰点 = 空车返程**（速度按空车算，故比重车稀）" : "空驶未画")
          + $"；点按**真实车速**沿线前进（车流钟 1× = 实时，与期次推进不是同一个钟）；"
          + $"抬升 {Params.LiftM:0.##} m 只为避 z-fight。"
          + $"　强度口径：λ=T/(W·H)、在途 n=λ·t（Little）。{Params.SourceLine}";
    }

    // ── 逐帧（只算位置）────────────────────────────────────────────────────

    /// <summary>
    /// 按矿山时刻 <paramref name="mineHours"/> 重算所有动点位置并推两组。
    /// <b>不重算强度</b>（那是 <see cref="Rebuild"/> 的事）。返回本帧画了几个点。
    /// </summary>
    public int Tick(double mineHours)
    {
        var res = Last;
        if (res == null || res.Lines.Count == 0)
        {
            if (_pushedAny) { _sink.SetMarkers(LoadedGroup, null, null, null, null, 0); _sink.SetMarkers(EmptyGroup, null, null, null, null, 0); }
            return 0;
        }
        if (double.IsNaN(mineHours) || double.IsInfinity(mineHours) || mineHours < 0) mineHours = 0;

        int cap = Math.Max(0, Params.MaxParticles);
        EnsureBuffers(cap);

        int nLoaded = Fill(res, mineHours, loaded: true, cap, 0);
        res.DrawnLoaded = nLoaded;
        _sink.SetMarkers(LoadedGroup, _bufXyz, _bufArgb, _bufSize, _bufStyle, nLoaded);

        int nEmpty = 0;
        if (Params.ShowEmptyRun)
        {
            nEmpty = Fill(res, mineHours, loaded: false, cap, 0);
            _sink.SetMarkers(EmptyGroup, _bufXyz, _bufArgb, _bufSize, _bufStyle, nEmpty);
        }
        else if (_pushedAny)
        {
            _sink.SetMarkers(EmptyGroup, null, null, null, null, 0);
        }
        res.DrawnEmpty = nEmpty;
        _pushedAny = true;
        return nLoaded + nEmpty;
    }

    /// <summary>撤掉本舞台的三个组。幂等。</summary>
    public void Clear()
    {
        try
        {
            _sink.Clear(LoadedGroup);
            _sink.Clear(EmptyGroup);
            _sink.Clear(ChevronGroup);
        }
        catch { }
        _pushedAny = false;
    }

    /// <summary>帧末请求重绘（一帧一次；Set* 内部刻意不 render）。</summary>
    public void RequestRender() => _sink.RequestRender();

    // ── 布点：按发车时刻，不取 mod（R-F2）──────────────────────────────────

    private int Fill(SimFlowResult res, double t, bool loaded, int cap, int start)
    {
        int n = start;
        double lift = double.IsNaN(Params.LiftM) || double.IsInfinity(Params.LiftM) ? 0 : Params.LiftM;
        float px = Params.PixelSize > 0 ? Params.PixelSize : 4f;
        int dropped = 0;

        foreach (var line in res.Lines)
        {
            double lam = line.TripsPerHour;                       // 含倍率
            if (lam <= 1e-12) continue;
            double travelH = (loaded ? line.LoadedMin : line.EmptyMin) / 60.0;
            if (travelH <= 1e-12) continue;
            double v = line.PolyLengthM / travelH;                 // m/h

            uint rgb = loaded
                ? (line.IsCoal ? SimMaterialColorProvider.RgbCoal : SimMaterialColorProvider.RgbRock)
                : EmptyRunRgb;
            uint argb = 0xFF000000u | rgb;

            // 在路上的车 = 发车时刻落在 [t − travelH, t] 内的那些。
            // j 从 0 起按 1/λ 发车 ⇒ j ∈ [ceil((t−travelH)·λ), floor(t·λ)]。
            double jLoD = (t - travelH) * lam;
            double jHiD = t * lam;
            if (jHiD < 0) continue;
            long jLo = (long)Math.Ceiling(Math.Max(0, jLoD));
            long jHi = (long)Math.Floor(jHiD);
            if (jHi < jLo) continue;                               // 这条路此刻真的空着（n<1 的常态）

            for (long j = jLo; j <= jHi; j++)
            {
                if (n >= cap) { dropped++; continue; }
                double s = v * (t - j / lam);                       // 已走弧长 m
                if (s < 0) s = 0;
                if (s > line.PolyLengthM) s = line.PolyLengthM;
                // 空驶是反向跑：从汇端往源端。用同一条折线，弧长从末端量起。
                double sArc = loaded ? s : line.PolyLengthM - s;
                PointAt(line, sArc, out double x, out double y, out double z);

                int o3 = n * 3;
                _bufXyz[o3] = x; _bufXyz[o3 + 1] = y; _bufXyz[o3 + 2] = z + lift;
                _bufArgb[n] = argb;
                _bufSize[n] = px;
                _bufStyle[n] = (byte)SimMarkerStyle.Dot;
                n++;
            }
        }

        if (dropped > 0 && res.DroppedByCap != dropped)
        {
            res.DroppedByCap = dropped;
            // 只在数变了时记一条，别每帧往 Notes 里堆
            res.Notes.RemoveAll(s => s.StartsWith("◆ 动点数超过一帧上限", StringComparison.Ordinal));
            res.Notes.Add($"◆ 动点数超过一帧上限 {cap}，本帧**舍掉 {dropped} 个**（不是没有车，是没画）。");
        }
        return n;
    }

    /// <summary>弧长 → 三维点（折线上线性插值）。<paramref name="s"/> 已夹在 [0, L]。</summary>
    private static void PointAt(SimFlowLine line, double s, out double x, out double y, out double z)
    {
        var cum = line.Cum; var xyz = line.Xyz;
        int n = cum.Length;
        // 二分找第一个 cum[i] >= s
        int lo = 0, hi = n - 1;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (cum[mid] < s) lo = mid + 1; else hi = mid; }
        int i = Math.Max(1, lo);
        double seg = cum[i] - cum[i - 1];
        double f = seg > 1e-12 ? (s - cum[i - 1]) / seg : 0;
        if (f < 0) f = 0; else if (f > 1) f = 1;
        int a = (i - 1) * 3, b = i * 3;
        x = xyz[a] + (xyz[b] - xyz[a]) * f;
        y = xyz[a + 1] + (xyz[b + 1] - xyz[a + 1]) * f;
        z = xyz[a + 2] + (xyz[b + 2] - xyz[a + 2]) * f;
    }

    // ── 流向箭头（换期推一次）──────────────────────────────────────────────

    private void PushChevrons(SimFlowResult res)
    {
        double spacing = Params.ChevronSpacingM;
        if (spacing <= 0 || res.Lines.Count == 0)
        {
            if (_pushedAny) _sink.SetLines(ChevronGroup, null, null, 0);
            res.ChevronSegments = 0;
            return;
        }

        double lift = double.IsNaN(Params.LiftM) || double.IsInfinity(Params.LiftM) ? 0 : Params.LiftM;
        var xyz = new List<double>();
        var col = new List<uint>();
        // 箭头张开的世界尺寸：取间距的 1/12，且夹在 [8, 40] m —— 太小看不见、太大糊成一片。
        double wing = Math.Clamp(spacing / 12.0, 8.0, 40.0);
        int capSegs = 3000;

        foreach (var line in res.Lines)
        {
            for (double s = spacing * 0.5; s < line.PolyLengthM && xyz.Count / 6 < capSegs; s += spacing)
            {
                PointAt(line, s, out double x, out double y, out double z);
                double sb = Math.Max(0, s - wing * 0.6);
                PointAt(line, sb, out double bx, out double by, out double bz);
                double dx = x - bx, dy = y - by;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) continue;
                dx /= len; dy /= len;
                double nx = -dy, ny = dx;                       // 左法向
                uint argb = 0xFF000000u | (line.IsCoal ? SimMaterialColorProvider.RgbCoal : SimMaterialColorProvider.RgbRock);

                // 「>」形：尖端在前进方向，两条尾巴往后张开
                double tailX = x - dx * wing, tailY = y - dy * wing;
                xyz.AddRange(new[] { tailX + nx * wing * 0.5, tailY + ny * wing * 0.5, z + lift, x, y, z + lift });
                col.Add(argb);
                xyz.AddRange(new[] { tailX - nx * wing * 0.5, tailY - ny * wing * 0.5, z + lift, x, y, z + lift });
                col.Add(argb);
            }
        }

        res.ChevronSegments = col.Count;
        _sink.SetLines(ChevronGroup, xyz.ToArray(), col.ToArray(), col.Count);
        _pushedAny = true;
        if (col.Count >= capSegs)
            res.Notes.Add($"· 流向箭头达到上限 {capSegs} 段，后面的线没画箭头（把「箭头间距」调大即可）。");
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────

    private void EnsureBuffers(int cap)
    {
        if (_bufArgb.Length >= cap && cap > 0) return;
        int n = Math.Max(cap, 256);
        _bufXyz = new double[n * 3];
        _bufArgb = new uint[n];
        _bufSize = new float[n];
        _bufStyle = new byte[n];
    }

    /// <summary>逐点累计弧长（三维）。返回 (cum, 总长)。</summary>
    internal static (double[] Cum, double Length) Cumulative(double[] xyz)
    {
        int n = xyz.Length / 3;
        var cum = new double[n];
        double acc = 0;
        for (int i = 1; i < n; i++)
        {
            double dx = xyz[3 * i] - xyz[3 * (i - 1)];
            double dy = xyz[3 * i + 1] - xyz[3 * (i - 1) + 1];
            double dz = xyz[3 * i + 2] - xyz[3 * (i - 1) + 2];
            acc += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            cum[i] = acc;
        }
        return (cum, acc);
    }

    /// <summary>空驶点色（近灰；与运输线阶段的空驶灰同族，但这里是点不是线，不会混淆）。</summary>
    private const uint EmptyRunRgb = 0x9AA3AD;
}
