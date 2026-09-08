using System;
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「输出报告」的 PDF 渲染（忠实原 BlockReportGenerator.WritePdfFile/BuildPdfHeader/BuildPdfBody）：
/// A4 + 页眉（模型名 / 生成时间 / 描述）+ 汇总四列表 + 属性统计表（含每属性直方图条）+ 页脚页码。
/// 与 <see cref="BlockModelReport.RenderHtml"/>/<see cref="BlockModelReport.RenderCsv"/> 同源同数据，只是换个渲染器。
/// </summary>
public static class BlockModelReportPdf
{
    private static int s_licenseSet;

    private static void EnsureLicense()
    {
        if (System.Threading.Interlocked.Exchange(ref s_licenseSet, 1) == 0)
            QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>渲染成 PDF 字节（内存版，供单测）。</summary>
    public static byte[] Render(BlockModelReport.Report r)
    {
        EnsureLicense();
        if (r == null) throw new ArgumentNullException(nameof(r));
        return Build(r).GeneratePdf();
    }

    /// <summary>渲染并写盘。</summary>
    public static void Write(BlockModelReport.Report r, string filePath)
    {
        EnsureLicense();
        if (r == null) throw new ArgumentNullException(nameof(r));
        if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath 不能为空", nameof(filePath));
        Build(r).GeneratePdf(filePath);
    }

    private static IDocument Build(BlockModelReport.Report r) => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20);
            // 中文字形：麒麟上常见的开源中文字族在前，Windows 字体次之，最后回落 Lato（QuestPDF 自带）
            page.DefaultTextStyle(x => x.FontFamily("Noto Sans CJK SC", "WenQuanYi Zen Hei", "Microsoft YaHei", "SimSun", Fonts.Calibri).FontSize(10));

