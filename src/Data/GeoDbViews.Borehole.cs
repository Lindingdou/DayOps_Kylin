using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 「钻孔管理」组(导入钻孔数据 / 展绘钻孔 / 虚拟钻孔 / 开孔坐标管理 / 原始钻孔柱状图 / 展绘层位数据)的
/// 查询、写库与纯逻辑 —— 逐字移植原 GeoDataBase 的 BoreholeService / CoalSeamService / BoreholeImportIo /
/// BoreholeColumnBuilder / HorizonPointBuilder / VirtualDrillService / VirtualDrillEngine / VirtualDrillGeometry /
/// VirtualDrillCapture。窗口层只做绑定与交互。
/// </summary>
public static partial class GeoDbViews
{
    // ═════════════════════════════════════════════════════════════════════
    //  钻孔(borehole)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>钻孔行(原 Borehole 实体; 可编辑表格用, 数值列另提供文本包装属性供 DataGrid 编辑)。</summary>
    public sealed class BoreholeRow : INotifyPropertyChanged
    {
        private string _holeId = "";
        private double _x, _y;
        private double? _zCollar, _depthTotal;
        private string? _terminateHorizon, _drillDate, _drillUnit, _drillRating, _logRating, _overallRating, _category, _remark;
        private string _coordFilled = "原始";

        public long Id { get; set; }
        public string HoleId { get => _holeId; set { _holeId = value ?? ""; OnChanged(); } }
        public double X { get => _x; set { _x = value; OnChanged(); OnChanged(nameof(XStr)); } }
        public double Y { get => _y; set { _y = value; OnChanged(); OnChanged(nameof(YStr)); } }
        public double? ZCollar { get => _zCollar; set { _zCollar = value; OnChanged(); OnChanged(nameof(ZCollarStr)); } }
        public double? DepthTotal { get => _depthTotal; set { _depthTotal = value; OnChanged(); OnChanged(nameof(DepthTotalStr)); } }
        public string? TerminateHorizon { get => _terminateHorizon; set { _terminateHorizon = value; OnChanged(); } }
        public string? DrillDate { get => _drillDate; set { _drillDate = value; OnChanged(); } }
        public string? DrillUnit { get => _drillUnit; set { _drillUnit = value; OnChanged(); } }
        public string? DrillRating { get => _drillRating; set { _drillRating = value; OnChanged(); } }
        public string? LogRating { get => _logRating; set { _logRating = value; OnChanged(); } }
        public string? OverallRating { get => _overallRating; set { _overallRating = value; OnChanged(); } }
        public string? Category { get => _category; set { _category = value; OnChanged(); } }
        public string CoordFilled { get => _coordFilled; set { _coordFilled = value ?? "原始"; OnChanged(); } }
        public string? Remark { get => _remark; set { _remark = value; OnChanged(); } }

        // ── 数值列的文本包装(原 WPF 用 StringFormat=F2 的 TwoWay 绑定; Avalonia 编辑列走文本 ↔ 数值) ──
        public string XStr { get => _x.ToString("F2", CultureInfo.InvariantCulture); set { if (BoreholeParseNum(value) is { } v) X = v; } }
        public string YStr { get => _y.ToString("F2", CultureInfo.InvariantCulture); set { if (BoreholeParseNum(value) is { } v) Y = v; } }
        public string ZCollarStr { get => _zCollar?.ToString("F2", CultureInfo.InvariantCulture) ?? ""; set { ZCollar = string.IsNullOrWhiteSpace(value) ? null : BoreholeParseNum(value) ?? _zCollar; } }
        public string DepthTotalStr { get => _depthTotal?.ToString("F2", CultureInfo.InvariantCulture) ?? ""; set { DepthTotal = string.IsNullOrWhiteSpace(value) ? null : BoreholeParseNum(value) ?? _depthTotal; } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private const string BoreholeSelect = @"SELECT id AS Id, hole_id AS HoleId, x AS X, y AS Y, z_collar AS ZCollar, depth_total AS DepthTotal,
        terminate_horizon AS TerminateHorizon, drill_date AS DrillDate, drill_unit AS DrillUnit, drill_rating AS DrillRating,
        log_rating AS LogRating, overall_rating AS OverallRating, category AS Category, coord_filled AS CoordFilled, remark AS Remark
        FROM borehole";

    /// <summary>全部钻孔(原 IBoreholeService.All, 按孔号不区分大小写排序)。</summary>
    public static List<BoreholeRow> LoadBoreholes(SqliteConnection conn)
        => conn.Query<BoreholeRow>(BoreholeSelect).OrderBy(b => b.HoleId, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>按孔号取一孔(原 GetByHoleId); 无则 null。</summary>
    public static BoreholeRow? GetBoreholeByHoleId(SqliteConnection conn, string holeId)
        => conn.Query<BoreholeRow>(BoreholeSelect + " WHERE hole_id = @h", new { h = holeId }).FirstOrDefault();

    /// <summary>新增钻孔(原 Insert), 返回自增 id。</summary>
    public static long InsertBorehole(SqliteConnection conn, BoreholeRow b)
    {
        conn.Execute(@"INSERT INTO borehole(hole_id, x, y, z_collar, depth_total, terminate_horizon, drill_date, drill_unit,
                       drill_rating, log_rating, overall_rating, category, coord_filled, remark)
                       VALUES(@HoleId, @X, @Y, @ZCollar, @DepthTotal, @TerminateHorizon, @DrillDate, @DrillUnit,
                       @DrillRating, @LogRating, @OverallRating, @Category, @CoordFilled, @Remark)", b);
        b.Id = conn.ExecuteScalar<long>("SELECT last_insert_rowid()");
        return b.Id;
    }

    /// <summary>更新钻孔全部可编辑列(原 Update)。</summary>
    public static int UpdateBorehole(SqliteConnection conn, BoreholeRow b)
        => conn.Execute(@"UPDATE borehole SET hole_id=@HoleId, x=@X, y=@Y, z_collar=@ZCollar, depth_total=@DepthTotal,
                          terminate_horizon=@TerminateHorizon, drill_date=@DrillDate, drill_unit=@DrillUnit, drill_rating=@DrillRating,
                          log_rating=@LogRating, overall_rating=@OverallRating, category=@Category, coord_filled=@CoordFilled, remark=@Remark
                          WHERE id=@Id", b);

    /// <summary>删除钻孔(原 Delete; 见煤成果随外键级联)。</summary>
    public static int DeleteBorehole(SqliteConnection conn, long id)
        => conn.Execute("DELETE FROM borehole WHERE id=@id", new { id });

    // ═════════════════════════════════════════════════════════════════════
    //  钻孔 CSV 导入 / 模板(原 BoreholeImportIo; Excel 分支改为 CSV)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>模板 / 导出的标准表头(顺序即模板列序)。</summary>
    public static readonly string[] BoreholeImportHeaders =
        { "孔号", "经距X", "纬距Y", "孔口高程", "总孔深", "终孔层位", "施工时间", "施工单位", "类别", "备注" };

    /// <summary>模板示例行(提示可删)。</summary>
    public static readonly string[] BoreholeImportExample =
        { "ZK001", "37500000.0", "4400000.0", "1285.6", "320.5", "O", "2014-06", "某某地勘院", "2014补勘", "示例行，可删除" };

    /// <summary>必填列的规范化键。</summary>
    public static readonly string[] BoreholeRequiredKeys = { "hole", "x", "y" };

    /// <summary>表头别名 → 规范化键(大小写/BOM/常见中英文别名兼容)。</summary>
    public static string? BoreholeCanon(string h)
    {
        h = h.Trim().Trim('﻿').ToLowerInvariant();
        return h switch
        {
            "孔号" or "hole_id" or "holeid" or "钻孔编号" or "钻孔号" => "hole",
            "经距x" or "经距" or "x" => "x",
            "纬距y" or "纬距" or "y" => "y",
            "孔口高程" or "z_collar" or "z" or "高程" => "z",
            "总孔深" or "depth_total" or "孔深" or "depth" => "depth",
            "终孔层位" or "terminate_horizon" => "horizon",
            "施工时间" or "drill_date" or "施工日期" => "date",
            "施工单位" or "drill_unit" => "unit",
            "类别" or "category" => "cat",
            "备注" or "remark" => "remark",
            _ => null,
        };
    }

    /// <summary>由表头行构建「规范化键 → 列序号」映射。</summary>
    public static Dictionary<string, int> BoreholeBuildHeaderMap(List<string> header)
    {
        var map = new Dictionary<string, int>();
        for (int i = 0; i < header.Count; i++)
        {
            var key = BoreholeCanon(header[i]);
            if (key != null && !map.ContainsKey(key)) map[key] = i;
        }
        return map;
    }

    /// <summary>缺一必填列即返回 false。</summary>
    public static bool BoreholeHasRequired(Dictionary<string, int> map) => BoreholeRequiredKeys.All(map.ContainsKey);

    /// <summary>按规范化键取字段值(缺列 / 越界返回空串)。</summary>
    public static string BoreholeField(List<string> f, Dictionary<string, int> map, string key)
        => map.TryGetValue(key, out var i) && i < f.Count ? f[i].Trim() : "";

    public static double? BoreholeParseNum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return v;
        return null;
    }

    /// <summary>读 CSV 文本为「记录 → 字段」序列(去全空行)。原 ReadRecords 的 CSV 分支。</summary>
    public static List<List<string>> BoreholeReadCsvRecords(string text)
        => BoreholeParseRecords(text).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();

    /// <summary>CSV 导入模板文本(表头 + 示例行; BOM 由 ctx.SaveTextAsync 写出时加)。原 WriteCsvTemplate。</summary>
    public static string BoreholeCsvTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", BoreholeImportHeaders.Select(BoreholeCsvField)));
        sb.AppendLine(string.Join(",", BoreholeImportExample.Select(BoreholeCsvField)));
        return sb.ToString();
    }

    public static string BoreholeCsvField(string? s)
    {
        s ??= "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>把整份 CSV 文本解析为「记录 → 字段」: 引号内逗号/换行按内容, "" 为转义引号。</summary>
    private static List<List<string>> BoreholeParseRecords(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQ)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQ = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear();
                records.Add(fields); fields = new List<string>();
            }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        records.Add(fields);
        return records;
    }

