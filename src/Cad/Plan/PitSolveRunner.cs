using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>求解结果（含成败 + 回填用 PitResult）。</summary>
public sealed class PitSolveOutcome
{
    public bool Ok;
    public string Message = "";
    public PitResult? Result;
    public static PitSolveOutcome Fail(string m) => new() { Ok = false, Message = m };
}

/// <summary>
/// 境界求解编排（原 <c>PitSolveRunner</c>，三段式）：
///   段① 取激活块体 + 猜煤属性 + 算 n经 + 校核（不可行立刻 Ok=false，不抛）。
///   段② 后台线程——采样 → 深度求根 → 评价 → PitResult。
/// 段③ 落地（<see cref="PitMaterializer"/>）由"确定最终境界"另行触发。
/// </summary>
public static class PitSolveRunner
{
    public static async Task<PitSolveOutcome> SolveAsync(PitScheme s, IPlanEntityHost? host = null,
                                                         IProgress<string>? progress = null,
                                                         CancellationToken ct = default)
    {
        var model = host?.ActiveBlockModel;
        if (model == null || model.Blocks.Count == 0)
            return PitSolveOutcome.Fail("无激活块体模型（请先在「块体模型」导入/选择，且已赋煤/岩属性）");

        if (!BlockModelCoal.TryGuessCoal(model, out var attr, out var val, out var tol))
            return PitSolveOutcome.Fail("块体未找到煤/岩属性（列名含 coal/煤，或分类列含\"煤\"标签）");

        if (s.Econ.ComputeEconRatio() is not { } njh || njh <= 0)
            return PitSolveOutcome.Fail("经济合理剥采比无效（检查价/成本：剥离成本 b>0、售价>采矿成本）");

        double revenue = s.Econ.Price - s.Econ.MiningCost;  // (d−a) 元/t
        double stripCost = s.Econ.StripCost;                // b 元/m³
        double density = s.Econ.CoalDensity;

        // 几何圈定：地表界(或块体足迹兜底) + 最小底宽 + β → 几何允许最大深度（对求解封顶）
        double maxDepth = double.MaxValue; string envSrc = "";
        double[]? clipXY = null;
        double[][]? layerClips = null;
        var top = PitSchemeEnvelope.ResolveTopOutline(host, s.Geometry.SurfaceLimitHandle, model);
        if (top != null)
        {
            var edgeBeta = PitSchemeEnvelope.ResolveEdgeBetas(top, s);
            double dgeo = PitSchemeEnvelope.GeometricDepthCapPerWall(top, s.BottomWidthM, edgeBeta);
            if (dgeo > 0) { maxDepth = dgeo; envSrc = top.FromSurfaceLimit ? "地表界" : "块体足迹"; }

            var contra = PitSchemeEnvelope.ResolveEdgeContraction(top, s);
            double[] tx = top.X.ToArray(), ty = top.Y.ToArray();
            double[] fx, fy;
            if (contra.Any(d => d > 1e-6))
                (fx, fy) = PitSchemeEnvelope.OffsetPolygon(tx, ty, contra);
            else { fx = tx; fy = ty; }
            clipXY = Flatten(fx, fy);

            // 逐 Z 层 β 截锥：第 k 层足迹 = 顶口按(收缩 + 该层放坡内移 drop/tanβ)逐边内缩；收口层置空。
            var bounds = model.Bounds;
            double dz = model.Sz > 1e-9 ? model.Sz : 1.0, oz = bounds.minZ;
            int nz = Math.Max(1, (int)Math.Round((bounds.maxZ - bounds.minZ) / dz));
            int me = top.Count; double zTop = top.Zsurface;
            double origSign = Math.Sign(SignedArea(tx, ty));
            double collapseArea = Math.Max(1.0, s.BottomWidthM * s.BottomWidthM * 0.25);
            layerClips = new double[nz][];
            for (int k = 0; k < nz; k++)
            {
                double drop = zTop - (oz + (k + 0.5) * dz);
                if (drop <= 0) { layerClips[k] = clipXY; continue; }   // 顶口之上：用收缩后顶口足迹
                var inset = new double[me];
                for (int i = 0; i < me; i++)
                    inset[i] = contra[i] + drop / Math.Tan(ClampBeta(edgeBeta[i]) * Math.PI / 180.0);
                var (rx, ry) = PitSchemeEnvelope.OffsetPolygon(tx, ty, inset);
                double a = SignedArea(rx, ry);
                layerClips[k] = (Math.Sign(a) != origSign || Math.Abs(a) < collapseArea)
                    ? Array.Empty<double>()
                    : Flatten(rx, ry);
            }
        }

        string? ashAttr = BlockModelCoal.FindAshAttr(model);
        var captured = model;
        progress?.Report($"采样块体并求解：{s.Name}…");

        return await Task.Run(() =>
        {
            try
            {
                var prof = PlanSectionSampler.SampleLayers(captured, attr, val, tol, density, ct, clipXY, layerClips, ashAttr);
                if (prof.TotalCoalVol <= 0)
                    return PitSolveOutcome.Fail("采样到的煤量为 0（检查煤属性取值/筛选）");
                ct.ThrowIfCancellationRequested();

                var sol = PitSectionSolver.SolveDepth(prof, revenue, stripCost, maxDepth);
                var result = PitEvaluator.Evaluate(prof, sol, s, njh);
                string bound = (maxDepth < double.MaxValue && result.DepthM >= maxDepth - prof.Dz)
                    ? $"（{envSrc}+底宽 几何限深）" : "（经济限深）";
                return new PitSolveOutcome
                {
                    Ok = true,
                    Result = result,
                    Message = $"{s.Name}：深 {result.DepthM:N0} m {bound} · 煤 {result.CoalWanT:N0} 万t · 平均剥采比 {result.AvgRatio:F2} m³/t",
                };
            }
            catch (OperationCanceledException) { return PitSolveOutcome.Fail("已取消"); }
            catch (Exception ex) { return PitSolveOutcome.Fail($"求解异常：{ex.Message}"); }
        }, ct);
    }

