// 忠实移植自原 PitMine3D Modules/RoadLib/Evolution/GeometryIndex.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 一期中线的匹配索引：回答「本期这个采样点，在<b>对期</b>有没有对应的路面」。
///
/// <b>为什么不是"找最近那条再看角度"</b>（RE2）：老写法先取全局最近的一条线拿它的切向判角度，
/// 于是路口和接缝处必错 —— 最近的那条是横穿过去的另一条路，角度不合，这个点就被判成"新增"，
/// 哪怕容差内明明还躺着一条同向的对期中线。对着真实存档量过：判成"变了"的边排行榜前十名
/// 全是「未覆盖 4m」，正好两个采样点，全是接缝伪影（见 <c>RoadEvolutionDiagnosticTests</c> C13/C15/C17）。
/// 正确口径是<b>在"容差内 + 同向"的候选里取最近</b>，没有这样的候选才算新增。
///
/// <b>端外闸</b>（RE2b）：采样点超出对期折线端点之外时，最近点会黏在端点上，距离仍 ≤ 容差 ——
/// 于是端头长出来的头一个容差带宽（默认 8m）会被误判成"覆盖"，延拓量固定少报一个容差。
/// 这里按沿切向的超出量单独挡掉：超出 &gt; <paramref name="overshootTolM"/> 即判端外，不算覆盖。
///
/// 索引只按 XY（同 <see cref="SegmentGrid"/>），把逐点对全网的 O(N²) 压成近线性。
/// </summary>
internal sealed class GeometryIndex
{
    private readonly List<double[]> _flat = new();
    private readonly SegmentGrid _grid;

    private GeometryIndex(List<double[]> flat, SegmentGrid grid)
    {
        _flat = flat;
        _grid = grid;
    }

    public int LineCount => _flat.Count;

    public static GeometryIndex Build(IReadOnlyList<IReadOnlyList<Point3d>> lines, double tolM)
    {
        var flat = new List<double[]>(lines.Count);
        foreach (var pl in lines)
        {
            var f = new double[Math.Max(pl.Count, 0) * 3];
            for (int i = 0; i < pl.Count; i++) { f[3 * i] = pl[i].X; f[3 * i + 1] = pl[i].Y; f[3 * i + 2] = pl[i].Z; }
            flat.Add(f);
        }
        return new GeometryIndex(flat, SegmentGrid.Build(flat, tolM * 2));
    }

    /// <summary>一次查询的结果。</summary>
    internal readonly record struct Hit(bool Matched, int LineIndex, double DistanceM, double SignedOffsetM);

    /// <summary>
    /// 查采样点 <paramref name="p"/>（走向 <paramref name="tx"/>,<paramref name="ty"/>）在本索引里的对应路面。
    /// 命中 = 容差内、走向夹角合格、且不在对期线的端外。
    /// </summary>
    public Hit Match(in Point3d p, double tx, double ty, double tolM, double cosTol, double overshootTolM)
    {
        double best = double.MaxValue;
        int bestLine = -1;
        double bestOff = 0;

        foreach (var (li, si) in _grid.Query(p.X, p.Y, tolM))
        {
            var f = _flat[li];
            if (si + 5 >= f.Length) continue;

            double ax = f[si], ay = f[si + 1], bx = f[si + 3], by = f[si + 4];
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12) continue;
            double len = Math.Sqrt(len2);
            double ux = dx / len, uy = dy / len;

            // 走向闸：与采样点走向夹角超阈值 → 这是横穿的另一条路，不是同一条。
            double dot = ux * tx + uy * ty;
            if (Math.Abs(dot) < cosTol) continue;

            double t = ((p.X - ax) * dx + (p.Y - ay) * dy) / len2;
            double tc = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = ax + tc * dx, qy = ay + tc * dy;
            double ex = p.X - qx, ey = p.Y - qy;
            double d = Math.Sqrt(ex * ex + ey * ey);
            if (d > tolM || d >= best) continue;

            // 端外闸：最近点黏在整条线的首/末顶点上，且沿切向超出太多 → 采样点在对期线之外。
            if (t < 0 && si == 0)
            {
                double over = -t * len;                       // 沿反向超出量
                if (over > overshootTolM) continue;
            }
            else if (t > 1 && si + 8 >= f.Length)             // si 是最后一段（下一段起点已越界）
            {
                double over = (t - 1) * len;
                if (over > overshootTolM) continue;
            }

            // 带符号横移：把对期切向翻到与本期走向同侧，正 = 采样点在本期走向左侧。
            double sx = dot >= 0 ? ux : -ux, sy = dot >= 0 ? uy : -uy;
            best = d;
            bestLine = li;
            bestOff = sx * ey - sy * ex;
        }

        return bestLine >= 0
            ? new Hit(true, bestLine, best, bestOff)
            : new Hit(false, -1, double.NaN, 0);
    }
}
