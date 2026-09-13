using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 标注台阶标高 —— 忠实移植原 PlanLib.ShortTerm.BenchElevationAnnotator 的**放置算法**。
/// 把一批台阶线(坡顶/坡底等接近等高的多段线)逐条定出一处「▽ 标高符号 + 高程数字」的放置点。
///
/// 放置核(与原版逐字一致): ① 每条线代表点 = 最接近 XY 质心的顶点(符号坐落线上而非悬空质心),
/// 代表高程 = 顶点 Z 均值取整; ② 平盘居中 = 代表点与「最近同高程·异线」边点取中点(坡顶线↔坡底线
/// 夹出的平盘两沿同高程, 中点落平盘内部); ③ 去重 = 平盘点落同一网格 + 同整米高程只留一处
/// (坡顶/坡底各算一次会并到同一平盘中心); ④ 按作业区域类别配色。
///
/// 与原版差异(2D 场景适配, 记录): 原产出 PMBI 三维实体载荷(含绕 X/Y/Z 倾斜的立式朝向框、字体样式、
/// 引线), 推入内核生成 AcDb 实体; Kylin 场景为 2D XY, 故本类只出**放置点列**(Marker), 由命令层
/// 画成 2D 场景实体(▽ 闭合多段线 + 顶边引线 + 文字), 不做三维倾斜/朝向。放置算法完全保真, 可单测。
/// </summary>
public sealed class BenchElevationAnnotator
{
    public const string Layer = "台阶标高标注";
    /// <summary>「查询台阶平盘标高」的标记图层(点 + 高程, 可单独清除)。</summary>
    public const string QueryLayer = "台阶标高查询";
    /// <summary>可选字体(显示名, 别名); 别名空 = 引擎默认(宋体)。原版清单照抄。</summary>
    public static readonly (string Display, string Alias)[] Fonts =
    {
        ("默认(宋体)", ""), ("宋体 SimSun", "SimSun"), ("黑体 SimHei", "SimHei"), ("仿宋 FangSong", "FangSong"), ("楷体 KaiTi", "KaiTi"), ("Arial", "Arial"),
    };

    /// <summary>一条台阶线(接近等高的多段线) + 其作业区域类别(配色/过滤)。</summary>
    public sealed class BenchLine
    {
        public double[] Xyz = Array.Empty<double>();   // 扁平世界坐标 [x0,y0,z0,...]
        public string Category = "";                   // pit / external_dump / internal_dump / ""(未知)
    }

    public sealed class Options
    {
        /// <summary>符号大小(三角形高度, 米); &lt;=0 = 按数据范围自动取。</summary>
        public double SymbolSize = 0;
        /// <summary>同高程标注最小间距 = 符号大小 × 此系数(防重叠: 网格去重)。</summary>
        public double DedupSpacingFactor = 3.0;
        /// <summary>把标注落到平盘中央(每点与最近同高程·异线边点取中点)。缺对向边则退回线上。</summary>
        public bool PlaceOnBenchCenter = true;
        /// <summary>统一固定颜色(0xRRGGBB); null = 按区域类别自动配色。</summary>
        public uint? FixedColorRgb = null;
        /// <summary>倾斜轴: 0=正立 / 1=绕 X / 2=绕 Y / 3=绕 Z(原版三维文字朝向; Kylin 平面文字只把 绕 Z 当作旋转角, 其余记录不变)。</summary>
        public int TiltAxis = 1;
        /// <summary>倾斜角(度, 带符号)。</summary>
        public double TiltDeg = 30;
        /// <summary>字体别名(见 <see cref="Fonts"/>); 空 = 默认。</summary>
        public string FontName = "";
    }

    /// <summary>一处标注的放置结果(命令层据此画 ▽ + 引线 + 文字)。</summary>
    public sealed class Marker
    {
        public double X, Y, Z;         // 平盘点(2D 场景用 X,Y; Z 保留供高程)
        public int Elevation;          // 整米高程
        public string Label = "";      // "+1180" / "-25" / "0"
        public string Category = "";
        public byte R, G, B;           // 配色
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public List<Marker> Markers = new();
        public int MarkerCount;
        public int CenteredCount;
        public double SymbolSize;
    }