    private static double[] Flatten(double[] x, double[] y)
    {
        var f = new double[x.Length * 2];
        for (int i = 0; i < x.Length; i++) { f[i * 2] = x[i]; f[i * 2 + 1] = y[i]; }
        return f;
    }

    /// <summary>多边形有向面积（鞋带；符号判朝向，用于检测过内缩翻转）。</summary>
    public static double SignedArea(double[] x, double[] y)
    {
        double a = 0; int n = x.Length;
        for (int i = 0, j = n - 1; i < n; j = i++) a += (x[j] + x[i]) * (y[i] - y[j]);
        return a / 2.0;
    }

    private static double ClampBeta(double b) => Math.Max(1.0, Math.Min(89.0, b));
}

/// <summary>
/// 储量 / 剥采比 / 经济 / 时序 / 安全 指标评价（原 <c>PitEvaluator</c>，纯函数）。
/// 年产用泰勒规则、NPV 用等额现金流 DCF、灰分优先块体采样、台阶高取方案值。
/// </summary>
public static class PitEvaluator
{
    private const double DefaultBenchH = 12.0;
    private const double FallbackAshPct = 14.0;

    public static PitResult Evaluate(ResourceProfile p, PitDepthSolveResult sol, PitScheme s, double njh)
    {
        double coalWanT = sol.CoalT / 1e4;
        double wasteWanM3 = sol.WasteM3 / 1e4;
        double avgSR = coalWanT > 1e-9 ? wasteWanM3 / coalWanT : 0;
        double depth = (p.Nz - sol.BottomK) * p.Dz;
        double netWan = sol.NetValueYuan / 1e4;
        double unitCost = s.Econ.MiningCost + avgSR * s.Econ.StripCost;
        double totalCoalT = p.TotalCoalVol * p.Density;
        double recovery = totalCoalT > 1e-9 ? sol.CoalT / totalCoalT * 100.0 : 0;
        double minF = s.Walls.Where(w => w.SafetyF.HasValue).Select(w => w.SafetyF!.Value)
                             .DefaultIfEmpty(0).Min();

        double benchH = s.BenchHeightM > 0.5 ? s.BenchHeightM : DefaultBenchH;
        int benchCount = depth > 0 ? (int)Math.Ceiling(depth / benchH) : 0;

        double annual, life;
        if (s.TargetAnnualWanT > 0)
        { annual = s.TargetAnnualWanT; life = annual > 1e-9 ? coalWanT / annual : 0; }
        else
        {
            double coalMt = coalWanT / 100.0;
            life = coalMt > 1e-9 ? Math.Min(60.0, Math.Max(5.0, 6.5 * Math.Pow(coalMt, 0.25))) : 0;
            annual = life > 1e-9 ? coalWanT / life : 0;
        }

        double r = s.Econ.DiscountRatePct > 0 ? Math.Min(0.30, s.Econ.DiscountRatePct / 100.0) : 0.08;
        double npv;
        if (life > 1e-9)
        {
            double pvFactor = r > 1e-9 ? (1 - Math.Pow(1 + r, -life)) / r : life;
            npv = (netWan / life) * pvFactor;
        }
        else npv = 0;

        double ash = p.HasAsh ? p.AvgAshPct : (s.AshPct > 0 ? s.AshPct : FallbackAshPct);

        return new PitResult
        {
            EconRatio = njh,
            DepthM = depth,
            CoalWanT = coalWanT,
            WasteWanM3 = wasteWanM3,
            AvgRatio = avgSR,
            ContourRatio = sol.ContourSR,
            NetValue = netWan,
            Ok = avgSR <= njh,
            ProductionRatioPeak = Math.Max(avgSR, sol.ContourSR),
            RecoveryPct = Math.Min(100.0, recovery),
            AvgAshPct = ash,
            TopAreaHa = p.PlanAreaM2 / 1e4,
            BottomWidthM = s.BottomWidthM,
            BenchCount = benchCount,
            Npv = npv,
            UnitCostYuanPerT = unitCost,
            ServiceLifeYears = life,
            AnnualCoalWanT = annual,
            MinSafetyF = minF,
            CurveDepthM = sol.CurveDepthM,
            CurveContourSR = sol.CurveContourSR,
            CurveNetWan = sol.CurveNetYuan.Select(v => v / 1e4).ToArray(),
        };
    }
}

