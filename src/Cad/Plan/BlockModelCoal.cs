using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 块体侧煤/岩口径（原 <c>BlockModelLib.Domain.DepositAutoDetector.TryGuessCoal / TryGetClassifier / DetectAuto</c>
/// + <c>SectionSampler.SampleLayers</c> 在 Kylin <see cref="BlockModelMeta"/> 上的实现）。
/// 猜煤规则逐字照原版：① 分类列里标签含"煤/coal"→取该码；② 列名含 coal/煤 →按 ==1。
/// 显式关联（<see cref="BlockModelMeta.CoalAttribute"/>）优先。
/// </summary>
public static class BlockModelCoal
{
    private readonly record struct CoalSel(string Attr, double Value, double Tol);

    /// <summary>对外暴露"煤属性"（单值口径）。找不到返回 false。</summary>
    public static bool TryGuessCoal(BlockModelMeta? model, out string attr, out double value, out double tol)
    {
        attr = ""; value = 0; tol = 0;
        if (model == null) return false;
        if (!string.IsNullOrEmpty(model.CoalAttribute) && model.HasData(model.CoalAttribute))
        {
            // 显式关联：分类列取首个含"煤"的码，否则按 ==1
            var col = model.FindColumn(model.CoalAttribute);
            if (col is { IsCategorical: true, CategoryLabels: { Count: > 0 } labels })
            {
                for (int code = 0; code < labels.Count; code++)
                    if (IsCoalLabel(labels[code])) { attr = model.CoalAttribute; value = code; tol = 0.5; return true; }
            }
            attr = model.CoalAttribute; value = 1.0; tol = 0.5; return true;
        }
        if (GuessCoal(model) is { } s) { attr = s.Attr; value = s.Value; tol = s.Tol; return true; }
        return false;
    }

    /// <summary>煤/岩判别器（多层煤）。显式关联优先，退回自动猜（单煤值，岩=非煤）。</summary>
    public static bool TryGetClassifier(BlockModelMeta? model, out string attr, out CoalRockClassifier cls)
    {
        cls = new CoalRockClassifier(); attr = "";
        if (model == null) return false;
        if (!string.IsNullOrEmpty(model.CoalAttribute) && model.HasData(model.CoalAttribute))
        {
            var col = model.FindColumn(model.CoalAttribute);
            var codes = new List<double>();
            if (col is { IsCategorical: true, CategoryLabels: { Count: > 0 } labels })
                for (int code = 0; code < labels.Count; code++) if (IsCoalLabel(labels[code])) codes.Add(code);
            if (codes.Count == 0) codes.Add(1.0);
            attr = model.CoalAttribute; cls.CoalCodes = codes.ToArray(); cls.Tol = 0.5;
            return true;
        }
        if (GuessCoal(model) is { } s)
        {
            attr = s.Attr; cls.CoalCodes = new[] { s.Value }; cls.RockCodes = Array.Empty<double>(); cls.Tol = s.Tol;
            return true;
        }
        return false;
    }

    private static bool IsCoalLabel(string? lbl)
        => !string.IsNullOrEmpty(lbl) && (lbl.Contains('煤') || lbl.IndexOf("coal", StringComparison.OrdinalIgnoreCase) >= 0);

    private static CoalSel? GuessCoal(BlockModelMeta model)
    {
        foreach (var col in model.PropertySchema)
        {
            if (col.IsCategorical && col.CategoryLabels != null && model.HasData(col.Name))
            {
                for (int code = 0; code < col.CategoryLabels.Count; code++)
                    if (IsCoalLabel(col.CategoryLabels[code])) return new CoalSel(col.Name, code, 0.5);
            }
        }
        foreach (var col in model.PropertySchema)
        {
            if ((col.Name.IndexOf("coal", StringComparison.OrdinalIgnoreCase) >= 0 || col.Name.Contains('煤')) && model.HasData(col.Name))
                return new CoalSel(col.Name, 1.0, 0.5);
        }
        return null;
    }