    /// <summary>
    /// 把台阶线集合编成标注放置点列。每条线取最接近 XY 质心的顶点为代表点, 代表高程 = 顶点 Z 均值取整;
    /// 同高程且落同一去重网格只保留一处。任何输入异常都降级返回, 绝不抛。
    /// </summary>
    public static Result Build(IReadOnlyList<BenchLine> lines, Options? opt = null)
    {
        opt ??= new Options();
        var res = new Result();
        if (lines == null || lines.Count == 0) { res.Message = "没有可标注的台阶线。"; return res; }

        // ① 每条线 → 代表点 + 类别; 同时收集全部顶点用于平盘配对。
        var reps = new List<(double x, double y, double z, string cat, int li)>(lines.Count);
        var vx = new List<double>(); var vy = new List<double>(); var vz = new List<double>(); var vl = new List<int>();
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        for (int li = 0; li < lines.Count; li++)
        {
            var ln = lines[li];
            var p = ln?.Xyz;
            int n = p == null ? 0 : p.Length / 3;
            if (n < 1) continue;
            double sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < n; i++)
            {
                double x = p![i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                sx += x; sy += y; sz += z;
                vx.Add(x); vy.Add(y); vz.Add(z); vl.Add(li);
            }
            double cx = sx / n, cy = sy / n, mz = sz / n;
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double ex = p![i * 3] - cx, ey = p[i * 3 + 1] - cy, d = ex * ex + ey * ey;
                if (d < bestD) { bestD = d; best = i; }
            }
            double rx = p![best * 3], ry = p[best * 3 + 1];
            reps.Add((rx, ry, mz, ln!.Category ?? "", li));
            minx = Math.Min(minx, rx); maxx = Math.Max(maxx, rx);
            miny = Math.Min(miny, ry); maxy = Math.Max(maxy, ry);
        }
        if (reps.Count == 0) { res.Message = "台阶线顶点为空, 无法标注。"; return res; }

        // ② 符号大小: 用户给定优先, 否则按代表点范围对角线 0.6% 自动取(夹到合理区间)。
        double diag = Math.Sqrt((maxx - minx) * (maxx - minx) + (maxy - miny) * (maxy - miny));
        double size = opt.SymbolSize > 0 ? opt.SymbolSize : Math.Clamp(diag * 0.006, 1.0, 50.0);
        if (size <= 0 || double.IsNaN(size)) size = 5.0;
        res.SymbolSize = size;

        // ③ 平盘配对上限 + 高程分桶。
        double pairCap = Math.Clamp(diag * 0.05, 20.0, 150.0);
        var levels = BuildLevelBuckets(vz);

        // ④ 去重网格。
        double cell = Math.Max(size * Math.Max(opt.DedupSpacingFactor, 0.5), 1e-3);
        var seen = new HashSet<(long gx, long gy, int e)>();

        int placed = 0, centered = 0;
        foreach (var r in reps)
        {
            double px = r.x, py = r.y, pz = r.z;
            if (opt.PlaceOnBenchCenter &&
                NearestOppositeEdge(r.x, r.y, r.z, r.li, vx, vy, vz, vl, levels, pairCap,
                                    out double ox, out double oy, out double oz))
            { px = (r.x + ox) * 0.5; py = (r.y + oy) * 0.5; pz = (r.z + oz) * 0.5; centered++; }

            int e = (int)Math.Round(pz);
            long gx = (long)Math.Floor(px / cell), gy = (long)Math.Floor(py / cell);
            if (!seen.Add((gx, gy, e))) continue;   // 同格同高程: 跳过(坡顶/坡底并成一处)

            var (cr, cg, cb) = opt.FixedColorRgb is uint rgb
                ? ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : ColorOf(r.cat);

            string label = (e > 0 ? "+" : "") + e.ToString();   // 负数自带 '-', 零写 "0"
            res.Markers.Add(new Marker { X = px, Y = py, Z = pz, Elevation = e, Label = label, Category = r.cat, R = cr, G = cg, B = cb });
            placed++;
        }

