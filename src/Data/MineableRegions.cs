using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Data;

/// <summary>作业区域一条（对应 <c>mineable_region</c> 一行）。</summary>
public sealed class RegionRecord
{
    public long Id;
    public string Name = "";
    /// <summary>类别：mineable(可采) / pit(采场) / external_dump(外排) / internal_dump(内排)。</summary>
    public string Category = MineableRegions.CatMineable;
    /// <summary>扁平顶点 [x0,y0,z0,x1,y1,z1,…]。</summary>
    public List<double> Points = new();
    public bool Visible = true;
    /// <summary>overlay 颜色 "RRGGBB"（无 #）；空 = 按类别默认色。</summary>
    public string? Color;
    public string? Note;

    public int PointCount => Points.Count / 3;
    public bool RingUsable => PointCount >= 3;

    public bool IsDump => Category is MineableRegions.CatExternalDump or MineableRegions.CatInternalDump;

    /// <summary>环上平均 Z。没有顶点返回 NaN（**不是 0** —— 0 是个合法标高）。</summary>
    public double AvgZ
    {
        get
        {
            if (PointCount == 0) return double.NaN;
            double s = 0;
            for (int i = 2; i < Points.Count; i += 3) s += Points[i];
            return s / PointCount;
        }
    }

    /// <summary>水平投影面积 m²（鞋带公式，取绝对值）。</summary>
    public double AreaM2
    {
        get
        {
            int n = PointCount;
            if (n < 3) return 0;
            double a = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                a += Points[i * 3] * Points[j * 3 + 1] - Points[j * 3] * Points[i * 3 + 1];
            }
            return Math.Abs(a) / 2.0;
        }
    }

    /// <summary>Z 的出处（写在 note 里，形如 "Z:现状面采样"）。空 = 没有记录。</summary>
    public string ZProvenance
    {
        get
        {
            string n = Note ?? "";
            int i = n.IndexOf(MineableRegions.ZTag, StringComparison.Ordinal);
            if (i < 0) return "";
            string rest = n[(i + MineableRegions.ZTag.Length)..];
            int end = rest.IndexOf(';');
            return (end >= 0 ? rest[..end] : rest).Trim();
        }
    }
}

/// <summary>一条关联诊断。</summary>
public sealed class RegionDiagnosis
{
    public RegionRecord Region = null!;
    public List<string> Lines = new();
    /// <summary>四条都过、且本期选定 ⇒ 能进推演。</summary>
    public bool Usable;
}

/// <summary>
/// 作业区域台账（<c>mineable_region</c>）的读写与关联诊断 ——
/// 「作业区划分」落的就是这张表（移植原 <c>TaskLib.Zoning.ZoneStore</c> + <c>ZoneLinkage</c> 的口径）。
///
/// <b>为什么是这张表</b>（原注释的话）：它是全项目共用的「作业区域」注册表 ——
/// 三维推演读它当推进轮廓、路网中心线提取读它当裁剪范围。**另起一张表就等于"画完了推演里什么也不变"**。
/// Kylin 侧这张表 V015/V016/V018 建好之后**一直零消费者**，本类是那条接线。
///
/// ── 本层守住的两件事（原版三件里 Kylin 结构上只剩两件）──
/// ② <b>Z 有出处</b>：落库前逐顶点从现状面采 Z，采不到就要人填基准标高，
///    两条都不成立 <b>拒绝入库</b> —— 写 0 会被推演当成台账实测高程，层体整体摆在 0 米。
/// ③ <b>关联当场可见</b>：能不能进推演取决于 选定 / 类别极性 / 顶点数 / Z / 名字对不对得上源汇，
///    <b>五条全都不会报错</b>，故逐条判给人看。
/// （原版第 ① 件"坐标是真的"是防影像未配准时拿区域包围盒凑四至；Kylin 侧区域直接来自
///  场景里的世界坐标闭合多段线，<b>那个失败模式结构上不存在</b>，故不移。）
/// </summary>
public static class MineableRegions
{
    public const string CatMineable = "mineable";
    public const string CatPit = "pit";
    public const string CatExternalDump = "external_dump";
    public const string CatInternalDump = "internal_dump";
    /// <summary>剥采工作帮 —— 采场范围之内再收一层（「采矿模型」的范围闸门）；不是"一块地"，不参与归属（忠实原 MineableRegion）。</summary>
    public const string CatPitWorkingSlope = "pit_working_slope";
    /// <summary>排土工作帮 —— 排土场范围之内再收一层，「排土条带」的范围闸门。</summary>
    public const string CatDumpWorkingSlope = "dump_working_slope";

