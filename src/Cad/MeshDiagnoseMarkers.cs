using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 格网质量检测·问题定位（原 native <c>PitMine_ShowDiagnosticMarkers(errorType)</c> 的托管等价）：
/// 与 <see cref="MeshDiagnose.Analyze"/> 同口径把 6 类问题的**具体元素**找出来 —— 0=开放边 1=非流形边 2=退化面
/// 3=自相交 4=孤立点 5=重复点，并能生成场景高亮实体(诊断标记图层)。纯拓扑/几何、可单测。
/// </summary>
public static class MeshDiagnoseMarkers
{
    public const string MarkerLayer = "诊断标记";

    public sealed class Result
    {
        public List<(int u, int v)> OpenEdges = new();
        public List<(int u, int v)> NonManifoldEdges = new();
        public List<int> DegenerateTris = new();
        public List<int> SelfIntersectTris = new();
        public List<int> IsolatedVerts = new();
        public List<int> DuplicateVerts = new();
        /// <summary>自相交三角数超上限未检时为 true。</summary>
        public bool SelfIntersectSkipped;

        public int CountOf(int errorType) => errorType switch
        {
            0 => OpenEdges.Count, 1 => NonManifoldEdges.Count, 2 => DegenerateTris.Count,
            3 => SelfIntersectTris.Count, 4 => IsolatedVerts.Count, 5 => DuplicateVerts.Count, _ => 0,
        };
    }

    private const int SelfIntersectCap = 60000;

