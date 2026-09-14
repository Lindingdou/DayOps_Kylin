using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

/// <summary>新台阶的一级：标高 + 身份（入图只留图层名 + 几何，Kind/煤层/级号/坡顶坡底全靠交接单带过去）。</summary>
public sealed class EpBenchLevel
{
    /// <summary>代表标高：该线各点 Z 的中位数（削顶段 Z 跟着地表走，均值会被拉偏）。</summary>
    public double Z;
    public double ZSpread;
    /// <summary>0=煤台阶 1=岩台阶 2=露煤走廊 3=搭接线。</summary>
    public int    Kind;
    public string SeamName = "";
    public int    SeamIndex = -1, Level, RockLevel = -1, LineIndex;
    public bool   IsCrest;
    public string Layer = "";

    public string Tag
        => (Kind switch { 1 => "岩台阶", 2 => "露煤", 3 => "搭接", _ => string.IsNullOrEmpty(SeamName) ? "煤台阶" : SeamName })
           + (IsCrest ? "·坡顶" : "·坡底");
}

/// <summary>一条工作线上模板占的推进区间（用原始台阶结果投出来，比入图后重投准）。</summary>
public readonly struct EpCoverRange
{
    public readonly int    Line;
    public readonly double ALo, AHi;
    public readonly int    Pts;
    public EpCoverRange(int line, double aLo, double aHi, int pts) { Line = line; ALo = aLo; AHi = aHi; Pts = pts; }
}

/// <summary>「驱动量」→「创建工程位置」的一次性交接单：范围（工作线 + 推进区间 + 前界 + 控制线）与高程（标高格 + 每一级的标高与身份）。</summary>
public sealed class EpHandoff
{
    public DateTime SavedAt = DateTime.Now;
    public string   Note = "";

    public readonly List<WorkLineSamples> WorkLines = new();
    public double DStage1;
    public readonly List<double> DSeamBySeam = new();
    public readonly List<EpCoverRange> Cover = new();
    public readonly List<(double[] Xyz, int Line)> Boundaries = new();

    public double BenchGridAnchorZ;
    public double RockBenchH = 15;
    public double WallTopZ, ZTop, ZFloor, AlphaDeg;
    public readonly List<double> CoalBenchHBySeam = new();
    public readonly List<EpBenchLevel> Levels = new();

    /// <summary>这一跑参与开采的全部煤层名册（自下而上）；名册 − Levels 里出现过的层 = 一条台阶线都没建出来的煤层。</summary>
    public readonly List<string> SeamRoster = new();
    public readonly List<bool> SeamReady = new();

    /// <summary>归格容差：半个标高格。</summary>
    public double LevelTol => Math.Max(0.25, RockBenchH * 0.5);

    public string Summary
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            return $"{SavedAt:HH:mm:ss} · 工作线 {WorkLines.Count} 条 · 前界 d={DStage1.ToString("0.#", ci)}m · " +
                   $"台阶 {Levels.Count} 级线 · 标高格 {(BenchGridAnchorZ > 0 ? BenchGridAnchorZ.ToString("0.##", ci) : "绝对整数倍")}" +
                   $"+k×{RockBenchH.ToString("0.#", ci)}m" +
                   (string.IsNullOrEmpty(Note) ? "" : $" · {Note}");
        }
    }
}

/// <summary>
/// 交接单的进程内单例挂载点（忠实原版：单次即可、刻意不落盘 —— 落盘会在换图时读到上一张的交接单且无法察觉）。
/// 进程重启就是没有，「创建工程位置」退回图层解析。
/// </summary>
public static class EngineeringPositionHandoff
{
    private static readonly object _gate = new();
    private static EpHandoff? _latest;

    public static EpHandoff? Latest { get { lock (_gate) return _latest; } }
    public static void Publish(EpHandoff? h) { lock (_gate) _latest = h; }
    public static void Clear() { lock (_gate) _latest = null; }

