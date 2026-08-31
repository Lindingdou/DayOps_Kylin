using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 按闭合边界多边形把**既有**三角网分内/外两片（忠实原 pc_tin_split「沿多段线把三角网切分为内/外两片」）。
/// 判别用三角质心是否在边界内——与 Kylin「裁剪三角网」(<see cref="Delaunay.TriangulateClipped"/>) 同一质心约定；
/// 各片重映射顶点为独立网格。三角质心粒度(不逐边精确切straddling三角，与 Kylin 现有裁剪一致)。纯逻辑、可单测。
/// </summary>
public static class MeshBoundarySplit
{
    public readonly record struct Piece(List<(double x, double y, double z)> Verts, List<(int a, int b, int c)> Tris);

    /// <summary>按 boundary(闭合多边形 XY)分内/外。boundary&lt;3 点 → 全归外片。返回 (内片, 外片)。</summary>
    public static (Piece inside, Piece outside) ByPolygon(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        IReadOnlyList<(double x, double y)> boundary)
    {
        var inV = new List<(double x, double y, double z)>(); var inT = new List<(int, int, int)>();
        var outV = new List<(double x, double y, double z)>(); var outT = new List<(int, int, int)>();
        if (verts == null || tris == null) return (new Piece(inV, inT), new Piece(outV, outT));
        var inMap = new Dictionary<int, int>(); var outMap = new Dictionary<int, int>();
        bool hasBoundary = boundary != null && boundary.Count >= 3;

        int Remap(Dictionary<int, int> map, List<(double x, double y, double z)> vlist, int vi)
        {
            if (!map.TryGetValue(vi, out int ni)) { ni = vlist.Count; vlist.Add(verts[vi]); map[vi] = ni; }
            return ni;
        }
        foreach (var (a, b, c) in tris)
        {
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            double cx = (verts[a].x + verts[b].x + verts[c].x) / 3.0;
            double cy = (verts[a].y + verts[b].y + verts[c].y) / 3.0;
            bool inside = hasBoundary && LineMath.PointInPolygon(cx, cy, boundary);
            if (inside) inT.Add((Remap(inMap, inV, a), Remap(inMap, inV, b), Remap(inMap, inV, c)));
            else outT.Add((Remap(outMap, outV, a), Remap(outMap, outV, b), Remap(outMap, outV, c)));
        }
        return (new Piece(inV, inT), new Piece(outV, outT));
    }
}
