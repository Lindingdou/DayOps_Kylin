using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>源—汇矩阵里的一格（一条 O-D）。</summary>
public sealed class PairingCell
{
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    /// <summary>该 O-D 的实方量 m³。</summary>
    public double InSituM3 { get; set; }
    /// <summary>该 O-D 的等效运距 km。0 = 没录。</summary>
    public double HaulKm { get; set; }

    public string Caption => InSituM3 <= 1e-9 ? "—"
        : $"{InSituM3:N0} m³" + (HaulKm > 1e-6 ? $" / {HaulKm:0.##} km" : " / 运距未录");
}

/// <summary>矩阵的一行：一个作业面 × 一种物料。</summary>
public sealed class PairingRow
{
    public string SourceName { get; set; } = "";
    public string MaterialCode { get; set; } = "";
    public string MaterialName => MaterialCatalog.Resolve(MaterialCode).Name;
    /// <summary>本行合计实方 m³。</summary>
    public double InSituM3 { get; set; }
    /// <summary>去向 Id → 格。</summary>
    public Dictionary<string, PairingCell> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这一行的量<b>有没有全部落到去向上</b>。差额即"采了没处去"。</summary>
    public double RoutedM3 => Cells.Values.Sum(c => c.InSituM3);
    public double UnroutedM3 => Math.Max(0, InSituM3 - RoutedM3);
}

/// <summary>一个去向的库容条。</summary>
public sealed class SinkCapacityBar
{
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind Kind { get; set; }

    /// <summary>设计库容 m³。0 = 台账没录（此时剩余判不了）。</summary>
    public double DesignM3 { get; set; }
    /// <summary>已填 m³（台账的现状填埋量）。</summary>
    public double FilledM3 { get; set; }
    /// <summary>本期入方<b>占容方</b> m³ —— <b>按 V容 = V实 × Kr 扣</b>，不是按实方扣。</summary>
    public double InboundDumpM3 { get; set; }

    /// <summary>期末剩余 m³。设计库容没录时<b>判不了</b>，返回 null（不写 0）。</summary>
    public double? RemainM3 => DesignM3 > 1e-6 ? DesignM3 - FilledM3 - InboundDumpM3 : null;

    /// <summary>期末占用率 %。设计库容没录时判不了。</summary>
    public double? UsedPct => DesignM3 > 1e-6 ? (FilledM3 + InboundDumpM3) / DesignM3 * 100 : null;

    /// <summary>本期这个点<b>装不下</b>（期末剩余为负）。判不了时为 false —— 判不了不是"装得下"。</summary>
    public bool Overflow => RemainM3 is { } r && r < -1e-6;

    public string Caption => DesignM3 <= 1e-6
        ? $"{SinkName}：设计库容未录 ⇒ 期末剩余判不了（本期入方占容 {InboundDumpM3:N0} m³）"
        : $"{SinkName}：设计 {DesignM3 / 1e4:0.##} 万m³ · 已填 {FilledM3 / 1e4:0.##} · 本期入方占容 {InboundDumpM3 / 1e4:0.##}"
          + $" ⇒ 期末剩余 {RemainM3!.Value / 1e4:0.##} 万m³（占用 {UsedPct!.Value:0.#}%）"
          + (Overflow ? "　⚠ 本期排不下" : "");
}

/// <summary>采排配对的完整视图。</summary>
public sealed class PairingView
{
    public string Period { get; set; } = "";
    public List<PairingRow> Rows { get; } = new();
    /// <summary>列（去向），按 Id 去重、按种类与名字排。</summary>
    public List<SinkCapacityBar> Sinks { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>本期汇总 —— <b>复用 <see cref="PeriodBalance"/></b>，不在这里另算一份。</summary>
    public PeriodBalance Balance { get; set; } = new();

    public bool Empty => Rows.Count == 0;

    /// <summary>汇总文案（采出 / 剥离 / 排弃占容 / 剥采比 / 内排率 / 运输功 / 吨量加权运距）。</summary>
    public string SummaryCaption
    {
        get
        {
            if (Empty) return "本期没有物料流 —— 采排配对无从谈起。";
            var b = Balance;
            return $"采出 {b.OreWanT:0.00} 万t　·　剥离 {b.StripWanM3:0.00} 万m³实方"
                 + $"　·　剥采比 {(b.OreWanT > 1e-9 ? b.StripRatio.ToString("0.00", CultureInfo.InvariantCulture) + " m³/t" : "—")}"
                 + $"　·　排弃占容 {b.DumpedWanM3:0.00} 万m³"
                 + $"　·　内排率 {(b.DumpedWanM3 > 1e-9 ? b.InternalDumpPct.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—")}"
                 + $"　·　运输功 {b.TransportWorkWanTKm:0.00} 万t·km"
                 + $"　·　吨量加权运距 {(b.WeightedAvgHaulKm > 1e-9 ? b.WeightedAvgHaulKm.ToString("0.00", CultureInfo.InvariantCulture) + " km" : "—")}";
        }
    }
}