            page.Header().Element(h => BuildHeader(h, r));
            page.Content().Element(c => BuildBody(c, r));
            page.Footer().AlignRight().Text(t =>
            {
                t.Span("PitMine3D 块体模型 · ").FontSize(8).FontColor(Colors.Grey.Medium);
                t.Span("第 ").FontSize(8).FontColor(Colors.Grey.Medium);
                t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                t.Span(" 页").FontSize(8).FontColor(Colors.Grey.Medium);
            });
        });
    });

    private static void BuildHeader(IContainer container, BlockModelReport.Report r)
    {
        container.PaddingBottom(8).BorderBottom(2).BorderColor("#0D6EFD").Column(col =>
        {
            col.Item().Text($"块体模型报告: {r.ModelName}").FontSize(18).Bold().FontColor("#1E3A5F");
            col.Item().Text(t =>
            {
                t.Span($"生成时间: {r.GeneratedAt:yyyy-MM-dd HH:mm:ss}").FontSize(9).FontColor(Colors.Grey.Medium);
                if (!string.IsNullOrEmpty(r.Description))
                {
                    t.Span("  ·  ").FontSize(9).FontColor(Colors.Grey.Medium);
                    t.Span(r.Description).FontSize(9).FontColor(Colors.Grey.Medium);
                }
            });
        });
    }

    private static void BuildBody(IContainer container, BlockModelReport.Report r)
    {
        container.PaddingVertical(12).Column(col =>
        {
            // ── 汇总 ──
            col.Item().Text("汇总").FontSize(13).Bold().FontColor("#1E3A5F");
            col.Item().PaddingTop(4).Background("#F8F9FA").Padding(10).Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(3); });
                if (r.IsScoped)
                {
                    Cell(t, "统计范围", r.ScopeLabel);
                    Cell(t, "范围内块数", $"{r.ScopedCells:N0}  ({Pct(r.ScopedCells, r.TotalCells)})");
                    Cell(t, "范围内体积", $"{r.ScopedVolume:N0} m³");
                    Cell(t, "", "");   // 占位补满当前行的 4 列网格
                }
                Cell(t, "总块数", r.TotalCells.ToString("N0"));
                Cell(t, "存储模式", r.StorageMode.ToChineseLabel());
                Cell(t, "可见块数", $"{r.VisibleCells:N0}  ({Pct(r.VisibleCells, r.TotalCells)})");
                Cell(t, "网格数", $"{r.Nx} × {r.Ny} × {r.Nz}");
                Cell(t, "已删块数", $"{r.DeletedCells:N0}  ({Pct(r.DeletedCells, r.TotalCells)})");
                Cell(t, "块尺寸 (m)", $"{r.Sx:0.##} × {r.Sy:0.##} × {r.Sz:0.##}");
                Cell(t, "单 cell 体积", $"{r.SingleCellVolume:N3} m³");
                Cell(t, "可见总体积", $"{r.VisibleVolume:N0} m³");
                Cell(t, "AABB", r.BoundsText);
                Cell(t, "着色驱动", string.IsNullOrEmpty(r.ActiveColormapAttribute) ? "（按 Z 渐变）" : r.ActiveColormapAttribute);
            });

            // ── 属性统计 ──
            col.Item().PaddingTop(16).Text("属性统计").FontSize(13).Bold().FontColor("#1E3A5F");
            if (r.Attributes.Count == 0)
            {
                col.Item().PaddingTop(6).Text("模型未定义任何属性列。").FontSize(10).FontColor(Colors.Grey.Medium).Italic();
            }
            else
            {
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(3);
                        c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(5);
                    });
                    t.Header(h =>
                    {
                        h.Cell().Element(HeaderCell).Text("属性");
                        h.Cell().Element(HeaderCell).Text("单位");
                        h.Cell().Element(HeaderCell).Text("类型");
                        h.Cell().Element(HeaderCell).AlignRight().Text("非默认");
                        h.Cell().Element(HeaderCell).AlignRight().Text("Min");
                        h.Cell().Element(HeaderCell).AlignRight().Text("Max");
                        h.Cell().Element(HeaderCell).AlignRight().Text("Mean");
                        h.Cell().Element(HeaderCell).AlignRight().Text("Std");
                        h.Cell().Element(HeaderCell).Text("分布");
                    });
                    foreach (var a in r.Attributes)
                    {
                        t.Cell().Element(BodyCell).Text(a.Name);
                        t.Cell().Element(BodyCell).Text(string.IsNullOrEmpty(a.Unit) ? "—" : a.Unit);
                        t.Cell().Element(BodyCell).Text(a.DataType.ToString());
                        t.Cell().Element(BodyCell).AlignRight().Text(a.NonDefaultCount.ToString("N0"));
                        t.Cell().Element(BodyCell).AlignRight().Text(Fmt(a.Min));
                        t.Cell().Element(BodyCell).AlignRight().Text(Fmt(a.Max));
                        t.Cell().Element(BodyCell).AlignRight().Text(Fmt(a.Mean));
                        t.Cell().Element(BodyCell).AlignRight().Text(Fmt(a.Std));
                        t.Cell().Element(BodyCell).Element(c => Histogram(c, a));
                    }
                });
            }

            // ── 分标高统计（Kylin 报告与 HTML/CSV 同源, 有则一并出） ──
            if (r.Levels.Count > 0)
            {
                col.Item().PaddingTop(16).Text("分标高统计").FontSize(13).Bold().FontColor("#1E3A5F");
                col.Item().PaddingTop(4).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(4); });
                    t.Header(h =>
                    {
                        h.Cell().Element(HeaderCell).AlignRight().Text("下标高");
                        h.Cell().Element(HeaderCell).AlignRight().Text("上标高");
                        h.Cell().Element(HeaderCell).AlignRight().Text("块数");
                        h.Cell().Element(HeaderCell).AlignRight().Text("体积 (m³)");
                    });
                    foreach (var lv in r.Levels)
                    {
                        t.Cell().Element(BodyCell).AlignRight().Text(lv.ZLow.ToString("0.##", CultureInfo.InvariantCulture));
                        t.Cell().Element(BodyCell).AlignRight().Text(lv.ZHigh.ToString("0.##", CultureInfo.InvariantCulture));
                        t.Cell().Element(BodyCell).AlignRight().Text(lv.Cells.ToString("N0"));
                        t.Cell().Element(BodyCell).AlignRight().Text(lv.Volume.ToString("N0"));
                    }
                });
            }
        });
    }

    private static IContainer HeaderCell(IContainer c)
        => c.Background("#E9ECEF").Padding(4).BorderBottom(0.5f).BorderColor("#ADB5BD")
            .DefaultTextStyle(t => t.SemiBold().FontSize(9).FontColor("#495057"));

    private static IContainer BodyCell(IContainer c)
        => c.BorderBottom(0.3f).BorderColor("#DEE2E6").Padding(4).DefaultTextStyle(t => t.FontSize(9));

    private static void Cell(TableDescriptor t, string key, string value)
    {
        t.Cell().Padding(3).Text(key).FontSize(9).FontColor(Colors.Grey.Medium);
        t.Cell().Padding(3).Text(value).FontSize(9).SemiBold().FontColor("#1E3A5F");
    }

    /// <summary>用一行小方块表示直方图（每桶高度按 cell 数归一化）——QuestPDF 无内联 SVG，同原版用 Row+Height 近似。</summary>
    private static void Histogram(IContainer container, BlockModelReport.AttributeStats a)
    {
        if (a.Histogram == null || a.Histogram.Length == 0) { container.Text("—").FontSize(9).FontColor(Colors.Grey.Lighten1); return; }
        long max = 0;
        foreach (var c in a.Histogram) if (c > max) max = c;
        if (max == 0) { container.Text("—").FontSize(9).FontColor(Colors.Grey.Lighten1); return; }
        container.Height(16).Row(row =>
        {
            foreach (var c in a.Histogram)
            {
                float h = (float)((double)c / max * 14 + 1);   // 至少 1pt 高让所有桶都看得到
                row.RelativeItem().PaddingHorizontal(0.3f).AlignBottom().Background("#0D6EFD").Height(h);
            }
        });
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Pct(long part, long total) => total > 0 ? $"{(double)part / total * 100:0.##}%" : "—";
}
