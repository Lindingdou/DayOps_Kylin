using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 煤质属性联动（忠实原 CoalQualityBlockBridge）：把钻孔煤质指标按煤层估到块体煤块 → 写成块体属性列（Ad/St/Qnet/Vdaf/G，
/// 克里金另写 _σ 标准差列 + 煤质可信度列）。样本来自 <see cref="GeoDataQueries.GetCoalSamples"/>；
/// 插值走 <see cref="OrdinaryKriging"/>（OK / IDW / 最近邻）。反向：在钻孔点采样块体属性做 煤质×块体 交会。纯逻辑、可单测。
/// </summary>
public static class BlockCoalQualityLink
{
    public static readonly string[] Methods = { "克里金 (OK)", "IDW", "最近邻" };
    public const string Skip = "(跳过)";

    public sealed record SeamMapping(double BlockValue, string SeamCode);

    public sealed class Result
    {
        public bool Ok; public string Message = ""; public int CoalCells;
        public readonly List<(string Column, int Written)> Columns = new();
        public readonly List<(string Column, int Written)> VarianceColumns = new();
        public string? ReliabilityColumn; public int RelHigh, RelMid, RelLow;
    }

    /// <summary>指标代码 → (列名, 取值)。</summary>
    public static (string Col, Func<CoalSample, double?> Get, string Name) Indicator(string code, bool clean) => code switch
    {
        "ad" => (clean ? "AdClean" : "Ad", r => clean ? r.AdClean : r.AdRaw, "Ad 灰分"),
        "std" => (clean ? "StClean" : "St", r => clean ? r.StdClean : r.StdRaw, "St 全硫"),
        "vdaf" => (clean ? "VdafClean" : "Vdaf", r => clean ? r.VdafClean : r.VdafRaw, "Vdaf 挥发分"),
        "qnet" => ("Qnet", r => r.QnetAd, "Qnet 发热量"),
        "caking" => ("G", r => r.CakingG, "G 粘结指数"),
        _ => (code, _ => null, code),
    };

    /// <summary>某属性的不同取值 + 块数 + 类别名（原 DistinctValues），升序。</summary>
    public static List<(double Value, int Count, string? Label)> DistinctValues(BlockModelMeta m, string attr)
    {
        var res = new List<(double, int, string?)>();
        var arr = m.GetAttr(attr);
        if (arr == null) return res;
        var counts = new SortedDictionary<double, int>();
        for (int i = 0; i < arr.Length; i++)
        {
            if (m.DeletedIds.Contains(i)) continue;
            double v = arr[i]; if (double.IsNaN(v)) continue;
            counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
            if (counts.Count > 256) break;
        }
        var labels = m.FindColumn(attr)?.CategoryLabels;
        foreach (var kv in counts)
        {
            int code = (int)Math.Round(kv.Key);
            string? label = labels != null && Math.Abs(kv.Key - code) < 1e-9 && code >= 0 && code < labels.Count ? labels[code] : null;
            res.Add((kv.Key, kv.Value, string.IsNullOrEmpty(label) ? null : label));
        }
        return res;
    }

    private static string? SeamOf(double v, IReadOnlyList<SeamMapping> mapping)
    {
        foreach (var mm in mapping) if (Math.Abs(mm.BlockValue - v) < 0.5) return mm.SeamCode;
        return null;
    }