    /// <summary>导入预览行(原 BoreholeImportWindow.ImportRow)。</summary>
    public sealed class BoreholeImportRow : INotifyPropertyChanged
    {
        private string _status = "";
        public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
        public bool Valid { get; set; }
        public string HoleId { get; set; } = "";
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? ZCollar { get; set; }
        public double? DepthTotal { get; set; }
        public string? TerminateHorizon { get; set; }
        public string? DrillDate { get; set; }
        public string? DrillUnit { get; set; }
        public string? Category { get; set; }
        public string? Remark { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 表头映射 + 逐行校验 + 冲突检测(原 LoadRecords)。返回预览行与状态栏文案; rows 为空表示表头/文件问题。
    /// </summary>
    public static (List<BoreholeImportRow> rows, string message) BoreholePreviewImport(SqliteConnection conn, List<List<string>> records)
    {
        var rows = new List<BoreholeImportRow>();
        if (records.Count < 2) return (rows, "文件为空或只有表头");
        var map = BoreholeBuildHeaderMap(records[0]);
        if (!BoreholeHasRequired(map)) return (rows, "缺少必要列：需要 孔号 / 经距X / 纬距Y(表头可用别名)");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int valid = 0;
        for (int li = 1; li < records.Count; li++)
        {
            var f = records[li];
            string F(string k) => BoreholeField(f, map, k);
            var row = new BoreholeImportRow
            {
                HoleId = F("hole"),
                TerminateHorizon = NullIfEmpty(F("horizon")),
                DrillDate = NullIfEmpty(F("date")),
                DrillUnit = NullIfEmpty(F("unit")),
                Category = NullIfEmpty(F("cat")),
                Remark = NullIfEmpty(F("remark")),
                X = BoreholeParseNum(F("x")),
                Y = BoreholeParseNum(F("y")),
                ZCollar = BoreholeParseNum(F("z")),
                DepthTotal = BoreholeParseNum(F("depth")),
            };
            if (string.IsNullOrWhiteSpace(row.HoleId)) row.Status = "✗ 缺孔号";
            else if (row.X is null) row.Status = "✗ 经距非数字";
            else if (row.Y is null) row.Status = "✗ 纬距非数字";
            else if (!seenIds.Add(row.HoleId)) row.Status = "✗ 文件内孔号重复";
            else
            {
                row.Valid = true;
                row.Status = GetBoreholeByHoleId(conn, row.HoleId) != null ? "↻ 将更新(已存在)" : "＋ 将新增";
                valid++;
            }
            rows.Add(row);
        }
        return (rows, $"解析 {rows.Count} 行，可导入 {valid} 行；点「导入入库」执行");
    }

    public sealed record BoreholeImportOutcome(int Inserted, int Updated, int Skipped, int Failed, string? FirstError);

    /// <summary>预览行入库(原 OnImportClick): 有效行按孔号 upsert(覆盖开关), 失败行状态回写。</summary>
    public static BoreholeImportOutcome BoreholeApplyImport(SqliteConnection conn, IEnumerable<BoreholeImportRow> rows, bool overwrite)
    {
        int ins = 0, upd = 0, skip = 0, fail = 0;
        string? firstError = null;
        foreach (var r in rows)
        {
            if (!r.Valid) { skip++; continue; }
            try
            {
                var existing = GetBoreholeByHoleId(conn, r.HoleId);
                if (existing != null)
                {
                    if (!overwrite) { skip++; continue; }
                    existing.X = r.X!.Value; existing.Y = r.Y!.Value;
                    existing.ZCollar = r.ZCollar; existing.DepthTotal = r.DepthTotal;
                    existing.TerminateHorizon = r.TerminateHorizon; existing.DrillDate = r.DrillDate;
                    existing.DrillUnit = r.DrillUnit; existing.Category = r.Category; existing.Remark = r.Remark;
                    UpdateBorehole(conn, existing); upd++;
                }
                else
                {
                    InsertBorehole(conn, new BoreholeRow
                    {
                        HoleId = r.HoleId, X = r.X!.Value, Y = r.Y!.Value,
                        ZCollar = r.ZCollar, DepthTotal = r.DepthTotal,
                        TerminateHorizon = r.TerminateHorizon, DrillDate = r.DrillDate,
                        DrillUnit = r.DrillUnit, Category = r.Category, Remark = r.Remark,
                        CoordFilled = "原始",
                    });
                    ins++;
                }
            }
            catch (Exception ex)
            {
                fail++;
                r.Status = $"✗ 入库失败：{ex.Message}";
                firstError ??= ex.Message;
            }
        }
        return new BoreholeImportOutcome(ins, upd, skip, fail, firstError);
    }

    /// <summary>
    /// 开孔坐标管理的直接导入(原 BoreholeDataWindow.OnImportClick): 缺必填/文件内重复直接跳过, 按孔号 upsert。
    /// 返回 null 表示表头问题(message 给出原因)。
    /// </summary>
    public static BoreholeImportOutcome? BoreholeImportDirect(SqliteConnection conn, List<List<string>> records, bool overwrite, out string message)
    {
        message = "";
        if (records.Count < 2) { message = "文件为空或只有表头"; return null; }
        var map = BoreholeBuildHeaderMap(records[0]);
        if (!BoreholeHasRequired(map)) { message = "缺少必要列：需要 孔号 / 经距X / 纬距Y（表头可用别名）"; return null; }

        int ins = 0, upd = 0, skip = 0, fail = 0;
        string? firstError = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int li = 1; li < records.Count; li++)
        {
            var f = records[li];
            string F(string k) => BoreholeField(f, map, k);
            string holeId = F("hole");
            double? x = BoreholeParseNum(F("x"));
            double? y = BoreholeParseNum(F("y"));
            if (string.IsNullOrWhiteSpace(holeId) || x is null || y is null) { skip++; continue; }
            if (!seen.Add(holeId)) { skip++; continue; }
            try
            {
                var existing = GetBoreholeByHoleId(conn, holeId);
                if (existing != null)
                {
                    if (!overwrite) { skip++; continue; }
                    existing.X = x.Value; existing.Y = y.Value;
                    existing.ZCollar = BoreholeParseNum(F("z"));
                    existing.DepthTotal = BoreholeParseNum(F("depth"));
                    existing.TerminateHorizon = NullIfEmpty(F("horizon"));
                    existing.DrillDate = NullIfEmpty(F("date"));
                    existing.DrillUnit = NullIfEmpty(F("unit"));
                    existing.Category = NullIfEmpty(F("cat"));
                    existing.Remark = NullIfEmpty(F("remark"));
                    UpdateBorehole(conn, existing); upd++;
                }
                else
                {
                    InsertBorehole(conn, new BoreholeRow
                    {
                        HoleId = holeId, X = x.Value, Y = y.Value,
                        ZCollar = BoreholeParseNum(F("z")), DepthTotal = BoreholeParseNum(F("depth")),
                        TerminateHorizon = NullIfEmpty(F("horizon")), DrillDate = NullIfEmpty(F("date")),
                        DrillUnit = NullIfEmpty(F("unit")), Category = NullIfEmpty(F("cat")), Remark = NullIfEmpty(F("remark")),
                        CoordFilled = "原始",
                    });
                    ins++;
                }
            }
            catch (Exception ex) { fail++; firstError ??= ex.Message; }
        }
        return new BoreholeImportOutcome(ins, upd, skip, fail, firstError);
    }

