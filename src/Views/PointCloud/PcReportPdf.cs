using System;
using System.Collections.Generic;
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PitMine3D.Kylin.Views.PointCloud;

/// <summary>
/// 点云结果窗的 PDF 导出（对应原版 VolumeResultWindow 的「导出 PDF」）：
/// 标题 + 说明 + 键值表 + 直方图（用一行小方块近似，QuestPDF 无内联 SVG —— 同块体报告的做法）。
/// 字族顺序与块体报告一致：麒麟常见开源中文字族在前，Windows 字体次之，最后回落 QuestPDF 自带的 Lato。
/// </summary>
internal static class PcReportPdf
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static int s_licenseSet;

    private static void EnsureLicense()
    {
        if (System.Threading.Interlocked.Exchange(ref s_licenseSet, 1) == 0)
            QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void Write(string title, string? note, IReadOnlyList<(string k, string v)> rows,
                             IReadOnlyList<double>? hist, double histLo, double histHi, string? histTitle,
                             string filePath)
        => Build(title, note, rows, hist, histLo, histHi, histTitle).GeneratePdf(filePath);

    /// <summary>内存版（单测用：不落盘也能验证能渲染出非空 PDF）。</summary>
    public static byte[] Render(string title, string? note, IReadOnlyList<(string k, string v)> rows,
                                IReadOnlyList<double>? hist = null, double histLo = 0, double histHi = 0,
                                string? histTitle = null)
        => Build(title, note, rows, hist, histLo, histHi, histTitle).GeneratePdf();

    private static IDocument Build(string title, string? note, IReadOnlyList<(string k, string v)> rows,
                                   IReadOnlyList<double>? hist, double histLo, double histHi, string? histTitle)
    {
        EnsureLicense();
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.DefaultTextStyle(x => x.FontFamily("Noto Sans CJK SC", "WenQuanYi Zen Hei", "Microsoft YaHei", "SimSun", Fonts.Calibri).FontSize(10));

                page.Header().PaddingBottom(8).BorderBottom(2).BorderColor("#0086D1").Column(col =>
                {
                    col.Item().Text(title).FontSize(16).Bold().FontColor("#1A2430");
                    col.Item().Text($"导出时间 {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    if (!string.IsNullOrEmpty(note))
                        col.Item().PaddingBottom(8).Text(note).FontSize(9).FontColor(Colors.Grey.Darken1);

                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.ConstantColumn(170); c.RelativeColumn(); });
                        foreach (var (k, v) in rows)
                        {
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3)
                                .Text(k).FontSize(9).FontColor(Colors.Grey.Darken2);
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3)
                                .Text(v).FontSize(9);
                        }
                    });

                    if (hist is { Count: > 0 })
                    {
                        col.Item().PaddingTop(12).Text(histTitle ?? "分布直方图").FontSize(11).Bold();
                        col.Item().PaddingTop(2).Text($"区间 {histLo.ToString("0.###", Inv)} ~ {histHi.ToString("0.###", Inv)}")
                            .FontSize(8).FontColor(Colors.Grey.Medium);
                        double max = 0;
                        foreach (double h in hist) if (h > max) max = h;
                        col.Item().PaddingTop(4).Height(90).Row(r =>
                        {
                            foreach (double h in hist)
                                r.RelativeItem().PaddingHorizontal(1).AlignBottom()
                                    .Height((float)Math.Max(1, max > 0 ? h / max * 88 : 1)).Background("#4D8FE0");
                        });
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("DayOps 点云处理 · ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }
}