    /// <summary>这个类别是"工作帮"（范围闸门）而不是一块地。</summary>
    public static bool IsWorkingSlope(string? category)
        => string.Equals(category, CatPitWorkingSlope, StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, CatDumpWorkingSlope, StringComparison.OrdinalIgnoreCase);
    /// <summary>采场 = 白名单，只有 pit 算（原版教训：黑名单"不含 dump"会把工作帮当独立采场再建一遍）。</summary>
    public static bool IsPit(string? category) => string.Equals(category, CatPit, StringComparison.OrdinalIgnoreCase);
    /// <summary>排土场（内排 / 外排）；工作帮那两类不算。</summary>
    public static bool IsDumpSite(string? category)
        => string.Equals(category, CatExternalDump, StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, CatInternalDump, StringComparison.OrdinalIgnoreCase);
    /// <summary>「工作帮 ↔ 它自己的母范围」那一对（剥采工作帮↔采场 / 排土工作帮↔排土场）—— 只有这一对之间的重叠是正常的。参数无序。</summary>
    public static bool IsGateOverItsParent(string? a, string? b)
        => (IsPitGate(a) && IsPit(b)) || (IsPit(a) && IsPitGate(b))
        || (IsDumpGate(a) && IsDumpSite(b)) || (IsDumpSite(a) && IsDumpGate(b));
    /// <summary>类别显示名（忠实原 MineableRegion.DisplayName）：不叫"可采区域"（那是整库统称），未分类就叫未分类；认不出的把原码显示出来，不冒充。</summary>
    public static string DisplayName(string? category) => (category ?? "").Trim() switch
    {
        CatPit => "采场",
        CatExternalDump => "外排土场",
        CatInternalDump => "内排土场",
        CatPitWorkingSlope => "剥采工作帮",
        CatDumpWorkingSlope => "排土工作帮",
        "" or CatMineable => "未分类",
        "wide_bench" => "达标平盘",
        var other => other,
    };
    public static bool IsPitGate(string? category) => string.Equals(category, CatPitWorkingSlope, StringComparison.OrdinalIgnoreCase);
    public static bool IsDumpGate(string? category) => string.Equals(category, CatDumpWorkingSlope, StringComparison.OrdinalIgnoreCase);

    /// <summary>Z 出处在 note 里的前缀标记。</summary>
    public const string ZTag = "Z:";

    /// <summary>「本期选定」在 note 里的标记 —— 表上没有这一列，落在 note 上（登记）。</summary>
    public const string ActiveTag = "[选定]";

    public static string CategoryZh(string? cat) => (cat ?? "").Trim() switch
    {
        CatPit => "采场",
        CatExternalDump => "外排土场",
        CatInternalDump => "内排土场",
        CatPitWorkingSlope => "剥采工作帮",
        CatDumpWorkingSlope => "排土工作帮",
        _ => "可采区域",
    };

    /// <summary>类别默认 overlay 色（采场橙 / 外排蓝 / 内排青 / 可采绿）。</summary>
    public static string DefaultColor(string? cat) => (cat ?? "").Trim() switch
    {
        CatPit => "D9480F",
        CatExternalDump => "1971C2",
        CatInternalDump => "0C8599",
        _ => "2B8A3E",
    };

    public static bool IsActive(RegionRecord r) => (r.Note ?? "").Contains(ActiveTag, StringComparison.Ordinal);

