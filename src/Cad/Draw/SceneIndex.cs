using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 场景图元的均匀网格空间索引 —— 点选 / 框选用（纯逻辑，可单测）。
///
/// 为什么需要：点选走的是"对每个图元算一次距离"，框选走的是"把每个图元细分一遍再判框"，
/// 都是 O(全场景)。实测一张 51MB 采剥图（50.7 万图元）单次点选 137 ms、单次框选扫描 118 ms ——
/// 鼠标一动就是这个开销，手感就是"卡"。有了索引，只有落在光标/选框附近格子里的图元才参与计算。
///
/// 结构是 CSR（偏移表 + 条目表），不是每格一个 List：50 万图元要几十万个格子，
/// 每格一个 List 光对象头就上百 MB。跨格太多的大图元（长中线、整张三角网）单独放
/// <see cref="_oversize"/>，每次查询都带上 —— 这类图元本来就没几个。
///
/// 索引不自己盯着场景变化：场景一改就整个丢掉重建（同 MainWindow 里捕捉几何缓存的做法），
/// 因为 <c>Scene.Entities</c> 是公开可变列表，靠版本号盯不住。
/// </summary>
public sealed class SceneIndex
{
    private readonly IReadOnlyList<SceneEntity> _all;
    private readonly double _x0, _y0, _cw, _ch;
    private readonly int _nx, _ny;
    private readonly int[] _start;      // CSR: 每格在 _items 里的起点（长度 nx*ny+1）
    private readonly int[] _items;      // CSR: 图元下标
    private readonly List<int> _oversize = new();   // 跨格过多的图元：每次查询都参与
    private readonly double[] _aabb;    // 每图元 [minX,minY,maxX,maxY]，粗排斥用

    /// <summary>一个图元最多登记到这么多格子；超过就归入 oversize。</summary>
    private const int MaxCellsPerEntity = 64;

    public int EntityCount => _all.Count;
    public int OversizeCount => _oversize.Count;
    public int CellCount => _nx * _ny;

    public SceneIndex(IReadOnlyList<SceneEntity> entities)
    {
        _all = entities ?? Array.Empty<SceneEntity>();
        int n = _all.Count;
        _aabb = new double[Math.Max(1, n) * 4];

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var buf = new List<float>(256);
        for (int i = 0; i < n; i++)
        {
            buf.Clear();
            var (a, b, c, d) = Aabb(_all[i], buf);
            _aabb[i * 4] = a; _aabb[i * 4 + 1] = b; _aabb[i * 4 + 2] = c; _aabb[i * 4 + 3] = d;
            if (a > c) continue;                        // 空图元(无几何)
            if (a < minX) minX = a; if (b < minY) minY = b;
            if (c > maxX) maxX = c; if (d > maxY) maxY = d;
        }
        if (minX > maxX) { minX = minY = 0; maxX = maxY = 1; }

        // 目标每格 ~4 个图元；两轴各不超过 512 格（50 万图元 → 262144 格，CSR 下约 2MB）
        int side = Math.Clamp((int)Math.Sqrt(Math.Max(1, n) / 4.0) + 1, 1, 512);
        _nx = side; _ny = side;
        _x0 = minX; _y0 = minY;
        _cw = Math.Max((maxX - minX) / _nx, 1e-9);
        _ch = Math.Max((maxY - minY) / _ny, 1e-9);

        // 两趟计数排序建 CSR
        var counts = new int[_nx * _ny + 1];
        for (int i = 0; i < n; i++)
            ForEachCell(i, ci => counts[ci + 1]++);
        for (int c = 0; c < _nx * _ny; c++) counts[c + 1] += counts[c];
        _start = counts;
        _items = new int[_start[_nx * _ny]];
        var cursor = new int[_nx * _ny];
        for (int i = 0; i < n; i++)
            ForEachCell(i, ci => _items[_start[ci] + cursor[ci]++] = i);
    }

