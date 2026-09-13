// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportEngine.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.TaskLib.Domain;   // SinkKind.Label()：去向类型中文只此一份映射

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 报表引擎 —— 单入口：模板定义 + 上下文 + 事实宽表 → 渲染树 <see cref="ReportDocument"/>。
/// 「一键生成」按钮背后就是调它一次：取数(范围过滤) → 明细带按维分组展开 → 每格算指标 → 合计 → 图表 → 结论。
/// </summary>
public sealed class ReportEngine
{
    private readonly IndicatorRegistry _indicators;
    public ReportEngine(IndicatorRegistry indicators) => _indicators = indicators;

    public ReportDocument Generate(ReportDefinition def, GenerationContext ctx, IReadOnlyList<ProductionFact> allFacts)
    {
        // ── 范围过滤（口径缺省走上下文，这是"一键"的关键）──
        //  四个维度的范围：时间(区间) / 源侧(作业面) / 汇侧(去向) / 物料。缺一个都问不出
        //  「北排土场这周从哪几个面收了多少岩」这种最常见的调度问题。
        //  时间维必须在这里过滤：取数层可能给了整月的事实，而本次要出的是周报。
        var facts = allFacts.Where(f =>
                (!ctx.HasRange || (f.Date.Date >= ctx.From.Date && f.Date.Date <= ctx.To.Date))
             && (string.IsNullOrEmpty(ctx.PanelFilter) || f.Panel == ctx.PanelFilter)
             && (string.IsNullOrEmpty(ctx.DestinationFilter) || f.DestinationKey == ctx.DestinationFilter)
             && (string.IsNullOrEmpty(ctx.MaterialFilter) || f.MaterialKey == ctx.MaterialFilter))
            .ToList();

        var doc = new ReportDocument
        {
            Title = def.Title,
            Subtitle = def.Kind == ReportKind.Matrix && def.Matrix != null
                ? $"{GenerationContext.PeriodName(ctx.Period)} · {ReportDefinition.DimLabel(def.Matrix.RowDim)}×{ReportDefinition.DimLabel(def.Matrix.ColDim)} 流向矩阵"
                : $"{GenerationContext.PeriodName(ctx.Period)} · {ReportDefinition.DimLabel(def.DetailGroup)}汇总",
        };
        doc.MetaLines.Add($"单位：{ctx.Mine}　范围：{ScopeCaption(ctx)}");
        doc.MetaLines.Add($"期间：{(string.IsNullOrEmpty(ctx.PeriodLabel) ? ctx.AsOf.ToString("yyyy-MM-dd") : ctx.PeriodLabel)}　生成：{ctx.AsOf:yyyy-MM-dd HH:mm}");

        foreach (var c in def.Columns)
            doc.Columns.Add(new ColumnView { Header = c.Header, Width = c.Width, Align = c.Align });

        // ── 顶部 KPI 指标卡（对全范围聚合，带红黄绿+目标）──
        foreach (var id in def.KpiIndicators)
        {
            var ki = _indicators.Resolve(id);
            if (ki == null) continue;
            double kv = ki.Evaluate(facts);
            doc.KpiCards.Add(new KpiCard
            {
                Label = ki.Name,
                ValueText = ki.FormatValue(kv),
                Unit = ki.Unit,
                Status = ki.StatusOf(kv),
                SubText = ki.Target is double tt ? $"目标 {tt:0.#}{ki.Unit}" : "",
            });
        }

        // ── 明细带：按维分组，每组一行 ──
        Func<ProductionFact, string> keyOf = KeySelector(def.DetailGroup);
        var groups = facts.GroupBy(keyOf).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var g in groups)
        {
            var groupFacts = g.ToList();
            var row = new RowView();
            foreach (var col in def.Columns)
                row.Cells.Add(BuildCell(col, g.Key, groupFacts, def.DetailGroup));
            doc.Rows.Add(row);
        }

