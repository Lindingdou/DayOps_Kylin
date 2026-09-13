// 忠实移植自原 PitMine3D Modules/RoadLib/Network/SegmentGrid.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 折线段的均匀格索引：格 → 落在其中的 (线号, 段起点扁平下标)。查询只扫邻近几格，
/// 把"逐段对全网络"的 O(N²) 压成近线性。
///
/// <b>为什么单独成文件</b>：<see cref="CenterlineInventory"/>（连通片/悬空端）与
/// <see cref="CenterlineJunctions"/>（交点捕捉）判的是同一批"段与段贴上没有"的关系，
/// 两边各留一份索引早晚会漂成两套格边/两套取整口径 —— 那种漂移不会报错，
/// 只会让"清单说连着、捕捉说没交点"这类对不上的事悄悄发生。
///
/// 索引<b>只按 XY</b> 建（标高闸门由调用方在拿到候选后自己判）：格里混着上下台阶的段是对的，
/// 立交的上下层本来就要先看见、再按 Δz 分开，索引阶段就把它们分掉的话调用方连"这里有个立交"都答不出。
/// </summary>
internal sealed class SegmentGrid
{
    private readonly Dictionary<(int, int), List<(int Line, int Si)>> _cells = new();
    private readonly double _cs;

    private SegmentGrid(double cellSize) => _cs = cellSize;

    /// <summary>
    /// 建索引。<paramref name="minCellM"/> 是格边下限（一般取容差的 2 倍）——
    /// 格边取"平均段长"量级但不小于它：太小 → 一段登记进上百格；太大 → 每格候选爆炸。
    /// </summary>
    public static SegmentGrid Build(IReadOnlyList<double[]> lines, double minCellM)
    {
        double sum = 0;
        int segs = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var f = lines[i];
            if (f == null || f.Length < 6) continue;
            for (int s = 0; s + 5 < f.Length; s += 3)
            {
                double dx = f[s + 3] - f[s], dy = f[s + 4] - f[s + 1];
                sum += Math.Sqrt(dx * dx + dy * dy);
                segs++;
            }
        }
        double cs = Math.Max(Math.Max(minCellM, 5.0), segs > 0 ? sum / segs : 25.0);

        var g = new SegmentGrid(cs);
        for (int i = 0; i < lines.Count; i++)
        {
            var f = lines[i];
            if (f == null || f.Length < 6) continue;
            for (int s = 0; s + 5 < f.Length; s += 3)
                g.Register(i, s, f[s], f[s + 1], f[s + 3], f[s + 4]);
        }
        return g;
    }

    private void Register(int line, int si, double ax, double ay, double bx, double by)
    {
        int x0 = Cell(Math.Min(ax, bx)), x1 = Cell(Math.Max(ax, bx));
        int y0 = Cell(Math.Min(ay, by)), y1 = Cell(Math.Max(ay, by));
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                var k = (cx, cy);
                if (!_cells.TryGetValue(k, out var l)) { l = new List<(int, int)>(); _cells[k] = l; }
                l.Add((line, si));
            }
    }

    private int Cell(double v) => (int)Math.Floor(v / _cs);

    /// <summary>点周围 <paramref name="radius"/> 内的候选段（含重复，调用方自己判距）。</summary>
    public IEnumerable<(int Line, int Si)> Query(double x, double y, double radius)
        => QueryBox(x - radius, y - radius, x + radius, y + radius);

    /// <summary>与给定包围盒相交的格里的候选段（含重复）。</summary>
    public IEnumerable<(int Line, int Si)> QueryBox(double xmin, double ymin, double xmax, double ymax)
    {
        int x0 = Cell(xmin), x1 = Cell(xmax);
        int y0 = Cell(ymin), y1 = Cell(ymax);
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
                if (_cells.TryGetValue((cx, cy), out var l))
                    foreach (var it in l) yield return it;
    }
}
