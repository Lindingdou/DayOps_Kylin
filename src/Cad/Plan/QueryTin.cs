// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/QueryTin.cs（逐行对应；仅命名空间适配）
using System;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 「查询台阶平盘标高」用的后端三角网：从 CDT 的 PMCB 载荷解析出顶点 / 三角形，
/// <b>只驻留内存、不入场景</b>（故不显示），在世界 XY 处竖直落到最上表面取 Z。
/// 与引擎 PickWorldOnGeometry 同法（重心坐标 + 取最高命中），保证一致。
/// </summary>
internal sealed class QueryTin
{
    private readonly double[] _vx, _vy, _vz;   // 世界顶点
    private readonly int[] _tris;              // 三角索引（3 个/面）

    public int TriangleCount => _tris.Length / 3;

    private QueryTin(double[] vx, double[] vy, double[] vz, int[] tris)
    { _vx = vx; _vy = vy; _vz = vz; _tris = tris; }

    /// <summary>从 CDT 返回的 PMCB 载荷解析（顶点存绝对世界坐标）。空 / 坏数据返回 null。</summary>
    public static QueryTin? FromPmcb(byte[] pmcb)
    {
        if (pmcb == null || pmcb.Length < 12) return null;
        int pos = 0;
        if (BitConverter.ToUInt32(pmcb, pos) != 0x42434D50u) return null;   // 'PMCB'
        pos += 8;   // magic + version
        byte success = pmcb[pos++];
        int errLen = BitConverter.ToInt32(pmcb, pos); pos += 4;
        pos += Math.Max(0, errLen);
        if (success == 0) return null;

        float bx = BitConverter.ToSingle(pmcb, pos); pos += 4;
        float by = BitConverter.ToSingle(pmcb, pos); pos += 4;
        float bz = BitConverter.ToSingle(pmcb, pos); pos += 4;

        int vc = BitConverter.ToInt32(pmcb, pos); pos += 4;
        if (vc < 3) return null;
        var vx = new double[vc]; var vy = new double[vc]; var vz = new double[vc];
        for (int i = 0; i < vc; i++)
        {
            vx[i] = BitConverter.ToSingle(pmcb, pos) + bx; pos += 4;
            vy[i] = BitConverter.ToSingle(pmcb, pos) + by; pos += 4;
            vz[i] = BitConverter.ToSingle(pmcb, pos) + bz; pos += 4;
        }
        int tc = BitConverter.ToInt32(pmcb, pos); pos += 4;
        if (tc < 1) return null;
        var tris = new int[tc * 3];
        for (int i = 0; i < tc * 3; i++) { tris[i] = BitConverter.ToInt32(pmcb, pos); pos += 4; }

        return new QueryTin(vx, vy, vz, tris);
    }

    /// <summary>在世界 (x,y) 竖直落到最上表面三角网取 Z。命中返回 true；点在网外返回 false。</summary>
    public bool SampleZ(double x, double y, out double z)
    {
        z = 0; bool found = false; double best = double.NegativeInfinity;
        for (int t = 0; t + 3 <= _tris.Length; t += 3)
        {
            int a = _tris[t], b = _tris[t + 1], c = _tris[t + 2];
            if (a < 0 || b < 0 || c < 0 || a >= _vx.Length || b >= _vx.Length || c >= _vx.Length) continue;
            double ax = _vx[a], ay = _vy[a], bx = _vx[b], by = _vy[b], cx = _vx[c], cy = _vy[c];
            double d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(d) < 1e-12) continue;
            double l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
            double l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
            double l3 = 1.0 - l1 - l2;
            if (l1 < -1e-6 || l2 < -1e-6 || l3 < -1e-6) continue;
            double zz = l1 * _vz[a] + l2 * _vz[b] + l3 * _vz[c];
            if (!found || zz > best) { best = zz; found = true; }
        }
        z = best;
        return found;
    }
}