    /// <summary>预检：每个煤层的煤块数（原 PreviewCells）。</summary>
    public static List<(string Seam, int Cells)> PreviewCells(BlockModelMeta m, string seamAttr, IReadOnlyList<SeamMapping> mapping)
    {
        var d = new Dictionary<string, int>();
        var arr = m.GetAttr(seamAttr);
        if (arr != null)
            for (int i = 0; i < arr.Length; i++)
            {
                if (m.DeletedIds.Contains(i)) continue;
                var seam = SeamOf(arr[i], mapping); if (seam == null) continue;
                d[seam] = d.TryGetValue(seam, out var c) ? c + 1 : 0 + 1;
            }
        return d.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>某煤层可用控制点（有 Z 与该指标）。seam=null 取全部。</summary>
    public static List<OrdinaryKriging.ControlPoint> ControlPoints(IReadOnlyList<CoalSample> samples, string? seam, Func<CoalSample, double?> get)
    {
        var l = new List<OrdinaryKriging.ControlPoint>();
        foreach (var s in samples)
        {
            if (seam != null && !string.Equals(s.SeamCode, seam, StringComparison.OrdinalIgnoreCase)) continue;
            var v = get(s); if (!v.HasValue || !s.Z.HasValue) continue;
            l.Add(new OrdinaryKriging.ControlPoint(s.X, s.Y, s.Z.Value, v.Value));
        }
        return l;
    }

    /// <summary>按方法估一点：返回 (估值, 方差或 null)；半径外/无样本 null。</summary>
    public static (double est, double? var)? EstimatePoint(IReadOnlyList<OrdinaryKriging.ControlPoint> ctrl, double x, double y, double z, string method, OrdinaryKriging.Variogram? vg)
    {
        if (ctrl.Count == 0) return null;
        if (method.StartsWith("IDW", StringComparison.OrdinalIgnoreCase))
        {
            var r = OrdinaryKriging.IdwEstimate(ctrl, x, y, z);
            return r.HasValue ? (r.Value.est, null) : null;
        }
        if (method.Contains("最近邻"))
        {
            double best = double.MaxValue; double v = 0;
            foreach (var p in ctrl) { double dx = p.X - x, dy = p.Y - y, dz = p.Z - z, d2 = dx * dx + dy * dy + dz * dz; if (d2 < best) { best = d2; v = p.V; } }
            return (v, null);
        }
        if (ctrl.Count < 3) { var r = OrdinaryKriging.IdwEstimate(ctrl, x, y, z); return r.HasValue ? (r.Value.est, null) : null; }
        var k = OrdinaryKriging.EstimateAt(ctrl, x, y, z, vg: vg);
        return k.HasValue ? (k.Value.est, k.Value.variance) : null;
    }

    /// <summary>估值主流程（原 Estimate）：按煤层分组煤块 → 逐指标插值写列。</summary>
    public static Result Estimate(BlockModelMeta m, string seamAttr, IReadOnlyList<SeamMapping> mapping, IReadOnlyList<string> indicators,
        bool useClean, string method, IReadOnlyList<CoalSample> samples)
    {
        var res = new Result();
        if (mapping.Count == 0) { res.Message = "未设置任何「值→煤层」映射。"; return res; }
        if (indicators.Count == 0) { res.Message = "未选择任何指标。"; return res; }
        var arr = m.GetAttr(seamAttr);
        if (arr == null) { res.Message = $"块体无属性列「{seamAttr}」数据，无法判煤层。"; return res; }

        var cellsBySeam = new Dictionary<string, List<int>>();
        for (int i = 0; i < arr.Length; i++)
        {
            if (m.DeletedIds.Contains(i)) continue;
            double sv = arr[i]; if (double.IsNaN(sv)) continue;
            var seam = SeamOf(sv, mapping); if (seam == null) continue;
            if (!cellsBySeam.TryGetValue(seam, out var l)) cellsBySeam[seam] = l = new List<int>();
            l.Add(i);
        }
        res.CoalCells = cellsBySeam.Sum(kv => kv.Value.Count);
        if (res.CoalCells == 0) { res.Message = "没有匹配到任何煤块（检查「值→煤层」映射与属性列）。"; return res; }

        bool kriging = method.Contains("克里金");
        foreach (var code in indicators)
        {
            var (col, get, _) = Indicator(code, useClean);
            var data = m.EnsureAttr(col, 0);
            if (m.FindColumn(col) is { } c0 && string.IsNullOrEmpty(c0.Description)) c0.Description = "煤质估值(块体联动)";
            string vcol = col + "_σ";
            double[]? vdata = null;
            int written = 0, varWritten = 0;
            foreach (var (seam, cells) in cellsBySeam)
            {
                var ctrl = ControlPoints(samples, seam, get);
                if (ctrl.Count < 1) continue;
                OrdinaryKriging.Variogram? vg = null;
                if (kriging && ctrl.Count >= 3) { try { vg = OrdinaryKriging.FitVariogram(ctrl); } catch { vg = null; } }
                foreach (var idx in cells)
                {
                    var b = m.Blocks[idx];
                    var e = EstimatePoint(ctrl, b.X, b.Y, b.Z, method, vg);
                    if (e == null) continue;
                    data[idx] = e.Value.est; written++;
                    if (e.Value.var is double vv && vv >= 0)
                    {
                        vdata ??= m.EnsureAttr(vcol, 0);
                        if (varWritten == 0 && m.FindColumn(vcol) is { } vc) vc.Description = "煤质估计标准差 ±(块体联动)";
                        vdata[idx] = Math.Sqrt(vv); varWritten++;
                    }
                }
            }
            res.Columns.Add((col, written));
            if (varWritten > 0) res.VarianceColumns.Add((vcol, varWritten));
        }

        WriteReliability(m, cellsBySeam, samples, res);

        if (res.Columns.Sum(c => c.Written) == 0)
        {
            res.Message = "未写入任何值：所选煤层没有带坐标/指标的钻孔化验点，或煤块都落在化验区搜索半径外（可改用 IDW / 增大半径）。";
            return res;
        }
        res.Ok = true;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"煤块 {res.CoalCells:N0} 个，方法 {method}，样品 {(useClean ? "浮煤" : "原煤")}。已写入属性列：");
        foreach (var (c, w) in res.Columns) sb.AppendLine($"  {c}: {w:N0} 块");
        foreach (var (c, w) in res.VarianceColumns) sb.AppendLine($"  {c}: {w:N0} 块（估计标准差 ±）");
        if (res.ReliabilityColumn != null) sb.AppendLine($"  {res.ReliabilityColumn}: 高 {res.RelHigh:N0} / 中 {res.RelMid:N0} / 低 {res.RelLow:N0}");
        res.Message = sb.ToString().TrimEnd();
        return res;
    }