        // ── 报表尾合计行：Total 列对全体聚合；非 Total 列留空或标签 ──
        if (def.ShowTotalRow && def.Columns.Count > 0)
        {
            var total = new RowView { Emphasize = true };
            bool labelPlaced = false;
            foreach (var col in def.Columns)
            {
                if (col.Bind == ColBind.Dimension && !labelPlaced)
                {
                    total.Cells.Add(new CellView { Text = def.TotalRowLabel, Align = col.Align, Bold = true });
                    labelPlaced = true;
                }
                else if (col.Bind == ColBind.Indicator && col.Total)
                {
                    var ind = _indicators.Resolve(col.IndicatorId);
                    double v = ind?.Evaluate(facts) ?? double.NaN;
                    total.Cells.Add(new CellView { Text = ind?.FormatValue(v) ?? "—", Align = col.Align, Bold = true, Status = ind?.StatusOf(v) ?? ReportStatus.None });
                }
                else
                {
                    total.Cells.Add(new CellView { Text = labelPlaced ? "" : def.TotalRowLabel, Align = col.Align, Bold = true });
                    labelPlaced = true;
                }
            }
            doc.TotalRow = total;
        }

        // ── 交叉表带（采—排流向矩阵：行=源、列=汇、格=该 O-D 的量与运距）──
        if (def.Matrix != null) doc.Matrix = BuildMatrix(def.Matrix, facts);

        // ── 图表带（给 IndicatorId2 则出"计划vs实绩"双色对比柱）──
        foreach (var ch in def.Charts)
        {
            var ind = _indicators.Resolve(ch.IndicatorId);
            if (ind == null) continue;
            var ind2 = string.IsNullOrEmpty(ch.IndicatorId2) ? null : _indicators.Resolve(ch.IndicatorId2);
            var byKey = KeySelector(ch.By);
            var bars = facts.GroupBy(byKey).OrderBy(gr => gr.Key, StringComparer.Ordinal)
                .Select(gr =>
                {
                    var gf = gr.ToList();
                    return new ChartBar { Label = gr.Key, Value = ind.Evaluate(gf), Value2 = ind2 != null ? ind2.Evaluate(gf) : double.NaN };
                })
                .Where(b => !double.IsNaN(b.Value)).ToList();
            doc.Charts.Add(new ChartView
            {
                Title = string.IsNullOrEmpty(ch.Title) ? ind.Name : ch.Title,
                Unit = ind.Unit,
                SeriesA = ind2 != null ? ind.Name : "",
                SeriesB = ind2?.Name ?? "",
                Bars = bars,
            });
        }

        // ── 结论（规则评语，报告叙述段）──
        if (def.ShowConclusion) doc.Conclusion = BuildConclusion(facts);

        // ── 口径脚注：模板自带的 + 取数说明 + 引擎自动发现的数据缺口 ──
        doc.Footnotes.AddRange(def.Footnotes);
        if (!string.IsNullOrWhiteSpace(ctx.SourceNote)) doc.Footnotes.Add("取数：" + ctx.SourceNote);
        doc.Footnotes.AddRange(DataGapNotes(facts));

        // ── 报告(Narrative)：叙述章节占位符成文 + 签批栏 ──
        if (def.Kind == ReportKind.Narrative)
        {
            foreach (var s in def.NarrativeSections)
                doc.Narrative.Add(new NarrativeBlock { Heading = s.Heading, Body = Substitute(s.Body, facts, ctx) });
            doc.Signatures = def.SignatureLine;
        }

