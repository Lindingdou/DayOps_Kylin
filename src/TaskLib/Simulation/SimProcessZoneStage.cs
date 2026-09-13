// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimProcessZoneStage.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;   // ProcessZone

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  工序作业区图层 —— **让五道工序各占一块地**（PS1）。
//
//  ══ 在这之前 ══
//  三维模拟里工序只有**一个彩色点**（`DynamicSimWindow.PushActivities`）：
//  同一个作业面的穿孔/爆破/采装/运输/排土全摆在**同一个坐标**上（面档案的 source_x/y 或区域质心），
//  靠错开铭牌勉强分开。于是「工序错开成几条带」这件工序模拟本该说明的事，图上一点也看不见 ——
//  五个点叠在一起，每一帧都规整，量与班次也都是真的，只有**位置**说的是另一件事。
//  而地本来就有：`process_zone`（V047）按「期次 + 工序 + 名字」存着五类地的真实环。
//
//  ══ 这一层做什么 ══
//  把本期 `process_zone` 的地画出来：**边界 + 按工序错开角度的斜线填充 + 工序名铭牌**。
//
//  ══ 五条口径 ══
//  **PS1 取不到地就不画，不退回面级轮廓。**（与 `SimProcessRegions` 的 S3 相反，且是故意的：
//      那边退回面级是为了**定位一个点**，退了还能用；这边退回面级会让五道工序的地**重新重合**，
//      而重合正是本层要治的病 —— 退化后的图与病态图长得一模一样。）
//  **PS4 警戒区只画虚线边界，不填充**：它是禁入区不是作业区（语义与其余四类相反，且不派设备）。
//      实测本矿一块警戒区 447 万 m²，是全部穿孔区的 32 倍 —— 填了就把整张图盖住。
//  **PS5 排土两带都画**：卸载带（卡车）与推排带（推土机）是两道工序两块地，分色不合并。
//  **PS-H 填充走扫描线斜纹，不走三角化**：工序区来自栅格反出的环，**凹的**，
//      扇形三角化会生出穿出边界的长条；而且工序区之间**天然重叠**（警戒区盖住穿孔区），
//      实心填充叠起来就是一团糊 —— 斜纹按工序错开角度，叠起来仍分得出是哪几道。
//  **PS-Z 每块地用它自己的高程**：工序区的环带真 xyz。摊平到 0m 的话，
//      排土场（+1300m）与采场底（+1100m）会画在一起，而画面上看着完全正常。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>工序作业区图层。永不抛：任何一块画不出来只丢那一块并记账。</summary>
public sealed class SimProcessZoneStage
{
    public const string EdgeGroup = "sim.proc.edge";
    public const string HatchGroup = "sim.proc.hatch";
    public const string LabelGroup = "sim.proc.label";

    private readonly ISimDynamicOverlay _sink;

    public SimProcessZoneStage(ISimDynamicOverlay sink) => _sink = sink ?? new NullDynamicOverlay();

    /// <summary>上一次实际画出的块数（按工序分）。</summary>
    public IReadOnlyDictionary<string, int> DrawnByProcess => _drawn;
    private Dictionary<string, int> _drawn = new(StringComparer.Ordinal);

    /// <summary>上一次画了多少条线段 / 多少个铭牌（判据与状态栏用）。</summary>
    public int LastSegments { get; private set; }
    public int LastLabels { get; private set; }

    /// <summary>被丢掉的块数与原因（环点不足 / 坐标无效）。</summary>
    public string DropNote { get; private set; } = "";

    /// <summary>图例文本（**画了什么就写什么**：没画的工序不出现在图例里）。</summary>
    public string Legend { get; private set; } = "";

    public void Clear()
    {
        _sink.Clear(EdgeGroup); _sink.Clear(HatchGroup); _sink.Clear(LabelGroup);
        _drawn = new Dictionary<string, int>(StringComparer.Ordinal);
        LastSegments = 0; LastLabels = 0; Legend = ""; DropNote = "";
    }

