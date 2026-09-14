using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Data;

/// <summary>一个去向的当日入方账（实方 / 占容 / 吨量）。</summary>
public sealed class SinkInboundAcc
{
    public string SinkId = "";
    public string SinkName = "";
    public SinkKind Kind;
    public double PlanInSituM3, PlanDumpM3, PlanTonnageT;
    /// <summary>已排占容方。Kylin 侧**没有数据源**（见 <see cref="SinkInbound"/> 抬头），故恒 0 且不显示成"0"。</summary>
    public double DoneDumpM3;
    public bool DoneKnown;
    /// <summary>尚未排弃的占容方（已排部分已计入去向的 FilledM3，不能再扣一次）。</summary>
    public double PendingDumpM3 => Math.Max(0, PlanDumpM3 - DoneDumpM3);
}

/// <summary>
/// 当日入方：各作业面的当日目标按去向汇总，回答"今天要往这个场排多少、排完还剩多少"。
///
/// ── 与原版的取数差异（登记）──
///   原 <c>SinkLedgerWindow.TodayInbound()</c> 吃的是 <c>SampleTaskBoard.Day()</c> —— **样例任务台账**。
///   Kylin 不建样例数据源（<see cref="SinkRegistry.Sample"/> 在两边都是空登记簿，就是这条纪律），
///   故改从 <c>working_face_routing</c> 取：那张表 V035 就建好了，存的正是"当日怎么干"
///   （去向 / 物料 / 混采构成 / 当日目标 m³ 实方）。口径（实方→占容→吨量的三步换算）与原版一字不差。
///
///   <b>代价要说清楚</b>：<c>working_face_routing</c> 只存**当日目标**，没有实绩列，
///   所以"已排"这一列 Kylin 判不出来（<see cref="SinkInboundAcc.DoneKnown"/> 恒 false），
///   显示"—"而不是"0"。随之而来的是：<b>排后剩余按"今日一方都还没排"的保守口径算</b>；
///   若今天已经用 <c>AddFilled</c> 回灌过实绩，那部分会被重复扣一次，界面上要如实提示。
///
/// ── 换算口径（露天矿铁律，与 <see cref="MaterialFlow"/> 同一处公式，本文件不另写一套）──
///   实方 V实 →（× Kr 残余松散系数）→ 占容方 V容（排土场按这个扣库容）
///   实方 V实 →（× 原岩密度）→ 吨量 t（三个体积口径间唯一的守恒中间量）
/// </summary>
public static class SinkInbound
{
    /// <summary>
    /// 从 <c>working_face_routing</c> 读出当日采装面的物料流。混采面按 <c>splits_json</c> 拆成多条流
    /// （一条任务可拆出煤 / 岩两条），拆不开就按主物料 + 主去向记一条。
    /// </summary>
    public static List<MaterialFlow> FlowsFromRouting(DbConnection? conn, out string err)
    {
        err = "";
        var flows = new List<MaterialFlow>();
        if (conn == null) { err = "没有数据库连接"; return flows; }

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT face_code, process, material_code, destination_id, destination_name, destination_kind, "
              + "haul_distance_km, equiv_haul_km, day_target_m3, splits_json, bench_elevation_m "
              + "FROM working_face_routing";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                // 只看采装面：排土面的日目标由入方推导（derived_from_inbound），再算一遍就是重复计
                if (!string.Equals(S(rd, 1).Trim(), "Load", StringComparison.OrdinalIgnoreCase)) continue;

                double target = D(rd, 8);
                if (target <= 1e-6) continue;

                string face = S(rd, 0);
                double bench = D(rd, 10);
                var splits = ParseSplits(S(rd, 9));

                if (splits.Count > 0)
                {
                    foreach (var sp in splits)
                    {
                        double frac = sp.Fraction;
                        if (frac <= 1e-9) continue;
                        flows.Add(new MaterialFlow
                        {
                            SourceId = face, SourceName = face, SourceBenchElevationM = bench,
                            MaterialCode = string.IsNullOrWhiteSpace(sp.MaterialCode) ? S(rd, 2) : sp.MaterialCode,
                            InSituM3 = target * frac,
                            // 分项自带去向就用它的；没填就落回主去向（混采里常见"煤单独走、岩跟主去向"）
                            SinkId = string.IsNullOrWhiteSpace(sp.DestinationId) ? S(rd, 3) : sp.DestinationId,
                            SinkName = string.IsNullOrWhiteSpace(sp.DestinationName) ? S(rd, 4) : sp.DestinationName,
                            // 分项的 DestinationKind 是枚举、没有"未填"这个取值：只有分项自带去向时才认它，
                            // 否则跟主去向走 —— 否则没填去向的分项会一律带上枚举默认值（外排土场）
                            SinkKind = string.IsNullOrWhiteSpace(sp.DestinationId) && string.IsNullOrWhiteSpace(sp.DestinationName)
                                ? KindOf(S(rd, 5)) : sp.DestinationKind,
                            HaulKm = sp.HaulKm > 0 ? sp.HaulKm : D(rd, 6),
                            EquivHaulKm = sp.EquivHaulKm > 0 ? sp.EquivHaulKm : D(rd, 7),
                        });
                    }
                }
                else
                {
                    flows.Add(new MaterialFlow
                    {
                        SourceId = face, SourceName = face, SourceBenchElevationM = bench,
                        MaterialCode = S(rd, 2), InSituM3 = target,
                        SinkId = S(rd, 3), SinkName = S(rd, 4), SinkKind = KindOf(S(rd, 5)),
                        HaulKm = D(rd, 6), EquivHaulKm = D(rd, 7),
                    });
                }
            }
        }
        catch (Exception ex) { err = Short(ex); }
        return flows;
    }

    /// <summary>按去向汇总物料流。没填去向的流归到 <c>SinkId=""</c> 那一条上，由界面单独提示。</summary>
    public static List<SinkInboundAcc> Aggregate(IEnumerable<MaterialFlow>? flows)
    {
        var map = new Dictionary<string, SinkInboundAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in flows ?? Enumerable.Empty<MaterialFlow>())
        {
            string key = string.IsNullOrWhiteSpace(f.SinkId)
                ? (string.IsNullOrWhiteSpace(f.SinkName) ? "" : f.SinkName)
                : f.SinkId;

            if (!map.TryGetValue(key, out var acc))
                map[key] = acc = new SinkInboundAcc
                {
                    SinkId = key,
                    SinkName = string.IsNullOrWhiteSpace(f.SinkName) ? key : f.SinkName,
                    Kind = f.SinkKind,
                };

            acc.PlanInSituM3 += f.InSituM3;
            acc.PlanDumpM3 += f.DumpM3;          // ★ 占容方，排土场按这个扣库容
            acc.PlanTonnageT += f.TonnageT;
        }
        return map.Values.OrderByDescending(x => x.PlanDumpM3).ToList();
    }

    /// <summary>一步到位：读表 + 汇总。</summary>
    public static List<SinkInboundAcc> Today(DbConnection? conn, out string err)
        => Aggregate(FlowsFromRouting(conn, out err));

    private static SinkKind KindOf(string? s)
        => Enum.TryParse<SinkKind>((s ?? "").Trim(), ignoreCase: true, out var k) ? k : SinkKind.ExternalDump;

    /// <summary>
    /// <c>MaterialDestination.DestinationKind</c> 是 <see cref="SinkKind"/> 枚举，而库里存的是**枚举名**
    /// （V035 注释写明"SinkKind 枚举名"）。System.Text.Json 默认只认枚举的数字形式，
    /// 遇到 "Silo" 会抛 —— 而本文件的容错是"拆不开就按主物料记一条"，于是**整个混采拆分会静默退化**：
    /// 界面上煤与岩合成一条、去向全落到主去向，谁也看不出哪儿错了。故必须挂字符串枚举转换器。
    /// </summary>
    private static readonly JsonSerializerOptions SplitOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>splits_json → 分项。**解析不了就当没有**（回落主物料一条流），不抛、不半途而废。</summary>
    internal static List<MaterialDestination> ParseSplits(string? json)
    {
        var list = new List<MaterialDestination>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            var arr = JsonSerializer.Deserialize<List<MaterialDestination>>(json, SplitOpts);
            if (arr != null) list.AddRange(arr.Where(x => x != null));
        }
        catch { /* opaque 列, 存的什么都可能; 拆不开就按主物料记一条 */ }
        return list;
    }

    private static string S(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static double D(DbDataReader rd, int i)
        => rd.IsDBNull(i) ? 0 : Convert.ToDouble(rd.GetValue(i), CultureInfo.InvariantCulture);

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        int nl = m.IndexOfAny(new[] { '\r', '\n' });
        if (nl > 0) m = m[..nl];
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
