using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>「量驱动斜面模板」预处理产物（忠实原 <c>PreprocessResult</c>）：合并 bbox 的 z_top/z_floor + XY 范围 + 现状面与各层顶底板采样器。</summary>
public sealed class InclinePreprocessResult
{
    public bool   Success;
    public string Error = "";
    public double ZTop, ZFloor;                 // 顶+底板面合并包围盒的顶/底（常数水平）
    public double MinX, MinY, MaxX, MaxY;        // 合并 XY 范围
    public TinSampler? CurrentSurface;          // 现状面
    public readonly List<SeamSurfaces> Seams = new();
    public int ReadySeamCount;                  // 顶+底板都就绪的层数
}

/// <summary>一层煤的输入（原 <c>SeamInput</c>；Kylin 无实体 handle → 直接携带世界三角网，来源=场景 MeshEntity 或地质库 virtual_drill_surface）。</summary>
public sealed class InclineSeamInput
{
    public string Name = "";
    public double[]? RoofVerts;  public int[]? RoofTris;
    public double[]? FloorVerts; public int[]? FloorTris;
    public string RoofSource = "";      // 诊断：面从哪来（场景层名 / 库表）
    public string FloorSource = "";
    public string Attribute = "";       // 用于计算的属性列名
    public string Category = "";        // 该层在属性里对应的「类别」标签
    public double Density = 1.35;       // 容重 t/m³
    public double IncrementWt;          // Stage2：该层回采增量目标（万t）
    public double BenchHeight;          // 该层煤台阶高度(m)：0=随煤厚整层一个台阶
}

/// <summary>「量驱动斜面模板」全部输入（忠实原 <c>InclineTemplateInput</c>；handle 改为直接几何）。</summary>
public sealed class InclineTemplateInput
{
    public readonly List<InclineSeamInput> Seams = new();
    public readonly List<WorkLineSamples> WorkLines = new();
    public double[]? CurrentSurfaceVerts; public int[]? CurrentSurfaceTris;
    public string CurrentSurfaceName = "";
    public string RegionName = "";
    public string BlockModelName = "";
    public double SlopeAngleDeg = 15;
    public double AnnualProductionWt = 2000;
    public double RecoveryTotalWt = 400;
    public double ApproachThresholdPct = 5.0;
    public int    CoalMode = 0;                // 0=自动识别煤 1=属性≥阈值为煤 2=全部算煤
    public double CoalThreshold = 0;
    public double BenchHeight = 15;
    public double MinBermWidth = 80;
    public double FinalSlopeAngleDeg = 10;
    public double CoalFaceDeg = 65;
    public double RockFaceDeg = 65;
    public bool ConstrainBlockModel = true;
    public int  ConstraintFrontMode = 0;       // 0=Stage1 统一前界 d  1=Stage2 逐层前界 d_seam

    public string Summary =>
        $"{Seams.Count} 层 · 现状面{(CurrentSurfaceVerts != null ? "已选" : "未选")} · 块体「{(string.IsNullOrEmpty(BlockModelName) ? "未选" : BlockModelName)}」· 区域「{(string.IsNullOrEmpty(RegionName) ? "自动·可采" : RegionName)}」· 工作线{WorkLines.Count}条 · α={SlopeAngleDeg:0.#}° · 年产量={AnnualProductionWt:0.#}万t · 回采煤量={RecoveryTotalWt:0.#}万t · 煤判据{CoalMode switch { 1 => "≥阈值", 2 => "全算煤", _ => "自动" }} · 阈值±{ApproachThresholdPct:0.#}%";
}

