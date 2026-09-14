using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;

namespace PitMine3D.Kylin.Data;

/// <summary>一份要存进模板库的台阶参数（编辑器 ⇄ 落库之间的解耦点，让落库口径能脱 GUI 验收）。</summary>
public sealed class BenchTemplateSpec
{
    /// <summary>模板名（同时用作 <c>code</c>；重名 = 更新同一条，不新建）。</summary>
    public string Name = "";
    public bool IsDump;
    public string? Material;
    public string? Hardness;

    // 岩台阶
    public double BenchHeight = 12;
    public double FaceAngleDeg = 70;
    public double BermWidth = 4;            // 安全平台宽
    public double MinWorkingBerm = BenchTemplateResolver.DefaultMinWorkingBerm;
    public double? MiningWidth;

    // 煤台阶（只在采场模板里写；排土场没有煤岩分层这回事）
    public double CoalBenchHeight = BenchTemplateResolver.DefaultCoalH;
    public double CoalFaceAngleDeg = BenchTemplateResolver.DefaultCoalA;
    public double CoalBermWidth = BenchTemplateResolver.DefaultCoalW;
}

/// <summary>落库结果（回显用）。</summary>
public sealed class BenchTemplateSaveResult
{
    public bool Ok;
    public bool Inserted;
    public long TemplateId;
    /// <summary>实际写进 <c>template_param_value</c> 的项数。</summary>
    public int ParamsWritten;
    /// <summary>库里没有对应参数码、因而没写成的项（**如实报出来**，不静默丢）。</summary>
    public List<string> MissingCodes = new();
    public string Error = "";
}

/// <summary>
/// 台阶模板的**写入器**（对应原 <c>MiningTemplateEditorWindow</c> 的保存路径）。
/// 与 <see cref="BenchTemplateResolver"/> 是同一件事的两头：这里写、那里读。
///
/// <b>标记必须与读取端同一个常量</b> —— 两边字面量对不上就等于"排土场永不读模板"那个坑
/// 原样重现（改了没反应，还不报错）。故这里直接引用
/// <see cref="BenchTemplateResolver.DumpTemplateMark"/> / <see cref="BenchTemplateResolver.PitTemplateMark"/>，
/// 不另抄一份字符串。
///
/// ── Kylin 侧的实现差异（登记）──
/// 原版走 <c>ITemplateService.Insert/UpsertValue</c>，Kylin 直接对
/// <c>process_template</c> / <c>template_param_value</c> 发 SQL；写哪几个参数码、
/// 排土场不写煤台阶这条，与原版一字不差。
/// </summary>
public static class BenchTemplateStore
{
    /// <summary>按名存模板（有同名的就更新，没有就新建）。失败只回 <see cref="BenchTemplateSaveResult.Error"/>，不抛。</summary>
    public static BenchTemplateSaveResult Save(DbConnection? conn, BenchTemplateSpec? spec)
    {
        var r = new BenchTemplateSaveResult();
        if (spec == null || string.IsNullOrWhiteSpace(spec.Name)) { r.Error = "模板名不能为空"; return r; }
        if (conn == null) { r.Error = "没有数据库连接"; return r; }

        // 参数本身不合法就别落库 —— 存进去之后是放坡入口在用，错值会一路错到台阶几何上
        if (!(spec.BenchHeight > 0)) { r.Error = "台阶高必须 > 0"; return r; }
        if (!(spec.FaceAngleDeg > 0 && spec.FaceAngleDeg < 90)) { r.Error = "坡面角需在 (0, 90) 之间"; return r; }
        if (spec.BermWidth < 0 || spec.MinWorkingBerm < 0) { r.Error = "平盘宽不能为负"; return r; }

        string name = spec.Name.Trim();
        string mark = spec.IsDump ? BenchTemplateResolver.DumpTemplateMark : BenchTemplateResolver.PitTemplateMark;

        try
        {
            long id = FindByCode(conn, name);
            if (id > 0)
            {
                Exec(conn, "UPDATE process_template SET name = " + L(name)
                         + ", description = " + L(mark)
                         + ", applicable_material = " + L(spec.Material)
                         + ", applicable_hardness = " + L(spec.Hardness)
                         + ", is_current = 1, status = 'active' WHERE template_id = " + N(id));
                r.Inserted = false;
            }
            else
            {
                Exec(conn, "INSERT INTO process_template (code, name, description, applicable_material, "
                         + "applicable_hardness, is_current, status) VALUES ("
                         + L(name) + ", " + L(name) + ", " + L(mark) + ", "
                         + L(spec.Material) + ", " + L(spec.Hardness) + ", 1, 'active')");
                id = FindByCode(conn, name);
                if (id <= 0) { r.Error = "模板插入后取不到编号"; return r; }
                r.Inserted = true;
            }
            r.TemplateId = id;

            void Put(string code, double val)
            {
                long pid = ParamId(conn, code);
                // 库里没有这个参数码 —— 如实记下来, 不静默当成"存好了"
                if (pid <= 0) { r.MissingCodes.Add(code); return; }
                Exec(conn, $"DELETE FROM template_param_value WHERE template_id={id} AND param_id={pid}");
                Exec(conn, "INSERT INTO template_param_value (template_id, param_id, recommended_value) "
                         + $"VALUES ({id},{pid},{Num(val)})");
                r.ParamsWritten++;
            }

            Put("bench_height", spec.BenchHeight);
            Put("bench_slope_angle", spec.FaceAngleDeg);
            Put("safety_platform_width", spec.BermWidth);
            Put("working_platform_width", spec.MinWorkingBerm);   // 最小工作平盘 → 放坡入口的"工作平盘"取值
            if (spec.MiningWidth is > 0) Put("mining_width", spec.MiningWidth.Value);
            if (!spec.IsDump)                                     // 煤台阶 → 煤岩分层放坡取值(V034)
            {
                Put("coal_bench_height", spec.CoalBenchHeight);
                Put("coal_bench_slope_angle", spec.CoalFaceAngleDeg);
                Put("coal_platform_width", spec.CoalBermWidth);
            }

            r.Ok = true;
            return r;
        }
        catch (Exception ex) { r.Error = Short(ex); return r; }
    }

