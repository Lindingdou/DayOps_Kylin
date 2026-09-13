using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 一个潜在排土位置的【壳子体】（托管等价原内核 <c>CarveDumpStripsByRails</c>）：
/// 前脸 = 坡顶轨 ↔ 坡底轨放样；把前脸沿推进方向（由坡顶指向坡底那一侧 = 排土往外）平移 W 得后脸；
/// 顶/底/两端封口 ⇒ 闭合六面壳。体积按闭合网格散度定理算（原版是内核实测体积，这里同口径的托管实测）。
/// 顶点数不等的两条轨先按等弧长重采样到同点数。
/// </summary>
public static class DumpStripShell
{
    public sealed class Mesh
    {
        public List<(double x, double y, double z)> Verts { get; } = new();
        public List<(int a, int b, int c)> Tris { get; } = new();
        public double VolumeM3;
        public bool Ok;
        public string Error = "";
    }

    public static Mesh Build(double[] crestXyz, double[] toeXyz, double stripWidthM, int resample = 0)
    {
        var m = new Mesh();
        if (crestXyz == null || toeXyz == null || crestXyz.Length < 6 || toeXyz.Length < 6) { m.Error = "轨点数不足"; return m; }
        if (stripWidthM <= 1e-6) { m.Error = "条带宽须 > 0"; return m; }
        int n = resample > 1 ? resample : Math.Max(crestXyz.Length, toeXyz.Length) / 3;
        var c = Resample(crestXyz, n); var t = Resample(toeXyz, n);
        if (c.Count < 2 || t.Count < 2) { m.Error = "重采样失败"; return m; }

        // 推进方向：由坡顶指向坡底（XY），逐点取；退化处沿用邻点
        var dir = new (double x, double y)[n];
        for (int i = 0; i < n; i++)
        {
            double dx = t[i].x - c[i].x, dy = t[i].y - c[i].y, l = Math.Sqrt(dx * dx + dy * dy);
            dir[i] = l > 1e-9 ? (dx / l, dy / l) : (double.NaN, double.NaN);
        }
        for (int i = 0; i < n; i++)
            if (double.IsNaN(dir[i].x))
            {
                // 用轨的切向转 90°兜底（坡顶/坡底重合时）
                int j = Math.Min(i + 1, n - 1), k = Math.Max(i - 1, 0);
                double tx = c[j].x - c[k].x, ty = c[j].y - c[k].y, l = Math.Sqrt(tx * tx + ty * ty);
                dir[i] = l > 1e-9 ? (ty / l, -tx / l) : (1, 0);
            }
        // 8 类顶点：前脸顶(c) 前脸底(t) 后脸顶(c+W) 后脸底(t+W)
        int V(double x, double y, double z) { m.Verts.Add((x, y, z)); return m.Verts.Count - 1; }
        var fc = new int[n]; var ft = new int[n]; var bc = new int[n]; var bt = new int[n];
        for (int i = 0; i < n; i++)
        {
            fc[i] = V(c[i].x, c[i].y, c[i].z);
            ft[i] = V(t[i].x, t[i].y, t[i].z);
            bc[i] = V(c[i].x + dir[i].x * stripWidthM, c[i].y + dir[i].y * stripWidthM, c[i].z);
            bt[i] = V(t[i].x + dir[i].x * stripWidthM, t[i].y + dir[i].y * stripWidthM, t[i].z);
        }
        void Quad(int a, int b, int cc, int d) { m.Tris.Add((a, b, cc)); m.Tris.Add((a, cc, d)); }
        for (int i = 0; i + 1 < n; i++)
        {
            Quad(fc[i], ft[i], ft[i + 1], fc[i + 1]);      // 前脸
            Quad(bt[i], bc[i], bc[i + 1], bt[i + 1]);      // 后脸（反向）
            Quad(bc[i], fc[i], fc[i + 1], bc[i + 1]);      // 顶
            Quad(ft[i], bt[i], bt[i + 1], ft[i + 1]);      // 底
        }
        Quad(fc[0], bc[0], bt[0], ft[0]);                  // 端 0
        Quad(ft[n - 1], bt[n - 1], bc[n - 1], fc[n - 1]);  // 端 n-1
        m.VolumeM3 = Math.Abs(SignedVolume(m));
        m.Ok = m.Tris.Count > 0;
        return m;
    }

    /// <summary>闭合网格的有符号体积（散度定理）。</summary>
    public static double SignedVolume(Mesh m)
    {
        double v = 0;
        foreach (var (a, b, c) in m.Tris)
        {
            var p = m.Verts[a]; var q = m.Verts[b]; var r = m.Verts[c];
            v += p.x * (q.y * r.z - q.z * r.y) - p.y * (q.x * r.z - q.z * r.x) + p.z * (q.x * r.y - q.y * r.x);
        }
        return v / 6.0;
    }

    /// <summary>沿折线按等弧长重采样到 n 点（含首末；n ≥ 2）。</summary>
    public static List<(double x, double y, double z)> Resample(double[] xyz, int n)
    {
        var pts = new List<(double x, double y, double z)>();
        int m = xyz.Length / 3;
        if (m == 0) return pts;
        if (n < 2) n = 2;
        var cum = new double[m];
        for (int i = 1; i < m; i++)
        {
            double dx = xyz[i * 3] - xyz[(i - 1) * 3], dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1], dz = xyz[i * 3 + 2] - xyz[(i - 1) * 3 + 2];
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        double total = cum[m - 1];
        if (total < 1e-12) { for (int i = 0; i < n; i++) pts.Add((xyz[0], xyz[1], xyz[2])); return pts; }
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            double s = total * i / (n - 1);
            while (k + 1 < m - 1 && cum[k + 1] < s) k++;
            double seg = cum[k + 1] - cum[k];
            double u = seg < 1e-12 ? 0 : (s - cum[k]) / seg;
            pts.Add((xyz[k * 3] + (xyz[(k + 1) * 3] - xyz[k * 3]) * u,
                     xyz[k * 3 + 1] + (xyz[(k + 1) * 3 + 1] - xyz[k * 3 + 1]) * u,
                     xyz[k * 3 + 2] + (xyz[(k + 1) * 3 + 2] - xyz[k * 3 + 2]) * u));
        }
        return pts;
    }
}
