// 生产任务书的 PDF 版式 —— 对应原 TaskOrderWindow.OnPrint（WPF PrintDialog 横向 + 按纸面缩放）。
// Avalonia 没有打印对话框，出同版式 PDF：抬头 / 指标条 / 十二列表（缺卸点行标红）/ 结论 / 缺卸点清单 / 四签 / 落款。
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PitMine3D.Kylin.Views.TaskLib;

internal static class TaskOrderPdf
{
    public static void Save(string path, string mine, string meta, string metrics, IReadOnlyList<TaskOrderWindow.OrderRow> rows,
                            string summary, string noSink, string issue)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontFamily("Noto Sans CJK SC", "WenQuanYi Zen Hei", "Microsoft YaHei", "SimSun", Fonts.Calibri).FontSize(8.5f));

                page.Content().Column(col =>
                {
                    col.Item().AlignCenter().Text(mine).FontSize(12).FontColor("#333333");
                    col.Item().AlignCenter().PaddingTop(2).PaddingBottom(6).Text("生产任务书").FontSize(18).Bold().FontColor("#111111");
                    col.Item().AlignCenter().Text(meta).FontSize(10).FontColor("#444444");
                    col.Item().AlignCenter().PaddingBottom(8).Text(metrics).FontSize(9).FontColor("#333333");

                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(20); c.RelativeColumn(1.7f); c.RelativeColumn(1.15f); c.RelativeColumn(1.6f);
                            c.ConstantColumn(32); c.ConstantColumn(46); c.ConstantColumn(64); c.ConstantColumn(44);
                            c.ConstantColumn(46); c.RelativeColumn(1.2f); c.RelativeColumn(1.25f); c.ConstantColumn(60);
                        });
                        string[] heads = { "序", "设备编组", "作业地点", "卸载地点", "工序", "物料", "计划量", "量口径", "运距 km", "质量目标", "作业人员", "时段 / 工时" };
                        t.Header(h =>
                        {
                            foreach (var s in heads)
                                h.Cell().Background("#F1F1F1").Border(0.5f).BorderColor("#CCCCCC").Padding(3).AlignCenter().Text(s).SemiBold();
                        });
                        foreach (var r in rows)
                        {
                            string bg = r.NoDestination ? "#FDECEC" : "#FFFFFF";
                            string fg = r.NoDestination ? "#A31414" : "#111111";
                            IContainer C() => t.Cell().Background(bg).Border(0.5f).BorderColor("#CCCCCC").Padding(3);
                            C().AlignCenter().Text(r.No.ToString()).FontColor(fg);
                            C().Text(r.Group).FontColor(fg);
                            C().Text(x => { x.Span(r.Place).FontColor(fg); if (r.HasUnit) x.Span("\n" + r.Unit).FontSize(7).FontColor("#555555"); });
                            C().Text(x => { x.Span(r.Destination).FontColor(fg); if (r.HasSplits) x.Span("\n" + r.Splits).FontSize(7).FontColor("#555555"); });
                            C().AlignCenter().Text(r.Process).FontColor(fg);
                            C().Text(r.Material).FontColor(fg);
                            C().AlignRight().Text(r.Plan).FontColor(fg);
                            C().AlignCenter().Text(r.Basis).FontColor(fg);
                            C().AlignRight().Text(r.Haul).FontColor(fg);
                            C().Text(r.Quality).FontColor(fg);
                            C().Text(r.Crew).FontColor(fg);
                            C().AlignCenter().Text(r.Span).FontColor(fg);
                        }
                    });

                    col.Item().PaddingTop(8).Text(summary).FontSize(9).FontColor("#444444");
                    if (noSink.Length > 0)
                        col.Item().PaddingTop(8).Background("#FDECEC").Border(0.5f).BorderColor("#E0A5A5").Padding(6).Column(c =>
                        {
                            c.Item().Text("以下任务未指定卸点，不得下达：").Bold().FontSize(9).FontColor("#8E1414");
                            c.Item().Text(noSink).FontSize(8.5f).FontColor("#8E1414");
                        });
                    col.Item().PaddingTop(18).Row(r =>
                    {
                        foreach (var s in new[] { "编制：________", "班长：________", "调度：________", "值班矿长：________" })
                            r.RelativeItem().Text(s).FontColor("#333333");
                    });
                    col.Item().PaddingTop(8).Text(issue).FontSize(8.5f).FontColor("#666666");
                });
            });
        }).GeneratePdf(path);
    }
}