    /// <summary>「煤质可信度」列（0..1，按到最近钻孔距离归一，1=紧邻实测）+ 高/中/低分区计数。</summary>
    private static void WriteReliability(BlockModelMeta m, Dictionary<string, List<int>> cellsBySeam, IReadOnlyList<CoalSample> samples, Result res)
    {
        const string col = "煤质可信度";
        double[]? data = null;
        foreach (var (seam, cells) in cellsBySeam)
        {
            var pts = samples.Where(s => string.Equals(s.SeamCode, seam, StringComparison.OrdinalIgnoreCase) && s.Z.HasValue).Select(s => (s.X, s.Y, s.Z!.Value)).ToList();
            if (pts.Count == 0) continue;
            double s0 = MedianNearestSpacing(pts);
            if (!(s0 > 0)) s0 = 1;
            data ??= m.EnsureAttr(col, 0);
            foreach (var idx in cells)
            {
                var b = m.Blocks[idx];
                double best = double.MaxValue;
                foreach (var (px, py, pz) in pts) { double dx = px - b.X, dy = py - b.Y, dz = pz - b.Z, d2 = dx * dx + dy * dy + dz * dz; if (d2 < best) best = d2; }
                double d = Math.Sqrt(best);
                double rel = Math.Clamp(1.0 - d / (2.0 * s0), 0, 1);
                data[idx] = rel;
                if (rel >= 0.67) res.RelHigh++; else if (rel >= 0.33) res.RelMid++; else res.RelLow++;
            }
        }
        if (data != null) { res.ReliabilityColumn = col; if (m.FindColumn(col) is { } c) c.Description = "煤质估值可信度(0..1)"; }
    }

    /// <summary>控制点集的中位最近邻间距。</summary>
    public static double MedianNearestSpacing(List<(double X, double Y, double Z)> pts)
    {
        if (pts.Count < 2) return 0;
        var nn = new List<double>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < pts.Count; j++)
            {
                if (i == j) continue;
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y, dz = pts[i].Z - pts[j].Z, d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best) best = d2;
            }
            nn.Add(Math.Sqrt(best));
        }
        nn.Sort();
        return nn[nn.Count / 2];
    }

    /// <summary>在世界点采样块体属性值（落在哪个块就取哪个；块外 null）（原 BlockValueAt）。</summary>
    public static double? BlockValueAt(BlockModelMeta m, double x, double y, double z, string attr)
    {
        var arr = m.GetAttr(attr);
        if (arr == null) return null;
        if (m.IsRegular)
        {
            int i = (int)Math.Floor((x - m.Ox) / m.Sx), j = (int)Math.Floor((y - m.Oy) / m.Sy), k = (int)Math.Floor((z - m.Oz) / m.Sz);
            if (i < 0 || j < 0 || k < 0 || i >= m.Nx || j >= m.Ny || k >= m.Nz) return null;
            int idx = i + j * m.Nx + k * m.Nx * m.Ny;
            return idx < arr.Length && !m.DeletedIds.Contains(idx) ? arr[idx] : null;
        }
        for (int idx = 0; idx < m.Blocks.Count; idx++)
        {
            if (m.DeletedIds.Contains(idx)) continue;
            var b = m.Blocks[idx];
            double k = m.CellScale(b);
            double hx = k * m.Sx * 0.5, hy = k * m.Sy * 0.5, hz = k * m.Sz * 0.5;
            if (Math.Abs(x - b.X) <= hx && Math.Abs(y - b.Y) <= hy && Math.Abs(z - b.Z) <= hz) return idx < arr.Length ? arr[idx] : null;
        }
        return null;
    }

    public static double Pearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        int n = xs.Count; if (n < 2) return 0;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sx += dx * dx; sy += dy * dy; }
        double d = Math.Sqrt(sx * sy);
        return d < 1e-12 ? 0 : sxy / d;
    }
}
