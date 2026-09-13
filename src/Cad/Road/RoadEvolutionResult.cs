// 忠实移植自原 PitMine3D Modules/RoadLib/Evolution/RoadEvolutionResult.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 线路演化类别（本功能只识别，不执行）。五类，<b>按段</b>下判不是按整条边（RE1）。
///
/// 为什么是五类：三类那版把「截短」压进保持、把「移位」拆成新建+废除，两种都是现场最常发生的变化。
/// 台架实测（<c>RoadEvolutionDiagnosticTests</c>）：截短 500→200 判成"保持"一个字不报，
/// 整体横移 12m 判成"新建 300m + 废除 300m"凭空多出 300m。
/// </summary>
public enum RoadEvolutionClass
{
    /// <summary>延拓：本期有、上期没有的那一段（端头长出来 = 延伸；中段冒出来 = 改线的新线）。</summary>
    Extend,
    /// <summary>截短：上期有、本期没有的那一段（端头缩回去 = 被采穿/回收；中段没了 = 改线的旧线）。</summary>
    Shorten,
    /// <summary>已废除：整条上期边在本期通篇找不到对应。</summary>
    Abolish,
    /// <summary>移位：两期都在、但整段横向挪了（≥ 移位阈值）。不是"没变"。</summary>
    Shift,
    /// <summary>保持：两期都在且贴合（横移 &lt; 阈值）。</summary>
    Keep,
}

/// <summary>这一行说的是哪一期的几何。更新路网只能并入本期侧（RE12）。</summary>
public enum EvolutionSide
{
    /// <summary>本期几何（保持 / 移位 / 延拓 / 新建入网）。</summary>
    Curr,
    /// <summary>上期几何（截短 / 已废除）。</summary>
    Prev,
}

/// <summary>
/// <b>一段</b>线路的演化判定结果：类别 + "为什么这么判"的证据。
///
/// 判定原子是「某条边上的一段」而不是整条边（RE1）：一条边可以同时出
/// 「保持 380m + 延拓 100m + 截短 60m」三行。整条边一个类别那版答不了截短、
/// 答不了中段改线、两头都长只报一头。
///
/// 同一条边的所有段首尾相接、长度之和 = 该边全长（RE8 不重不漏），
/// 所以 <see cref="RoadEvolutionResult.Ledger"/> 的里程账是可配平的硬账。
/// <see cref="Class"/> 可被人工改判。
/// </summary>
public sealed class RouteEvolution
{
    public RoadEvolutionClass Class { get; set; }

    /// <summary>这一行的几何属于哪一期。</summary>
    public EvolutionSide Side { get; init; }

    /// <summary>本期全新出现（上期通篇无对应）。归延拓，单独打标以便区分。</summary>
    public bool IsNewRoad { get; init; }

    /// <summary>这一行就是整条边（新建入网 / 已废除），不是边内的一段。</summary>
    public bool IsWholeEdge { get; init; }

    /// <summary>段所在边的 Id（本期侧填 <see cref="CurrEdgeId"/>，上期侧填 <see cref="PrevEdgeId"/>）。</summary>
    public string? PrevEdgeId { get; init; }
    public string? CurrEdgeId { get; init; }

    /// <summary>清单/overlay 显示用中线 = <b>这一段</b>的中线。</summary>
    public IReadOnlyList<Point3d> DisplayCenterline { get; init; } = Array.Empty<Point3d>();

    // ── 证据 ──────────────────────────────────────────────────────────────
    /// <summary>本段平面长度 m。</summary>
    public double SegLenM { get; init; }
    /// <summary>本段所属边的全长 m（上下文：这一段占整条边多少）。</summary>
    public double EdgeLenM { get; init; }
    /// <summary>本段起点沿边的里程 m（定位用）。</summary>
    public double SegStartM { get; init; }
    /// <summary>本段贴着边的端头（延伸/截短），false = 夹在中间（改线）。</summary>
    public bool AtEdgeEnd { get; init; }