    /// <summary>导出 CSV(原 OnExportCsvClick 的 12 列; BOM 由 ctx.SaveTextAsync 写出时加)。</summary>
    public static string BoreholeExportCsv(IEnumerable<BoreholeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("孔号,经距X,纬距Y,孔口高程,总孔深,终孔层位,施工时间,施工单位,钻探评级,综合评级,类别,备注");
        foreach (var b in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                BoreholeCsvField(b.HoleId), Num(b.X), Num(b.Y), Num(b.ZCollar), Num(b.DepthTotal),
                BoreholeCsvField(b.TerminateHorizon), BoreholeCsvField(b.DrillDate), BoreholeCsvField(b.DrillUnit),
                BoreholeCsvField(b.DrillRating), BoreholeCsvField(b.OverallRating), BoreholeCsvField(b.Category), BoreholeCsvField(b.Remark)
            }));
        }
        return sb.ToString();
        static string Num(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ═════════════════════════════════════════════════════════════════════
    //  见煤成果 / 煤层字典
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>钻孔煤层成果行(原 BoreholeSeamResult 实体)。</summary>
    public sealed class BoreholeSeamRow
    {
        public long Id { get; set; }
        public long BoreholeId { get; set; }
        public string SeamCode { get; set; } = "";
        public double? DrillEndDepth { get; set; }
        public double? DrillSeamThickness { get; set; }
        public string? DrillStructure { get; set; }
        public double? DrillRecoveryRate { get; set; }
        public string? DrillQuality { get; set; }
        public double? LogEndDepth { get; set; }
        public double? LogSeamThickness { get; set; }
        public string? LogStructure { get; set; }
        public string? LogQuality { get; set; }
        public double? OverallThickness { get; set; }
        public double? AdoptedThickness { get; set; }
        public double? WeatheredCoalThickness { get; set; }
        public double? PartingThickness { get; set; }
        public string? RoofLithology { get; set; }
        public string? FloorLithology { get; set; }
        public double? FloorElevation { get; set; }
        public string? OverallRating { get; set; }
        public string Status { get; set; } = "正常";
        public string? Remark { get; set; }
    }

    private const string SeamSelect = @"SELECT id AS Id, borehole_id AS BoreholeId, seam_code AS SeamCode, drill_end_depth AS DrillEndDepth,
        drill_seam_thickness AS DrillSeamThickness, drill_structure AS DrillStructure, drill_recovery_rate AS DrillRecoveryRate,
        drill_quality AS DrillQuality, log_end_depth AS LogEndDepth, log_seam_thickness AS LogSeamThickness, log_structure AS LogStructure,
        log_quality AS LogQuality, overall_thickness AS OverallThickness, adopted_thickness AS AdoptedThickness,
        weathered_coal_thickness AS WeatheredCoalThickness, parting_thickness AS PartingThickness, roof_lithology AS RoofLithology,
        floor_lithology AS FloorLithology, floor_elevation AS FloorElevation, overall_rating AS OverallRating, status AS Status, remark AS Remark
        FROM borehole_seam_result";

    /// <summary>按状态取见煤成果(原 SeamResultsByStatus)。</summary>
    public static List<BoreholeSeamRow> LoadSeamResultsByStatus(SqliteConnection conn, string status)
        => conn.Query<BoreholeSeamRow>(SeamSelect + " WHERE status = @s", new { s = status }).ToList();

    /// <summary>按钻孔取见煤成果(原 SeamResultsByBorehole)。</summary>
    public static List<BoreholeSeamRow> LoadSeamResultsByBorehole(SqliteConnection conn, long boreholeId)
        => conn.Query<BoreholeSeamRow>(SeamSelect + " WHERE borehole_id = @id", new { id = boreholeId }).ToList();

    /// <summary>煤层字典行(coal_seam_def)。</summary>
    public sealed class SeamDefRow
    {
        public SeamDefRow() { }
        public SeamDefRow(string code, string name, int sortOrder, string? colorHex) { Code = code; Name = name; SortOrder = sortOrder; ColorHex = colorHex; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int SortOrder { get; set; }
        public string? ColorHex { get; set; }
    }

    public static List<SeamDefRow> LoadSeamDefs(SqliteConnection conn)
        => conn.Query<SeamDefRow>("SELECT code AS Code, name AS Name, sort_order AS SortOrder, color_hex AS ColorHex FROM coal_seam_def ORDER BY sort_order").ToList();

    /// <summary>煤层调色板(原 SeamPalette.FromReference): code → RGB, 未知/坏 hex 回退 #555555。</summary>
    public static (byte r, byte g, byte b) SeamPaletteColor(IReadOnlyList<SeamDefRow> defs, string? code)
    {
        const byte fb = 0x55;
        if (code == null) return (fb, fb, fb);
        foreach (var d in defs)
            if (d.Code == code && TryParseHexColor(d.ColorHex, out var rgb)) return rgb;
        return (fb, fb, fb);
    }

    /// <summary>煤层显示名(原 SeamName): 字典有名用名, 否则 "{code} 号煤层"。</summary>
    public static string SeamDisplayName(IReadOnlyList<SeamDefRow> defs, string code)
    {
        foreach (var d in defs) if (d.Code == code && !string.IsNullOrEmpty(d.Name)) return d.Name;
        return $"{code} 号煤层";
    }

    /// <summary>解析 "#RRGGBB" / "RRGGBB"; 失败 false。</summary>
    public static bool TryParseHexColor(string? hex, out (byte r, byte g, byte b) rgb)
    {
        rgb = (0x55, 0x55, 0x55);
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.Length != 6) return false;
        try
        {
            rgb = (Convert.ToByte(s.Substring(0, 2), 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
            return true;
        }
        catch { return false; }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  展绘钻孔(原 BoreholeColumnBuilder → 2D 平面柱)
    // ═════════════════════════════════════════════════════════════════════

    public const string BoreholeColumnLayerName = "钻孔柱状图";
    private const double BcRadius = 7.0;             // 柱半径 (m)
    private const double BcHoleLabelHeight = 34.0;   // 孔号字高 (m)
    private const double BcSeamLabelHeight = 19.0;   // 煤层名字高 (m)
    private const double BcLabelGap = 18.0;          // 标注离柱面间距 (m)
    private const double BcSeamLabelZGap = 20.0;     // 相邻煤层名最小竖向间距 (m)
    private static readonly (byte r, byte g, byte b) BcRockColor = (0x5F, 0xA8, 0x4E);
    private static readonly (byte r, byte g, byte b) BcCoalColor = (0x3C, 0x3C, 0x3C);

    public sealed class BoreholeColumnResult
    {
        public List<SceneEntity> Entities { get; } = new();
        public int HoleCount;
        public int SeamCount;
        public int SkippedHoles;
        public int SkippedSeams;
        public int MeshGroups;   // 颜色种类(岩色 + 各煤层名)
        public double[]? Bounds;
    }

    /// <summary>
    /// 真实钻孔 → 柱状图实体(原 Build): 每孔一柱, 孔口 z_collar → 孔底, 无煤处岩色、"正常" 煤层段煤色;
    /// 孔号置柱顶、煤层名置柱侧(带引线, 竖向避让)。Kylin 场景为 2D 平面: 柱在孔位 (x,y) 竖直向下按 1:1 垂向展开。
    /// </summary>
    public static BoreholeColumnResult BuildBoreholeColumns(SqliteConnection conn, IReadOnlyList<BoreholeRow>? holesFilter = null)
    {
        var holes = holesFilter ?? LoadBoreholes(conn);
        var seamsByHole = new Dictionary<long, List<BoreholeSeamRow>>();
        foreach (var sr in LoadSeamResultsByStatus(conn, "正常"))
        {
            if (!seamsByHole.TryGetValue(sr.BoreholeId, out var list)) seamsByHole[sr.BoreholeId] = list = new List<BoreholeSeamRow>();
            list.Add(sr);
        }
        return BuildBoreholeColumns(holes, seamsByHole);
    }

    /// <summary>纯逻辑版(可单测): holes + 按孔分组的"正常"煤层。</summary>
    public static BoreholeColumnResult BuildBoreholeColumns(IReadOnlyList<BoreholeRow> holes, IReadOnlyDictionary<long, List<BoreholeSeamRow>> seamsByHole)
    {
        var result = new BoreholeColumnResult();
        var groups = new HashSet<string>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Bound(double x, double y) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }

        foreach (var h in holes)
        {
            if (h.ZCollar is null || h.DepthTotal is null || h.DepthTotal.Value <= 0) { result.SkippedHoles++; continue; }
            double baseX = h.X, baseY = h.Y, baseZ = h.ZCollar.Value;
            double zTop = baseZ, zBot = baseZ - h.DepthTotal.Value;
            double labelX = baseX + BcRadius + BcLabelGap;
            double YOf(double z) => baseY - (zTop - z);   // 1:1 垂向, 向下(−Y)

            var bands = new List<(double f, double t, string code)>();
            if (seamsByHole.TryGetValue(h.Id, out var seams))
            {
                foreach (var sr in seams)
                {
                    double? thick = sr.AdoptedThickness ?? sr.OverallThickness;
                    if (sr.FloorElevation is null || thick is null || thick.Value <= 0) { result.SkippedSeams++; continue; }
                    double f = Math.Max(sr.FloorElevation.Value, zBot);
                    double t = Math.Min(sr.FloorElevation.Value + thick.Value, zTop);
                    if (t <= f) { result.SkippedSeams++; continue; }
                    bands.Add((f, t, sr.SeamCode));
                }
                bands.Sort((a, b) => a.f.CompareTo(b.f));
            }

            groups.Add("rock");
            double cursor = zBot, lastSeamLabelZ = double.NegativeInfinity;
            foreach (var band in bands)
            {
                double bf = band.f, bt = band.t;
                if (bf < cursor) bf = cursor;
                if (bt <= cursor) continue;
                if (bf > cursor) result.Entities.Add(BcRect(baseX, YOf(cursor), YOf(bf), BcRockColor));
                result.Entities.Add(BcRect(baseX, YOf(bf), YOf(bt), BcCoalColor));
                groups.Add(band.code);

                double seamMidZ = (bf + bt) * 0.5;
                double labelZ = Math.Max(seamMidZ, lastSeamLabelZ + BcSeamLabelZGap);
                lastSeamLabelZ = labelZ;
                string seamName = string.IsNullOrWhiteSpace(band.code) ? "煤" : band.code + "煤";
                result.Entities.Add(new LineEntity   // 引线: 柱面煤层段 → 标注锚点(黄色虚线)
                {
                    X0 = baseX + BcRadius, Y0 = YOf(seamMidZ), X1 = labelX, Y1 = YOf(labelZ),
                    Cr = 1f, Cg = 0.88f, Cb = 0f, Dash = new[] { 3.0, 2.0 },
                });
                result.Entities.Add(new TextEntity
                {
                    X = labelX, Y = YOf(labelZ), Height = BcSeamLabelHeight, HAlign = 0, VAlign = 1, Text = seamName,
                    Cr = 0xF0 / 255f, Cg = 0xF0 / 255f, Cb = 0xF0 / 255f,
                });
                result.SeamCount++;
                cursor = bt;
            }
            if (cursor < zTop) result.Entities.Add(BcRect(baseX, YOf(cursor), YOf(zTop), BcRockColor));   // 上覆岩层段

            result.Entities.Add(new TextEntity   // 孔号(柱顶正上方, 居中)
            {
                X = baseX, Y = YOf(zTop) + BcHoleLabelHeight * 0.6, Height = BcHoleLabelHeight, HAlign = 1, VAlign = 1, Text = h.HoleId,
                Cr = 1f, Cg = 0xE0 / 255f, Cb = 0f,
            });
            Bound(baseX - BcRadius, YOf(zBot)); Bound(labelX + BcSeamLabelHeight * 4, YOf(zTop) + BcHoleLabelHeight * 1.2);
            result.HoleCount++;
        }
        result.MeshGroups = result.HoleCount > 0 ? groups.Count : 0;
        if (result.HoleCount > 0) result.Bounds = new[] { minX, minY, maxX, maxY };
        return result;
    }

    private static RectEntity BcRect(double cx, double y0, double y1, (byte r, byte g, byte b) c)
        => new() { X0 = cx - BcRadius, Y0 = y0, X1 = cx + BcRadius, Y1 = y1, Cr = c.r / 255f, Cg = c.g / 255f, Cb = c.b / 255f };

    // ═════════════════════════════════════════════════════════════════════
    //  原始钻孔柱状图(原 OriginalBoreholeColumnWindow.BuildSegments)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>柱状图单个分层段(煤层或围岩)。</summary>
    public sealed class OriginalColumnSeg
    {
        public double DepthTop, DepthBot;      // 段顶/底深 (m)
        public double ZTop, ZBot;              // 段顶/底标高 (m)
        public bool IsCoal;
        public string LayerName = "";          // "4-1 号煤层" / "上覆岩层" …
        public (byte r, byte g, byte b) Fill;
        public BoreholeSeamRow? Seam;          // 煤层详情来源
        public string? RockLith;               // 围岩推断岩性(相邻煤层顶/底板)
        public double Thick => Math.Max(0, DepthBot - DepthTop);
    }

    private const double OcEps = 0.05;   // 深度合并容差 (m)
    public static readonly (byte r, byte g, byte b) OcRockOverburden = (0xB8, 0xAA, 0x94);
    public static readonly (byte r, byte g, byte b) OcRockInterburden = (0x9A, 0x8C, 0x78);
    public static readonly (byte r, byte g, byte b) OcRockBase = (0x7C, 0x70, 0x5E);

    /// <summary>由煤层成果推导整孔逐分层序列(煤层 + 填满空隙的围岩段)。</summary>
    public static List<OriginalColumnSeg> BuildOriginalSegments(double collar, double depth, IEnumerable<BoreholeSeamRow> seamRows,
        IReadOnlyList<SeamDefRow> defs, out int skipped)
    {
        skipped = 0;
        var coals = new List<OriginalColumnSeg>();
        foreach (var s in seamRows)
        {
            if (s.Status is "未达" or "尖灭" or "空巷") { skipped++; continue; }
            double? thick = s.AdoptedThickness ?? s.OverallThickness;
            double? depthBot = s.LogEndDepth ?? (s.FloorElevation is { } fe ? collar - fe : (double?)null);
            if (depthBot is null || thick is null || thick.Value <= 0) { skipped++; continue; }
            double dBot = Math.Clamp(depthBot.Value, 0, depth);
            double dTop = Math.Clamp(dBot - thick.Value, 0, depth);
            if (dBot - dTop < OcEps) { skipped++; continue; }
            coals.Add(new OriginalColumnSeg
            {
                DepthTop = dTop, DepthBot = dBot, ZTop = collar - dTop, ZBot = collar - dBot,
                IsCoal = true, Seam = s, LayerName = SeamDisplayName(defs, s.SeamCode), Fill = SeamPaletteColor(defs, s.SeamCode),
            });
        }
        coals.Sort((a, b) => a.DepthTop.CompareTo(b.DepthTop));

        var segs = new List<OriginalColumnSeg>();
        double cursor = 0;
        for (int i = 0; i < coals.Count; i++)
        {
            var c = coals[i];
            if (c.DepthTop > cursor + OcEps)
                segs.Add(OcMakeRock(collar, cursor, c.DepthTop, i == 0 ? "上覆岩层" : "层间岩层", i > 0 ? coals[i - 1].Seam : null, c.Seam));
            if (c.DepthTop < cursor - OcEps) c.DepthTop = cursor;
            segs.Add(c);
            cursor = Math.Max(cursor, c.DepthBot);
        }
        if (cursor < depth - OcEps)
            segs.Add(OcMakeRock(collar, cursor, depth, coals.Count == 0 ? "岩层" : "底部岩层", coals.Count > 0 ? coals[^1].Seam : null, null));
        return segs;
    }

    private static OriginalColumnSeg OcMakeRock(double collar, double dTop, double dBot, string kind, BoreholeSeamRow? prev, BoreholeSeamRow? next)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prev?.FloorLithology)) parts.Add(prev!.FloorLithology!);
        if (!string.IsNullOrWhiteSpace(next?.RoofLithology)) parts.Add(next!.RoofLithology!);
        string? lith = parts.Count > 0 ? string.Join(" / ", parts.Distinct()) : null;
        var fill = kind == "上覆岩层" ? OcRockOverburden : kind == "底部岩层" ? OcRockBase : OcRockInterburden;
        return new OriginalColumnSeg
        {
            DepthTop = dTop, DepthBot = dBot, ZTop = collar - dTop, ZBot = collar - dBot,
            IsCoal = false, LayerName = kind, RockLith = lith, Fill = fill,
        };
    }

