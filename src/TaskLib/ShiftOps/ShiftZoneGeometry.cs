// 忠实移植自原 PitMine3D Modules/TaskLib/ShiftOps/ShiftZoneGeometry.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.TaskLib.ShiftOps;

// ─────────────────────────────────────────────────────────────────────────────
//  班内工序推演的「地」—— 把作业区域规划的几何配到已编制的任务上。
//
//  ══ 在这之前 ══
//  ShiftProcessModel 里的 ProcessLine / ProcessStep **只带一个点**（面的源坐标）。
//  于是班内推演只能在影像上打几个标注：任务的量、节拍、瓶颈全是真的，
//  唯独"这活在哪块地上干"完全没有 —— 而那正是"作业区域"这三个字要说明的事。
//  三个采装面挤在同一处、看不出各自的范围，根因就在这一层缺失，不在画法。
//
//  ══ 这一层做什么 ══
//  给「一道工序 + 一个作业面」找那块地，两级来源，取不到就如实说没有：
//    ① 工序作业区 process_zone（期次 + 工序 + 面名）—— 每道工序演在自己的地上
//       （穿孔在孔位带、采装在采装带、排土在排土幅），走已有的 SimProcessRegions。
//    ② 可采区域 mineable_region（面名 / 采场名）—— 退回面级轮廓，整个面一块地。
//    ③ 都没有 ⇒ 不画面，只保留原来的点，并记一条账。
//
//  ══ 三条纪律 ══
//  **G1 不另造匹配器**：面名→区域一律走 SimPlanScene.NameHit，与 StageSimPlayer 同一条。
//      面名是「采场1（pit）·岩1308」这种派生名，区域名是「采场1」，靠的就是这个匹配。
//      在这儿再写一个"差不多"的匹配，两个推演会对同一个面给出不同的地。
//  **G2 退级要说出来**：「演在工序区」与「演在整个面」画面上分不出来，而它们说的是两件事。
//  **G3 只给几何，不给身份**：类别/汇绑定/坡面角一律沿用面级那块区域 —— 工序区自己不带内外排，
//      拿它判极性会让排土轮廓朝反方向动（同 SimProcessRegions 的 S1）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次区域解析的结果。</summary>
public readonly struct ZoneHit
{
    private readonly string? _source;

    public ZoneHit(SimRegion? region, string source)
    {
        Region = region;
        _source = source;
    }

    public SimRegion? Region { get; }

    /// <summary>
    /// 「工序区」/「面级」/「无」—— 界面按它标注这一格演的到底是什么。
    /// <para><b>永不为 null</b>：<c>default(ZoneHit)</c>（调用方没传几何时就是它）绕过构造函数，
    /// 字段是 null；直接暴露自动属性会让"没配地"这条路上到处是 null 串。</para>
    /// </summary>
    public string Source => string.IsNullOrEmpty(_source) ? ShiftZoneGeometry.SrcNone : _source!;

    public bool Has => Region != null && Region.Ring.Count >= 3;
}

/// <summary>班内工序推演用的作业区域索引（一期一份）。</summary>
public sealed class ShiftZoneGeometry
{
    public const string SrcProcess = "工序区";
    public const string SrcFace = "面级";
    public const string SrcNone = "无";

    private SimRegionSet _faceLevel = new();
    private SimProcessRegionSet? _procLevel;

    /// <summary>面名 → 面级区域的缓存（NameHit 是模糊匹配，逐条重算在 46 个面 × 三班上不划算）。</summary>
    private readonly Dictionary<string, SimRegion?> _byName = new(StringComparer.OrdinalIgnoreCase);

    public string Period { get; private set; } = "";
    public string SourceLabel { get; private set; } = "";
    public List<string> Notes { get; } = new();

    /// <summary>面级区域一块都没有 ⇒ 这一层什么也配不出来，画面只能退回打点。</summary>
    public bool IsEmpty => _faceLevel.IsEmpty && (_procLevel?.IsEmpty ?? true);