/// <summary>
/// 预处理（忠实原 <c>InclineTemplatePreprocess</c>）：由输入携带的三角网建 <see cref="TinSampler"/>；
/// 顶+底板面合并包围盒求 z_top/z_floor（斜面竖向范围，常数水平）。现状面单独建采样器。
/// </summary>
public static class InclineTemplatePreprocess
{
    public static InclinePreprocessResult Run(InclineTemplateInput input)
    {
        var r = new InclinePreprocessResult();
        if (input == null) { r.Error = "输入为空"; return r; }
        if (input.Seams.Count == 0) { r.Error = "没有煤层"; return r; }

        r.CurrentSurface = TinSampler.TryBuild(input.CurrentSurfaceVerts, input.CurrentSurfaceTris);
        if (r.CurrentSurface == null) { r.Error = "现状面无效（未选 / 不是三角网 / 几何空）"; return r; }

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool anyBox = false;

        foreach (var s in input.Seams)
        {
            var ss = new SeamSurfaces
            {
                Name = s.Name, Attribute = s.Attribute, Category = s.Category,
                Density = s.Density, IncrementWt = s.IncrementWt, BenchHeight = s.BenchHeight,
                Roof = TinSampler.TryBuild(s.RoofVerts, s.RoofTris),
                Floor = TinSampler.TryBuild(s.FloorVerts, s.FloorTris),
            };
            foreach (var t in new[] { ss.Roof, ss.Floor })
            {
                if (t == null) continue;
                anyBox = true;
                if (t.MinX < minX) minX = t.MinX; if (t.MaxX > maxX) maxX = t.MaxX;
                if (t.MinY < minY) minY = t.MinY; if (t.MaxY > maxY) maxY = t.MaxY;
                if (t.MinZ < minZ) minZ = t.MinZ; if (t.MaxZ > maxZ) maxZ = t.MaxZ;
            }
            if (ss.Ready) r.ReadySeamCount++;
            r.Seams.Add(ss);
        }

        if (!anyBox) { r.Error = "没有任何顶/底板面就绪（请逐层指定顶板面、底板面）"; return r; }

        r.ZTop = maxZ; r.ZFloor = minZ;
        r.MinX = minX; r.MinY = minY; r.MaxX = maxX; r.MaxY = maxY;
        r.Success = true;
        return r;
    }
}

/// <summary>斜面与某层煤顶/底板的交线（前界，每层 2 条；显示用，可不规则）。</summary>
public sealed class SeamIntersection
{
    public string Name = "";
    public readonly List<(double X, double Y, double Z)> RoofLine = new();
    public readonly List<(double X, double Y, double Z)> FloorLine = new();
}

/// <summary>一条工作线生成的工作帮斜面：规则坡顶/坡底折线 + 直纹面三角网 + 各层交线。</summary>
public sealed class InclineSurface
{
    public bool   Success;
    public string Error = "";
    public readonly List<(double X, double Y, double Z)> Crest = new();   // 坡顶线（z_top，前/高）
    public readonly List<(double X, double Y, double Z)> Toe   = new();   // 坡底线（z_floor，后/低）
    public double BaseX, BaseY, BaseZ;
    public readonly List<double> LocalVerts = new();
    public readonly List<int>    Indices    = new();
    public readonly List<SeamIntersection> Intersections = new();

    /// <summary>世界坐标三角网（Kylin MeshEntity 用）。</summary>
    public (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) ToWorldMesh()
    {
        var v = new List<(double, double, double)>(LocalVerts.Count / 3);
        for (int i = 0; i + 2 < LocalVerts.Count; i += 3) v.Add((LocalVerts[i] + BaseX, LocalVerts[i + 1] + BaseY, LocalVerts[i + 2] + BaseZ));
        var t = new List<(int, int, int)>(Indices.Count / 3);
        for (int i = 0; i + 2 < Indices.Count; i += 3) t.Add((Indices[i], Indices[i + 1], Indices[i + 2]));
        return (v, t);
    }
}