    /// <summary>
    /// 画一期。
    /// </summary>
    /// <param name="set">本期工序作业区（<see cref="SimProcessRegions.Load"/>）。</param>
    /// <param name="onlyProcess">只画这一道工序（工序码）；空 = 全画。</param>
    /// <param name="labelHeightM">铭牌字高（世界米，由调用方按相机比例反算 —— 写死世界米会在拉远时一个字都不画）。</param>
    /// <param name="hatch">画斜纹填充。关掉只剩边界（图上东西多时用）。</param>
    public void Apply(SimProcessRegionSet? set, string? onlyProcess, float labelHeightM, bool hatch = true)
    {
        if (set == null || set.Patches.Count == 0) { Clear(); return; }

        string only = (onlyProcess ?? "").Trim();
        var patches = set.Patches
            .Where(p => only.Length == 0 || string.Equals(p.Process, only, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (patches.Count == 0) { Clear(); return; }

        var exyz = new List<double>(); var eargb = new List<uint>();
        var hxyz = new List<double>(); var hargb = new List<uint>();
        var drawn = new Dictionary<string, int>(StringComparer.Ordinal);
        int dropped = 0;

        // 画序：**面积大的先画，小的压在上面**（PS-D）。
        //  按工序序画的话，后画的那道工序会把先画的整条边盖掉 —— 而工序区之间大面积重叠是常态
        //  （警戒区盖住穿孔区、穿孔区与采装区在月尺度上本来就是同一批单元）。
        //  现象是"这道工序一块地都没有"，与台账里真的没有它长得一模一样。
        foreach (var p in patches.OrderByDescending(p => p.AreaM2))
        {
            var ring = p.Region?.Ring;
            if (ring == null || ring.Count < 3) { dropped++; continue; }
            double z = p.Region!.Z;
            if (double.IsNaN(z) || double.IsInfinity(z)) z = 0;
            if (ring.Any(q => double.IsNaN(q.X) || double.IsNaN(q.Y)
                           || double.IsInfinity(q.X) || double.IsInfinity(q.Y))) { dropped++; continue; }

            uint rgb = SimProcessPalette.RgbOfCode(p.Process);
            bool keepOut = p.Draw == SimProcessDraw.KeepOut;

            // ── 边界 ──
            //  作业区：整环实线。禁入区：**虚线** —— 它是"谁都不许进"，与"谁在这儿干活"必须一眼分开，
            //  只靠颜色分不开（红色在这套配色里同时是爆破工序的色）。
            AppendRing(exyz, eargb, ring, z + 1.0, SimProcessPalette.With(rgb, keepOut ? (byte)0xCC : (byte)0xFF),
                       dashed: keepOut);

            // ── 斜纹填充（PS4：禁入区不填）──
            if (hatch && !keepOut)
                AppendHatch(hxyz, hargb, ring, z + 0.5, SimProcessPalette.With(rgb, 0x77), AngleOf(p.Process));

            drawn[p.Process] = drawn.GetValueOrDefault(p.Process) + 1;
        }

        _sink.SetLines(EdgeGroup, exyz.ToArray(), eargb.ToArray(), eargb.Count);
        _sink.SetLines(HatchGroup, hxyz.ToArray(), hargb.ToArray(), hargb.Count);
        LastSegments = eargb.Count + hargb.Count;

        // ── 铭牌：每个「工序 × 分组」只给最大的那一块 ──
        //  75 块地各挂一个铭牌会把面板糊满，而铭牌要答的是「这条带是哪道工序的」，
        //  同一道工序同一个面的三小块各写一遍并不多答任何东西。
        var lxyz = new List<double>(); var ltxt = new List<string>();
        var largb = new List<uint>(); var lh = new List<float>();
        foreach (var g in patches.Where(p => p.Region?.Ring.Count >= 3)
                                 .GroupBy(p => (p.Process, p.GroupKey))
                                 .Select(g => g.OrderByDescending(p => p.AreaM2).First())
                                 .OrderByDescending(p => p.AreaM2)
                                 .Take(30))
        {
            var c = g.Region!.Centroid;
            double z = double.IsNaN(g.Region.Z) ? 0 : g.Region.Z;
            lxyz.Add(c.X); lxyz.Add(c.Y); lxyz.Add(z + 4.0);
            ltxt.Add(g.GroupKey.Length > 0 ? $"{g.ProcessName}·{g.GroupKey}" : g.ProcessName);
            largb.Add(SimProcessPalette.With(SimProcessPalette.RgbOfCode(g.Process), 0xFF));
            lh.Add(labelHeightM);
        }
        DeCollide(lxyz, lh);
        _sink.SetLabels(LabelGroup, lxyz.ToArray(), ltxt, largb.ToArray(), lh.ToArray(), null, null, ltxt.Count);
        LastLabels = ltxt.Count;

        _drawn = drawn;
        DropNote = dropped > 0
            ? $"⚠ {dropped} 块工序区没画出来（环不足 3 点或坐标含 NaN）—— 整块丢弃，不截断。"
            : "";

        // 图例只列**这一帧真画了的**工序。列上没画的那几道，等于告诉人"图上应该有"，
        // 而它其实一块地都没有 —— 那是 [[absence-cannot-prove-absence]] 的反面：谎报存在。
        Legend = string.Join("　",
            drawn.OrderBy(kv => SimProcessPalette.OrderOfCode(kv.Key))
                 .Select(kv => $"{Swatch(kv.Key)}{SimProcessPalette.NameOfCode(kv.Key)}"
                             + (SimProcessPalette.DrawOf(kv.Key) == SimProcessDraw.KeepOut ? "(虚线·禁入区)" : "")
                             + $" {kv.Value}块"));
    }

    private static string Swatch(string code) => "■";

    /// <summary>本层的一句话状态（信息栏/状态栏用）。</summary>
    public string StatusOf(SimProcessRegionSet? set)
    {
        if (set == null || set.Patches.Count == 0)
            return "工序作业区：本期一块地都没有 —— 五道工序在图上只能共用作业面的位置，"
                 + "看不出「穿孔超前、爆破在其后、采装再后、排土在另一头」。到「作业区划分」生成并入库即可。";
        int n = _drawn.Values.Sum();
        return $"工序作业区：{set.Period} 画出 {n}/{set.Patches.Count} 块（{LastSegments} 段线 · {LastLabels} 个铭牌）。"
             + DropNote;
    }

    /// <summary>
    /// **重合要说出来**（PS10）。
    ///
    /// <para>月尺度上「这个月要打孔的单元」与「这个月要采的单元」本来就是同一批 ——
    /// 差的只是队头被切掉的那一段（超前 lead 的量）。实测本矿 2026-08：
    /// 11 块穿孔区里 <b>10 块与采装区的环逐点相同</b>，只有一个台阶因为队头切在它中间才小了一圈。</para>
    ///
    /// <para><b>为什么必须写出来</b>：图上两块地严丝合缝地叠着，看图的人只有两种解释 ——
    /// 「画错了」或者「工序根本没错开」。而真相是第三种：<b>它们错开在时间轴上，不在平面图上</b>。
    /// 不说这一句，这一层就把人引向去查一个并不存在的几何 bug（我自己就先去查了生成端）。</para>
    /// </summary>
    public static string CoincidenceNote(SimProcessRegionSet? set)
    {
        if (set == null || set.Patches.Count < 2) return "";
        var byName = set.Patches.GroupBy(p => p.Name ?? "")
                                .Where(g => g.Count() > 1)
                                .ToList();
        int same = 0, near = 0;
        var pairs = new List<string>();
        foreach (var g in byName)
        {
            var list = g.ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[i].Process == list[j].Process) continue;
                    bool identical = SameRing(list[i].Region.Ring, list[j].Region.Ring);
                    if (identical)
                    {
                        same++;
                        string k = $"{list[i].ProcessName}＝{list[j].ProcessName}";
                        if (!pairs.Contains(k)) pairs.Add(k);
                    }
                    else
                    {
                        // 「几乎同一块地」= **质心几乎重合且面积相当**。
                        // ⚠ 只比面积是错的（第一版就是）：两块 100×400 的地相距 300m 面积一模一样，
                        //   却是真真正正错开的两块 —— 判据 PS10b 当场把它抓了出来。
                        //   面积是标量，答不了"在不在同一处"这个问题。
                        var ci = list[i].Region.Centroid; var cj = list[j].Region.Centroid;
                        double d = Math.Sqrt((ci.X - cj.X) * (ci.X - cj.X) + (ci.Y - cj.Y) * (ci.Y - cj.Y));
                        double amax = Math.Max(list[i].AreaM2, list[j].AreaM2);
                        if (d < 1.0 && Math.Abs(list[i].AreaM2 - list[j].AreaM2) <= 0.02 * amax) near++;
                    }
                }
        }
        if (same == 0 && near == 0) return "";
        return $"ⓘ 本期有 {same} 对工序区**是同一块地**（环逐点相同：{string.Join("、", pairs)}）"
             + (near > 0 ? $"，另有 {near} 对质心重合、面积相差不到 2%" : "")
             + " —— 这不是画错：月尺度上这两道工序动的是**同一批采掘单元**，"
             + "差别只在队头被超前期切掉的那一段。它们真正的错开在**时间轴**上，"
             + "看下面的「工序链」那一条带。";
    }