    /// <summary>从一次真实的台阶生成结果装配交接单。要在按控制线裁剪之后调。装不出返回 null，不抛。</summary>
    public static EpHandoff? Build(
        BenchTemplateResult? res, IReadOnlyList<WorkLineSamples>? wls, BenchTemplateParams? bp,
        double advance, IReadOnlyList<double>? dSeamBySeam, double alphaDeg, double zTop, double zFloor,
        IReadOnlyList<(double[] Xyz, int Line)>? boundaries, string note, IReadOnlyList<SeamSurfaces>? seams = null)
    {
        try
        {
            if (res == null || !res.Success || wls == null || wls.Count == 0) return null;
            bp ??= new BenchTemplateParams();
            var h = new EpHandoff
            {
                Note = note ?? "", DStage1 = advance,
                BenchGridAnchorZ = bp.BenchGridAnchorZ, RockBenchH = bp.RockBenchH > 0 ? bp.RockBenchH : 15,
                WallTopZ = bp.WallTopZ, ZTop = zTop, ZFloor = zFloor, AlphaDeg = alphaDeg,
            };
            h.WorkLines.AddRange(wls);
            if (dSeamBySeam != null) h.DSeamBySeam.AddRange(dSeamBySeam);
            if (bp.CoalBenchHBySeam != null) h.CoalBenchHBySeam.AddRange(bp.CoalBenchHBySeam);
            if (boundaries != null) h.Boundaries.AddRange(boundaries);
            if (seams != null) foreach (var s in seams) { h.SeamRoster.Add(s?.Name ?? ""); h.SeamReady.Add(s?.Ready == true); }

            var proj = WorkLineProjector.Build(wls, 5.0);
            int L = wls.Count;
            var lo = new double[L]; var hi = new double[L]; var cnt = new int[L];
            for (int i = 0; i < L; i++) { lo[i] = double.MaxValue; hi[i] = double.MinValue; }
            foreach (var bl in res.Benches)
                foreach (var line in new[] { bl.Crest, bl.Toe })
                    foreach (var pt in line)
                    {
                        if (!proj.TryProject(pt.X, pt.Y, out double a0, out _, out int ln)) continue;
                        if (ln < 0 || ln >= L) continue;
                        if (a0 < lo[ln]) lo[ln] = a0;
                        if (a0 > hi[ln]) hi[ln] = a0;
                        cnt[ln]++;
                    }
            for (int i = 0; i < L; i++) if (cnt[i] > 0) h.Cover.Add(new EpCoverRange(i, lo[i], hi[i], cnt[i]));

            foreach (var bl in res.Benches)
            {
                string layer = LayerOf(bl);
                AddLevel(h, bl, bl.Crest, true, layer);
                AddLevel(h, bl, bl.Toe, false, layer);
            }
            return h.Levels.Count > 0 ? h : null;
        }
        catch { return null; }
    }

    /// <summary>图层名口径必须与入图一致（台阶_搭接线 / 台阶_岩台阶 / 台阶_煤名）。</summary>
    public static string LayerOf(BenchLine bl)
        => bl.Kind == 3 ? "台阶_搭接线"
         : bl.Kind == 1 ? "台阶_岩台阶"
         : $"台阶_{(string.IsNullOrEmpty(bl.SeamName) ? "煤" : bl.SeamName)}";

    private static void AddLevel(EpHandoff h, BenchLine bl, List<(double X, double Y, double Z)> pts, bool isCrest, string layer)
    {
        if (pts == null || pts.Count < 2) return;
        var zs = new List<double>(pts.Count);
        foreach (var p in pts) zs.Add(p.Z);
        zs.Sort();
        h.Levels.Add(new EpBenchLevel
        {
            Z = zs[zs.Count / 2], ZSpread = zs[zs.Count - 1] - zs[0],
            Kind = bl.Kind, SeamName = bl.SeamName ?? "", SeamIndex = bl.SeamIndex, Level = bl.Level, RockLevel = bl.RockLevel,
            LineIndex = bl.LineIndex, IsCrest = isCrest, Layer = layer,
        });
    }
}
