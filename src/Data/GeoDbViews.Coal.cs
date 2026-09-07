using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  「煤质管理」组(5 窗: 煤质数据管理 / 空间分布 / 数据看板 / 统计分析 / 钻孔柱状图)的查询与纯逻辑。
//  忠实移植原 GeoDataBase: CoalQualityService(CRUD/StatsBySeam/RebuildSummary)、CoalQualityExcelIo(30 列模板,
//  Excel→CSV)、CoalReferenceService(字典)、CoalQualityEstimator(OK/IDW/NN/MA 规则网格)、各窗内的纯算子。
//  写库全部真正落 coal_sample / coal_sample_summary。
// ─────────────────────────────────────────────────────────────────────────────
public static partial class GeoDbViews
{
    // ═══════════════════════════ 煤样行(30 字段 + 孔号/坐标) ═══════════════════════════

    /// <summary>coal_sample 全列 + 钻孔孔号/坐标(join borehole)。可变类, 供表格显示与编辑对话框回写。</summary>
    public sealed class CoalSampleRow
    {
        public long Id { get; set; }
        public long BoreholeId { get; set; }
        public string HoleId { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public string SeamCode { get; set; } = "";
        public double? DepthFrom { get; set; }
        public double? DepthTo { get; set; }
        public double? SampleThickness { get; set; }
        public double? ZSample { get; set; }
        public double? ApparentDensity { get; set; }
        public double? TrueDensity { get; set; }
        public double? MadRaw { get; set; }
        public double? MadClean { get; set; }
        public double? AdRaw { get; set; }
        public double? AdClean { get; set; }
        public double? VdafRaw { get; set; }
        public double? VdafClean { get; set; }
        public double? FcdRaw { get; set; }
        public double? FcdClean { get; set; }
        public double? StdRaw { get; set; }
        public double? StdClean { get; set; }
        public double? QgrD { get; set; }
        public double? QnetAd { get; set; }
        public double? PlasticXMm { get; set; }
        public double? PlasticYMm { get; set; }
        public string? PlastometricCurve { get; set; }
        public double? CakingG { get; set; }
        public int? CharResidueRaw { get; set; }
        public int? CharResidueClean { get; set; }
        public double? CleanCoalYield { get; set; }
        public string? CoalType { get; set; }
        public int? SourcePage { get; set; }
        public string? Remark { get; set; }

        public CoalSampleRow Clone() => (CoalSampleRow)MemberwiseClone();

        /// <summary>转 CoalAnalytics 用的分析记录(字段子集)。</summary>
        public CoalSample ToAnalytics() => new(Id, HoleId, SeamCode, X, Y, ZSample,
            AdRaw, AdClean, StdRaw, StdClean, QgrD, QnetAd, VdafRaw, VdafClean,
            SampleThickness, ApparentDensity, CleanCoalYield, CakingG, PlasticYMm, CoalType);
    }

    private const string CoalSampleSelect = @"
        SELECT cs.id AS Id, cs.borehole_id AS BoreholeId,
               COALESCE(b.hole_id, '#' || cs.borehole_id) AS HoleId,
               COALESCE(b.x, 0.0) AS X, COALESCE(b.y, 0.0) AS Y,
               cs.seam_code AS SeamCode, cs.depth_from AS DepthFrom, cs.depth_to AS DepthTo,
               cs.sample_thickness AS SampleThickness, cs.z_sample AS ZSample,
               cs.apparent_density AS ApparentDensity, cs.true_density AS TrueDensity,
               cs.mad_raw AS MadRaw, cs.mad_clean AS MadClean, cs.ad_raw AS AdRaw, cs.ad_clean AS AdClean,
               cs.vdaf_raw AS VdafRaw, cs.vdaf_clean AS VdafClean, cs.fcd_raw AS FcdRaw, cs.fcd_clean AS FcdClean,
               cs.std_raw AS StdRaw, cs.std_clean AS StdClean, cs.qgr_d AS QgrD, cs.qnet_ad AS QnetAd,
               cs.plastic_x_mm AS PlasticXMm, cs.plastic_y_mm AS PlasticYMm, cs.plastometric_curve AS PlastometricCurve,
               cs.caking_g AS CakingG, cs.char_residue_raw AS CharResidueRaw, cs.char_residue_clean AS CharResidueClean,
               cs.clean_coal_yield AS CleanCoalYield, cs.coal_type AS CoalType, cs.source_page AS SourcePage, cs.remark AS Remark
          FROM coal_sample cs LEFT JOIN borehole b ON b.id = cs.borehole_id";

    /// <summary>全部化验段(忠实原 CoalQualityService.All + 孔号映射), 按 孔号/煤层/起深 排。</summary>
    public static List<CoalSampleRow> CoalLoadSamples(SqliteConnection conn)
        => conn.Query<CoalSampleRow>(CoalSampleSelect + " ORDER BY HoleId, cs.seam_code, cs.depth_from").ToList();

    /// <summary>按主键取一条(忠实原 GetSample); 不存在返回 null。</summary>
    public static CoalSampleRow? CoalGetSample(SqliteConnection conn, long id)
        => conn.Query<CoalSampleRow>(CoalSampleSelect + " WHERE cs.id = @id", new { id }).FirstOrDefault();

    /// <summary>某孔全部化验段(忠实原 SamplesByBorehole: ORDER BY depth_from)。</summary>
    public static List<CoalSampleRow> CoalSamplesByBorehole(SqliteConnection conn, long boreholeId)
        => conn.Query<CoalSampleRow>(CoalSampleSelect + " WHERE cs.borehole_id = @b ORDER BY cs.depth_from", new { b = boreholeId }).ToList();

    private const string CoalSampleCols =
        "borehole_id, seam_code, depth_from, depth_to, sample_thickness, z_sample, apparent_density, true_density, " +
        "mad_raw, mad_clean, ad_raw, ad_clean, vdaf_raw, vdaf_clean, fcd_raw, fcd_clean, std_raw, std_clean, qgr_d, qnet_ad, " +
        "plastic_x_mm, plastic_y_mm, plastometric_curve, caking_g, char_residue_raw, char_residue_clean, clean_coal_yield, coal_type, source_page, remark";
    private const string CoalSampleVals =
        "@BoreholeId, @SeamCode, @DepthFrom, @DepthTo, @SampleThickness, @ZSample, @ApparentDensity, @TrueDensity, " +
        "@MadRaw, @MadClean, @AdRaw, @AdClean, @VdafRaw, @VdafClean, @FcdRaw, @FcdClean, @StdRaw, @StdClean, @QgrD, @QnetAd, " +
        "@PlasticXMm, @PlasticYMm, @PlastometricCurve, @CakingG, @CharResidueRaw, @CharResidueClean, @CleanCoalYield, @CoalType, @SourcePage, @Remark";