    private static bool SameRing(IReadOnlyList<SimPoint> a, IReadOnlyList<SimPoint> b)
    {
        if (a.Count != b.Count || a.Count == 0) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X - b[i].X) > 1e-6 || Math.Abs(a[i].Y - b[i].Y) > 1e-6) return false;
        return true;
    }

    // ── 几何 ────────────────────────────────────────────────────────────────

    /// <summary>斜纹角度（°）：**按工序错开**，重叠时仍分得出是哪几道（PS-H）。</summary>
    private static double AngleOf(string? code) => (code ?? "").Trim() switch
    {
        ProcessZone.ProcDrill => 0,
        ProcessZone.ProcLoad => 45,
        ProcessZone.ProcDumpTip => -45,
        ProcessZone.ProcDumpDoze => 90,
        _ => 20,
    };

    private static void AppendRing(List<double> xyz, List<uint> argb, IReadOnlyList<SimPoint> ring,
                                   double z, uint color, bool dashed)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];         // 闭合
            if (!dashed)
            {
                xyz.Add(a.X); xyz.Add(a.Y); xyz.Add(z);
                xyz.Add(b.X); xyz.Add(b.Y); xyz.Add(z);
                argb.Add(color);
                continue;
            }
            // 虚线：这一条边切成 4 段画 2 段。按**边长比例**切而不是按固定米数 ——
            // 固定米数在几百米的长边上会生出上百段，而在几米的短边上一段虚线都没有（看着就是实线）。
            const int Cuts = 4;
            for (int k = 0; k < Cuts; k += 2)
            {
                double t0 = k / (double)Cuts, t1 = (k + 1) / (double)Cuts;
                xyz.Add(a.X + (b.X - a.X) * t0); xyz.Add(a.Y + (b.Y - a.Y) * t0); xyz.Add(z);
                xyz.Add(a.X + (b.X - a.X) * t1); xyz.Add(a.Y + (b.Y - a.Y) * t1); xyz.Add(z);
                argb.Add(color);
            }
        }
    }

    /// <summary>
    /// 斜纹填充：把环转到「纹路水平」的坐标系里做扫描线，交点成对连段，再转回来。
    ///
    /// <para><b>为什么不三角化</b>：工序区是栅格反出的环，凹的；扇形三角化会生出穿出边界的长条平面。
    /// 扫描线对凹多边形本来就是对的（交点必成偶数对），且不需要任何三角化库。</para>
    /// <para>纹路条数按块自适应（目标 ~14 条）：写死间距的话，大块（447 万 m²）会生出上万条，
    /// 小块（几百 m²）一条都没有 —— 后者的现象是「这块地看着是空的」。</para>
    /// </summary>
    private static void AppendHatch(List<double> xyz, List<uint> argb, IReadOnlyList<SimPoint> ring,
                                    double z, uint color, double angleDeg)
    {
        int n = ring.Count;
        if (n < 3) return;
        double ca = Math.Cos(-angleDeg * Math.PI / 180.0), sa = Math.Sin(-angleDeg * Math.PI / 180.0);

        var rx = new double[n]; var ry = new double[n];
        double minV = double.MaxValue, maxV = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            rx[i] = ring[i].X * ca - ring[i].Y * sa;
            ry[i] = ring[i].X * sa + ring[i].Y * ca;
            if (ry[i] < minV) minV = ry[i];
            if (ry[i] > maxV) maxV = ry[i];
        }
        double span = maxV - minV;
        if (span <= 1e-6) return;

        const int Lines = 14;
        double step = span / (Lines + 1);

        var xs = new List<double>(8);
        for (int k = 1; k <= Lines; k++)
        {
            double y = minV + step * k;
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double y0 = ry[i], y1 = ry[j];
                // 半开区间判交：顶点正好落在扫描线上时只算一次，否则同一个顶点被两条边各记一笔
                // ⇒ 交点数成奇数 ⇒ 配对整体错位，纹路会横穿到多边形外面。
                if ((y0 <= y && y1 > y) || (y1 <= y && y0 > y))
                {
                    double t = (y - y0) / (y1 - y0);
                    xs.Add(rx[i] + (rx[j] - rx[i]) * t);
                }
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int s = 0; s + 1 < xs.Count; s += 2)
            {
                double x0 = xs[s], x1 = xs[s + 1];
                if (x1 - x0 < 1e-6) continue;
                // 转回世界系：正向是 R(-θ)，逆变换就是它的转置（正交阵）
                xyz.Add(x0 * ca + y * sa); xyz.Add(-x0 * sa + y * ca); xyz.Add(z);
                xyz.Add(x1 * ca + y * sa); xyz.Add(-x1 * sa + y * ca); xyz.Add(z);
                argb.Add(color);
            }
        }
    }

    /// <summary>铭牌竖向去重叠（同一块地里挤在一起的往上错开）。</summary>
    private static void DeCollide(List<double> xyz, List<float> h)
    {
        int n = h.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < i; j++)
            {
                double dx = xyz[i * 3] - xyz[j * 3], dy = xyz[i * 3 + 1] - xyz[j * 3 + 1];
                double dz = xyz[i * 3 + 2] - xyz[j * 3 + 2];
                if (dx * dx + dy * dy < (h[i] * 6) * (h[i] * 6) && Math.Abs(dz) < h[i] * 1.2)
                { xyz[i * 3 + 2] += h[i] * 1.6; j = -1; }
            }
        }
    }
}
