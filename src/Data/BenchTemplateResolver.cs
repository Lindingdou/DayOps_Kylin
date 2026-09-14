using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 自动解析出的整套台阶参数 + 推导量 + 来源/告警。
/// 「自动化优先」：尽量不让用户手填 —— 给最少上下文即可解析。
/// 移植 <c>MineAssLib.Services.ResolvedBenchParams</c>。
/// </summary>
public sealed record ResolvedBenchParams(
    double BenchHeight,             // H 台阶高
    double FaceAngleDeg,            // α 坡面角
    double BermWidth,               // W 安全平台宽(最终帮/端帮的保安平台)
    double? MiningWidth,            // 采宽(可选)
    double OverallSlopeAngleDeg,    // 最终(组合)帮坡角 —— 自动推导
    long? TemplateId,               // 解析到的模板(无=规范默认/兜底)
    string Provenance,              // 来源说明(给 UI 显示"依据")
    IReadOnlyList<string> Warnings, // 越界/冲突告警(自动校验)
    double MinWorkingBermWidth,     // W_work 最小工作平盘宽(工作帮)
    double WorkingSlopeAngleDeg,    // 工作帮坡角 —— 由 W_work 推导
    double HaulBermWidth,           // W_haul 运输平台宽(最终帮,取路基宽)
    // 煤台阶那一套(煤岩分层放坡:顶板以下的台阶用它;上面那几项是岩台阶的)
    double CoalBenchHeight,
    double CoalFaceAngleDeg,
    double CoalBermWidth);

/// <summary>
/// 台阶参数自动解析引擎（移植 <c>MineAssLib.Services.BenchTemplateResolver</c>）。
/// 优先级链（从具体到兜底）：
///   1. 匹配模板（按 物料+硬度 评分，优先现行版 <c>is_current</c>）
///   2. 规范默认（按 是否排土 × 硬度 的内置规范表）
///   3. 硬兜底（保证数据库未就绪也能用）
/// 另自动：① 推导最终(组合)帮坡角 ② 对各项做越界校验（<c>parameter_definition</c> 的
/// <c>standard_min</c>/<c>standard_max</c>）。**任何数据源异常都降级、绝不抛。**
///
/// ── 原版一处踩过的坑，照搬其修法 ──
/// 早先这里写死"排土场永不读模板"，于是「排土模板」里配的 H/α/W 存进了模板库却永远取不到，
/// 放坡恒用硬编码的 (10,35,3) —— <b>改了没反应，而且不报错</b>。
/// 反方向同样要挡：采场有可能悄悄选中一条排土场模板（35° 安息角当成采场坡角）。
/// 两个方向都按模板描述里的标记筛，且**过滤是硬条件不是加分项** ——
/// 选错类型带来的是坡角整体错（35° vs 70°），不是"不够优"。
///
/// ── Kylin 侧的实现差异（登记）──
/// 原版走 <c>EquipmentDataContext</c> 仓储门面，Kylin 直接读
/// <c>process_template</c> / <c>template_param_value</c> / <c>parameter_definition</c>（V008 + V007 + V034）。
/// 优先级链、评分规则、规范默认表、越界校验口径一字不差。
/// </summary>
public static class BenchTemplateResolver
{
    // 参数码（按 code 取，不写死 param_id）
    private const string CodeH = "bench_height";
    private const string CodeA = "bench_slope_angle";
    private const string CodeW = "safety_platform_width";    // 安全平台宽(最终帮/端帮)
    private const string CodeMW = "mining_width";
    private const string CodeWW = "working_platform_width";  // 最小工作平盘宽(工作帮,放坡默认取它)
    private const string CodeRW = "road_width";              // 路基宽 → 运输平台宽(最终帮每隔几级一条)
    private const string CodeCH = "coal_bench_height";       // 煤台阶(V034):煤岩分层放坡,顶板以下用这套
    private const string CodeCA = "coal_bench_slope_angle";
    private const string CodeCW = "coal_platform_width";