public sealed class MaterializeOutcome
{
    public bool Ok;
    public string Message = "";
    public int Levels;
    public static MaterializeOutcome Fail(string m) => new() { Ok = false, Message = m };
}

/// <summary>
/// 段③ 三维境界落地（原 <c>PitMaterializer</c>）。把已求解方案的"最终境界"落地为**三维台阶面 + 台阶线**。
/// 各台阶线首尾相接——坡顶线↘(坡面)↘坡底线↘(平盘)↘下一坡顶线。自顶向下逐帮 β 生成 crest/toe 环
/// （坡面内缩 H/tanα；平盘内缩 W=H/tanβ−H/tanα，各帮不同），相邻环顶点数相同 → 直接放样三角化成三维台阶面。
/// 顶口优先地表界，否则块体足迹兜底。按方案图层幂等（先删后建）。
/// </summary>
public static class PitMaterializer
{
    private const double DefaultBenchH = 12.0;
    private const double DefaultSlopeFaceAngleDeg = 70;

    public static MaterializeOutcome Materialize(PitScheme s, IPlanEntityHost? host)
    {
        if (host == null) return MaterializeOutcome.Fail("实体能力不可用");
        if (s.Result is not { } r || r.DepthM <= 0) return MaterializeOutcome.Fail("请先求解出有效境界（深度 > 0）再确定");

        var model = host.ActiveBlockModel;
        var top = PitSchemeEnvelope.ResolveTopOutline(host, s.Geometry.SurfaceLimitHandle, model);
        if (top == null || top.Count < 3) return MaterializeOutcome.Fail("无地表界且无激活块体，或顶口顶点不足，无法取顶口范围");

        double benchH = s.BenchHeightM > 0.5 ? s.BenchHeightM : DefaultBenchH;
        double faceA = (s.BenchFaceAngleDeg >= 30 && s.BenchFaceAngleDeg <= 89) ? s.BenchFaceAngleDeg : DefaultSlopeFaceAngleDeg;

        double depth = r.DepthM;
        double tanA = Math.Tan(faceA * Math.PI / 180.0);
        var edgeBeta = PitSchemeEnvelope.ResolveEdgeBetas(top, s);
        int m = top.Count;
        int n = Math.Max(1, (int)Math.Round(depth / benchH));

        double slopeRun = benchH / tanA;
        var slopeInset = new double[m];
        var bermInset = new double[m];
        for (int i = 0; i < m; i++)
        {
            slopeInset[i] = slopeRun;
            double beta = Math.Max(1.0, Math.Min(89.0, edgeBeta[i]));
            double perBenchRun = benchH / Math.Tan(beta * Math.PI / 180.0);
            bermInset[i] = Math.Max(0.0, perBenchRun - slopeRun);
        }

        var rings = new List<(double[] x, double[] y, double z, bool crest)>();
        var contra = PitSchemeEnvelope.ResolveEdgeContraction(top, s);
        double[] curX, curY;
        if (contra.Any(d => d > 1e-6))
            (curX, curY) = PitSchemeEnvelope.OffsetPolygon(top.X.ToArray(), top.Y.ToArray(), contra);
        else { curX = top.X.ToArray(); curY = top.Y.ToArray(); }
        double z = top.Zsurface;
        rings.Add((curX, curY, z, true));
        for (int k = 1; k <= n; k++)
        {
            (curX, curY) = PitSchemeEnvelope.OffsetPolygon(curX, curY, slopeInset);
            z -= benchH;
            rings.Add((curX, curY, z, false));
            if (Degenerate(curX, curY)) break;
            if (k < n)
            {
                (curX, curY) = PitSchemeEnvelope.OffsetPolygon(curX, curY, bermInset);
                rings.Add((curX, curY, z, true));
                if (Degenerate(curX, curY)) break;
            }
        }

        string layer = "境界_" + Sanitize(s.Name);
        try { var old = host.GetHandlesByLayer(layer); if (old.Length > 0) host.DeleteEntities(old); }
        catch { /* 无旧实体 */ }

        var batch = new PlanEntityBatch { Layer = layer, LayerColor = (20, 184, 166) };
        BuildLoftMesh(rings, m, out var verts, out var idx);
        if (verts.Count >= 3 && idx.Count >= 1)
            batch.Meshes.Add(($"境界台阶面_{Sanitize(s.Name)}", verts, idx, 120, 200, 190));

        foreach (var ring in rings)
        {
            var (cr, cg, cb) = ring.crest ? ((byte)220, (byte)60, (byte)60) : ((byte)40, (byte)110, (byte)230);
            batch.Rings.Add((ring.x, ring.y, ring.z, cr, cg, cb));
        }

        long[] handles;
        try { handles = host.Import(batch); }
        catch (Exception ex) { return MaterializeOutcome.Fail($"落地失败：{ex.Message}"); }
        if (handles.Length == 0) return MaterializeOutcome.Fail("引擎导入失败");
        r.BenchLineHandles = handles.ToList();

        int benches = rings.Count(ring => !ring.crest);
        return new MaterializeOutcome
        {
            Ok = true,
            Levels = rings.Count,
            Message = $"已在图层「{layer}」落地三维台阶面 + 台阶线（顶口={(top.FromSurfaceLimit ? "地表界" : "块体足迹")}，{benches} 台阶 / {rings.Count} 环，深 {depth:N0} m，台阶 H={benchH:0.#} m·坡面 α={faceA:0.#}°{(s.ContractionM > 0 ? $"，整体收缩 {s.ContractionM:0} m" : "")}）",
        };
    }

