// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportPdfRenderer.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Threading;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 渲染器 —— 把渲染树 <see cref="ReportDocument"/> 输出成 PDF（QuestPDF 社区版）。
/// 版式：报表头 · KPI 指标卡 · 报告叙述 · 明细表(斑马纹) · 图表(计划vs实绩双色柱) · 结论 · 签批。
/// </summary>
public static class ReportPdfRenderer
{
    private static int s_license;
    private static void EnsureLicense()
    {
        if (Interlocked.Exchange(ref s_license, 1) == 0)
            QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void Write(ReportDocument doc, string filePath)
    {
        EnsureLicense();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(22);
                page.DefaultTextStyle(x => x.FontFamily("Microsoft YaHei", "SimSun", Fonts.Calibri).FontSize(9));
                page.Header().Element(h => Header(h, doc));
                page.Content().Element(c => Body(c, doc));
                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("DayOps 生产报表 · 第 ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" 页").FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        }).GeneratePdf(filePath);
    }

    private static void Header(IContainer c, ReportDocument doc)
    {
        c.PaddingBottom(8).BorderBottom(2).BorderColor("#00BFFE").Column(col =>
        {
            col.Item().AlignCenter().Text(doc.Title).FontSize(17).Bold().FontColor("#1E3A5F");
            if (!string.IsNullOrEmpty(doc.Subtitle))
                col.Item().AlignCenter().Text(doc.Subtitle).FontSize(10).FontColor(Colors.Grey.Medium);
            col.Item().PaddingTop(3).Row(r =>
            {
                foreach (var m in doc.MetaLines)
                    r.RelativeItem().Text(m).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            });
        });
    }

    private static void Body(IContainer c, ReportDocument doc)
    {
        c.PaddingVertical(10).Column(col =>
        {
            // ── KPI 指标卡 ──
            if (doc.KpiCards.Count > 0)
                col.Item().PaddingBottom(4).Row(row =>
                {
                    foreach (var k in doc.KpiCards)
                    {
                        row.RelativeItem().PaddingRight(6).Border(0.6f).BorderColor("#E2E8F0").Background("#F8FAFC").Padding(8).Column(cc =>
                        {
                            cc.Item().Text(k.Label).FontSize(8.5f).FontColor("#6C757D");
                            cc.Item().Text(t =>
                            {
                                t.Span(k.ValueText).FontSize(15).SemiBold().FontColor(StatusColor(k.Status) ?? "#1F2937");
                                if (!string.IsNullOrEmpty(k.Unit)) t.Span(" " + k.Unit).FontSize(8).FontColor("#6C757D");
                            });
                            if (!string.IsNullOrEmpty(k.SubText)) cc.Item().Text(k.SubText).FontSize(7.5f).FontColor("#9AA5B1");
                        });
                    }
                });

            // ── 报告叙述章节 ──
            foreach (var nb in doc.Narrative)
            {
                if (!string.IsNullOrEmpty(nb.Heading))
                    col.Item().PaddingTop(10).Text(nb.Heading).FontSize(12).Bold().FontColor("#1E3A5F");
                col.Item().PaddingTop(3).Text(nb.Body).FontSize(10).LineHeight(1.5f).FontColor("#1F2937");
            }

            // ── 明细表（斑马纹）──
            if (doc.Columns.Count > 0)
                col.Item().PaddingTop(8).Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        foreach (var cv in doc.Columns) cd.RelativeColumn((float)Math.Max(1, cv.Width));
                    });
                    t.Header(h =>
                    {
                        foreach (var cv in doc.Columns)
                            AlignedCell(h.Cell().Element(HeaderCell), cv.Align).Text(cv.Header);
                    });
                    for (int ri = 0; ri < doc.Rows.Count; ri++)
                    {
                        var row = doc.Rows[ri];
                        string? zebra = ri % 2 == 1 ? "#F6F9FC" : null;
                        foreach (var cell in row.Cells)
                            AlignedCell(t.Cell().Element(x => BodyCell(x, zebra)), cell.Align).Text(txt =>
                            {
                                var span = txt.Span(cell.Text);
                                var color = StatusColor(cell.Status);
                                if (color != null) span.FontColor(color);
                                if (cell.Bold) span.SemiBold();
                            });
                    }
                    if (doc.TotalRow != null)
                        foreach (var cell in doc.TotalRow.Cells)
                            AlignedCell(t.Cell().Element(TotalCell), cell.Align).Text(txt =>
                            {
                                var span = txt.Span(cell.Text).SemiBold();
                                var color = StatusColor(cell.Status);
                                if (color != null) span.FontColor(color);
                            });
                });

            // ── 交叉表（采—排流向矩阵）──
            if (doc.Matrix is { Rows.Count: > 0 } mx)
            {
                col.Item().PaddingTop(14).Text(mx.Title).FontSize(11).Bold().FontColor("#1E3A5F");
                if (!string.IsNullOrEmpty(mx.Legend))
                    col.Item().PaddingTop(2).Text(mx.Legend).FontSize(8).FontColor(Colors.Grey.Medium);
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1.6f);
                        foreach (var _ in mx.ColumnKeys) cd.RelativeColumn(1.2f);
                    });
                    t.Header(h =>
                    {
                        h.Cell().Element(HeaderCell).AlignLeft().Text(mx.RowHeader);
                        foreach (var ck in mx.ColumnKeys) h.Cell().Element(HeaderCell).AlignRight().Text(ck);
                    });
                    foreach (var row in mx.Rows)
                    {
                        string? bg = row.Emphasize ? "#EAF0F6" : null;
                        t.Cell().Element(x => BodyCell(x, bg)).AlignLeft().Text(row.Key).SemiBold();
                        foreach (var cell in row.Cells)
                            t.Cell().Element(x => BodyCell(x, bg)).AlignRight().Text(txt =>
                            {
                                var span = txt.Span(cell.Text);
                                if (cell.Bold || row.Emphasize) span.SemiBold();
                            });
                    }
                });
            }

            // ── 图表条 ──
            foreach (var ch in doc.Charts.Where(c2 => c2.Bars.Count > 0))
            {
                col.Item().PaddingTop(14).Text(t =>
                {
                    t.Span($"{ch.Title}（{ch.Unit}）").FontSize(11).Bold().FontColor("#1E3A5F");
                    if (!string.IsNullOrEmpty(ch.SeriesB))
                    {
                        t.Span($"   ■{ch.SeriesA}").FontSize(9).FontColor("#7FB0E0");
                        t.Span($" ■{ch.SeriesB}").FontSize(9).FontColor("#1D9E75");
                    }
                });
                col.Item().PaddingTop(4).Element(e => BarChart(e, ch));
            }

            // ── 结论 ──
            if (!string.IsNullOrEmpty(doc.Conclusion))
            {
                col.Item().PaddingTop(14).Text("分析结论").FontSize(11).Bold().FontColor("#1E3A5F");
                col.Item().PaddingTop(3).Background("#F5F8FC").Padding(8).Text(doc.Conclusion).FontSize(9.5f).FontColor("#1F2937");
            }

            // ── 口径脚注 ──
            if (doc.Footnotes.Count > 0)
            {
                col.Item().PaddingTop(12).Text("口径说明").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                foreach (var n in doc.Footnotes)
                    col.Item().PaddingTop(1).Text("· " + n).FontSize(7.5f).LineHeight(1.4f).FontColor(Colors.Grey.Darken1);
            }

            // ── 签批栏 ──
            if (!string.IsNullOrEmpty(doc.Signatures))
                col.Item().PaddingTop(24).AlignRight().Text(doc.Signatures).FontSize(10).FontColor("#1F2937");
        });
    }

    private static void BarChart(IContainer c, ChartView ch)
    {
        bool grouped = !string.IsNullOrEmpty(ch.SeriesB);
        double max = Math.Max(1e-6, ch.Bars.Max(b => grouped ? Math.Max(b.Value, double.IsNaN(b.Value2) ? 0 : b.Value2) : b.Value));
        c.Column(col =>
        {
            foreach (var b in ch.Bars)
            {
                if (grouped)
                {
                    col.Item().PaddingVertical(2).Row(r =>
                    {
                        r.ConstantItem(96).AlignMiddle().Text(b.Label).FontSize(8.5f);
                        r.RelativeItem().Column(cc =>
                        {
                            cc.Item().Element(e => BarLine(e, b.Value, max, "#B5D4F4"));
                            cc.Item().PaddingTop(1).Element(e => BarLine(e, double.IsNaN(b.Value2) ? 0 : b.Value2, max, "#1D9E75"));
                        });
                    });
                }
                else
                {
                    col.Item().PaddingVertical(1).Row(r =>
                    {
                        r.ConstantItem(96).Text(b.Label).FontSize(8.5f);
                        float frac = (float)(b.Value / max);
                        r.RelativeItem(Math.Max(0.02f, frac)).Background("#1D9E75").Height(11);
                        r.RelativeItem(Math.Max(0.001f, 1 - frac)).Height(11);
                        r.ConstantItem(70).AlignRight().Text(b.Value.ToString("N0")).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    });
                }
            }
        });
    }

    private static void BarLine(IContainer c, double v, double max, string color)
    {
        c.Row(r =>
        {
            float frac = (float)(v / max);
            r.RelativeItem(Math.Max(0.02f, frac)).Background(color).Height(8);
            r.RelativeItem(Math.Max(0.001f, 1 - frac)).Height(8);
            r.ConstantItem(62).AlignRight().Text(v.ToString("N0")).FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    private static IContainer AlignedCell(IContainer c, CellAlign a) => a switch
    {
        CellAlign.Right => c.AlignRight(),
        CellAlign.Center => c.AlignCenter(),
        _ => c.AlignLeft(),
    };

    private static IContainer HeaderCell(IContainer c)
        => c.Background("#E9EEF4").Padding(4).BorderBottom(0.6f).BorderColor("#9DB2C6")
            .DefaultTextStyle(t => t.SemiBold().FontSize(9).FontColor("#334155"));
    private static IContainer BodyCell(IContainer c, string? bg)
        => (bg != null ? c.Background(bg) : c).BorderBottom(0.3f).BorderColor("#DEE2E6").PaddingVertical(3).PaddingHorizontal(4);
    private static IContainer TotalCell(IContainer c)
        => c.Background("#EAF0F6").BorderTop(1).BorderColor("#9DB2C6").PaddingVertical(4).PaddingHorizontal(4);

    private static string? StatusColor(ReportStatus s) => s switch
    {
        ReportStatus.Ok => "#1D9E75",
        ReportStatus.Warn => "#C98A18",
        ReportStatus.Bad => "#C0392B",
        _ => null,
    };
}
