// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneFaceBinding.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;                        // FaceInput
using PitMine3D.Kylin.TaskLib.Simulation;                    // SimPlanScene.NameHit

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  区域 ↔ 作业面：**按位置绑，名字只是最后一条退路**。
//
//  ══ 之前是纯字符串 ══
//  推演与「作业区划分」的诊断都走 `SimPlanScene.NameHit` —— 一个「互相包含」的字符串判据，
//  **一个坐标都不用**。于是界面上常年是这一句：
//        ④ 绑定：名字「采场1」对不上盘子里任何作业面的「作业面/采区」或「工程位置」
//  而两边**都有坐标**：区域有环（真 xyz），作业面有源端代表点（铲位/装载点）。
//  靠名字对号的代价是：谁改了个名字，这块地就整期不动，画面上只显示「本期无采掘」，不报错。
//
//  ══ 三条路，按可靠性排 ══
//  **B1 源点落在环内** —— 最硬的证据：这个面的铲就站在这块地里。
//  **B2 源点在环外但在容差内** —— 铲位常常标在坡顶线外侧几十米；容差要报出来，
//      而且**必须有上限**：不设上限时最近的那个面无论多远都会被配上。
//  **B3 名字** —— 前两条都不成立时的退路。名字对得上不代表地对得上，所以排最后。
//
//  ══ 两条纪律 ══
//  **B4 走了哪条路必须说出来。** 「按位置配上的」与「按名字配上的」在界面上长得一样，
//      而后者随时会因为有人改名而断 —— 看的人有权知道自己靠的是哪一条。
//  **B5 配上多个不挑一个。** 一块地上真的可能有两个面（上下台阶）。
//      随便挑一个的话，另一个面的量整期不进这块地，而两边各自都"看着对"。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>区域配到作业面走的哪条路。</summary>
public enum BindRoute
{
    /// <summary>没配上。</summary>
    None = 0,
    /// <summary>源点落在区域环内 —— 最硬。</summary>
    Inside = 1,
    /// <summary>源点在环外但在容差内。</summary>
    Near = 2,
    /// <summary>只对上了名字（<b>名字对得上不代表地对得上</b>）。</summary>
    Name = 3,
}

/// <summary>一次配对。</summary>
public sealed class ZoneFaceHit
{
    public FaceInput Face = null!;
    public BindRoute Route;
    /// <summary>源点到环的距离 m（<see cref="BindRoute.Inside"/> 时为 0，名字路为 NaN）。</summary>
    public double DistanceM = double.NaN;

    public string RouteZh => Route switch
    {
        BindRoute.Inside => "位置（铲位在区内）",
        BindRoute.Near => $"位置（铲位在区外 {DistanceM:0} m，容差内）",
        BindRoute.Name => "名字（**位置对不上，只是名字像**）",
        _ => "没配上",
    };
}

/// <summary>一次匹配的结果。</summary>
public sealed class ZoneFaceBindResult
{
    public List<ZoneFaceHit> Hits = new();
    public List<string> Notes = new();
    public bool Ok => Hits.Count > 0;

    /// <summary>全部命中都是靠名字来的 —— 这种绑定随时会因为改名而断。</summary>
    public bool NameOnly => Hits.Count > 0 && Hits.All(h => h.Route == BindRoute.Name);
}

/// <summary>区域 ↔ 作业面的位置优先匹配。纯几何，不碰数据库。</summary>
public static class ZoneFaceBinding
{
    /// <summary>源点在环外多远之内仍然算这块地的（m）。<b>必须有上限</b>。</summary>
    public const double DefaultToleranceM = 150;

