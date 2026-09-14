using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>一天的实绩（对应表 <c>daily_mine_summary</c> 一行）。</summary>
public sealed class DailyActualRow
{
    public DateTime Date;

    /// <summary>六路外运出煤 t（列序与 <see cref="WeekPlanSource.CoalColumns"/> 一致）。</summary>
    public double BigBelt, SmallBelt, Longhua, TruckExport, Winnowed, BigTruckPile;

    /// <summary>剥离 m³ 实方。</summary>
    public double StrippingM3;

    /// <summary>三个筒仓**存量** t —— 是库存不是产出，故不计入当日出煤。</summary>
    public double Silo1, Silo2, Silo3;

    /// <summary>当日出煤合计 t（口径 = <see cref="WeekPlanSource.CoalColumns"/> 六列相加）。</summary>
    public double CoalTotalT => BigBelt + SmallBelt + Longhua + TruckExport + Winnowed + BigTruckPile;

    /// <summary>当日出煤折实方 m³（与计划同口径，按煤密度折）。</summary>
    public double CoalM3 => CoalTotalT / WeekPlanSource.CoalDensity;

    /// <summary>筒仓存量合计 t。</summary>
    public double SiloTotalT => Silo1 + Silo2 + Silo3;
}

/// <summary>
/// 日实绩的读写（<c>daily_mine_summary</c>）。
///
/// <b>为什么需要它</b>：§三三六 的周计划已经在**读**这张表算实绩与达成度，
/// 但 Kylin 侧一直没有**写**的入口 —— 表是空的，达成度那几列就永远显示「—」。
/// 本类与「实绩录入」窗口把那一头补上。
///
/// ── 口径只此一份 ──
/// 出煤合计与折方**直接引用 <see cref="WeekPlanSource"/> 的那一份**（列名数组 + 煤密度），
/// 不在这里另抄一遍。否则改了口径只改一处，录入端与计划端就会显示成两个数 ——
/// 那种不一致最难查（见 §三三六 的同一条理由）。
///
/// ── 与原版的对应关系（登记）──
/// 原 <c>TaskLib.Features.ActualEntryWindow</c> 写的是 <c>ActualRecord</c> 文件 + 内存里的
/// <c>ProductionTask</c>（班/设备/工序粒度），那套执行域 Kylin 没有。
/// Kylin 侧的实绩事实表就是 <c>daily_mine_summary</c>（日粒度、按外运通道分列），
/// 故本实现是**同一件事换在 Kylin 自己的数据模型上落地**，不是另造一个功能。
/// 原版那条"排弃类去向按占容方扣库容、且**只补增量**"的纪律照搬，
/// 走的是 §三三七 已经移好的 <see cref="SinkRegistryLoader.AddFilled"/>。
/// </summary>
public static class DailyActuals
{
    /// <summary>读一天；没有这一天返回 null（**不是给一行全 0** —— "没录"与"录了 0"是两回事）。</summary>
    public static DailyActualRow? Load(DbConnection? conn, DateTime day)
    {
        if (conn == null) return null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT big_belt_coal_t, small_belt_coal_t, longhua_coal_t, truck_coal_export_t, "
                            + "winnowed_coal_t, big_truck_pile_coal_t, stripping_total_m3, silo_1_t, silo_2_t, silo_3_t "
                            + $"FROM daily_mine_summary WHERE date = '{D(day)}'";
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;
            return new DailyActualRow
            {
                Date = day.Date,
                BigBelt = V(rd, 0), SmallBelt = V(rd, 1), Longhua = V(rd, 2),
                TruckExport = V(rd, 3), Winnowed = V(rd, 4), BigTruckPile = V(rd, 5),
                StrippingM3 = V(rd, 6),
                Silo1 = V(rd, 7), Silo2 = V(rd, 8), Silo3 = V(rd, 9),
            };
        }
        catch { return null; }
    }

    /// <summary>读一段（按日期升序）。</summary>
    public static List<DailyActualRow> LoadRange(DbConnection? conn, DateTime from, DateTime to)
    {
        var list = new List<DailyActualRow>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT date, big_belt_coal_t, small_belt_coal_t, longhua_coal_t, truck_coal_export_t, "
                            + "winnowed_coal_t, big_truck_pile_coal_t, stripping_total_m3, silo_1_t, silo_2_t, silo_3_t "
                            + $"FROM daily_mine_summary WHERE date >= '{D(from)}' AND date <= '{D(to)}' ORDER BY date";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (!DateTime.TryParse(rd.IsDBNull(0) ? "" : rd.GetValue(0)?.ToString() ?? "",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                list.Add(new DailyActualRow
                {
                    Date = d.Date,
                    BigBelt = V(rd, 1), SmallBelt = V(rd, 2), Longhua = V(rd, 3),
                    TruckExport = V(rd, 4), Winnowed = V(rd, 5), BigTruckPile = V(rd, 6),
                    StrippingM3 = V(rd, 7), Silo1 = V(rd, 8), Silo2 = V(rd, 9), Silo3 = V(rd, 10),
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 存一天（先删后插，各家 upsert 语法不同，主键是 date，删一条再插一条语义等价）。
    /// 成功返回空串，失败返回原因，**不抛**。
    /// </summary>
    public static string Save(DbConnection? conn, DailyActualRow? row)
    {
        if (conn == null) return "没有数据库连接";
        if (row == null) return "没有要保存的实绩";
        // 负数一律拦下：产量/剥离没有负的，录进去会把周月合计悄悄拉低
        foreach (var (name, v) in Fields(row))
            if (v < 0) return $"{name} 不能为负";
        try
        {
            Exec(conn, $"DELETE FROM daily_mine_summary WHERE date = '{D(row.Date)}'");
            Exec(conn,
                "INSERT INTO daily_mine_summary (date, big_belt_coal_t, small_belt_coal_t, longhua_coal_t, "
              + "truck_coal_export_t, winnowed_coal_t, big_truck_pile_coal_t, stripping_total_m3, "
              + "silo_1_t, silo_2_t, silo_3_t) VALUES ("
              + $"'{D(row.Date)}', {N(row.BigBelt)}, {N(row.SmallBelt)}, {N(row.Longhua)}, "
              + $"{N(row.TruckExport)}, {N(row.Winnowed)}, {N(row.BigTruckPile)}, {N(row.StrippingM3)}, "
              + $"{N(row.Silo1)}, {N(row.Silo2)}, {N(row.Silo3)})");
            return "";
        }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>删一天。</summary>
    public static string Delete(DbConnection? conn, DateTime day)
    {
        if (conn == null) return "没有数据库连接";
        try { Exec(conn, $"DELETE FROM daily_mine_summary WHERE date = '{D(day)}'"); return ""; }
        catch (Exception ex) { return Short(ex); }
    }

    /// <summary>逐字段（中文名, 值）—— 校验与界面共用一份清单，免得两处对不上。</summary>
    internal static IEnumerable<(string Name, double Value)> Fields(DailyActualRow r)
    {
        yield return ("大皮带出煤", r.BigBelt);
        yield return ("小皮带出煤", r.SmallBelt);
        yield return ("龙华出煤", r.Longhua);
        yield return ("汽车外运煤", r.TruckExport);
        yield return ("风选煤", r.Winnowed);
        yield return ("大车堆煤", r.BigTruckPile);
        yield return ("剥离总量", r.StrippingM3);
        yield return ("1号筒仓", r.Silo1);
        yield return ("2号筒仓", r.Silo2);
        yield return ("3号筒仓", r.Silo3);
    }

    /// <summary>
    /// 把剥离实绩回灌进排土场库容 —— <b>按占容方扣，且只补增量</b>（原版这条纪律照搬）。
    /// <paramref name="alreadyDumpedM3"/> 是这一天此前已经扣过的占容方（没有就传 0）。
    /// 返回实际补扣的占容方 m³；去向不存在或不是排弃类返回 0。
    /// </summary>
    public static double BackfillDump(DbConnection? conn, Cad.Tasks.SinkRegistry? sinks,
                                      string? sinkId, double strippingM3, double alreadyDumpedM3 = 0)
    {
        if (sinks == null || string.IsNullOrWhiteSpace(sinkId) || strippingM3 <= 0) return 0;
        var s = sinks.Find(sinkId);
        if (s == null || !s.IsDumping) return 0;

        // ★ 排土场吃的是沉降稳定后的体积：V容 = V实 × Kr，不是实方也不是松方
        double want = strippingM3 * Cad.LongTermSimTimeline.RockSwell;
        double delta = want - Math.Max(0, alreadyDumpedM3);
        if (delta <= 1e-9) return 0;               // 已经扣够了，再扣就是重复记账
        SinkRegistryLoader.AddFilled(conn, sinks, sinkId, delta);
        return delta;
    }

    private static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string N(double v)
        => (double.IsNaN(v) || double.IsInfinity(v) ? 0 : v).ToString("R", CultureInfo.InvariantCulture);
    private static double V(DbDataReader rd, int i)
        => rd.IsDBNull(i) ? 0 : Convert.ToDouble(rd.GetValue(i), CultureInfo.InvariantCulture);

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
