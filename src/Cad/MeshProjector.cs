using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点/线落到面上（对应原 CAD 端 POINTPROJECT / POLYPROJECT）——把 XY 点按 2.5D 网格重心插值重定 Z。
/// 复用 <see cref="MineableAreaIdentifier.SampleMeshZ"/>(点在三角内则重心插值 Z)。原走内核逐点投影,
/// 此以托管从 (verts,tris) 直算; 未命中(点落网格外)保留原点并计入 missed。纯几何、可单测。
/// </summary>
public static class MeshProjector
{
    /// <summary>把 XY 点表投影到网格取 Z；返回 (投影后点表, 未命中数)。未命中点 Z 保持 0。</summary>
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
}
