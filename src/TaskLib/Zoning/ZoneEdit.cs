// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneEdit.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;              // RingOffset / SimPoint / SimAdvanceMode

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  作业区域的手工调整 —— 自动生成给的是「本期计划的那块地」，
//  现场还会有自动化拿不到的东西：临时道路、保护煤柱、边坡观测点、今天被雨冲了的一角。
//  所以自动生成之后必须能改，而且改的只能是**几何**。
//
//  **Z12 手工调整不改身份**：拖顶点 / 插点 / 删点 / 整体平移 / 等距外扩内缩，
//  改的都只是 points_json。名字、类别、选定、id 一律不动 ——
//  区域的身份挂在名字和 id 上（推演按名字配源、路网按 id 引用），
//  换几何时顺手把名字也换了，等于把这块区域从别处的引用里悄悄摘走。
//
//  **Z13 每一次调整都要说得出改了什么**：面积从多少变到多少、顶点数变没变、
//  Z 是沿用的还是重新解的。区域边界是会被人拿去量工程量的东西，
//  "拖了一下、没说清拖成什么样"是这里最容易埋的账。
//
//  纯函数：不碰数据库、不碰界面，可离线判据。入库由调用方走 <see cref="ZoneStore.UpdateRing"/>。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次几何调整的结果。</summary>
public sealed class ZoneEditResult
{
    public bool Ok { get; init; }
    /// <summary>调整后的环（Ok=false 时为空 —— <b>不返回半份几何</b>）。</summary>
    public List<ZonePoint> Ring { get; init; } = new();
    /// <summary>人读说明（改了什么、面积变了多少）。成功失败都有话说。</summary>
    public string Message { get; init; } = "";
    /// <summary>Z 出处的补充说明；空 = 沿用原来那一段。</summary>
    public string Provenance { get; init; } = "";

    public static ZoneEditResult Fail(string why) => new() { Ok = false, Message = why };
}

/// <summary>区域边界的手工调整（纯几何）。</summary>
public static class ZoneEdit
{
    /// <summary>成环的最少顶点数。</summary>
    public const int MinVertices = 3;

    /// <summary>移动一个顶点到新的平面位置。<b>Z 原样保留</b>（横向挪几米不改变它的高程出处）。</summary>
    public static ZoneEditResult MoveVertex(IReadOnlyList<ZonePoint> ring, int index, double x, double y)
    {
        if (ring == null || ring.Count < MinVertices) return ZoneEditResult.Fail("环不成立。");
        if (index < 0 || index >= ring.Count) return ZoneEditResult.Fail("顶点下标越界。");
        if (double.IsNaN(x) || double.IsNaN(y)) return ZoneEditResult.Fail("新位置不是有效坐标。");

        var next = ring.ToList();
        var old = next[index];
        next[index] = new ZonePoint(x, y, old.Z);
        double moved = Math.Sqrt((x - old.X) * (x - old.X) + (y - old.Y) * (y - old.Y));
        return new ZoneEditResult
        {
            Ok = true,
            Ring = next,
            Message = $"第 {index + 1} 个顶点移了 {moved:0.##} m，面积 {Area(ring) / 1e4:0.##} → {Area(next) / 1e4:0.##} 万m²"
                    + "（Z 原样保留）",
        };
    }

    /// <summary>
    /// 在第 <paramref name="edgeIndex"/> 条边（顶点 i → i+1）上插一个点。
    /// <b>Z 按该边两端线性内插</b> —— 不去现状面重采：插点是把一条直边拆成两段，
    /// 拆出来的点本来就在原边界面上，跑去采一个地表 Z 反而会让边界起伏。
    /// </summary>
    public static ZoneEditResult InsertVertex(IReadOnlyList<ZonePoint> ring, int edgeIndex, double x, double y)
    {
        if (ring == null || ring.Count < MinVertices) return ZoneEditResult.Fail("环不成立。");
        if (edgeIndex < 0 || edgeIndex >= ring.Count) return ZoneEditResult.Fail("边下标越界。");

        var a = ring[edgeIndex];
        var b = ring[(edgeIndex + 1) % ring.Count];
        double ex = b.X - a.X, ey = b.Y - a.Y;
        double len2 = ex * ex + ey * ey;
        double t = len2 <= 1e-12 ? 0 : Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / len2, 0, 1);
        double z = a.Z + (b.Z - a.Z) * t;

