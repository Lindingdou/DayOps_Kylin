using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 坡顶/坡底线(crest/toe)提取 —— 标准坡度断棱线检测: 三角按坡度分 平/陡, 平-陡相邻三角的公共边即断棱线;
/// 平三角更高→坡顶线(crest, 平台顶与坡面交), 平三角更低→坡底线(toe, 坡面与平台底交)。
/// 对应原 PointCloudLib「坡顶底线提取」的意图(原走 native PMTB 栅格化算法, 此为标准 TIN 坡度式托管重实现,
/// 断棱线几何等价——平陡分界, 可能与 native 精确断线有别)。纯逻辑、可单测。
/// </summary>
public static class CrestToe
{
    public readonly record struct Edge(double X0, double Y0, double X1, double Y1);

    /// <summary>提取坡顶/坡底断棱边。slopeThresholdDeg 分平/陡(默认 30°)。</summary>
    public static (List<Edge> crest, List<Edge> toe) Extract(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        double slopeThresholdDeg = 30)
    {
        var crest = new List<Edge>(); var toe = new List<Edge>();
        if (verts == null || tris == null) return (crest, toe);
        int nt = tris.Count;
        var steep = new bool[nt]; var cz = new double[nt];
        for (int t = 0; t < nt; t++)
        {
            var (a, b, c) = tris[t];
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) { steep[t] = false; continue; }
            cz[t] = (verts[a].z + verts[b].z + verts[c].z) / 3.0;
            steep[t] = MeshFaceCull.SlopeDeg(verts[a], verts[b], verts[c], out double s) && s > slopeThresholdDeg;
        }
        // 无向边 → 关联三角
        var edgeTris = new Dictionary<(int, int), (int t0, int t1)>();
        void AddEdge(int u, int v, int t)
        {
            var k = u < v ? (u, v) : (v, u);
            if (edgeTris.TryGetValue(k, out var e)) edgeTris[k] = (e.t0, t);
            else edgeTris[k] = (t, -1);
        }
        for (int t = 0; t < nt; t++)
        {
            var (a, b, c) = tris[t];
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) continue;
            AddEdge(a, b, t); AddEdge(b, c, t); AddEdge(c, a, t);
        }
        foreach (var kv in edgeTris)
        {
            var (t0, t1) = kv.Value;
            if (t1 < 0) continue;                       // 边界边(仅 1 三角)跳过
            if (steep[t0] == steep[t1]) continue;       // 同类(平-平 或 陡-陡)非断棱
            int flat = steep[t0] ? t1 : t0, steepT = steep[t0] ? t0 : t1;
            var (u, v) = kv.Key;
            var e = new Edge(verts[u].x, verts[u].y, verts[v].x, verts[v].y);
            if (cz[flat] > cz[steepT]) crest.Add(e);    // 平台在上 → 坡顶线
            else toe.Add(e);                            // 平台在下 → 坡底线
        }
        return (crest, toe);
    }
}
