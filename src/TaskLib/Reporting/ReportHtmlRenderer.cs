// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportHtmlRenderer.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>渲染器 —— 把渲染树输出成自包含 HTML（内联 CSS，KPI 卡片 + 双色对比柱 + 斑马纹表）。</summary>
public static class ReportHtmlRenderer
{
    public static string Render(ReportDocument doc)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-cn\"><head><meta charset=\"UTF-8\">");
        sb.Append("<title>").Append(Esc(doc.Title)).AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:'Microsoft YaHei',-apple-system,'Segoe UI',sans-serif;margin:28px;color:#1f2937;}");
        sb.AppendLine("h1{text-align:center;color:#1e3a5f;margin:0 0 2px;border-bottom:2px solid #00BFFE;padding-bottom:8px;}");
        sb.AppendLine(".sub{text-align:center;color:#6c757d;font-size:13px;margin:4px 0;}");
        sb.AppendLine(".meta{display:flex;justify-content:center;gap:24px;color:#6c757d;font-size:12px;margin:8px 0 4px;}");
        sb.AppendLine(".cards{display:flex;flex-wrap:wrap;gap:10px;margin:16px 0 8px;}");
        sb.AppendLine(".card{border:1px solid #e2e8f0;border-left:4px solid #00BFFE;border-radius:6px;background:#f8fafc;padding:9px 16px 9px 13px;min-width:118px;}");
        sb.AppendLine(".card .lab{font-size:12px;color:#6c757d;} .card .val{font-size:22px;font-weight:700;} .card .unit{font-size:12px;color:#6c757d;font-weight:400;} .card .sub{font-size:11px;color:#9aa5b1;margin-top:1px;}");
        sb.AppendLine(".card.ok{border-left-color:#1d9e75;} .card.warn{border-left-color:#c98a18;} .card.bad{border-left-color:#c0392b;}");
        sb.AppendLine(".val.ok{color:#0f6e56;} .val.warn{color:#b7791f;} .val.bad{color:#c0392b;}");
        sb.AppendLine(".sec{display:flex;align-items:center;gap:7px;margin:20px 0 6px;} .sec i{width:4px;height:15px;background:#00BFFE;border-radius:2px;} .sec b{color:#1e3a5f;font-size:15px;} .sec span{color:#9aa5b1;font-size:11px;}");
        sb.AppendLine("h3{color:#1e3a5f;font-size:14px;margin:16px 0 6px;}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:13px;}");
        sb.AppendLine("th,td{padding:6px 10px;border-bottom:1px solid #dee2e6;}");
        sb.AppendLine("th{background:#e9eef4;color:#334155;font-weight:600;border-bottom:1px solid #9db2c6;}");
        sb.AppendLine("tbody tr:nth-child(even) td{background:#f6f9fc;}");
        sb.AppendLine("tr.total td{background:#eaf0f6;border-top:2px solid #9db2c6;font-weight:600;}");
        sb.AppendLine(".r{text-align:right;} .c{text-align:center;} .l{text-align:left;}");
        sb.AppendLine("td.ok{color:#1d9e75;} td.warn{color:#c98a18;} td.bad{color:#c0392b;font-weight:600;}");
        sb.AppendLine(".concl{margin-top:18px;background:#f5f8fc;padding:12px 14px;border-radius:6px;font-size:13px;}");
        sb.AppendLine(".notes{margin-top:14px;color:#6c757d;font-size:11.5px;} .notes ul{margin:4px 0 0;padding-left:18px;} .notes li{margin:2px 0;line-height:1.6;}");
        sb.AppendLine(".sign{text-align:right;margin-top:28px;font-size:13px;}");
        sb.AppendLine(".legend{font-size:12px;color:#6c757d;font-weight:400;} .legend i{display:inline-block;width:11px;height:11px;border-radius:2px;vertical-align:middle;margin:0 3px 0 10px;}");
        sb.AppendLine(".bar-row{display:flex;align-items:center;gap:8px;margin:3px 0;font-size:12px;}");
        sb.AppendLine(".bar-lab{width:110px;} .bar-track{flex:1;background:#eef2f6;height:14px;border-radius:2px;} .bar-fill{background:#1d9e75;height:14px;border-radius:2px;} .bar-val{width:120px;text-align:right;color:#6c757d;}");
        sb.AppendLine(".bar2{flex:1;display:flex;flex-direction:column;gap:2px;} .bar-track2{width:100%;height:9px;background:#eef2f6;border-radius:2px;} .bar-fill2{height:9px;border-radius:2px;}");
        sb.AppendLine("</style></head><body>");

        sb.Append("<h1>").Append(Esc(doc.Title)).AppendLine("</h1>");
        if (!string.IsNullOrEmpty(doc.Subtitle)) sb.Append("<div class=\"sub\">").Append(Esc(doc.Subtitle)).AppendLine("</div>");
        sb.Append("<div class=\"meta\">");
        foreach (var m in doc.MetaLines) sb.Append("<span>").Append(Esc(m)).Append("</span>");
        sb.AppendLine("</div>");

        // KPI 卡片
        if (doc.KpiCards.Count > 0)
        {
            sb.AppendLine("<div class=\"cards\">");
            foreach (var k in doc.KpiCards)
            {
                string cls = StatusCls(k.Status);
                sb.Append("<div class=\"card ").Append(cls).Append("\"><div class=\"lab\">").Append(Esc(k.Label)).Append("</div>");
                sb.Append("<div class=\"val ").Append(cls).Append("\">").Append(Esc(k.ValueText));
                if (!string.IsNullOrEmpty(k.Unit)) sb.Append("<span class=\"unit\"> ").Append(Esc(k.Unit)).Append("</span>");
                sb.Append("</div>");
                if (!string.IsNullOrEmpty(k.SubText)) sb.Append("<div class=\"sub\">").Append(Esc(k.SubText)).Append("</div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        // 报告叙述章节
        foreach (var nb in doc.Narrative)
        {
            if (!string.IsNullOrEmpty(nb.Heading))
                sb.Append("<h3>").Append(Esc(nb.Heading)).AppendLine("</h3>");
            sb.Append("<p style=\"line-height:1.7;margin:0 0 6px\">").Append(Esc(nb.Body)).AppendLine("</p>");
        }

        // 明细表（斑马纹由 CSS 处理）
        if (doc.Columns.Count > 0)
        {
            sb.Append("<div class=\"sec\"><i></i><b>明细</b><span>共 ").Append(doc.Rows.Count).AppendLine(" 行</span></div>");
            sb.AppendLine("<table><thead><tr>");
            foreach (var c in doc.Columns) sb.Append("<th class=\"").Append(AlignCls(c.Align)).Append("\">").Append(Esc(c.Header)).Append("</th>");
            sb.AppendLine("</tr></thead><tbody>");
            foreach (var row in doc.Rows)
            {
                sb.Append("<tr>");
                foreach (var cell in row.Cells) Cell(sb, cell);
                sb.AppendLine("</tr>");
            }
            if (doc.TotalRow != null)
            {
                sb.Append("<tr class=\"total\">");
                foreach (var cell in doc.TotalRow.Cells) Cell(sb, cell);
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        // 交叉表（采—排流向矩阵）
        if (doc.Matrix is { Rows.Count: > 0 } mx)
        {
            sb.Append("<div class=\"sec\"><i></i><b>").Append(Esc(mx.Title)).Append("</b><span>")
              .Append(Esc(mx.Legend)).AppendLine("</span></div>");
            sb.AppendLine("<table><thead><tr>");
            sb.Append("<th class=\"l\">").Append(Esc(mx.RowHeader)).Append("</th>");
            foreach (var c in mx.ColumnKeys) sb.Append("<th class=\"r\">").Append(Esc(c)).Append("</th>");
            sb.AppendLine("</tr></thead><tbody>");
            foreach (var row in mx.Rows)
            {
                sb.Append(row.Emphasize ? "<tr class=\"total\">" : "<tr>");
                sb.Append("<td class=\"l\">").Append(Esc(row.Key)).Append("</td>");
                foreach (var cell in row.Cells) Cell(sb, cell);
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        // 图表（对比图双色柱）
        foreach (var ch in doc.Charts.Where(c => c.Bars.Count > 0))
        {
            bool grouped = !string.IsNullOrEmpty(ch.SeriesB);
            double max = ch.Bars.Max(b => grouped ? Math.Max(b.Value, double.IsNaN(b.Value2) ? 0 : b.Value2) : b.Value);
            if (max <= 0) continue;
            sb.Append("<h3>").Append(Esc(ch.Title)).Append("（").Append(Esc(ch.Unit)).Append("）");
            if (grouped)
                sb.Append("<span class=\"legend\"><i style=\"background:#b5d4f4\"></i>").Append(Esc(ch.SeriesA))
                  .Append("<i style=\"background:#1d9e75\"></i>").Append(Esc(ch.SeriesB)).Append("</span>");
            sb.AppendLine("</h3>");
            foreach (var b in ch.Bars)
            {
                if (grouped)
                {
                    double v2 = double.IsNaN(b.Value2) ? 0 : b.Value2;
                    sb.Append("<div class=\"bar-row\"><span class=\"bar-lab\">").Append(Esc(b.Label)).Append("</span><span class=\"bar2\">")
                      .Append("<span class=\"bar-track2\"><span class=\"bar-fill2\" style=\"background:#b5d4f4;width:").Append(P(b.Value / max * 100)).Append("%\"></span></span>")
                      .Append("<span class=\"bar-track2\"><span class=\"bar-fill2\" style=\"background:#1d9e75;width:").Append(P(v2 / max * 100)).Append("%\"></span></span>")
                      .Append("</span><span class=\"bar-val\">").Append(b.Value.ToString("N0")).Append(" / ").Append(v2.ToString("N0")).AppendLine("</span></div>");
                }
                else
                {
                    sb.Append("<div class=\"bar-row\"><span class=\"bar-lab\">").Append(Esc(b.Label))
                      .Append("</span><span class=\"bar-track\"><span class=\"bar-fill\" style=\"width:").Append(P(b.Value / max * 100)).Append("%\"></span></span><span class=\"bar-val\">")
                      .Append(b.Value.ToString("N0")).AppendLine("</span></div>");
                }
            }
        }

        if (!string.IsNullOrEmpty(doc.Conclusion))
            sb.Append("<div class=\"concl\"><b>分析结论：</b>").Append(Esc(doc.Conclusion)).AppendLine("</div>");

        if (doc.Footnotes.Count > 0)
        {
            sb.AppendLine("<div class=\"notes\"><b>口径说明</b><ul>");
            foreach (var n in doc.Footnotes) sb.Append("<li>").Append(Esc(n)).AppendLine("</li>");
            sb.AppendLine("</ul></div>");
        }

        if (!string.IsNullOrEmpty(doc.Signatures))
            sb.Append("<div class=\"sign\">").Append(Esc(doc.Signatures)).AppendLine("</div>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void Cell(StringBuilder sb, CellView cell)
    {
        string cls = AlignCls(cell.Align);
        string st = cell.Status switch { ReportStatus.Ok => " ok", ReportStatus.Warn => " warn", ReportStatus.Bad => " bad", _ => "" };
        sb.Append("<td class=\"").Append(cls).Append(st).Append("\">").Append(Esc(cell.Text)).Append("</td>");
    }

    private static string StatusCls(ReportStatus s) => s switch { ReportStatus.Ok => "ok", ReportStatus.Warn => "warn", ReportStatus.Bad => "bad", _ => "" };
    private static string AlignCls(CellAlign a) => a switch { CellAlign.Right => "r", CellAlign.Center => "c", _ => "l" };
    private static string P(double x) => x.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" :
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