        var next = ring.ToList();
        next.Insert(edgeIndex + 1, new ZonePoint(x, y, z));
        return new ZoneEditResult
        {
            Ok = true,
            Ring = next,
            Message = $"在第 {edgeIndex + 1} 条边上插了一个点（{ring.Count} → {next.Count} 个顶点，"
                    + $"Z={z:0.##}m 按该边两端内插）",
        };
    }

    /// <summary>删一个顶点。<b>剩不到 3 个就拒绝</b>（少一个点整块区域会被推演静默跳过）。</summary>
    public static ZoneEditResult DeleteVertex(IReadOnlyList<ZonePoint> ring, int index)
    {
        if (ring == null || ring.Count < MinVertices) return ZoneEditResult.Fail("环不成立。");
        if (index < 0 || index >= ring.Count) return ZoneEditResult.Fail("顶点下标越界。");
        if (ring.Count <= MinVertices)
            return ZoneEditResult.Fail($"只剩 {ring.Count} 个顶点了，再删就不成区域 —— "
                                     + "顶点少于 3 的区域会被推演装载时**静默跳过**（台账里看得见、推演里没有）。");

        var next = ring.ToList();
        next.RemoveAt(index);
        return new ZoneEditResult
        {
            Ok = true,
            Ring = next,
            Message = $"删了第 {index + 1} 个顶点（{ring.Count} → {next.Count} 个顶点，"
                    + $"面积 {Area(ring) / 1e4:0.##} → {Area(next) / 1e4:0.##} 万m²）",
        };
    }

    /// <summary>整体平移。形状与面积不变，Z 原样带走。</summary>
    public static ZoneEditResult Translate(IReadOnlyList<ZonePoint> ring, double dx, double dy)
    {
        if (ring == null || ring.Count < MinVertices) return ZoneEditResult.Fail("环不成立。");
        if (double.IsNaN(dx) || double.IsNaN(dy)) return ZoneEditResult.Fail("平移量不是有效数字。");
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return ZoneEditResult.Fail("没有移动。");

        var next = ring.Select(p => new ZonePoint(p.X + dx, p.Y + dy, p.Z)).ToList();
        return new ZoneEditResult
        {
            Ok = true,
            Ring = next,
            Message = $"整体平移 {Math.Sqrt(dx * dx + dy * dy):0.##} m（ΔX {dx:+0.##;-0.##;0}、ΔY {dy:+0.##;-0.##;0}）"
                    + "—— 形状与面积不变，**Z 原样带走**：平移后的绝对高程不再对应地表，"
                    + "挪动距离大时要重新核一下高程。",
            Provenance = Math.Sqrt(dx * dx + dy * dy) > 1
                ? $"（边界整体平移过 {Math.Sqrt(dx * dx + dy * dy):0.#}m，Z 沿用平移前的值）" : "",
        };
    }

    /// <summary>
    /// 等距外扩 / 内缩。走推演那条 <see cref="RingOffset"/>（同一套斜接限幅），不另写一套偏移。
    ///
    /// <para><b>Z 按最近的原顶点取</b>：偏移会在拐角处插点/并点，下标对不上，
    /// 所以按平面最近邻搬 Z，而不是靠下标。出处里如实写"没有重新采高程"。</para>
    /// <para><b>缩过头就拒绝</b>：<see cref="RingOffset"/> 在自交时会退化成一个收口小环
    /// （那是推演表达"采空"用的），当成编辑结果存回台账就是把区域悄悄缩成一个点。</para>
    /// </summary>
    /// <param name="distM">距离 m，&gt;0 外扩、&lt;0 内缩。</param>
    public static ZoneEditResult OffsetRing(IReadOnlyList<ZonePoint> ring, double distM)
    {
        if (ring == null || ring.Count < MinVertices) return ZoneEditResult.Fail("环不成立。");
        if (double.IsNaN(distM) || double.IsInfinity(distM)) return ZoneEditResult.Fail("距离不是有效数字。");
        if (Math.Abs(distM) < 1e-6) return ZoneEditResult.Fail("距离是 0，没有改动。");

        // 先把源环摆成 CCW —— RingOffset 内部也会摆一次（SignedArea<0 就 Reverse），
        // 自己先摆好，返回的顶点才与这里的下标一一对应，下面那条方向判据才成立。
        var src = ring.Select(p => new SimPoint(p.X, p.Y)).ToList();
        if (Signed(src) < 0) src.Reverse();

        var moved = RingOffset.Offset(src, Math.Abs(distM), distM > 0, SimAdvanceMode.Uniform, 0);
        if (moved.Count < MinVertices) return ZoneEditResult.Fail("偏移后环退化了，没有改动。");

        double a0 = Area(ring), a1 = AreaOf(moved);
        if (distM < 0 && (a1 <= 0 || (a0 > 1e-6 && a1 / a0 < 0.10)))
            return ZoneEditResult.Fail(
                $"内缩 {Math.Abs(distM):0.##} m 会把这块区域缩掉 {(1 - a1 / Math.Max(1e-9, a0)) * 100:0}%（自交/收口）—— "
              + "没有改动。要缩这么多，直接重画一块更靠谱。");

        // ── 翻面：**面积判不出来** ──
        // 100m 见方的区域内缩 80m，四个角各朝对角挪 80m ⇒ 左边界跑到右边界的右侧，
        // 环被里外翻了个个儿。可它仍然是个规整方块：面积 3600 m²（剩 36%）、绕向照旧为正，
        // RingOffset 的收口守卫（面积翻号 或 <2%）与上面那条面积比**两条都判不出来**。
        // 唯一判得出来的是**逐边方向**：翻面时每条边的走向都反了。
        if (moved.Count == src.Count)
            for (int i = 0; i < src.Count; i++)
            {
                var o = src[(i + 1) % src.Count] - src[i];
                var m = moved[(i + 1) % moved.Count] - moved[i];
                if (o.X * m.X + o.Y * m.Y < 0)
                    return ZoneEditResult.Fail(
                        $"{(distM > 0 ? "外扩" : "内缩")} {Math.Abs(distM):0.##} m 会把边界翻过去"
                      + $"（第 {i + 1} 条边的走向反了，多半是内缩超过了本区的半宽）—— 没有改动。"
                      + "面积看着还剩不少，但那是个里外翻转的环：存回台账后推进轮廓会朝反方向走。");
            }

        var next = new List<ZonePoint>(moved.Count);
        foreach (var p in moved) next.Add(new ZonePoint(p.X, p.Y, NearestZ(ring, p.X, p.Y)));

        return new ZoneEditResult
        {
            Ok = true,
            Ring = next,
            Message = $"等距{(distM > 0 ? "外扩" : "内缩")} {Math.Abs(distM):0.##} m："
                    + $"面积 {a0 / 1e4:0.##} → {a1 / 1e4:0.##} 万m²（{(a1 - a0) / Math.Max(1e-9, a0) * 100:+0.#;-0.#;0}%）、"
                    + $"顶点 {ring.Count} → {next.Count} 个",
            Provenance = $"（在原边界上等距{(distM > 0 ? "外扩" : "内缩")} {Math.Abs(distM):0.#}m，"
                       + "Z 取自原边界最近顶点、未重新采）",
        };
    }

    /// <summary>
    /// 换一整条环（重画），<b>只换几何</b>。新环的 Z 由调用方解好（走 <see cref="ZoneElevation.Resolve"/>）。
    /// </summary>
    public static ZoneEditResult Replace(IReadOnlyList<ZonePoint> oldRing, IReadOnlyList<ZonePoint> newRing)
    {
        if (newRing == null || newRing.Count < MinVertices)
            return ZoneEditResult.Fail($"新边界只有 {newRing?.Count ?? 0} 个顶点，至少要 3 个。");

        double a0 = oldRing == null ? 0 : Area(oldRing), a1 = Area(newRing);
        return new ZoneEditResult
        {
            Ok = true,
            Ring = newRing.ToList(),
            Message = $"边界已换成新画的这一条：面积 {a0 / 1e4:0.##} → {a1 / 1e4:0.##} 万m²、"
                    + $"顶点 {oldRing?.Count ?? 0} → {newRing.Count} 个（名称/类别/选定状态不动）",
        };
    }

    // ── 命中测试（界面拖拽用，放这儿是为了能离线判据）────────────────────

    /// <summary>离 (x,y) 最近的顶点下标；超过 <paramref name="tolM"/> 返回 −1。</summary>
    public static int HitVertex(IReadOnlyList<ZonePoint> ring, double x, double y, double tolM)
    {
        int best = -1; double bestD = tolM * tolM;
        for (int i = 0; i < ring.Count; i++)
        {
            double d = (ring[i].X - x) * (ring[i].X - x) + (ring[i].Y - y) * (ring[i].Y - y);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>离 (x,y) 最近的边下标（顶点 i → i+1）；超过 <paramref name="tolM"/> 返回 −1。</summary>
    public static int HitEdge(IReadOnlyList<ZonePoint> ring, double x, double y, double tolM)
    {
        int best = -1; double bestD = tolM * tolM;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double len2 = ex * ex + ey * ey;
            double t = len2 <= 1e-12 ? 0 : Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / len2, 0, 1);
            double px = a.X + ex * t, py = a.Y + ey * t;
            double d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ── 小件 ────────────────────────────────────────────────────────

    /// <summary>多边形面积 m²（绝对值）。</summary>
    public static double Area(IReadOnlyList<ZonePoint> ring)
    {
        if (ring == null || ring.Count < 3) return 0;
        double a = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a / 2);
    }

    private static double AreaOf(IReadOnlyList<SimPoint> ring) => Math.Abs(Signed(ring));

    /// <summary>带符号面积（正 = CCW）。</summary>
    private static double Signed(IReadOnlyList<SimPoint> ring)
    {
        if (ring.Count < 3) return 0;
        double a = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2;
    }

    private static double NearestZ(IReadOnlyList<ZonePoint> ring, double x, double y)
    {
        double best = ring[0].Z, bestD = double.MaxValue;
        foreach (var p in ring)
        {
            double d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
            if (d < bestD) { bestD = d; best = p.Z; }
        }
        return best;
    }
}