    /// <summary>
    /// 排土模板在描述里的标记。<b>字面量必须与原版一字不差</b> ——
    /// 模板是编辑器写、解析器读的，两边对不上就等于"排土场永不读模板"那个坑原样重现一遍
    /// （改了没反应，还不报错）。原版常量在 <c>BenchTemplateReader</c>。
    /// </summary>
    public const string DumpTemplateMark = "排土场模板";

    /// <summary>采场模板的标记（同上）。没有任何标记的模板按采场算，与原版一致。</summary>
    public const string PitTemplateMark = "采场模板";

    // 硬兜底（数据库完全不可用时）
    public const double DefaultMinWorkingBerm = 80.0;
    public const double DefaultHaulBerm = 30.0;
    public const double DefaultCoalH = 15.0;
    public const double DefaultCoalA = 65.0;
    public const double DefaultCoalW = 6.0;

    /// <summary>
    /// 自动解析台阶参数。<paramref name="material"/>/<paramref name="hardness"/> 可空 ——
    /// 空则按现行模板/规范兜底，仍能给出完整结果。
    /// </summary>
    public static ResolvedBenchParams Resolve(DbConnection? conn, bool isDump,
                                              string? material = null, string? hardness = null)
    {
        var warnings = new List<string>();
        double H, A, W, ww, wh, cH, cA, cW;
        double? mw = null;
        long? tplId = null;
        string prov;

        var tpl = PickTemplate(conn, material, hardness, isDump);
        if (tpl != null)
        {
            var bp = ReadTemplateParams(conn!, tpl.Value.Id, isDump, hardness);
            H = bp.H; A = bp.A; W = bp.W; mw = bp.MiningWidth;
            ww = bp.WorkBerm; wh = bp.HaulBerm;
            cH = bp.CoalH; cA = bp.CoalA; cW = bp.CoalW;
            tplId = tpl.Value.Id;
            prov = isDump ? $"排土模板 {tpl.Value.Code}" : $"模板 {tpl.Value.Code}";
        }
        else
        {
            (H, A, W) = Norm(isDump, hardness);
            ww = StandardDefault(conn, CodeWW) ?? DefaultMinWorkingBerm;   // 无模板 → 取指标规范默认
            wh = StandardDefault(conn, CodeRW) ?? DefaultHaulBerm;
            cH = StandardDefault(conn, CodeCH) ?? DefaultCoalH;
            cA = StandardDefault(conn, CodeCA) ?? DefaultCoalA;
            cW = StandardDefault(conn, CodeCW) ?? DefaultCoalW;
            prov = isDump ? "规范默认 · 排土场" : $"规范默认 · {HardnessLabel(hardness)}";
        }

        // 自动越界校验（数据库未就绪则静默跳过）
        ValidateRange(conn, CodeH, H, warnings);
        ValidateRange(conn, CodeA, A, warnings);
        ValidateRange(conn, CodeW, W, warnings);
        ValidateRange(conn, CodeWW, ww, warnings);
        if (!isDump)
        {
            ValidateRange(conn, CodeRW, wh, warnings);   // 运输平台只在采场最终帮用
            // 煤台阶只在采场(煤岩分层放坡)用
            ValidateRange(conn, CodeCH, cH, warnings);
            ValidateRange(conn, CodeCA, cA, warnings);
            ValidateRange(conn, CodeCW, cW, warnings);
        }

        // 最终帮：H/α + 安全平台 W。工作帮：H/α + 最小工作平盘 W_work(80m 级) → 远缓(≈10°)。
        double beta = OverallSlopeAngleDeg(H, A, W);
        double betaWork = OverallSlopeAngleDeg(H, A, ww);
        return new ResolvedBenchParams(H, A, W, mw, beta, tplId, prov, warnings,
                                       ww, betaWork, wh, cH, cA, cW);
    }

