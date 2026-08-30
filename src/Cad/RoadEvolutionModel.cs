using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>三维点(演化对比用；原 RoadLib.Network.Point3d 的最小替代)。</summary>
public readonly struct Pt3
{
    public readonly double X, Y, Z;
    public Pt3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public double DistanceTo(Pt3 o) { double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    public double HorizontalDistanceTo(Pt3 o) { double dx = X - o.X, dy = Y - o.Y; return Math.Sqrt(dx * dx + dy * dy); }
}

/// <summary>一条中线(演化对比用；原 RoadEdge 的最小替代)。</summary>
public sealed class EvoLine
{
    public string Id = "";
    public List<Pt3> Centerline = new();
}

/// <summary>线路演化类别(五类, 按段下判 RE1)。</summary>
public enum RoadEvolutionClass { Extend, Shorten, Abolish, Shift, Keep }

/// <summary>这一行说的是哪一期几何。</summary>
public enum EvolutionSide { Curr, Prev }

/// <summary>一段线路的演化判定结果 + 证据（忠实移植原 RouteEvolution）。</summary>
public sealed class RouteEvolution
{
    public RoadEvolutionClass Class { get; set; }
    public EvolutionSide Side { get; init; }
    public bool IsNewRoad { get; init; }
    public bool IsWholeEdge { get; init; }
    public string? PrevEdgeId { get; init; }
    public string? CurrEdgeId { get; init; }
    public IReadOnlyList<Pt3> DisplayCenterline { get; init; } = Array.Empty<Pt3>();
    public double SegLenM { get; init; }
    public double EdgeLenM { get; init; }
    public double SegStartM { get; init; }
    public bool AtEdgeEnd { get; init; }
    public double MatchDistanceM { get; init; }
    public double ShiftM { get; init; }
    public double GrowthDx { get; init; }
    public double GrowthDy { get; init; }
    public Pt3? FreeEnd { get; init; }
    public bool? DirAgreesWithAdvance { get; init; }

    public double SegFrac => EdgeLenM > 1e-9 ? SegLenM / EdgeLenM : 0;

    public string ClassText => Class switch
    {
        RoadEvolutionClass.Extend => IsNewRoad ? "新建入网" : (AtEdgeEnd ? "延拓" : "改线(新)"),
        RoadEvolutionClass.Shorten => AtEdgeEnd ? "截短" : "改线(旧)",
        RoadEvolutionClass.Abolish => "已废除",
        RoadEvolutionClass.Shift => "移位",
        _ => "保持",
    };
}

/// <summary>两期里程账(RE8, 不重不漏自证)。忠实移植原 EvolutionLedger。</summary>
public sealed class EvolutionLedger
{
    public double PrevTotalM { get; set; }
    public double CurrTotalM { get; set; }
    public double NewM { get; set; }
    public double GoneM { get; set; }
    public double SharedCurrM { get; set; }
    public double SharedPrevM { get; set; }
    public double ShiftedM { get; set; }

    public double NetM => CurrTotalM - PrevTotalM;
    public double SharedDriftM => SharedCurrM - SharedPrevM;
    public double CurrResidualM => CurrTotalM - (SharedCurrM + NewM);
    public double PrevResidualM => PrevTotalM - (SharedPrevM + GoneM);
    public bool IsBalanced =>
        Math.Abs(CurrResidualM) <= Math.Max(0.5, CurrTotalM * 1e-6) &&
        Math.Abs(PrevResidualM) <= Math.Max(0.5, PrevTotalM * 1e-6);

    public string Text =>
        $"里程账：上期 {PrevTotalM / 1000:F2} km → 本期 {CurrTotalM / 1000:F2} km(净 {NetM:+0;-0;0} m)＝ " +
        $"新增 {NewM:F0} m − 消失 {GoneM:F0} m + 共有段长度差 {SharedDriftM:+0;-0;0} m" +
        (IsBalanced ? "" : $"　⚠不配平(本期残差 {CurrResidualM:F1} m / 上期残差 {PrevResidualM:F1} m)");
}

/// <summary>两期演化判定结果集(含统计/里程账/CSV)。忠实移植原 RoadEvolutionResult。</summary>
public sealed class RoadEvolutionResult
{
    public List<RouteEvolution> Routes { get; } = new();
    public EvolutionLedger Ledger { get; } = new();

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

    public double LenOf(RoadEvolutionClass c) => Natural(c).Sum(r => r.SegLenM);

    public string Summary =>
        $"延拓 {ExtendCount} 段 {LenOf(RoadEvolutionClass.Extend):F0} m(含新建入网 {NewRoadCount} 条) / " +
        $"截短 {ShortenCount} 段 {LenOf(RoadEvolutionClass.Shorten):F0} m / " +
        $"废除 {AbolishCount} 段 {LenOf(RoadEvolutionClass.Abolish):F0} m / " +
        $"移位 {ShiftCount} 段 {LenOf(RoadEvolutionClass.Shift):F0} m / " +
        $"保持 {KeepCount} 段 {LenOf(RoadEvolutionClass.Keep):F0} m";

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.0", CultureInfo.InvariantCulture);

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

/// <summary>两期演化判定参数(务实默认值)。忠实移植原 RoadEvolutionOptions。</summary>
public sealed class RoadEvolutionOptions
{
    public double SampleStepM { get; set; } = 2.0;
    public double MatchToleranceM { get; set; } = 8.0;
    public double MatchAngleDeg { get; set; } = 35.0;
    public double NewCoverFrac { get; set; } = 0.35;
    public double GoneCoverFrac { get; set; } = 0.40;
    public double GrowMinLenM { get; set; } = 15.0;
    public double ShiftThresholdM { get; set; } = 3.0;
    public (double X, double Y)? AdvanceDirXY { get; set; }
}
