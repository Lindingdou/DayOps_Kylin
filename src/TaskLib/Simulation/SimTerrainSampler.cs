// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimTerrainSampler.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  现状面 Z 采样 —— 让「只有平面 XY 的区域轮廓」拿到真实高程，而不是一律摆在 0m。
//
//  区域轮廓的高程有三层来源（见 SimRegionLoader.ResolveElevations）：
//    ① 可采区域台账 mineable_region.points_json 存的是**扁平 xyz**，直接有真高程；
//    ② 台账拿不到（走 IPitDesignCapability 只给 XY）→ 本类：把轮廓点做 XY 投影，
//       落在文档现状面三角网的哪个三角形里，就按**重心坐标**插值出该点的地表 Z；
//    ③ 都不行 → 0m 兜底，并保留警示。
//
//  为什么要做 ②：层体的形状与体积只跟轮廓和台阶高有关，摆在 0m 也是对的形状；
//  但摆在 0m 的层体会**穿到现状面下面几百米**，图上根本对不上，看图的人会以为几何错了。
//  能插值出地表 Z，层体就落在该落的位置。插值值不是实测值，来源必须如实标注。
//
//  性能：现状面动辄几十万面，而轮廓点只有几十~几百个。所以建一次**均匀网格索引**
//  （按三角形 XY 包围盒登记到格子里），再逐点查格子 —— O(F + P·k)，不做 P×F 的暴力扫。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 文档现状面（三角网）的 XY→Z 采样器。取不到面时 <see cref="Available"/>=false 并给出人读原因。
/// 一次构建、多次查询；不缓存跨调用状态（现状面可能被替换，每次装载重建一份最新的）。
/// </summary>
public sealed class SimTerrainSampler
{
    /// <summary>AcDbEntityType.TriangleMesh。</summary>
    private const int TypeIdTriangleMesh = 6;

    /// <summary>最多合并几张现状面（多张时按面数从大到小取，够覆盖场地即可）。</summary>
    private const int MaxMeshes = 4;
    /// <summary>索引格子数上限（沿一个方向），防止病态包围盒把内存吃爆。</summary>
    private const int MaxGridSide = 512;

    private double[] _vx = Array.Empty<double>();   // 顶点 x
    private double[] _vy = Array.Empty<double>();
    private double[] _vz = Array.Empty<double>();
    private int[] _tri = Array.Empty<int>();        // 三角顶点索引，每 3 个一面

    private double _minX, _minY, _cellX, _cellY;
    private int _gw, _gh;
    private List<int>[] _grid = Array.Empty<List<int>>();

    public bool Available { get; private set; }
    /// <summary>来源 / 不可用原因（界面直接显示这一句）。</summary>
    public string Reason { get; private set; } = "未探测";
    /// <summary>参与采样的三角形数。</summary>
    public int TriangleCount => _tri.Length / 3;

    private SimTerrainSampler() { }

    /// <summary>
    /// 从当前文档里找现状面三角网建采样器。**排除推演层体自己建的那些体**
    /// （否则会拿上一次生成的层体顶面当地表，越采越偏）。永不抛。
    /// </summary>
    public static SimTerrainSampler FromDocument()
    {
        var s = new SimTerrainSampler();
        IEntityCapability? ent;
        try { ent = SimHost.Entities; }
        catch { ent = null; }
        if (ent == null) { s.Reason = "未注入实体能力 IEntityCapability，取不到现状面"; return s; }

        ulong[] all;
        try { all = ent.GetAllHandles() ?? Array.Empty<ulong>(); }
        catch (Exception ex) { s.Reason = $"读取文档实体失败（{ex.GetType().Name}）"; return s; }
        if (all.Length == 0) { s.Reason = "文档中没有任何实体"; return s; }

        // 推演自己建的层体不算现状面
        var mine = new HashSet<ulong>();
        foreach (var lay in new[] { SolidGeometryPort.LayerPit, SolidGeometryPort.LayerDump })
        {
            try
            {
                var arr = ent.GetHandlesByLayer(lay);
                if (arr != null) foreach (var h in arr) mine.Add(h);
            }
            catch { }
        }

        int[] types;
        try { types = ent.GetEntityTypes(all); }
        catch (Exception ex) { s.Reason = $"读取实体类型失败（{ex.GetType().Name}）"; return s; }

        var picked = new List<(double[] V, int[] T)>();
        int n = Math.Min(all.Length, types.Length);
        for (int i = 0; i < n && picked.Count < MaxMeshes; i++)
        {
            if (types[i] != TypeIdTriangleMesh || mine.Contains(all[i])) continue;
            try
            {
                if (!ent.TryGetMeshGeometry(all[i], out var verts, out var tris)) continue;
                if (verts == null || tris == null || verts.Length < 9 || tris.Length < 3) continue;
                picked.Add((verts, tris));
            }
            catch { }
        }

        if (picked.Count == 0)
        {
            s.Reason = "文档中没有可用的现状面三角网（只有层体或非 mesh 实体）";
            return s;
        }

        s.Assemble(picked);
        return s;
    }