    /// <summary>
    /// 载入一期的作业区域。任何一层读不到都只降级并记账，不抛
    /// （班内推演不能因为"图上没画区域"就打不开）。
    /// </summary>
    public static ShiftZoneGeometry Load(string period, SinkRegistry? sinks)
    {
        var g = new ShiftZoneGeometry { Period = period ?? "" };

        try { g._faceLevel = SimRegionLoader.Load(sinks); }
        catch (Exception ex)
        {
            g._faceLevel = new SimRegionSet();
            g.Notes.Add($"可采区域读不出（{Short(ex)}）⇒ 面级轮廓这一层不可用。");
        }

        try { g._procLevel = SimProcessRegions.Load(g.Period, g._faceLevel); }
        catch (Exception ex)
        {
            g._procLevel = null;
            g.Notes.Add($"工序作业区读不出（{Short(ex)}）⇒ 各工序只能共用面级轮廓。");
        }

        int face = g._faceLevel.Regions.Count;
        int proc = g._procLevel?.Count ?? 0;
        g.SourceLabel = face == 0 && proc == 0
            ? "作业区域：一块都没读到 —— 工序只能在影像上打点，画不出各自的地"
            : $"作业区域：工序区 {proc} 块 · 面级 {face} 块（{g.Period}）";

        if (proc == 0 && face > 0)
            g.Notes.Add($"{g.Period} 没有工序作业区 ⇒ 五道工序共用同一条面级轮廓："
                      + "穿孔演在采装带上、排土演在整幅上，位置是对不上的。"
                      + "补法：在「作业区划分」里按期生成工序作业区。");

        if (!string.IsNullOrWhiteSpace(g._faceLevel.SourceLabel))
            g.Notes.Add(g._faceLevel.SourceLabel);

        return g;
    }

    /// <summary>
    /// 找「这道工序在这个面上」的那块地。工序区 → 面级 → 无，逐级退，退到哪一级由 <see cref="ZoneHit.Source"/> 说明。
    /// </summary>
    public ZoneHit Resolve(ProcessType process, string? faceName)
    {
        if (string.IsNullOrWhiteSpace(faceName)) return new ZoneHit(null, SrcNone);

        // ① 工序区：每道工序自己的地
        try
        {
            var byProc = _procLevel?.Find(process, faceName);
            if (byProc != null && byProc.Ring.Count >= 3) return new ZoneHit(byProc, SrcProcess);
        }
        catch { /* 工序区这一层坏了不影响退回面级 */ }

        // ② 面级：整个面一块地
        var byFace = FaceRegion(faceName);
        if (byFace != null && byFace.Ring.Count >= 3) return new ZoneHit(byFace, SrcFace);

        return new ZoneHit(null, SrcNone);
    }

    /// <summary>面名 → 面级区域。<b>匹配器与 StageSimPlayer 共用一条</b>（先精确、再 NameHit）。</summary>
    private SimRegion? FaceRegion(string faceName)
    {
        string n = faceName.Trim();
        if (_byName.TryGetValue(n, out var cached)) return cached;

        SimRegion? hit = null;
        try
        {
            hit = _faceLevel.Regions.FirstOrDefault(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase))
               ?? _faceLevel.Regions.FirstOrDefault(r => SimPlanScene.NameHit(r.Name, n));
        }
        catch { hit = null; }

