using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 现状台阶参数提取(件二·提取) —— 忠实移植原 PlanLib.ShortTerm.ParameterExtractor。
/// 从坡顶/坡底台阶线几何反推现状台阶参数(台阶高 H、坡面角 α、平盘宽 W、整体帮坡角 β、采深/堆高、台阶数 + 均匀性)。
///
/// 与 Kylin 既有 <c>BenchAnalyzer</c> 区别: 后者吃剖面(里程,高程)一维断面; 本类吃平面台阶线(世界坐标),
/// 走「逐顶点最近邻」而非整条线配对——真实提取的坡顶/坡底线常碎裂、长短不一, 单条配对会把坡面投影算大、α 偏小。
///   坡面 = 每个坡顶顶点找下方(Δz∈[min,max])最近坡底顶点 → (H,run,α);
///   平盘 = 每个坡底顶点找≈同标高(±0.6·中位H)最近坡顶顶点 → W。median 聚合, IQR 报离散。
/// 纯 C# 几何, 无内核依赖。O(Nc·Nt) 暴力最近邻(区域级点数足够)。
/// Kylin 场景 2D 无逐点 Z, 故命令侧由 CSV(role,lineId,x,y,z)喂料; 算法与原版逐字一致。
/// </summary>
public sealed class BenchParameterExtractor
{
    /// <summary>一条台阶线(坡顶 crest / 坡底 toe), 扁平世界坐标 + 派生量。</summary>
    public sealed class Line
    {
        public bool IsCrest;
        public double[] Xyz = Array.Empty<double>();   // [x0,y0,z0,...]
        public double RepZ;                            // 代表标高(顶点 Z 均值)
        public double Cx, Cy;                          // XY 质心

        public static Line From(double[] xyz, bool isCrest)
        {
            var l = new Line { IsCrest = isCrest, Xyz = xyz ?? Array.Empty<double>() };
            int n = l.Xyz.Length / 3;
            if (n > 0)
            {
                double sx = 0, sy = 0, sz = 0;
                for (int i = 0; i < n; i++) { sx += l.Xyz[i * 3]; sy += l.Xyz[i * 3 + 1]; sz += l.Xyz[i * 3 + 2]; }
                l.Cx = sx / n; l.Cy = sy / n; l.RepZ = sz / n;
            }
            return l;
        }
    }

    /// <summary>单坡面采样(一个坡顶顶点对其下方最近坡底顶点)。</summary>
    public sealed class BenchMeasure
    {
        public double CrestZ, ToeZ;
        public double H;             // 台阶高(Δz)
        public double FaceRunM;      // 坡面水平投影
        public double FaceAngleDeg;  // α
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public int BenchCount;                       // 台阶级数(坡顶标高聚类)
        public double BenchHeight;                   // 中位 H
        public double FaceAngleDeg;                  // 中位 α
        public double BermWidth;                     // 中位 W
        public double OverallSlopeAngleDeg;          // β: 由中位 H/α/W 推导(与设计同式)
        public double OverallSlopeAngleMeasuredDeg;  // β: 直接量(最高坡顶 → 最低坡底)
        public double DepthM;                        // 采深/堆高 = Zmax − Zmin
        public double HeightSpreadM;                 // H 离散(IQR)
        public double BermSpreadM;                   // W 离散(IQR)
        public List<BenchMeasure> Benches = new();   // 逐坡面采样(画分布用)
        public List<string> Warnings = new();
    }