    /// <summary>新增化验段(忠实原 InsertSample), 返回新 id。煤类非字典码置 NULL(免外键失败)。</summary>
    public static long CoalInsertSample(SqliteConnection conn, CoalSampleRow row)
    {
        row.CoalType = CoalValidType(conn, row.CoalType);
        return conn.ExecuteScalar<long>($"INSERT INTO coal_sample ({CoalSampleCols}) VALUES ({CoalSampleVals}); SELECT last_insert_rowid();", row);
    }

    /// <summary>更新化验段全部字段(忠实原 UpdateSample)。返回受影响行数。</summary>
    public static int CoalUpdateSample(SqliteConnection conn, CoalSampleRow row)
    {
        row.CoalType = CoalValidType(conn, row.CoalType);
        return conn.Execute(@"UPDATE coal_sample SET borehole_id=@BoreholeId, seam_code=@SeamCode, depth_from=@DepthFrom, depth_to=@DepthTo,
            sample_thickness=@SampleThickness, z_sample=@ZSample, apparent_density=@ApparentDensity, true_density=@TrueDensity,
            mad_raw=@MadRaw, mad_clean=@MadClean, ad_raw=@AdRaw, ad_clean=@AdClean, vdaf_raw=@VdafRaw, vdaf_clean=@VdafClean,
            fcd_raw=@FcdRaw, fcd_clean=@FcdClean, std_raw=@StdRaw, std_clean=@StdClean, qgr_d=@QgrD, qnet_ad=@QnetAd,
            plastic_x_mm=@PlasticXMm, plastic_y_mm=@PlasticYMm, plastometric_curve=@PlastometricCurve, caking_g=@CakingG,
            char_residue_raw=@CharResidueRaw, char_residue_clean=@CharResidueClean, clean_coal_yield=@CleanCoalYield,
            coal_type=@CoalType, source_page=@SourcePage, remark=@Remark, updated_at=CURRENT_TIMESTAMP WHERE id=@Id", row);
    }

    /// <summary>删除化验段(忠实原 DeleteSample)。</summary>
    public static int CoalDeleteSample(SqliteConnection conn, long id)
        => conn.Execute("DELETE FROM coal_sample WHERE id=@id", new { id });

    private static string? CoalValidType(SqliteConnection conn, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM coal_classification WHERE code=@c", new { c }) > 0 ? c : null;
    }

    // ═══════════════════════════ 字典(忠实原 CoalReferenceService) ═══════════════════════════

    public sealed record CoalSeamDefRow(string Code, string Name, long SortOrder, string? ColorHex);
    public sealed record CoalClassRefRow(string Code, string NameCn);
    public sealed record CoalGradeRuleFull(string RuleType, string LevelCode, string LevelName, double? ValueMin, double? ValueMax, string? ColorHex);

    /// <summary>煤层字典(ORDER BY sort_order)。</summary>
    public static List<CoalSeamDefRow> CoalSeamDefs(SqliteConnection conn)
        => conn.Query<CoalSeamDefRow>("SELECT code AS Code, COALESCE(name,'') AS Name, COALESCE(sort_order,0) AS SortOrder, color_hex AS ColorHex FROM coal_seam_def ORDER BY sort_order, code").ToList();

    /// <summary>GB/T 5751 煤类字典(ORDER BY sort_order)。</summary>
    public static List<CoalClassRefRow> CoalClassRefs(SqliteConnection conn)
        => conn.Query<CoalClassRefRow>("SELECT code AS Code, COALESCE(name_cn,'') AS NameCn FROM coal_classification ORDER BY sort_order, code").ToList();

    /// <summary>某类型分级规则(含色标, ORDER BY sort_order)。ruleType ∈ ash/sulfur/qnet。</summary>
    public static List<CoalGradeRuleFull> CoalGradeRules(SqliteConnection conn, string ruleType)
        => conn.Query<CoalGradeRuleFull>("SELECT rule_type AS RuleType, level_code AS LevelCode, level_name AS LevelName, value_min AS ValueMin, value_max AS ValueMax, color_hex AS ColorHex FROM coal_grade_rule WHERE rule_type=@t ORDER BY sort_order", new { t = ruleType }).ToList();

    /// <summary>忠实原 FindLevel: [min,max) 首命中(min=null −∞, max=null +∞)。</summary>
    public static CoalGradeRuleFull? CoalFindLevel(IReadOnlyList<CoalGradeRuleFull> rules, double? value)
    {
        if (value is null) return null;
        foreach (var r in rules)
        {
            bool minOk = r.ValueMin is null || value.Value >= r.ValueMin.Value;
            bool maxOk = r.ValueMax is null || value.Value < r.ValueMax.Value;
            if (minOk && maxOk) return r;
        }
        return null;
    }

    /// <summary>煤层码 → 颜色(字典 color_hex, 缺/坏 hex 回退灰 #555555; 忠实原 SeamPalette.Fallback)。</summary>
    public static (byte r, byte g, byte b) CoalSeamColor(IReadOnlyList<CoalSeamDefRow> seams, string? code)
    {
        if (code != null)
            foreach (var s in seams) if (s.Code == code) return CoalHexToRgb(s.ColorHex, (0x55, 0x55, 0x55));
        return (0x55, 0x55, 0x55);
    }

    /// <summary>"#RRGGBB" → RGB; 解析失败回退。</summary>
    public static (byte r, byte g, byte b) CoalHexToRgb(string? hex, (byte r, byte g, byte b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.Length != 6) return fallback;
        try
        {
            return (Convert.ToByte(s.Substring(0, 2), 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { return fallback; }
    }

    // ═══════════════════════════ 钻孔(供编辑对话框/柱状图) ═══════════════════════════

    public sealed record CoalBoreholeRow(long Id, string HoleId, double X, double Y, double? ZCollar, double? DepthTotal, string CoordFilled);

    /// <summary>全部钻孔(Id/孔号/坐标/孔口高程/孔深), 按孔号。</summary>
    public static List<CoalBoreholeRow> CoalBoreholes(SqliteConnection conn)
        => conn.Query<CoalBoreholeRow>("SELECT id AS Id, hole_id AS HoleId, COALESCE(x,0.0) AS X, COALESCE(y,0.0) AS Y, z_collar AS ZCollar, depth_total AS DepthTotal, COALESCE(coord_filled,'原始') AS CoordFilled FROM borehole ORDER BY hole_id").ToList();

    /// <summary>化验孔(只列在 coal_sample 出现过的孔; 忠实原 IBoreholeService.WithCoalSamples)。</summary>
    public static List<CoalBoreholeRow> CoalBoreholesWithSamples(SqliteConnection conn)
        => conn.Query<CoalBoreholeRow>(@"SELECT b.id AS Id, b.hole_id AS HoleId, COALESCE(b.x,0.0) AS X, COALESCE(b.y,0.0) AS Y, b.z_collar AS ZCollar, b.depth_total AS DepthTotal, COALESCE(b.coord_filled,'原始') AS CoordFilled
            FROM borehole b WHERE EXISTS (SELECT 1 FROM coal_sample cs WHERE cs.borehole_id = b.id) ORDER BY b.hole_id").ToList();

    public sealed record CoalSeamResultRow(long BoreholeId, string SeamCode, double? LogEndDepth, double? OverallThickness, double? FloorElevation, string Status);

    /// <summary>全部见煤成果(柱状图煤层段用; 忠实原 SeamResultsByBorehole 的整表版)。</summary>
    public static List<CoalSeamResultRow> CoalSeamResults(SqliteConnection conn)
        => conn.Query<CoalSeamResultRow>("SELECT borehole_id AS BoreholeId, seam_code AS SeamCode, log_end_depth AS LogEndDepth, overall_thickness AS OverallThickness, floor_elevation AS FloorElevation, COALESCE(status,'正常') AS Status FROM borehole_seam_result ORDER BY borehole_id, log_end_depth").ToList();

    // ═══════════════════════════ 重算层平均(忠实原 RebuildSummary) ═══════════════════════════

    /// <summary>清空 coal_sample_summary 后按 (孔, 煤层) 重灌平均行; 返回写入行数。</summary>
    public static int CoalRebuildSummary(SqliteConnection conn)
    {
        var rows = CoalLoadSamples(conn);
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM coal_sample_summary", transaction: tx);
        int n = 0;
        foreach (var g in rows.GroupBy(r => (r.BoreholeId, r.SeamCode)))
        {
            var items = g.ToList();
            string? dom = items.Where(i => !string.IsNullOrEmpty(i.CoalType)).GroupBy(i => i.CoalType!)
                .OrderByDescending(grp => grp.Count()).FirstOrDefault()?.Key;
            conn.Execute(@"INSERT INTO coal_sample_summary (borehole_id, seam_code, sample_count, avg_thickness,
                    avg_mad_raw, avg_mad_clean, avg_ad_raw, avg_ad_clean, avg_vdaf_raw, avg_vdaf_clean, avg_fcd_raw, avg_fcd_clean,
                    avg_std_raw, avg_std_clean, avg_qgr_d, avg_qnet_ad, avg_caking_g, avg_plastic_y, avg_clean_yield,
                    dominant_coal_type, is_from_source, last_built_at)
                VALUES (@b, @s, @n, @th, @madR, @madC, @adR, @adC, @vR, @vC, @fR, @fC, @sR, @sC, @qgr, @qnet, @g, @y, @yield, @dom, 0, @at)",
                new
                {
                    b = g.Key.BoreholeId, s = g.Key.SeamCode, n = items.Count,
                    th = Avg(items.Select(i => i.SampleThickness)),
                    madR = Avg(items.Select(i => i.MadRaw)), madC = Avg(items.Select(i => i.MadClean)),
                    adR = Avg(items.Select(i => i.AdRaw)), adC = Avg(items.Select(i => i.AdClean)),
                    vR = Avg(items.Select(i => i.VdafRaw)), vC = Avg(items.Select(i => i.VdafClean)),
                    fR = Avg(items.Select(i => i.FcdRaw)), fC = Avg(items.Select(i => i.FcdClean)),
                    sR = Avg(items.Select(i => i.StdRaw)), sC = Avg(items.Select(i => i.StdClean)),
                    qgr = Avg(items.Select(i => i.QgrD)), qnet = Avg(items.Select(i => i.QnetAd)),
                    g = Avg(items.Select(i => i.CakingG)), y = Avg(items.Select(i => i.PlasticYMm)),
                    yield = Avg(items.Select(i => i.CleanCoalYield)), dom,
                    at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                }, tx);
            n++;
        }
        tx.Commit();
        return n;
    }

    private static double? Avg(IEnumerable<double?> src)
    {
        var xs = src.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return xs.Count == 0 ? null : xs.Average();
    }

    // ═══════════════════════════ CSV 导入/导出/模板(忠实原 CoalQualityExcelIo, Excel→CSV) ═══════════════════════════

    /// <summary>30 列标准表头(顺序即列序)。</summary>
    public static readonly string[] CoalCsvHeaders =
    {
        "孔号", "煤层", "采样起深", "采样止深", "采样厚度", "采样高程",
        "视密度", "真密度",
        "Mad_原", "Mad_浮", "Ad_原", "Ad_浮", "Vdaf_原", "Vdaf_浮", "FCd_原", "FCd_浮",
        "S_原", "S_浮", "Qgr_d", "Qnet_ad",
        "胶质层X_mm", "胶质层Y_mm", "曲线形状", "粘结指数G",
        "焦渣_原", "焦渣_浮", "浮煤回收率", "煤类", "来源页", "备注",
    };

    /// <summary>模板示例行(提示可删)。</summary>
    public static readonly string[] CoalCsvExample =
    {
        "1413", "4-2", "139.37", "141.87", "2.50", "1180.5",
        "1.38", "1.45",
        "3.98", "3.06", "12.21", "6.97", "39.61", "40.02", "", "",
        "1.22", "0.68", "33.24", "",
        "57.50", "8.00", "", "",
        "4", "", "", "1/2ZN", "", "示例行，可删除",
    };

    /// <summary>必填列(缺一即拒绝导入)。</summary>
    public static readonly string[] CoalCsvRequired = { "孔号", "煤层", "采样起深", "采样止深" };

    /// <summary>同键(孔号|煤层|采样起深)已存在时的处理策略。</summary>
    public enum CoalConflict { Skip, Overwrite, Abort }

    /// <summary>导入结果汇总(忠实原 ImportReport)。</summary>
    public sealed class CoalImportReport
    {
        public int Inserted, Overwritten, Skipped, SummaryRows;
        public readonly List<string> UnknownHoles = new();
        public string ToMessage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("导入完成。");
            sb.AppendLine($"新增 {Inserted} 条 / 覆盖 {Overwritten} 条 / 跳过 {Skipped} 条");
            if (UnknownHoles.Count > 0)
                sb.AppendLine($"⚠ {UnknownHoles.Count} 个孔号不在钻孔表（已跳过）：" + string.Join("、", UnknownHoles.Take(8)) + (UnknownHoles.Count > 8 ? " …" : ""));
            sb.Append($"自动重算 {SummaryRows} 条层平均。");
            return sb.ToString();
        }
    }

    /// <summary>把一行样品打平成与 <see cref="CoalCsvHeaders"/> 同序的 30 个单元格。</summary>
    public static string[] CoalRowCells(CoalSampleRow r) => new[]
    {
        r.HoleId, r.SeamCode,
        F3(r.DepthFrom), F3(r.DepthTo), F3(r.SampleThickness), F3(r.ZSample),
        F3(r.ApparentDensity), F3(r.TrueDensity),
        F3(r.MadRaw), F3(r.MadClean), F3(r.AdRaw), F3(r.AdClean),
        F3(r.VdafRaw), F3(r.VdafClean), F3(r.FcdRaw), F3(r.FcdClean),
        F3(r.StdRaw), F3(r.StdClean), F3(r.QgrD), F3(r.QnetAd),
        F3(r.PlasticXMm), F3(r.PlasticYMm), r.PlastometricCurve ?? "",
        F3(r.CakingG),
        I(r.CharResidueRaw), I(r.CharResidueClean),
        F3(r.CleanCoalYield), r.CoalType ?? "",
        I(r.SourcePage), r.Remark ?? "",
    };

    private static string F3(double? v) => v?.ToString("F3", CultureInfo.InvariantCulture) ?? "";
    private static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>数据 CSV 文本(表头 + 各行; 不含 BOM, 由保存方加)。导出即模板(闭环)。</summary>
    public static string CoalCsvText(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CoalCsvHeaders.Select(CsvField)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", Enumerable.Range(0, CoalCsvHeaders.Length).Select(c => CsvField(c < row.Length ? row[c] : ""))));
        return sb.ToString();
    }

    /// <summary>空模板 CSV(表头 + 示例行 + 以 # 开头的填写说明)。</summary>
    public static string CoalTemplateCsv()
    {
        var sb = new StringBuilder(CoalCsvText(new[] { CoalCsvExample }));
        foreach (var line in new[]
        {
            "煤质化验数据导入模板 — 填写说明",
            "1. 每行填写一个采样段；第 2 行为示例，导入前请删除。",
            "2. 必填列：孔号、煤层、采样起深、采样止深。其余列可留空。",
            "3. 孔号须与「钻孔数据」中已存在的孔号一致，否则该行按未知孔号跳过。",
            "4. 原煤/浮煤成对列：Mad_原/Mad_浮、Ad_原/Ad_浮 等；单值列如 Qgr_d、粘结指数G。",
            "5. 焦渣_原/焦渣_浮、来源页 为整数；其余数值列为小数。",
            "6. 去重键 = 孔号 + 煤层 + 采样起深；导入时按所选冲突策略跳过/覆盖。",
            "7. 保存为 .csv 即可导入；导出的数据也用同一套列，可直接作为再导入的模板。",
        }) sb.Append("# ").AppendLine(line);
        return sb.ToString();
    }

    private static string CsvField(string s)
        => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>读 CSV 文本 → 映射煤样 → 解析孔号 FK → 去重入库 → 重算层平均(忠实原 Import)。</summary>
    public static CoalImportReport CoalImportCsv(SqliteConnection conn, string text, CoalConflict policy)
    {
        var records = GeoDataQueries.ParseCsv(text);
        if (records.Count < 1) throw new InvalidOperationException("文件至少需 1 行表头 + 1 行数据。");
        var header = new HashSet<string>(records.SelectMany(r => r.Keys), StringComparer.OrdinalIgnoreCase);
        var missing = CoalCsvRequired.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("缺少必填列：" + string.Join("、", missing));

        var holeMap = CoalBoreholes(conn).GroupBy(b => b.HoleId).ToDictionary(g => g.Key, g => g.First().Id);
        var seamCodes = CoalSeamDefs(conn).Select(s => s.Code).ToHashSet();
        var existing = CoalLoadSamples(conn).Select(s => $"{s.BoreholeId}|{s.SeamCode}|{s.DepthFrom:F2}").ToHashSet();

        var rep = new CoalImportReport();
        foreach (var cells in records)
        {
            string Cell(string name) => cells.TryGetValue(name, out var v) ? v.Trim() : "";
            var holeId = Cell("孔号");
            if (holeId.Length == 0) { rep.Skipped++; continue; }
            if (!holeMap.TryGetValue(holeId, out var bhid))
            {
                if (!rep.UnknownHoles.Contains(holeId)) rep.UnknownHoles.Add(holeId);
                rep.Skipped++;
                continue;
            }
            var seam = CoalNormalizeSeam(Cell("煤层"), seamCodes);
            if (!TryD(Cell("采样起深"), out var df)) { rep.Skipped++; continue; }

            var key = $"{bhid}|{seam}|{df:F2}";
            if (existing.Contains(key))
            {
                if (policy == CoalConflict.Skip) { rep.Skipped++; continue; }
                if (policy == CoalConflict.Abort) throw new InvalidOperationException($"存在冲突记录：{holeId} / {seam} / {df:F2}（已中止）");
                var old = CoalSamplesByBorehole(conn, bhid).FirstOrDefault(s => s.SeamCode == seam && Math.Abs((s.DepthFrom ?? -999) - df) < 0.01);
                if (old != null) CoalDeleteSample(conn, old.Id);
                rep.Overwritten++;
            }
            else rep.Inserted++;

            CoalInsertSample(conn, new CoalSampleRow
            {
                BoreholeId = bhid, SeamCode = seam, DepthFrom = df,
                DepthTo = Num(Cell("采样止深")), SampleThickness = Num(Cell("采样厚度")),
                ZSample = Num(Cell("采样高程")) ?? Num(Cell("z_sample")),
                ApparentDensity = Num(Cell("视密度")), TrueDensity = Num(Cell("真密度")),
                MadRaw = Num(Cell("Mad_原")), MadClean = Num(Cell("Mad_浮")),
                AdRaw = Num(Cell("Ad_原")), AdClean = Num(Cell("Ad_浮")),
                VdafRaw = Num(Cell("Vdaf_原")), VdafClean = Num(Cell("Vdaf_浮")),
                FcdRaw = Num(Cell("FCd_原")), FcdClean = Num(Cell("FCd_浮")),
                StdRaw = Num(Cell("S_原")), StdClean = Num(Cell("S_浮")),
                QgrD = Num(Cell("Qgr_d")), QnetAd = Num(Cell("Qnet_ad")),
                PlasticXMm = Num(Cell("胶质层X_mm")), PlasticYMm = Num(Cell("胶质层Y_mm")),
                PlastometricCurve = NullIfEmpty(Cell("曲线形状")),
                CakingG = Num(Cell("粘结指数G")),
                CharResidueRaw = Int(Cell("焦渣_原")), CharResidueClean = Int(Cell("焦渣_浮")),
                CleanCoalYield = Num(Cell("浮煤回收率")),
                CoalType = NullIfEmpty(Cell("煤类")),
                SourcePage = Int(Cell("来源页")),
                Remark = NullIfEmpty(Cell("备注")),
            });
            existing.Add(key);
        }
        rep.SummaryRows = CoalRebuildSummary(conn);
        return rep;
    }

    /// <summary>煤层归一化(忠实原 NormalizeSeam 意图): 字典里没有的 4 系分层(4-1/4-2/4(4-1))并入「4」, 其余原样。</summary>
    public static string CoalNormalizeSeam(string seam, ISet<string> knownCodes)
    {
        var s = seam.Trim();
        if (knownCodes.Contains(s)) return s;
        if (s == "4" || s.StartsWith("4-") || s.StartsWith("4(") || s.StartsWith("4（")) return "4";
        return s;
    }

    private static bool TryD(string s, out double v)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) || double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v);
    private static double? Num(string s) => string.IsNullOrWhiteSpace(s) ? null : (TryD(s, out var v) ? v : null);
    private static int? Int(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, out var i)) return i;
        return TryD(s, out var d) ? (int)d : null;
    }
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ═══════════════════════════ 指标取值/命名 ═══════════════════════════

