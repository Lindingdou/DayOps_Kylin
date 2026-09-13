using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;

namespace PitMine3D.Kylin.Data;

/// <summary>路网存档一条（对应 <c>road_network</c> 一行，V019）。忠实原 GeoDataBase.Public.Entities.RoadNetwork。</summary>
public sealed class RoadNetworkRecord
{
    public long Id;
    public string Name = "";
    /// <summary>所属/采集时刻（"什么时候的路网"）。</summary>
    public string CapturedAt = "";
    /// <summary>RoadGraphSerializer 序列化的图（节点+边），库里不解析。</summary>
    public string GraphJson = "{}";
    public int NodeCount;
    public int EdgeCount;
    public double LengthKm;
    public string? Note;
}

/// <summary>
/// 路网存档读写（忠实原 GeoDataBase.Domain.Services.RoadNetworkService 的 All/Get/Insert/Update/Delete）。
/// 与 <see cref="MineableRegions"/> 同一套写法：字面量 SQL，三种方言（SQLite / 达梦 / PG）都走得通。
/// </summary>
public static class RoadNetworkStore
{
    public static List<RoadNetworkRecord> List(DbConnection? conn)
    {
        var list = new List<RoadNetworkRecord>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, captured_at, graph_json, node_count, edge_count, length_km, note FROM road_network ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Read(rd));
        }
        catch { }
        return list;
    }

    public static RoadNetworkRecord? Get(DbConnection? conn, long id)
    {
        if (conn == null) return null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, captured_at, graph_json, node_count, edge_count, length_km, note FROM road_network WHERE id = "
                              + id.ToString(CultureInfo.InvariantCulture);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? Read(rd) : null;
        }
        catch { return null; }
    }

    /// <summary>新建，返回新 id（失败 0）。</summary>
    public static long Insert(DbConnection conn, RoadNetworkRecord r)
    {
        Exec(conn, "INSERT INTO road_network (name, captured_at, graph_json, node_count, edge_count, length_km, note) VALUES ("
                   + L(r.Name) + ", " + L(r.CapturedAt) + ", " + L(r.GraphJson) + ", "
                   + r.NodeCount.ToString(CultureInfo.InvariantCulture) + ", " + r.EdgeCount.ToString(CultureInfo.InvariantCulture) + ", "
                   + N(r.LengthKm) + ", " + L(r.Note) + ")");
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT MAX(id) FROM road_network";
        object? v = q.ExecuteScalar();
        r.Id = v == null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return r.Id;
    }

    public static void Update(DbConnection conn, RoadNetworkRecord r)
    {
        Exec(conn, "UPDATE road_network SET name = " + L(r.Name) + ", captured_at = " + L(r.CapturedAt)
                   + ", graph_json = " + L(r.GraphJson) + ", node_count = " + r.NodeCount.ToString(CultureInfo.InvariantCulture)
                   + ", edge_count = " + r.EdgeCount.ToString(CultureInfo.InvariantCulture) + ", length_km = " + N(r.LengthKm)
                   + ", note = " + L(r.Note) + " WHERE id = " + r.Id.ToString(CultureInfo.InvariantCulture));
    }

    public static void Delete(DbConnection conn, long id)
        => Exec(conn, "DELETE FROM road_network WHERE id = " + id.ToString(CultureInfo.InvariantCulture));

    private static RoadNetworkRecord Read(DbDataReader rd) => new()
    {
        Id = rd.IsDBNull(0) ? 0 : Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture),
        Name = S(rd, 1),
        CapturedAt = S(rd, 2),
        GraphJson = S(rd, 3),
        NodeCount = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture),
        EdgeCount = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5), CultureInfo.InvariantCulture),
        LengthKm = D(rd, 6),
        Note = rd.IsDBNull(7) ? null : rd.GetValue(7)?.ToString(),
    };

    internal static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    internal static string L(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";
    internal static string N(double v) => (double.IsNaN(v) || double.IsInfinity(v) ? 0 : v).ToString("R", CultureInfo.InvariantCulture);
    internal static string S(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    internal static double D(DbDataReader rd, int i) => rd.IsDBNull(i) ? 0 : Convert.ToDouble(rd.GetValue(i), CultureInfo.InvariantCulture);
}

/// <summary>中心线存档一条（对应 <c>road_centerline_set</c> 一行，V045）。忠实原 GeoDataBase.Public.Entities.RoadCenterlineSet。</summary>
public sealed class CenterlineSetRecord
{
    public long Id;
    public string Name = "";
    public string CapturedAt = "";
    /// <summary>来路标注：提取/手动/管理整理…（纯留痕）。</summary>
    public string Source = "";
    /// <summary>GZip 二进制 base64（RCL1，见 CenterlineSetCodec），库里不解析。</summary>
    public string GeometryB64 = "";
    public int LineCount;
    public int VertexCount;
    public double LengthKm;
    public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    public string? Note;
}

/// <summary>中心线存档读写（忠实原 RoadCenterlineSetService）。</summary>
public static class CenterlineSetStore
{
    private const string Cols = "id, name, captured_at, source, geometry_b64, line_count, vertex_count, length_km, min_x, min_y, min_z, max_x, max_y, max_z, note";

    public static List<CenterlineSetRecord> List(DbConnection? conn)
    {
        var list = new List<CenterlineSetRecord>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + Cols + " FROM road_centerline_set ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Read(rd));
        }
        catch { }
        return list;
    }

    public static CenterlineSetRecord? Get(DbConnection? conn, long id)
    {
        if (conn == null) return null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + Cols + " FROM road_centerline_set WHERE id = " + id.ToString(CultureInfo.InvariantCulture);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? Read(rd) : null;
        }
        catch { return null; }
    }

    public static long Insert(DbConnection conn, CenterlineSetRecord r)
    {
        RoadNetworkStore.Exec(conn,
            "INSERT INTO road_centerline_set (name, captured_at, source, geometry_b64, line_count, vertex_count, length_km, min_x, min_y, min_z, max_x, max_y, max_z, note) VALUES ("
            + RoadNetworkStore.L(r.Name) + ", " + RoadNetworkStore.L(r.CapturedAt) + ", " + RoadNetworkStore.L(r.Source) + ", "
            + RoadNetworkStore.L(r.GeometryB64) + ", " + r.LineCount.ToString(CultureInfo.InvariantCulture) + ", "
            + r.VertexCount.ToString(CultureInfo.InvariantCulture) + ", " + RoadNetworkStore.N(r.LengthKm) + ", "
            + RoadNetworkStore.N(r.MinX) + ", " + RoadNetworkStore.N(r.MinY) + ", " + RoadNetworkStore.N(r.MinZ) + ", "
            + RoadNetworkStore.N(r.MaxX) + ", " + RoadNetworkStore.N(r.MaxY) + ", " + RoadNetworkStore.N(r.MaxZ) + ", "
            + RoadNetworkStore.L(r.Note) + ")");
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT MAX(id) FROM road_centerline_set";
        object? v = q.ExecuteScalar();
        r.Id = v == null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return r.Id;
    }

    /// <summary>只改元数据（名称/备注）——几何列不重写（几十 MB 的 base64 没必要再传一遍）。</summary>
    public static void UpdateMeta(DbConnection conn, CenterlineSetRecord r)
        => RoadNetworkStore.Exec(conn, "UPDATE road_centerline_set SET name = " + RoadNetworkStore.L(r.Name)
                                       + ", note = " + RoadNetworkStore.L(r.Note)
                                       + " WHERE id = " + r.Id.ToString(CultureInfo.InvariantCulture));

    public static void Delete(DbConnection conn, long id)
        => RoadNetworkStore.Exec(conn, "DELETE FROM road_centerline_set WHERE id = " + id.ToString(CultureInfo.InvariantCulture));

    private static CenterlineSetRecord Read(DbDataReader rd) => new()
    {
        Id = rd.IsDBNull(0) ? 0 : Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture),
        Name = RoadNetworkStore.S(rd, 1),
        CapturedAt = RoadNetworkStore.S(rd, 2),
        Source = RoadNetworkStore.S(rd, 3),
        GeometryB64 = RoadNetworkStore.S(rd, 4),
        LineCount = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5), CultureInfo.InvariantCulture),
        VertexCount = rd.IsDBNull(6) ? 0 : Convert.ToInt32(rd.GetValue(6), CultureInfo.InvariantCulture),
        LengthKm = RoadNetworkStore.D(rd, 7),
        MinX = RoadNetworkStore.D(rd, 8), MinY = RoadNetworkStore.D(rd, 9), MinZ = RoadNetworkStore.D(rd, 10),
        MaxX = RoadNetworkStore.D(rd, 11), MaxY = RoadNetworkStore.D(rd, 12), MaxZ = RoadNetworkStore.D(rd, 13),
        Note = rd.IsDBNull(14) ? null : rd.GetValue(14)?.ToString(),
    };
}