    // ═════════════════════════════════════════════════════════════════════
    //  展绘层位数据(原 HorizonPointBuilder)
    // ═════════════════════════════════════════════════════════════════════

    private static readonly (byte r, byte g, byte b) HpFallback = (0x88, 0x88, 0x88);
    private static readonly Dictionary<string, (byte r, byte g, byte b)> HpSeamColorMap = new()
    {
        ["4"] = (0xD4, 0xA0, 0x17), ["4-1"] = (0xE5, 0xB6, 0x22), ["4-2"] = (0xC9, 0x95, 0x18), ["4（4-1）"] = (0xD4, 0xA0, 0x17),
        ["7-1"] = (0x8B, 0x69, 0x14), ["9"] = (0x4A, 0x7C, 0x2E), ["11"] = (0x2C, 0x5F, 0x8D),
    };
    private static readonly string[] HpSeamOrder = { "4", "4-1", "4-2", "4（4-1）", "7-1", "9", "11" };

    /// <summary>煤层色(层位展点 / 选煤层对话框共用固定表; 未知回退灰)。</summary>
    public static (byte r, byte g, byte b) HorizonSeamColor(string code)
        => HpSeamColorMap.TryGetValue(code, out var c) ? c : HpFallback;

    /// <summary>提取有底板标高数据的可用煤层编号(供 UI 勾选); 按地质顺序(浅→深)排。</summary>
    public static List<string> HorizonAvailableSeams(SqliteConnection conn)
    {
        var set = new HashSet<string>();
        foreach (var sr in LoadSeamResultsByStatus(conn, "正常")) if (!string.IsNullOrWhiteSpace(sr.SeamCode)) set.Add(sr.SeamCode);
        foreach (var c in conn.Query<string>("SELECT seam_code FROM coal_observation_point")) if (!string.IsNullOrWhiteSpace(c)) set.Add(c);
        var ordered = HpSeamOrder.Where(set.Contains).ToList();
        ordered.AddRange(set.Where(s => !HpSeamOrder.Contains(s)).OrderBy(s => s, StringComparer.Ordinal));
        return ordered;
    }