    /// <summary>
    /// 最终(组合)帮坡角：一摞均匀台阶，每级垂直 H、水平 (H/tanα + W)，
    /// 整体帮坡角 β = atan( H / (H/tanα + W) )。**与级数无关**（渐近/匀质近似）。
    /// </summary>
    public static double OverallSlopeAngleDeg(double benchHeight, double faceAngleDeg, double bermWidth)
    {
        if (benchHeight <= 0) return 0;
        double a = faceAngleDeg * Math.PI / 180.0;
        double tan = Math.Tan(a);
        if (tan <= 1e-9) return 0;                 // 坡面角→0：退化
        double run = benchHeight / tan + Math.Max(0.0, bermWidth);
        if (run <= 1e-9) return 90.0;              // 无水平投影：直立
        return Math.Atan(benchHeight / run) * 180.0 / Math.PI;
    }

    /// <summary>帮坡角反算平盘宽：给 H/α 与目标整体帮坡角 β，求 W = H/tanβ − H/tanα（钳 ≥0）。</summary>
    public static double SolveBermForOverallAngle(double benchHeight, double faceAngleDeg, double targetBetaDeg)
    {
        if (benchHeight <= 0 || targetBetaDeg <= 0 || targetBetaDeg >= 90) return 0;
        double tb = Math.Tan(targetBetaDeg * Math.PI / 180.0);
        double ta = Math.Tan(faceAngleDeg * Math.PI / 180.0);
        if (tb <= 1e-9 || ta <= 1e-9) return 0;
        return Math.Max(0.0, benchHeight / tb - benchHeight / ta);
    }

    /// <summary>内置规范默认（硬岩 15/70/8、中硬 12/68/6、软岩 10/60/5、排土 10/35/3）。</summary>
    internal static (double H, double A, double W) Norm(bool isDump, string? hardness)
    {
        if (isDump) return (10, 35, 3);
        return (hardness ?? "").ToLowerInvariant() switch
        {
            "hard" => (15, 70, 8),
            "medium" => (12, 68, 6),
            "soft" => (10, 60, 5),
            _ => (12, 70, 4),   // 通用兜底
        };
    }

    internal static string HardnessLabel(string? h) => (h ?? "").ToLowerInvariant() switch
    {
        "hard" => "硬岩",
        "medium" => "中硬岩",
        "soft" => "软岩",
        _ => "通用",
    };

    // ── 模板挑选 ────────────────────────────────────────────────────────────

    internal readonly record struct TemplateRow(long Id, string Code, string? Material, string? Hardness,
                                                bool IsCurrent, string? Description);

    /// <summary>
    /// 按 物料 + 硬度 评分挑模板，并先按【边坡类型】过滤 —— 排土场只看排土模板，采场只看非排土模板。
    /// <b>过滤是硬条件不是加分项</b>：选错类型带来的是坡角整体错（35° vs 70°），不是"不够优"。
    /// </summary>
    internal static TemplateRow? PickTemplate(DbConnection? conn, string? material, string? hardness, bool isDump)
    {
        if (conn == null) return null;
        try
        {
            TemplateRow? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var t in ListTemplates(conn))
            {
                bool isDumpTpl = (t.Description ?? "").Contains(DumpTemplateMark, StringComparison.Ordinal);
                if (isDumpTpl != isDump) continue;           // 类型不符 → 直接不参与
                double s = 0;
                if (!string.IsNullOrEmpty(material)
                    && string.Equals(t.Material, material, StringComparison.OrdinalIgnoreCase)) s += 2;
                if (!string.IsNullOrEmpty(hardness)
                    && string.Equals(t.Hardness, hardness, StringComparison.OrdinalIgnoreCase)) s += 1;
                if (t.IsCurrent) s += 0.5;
                if (s > bestScore) { bestScore = s; best = t; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>现行可用模板（<c>status='active'</c>）。</summary>
    internal static List<TemplateRow> ListTemplates(DbConnection conn)
    {
        var list = new List<TemplateRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id, code, applicable_material, applicable_hardness, "
                        + "is_current, description FROM process_template WHERE status = 'active'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new TemplateRow(
                Convert.ToInt64(rd.GetValue(0)),
                rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? "",
                rd.IsDBNull(2) ? null : rd.GetValue(2)?.ToString(),
                rd.IsDBNull(3) ? null : rd.GetValue(3)?.ToString(),
                !rd.IsDBNull(4) && Convert.ToInt64(rd.GetValue(4)) != 0,
                rd.IsDBNull(5) ? null : rd.GetValue(5)?.ToString()));
        return list;
    }

    private readonly record struct TemplateParams(double H, double A, double W, double? MiningWidth,
                                                  double WorkBerm, double HaulBerm,
                                                  double CoalH, double CoalA, double CoalW);

    /// <summary>
    /// 读一个模板的各项推荐值；**该模板没配的项回落规范默认**（不是回落 0）——
    /// 模板通常只配 H/α/W 三项，其余项按 0 处理会让工作帮坡角算成 90°。
    /// </summary>
    private static TemplateParams ReadTemplateParams(DbConnection conn, long templateId, bool isDump, string? hardness)
    {
        var byCode = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT p.code, v.recommended_value FROM template_param_value v "
                            + "JOIN parameter_definition p ON p.param_id = v.param_id "
                            + "WHERE v.template_id = " + templateId.ToString(CultureInfo.InvariantCulture);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
                string code = rd.GetValue(0)?.ToString() ?? "";
                if (code.Length > 0) byCode[code] = Convert.ToDouble(rd.GetValue(1), CultureInfo.InvariantCulture);
            }
        }
        catch { /* 取不到就整份回落规范默认 */ }