    /// <summary>自动选煤属性并做 PCA 判型；找不到煤属性或煤单元过少返回 null。</summary>
    public static (DepositSignature sig, string attr)? DetectAuto(BlockModelMeta? model)
    {
        if (!TryGuessCoal(model, out var attr, out var val, out var tol)) return null;
        var arr = model!.GetAttr(attr);
        if (arr == null) return null;
        var cells = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < model.Blocks.Count && i < arr.Length; i++)
        {
            if (model.DeletedIds.Contains(i)) continue;
            if (Math.Abs(arr[i] - val) > tol) continue;
            var b = model.Blocks[i];
            cells.Add((b.X, b.Y, b.Z));
        }
        var sig = DepositAutoDetector.Detect(cells, model.Sz);
        return sig is { } s ? (s, attr) : null;
    }

    /// <summary>找灰分属性列（名称含 ash/灰 的非分类列）；找不到返回 null。</summary>
    public static string? FindAshAttr(BlockModelMeta model)
    {
        foreach (var col in model.PropertySchema)
        {
            if (col.IsCategorical) continue;
            var nm = col.Name ?? "";
            if ((nm.IndexOf("ash", StringComparison.OrdinalIgnoreCase) >= 0 || nm.Contains('灰')) && model.HasData(nm))
                return nm;
        }
        return null;
    }

    /// <summary>取块体平面尺寸 (m)（原 TryModelExtent）。</summary>
    public static bool TryModelExtent(BlockModelMeta? m, out double lx, out double ly)
    {
        lx = ly = 0;
        if (m == null) return false;
        var b = m.Bounds;
        lx = b.maxX - b.minX; ly = b.maxY - b.minY;
        return lx > 0 && ly > 0;
    }
}

/// <summary>资源纵向剖面（按 Z 层聚合的煤/岩体积）。境界优化求解的采样产物。k=0 在底，k=Nz-1 在顶。（原 <c>ResourceProfile</c>）</summary>
public sealed class ResourceProfile
{
    public int Nz;
    public double Dz;           // 层厚 = BlockSize.Z (m)
    public double CellVolM3;    // 单元体积 dx·dy·dz (m³)
    public double Density;      // 煤密度 (t/m³)
    public double PlanAreaM2;   // 模型平面投影面积 (m²)
    public double[] CoalVol = Array.Empty<double>();   // 每层煤体积 (m³)
    public double[] WasteVol = Array.Empty<double>();  // 每层岩体积 (m³)
    public double TotalCoalVol;                         // 全模型煤体积 (m³)，算回收率用
    public double AvgAshPct;                            // 圈入煤的体积加权平均灰分 (%)
    public bool HasAsh;
}