        res.MarkerCount = placed;
        res.CenteredCount = centered;
        res.Ok = placed > 0;
        string centerNote = opt.PlaceOnBenchCenter
            ? (centered > 0 ? $", {centered} 处落到平盘中央" : ", 未配出平盘中心(缺对向边线)→标在线上")
            : "";
        res.Message = placed > 0
            ? $"已生成 {placed} 处标高标注(符号大小 {size:0.#}m{centerNote})。"
            : "没有生成标注(台阶线无有效顶点)。";
        return res;
    }

    /// <summary>
    /// 从 CSV 文本解析台阶线(命令侧喂料)。每行 <c>lineId,x,y,z[,category]</c>; 同 lineId 的行按序连成一条线;
    /// category ∈ {pit/external_dump/internal_dump/...}(可空)。表头/空行/#注释跳过。
    /// </summary>
    public static List<BenchLine> ParseCsv(string csv)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, (List<double> xyz, string cat)>();
        foreach (var raw in (csv ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var t = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            if (!double.TryParse(t[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
            if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;
            string id = t[0];
            string cat = t.Length > 4 ? t[4].Trim() : "";
            if (!byId.TryGetValue(id, out var acc)) { acc = (new List<double>(), cat); byId[id] = acc; order.Add(id); }
            acc.xyz.Add(x); acc.xyz.Add(y); acc.xyz.Add(z);
            if (acc.cat.Length == 0 && cat.Length > 0) byId[id] = (acc.xyz, cat);
        }
        var result = new List<BenchLine>(order.Count);
        foreach (var id in order) { var (xyz, cat) = byId[id]; result.Add(new BenchLine { Xyz = xyz.ToArray(), Category = cat }); }
        return result;
    }

    /// <summary>▽ 倒三角形符号的 3 个顶点(2D): 尖点落在 (bx,by), 向上(+Y)张开。忠实原符号几何(平面化)。</summary>
    public static (double x, double y)[] TriangleXY(double bx, double by, double size)
    {
        double triH = size * 0.85, triHalf = size * 0.45;
        return new[]
        {
            (bx, by),                       // 尖点
            (bx - triHalf, by + triH),      // 左上
            (bx + triHalf, by + triH),      // 右上
        };
    }

    /// <summary>顶边引线端点(2D): 从三角形右上沿 +X 拉一段托住数字。</summary>
    public static (double x0, double y0, double x1, double y1) LeaderXY(double bx, double by, double size, int labelLen)
    {
        double triH = size * 0.85, triHalf = size * 0.45, textH = size, gw = 0.6 * textH, gap = 0.3 * textH;
        double textU0 = triHalf + size * 0.15;
        double leadEndU = textU0 + labelLen * (gw + gap) + size * 0.1;
        double u0 = triHalf, u1 = Math.Max(leadEndU, triHalf + size);
        return (bx + u0, by + triH, bx + u1, by + triH);
    }

    /// <summary>文字锚点(2D, 左/底对齐)。</summary>
    public static (double x, double y) TextAnchorXY(double bx, double by, double size)
    {
        double triH = size * 0.85, triHalf = size * 0.45;
        return (bx + triHalf + size * 0.15, by + triH + size * 0.18);
    }

    // ── 辅助(忠实原版) ────────────────────────────────────
    private static Dictionary<int, List<int>> BuildLevelBuckets(List<double> vz)
    {
        var buckets = new Dictionary<int, List<int>>();
        for (int i = 0; i < vz.Count; i++)
        {
            int e = (int)Math.Round(vz[i]);
            if (!buckets.TryGetValue(e, out var lst)) { lst = new List<int>(); buckets[e] = lst; }
            lst.Add(i);
        }
        return buckets;
    }

    private static bool NearestOppositeEdge(double x, double y, double z, int li,
        List<double> vx, List<double> vy, List<double> vz, List<int> vl,
        Dictionary<int, List<int>> levels, double cap,
        out double ox, out double oy, out double oz)
    {
        ox = oy = oz = 0;
        int e0 = (int)Math.Round(z);
        double best = cap * cap; bool hit = false;
        for (int e = e0 - 1; e <= e0 + 1; e++)
        {
            if (!levels.TryGetValue(e, out var lst)) continue;
            foreach (int i in lst)
            {
                if (vl[i] == li) continue;   // 必须是另一条线
                double dx = vx[i] - x, dy = vy[i] - y, d = dx * dx + dy * dy;
                if (d < best) { best = d; ox = vx[i]; oy = vy[i]; oz = vz[i]; hit = true; }
            }
        }
        return hit;
    }

    /// <summary>「查询台阶平盘标高」单点标记(原 BuildQueryMarkerPmbi): 拾取点本身一个点 + 紧挨右上方的整数高程文字。单点无范围 → 符号大小缺省 10 m; 配色有固定色用固定色, 否则醒目青色(0x00C8FF)以区别正式标注。</summary>
    public static Marker BuildQueryMarker(double x, double y, double z, Options? opt = null)
    {
        opt ??= new Options();
        double size = opt.SymbolSize > 0 ? opt.SymbolSize : 10.0;
        int e = (int)Math.Round(z);
        string label = (e > 0 ? "+" : "") + e.ToString();
        (byte cr, byte cg, byte cb) = opt.FixedColorRgb is uint rgb ? ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : ((byte)0x00, (byte)0xC8, (byte)0xFF);
        return new Marker { X = x, Y = y, Z = z, Elevation = e, Label = label, Category = "", R = cr, G = cg, B = cb };
    }

    /// <summary>查询标记的文字锚点(点右上方 0.25·size / 0.15·size)。</summary>
    public static (double x, double y) QueryTextAnchorXY(double x, double y, double size) => (x + size * 0.25, y + size * 0.15);

    private static (byte r, byte g, byte b) ColorOf(string? cat) => cat switch
    {
        "pit" => (0xD8, 0x5A, 0x30),
        "external_dump" => (0x2E, 0x6F, 0xCF),
        "internal_dump" => (0x1D, 0x9E, 0x75),
        "pit_working_slope" => (0xF2, 0xB1, 0x4A),
        "dump_working_slope" => (0x6F, 0xB7, 0xF5),
        _ => (0xFF, 0xC4, 0x00),
    };
}