    public sealed class HorizonLayer
    {
        public string Name = "";
        public byte R, G, B;
        public readonly List<(double x, double y, double z)> Pts = new();
    }

    public sealed class HorizonBuildResult
    {
        public List<HorizonLayer> Layers { get; } = new();
        public int FloorPoints;
        public int RoofPoints;
        public int SeamLayers;     // 图层数(煤层 × 顶/底)
        public int Skipped;        // 缺底板标高/孔位而跳过
        public List<string> Seams { get; } = new();
    }

    /// <summary>
    /// 分煤层提取顶板 / 底板高程点(原 Build): ① borehole_seam_result(status=正常, join 孔位): 底=floor_elevation, 顶=底+采用厚度;
    /// ② coal_observation_point: 底=floor_elevation, 顶=底+见煤厚度(退化估算厚)。图层「层位_{煤层}_顶板/底板」按煤层分色。
    /// </summary>
    public static HorizonBuildResult BuildHorizonPoints(SqliteConnection conn, ISet<string>? seams = null, bool includeRoof = true, bool includeFloor = true)
    {
        var result = new HorizonBuildResult();
        var holeXY = new Dictionary<long, (double x, double y)>();
        foreach (var h in conn.Query<(long id, double x, double y)>("SELECT id, x, y FROM borehole")) holeXY[h.id] = (h.x, h.y);

        var layers = new Dictionary<string, HorizonLayer>();
        var seamSet = new HashSet<string>();
        HorizonLayer LayerFor(string seamCode, bool roof)
        {
            string key = $"层位_{seamCode}_{(roof ? "顶板" : "底板")}";
            if (!layers.TryGetValue(key, out var lay))
            {
                var (r, g, b) = HorizonSeamColor(seamCode);
                layers[key] = lay = new HorizonLayer { Name = key, R = r, G = g, B = b };
                seamSet.Add(seamCode);
            }
            return lay;
        }
        void AddFloor(string code, double x, double y, double z) { LayerFor(code, false).Pts.Add((x, y, z)); result.FloorPoints++; }
        void AddRoof(string code, double x, double y, double z) { LayerFor(code, true).Pts.Add((x, y, z)); result.RoofPoints++; }

        foreach (var sr in LoadSeamResultsByStatus(conn, "正常"))
        {
            if (seams != null && !seams.Contains(sr.SeamCode)) continue;
            if (!holeXY.TryGetValue(sr.BoreholeId, out var xy)) { result.Skipped++; continue; }
            if (sr.FloorElevation is null) { result.Skipped++; continue; }
            double floor = sr.FloorElevation.Value;
            if (includeFloor) AddFloor(sr.SeamCode, xy.x, xy.y, floor);
            double? thick = sr.AdoptedThickness ?? sr.OverallThickness;
            if (includeRoof && thick is not null && thick.Value > 0) AddRoof(sr.SeamCode, xy.x, xy.y, floor + thick.Value);
        }
        foreach (var p in conn.Query<(string code, double x, double y, double? floor, double? thick, double? est)>(
                     "SELECT seam_code, x, y, floor_elevation, seam_thickness, estimated_thickness FROM coal_observation_point ORDER BY point_id"))
        {
            if (seams != null && !seams.Contains(p.code)) continue;
            if (p.floor is null) { result.Skipped++; continue; }
            double floor = p.floor.Value;
            if (includeFloor) AddFloor(p.code, p.x, p.y, floor);
            double? thick = p.thick ?? p.est;
            if (includeRoof && thick is not null && thick.Value > 0) AddRoof(p.code, p.x, p.y, floor + thick.Value);
        }
        foreach (var lay in layers.Values)
        {
            if (lay.Pts.Count == 0) continue;
            result.Layers.Add(lay);
            result.SeamLayers++;
        }
        result.Seams.AddRange(seamSet);
        return result;
    }