/// <summary>
/// 采排配对（移植原 <c>PlanLib</c> 「采排配对」窗口的<b>视图</b>部分）：
/// 源—汇流向矩阵（行 = 作业面·物料，列 = 去向，格 = 该 O-D 的实方量与运距）
/// + 各去向库容条 + 本期汇总。
///
/// <para>
/// <b>★ 本层不自己做配对</b>（照搬原版那条最要紧的话）：配对只在一处解，这里只是<b>把它取过来看</b>。
/// 两处各解一次的话，矩阵上显示的对位关系与真正排产用的那一份会分叉，而两边各自都自洽。
/// </para>
///
/// <para>
/// <b>库容按占容方扣</b>：<c>V容 = V实 × Kr</c>。拿实方去扣库容会把排土场算得比实际能装得多 ——
/// 松散系数残余 Kr &gt; 1，同样的实方占的库容更大。
/// </para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item>原版的对位关系来自<b>采掘单元清单</b>（一行一个采掘单元 = 图上一个体）——
///     那张台账未移植。Kylin 侧的源—汇取自<b>作业面台账</b>（<c>working_face_routing</c> 的
///     <c>splits_json</c> 混采拆分，§三三七 已接）。两者粒度不同：单元是"图上一个体"，
///     作业面是"一个面"，<b>不能互相冒充</b> —— 本窗如实标明取自作业面台账。</item>
///   <item>因此「取单元链的对位结果」「回去改轴重排」两个按钮<b>未移</b>。</item>
///   <item>手改分配（人工覆盖）未移：Kylin 侧这里是只读视图。</item>
/// </list>
/// </summary>
public static class DumpPairingModel
{
    /// <summary>
    /// 由物料流 + 去向登记簿建视图。
    /// <paramref name="sinks"/> 为 null 时库容条只有本期入方那一段（设计/已填判不了）。
    /// </summary>
    public static PairingView Build(IEnumerable<MaterialFlow>? flows, SinkRegistry? sinks, string period)
    {
        var view = new PairingView { Period = period };
        var list = (flows ?? Enumerable.Empty<MaterialFlow>()).Where(f => f != null && f.InSituM3 > 1e-9).ToList();
        view.Balance = new PeriodBalance { Period = period, Flows = list };

        if (list.Count == 0)
        {
            view.Notes.Add("本期没有物料流 —— 先到「作业面台账」把各面的当日目标与去向录进去。");
            return view;
        }

        // ── 行：作业面 × 物料 ──
        foreach (var g in list.GroupBy(f => (f.SourceName, f.MaterialCode))
                              .OrderBy(g => g.Key.SourceName, StringComparer.Ordinal)
                              .ThenBy(g => g.Key.MaterialCode, StringComparer.Ordinal))
        {
            var row = new PairingRow
            {
                SourceName = g.Key.SourceName,
                MaterialCode = g.Key.MaterialCode,
                InSituM3 = g.Sum(f => f.InSituM3),
            };
            foreach (var s in g.GroupBy(f => f.SinkId.Length > 0 ? f.SinkId : f.SinkName, StringComparer.OrdinalIgnoreCase))
            {
                if (s.Key.Length == 0) continue;              // 没去向的量不进矩阵，由 UnroutedM3 报
                row.Cells[s.Key] = new PairingCell
                {
                    SinkId = s.Key,
                    SinkName = s.First().SinkName.Length > 0 ? s.First().SinkName : s.Key,
                    InSituM3 = s.Sum(f => f.InSituM3),
                    // 一条 O-D 上多笔流时运距取吨量加权 —— 直接取平均会让小笔和大笔一样重
                    HaulKm = WeightedKm(s),
                };
            }
            view.Rows.Add(row);
        }

        // ── 列：去向库容条 ──
        var byId = new Dictionary<string, SinkCapacityBar>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list)
        {
            string key = f.SinkId.Length > 0 ? f.SinkId : f.SinkName;
            if (key.Length == 0) continue;
            if (!byId.TryGetValue(key, out var bar))
            {
                var node = sinks?.Find(f.SinkId)
                        ?? sinks?.All.FirstOrDefault(x => string.Equals(x.Name, f.SinkName, StringComparison.OrdinalIgnoreCase));
                bar = new SinkCapacityBar
                {
                    SinkId = key,
                    SinkName = f.SinkName.Length > 0 ? f.SinkName : key,
                    Kind = node?.Kind ?? f.SinkKind,
                    DesignM3 = node?.DesignCapacityM3 ?? 0,
                    FilledM3 = node?.FilledM3 ?? 0,
                };
                byId[key] = bar;
            }
            // ★ 只有排弃类去向才扣库容：破碎站/煤仓是过站，不占排土库容
            if (bar.Kind.IsDumping()) bar.InboundDumpM3 += f.DumpM3;
        }
        view.Sinks.AddRange(byId.Values
            .OrderBy(b => b.Kind.IsDumping() ? 0 : 1)
            .ThenBy(b => b.SinkName, StringComparer.Ordinal));

