using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;

namespace PitMine3D.Kylin.Data;

/// <summary>破碎站的一条待存记录。窗口行 ⇄ 落库之间的解耦点，让落库口径能脱 GUI 验收。</summary>
public readonly record struct CrusherSpec(string Name, double X, double Y, double Z, double ThroughputTph);

/// <summary>落库结果计数（回显用）。</summary>
public readonly record struct CrusherSaveResult(int Inserted, int Updated, int Deleted);

/// <summary>
/// 破碎站在 <c>load_unload_point</c> 表里的<b>子集写入器</b>（忠实移植 <c>RoadLib.Transport.CrusherStore</c>）。
///
/// ── 为什么必须单开这一层（原注释的理由，照搬）──
/// 「破碎站位置设置」的前身「装卸点设置」管四类点（采剥点 / 破碎站 / 排土场 / 储矿场），
/// 保存走 <c>ReplaceAll</c> —— 清表重插，全表由那一个窗口说了算。
/// 窄化成只管破碎站之后，那条路会连出两个事故：
/// <list type="number">
///   <item>窗口里没有的类别（采剥点 / 排土场 / 储矿场，含「去向台账」建的档）被<b>整片抹掉</b>；</item>
///   <item>幸存行<b>重新编号</b>，而去向台账用 <c>LUP-{id}</c> 记引用 ⇒ 引用全指错，
///     症状是台账里的卸载点忽然对不上，且看不出跟改破碎站有关。</item>
/// </list>
/// 故本类只做三件事：破碎站按 <c>name</c> 匹配，有则 <b>UPDATE（主键不变）</b>、无则 INSERT、
/// 窗口里消失的 DELETE。<b>非破碎站的记录一条都不读进来、更不写回去。</b>
/// 任何时候都不要把它换回"清表重插"—— <c>CrusherStoreTests</c> 会红。
///
/// <para>§三三七 把 <see cref="SinkRegistryLoader"/> 接上之后，第 2 条不再是假设：
/// 去向台账现在**真的**按 <c>LUP-{id}</c> 存引用（<c>sink_profile.sink_id</c> 就是那个字符串），
/// 一次重新编号会让所有卸载点的扩展档案集体错位。</para>
///
/// ── Kylin 侧的实现差异（登记）──
/// 原版走 <c>ILoadUnloadPointService</c> 仓储，Kylin 没有服务层，这里直接对表发 SQL；
/// 语义（按名匹配、主键不变、删除权限只覆盖种子集合）一字不差。
/// </summary>
public static class CrusherStore
{
    /// <summary>类别码：卸载点（汇）。</summary>
    public const string KindUnloading = "unloading";

    /// <summary>卸载子类码：破碎站。</summary>
    public const string SubCrusher = "crusher";

    /// <summary>库里的一行破碎站（带主键，供 UPDATE / DELETE 定位）。</summary>
    public sealed class CrusherRecord
    {
        public long Id;
        public string Name = "";
        public double X, Y, Z, ThroughputTph;
    }