        var (nH, nA, nW) = Norm(isDump, hardness);
        double Pick(string code, double fallback) => byCode.TryGetValue(code, out double v) && v > 0 ? v : fallback;

        return new TemplateParams(
            Pick(CodeH, nH), Pick(CodeA, nA), Pick(CodeW, nW),
            byCode.TryGetValue(CodeMW, out double m) && m > 0 ? m : null,
            Pick(CodeWW, StandardDefault(conn, CodeWW) ?? DefaultMinWorkingBerm),
            Pick(CodeRW, StandardDefault(conn, CodeRW) ?? DefaultHaulBerm),
            Pick(CodeCH, StandardDefault(conn, CodeCH) ?? DefaultCoalH),
            Pick(CodeCA, StandardDefault(conn, CodeCA) ?? DefaultCoalA),
            Pick(CodeCW, StandardDefault(conn, CodeCW) ?? DefaultCoalW));
    }

    // ── 参数定义 ────────────────────────────────────────────────────────────

    /// <summary>某参数码的规范推荐值（<c>standard_default</c>）；没有返回 null（**不猜 0**）。</summary>
    internal static double? StandardDefault(DbConnection? conn, string code)
    {
        if (conn == null) return null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT standard_default FROM parameter_definition WHERE code = '{code}'";
            object? v = cmd.ExecuteScalar();
            if (v == null || v is DBNull) return null;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static void ValidateRange(DbConnection? conn, string code, double value, List<string> warnings)
    {
        if (conn == null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT name, unit, standard_min, standard_max FROM parameter_definition WHERE code = '{code}'";
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return;
            string name = rd.IsDBNull(0) ? code : rd.GetValue(0)?.ToString() ?? code;
            string unit = rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? "";
            if (!rd.IsDBNull(2))
            {
                double lo = Convert.ToDouble(rd.GetValue(2), CultureInfo.InvariantCulture);
                if (value < lo)
                    warnings.Add($"{name} {value.ToString("0.##", CultureInfo.InvariantCulture)}{unit} "
                               + $"低于规范下限 {lo.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            if (!rd.IsDBNull(3))
            {
                double hi = Convert.ToDouble(rd.GetValue(3), CultureInfo.InvariantCulture);
                if (value > hi)
                    warnings.Add($"{name} {value.ToString("0.##", CultureInfo.InvariantCulture)}{unit} "
                               + $"高于规范上限 {hi.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }
        catch { /* 数据库未就绪 → 跳过校验 */ }
    }
}