    /// <summary>共有段：本段采样点到对期中线距离的中位数 m（NaN = 变化段，无对应）。</summary>
    public double MatchDistanceM { get; init; }
    /// <summary>共有段：带符号横移中位数 m（+ = 在本期走向左侧）。抖动的中位数≈0，整体平移才顶得上去。</summary>
    public double ShiftM { get; init; }

    /// <summary>延拓段：朝外单位方向(XY)。</summary>
    public double GrowthDx { get; init; }
    public double GrowthDy { get; init; }
    /// <summary>延拓段的自由端点；null = 非端头延拓。</summary>
    public Point3d? FreeEnd { get; init; }
    /// <summary>延伸方向是否合推进方向先验（null = 未提供先验 / 非延拓段）。</summary>
    public bool? DirAgreesWithAdvance { get; init; }

    /// <summary>段占所属边的比例（0..1）。</summary>
    public double SegFrac => EdgeLenM > 1e-9 ? SegLenM / EdgeLenM : 0;

    public string ClassText => Class switch
    {
        RoadEvolutionClass.Extend => IsNewRoad ? "新建入网" : (AtEdgeEnd ? "延拓" : "改线(新)"),
        RoadEvolutionClass.Shorten => AtEdgeEnd ? "截短" : "改线(旧)",
        RoadEvolutionClass.Abolish => "已废除",
        RoadEvolutionClass.Shift => "移位",
        _ => "保持",
    };

    /// <summary>所属边 + 位置，一列说清"这一段在哪"。</summary>
    public string WhereText
    {
        get
        {
            string edge = Side == EvolutionSide.Curr ? (CurrEdgeId ?? "-") : (PrevEdgeId ?? "-");
            string era = Side == EvolutionSide.Curr ? "本" : "上";
            return IsWholeEdge
                ? $"{era} {edge}（整条）"
                : $"{era} {edge} {SegStartM:F0}~{SegStartM + SegLenM:F0}m";
        }
    }
}

/// <summary>
/// 两期里程账（RE8）。<b>不重不漏靠它自证</b>：本期侧所有段长之和必须等于本期总里程，
/// 上期侧同理 —— 差一米就说明有段被吞了或被重复计了。
///
/// 净变化 = (本期总 − 上期总) = (新增 − 消失) + (共有段两期长度差)。
/// 最后那一项是走向微调带来的，单列出来，免得它混进"新增/消失"里说不清。
/// </summary>
public sealed class EvolutionLedger
{
    public double PrevTotalM { get; set; }
    public double CurrTotalM { get; set; }
    /// <summary>新增里程 = Σ本期未被上期覆盖的段（延拓 + 新建入网）。</summary>
    public double NewM { get; set; }
    /// <summary>消失里程 = Σ上期未被本期覆盖的段（截短 + 已废除）。</summary>
    public double GoneM { get; set; }
    /// <summary>共有段按本期几何量的里程（保持 + 移位）。</summary>
    public double SharedCurrM { get; set; }
    /// <summary>共有段按上期几何量的里程。</summary>
    public double SharedPrevM { get; set; }
    /// <summary>其中判成"移位"的（本期几何计）。</summary>
    public double ShiftedM { get; set; }

    public double NetM => CurrTotalM - PrevTotalM;
    /// <summary>共有段两期长度差（走向微调；非新增也非消失）。</summary>
    public double SharedDriftM => SharedCurrM - SharedPrevM;

    /// <summary>本期侧配平残差 m（应为 0）。</summary>
    public double CurrResidualM => CurrTotalM - (SharedCurrM + NewM);
    /// <summary>上期侧配平残差 m（应为 0）。</summary>
    public double PrevResidualM => PrevTotalM - (SharedPrevM + GoneM);
    /// <summary>两侧都配平（残差 &lt; 0.5m 或 &lt; 相对 1e-6）。</summary>
    public bool IsBalanced =>
        Math.Abs(CurrResidualM) <= Math.Max(0.5, CurrTotalM * 1e-6) &&
        Math.Abs(PrevResidualM) <= Math.Max(0.5, PrevTotalM * 1e-6);