    public static Result Collect(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var r = new Result();
        if (verts == null || tris == null) return r;
        var inc = new Dictionary<(int, int), int>();
        var valid = new List<(int idx, int a, int b, int c)>();
        void Edge(int u, int v) { var k = u < v ? (u, v) : (v, u); inc[k] = inc.TryGetValue(k, out var c) ? c + 1 : 1; }
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            if (a == b || b == c || a == c) { r.DegenerateTris.Add(i); continue; }
            if (a >= 0 && b >= 0 && c >= 0 && a < verts.Count && b < verts.Count && c < verts.Count)
            {
                var p = verts[a]; var q = verts[b]; var s = verts[c];
                double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = s.x - p.x, vy = s.y - p.y, vz = s.z - p.z;
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                if (cx * cx + cy * cy + cz * cz < 1e-18) { r.DegenerateTris.Add(i); continue; }
            }
            Edge(a, b); Edge(b, c); Edge(c, a);
            valid.Add((i, a, b, c));
        }
        foreach (var kv in inc)
        {
            if (kv.Value == 1) r.OpenEdges.Add(kv.Key);
            else if (kv.Value >= 3) r.NonManifoldEdges.Add(kv.Key);
        }
        // 孤立点 / 重复点(1e-6 量化, 与 MeshDiagnose 同口径: 后出现者算重复)
        if (verts.Count > 0)
        {
            var referenced = new bool[verts.Count];
            foreach (var (a, b, c) in tris)
            {
                if (a >= 0 && a < verts.Count) referenced[a] = true;
                if (b >= 0 && b < verts.Count) referenced[b] = true;
                if (c >= 0 && c < verts.Count) referenced[c] = true;
            }
            for (int i = 0; i < verts.Count; i++) if (!referenced[i]) r.IsolatedVerts.Add(i);
            const double tol = 1e-6;
            var seen = new HashSet<(long, long, long)>();
            for (int i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                var key = ((long)Math.Round(v.x / tol), (long)Math.Round(v.y / tol), (long)Math.Round(v.z / tol));
                if (!seen.Add(key)) r.DuplicateVerts.Add(i);
            }
        }
        // 自交三角(横切另一非相邻三角), 均匀网格 broad-phase
        if (valid.Count > SelfIntersectCap) r.SelfIntersectSkipped = true;
        else if (valid.Count >= 2) CollectSelfIntersect(verts, valid, r.SelfIntersectTris);
        return r;
    }

    private static void CollectSelfIntersect(IReadOnlyList<(double x, double y, double z)> verts,
        List<(int idx, int a, int b, int c)> tris, List<int> outTris)
    {
        int m = tris.Count;
        var box = new (double x0, double y0, double z0, double x1, double y1, double z1)[m];
        double gx0 = double.MaxValue, gy0 = double.MaxValue, gz0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue, gz1 = double.MinValue;
        for (int i = 0; i < m; i++)
        {
            var (_, a, b, c) = tris[i];
            if (a >= verts.Count || b >= verts.Count || c >= verts.Count || a < 0 || b < 0 || c < 0) { box[i] = (0, 0, 0, 0, 0, 0); continue; }
            var p = verts[a]; var q = verts[b]; var s = verts[c];
            box[i] = (Math.Min(p.x, Math.Min(q.x, s.x)), Math.Min(p.y, Math.Min(q.y, s.y)), Math.Min(p.z, Math.Min(q.z, s.z)),
                      Math.Max(p.x, Math.Max(q.x, s.x)), Math.Max(p.y, Math.Max(q.y, s.y)), Math.Max(p.z, Math.Max(q.z, s.z)));
            if (box[i].x0 < gx0) gx0 = box[i].x0; if (box[i].y0 < gy0) gy0 = box[i].y0; if (box[i].z0 < gz0) gz0 = box[i].z0;
            if (box[i].x1 > gx1) gx1 = box[i].x1; if (box[i].y1 > gy1) gy1 = box[i].y1; if (box[i].z1 > gz1) gz1 = box[i].z1;
        }
        int grid = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(m)));
        double sx = Math.Max((gx1 - gx0) / grid, 1e-9), sy = Math.Max((gy1 - gy0) / grid, 1e-9), sz = Math.Max((gz1 - gz0) / grid, 1e-9);
        var cells = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < m; i++)
        {
            int cx0 = (int)Math.Floor((box[i].x0 - gx0) / sx), cx1 = (int)Math.Floor((box[i].x1 - gx0) / sx);
            int cy0 = (int)Math.Floor((box[i].y0 - gy0) / sy), cy1 = (int)Math.Floor((box[i].y1 - gy0) / sy);
            int cz0 = (int)Math.Floor((box[i].z0 - gz0) / sz), cz1 = (int)Math.Floor((box[i].z1 - gz0) / sz);
            for (int X = cx0; X <= cx1; X++) for (int Y = cy0; Y <= cy1; Y++) for (int Z = cz0; Z <= cz1; Z++)
            {
                var key = (X, Y, Z);
                if (!cells.TryGetValue(key, out var l)) { l = new List<int>(); cells[key] = l; }
                l.Add(i);
            }
        }
        var tested = new HashSet<(int, int)>();
        var hit = new HashSet<int>();
        foreach (var bucket in cells.Values)
        {
            for (int u = 0; u < bucket.Count; u++)
                for (int w = u + 1; w < bucket.Count; w++)
                {
                    int i = bucket[u], j = bucket[w];
                    if (i > j) (i, j) = (j, i);
                    if (!tested.Add((i, j))) continue;
                    var (ia, a0, a1, a2) = tris[i]; var (ib, b0, b1, b2) = tris[j];
                    if (a0 == b0 || a0 == b1 || a0 == b2 || a1 == b0 || a1 == b1 || a1 == b2 || a2 == b0 || a2 == b1 || a2 == b2) continue;
                    var ba = box[i]; var bb = box[j];
                    if (ba.x1 < bb.x0 || bb.x1 < ba.x0 || ba.y1 < bb.y0 || bb.y1 < ba.y0 || ba.z1 < bb.z0 || bb.z1 < ba.z0) continue;
                    if (a0 >= verts.Count || a1 >= verts.Count || a2 >= verts.Count || b0 >= verts.Count || b1 >= verts.Count || b2 >= verts.Count) continue;
                    if (TriTri(verts[a0], verts[a1], verts[a2], verts[b0], verts[b1], verts[b2])) { hit.Add(ia); hit.Add(ib); }
                }
        }
        outTris.AddRange(hit);
        outTris.Sort();
    }

    /// <summary>两三角是否横切(任一边穿过另一三角内部; 共面/擦边不算)。</summary>
    public static bool TriTri((double x, double y, double z) a0, (double x, double y, double z) a1, (double x, double y, double z) a2,
                              (double x, double y, double z) b0, (double x, double y, double z) b1, (double x, double y, double z) b2)
        => SegTri(a0, a1, b0, b1, b2) || SegTri(a1, a2, b0, b1, b2) || SegTri(a2, a0, b0, b1, b2)
        || SegTri(b0, b1, a0, a1, a2) || SegTri(b1, b2, a0, a1, a2) || SegTri(b2, b0, a0, a1, a2);

    // 线段 p→q 与三角 (t0,t1,t2) 严格相交(Möller–Trumbore, 端点/边缘擦碰不算)
    private static bool SegTri((double x, double y, double z) p, (double x, double y, double z) q,
                               (double x, double y, double z) t0, (double x, double y, double z) t1, (double x, double y, double z) t2)
    {
        double dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z;
        double e1x = t1.x - t0.x, e1y = t1.y - t0.y, e1z = t1.z - t0.z;
        double e2x = t2.x - t0.x, e2y = t2.y - t0.y, e2z = t2.z - t0.z;
        double hx = dy * e2z - dz * e2y, hy = dz * e2x - dx * e2z, hz = dx * e2y - dy * e2x;
        double a = e1x * hx + e1y * hy + e1z * hz;
        if (Math.Abs(a) < 1e-14) return false;   // 平行/共面
        double f = 1.0 / a;
        double sx = p.x - t0.x, sy = p.y - t0.y, sz = p.z - t0.z;
        double u = f * (sx * hx + sy * hy + sz * hz);
        const double eps = 1e-9;
        if (u <= eps || u >= 1 - eps) return false;
        double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        double v = f * (dx * qx + dy * qy + dz * qz);
        if (v <= eps || u + v >= 1 - eps) return false;
        double t = f * (e2x * qx + e2y * qy + e2z * qz);
        return t > eps && t < 1 - eps;
    }

    /// <summary>某类问题 → 场景高亮实体(边→三维直线, 三角→闭合三维多段线, 顶点→点), 落「诊断标记」层。</summary>
    public static List<SceneEntity> Entities(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
        Result r, int errorType, (float r, float g, float b) color, double pointSize)
    {
        var list = new List<SceneEntity>();
        void Line(int u, int v)
        {
            if (u < 0 || v < 0 || u >= verts.Count || v >= verts.Count) return;
            var p = verts[u]; var q = verts[v];
            var pl = new PolylineEntity { Zs = new List<double> { p.z, q.z }, LayerName = MarkerLayer, Cr = color.r, Cg = color.g, Cb = color.b };
            pl.Points.Add((p.x, p.y)); pl.Points.Add((q.x, q.y));
            list.Add(pl);
        }
        void Tri(int i)
        {
            if (i < 0 || i >= tris.Count) return;
            var (a, b, c) = tris[i];
            if (a < 0 || b < 0 || c < 0 || a >= verts.Count || b >= verts.Count || c >= verts.Count) return;
            var pl = new PolylineEntity { Closed = true, Zs = new List<double> { verts[a].z, verts[b].z, verts[c].z }, LayerName = MarkerLayer, Cr = color.r, Cg = color.g, Cb = color.b };
            pl.Points.Add((verts[a].x, verts[a].y)); pl.Points.Add((verts[b].x, verts[b].y)); pl.Points.Add((verts[c].x, verts[c].y));
            list.Add(pl);
        }
        void Pt(int i)
        {
            if (i < 0 || i >= verts.Count) return;
            list.Add(new PointEntity { X = verts[i].x, Y = verts[i].y, Elevation = verts[i].z, Size = pointSize, Style = 3 + 32, LayerName = MarkerLayer, Cr = color.r, Cg = color.g, Cb = color.b });
        }
        switch (errorType)
        {
            case 0: foreach (var (u, v) in r.OpenEdges) Line(u, v); break;
            case 1: foreach (var (u, v) in r.NonManifoldEdges) Line(u, v); break;
            case 2: foreach (var i in r.DegenerateTris) Tri(i); break;
            case 3: foreach (var i in r.SelfIntersectTris) Tri(i); break;
            case 4: foreach (var i in r.IsolatedVerts) Pt(i); break;
            case 5: foreach (var i in r.DuplicateVerts) Pt(i); break;
        }
        return list;
    }
}