    /// <summary>列名 → 取值器(ad_raw/ad_clean/vdaf_*/std_*/qnet_ad/qgr_d/caking_g/mad_*/fcd_*)。</summary>
    public static Func<CoalSampleRow, double?> CoalGetter(string col) => col switch
    {
        "ad_raw" => s => s.AdRaw, "ad_clean" => s => s.AdClean,
        "vdaf_raw" => s => s.VdafRaw, "vdaf_clean" => s => s.VdafClean,
        "std_raw" => s => s.StdRaw, "std_clean" => s => s.StdClean,
        "qnet_ad" => s => s.QnetAd, "qgr_d" => s => s.QgrD, "caking_g" => s => s.CakingG,
        "mad_raw" => s => s.MadRaw, "mad_clean" => s => s.MadClean,
        "fcd_raw" => s => s.FcdRaw, "fcd_clean" => s => s.FcdClean,
        "plastic_y_mm" => s => s.PlasticYMm,
        _ => s => null,
    };

    /// <summary>指标 + 原/浮 → 实际列(忠实原 MapColumn: qnet/caking 无原浮区分)。</summary>
    public static string CoalMapColumn(string indicator, bool useClean) => indicator switch
    {
        "ad_raw" => useClean ? "ad_clean" : "ad_raw",
        "vdaf_raw" => useClean ? "vdaf_clean" : "vdaf_raw",
        "std_raw" => useClean ? "std_clean" : "std_raw",
        _ => indicator,
    };