    /// <summary>
    /// 图元的二维包围盒。按类型走最便宜的那条路 —— 建索引要把全场景过一遍，这里贵一点点就是几秒：
    /// · 三角网 / 点云：直接问它自己的 Bounds（O(1)），别去细分几十万三角 / 百万个点；
    /// · 文字：走 <see cref="TextEntity.ApproxBounds"/>（只按排版步进算，不生成字形几何）。
    ///   有真字体时 <see cref="TextEntity.Tessellate"/> 对可填充字形什么都不出（由实心三角负责），
    ///   拿它算包围盒会得到空盒 → 文字点不中；而走实心三角/轮廓笔画，3.7 万条注记要 1.9~3 秒
    ///   （踩过两次：第一次点选先卡 3.2 秒、改轮廓后仍卡 1.9 秒）。
    /// · 其余：细分线段取极值；线段为空(如着色面模式下的面)再退回三角面。
    /// </summary>
    /// <summary>单个实体的世界 XY 包围盒（供 <see cref="Scene.WorldBoundsXY"/> 复用同一套口径）。</summary>
    internal static (double, double, double, double) WorldAabb(SceneEntity e, List<float> buf) => Aabb(e, buf);

    private static (double, double, double, double) Aabb(SceneEntity e, List<float> buf)
    {
        using var _ro = RenderOrigin.Suspend();   // 索引存世界包围盒，镶嵌须回世界系

        if (e is MeshEntity me)
        {
            if (me.VertexCount == 0) return (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            var mb = me.Bounds;
            return (mb.minX, mb.minY, mb.maxX, mb.maxY);
        }
        if (e is TextEntity te) return te.ApproxBounds();   // 只按排版步进算, 不生成字形几何
        if (e is PointCloudEntity pc)
        {
            // 点云的 Tessellate 是空的(点走 GL_POINTS 专用通道), 按"细分取极值"算出来是空盒 →
            // 点云既进不了索引(点不中), 也进不了 Scene.WorldBoundsXY —— 场景里只有一份点云时
            // 渲染局部原点定不下来, 百万级矿区坐标直接转 float 上 GPU, 点云整份不显示(实测 |x|>1.6 万即消失)。
            if (pc.PointCount == 0) return (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            var pb = pc.Bounds;
            return (pb.minX, pb.minY, pb.maxX, pb.maxY);
        }
        e.Tessellate(buf);
        if (buf.Count == 0) e.TessellateFaces(buf);

        double a = double.MaxValue, b = double.MaxValue, c = double.MinValue, d = double.MinValue;
        for (int i = 0; i + 1 < buf.Count; i += 6)
        {
            float x = buf[i], y = buf[i + 1];
            if (x < a) a = x; if (y < b) b = y; if (x > c) c = x; if (y > d) d = y;
        }
        return (a, b, c, d);
    }

    private void ForEachCell(int i, Action<int> visit)
    {
        double a = _aabb[i * 4], b = _aabb[i * 4 + 1], c = _aabb[i * 4 + 2], d = _aabb[i * 4 + 3];
        if (a > c) return;                                     // 无几何
        int cx0 = Col(a), cx1 = Col(c), cy0 = Row(b), cy1 = Row(d);
        long cells = (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);
        if (cells > MaxCellsPerEntity) { if (!_oversize.Contains(i)) _oversize.Add(i); return; }
        for (int gy = cy0; gy <= cy1; gy++)
            for (int gx = cx0; gx <= cx1; gx++)
                visit(gy * _nx + gx);
    }

    private int Col(double x) => Math.Clamp((int)((x - _x0) / _cw), 0, _nx - 1);
    private int Row(double y) => Math.Clamp((int)((y - _y0) / _ch), 0, _ny - 1);

    /// <summary>与查询框的包围盒相交的图元（粗筛，调用方仍需做精确判定）。</summary>
    public IEnumerable<SceneEntity> Query(double x0, double y0, double x1, double y1)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (y0 > y1) (y0, y1) = (y1, y0);
        int cx0 = Col(x0), cx1 = Col(x1), cy0 = Row(y0), cy1 = Row(y1);

        var seen = new HashSet<int>();
        foreach (int i in _oversize)
            if (Overlaps(i, x0, y0, x1, y1) && seen.Add(i)) yield return _all[i];

        for (int gy = cy0; gy <= cy1; gy++)
            for (int gx = cx0; gx <= cx1; gx++)
            {
                int ci = gy * _nx + gx;
                for (int k = _start[ci]; k < _start[ci + 1]; k++)
                {
                    int i = _items[k];
                    if (Overlaps(i, x0, y0, x1, y1) && seen.Add(i)) yield return _all[i];
                }
            }
    }

    private bool Overlaps(int i, double x0, double y0, double x1, double y1)
    {
        double a = _aabb[i * 4], b = _aabb[i * 4 + 1], c = _aabb[i * 4 + 2], d = _aabb[i * 4 + 3];
        return a <= c && a <= x1 && c >= x0 && b <= y1 && d >= y0;
    }
}
