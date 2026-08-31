using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 块体模型（BlockModelLib 托管切片）—— CSV 块体导入(x,y,z[,尺寸[,品位]]) + 品位配色方块 + 统计。
/// 对应外部交换格式(.blk 思路)；原版 .pmb 私有二进制需格式规范/样本(记录待做)。纯逻辑、可单测。
/// </summary>
public static class BlockModel
{
    public struct Block { public double X, Y, Z, Size, Grade; }

    public sealed class Result
    {
        public bool Success => Error == null;
        public string? Error { get; set; }
        public List<Block> Blocks { get; } = new();
        public double GradeMin, GradeMax, GradeMean;
        public double[] Bounds { get; set; } = { 0, 0, 0, 0 };
        public int SkippedLines { get; set; }
    }

    private static readonly char[] Seps = { ',', '\t', ';' };
    private static bool Num(string s, out double v) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public static Result Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) { return new Result { Error = $"读取失败：{ex.Message}" }; }
    }

    public static Result Parse(string text)
    {
        var r = new Result();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, gsum = 0;
        double gmin = double.MaxValue, gmax = double.MinValue;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) { if (line.Length > 0) r.SkippedLines++; continue; }
            var t = line.Split(Seps, StringSplitOptions.None);
            if (t.Length < 3 || !Num(t[0], out double x) || !Num(t[1], out double y) || !Num(t[2], out double z)) { r.SkippedLines++; continue; }
            var b = new Block { X = x, Y = y, Z = z, Size = 1, Grade = 0 };
            if (t.Length > 3 && Num(t[3], out double sz)) b.Size = sz;
            if (t.Length > 4 && Num(t[4], out double g)) b.Grade = g;
            r.Blocks.Add(b);
            if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            gsum += b.Grade; if (b.Grade < gmin) gmin = b.Grade; if (b.Grade > gmax) gmax = b.Grade;
        }
        if (r.Blocks.Count == 0) { r.Error = "未解析到块体（每行需 X,Y,Z[,尺寸,品位]）"; return r; }
        r.GradeMin = gmin; r.GradeMax = gmax; r.GradeMean = gsum / r.Blocks.Count;
        r.Bounds = new[] { minX, minY, maxX, maxY };
        return r;
    }

    /// <summary>资源量/剥采比：按边界品位 cutoff 分矿石/废石(块体积=尺寸³)。
    /// 返回(矿石体积, 废石体积, 剥采比=废/矿, 矿石平均品位, 金属量=品位·体积Σ, 矿石吨位=体积·密度)。</summary>
    public static (double oreVol, double wasteVol, double stripRatio, double avgGrade, double metal, double tonnage)
        Resource(IReadOnlyList<Block> blocks, double cutoff, double density)
    {
        double ore = 0, waste = 0, gsum = 0;
        foreach (var b in blocks)
        {
            double vol = b.Size * b.Size * b.Size;
            if (b.Grade >= cutoff) { ore += vol; gsum += b.Grade * vol; }
            else waste += vol;
        }
        double strip = ore > 1e-9 ? waste / ore : 0;
        double avg = ore > 1e-9 ? gsum / ore : 0;
        return (ore, waste, strip, avg, gsum, ore * density);
    }

    public readonly record struct BenchResource(
        double ZLow, double ZHigh, double OreVol, double WasteVol,
        double StripRatio, double AvgGrade, double Metal, double Tonnage);

    /// <summary>
    /// 分标高(台阶)资源量 —— 按 benchHeight 把块体分高程带, 各带独立算矿/废/剥采比/品位/金属。
    /// 忠实原「整体+分台阶报量」的分台阶部分, 供分级规划。各带矿量之和 == 整体矿量(守恒)。
    /// </summary>
    public static List<BenchResource> ResourceByElevation(
        IReadOnlyList<Block> blocks, double cutoff, double density, double benchHeight)
    {
        var res = new List<BenchResource>();
        if (blocks == null || blocks.Count == 0) return res;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var b in blocks) { if (b.Z < minZ) minZ = b.Z; if (b.Z > maxZ) maxZ = b.Z; }
        if (benchHeight <= 1e-9) benchHeight = (maxZ - minZ) / 10.0;
        if (benchHeight <= 1e-9) benchHeight = 1.0;
        int nBench = Math.Max(1, (int)Math.Ceiling((maxZ - minZ + 1e-9) / benchHeight));
        var buckets = new List<Block>[nBench];
        for (int i = 0; i < nBench; i++) buckets[i] = new List<Block>();
        foreach (var b in blocks)
        {
            int idx = (int)((b.Z - minZ) / benchHeight);
            if (idx < 0) idx = 0; if (idx >= nBench) idx = nBench - 1;
            buckets[idx].Add(b);
        }
        for (int i = 0; i < nBench; i++)
        {
            if (buckets[i].Count == 0) continue;
            var (ore, waste, strip, avg, metal, tonnage) = Resource(buckets[i], cutoff, density);
            double zl = minZ + i * benchHeight, zh = Math.Min(maxZ, zl + benchHeight);
            res.Add(new BenchResource(zl, zh, ore, waste, strip, avg, metal, tonnage));
        }
        return res;
    }

    /// <summary>分标高资源量 → CSV。</summary>
    public static string ResourceByElevationCsv(IReadOnlyList<BenchResource> benches)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("z_low,z_high,ore_vol,waste_vol,strip_ratio,avg_grade,metal,tonnage\n");
        foreach (var b in benches)
            sb.Append(b.ZLow.ToString("R", inv)).Append(',').Append(b.ZHigh.ToString("R", inv)).Append(',')
              .Append(b.OreVol.ToString("R", inv)).Append(',').Append(b.WasteVol.ToString("R", inv)).Append(',')
              .Append(b.StripRatio.ToString("R", inv)).Append(',').Append(b.AvgGrade.ToString("R", inv)).Append(',')
              .Append(b.Metal.ToString("R", inv)).Append(',').Append(b.Tonnage.ToString("R", inv)).Append('\n');
        return sb.ToString();
    }

    /// <summary>品位 → 蓝(低)→红(高)。</summary>
    public static (float r, float g, float b) GradeColor(double grade, double min, double max)
    {
        double t = Math.Clamp((grade - min) / (max > min ? max - min : 1), 0, 1);
        return ((float)t, 0.30f, (float)(1 - t));
    }

    /// <summary>块体 → 品位配色方块(RectEntity, 平面投影)。</summary>
    public static List<SceneEntity> BuildCells(IReadOnlyList<Block> blocks, double gmin, double gmax)
    {
        var list = new List<SceneEntity>();
        foreach (var b in blocks)
        {
            double h = b.Size / 2;
            var (cr, cg, cb) = GradeColor(b.Grade, gmin, gmax);
            list.Add(new RectEntity { X0 = b.X - h, Y0 = b.Y - h, X1 = b.X + h, Y1 = b.Y + h, Cr = cr, Cg = cg, Cb = cb });
        }
        return list;
    }
}