    /// <summary>
    /// 提取。lines = 坡顶/坡底台阶线集(各带 IsCrest 标); pairMin/MaxDrop = 坡顶顶点向下找坡底的 Δz 窗口;
    /// uniformityTolFrac = IQR/中位 超此值即报"不齐"。任何输入异常都降级, 绝不抛。
    /// </summary>
    public static Result Extract(IReadOnlyList<Line> lines,
        double pairMinDrop = 2.5, double pairMaxDrop = 60.0, double uniformityTolFrac = 0.25)
    {
        var res = new Result();
        if (lines == null || lines.Count == 0) { res.Message = "未提供台阶线。"; return res; }

        var crests = lines.Where(l => l.IsCrest && l.Xyz.Length >= 6).ToList();
        var toes = lines.Where(l => !l.IsCrest && l.Xyz.Length >= 6).ToList();
        if (crests.Count == 0 || toes.Count == 0)
        { res.Message = "需要同时提供坡顶线和坡底线。"; return res; }

        Flatten(crests, out var cx, out var cy, out var cz);
        Flatten(toes, out var tx, out var ty, out var tz);

        // ── 坡面: 每个坡顶顶点 → 下方(Δz∈[min,max])最近坡底顶点 → (H, run, α) ──
        var hs = new List<double>(cx.Length);
        var angs = new List<double>(cx.Length);
        for (int i = 0; i < cx.Length; i++)
        {
            double bestGap2 = double.MaxValue, bestDz = 0;
            for (int j = 0; j < tx.Length; j++)
            {
                double dz = cz[i] - tz[j];
                if (dz < pairMinDrop || dz > pairMaxDrop) continue;
                double dx = cx[i] - tx[j], dy = cy[i] - ty[j];
                double g2 = dx * dx + dy * dy;
                if (g2 < bestGap2) { bestGap2 = g2; bestDz = dz; }
            }
            if (bestGap2 < double.MaxValue && bestGap2 > 1e-12)
            {
                double run = Math.Sqrt(bestGap2);
                double ang = Math.Atan(bestDz / run) * 180.0 / Math.PI;
                hs.Add(bestDz); angs.Add(ang);
                res.Benches.Add(new BenchMeasure { CrestZ = cz[i], ToeZ = cz[i] - bestDz, H = bestDz, FaceRunM = run, FaceAngleDeg = ang });
            }
        }
        if (hs.Count == 0)
        { res.Message = "坡顶顶点找不到下方相邻坡底(检查 Δz 范围/是否同坐标系)。"; return res; }

        double medH = Median(hs);

        // ── 平盘宽: 每个坡底顶点 → ≈同标高(±0.6·中位H)最近坡顶顶点(下一级坡顶坐落本级平盘) ──
        double band = Math.Max(1.0, 0.6 * medH);
        var ws = new List<double>(tx.Length);
        for (int j = 0; j < tx.Length; j++)
        {
            double bestGap2 = double.MaxValue;
            for (int i = 0; i < cx.Length; i++)
            {
                if (Math.Abs(cz[i] - tz[j]) > band) continue;   // 同平盘标高(排除本级自身坡顶, 其高 ~H)
                double dx = tx[j] - cx[i], dy = ty[j] - cy[i];
                double g2 = dx * dx + dy * dy;
                // 重合点守卫: 调用方若把同一条线同时当坡顶/坡底喂进来, 每个坡底顶点会匹配到坐标全同的自己,
                // 距离 0 ⇒ 平盘宽中位恒 0 ⇒ β≡α ⇒ 下游全错且不报错。兜底跳过重合。
                if (g2 <= 1e-12) continue;
                if (g2 < bestGap2) bestGap2 = g2;
            }
            if (bestGap2 < double.MaxValue) ws.Add(Math.Sqrt(bestGap2));
        }

        res.Ok = true;
        res.BenchCount = CountLevels(cz, Math.Max(1.0, 0.5 * medH));
        res.BenchHeight = medH;
        res.FaceAngleDeg = Median(angs);
        res.BermWidth = ws.Count > 0 ? Median(ws) : 0;
        res.OverallSlopeAngleDeg = OverallSlopeAngleDeg(res.BenchHeight, res.FaceAngleDeg, res.BermWidth);

        // 直接量整体帮坡角: 最高坡顶 → 最低坡底
        int iTop = ArgMax(cz); int jBot = ArgMin(tz);
        double totH = cz[iTop] - tz[jBot];
        double totRun = Math.Sqrt((cx[iTop] - tx[jBot]) * (cx[iTop] - tx[jBot]) + (cy[iTop] - ty[jBot]) * (cy[iTop] - ty[jBot]));
        res.OverallSlopeAngleMeasuredDeg = totRun > 1e-6 ? Math.Atan(totH / totRun) * 180.0 / Math.PI : 90.0;

        double zmin = Math.Min(cz.Min(), tz.Min()), zmax = Math.Max(cz.Max(), tz.Max());
        res.DepthM = zmax - zmin;
        res.HeightSpreadM = Iqr(hs);
        res.BermSpreadM = ws.Count > 0 ? Iqr(ws) : 0;

        if (res.BenchCount < 2) res.Warnings.Add("仅识别到 1 级台阶, 平盘宽不可测。");
        if (res.BenchHeight > 1e-6 && res.HeightSpreadM / res.BenchHeight > uniformityTolFrac)
            res.Warnings.Add($"台阶高不齐: 四分位距 {res.HeightSpreadM:0.#}m(中位 {res.BenchHeight:0.#}m)。");
        if (res.BermWidth > 1e-6 && res.BermSpreadM / res.BermWidth > uniformityTolFrac)
            res.Warnings.Add($"平盘宽忽宽忽窄: 四分位距 {res.BermSpreadM:0.#}m(中位 {res.BermWidth:0.#}m)。");

        res.Message = $"提取到 {res.BenchCount} 级台阶: H≈{res.BenchHeight:0.#}m · α≈{res.FaceAngleDeg:0.#}° · "
                    + $"W≈{res.BermWidth:0.#}m · β≈{res.OverallSlopeAngleDeg:0.#}° · 采深/堆高≈{res.DepthM:0.#}m。";
        return res;
    }

