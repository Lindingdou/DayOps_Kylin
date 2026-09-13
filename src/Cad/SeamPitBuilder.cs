using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>「批量扩坑」一级台阶线上的一段（岩 / 煤），以及它两端是否尖灭于点。</summary>
public sealed class SeamPitRun
{
    public int Level { get; init; }
    public double Z { get; init; }
    /// <summary>true = 煤台阶段（顶板以下、底板以上），false = 岩台阶段（顶板以上）。</summary>
    public bool IsCoal { get; init; }
    public List<(double x, double y)> Points { get; } = new();
    /// <summary>整圈都是同一种（没被交线切开）。</summary>
    public bool FullRing { get; init; }
    public double LengthM => Points.Count < 2 ? 0 : Enumerable.Range(1, Points.Count - 1).Sum(i => Math.Sqrt((Points[i].x - Points[i - 1].x) * (Points[i].x - Points[i - 1].x) + (Points[i].y - Points[i - 1].y) * (Points[i].y - Points[i - 1].y)));
}

public sealed class SeamPitResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public List<SeamPitRun> Runs { get; } = new();
    /// <summary>岩→煤分界（顶板交线）落点数 / 煤→底板分界落点数（尖灭点）。</summary>
    public List<(double x, double y, double z)> TopPinchPoints { get; } = new();
    public List<(double x, double y, double z)> FloorPinchPoints { get; } = new();
    public int RockLevels { get; set; }
    public int CoalLevels { get; set; }
    public int SkippedBelowFloor { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// 「批量扩坑」（按煤层层位分层放坡）的托管等价 —— 原版走内核 <c>BuildSeamPitMultiSeam</c>（无源）。口径照原版按钮说明：
/// ① 顶板以上出<b>水平岩台阶</b>逐级向下，每级与顶板求交、台阶在交线处尖灭于点（交线本身不作为台阶线画出）；
/// ② 交线以下按<b>煤台阶高</b>分级出煤台阶，末级钉在底板上、煤厚→0 处尖灭于点；
/// ③ 底板以下不出台阶。
/// 做法：先按岩模板逐级偏移出整圈（<see cref="BenchBuilder"/> 同一套 W+H/tanα），再把每圈顶点按
/// z ≥ 顶板 → 岩 / 底板 ≤ z &lt; 顶板 → 煤 / z &lt; 底板 → 不出 分类，沿圈切成若干段；段端点即尖灭点（线性内插到面）。
/// 采不到面的顶点按"面没盖到"处理：沿用邻点的类别，不当作尖灭。
/// 【登记的差异】原版煤台阶是<b>倾斜</b>的（沿顶板走），这里煤台阶仍按水平级出、只按底板截；煤台阶高单独给（默认取岩台阶高）。
/// </summary>
public static class SeamPitBuilder
{
    public static SeamPitResult Build(IReadOnlyList<(double x, double y)> boundary, double z0,
                                      double rockH, double coalH, double faceAngleDeg, double bermW,
                                      IRoadZSampler top, IRoadZSampler bottom, int maxLevels = 60)
    {
        var res = new SeamPitResult();
        if (boundary == null || boundary.Count < 3) { res.Error = "境界线至少 3 点"; return res; }
        if (top == null || bottom == null) { res.Error = "顶板 / 底板面都得有"; return res; }
        if (rockH <= 1e-6 || coalH <= 1e-6) { res.Error = "台阶高 H 必须 > 0"; return res; }
        if (faceAngleDeg <= 1 || faceAngleDeg >= 89.9) { res.Error = "坡面角 α 须在 (1°, 90°)"; return res; }

        double run(double h) => h / Math.Tan(faceAngleDeg * Math.PI / 180.0);
        var crest = new List<(double x, double y)>(boundary);
        double zc = z0;
        bool inCoal = false;   // 一旦某圈没有一个岩顶点，后面按煤台阶高分级
        for (int k = 0; k < maxLevels; k++)
        {
            double h = inCoal ? coalH : rockH;
            var toe = BenchBuilder.OffsetRing(crest, run(h), inward: true);
            if (toe == null || Math.Abs(BenchLines.SignedArea(toe)) < 1e-9) { res.Notes.Add($"第 {k + 1} 级：环收缩到零 / 偏移退化，停。"); break; }
            double zt = zc - h;

            // 逐顶点分类：2 = 岩(z ≥ 顶板)，1 = 煤(底板 ≤ z < 顶板)，0 = 底板以下；-1 = 面没盖到
            int n = toe.Count;
            var cls = new int[n];
            var topZ = new double[n]; var botZ = new double[n];
            int rock = 0, coal = 0, below = 0, missed = 0;
            for (int i = 0; i < n; i++)
            {
                bool ht = top.TrySample(toe[i].x, toe[i].y, out topZ[i]);
                bool hb = bottom.TrySample(toe[i].x, toe[i].y, out botZ[i]);
                if (!ht && !hb) { cls[i] = -1; missed++; continue; }
                if (ht && zt >= topZ[i]) { cls[i] = 2; rock++; }
                else if (hb && zt < botZ[i]) { cls[i] = 0; below++; }
                else { cls[i] = 1; coal++; }
            }
            // 没盖到的顶点沿用最近的已分类邻点（面没盖到 ≠ 尖灭）
            if (missed > 0 && missed < n)
                for (int i = 0; i < n; i++) if (cls[i] < 0) cls[i] = NearestClass(cls, i);
            if (missed == n) { res.Notes.Add($"第 {k + 1} 级：整圈都采不到顶/底板，停。"); break; }
            if (rock + coal == 0) { res.SkippedBelowFloor++; res.Notes.Add($"第 {k + 1} 级 Z={zt:0.#}：整圈已在底板以下，不出台阶，停。"); break; }

            if (rock == n) res.RockLevels++;
            else if (coal + below == n) { res.CoalLevels++; inCoal = true; }
            else { if (rock > 0) res.RockLevels++; if (coal > 0) res.CoalLevels++; }
            if (rock == 0) inCoal = true;

            // 沿圈切段：同类相邻顶点连成段，类别变化处按 z 与面高差线性内插出分界点（尖灭点）
            EmitRuns(res, toe, cls, topZ, botZ, zt, k + 1);

            crest = toe; zc = zt;
            // 平盘：下一级从坡脚再往内 W（同 BenchBuilder：坡脚线 → 平盘外缘 = 下一级坡顶）
            if (bermW > 1e-9)
            {
                var next = BenchBuilder.OffsetRing(crest, bermW, inward: true);
                if (next == null || Math.Abs(BenchLines.SignedArea(next)) < 1e-9) { res.Notes.Add($"第 {k + 1} 级：平盘内缘收缩到零，停。"); break; }
                crest = next;
            }
        }
        res.Ok = res.Runs.Count > 0;
        if (!res.Ok && res.Error.Length == 0) res.Error = "没有出任何台阶段（境界线是否已在底板以下？）";
        return res;
    }

    private static int NearestClass(int[] cls, int i)
    {
        int n = cls.Length;
        for (int d = 1; d < n; d++)
        {
            int a = cls[(i + d) % n], b = cls[(i - d + n) % n];
            if (a >= 0) return a;
            if (b >= 0) return b;
        }
        return 1;
    }

    private static void EmitRuns(SeamPitResult res, List<(double x, double y)> ring, int[] cls, double[] topZ, double[] botZ, double z, int level)
    {
        int n = ring.Count;
        bool uniform = true;
        for (int i = 1; i < n; i++) if (cls[i] != cls[0]) { uniform = false; break; }
        if (uniform)
        {
            if (cls[0] == 0) return;
            var full = new SeamPitRun { Level = level, Z = z, IsCoal = cls[0] == 1, FullRing = true };
            full.Points.AddRange(ring);
            res.Runs.Add(full);
            return;
        }
        // 从一个类别变化处起走一整圈，避免把一段切成两半
        int start = 0;
        for (int i = 0; i < n; i++) if (cls[i] != cls[(i - 1 + n) % n]) { start = i; break; }
        SeamPitRun? cur = null;
        for (int s = 0; s < n; s++)
        {
            int i = (start + s) % n, j = (i + 1) % n;
            int ci = cls[i], cj = cls[j];
            if (ci != 0)
            {
                cur ??= NewRun(level, z, ci == 1);
                if (cur.Points.Count == 0 || cur.Points[^1] != ring[i]) cur.Points.Add(ring[i]);
            }
            if (ci == cj) continue;
            // 类别变化：在 i→j 段上内插分界点。岩↔煤按顶板，煤↔底板以下按底板
            var pb = Boundary(ring[i], ring[j], z, ci, cj, topZ[i], topZ[j], botZ[i], botZ[j]);
            if (cur != null) { cur.Points.Add(pb); Flush(res, cur); cur = null; }
            if (ci == 2 || cj == 2) res.TopPinchPoints.Add((pb.x, pb.y, z)); else res.FloorPinchPoints.Add((pb.x, pb.y, z));
            if (cj != 0) { cur = NewRun(level, z, cj == 1); cur.Points.Add(pb); }
        }
        if (cur != null) { if (cls[start] != 0 && (cur.Points.Count == 0 || cur.Points[^1] != ring[start])) cur.Points.Add(ring[start]); Flush(res, cur); }
    }

    private static SeamPitRun NewRun(int level, double z, bool coal) => new() { Level = level, Z = z, IsCoal = coal, FullRing = false };
    private static void Flush(SeamPitResult res, SeamPitRun r) { if (r.Points.Count >= 2) res.Runs.Add(r); }

    private static (double x, double y) Boundary((double x, double y) a, (double x, double y) b, double z, int ca, int cb,
                                                 double ta, double tb, double ba, double bb)
    {
        // 用"台阶标高 − 面标高"在两端的符号变化做线性内插；面缺失时退到中点
        double fa, fb;
        if (ca == 2 || cb == 2) { fa = z - ta; fb = z - tb; } else { fa = z - ba; fb = z - bb; }
        double t = 0.5;
        if (!double.IsNaN(fa) && !double.IsNaN(fb) && Math.Abs(fa - fb) > 1e-12) t = Math.Clamp(fa / (fa - fb), 0, 1);
        return (a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }
}