/// <summary>装卸点台账一行（<c>load_unload_point</c>，V017）。忠实原 GeoDataBase.Public.Entities.LoadUnloadPoint 的路网侧用到的列。</summary>
public sealed class LoadUnloadRecord
{
    public long Id;
    public string Name = "";
    /// <summary>loading(采剥点/源) | unloading(卸载点/汇)。</summary>
    public string Kind = "loading";
    /// <summary>crusher | dump | stockpile（仅卸载点）。</summary>
    public string? UnloadSub;
    public double X, Y, Z, ThroughputTph;
    public string? Note;
    public bool IsLoading => string.Equals(Kind, "loading", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条待入账的卸载点（「等效运距」里视口点的候选位置 + 用户补的身份）。忠实原 RoadLib.Transport.SinkSpec。</summary>
public readonly record struct SinkSpec(string Name, double X, double Y, double Z, string UnloadSub, double ThroughputTph, string? DumpSiteId);

/// <summary>入账结果（回显用）。</summary>
public readonly record struct SinkSaveResult(int Inserted, int Updated, IReadOnlyList<string> Names);

/// <summary>
/// 装卸点台账整表读取 + 候选卸点入账（忠实原 ILoadUnloadPointService.All + RoadLib.Transport.SinkCandidateStore）。
/// 只增不删、主键不重排、挂接关系记进 note（<c>dump:{dumpId}</c> 前缀）—— 三条口径原样。
/// </summary>
public static class LoadUnloadPointStore
{
    public const string KindUnloading = "unloading";
    public const string DumpNotePrefix = "dump:";

    public static List<LoadUnloadRecord> LoadAll(DbConnection? conn)
    {
        var list = new List<LoadUnloadRecord>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, kind, unload_sub, x, y, z, throughput_tph, note FROM load_unload_point ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new LoadUnloadRecord
                {
                    Id = rd.IsDBNull(0) ? 0 : Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture),
                    Name = RoadNetworkStore.S(rd, 1),
                    Kind = RoadNetworkStore.S(rd, 2),
                    UnloadSub = rd.IsDBNull(3) ? null : rd.GetValue(3)?.ToString(),
                    X = RoadNetworkStore.D(rd, 4), Y = RoadNetworkStore.D(rd, 5), Z = RoadNetworkStore.D(rd, 6),
                    ThroughputTph = RoadNetworkStore.D(rd, 7),
                    Note = rd.IsDBNull(8) ? null : rd.GetValue(8)?.ToString(),
                });
        }
        catch { }
        return list;
    }

    /// <summary>排土场档案（<c>dump_site</c> 活跃行）：(dump_id, name)，供候选卸点挂接。</summary>
    public static List<(string Id, string Name)> ActiveDumpSites(DbConnection? conn)
    {
        var list = new List<(string, string)>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT dump_id, name FROM dump_site WHERE status = 'active' ORDER BY dump_id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add((RoadNetworkStore.S(rd, 0), RoadNetworkStore.S(rd, 1)));
        }
        catch { }
        return list;
    }

    /// <summary>从 note 里读回挂接的排土场 Id（没挂返回 null）。</summary>
    public static string? DumpSiteIdOf(LoadUnloadRecord? p)
    {
        var note = p?.Note;
        if (string.IsNullOrWhiteSpace(note)) return null;
        foreach (var part in note.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith(DumpNotePrefix, StringComparison.Ordinal))
                return part[DumpNotePrefix.Length..];
        return null;
    }

    /// <summary>增量写入：按名称匹配，有则更新坐标/子类/吞吐（主键不动），无则新插。<b>不删任何记录</b>。</summary>
    public static SinkSaveResult SaveScoped(DbConnection conn, IReadOnlyList<SinkSpec>? rows)
    {
        if (conn is null) throw new ArgumentNullException(nameof(conn));
        rows ??= Array.Empty<SinkSpec>();
        var existing = LoadAll(conn);
        int ins = 0, upd = 0;
        var names = new List<string>();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) continue;
            names.Add(r.Name);
            string note = string.IsNullOrWhiteSpace(r.DumpSiteId) ? "" : DumpNotePrefix + r.DumpSiteId;
            LoadUnloadRecord? hit = null;
            foreach (var p in existing) if (string.Equals(p.Name, r.Name, StringComparison.Ordinal)) { hit = p; break; }
            if (hit is not null)
            {
                RoadNetworkStore.Exec(conn, "UPDATE load_unload_point SET kind = " + RoadNetworkStore.L(KindUnloading)
                    + ", unload_sub = " + RoadNetworkStore.L(r.UnloadSub)
                    + ", x = " + RoadNetworkStore.N(r.X) + ", y = " + RoadNetworkStore.N(r.Y) + ", z = " + RoadNetworkStore.N(r.Z)
                    + ", throughput_tph = " + RoadNetworkStore.N(r.ThroughputTph)
                    + ", note = " + RoadNetworkStore.L(MergeNote(hit.Note, note))
                    + " WHERE id = " + hit.Id.ToString(CultureInfo.InvariantCulture));
                upd++;
                continue;
            }
            RoadNetworkStore.Exec(conn, "INSERT INTO load_unload_point (name, kind, unload_sub, x, y, z, throughput_tph, visible, note) VALUES ("
                + RoadNetworkStore.L(r.Name) + ", " + RoadNetworkStore.L(KindUnloading) + ", " + RoadNetworkStore.L(r.UnloadSub) + ", "
                + RoadNetworkStore.N(r.X) + ", " + RoadNetworkStore.N(r.Y) + ", " + RoadNetworkStore.N(r.Z) + ", "
                + RoadNetworkStore.N(r.ThroughputTph) + ", 1, " + RoadNetworkStore.L(note) + ")");
            ins++;
        }
        return new SinkSaveResult(ins, upd, names);
    }

    /// <summary>把新的挂接前缀并进原 note：替换掉旧的 <c>dump:</c> 段，其余原样保留（别人写的备注不能吞）。</summary>
    public static string MergeNote(string? old, string dumpPart)
    {
        var keep = new List<string>();
        foreach (var s in (old ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!s.StartsWith(DumpNotePrefix, StringComparison.Ordinal)) keep.Add(s);
        if (dumpPart.Length > 0) keep.Insert(0, dumpPart);
        return string.Join("; ", keep);
    }
}
