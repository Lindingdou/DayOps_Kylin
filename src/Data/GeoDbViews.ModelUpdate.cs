using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 「三维地质建模 → 更新地质模型」组(补勘钻孔写实 / 现状写实 / 更新煤层面)的查询、写库与纯逻辑 ——
/// 逐字移植原 GeoDataBase 的 SupplementaryBoreholeService / CurrentStateService(Dapper 直写 SQLite)、
/// SupplementaryBoreholeExcelIo / CurrentStatePointExcelIo(Excel → CSV 同列头)、MeshEditLib.ModelUpdate.SeamStructureConfig
/// (IUserSettings → 本地 JSON 记住)。窗口层只做绑定与交互。方法名带 Sup/Cs/Mu 前缀避免与其它组冲突。
/// </summary>
public static partial class GeoDbViews
{
    // ═════════════════════════════════════════════════════════════════════
    //  补勘写实批次 / 补勘孔 / 层位 (原 SupplementaryBoreholeService)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>补勘写实批次(supplementary_batch)。HoleCount 派生不落库。</summary>
    public sealed class SupBatchRow
    {
        public long Id { get; set; }
        public string Label { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Source { get; set; }
        public string? Remark { get; set; }
        public int HoleCount { get; set; }
    }

    /// <summary>补勘钻孔(supplementary_borehole)。</summary>
    public sealed class SupHoleRow
    {
        public long Id { get; set; }
        public string HoleId { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double? ZCollar { get; set; }
        public string? Remark { get; set; }
        public long? BatchId { get; set; }
    }

    /// <summary>补勘煤层顶/底板层位(supplementary_seam_horizon, 一孔一层一条)。</summary>
    public sealed class SupHorizonRow
    {
        public long Id { get; set; }
        public long SupBoreholeId { get; set; }
        public string SeamCode { get; set; } = "";
        public double? RoofElevation { get; set; }
        public double? FloorElevation { get; set; }
        public int SortOrder { get; set; }
        public string? Remark { get; set; }
    }

    private const string SupBatchSelect = "SELECT id AS Id, label AS Label, name AS Name, source AS Source, remark AS Remark FROM supplementary_batch";
    private const string SupHoleSelect = "SELECT id AS Id, hole_id AS HoleId, x AS X, y AS Y, z_collar AS ZCollar, remark AS Remark, batch_id AS BatchId FROM supplementary_borehole";
    private const string SupHorizonSelect = "SELECT id AS Id, sup_borehole_id AS SupBoreholeId, seam_code AS SeamCode, roof_elevation AS RoofElevation, floor_elevation AS FloorElevation, sort_order AS SortOrder, remark AS Remark FROM supplementary_seam_horizon";

    /// <summary>全部批次(按标签倒序 = 最新在前, 含 HoleCount)。</summary>
    public static List<SupBatchRow> SupAllBatches(SqliteConnection conn)
    {
        var list = conn.Query<SupBatchRow>(SupBatchSelect + " ORDER BY label DESC").ToList();
        foreach (var b in list)
            b.HoleCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM supplementary_borehole WHERE batch_id = @b", new { b = b.Id });
        return list;
    }

    public static SupBatchRow? SupGetBatch(SqliteConnection conn, long id)
        => conn.Query<SupBatchRow>(SupBatchSelect + " WHERE id = @id", new { id }).FirstOrDefault();

    /// <summary>新建批次: 系统唯一时间标签 SBW-yyyyMMdd-HHmmss[-nn], 友好名默认 "{来源} yyyy-MM-dd HH:mm"; 返回 id。</summary>
    public static long SupCreateBatch(SqliteConnection conn, string source, string? name = null, DateTime? now = null)
    {
        string label = MuUniqueLabel(conn, "supplementary_batch", "SBW", now ?? DateTime.Now);
        return conn.ExecuteScalar<long>(
            "INSERT INTO supplementary_batch(label, name, source) VALUES(@label, @name, @source); SELECT last_insert_rowid();",
            new { label, name = string.IsNullOrWhiteSpace(name) ? MuDefaultBatchName(source, label) : name!, source });
    }

    public static void SupRenameBatch(SqliteConnection conn, long id, string name)
        => conn.Execute("UPDATE supplementary_batch SET name = @name WHERE id = @id", new { id, name = name ?? "" });

