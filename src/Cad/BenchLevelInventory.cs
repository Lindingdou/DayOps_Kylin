using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「平盘标高清单」纯算法 —— 忠实移植原 PitMine.Platform.Geometry.BenchLevelInventory。
/// 把一批台阶线(坡顶/坡底等近似等高的多段线)按标高归并成若干「平盘标高级」，
/// 数出设计里一共有多少个平盘标高，逐级给出标高/线条数/平面长度/级间距。
///
/// 为何按标高归并而非按线数: 同一级平盘的坡顶线与下一级坡底线标高相同(平盘就是这两条线夹出来的)，
/// 且一级平盘常被打断成多条线，按线数会成倍虚高。<see cref="Options.MergeTolM"/> 内的线视为同一级。
///
/// 纯托管、无副作用、可单测。取线/图层过滤/展示由调用方完成。
/// Kylin: 场景为 2D(实体无逐点 Z)，活图取线拿不到标高，故命令侧由 CSV(lineId,x,y,z)喂料; 算法本身与原版逐字一致。
/// </summary>
public sealed class BenchLevelInventory
{
    /// <summary>参与统计的一条线(世界坐标扁平 xyz + 来源信息)。</summary>
    public sealed class SourceLine
    {
        public ulong Handle;
        public string Layer = "";
        public double[] Xyz = Array.Empty<double>();   // [x0,y0,z0,...]
    }

    /// <summary>归并出的一级平盘标高。</summary>
    public sealed class Level
    {
        public int Index;                 // 1 = 最高一级，往下递增
        public double Elevation;          // 代表标高(本级各线代表标高的中位数)
        public int LineCount;
        public double TotalLengthM;       // 平面(XY)长度合计
        public double SpanM;              // 本级内代表标高极差(0 = 完全共面)
        public double DropToNextM;        // 与下一级(更低)的高差; 最低一级 = 0
        public readonly List<ulong> Handles = new();
        public readonly List<string> Layers = new();   // 参与图层(去重, 保持首次出现次序)
    }

    public sealed class Options
    {
        /// <summary>标高差 ≤ 此值的线归为同一级平盘(米)。设计线通常严格共面, 0.5 足够; 实测线可放大。</summary>
        public double MergeTolM = 0.5;

        /// <summary>单条线自身起伏 &gt; 此值即判为斜线(坡面线/道路/出入沟), 按 <see cref="SkipTilted"/> 处理。</summary>
        public double FlatTolM = 0.5;

        /// <summary>true = 斜线不参与统计(默认; 平盘标高只由水平线定)。</summary>
        public bool SkipTilted = true;

        /// <summary>平面长度小于此值的碎线丢弃(米); 0 = 不丢。</summary>
        public double MinLengthM = 0;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public List<Level> Levels = new();       // 高 → 低
        public int UsedLineCount;                // 实际参与统计的线数
        public int SkippedTilted;                // 起伏超限被剔的斜线
        public int SkippedShort;                 // 太短被剔的碎线
        public int SkippedInvalid;               // 顶点不足/读不到几何
        public double TopZ, BottomZ;             // 最高/最低一级代表标高
        public double MedianDropM;               // 级间距中位数(≈ 台阶高)
        public double DropSpreadM;               // 级间距四分位距(离散度)
        public List<string> Warnings = new();

        /// <summary>设计了多少个平盘标高 —— 本清单的答案。</summary>
        public int LevelCount => Levels.Count;

        /// <summary>顶底落差(最高级 − 最低级)。</summary>
        public double RangeM => Levels.Count > 0 ? TopZ - BottomZ : 0;
    }

    /// <summary>归并统计。任何输入异常都降级返回 Ok=false，绝不抛。</summary>
    public static Result Build(IReadOnlyList<SourceLine>? lines, Options? opt = null)
    {
        opt ??= new Options();
        var res = new Result();
        if (lines == null || lines.Count == 0) { res.Message = "没有可统计的线。"; return res; }

        double mergeTol = Math.Max(1e-3, opt.MergeTolM);

        // ── ① 逐线求「代表标高 + 起伏 + 平面长度」，剔掉斜线/碎线 ──
        var items = new List<(double Z, double Len, ulong Handle, string Layer)>(lines.Count);
        foreach (var l in lines)
        {
            var xyz = l.Xyz;
            int n = xyz == null ? 0 : xyz.Length / 3;
            if (n < 2) { res.SkippedInvalid++; continue; }

            double zmin = double.MaxValue, zmax = double.MinValue, len = 0;
            var zs = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double z = xyz![i * 3 + 2];
                zs.Add(z);
                if (z < zmin) zmin = z;
                if (z > zmax) zmax = z;
                if (i > 0)
                {
                    double dx = xyz[i * 3] - xyz[(i - 1) * 3];
                    double dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
                    len += Math.Sqrt(dx * dx + dy * dy);   // 平面长度: 平盘周长语义, 不含坡面爬升
                }
            }
            if (opt.SkipTilted && zmax - zmin > Math.Max(0, opt.FlatTolM)) { res.SkippedTilted++; continue; }
            if (opt.MinLengthM > 0 && len < opt.MinLengthM) { res.SkippedShort++; continue; }

            items.Add((Median(zs), len, l.Handle, l.Layer ?? ""));   // 中位数: 个别歪顶点不带偏代表标高
        }