        _byName[n] = hit;
        return hit;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  环 → 三角。面通道（SimPanelOverlay.SetFaces）吃的是三角，不是环。
//
//  **必须用耳切，不能用扇形**：采场/排土场的轮廓是强凹的（帮坡一圈一圈往里收），
//  扇形三角化会把凹口整片填成实心，画面上看着像"这块地比实际大一圈"——
//  而且它不会报错，只是画错。
//
//  自交环（台账里偶有）耳切会剩下补不完的顶点：此时**整块不画**并返回 false，
//  由调用方记一条账。硬填一个近似形状是在给一个不存在的边界。
// ─────────────────────────────────────────────────────────────────────────────
public static class RingMesh
{
    /// <summary>
    /// 把一条平面环三角化成 <c>xyzFlat</c>（每三角 9 个 double）。
    /// 成功返回 true 并给出 <paramref name="triCount"/>；环退化/自交返回 false。
    /// </summary>
    public static bool Triangles(IReadOnlyList<SimPoint>? ring, double z,
                                 out double[] xyzFlat, out int triCount)
    {
        xyzFlat = Array.Empty<double>();
        triCount = 0;
        if (ring == null || ring.Count < 3) return false;

        // 去掉首尾重复点（台账里的环常是闭合的）
        var pts = new List<SimPoint>(ring);
        while (pts.Count > 3 && Near(pts[0], pts[^1])) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return false;

        bool ccw = SignedArea(pts) > 0;
        var idx = new List<int>(pts.Count);
        for (int i = 0; i < pts.Count; i++) idx.Add(i);

        var tris = new List<int>((pts.Count - 2) * 3);
        int guard = pts.Count * pts.Count + 16;      // 自交环会耗尽它，正是要的
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int k = 0; k < idx.Count; k++)
            {
                int a = idx[(k - 1 + idx.Count) % idx.Count], b = idx[k], c = idx[(k + 1) % idx.Count];
                if (!IsEar(pts, idx, a, b, c, ccw)) continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
                idx.RemoveAt(k);
                clipped = true;
                break;
            }
            if (!clipped) return false;              // 剩下的全不是耳朵 ⇒ 环自交，整块不画
        }
        if (idx.Count != 3) return false;
        tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);

        triCount = tris.Count / 3;

        // ★ 面积守恒校验：朴素耳切**骗得过**自交环 —— 蝴蝶结 (0,0)(10,10)(10,0)(0,10)
        //   只有四个顶点，剪掉一个"耳朵"就剩三个，循环直接结束，返回两个交叠的三角。
        //   环的有向面积为 0，而三角面积和是 50 ⇒ 一比就露馅。
        //   这一条同时挡住"多填/漏填"的一切形态（扇形三角化在凹环上也会在这里红）。
        double ringArea = Math.Abs(SignedArea(pts));
        double triArea = 0;
        for (int t = 0; t < triCount; t++)
        {
            var a0 = pts[tris[t * 3]]; var b0 = pts[tris[t * 3 + 1]]; var c0 = pts[tris[t * 3 + 2]];
            triArea += Math.Abs((b0.X - a0.X) * (c0.Y - a0.Y) - (b0.Y - a0.Y) * (c0.X - a0.X)) / 2;
        }
        if (Math.Abs(triArea - ringArea) > Math.Max(1e-6, ringArea * 1e-9))
        {
            triCount = 0;
            return false;
        }

        xyzFlat = new double[triCount * 9];
        for (int t = 0; t < triCount; t++)
            for (int c = 0; c < 3; c++)
            {
                var p = pts[tris[t * 3 + c]];
                xyzFlat[t * 9 + c * 3] = p.X;
                xyzFlat[t * 9 + c * 3 + 1] = p.Y;
                xyzFlat[t * 9 + c * 3 + 2] = z;
            }
        return true;
    }

    private static bool IsEar(List<SimPoint> pts, List<int> idx, int a, int b, int c, bool ccw)
    {
        double cross = Cross(pts[a], pts[b], pts[c]);
        if (ccw ? cross <= 0 : cross >= 0) return false;    // 凸顶点才可能是耳朵

        foreach (int i in idx)
        {
            if (i == a || i == b || i == c) continue;
            if (InTriangle(pts[i], pts[a], pts[b], pts[c])) return false;
        }
        return true;
    }

    private static bool InTriangle(SimPoint p, SimPoint a, SimPoint b, SimPoint c)
    {
        double d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static double Cross(SimPoint a, SimPoint b, SimPoint c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static double SignedArea(List<SimPoint> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var q = p[(i + 1) % p.Count];
            s += p[i].X * q.Y - q.X * p[i].Y;
        }
        return s / 2;
    }

    private static bool Near(SimPoint a, SimPoint b)
        => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
}