    /// <summary>一图层的高程点 → 点实体(标高存 Elevation, 图层色)。</summary>
    public static List<SceneEntity> HorizonLayerEntities(HorizonLayer lay)
    {
        var list = new List<SceneEntity>(lay.Pts.Count);
        foreach (var (x, y, z) in lay.Pts)
            list.Add(new PointEntity { X = x, Y = y, Elevation = z, Size = 1.6, Cr = lay.R / 255f, Cg = lay.G / 255f, Cb = lay.B / 255f, LayerName = lay.Name });
        return list;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  虚拟钻孔: 地质模型面持久化(原 VirtualDrillService + VirtualDrillGeometry)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>已捕获的一张地质模型面(原 VirtualDrillSurface 实体, 不含几何)。</summary>
    public sealed class VdSurfaceRow
    {
        public long Id { get; set; }
        public string Role { get; set; } = "roof";
        public string SeamName { get; set; } = "";
        public int SeamOrder { get; set; }
        public string ColorHex { get; set; } = "#3C3C3C";
        public string SourceLayer { get; set; } = "";
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
    }

    private const string VdSelect = @"SELECT id AS Id, role AS Role, seam_name AS SeamName, seam_order AS SeamOrder, color_hex AS ColorHex,
        source_layer AS SourceLayer, vertex_count AS VertexCount, triangle_count AS TriangleCount,
        min_x AS MinX, min_y AS MinY, min_z AS MinZ, max_x AS MaxX, max_y AS MaxY, max_z AS MaxZ FROM virtual_drill_surface";

    /// <summary>全部已捕获面, 按 seam_order、role 排序。</summary>
    public static List<VdSurfaceRow> VdAllSurfaces(SqliteConnection conn)
        => conn.Query<VdSurfaceRow>(VdSelect + " ORDER BY seam_order, role").ToList();

    public static VdSurfaceRow? VdGetByKey(SqliteConnection conn, string role, string seamName)
        => conn.Query<VdSurfaceRow>(VdSelect + " WHERE role = @r AND seam_name = @s", new { r = role ?? "", s = seamName ?? "" }).FirstOrDefault();

    public static bool VdHasAnySurface(SqliteConnection conn)
        => conn.ExecuteScalar<long>("SELECT COUNT(*) FROM virtual_drill_surface") > 0;

    /// <summary>保存一张面(按 (role, seamName) upsert)。verts 扁平 [x,y,z,...], tris 三角索引。非法几何不写返回 0。</summary>
    public static long VdSaveSurface(SqliteConnection conn, string role, string seamName, int seamOrder, string colorHex,
                                     string sourceLayer, double[] verts, int[] tris)
    {
        string b64 = VdPack(verts, tris);
        if (b64.Length == 0) return 0;
        VdComputeBounds(verts, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        role ??= "roof"; seamName ??= "";
        colorHex = string.IsNullOrWhiteSpace(colorHex) ? "#3C3C3C" : colorHex;
        int vCount = verts.Length / 3, tCount = tris.Length / 3;
        var existing = VdGetByKey(conn, role, seamName);
        if (existing != null)
        {
            conn.Execute(@"UPDATE virtual_drill_surface SET seam_order=@o, color_hex=@c, source_layer=@src, vertex_count=@vc, triangle_count=@tc,
                           min_x=@minX, min_y=@minY, min_z=@minZ, max_x=@maxX, max_y=@maxY, max_z=@maxZ, geometry_b64=@g, updated_at=@now WHERE id=@id",
                new { o = seamOrder, c = colorHex, src = sourceLayer ?? "", vc = vCount, tc = tCount, minX, minY, minZ, maxX, maxY, maxZ, g = b64, now, id = existing.Id });
            return existing.Id;
        }
        conn.Execute(@"INSERT INTO virtual_drill_surface(role, seam_name, seam_order, color_hex, source_layer, vertex_count, triangle_count,
                       min_x, min_y, min_z, max_x, max_y, max_z, geometry_b64, created_at, updated_at)
                       VALUES(@role, @seamName, @o, @c, @src, @vc, @tc, @minX, @minY, @minZ, @maxX, @maxY, @maxZ, @g, @now, @now)",
            new { role, seamName, o = seamOrder, c = colorHex, src = sourceLayer ?? "", vc = vCount, tc = tCount, minX, minY, minZ, maxX, maxY, maxZ, g = b64, now });
        return conn.ExecuteScalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>取某面几何: 解包 base64 → 顶点 + 三角索引。缺行 / 坏数据 false。</summary>
    public static bool VdTryGetGeometry(SqliteConnection conn, long id, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>(); tris = Array.Empty<int>();
        var b64 = conn.ExecuteScalar<string?>("SELECT geometry_b64 FROM virtual_drill_surface WHERE id=@id", new { id });
        return b64 != null && VdTryUnpack(b64, out verts, out tris);
    }

    /// <summary>改某煤层(顶+底同步)的层序与颜色, 返回受影响行数。</summary>
    public static int VdUpdateSeamMeta(SqliteConnection conn, string seamName, int seamOrder, string colorHex)
    {
        colorHex = string.IsNullOrWhiteSpace(colorHex) ? "#3C3C3C" : colorHex;
        return conn.Execute("UPDATE virtual_drill_surface SET seam_order=@o, color_hex=@c, updated_at=@now WHERE seam_name=@s",
            new { o = seamOrder, c = colorHex, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), s = seamName ?? "" });
    }

    public static int VdDelete(SqliteConnection conn, long id) => conn.Execute("DELETE FROM virtual_drill_surface WHERE id=@id", new { id });

    /// <summary>清空全部地质模型面, 返回删除行数。</summary>
    public static int VdClearAll(SqliteConnection conn) => conn.Execute("DELETE FROM virtual_drill_surface");

    private const int VdMagic = 0x56445431;   // 'VDT1'
    private const int VdVersion = 1;

    /// <summary>顶点(扁平)+三角索引 → base64(小端: magic, version, vCount, tCount, 顶点 float64×3V, 索引 int32×3T)。非法返回空串。</summary>
    public static string VdPack(double[] verts, int[] tris)
    {
        if (verts == null || tris == null) return "";
        if (verts.Length < 9 || verts.Length % 3 != 0) return "";
        if (tris.Length < 3 || tris.Length % 3 != 0) return "";
        using var ms = new MemoryStream(16 + verts.Length * 8 + tris.Length * 4);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(VdMagic); w.Write(VdVersion); w.Write(verts.Length / 3); w.Write(tris.Length / 3);
            foreach (var v in verts) w.Write(v);
            foreach (var t in tris) w.Write(t);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>base64 → 顶点 + 三角索引。任何损坏 / 越界都判失败(不抛)。</summary>
    public static bool VdTryUnpack(string? b64, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>(); tris = Array.Empty<int>();
        if (string.IsNullOrEmpty(b64)) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); } catch { return false; }
        if (bytes.Length < 16) return false;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms);
            if (r.ReadInt32() != VdMagic) return false;
            if (r.ReadInt32() != VdVersion) return false;
            int vCount = r.ReadInt32(), tCount = r.ReadInt32();
            if (vCount < 3 || tCount < 1) return false;
            long need = 16L + (long)vCount * 3 * 8 + (long)tCount * 3 * 4;
            if (bytes.Length < need) return false;
            var v = new double[vCount * 3];
            for (int i = 0; i < v.Length; i++) v[i] = r.ReadDouble();
            var t = new int[tCount * 3];
            for (int i = 0; i < t.Length; i++)
            {
                int idx = r.ReadInt32();
                if (idx < 0 || idx >= vCount) return false;
                t[i] = idx;
            }
            verts = v; tris = t;
            return true;
        }
        catch { return false; }
    }

    /// <summary>顶点包围盒(空/非法全 0)。</summary>
    public static void VdComputeBounds(double[]? verts, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
    {
        minX = minY = minZ = 0; maxX = maxY = maxZ = 0;
        if (verts == null || verts.Length < 3) return;
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            double x = verts[i], y = verts[i + 1], z = verts[i + 2];
            if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; if (z < z0) z0 = z; if (z > z1) z1 = z;
        }
        minX = x0; minY = y0; minZ = z0; maxX = x1; maxY = y1; maxZ = z1;
    }

    // ── 面来源(替代原"视图中选三角网"): 场景图层顶点 → Delaunay 重建; OFF 文件 ──

    /// <summary>场景图层顶点 → 三角网(XY 去重后 Delaunay)。点少于 3 返回 false。</summary>
    public static bool VdMeshFromVertices(IReadOnlyList<(double x, double y, double z)> pts, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>(); tris = Array.Empty<int>();
        var uniq = new List<(double x, double y, double z)>();
        var seen = new HashSet<(long, long)>();
        foreach (var p in pts)
        {
            var key = ((long)Math.Round(p.x * 1000), (long)Math.Round(p.y * 1000));
            if (seen.Add(key)) uniq.Add(p);
        }
        if (uniq.Count < 3) return false;
        var xy = uniq.Select(p => (p.x, p.y)).ToList();
        var t = Delaunay.Triangulate(xy);
        if (t.Count == 0) return false;
        return VdFlatten(uniq, t, out verts, out tris);
    }

    /// <summary>OFF 文本 → 三角网(多边形面扇形三角化)。</summary>
    public static bool VdMeshFromOff(string text, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>(); tris = Array.Empty<int>();
        var (v, t) = MeshMetrics.ParseOff(text);
        return VdFlatten(v, t, out verts, out tris);
    }

    private static bool VdFlatten(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>(); tris = Array.Empty<int>();
        if (v.Count < 3 || t.Count < 1) return false;
        verts = new double[v.Count * 3];
        for (int i = 0; i < v.Count; i++) { verts[i * 3] = v[i].x; verts[i * 3 + 1] = v[i].y; verts[i * 3 + 2] = v[i].z; }
        tris = new int[t.Count * 3];
        for (int i = 0; i < t.Count; i++) { tris[i * 3] = t[i].a; tris[i * 3 + 1] = t[i].b; tris[i * 3 + 2] = t[i].c; }
        return true;
    }

    // ── 自动识别(原 VirtualDrillCapture.AutoDetect: 按图层名配对) ──

    public sealed class VdDetectedSeam { public string Name = ""; public string RoofLayer = ""; public string FloorLayer = ""; public int Order; }
    public sealed class VdDetectedModel { public string SurfaceLayer = ""; public readonly List<VdDetectedSeam> Seams = new(); }

    private static readonly Regex VdSurfaceRx = new(@"地表|表土|现状|surface|dem", RegexOptions.IgnoreCase);
    private static readonly Regex VdRoofRx = new(@"顶板|顶面|顶|roof|top", RegexOptions.IgnoreCase);
    private static readonly Regex VdFloorRx = new(@"底板|底面|底|floor|bottom|base", RegexOptions.IgnoreCase);

    private static string VdSeamToken(string layer)
    {
        string s = VdRoofRx.Replace(layer, "");
        s = VdFloorRx.Replace(s, "");
        return s.Trim(' ', '_', '-', '面', '层', '煤').Trim();
    }

    private static double VdOrderKey(string token)
    {
        var m = Regex.Match(token, @"\d+(\.\d+)?");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 1e9;
    }

    /// <summary>从图层名自动识别地表 + 煤层顶/底板配对(识别不到 Seams 为空)。</summary>
    public static VdDetectedModel VdAutoDetect(IEnumerable<string> layerNames)
    {
        var model = new VdDetectedModel();
        var layers = layerNames?.ToList() ?? new List<string>();
        model.SurfaceLayer = layers.FirstOrDefault(l => VdSurfaceRx.IsMatch(l)) ?? "";
        var roofs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var floors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string layer in layers)
        {
            if (layer == model.SurfaceLayer) continue;
            bool isRoof = VdRoofRx.IsMatch(layer);
            bool isFloor = VdFloorRx.IsMatch(layer);
            if (isFloor && !VdRoofRx.IsMatch(layer.Replace("底", "")))
            {
                string tok = VdSeamToken(layer);
                if (tok.Length > 0 && !floors.ContainsKey(tok)) floors[tok] = layer;
            }
            else if (isRoof)
            {
                string tok = VdSeamToken(layer);
                if (tok.Length > 0 && !roofs.ContainsKey(tok)) roofs[tok] = layer;
            }
        }
        var tokens = roofs.Keys.Intersect(floors.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(VdOrderKey).ThenBy(t => t, StringComparer.Ordinal).ToList();
        int order = 0;
        foreach (string tok in tokens)
            model.Seams.Add(new VdDetectedSeam { Name = tok, RoofLayer = roofs[tok], FloorLayer = floors[tok], Order = order++ });
        return model;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  虚拟钻孔: 求交模型 / 钻孔 / 柱状(原 VirtualDrillEngine + TinZSampler)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>2.5D 三角网 Z 采样器(包围盒预筛 + TinSampler 竖直求交)。</summary>
    public sealed class VdTinSampler
    {
        private readonly List<(double x, double y, double z)> _pts;
        private readonly List<(int a, int b, int c)> _tris;
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        private VdTinSampler(List<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris)
        {
            _pts = pts; _tris = tris;
            MinX = MinY = MinZ = double.MaxValue; MaxX = MaxY = MaxZ = double.MinValue;
            foreach (var p in pts)
            {
                if (p.x < MinX) MinX = p.x; if (p.x > MaxX) MaxX = p.x; if (p.y < MinY) MinY = p.y; if (p.y > MaxY) MaxY = p.y;
                if (p.z < MinZ) MinZ = p.z; if (p.z > MaxZ) MaxZ = p.z;
            }
        }
        public static VdTinSampler? TryBuild(double[]? worldVerts, int[]? triIndices)
        {
            if (worldVerts == null || triIndices == null) return null;
            int vn = worldVerts.Length / 3;
            if (vn < 3 || triIndices.Length < 3) return null;
            var pts = new List<(double x, double y, double z)>(vn);
            for (int i = 0; i < vn; i++) pts.Add((worldVerts[i * 3], worldVerts[i * 3 + 1], worldVerts[i * 3 + 2]));
            var tris = new List<(int a, int b, int c)>(triIndices.Length / 3);
            for (int i = 0; i + 2 < triIndices.Length; i += 3) tris.Add((triIndices[i], triIndices[i + 1], triIndices[i + 2]));
            return new VdTinSampler(pts, tris);
        }
        public bool TrySampleZ(double x, double y, out double z)
        {
            z = 0;
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            var r = TinSampler.SampleZ(_pts, _tris, x, y, 1e-7);
            if (r == null) return false;
            z = r.Value; return true;
        }
    }

    public sealed class VdSeamModel { public string Name = ""; public int Order; public byte R, G, B; public VdTinSampler? Roof; public VdTinSampler? Floor; }

    public sealed class VdModel
    {
        public VdTinSampler? Surface;
        public readonly List<VdSeamModel> Seams = new();
        public bool HasSurface => Surface != null;
        public int SeamCount => Seams.Count;
        public int SamplerCount
        {
            get { int n = Surface != null ? 1 : 0; foreach (var s in Seams) { if (s.Roof != null) n++; if (s.Floor != null) n++; } return n; }
        }
    }

    public static readonly (byte r, byte g, byte b) VdColorFallback = (0x3C, 0x3C, 0x3C);
    public static (byte r, byte g, byte b) VdColorParse(string? hex) => TryParseHexColor(hex, out var c) ? c : VdColorFallback;
    public static string VdColorToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>读库中全部面, 解包几何建采样器: 地表 1 张 + 各煤层顶/底板按 seam_name 配对(按 Order 自上而下)。</summary>
    public static VdModel VdBuildModel(SqliteConnection conn)
    {
        var model = new VdModel();
        var seams = new Dictionary<string, VdSeamModel>(StringComparer.Ordinal);
        VdSeamModel SeamFor(string name, int order, string colorHex)
        {
            if (!seams.TryGetValue(name, out var sm))
            {
                var (r, g, b) = VdColorParse(colorHex);
                seams[name] = sm = new VdSeamModel { Name = name, Order = order, R = r, G = g, B = b };
            }
            return sm;
        }
        foreach (var s in VdAllSurfaces(conn))
        {
            VdTinSampler? sampler = null;
            if (VdTryGetGeometry(conn, s.Id, out var verts, out var tris)) sampler = VdTinSampler.TryBuild(verts, tris);
            switch (s.Role)
            {
                case "surface": if (sampler != null) model.Surface = sampler; break;
                case "roof":
                {
                    var sm = SeamFor(s.SeamName, s.SeamOrder, s.ColorHex);
                    sm.Roof = sampler; sm.Order = s.SeamOrder;
                    var (r, g, b) = VdColorParse(s.ColorHex); sm.R = r; sm.G = g; sm.B = b;
                    break;
                }
                case "floor":
                {
                    var sm = SeamFor(s.SeamName, s.SeamOrder, s.ColorHex);
                    sm.Floor = sampler; sm.Order = s.SeamOrder;
                    break;
                }
            }
        }
        model.Seams.AddRange(seams.Values.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.Ordinal));
        return model;
    }

    /// <summary>某煤层求交结果(一行 = 一层)。</summary>
    public sealed class VdSeamHit
    {
        public string Name = "";
        public bool Present;
        public double RoofZ, FloorZ;
        public double Thickness => Present ? RoofZ - FloorZ : 0.0;
        public double? Interval;
        public byte R, G, B;
    }

    /// <summary>一次虚拟钻孔的完整结果。</summary>
    public sealed class VdResult
    {
        public double X, Y;
        public double? SurfaceZ;
        public List<VdSeamHit> Seams = new();
        public double TotalCoal, TotalRock;
        public double? ColumnTopZ, ColumnBottomZ, StripRatio;
        public int SeamsPresent;
    }

    /// <summary>钻孔柱上的一段(煤层或岩层), 自底向上。</summary>
    public sealed class VdColumnLayer
    {
        public bool IsCoal; public string Name = ""; public double BottomZ, TopZ; public double Thickness => TopZ - BottomZ; public byte R, G, B;
    }

    public const string VdLayerName = "虚拟钻孔";
    private const double VdRadius = 6.0;
    private const double VdSeamLabelHeight = 16.0;
    private const double VdLabelGap = 5.0;
    public static readonly (byte r, byte g, byte b) VdRockColor = (0x9A, 0x8C, 0x78);

    /// <summary>在 (x,y) 对模型各煤层顶/底板面竖直求交(原 Drill)。</summary>
    public static VdResult VdDrill(VdModel model, double x, double y)
    {
        var result = new VdResult { X = x, Y = y };
        if (model == null) return result;
        if (model.Surface != null && model.Surface.TrySampleZ(x, y, out double zs)) result.SurfaceZ = zs;
        foreach (var seam in model.Seams)
        {
            var hit = new VdSeamHit { Name = seam.Name, R = seam.R, G = seam.G, B = seam.B };
            double zr = 0, zf = 0;
            bool hr = seam.Roof != null && seam.Roof.TrySampleZ(x, y, out zr);
            bool hf = seam.Floor != null && seam.Floor.TrySampleZ(x, y, out zf);
            if (hr && hf && zr > zf + 1e-6) { hit.Present = true; hit.RoofZ = zr; hit.FloorZ = zf; }
            result.Seams.Add(hit);
        }
        var present = result.Seams.Where(s => s.Present).ToList();
        for (int i = 0; i < present.Count - 1; i++)
        {
            double gap = present[i].FloorZ - present[i + 1].RoofZ;
            present[i].Interval = gap > 0 ? gap : 0;
        }
        result.SeamsPresent = present.Count;
        result.TotalCoal = present.Sum(s => s.Thickness);
        if (present.Count > 0)
        {
            double deepestFloor = present.Min(s => s.FloorZ), topRoof = present.Max(s => s.RoofZ);
            double top = result.SurfaceZ ?? topRoof;
            if (top < topRoof) top = topRoof;
            result.ColumnTopZ = top; result.ColumnBottomZ = deepestFloor;
            result.TotalRock = Math.Max(0, top - deepestFloor - result.TotalCoal);
            result.StripRatio = result.TotalCoal > 1e-6 ? result.TotalRock / result.TotalCoal : null;
        }
        return result;
    }

    /// <summary>求交结果 → 自底向上铺满整柱的分层(煤层 + 夹层 + 上覆岩层)。</summary>
    public static List<VdColumnLayer> VdBuildColumnLayers(VdResult r)
    {
        var list = new List<VdColumnLayer>();
        if (r == null || r.ColumnTopZ == null || r.ColumnBottomZ == null) return list;
        var present = r.Seams.Where(s => s.Present).OrderBy(s => s.FloorZ).ToList();
        if (present.Count == 0) return list;
        double top = r.ColumnTopZ.Value, bottom = r.ColumnBottomZ.Value, cursor = bottom;
        foreach (var seam in present)
        {
            double bf = seam.FloorZ, bt = seam.RoofZ;
            if (bf < cursor) bf = cursor;
            if (bt <= cursor) continue;
            if (bf > cursor) list.Add(new VdColumnLayer { IsCoal = false, Name = "夹层", BottomZ = cursor, TopZ = bf, R = VdRockColor.r, G = VdRockColor.g, B = VdRockColor.b });
            list.Add(new VdColumnLayer { IsCoal = true, Name = seam.Name, BottomZ = bf, TopZ = bt, R = seam.R, G = seam.G, B = seam.B });
            cursor = bt;
        }
        if (cursor < top) list.Add(new VdColumnLayer { IsCoal = false, Name = "上覆岩层", BottomZ = cursor, TopZ = top, R = VdRockColor.r, G = VdRockColor.g, B = VdRockColor.b });
        return list;
    }

    /// <summary>
    /// 结果 → 三维柱的 2D 场景等价(原 BuildColumnPmbi): 分色柱段(煤层各自色/岩色) + 煤层厚度标柱右、岩层厚度标柱左 +
    /// 钻孔轴线(红) + 孔口「地表」标注(黄)。柱在 (x,y) 竖直向下 1:1 展开。空结果返回空表。
    /// </summary>
    public static List<SceneEntity> VdBuildColumnEntities(VdResult r, out double[]? bounds)
    {
        bounds = null;
        var list = new List<SceneEntity>();
        var layers = VdBuildColumnLayers(r);
        if (layers.Count == 0 || r.ColumnTopZ == null || r.ColumnBottomZ == null) return list;
        double x = r.X, y = r.Y, top = r.ColumnTopZ.Value, bottom = r.ColumnBottomZ.Value;
        double YOf(double z) => y - (top - z);
        double labelXR = x + VdRadius + VdLabelGap, labelXL = x - VdRadius - VdLabelGap;
        foreach (var layer in layers)
        {
            list.Add(new RectEntity { X0 = x - VdRadius, Y0 = YOf(layer.BottomZ), X1 = x + VdRadius, Y1 = YOf(layer.TopZ), Cr = layer.R / 255f, Cg = layer.G / 255f, Cb = layer.B / 255f });
            double midZ = (layer.BottomZ + layer.TopZ) * 0.5;
            if (layer.IsCoal)
            {
                list.Add(new LineEntity { X0 = x + VdRadius, Y0 = YOf(midZ), X1 = labelXR, Y1 = YOf(midZ), Cr = 0xF0 / 255f, Cg = 0xF0 / 255f, Cb = 0xF0 / 255f });
                list.Add(new TextEntity { X = labelXR, Y = YOf(midZ), Height = VdSeamLabelHeight, HAlign = 0, VAlign = 1, Text = $"{layer.Name}煤 厚{layer.Thickness:0.##}m", Cr = 0xF0 / 255f, Cg = 0xF0 / 255f, Cb = 0xF0 / 255f });
            }
            else if (layer.Thickness >= 0.5)
            {
                list.Add(new LineEntity { X0 = x - VdRadius, Y0 = YOf(midZ), X1 = labelXL, Y1 = YOf(midZ), Cr = 0xC8 / 255f, Cg = 0xB8 / 255f, Cb = 0xA0 / 255f });
                list.Add(new TextEntity { X = labelXL, Y = YOf(midZ), Height = VdSeamLabelHeight, HAlign = 2, VAlign = 1, Text = $"{layer.Name} 厚{layer.Thickness:0.##}m", Cr = 0xC8 / 255f, Cg = 0xB8 / 255f, Cb = 0xA0 / 255f });
            }
        }
        double axisTop = (r.SurfaceZ ?? top) + Math.Max(8.0, (top - bottom) * 0.06);
        list.Add(new LineEntity { X0 = x, Y0 = YOf(axisTop), X1 = x, Y1 = YOf(bottom), Cr = 0xE0 / 255f, Cg = 0x30 / 255f, Cb = 0x30 / 255f });
        string collar = r.SurfaceZ.HasValue ? $"地表 {r.SurfaceZ.Value:0.#}" : "地表";
        list.Add(new TextEntity { X = x, Y = YOf(axisTop + VdSeamLabelHeight * 0.6), Height = VdSeamLabelHeight, HAlign = 1, VAlign = 1, Text = collar, Cr = 1f, Cg = 0xE0 / 255f, Cb = 0f });
        bounds = new[] { labelXL - VdSeamLabelHeight * 8, YOf(bottom), labelXR + VdSeamLabelHeight * 8, YOf(axisTop + VdSeamLabelHeight * 1.5) };
        return list;
    }
}