    /// <summary>这条记录是不是破碎站（本写入器唯一有处置权的那一类）。</summary>
    public static bool IsCrusher(string? kind, string? unloadSub)
        => string.Equals((kind ?? "").Trim(), KindUnloading, StringComparison.OrdinalIgnoreCase)
        && string.Equals((unloadSub ?? "").Trim(), SubCrusher, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读出库里现有的破碎站（窗口的种子）。
    /// <b>只取破碎站</b>：子类信息只有库里有，读不到就宁可不播种，
    /// 也不能把排土场当成破碎站端上来编辑后写回。
    /// </summary>
    public static List<CrusherRecord> LoadCrushers(DbConnection? conn)
    {
        var list = new List<CrusherRecord>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, x, y, z, throughput_tph, kind, unload_sub FROM load_unload_point";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (!IsCrusher(S(rd, 6), S(rd, 7))) continue;
                list.Add(new CrusherRecord
                {
                    Id = rd.IsDBNull(0) ? 0 : Convert.ToInt64(rd.GetValue(0)),
                    Name = S(rd, 1), X = D(rd, 2), Y = D(rd, 3), Z = D(rd, 4), ThroughputTph = D(rd, 5),
                });
            }
        }
        catch { /* 库不通 ⇒ 不播种（见方法注释）, 由调用方给"本次只能新增"的提示 */ }
        return list;
    }

    /// <summary>
    /// 把 <paramref name="rows"/> 写进库：增量 CRUD，非破碎站记录不动。
    ///
    /// <para><b>删除权限只覆盖 <paramref name="seededNames"/>（窗口打开时看见的那批破碎站）</b>，
    /// 不是"库里所有破碎站"。因为破碎站有两个非模态入口 —— 本窗口与「去向台账」（新增卸载点也能建破碎站）。
    /// 若按"rows 即全集"删，那么本窗口开着的这段时间里另一个窗口新建的破碎站，
    /// 会在本窗口一存的时候被静默删掉，而现场只会看到"我明明建过"。
    /// 收窄到种子集合，这种交叉删除**结构性不可能**。</para>
    ///
    /// <paramref name="rows"/> 里的 Name 须已去重（窗口已校验）；重名时后者覆盖前者，不抛。
    /// </summary>
    /// <param name="seededNames">本次窗口打开时读到的破碎站名字集合（删除权限的边界）。</param>
    public static CrusherSaveResult SaveScoped(DbConnection conn,
                                               IReadOnlyList<CrusherSpec>? rows,
                                               IReadOnlyCollection<string> seededNames)
    {
        if (conn is null) throw new ArgumentNullException(nameof(conn));
        if (seededNames is null) throw new ArgumentNullException(nameof(seededNames));
        rows ??= Array.Empty<CrusherSpec>();
        var owned = new HashSet<string>(seededNames, StringComparer.Ordinal);

        // 只把破碎站读进手里。别的类别连引用都不取，从源头上杜绝"顺手写回去"。
        var existing = new Dictionary<string, CrusherRecord>(StringComparer.Ordinal);
        foreach (var p in LoadCrushers(conn)) existing[p.Name] = p;

        int ins = 0, upd = 0, del = 0;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) continue;   // 空名不落库（窗口已挡，这里兜底）
            keep.Add(r.Name);

            if (existing.TryGetValue(r.Name, out var e))
            {
                // ★ UPDATE 而不是删了重插：主键保持不变 ⇒ 去向台账的 LUP-{id} 引用不断
                Exec(conn, "UPDATE load_unload_point SET x = " + N(r.X) + ", y = " + N(r.Y) + ", z = " + N(r.Z)
                         + ", throughput_tph = " + N(r.ThroughputTph)
                         + ", kind = " + L(KindUnloading) + ", unload_sub = " + L(SubCrusher)
                         + " WHERE id = " + e.Id.ToString(CultureInfo.InvariantCulture));
                upd++;
            }
            else
            {
                Exec(conn, "INSERT INTO load_unload_point (name, kind, unload_sub, x, y, z, throughput_tph, visible) "
                         + "VALUES (" + L(r.Name) + ", " + L(KindUnloading) + ", " + L(SubCrusher) + ", "
                         + N(r.X) + ", " + N(r.Y) + ", " + N(r.Z) + ", " + N(r.ThroughputTph) + ", 1)");
                ins++;
            }
        }

        // 只删"本窗口开的时候看见过、现在被用户去掉"的那些；期间别处新建的破碎站不在权限内
        foreach (var kv in existing)
            if (owned.Contains(kv.Key) && !keep.Contains(kv.Key))
            {
                Exec(conn, "DELETE FROM load_unload_point WHERE id = " + kv.Value.Id.ToString(CultureInfo.InvariantCulture));
                del++;
            }

        return new CrusherSaveResult(ins, upd, del);
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string L(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string N(double v)
        => (double.IsNaN(v) || double.IsInfinity(v) ? 0 : v).ToString("R", CultureInfo.InvariantCulture);
    private static string S(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static double D(DbDataReader rd, int i)
        => rd.IsDBNull(i) ? 0 : Convert.ToDouble(rd.GetValue(i), CultureInfo.InvariantCulture);
}