    /// <summary>清单报表(CSV)。</summary>
    public static string BuildReport(Result r, string sourceNote = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("现状台阶参数提取");
        if (!string.IsNullOrWhiteSpace(sourceNote)) sb.AppendLine("取线范围," + sourceNote.Replace(',', '，'));
        sb.AppendLine($"台阶级数,{r.BenchCount}");
        sb.AppendLine($"台阶高中位(m),{r.BenchHeight:0.##}");
        sb.AppendLine($"坡面角中位(°),{r.FaceAngleDeg:0.##}");
        sb.AppendLine($"平盘宽中位(m),{r.BermWidth:0.##}");
        sb.AppendLine($"整体帮坡角-推导(°),{r.OverallSlopeAngleDeg:0.##}");
        sb.AppendLine($"整体帮坡角-实量(°),{r.OverallSlopeAngleMeasuredDeg:0.##}");
        sb.AppendLine($"采深堆高(m),{r.DepthM:0.##}");
        sb.AppendLine($"台阶高四分位距(m),{r.HeightSpreadM:0.##}");
        sb.AppendLine($"平盘宽四分位距(m),{r.BermSpreadM:0.##}");
        sb.AppendLine();
        sb.AppendLine("坡面采样,坡顶标高(m),坡底标高(m),台阶高(m),坡面投影(m),坡面角(°)");
        int k = 1;
        foreach (var b in r.Benches)
            sb.AppendLine($"{k++},{b.CrestZ:0.##},{b.ToeZ:0.##},{b.H:0.##},{b.FaceRunM:0.##},{b.FaceAngleDeg:0.##}");
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("提示");
            foreach (var w in r.Warnings) sb.AppendLine(w.Replace(',', '，'));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 从 CSV 文本解析台阶线(Kylin 命令侧喂料)。每行 <c>role,lineId,x,y,z</c>:
    /// role ∈ {C/crest/坡顶/1 = 坡顶, T/toe/坡底/0 = 坡底}; 同 (role,lineId) 的行按序连成一条线。
    /// 表头/空行/#注释跳过。
    /// </summary>
    public static List<Line> ParseCsv(string csv)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, (List<double> xyz, bool crest)>();
        foreach (var raw in (csv ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var t = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5) continue;
            if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
            if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
            if (!double.TryParse(t[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;
            string role = t[0].Trim().ToLowerInvariant();
            bool crest = role is "c" or "crest" or "坡顶" or "顶" or "1" or "true";
            string key = (crest ? "C:" : "T:") + t[1];
            if (!byKey.TryGetValue(key, out var acc)) { acc = (new List<double>(), crest); byKey[key] = acc; order.Add(key); }
            acc.xyz.Add(x); acc.xyz.Add(y); acc.xyz.Add(z);
        }
        var result = new List<Line>(order.Count);
        foreach (var key in order)
        {
            var (xyz, crest) = byKey[key];
            result.Add(Line.From(xyz.ToArray(), crest));
        }
        return result;
    }

    // ── 辅助 ──────────────────────────────────────────────

    /// <summary>最终(组合)帮坡角: β = atan( H / (H/tanα + W) )。与级数无关(匀质近似)。忠实原 BenchTemplateResolver。</summary>
    public static double OverallSlopeAngleDeg(double benchHeight, double faceAngleDeg, double bermWidth)
    {
        if (benchHeight <= 0) return 0;
        double a = faceAngleDeg * Math.PI / 180.0;
        double tan = Math.Tan(a);
        if (tan <= 1e-9) return 0;
        double run = benchHeight / tan + Math.Max(0.0, bermWidth);
        if (run <= 1e-9) return 90.0;
        return Math.Atan(benchHeight / run) * 180.0 / Math.PI;
    }

    /// <summary>帮坡角反算平盘宽: 给 H/α 与目标整体帮坡角 β, 求 W = H/tanβ − H/tanα(钳 ≥0)。<see cref="OverallSlopeAngleDeg"/> 的逆。忠实原 BenchTemplateResolver.SolveBermForOverallAngle。</summary>
    public static double SolveBermForOverallAngle(double benchHeight, double faceAngleDeg, double targetOverallAngleDeg)
    {
        if (benchHeight <= 0) return 0;
        double tanf = Math.Tan(faceAngleDeg * Math.PI / 180.0);
        double tanb = Math.Tan(targetOverallAngleDeg * Math.PI / 180.0);
        if (tanf <= 1e-9 || tanb <= 1e-9) return 0;
        return Math.Max(0.0, benchHeight / tanb - benchHeight / tanf);
    }

    private static void Flatten(List<Line> set, out double[] xs, out double[] ys, out double[] zs)
    {
        int n = set.Sum(l => l.Xyz.Length / 3);
        xs = new double[n]; ys = new double[n]; zs = new double[n];
        int k = 0;
        foreach (var l in set)
        {
            int m = l.Xyz.Length / 3;
            for (int i = 0; i < m; i++) { xs[k] = l.Xyz[i * 3]; ys[k] = l.Xyz[i * 3 + 1]; zs[k] = l.Xyz[i * 3 + 2]; k++; }
        }
    }

    /// <summary>标高聚类成台阶级: 升序后, 相邻差 ≥ minGap 即新一级。</summary>
    private static int CountLevels(double[] zs, double minGap)
    {
        if (zs.Length == 0) return 0;
        var s = (double[])zs.Clone();
        Array.Sort(s);
        int levels = 1;
        double anchor = s[0];
        for (int i = 1; i < s.Length; i++)
            if (s[i] - anchor >= minGap) { levels++; anchor = s[i]; }
        return levels;
    }

    private static int ArgMax(double[] a) { int b = 0; for (int i = 1; i < a.Length; i++) if (a[i] > a[b]) b = i; return b; }
    private static int ArgMin(double[] a) { int b = 0; for (int i = 1; i < a.Length; i++) if (a[i] < a[b]) b = i; return b; }

    private static double Median(List<double> xs) => Percentile(xs, 50);
    private static double Iqr(List<double> xs) => Percentile(xs, 75) - Percentile(xs, 25);

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
}
