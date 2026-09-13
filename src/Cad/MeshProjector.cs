using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点/线落到面上（对应原 CAD 端 POINTPROJECT / POLYPROJECT）——把 XY 点按 2.5D 网格重心插值重定 Z。
/// 复用 <see cref="MineableAreaIdentifier.SampleMeshZ"/>(点在三角内则重心插值 Z)。原走内核逐点投影,
/// 此以托管从 (verts,tris) 直算; 未命中(点落网格外)保留原点并计入 missed。纯几何、可单测。
/// </summary>
public static class MeshProjector
{
    /// <summary>把 XY 点表逐点投影到网格取 Z(点落面 / 只投顶点)；返回 (投影后点表, 未命中数)。未命中点 Z 保持 0。</summary>
    public static (List<(double x, double y, double z)> draped, int missed) Drape(
        double[] verts, int[] tris, IReadOnlyList<(double x, double y)> pts)
    {
        var outPts = new List<(double x, double y, double z)>();
        int missed = 0;
        if (pts == null) return (outPts, 0);
        foreach (var (x, y) in pts)
        {
            var z = MineableAreaIdentifier.SampleMeshZ(verts, tris, x, y);
            if (z.HasValue) outPts.Add((x, y, z.Value));
            else { outPts.Add((x, y, 0)); missed++; }
        }
        return (outPts, missed);
    }

    /// <summary>
    /// 把一条多段线落到网格上(线落面)：顶点投 Z 之外, 每段与三角边的交点都补成节点(高程沿边插值), 整条线逐段贴面
    /// —— 只投顶点时两顶点之间的直段会穿山悬空。返回 (落面后点串, 网外顶点数)；网外顶点 Z 保持 0。
    /// </summary>
    public static (List<(double x, double y, double z)> draped, int missed) DrapePolyline(
        double[] verts, int[] tris, IReadOnlyList<(double x, double y)> pts, bool closed = false, double tolerance = 1e-6)
    {
        if (pts == null || pts.Count == 0) return (new List<(double x, double y, double z)>(), 0);
        var v = new List<(double x, double y, double z)>(verts.Length / 3);
        for (int i = 0; i + 2 < verts.Length; i += 3) v.Add((verts[i], verts[i + 1], verts[i + 2]));
        var t = new List<(int a, int b, int c)>(tris.Length / 3);
        for (int i = 0; i + 2 < tris.Length; i += 3) t.Add((tris[i], tris[i + 1], tris[i + 2]));
        var res = MeshEmbed.Drape(v, t, new[] { new MeshEmbed.Line(pts, null, closed) }, tolerance);
        if (res == null || res.Polylines.Count == 0) return Drape(verts, tris, pts);   // 网退化：退回只投顶点
        return (res.Polylines[0], res.OutsideNodes);
    }
}