        return doc;
    }

    /// <summary>把叙述模板里的 {指标Id} 与 {@mine/@scope/@period/@date} 占位符替换成实际值。</summary>
    private string Substitute(string body, IReadOnlyList<ProductionFact> facts, GenerationContext ctx)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var sb = new StringBuilder(body.Length + 32);
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == '{')
            {
                int j = body.IndexOf('}', i);
                if (j < 0) { sb.Append(body, i, body.Length - i); break; }
                sb.Append(ResolvePlaceholder(body.Substring(i + 1, j - i - 1).Trim(), facts, ctx));
                i = j + 1;
            }
            else { sb.Append(body[i]); i++; }
        }
        return sb.ToString();
    }

    private string ResolvePlaceholder(string key, IReadOnlyList<ProductionFact> facts, GenerationContext ctx)
    {
        if (key.StartsWith("@"))
            return key switch
            {
                "@mine" => ctx.Mine,
                "@scope" => ctx.ScopeLabel,
                "@period" => GenerationContext.PeriodName(ctx.Period),
                "@periodLabel" => ctx.PeriodLabel,
                "@date" => ctx.AsOf.ToString("yyyy-MM-dd"),
                _ => "",
            };
        var ind = _indicators.Resolve(key);
        if (ind == null) return "{" + key + "}";   // 未知占位符原样保留，便于发现拼写错误
        double v = ind.Evaluate(facts);
        return double.IsNaN(v) ? "—" : ind.FormatValue(v);
    }

    private CellView BuildCell(ReportColumn col, string groupKey, IReadOnlyList<ProductionFact> groupFacts, GroupDim dim)
    {
        if (col.Bind == ColBind.Dimension)
        {
            // 去向维的分组头带上去向类型（内排/外排/破碎站…）——内排与外排的成本差是决策的关键，
            // 光看名字看不出来。SinkKind 的中文一律走 .Label()，不在渲染层另写一套映射。
            string text = groupKey;
            if (dim == GroupDim.Destination)
            {
                var head = groupFacts.FirstOrDefault(x => x.HasDestination);
                if (head != null) text += $"（{head.DestinationKind.Label()}）";
            }
            return new CellView { Text = text, Align = col.Align };
        }

        var ind = _indicators.Resolve(col.IndicatorId);
        if (ind == null) return new CellView { Text = "?", Align = col.Align };
        double v = ind.Evaluate(groupFacts);
        return new CellView { Text = ind.FormatValue(v), Align = col.Align, Status = ind.StatusOf(v) };
    }

    private static Func<ProductionFact, string> KeySelector(GroupDim d) => d switch
    {
        GroupDim.Equipment => f => f.Equipment,
        GroupDim.Process => f => f.Process,
        GroupDim.Shift => f => f.Shift,
        // 去向维：未定去向的量归到"未指定去向"独立成行，绝不并进某个真实排土场
        GroupDim.Destination => f => f.DestinationKey,
        GroupDim.Material => f => f.MaterialKey,
        _ => f => f.Panel,
    };

    /// <summary>范围文案：三个过滤器都体现出来，免得看报表的人不知道这份数据被裁过。</summary>
    private static string ScopeCaption(GenerationContext ctx)
    {
        var parts = new List<string> { string.IsNullOrWhiteSpace(ctx.ScopeLabel) ? "全矿" : ctx.ScopeLabel };
        if (!string.IsNullOrEmpty(ctx.DestinationFilter)) parts.Add($"去向：{ctx.DestinationFilter}");
        if (!string.IsNullOrEmpty(ctx.MaterialFilter)) parts.Add($"物料：{ctx.MaterialFilter}");
        return string.Join(" · ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  交叉表（O-D 流向矩阵）
    //
    //  分带结构是「一行 = 一个维成员」，撑不起两维交叉，故单列一条渲染路径：
    //  行=源(作业面)、列=汇(去向)、单元格=该 O-D 的主指标（括号里带副指标，通常是运距）。
    //  这正是露天矿调度最经典的一张表——"从哪采剥、排弃到哪"这个问题本身。
    // ─────────────────────────────────────────────────────────────────────────
    private MatrixTableView? BuildMatrix(MatrixSpec spec, IReadOnlyList<ProductionFact> facts)
    {
        var ind = _indicators.Resolve(spec.IndicatorId);
        if (ind == null) return null;
        var ind2 = string.IsNullOrEmpty(spec.IndicatorId2) ? null : _indicators.Resolve(spec.IndicatorId2);

        // O-D 的源只能是采装侧，否则排土面会以"内排场→内排场"的形式把同一批料再记一遍
        var src = spec.SourceSideOnly ? facts.Where(f => f.CountsAsMined).ToList() : facts.ToList();

        var rowKey = KeySelector(spec.RowDim);
        var colKey = KeySelector(spec.ColDim);

        var rowKeys = src.Select(rowKey).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var colKeys = src.Select(colKey).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();

        // 稀疏矩阵：整行/整列都没量的直接不出（O-D 矩阵天然稀疏，全画出来没法看）
        if (spec.HideEmpty)
        {
            colKeys = colKeys.Where(c => HasValue(ind, src.Where(f => colKey(f) == c).ToList())).ToList();
            rowKeys = rowKeys.Where(r => HasValue(ind, src.Where(f => rowKey(f) == r).ToList())).ToList();
        }
        if (rowKeys.Count == 0 || colKeys.Count == 0) return null;

        var m = new MatrixTableView
        {
            Title = string.IsNullOrEmpty(spec.Title) ? $"{ReportDefinition.DimLabel(spec.RowDim)} → {ReportDefinition.DimLabel(spec.ColDim)} 流向矩阵" : spec.Title,
            RowHeader = $"{ReportDefinition.DimLabel(spec.RowDim)}＼{ReportDefinition.DimLabel(spec.ColDim)}",
            Legend = ind2 == null
                ? $"单元格：{ind.Name}（{ind.Unit}）"
                : $"单元格：{ind.Name}（{ind.Unit}），括号内为{ind2.Name}（{ind2.Unit}）",
        };
        m.ColumnKeys.AddRange(colKeys);
        if (spec.ShowTotals) m.ColumnKeys.Add("合计");

        foreach (var rk in rowKeys)
        {
            var rowFacts = src.Where(f => rowKey(f) == rk).ToList();
            var row = new MatrixRowView { Key = rk };
            foreach (var ck in colKeys)
                row.Cells.Add(MatrixCell(ind, ind2, rowFacts.Where(f => colKey(f) == ck).ToList()));
            if (spec.ShowTotals) row.Cells.Add(MatrixCell(ind, ind2, rowFacts, bold: true));
            m.Rows.Add(row);
        }

        if (spec.ShowTotals)
        {
            var total = new MatrixRowView { Key = "合计", Emphasize = true };
            foreach (var ck in colKeys)
                total.Cells.Add(MatrixCell(ind, ind2, src.Where(f => colKey(f) == ck).ToList(), bold: true));
            total.Cells.Add(MatrixCell(ind, ind2, src, bold: true));
            m.Rows.Add(total);
        }
        return m;
    }

    private static bool HasValue(IndicatorDef ind, IReadOnlyList<ProductionFact> sel)
    {
        if (sel.Count == 0) return false;
        double v = ind.Evaluate(sel);
        return !double.IsNaN(v) && Math.Abs(v) > 1e-9;
    }

    /// <summary>一个 O-D 单元格："主指标（副指标）"；该 O-D 无流量则留"—"，不写 0 假装有条通道。</summary>
    private static CellView MatrixCell(IndicatorDef ind, IndicatorDef? ind2, IReadOnlyList<ProductionFact> sel, bool bold = false)
    {
        if (sel.Count == 0) return new CellView { Text = "—", Align = CellAlign.Right, Bold = bold };
        double v = ind.Evaluate(sel);
        if (double.IsNaN(v) || Math.Abs(v) <= 1e-9) return new CellView { Text = "—", Align = CellAlign.Right, Bold = bold };

        string text = ind.FormatValue(v);
        if (ind2 != null)
        {
            double v2 = ind2.Evaluate(sel);
            text += double.IsNaN(v2) ? "（—）" : $"（{ind2.FormatValue(v2)}）";
        }
        return new CellView { Text = text, Align = CellAlign.Right, Bold = bold, Status = ind.StatusOf(v) };
    }

    /// <summary>
    /// 数据缺口自动脚注 —— 报表显示"—"时必须说清是"没这回事"还是"数据没接上"。
    /// 这正是本轮把编造数据换成空值后最需要补的一句话。
    /// </summary>
    private static List<string> DataGapNotes(IReadOnlyList<ProductionFact> facts)
    {
        var notes = new List<string>();
        if (facts.Count == 0) return notes;

        var moving = facts.Where(f => f.MovesMaterial && f.ActualVolumeM3 > 1e-6).ToList();
        double movingT = moving.Sum(f => f.ActualTonnage);
        double knownT = moving.Where(f => f.HaulKnown).Sum(f => f.ActualTonnage);
        if (movingT > 1e-6 && knownT < movingT - 1e-6)
        {
            var zones = moving.Where(f => !f.HaulKnown).Select(f => f.Panel).Distinct().Take(4).ToList();
            notes.Add($"运距口径不全：仅 {knownT / movingT * 100:0.#}% 的搬运吨量有实测/解算运距，"
                    + $"缺运距的作业面为 {string.Join("、", zones)}；运距、运输功、单位油耗按已知部分统计，其余留空（不做估算填充）。");
        }

        var rejected = facts.Where(f => f.DestinationRejectedForMaterial && f.ActualVolumeM3 > 1e-6)
                            .Select(f => $"{f.Panel}·{f.MaterialName}").Distinct().ToList();
        if (rejected.Count > 0)
            notes.Add($"混采拆分未落到去向：{string.Join("、", rejected.Take(4))} —— 任务只能记一个主去向，"
                    + "而该主去向不接纳这些物料（如岩石不能进破碎站），故按「未指定去向」计，不编造运输通道；"
                    + "要让这部分量归位，需按物料把任务拆到各自的去向。");

        var noDest = facts.Where(f => f.CountsAsMined && !f.HasDestination
                                   && !f.DestinationRejectedForMaterial && f.ActualVolumeM3 > 1e-6)
                          .Select(f => f.Panel).Distinct().ToList();
        if (noDest.Count > 0)
            notes.Add($"去向未定：{string.Join("、", noDest.Take(4))} 的采装量尚未指定卸点，去向维归入「未指定去向」，内排率等汇侧指标留空。");

        notes.Add("口径：采出/剥离/剥采比按【实方】且只计采装侧（排土为同批料的接收侧，不重复计量）；"
                + "排弃量按【占容方＝实方×Kr】；配车/松方按【松方＝实方×Ks】；吨量为三者间的守恒量。"
                + "油耗、电耗、车次为经验系数估算，非实测。");
        return notes;
    }

    /// <summary>规则评语：整体达成率 + 主要欠产原因 + 建议（把结构化偏差翻成决策语言）。</summary>
    private string BuildConclusion(IReadOnlyList<ProductionFact> facts)
    {
        double plan = facts.Sum(f => f.PlanVolumeM3);
        double actual = facts.Sum(f => f.ActualVolumeM3);
        double attain = plan > 1e-9 ? actual / plan * 100 : double.NaN;
        double shortfall = facts.Sum(f => f.ShortfallM3);

        if (double.IsNaN(attain)) return "本期无计划量，暂无达成度评价。";

        // 欠产 TopN 原因
        var topReason = facts.Where(f => !string.IsNullOrEmpty(f.TopReason))
            .GroupBy(f => f.TopReason)
            .OrderByDescending(g => g.Sum(f => f.ShortfallM3))
            .Select(g => g.Key).FirstOrDefault();

        if (attain >= 100)
            return $"本期计划 {plan / 1e4:0.00} 万m³，实绩 {actual / 1e4:0.00} 万m³，达成率 {attain:0.0}% ✔ 达标。";

        string reason = string.IsNullOrEmpty(topReason) ? "" : $"，主要影响因素为「{topReason}」";
        string advice = attain < 90 ? "，建议排查欠产作业面、调配设备编组并将欠量滚动回摊。" : "，建议关注临界作业面。";
        return $"本期计划 {plan / 1e4:0.00} 万m³，实绩 {actual / 1e4:0.00} 万m³，达成率 {attain:0.0}%⚠，欠产 {shortfall / 1e4:0.00} 万m³{reason}{advice}";
    }
}