    public static List<RegionRecord> List(DbConnection? conn)
    {
        var list = new List<RegionRecord>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, points_json, visible, note, category, color FROM mineable_region ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new RegionRecord
                {
                    Id = Convert.ToInt64(rd.GetValue(0)),
                    Name = S(rd, 1),
                    Points = ParseXyz(S(rd, 2)),
                    Visible = !rd.IsDBNull(3) && Convert.ToInt64(rd.GetValue(3)) != 0,
                    Note = rd.IsDBNull(4) ? null : rd.GetValue(4)?.ToString(),
                    Category = string.IsNullOrWhiteSpace(S(rd, 5)) ? CatMineable : S(rd, 5).Trim(),
                    Color = rd.IsDBNull(6) ? null : rd.GetValue(6)?.ToString(),
                });
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 存一条（<c>Id&gt;0</c> 更新，否则新建）。
    /// <b>拒绝入库的两种</b>：顶点不足 3；以及 <b>Z 既没采到也没填基准标高</b>
    /// （那会让推演把 0 当成实测高程）。成功返回空串。
    /// </summary>
    public static string Save(DbConnection? conn, RegionRecord? r, out long id)
    {
        id = 0;
        if (conn == null) return "没有数据库连接";
        if (r == null) return "没有要保存的区域";
        if (string.IsNullOrWhiteSpace(r.Name)) return "区域名不能为空";
        if (!r.RingUsable) return $"只有 {r.PointCount} 个顶点（需 ≥3）—— 推演装载时会静默跳过，不入库";
        if (r.ZProvenance.Length == 0)
            return "Z 没有出处 —— 请先从现状面采样，或填一个基准标高。"
                 + "直接写 0 会被推演当成台账实测高程，层体整体摆在 0 米";

        try
        {
            string pts = JsonSerializer.Serialize(r.Points);
            string color = string.IsNullOrWhiteSpace(r.Color) ? "NULL" : L(r.Color!.Trim().TrimStart('#'));
            if (r.Id > 0)
            {
                Exec(conn, "UPDATE mineable_region SET name = " + L(r.Name) + ", points_json = " + L(pts)
                         + ", visible = " + (r.Visible ? 1 : 0) + ", note = " + L(r.Note)
                         + ", category = " + L(r.Category) + ", color = " + color
                         + " WHERE id = " + r.Id.ToString(CultureInfo.InvariantCulture));
                id = r.Id;
            }
            else
            {
                Exec(conn, "INSERT INTO mineable_region (name, points_json, visible, note, category, color) VALUES ("
                         + L(r.Name) + ", " + L(pts) + ", " + (r.Visible ? 1 : 0) + ", "
                         + L(r.Note) + ", " + L(r.Category) + ", " + color + ")");
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT MAX(id) FROM mineable_region";
                object? v = q.ExecuteScalar();
                id = v == null || v is DBNull ? 0 : Convert.ToInt64(v);
                r.Id = id;
            }
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    public static string Delete(DbConnection? conn, long id)
    {
        if (conn == null) return "没有数据库连接";
        try { Exec(conn, "DELETE FROM mineable_region WHERE id = " + id.ToString(CultureInfo.InvariantCulture)); return ""; }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>解析扁平 [x,y,z,…]；解析不了返回空表（**不是抛**）。</summary>
    internal static List<double> ParseXyz(string? json)
    {
        try
        {
            var a = JsonSerializer.Deserialize<List<double>>(json ?? "[]");
            if (a == null) return new List<double>();
            // 长度必须是 3 的倍数：不是就截到最近的整点，剩下的半个点没有意义
            int keep = a.Count / 3 * 3;
            return a.Take(keep).ToList();
        }
        catch { return new List<double>(); }
    }

    /// <summary>只取 XY 环（供范围裁剪：<see cref="Cad.SeamOutcrop.SeamOutcropRunner"/> 等吃的是扁平 [x,y,…]）。</summary>
    public static double[] RingXy(RegionRecord r)
    {
        int n = r.PointCount;
        var flat = new double[n * 2];
        for (int i = 0; i < n; i++) { flat[i * 2] = r.Points[i * 3]; flat[i * 2 + 1] = r.Points[i * 3 + 1]; }
        return flat;
    }

    /// <summary>
    /// 关联诊断（原 <c>ZoneLinkage.Diagnose</c> 的口径）：**五条全都不会报错**，故逐条判给人看。
    /// <paramref name="sinks"/> 为空则第 ④ 条判不了，如实说，不当成通过。
    /// </summary>
    public static RegionDiagnosis Diagnose(RegionRecord r, SinkRegistry? sinks)
    {
        var d = new RegionDiagnosis { Region = r };
        bool ringOk = r.RingUsable;
        bool active = IsActive(r);

        // ⓪ 本期选定 —— 排在最前：没选定，后面几条判得再对也进不去
        d.Lines.Add(active
            ? "⓪ 本期选定：是。"
            : "⓪ 本期未选定：这块区域不进推演的推进轮廓，也不参与路网中心线提取的裁剪范围"
              + "（台账里还在，别处按名字挑区域照常能选到它）。");

        // ① 极性
        d.Lines.Add("① 推进极性：" + (r.IsDump
            ? $"外扩（排土堆填）—— 类别「{CategoryZh(r.Category)}」"
            : $"内缩（采场推进）—— 类别「{CategoryZh(r.Category)}」")
            + "。类别填反了轮廓会朝反方向动，推演不会报错。");

        // ② 几何
        d.Lines.Add(ringOk
            ? $"② 几何：{r.PointCount} 个顶点，面积 {r.AreaM2 / 1e4:0.##} 万 m²，可用。"
            : $"② 几何：只有 {r.PointCount} 个顶点（需 ≥3）—— 推演装载时会静默跳过，台账里看得见、推演里没有。");

        // ③ 高程
        string prov = r.ZProvenance;
        double az = r.AvgZ;
        string zText = !ringOk ? "—"
            : prov.Length > 0 ? prov
            : double.IsNaN(az) || Math.Abs(az) < 1e-9 ? "⚠ 全环 Z=0 且无出处记录"
            : $"均值 {az.ToString("0.##", CultureInfo.InvariantCulture)}m（无出处记录，本窗口之外入库的）";
        d.Lines.Add("③ 高程：" + zText
            + (ringOk && !double.IsNaN(az) && Math.Abs(az) < 1e-9
               ? " —— 0m 会被推演当作台账实测高程，层体整体摆在 0 米。" : ""));

        // ④ 绑定（排土类才要对上去向；采场类不需要）
        bool bindOk;
        if (!r.IsDump) { bindOk = true; d.Lines.Add("④ 绑定：采场类不需对上去向，跳过。"); }
        else if (sinks == null || sinks.All.Count == 0)
        {
            bindOk = false;
            d.Lines.Add("④ 绑定：去向台账读不出来，无法判断名字能不能对上去向 —— 判不了，不当成通过。");
        }
        else
        {
            var hit = sinks.All.FirstOrDefault(s =>
                       string.Equals(s.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Id, r.Name, StringComparison.OrdinalIgnoreCase)
                    || (r.Name.Length > 1 && s.Name.Contains(r.Name, StringComparison.OrdinalIgnoreCase))
                    || (s.Name.Length > 1 && r.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase)));
            bindOk = hit != null;
            d.Lines.Add(bindOk
                ? $"④ 绑定：对上去向「{hit!.Name}」（{hit.Kind.Label()}）。"
                : $"④ 绑定：去向台账里没有叫得上「{r.Name}」的排土场 —— 推演里这块区域收不到量。");
        }

        d.Usable = active && ringOk && prov.Length > 0 && bindOk;
        return d;
    }

    private static string S(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static string L(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        int nl = m.IndexOfAny(new[] { '\r', '\n' });
        if (nl > 0) m = m[..nl];
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