    /// <summary>
    /// 把作业面配到一块区域上。
    /// </summary>
    /// <param name="ring">区域环（世界坐标）。</param>
    /// <param name="zoneName">区域名（名字那条退路用）。</param>
    /// <param name="faces">候选作业面。</param>
    /// <param name="toleranceM">容差；&lt;=0 用缺省。</param>
    public static ZoneFaceBindResult Match(IReadOnlyList<ZonePoint>? ring, string zoneName,
                                           IEnumerable<FaceInput>? faces, double toleranceM = 0)
    {
        var res = new ZoneFaceBindResult();
        var list = (faces ?? Array.Empty<FaceInput>()).Where(f => f != null).ToList();
        if (list.Count == 0) { res.Notes.Add("盘子里一个作业面都没有。"); return res; }

        double tol = toleranceM > 0 ? toleranceM : DefaultToleranceM;
        var poly = (ring ?? Array.Empty<ZonePoint>()).Select(p => (p.X, p.Y)).ToList();

        int noPos = list.Count(f => !f.HasSourcePosition);

        if (poly.Count >= 3)
        {
            // B1：源点落在环内
            var inside = list.Where(f => f.HasSourcePosition && Contains(poly, f.SourceX, f.SourceY))
                             .Select(f => new ZoneFaceHit { Face = f, Route = BindRoute.Inside, DistanceM = 0 })
                             .ToList();
            if (inside.Count > 0)
            {
                res.Hits.AddRange(inside);
                Report(res, zoneName, noPos);
                return res;
            }

            // B2：环外但在容差内 —— **必须有上限**，否则最近的那个面无论多远都会被配上
            var near = list.Where(f => f.HasSourcePosition)
                           .Select(f => new ZoneFaceHit
                           {
                               Face = f, Route = BindRoute.Near,
                               DistanceM = DistanceToRing(poly, f.SourceX, f.SourceY),
                           })
                           .Where(h => h.DistanceM <= tol)
                           .OrderBy(h => h.DistanceM)
                           .ToList();
            if (near.Count > 0)
            {
                res.Hits.AddRange(near);
                Report(res, zoneName, noPos);
                return res;
            }
        }
        else res.Notes.Add($"区域「{zoneName}」的环不足 3 个点，位置这条路走不了，只能退名字。");

        // B3：名字 —— 最后一条退路
        var byName = list.Where(f => SimPlanScene.NameHit(zoneName, f.Zone)
                                  || SimPlanScene.NameHit(zoneName, f.EngineeringPositionId))
                         .Select(f => new ZoneFaceHit { Face = f, Route = BindRoute.Name })
                         .ToList();
        res.Hits.AddRange(byName);
        Report(res, zoneName, noPos);
        return res;
    }

    private static void Report(ZoneFaceBindResult res, string zoneName, int noPos)
    {
        // B4：走了哪条路必须说出来
        if (res.Hits.Count == 0)
        {
            res.Notes.Add($"◆ 区域「{zoneName}」**位置与名字都对不上任何作业面** —— 这块地整期不动。"
                        + "位置对不上说明本期没有面在这儿作业；名字也对不上说明改名也救不了。");
        }
        else if (res.NameOnly)
        {
            res.Notes.Add($"◆ 区域「{zoneName}」是**靠名字**配上的，位置对不上 —— "
                        + "名字对得上不代表地对得上，而这种绑定**谁改个名字就断**。"
                        + "要让它稳，得让那个面的源端坐标（铲位/装载点）落进这块地里。");
        }
        else
        {
            var route = res.Hits[0].RouteZh;
            res.Notes.Add($"· 区域「{zoneName}」按{route}配到 {res.Hits.Count} 个作业面"
                        + $"（{string.Join("、", res.Hits.Select(h => h.Face.Zone).Take(4))}）。");
        }

        // B5：配上多个不挑一个
        if (res.Hits.Count > 1)
            res.Notes.Add($"· 本区配上 {res.Hits.Count} 个面 —— **全都保留，不挑一个**："
                        + "一块地上真的可能有两个面（上下台阶）。随便挑一个的话，"
                        + "另一个面的量整期不进这块地，而两边各自都看着对。");

        if (noPos > 0)
            res.Notes.Add($"· 有 {noPos} 个作业面**没有源端坐标**，它们只能走名字那条路。"
                        + "补法：在「作业面台账」上录铲位坐标，或让面从本期工序作业区派生"
                        + "（派生的面自带工序区质心）。");
    }

    // ── 几何小件 ──────────────────────────────────────────────────────

    /// <summary>射线法：点在环内。</summary>
    internal static bool Contains(IReadOnlyList<(double X, double Y)> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring[i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi + double.Epsilon) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>点到环（**边**，不是顶点）的最短距离。</summary>
    internal static double DistanceToRing(IReadOnlyList<(double X, double Y)> ring, double x, double y)
    {
        if (ring.Count == 0) return double.NaN;
        if (ring.Count == 1) return Math.Sqrt(Sq(ring[0].X - x) + Sq(ring[0].Y - y));

        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            // 到顶点的距离是错的：细长区域上，点可能离每个顶点都很远却贴着边
            double d = PointToSegment(x, y, ring[j].X, ring[j].Y, ring[i].X, ring[i].Y);
            if (d < best) best = d;
        }
        return best;
    }

    private static double PointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Math.Sqrt(Sq(px - ax) + Sq(py - ay));
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        return Math.Sqrt(Sq(px - (ax + t * dx)) + Sq(py - (ay + t * dy)));
    }

    private static double Sq(double v) => v * v;
}