    public static string CoalIndicatorName(string indicator) => indicator switch
    {
        "ad_raw" => "Ad 灰分", "vdaf_raw" => "Vdaf 挥发分", "std_raw" => "S 全硫",
        "qnet_ad" => "Qnet 低位发热", "qgr_d" => "Qgr 弹筒发热", "caking_g" => "G 粘结指数", _ => indicator,
    };

    public static string CoalIndicatorUnit(string indicator) => indicator switch
    {
        "qnet_ad" or "qgr_d" => "MJ/kg", "caking_g" => "", _ => "%",
    };

    /// <summary>指标 → GB 分级规则类别(空间分布窗口径: 无对应者按 ash)。</summary>
    public static string CoalRuleForSpatial(string indicator) => indicator switch
    {
        "ad_raw" => "ash", "std_raw" => "sulfur", "qnet_ad" => "qnet", _ => "ash",
    };

    /// <summary>指标 → GB 分级规则类别(统计窗口径: 无对应者 null)。</summary>
    public static string? CoalRuleForStats(string indicator) => indicator switch
    {
        "ad_raw" => "ash", "std_raw" => "sulfur", "qnet_ad" => "qnet", _ => null,
    };

    // ═══════════════════════════ 统计(忠实原 StatsBySeam / 直方 / 相关 / 分位) ═══════════════════════════

