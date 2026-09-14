using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 块体模型报告（忠实原 BlockReportGenerator）：汇总（块数/可见/已删/体积/AABB）+ 每属性 min/max/mean/std/非默认数 + 20 桶直方图
/// + 分标高统计（Kylin 补：按 Z 层块数/体积，用 <see cref="BlockModel.ResourceByElevation"/> 的分层口径）。输出 纯文本 / CSV / HTML。
/// PDF 依赖 QuestPDF，Kylin 无该库 → 不提供。纯逻辑、可单测。
/// </summary>
public static class BlockModelReport
{
    public const int HistogramBins = 20;

    public sealed class AttributeStats
    {
        public string Name = ""; public string Unit = ""; public BlockPropertyType DataType;
        public long NonDefaultCount; public double Min = double.NaN, Max = double.NaN, Mean = double.NaN, Std = double.NaN, DefaultValue;
        public long[]? Histogram;
    }

    public sealed class LevelRow { public double ZLow, ZHigh; public long Cells; public double Volume; }

    public sealed class Report
    {
        public string ModelName = ""; public string Description = ""; public DateTime GeneratedAt = DateTime.Now;
        public long TotalCells, DeletedCells, VisibleCells;
        public double Sx, Sy, Sz; public int Nx, Ny, Nz;
        public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) Bounds;
        public BlockStorageMode StorageMode; public string ActiveColormapAttribute = "";
        public bool IsScoped; public string ScopeLabel = "全部块"; public long ScopedCells;
        public long SubCellCount; public double SubCellVolume;
        public double SingleCellVolume => Sx * Sy * Sz;
        public double ScopedVolume => ScopedCells * SingleCellVolume;
        public double VisibleVolume => VisibleCells * SingleCellVolume + SubCellVolume;
        public List<AttributeStats> Attributes = new();
        public List<LevelRow> Levels = new();
        public string BoundsText => $"({Bounds.minX:0.##}, {Bounds.minY:0.##}, {Bounds.minZ:0.##}) – ({Bounds.maxX:0.##}, {Bounds.maxY:0.##}, {Bounds.maxZ:0.##})";
    }

    /// <summary>计算报告。scopeFilter 非空时统计限定到匹配 cell 子集（引用属性须有数据，否则退回全部）。</summary>
    public static Report Compute(BlockModelMeta m, BlockFilterSet? scopeFilter = null)
    {
        int total = m.Blocks.Count;
        long deleted = m.DeletedIds.Count;
        long sub = 0; double subVol = 0;
        for (int i = 0; i < total; i++)
        {
            if (m.DeletedIds.Contains(i)) continue;
            var b = m.Blocks[i];
            if (m.IsVarCell(b)) { double k = m.CellScale(b); sub++; subVol += k * k * k * m.CellVolume; }
        }
        long visible = total - deleted - sub;

        bool scoped = false; string scopeLabel = "全部块";
        if (scopeFilter != null && scopeFilter.Conditions.Count > 0)
        {
            bool ready = true;
            foreach (var c in scopeFilter.Conditions) if (!m.HasData(c.AttributeName)) { ready = false; break; }
            scoped = ready;
            if (ready) scopeLabel = "当前筛选：" + scopeFilter;
        }
        bool[]? mask = null; long scopedCells = 0;
        if (scoped)
        {
            mask = new bool[total];
            for (int i = 0; i < total; i++)
            {
                if (m.DeletedIds.Contains(i)) continue;
                if (!scopeFilter!.Match(a => m.GetValue(a, i))) continue;
                mask[i] = true; scopedCells++;
            }
        }

        var rep = new Report
        {
            ModelName = m.Name, Description = m.Description ?? "", TotalCells = m.BlockCount, DeletedCells = deleted, VisibleCells = Math.Max(0, visible),
            Sx = m.Sx, Sy = m.Sy, Sz = m.Sz, Nx = m.Nx, Ny = m.Ny, Nz = m.Nz, Bounds = m.Bounds, StorageMode = m.StorageMode,
            ActiveColormapAttribute = m.ActiveColormapAttribute ?? "", IsScoped = scoped, ScopeLabel = scopeLabel, ScopedCells = scopedCells,
            SubCellCount = sub, SubCellVolume = subVol,
        };

        bool Included(int i) => mask != null ? mask[i] : !(m.DeletedIds.Count > 0 && m.DeletedIds.Contains(i));

        foreach (var col in m.PropertySchema)
        {
            var arr = m.GetAttr(col.Name);
            if (arr == null)
            {
                rep.Attributes.Add(new AttributeStats { Name = col.Name, Unit = col.Unit, DataType = col.DataType, DefaultValue = col.DefaultValue });
                continue;
            }
            double mn = double.PositiveInfinity, mx = double.NegativeInfinity, sum = 0; long nonDef = 0, n = 0;
            for (int i = 0; i < total && i < arr.Length; i++)
            {
                if (!Included(i)) continue;
                double v = arr[i]; if (double.IsNaN(v)) continue;
                if (v < mn) mn = v; if (v > mx) mx = v; sum += v; n++;
                if (Math.Abs(v - col.DefaultValue) > 1e-12) nonDef++;
            }
            if (n == 0) { rep.Attributes.Add(new AttributeStats { Name = col.Name, Unit = col.Unit, DataType = col.DataType, DefaultValue = col.DefaultValue }); continue; }
            double mean = sum / n, sse = 0; long[]? hist = null; double range = mx - mn;
            if (range > 1e-12)
            {
                hist = new long[HistogramBins]; double inv = HistogramBins / range;
                for (int i = 0; i < total && i < arr.Length; i++)
                {
                    if (!Included(i)) continue;
                    double v = arr[i]; if (double.IsNaN(v)) continue;
                    double d = v - mean; sse += d * d;
                    int b = (int)((v - mn) * inv); if (b >= HistogramBins) b = HistogramBins - 1; if (b < 0) b = 0;
                    hist[b]++;
                }
            }
            rep.Attributes.Add(new AttributeStats
            {
                Name = col.Name, Unit = col.Unit, DataType = col.DataType, DefaultValue = col.DefaultValue,
                NonDefaultCount = nonDef, Min = mn, Max = mx, Mean = mean, Std = Math.Sqrt(sse / n), Histogram = hist,
            });
        }

        // 分标高（按块尺寸 Z 分层，用 ResourceByElevation 的分带口径：cutoff=-∞ 即全部块计体积）
        var live = new List<BlockModel.Block>();
        for (int i = 0; i < total; i++) if (Included(i)) live.Add(m.Blocks[i]);
        if (live.Count > 0)
        {
            // 分带口径与 ResourceByElevation 一致：minZ 起按块高 Sz 分带, idx=(z-minZ)/Sz, 只列非空带
            var bench = BlockModel.ResourceByElevation(live, double.NegativeInfinity, 1.0, m.Sz);
            double minZ = live.Min(b => b.Z), bh = m.Sz > 1e-9 ? m.Sz : 1.0;
            var cells = new Dictionary<int, (long n, double vol)>();
            foreach (var blk in live)
            {
                int idx = Math.Max(0, (int)((blk.Z - minZ) / bh));
                double k = m.CellScale(blk); double v = k * k * k * m.CellVolume;
                cells[idx] = cells.TryGetValue(idx, out var c) ? (c.n + 1, c.vol + v) : (1, v);
            }
            foreach (var b in bench)
            {
                int idx = (int)Math.Round((b.ZLow - minZ) / bh);
                cells.TryGetValue(idx, out var c);
                rep.Levels.Add(new LevelRow { ZLow = b.ZLow, ZHigh = b.ZHigh, Cells = c.n, Volume = c.vol });
            }
        }
        return rep;
    }

    private static string Pct(long part, long total) => total > 0 ? $"{(double)part / total * 100:0.##}%" : "—";
    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>预览面板的纯文本摘要（原 BuildPlainTextSummary）。</summary>
    public static string RenderPlainText(Report r)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        sb.AppendLine($"块体模型报告: {r.ModelName}");
        sb.AppendLine($"生成时间: {r.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrEmpty(r.Description)) sb.AppendLine($"描述: {r.Description}");
        sb.AppendLine($"统计范围: {r.ScopeLabel}");
        sb.AppendLine();
        sb.AppendLine("══════ 汇总 ══════");
        if (r.IsScoped)
        {
            sb.AppendLine($"  范围内块数:   {r.ScopedCells:N0}  ({Pct(r.ScopedCells, r.TotalCells)} of 总块)");
            sb.AppendLine($"  范围内体积:   {r.ScopedVolume:N0} m³");
        }
        sb.AppendLine($"  总块数:       {r.TotalCells:N0}");
        sb.AppendLine($"  可见块数:     {r.VisibleCells:N0}  ({Pct(r.VisibleCells, r.TotalCells)})");
        sb.AppendLine($"  已删块数:     {r.DeletedCells:N0}  ({Pct(r.DeletedCells, r.TotalCells)})");
        sb.AppendLine($"  存储模式:     {r.StorageMode.ToChineseLabel()}");
        sb.AppendLine($"  网格数:       {r.Nx} × {r.Ny} × {r.Nz}");
        sb.AppendLine($"  块尺寸:       {r.Sx:0.##} × {r.Sy:0.##} × {r.Sz:0.##} m");
        sb.AppendLine($"  AABB:         {r.BoundsText}");
        sb.AppendLine($"  单 cell 体积: {r.SingleCellVolume:N3} m³");
        sb.AppendLine($"  可见总体积:   {r.VisibleVolume:N0} m³");
        if (!string.IsNullOrEmpty(r.ActiveColormapAttribute)) sb.AppendLine($"  着色驱动属性: {r.ActiveColormapAttribute}");
        sb.AppendLine();
        sb.AppendLine("══════ 属性统计 ══════");
        if (r.Attributes.Count == 0) sb.AppendLine("  (模型无属性列)");
        else
        {
            sb.AppendLine($"  {"属性",-20} {"单位",-8} {"非默认",10} {"Min",12} {"Max",12} {"Mean",12} {"Std",12}");
            sb.AppendLine($"  {new string('-', 88)}");
            foreach (var a in r.Attributes)
            {
                string F(double v) => double.IsNaN(v) ? "—" : v.ToString("0.####", ci);
                sb.AppendLine($"  {Trunc(a.Name, 20),-20} {Trunc(a.Unit, 8),-8} {a.NonDefaultCount,10:N0} {F(a.Min),12} {F(a.Max),12} {F(a.Mean),12} {F(a.Std),12}");
            }
        }
        if (r.Levels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("══════ 分标高统计 ══════");
            sb.AppendLine($"  {"标高区间 (m)",-24} {"块数",10} {"体积 (m³)",16}");
            foreach (var l in r.Levels)
                sb.AppendLine($"  {l.ZLow.ToString("0.###", ci) + " ~ " + l.ZHigh.ToString("0.###", ci),-24} {l.Cells,10:N0} {l.Volume,16:N1}");
        }
        return sb.ToString();
    }

    public static string RenderCsv(Report r)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(4096);
        sb.AppendLine("section,key,value");
        void Csv(string sec, string k, string v) => sb.Append(EscCsv(sec)).Append(',').Append(EscCsv(k)).Append(',').AppendLine(EscCsv(v));
        Csv("summary", "model_name", r.ModelName);
        Csv("summary", "description", r.Description);
        Csv("summary", "generated_at", r.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Csv("summary", "scope", r.ScopeLabel);
        if (r.IsScoped) { Csv("summary", "scoped_cells", r.ScopedCells.ToString(ci)); Csv("summary", "scoped_volume_m3", r.ScopedVolume.ToString("0.######", ci)); }
        Csv("summary", "total_cells", r.TotalCells.ToString(ci));
        Csv("summary", "visible_cells", r.VisibleCells.ToString(ci));
        Csv("summary", "deleted_cells", r.DeletedCells.ToString(ci));
        Csv("summary", "storage_mode", r.StorageMode.ToString());
        Csv("summary", "nx", r.Nx.ToString(ci)); Csv("summary", "ny", r.Ny.ToString(ci)); Csv("summary", "nz", r.Nz.ToString(ci));
        Csv("summary", "block_size_x", r.Sx.ToString(ci)); Csv("summary", "block_size_y", r.Sy.ToString(ci)); Csv("summary", "block_size_z", r.Sz.ToString(ci));
        Csv("summary", "aabb_min_x", r.Bounds.minX.ToString(ci)); Csv("summary", "aabb_min_y", r.Bounds.minY.ToString(ci)); Csv("summary", "aabb_min_z", r.Bounds.minZ.ToString(ci));
        Csv("summary", "aabb_max_x", r.Bounds.maxX.ToString(ci)); Csv("summary", "aabb_max_y", r.Bounds.maxY.ToString(ci)); Csv("summary", "aabb_max_z", r.Bounds.maxZ.ToString(ci));
        Csv("summary", "single_cell_volume_m3", r.SingleCellVolume.ToString("0.######", ci));
        Csv("summary", "visible_total_volume_m3", r.VisibleVolume.ToString("0.######", ci));
        Csv("summary", "active_colormap_attribute", r.ActiveColormapAttribute);
        sb.AppendLine();
        sb.AppendLine("attribute,unit,data_type,non_default_count,min,max,mean,std,default_value");
        foreach (var a in r.Attributes)
        {
            string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.######", ci);
            sb.Append(EscCsv(a.Name)).Append(',').Append(EscCsv(a.Unit)).Append(',').Append(a.DataType).Append(',')
              .Append(a.NonDefaultCount.ToString(ci)).Append(',').Append(F(a.Min)).Append(',').Append(F(a.Max)).Append(',')
              .Append(F(a.Mean)).Append(',').Append(F(a.Std)).Append(',').AppendLine(a.DefaultValue.ToString("0.######", ci));
        }
        if (r.Levels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("z_low,z_high,cells,volume_m3");
            foreach (var l in r.Levels)
                sb.Append(l.ZLow.ToString("0.######", ci)).Append(',').Append(l.ZHigh.ToString("0.######", ci)).Append(',').Append(l.Cells.ToString(ci)).Append(',').AppendLine(l.Volume.ToString("0.######", ci));
        }
        return sb.ToString();
    }

    public static string RenderHtml(Report r)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-cn\"><head><meta charset=\"UTF-8\">");
        sb.Append("<title>块体模型报告 - ").Append(Esc(r.ModelName)).AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:-apple-system,'Segoe UI','Microsoft YaHei',sans-serif;margin:32px;color:#1f2937;}");
        sb.AppendLine("h1{color:#1e3a5f;border-bottom:2px solid #0d6efd;padding-bottom:8px;} h2{color:#1e3a5f;margin-top:28px;}");
        sb.AppendLine(".meta{color:#6c757d;font-size:12px;margin-bottom:16px;}");
        sb.AppendLine(".summary-grid{display:grid;grid-template-columns:repeat(2,1fr);gap:8px 24px;background:#f8f9fa;padding:16px;border-radius:6px;margin:16px 0;}");
        sb.AppendLine(".summary-grid .k{color:#6c757d;} .summary-grid .v{font-weight:600;color:#1e3a5f;}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;margin-top:8px;font-size:13px;} th,td{padding:8px 12px;text-align:left;border-bottom:1px solid #dee2e6;}");
        sb.AppendLine("th{background:#e9ecef;font-weight:600;color:#495057;} .num{text-align:right;font-family:'Consolas',monospace;} tr:hover{background:#f8f9fa;}");
        sb.AppendLine(".hist{display:inline-block;vertical-align:bottom;background:#0d6efd;width:14px;margin-right:2px;}");
        sb.AppendLine("</style></head><body>");
        sb.Append("<h1>块体模型报告: ").Append(Esc(r.ModelName)).AppendLine("</h1>");
        sb.Append("<div class=\"meta\">生成时间: ").Append(r.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss")).Append("　统计范围: ").Append(Esc(r.ScopeLabel)).AppendLine("</div>");
        if (!string.IsNullOrEmpty(r.Description)) sb.Append("<p>").Append(Esc(r.Description)).AppendLine("</p>");
        sb.AppendLine("<h2>汇总</h2><div class=\"summary-grid\">");
        void Row(string k, string v) => sb.Append("<span class=\"k\">").Append(Esc(k)).Append("</span><span class=\"v\">").Append(Esc(v)).AppendLine("</span>");
        if (r.IsScoped) { Row("范围内块数", $"{r.ScopedCells:N0}"); Row("范围内体积", $"{r.ScopedVolume:N0} m³"); }
        Row("总块数", $"{r.TotalCells:N0}"); Row("可见块数", $"{r.VisibleCells:N0} ({Pct(r.VisibleCells, r.TotalCells)})");
        Row("已删块数", $"{r.DeletedCells:N0} ({Pct(r.DeletedCells, r.TotalCells)})"); Row("存储模式", r.StorageMode.ToChineseLabel());
        Row("网格数", $"{r.Nx} × {r.Ny} × {r.Nz}"); Row("块尺寸", $"{r.Sx:0.##} × {r.Sy:0.##} × {r.Sz:0.##} m");
        Row("AABB", r.BoundsText); Row("单 cell 体积", $"{r.SingleCellVolume:N3} m³"); Row("可见总体积", $"{r.VisibleVolume:N0} m³");
        if (!string.IsNullOrEmpty(r.ActiveColormapAttribute)) Row("着色驱动属性", r.ActiveColormapAttribute);
        sb.AppendLine("</div>");
        sb.AppendLine("<h2>属性统计</h2>");
        if (r.Attributes.Count == 0) sb.AppendLine("<p style=\"color:#6c757d\">模型无属性列。</p>");
        else
        {
            sb.AppendLine("<table><thead><tr><th>属性</th><th>单位</th><th class=\"num\">非默认</th><th class=\"num\">Min</th><th class=\"num\">Max</th><th class=\"num\">Mean</th><th class=\"num\">Std</th><th>直方图</th></tr></thead><tbody>");
            foreach (var a in r.Attributes)
            {
                string F(double v) => double.IsNaN(v) ? "—" : v.ToString("0.####", ci);
                sb.Append("<tr><td>").Append(Esc(a.Name)).Append("</td><td>").Append(Esc(a.Unit)).Append("</td><td class=\"num\">").Append(a.NonDefaultCount.ToString("N0", ci))
                  .Append("</td><td class=\"num\">").Append(F(a.Min)).Append("</td><td class=\"num\">").Append(F(a.Max)).Append("</td><td class=\"num\">").Append(F(a.Mean))
                  .Append("</td><td class=\"num\">").Append(F(a.Std)).Append("</td><td>");
                if (a.Histogram != null)
                {
                    long mx = 1; foreach (var h in a.Histogram) if (h > mx) mx = h;
                    foreach (var h in a.Histogram) sb.Append("<span class=\"hist\" style=\"height:").Append((int)Math.Round(40.0 * h / mx)).Append("px\"></span>");
                }
                sb.AppendLine("</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        if (r.Levels.Count > 0)
        {
            sb.AppendLine("<h2>分标高统计</h2><table><thead><tr><th>标高区间 (m)</th><th class=\"num\">块数</th><th class=\"num\">体积 (m³)</th></tr></thead><tbody>");
            foreach (var l in r.Levels)
                sb.Append("<tr><td>").Append(l.ZLow.ToString("0.###", ci)).Append(" ~ ").Append(l.ZHigh.ToString("0.###", ci)).Append("</td><td class=\"num\">").Append(l.Cells.ToString("N0", ci)).Append("</td><td class=\"num\">").Append(l.Volume.ToString("N1", ci)).AppendLine("</td></tr>");
            sb.AppendLine("</tbody></table>");
        }
        sb.AppendLine("<div style=\"margin-top:32px;color:#6c757d;font-size:11px\">PitMine3D BlockModelLib · 块体模型报告</div></body></html>");
        return sb.ToString();
    }

    public static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    public static string EscCsv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