        // ── 逐条提示 ──
        double unrouted = view.Rows.Sum(r => r.UnroutedM3);
        if (unrouted > 1)
            view.Notes.Add($"有 {unrouted:N0} m³ 实方没有去向 —— 采了没处去。到「作业面台账」给这些面指派卸点。");

        foreach (var b in view.Sinks.Where(b => b.Overflow))
            view.Notes.Add($"{b.SinkName}：本期排不下，还差 {Math.Abs(b.RemainM3!.Value) / 1e4:0.##} 万m³ 库容 —— "
                         + "扩容、改排别处，或把这部分量挪到下期。");

        int noDesign = view.Sinks.Count(b => b.Kind.IsDumping() && b.DesignM3 <= 1e-6);
        if (noDesign > 0)
            view.Notes.Add($"{noDesign} 个排弃去向没录设计库容 ⇒ 期末剩余**判不了**（不是「够用」）。"
                         + "到「去向台账」补设计库容。");

        int noKm = list.Count(f => f.EffectiveHaulKm <= 1e-6);
        if (noKm > 0)
            view.Notes.Add($"{noKm} 条流向没录运距 ⇒ 运输功与加权运距都偏小（按 0 km 计入了分母）。");

        return view;
    }

    private static double WeightedKm(IEnumerable<MaterialFlow> flows)
    {
        double t = flows.Sum(f => f.TonnageT);
        return t <= 1e-6 ? 0 : flows.Sum(f => f.TransportWorkTKm) / t;
    }

    /// <summary>导出 CSV（矩阵按行铺开，一行一条 O-D）。</summary>
    public static string ToCsv(PairingView view)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# ").Append(view.SummaryCaption.Replace('　', ' ')).Append('\n');
        sb.Append("作业面,物料,去向,实方m3,运距km\n");
        foreach (var r in view.Rows)
            foreach (var c in r.Cells.Values.OrderBy(c => c.SinkName, StringComparer.Ordinal))
                sb.Append(string.Join(",", new[]
                {
                    Q(r.SourceName), Q(r.MaterialName), Q(c.SinkName),
                    c.InSituM3.ToString("0.##", CultureInfo.InvariantCulture),
                    c.HaulKm > 1e-6 ? c.HaulKm.ToString("0.###", CultureInfo.InvariantCulture) : "",
                })).Append('\n');
        sb.Append("\n# 去向库容（占容方 V容 = V实 × Kr）\n");
        sb.Append("去向,种类,设计m3,已填m3,本期入方占容m3,期末剩余m3\n");
        foreach (var b in view.Sinks)
            sb.Append(string.Join(",", new[]
            {
                Q(b.SinkName), Q(b.Kind.Label()),
                b.DesignM3 > 1e-6 ? b.DesignM3.ToString("0.##", CultureInfo.InvariantCulture) : "",
                b.FilledM3.ToString("0.##", CultureInfo.InvariantCulture),
                b.InboundDumpM3.ToString("0.##", CultureInfo.InvariantCulture),
                b.RemainM3 is { } r ? r.ToString("0.##", CultureInfo.InvariantCulture) : "",
            })).Append('\n');
        return sb.ToString();
    }

    private static string Q(string? s)
    {
        string v = s ?? "";
        return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