    /// <summary>删一个模板（连同它的参数值；<c>ON DELETE CASCADE</c> 也会做，这里显式删更稳）。</summary>
    public static string Delete(DbConnection? conn, string? name)
    {
        if (conn == null) return "没有数据库连接";
        if (string.IsNullOrWhiteSpace(name)) return "未指定模板";
        try
        {
            long id = FindByCode(conn, name!.Trim());
            if (id <= 0) return $"库里没有模板「{name}」";
            Exec(conn, $"DELETE FROM template_param_value WHERE template_id={id}");
            Exec(conn, $"DELETE FROM process_template WHERE template_id={id}");
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>列出模板（名字 + 是不是排土），供编辑器下拉。</summary>
    public static List<(string Name, bool IsDump)> List(DbConnection? conn)
    {
        var list = new List<(string, bool)>();
        if (conn == null) return list;
        try
        {
            foreach (var t in BenchTemplateResolver.ListTemplates(conn))
                list.Add((t.Code, (t.Description ?? "").Contains(BenchTemplateResolver.DumpTemplateMark, StringComparison.Ordinal)));
        }
        catch { }
        return list;
    }

    /// <summary>把库里一条模板读回成可编辑的 spec；没有返回 null。</summary>
    public static BenchTemplateSpec? Load(DbConnection? conn, string? name)
    {
        if (conn == null || string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            foreach (var t in BenchTemplateResolver.ListTemplates(conn))
            {
                if (!string.Equals(t.Code, name!.Trim(), StringComparison.Ordinal)) continue;
                bool dump = (t.Description ?? "").Contains(BenchTemplateResolver.DumpTemplateMark, StringComparison.Ordinal);
                var spec = new BenchTemplateSpec { Name = t.Code, IsDump = dump, Material = t.Material, Hardness = t.Hardness };
                var vals = ReadValues(conn, t.Id);
                double G(string code, double dflt) => vals.TryGetValue(code, out double v) ? v : dflt;
                var (nH, nA, nW) = BenchTemplateResolver.Norm(dump, t.Hardness);
                spec.BenchHeight = G("bench_height", nH);
                spec.FaceAngleDeg = G("bench_slope_angle", nA);
                spec.BermWidth = G("safety_platform_width", nW);
                spec.MinWorkingBerm = G("working_platform_width", BenchTemplateResolver.DefaultMinWorkingBerm);
                spec.MiningWidth = vals.TryGetValue("mining_width", out double mw) && mw > 0 ? mw : null;
                spec.CoalBenchHeight = G("coal_bench_height", BenchTemplateResolver.DefaultCoalH);
                spec.CoalFaceAngleDeg = G("coal_bench_slope_angle", BenchTemplateResolver.DefaultCoalA);
                spec.CoalBermWidth = G("coal_platform_width", BenchTemplateResolver.DefaultCoalW);
                return spec;
            }
        }
        catch { }
        return null;
    }

    private static Dictionary<string, double> ReadValues(DbConnection conn, long templateId)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT p.code, v.recommended_value FROM template_param_value v "
                        + "JOIN parameter_definition p ON p.param_id = v.param_id "
                        + "WHERE v.template_id = " + N(templateId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
            string c = rd.GetValue(0)?.ToString() ?? "";
            if (c.Length > 0) map[c] = Convert.ToDouble(rd.GetValue(1), CultureInfo.InvariantCulture);
        }
        return map;
    }

    private static long FindByCode(DbConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_id FROM process_template WHERE code = " + L(code);
        object? v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static long ParamId(DbConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT param_id FROM parameter_definition WHERE code = " + L(code);
        object? v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string L(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Num(double v)
        => (double.IsNaN(v) || double.IsInfinity(v) ? 0 : v).ToString("R", CultureInfo.InvariantCulture);

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        int nl = m.IndexOfAny(new[] { '\r', '\n' });
        if (nl > 0) m = m[..nl];
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