/// <summary>
/// 「量驱动斜面模板」构面 + 交线（忠实原 <c>InclineSurfaceBuilder</c>，纯几何）。
/// 按工作线（规则折线）+ 最大工作帮坡角 α + z_top/z_floor（合并 bbox）构一张**规则直纹面**：
/// 坡顶 crest 在 +di（前/高，z_top）、坡底 toe 在 −di（后/低，z_floor），整体可沿 +di 推进 advance。
/// 交线 = 斜面沿坡向（crest→toe）扫描，与各层顶/底板面 Z 相交（逐顶点 t-扫描，吃不规则面）。
/// </summary>
public static class InclineSurfaceBuilder
{
    public static InclineSurface BuildForWorkLine(
        WorkLineSamples wl, double alphaDeg, double zTop, double zFloor, double advance,
        IReadOnlyList<SeamSurfaces>? seams, double extStart = 0, double extEnd = 0)
    {
        var o = new InclineSurface();
        if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1)
        { o.Error = "工作线几何无效"; return o; }
        if (zTop <= zFloor) { o.Error = "z_top ≤ z_floor（合并包围盒异常）"; return o; }

        int n = wl.Baseline.Count;
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);

        double zDatum = 0; for (int i = 0; i < n; i++) zDatum += wl.Baseline[i].Z; zDatum /= n;
        double upOff   = Math.Max(0.0, zTop   - zDatum) / tanA;
        double downOff = Math.Max(0.0, zDatum - zFloor) / tanA;

        int nSeg = Math.Min(wl.Samples.Count, n - 1 + (wl.Closed ? 1 : 0));
        var segDx = new double[nSeg]; var segDy = new double[nSeg];
        for (int i = 0; i < nSeg; i++)
        {
            double dx = wl.Samples[i].Dx, dy = wl.Samples[i].Dy;
            double dl = Math.Sqrt(dx * dx + dy * dy); if (dl < 1e-9) { dx = 1; dy = 0; dl = 1; }
            segDx[i] = dx / dl; segDy[i] = dy / dl;
        }
        var vdx = new double[n]; var vdy = new double[n];
        for (int vi = 0; vi < n; vi++)
        {
            double ax = 0, ay = 0; int cnt = 0;
            if (vi - 1 >= 0 && vi - 1 < nSeg) { ax += segDx[vi - 1]; ay += segDy[vi - 1]; cnt++; }
            if (vi < nSeg)                    { ax += segDx[vi];     ay += segDy[vi];     cnt++; }
            if (cnt == 0 && nSeg > 0)         { ax = segDx[0]; ay = segDy[0]; }
            double al = Math.Sqrt(ax * ax + ay * ay); if (al < 1e-9) { ax = 1; ay = 0; al = 1; }
            vdx[vi] = ax / al; vdy[vi] = ay / al;
        }

        for (int i = 0; i < n; i++)
        {
            var p = wl.Baseline[i];
            double px = p.X + advance * vdx[i], py = p.Y + advance * vdy[i];
            o.Crest.Add((px + upOff   * vdx[i], py + upOff   * vdy[i], zTop));
            o.Toe.Add  ((px - downOff * vdx[i], py - downOff * vdy[i], zFloor));
        }

        ExtendPolyEnds(o.Crest, extStart, extEnd);
        ExtendPolyEnds(o.Toe,   extStart, extEnd);

        int m = o.Crest.Count;
        o.BaseX = o.Crest[0].X; o.BaseY = o.Crest[0].Y; o.BaseZ = o.Crest[0].Z;
        void AddV((double X, double Y, double Z) v)
        { o.LocalVerts.Add(v.X - o.BaseX); o.LocalVerts.Add(v.Y - o.BaseY); o.LocalVerts.Add(v.Z - o.BaseZ); }
        for (int i = 0; i < m; i++) AddV(o.Crest[i]);
        for (int i = 0; i < m; i++) AddV(o.Toe[i]);
        for (int i = 0; i + 1 < m; i++)
        {
            int c0 = i, c1 = i + 1, t0 = m + i, t1 = m + i + 1;
            o.Indices.Add(c0); o.Indices.Add(c1); o.Indices.Add(t1);
            o.Indices.Add(c0); o.Indices.Add(t1); o.Indices.Add(t0);
        }

        if (seams != null)
            foreach (var s in seams)
            {
                var si = new SeamIntersection { Name = s.Name };
                IntersectAlongDip(o.Crest, o.Toe, zTop, zFloor, s.Roof,  si.RoofLine);
                IntersectAlongDip(o.Crest, o.Toe, zTop, zFloor, s.Floor, si.FloorLine);
                if (si.RoofLine.Count > 0 || si.FloorLine.Count > 0) o.Intersections.Add(si);
            }

        o.Success = o.Indices.Count >= 3;
        if (!o.Success) o.Error = "未生成三角网";
        return o;
    }

    private static void ExtendPolyEnds(List<(double X, double Y, double Z)> line, double extStart, double extEnd)
    {
        if (line.Count < 2) return;
        if (extStart > 0.5)
        {
            var p0 = line[0]; var p1 = line[1];
            double dx = p0.X - p1.X, dy = p0.Y - p1.Y;
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l > 1e-9) line.Insert(0, (p0.X + dx / l * extStart, p0.Y + dy / l * extStart, p0.Z));
        }
        if (extEnd > 0.5)
        {
            var pn = line[line.Count - 1]; var pm = line[line.Count - 2];
            double dx = pn.X - pm.X, dy = pn.Y - pm.Y;
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l > 1e-9) line.Add((pn.X + dx / l * extEnd, pn.Y + dy / l * extEnd, pn.Z));
        }
    }

    private static void IntersectAlongDip(
        List<(double X, double Y, double Z)> crest, List<(double X, double Y, double Z)> toe,
        double zTop, double zFloor, TinSampler? sampler, List<(double X, double Y, double Z)> outLine)
    {
        var per = SampleDipPerStation(crest, toe, zTop, zFloor, sampler);
        foreach (var p in per) if (p.HasValue) outLine.Add(p.Value);
    }

    /// <summary>沿坡向逐站求斜面与面 Z 的交点，按站点索引对齐输出（缺覆盖处为 null）。步数由竖向跨度推出（~0.5m/步）。</summary>
    public static (double X, double Y, double Z)?[] SampleDipPerStation(
        IReadOnlyList<(double X, double Y, double Z)> crest,
        IReadOnlyList<(double X, double Y, double Z)> toe,
        double zTop, double zFloor, TinSampler? sampler)
    {
        int n = Math.Min(crest.Count, toe.Count);
        var outArr = new (double X, double Y, double Z)?[n];
        if (sampler == null) return outArr;
        int STEPS = Math.Clamp((int)Math.Ceiling(Math.Abs(zTop - zFloor) / 0.5), 96, 4096);
        for (int i = 0; i < n; i++)
        {
            double cx = crest[i].X, cy = crest[i].Y, tx = toe[i].X, ty = toe[i].Y;
            double? prevF = null; double pX = 0, pY = 0, pZ = 0;
            for (int k = 0; k <= STEPS; k++)
            {
                double t = k / (double)STEPS;
                double x = cx + (tx - cx) * t, y = cy + (ty - cy) * t, z = zTop + (zFloor - zTop) * t;
                if (!sampler.TrySampleZ(x, y, out double sz)) { prevF = null; continue; }
                double f = z - sz;
                if (prevF.HasValue && Math.Sign(f) != Math.Sign(prevF.Value))
                {
                    double denom = prevF.Value - f;
                    double frac = Math.Abs(denom) < 1e-12 ? 0.5 : prevF.Value / denom;
                    outArr[i] = (pX + (x - pX) * frac, pY + (y - pY) * frac, pZ + (z - pZ) * frac);
                    break;
                }
                prevF = f; pX = x; pY = y; pZ = z;
            }
        }
        return outArr;
    }
}