        if (items.Count == 0)
        {
            res.Message = res.SkippedTilted > 0
                ? $"取到的 {lines.Count} 条线全被判为斜线(起伏 > {opt.FlatTolM:0.##}m)。若台阶线本身不严格水平, 请调大「视为水平的起伏上限」或取消「只统计水平线」。"
                : "取到的线都不可用(顶点不足/长度不够)。";
            return res;
        }
        res.UsedLineCount = items.Count;

        // ── ② 标高一维聚类: 升序扫描, 间隙 > 容差 即断新一级; 再限本级跨度 ≤ 2×容差 防「链式吞并」 ──
        items.Sort((a, b) => a.Z.CompareTo(b.Z));
        var clusters = new List<List<(double Z, double Len, ulong Handle, string Layer)>>();
        var cur = new List<(double Z, double Len, ulong Handle, string Layer)> { items[0] };
        double clusterMin = items[0].Z;
        for (int i = 1; i < items.Count; i++)
        {
            double gap = items[i].Z - items[i - 1].Z;
            if (gap > mergeTol || items[i].Z - clusterMin > 2 * mergeTol)
            {
                clusters.Add(cur);
                cur = new List<(double, double, ulong, string)>();
                clusterMin = items[i].Z;
            }
            cur.Add(items[i]);
        }
        clusters.Add(cur);

        // ── ③ 落成级(高 → 低) ──
        clusters.Reverse();
        int idx = 1;
        foreach (var c in clusters)
        {
            var lv = new Level
            {
                Index = idx++,
                Elevation = Median(c.Select(t => t.Z).ToList()),
                LineCount = c.Count,
                TotalLengthM = c.Sum(t => t.Len),
                SpanM = c.Max(t => t.Z) - c.Min(t => t.Z),
            };
            foreach (var t in c)
            {
                lv.Handles.Add(t.Handle);
                if (t.Layer.Length > 0 && !lv.Layers.Contains(t.Layer)) lv.Layers.Add(t.Layer);
            }
            res.Levels.Add(lv);
        }

        // ── ④ 级间距 + 离散度 ──
        var drops = new List<double>(res.Levels.Count);
        for (int i = 0; i < res.Levels.Count - 1; i++)
        {
            double d = res.Levels[i].Elevation - res.Levels[i + 1].Elevation;
            res.Levels[i].DropToNextM = d;
            drops.Add(d);
        }
        res.TopZ = res.Levels[0].Elevation;
        res.BottomZ = res.Levels[^1].Elevation;
        res.MedianDropM = drops.Count > 0 ? Median(drops) : 0;
        res.DropSpreadM = drops.Count > 0 ? Percentile(drops, 75) - Percentile(drops, 25) : 0;

        // ── ⑤ 体检提示 ──
        if (res.Levels.Count == 1)
            res.Warnings.Add("只归出 1 级标高: 取线范围里可能只有一条平盘, 或合并容差调得过大。");
        if (drops.Count > 0 && res.MedianDropM > 1e-6 && res.MedianDropM < 5.0 && res.Levels.Count >= 20)
            res.Warnings.Add($"级间距中位数仅 {res.MedianDropM:0.##}m 却有 {res.Levels.Count} 级 —— 多半把地形等高线也统计进来了。"
                           + "请改用「指定图层」只勾设计台阶线所在图层, 或先在视口选中设计线再统计。");
        if (res.MedianDropM > 1e-6 && res.DropSpreadM / res.MedianDropM > 0.25)
            res.Warnings.Add($"级间距不齐: 四分位距 {res.DropSpreadM:0.##}m(中位 {res.MedianDropM:0.##}m), 可能有并段台阶或漏掉某级。");
        foreach (var lv in res.Levels)
            if (lv.DropToNextM > 1e-6 && res.MedianDropM > 1e-6 && lv.DropToNextM > 1.8 * res.MedianDropM)
            {
                res.Warnings.Add($"第 {lv.Index} 级 {lv.Elevation:0.##}m 到下一级落差 {lv.DropToNextM:0.##}m, 约为常规台阶高的 "
                               + $"{lv.DropToNextM / res.MedianDropM:0.#} 倍 —— 这一段可能漏了平盘线。");
                break;   // 只报第一处, 避免刷屏
            }
        if (res.SkippedTilted > 0)
            res.Warnings.Add($"另有 {res.SkippedTilted} 条线起伏超过 {opt.FlatTolM:0.##}m 被当作坡面线/出入沟剔除(不定平盘标高)。");

