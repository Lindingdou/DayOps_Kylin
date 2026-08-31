using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad;

/// <summary>网格度量结果。</summary>
public readonly record struct MeshMetricsResult(
    int VertexCount, int TriangleCount, double SurfaceArea, double Volume,
    double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

/// <summary>
/// 三角网格度量（表面积 = Σ 三角面积；有向体积 = Σ 四面体 a·(b×c)/6，闭合网取绝对值）+ 包围盒。
/// 纯几何、可单测。对应 MeshEditLib 的网格体积/面积（原走内核, 此为托管从 (verts,tris) 直算）。含 OFF 3D 网格解析。
/// </summary>
public static class MeshMetrics
{
    public static MeshMetricsResult Compute(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        if (verts == null || verts.Count == 0 || tris == null)
            return new MeshMetricsResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var v in verts)
        {
            if (v.x < minX) minX = v.x; if (v.y < minY) minY = v.y; if (v.z < minZ) minZ = v.z;
            if (v.x > maxX) maxX = v.x; if (v.y > maxY) maxY = v.y; if (v.z > maxZ) maxZ = v.z;
        }
        double area = 0;
        int nt = 0;
        foreach (var (ia, ib, ic) in tris)
        {
            if (ia < 0 || ib < 0 || ic < 0 || ia >= verts.Count || ib >= verts.Count || ic >= verts.Count) continue;
            var a = verts[ia]; var b = verts[ib]; var c = verts[ic];
            double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
            double vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            area += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
            nt++;
        }
        return new MeshMetricsResult(verts.Count, nt, area, RobustVolume(verts, tris), minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>有向四面体散度法（6×体积）：Σ a·(b×c)。水密网格严密。</summary>
    private static double SignedVolume6(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        double vol6 = 0;
        foreach (var (ia, ib, ic) in tris)
        {
            if (ia < 0 || ib < 0 || ic < 0 || ia >= verts.Count || ib >= verts.Count || ic >= verts.Count) continue;
            var a = verts[ia]; var b = verts[ib]; var c = verts[ic];
            double bx = b.y * c.z - b.z * c.y, by = b.z * c.x - b.x * c.z, bz = b.x * c.y - b.y * c.x;
            vol6 += a.x * bx + a.y * by + a.z * bz;
        }
        return vol6;
    }

    /// <summary>
    /// 三角网体积（忠实原 MeshVolume 分级容错）：① 水密 → 散度法【严密】；
    /// ② 非水密 → 焊接拓扑 + 扇形补洞封盖 → 散度取绝对值（近似正解）。
    /// </summary>
    public static double RobustVolume(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        if (verts == null || verts.Count == 0 || tris == null || tris.Count == 0) return 0;
        var diag = MeshDiagnose.Analyze(verts, tris);
        if (diag.IsClosed) return Math.Abs(SignedVolume6(verts, tris)) / 6.0;   // 水密 → 严密
        // 非水密：焊接 → 补洞封盖 → 再散度
        try
        {
            // 焊接容差按坐标尺度取(合并浮点重合顶点, 使补洞能找到闭合环)
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
            foreach (var v in verts) { if (v.x < mnx) mnx = v.x; if (v.y < mny) mny = v.y; if (v.z < mnz) mnz = v.z; if (v.x > mxx) mxx = v.x; if (v.y > mxy) mxy = v.y; if (v.z > mxz) mxz = v.z; }
            double bboxDiag = Math.Sqrt((mxx - mnx) * (mxx - mnx) + (mxy - mny) * (mxy - mny) + (mxz - mnz) * (mxz - mnz));
            double tol = Math.Max(1e-9, bboxDiag * 1e-7);
            var w = MeshWeld.Weld(verts, tris, tol);
            var (cv, ct, _) = MeshHoleFill.Fill(w.Verts, w.Tris);
            return Math.Abs(SignedVolume6(cv, ct)) / 6.0;
        }
        catch { return Math.Abs(SignedVolume6(verts, tris)) / 6.0; }   // 修复失败 → 原始散度兜底
    }

    /// <summary>解析 OFF 为 3D (verts, tris)；多边形面按扇形三角化。失败返回 (空, 空)。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) ParseOff(string text)
    {
        var verts = new List<(double x, double y, double z)>();
        var tris = new List<(int a, int b, int c)>();
        if (string.IsNullOrWhiteSpace(text)) return (verts, tris);
        var ws = new[] { ' ', '\t' };
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var s = raw.Trim();
            if (s.Length > 0 && !s.StartsWith("#")) lines.Add(s);
        }
        if (lines.Count == 0) return (verts, tris);
        int idx = 0;
        if (lines[0].Replace(" ", "").ToUpperInvariant().EndsWith("OFF")) idx = 1;
        if (idx >= lines.Count) return (verts, tris);
        var counts = lines[idx++].Split(ws, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length < 2 || !int.TryParse(counts[0], out int nV) || !int.TryParse(counts[1], out int nF)) return (verts, tris);
        for (int i = 0; i < nV && idx < lines.Count; i++, idx++)
        {
            var t = lines[idx].Split(ws, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 3) { verts.Add((0, 0, 0)); continue; }
            double.TryParse(t[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x);
            double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y);
            double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z);
            verts.Add((x, y, z));
        }
        for (int f = 0; f < nF && idx < lines.Count; f++, idx++)
        {
            var t = lines[idx].Split(ws, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 1 || !int.TryParse(t[0], out int k) || k < 3 || t.Length < k + 1) continue;
            var poly = new int[k];
            bool ok = true;
            for (int j = 0; j < k; j++) if (!int.TryParse(t[1 + j], out poly[j])) { ok = false; break; }
            if (!ok) continue;
            for (int j = 1; j + 1 < k; j++) tris.Add((poly[0], poly[j], poly[j + 1]));   // 扇形三角化
        }
        return (verts, tris);
    }
}
