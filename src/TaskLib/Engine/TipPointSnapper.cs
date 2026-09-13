// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/TipPointSnapper.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Cad.Road;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  卸载点吸附到路面（TP 组，2026-08-20）。
//
//  ══ 为什么块体重心不能直接当卸点 ══
//  卡车卸在**块体靠路的那一侧**，不是块体的几何中心。中心落在排土场内部，
//  离运输道路可能几百米 —— 按容差吸附时吸不上，整条腿就落回兜底运距。
//  实测：代表点从"全场重心"换成"本期在排块重心"之后，路网腿 16 → 8 条，
//  少的那些不是没有路，是**点不在路上**。
//
//  ══ 做法 ══
//  把点**投影到最近的路面中线上**（不是吸到最近的节点 —— 节点只长在中线端点上，
//  真实路网里节点稀疏，吸节点会把卸点挪到几百米外的一个路口）。
//  投影点 = 点到各条边中线的最近点；超出 <see cref="MaxSnapM"/> 就不吸（那是真的没路）。
//
//  ══ 两条纪律 ══
//   TP1 **吸路面不吸节点**（这条在路网那边栽过一次：中位 67m 的偏差）。
//   TP2 **吸不上就不吸**，并把距离报出来 —— 硬吸到几百米外的一条路上，
//       运距会算得又准又假：数字有了，而那条路根本到不了这个卸点。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>把卸载点吸到最近的路面上。纯计算（只读路网存档），永不抛。</summary>
public static class TipPointSnapper
{
    /// <summary>最远吸附距离（m）。超过就是真没路，不硬吸。★工程缺省。</summary>
    public const double MaxSnapM = 800;

    /// <summary>吸附结果。</summary>
    public readonly struct Snapped
    {
        public Snapped(double x, double y, double z, double distM, bool ok)
        { X = x; Y = y; Z = z; DistanceM = distM; Ok = ok; }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        /// <summary>原点到路面的距离（m）—— 它就是"这个卸点离路多远"。</summary>
        public double DistanceM { get; }

        /// <summary>吸上了（距离在 <see cref="MaxSnapM"/> 内）。</summary>
        public bool Ok { get; }
    }

    /// <summary>
    /// 把 (x,y,z) 投到最近的路面中线上。
    /// <para>路网读不到、或最近的路超过 <see cref="MaxSnapM"/>：返回原点并 <c>Ok=false</c>。</para>
    /// </summary>
    public static Snapped Snap(double x, double y, double z, double maxSnapM = MaxSnapM)
    {
        try { return SnapCore(x, y, z, maxSnapM); }
        // 永不抛：吸不上就还它原样。这一步抛出去会把**整条去向重建**带下水
        // （调用方在 SafeInt 里，异常被吞掉之后连"补了几个"都不会说一声）。
        catch { return new Snapped(x, y, z, double.PositiveInfinity, false); }
    }

    private static Snapped SnapCore(double x, double y, double z, double maxSnapM)
    {
        RoadGraph? g = LoadGraph();
        if (g == null || g.EdgeCount == 0) return new Snapped(x, y, z, double.PositiveInfinity, false);

        double bestD2 = double.MaxValue, bx = x, by = y, bz = z;
        foreach (var e in g.Edges)
        {
            var pl = e?.Centerline;
            if (pl == null || pl.Count < 2) continue;
            for (int i = 1; i < pl.Count; i++)
            {
                ClosestOnSeg(x, y, pl[i - 1].X, pl[i - 1].Y, pl[i].X, pl[i].Y,
                             out double px, out double py, out double t);
                double d2 = (px - x) * (px - x) + (py - y) * (py - y);
                if (d2 < bestD2)
                {
                    bestD2 = d2; bx = px; by = py;
                    bz = pl[i - 1].Z + (pl[i].Z - pl[i - 1].Z) * t;   // 高程沿线插值
                }
            }
        }

        double dist = Math.Sqrt(bestD2);
        return dist <= maxSnapM
            ? new Snapped(Math.Round(bx, 3), Math.Round(by, 3), Math.Round(bz, 3), Math.Round(dist, 1), true)
            : new Snapped(x, y, z, Math.Round(dist, 1), false);      // TP2：吸不上就还它原样
    }

    // ── 内部 ──────────────────────────────────────────────────────────

    private static RoadGraph? _graph;
    private static bool _tried;

    /// <summary>判据用：忘掉缓存的路网。</summary>
    internal static void ResetForTest() { _graph = null; _tried = false; }

    /// <summary>判据用：直接喂一张图（不碰库）。</summary>
    internal static void SetGraphForTest(RoadGraph? g) { _graph = g; _tried = true; }

    private static RoadGraph? LoadGraph()
    {
        if (_tried) return _graph;
        _tried = true;
        try
        {
            var archive = EquipmentDataContext.RoadNetworks.All().FirstOrDefault();
            if (archive == null || string.IsNullOrWhiteSpace(archive.GraphJson)) return _graph = null;
            return _graph = RoadGraphSerializer.FromJson(archive.GraphJson);
        }
        catch { return _graph = null; }
    }

    /// <summary>点到线段的最近点（平面）；<paramref name="t"/> 是它在段上的参数，用来插高程。</summary>
    private static void ClosestOnSeg(double px, double py, double ax, double ay, double bx, double by,
                                     out double cx, out double cy, out double t)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        if (len2 <= 1e-12) { cx = ax; cy = ay; t = 0; return; }
        t = ((px - ax) * dx + (py - ay) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        cx = ax + dx * t;
        cy = ay + dy * t;
    }
}