        res.Ok = true;
        res.Message = $"共 {res.Levels.Count} 个平盘标高: {res.BottomZ:0.##} ~ {res.TopZ:0.##}m(落差 {res.RangeM:0.##}m)"
                    + (drops.Count > 0 ? $", 级间距中位 {res.MedianDropM:0.##}m" : "")
                    + $"; 用了 {res.UsedLineCount} 条线。";
        return res;
    }

    /// <summary>清单报表(CSV, UTF-8 写盘/剪贴板通用)。</summary>
    public static string BuildReport(Result r, string sourceNote = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("平盘标高清单");
        if (!string.IsNullOrWhiteSpace(sourceNote)) sb.AppendLine("取线范围," + sourceNote.Replace(',', '，'));
        sb.AppendLine($"平盘标高个数,{r.LevelCount}");
        if (r.LevelCount > 0)
        {
            sb.AppendLine($"最高级(m),{r.TopZ:0.##}");
            sb.AppendLine($"最低级(m),{r.BottomZ:0.##}");
            sb.AppendLine($"顶底落差(m),{r.RangeM:0.##}");
            sb.AppendLine($"级间距中位(m),{r.MedianDropM:0.##}");
            sb.AppendLine($"级间距四分位距(m),{r.DropSpreadM:0.##}");
        }
        sb.AppendLine($"参与统计线数,{r.UsedLineCount}");
        sb.AppendLine($"剔除-斜线,{r.SkippedTilted}");
        sb.AppendLine($"剔除-过短,{r.SkippedShort}");
        sb.AppendLine($"剔除-无效,{r.SkippedInvalid}");
        sb.AppendLine();
        sb.AppendLine("序号,标高(m),线条数,平面长度(m),本级起伏(m),到下一级(m),图层");
        foreach (var lv in r.Levels)
            sb.AppendLine($"{lv.Index},{lv.Elevation:0.##},{lv.LineCount},{lv.TotalLengthM:0.#},{lv.SpanM:0.##},"
                        + $"{(lv.DropToNextM > 0 ? lv.DropToNextM.ToString("0.##") : "")},"
                        + string.Join(" / ", lv.Layers).Replace(',', '，'));
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("提示");
            foreach (var w in r.Warnings) sb.AppendLine(w.Replace(',', '，'));
        }
        return sb.ToString();
    }

    // ── 辅助 ──────────────────────────────────────────────
    private static double Median(List<double> xs) => Percentile(xs, 50);

    /// <summary>线性插值分位数(p 为 0..100)。</summary>
    private static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(v => v).ToList();
        if (s.Count == 1) return s[0];
        double idx = p / 100.0 * (s.Count - 1);
        int lo = (int)Math.Floor(idx); int hi = (int)Math.Ceiling(idx);
        double f = idx - lo;
        return s[lo] * (1 - f) + s[hi] * f;
    }

    /// <summary>
    /// 从 CSV 文本解析台阶线(Kylin 命令侧喂料; 场景 2D 无逐点 Z, 故由 CSV 提供标高)。
    /// 每行 <c>lineId,x,y,z[,layer]</c>; 同 lineId 的行按出现次序连成一条线。表头/空行/#注释跳过。
    /// </summary>
    public static List<SourceLine> ParseCsv(string csv)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, (List<double> xyz, string layer)>();
        foreach (var raw in (csv ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var t = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            // 表头: 头三/四列非数字 → 跳过
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
            if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;
            string id = t[0];
            string layer = t.Length > 4 ? t[4] : "";
            if (!byId.TryGetValue(id, out var acc)) { acc = (new List<double>(), layer); byId[id] = acc; order.Add(id); }
            acc.xyz.Add(x); acc.xyz.Add(y); acc.xyz.Add(z);
            if (acc.layer.Length == 0 && layer.Length > 0) byId[id] = (acc.xyz, layer);
        }
        var result = new List<SourceLine>(order.Count);
        ulong h = 0;
        foreach (var id in order)
        {
            var (xyz, layer) = byId[id];
            result.Add(new SourceLine { Handle = h++, Layer = layer.Length > 0 ? layer : id, Xyz = xyz.ToArray() });
        }
        return result;
    }
}