    public sealed record CoalSeamStat(string SeamCode, string Indicator, int Count, double Mean, double Std,
        double Min, double Max, double P25, double P50, double P75)
    {
        /// <summary>样本量评级(忠实原 StatsRow.SampleSizeLevel: ≥50 充分 / ≥20 紧张 / 不足)。</summary>
        public string SampleSizeLevel => Count >= 50 ? "🟢 充分" : Count >= 20 ? "🟡 紧张" : "🔴 不足";
    }

    /// <summary>按煤层算某列的 n/均值/样本σ/极值/分位(忠实原 CoalQualityService.StatsBySeam 单指标), 按煤层码升序。</summary>
    public static List<CoalSeamStat> CoalStatsBySeam(IReadOnlyList<CoalSampleRow> rows, string column)
    {
        var get = CoalGetter(column);
        var res = new List<CoalSeamStat>();
        foreach (var g in rows.Where(r => get(r).HasValue).GroupBy(r => r.SeamCode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var q = QualityStatistics.Compute(g.Select(r => get(r)!.Value).ToList());
            res.Add(new CoalSeamStat(g.Key, column, q.Count, q.Mean, q.Std, q.Min, q.Max, q.P25, q.P50, q.P75));
        }
        return res;
    }

    /// <summary>分组统计 → CSV(忠实原 OnExportClick 列)。</summary>
    public static string CoalStatsCsv(IReadOnlyList<CoalSeamStat> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("煤层,样本,均值,标准差,Min,P25,P50,P75,Max,评级");
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in rows)
            sb.AppendLine($"{r.SeamCode},{r.Count},{r.Mean.ToString("F3", inv)},{r.Std.ToString("F3", inv)},{r.Min.ToString("F3", inv)},{r.P25.ToString("F3", inv)},{r.P50.ToString("F3", inv)},{r.P75.ToString("F3", inv)},{r.Max.ToString("F3", inv)},{r.SampleSizeLevel}");
        return sb.ToString();
    }