    public string Text =>
        $"里程账：上期 {PrevTotalM / 1000:F2} km → 本期 {CurrTotalM / 1000:F2} km（净 {NetM:+0;-0;0} m）＝ " +
        $"新增 {NewM:F0} m − 消失 {GoneM:F0} m + 共有段长度差 {SharedDriftM:+0;-0;0} m" +
        (IsBalanced ? "" : $"　⚠不配平（本期残差 {CurrResidualM:F1} m / 上期残差 {PrevResidualM:F1} m）");
}

/// <summary>两期演化判定结果集（含统计、里程账、CSV 导出）。</summary>
public sealed class RoadEvolutionResult
{
    public List<RouteEvolution> Routes { get; } = new();

    /// <summary>里程账（RE8）。判据自证不重不漏的地方。</summary>
    public EvolutionLedger Ledger { get; } = new();

    /// <summary>
    /// 保持/移位段两期各有一行（同一段几何、两份几何），统计时<b>只数本期那一份</b> ——
    /// 否则一条 300 m 没动过的路会被报成"保持 600 m"。
    /// </summary>
    private IEnumerable<RouteEvolution> Natural(RoadEvolutionClass c) =>
        c is RoadEvolutionClass.Keep or RoadEvolutionClass.Shift
            ? Routes.Where(r => r.Class == c && r.Side == EvolutionSide.Curr)
            : Routes.Where(r => r.Class == c);

    public int ExtendCount => Natural(RoadEvolutionClass.Extend).Count();
    public int ShortenCount => Natural(RoadEvolutionClass.Shorten).Count();
    public int AbolishCount => Natural(RoadEvolutionClass.Abolish).Count();
    public int ShiftCount => Natural(RoadEvolutionClass.Shift).Count();
    public int KeepCount => Natural(RoadEvolutionClass.Keep).Count();
    public int NewRoadCount => Routes.Count(r => r.IsNewRoad);

    private double LenOf(RoadEvolutionClass c) => Natural(c).Sum(r => r.SegLenM);

    public string Summary =>
        $"延拓 {ExtendCount} 段 {LenOf(RoadEvolutionClass.Extend):F0} m（含新建入网 {NewRoadCount} 条） / " +
        $"截短 {ShortenCount} 段 {LenOf(RoadEvolutionClass.Shorten):F0} m / " +
        $"废除 {AbolishCount} 段 {LenOf(RoadEvolutionClass.Abolish):F0} m / " +
        $"移位 {ShiftCount} 段 {LenOf(RoadEvolutionClass.Shift):F0} m / " +
        $"保持 {KeepCount} 段 {LenOf(RoadEvolutionClass.Keep):F0} m";

    private static string F(double v) =>
        double.IsNaN(v) ? "" : v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>导出 CSV（Excel 友好，UTF-8）。段级一行一段，末尾附里程账。</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("类别,期别,整条边,所属边,段起点m,段长m,占边%,位置,所属边全长m,匹配距m,横移m,方向");
        foreach (var r in Routes)
        {
            string edge = r.Side == EvolutionSide.Curr ? (r.CurrEdgeId ?? "") : (r.PrevEdgeId ?? "");
            sb.Append(r.ClassText).Append(',')
              .Append(r.Side == EvolutionSide.Curr ? "本期" : "上期").Append(',')
              .Append(r.IsWholeEdge ? "是" : "").Append(',')
              .Append(edge).Append(',')
              .Append(F(r.SegStartM)).Append(',')
              .Append(F(r.SegLenM)).Append(',')
              .Append(F(r.SegFrac * 100)).Append(',')
              .Append(r.IsWholeEdge ? "整条" : (r.AtEdgeEnd ? "端头" : "中段")).Append(',')
              .Append(F(r.EdgeLenM)).Append(',')
              .Append(F(r.MatchDistanceM)).Append(',')
              .Append(r.Class is RoadEvolutionClass.Keep or RoadEvolutionClass.Shift ? F(r.ShiftM) : "").Append(',')
              .Append(r.DirAgreesWithAdvance is null ? "" : (r.DirAgreesWithAdvance.Value ? "合推进" : "逆推进"))
              .AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine(Ledger.Text.Replace(',', '，'));
        return sb.ToString();
    }
}