    /// <summary>合并顶点/三角并建网格索引。</summary>
    private void Assemble(List<(double[] V, int[] T)> meshes)
    {
        int nv = meshes.Sum(m => m.V.Length / 3);
        int nt = meshes.Sum(m => m.T.Length / 3);
        _vx = new double[nv]; _vy = new double[nv]; _vz = new double[nv];
        _tri = new int[nt * 3];

        int vo = 0, to = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (v, t) in meshes)
        {
            int baseV = vo;
            int cnt = v.Length / 3;
            for (int i = 0; i < cnt; i++)
            {
                double x = v[3 * i], y = v[3 * i + 1], z = v[3 * i + 2];
                _vx[vo] = x; _vy[vo] = y; _vz[vo] = z; vo++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i] + baseV, b = t[i + 1] + baseV, c = t[i + 2] + baseV;
                if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv) continue;
                _tri[to++] = a; _tri[to++] = b; _tri[to++] = c;
            }
        }
        if (to < _tri.Length) Array.Resize(ref _tri, to);

        if (to < 3 || !(maxX > minX) || !(maxY > minY))
        {
            Reason = "现状面三角网退化（无有效三角或包围盒为零）";
            return;
        }

        // 网格边长按「每格约 2 个三角」定，再钳到上限
        int faces = _tri.Length / 3;
        int side = Math.Clamp((int)Math.Sqrt(faces / 2.0) + 1, 1, MaxGridSide);
        _gw = side; _gh = side;
        _minX = minX; _minY = minY;
        _cellX = (maxX - minX) / _gw; _cellY = (maxY - minY) / _gh;
        if (_cellX <= 1e-12) _cellX = 1e-12;
        if (_cellY <= 1e-12) _cellY = 1e-12;

        _grid = new List<int>[_gw * _gh];
        for (int f = 0; f < faces; f++)
        {
            int a = _tri[3 * f], b = _tri[3 * f + 1], c = _tri[3 * f + 2];
            double lo_x = Math.Min(_vx[a], Math.Min(_vx[b], _vx[c]));
            double hi_x = Math.Max(_vx[a], Math.Max(_vx[b], _vx[c]));
            double lo_y = Math.Min(_vy[a], Math.Min(_vy[b], _vy[c]));
            double hi_y = Math.Max(_vy[a], Math.Max(_vy[b], _vy[c]));
            int i0 = Col(lo_x), i1 = Col(hi_x), j0 = Row(lo_y), j1 = Row(hi_y);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * _gw + i;
                    (_grid[k] ??= new List<int>(4)).Add(f);
                }
        }

        Available = true;
        Reason = $"现状面三角网 {meshes.Count} 张 / {faces:N0} 面，范围 X[{minX:0}~{maxX:0}] Y[{minY:0}~{maxY:0}]";
    }

    private int Col(double x) => Math.Clamp((int)((x - _minX) / _cellX), 0, _gw - 1);
    private int Row(double y) => Math.Clamp((int)((y - _minY) / _cellY), 0, _gh - 1);

    /// <summary>
    /// 单点采 Z：XY 投影落在哪个三角形里，就按重心坐标插值。落在面外返回 NaN。
    /// </summary>
    public double SampleZ(double x, double y)
    {
        if (!Available) return double.NaN;
        var cell = _grid[Row(y) * _gw + Col(x)];
        if (cell == null) return double.NaN;

        foreach (int f in cell)
        {
            int a = _tri[3 * f], b = _tri[3 * f + 1], c = _tri[3 * f + 2];
            double x1 = _vx[a], y1 = _vy[a], x2 = _vx[b], y2 = _vy[b], x3 = _vx[c], y3 = _vy[c];
            double d = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
            if (Math.Abs(d) < 1e-12) continue;                 // XY 上退化成线的三角（陡壁），跳过
            double l1 = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / d;
            double l2 = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / d;
            double l3 = 1 - l1 - l2;
            const double eps = -1e-9;
            if (l1 < eps || l2 < eps || l3 < eps) continue;
            return l1 * _vz[a] + l2 * _vz[b] + l3 * _vz[c];
        }
        return double.NaN;
    }

    /// <summary>
    /// 一条轮廓环的代表高程 = 命中点 Z 的均值。
    /// </summary>
    /// <returns>(代表高程 Z（全落空返回 NaN）, 命中点数, 参与点数)。</returns>
    public (double Z, int Hit, int Total) SampleRingZ(IReadOnlyList<SimPoint> ring)
    {
        if (!Available || ring == null || ring.Count == 0) return (double.NaN, 0, ring?.Count ?? 0);
        double sum = 0; int hit = 0;
        foreach (var p in ring)
        {
            double z = SampleZ(p.X, p.Y);
            if (double.IsNaN(z) || double.IsInfinity(z)) continue;
            sum += z; hit++;
        }
        return (hit > 0 ? sum / hit : double.NaN, hit, ring.Count);
    }
}