    /// <summary>直方分箱(忠实原 DrawHistogram): 箱数 = clamp(n/8, 5, 15); 标签为箱下界 F1。空集返回空。</summary>
    public static (string[] labels, int[] bins) CoalHistogram(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (Array.Empty<string>(), Array.Empty<int>());
        double min = values.Min(), max = values.Max();
        int binCount = Math.Min(15, Math.Max(5, values.Count / 8));
        double width = (max - min) / binCount;
        if (width < 1e-9) width = 1;
        var bins = new int[binCount];
        var labels = new string[binCount];
        for (int i = 0; i < binCount; i++) labels[i] = (min + i * width).ToString("F1", CultureInfo.InvariantCulture);
        foreach (var v in values)
        {
            int idx = (int)((v - min) / width);
            if (idx >= binCount) idx = binCount - 1;
            if (idx < 0) idx = 0;
            bins[idx]++;
        }
        return (labels, bins);
    }

    /// <summary>线性插值分位数(忠实原 Pctl; sorted 须升序, p∈[0,100])。</summary>
    public static double CoalPctl(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Pearson 相关(n&lt;3 → null; 忠实原 Pearson)。</summary>
    public static double? CoalPearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        int n = xs.Count;
        if (n < 3) return null;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sx += dx * dx; sy += dy * dy; }
        double d = Math.Sqrt(sx * sy);
        return d < 1e-12 ? null : Math.Max(-1, Math.Min(1, sxy / d));
    }

    /// <summary>指标相关矩阵名(忠实原 BuildCorrelation 六指标)。</summary>
    public static readonly string[] CoalCorrNames = { "Ad", "Vdaf", "St", "Qnet", "G", "Mad" };

    /// <summary>六指标两两 Pearson 矩阵(对角 1; 成对缺失剔除; n&lt;3 → null)。</summary>
    public static double?[,] CoalCorrelationMatrix(IReadOnlyList<CoalSampleRow> rows, bool useClean)
    {
        var gets = new Func<CoalSampleRow, double?>[]
        {
            s => useClean ? s.AdClean : s.AdRaw, s => useClean ? s.VdafClean : s.VdafRaw, s => useClean ? s.StdClean : s.StdRaw,
            s => s.QnetAd, s => s.CakingG, s => useClean ? s.MadClean : s.MadRaw,
        };
        int n = gets.Length;
        var data = rows.Select(s => gets.Select(g => g(s)).ToArray()).ToList();
        var m = new double?[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (i == j) { m[i, j] = 1.0; continue; }
                var xs = new List<double>(); var ys = new List<double>();
                foreach (var d in data) if (d[i].HasValue && d[j].HasValue) { xs.Add(d[i]!.Value); ys.Add(d[j]!.Value); }
                m[i, j] = CoalPearson(xs, ys);
            }
        return m;
    }

    /// <summary>Tukey IQR 离群(忠实原 DetectOutliers, 支持全部指标列含 caking_g)。n&lt;5 不检测。</summary>
    public static CoalAnalytics.OutlierResult CoalDetectOutliers(IReadOnlyList<CoalSampleRow> rows, string column, string? seam)
    {
        var get = CoalGetter(column);
        var sel = rows.Where(r => seam is null or "全部" || r.SeamCode == seam)
                      .Select(r => (r, v: get(r))).Where(t => t.v.HasValue).Select(t => (t.r, val: t.v!.Value)).ToList();
        if (sel.Count < 5) return new CoalAnalytics.OutlierResult(column, sel.Count, 0, 0, 0, 0, 0, new List<CoalAnalytics.OutlierRow>());
        var vals = sel.Select(t => t.val).OrderBy(v => v).ToList();
        double q1 = CoalPctl(vals, 25), q3 = CoalPctl(vals, 75), med = CoalPctl(vals, 50);
        double iqr = q3 - q1, lo = q1 - 1.5 * iqr, hi = q3 + 1.5 * iqr;
        var outs = new List<CoalAnalytics.OutlierRow>();
        foreach (var (r, val) in sel)
        {
            if (val >= lo && val <= hi) continue;
            double sev = iqr > 1e-9 ? (val < lo ? (lo - val) : (val - hi)) / iqr : 0;
            outs.Add(new CoalAnalytics.OutlierRow(r.Id, r.HoleId, r.SeamCode, val, r.ZSample, val < lo ? "偏低" : "偏高", sev));
        }
        outs.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new CoalAnalytics.OutlierResult(column, sel.Count, q1, q3, lo, hi, med, outs);
    }

    /// <summary>KPI 概览(忠实原 BuildKpiStrip): n/均值/样本σ/CV%/极差。</summary>
    public sealed record CoalKpi(int N, double Mean, double Std, double CvPct, double Min, double Max);
    public static CoalKpi? CoalKpiOf(IReadOnlyList<double> vals)
    {
        if (vals.Count == 0) return null;
        double mean = vals.Average();
        double sd = vals.Count > 1 ? Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / (vals.Count - 1)) : 0;
        double cv = Math.Abs(mean) > 1e-9 ? sd / mean * 100 : 0;
        return new CoalKpi(vals.Count, mean, sd, cv, vals.Min(), vals.Max());
    }

    /// <summary>GB 分级分布计数(按规则序; 忠实原 GradeBar): (级名, 色 hex, 段数), 仅含非零级。</summary>
    public static List<(string level, string? colorHex, int count)> CoalGradeCounts(IReadOnlyList<CoalGradeRuleFull> rules, IEnumerable<double> vals)
    {
        var counts = new Dictionary<string, int>();
        foreach (var v in vals)
        {
            var r = CoalFindLevel(rules, v);
            if (r != null) counts[r.LevelCode] = counts.GetValueOrDefault(r.LevelCode) + 1;
        }
        var res = new List<(string, string?, int)>();
        foreach (var r in rules)
        {
            int c = counts.GetValueOrDefault(r.LevelCode);
            if (c > 0) res.Add((r.LevelName, r.ColorHex, c));
        }
        return res;
    }

    // ═══════════════════════════ 看板(忠实原 CoalQualityDashboardWindow 纯算) ═══════════════════════════

    /// <summary>煤类构成(按样本数降序, 末尾追加"(未标注)")。</summary>
    public static List<(string code, int count)> CoalTypeCounts(IReadOnlyList<CoalSampleRow> rows)
    {
        var counter = rows.Where(s => !string.IsNullOrEmpty(s.CoalType)).GroupBy(s => s.CoalType!)
            .Select(g => (code: g.Key, count: g.Count())).OrderByDescending(x => x.count).ToList();
        int unknown = rows.Count - counter.Sum(c => c.count);
        if (unknown > 0) counter.Add(("(未标注)", unknown));
        return counter;
    }

    /// <summary>各煤层 GB 分级构成(堆叠柱; 忠实原 DrawGradeDistribution): 每级一行 counts[seam], 全零级跳过。</summary>
    public static List<(string level, string? colorHex, int[] counts)> CoalGradeDistribution(
        IReadOnlyList<CoalSampleRow> rows, IReadOnlyList<string> seams, IReadOnlyList<CoalGradeRuleFull> rules, Func<CoalSampleRow, double?> get)
    {
        var res = new List<(string, string?, int[])>();
        foreach (var rule in rules)
        {
            var vals = seams.Select(seam => rows.Count(s => s.SeamCode == seam && get(s) is { } v && CoalFindLevel(rules, v)?.LevelCode == rule.LevelCode)).ToArray();
            if (vals.Sum() == 0) continue;
            res.Add((rule.LevelName, rule.ColorHex, vals));
        }
        return res;
    }

    /// <summary>粘结指数分档(忠实原 kpiGSub): &gt;65 强粘结 / &gt;35 中粘结 / 弱粘结。</summary>
    public static string CoalCakingWord(double g) => g > 65 ? "强粘结" : g > 35 ? "中粘结" : "弱粘结";

    // ═══════════════════════════ 空间分布(忠实原 CoalQualitySpatialWindow + CoalQualityEstimator) ═══════════════════════════

    /// <summary>可选插值方法(忠实原 CoalQualityEstimator.AvailableMethods)。</summary>
    public static readonly string[] CoalInterpMethods = { "普通克里金 OK", "反距离加权 IDW", "最近邻 NN", "移动平均 MA" };

    public sealed record CoalVoxel(double X, double Y, double Z, double Value, double? Variance);

    /// <summary>
    /// 规则网格插值(忠实原 Interpolate): 逐体素取最近邻集合, 最近点超搜索半径(2.5×平均点距)不外插; 落控制点上精确。
    /// OK 走 <see cref="OrdinaryKriging.EstimateAt"/>(变差函数整场拟合一次); IDW 1/dᵖ; NN 最近值; MA k 邻等权。
    /// 体素数超 maxVoxels 抛异常(提示调大分辨率)。
    /// </summary>
    public static List<CoalVoxel> CoalInterpolate(IReadOnlyList<OrdinaryKriging.ControlPoint> control, string method,
        double resolution, double p = 2, int k = 5, int maxVoxels = 400_000)
    {
        var result = new List<CoalVoxel>();
        if (control.Count == 0 || resolution <= 0) return result;
        double xn = control.Min(c => c.X), xx = control.Max(c => c.X), yn = control.Min(c => c.Y), yx = control.Max(c => c.Y);
        double zn = control.Min(c => c.Z), zx = control.Max(c => c.Z);
        long nx = (long)((xx - xn) / resolution) + 1, ny = (long)((yx - yn) / resolution) + 1, nz = (long)((zx - zn) / resolution) + 1;
        if (nx * ny * nz > maxVoxels) throw new InvalidOperationException($"体素数 {nx * ny * nz} 过多，请调大网格分辨率。");

        k = Math.Max(1, k);
        double spacing = Math.Sqrt(Math.Max((xx - xn) * (yx - yn), 1) / Math.Max(control.Count, 1));
        double radius = spacing * 2.5;
        string kind = method.Contains("OK") || method.Contains("克里金") ? "OK"
                    : method.Contains("NN") || method.Contains("最近邻") ? "NN"
                    : method.Contains("MA") || method.Contains("移动平均") ? "MA" : "IDW";
        OrdinaryKriging.Variogram? vg = kind == "OK" ? OrdinaryKriging.FitVariogram(control) : null;

        var neigh = new List<(double d, double v)>(control.Count);
        for (double x = xn; x <= xx + 1e-6; x += resolution)
        for (double y = yn; y <= yx + 1e-6; y += resolution)
        for (double z = zn; z <= zx + 1e-6; z += resolution)
        {
            if (kind == "OK")
            {
                var e = OrdinaryKriging.EstimateAt(control, x, y, z, k, radius, vg);
                if (e != null) result.Add(new CoalVoxel(x, y, z, e.Value.est, e.Value.variance));
                continue;
            }
            neigh.Clear();
            foreach (var pt in control)
            {
                double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
                neigh.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt.V));
            }
            neigh.Sort((a, b) => a.d.CompareTo(b.d));
            if (neigh[0].d > radius) continue;                       // 无数据支撑
            if (neigh[0].d < 1e-6) { result.Add(new CoalVoxel(x, y, z, neigh[0].v, null)); continue; }
            int take = Math.Min(k, neigh.Count);
            double est;
            if (kind == "NN") est = neigh[0].v;
            else if (kind == "MA") { double s = 0; for (int i = 0; i < take; i++) s += neigh[i].v; est = s / take; }
            else
            {
                double sw = 0, s = 0;
                for (int i = 0; i < take; i++) { double w = 1.0 / Math.Pow(neigh[i].d, p); sw += w; s += w * neigh[i].v; }
                est = s / sw;
            }
            result.Add(new CoalVoxel(x, y, z, est, null));
        }
        return result;
    }

    /// <summary>连续色带(忠实原 Colormap): t∈[0,1] → RGB(warm 冷暖 / spectral 光谱)。</summary>
    public static (byte r, byte g, byte b) CoalColormap(string mode, double t)
    {
        (byte, byte, byte)[] stops = mode == "spectral"
            ? new[] { ((byte)0x44, (byte)0x01, (byte)0x54), ((byte)0x31, (byte)0x68, (byte)0x8E), ((byte)0x35, (byte)0xB7, (byte)0x79), ((byte)0xFD, (byte)0xE7, (byte)0x25) }
            : new[] { ((byte)0x31, (byte)0x6D, (byte)0xC2), ((byte)0x4B, (byte)0xB6, (byte)0xC4), ((byte)0xF2, (byte)0xE6, (byte)0x3F), ((byte)0xD8, (byte)0x3A, (byte)0x2E) };
        t = Math.Max(0, Math.Min(1, t));
        double x = t * (stops.Length - 1);
        int i = (int)Math.Floor(x);
        if (i >= stops.Length - 1) return stops[^1];
        double f = x - i;
        var a = stops[i]; var b = stops[i + 1];
        return ((byte)(a.Item1 + (b.Item1 - a.Item1) * f), (byte)(a.Item2 + (b.Item2 - a.Item2) * f), (byte)(a.Item3 + (b.Item3 - a.Item3) * f));
    }

    /// <summary>化验区统计(忠实原 UpdateCoverageInfo)。</summary>
    public sealed record CoalCoverage(int N, int Holes, double AreaKm2, double AvgDistM, double ZSpanM, string Rating)
    {
        public string Text => $"控制点 {N}  ({Holes} 孔)\n面积 ≈ {AreaKm2:F2} km²\n平均点距 ≈ {AvgDistM:F0} m\nZ 跨度 {ZSpanM:F0} m\n评级 {Rating}";
    }

    public static CoalCoverage? CoalCoverageOf(IReadOnlyList<CoalSampleRow> pts)
    {
        if (pts.Count == 0) return null;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double minZ = pts.Min(p => p.ZSample ?? 0), maxZ = pts.Max(p => p.ZSample ?? 0);
        double areaKm2 = (maxX - minX) * (maxY - minY) / 1e6;
        int holes = pts.Select(p => p.BoreholeId).Distinct().Count();
        double avgDist = areaKm2 > 0 ? Math.Sqrt(areaKm2 * 1e6 / pts.Count) : 0;
        string rating = pts.Count switch { >= 100 => "🟢 充分", >= 50 => "🟢 可用", >= 30 => "🟡 紧张", _ => "🔴 不足" };
        return new CoalCoverage(pts.Count, holes, areaKm2, avgDist, maxZ - minZ, rating);
    }

    /// <summary>空间分布结论(忠实原 BuildSpatialConclusion): 值域 + 平面主趋势 + 垂向趋势 + 控制程度。</summary>
    public static string CoalSpatialConclusion(IReadOnlyList<OrdinaryKriging.ControlPoint> control, string indicator)
    {
        if (control.Count < 5) return $"控制点仅 {control.Count} 个，不足以判空间趋势。";
        var v = control.Select(c => c.V).ToList();
        double mean = v.Average(), min = v.Min(), max = v.Max();
        double rx = Pear(control.Select(c => c.X).ToList(), v);
        double ry = Pear(control.Select(c => c.Y).ToList(), v);
        double rz = Pear(control.Select(c => c.Z).ToList(), v);
        string plane;
        if (Math.Abs(rx) < 0.2 && Math.Abs(ry) < 0.2) plane = "平面无明显方向趋势";
        else if (Math.Abs(rx) >= Math.Abs(ry)) plane = (rx > 0 ? "平面自西向东递增" : "平面自东向西递增") + $"(r={rx:F2})";
        else plane = (ry > 0 ? "平面自南向北递增" : "平面自北向南递增") + $"(r={ry:F2})";
        string vert = Math.Abs(rz) < 0.2 ? "垂向无明显趋势" : (rz > 0 ? "随高程增高(浅部)增大" : "随深度增大") + $"(r={rz:F2})";
        string unit = indicator switch { "qnet_ad" => " MJ/kg", "caking_g" => "", _ => "%" };
        string name = indicator switch
        {
            "ad_raw" => "Ad 灰分", "vdaf_raw" => "Vdaf 挥发分", "std_raw" => "S 全硫",
            "qnet_ad" => "Qnet 发热量", "caking_g" => "G 粘结指数", _ => indicator,
        };
        return $"{name}：均值 {mean:F1}{unit}（{min:F1}~{max:F1}）；{plane}；{vert}。控制点 {control.Count} 个。";
    }

    private static double Pear(List<double> xs, List<double> ys)
    {
        int n = xs.Count;
        if (n < 3) return 0;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sx += dx * dx; sy += dy * dy; }
        double d = Math.Sqrt(sx * sy);
        return d < 1e-12 ? 0 : sxy / d;
    }

    /// <summary>置信度淡出系数(忠实原 RenderField): 有克里金方差→1−var/varMax(≥0.15); 否则按离最近实测点距离(≥0.12)。</summary>
    public static double CoalConfidenceKeep(double? variance, double varMax, double nearestDist, double resolution)
    {
        if (variance is double vv && varMax > 1e-9) return Math.Max(0.15, 1.0 - vv / varMax);
        double fadeSpan = resolution * 6;
        return Math.Max(0.12, Math.Min(1.0, 1.0 - (nearestDist - resolution) / fadeSpan));
    }

    // ═══════════════════════════ 钻孔柱状图(忠实原 CoalQualityBoreholeColumnWindow 纯算) ═══════════════════════════

    /// <summary>逐孔煤质小结(忠实原 BuildHoleSummary): 见煤层数/主采层/全孔均质(灰硫档)/灰分纵向趋势/煤类。</summary>
    public static string CoalHoleSummary(IReadOnlyList<CoalSampleRow> samples, IReadOnlyList<CoalGradeRuleFull> ashRules, IReadOnlyList<CoalGradeRuleFull> sulfurRules)
    {
        if (samples == null || samples.Count == 0) return "该孔无化验段。";
        var bySeam = samples.GroupBy(s => s.SeamCode).ToList();
        var main = bySeam.OrderByDescending(g => g.Sum(s => s.SampleThickness ?? 0)).First();
        double? mainAd = Avg(main.Select(s => s.AdRaw));
        double? ad = Avg(samples.Select(s => s.AdRaw));
        double? st = Avg(samples.Select(s => s.StdRaw));
        string adLvl = CoalFindLevel(ashRules, ad)?.LevelName ?? "";
        string stLvl = CoalFindLevel(sulfurRules, st)?.LevelName ?? "";
        var zt = samples.Where(s => s.AdRaw.HasValue && s.ZSample.HasValue).OrderByDescending(s => s.ZSample!.Value).ToList();
        string trend = "";
        if (zt.Count >= 4)
        {
            int k = Math.Max(1, zt.Count / 3);
            double shallow = zt.Take(k).Average(s => s.AdRaw!.Value);
            double deep = zt.Skip(zt.Count - k).Average(s => s.AdRaw!.Value);
            double d = deep - shallow;
            trend = Math.Abs(d) < 3 ? "，灰分纵向较稳定" : d > 0 ? "，灰分向深部增高" : "，灰分向浅部增高";
        }
        var types = samples.Where(s => s.CoalType != null).Select(s => s.CoalType!).Distinct().ToList();
        string typeStr = types.Count > 0 ? string.Join("/", types) : "未标注";
        return $"见煤 {bySeam.Count} 层、{samples.Count} 化验段；主采 {main.Key}煤（Ad均 {mainAd?.ToString("F1") ?? "—"}%）；"
             + $"全孔均 Ad {ad?.ToString("F1") ?? "—"}%（{adLvl}）、St {st?.ToString("F2") ?? "—"}%（{stLvl}）{trend}；煤类 {typeStr}。";
    }

    /// <summary>煤层段底高程(忠实原: 优先权威底板标高 floor_elevation, 缺则 孔口高程−测井止煤深度; 皆缺 null)。</summary>
    public static double? CoalSeamSegmentFloor(CoalSeamResultRow sr, double topZ)
        => sr.FloorElevation ?? (sr.LogEndDepth is null ? (double?)null : topZ - sr.LogEndDepth.Value);
}