    /// <summary>相邻环放样三角化（各环 m 顶点，按 j↔j 连成条带）。</summary>
    public static void BuildLoftMesh(List<(double[] x, double[] y, double z, bool crest)> rings, int m,
                                     out List<(double x, double y, double z)> verts, out List<(int a, int b, int c)> idx)
    {
        verts = new List<(double x, double y, double z)>(rings.Count * m);
        idx = new List<(int a, int b, int c)>();
        foreach (var ring in rings)
            for (int j = 0; j < m; j++) verts.Add((ring.x[j], ring.y[j], ring.z));

        for (int r = 0; r < rings.Count - 1; r++)
        {
            int aBase = r * m, bBase = (r + 1) * m;
            for (int j = 0; j < m; j++)
            {
                int j2 = (j + 1) % m;
                int a0 = aBase + j, a1 = aBase + j2, b0 = bBase + j, b1 = bBase + j2;
                idx.Add((a0, b0, b1));
                idx.Add((a0, b1, a1));
            }
        }
    }

    /// <summary>环退化（外接框短边 &lt; 5 m）即停止下降——已到坑底。</summary>
    private static bool Degenerate(double[] x, double[] y)
    {
        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        for (int i = 0; i < x.Length; i++)
        {
            xmin = Math.Min(xmin, x[i]); xmax = Math.Max(xmax, x[i]);
            ymin = Math.Min(ymin, y[i]); ymax = Math.Max(ymax, y[i]);
        }
        return Math.Min(xmax - xmin, ymax - ymin) < 5.0;
    }

    /// <summary>图层名安全化（去非法字符）。</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "方案";
        const string bad = "<>/\\\":;?*|,=`";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(bad.IndexOf(ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }
}