/// <summary>
/// 块体侧采样器（"块体归块体"，原 <c>BlockModelLib.Domain.SectionSampler.SampleLayers</c>）。把激活块体按 Z 层聚合成煤/岩体积纵剖面。
/// 非煤、非删除单元视为岩（剥离）。层号 k 由块中心相对模型底 Oz 按 Sz 分层（子块按尺寸倍数计体积）。
/// </summary>
public static class PlanSectionSampler
{
    public static ResourceProfile SampleLayers(BlockModelMeta model, string coalAttr, double coalValue,
                                               double tol, double density, CancellationToken ct = default,
                                               double[]? clipPolygonXY = null,
                                               double[][]? layerClipsXY = null,
                                               string? ashAttr = null)
    {
        var bounds = model.Bounds;
        double dz = model.Sz > 1e-9 ? model.Sz : 1.0;
        int nz = Math.Max(1, (int)Math.Round((bounds.maxZ - bounds.minZ) / dz));
        double oz = bounds.minZ;
        bool clip = clipPolygonXY != null && clipPolygonXY.Length >= 6;
        bool perLayer = layerClipsXY != null;

        var prof = new ResourceProfile
        {
            Nz = nz,
            Dz = dz,
            CellVolM3 = model.Sx * model.Sy * model.Sz,
            Density = density,
            PlanAreaM2 = clip ? PolygonArea(clipPolygonXY!) : (bounds.maxX - bounds.minX) * (bounds.maxY - bounds.minY),
            CoalVol = new double[nz],
            WasteVol = new double[nz],
        };

        var arr = model.GetAttr(coalAttr);
        if (arr == null) return prof;
        var ashArr = ashAttr != null ? model.GetAttr(ashAttr) : null;
        double ashSum = 0, ashVol = 0;

        int n = Math.Min(model.Blocks.Count, arr.Length);
        for (int idx = 0; idx < n; idx++)
        {
            if ((idx & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (model.DeletedIds.Contains(idx)) continue;
            var b = model.Blocks[idx];
            double sc = model.CellScale(b);
            double cellVol = prof.CellVolM3 * sc * sc * sc;
            int k = Math.Clamp((int)Math.Floor((b.Z - oz) / dz), 0, nz - 1);
            bool isCoal = Math.Abs(arr[idx] - coalValue) <= tol;
            if (isCoal) prof.TotalCoalVol += cellVol;          // 全模型煤（回收率分母，不受裁剪）

            if (perLayer)
            {
                double[]? poly = (k >= 0 && k < layerClipsXY!.Length) ? layerClipsXY[k] : null;
                if (poly != null)
                {
                    if (poly.Length < 6) continue;              // 空/退化=该层圈外（坑已收口）
                    if (!PointInPolygon(poly, b.X, b.Y)) continue;
                }
            }
            else if (clip)
            {
                if (!PointInPolygon(clipPolygonXY!, b.X, b.Y)) continue;
            }

            if (isCoal)
            {
                prof.CoalVol[k] += cellVol;
                if (ashArr != null && idx < ashArr.Length) { ashSum += ashArr[idx] * cellVol; ashVol += cellVol; }
            }
            else prof.WasteVol[k] += cellVol;
        }

        if (ashVol > 0) { prof.AvgAshPct = ashSum / ashVol; prof.HasAsh = true; }
        return prof;
    }

    /// <summary>射线法点在多边形内判定（xy 扁平 [x0,y0,...]）。</summary>
    public static bool PointInPolygon(double[] xy, double px, double py)
    {
        int n = xy.Length / 2; bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = xy[i * 2], yi = xy[i * 2 + 1], xj = xy[j * 2], yj = xy[j * 2 + 1];
            if (((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi) + xi)) inside = !inside;
        }
        return inside;
    }

    /// <summary>鞋带公式求多边形面积（m²）。</summary>
    public static double PolygonArea(double[] xy)
    {
        int n = xy.Length / 2; double a = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
            a += (xy[j * 2] + xy[i * 2]) * (xy[j * 2 + 1] - xy[i * 2 + 1]);
        return Math.Abs(a) / 2.0;
    }
}

/// <summary>深度版境界圈定求解结果（原 <c>DepthSolveResult</c>）。</summary>
public sealed class PitDepthSolveResult
{
    public int BottomK;          // 坑底所在层（k 越小越深）
    public double CoalT;         // 圈入煤量 (t)
    public double WasteM3;       // 圈入岩量 (m³)
    public double ContourSR;     // 境界剥采比（坑底层边际，m³/t）
    public double NetValueYuan;  // 最优净值 (元)
    // 逐层曲线（自顶向下每层：累计深度 / 该层边际剥采比 / 累计净值）
    public double[] CurveDepthM = Array.Empty<double>();
    public double[] CurveContourSR = Array.Empty<double>();
    public double[] CurveNetYuan = Array.Empty<double>();
}

/// <summary>
/// 境界圈定求解（原 <c>SectionSolver.SolveDepth</c>）：从顶向下逐层累加煤/岩，按净值最大定坑底。
/// 与"境界剥采比法"等价——净值最大处的边际剥采比 ≈ 经济合理剥采比。
/// </summary>
public static class PitSectionSolver
{
    public static PitDepthSolveResult SolveDepth(ResourceProfile p, double revenuePerCoalT, double stripCostPerM3,
                                                 double maxDepthM = double.MaxValue)
    {
        int nz = p.Nz;
        double dens = p.Density;
        int kFloor = 0;
        if (maxDepthM < double.MaxValue && p.Dz > 1e-9)
            kFloor = Math.Max(0, nz - Math.Max(1, (int)Math.Floor(maxDepthM / p.Dz)));

        double cumCoalT = 0, cumWasteM3 = 0, bestNet = double.NegativeInfinity;
        int bestK = nz;
        double bestCoalT = 0, bestWasteM3 = 0;
        var cd = new List<double>(); var csr = new List<double>(); var cnet = new List<double>();

        for (int k = nz - 1; k >= kFloor; k--)
        {
            cumCoalT += p.CoalVol[k] * dens;
            cumWasteM3 += p.WasteVol[k];
            double net = cumCoalT * revenuePerCoalT - cumWasteM3 * stripCostPerM3;
            double layerCoalT = p.CoalVol[k] * dens;
            cd.Add((nz - k) * p.Dz);
            csr.Add(layerCoalT > 1e-6 ? p.WasteVol[k] / layerCoalT : (p.WasteVol[k] > 0 ? double.PositiveInfinity : 0));
            cnet.Add(net);
            if (net > bestNet)
            {
                bestNet = net; bestK = k;
                bestCoalT = cumCoalT; bestWasteM3 = cumWasteM3;
            }
        }

        double margSR = (bestK < nz && p.CoalVol[bestK] * dens > 1e-6)
            ? p.WasteVol[bestK] / (p.CoalVol[bestK] * dens)
            : 0;

        return new PitDepthSolveResult
        {
            BottomK = bestK,
            CoalT = bestCoalT,
            WasteM3 = bestWasteM3,
            ContourSR = margSR,
            NetValueYuan = bestNet < 0 ? 0 : bestNet,
            CurveDepthM = cd.ToArray(), CurveContourSR = csr.ToArray(), CurveNetYuan = cnet.ToArray(),
        };
    }
}