    /// <summary>删除批次: 连带删该批全部孔+层位, 返回删除的孔数。</summary>
    public static int SupDeleteBatch(SqliteConnection conn, long id)
    {
        var holeIds = conn.Query<long>("SELECT id FROM supplementary_borehole WHERE batch_id = @b", new { b = id }).ToList();
        foreach (var hid in holeIds)
            conn.Execute("DELETE FROM supplementary_seam_horizon WHERE sup_borehole_id = @h", new { h = hid });
        conn.Execute("DELETE FROM supplementary_borehole WHERE batch_id = @b", new { b = id });
        conn.Execute("DELETE FROM supplementary_batch WHERE id = @id", new { id });
        return holeIds.Count;
    }

    // 系统唯一时间标签: {prefix}-yyyyMMdd-HHmmss, 同秒并发再加 -nn 直到唯一。
    private static string MuUniqueLabel(SqliteConnection conn, string table, string prefix, DateTime now)
    {
        string head = prefix + "-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string label = head;
        int n = 1;
        while (conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE label = @l", new { l = label }) > 0)
            label = head + "-" + (++n).ToString("00", CultureInfo.InvariantCulture);
        return label;
    }

    /// <summary>从 label(XXX-yyyyMMdd-HHmmss) 取时刻做友好名 "{source} yyyy-MM-dd HH:mm"。</summary>
    public static string MuDefaultBatchName(string source, string label)
    {
        string t = label.Length >= 19
            ? $"{label.Substring(4, 4)}-{label.Substring(8, 2)}-{label.Substring(10, 2)} {label.Substring(13, 2)}:{label.Substring(15, 2)}"
            : label;
        return $"{source} {t}";
    }

    /// <summary>列出补勘孔(batchId=null 则全部; 否则仅该批次), 按孔号排序。</summary>
    public static List<SupHoleRow> SupAllHoles(SqliteConnection conn, long? batchId = null)
        => batchId is { } b
            ? conn.Query<SupHoleRow>(SupHoleSelect + " WHERE batch_id = @b ORDER BY hole_id", new { b }).ToList()
            : conn.Query<SupHoleRow>(SupHoleSelect + " ORDER BY hole_id").ToList();

    public static SupHoleRow? SupGetHole(SqliteConnection conn, long id)
        => conn.Query<SupHoleRow>(SupHoleSelect + " WHERE id = @id", new { id }).FirstOrDefault();

    public static SupHoleRow? SupGetHoleByHoleId(SqliteConnection conn, string holeId)
        => conn.Query<SupHoleRow>(SupHoleSelect + " WHERE hole_id = @h", new { h = holeId }).FirstOrDefault();

    public static long SupInsertHole(SqliteConnection conn, SupHoleRow h)
    {
        h.Id = conn.ExecuteScalar<long>(
            "INSERT INTO supplementary_borehole(hole_id, x, y, z_collar, remark, batch_id) VALUES(@HoleId, @X, @Y, @ZCollar, @Remark, @BatchId); SELECT last_insert_rowid();", h);
        return h.Id;
    }

    public static int SupUpdateHole(SqliteConnection conn, SupHoleRow h)
        => conn.Execute("UPDATE supplementary_borehole SET hole_id = @HoleId, x = @X, y = @Y, z_collar = @ZCollar, remark = @Remark, batch_id = @BatchId WHERE id = @Id", h);

    /// <summary>删除补勘孔(显式先删层位, 不依赖 PRAGMA foreign_keys)。</summary>
    public static void SupDeleteHole(SqliteConnection conn, long id)
    {
        conn.Execute("DELETE FROM supplementary_seam_horizon WHERE sup_borehole_id = @id", new { id });
        conn.Execute("DELETE FROM supplementary_borehole WHERE id = @id", new { id });
    }

    public static List<SupHorizonRow> SupHorizonsByHole(SqliteConnection conn, long supBoreholeId)
        => conn.Query<SupHorizonRow>(SupHorizonSelect + " WHERE sup_borehole_id = @id ORDER BY sort_order", new { id = supBoreholeId }).ToList();

    /// <summary>整体替换某孔的层位(先删旧层再批量插新层), 保证一孔层位一致。</summary>
    public static void SupReplaceHorizons(SqliteConnection conn, long supBoreholeId, IEnumerable<SupHorizonRow> horizons)
    {
        conn.Execute("DELETE FROM supplementary_seam_horizon WHERE sup_borehole_id = @id", new { id = supBoreholeId });
        foreach (var h in horizons)
        {
            h.SupBoreholeId = supBoreholeId;
            h.Id = conn.ExecuteScalar<long>(
                "INSERT INTO supplementary_seam_horizon(sup_borehole_id, seam_code, roof_elevation, floor_elevation, sort_order, remark) VALUES(@SupBoreholeId, @SeamCode, @RoofElevation, @FloorElevation, @SortOrder, @Remark); SELECT last_insert_rowid();", h);
        }
    }

    /// <summary>全部层位(按孔、层序), 用于按煤层聚合作观测数据。</summary>
    public static List<SupHorizonRow> SupAllHorizons(SqliteConnection conn)
        => conn.Query<SupHorizonRow>(SupHorizonSelect + " ORDER BY sup_borehole_id, sort_order").ToList();

    /// <summary>
    /// 「更新煤层面」观测点: 某煤层某层位(顶板/底板)的补勘孔高程 (x, y, z, 批次名)。
    /// 忠实原 UpdateSeamSurfaceWindow.OnLoadObs 的补勘分支(bhBatch=null 跨批次全取)。
    /// </summary>
    public static List<(double x, double y, double z, string batch)> SupObservations(SqliteConnection conn, string seamCode, string horizon, long? batchId)
    {
        var names = SupAllBatches(conn).ToDictionary(b => b.Id, b => MuBatchName(b.Name, b.Label));
        var holes = SupAllHoles(conn, batchId);
        var xy = holes.ToDictionary(h => h.Id, h => (h.X, h.Y, h.BatchId));
        var list = new List<(double, double, double, string)>();
        foreach (var hz in SupAllHorizons(conn))
        {
            if (hz.SeamCode != seamCode || !xy.TryGetValue(hz.SupBoreholeId, out var pos)) continue;
            double? z = horizon == "顶板" ? hz.RoofElevation : hz.FloorElevation;
            if (z == null) continue;
            list.Add((pos.X, pos.Y, z.Value, pos.BatchId.HasValue && names.TryGetValue(pos.BatchId.Value, out var n) ? n : ""));
        }
        return list;
    }

    /// <summary>批次友好名(原 BatchName): 名字空则用时间标签。</summary>
    public static string MuBatchName(string? name, string? label) => string.IsNullOrWhiteSpace(name) ? (label ?? "") : name;

    // ═════════════════════════════════════════════════════════════════════
    //  现状写实批次 / 现状见煤点 (原 CurrentStateService)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>现状写实批次(current_state_batch)。PointCount 派生不落库。</summary>
    public sealed class CsBatchRow
    {
        public long Id { get; set; }
        public string Label { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Source { get; set; }
        public string? Remark { get; set; }
        public int PointCount { get; set; }
    }

    /// <summary>现状见煤点(current_state_point; V033 起带 煤层编号 + 顶/底板)。</summary>
    public sealed class CsPointRow
    {
        public long Id { get; set; }
        public long BatchId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? Remark { get; set; }
        public string? SeamCode { get; set; }
        public string? Horizon { get; set; }
    }

    private const string CsBatchSelect = "SELECT id AS Id, label AS Label, name AS Name, source AS Source, remark AS Remark FROM current_state_batch";
    private const string CsPointSelect = "SELECT id AS Id, batch_id AS BatchId, x AS X, y AS Y, z AS Z, remark AS Remark, seam_code AS SeamCode, horizon AS Horizon FROM current_state_point";

    public static List<CsBatchRow> CsAllBatches(SqliteConnection conn)
    {
        var list = conn.Query<CsBatchRow>(CsBatchSelect + " ORDER BY label DESC").ToList();
        foreach (var b in list)
            b.PointCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM current_state_point WHERE batch_id = @b", new { b = b.Id });
        return list;
    }

    public static CsBatchRow? CsGetBatch(SqliteConnection conn, long id)
        => conn.Query<CsBatchRow>(CsBatchSelect + " WHERE id = @id", new { id }).FirstOrDefault();

    /// <summary>新建现状批次: 标签 CSR-yyyyMMdd-HHmmss[-nn]; 返回 id。</summary>
    public static long CsCreateBatch(SqliteConnection conn, string source, string? name = null, DateTime? now = null)
    {
        string label = MuUniqueLabel(conn, "current_state_batch", "CSR", now ?? DateTime.Now);
        return conn.ExecuteScalar<long>(
            "INSERT INTO current_state_batch(label, name, source) VALUES(@label, @name, @source); SELECT last_insert_rowid();",
            new { label, name = string.IsNullOrWhiteSpace(name) ? MuDefaultBatchName(source, label) : name!, source });
    }

    public static void CsRenameBatch(SqliteConnection conn, long id, string name)
        => conn.Execute("UPDATE current_state_batch SET name = @name WHERE id = @id", new { id, name = name ?? "" });

    /// <summary>删除批次: 连带删该批全部现状点, 返回删除的点数。</summary>
    public static int CsDeleteBatch(SqliteConnection conn, long id)
    {
        int n = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM current_state_point WHERE batch_id = @b", new { b = id });
        conn.Execute("DELETE FROM current_state_point WHERE batch_id = @b", new { b = id });
        conn.Execute("DELETE FROM current_state_batch WHERE id = @id", new { id });
        return n;
    }

    public static List<CsPointRow> CsPointsByBatch(SqliteConnection conn, long batchId)
        => conn.Query<CsPointRow>(CsPointSelect + " WHERE batch_id = @b ORDER BY id", new { b = batchId }).ToList();

    /// <summary>全部现状点(跨批次; 供按煤层+顶/底筛选作观测数据)。</summary>
    public static List<CsPointRow> CsAllPoints(SqliteConnection conn)
        => conn.Query<CsPointRow>(CsPointSelect + " ORDER BY id").ToList();

    /// <summary>整体替换某批次的点(先删旧点再批量插)。</summary>
    public static void CsReplacePoints(SqliteConnection conn, long batchId, IEnumerable<CsPointRow> points)
    {
        conn.Execute("DELETE FROM current_state_point WHERE batch_id = @b", new { b = batchId });
        foreach (var p in points)
        {
            p.BatchId = batchId;
            p.Id = conn.ExecuteScalar<long>(
                "INSERT INTO current_state_point(batch_id, x, y, z, remark, seam_code, horizon) VALUES(@BatchId, @X, @Y, @Z, @Remark, @SeamCode, @Horizon); SELECT last_insert_rowid();", p);
        }
    }

    /// <summary>「更新煤层面」观测点: 某煤层某层位的现状见煤点 (x, y, z, 批次名); csBatch=null 跨批次全取。</summary>
    public static List<(double x, double y, double z, string batch)> CsObservations(SqliteConnection conn, string seamCode, string horizon, long? batchId)
    {
        var names = CsAllBatches(conn).ToDictionary(b => b.Id, b => MuBatchName(b.Name, b.Label));
        var list = new List<(double, double, double, string)>();
        foreach (var p in CsAllPoints(conn))
            if (p.SeamCode == seamCode && p.Horizon == horizon && (batchId == null || p.BatchId == batchId.Value))
                list.Add((p.X, p.Y, p.Z, names.TryGetValue(p.BatchId, out var n) ? n : ""));
        return list;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  本地设置(原 IUserSettings: 煤层结构 / 标注开关字高 全局记住)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>设置文件路径(JSON 键值); 测试可改指向临时目录。null=默认 LocalApplicationData/PitMine3D.Kylin/model_update_settings.json。</summary>
    public static string? MuSettingsPath { get; set; }

    private static string MuSettingsFile()
        => MuSettingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine3D.Kylin", "model_update_settings.json");

    private static Dictionary<string, JsonElement> MuReadAll()
    {
        try
        {
            string f = MuSettingsFile();
            if (!File.Exists(f)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(f)) ?? new();
        }
        catch { return new(); }
    }

    public static T? MuSettingsGet<T>(string key) where T : class
    {
        try
        {
            var all = MuReadAll();
            return all.TryGetValue(key, out var el) ? el.Deserialize<T>() : null;
        }
        catch { return null; }
    }

    public static void MuSettingsSet<T>(string key, T value)
    {
        var all = MuReadAll();
        all[key] = JsonSerializer.SerializeToElement(value);
        string f = MuSettingsFile();
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    // ═════════════════════════════════════════════════════════════════════
    //  煤层结构(原 SeamStructureConfig): 有几层煤 + 先后顺序(上→下 = 浅→深)
    // ═════════════════════════════════════════════════════════════════════

    public sealed class SeamStructureConfig
    {
        public const string SettingsKey = "geo.realistic.seam.structure";

        public sealed class SeamItem
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public override string ToString() => Name;
        }

        /// <summary>煤层项(顺序即上→下)。</summary>
        public List<SeamItem> Seams { get; set; } = new();

        /// <summary>供 CSV IO / 重建器使用的 (code,name) 序列。</summary>
        public List<(string code, string name)> AsPairs()
        {
            var list = new List<(string, string)>();
            foreach (var s in Seams) list.Add((s.Code, s.Name));
            return list;
        }

        /// <summary>读已记住的结构; 没有则从煤层字典(coal_seam_def, 按 sort_order 浅→深)生成默认。</summary>
        public static SeamStructureConfig LoadOrDefault(SqliteConnection? conn)
        {
            var cfg = MuSettingsGet<SeamStructureConfig>(SettingsKey);
            if (cfg != null && cfg.Seams.Count > 0) return cfg;
            return Default(conn);
        }

        /// <summary>默认结构 = 煤层字典全部煤层(浅→深)。字典不可用则留空, 交用户手动增删。</summary>
        public static SeamStructureConfig Default(SqliteConnection? conn)
        {
            var cfg = new SeamStructureConfig();
            try
            {
                if (conn != null)
                    foreach (var s in LoadSeamDefs(conn))
                        cfg.Seams.Add(new SeamItem { Code = s.Code, Name = s.Name });
            }
            catch { /* 字典缺失: 留空, 用户在「煤层结构…」里手动加 */ }
            return cfg;
        }

        public void Save() => MuSettingsSet(SettingsKey, this);

        public string NameFor(string? code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            var it = Seams.FirstOrDefault(s => s.Code == code);
            return it != null ? it.Name : code!;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  补勘钻孔写实 CSV 模板 / 解析 (原 SupplementaryBoreholeExcelIo, 列随煤层结构动态生成)
    // ═════════════════════════════════════════════════════════════════════

    public const string SupHHole = "孔号", SupHX = "经距X", SupHY = "纬距Y", SupHZ = "孔口高程";
    public static string SupRoofHeader(string seamName) => $"{seamName}顶板高程";
    public static string SupFloorHeader(string seamName) => $"{seamName}底板高程";

    /// <summary>解析出的单孔写实数据。</summary>
    public sealed class SupParsedHole
    {
        public string HoleId = "";
        public double? X, Y, ZCollar;
        /// <summary>煤层编号 → (顶板高程, 底板高程)。仅含表中填了值的层。</summary>
        public readonly Dictionary<string, (double? roof, double? floor)> Horizons = new();
    }

    /// <summary>按配置煤层解析整份 CSV 文本为逐孔写实数据。seams 顺序 = 煤层结构(浅→深)。</summary>
    public static List<SupParsedHole> SupParseCsv(string text, IReadOnlyList<(string code, string name)> seams, out string status)
    {
        var result = new List<SupParsedHole>();
        List<List<string>> records;
        try { records = BoreholeReadCsvRecords(text); }
        catch (Exception ex) { status = $"读取失败：{ex.Message}"; return result; }
        if (records.Count < 2) { status = "文件为空或只有表头"; return result; }

        var header = records[0];
        int idxHole = MuFindCol(header, SupHHole, "孔号", "hole_id", "holeid", "钻孔编号", "钻孔号");
        int idxX = MuFindCol(header, SupHX, "经距x", "经距", "x");
        int idxY = MuFindCol(header, SupHY, "纬距y", "纬距", "y");
        int idxZ = MuFindCol(header, SupHZ, "孔口高程", "高程", "z", "z_collar");
        if (idxHole < 0) { status = "缺少必要列：孔号"; return result; }

        var roofCol = new Dictionary<string, int>();
        var floorCol = new Dictionary<string, int>();
        foreach (var s in seams)
        {
            roofCol[s.code] = MuFindCol(header, SupRoofHeader(s.name), $"{s.code}顶板高程", $"{s.name}顶板", $"{s.code}顶板");
            floorCol[s.code] = MuFindCol(header, SupFloorHeader(s.name), $"{s.code}底板高程", $"{s.name}底板", $"{s.code}底板");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dup = 0;
        for (int r = 1; r < records.Count; r++)
        {
            var f = records[r];
            string hole = MuCell(f, idxHole);
            if (string.IsNullOrWhiteSpace(hole)) continue;
            if (!seen.Add(hole)) { dup++; continue; }
            var ph = new SupParsedHole
            {
                HoleId = hole,
                X = BoreholeParseNum(MuCell(f, idxX)),
                Y = BoreholeParseNum(MuCell(f, idxY)),
                ZCollar = BoreholeParseNum(MuCell(f, idxZ)),
            };
            foreach (var s in seams)
            {
                double? roof = BoreholeParseNum(MuCell(f, roofCol[s.code]));
                double? floor = BoreholeParseNum(MuCell(f, floorCol[s.code]));
                if (roof != null || floor != null) ph.Horizons[s.code] = (roof, floor);
            }
            result.Add(ph);
        }
        status = $"解析 {result.Count} 孔" + (dup > 0 ? $"，跳过 {dup} 个重复孔号" : "");
        return result;
    }

    public static List<string> SupCsvHeaders(IReadOnlyList<(string code, string name)> seams)
    {
        var h = new List<string> { SupHHole, SupHX, SupHY, SupHZ };
        foreach (var s in seams) { h.Add(SupRoofHeader(s.name)); h.Add(SupFloorHeader(s.name)); }
        return h;
    }

    private static List<string> SupCsvExample(IReadOnlyList<(string code, string name)> seams)
    {
        var e = new List<string> { "BK001", "37500000.0", "4400000.0", "1285.6" };
        double top = 1200;
        foreach (var _ in seams)
        {
            e.Add(top.ToString("0.0", CultureInfo.InvariantCulture));
            e.Add((top - 5).ToString("0.0", CultureInfo.InvariantCulture));
            top -= 30;
        }
        return e;
    }

    /// <summary>CSV 模板文本(表头 + 示例行; BOM 由 SaveTextAsync 写出时加)。原 WriteCsvTemplate。</summary>
    public static string SupCsvTemplate(IReadOnlyList<(string code, string name)> seams)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", SupCsvHeaders(seams).Select(BoreholeCsvField)));
        sb.AppendLine(string.Join(",", SupCsvExample(seams).Select(BoreholeCsvField)));
        return sb.ToString();
    }

    /// <summary>把某批次(或全部)补勘孔 + 层位导出为与模板同列头的 CSV(逐孔一行)。</summary>
    public static string SupExportCsv(SqliteConnection conn, long? batchId, IReadOnlyList<(string code, string name)> seams)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", SupCsvHeaders(seams).Select(BoreholeCsvField)));
        foreach (var h in SupAllHoles(conn, batchId))
        {
            var hz = SupHorizonsByHole(conn, h.Id).ToDictionary(z => z.SeamCode, z => z);
            var f = new List<string> { h.HoleId, h.X.ToString(CultureInfo.InvariantCulture), h.Y.ToString(CultureInfo.InvariantCulture), h.ZCollar?.ToString(CultureInfo.InvariantCulture) ?? "" };
            foreach (var s in seams)
            {
                hz.TryGetValue(s.code, out var z);
                f.Add(z?.RoofElevation?.ToString(CultureInfo.InvariantCulture) ?? "");
                f.Add(z?.FloorElevation?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
            sb.AppendLine(string.Join(",", f.Select(BoreholeCsvField)));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把解析结果入库(原 OnImportExcel 主体): 新建「CSV导入」批次, 孔号已存在则整孔覆盖(归入导入批次),
    /// 层位按煤层结构顺序整体替换; 一孔都没成则删掉空批次。返回 (批次 id 或 null, 新增, 更新, 失败)。
    /// </summary>
    public static (long? batchId, int ins, int upd, int fail) SupImportParsed(SqliteConnection conn, IReadOnlyList<SupParsedHole> parsed, SeamStructureConfig config, string source = "CSV导入")
    {
        long importBatch = SupCreateBatch(conn, source);
        int ins = 0, upd = 0, fail = 0;
        foreach (var ph in parsed)
        {
            try
            {
                var hole = SupGetHoleByHoleId(conn, ph.HoleId);
                bool isNew = hole is null;
                hole ??= new SupHoleRow { HoleId = ph.HoleId };
                hole.X = ph.X ?? hole.X;
                hole.Y = ph.Y ?? hole.Y;
                hole.ZCollar = ph.ZCollar ?? hole.ZCollar;
                hole.BatchId = importBatch;
                if (hole.Id <= 0) SupInsertHole(conn, hole); else SupUpdateHole(conn, hole);

                var horizons = new List<SupHorizonRow>();
                int order = 0;
                foreach (var seam in config.Seams)
                {
                    if (ph.Horizons.TryGetValue(seam.Code, out var rf) && (rf.roof != null || rf.floor != null))
                        horizons.Add(new SupHorizonRow { SeamCode = seam.Code, RoofElevation = rf.roof, FloorElevation = rf.floor, SortOrder = order });
                    order++;
                }
                SupReplaceHorizons(conn, hole.Id, horizons);
                if (isNew) ins++; else upd++;
            }
            catch { fail++; }
        }
        if (ins + upd == 0) { SupDeleteBatch(conn, importBatch); return (null, ins, upd, fail); }
        return (importBatch, ins, upd, fail);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  现状点 CSV 模板 / 解析 (原 CurrentStatePointExcelIo; V033 后追加可选 煤层编号/层位 两列)
    // ═════════════════════════════════════════════════════════════════════

    public static readonly string[] CsCsvHeaders = { "经距X", "纬距Y", "高程Z", "备注", "煤层编号", "层位" };
    public static readonly string[] CsCsvExample = { "37500000.0", "4400000.0", "1180.5", "示例行，可删除", "4", "顶板" };

    public sealed class CsParsedPoint
    {
        public double X, Y, Z;
        public string? Remark;
        public string? SeamCode;
        public string? Horizon;
    }

    public static List<CsParsedPoint> CsParseCsv(string text, out string status)
    {
        var result = new List<CsParsedPoint>();
        List<List<string>> records;
        try { records = BoreholeReadCsvRecords(text); }
        catch (Exception ex) { status = $"读取失败：{ex.Message}"; return result; }
        if (records.Count < 2) { status = "文件为空或只有表头"; return result; }

        var header = records[0];
        int ix = MuFindCol(header, "经距x", "经距", "x");
        int iy = MuFindCol(header, "纬距y", "纬距", "y");
        int iz = MuFindCol(header, "高程z", "高程", "z");
        int ir = MuFindCol(header, "备注", "remark");
        int isc = MuFindCol(header, "煤层编号", "煤层", "seam_code", "seam");
        int ih = MuFindCol(header, "层位", "horizon");
        if (ix < 0 || iy < 0 || iz < 0) { status = "缺少必要列：经距X / 纬距Y / 高程Z"; return result; }

        int bad = 0;
        for (int r = 1; r < records.Count; r++)
        {
            var f = records[r];
            double? x = BoreholeParseNum(MuCell(f, ix)), y = BoreholeParseNum(MuCell(f, iy)), z = BoreholeParseNum(MuCell(f, iz));
            if (x == null || y == null || z == null)
            {
                if (MuCell(f, ix).Length > 0 || MuCell(f, iy).Length > 0 || MuCell(f, iz).Length > 0) bad++;
                continue;
            }
            string hz = MuCell(f, ih);
            result.Add(new CsParsedPoint
            {
                X = x.Value, Y = y.Value, Z = z.Value,
                Remark = MuNullIfEmpty(MuCell(f, ir)),
                SeamCode = MuNullIfEmpty(MuCell(f, isc)),
                Horizon = hz == "底板" ? "底板" : hz.Length > 0 ? "顶板" : null,
            });
        }
        status = $"解析 {result.Count} 点" + (bad > 0 ? $"，跳过 {bad} 行(坐标非数字)" : "");
        return result;
    }

    public static string CsCsvTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsCsvHeaders.Select(BoreholeCsvField)));
        sb.AppendLine(string.Join(",", CsCsvExample.Select(BoreholeCsvField)));
        return sb.ToString();
    }

    /// <summary>某批次现状点导出为与模板同列头的 CSV。</summary>
    public static string CsExportCsv(IReadOnlyList<CsPointRow> points)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsCsvHeaders.Select(BoreholeCsvField)));
        foreach (var p in points)
            sb.AppendLine(string.Join(",", new[]
            {
                p.X.ToString("F3", CultureInfo.InvariantCulture), p.Y.ToString("F3", CultureInfo.InvariantCulture), p.Z.ToString("F3", CultureInfo.InvariantCulture),
                p.Remark ?? "", p.SeamCode ?? "", p.Horizon ?? "",
            }.Select(BoreholeCsvField)));
        return sb.ToString();
    }

    // ─────────────────────────── 工具 ───────────────────────────
    private static string MuCell(List<string> f, int idx) => idx >= 0 && idx < f.Count ? f[idx].Trim() : "";
    private static string? MuNullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>在表头行里按别名(去 BOM / 大小写不敏感)找列序号; 找不到返回 -1。</summary>
    private static int MuFindCol(List<string> header, params string[] aliases)
    {
        for (int i = 0; i < header.Count; i++)
        {
            string h = header[i].Trim().Trim('﻿').ToLowerInvariant();
            if (aliases.Any(a => h == a.Trim().ToLowerInvariant())) return i;
        }
        return -1;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  补勘钻孔写实展绘(孔位点 + 各层位顶/底点 + 孔柱 + 孔号文字) → 场景实体
    // ═════════════════════════════════════════════════════════════════════

    public const string SupDrawLayer = "补勘钻孔写实";

    /// <summary>
    /// 一孔一组实体: 孔位点(孔口高程, 外接圆)、孔号文字、逐层顶板/底板点(按煤层调色板着色, 顶=十字 底=叉)、
    /// 竖直孔柱(三维多段线: 孔口→最深底板, 缺孔口高程则从最高顶板起)。size = 点符号/字高基准(m)。
    /// </summary>
    public static List<SceneEntity> SupBuildHoleEntities(IReadOnlyList<SupHoleRow> holes, IReadOnlyDictionary<long, List<SupHorizonRow>> horizonsByHole,
        IReadOnlyList<SeamDefRow> defs, double size)
    {
        var ents = new List<SceneEntity>();
        double s = Math.Max(0.1, size);
        foreach (var h in holes)
        {
            horizonsByHole.TryGetValue(h.Id, out var hzs);
            hzs ??= new List<SupHorizonRow>();
            var zs = new List<double>();
            if (h.ZCollar is { } zc) zs.Add(zc);
            foreach (var z in hzs) { if (z.RoofElevation is { } r) zs.Add(r); if (z.FloorElevation is { } f) zs.Add(f); }
            double top = zs.Count > 0 ? zs.Max() : 0, bottom = zs.Count > 0 ? zs.Min() : 0;

            ents.Add(new PointEntity { X = h.X, Y = h.Y, Size = s, Style = 2 + 32, Elevation = h.ZCollar ?? top, LayerName = SupDrawLayer, Cr = 0.95f, Cg = 0.95f, Cb = 0.95f });
            ents.Add(new TextEntity { X = h.X + s * 1.2, Y = h.Y + s * 1.2, Height = s, Text = h.HoleId, Elevation = h.ZCollar ?? top, LayerName = SupDrawLayer, Cr = 0.95f, Cg = 0.95f, Cb = 0.95f });
            if (zs.Count >= 2 && top > bottom)
                ents.Add(new PolylineEntity
                {
                    Points = { (h.X, h.Y), (h.X, h.Y) }, Zs = new List<double> { top, bottom },
                    LayerName = SupDrawLayer, Cr = 0.7f, Cg = 0.7f, Cb = 0.7f,
                });
            foreach (var z in hzs)
            {
                var (r, g, b) = SeamPaletteColor(defs, z.SeamCode);
                float cr = r / 255f, cg = g / 255f, cb = b / 255f;
                if (z.RoofElevation is { } rz)
                    ents.Add(new PointEntity { X = h.X, Y = h.Y, Size = s * 0.7, Style = 2, Elevation = rz, LayerName = SupDrawLayer, Cr = cr, Cg = cg, Cb = cb });
                if (z.FloorElevation is { } fz)
                    ents.Add(new PointEntity { X = h.X, Y = h.Y, Size = s * 0.7, Style = 3, Elevation = fz, LayerName = SupDrawLayer, Cr = cr, Cg = cg, Cb = cb });
            }
        }
        return ents;
    }
}
